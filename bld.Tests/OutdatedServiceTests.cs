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

    [Theory]
    [InlineData("8.4.0", "8.9.1", "Minor", true)]
    [InlineData("8.4.0", "9.0.0", "Minor", false)]
    // A prerelease sorts below its release, so an upper-bound test would wrongly let this through.
    [InlineData("8.4.0", "9.0.0-preview.1", "Minor", false)]
    [InlineData("8.4.0", "8.5.0", "Patch", false)]
    [InlineData("8.4.0", "8.4.7", "Patch", true)]
    [InlineData("8.4.0", "10.0.0", "Major", true)]
    [InlineData("8.4.0", "9.0.0-preview.1", "Major", true)]
    [InlineData("0.2.0", "0.9.0", "Minor", true)]
    public void WithinBump_CapsByVersionComponent(string current, string candidate, string bump, bool expected) {
        var parsedBump = Enum.Parse<MaxBump>(bump);
        Assert.Equal(expected, OutdatedService.WithinBump(NuGetVersion.Parse(current), NuGetVersion.Parse(candidate), parsedBump));
    }

    [Fact]
    public void SelectByFilter_IncludePatternsMatchWithWildcards() {
        var ids = new[] { "Serilog", "Serilog.Sinks.File", "Microsoft.Extensions.Logging" };

        var selected = OutdatedService.SelectByFilter(ids, ["Serilog.*"], []);

        Assert.Contains("Serilog.Sinks.File", selected);
        Assert.DoesNotContain("Microsoft.Extensions.Logging", selected);
    }

    [Fact]
    public void SelectByFilter_EmptyIncludeMeansEverything() {
        var ids = new[] { "Serilog", "Microsoft.Extensions.Logging" };

        var selected = OutdatedService.SelectByFilter(ids, [], []);

        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void SelectByFilter_ExcludeWinsOverInclude() {
        var ids = new[] { "Serilog", "Serilog.Sinks.Seq" };

        var selected = OutdatedService.SelectByFilter(ids, ["Serilog*"], ["Serilog.Sinks.Seq"]);

        Assert.Contains("Serilog", selected);
        Assert.DoesNotContain("Serilog.Sinks.Seq", selected);
    }

    [Fact]
    public void SelectByFilter_MatchesCaseInsensitively() {
        var selected = OutdatedService.SelectByFilter(["SeriLog.Sinks.File"], ["serilog.*"], []);

        Assert.Single(selected);
    }

    [Theory]
    [InlineData(new[] { "Serilog.*;xunit*" }, 2)]
    [InlineData(new[] { "A", "B;C" }, 3)]
    [InlineData(new string[0], 0)]
    public void SplitPatterns_FlattensRepeatedAndSemicolonSeparatedValues(string[] raw, int expected) {
        Assert.Equal(expected, OutdatedService.SplitPatterns(raw).Count);
    }

    [Fact]
    public void SplitPatterns_NullMeansNoPatterns() {
        Assert.Empty(OutdatedService.SplitPatterns(null));
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
            (_, _, _, _, _, _) => { conflicts++; return OutdatedService.ConflictChoice.AcceptRisk; });

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
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.IncludeDep);

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
            (_, _, _, _, _, _) => { conflictCount++; return OutdatedService.ConflictChoice.SkipPicker; });

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
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

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
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.IncludeDep);

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
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

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
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

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
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk);

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
            (_, _, _, _, _, _) => { conflicts++; return OutdatedService.ConflictChoice.AcceptRisk; });

        Assert.Equal(0, conflicts);
        Assert.Single(result, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_AcceptedDepWithCappedTargetStillConflicts() {
        // --max-bump holds Dep at 2.0.0 although Picker's new version needs 2.5.0. Selecting Dep is
        // not enough here, so the conflict must still be raised - and flagged as already included.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ["Dep"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.5.0, )")
        };
        var sawAlreadyIncluded = false;
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker", "Dep" }, outdated, meta,
            (_, _, _, _, _, depAlreadyIncluded) => {
                sawAlreadyIncluded = depAlreadyIncluded;
                return OutdatedService.ConflictChoice.SkipPicker;
            });

        Assert.True(sawAlreadyIncluded);
        Assert.DoesNotContain("Picker", result);
        Assert.Contains("Dep", result);
    }

    [Fact]
    public void ResolveInteractivePicks_ChecksDepsThatAreOnlyInCurrentPins() {
        // Dep is a direct reference that is up to date (or was filtered out), so it never entered the
        // outdated map. It still stays at 1.0.0, which does not satisfy Picker's range.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.5.0, )")
        };
        var pins = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase) {
            ["Dep"] = NuGetVersion.Parse("1.0.0")
        };

        var conflicts = 0;
        var skipped = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _, _) => { conflicts++; return OutdatedService.ConflictChoice.SkipPicker; },
            pins);

        Assert.Equal(1, conflicts);
        Assert.Empty(skipped);

        var risked = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.AcceptRisk,
            pins);

        Assert.Single(risked, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_SatisfiedCurrentPinIsNoConflict() {
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.5.0, )")
        };
        var pins = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase) {
            ["Dep"] = NuGetVersion.Parse("3.0.0")
        };

        var conflicts = 0;
        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _, _) => { conflicts++; return OutdatedService.ConflictChoice.SkipPicker; },
            pins);

        Assert.Equal(0, conflicts);
        Assert.Single(result, "Picker");
    }

    [Fact]
    public void ResolveInteractivePicks_IncludeDepIsIgnoredWhenThereIsNothingToInclude() {
        // The dependency has no update of its own, so "include it" cannot resolve anything; the
        // answer must not loop forever or fabricate a pick.
        var outdated = new Dictionary<string, (NuGetVersion, NuGetVersion)>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = (NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0"))
        };
        var meta = new Dictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase) {
            ["Picker"] = MetaWithDep("Dep", "[2.5.0, )")
        };
        var pins = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase) {
            ["Dep"] = NuGetVersion.Parse("1.0.0")
        };

        var result = OutdatedService.ResolveInteractivePicks(
            new[] { "Picker" }, outdated, meta,
            (_, _, _, _, _, _) => OutdatedService.ConflictChoice.IncludeDep,
            pins);

        Assert.Single(result, "Picker");
        Assert.DoesNotContain("Dep", result);
    }

    [Theory]
    [InlineData("error NU1605: Detected package downgrade: A from 2.0.0 to 1.0.0", 1)]
    [InlineData("warning NU1701: fallback framework", 0)]
    [InlineData("  Determining projects to restore...", 0)]
    public void ParseRestoreErrors_KeepsOnlyNuGetErrorLines(string line, int expected) {
        Assert.Equal(expected, OutdatedService.ParseRestoreErrors([line]).Count);
    }

    [Fact]
    public async Task RunRestoreAsync_ReportsAFailureToStartInsteadOfLookingLikeSuccess() {
        // An empty result means "restore succeeded", so a run that never produced a restore result
        // must return a reason. Provoked here with an input path that does not exist.
        var service = new OutdatedService(new TestConsole(), new CleaningOptions());
        var method = typeof(OutdatedService).GetMethod("RunRestoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<IReadOnlyList<string>>)method!.Invoke(service, [
            Path.Combine(Path.GetTempPath(), $"bld-missing-{Guid.NewGuid():N}", "Nope.csproj"),
            CancellationToken.None
        ])!;

        var errors = await task;

        Assert.NotEmpty(errors);
    }

    [Fact]
    public void ParseRestoreErrors_DeduplicatesRepeatedLines() {
        var lines = new[] {
            "error NU1605: Detected package downgrade: A",
            "error NU1605: Detected package downgrade: A",
            "error NU1202: package B is not compatible with net10.0"
        };

        Assert.Equal(2, OutdatedService.ParseRestoreErrors(lines).Count);
    }

    [Fact]
    public async Task UpdatePropsFileAsync_WritesNothingWhenTargetEqualsCurrentVersion() {
        // A package held back by --max-bump is reported with current == latest; the apply path must
        // treat that as a no-op rather than rewriting the file.
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-cpm-noop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var propsPath = Path.Combine(tempDir, "Directory.Packages.props");
            const string original = """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="Held.Back" Version="8.4.0" />
                  </ItemGroup>
                </Project>
                """;
            await File.WriteAllTextAsync(propsPath, original);

            var service = new OutdatedService(new TestConsole(), new CleaningOptions());
            var updates = new Dictionary<string, (string target, string? current)>(StringComparer.OrdinalIgnoreCase) {
                ["Held.Back"] = ("8.4.0", "8.4.0")
            };

            var applied = await service.UpdatePropsFileAsync(propsPath, updates, Array.Empty<string>(), CancellationToken.None);

            Assert.Equal(0, applied);
            Assert.Equal(original, await File.ReadAllTextAsync(propsPath));
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
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
