using bld.Infrastructure;
using bld.Models;
using bld.Services;

namespace bld.Tests;

/// <summary>
/// Marking behaviour for the SDK artifacts layout (UseArtifactsOutput=true) and for publish/pack output.
/// Neither was covered before: multi-targeted artifacts output was silently skipped, and PublishDir was never read.
/// </summary>
public class ArtifactsLayoutTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bld_art_" + Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    // ----- pivot parsing -------------------------------------------------------------------------

    [Theory]
    [InlineData("debug", "Debug", true, null)]
    [InlineData("Release", "Release", true, null)]
    [InlineData("debug_net8.0", "Debug", true, "net8.0")]
    [InlineData("release_net8.0-windows", "Release", true, "net8.0-windows")]
    [InlineData("release_net8.0_win-x64", "Release", true, "net8.0")]
    [InlineData("release_linux-musl-arm64", "Release", true, null)]
    [InlineData("release_osx.12-arm64", "Release", true, null)]
    [InlineData("tools", "Debug", false, null)]
    [InlineData("debug_backup", "Debug", false, null)]
    [InlineData("debug_net8.0_backup", "Debug", false, null)]
    [InlineData("staging", "Debug", false, null)]
    [InlineData("debug_", "Debug", false, null)]
    public void TryParseArtifactsPivots_RecognizesConfigTfmRid(string name, string config, bool expected, string? expectedTfm) {
        var ok = MarkDeleteProcessor.TryParseArtifactsPivots(name, new[] { config }, out var tfm);

        Assert.Equal(expected, ok);
        Assert.Equal(expectedTfm, tfm);
    }

    // ----- artifacts/bin -------------------------------------------------------------------------

    [Fact]
    public async Task ArtifactsLayout_MultiTargeted_MarksEveryTfmPivot() {
        var (csproj, artifacts) = CreateArtifactsProject("Multi");
        var net8 = CreateDir(artifacts, "bin", "Multi", "debug_net8.0");
        var net10 = CreateDir(artifacts, "bin", "Multi", "debug_net10.0");
        var tools = CreateDir(artifacts, "bin", "Multi", "tools");

        var marked = await Mark(new CleaningOptions(), ArtifactsInfo(csproj, artifacts, "Multi", tfms: new[] { "net8.0", "net10.0" }));

        Assert.Contains(marked, k => SamePath(k, net8));
        Assert.Contains(marked, k => SamePath(k, net10));
        Assert.DoesNotContain(marked, k => SamePath(k, tools));
        Assert.DoesNotContain(marked, k => SamePath(k, Path.Combine(artifacts, "bin", "Multi")));
    }

    [Fact]
    public async Task ArtifactsLayout_SingleTarget_MarksConfigPivot() {
        var (csproj, artifacts) = CreateArtifactsProject("Single");
        var debug = CreateDir(artifacts, "bin", "Single", "debug");

        var marked = await Mark(new CleaningOptions(), ArtifactsInfo(csproj, artifacts, "Single", tfm: "net10.0"));

        Assert.Contains(marked, k => SamePath(k, debug));
    }

    [Fact]
    public async Task ArtifactsLayout_RuntimeIdentifierPivot_IsMarked() {
        var (csproj, artifacts) = CreateArtifactsProject("Multi");
        var rid = CreateDir(artifacts, "bin", "Multi", "release_net8.0_win-x64");

        var marked = await Mark(new CleaningOptions(), ArtifactsInfo(csproj, artifacts, "Multi", tfms: new[] { "net8.0" }, configuration: "Release"));

        Assert.Contains(marked, k => SamePath(k, rid));
    }

    [Fact]
    public async Task ArtifactsLayout_NonCurrent_MarksOnlyStaleTfmPivots() {
        var (csproj, artifacts) = CreateArtifactsProject("Multi");
        var net8 = CreateDir(artifacts, "bin", "Multi", "debug_net8.0");
        var net10 = CreateDir(artifacts, "bin", "Multi", "debug_net10.0");
        var single = CreateDir(artifacts, "bin", "Multi", "debug");

        var marked = await Mark(new CleaningOptions { CleanOnlyNonCurrentTfms = true }, ArtifactsInfo(csproj, artifacts, "Multi", tfms: new[] { "net10.0" }));

        Assert.Contains(marked, k => SamePath(k, net8));
        Assert.DoesNotContain(marked, k => SamePath(k, net10));
        Assert.DoesNotContain(marked, k => SamePath(k, single));
    }

    [Fact]
    public async Task ArtifactsLayout_ProjectFileBelowArtifactsDir_IsSkippedWithWarning() {
        var (csproj, artifacts) = CreateArtifactsProject("Multi");
        var net8 = CreateDir(artifacts, "bin", "Multi", "debug_net8.0");
        File.WriteAllText(Path.Combine(net8, "Nested.csproj"), "<Project />");

        var marked = await Mark(new CleaningOptions(), ArtifactsInfo(csproj, artifacts, "Multi", tfms: new[] { "net8.0" }));

        Assert.DoesNotContain(marked, k => SamePath(k, net8));
        Assert.Contains(_console.Messages, m => m.Level == "Warning" && m.Message.Contains("contains project or solution files"));
    }

    // ----- unrecognized classic layout -----------------------------------------------------------

    [Fact]
    public async Task UnrecognizedOutputLayout_WarnsOncePerProject() {
        var csproj = CreateProject("Odd");
        var outDir = CreateDir(Path.GetDirectoryName(csproj)!, "out", "weird");

        var marked = await Mark(new CleaningOptions(),
            Info(csproj, "Odd", tfm: "net8.0", configuration: "Debug", outDir: outDir),
            Info(csproj, "Odd", tfm: "net8.0", configuration: "Release", outDir: outDir));

        Assert.Empty(marked);
        var warnings = _console.Messages.Where(m => m.Level == "Warning" && m.Message.Contains("output layout not recognized")).ToList();
        Assert.Single(warnings);
        Assert.Contains(outDir, warnings[0].Message);
    }

    [Fact]
    public async Task ClassicLayout_StillMarksTfmDirectory() {
        var csproj = CreateProject("Classic");
        var tfmDir = CreateDir(Path.GetDirectoryName(csproj)!, "bin", "Debug", "net8.0");

        var marked = await Mark(new CleaningOptions(), Info(csproj, "Classic", tfm: "net8.0", configuration: "Debug", outDir: tfmDir));

        Assert.Contains(marked, k => SamePath(k, tfmDir));
        Assert.DoesNotContain(_console.Messages, m => m.Level == "Warning");
    }

    // ----- --publish -----------------------------------------------------------------------------

    [Fact]
    public async Task Publish_ExplicitPublishDirOutsideBin_RequiresOption() {
        var csproj = CreateProject("App");
        var projDir = Path.GetDirectoryName(csproj)!;
        var tfmDir = CreateDir(projDir, "bin", "Release", "net8.0");
        var publishDir = CreateDir(_root, "deploy", "App");

        var info = Info(csproj, "App", tfm: "net8.0", configuration: "Release", outDir: tfmDir, publishDir: publishDir);

        var without = await Mark(new CleaningOptions(), info);
        Assert.DoesNotContain(without, k => SamePath(k, publishDir));

        var with = await Mark(new CleaningOptions { CleanPublishDirectory = true }, info);
        Assert.Contains(with, k => SamePath(k, publishDir));
        Assert.Contains(with, k => SamePath(k, tfmDir));
    }

    [Fact]
    public async Task Publish_DefaultPublishDirUnderBuildOutput_IsNotMarkedTwice() {
        var csproj = CreateProject("App");
        var projDir = Path.GetDirectoryName(csproj)!;
        var tfmDir = CreateDir(projDir, "bin", "Release", "net8.0");
        var publishDir = CreateDir(tfmDir, "publish");

        var marked = await Mark(new CleaningOptions { CleanPublishDirectory = true },
            Info(csproj, "App", tfm: "net8.0", configuration: "Release", outDir: tfmDir, publishDir: publishDir, packageOutputPath: tfmDir));

        Assert.Contains(marked, k => SamePath(k, tfmDir));
        Assert.DoesNotContain(marked, k => SamePath(k, publishDir));
        Assert.Single(marked);
    }

    [Fact]
    public async Task Publish_PackageOutputPathEqualToProjectDir_IsNeverMarked() {
        var csproj = CreateProject("Lib");
        var projDir = Path.GetDirectoryName(csproj)!;
        var tfmDir = CreateDir(projDir, "bin", "Release", "net8.0");

        var marked = await Mark(new CleaningOptions { CleanPublishDirectory = true },
            Info(csproj, "Lib", tfm: "net8.0", configuration: "Release", outDir: tfmDir, packageOutputPath: projDir));

        Assert.DoesNotContain(marked, k => SamePath(k, projDir));
        Assert.Contains(marked, k => SamePath(k, tfmDir));
        Assert.Contains(_console.Messages, m => m.Level == "Warning" && m.Message.Contains("Skipping") && m.Message.Contains(projDir));
    }

    [Fact]
    public async Task Publish_ArtifactsLayout_MarksPublishPivotsAndPackageDir() {
        var (csproj, artifacts) = CreateArtifactsProject("Single");
        CreateDir(artifacts, "bin", "Single", "release");
        var publish = CreateDir(artifacts, "publish", "Single", "release");
        // artifacts/package/<config>/ is shared by every project of the repo: only this project's own
        // package file may go, never the directory or another project's package.
        var package = CreateDir(artifacts, "package", "release");
        var own = Path.Combine(package, "Single.1.0.0.nupkg");
        var other = Path.Combine(package, "Other.1.0.0.nupkg");
        WriteNupkg(own, "Single", "1.0.0");
        WriteNupkg(other, "Other", "1.0.0");
        var foreign = CreateDir(artifacts, "publish", "Single", "notes");

        var info = ArtifactsInfo(csproj, artifacts, "Single", tfm: "net10.0", configuration: "Release") with {
            PublishDir = Path.Combine(artifacts, "publish", "Single", "release"),
            PackageOutputPath = package,
            PackageId = "Single",
        };

        var (without, withoutFiles) = await MarkAll(new CleaningOptions(), info);
        Assert.DoesNotContain(without, k => SamePath(k, publish));
        Assert.DoesNotContain(without, k => SamePath(k, package));
        Assert.Empty(withoutFiles);

        var (with, withFiles) = await MarkAll(new CleaningOptions { CleanPublishDirectory = true }, info);
        Assert.Contains(with, k => SamePath(k, publish));
        Assert.DoesNotContain(with, k => SamePath(k, package));
        Assert.DoesNotContain(with, k => SamePath(k, foreign));
        Assert.Equal(new[] { Norm(own) }, withFiles.Select(Norm));
    }

    // ----- helpers -------------------------------------------------------------------------------

    /// <summary>A real, minimal package: the marking reads the id out of the .nuspec inside it.</summary>
    private static void WriteNupkg(string path, string id, string version) {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
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

    private async Task<List<string>> Mark(CleaningOptions options, params ProjectInfo[] infos) =>
        (await MarkAll(options, infos)).Directories;

    private async Task<(List<string> Directories, List<string> Files)> MarkAll(CleaningOptions options, params ProjectInfo[] infos) {
        _console.Messages.Clear();
        var errorSink = new ErrorSink(_console);
        var fileSystem = new FileSystem(_console, errorSink);
        var processor = new MarkDeleteProcessor(_console, fileSystem, options, errorSink);

        foreach (var info in infos) {
            await processor.ProcessAsync(new ProjCfg(new Proj(info.ProjectPath, null), info.Configuration), info);
        }
        await processor.ProcessDirs();

        return (processor.GetMarkedDirectories().Keys.ToList(), processor.GetMarkedFiles().ToList());
    }

    private string CreateProject(string name) {
        var projDir = Path.Combine(_root, name);
        Directory.CreateDirectory(projDir);
        var csproj = Path.Combine(projDir, name + ".csproj");
        File.WriteAllText(csproj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        return csproj;
    }

    private (string Csproj, string Artifacts) CreateArtifactsProject(string name) {
        var csproj = CreateProject(name);
        var artifacts = Path.Combine(_root, "artifacts");
        Directory.CreateDirectory(artifacts);
        return (csproj, artifacts);
    }

    private static string CreateDir(params string[] parts) {
        var path = Path.Combine(parts);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "a.dll"), "x");
        return path;
    }

    private static ProjectInfo Info(string csproj, string name, string? tfm = null, string[]? tfms = null, string configuration = "Debug",
        string? outDir = null, string? publishDir = null, string? packageOutputPath = null) => new() {
            ProjectPath = csproj,
            ProjectName = name,
            TargetFramework = tfm,
            TargetFrameworks = tfms ?? Array.Empty<string>(),
            Configuration = configuration,
            OutDir = outDir,
            PublishDir = publishDir,
            PackageOutputPath = packageOutputPath,
        };

    private static ProjectInfo ArtifactsInfo(string csproj, string artifacts, string name, string? tfm = null, string[]? tfms = null, string configuration = "Debug") =>
        Info(csproj, name, tfm, tfms, configuration, outDir: Path.Combine(artifacts, "bin", name, configuration.ToLowerInvariant())) with {
            UseArtifactsOutput = true,
            ArtifactsPath = artifacts,
            ArtifactsProjectName = name,
        };

    private static bool SamePath(string a, string b) =>
        string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    private static string Norm(string p) =>
        Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
