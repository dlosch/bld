using bld.Infrastructure;
using bld.Models;
using bld.Services;

namespace bld.Tests;

/// <summary>
/// Behavioral tests for the destructive marking engine. clean/stats delete directories,
/// so "which directories get marked" is safety-critical and previously had no coverage.
/// </summary>
public class MarkDeleteProcessorTests {

    /// <summary>
    /// Regression: ProcessDirs() used `return` instead of `continue` when a candidate path
    /// was unsafe, so the first unsafe path aborted the whole run and silently left every
    /// remaining output directory unprocessed. Here the OutDir resolves to the project
    /// directory itself (unsafe — the .csproj lives below it); the project's obj directory
    /// must still be marked.
    /// </summary>
    [Fact]
    public async Task ProcessDirs_SkipsUnsafePath_ButStillMarksRemainingOutputDirs() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "MyApp");
        var objDir = Path.Combine(projDir, "obj");
        Directory.CreateDirectory(objDir);
        var csproj = Path.Combine(projDir, "MyApp.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var fileSystem = new FileSystem(console, errorSink);
            var options = new CleaningOptions { CleanObjDirectory = true };
            var processor = new MarkDeleteProcessor(console, fileSystem, options, errorSink);

            var info = new ProjectInfo {
                ProjectPath = csproj,
                ProjectName = "MyApp",
                TargetFramework = "net8.0",
                Configuration = "Debug",
                OutDir = projDir,                 // unsafe: the .csproj lives below this path
                IntermediateOutputPath = objDir,  // safe: must still be marked
            };
            var cfg = new ProjCfg(new Proj(csproj, null), "Debug");

            await processor.ProcessAsync(cfg, info);
            await processor.ProcessDirs();

            var marked = processor.GetMarkedDirectories().Keys.ToList();

            Assert.Contains(marked, k => SamePath(k, objDir));
            Assert.DoesNotContain(marked, k => SamePath(k, projDir));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
