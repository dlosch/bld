using bld.Services;

namespace bld.Tests;

public class PolicyServiceTests {

    private static string TempPolicyPath() =>
        Path.Combine(Path.GetTempPath(), "bld-tests", Guid.NewGuid().ToString("N"), "policy.json");

    [Fact]
    public void Load_MissingFileIsAnEmptyPolicy() {
        var policies = PolicyService.Load(TempPolicyPath());

        Assert.Empty(policies.Rules);
    }

    [Fact]
    public void Load_RuleWithoutALevelIsAnError_NotAnUncappedRule() {
        // MaxBump's default is Major, "no cap": a rule that lost its level must not fail open.
        var path = TempPolicyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"Rules\":[{\"Match\":\"Serilog.*\"}]}");

        Assert.Throws<InvalidDataException>(() => PolicyService.Load(path));
    }

    [Fact]
    public void SaveThenLoad_RoundTripsRulesIncludingTheLevelAndDate() {
        var path = TempPolicyPath();
        var policies = PolicyService.Load(path);
        policies.Set("MassTransit*", MaxBump.Minor, "v9 changes license");
        policies.Set("FluentAssertions", MaxBump.Patch, null);
        policies.Save();

        var reloaded = PolicyService.Load(path);

        Assert.Equal(2, reloaded.Rules.Count);
        var massTransit = reloaded.Rules.Single(r => r.Match == "MassTransit*");
        Assert.Equal(MaxBump.Minor, massTransit.MaxBump);
        Assert.Equal("v9 changes license", massTransit.Reason);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), massTransit.Since);
        Assert.Equal(MaxBump.Patch, reloaded.Rules.Single(r => r.Match == "FluentAssertions").MaxBump);
    }

    [Fact]
    public void Set_ReplacesTheRuleWithTheSameMatch_AndRemoveDropsIt() {
        var policies = PolicyService.Load(TempPolicyPath());
        policies.Set("Serilog.*", MaxBump.Minor, null);
        policies.Set("serilog.*", MaxBump.Patch, "tightened");

        var rule = Assert.Single(policies.Rules);
        Assert.Equal(MaxBump.Patch, rule.MaxBump);

        Assert.True(policies.Remove("SERILOG.*"));
        Assert.False(policies.Remove("Serilog.*"));
        Assert.Empty(policies.Rules);
    }

    [Fact]
    public void Load_CorruptFileIsAnErrorNamingThePath() {
        var path = TempPolicyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ \"rules\": [ { \"match\": ");

        var ex = Assert.Throws<InvalidDataException>(() => PolicyService.Load(path));

        Assert.Contains(path, ex.Message);
    }

    [Fact]
    public void Load_AcceptsLowerCaseLevelsWrittenByHand() {
        var path = TempPolicyPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{ "Rules": [ { "Match": "MassTransit*", "MaxBump": "minor" } ] }""");

        var policies = PolicyService.Load(path);

        Assert.Equal(MaxBump.Minor, Assert.Single(policies.Rules).MaxBump);
    }

    [Fact]
    public void Find_MostSpecificMatchWins_TiesGoToTheLaterRule() {
        var rules = new List<PolicyRule> {
            new("Microsoft.*", MaxBump.Patch, null, null),
            new("Microsoft.Extensions.*", MaxBump.Minor, null, null),
            new("Serilog.*", MaxBump.Patch, null, null),
            new("Serilog.*", MaxBump.Major, null, null),
        };

        Assert.Equal("Microsoft.Extensions.*", PolicyService.Find("Microsoft.Extensions.Hosting", rules)?.Match);
        Assert.Equal("Microsoft.*", PolicyService.Find("Microsoft.Data.SqlClient", rules)?.Match);
        Assert.Equal(MaxBump.Major, PolicyService.Find("Serilog.Sinks.File", rules)?.MaxBump);
        Assert.Null(PolicyService.Find("Polly", rules));
    }
}
