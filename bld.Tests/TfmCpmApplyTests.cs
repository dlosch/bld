using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.Xml.Linq;

namespace bld.Tests;

public class TfmCpmApplyTests {

    private static TfmService NewTfmService() => new(new TestConsole(), new CleaningOptions());

    private static ISet<string> Eol(params string[] tfms) => new HashSet<string>(tfms, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void GetUpdatedTfms_MigratesTheRequestedTfmEvenWhenItIsEndOfLife() {
        // Dropping EOL entries before matching --from discarded the very TFM being migrated, leaving
        // an empty list that was then written as <TargetFrameworks></TargetFrameworks>.
        var service = NewTfmService();

        var result = service.GetUpdatedTfms(["net6.0", "net7.0"], ["net6.0"], "net10.0", Eol("net6.0", "net7.0"));

        Assert.Equal(["net10.0"], result);
    }

    [Fact]
    public void GetUpdatedTfms_NeverReturnsAnEmptyList() {
        var service = NewTfmService();

        var result = service.GetUpdatedTfms(["net6.0"], [], "net10.0", Eol("net6.0"));

        Assert.NotEmpty(result);
        Assert.Contains("net10.0", result);
    }

    [Fact]
    public void GetUpdatedTfms_KeepsFrameworksThatAreNotEndOfLife() {
        var service = NewTfmService();

        var result = service.GetUpdatedTfms(["netstandard2.0", "net8.0"], [], "net10.0", Eol("net6.0"));

        Assert.Contains("netstandard2.0", result);
        Assert.Contains("net8.0", result);
    }

    [Fact]
    public void WillUpdateSingleTfm_MatchesTheWriterSoPreviewAndApplyAgree() {
        var service = NewTfmService();

        // The dry run used to list these; the writer then declined them while still reporting success.
        Assert.False(service.WillUpdateSingleTfm("netstandard2.0", "net10.0", Eol()));
        Assert.False(service.WillUpdateSingleTfm("net10.0", "net8.0", Eol()));
        Assert.True(service.WillUpdateSingleTfm("net8.0", "net10.0", Eol()));
    }

    /// <summary>
    /// Regression: Version.TryParse("8.0-windows") failed, so no platform TFM was ever migrated, and the
    /// dry run ("no changes") and --apply ("not migrated", exit 1) contradicted each other about it.
    /// </summary>
    [Fact]
    public void PlatformTfms_MigrateAndKeepTheirPlatform() {
        var service = NewTfmService();

        Assert.True(service.WillUpdateSingleTfm("net8.0-windows", "net10.0", Eol()));
        Assert.True(service.WillUpdateSingleTfm("net8.0-windows", "net10.0-windows", Eol()));
        Assert.True(service.WillUpdateSingleTfm("net6.0-android", "net10.0", Eol("net6.0")));
        Assert.False(service.WillUpdateSingleTfm("net10.0-windows", "net10.0", Eol()));
        // Another platform, or onto or off one, is not a framework upgrade.
        Assert.False(service.WillUpdateSingleTfm("net8.0-windows", "net10.0-android", Eol()));
        Assert.False(service.WillUpdateSingleTfm("net8.0", "net10.0-windows", Eol()));

        Assert.Equal("net10.0-windows10.0.19041.0", TfmService.TargetFor("net8.0-windows10.0.19041.0", "net10.0"));
        Assert.Equal("net10.0-windows", TfmService.TargetFor("net8.0-windows10.0.19041.0", "net10.0-windows"));
        Assert.Equal("net10.0", TfmService.TargetFor("net8.0", "net10.0"));
        Assert.Equal(("netstandard2.0", ""), TfmService.SplitPlatform("netstandard2.0"));

        Assert.Equal(["net10.0-windows", "net8.0"], service.GetUpdatedTfms(["net8.0-windows", "net8.0"], ["net8.0-windows"], "net10.0", Eol()));
    }

    [Fact]
    public void ReadPackageReferences_MarksConditionalEntries() {
        var doc = XDocument.Parse(
            "<Project>" +
            "<ItemGroup Condition=\"'$(TargetFramework)'=='net48'\"><PackageReference Include=\"A\" Version=\"6.0.0\" /></ItemGroup>" +
            "<ItemGroup><PackageReference Include=\"B\" Version=\"1.0.0\" /></ItemGroup>" +
            "</Project>");

        var refs = CpmService.ReadPackageReferences(doc);

        Assert.True(refs.Single(r => r.PackageId == "A").IsConditional);
        Assert.False(refs.Single(r => r.PackageId == "B").IsConditional);
    }

    /// <summary>
    /// Regression: a conditional reference kept its inline Version, which under Central Package Management
    /// is NU1008 - and since the package's id was then skipped in every project, one conditional use broke
    /// the unconditional ones too. Per-framework pins, property references and ranges now become
    /// VersionOverride; a literal version on an unconditional reference goes.
    /// </summary>
    [Fact]
    public void RemoveCentralizableVersionDeclarations_TurnsWhatMustStayPerProjectIntoVersionOverride() {
        var doc = XDocument.Parse(
            "<Project>" +
            "<ItemGroup Condition=\"'$(TargetFramework)'=='net48'\"><PackageReference Include=\"A\" Version=\"6.0.0\" PrivateAssets=\"all\" /></ItemGroup>" +
            "<ItemGroup>" +
            "<PackageReference Include=\"A\" Version=\"8.0.0\" />" +
            "<PackageReference Include=\"P\" Version=\"$(PVersion)\" />" +
            "<PackageReference Include=\"R\"><Version>[1.0,2.0)</Version></PackageReference>" +
            "<PackageReference Include=\"O\" VersionOverride=\"3.0.0\" />" +
            "</ItemGroup>" +
            "</Project>");

        Assert.True(CpmService.RemoveCentralizableVersionDeclarations(doc));

        var refs = doc.ElementsNamed("PackageReference").ToList();
        Assert.Equal("<PackageReference Include=\"A\" VersionOverride=\"6.0.0\" PrivateAssets=\"all\" />", refs[0].ToString());
        Assert.Equal("<PackageReference Include=\"A\" />", refs[1].ToString());
        Assert.Equal("$(PVersion)", refs[2].Attribute("VersionOverride")?.Value);
        Assert.Equal("[1.0,2.0)", refs[3].ChildNamed("VersionOverride")?.Value);
        Assert.Null(refs[3].ChildNamed("Version"));
        Assert.Equal("3.0.0", refs[4].Attribute("VersionOverride")?.Value);
        Assert.DoesNotContain(refs, r => r.Attribute("Version") is { });
    }

    [Fact]
    public void KeepsItsVersion_OnlyForConditionalOrNonLiteralVersions() {
        Assert.False(CpmService.KeepsItsVersion(false, "1.2.3"));
        Assert.False(CpmService.KeepsItsVersion(false, "2.0.0-beta.1"));
        Assert.True(CpmService.KeepsItsVersion(true, "1.2.3"));
        Assert.True(CpmService.KeepsItsVersion(false, "$(FooVersion)"));
        Assert.True(CpmService.KeepsItsVersion(false, "[1.2.3]"));
        Assert.True(CpmService.KeepsItsVersion(false, "1.*"));
    }

    /// <summary>Regression: a parent directory's props file was shadowed by a new one next to the solution.</summary>
    [Fact]
    public void DirectoryPackagesPropsFor_FindsTheInheritedFileBeforeCreatingOne() {
        var root = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}");
        var solutionDir = Path.Combine(root, "src", "App");
        Directory.CreateDirectory(solutionDir);
        try {
            Assert.Equal(Path.Combine(solutionDir, "Directory.Packages.props"), CpmService.DirectoryPackagesPropsFor(solutionDir));

            var inherited = Path.Combine(root, "Directory.Packages.props");
            File.WriteAllText(inherited, "<Project />");
            Assert.Equal(inherited, CpmService.DirectoryPackagesPropsFor(solutionDir));

            var own = Path.Combine(solutionDir, "Directory.Packages.props");
            File.WriteAllText(own, "<Project />");
            Assert.Equal(own, CpmService.DirectoryPackagesPropsFor(solutionDir + Path.DirectorySeparatorChar));
        }
        finally {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort cleanup */ }
        }
    }

    [Fact]
    public async Task CreateDirectoryPackagesProps_AddsToAnUnconditionedItemGroup() {
        var dir = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var props = Path.Combine(dir, "Directory.Packages.props");
        await File.WriteAllTextAsync(props,
            "<Project>\n" +
            "  <ItemGroup Condition=\"'$(TargetFramework)'=='net48'\">\n    <PackageVersion Include=\"Pinned\" Version=\"1.0.0\" />\n  </ItemGroup>\n" +
            "  <ItemGroup>\n    <PackageVersion Include=\"Existing\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        try {
            var service = new CpmService(new TestConsole(), new CleaningOptions());
            var method = typeof(CpmService).GetMethod("CreateDirectoryPackagesPropsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)method.Invoke(service, [props, new Dictionary<string, string> { ["Added"] = "2.0.0" }, CancellationToken.None])!;

            var doc = XDocument.Load(props);
            var added = doc.Descendants("PackageVersion").Single(e => (string?)e.Attribute("Include") == "Added");

            // The first group with PackageVersion items is the per-framework block; an entry placed
            // there would only exist for net48 and leave every other project without a version.
            Assert.Null(added.Parent!.Attribute("Condition"));
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NotMigratableReason_FlagsFrameworksBldCannotRewrite() {
        var dir = Path.Combine(Path.GetTempPath(), $"bld-tfm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try {
            var inline = Path.Combine(dir, "Inline.csproj");
            File.WriteAllText(inline, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            var imported = Path.Combine(dir, "Imported.csproj");
            File.WriteAllText(imported, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><Nullable>enable</Nullable></PropertyGroup></Project>");
            var conditional = Path.Combine(dir, "Conditional.csproj");
            File.WriteAllText(conditional, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup Condition=\"'$(OS)'=='Windows_NT'\"><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>");
            var reference = Path.Combine(dir, "Reference.csproj");
            File.WriteAllText(reference, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks>$(Tfms)</TargetFrameworks></PropertyGroup></Project>");

            var service = NewTfmService();

            Assert.Null(service.NotMigratableReason(inline, usesTargetFrameworks: false, "net8.0"));
            // The dry run listed these as migrations; --apply then failed each with "No unambiguous
            // <TargetFramework>" and exit 1.
            Assert.Contains("Directory.Build.props", service.NotMigratableReason(imported, usesTargetFrameworks: false, "net8.0"));
            Assert.Contains("Condition", service.NotMigratableReason(conditional, usesTargetFrameworks: false, "net8.0"));
            Assert.Contains("property reference", service.NotMigratableReason(reference, usesTargetFrameworks: true, "net8.0;net9.0"));
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ResolveEolTfms_FallsBackToTheEmbeddedListOffline() {
        var console = new TestConsole();
        var service = new TfmService(console, new CleaningOptions());

        var offline = await service.ResolveEolTfmsAsync(_ => throw new HttpRequestException("no network"), CancellationToken.None);

        // Offline used to yield an empty set, so nothing was ever flagged and the run looked clean.
        Assert.Contains("net6.0", offline);
        Assert.Equal(TfmService.EolFallbackTfms(DateOnly.FromDateTime(DateTime.UtcNow)).Order(), offline.Order());
        Assert.Contains(console.Messages, m => m.Level == "Warning" && m.Message.Contains(TfmService.EolFallbackAsOf.ToString("yyyy-MM-dd")));

        var live = await service.ResolveEolTfmsAsync(_ => Task.FromResult<TfmService.ReleasesIndex?>(new TfmService.ReleasesIndex([
            new TfmService.ReleaseChannel("8.0", "eol", null),
            new TfmService.ReleaseChannel("10.0", "active", null),
        ])), CancellationToken.None);

        Assert.Equal(["net8.0"], live.ToList());
    }

    [Fact]
    public void EolFallbackTfms_FollowsTheEndOfSupportDates() {
        // net8.0 (LTS) and net9.0 (STS, extended to 24 months) both end on 2026-11-10.
        var before = TfmService.EolFallbackTfms(new DateOnly(2026, 11, 9));
        Assert.Contains("net7.0", before);
        Assert.DoesNotContain("net8.0", before);
        Assert.DoesNotContain("net9.0", before);

        var after = TfmService.EolFallbackTfms(new DateOnly(2026, 11, 10));
        Assert.Contains("net8.0", after);
        Assert.Contains("net9.0", after);
        Assert.DoesNotContain("net10.0", after);
    }

    [Fact]
    public async Task CreateDirectoryPackagesProps_MergesInsteadOfReplacing() {
        var dir = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var props = Path.Combine(dir, "Directory.Packages.props");
        await File.WriteAllTextAsync(props,
            "<Project>\n  <PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>\n" +
            "  <ItemGroup>\n    <PackageVersion Include=\"Existing\" Version=\"1.0.0\" />\n" +
            "    <GlobalPackageReference Include=\"Guard\" Version=\"1.0.0\" />\n  </ItemGroup>\n</Project>\n");
        try {
            var service = new CpmService(new TestConsole(), new CleaningOptions());
            var method = typeof(CpmService).GetMethod("CreateDirectoryPackagesPropsAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            await (Task)method.Invoke(service, [props, new Dictionary<string, string> { ["Added"] = "2.0.0" }, CancellationToken.None])!;

            var text = await File.ReadAllTextAsync(props);

            // Rebuilding the document from scratch used to discard every entry the run did not
            // rediscover - which is all of them for projects already on CPM.
            Assert.Contains("Include=\"Existing\"", text);
            Assert.Contains("GlobalPackageReference", text);
            Assert.Contains("Include=\"Added\"", text);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }
}
