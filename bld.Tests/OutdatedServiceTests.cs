using bld.Infrastructure;
using bld.Models;
using bld.Services;
using bld.Services.NuGet;
using NuGet.Frameworks;
using NuGet.Versioning;
using System.Reflection;
using System.Xml.Linq;

namespace bld.Tests;

public class OutdatedServiceTests {

    [Fact]
    public void SelectCompatibleTargetFrameworks_RespectsSkipFlag() {
        var packageRefs = new OutdatedService.PackageInfoContainer();
        packageRefs.Add(new OutdatedService.PackageInfo {
            Id = "Example.Package",
            Item = new Pkg("Example.Package", "1.0.0"),
            ProjectPath = "Example.csproj",
            TargetFramework = "net8.0",
            TargetFrameworks = ["net8.0", "net10.0"]
        });

        var withCheck = OutdatedService.SelectCompatibleTargetFrameworks(skipTfmCheck: false, packageRefs);
        var skipped = OutdatedService.SelectCompatibleTargetFrameworks(skipTfmCheck: true, packageRefs);

        Assert.Contains("net8.0", withCheck, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("net10.0", withCheck, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(skipped);
    }

    [Theory]
    [InlineData("foo.sln", true)]
    [InlineData("foo.slnx", true)]
    [InlineData("foo.slnf", true)]
    [InlineData("foo.csproj", false)]
    [InlineData("foo.fsproj", false)]
    [InlineData("foo.txt", false)]
    public void IsSolutionFile_RecognizesSolutionExtensions(string path, bool expected) {
        Assert.Equal(expected, OutdatedService.IsSolutionFile(path));
    }

    private static PackageVersionResult MetaWithDep(string depId, string depRange) => new() {
        PackageId = "Picker",
        TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
        Dependencies = new Dictionary<NuGetFramework, DependencyGroup> {
            [NuGetFramework.AnyFramework] = new DependencyGroup {
                Dependencies = new[] { new Dependency { PackageId = depId, Range = depRange } }
            }
        }
    };

    [Fact]
    public void ResolveInteractivePicks_NoConflict_WhenSkippedDepSatisfiesPickerRange() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ["Dep"] = (NuGetVersion.Parse("3.0.0"), NuGetVersion.Parse("4.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.0.0, )")
        };
        var conflicts = 0;
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => { conflicts++; return OutdatedService.ConflictChoice.AcceptRisk; });

        Assert.Equal(0, conflicts);
        Assert.Single(result, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_Conflict_IncludeDep_AddsSkippedDep() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ["Dep"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.5.0, )")
        };
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => OutdatedService.ConflictChoice.IncludeDep);

