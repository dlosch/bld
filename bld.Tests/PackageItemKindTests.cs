using bld.Infrastructure;
using bld.Models;
using bld.Services;
using Xunit.Abstractions;

namespace bld.Tests;

/// <summary>
/// GlobalPackageReference and PackageDownload in `nuget` and `outdated`. Neither was handled: the
/// global reference was attributed to NuGet.targets, so --apply wrote nothing, and PackageDownload
/// items were invisible.
/// </summary>
public class PackageItemKindTests(ITestOutputHelper Console) {

    private const string PropsXml = """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="Newtonsoft.Json" Version="13.0.1" />
            <GlobalPackageReference Include="Guard" Version="1.0.0" />
          </ItemGroup>
        </Project>
        """;

    private const string ProjectXml = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net8.0</TargetFramework>
          </PropertyGroup>
          <ItemGroup>
            <PackageReference Include="Newtonsoft.Json" />
            <PackageDownload Include="Some.Tool" Version="[1.2.3];[2.0.0]" />
          </ItemGroup>
        </Project>
        """;

    [Theory]
    [InlineData("[8.0.0]", "8.0.0")]
    [InlineData("[1.2.3];[2.0.0]", "2.0.0")]
    [InlineData(" [2.0.0] ; [1.2.3]", "2.0.0")]
    [InlineData("8.0.0", "8.0.0")]
    [InlineData("", null)]
    [InlineData("garbage", null)]
    public void HighestExactVersion_TakesTheHighestBracketedEntry(string raw, string? expected) {
        Assert.Equal(expected, ProjParser.HighestExactVersion(raw)?.ToString());
    }

    [Fact]
    public void ProjParser_GlobalPackageReference_IsAttributedToThePropsFile_AndPackageDownloadIsRead() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-kinds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var propsPath = Path.Combine(tempDir, "Directory.Packages.props");
            File.WriteAllText(propsPath, PropsXml);
            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            File.WriteAllText(projectPath, ProjectXml);

            var console = new TestConsole(Console);
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var parser = new ProjParser(console, new ErrorSink(console), new CleaningOptions());

            var refs = parser.GetPackageReferences(new ProjCfg(new Proj(projectPath, null), "Release"));

            Assert.NotNull(refs);
            Assert.Equal(propsPath, refs!.CpmFile);

            var guard = refs.PackageReferences["Guard"];
            Assert.Equal(PackageItemKind.GlobalPackageReference, guard.Kind);
            Assert.Equal("1.0.0", guard.EffectiveVersion);
            // Was NuGet.targets before: that is where the SDK materializes the PackageVersion item.
            Assert.Equal(propsPath, refs.PackageVersions!["Guard"].SourceFile);
            Assert.Equal("1.0.0", refs.PackageVersions["Guard"].Version);

            var download = refs.PackageReferences["Some.Tool"];
            Assert.Equal(PackageItemKind.PackageDownload, download.Kind);
            Assert.Equal("2.0.0", download.EffectiveVersion);

            Assert.Equal(PackageItemKind.PackageReference, refs.PackageReferences["Newtonsoft.Json"].Kind);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdatePropsFile_UpdatesGlobalPackageReferenceInPlace() {
        var path = Path.Combine(Path.GetTempPath(), $"bld-gpr-{Guid.NewGuid():N}.props");
        await File.WriteAllTextAsync(path, PropsXml);
        try {
            var service = new OutdatedService(new TestConsole(Console), new CleaningOptions());
            var updates = new Dictionary<string, (string target, string? current)>(StringComparer.OrdinalIgnoreCase) {
                ["Guard"] = ("2.0.0", "1.0.0"),
            };

            var applied = await service.UpdatePropsFileAsync(path, updates, Array.Empty<string>(), default);

            Assert.Equal(1, applied);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("<GlobalPackageReference Include=\"Guard\" Version=\"2.0.0\" />", text);
            Assert.Contains("<PackageVersion Include=\"Newtonsoft.Json\" Version=\"13.0.1\" />", text);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdatePackageVersion_PackageDownload_RewritesOnlyTheReportedEntryInBrackets() {
        var path = Path.Combine(Path.GetTempPath(), $"bld-pd-{Guid.NewGuid():N}.csproj");
        await File.WriteAllTextAsync(path, ProjectXml);
        try {
            var console = new TestConsole(Console);
            var service = new OutdatedService(console, new CleaningOptions());

            var updated = await service.UpdatePackageVersionAsync(path, "Some.Tool", ("2.1.0", "2.0.0", VersionReason.PackageDownloadProj), default);

            Assert.True(updated);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("<PackageDownload Include=\"Some.Tool\" Version=\"[1.2.3];[2.1.0]\" />", text);
            Assert.DoesNotContain(console.Messages, m => m.Level == "Warning");
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdatePackageVersion_PackageDownload_WithoutMatchingEntry_WarnsAndWritesNothing() {
        var path = Path.Combine(Path.GetTempPath(), $"bld-pd-{Guid.NewGuid():N}.csproj");
        await File.WriteAllTextAsync(path, ProjectXml);
        try {
            var console = new TestConsole(Console);
            var service = new OutdatedService(console, new CleaningOptions());

            var updated = await service.UpdatePackageVersionAsync(path, "Some.Tool", ("2.1.0", "9.9.9", VersionReason.PackageDownloadProj), default);

            Assert.False(updated);
            Assert.Equal(ProjectXml, await File.ReadAllTextAsync(path));
            Assert.Single(console.Messages, m => m.Level == "Warning" && m.Message.Contains("no entry for 9.9.9"));
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public void Extractor_ListsGlobalAndDownloadItemsWithTheirKind() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-kinds-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            File.WriteAllText(Path.Combine(tempDir, "Directory.Packages.props"), PropsXml);
            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            File.WriteAllText(projectPath, ProjectXml);

            var console = new TestConsole(Console);
            MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
            var extractor = new NugetPackageExtractor(console, new ErrorSink(console), new NugetPackageCategorizer());

            var analysis = extractor.AnalyzeProject(new ProjCfg(new Proj(projectPath, null), "Release"), new Dictionary<string, string>());

            var byName = analysis.Packages.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(PackageItemKind.GlobalPackageReference, byName["Guard"].Kind);
            Assert.Equal("1.0.0", byName["Guard"].Version);
            Assert.Equal(PackageItemKind.PackageDownload, byName["Some.Tool"].Kind);
            Assert.Equal("2.0.0", byName["Some.Tool"].Version);
            Assert.Equal(PackageItemKind.PackageReference, byName["Newtonsoft.Json"].Kind);
            Assert.Equal("13.0.1", byName["Newtonsoft.Json"].Version);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
