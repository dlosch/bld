using bld.Infrastructure;
using bld.Models;
using bld.Services;
using Xunit.Abstractions;

namespace bld.Tests;

/// <summary>
/// Transitive packages for `bld nuget --transitive`: parsing project.assets.json and merging the result
/// into the per-project analysis.
/// </summary>
public class ProjectAssetsReaderTests(ITestOutputHelper Console) {

    // A is referenced directly and pulls in B; Lib is a ProjectReference and must not show up.
    private const string SingleTfmAssets = """
        {
          "version": 3,
          "targets": {
            "net8.0": {
              "A/1.0.0": { "type": "package", "dependencies": { "B": "2.0.0" } },
              "B/2.0.0": { "type": "package" },
              "Lib/1.0.0": { "type": "project", "dependencies": { "B": "2.0.0" } }
            }
          },
          "libraries": {},
          "projectFileDependencyGroups": { "net8.0": [ "A >= 1.0.0", "Lib >= 1.0.0" ] },
          "project": {
            "frameworks": {
              "net8.0": {
                "targetAlias": "net8.0",
                "dependencies": { "A": { "target": "Package", "version": "[1.0.0, )" } }
              }
            }
          }
        }
        """;

    [Fact]
    public void Parse_SplitsDirectAndTransitive_AndSkipsProjectReferences() {
        var packages = ProjectAssetsReader.Parse(SingleTfmAssets);

        Assert.Equal(new[] { "A", "B" }, packages.Select(p => p.Id));

        var a = packages.Single(p => p.Id == "A");
        Assert.True(a.IsDirect);
        Assert.Equal("1.0.0", a.Version);
        Assert.Equal(new[] { "net8.0" }, a.TargetFrameworks);

        var b = packages.Single(p => p.Id == "B");
        Assert.False(b.IsDirect);
        Assert.Equal("2.0.0", b.Version);
        Assert.Equal(new[] { "A", "Lib" }, b.RequestedBy);
    }

    [Fact]
    public void Parse_MergesAcrossTargetFrameworksAndRuntimes_UsingTheProjectAlias() {
        const string json = """
            {
              "targets": {
                "net8.0": {
                  "A/1.0.0": { "type": "package", "dependencies": { "B": "2.0.0" } },
                  "B/2.0.0": { "type": "package" }
                },
                "net8.0/win-x64": {
                  "A/1.0.0": { "type": "package", "dependencies": { "B": "2.0.0" } },
                  "B/2.0.0": { "type": "package" }
                },
                "net8.0-windows7.0": {
                  "A/1.0.0": { "type": "package" }
                }
              },
              "project": {
                "frameworks": {
                  "net8.0": { "framework": "net8.0", "targetAlias": "net8.0", "dependencies": { "A": { "version": "[1.0.0, )" } } },
                  "net8.0-windows": { "framework": "net8.0-windows7.0", "targetAlias": "net8.0-windows", "dependencies": { "A": { "version": "[1.0.0, )" } } }
                }
              }
            }
            """;

        var packages = ProjectAssetsReader.Parse(json);

        Assert.Equal(2, packages.Count);
        var a = packages.Single(p => p.Id == "A");
        Assert.Equal(new[] { "net8.0", "net8.0-windows" }, a.TargetFrameworks);
        var b = packages.Single(p => p.Id == "B");
        Assert.Equal(new[] { "net8.0" }, b.TargetFrameworks);
        Assert.False(b.IsDirect);
    }

    [Fact]
    public void Parse_TwoVersionsOfOnePackage_AreSeparateEntries() {
        const string json = """
            {
              "targets": {
                "net8.0": { "B/2.0.0": { "type": "package" } },
                "net472": { "B/1.0.0": { "type": "package" } }
              },
              "project": { "frameworks": { "net8.0": { "dependencies": {} }, "net472": { "dependencies": {} } } }
            }
            """;

        var packages = ProjectAssetsReader.Parse(json);

        Assert.Equal(new[] { "1.0.0", "2.0.0" }, packages.Select(p => p.Version));
        Assert.Equal(new[] { "net472" }, packages[0].TargetFrameworks);
        Assert.Equal(new[] { "net8.0" }, packages[1].TargetFrameworks);
    }