        Assert.Contains("Picker", result);
        Assert.Contains("Dep", result);
    }

    [Fact]
    public void ResolveInteractivePicks_Conflict_SkipPicker_DropsPickerAndStopsCheckingItsDeps() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ["DepA"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0")),
            ["DepB"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = new PackageVersionResult {
                PackageId = "Picker",
                TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
                Dependencies = new Dictionary<NuGetFramework, DependencyGroup> {
                    [NuGetFramework.AnyFramework] = new DependencyGroup {
                        Dependencies = new[] {
                            new Dependency { PackageId = "DepA", Range = "[2.0.0, )" },
                            new Dependency { PackageId = "DepB", Range = "[2.0.0, )" }
                        }
                    }
                }
            }
        };
        var conflictCount = 0;
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => { conflictCount++; return OutdatedService.ConflictChoice.SkipPicker; });

        Assert.Empty(result);
        Assert.Equal(1, conflictCount); // stopped checking DepB after dropping Picker on DepA conflict
    }

    [Fact]
    public void ResolveInteractivePicks_Conflict_AcceptRisk_KeepsPickerOnly() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ["Dep"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.5.0, )")
        };
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

        Assert.Single(result, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_TransitiveConflict_IncludingDepThenChecksItsDeps() {
        // Picker -> Mid (needs Mid >= 2.5), Mid -> Leaf (needs Leaf >= 2.5).
        // User skips both Mid and Leaf initially, then says IncludeDep at each conflict.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ["Mid"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0")),
            ["Leaf"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Mid", "[2.5.0, )"),
            ["Mid"] = MetaWithDep("Leaf", "[2.5.0, )")
        };
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => OutdatedService.ConflictChoice.IncludeDep);

        Assert.Contains("Picker", result);
        Assert.Contains("Mid", result);
        Assert.Contains("Leaf", result);
    }

    [Fact]
    public void ResolveInteractivePicks_HandlesNullMetadataValue() {
        // Defensive: TryGetValue returns true but the stored value is null.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = null!
        };
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

        Assert.Single(result, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_IgnoresAcceptedIdsNotInOutdatedMap() {
        // Caller passed an id we don't track; helper must not throw KeyNotFoundException.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["KnownPicker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase);
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "KnownPicker", "Ghost" }, outdated, meta,
            (_, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

        Assert.Contains("KnownPicker", result);
        Assert.Contains("Ghost", result); // pass-through; we don't filter unknowns out, we just don't crash
    }

    [Fact]
    public void ResolveInteractivePicks_HandlesNullDependencyListInGroup() {
        // NuGet catalog JSON occasionally has "dependencies": null on a group; deserialization
        // overrides the record's `= []` default. Helper must not crash.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = new PackageVersionResult {
                PackageId = "Picker",
                TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
                Dependencies = new Dictionary<NuGetFramework, DependencyGroup> {
                    [NuGetFramework.AnyFramework] = new DependencyGroup { Dependencies = null! }
                }
            }
        };
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

        Assert.Single(result, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_IgnoresDepsNotInOutdatedMap() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("UnknownDep", "[99.0.0, )")
        };
        var conflicts = 0;
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _) => { conflicts++; return OutdatedService.ConflictChoice.AcceptRisk; });

        Assert.Equal(0, conflicts);
        Assert.Single(result, "Picker");
    }

    [Fact]
    public async Task UpdatePropsFileAsync_CommentsOutOrphansAndUpdatesOthers() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var propsPath = Path.Combine(tempDir, "Directory.Packages.props");
            await File.WriteAllTextAsync(propsPath, """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="Stay.As.Is" Version="1.0.0" />
                    <PackageVersion Include="Will.Be.Updated" Version="2.0.0" />
                    <PackageVersion Include="Will.Be.Commented" Version="3.0.0" />
                  </ItemGroup>
                </Project>
                """);

            var service = new OutdatedService(new TestConsole(), new CleaningOptions());
            var updates = new Dictionary<string, (string target, string? current)>(StringComparer.OrdinalIgnoreCase) {
                ["Will.Be.Updated"] = ("2.5.0", "2.0.0")
            };
            var commentOut = new[] { "Will.Be.Commented" };

            await service.UpdatePropsFileAsync(propsPath, updates, commentOut, CancellationToken.None);

            var doc = XDocument.Load(propsPath);
            var liveEntries = doc.Descendants("PackageVersion")
                .ToDictionary(e => e.Attribute("Include")!.Value, e => e.Attribute("Version")!.Value, StringComparer.OrdinalIgnoreCase);

            Assert.Equal("1.0.0", liveEntries["Stay.As.Is"]);
            Assert.Equal("2.5.0", liveEntries["Will.Be.Updated"]);
            Assert.False(liveEntries.ContainsKey("Will.Be.Commented"));

            var comments = doc.DescendantNodes().OfType<XComment>().ToArray();
            Assert.Contains(comments, c => c.Value.Contains("Will.Be.Commented") && c.Value.Contains("3.0.0"));
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdatePackageVersionAsync_MatchesPackageIdCaseInsensitively() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-outdated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
                  </ItemGroup>
                </Project>
                """);

            var service = new OutdatedService(new TestConsole(), new CleaningOptions());
            var method = typeof(OutdatedService).GetMethod("UpdatePackageVersionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var updateTask = (Task?)method!.Invoke(service, [
                projectPath,
                "newtonsoft.json",
                (target: "14.0.0", currentVersion: (string?)"13.0.1", reason: VersionReason.PackageReferenceProj),
                CancellationToken.None
            ]);

            Assert.NotNull(updateTask);
            await updateTask!;

            var doc = XDocument.Load(projectPath);
            var updatedVersion = doc.Descendants("PackageReference")
                .Single()
                .Attribute("Version")?
                .Value;

            Assert.Equal("14.0.0", updatedVersion);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
