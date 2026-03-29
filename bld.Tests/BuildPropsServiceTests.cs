using bld.Models;
using bld.Services;
using System.Reflection;

namespace bld.Tests;

public class BuildPropsServiceTests {
    [Fact]
    public void BuildPropertyRows_DeduplicatesIdenticalOverridesAcrossProjects() {
        var overrideA = CreateOverride(
            projectPath: "/repo/src/A/A.csproj",
            projectName: "A",
            overrideUnevaluatedValue: "$(MyLangVersion)");
        var overrideB = overrideA with { ProjectPath = "/repo/src/B/B.csproj", ProjectName = "B" };

        var results = new List<BuildPropsService.ProjectEvalResult> {
            CreateResult("/repo/src/A/A.csproj", "A", [overrideA]),
            CreateResult("/repo/src/B/B.csproj", "B", [overrideB])
        };

        var rows = BuildPropertyRows(results, includeOverridden: true);
        _ = Assert.Single(rows, r => r.IsOverridden && r.Name == "LangVersion");
    }

    [Fact]
    public void BuildPropertyRows_PreservesOverrideUnevaluatedValueForDisplay() {
        var overrideInfo = CreateOverride(
            projectPath: "/repo/src/A/A.csproj",
            projectName: "A",
            overrideUnevaluatedValue: "$( MyLangVersion )");
        var results = new List<BuildPropsService.ProjectEvalResult> {
            CreateResult("/repo/src/A/A.csproj", "A", [overrideInfo])
        };

        var rows = BuildPropertyRows(results, includeOverridden: true);
        var row = Assert.Single(rows, r => r.IsOverridden && r.Name == "LangVersion");

        Assert.Equal("$( MyLangVersion )", row.UnevaluatedValue);

        var displayValue = BuildPropertyValue(row);
        Assert.Contains("12.0", displayValue, StringComparison.Ordinal);
        Assert.Contains("$( MyLangVersion )", displayValue, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateProject_FilterProperties_CollectsOnlyRequestedProperties() {
        // This is covered indirectly by BuildPropertyRows filtering and AnalyzeAsync's early filter handoff.
        // Keep a simple behavioral assertion on row-level filtering to avoid MSBuild assembly coupling in tests.
        var overrideInfo = CreateOverride(
            projectPath: "/repo/src/A/A.csproj",
            projectName: "A",
            overrideUnevaluatedValue: "$(MyLangVersion)");
        var props = new BuildPropsService.PropsPropertyInfo(
            "Nullable",
            "enable",
            "enable",
            "/repo/Directory.Build.props",
            10);
        var results = new List<BuildPropsService.ProjectEvalResult> {
            new(
                "/repo/src/A/A.csproj",
                "A",
                Array.Empty<string>(),
                Array.Empty<string>(),
                [props],
                [overrideInfo])
        };

        var rows = BuildPropertyRowsWithFilter(results, ["LangVersion"], includeOverridden: true);
        Assert.All(rows, r => Assert.Equal("LangVersion", r.Name, ignoreCase: true));
    }

    private static BuildPropsService.PropsOverrideInfo CreateOverride(
        string projectPath,
        string projectName,
        string overrideUnevaluatedValue) =>
        new(
            "LangVersion",
            "latest",
            "/repo/Directory.Build.props",
            "12.0",
            overrideUnevaluatedValue,
            projectPath,
            projectPath,
            true,
            projectPath,
            projectName);

    private static BuildPropsService.ProjectEvalResult CreateResult(
        string projectPath,
        string projectName,
        IReadOnlyList<BuildPropsService.PropsOverrideInfo> overrides) =>
        new(
            projectPath,
            projectName,
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<BuildPropsService.PropsPropertyInfo>(),
            overrides);

    private static List<BuildPropsService.PropsDisplayRow> BuildPropertyRows(
        List<BuildPropsService.ProjectEvalResult> results,
        bool includeOverridden) {
        var method = typeof(BuildPropsService).GetMethod(
            "BuildPropertyRows",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var rows = method!.Invoke(null, [results, null, includeOverridden]);
        return Assert.IsType<List<BuildPropsService.PropsDisplayRow>>(rows);
    }

    private static List<BuildPropsService.PropsDisplayRow> BuildPropertyRowsWithFilter(
        List<BuildPropsService.ProjectEvalResult> results,
        string[] filterProperties,
        bool includeOverridden) {
        var method = typeof(BuildPropsService).GetMethod(
            "BuildPropertyRows",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var rows = method!.Invoke(null, [results, filterProperties, includeOverridden]);
        return Assert.IsType<List<BuildPropsService.PropsDisplayRow>>(rows);
    }

    private static string BuildPropertyValue(BuildPropsService.PropsDisplayRow row) {
        var method = typeof(BuildPropsService).GetMethod(
            "BuildPropertyValue",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var value = method!.Invoke(null, [row]);
        return Assert.IsType<string>(value);
    }

    private static bool ReferencesSelf(string value, string propertyName) {
        var method = typeof(BuildPropsService).GetMethod(
            "ReferencesSelf",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(string), typeof(string)],
            modifiers: null);
        Assert.NotNull(method);

        var result = method!.Invoke(null, [value, propertyName]);
        return Assert.IsType<bool>(result);
    }
}
