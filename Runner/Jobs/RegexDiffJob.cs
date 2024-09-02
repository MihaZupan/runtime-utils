﻿using Microsoft.CodeAnalysis.CSharp;
using System.Globalization;
using System.IO.Compression;

namespace Runner.Jobs;

internal sealed class RegexDiffJob : JobBase
{
    private const string KnownPatternsPath = "KnownPatterns.json";

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    public RegexDiffJob(HttpClient client, Dictionary<string, string> metadata) : base(client, metadata) { }

    protected override async Task RunJobCoreAsync()
    {
        await ChangeWorkingDirectoryToRamDiskAsync();

        await DownloadKnownPatternsAsync();

        await RuntimeHelpers.CloneRuntimeAsync(this);

        await RunProcessAsync("bash", $"build.sh clr+libs -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: "main", workDir: "runtime");

        var mainSources = await RunSourceGeneratorOnKnownPatternsAsync("main");

        await RunProcessAsync("git", "checkout .", workDir: "runtime");
        await RunProcessAsync("git", "switch pr", workDir: "runtime");

        await RunProcessAsync("runtime/.dotnet/dotnet", $"build src/libraries/System.Text.RegularExpressions/gen -c Release {RuntimeHelpers.LibrariesExtraBuildArgs}", logPrefix: "pr", workDir: "runtime");

        var prSources = await RunSourceGeneratorOnKnownPatternsAsync("pr");

        var entries = await CreateRegexEntriesAsync(mainSources, prSources);

        await DiffRegexSourcesAsync(entries);

        await ExtractSearchValuesInfoAsync(entries);

        await UploadResultsAsync(entries);
    }

    private async Task DownloadKnownPatternsAsync()
    {
        KnownPattern[]? knownPatterns = await HttpClient.GetFromJsonAsync<KnownPattern[]>(
            "https://raw.githubusercontent.com/dotnet/runtime-assets/main/src/System.Text.RegularExpressions.TestData/Regex_RealWorldPatterns.json",
            s_jsonOptions);

        ArgumentNullException.ThrowIfNull(knownPatterns);
        ArgumentOutOfRangeException.ThrowIfZero(knownPatterns.Length);

        await LogAsync($"Downloaded {knownPatterns.Length} patterns");

        knownPatterns = knownPatterns.Distinct().ToArray();

        await LogAsync($"Using {knownPatterns.Length} distinct patterns");

        File.WriteAllText(KnownPatternsPath, JsonSerializer.Serialize(knownPatterns, s_jsonOptions));
    }

    private async Task<Dictionary<KnownPattern, string>> RunSourceGeneratorOnKnownPatternsAsync(string branch)
    {
        await LogAsync($"Generating {branch} Regex sources ...");

        const string TestFilePath = "runtime/src/libraries/System.Text.RegularExpressions/tests/FunctionalTests/RegexGeneratorParserTests.cs";

        string resultsPath = Path.GetFullPath($"results-{branch}.json");

        string patchSource =
            $$$""""
            public class InjectedGenerateAllSourcesTestClass
            {
                private record RegexEntry(string Pattern, RegexOptions Options, int Count);
                private record EntryWithGeneratedSource(string Pattern, RegexOptions Options, int Count, string OutputSource);

                [Fact]
                public async Task GenerateAllSourcesAsync()
                {
                    if (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.Contains("Framework", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    const string JsonPath = @"{{{Path.GetFullPath(KnownPatternsPath)}}}";
                    var regexEntries = System.Text.Json.JsonSerializer.Deserialize<RegexEntry[]>(System.IO.File.ReadAllText(JsonPath));
                    int entriesProcessed = 0;

                    List<EntryWithGeneratedSource> sources = new();

                    await Parallel.ForEachAsync(regexEntries, async (entry, _) =>
                    {
                        string program =
                            $$"""
                            using System.Text.RegularExpressions;
                            partial class C
                            {
                                [GeneratedRegex({{SymbolDisplay.FormatLiteral(entry.Pattern, quote: true)}}, (RegexOptions){{(int)entry.Options}})]
                                public static partial Regex Valid();
                            }
                            """;

                        try
                        {
                            string actual = await RegexGeneratorHelper.GenerateSourceText(program, allowUnsafe: true, checkOverflow: false);

                            lock (sources)
                            {
                                sources.Add(new EntryWithGeneratedSource(entry.Pattern, entry.Options, entry.Count, actual));
                            }
                        }
                        catch (Exception ex) when (ex.ToString().Contains("info SYSLIB1044", StringComparison.Ordinal)) { }

                        int currentProcessed = System.Threading.Interlocked.Increment(ref entriesProcessed);
                        if (currentProcessed % 1_000 == 0)
                        {
                            System.Console.WriteLine($"Processed {currentProcessed} out of {regexEntries.Length} patterns");
                        }
                    });

                    System.IO.File.WriteAllText(@"{{{resultsPath}}}", System.Text.Json.JsonSerializer.Serialize(sources));
                }
            }
            """";
        patchSource = string.Join('\n', patchSource.ReplaceLineEndings("\n").Split('\n').Select(l => $"    {l}"));

        await LogAsync($"Injecting test source patch:\n{TestFilePath}\n{patchSource}");
        string testSource = File.ReadAllText(TestFilePath);
        int offset = testSource.IndexOf('{') + 1;
        testSource = $"{testSource.AsSpan(0, offset)}\n{patchSource}{testSource.AsSpan(offset)}";
        File.WriteAllText(TestFilePath, testSource);
        await RunProcessAsync("git", "commit -am \"Patch test sources\"", workDir: "runtime");

        const string RegexTestsPath = "src/libraries/System.Text.RegularExpressions/tests/FunctionalTests";
        const string XUnitMethodName = "System.Text.RegularExpressions.Tests.InjectedGenerateAllSourcesTestClass.GenerateAllSourcesAsync";
        await RunProcessAsync("runtime/.dotnet/dotnet", $"build {RegexTestsPath} /t:Test -c Release /p:XUnitMethodName={XUnitMethodName}",
            logPrefix: $"Generating sources for {branch}", workDir: "runtime");

        EntryWithGeneratedSource[] generatedSources = JsonSerializer.Deserialize<EntryWithGeneratedSource[]>(File.ReadAllText(resultsPath), s_jsonOptions)!;

        return generatedSources.ToDictionary(s => new KnownPattern(s.Pattern, s.Options, s.Count), s => s.OutputSource);
    }

    private async Task<RegexEntry[]> CreateRegexEntriesAsync(Dictionary<KnownPattern, string> mainSources, Dictionary<KnownPattern, string> prSources)
    {
        RegexEntry[] entries = mainSources
            .Where(main => prSources.ContainsKey(main.Key))
            .Select(main => new RegexEntry
            {
                Regex = main.Key,
                MainSource = main.Value,
                PrSource = prSources[main.Key]
            })
            .OrderByDescending(r => r.Regex.Count)
            .ToArray();

        await LogAsync($"Combined {mainSources.Count} main sources and {prSources.Count} pr sources into {entries.Length} entries");

        return entries;
    }

    private async Task DiffRegexSourcesAsync(RegexEntry[] entries)
    {
        await LogAsync("Calculating diffs in generated sources ...");

        int entriesProcessed = 0;

        await Parallel.ForAsync(0, entries.Length, async (i, _) =>
        {
            RegexEntry entry = entries[i];
            string mainSource = entry.MainSource;
            string prSource = entry.PrSource;

            if (mainSource != prSource)
            {
                string mainFile = $"main-{i}.cs";
                string prFile = $"pr-{i}.cs";

                File.WriteAllText(mainFile, mainSource);
                File.WriteAllText(prFile, prSource);

                List<string> shortDiffLines = await GitHelper.DiffAsync(this, mainFile, prFile);
                List<string> fullDiffLines = await GitHelper.DiffAsync(this, mainFile, prFile, fullContext: true);

                File.Delete(mainFile);
                File.Delete(prFile);

                TrimExcessLeadingWhiteSpace(shortDiffLines);

                entry.ShortDiff = string.Join('\n', shortDiffLines);
                entry.FullDiff = string.Join('\n', fullDiffLines);
            }

            int currentProcessed = Interlocked.Increment(ref entriesProcessed);
            if (currentProcessed % 1_000 == 0)
            {
                await LogAsync($"Generated diffs for {currentProcessed} out of {entries.Length} patterns");
            }
        });

        static void TrimExcessLeadingWhiteSpace(List<string> lines)
        {
            if (lines.Count == 0)
                return;

            if (lines[0].AsSpan().TrimEnd().Length == 0)
            {
                lines.RemoveAt(0);
            }

            if (lines.Count == 0)
                return;

            if (lines[^1].AsSpan().TrimEnd().Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }

            if (lines.Count == 0)
                return;

            int minOffset = lines.Where(l => l.Length > 0).Min(CountWhiteSpace);

            if (minOffset <= 3)
                return;

            minOffset -= 2;

            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                if (line.Length > 0)
                {
                    lines[i] = line[0] is '+' or '-'
                        ? $"{line[0]}{line.AsSpan(minOffset - 1)}"
                        : line.Substring(minOffset);
                }
            }

            static int CountWhiteSpace(string line)
            {
                int i = 0;
                if (line.StartsWith('+') || line.StartsWith('-'))
                {
                    i++;
                }

                while (i < line.Length && char.IsWhiteSpace(line[i]))
                {
                    i++;
                }

                return i;
            }
        }
    }

