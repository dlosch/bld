using bld.Services;
using NuGet.Versioning;

namespace bld.Tests;

public class PackageGrouperTests {

    private static readonly string[] SampleIds = {
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Console",
        "Microsoft.Extensions.Hosting",
        "Microsoft.AspNetCore.OpenApi",
        "Microsoft.Data.SqlClient",
        "Serilog",
        "Serilog.Sinks.File",
        "FluentValidation"
    };

    private static (string Name, string[] Ids)[] Flatten(IReadOnlyList<PackageGroup> groups) =>
        groups.Select(g => (g.Name, g.Ids.ToArray())).ToArray();

    [Fact]
    public void GroupByPrefix_DefaultDepthGroupsFamiliesAndLeavesSingletonsInOther() {
        var groups = Flatten(PackageGrouper.GroupByPrefix(SampleIds));

        Assert.Equal(new[] { "Microsoft.*", "Microsoft.Extensions.*", "Serilog.*", "(other)" }, groups.Select(g => g.Name));
        Assert.Equal(new[] { "Microsoft.AspNetCore.OpenApi", "Microsoft.Data.SqlClient" }, groups[0].Ids);
        Assert.Equal(new[] { "Microsoft.Extensions.Hosting", "Microsoft.Extensions.Logging", "Microsoft.Extensions.Logging.Console" }, groups[1].Ids);
        Assert.Equal(new[] { "Serilog", "Serilog.Sinks.File" }, groups[2].Ids);
        Assert.Equal(new[] { "FluentValidation" }, groups[3].Ids);
    }

    [Fact]
    public void GroupByPrefix_Depth3SplitsOffTheLoggingFamily() {
        var groups = Flatten(PackageGrouper.GroupByPrefix(SampleIds, depth: 3));

        var logging = Assert.Single(groups, g => g.Name == "Microsoft.Extensions.Logging.*");
        Assert.Equal(new[] { "Microsoft.Extensions.Logging", "Microsoft.Extensions.Logging.Console" }, logging.Ids);
        // Only Hosting is left for Microsoft.Extensions.*, too few to stand on its own, so it falls
        // through to the next shorter prefix.
        Assert.DoesNotContain(groups, g => g.Name == "Microsoft.Extensions.*");
        var microsoft = Assert.Single(groups, g => g.Name == "Microsoft.*");
        Assert.Equal(new[] { "Microsoft.AspNetCore.OpenApi", "Microsoft.Data.SqlClient", "Microsoft.Extensions.Hosting" }, microsoft.Ids);
    }

    [Fact]
    public void GroupByPrefix_MinSizeDissolvesGroupsThatAreTooSmall() {
        var groups = Flatten(PackageGrouper.GroupByPrefix(SampleIds, minSize: 3));

        Assert.DoesNotContain(groups, g => g.Name == "Serilog.*");
        var other = Assert.Single(groups, g => g.Name == PackageGrouper.OtherGroupName);
        Assert.Contains("Serilog", other.Ids);
        Assert.Contains("Serilog.Sinks.File", other.Ids);
    }

    [Fact]
    public void GroupByPrefix_IdsWithoutADotEndUpInOther() {
        var groups = Flatten(PackageGrouper.GroupByPrefix(new[] { "xunit", "Polly", "Serilog", "Serilog.Sinks.File" }));

        Assert.Equal(new[] { "Serilog.*", "(other)" }, groups.Select(g => g.Name));
        Assert.Equal(new[] { "Polly", "xunit" }, groups[1].Ids);
    }

    [Fact]
    public void GroupByPrefix_MatchesPrefixesCaseInsensitively() {
        var groups = Flatten(PackageGrouper.GroupByPrefix(new[] { "Serilog", "SeriLog.Sinks.File" }));

        var group = Assert.Single(groups);
        Assert.Equal("Serilog.*", group.Name);
        Assert.Equal(new[] { "Serilog", "SeriLog.Sinks.File" }, group.Ids);
    }

    [Fact]
    public void GroupByPrefix_EmptyInputYieldsNoGroups() {
        Assert.Empty(PackageGrouper.GroupByPrefix(Array.Empty<string>()));
    }

    [Theory]
    [InlineData("1.2.3", "1.2.4", "Patch")]
    [InlineData("1.2.3", "1.3.0", "Minor")]
    [InlineData("1.2.3", "2.0.0", "Major")]
    [InlineData("1.0.0-beta", "1.0.0", "Patch")]
    [InlineData("1.0.0", "1.0.0.1", "Patch")]
    [InlineData("1.9.0", "2.0.0-rc.1", "Major")]
    public void Classify_UsesTheHighestDifferingComponent(string current, string target, string expected) {
        Assert.Equal(
            Enum.Parse<BumpKind>(expected),
            PackageGrouper.Classify(NuGetVersion.Parse(current), NuGetVersion.Parse(target)));
    }

    [Fact]
    public void GroupByBump_OrdersPatchMinorMajorAndOmitsEmptyClasses() {
        var rows = new[] {
            ("Zeta", NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("2.0.0")),
            ("Alpha", NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("1.0.1")),
            ("Beta", NuGetVersion.Parse("1.0.0"), NuGetVersion.Parse("3.0.0"))
        };

        var groups = Flatten(PackageGrouper.GroupByBump(rows));

        Assert.Equal(new[] { "patch", "major" }, groups.Select(g => g.Name));
        Assert.Equal(new[] { "Alpha" }, groups[0].Ids);
        Assert.Equal(new[] { "Beta", "Zeta" }, groups[1].Ids);
    }
}
