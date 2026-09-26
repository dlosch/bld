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
