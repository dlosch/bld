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

    /// <summary>
    /// Regression: an OutDir that is the configuration directory itself (legacy projects,
    /// AppendTargetFrameworkToOutputPath=false) was marked as a whole even with --non-current, which
    /// deleted the project's current output. Only a stale TFM directory below it may go.
    /// </summary>
    [Fact]
    public async Task NonCurrent_KeepsAFlatConfigurationDirectory_AndMarksOnlyStaleTfmsBelowIt() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "Flat");
        var cfgDir = Path.Combine(projDir, "bin", "Debug");
        var stale = Path.Combine(cfgDir, "net6.0");
        var current = Path.Combine(cfgDir, "net10.0");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(current);
        await File.WriteAllTextAsync(Path.Combine(cfgDir, "Flat.dll"), "");
        var csproj = Path.Combine(projDir, "Flat.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var options = new CleaningOptions { CleanObjDirectory = false, CleanOnlyNonCurrentTfms = true };
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), options, errorSink);

            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj,
                ProjectName = "Flat",
                TargetFramework = "net10.0",
                Configuration = "Debug",
                OutDir = cfgDir + Path.DirectorySeparatorChar,
                BaseOutputPath = Path.Combine(projDir, "bin") + Path.DirectorySeparatorChar,
            });
            await processor.ProcessDirs();

            var marked = processor.GetMarkedDirectories().Keys.ToList();
            Assert.DoesNotContain(marked, k => SamePath(k, cfgDir));
            Assert.DoesNotContain(marked, k => SamePath(k, current));
            Assert.Contains(marked, k => SamePath(k, stale));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// A1.2: --non-current has to reach obj the same way it reaches bin. obj mirrors
    /// obj/&lt;config&gt;/&lt;tfm&gt;, so only the stale TFM folder goes; the current one, the config and obj
    /// roots, and the restore artifacts at the obj root all stay.
    /// </summary>
    [Fact]
    public async Task NonCurrent_Obj_MarksOnlyStaleTfmFolders_AndKeepsRestoreArtifacts() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var objDir = Path.Combine(projDir, "obj");
        var cfgDir = Path.Combine(objDir, "Debug");
        var stale = Directory.CreateDirectory(Path.Combine(cfgDir, "net6.0")).FullName;
        var current = Directory.CreateDirectory(Path.Combine(cfgDir, "net10.0")).FullName;
        await File.WriteAllTextAsync(Path.Combine(objDir, "project.assets.json"), "{}");
        var csproj = Path.Combine(projDir, "App.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var options = new CleaningOptions { CleanObjDirectory = true, CleanOnlyNonCurrentTfms = true };
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), options, errorSink);

            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net10.0", Configuration = "Debug",
                IntermediateOutputPath = objDir,
            });
            await processor.ProcessDirs();

            var marked = processor.GetMarkedDirectories().Keys.ToList();
            Assert.Contains(marked, k => SamePath(k, stale));
            Assert.DoesNotContain(marked, k => SamePath(k, current));
            Assert.DoesNotContain(marked, k => SamePath(k, cfgDir));
            Assert.DoesNotContain(marked, k => SamePath(k, objDir));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// A1.2: publish, package and TestResults have no TFM to be stale by, so --non-current leaves them
    /// alone rather than treating the whole category as current output.
    /// </summary>
    [Fact]
    public async Task NonCurrent_DoesNotMarkPublishOrTestResults() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var publishDir = Directory.CreateDirectory(Path.Combine(root, "deploy")).FullName;
        Directory.CreateDirectory(Path.Combine(projDir, "TestResults"));
        var csproj = Path.Combine(projDir, "App.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var options = new CleaningOptions { CleanObjDirectory = false, CleanPublishDirectory = true, CleanTestResults = true, CleanOnlyNonCurrentTfms = true };
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), options, errorSink);

            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net10.0", Configuration = "Debug", PublishDir = publishDir,
            });
            await processor.ProcessDirs();

            var result = processor.GetResult();
            Assert.DoesNotContain(result.Directories, d => d.Category == CleanCategory.Publish);
            Assert.DoesNotContain(result.Directories, d => d.Category == CleanCategory.TestResults);
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// A1.3: AppendTargetFrameworkToOutputPath=false with a RuntimeIdentifier gives
    /// bin/&lt;Configuration&gt;/&lt;rid&gt;/ — a RID leaf under a configuration parent. It used to land in
    /// "output layout not recognized"; now it is cleaned like the configuration directory.
    /// </summary>
    [Fact]
    public async Task RidLeafUnderConfiguration_IsRecognisedAndCleaned() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var ridDir = Directory.CreateDirectory(Path.Combine(projDir, "bin", "Debug", "linux-x64")).FullName;
        await File.WriteAllTextAsync(Path.Combine(ridDir, "App.dll"), "");
        var csproj = Path.Combine(projDir, "App.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);

            var full = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), new CleaningOptions { CleanObjDirectory = false }, errorSink);
            await full.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net10.0", Configuration = "Debug", OutDir = ridDir + Path.DirectorySeparatorChar,
            });
            await full.ProcessDirs();
            Assert.Contains(full.GetMarkedDirectories().Keys, k => SamePath(k, ridDir));

            // --non-current: there is no TFM under the configuration, so the RID output is the current
            // output and stays.
            var nonCurrent = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), new CleaningOptions { CleanObjDirectory = false, CleanOnlyNonCurrentTfms = true }, errorSink);
            await nonCurrent.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net10.0", Configuration = "Debug", OutDir = ridDir + Path.DirectorySeparatorChar,
            });
            await nonCurrent.ProcessDirs();
            Assert.DoesNotContain(nonCurrent.GetMarkedDirectories().Keys, k => SamePath(k, ridDir));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>
    /// A1.4: deleting bin/Debug/net8.0 leaves bin/Debug empty; the delete processor sweeps that parent
    /// away but keeps the bin root, so a clean does not leave hollow shells behind.
    /// </summary>
    [Fact]
    public async Task Delete_RemovesEmptiedParents_ButKeepsTheBinRoot() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var binDir = Path.Combine(projDir, "bin");
        var cfgDir = Path.Combine(binDir, "Debug");
        var tfmDir = Directory.CreateDirectory(Path.Combine(cfgDir, "net8.0")).FullName;
        await File.WriteAllTextAsync(Path.Combine(tfmDir, "App.dll"), "x");
        var csproj = Path.Combine(projDir, "App.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var options = new CleaningOptions { CleanObjDirectory = false, Force = true };
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), options, errorSink);
            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net8.0", Configuration = "Debug", OutDir = tfmDir + Path.DirectorySeparatorChar,
            });
            await processor.ProcessDirs();

            await new MarkDeleteResultDeleteProcessor(console, errorSink, options).ProcessAsync(processor.GetResult());

            Assert.False(Directory.Exists(tfmDir));
            Assert.False(Directory.Exists(cfgDir));
            Assert.True(Directory.Exists(binDir));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    /// <summary>A1.4: the generated script carries the matching best-effort rmdir for the emptied parent.</summary>
    [Fact]
    public async Task BatchScript_SweepsTheEmptiedParent() {
        var root = Path.Combine(Path.GetTempPath(), "bld_mdp_" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "App");
        var cfgDir = Path.Combine(projDir, "bin", "Debug");
        var tfmDir = Directory.CreateDirectory(Path.Combine(cfgDir, "net8.0")).FullName;
        await File.WriteAllTextAsync(Path.Combine(tfmDir, "App.dll"), "x");
        var csproj = Path.Combine(projDir, "App.csproj");
        await File.WriteAllTextAsync(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var script = Path.Combine(root, OperatingSystem.IsWindows() ? "clean.cmd" : "clean.sh");

        try {
            var console = new TestConsole();
            var errorSink = new ErrorSink(console);
            var options = new CleaningOptions { CleanObjDirectory = false, OutputFile = script };
            var processor = new MarkDeleteProcessor(console, new FileSystem(console, errorSink), options, errorSink);
            await processor.ProcessAsync(new ProjCfg(new Proj(csproj, null), "Debug"), new ProjectInfo {
                ProjectPath = csproj, ProjectName = "App", TargetFramework = "net8.0", Configuration = "Debug", OutDir = tfmDir + Path.DirectorySeparatorChar,
            });
            await processor.ProcessDirs();

            await new MarkDeleteResultBatchFileProcessor(console, errorSink, options).ProcessAsync(processor.GetResult());

            var text = await File.ReadAllTextAsync(script);
            Assert.Contains("Debug", text);
            Assert.Contains(OperatingSystem.IsWindows() ? "rmdir /q" : "rmdir -- ", text);
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
