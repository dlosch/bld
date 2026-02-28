using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.Reflection;

namespace bld.Tests;

/// <summary>
/// Tests for npm/Node.js project detection in the scanner and model layers.
/// </summary>
public class NpmProjectDetectionTests {

    #region SlnScanner npm detection

    [Fact]
    public void IsNpmProjectFile_PackageJson_ReturnsTrue() {
        Assert.True(SlnScanner.IsNpmProjectFile("package.json"));
    }

    [Fact]
    public void IsNpmProjectFile_FullPath_ReturnsTrue() {
        Assert.True(SlnScanner.IsNpmProjectFile("/some/path/package.json"));
    }

    [Fact]
    public void IsNpmProjectFile_CaseInsensitive_ReturnsTrue() {
        Assert.True(SlnScanner.IsNpmProjectFile("Package.JSON"));
    }

    [Fact]
    public void IsNpmProjectFile_OtherJsonFile_ReturnsFalse() {
        Assert.False(SlnScanner.IsNpmProjectFile("tsconfig.json"));
    }

    [Fact]
    public void IsNpmProjectFile_CsprojFile_ReturnsFalse() {
        Assert.False(SlnScanner.IsNpmProjectFile("myproject.csproj"));
    }

    [Fact]
    public void IsProjectFile_PackageJson_ReturnsFalse() {
        // package.json should NOT be detected as a traditional MSBuild project file
        Assert.False(SlnScanner.IsProjectFile("package.json"));
    }

    #endregion

    #region ProjectType enum

    [Fact]
    public void ProjectType_HasNpmValue() {
        Assert.True(Enum.IsDefined(typeof(ProjectType), ProjectType.Npm));
    }

    #endregion

    #region Dir.ProjType for npm

    [Fact]
    public void Dir_ProjType_PackageJson_ReturnsNpm() {
        var dir = new Dir(
            new List<(string, DirType)> { ("/some/path/node_modules", DirType.OutDir) },
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { { "/some/path/package.json", "my-app" } },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );
        Assert.Equal(ProjectType.Npm, dir.ProjType);
    }

    [Fact]
    public void Dir_ProjType_Csproj_ReturnsCsproj() {
        var dir = new Dir(
            new List<(string, DirType)> { ("/some/path/bin/Debug/net10.0", DirType.OutDir) },
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { { "/some/path/myapp.csproj", "myapp" } },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        );
        Assert.Equal(ProjectType.Csproj, dir.ProjType);
    }

    #endregion

    #region Scanner enumeration with npm projects

    [Fact]
    public async Task Scanner_Enumerate_FindsPackageJson() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-test-{Guid.NewGuid():N}");
        try {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "package.json"), "{\"name\": \"test\"}");

            var options = new CleaningOptions { Depth = 3 };
            var errorSink = new ErrorSink(new TestConsoleOutput());
            var scanner = new SlnScanner(options, errorSink);

            var results = new List<string>();
            await foreach (var item in scanner.Enumerate(tempDir)) {
                results.Add(item);
            }

            Assert.Contains(results, f => Path.GetFileName(f) == "package.json");
        }
        finally {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task Scanner_Enumerate_IgnoresPackageJsonInNodeModules() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-test-{Guid.NewGuid():N}");
        try {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "package.json"), "{\"name\": \"test\"}");

            var nodeModulesDir = Path.Combine(tempDir, "node_modules", "some-pkg");
            Directory.CreateDirectory(nodeModulesDir);
            File.WriteAllText(Path.Combine(nodeModulesDir, "package.json"), "{\"name\": \"some-pkg\"}");

            var options = new CleaningOptions { Depth = 3 };
            var errorSink = new ErrorSink(new TestConsoleOutput());
            var scanner = new SlnScanner(options, errorSink);

            var results = new List<string>();
            await foreach (var item in scanner.Enumerate(tempDir)) {
                results.Add(item);
            }

            // Should find the root package.json but not the one in node_modules
            var packageJsonFiles = results.Where(f => Path.GetFileName(f) == "package.json").ToList();
            Assert.Single(packageJsonFiles);
            Assert.DoesNotContain(packageJsonFiles, f => f.Contains("node_modules"));
        }
        finally {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    #endregion

    private class TestConsoleOutput : IConsoleOutput {
        public void WriteInfo(string message) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message, Exception? exception = default) { }
        public void WriteDebug(string message) { }
        public void WriteVerbose(string message) { }
        public void WriteTable(Spectre.Console.Table table) { }
        public void WriteRule(string title) { }
        public bool Confirm(string message, bool defaultValue = false) => defaultValue;
        public T Prompt<T>(Spectre.Console.SelectionPrompt<T> prompt) where T : notnull => default!;
        public void StartProgress(string description, Action<Spectre.Console.ProgressContext> action) { }
        public Task StartProgressAsync(string description, Func<Spectre.Console.ProgressContext, Task> action) => Task.CompletedTask;
        public Task StartStatusAsync(string description, Func<Spectre.Console.StatusContext, Task> action) => Task.CompletedTask;
        public void WriteException(Exception exception) { }
        public void WriteOutput(string caption, string? content = default) { }
        public void WriteHeader(string caption, string? additionaltext = default) { }
    }
}
