﻿namespace Runner.Jobs;

internal sealed class JitDiffJob : JobBase
{
    public const string DiffsDirectory = "jit-diffs/frameworks";
    public const string DiffsMainDirectory = $"{DiffsDirectory}/main";
    public const string DiffsPrDirectory = $"{DiffsDirectory}/pr";
    public const string DasmSubdirectory = "dasmset_1/base";

    public JitDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamOrFastestDiskAsync();

        await CloneRuntimeAndSetupToolsAsync(this);

        await BuildAndCopyRuntimeBranchBitsAsync(this, "main");

        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await BuildAndCopyRuntimeBranchBitsAsync(this, "pr");

        await RuntimeHelpers.InstallRuntimeDotnetSdkAsync(this);

        string diffAnalyzeSummary = await CollectFrameworksDiffsAsync();

        await UploadJitDiffExamplesAsync(diffAnalyzeSummary, regressions: true);
        await UploadJitDiffExamplesAsync(diffAnalyzeSummary, regressions: false);
    }

    public static async Task CloneRuntimeAndSetupToolsAsync(JobBase job)
    {
        Task cloneRuntimeTask = RuntimeHelpers.CloneRuntimeAsync(job);

        Task setupZipAndWgetTask = job.RunProcessAsync("apt-get", "install -y zip wget", logPrefix: "Setup zip & wget");

        Task setupJitutilsTask = Task.Run(async () =>
        {
            const string LogPrefix = "Setup jitutils";
            await setupZipAndWgetTask;

            string repo = job.GetArgument("jitutils-repo", "dotnet/jitutils");
            string branch = job.GetArgument("jitutils-branch", "main");

            await job.RunProcessAsync("git", $"clone --no-tags --single-branch -b {branch} --progress https://github.com/{repo}.git", logPrefix: LogPrefix);

            if (IsArm)
            {
                const string ToolsLink = "https://raw.githubusercontent.com/MihaZupan/runtime-utils/clang-tools";
                Directory.CreateDirectory("jitutils/bin");
                await job.RunProcessAsync("wget", $"-O jitutils/bin/clang-format {ToolsLink}/clang-format", logPrefix: LogPrefix);
                await job.RunProcessAsync("wget", $"-O jitutils/bin/clang-tidy {ToolsLink}/clang-tidy", logPrefix: LogPrefix);
                await job.RunProcessAsync("chmod", "751 jitutils/bin/clang-format", logPrefix: LogPrefix);
                await job.RunProcessAsync("chmod", "751 jitutils/bin/clang-tidy", logPrefix: LogPrefix);
            }

            await job.RunProcessAsync("bash", "bootstrap.sh", logPrefix: LogPrefix, workDir: "jitutils");
        });

        Task createDirectoriesTask = Task.Run(() =>
        {
            Directory.CreateDirectory("artifacts-main");
            Directory.CreateDirectory("artifacts-pr");
            Directory.CreateDirectory("clr-checked-main");
            Directory.CreateDirectory("clr-checked-pr");
            Directory.CreateDirectory("jit-diffs");
            Directory.CreateDirectory(DiffsDirectory);
            Directory.CreateDirectory(DiffsMainDirectory);
            Directory.CreateDirectory(DiffsPrDirectory);
        });

        await createDirectoriesTask;
        await setupJitutilsTask;
        await setupZipAndWgetTask;
        await cloneRuntimeTask;
    }

    public static async Task BuildAndCopyRuntimeBranchBitsAsync(JobBase job, string branch, bool uploadArtifacts = true, bool buildChecked = true)
    {
        string arch = IsArm ? "arm64" : "x64";

        (bool rebuildClr, bool rebuildLibs) = await ShouldRebuildAsync();

        string targets = (rebuildClr, rebuildLibs) switch
        {
            (true, true) => "clr+libs",
            (true, false) => "clr",
            _ => "libs"
        };

        await job.RunProcessAsync("bash", $"build.sh {targets} -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: $"{branch} release", workDir: "runtime");

        Task copyReleaseBitsTask = RuntimeHelpers.CopyReleaseArtifactsAsync(job, branch, $"artifacts-{branch}");

        if (buildChecked)
        {
            if (rebuildClr)
            {
                await job.RunProcessAsync("bash", "build.sh clr.jit -c Checked", logPrefix: $"{branch} checked", workDir: "runtime");
            }

            await job.RunProcessAsync("cp", $"-r runtime/artifacts/bin/coreclr/linux.{arch}.Checked/. clr-checked-{branch}", logPrefix: $"{branch} checked");
        }

        if (uploadArtifacts)
        {
            job.PendingTasks.Enqueue(job.ZipAndUploadArtifactAsync($"build-artifacts-{branch}", $"artifacts-{branch}"));

            if (buildChecked)
            {
                job.PendingTasks.Enqueue(job.ZipAndUploadArtifactAsync($"build-clr-checked-{branch}", $"clr-checked-{branch}"));
            }
        }

        await copyReleaseBitsTask;

        async Task<(bool Clr, bool Libs)> ShouldRebuildAsync()
        {
            if (branch == "pr" && !job.TryGetFlag("forceRebuildAll"))
            {
                bool clr = false;
                bool libs = false;

                foreach (string file in await GitHelper.GetChangedFilesAsync(job, "main", "runtime"))
                {
                    if (file.Contains("/System.Private.CoreLib/", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                        libs = true;
                    }
                    else if (file.StartsWith("src/coreclr/", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                    }
                    else if (file.Contains("Common", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                        libs = true;
                    }
                    else if (file.StartsWith("src/libraries/", StringComparison.OrdinalIgnoreCase))
                    {
                        libs = true;
                    }
                    else if (!file.StartsWith("src/tests/", StringComparison.OrdinalIgnoreCase))
                    {
                        clr = true;
                        libs = true;
                    }
                }

                if (!clr && !libs)
                {
                    await job.LogAsync($"WARNING: Don't need to rebuild anything? What is this PR?");
                }

                return (clr, libs);
            }

            return (true, true);
        }
    }

    private async Task<string> CollectFrameworksDiffsAsync()
    {
        try
        {
            await Task.WhenAll(
                JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-main", "clr-checked-main", DiffsMainDirectory),
                JitDiffUtils.RunJitDiffOnFrameworksAsync(this, "artifacts-pr", "clr-checked-pr", DiffsPrDirectory));
        }
        finally
        {
            PendingTasks.Enqueue(ZipAndUploadArtifactAsync("jit-diffs-main", DiffsMainDirectory));
            PendingTasks.Enqueue(ZipAndUploadArtifactAsync("jit-diffs-pr", DiffsPrDirectory));
        }

        string diffAnalyzeSummary = await JitDiffUtils.RunJitAnalyzeAsync(this, $"{DiffsMainDirectory}/{DasmSubdirectory}", $"{DiffsPrDirectory}/{DasmSubdirectory}");

        PendingTasks.Enqueue(UploadTextArtifactAsync("diff-frameworks.txt", diffAnalyzeSummary));

        return diffAnalyzeSummary;
    }

    private async Task UploadJitDiffExamplesAsync(string diffAnalyzeSummary, bool regressions)
    {
        var (diffs, noisyDiffsRemoved) = await JitDiffUtils.GetDiffMarkdownAsync(
            this,
            JitDiffUtils.ParseDiffAnalyzeEntries(diffAnalyzeSummary, regressions),
            tryGetExtraInfo: null,
            replaceMethodName: name => name,
            maxCount: 20);

        string changes = JitDiffUtils.GetCommentMarkdown(diffs, GitHubHelpers.CommentLengthLimit, regressions, out bool truncated);

        await LogAsync($"Found {diffs.Length} changes, comment length={changes.Length} for {nameof(regressions)}={regressions}");

        if (changes.Length != 0)
        {
            if (noisyDiffsRemoved)
            {
                changes = $"{changes}\n\nNote: some changes were skipped as they were likely noise.";
            }

            PendingTasks.Enqueue(UploadTextArtifactAsync($"ShortDiffs{(regressions ? "Regressions" : "Improvements")}.md", changes));

            if (truncated)
            {
                changes = JitDiffUtils.GetCommentMarkdown(diffs, GitHubHelpers.GistLengthLimit, regressions, out _);

                PendingTasks.Enqueue(UploadTextArtifactAsync($"LongDiffs{(regressions ? "Regressions" : "Improvements")}.md", changes));
            }
        }
    }
}
