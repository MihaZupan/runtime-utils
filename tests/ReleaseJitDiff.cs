#:project ..\Runner\Runner.csproj
#:property PublishAot=false

// dotnet run --file tests\ReleaseJitDiff.cs -- <runtime-checkout> <release-core-root> <jitutils-bin>
// The core root must contain a Release JIT built with Helpers/Patches/ReleaseJitDisasmAssemblies.patch.
// jitutils/bin must contain jit-diff, jit-dasm-pmi, pmi, and jit-analyze (including dotnet/jitutils#440).
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Runner;

if (args.Length != 3)
{
    throw new ArgumentException("Expected runtime checkout, patched Release core root, and jitutils bin paths.");
}

string runtime = Path.GetFullPath(args[0]);
string coreRoot = Path.GetFullPath(args[1]);
string jitutils = Path.GetFullPath(args[2]);
string originalDirectory = Environment.CurrentDirectory;
string workspace = Directory.CreateTempSubdirectory("release-jit-diff-").FullName;
using var client = new HttpClient();
var job = new TestJob(client);

try
{
    Environment.CurrentDirectory = workspace;
    await TestPatchAsync();
    CopyDirectory(jitutils, Path.Combine("jitutils", "bin"));

    foreach (string branch in new[] { "main", "pr" })
    {
        string root = $"core-{branch}";
        Call<object?>("JitDiffUtils", "CreateCoreRootCloneForJitDiff", coreRoot, root);
        string jitBase = Path.Combine(root, "jit-base");
        Directory.CreateDirectory(jitBase);
        string jitName = OperatingSystem.IsWindows() ? "clrjit.dll" : "libclrjit.so";
        File.Copy(Path.Combine(root, jitName), Path.Combine(jitBase, jitName));

        string assemblies = Path.Combine(workspace, $"assemblies-{branch}");
        Directory.CreateDirectory(assemblies);
        string assembly = Path.Combine(assemblies, "ReleaseJitProbe.dll");
        string small = "x + 1";
        string large = "(x * x + 37) / (x | 1)";
        string source = $$"""
            using System.Runtime.CompilerServices;
            public static class ReleaseJitProbe
            {
                [MethodImpl(MethodImplOptions.NoInlining)]
                public static int Grow(int x) => {{(branch == "main" ? small : large)}};
                [MethodImpl(MethodImplOptions.NoInlining)]
                public static int Shrink(int x) => {{(branch == "main" ? large : small)}};
            }
            """;
        var compilation = CSharpCompilation.Create("ReleaseJitProbe",
            [CSharpSyntaxTree.ParseText(source)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));
        var emitted = compilation.Emit(assembly);
        Assert(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));

        Directory.CreateDirectory($"diffs-{branch}");
        await Call<Task>("JitDiffUtils", "RunJitDiffOnAssembliesAsync",
            job, root, jitBase, $"diffs-{branch}", new[] { assembly }, null, null, CancellationToken.None);

        string dasm = Path.Combine($"diffs-{branch}", "dasmset_1", "base", "ReleaseJitProbe.dasm");
        string[] headers = File.ReadLines(dasm)
            .Where(line => line.StartsWith("; Assembly listing for method ", StringComparison.Ordinal))
            .ToArray();
        Assert(headers.Length == 2, $"Expected only two target methods on {branch}, found {headers.Length}.");
        Assert(headers.All(line => line.StartsWith("; Assembly listing for method ReleaseJitProbe:", StringComparison.Ordinal)),
            "Release JIT ignored JitDisasmAssemblies and emitted PMI/startup methods.");
        Assert(File.ReadLines(dasm).Count(line => line.StartsWith("; Total bytes of code ", StringComparison.Ordinal) &&
            line.Contains(" for method ReleaseJitProbe:", StringComparison.Ordinal)) == 2,
            "Release code-size footers must retain method identity.");
    }

    string mainDasm = Path.Combine("diffs-main", "dasmset_1", "base");
    string prDasm = Path.Combine("diffs-pr", "dasmset_1", "base");
    string summary = await Call<Task<string>>("JitDiffUtils", "RunJitAnalyzeAsync", job, mainDasm, prDasm, 100);
    Assert(!summary.Contains("Infinity of base", StringComparison.Ordinal) &&
        !summary.Contains("-100.00 % of base", StringComparison.Ordinal), "Matched methods were reported as new/removed.");

    foreach (bool regressions in new[] { true, false })
    {
        var entries = Call<(string Description, string DasmFile, string Name)[]>(
            "JitDiffUtils", "ParseDiffAnalyzeEntries", summary, regressions);
        Assert(entries.Length == 1 && entries[0].Name.Contains(regressions ? ":Grow(" : ":Shrink(", StringComparison.Ordinal),
            $"Missing expected {(regressions ? "regression" : "improvement")}:\n{summary}");

        var (diffs, _) = await Call<Task<(string[] Diffs, bool NoisyDiffsRemoved)>>(
            "JitDiffUtils", "GetDiffMarkdownAsync", job, entries, mainDasm, prDasm, null, (Func<string, string>)(name => name), 20);
        Assert(diffs.Length == 1 && diffs[0].Contains("```diff", StringComparison.Ordinal) &&
            diffs[0].Contains("; Total bytes of code ", StringComparison.Ordinal), "Missing complete Markdown diff example.");
    }

    Console.WriteLine("Release JIT patch lifecycle, assembly filtering, code-size analysis, and both Markdown examples passed.");
}
finally
{
    Environment.CurrentDirectory = originalDirectory;
    if (OperatingSystem.IsWindows())
    {
        foreach (string file in Directory.EnumerateFiles(workspace, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
        }
    }
    Directory.Delete(workspace, recursive: true);
}

async Task TestPatchAsync()
{
    string[] paths = ["src/coreclr/jit/compiler.cpp", "src/coreclr/jit/compiler.h", "src/coreclr/jit/jitconfigvalues.h"];
    Directory.CreateDirectory("runtime");
    await job.RunProcessAsync("git", "init --quiet", workDir: "runtime");
    foreach (string path in paths)
    {
        List<string> lines = [];
        await job.RunProcessAsync("git", $"show HEAD:{path}", workDir: runtime, output: lines, suppressOutputLogs: true);
        string target = Path.Combine("runtime", path);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllLinesAsync(target, lines);
    }
    await job.RunProcessAsync("git", "add .", workDir: "runtime");
    string[] originals = paths.Select(path => File.ReadAllText(Path.Combine("runtime", path))).ToArray();

    await Call<Task>("RuntimePatches", "ApplyReleaseJitDisasmPatchAsync", job);
    string[] patched = paths.Select(path => File.ReadAllText(Path.Combine("runtime", path))).ToArray();
    Assert(patched[2].Contains("RELEASE_CONFIG_STRING(JitDisasmAssemblies,", StringComparison.Ordinal),
        "Embedded patch did not enable the release configuration.");
    await Call<Task>("RuntimePatches", "ApplyReleaseJitDisasmPatchAsync", job);
    Assert(paths.Select(path => File.ReadAllText(Path.Combine("runtime", path))).SequenceEqual(patched),
        "Applying an already-applied patch changed the source.");
    await Call<Task>("RuntimePatches", "RevertPatchesAsync", job);
    Assert(paths.Select(path => File.ReadAllText(Path.Combine("runtime", path)).ReplaceLineEndings("\n"))
        .SequenceEqual(originals.Select(text => text.ReplaceLineEndings("\n"))), "Patch cleanup did not restore the sources.");
}

static T Call<T>(string typeName, string methodName, params object?[] arguments)
{
    Type type = typeof(JobBase).Assembly.GetType($"Runner.Helpers.{typeName}", throwOnError: true)!;
    return (T)type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, arguments)!;
}

static void CopyDirectory(string source, string destination)
{
    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

sealed class TestJob(HttpClient client) : JobBase(client)
{
    protected override Task RunJobCoreAsync() => throw new NotSupportedException();
}
