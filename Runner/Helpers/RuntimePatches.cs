namespace Runner.Helpers;

internal static partial class RuntimePatches
{
    private const string RuntimeDir = "runtime";

    private const string MarvinFile = "src/libraries/System.Private.CoreLib/src/System/Marvin.cs";

    [GeneratedRegex(@"private static unsafe ulong GenerateSeed\(\)[\s\S]*?\n        \}")]
    private static partial Regex MarvinGenerateSeedRegex();

    private const string MarvinGenerateSeedReplacement =
        "private static ulong GenerateSeed() => 0xD1FFAB11Eul;";

    private static readonly string[] s_patchedFiles =
    [
        MarvinFile,
        "src/coreclr/jit/compiler.cpp",
        "src/coreclr/jit/compiler.h",
        "src/coreclr/jit/jitconfigvalues.h",
    ];

    public static async Task ApplyPatchesAsync(JobBase job, bool forJitDiff)
    {
        HashSet<string> prChangedFiles = new(
            await GitHelper.GetChangedFilesAsync(job, $"{job.BaselineRef}..pr", RuntimeDir),
            StringComparer.OrdinalIgnoreCase);

        await TryPatchMarvinAsync(job, prChangedFiles);

        if (forJitDiff)
        {
            await ApplyReleaseJitDisasmPatchAsync(job);
        }
    }

    private static async Task ApplyReleaseJitDisasmPatchAsync(JobBase job)
    {
        // PMI uses JitDisasm=* with JitDisasmAssemblies=<target>. Without this patch,
        // Release ignores the assembly filter and includes nondeterministic PMI/startup compilations.
        const string PatchName = "ReleaseJitDisasmAssemblies.patch";
        using Stream patch = typeof(RuntimePatches).Assembly.GetManifestResourceStream($"Runner.Helpers.Patches.{PatchName}")
            ?? throw new InvalidOperationException($"Missing embedded patch: {PatchName}");
        using var patchFile = new TempFile("patch");
        using (var file = File.Create(patchFile.Path))
        {
            await patch.CopyToAsync(file);
        }

        if (await job.RunProcessAsync("git", $"apply --reverse --check \"{patchFile.Path}\"",
            workDir: RuntimeDir, checkExitCode: false, suppressOutputLogs: true, suppressStartingLog: true) == 0)
        {
            await job.LogAsync($"[Patches] {PatchName} is already applied");
            return;
        }

        // Fail rather than silently produce unscoped diffs if upstream changes invalidate the patch.
        await job.RunProcessAsync("git", $"apply --check \"{patchFile.Path}\"", workDir: RuntimeDir, logPrefix: "Patches");
        await job.RunProcessAsync("git", $"apply \"{patchFile.Path}\"", workDir: RuntimeDir, logPrefix: "Patches");
        await job.LogAsync("[Patches] Enabled JitDisasmAssemblies in the Release JIT");
    }

    public static async Task RevertPatchesAsync(JobBase job)
    {
        // Restore any patched source files so subsequent `git switch`/builds see a clean tree.
        // Already-built artifacts are unaffected.
        foreach (string file in s_patchedFiles)
        {
            string path = $"{RuntimeDir}/{file}";
            if (!File.Exists(path))
            {
                continue;
            }

            await job.RunProcessAsync("git",
                $"checkout -- {file}",
                workDir: RuntimeDir,
                checkExitCode: false,
                suppressStartingLog: true);
        }
    }

    private static async Task TryPatchMarvinAsync(JobBase job, HashSet<string> prChangedFiles)
    {
        if (prChangedFiles.Contains(MarvinFile))
        {
            await job.LogAsync($"[Patches] Skipping Marvin patch - PR modifies {MarvinFile}");
            return;
        }

        string path = $"{RuntimeDir}/{MarvinFile}";
        if (!File.Exists(path))
        {
            await job.LogAsync($"[Patches] Skipping Marvin patch - file not found at {path}");
            return;
        }

        string content = await File.ReadAllTextAsync(path);

        if (!MarvinGenerateSeedRegex().IsMatch(content))
        {
            await job.LogAsync("[Patches] Skipping Marvin patch - GenerateSeed pattern not found");
            return;
        }

        string patched = MarvinGenerateSeedRegex().Replace(content, MarvinGenerateSeedReplacement, count: 1);

        await File.WriteAllTextAsync(path, patched);
        await job.LogAsync("[Patches] Replaced Marvin.GenerateSeed with constant 0xD1FFAB11E");
    }
}
