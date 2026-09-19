using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.IO.Compression;

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

    [Fact]
    public async Task TestResults_AreMarkedNextToTheProjectAndTheSolutionWithTheFlag() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "src", "App.Tests");
        var projResults = Directory.CreateDirectory(Path.Combine(projDir, "TestResults")).FullName;
        var slnResults = Directory.CreateDirectory(Path.Combine(root, "TestResults")).FullName;
        var csproj = Path.Combine(projDir, "App.Tests.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), new CleaningOptions { CleanObjDirectory = false, CleanTestResults = true }, errorSink);
            var cfg = new ProjCfg(new Proj(csproj, new Sln(Path.Combine(root, "App.sln"))), "Debug");

            await processor.ProcessAsync(cfg, new ProjectInfo { ProjectPath = csproj, ProjectName = "App.Tests", TargetFramework = "net8.0", Configuration = "Debug" });
            await processor.ProcessDirs();

            var result = processor.GetResult();
            Assert.Equal(2, result.Directories.Count);
            Assert.All(result.Directories, d => Assert.Equal(CleanCategory.TestResults, d.Category));
            Assert.Contains(result.Directories, d => SamePath(d.Directory.FullName, projResults));
            Assert.Contains(result.Directories, d => SamePath(d.Directory.FullName, slnResults));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public async Task Interactive_MarksEveryCategoryWhateverTheFlagsSay() {
        // The flags only decide what starts out checked in the picker; the marking has to offer it all.
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var objDir = Directory.CreateDirectory(Path.Combine(projDir, "obj")).FullName;
        var publishDir = Directory.CreateDirectory(Path.Combine(root, "deploy")).FullName;
        var testResults = Directory.CreateDirectory(Path.Combine(projDir, "TestResults")).FullName;
        var csproj = Path.Combine(projDir, "App.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var options = new CleaningOptions { Interactive = true, CleanObjDirectory = false, CleanPublishDirectory = false, CleanTestResults = false };
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), options, errorSink);
            var info = new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net8.0", Configuration = "Debug",
                IntermediateOutputPath = objDir, PublishDir = publishDir,
            };

            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), info);
            await processor.ProcessDirs();

            var byCategory = processor.GetResult().Directories.ToDictionary(d => d.Category, d => d.Directory.FullName);
            Assert.True(SamePath(byCategory[CleanCategory.Obj], objDir));
            Assert.True(SamePath(byCategory[CleanCategory.Publish], publishDir));
            Assert.True(SamePath(byCategory[CleanCategory.TestResults], testResults));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Theory]
    [InlineData("App.1.0.0.nupkg", "App", true)]
    [InlineData("app.1.0.0.NUPKG", "App", true)]
    [InlineData("App.1.0.0.snupkg", "App", true)]
    [InlineData("App.1.0.0.symbols.nupkg", "App", true)]
    [InlineData("App.2.0.0-beta.1+build.5.nupkg", "App", true)]
    [InlineData("App.Core.1.0.0.nupkg", "App", false)]
    [InlineData("App.1.0.0.nupkg", "App.Core", false)]
    [InlineData("App.nupkg", "App", false)]
    [InlineData("App.1.0.0.nupkg.bak", "App", false)]
    [InlineData("MyApp.1.0.0.nupkg", "App", false)]
    [InlineData("App.1.0.0.zip", "App", false)]
    public void IsPackageFileOf_MatchesOnlyThisIdsOwnPackages(string fileName, string packageId, bool expected) {
        Assert.Equal(expected, MarkDeleteProcessor.IsPackageFileOf(fileName, packageId));
    }

    [Fact]
    public async Task PackageOutput_MarksOnlyThisProjectsPackageFiles_NeverTheDirectory() {
        // A local feed: this project's packages next to other projects' packages and unrelated files.
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var feed = Directory.CreateDirectory(Path.Combine(root, "local-nuget")).FullName;
        var csproj = Path.Combine(projDir, "App.csproj");
        Directory.CreateDirectory(projDir);
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var own = new[] { "App.1.0.0.nupkg", "App.1.0.0.snupkg", "App.2.0.0-beta.1.nupkg" }.Select(n => Path.Combine(feed, n)).ToList();
        WriteNupkg(own[0], "App", "1.0.0");
        WriteNupkg(own[1], "App", "1.0.0");
        WriteNupkg(own[2], "App", "2.0.0-beta.1");
        WriteNupkg(Path.Combine(feed, "App.Core.1.0.0.nupkg"), "App.Core", "1.0.0");
        WriteNupkg(Path.Combine(feed, "Other.1.0.0.nupkg"), "Other", "1.0.0");
        await File.WriteAllTextAsync(Path.Combine(feed, "README.md"), "not a package");
        // A NuGet id may end in a numeric segment, so this file name reads as App 5.2.0 just as well
        // as App.5 2.0. Its manifest says App.5, and App must not claim it.
        WriteNupkg(Path.Combine(feed, "App.5.2.0.nupkg"), "App.5", "2.0");
        // Named like one of App's packages but not a package at all: unreadable means untouched.
        await File.WriteAllBytesAsync(Path.Combine(feed, "App.9.0.0.nupkg"), new byte[16]);
        // Only files directly in the directory count.
        var nested = Path.Combine(Directory.CreateDirectory(Path.Combine(feed, "archive")).FullName, "App.0.9.0.nupkg");
        WriteNupkg(nested, "App", "0.9.0");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), new CleaningOptions { CleanObjDirectory = false, CleanPublishDirectory = true }, errorSink);
            var info = new ProjectInfo { ProjectPath = csproj, ProjectName = "App", PackageId = "App", TargetFramework = "net8.0", Configuration = "Debug", PackageOutputPath = feed };

            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), info);
            await processor.ProcessDirs();

            var result = processor.GetResult();
            Assert.Empty(result.Directories);
            Assert.DoesNotContain(processor.GetMarkedDirectories().Keys, k => SamePath(k, feed));
            Assert.Equal(own.Select(Norm).OrderBy(p => p), result.Files.Select(f => Norm(f.File.FullName)).OrderBy(p => p));
            Assert.All(result.Files, f => Assert.Equal(CleanCategory.Package, f.Category));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public async Task PackageOutput_ProjectThatDoesNotPackMarksNothing() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App.Tests");
        var feed = Directory.CreateDirectory(Path.Combine(root, "local-nuget")).FullName;
        var csproj = Path.Combine(projDir, "App.Tests.csproj");
        Directory.CreateDirectory(projDir);
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WriteNupkg(Path.Combine(feed, "App.Tests.1.0.0.nupkg"), "App.Tests", "1.0.0");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), new CleaningOptions { CleanObjDirectory = false, CleanPublishDirectory = true }, errorSink);
            var info = new ProjectInfo { ProjectPath = csproj, ProjectName = "App.Tests", PackageId = "App.Tests", IsPackable = false, TargetFramework = "net8.0", Configuration = "Debug", PackageOutputPath = feed };

            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), info);
            await processor.ProcessDirs();

            var result = processor.GetResult();
            Assert.True(result.IsEmpty);
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// A real, minimal package: the marking reads the id out of the .nuspec inside the archive, so a
    /// file of random bytes with the right name is not a stand-in for one.
    /// </summary>
    private static void WriteNupkg(string path, string id, string version) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry($"{id}.nuspec").Open());
        writer.Write($"""
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>tests</authors>
                <description>tests</description>
              </metadata>
            </package>
            """);
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
