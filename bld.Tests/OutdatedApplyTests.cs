using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.Xml.Linq;

namespace bld.Tests;

/// <summary>
/// --apply rewrites project and props files in place, so "which element gets written" is as
/// safety-critical as the deletion engine.
/// </summary>
public class OutdatedApplyTests {

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("1.2.3-beta.1", true)]
    [InlineData("9.*", false)]
    [InlineData("[9.0.0,10.0.0)", false)]
    [InlineData("$(MyPkgVersion)", false)]
    [InlineData("", false)]
    public void IsLiteralVersion_RejectsFloatingRangesAndProperties(string version, bool expected) {
        // These used to be overwritten with a concrete version, silently pinning a reference that was
        // deliberately floating or detaching it from the property that fed it.
        Assert.Equal(expected, OutdatedService.IsLiteralVersion(version));
    }

    [Fact]
    public void ElementsNamed_FindsElementsInTheLegacyMSBuildNamespace() {
        var doc = XDocument.Parse(
            "<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">" +
            "<ItemGroup><PackageReference Include=\"A\" Version=\"1.0.0\" /></ItemGroup></Project>");

        Assert.Empty(doc.Descendants("PackageReference"));      // what the code used to do
        Assert.Single(doc.ElementsNamed("PackageReference"));   // namespace-agnostic
    }

    [Fact]
    public void IsConditioned_DetectsConditionOnElementOrAncestor() {
        var doc = XDocument.Parse(
            "<Project>" +
            "<ItemGroup Condition=\"'$(TargetFramework)'=='net472'\"><PackageReference Include=\"A\" Version=\"1.0.0\" /></ItemGroup>" +
            "<ItemGroup><PackageReference Include=\"B\" Version=\"1.0.0\" /></ItemGroup>" +
            "</Project>");

        var a = doc.ElementsNamed("PackageReference").First(e => e.Attribute("Include")!.Value == "A");
        var b = doc.ElementsNamed("PackageReference").First(e => e.Attribute("Include")!.Value == "B");

        Assert.True(a.IsConditioned());
        Assert.False(b.IsConditioned());
    }

    [Fact]
    public void ReadDeclaredPackageIds_SeesConditionalAndMultiIdReferences() {
        var path = Path.Combine(Path.GetTempPath(), $"bld-declared-{Guid.NewGuid():N}.csproj");
        File.WriteAllText(path,
            "<Project Sdk=\"Microsoft.NET.Sdk\">" +
            "<ItemGroup Condition=\"'$(TargetFramework)'=='net472'\"><PackageReference Include=\"OnlyNet472\" /></ItemGroup>" +
            "<ItemGroup><PackageReference Include=\"Serilog;Serilog.Sinks.Console\" /></ItemGroup>" +
            "<ItemGroup><PackageReference Update=\"Updated.Pkg\" Version=\"1.0.0\" /></ItemGroup>" +
            "</Project>");
        try {
            var ids = OutdatedService.ReadDeclaredPackageIds(path).ToList();

            // A conditional reference is invisible to MSBuild's evaluated view, which is what made its
            // central PackageVersion look like an orphan eligible for commenting out.
            Assert.Contains("OnlyNet472", ids);
            Assert.Contains("Serilog", ids);
            Assert.Contains("Serilog.Sinks.Console", ids);
            Assert.Contains("Updated.Pkg", ids);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task UpdatePropsFile_LeavesFloatingVersionsAloneAndReportsWhatItWrote() {
        var path = Path.Combine(Path.GetTempPath(), $"bld-props-{Guid.NewGuid():N}.props");
        await File.WriteAllTextAsync(path,
            "<Project>\n  <ItemGroup>\n" +
            "    <PackageVersion Include=\"Fixed\" Version=\"1.0.0\" />\n" +
            "    <PackageVersion Include=\"Floating\" Version=\"1.*\" />\n" +
            "  </ItemGroup>\n</Project>\n");
        try {
            var service = new OutdatedService(new TestConsole(), new CleaningOptions());
            var updates = new Dictionary<string, (string target, string? current)>(StringComparer.OrdinalIgnoreCase) {
                ["Fixed"] = ("2.0.0", "1.0.0"),
                ["Floating"] = ("2.0.0", "1.*"),
            };

            var applied = await service.UpdatePropsFileAsync(path, updates, Array.Empty<string>(), default);

            Assert.Equal(1, applied);
            var text = await File.ReadAllTextAsync(path);
            Assert.Contains("Include=\"Fixed\" Version=\"2.0.0\"", text);
            Assert.Contains("Include=\"Floating\" Version=\"1.*\"", text);
        }
        finally {
            File.Delete(path);
        }
    }
}
