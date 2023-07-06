﻿using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;

string? jobId = Environment.GetEnvironmentVariable("JOB_ID");

Console.WriteLine($"{nameof(jobId)}={jobId}");

if (jobId is null)
{
    return;
}

var job = new Job(jobId);
await job.RunJobAsync();

public class Job
{
    private readonly string _jobId;
    private readonly HttpClient _client;
    private readonly Channel<string> _channel;
    private CancellationToken _jobTimeout;
    private readonly Stopwatch _lastLogEntry = new();
    private Dictionary<string, string> _metadata = new();

    public string SourceRepo => _metadata["PrRepo"];
    public string SourceBranch => _metadata["PrBranch"];
    public string CustomArguments => _metadata["CustomArguments"];

    public Job(string jobId)
    {
        _jobId = jobId;

        _client = new HttpClient
        {
            DefaultRequestVersion = HttpVersion.Version20,
            BaseAddress = new Uri("https://mihubot.xyz/api/RuntimeUtils/Jobs/")
        };

        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public async Task RunJobAsync()
    {
        _lastLogEntry.Start();

        using var jobCts = new CancellationTokenSource(TimeSpan.FromHours(5));
        _jobTimeout = jobCts.Token;

        Task channelReaderTask = Task.Run(() => ReadChannelAsync());

        try
        {
            _metadata = await GetFromJsonAsync<Dictionary<string, string>>("Metadata");

            await RunJobAsyncCore();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Something went wrong: {ex}");
            await LogAsync(ex.ToString());
        }

        _channel.Writer.TryComplete();
        await channelReaderTask.WaitAsync(_jobTimeout);
    }

    private async Task RunJobAsyncCore()
    {
        await LogAsync($"{nameof(SourceRepo)}={SourceRepo}");
        await LogAsync($"{nameof(SourceBranch)}={SourceBranch}");
        await LogAsync($"{nameof(CustomArguments)}={CustomArguments}");

        string template = await File.ReadAllTextAsync("script.sh.template");
        string script = template
            .ReplaceLineEndings()
            .Replace("{{SOURCE_REPOSITORY}}", SourceRepo)
            .Replace("{{SOURCE_BRANCH}}", SourceBranch);

        await LogAsync($"Using script:\n{script}");

        await File.WriteAllTextAsync("script.sh", script);

        await RunProcessAsync("bash", "-x script.sh");

        await JitDiffAsync(baseline: true, corelib: true);
        await JitDiffAsync(baseline: false, corelib: true);
        string coreLibDiff = await JitAnalyzeAsync("corelib");
        await UploadArtifactAsync("diff-corelib.txt", coreLibDiff);

        await JitDiffAsync(baseline: true, corelib: false, sequential: true);
        await JitDiffAsync(baseline: false, corelib: false, sequential: true);
        string frameworksDiff = await JitAnalyzeAsync("frameworks");
        await UploadArtifactAsync("diff-frameworks.txt", frameworksDiff);

        await ZipAndUploadArtifactAsync("jit-diffs-corelib", "jit-diffs/corelib");
        await ZipAndUploadArtifactAsync("jit-diffs-frameworks", "jit-diffs/frameworks");

        await ZipAndUploadArtifactAsync("build-artifacts-main", "artifacts-main");
        await ZipAndUploadArtifactAsync("build-artifacts-pr", "artifacts-pr");
        await ZipAndUploadArtifactAsync("build-clr-checked-main", "clr-checked-main");
        await ZipAndUploadArtifactAsync("build-clr-checked-pr", "clr-checked-pr");

        async Task ZipAndUploadArtifactAsync(string zipFileName, string folderPath)
        {
            zipFileName = $"{zipFileName}.zip";
            await RunProcessAsync("zip", $"-r {zipFileName} {folderPath}");
            await UploadArtifactAsync(zipFileName);
        }
    }

    private async Task LogAsync(string message)
    {
        lock (_lastLogEntry)
        {
            _lastLogEntry.Restart();
        }

        try
        {
            await _channel.Writer.WriteAsync($"[{DateTime.UtcNow:HH:mm:ss}] {message}", _jobTimeout);
        }
        catch { }
    }

    private async Task ErrorAsync(string message)
    {
        try
        {
            _channel.Writer.TryComplete(new Exception(message));

            await PostAsJsonAsync("Logs", new[] { $"ERROR: {message}" });
        }
        catch { }
    }

    private async Task ReadChannelAsync()
    {
        bool completed = false;

        Task heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (!Volatile.Read(ref completed))
                {
                    await Task.Delay(100, _jobTimeout);

                    lock (_lastLogEntry)
                    {
                        if (_lastLogEntry.Elapsed.TotalSeconds < 2 * 60)
                        {
                            continue;
                        }
                    }

                    await LogAsync("Heartbeat - I'm still here");
                }
            }
            catch { }
        });

        try
        {
            ChannelReader<string> reader = _channel.Reader;

            while (await reader.WaitToReadAsync(_jobTimeout))
            {
                List<string> messages = new();
                while (reader.TryRead(out var message))
                {
                    messages.Add(message);
                }

                await PostAsJsonAsync("Logs", messages.ToArray());
            }
        }
        catch (Exception ex)
        {
            await ErrorAsync(ex.ToString());
        }

        Volatile.Write(ref completed, true);
        await heartbeatTask.WaitAsync(_jobTimeout);
    }

    private async Task<string> JitAnalyzeAsync(string folder)
    {
        List<string> output = new();

        await RunProcessAsync("bin/jit-analyze",
            $"-b jit-diffs/{folder}/dasmset_1/base -d jit-diffs/{folder}/dasmset_2/base -r -c 100",
            output);

        return string.Join('\n', output);
    }

    private async Task JitDiffAsync(bool baseline, bool corelib, bool sequential = false)
    {
        string corelibOrFrameworks = corelib ? "corelib" : "frameworks";
        string artifactsFolder = baseline ? "artifacts-main" : "artifacts-pr";
        string checkedClrFolder = baseline ? "clr-checked-main" : "clr-checked-pr";

        await RunProcessAsync("bin/jit-diff",
            $"diff {(sequential ? "--sequential" : "")} --cctors " +
            $"--output jit-diffs/{corelibOrFrameworks} --{corelibOrFrameworks} --pmi " +
            $"--core_root {artifactsFolder} " +
            $"--base {checkedClrFolder}");
    }

    private async Task RunProcessAsync(string fileName, string arguments, List<string>? output = null)
    {
        await LogAsync($"Running '{fileName} {arguments}'");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            }
        };

        process.Start();

        await Task.WhenAll(
            Task.Run(() => ReadOutputStreamAsync(process.StandardOutput)),
            Task.Run(() => ReadOutputStreamAsync(process.StandardError)),
            process.WaitForExitAsync(_jobTimeout));

        async Task ReadOutputStreamAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is string line)
            {
                if (output is not null)
                {
                    lock (output)
                    {
                        output.Add(line);
                    }
                }

                await LogAsync(line);
            }
        }
    }

    private async Task UploadArtifactAsync(string fileName, string contents)
    {
        string filePath = Path.Combine(Path.GetTempPath(), fileName);
        try
        {
            await File.WriteAllTextAsync(filePath, contents);

            await UploadArtifactAsync(filePath);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    private async Task UploadArtifactAsync(string path)
    {
        string name = Path.GetFileName(path);

        await LogAsync($"Uploading '{name}'");

        await using FileStream fs = File.OpenRead(path);
        using StreamContent content = new(fs);

        await PostAsJsonAsync("Artifact", content, Uri.EscapeDataString(name));
    }

    private async Task<T> GetFromJsonAsync<T>(string path)
    {
        try
        {
            return await _client.GetFromJsonAsync<T>($"{path}/{_jobId}", _jobTimeout) ?? throw new Exception("Null response");
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to fetch resource '{path}': {ex}");
            throw;
        }
    }

    private async Task PostAsJsonAsync(string path, object? value, string? pathArgument = null)
    {
        try
        {
            using var response = await _client.PostAsJsonAsync($"{path}/{_jobId}/{pathArgument}", value, _jobTimeout);
        }
        catch (Exception ex)
        {
            await LogAsync($"Failed to post resource '{path}': {ex}");
            throw;
        }
    }
}