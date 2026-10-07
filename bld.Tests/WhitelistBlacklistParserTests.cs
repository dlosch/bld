using bld.Models;
using bld.Services;

namespace bld.Tests;

public class WhitelistBlacklistParserTests {

    private static WhitelistBlacklistRules Parse(string content) {
        var path = Path.Combine(Path.GetTempPath(), $"bld-wbf-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, content);
        try {
            return WhitelistBlacklistParser.ParseFile(path);
        }
        finally {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(">=2.0.0", "2.0.0-beta", false)]   // a prerelease sorts below its release
    [InlineData(">=2.0.0", "2.0.0", true)]
    [InlineData(">=2.0.0-beta", "2.0.0-beta", true)]
    [InlineData(">=2.0.0-beta", "2.0.0", true)]
    [InlineData(">=2.0.0-beta", "1.9.9", false)]
    [InlineData("<=2.0.0", "2.0.0-beta", true)]
    [InlineData("=1.2.3", "1.2.3.0", true)]        // NuGet normalizes the trailing zero
    [InlineData(">=1.10.0", "1.9.0", false)]       // numeric, not lexical
    public void VersionConstraints_UseNuGetOrdering(string constraint, string version, bool expected) {
        // System.Version with a hand-rolled prerelease decrement put 2.0.0-beta into 1.x, so a
        // ">=2.0.0-beta" rule never matched the beta it named.
        var rules = Parse($"# whitelist\nSerilog,{constraint}\n");

        var match = WhitelistBlacklistParser.FindMatchingPattern("Serilog", version, rules.WhitelistPatterns);

        Assert.Equal(expected, match is not null);
    }

    [Fact]
    public void BlacklistedPackage_IsItsOwnCategory() {
        var rules = Parse("# blacklist\nSystem.Data.SqlClient\n# whitelist\nMicrosoft.Data.SqlClient\n");
        var categorizer = new NugetPackageCategorizer(rules);

        // Used to stay "MicrosoftOfficial" because of the prefix; the blacklist only colored the row.
        Assert.Equal(NugetPackageCategory.Blacklisted, categorizer.CategorizePackage("System.Data.SqlClient", "4.8.6"));
        Assert.Equal(NugetPackageCategory.MicrosoftOfficial, categorizer.CategorizePackage("Microsoft.Data.SqlClient", "5.2.0"));
        Assert.Equal(NugetPackageCategory.MicrosoftOfficial, categorizer.CategorizePackage("System.Text.Json", "9.0.0"));
    }

    [Fact]
    public void WhitelistWinsOverBlacklist() {
        var rules = Parse("# blacklist\nNewtonsoft.*\n# whitelist\nNewtonsoft.Json\n");
        var categorizer = new NugetPackageCategorizer(rules);

        Assert.Equal(NugetPackageCategory.Blacklisted, categorizer.CategorizePackage("Newtonsoft.Json.Bson", "1.0.0"));
        Assert.NotEqual(NugetPackageCategory.Blacklisted, categorizer.CategorizePackage("Newtonsoft.Json", "13.0.3"));
    }
}