    private async Task ExtractSearchValuesInfoAsync(RegexEntry[] entries)
    {
        await LogAsync("Extracting SearchValues constructors from generated sources ...");

        await Parallel.ForEachAsync(entries, async (entry, _) =>
        {
            try
            {
                ExtractSearchValuesInfo(entry);
            }
            catch (Exception ex)
            {
                await LogAsync($"Failed to extract SearchValues: {ex}\n\n{entry.PrSource}");
            }
        });

        static void ExtractSearchValuesInfo(RegexEntry entry)
        {
            string source = entry.PrSource;

            List<string> searchValuesOfChar = [];
            List<(string[], StringComparison)> searchValuesOfString = [];

            int utilitiesOffset = source.IndexOf("file static class Utilities", StringComparison.Ordinal);
            if (utilitiesOffset >= 0)
            {
                ReadOnlySpan<char> utilities = source.AsSpan(utilitiesOffset);

                foreach (var line in utilities.EnumerateLines())
                {
                    var trimmed = line.TrimStart(' ');

                    if (trimmed.StartsWith("internal static readonly SearchValues<char>", StringComparison.Ordinal))
                    {
                        int startOffset = trimmed.IndexOf("SearchValues.Create(", StringComparison.Ordinal);
                        trimmed = trimmed.Slice(startOffset + "SearchValues.Create(".Length);
                        int endOffset = trimmed.LastIndexOf(");", StringComparison.Ordinal);
                        trimmed = trimmed.Slice(0, endOffset);

                        if (trimmed == "\"\"") continue;

                        searchValuesOfChar.Add(ParseCSharpLiteral(trimmed, out _));
                    }
                    else if (trimmed.StartsWith("internal static readonly SearchValues<string>", StringComparison.Ordinal))
                    {
                        // SearchValues.Create(["foo", "bar"], StringComparison.SomeType);
                        int startOffset = trimmed.IndexOf("SearchValues.Create([", StringComparison.Ordinal);
                        trimmed = trimmed.Slice(startOffset + "SearchValues.Create([".Length);
                        int endOffset = trimmed.LastIndexOf(");", StringComparison.Ordinal);
                        trimmed = trimmed.Slice(0, endOffset);
                        // "foo", "bar"], StringComparison.SomeType

                        StringComparison comparisonType = Enum.Parse<StringComparison>(trimmed.Slice(trimmed.LastIndexOf('.') + 1));

                        trimmed = trimmed.Slice(0, trimmed.LastIndexOf(']'));
                        // "foo", "bar"

                        ArgumentOutOfRangeException.ThrowIfZero(trimmed.Length);

                        List<string> values = [];

                        while (!trimmed.IsEmpty)
                        {
                            if (trimmed.StartsWith(", ", StringComparison.Ordinal))
                            {
                                trimmed = trimmed.Slice(2);
                            }

                            values.Add(ParseCSharpLiteral(trimmed, out int indexOfEndingQuote));

                            trimmed = trimmed.Slice(indexOfEndingQuote + 1);
                        }

                        searchValuesOfString.Add((values.ToArray(), comparisonType));
                    }
                }
            }

            entry.SearchValuesOfChar = searchValuesOfChar.ToArray();
            entry.SearchValuesOfString = searchValuesOfString.ToArray();
        }

        static string ParseCSharpLiteral(ReadOnlySpan<char> literal, out int indexOfEndingQuote)
        {
            ArgumentOutOfRangeException.ThrowIfZero(literal.Length);
            ArgumentOutOfRangeException.ThrowIfNotEqual(literal[0], '"');

            indexOfEndingQuote = -1;
            StringBuilder sb = new();

            for (int i = 1; i < literal.Length; i++)
            {
                if (literal[i] == '\\' && i + 1 < literal.Length)
                {
                    if (literal[i + 1] == 'u' && i + 5 < literal.Length)
                    {
                        char unicode = (char)ushort.Parse(literal.Slice(i + 2, 4), NumberStyles.HexNumber);
                        sb.Append(unicode);
                        i += 5;
                        continue;
                    }

                    char replacement = literal[i + 1] switch
                    {
                        '\"' => '\"',
                        'a' => '\a',
                        'b' => '\b',
                        'v' => '\v',
                        't' => '\t',
                        'n' => '\n',
                        'f' => '\f',
                        'r' => '\r',
                        'e' => (char)27,
                        '\\' => '\\',
                        '0' => '\0',
                        _ => throw new NotImplementedException(literal[i + 1].ToString())
                    };
                    sb.Append(replacement);
                    i++;
                }
                else if (literal[i] == '"')
                {
                    indexOfEndingQuote = i;
                    break;
                }
                else
                {
                    sb.Append(literal[i]);
                }
            }

            ArgumentOutOfRangeException.ThrowIfNegative(indexOfEndingQuote);
            return sb.ToString();
        }
    }