    [Fact]
    public void TryRead_MissingFile_IsNull() {
        Assert.Null(ProjectAssetsReader.TryRead(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "project.assets.json")));
    }

    // ----- extractor end to end (MSBuild evaluation + assets file) ------------------------------

    [Fact]
    public void Extractor_WithTransitive_AppendsResolvedPackagesAndKeepsDirectOnesFirst() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "obj"));
        try {
            var projectPath = WriteProject(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "obj", "project.assets.json"), SingleTfmAssets);

            var console = new TestConsole(Console);
            var analysis = Analyze(console, projectPath, includeTransitive: true);

            Assert.Equal(new[] { "A", "B" }, analysis.Packages.Select(p => p.Name));
            Assert.Single(analysis.DirectPackages);
            var b = Assert.Single(analysis.TransitivePackages);
            Assert.Equal("2.0.0", b.Version);
            Assert.Equal(new[] { "A", "Lib" }, b.RequestedBy);
            Assert.Equal(NugetPackageCategory.Other, b.Category);
            Assert.DoesNotContain(console.Messages, m => m.Level == "Warning");
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Extractor_WithoutAssetsFile_WarnsAndListsDirectOnly() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projectPath = WriteProject(tempDir);

            var console = new TestConsole(Console);
            var analysis = Analyze(console, projectPath, includeTransitive: true);

            Assert.Equal(new[] { "A" }, analysis.Packages.Select(p => p.Name));
            Assert.Single(console.Messages, m => m.Level == "Warning" && m.Message.Contains("project.assets.json") && m.Message.Contains("dotnet restore"));
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void Extractor_WithoutTransitive_IgnoresAssetsFile() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-assets-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "obj"));
        try {
            var projectPath = WriteProject(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "obj", "project.assets.json"), SingleTfmAssets);

            var analysis = Analyze(new TestConsole(Console), projectPath, includeTransitive: false);

            Assert.Equal(new[] { "A" }, analysis.Packages.Select(p => p.Name));
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Each package line gets the source of its own version: a transitive package resolved at two versions
    /// (one per TFM) must not show the first version's source on both lines, and a direct reference that
    /// names a range is looked up at the version restore picked.
    /// </summary>
    [Fact]
    public void Extractor_SourcesFollowTheResolvedVersionOfEachLine() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-assets-{Guid.NewGuid():N}");
        var packages = Path.Combine(tempDir, "packages");
        Directory.CreateDirectory(Path.Combine(tempDir, "obj"));
        try {
            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            File.WriteAllText(projectPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFrameworks>net8.0;net9.0</TargetFrameworks></PropertyGroup>
                  <ItemGroup><PackageReference Include="A" Version="[1.0.0,2.0.0)" /></ItemGroup>
                </Project>
                """);
            Cache(packages, "A", "1.5.0", "https://a.test/v3/index.json");
            Cache(packages, "B", "2.0.0", "https://old.test/v3/index.json");
            Cache(packages, "B", "3.0.0", "https://new.test/v3/index.json");
            var folder = (packages + Path.DirectorySeparatorChar).Replace("\\", "\\\\");
            File.WriteAllText(Path.Combine(tempDir, "obj", "project.assets.json"), $$"""
                {
                  "version": 3,
                  "targets": {
                    "net8.0": { "A/1.5.0": { "type": "package", "dependencies": { "B": "2.0.0" } }, "B/2.0.0": { "type": "package" } },
                    "net9.0": { "A/1.5.0": { "type": "package", "dependencies": { "B": "3.0.0" } }, "B/3.0.0": { "type": "package" } }
                  },
                  "packageFolders": { "{{folder}}": {} },
                  "project": { "frameworks": {
                    "net8.0": { "dependencies": { "A": { "target": "Package", "version": "[1.0.0, 2.0.0)" } } },
                    "net9.0": { "dependencies": { "A": { "target": "Package", "version": "[1.0.0, 2.0.0)" } } }
                  } }
                }
                """);

            var analysis = Analyze(new TestConsole(Console), projectPath, includeTransitive: true);

            Assert.Equal("a.test", analysis.Packages.Single(p => p.Name == "A").Origin!.RestoredFrom);
            Assert.Equal("old.test", analysis.Packages.Single(p => p.Name == "B" && p.Version == "2.0.0").Origin!.RestoredFrom);
            Assert.Equal("new.test", analysis.Packages.Single(p => p.Name == "B" && p.Version == "3.0.0").Origin!.RestoredFrom);
        }
        finally {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void Cache(string packagesFolder, string id, string version, string source) {
        var directory = Path.Combine(packagesFolder, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{id.ToLowerInvariant()}.{version}.nupkg"), "");
        File.WriteAllText(Path.Combine(directory, ".nupkg.metadata"), $$"""{ "version": 2, "contentHash": "x", "source": "{{source}}" }""");
    }

    private static string WriteProject(string dir) {
        var projectPath = Path.Combine(dir, "Sample.csproj");
        File.WriteAllText(projectPath, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="A" Version="1.0.0" />
              </ItemGroup>
            </Project>
            """);
        return projectPath;
    }

    private static ProjectNugetAnalysis Analyze(TestConsole console, string projectPath, bool includeTransitive) {
        MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
        var extractor = new NugetPackageExtractor(console, new ErrorSink(console), new NugetPackageCategorizer());
        return extractor.AnalyzeProject(new ProjCfg(new Proj(projectPath, null), "Release"), new Dictionary<string, string>(), includeTransitive);
    }
}
