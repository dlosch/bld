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
        using var service = NewTfmService();

        var result = service.GetUpdatedTfms(["net6.0", "net7.0"], ["net6.0"], "net10.0", Eol("net6.0", "net7.0"));

        Assert.Equal(["net10.0"], result);
    }

    [Fact]
    public void GetUpdatedTfms_NeverReturnsAnEmptyList() {
        using var service = NewTfmService();

        var result = service.GetUpdatedTfms(["net6.0"], [], "net10.0", Eol("net6.0"));

        Assert.NotEmpty(result);
        Assert.Contains("net10.0", result);
    }

    [Fact]
    public void GetUpdatedTfms_KeepsFrameworksThatAreNotEndOfLife() {
        using var service = NewTfmService();

        var result = service.GetUpdatedTfms(["netstandard2.0", "net8.0"], [], "net10.0", Eol("net6.0"));

        Assert.Contains("netstandard2.0", result);
        Assert.Contains("net8.0", result);
    }

    [Fact]
    public void WillUpdateSingleTfm_MatchesTheWriterSoPreviewAndApplyAgree() {
        using var service = NewTfmService();

        // The dry run used to list these; the writer then declined them while still reporting success.
        Assert.False(service.WillUpdateSingleTfm("netstandard2.0", "net10.0", Eol()));
        Assert.False(service.WillUpdateSingleTfm("net10.0", "net8.0", Eol()));
        Assert.True(service.WillUpdateSingleTfm("net8.0", "net10.0", Eol()));
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

    [Fact]
    public void RemoveCentralizableVersionDeclarations_LeavesConditionalAndSkippedPackagesAlone() {
        // Collapsing per-framework pins to a single central version makes one framework resolve
        // another's version, which does not build.
        var doc = XDocument.Parse(
            "<Project>" +
            "<ItemGroup Condition=\"'$(TargetFramework)'=='net48'\"><PackageReference Include=\"A\" Version=\"6.0.0\" /></ItemGroup>" +
            "<ItemGroup><PackageReference Include=\"B\" Version=\"1.0.0\" /><PackageReference Include=\"C\" Version=\"2.0.0\" /></ItemGroup>" +
            "</Project>");

        var modified = CpmService.RemoveCentralizableVersionDeclarations(doc, new HashSet<string>(["C"], StringComparer.OrdinalIgnoreCase));

        Assert.True(modified);
        Assert.Equal("6.0.0", doc.ElementsNamed("PackageReference").First(e => e.Attribute("Include")!.Value == "A").Attribute("Version")?.Value);
        Assert.Null(doc.ElementsNamed("PackageReference").First(e => e.Attribute("Include")!.Value == "B").Attribute("Version"));
        Assert.Equal("2.0.0", doc.ElementsNamed("PackageReference").First(e => e.Attribute("Include")!.Value == "C").Attribute("Version")?.Value);
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