    private async Task UploadResultsAsync(RegexEntry[] entries)
    {
        PendingTasks.Enqueue(Task.Run(async () =>
        {
            using (ZipArchive archive = ZipFile.Open("Results.zip", ZipArchiveMode.Create))
            {
                ZipArchiveEntry entry = archive.CreateEntry("Results.json", CompressionLevel.Optimal);
                using Stream jsonEntryStream = entry.Open();
                JsonSerializer.Serialize(jsonEntryStream, entries, s_jsonOptions);
            }

            await UploadArtifactAsync("Results.zip");
        }));

        if (entries.Any(e => e.ShortDiff is not null))
        {
            string shortExample = GenerateExamplesMarkdown(entries, GitHubHelpers.CommentLengthLimit / 2, maxEntries: 10);
            string longExample = GenerateExamplesMarkdown(entries, GitHubHelpers.GistLengthLimit, maxEntries: int.MaxValue);

            await UploadTextArtifactAsync("ShortExampleDiffs.md", shortExample);

            if (shortExample != longExample)
            {
                await UploadTextArtifactAsync("LongExampleDiffs.md", longExample);
            }
        }

        static string GenerateExamplesMarkdown(RegexEntry[] entries, int maxMarkdownLength, int maxEntries)
        {
            StringBuilder sb = new();

            int entriesIncluded = 0;

            foreach (RegexEntry entry in entries)
            {
                if (entry.ShortDiff is not { } diff)
                {
                    continue;
                }

                if (sb.Length + diff.Length > maxMarkdownLength)
                {
                    continue;
                }

                int startLength = sb.Length;

                string options = entry.Regex.Options.ToString();
                options = int.TryParse(options, out _)
                    ? $"(RegexOptions){(int)entry.Regex.Options}"
                    : string.Join(" | ", options.Split(", ").Select(opt => $"{nameof(RegexOptions)}.{opt}"));

                string patternLiteral = SymbolDisplay.FormatLiteral(entry.Regex.Pattern, quote: true);

                string friendlyName = patternLiteral;
                if (friendlyName.Length > 50)
                {
                    friendlyName = $"{friendlyName.AsSpan(0, 45)} ...\"";
                }

                sb.AppendLine("<details>");
                sb.AppendLine($"<summary>{WebUtility.HtmlEncode(friendlyName)} ({entry.Regex.Count} uses)</summary>");
                sb.AppendLine();
                sb.AppendLine("```c#");
                sb.AppendLine($"[GeneratedRegex({patternLiteral}, {options})]");
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("```diff");
                sb.AppendLine(diff);
                sb.AppendLine("```");
                sb.AppendLine();
                sb.AppendLine("</details>");

                if (sb.Length > maxMarkdownLength && startLength != 0)
                {
                    sb.Length = startLength;
                    break;
                }

                if (++entriesIncluded == maxEntries)
                {
                    break;
                }
            }

            return sb.ToString();
        }
    }

    private record KnownPattern(string Pattern, RegexOptions Options, int Count);

    private record EntryWithGeneratedSource(string Pattern, RegexOptions Options, int Count, string OutputSource);

    private sealed class RegexEntry
    {
        public required KnownPattern Regex { get; set; }
        public required string MainSource { get; set; }
        public required string PrSource { get; set; }
        public string? FullDiff { get; set; }
        public string? ShortDiff { get; set; }
        public string[]? SearchValuesOfChar { get; set; }
        public (string[] Values, StringComparison ComparisonType)[]? SearchValuesOfString { get; set; }
    }
}