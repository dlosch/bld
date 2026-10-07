using bld.Services.NuGet;
using NuGet.Configuration;

namespace bld.Tests;

/// <summary>
/// nuget package sources: a restored package shows the source restore recorded in .nupkg.metadata, one that
/// is not restored the configured sources (or the mapped ones) restore would take it from.
/// </summary>
public class PackageSourceLocatorTests : IDisposable {
    private const string ContosoFeed = "https://feed.contoso.test/v3/index.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bld_psl_" + Guid.NewGuid().ToString("N"));
    private readonly string _packages;

    public PackageSourceLocatorTests() {
        _packages = Path.Combine(_root, "global-packages");
        Directory.CreateDirectory(_packages);
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    private ISettings Settings(bool mapped) {
        var mapping = mapped
            ? """
              <packageSourceMapping>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
                <packageSource key="Contoso"><package pattern="Contoso.*" /></packageSource>
              </packageSourceMapping>
              """
            : "";
        File.WriteAllText(Path.Combine(_root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                <add key="Contoso" value="{ContosoFeed}" />
              </packageSources>
              {mapping}
            </configuration>
            """);
        return NuGet.Configuration.Settings.LoadSpecificSettings(_root, "nuget.config");
    }

    /// <summary>A package as restore leaves it: lower-case layout, a .nupkg, .nupkg.metadata.</summary>
    private void Cache(string id, string version, string? source) {
        var directory = Path.Combine(_packages, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, $"{id.ToLowerInvariant()}.{version}.nupkg"), "");
        var sourceJson = source is null ? "" : $", \"source\": \"{source}\"";
        File.WriteAllText(Path.Combine(directory, ".nupkg.metadata"), $$"""{ "version": 2, "contentHash": "x"{{sourceJson}} }""");
    }

    [Fact]
    public void RestoredPackage_ShowsTheRecordedSourceByItsConfiguredKey() {
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        Cache("Newtonsoft.Json", "13.0.3", "https://api.nuget.org/v3/index.json");
        var locator = new PackageSourceLocator(Settings(mapped: false));

        Assert.Equal("Contoso", locator.Locate("Contoso.Core", ["1.2.0"], [_packages]).Describe());
        Assert.Equal("nuget.org", locator.Locate("Newtonsoft.Json", ["13.0.3"], [_packages]).Describe());
    }

    [Fact]
    public void RestoredPackage_FromAnUnconfiguredSourceShowsTheHostAndKeepsTheUrlForTheLegend() {
        Cache("Other.Lib", "2.0.0", "https://pkgs.elsewhere.test/v3/index.json");
        var locator = new PackageSourceLocator(Settings(mapped: false));

        var origin = locator.Locate("Other.Lib", ["2.0.0"], [_packages]);
        Assert.Equal("pkgs.elsewhere.test", origin.RestoredFrom);
        Assert.Equal("https://pkgs.elsewhere.test/v3/index.json", origin.Urls["pkgs.elsewhere.test"]);
    }

    [Fact]
    public void RangeIsSkippedInFavourOfTheResolvedVersion() {
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        var locator = new PackageSourceLocator(Settings(mapped: false));

        // The project names a range; the assets file has what restore picked.
        Assert.Equal("Contoso", locator.Locate("Contoso.Core", ["[1.0,2.0)", "1.2.0"], [_packages]).RestoredFrom);
    }

    [Fact]
    public void NotRestored_WithoutMappingAnyEnabledSourceMayServeIt() {
        var locator = new PackageSourceLocator(Settings(mapped: false));

        var origin = locator.Locate("Contoso.Core", ["1.2.0"], [_packages]);

        Assert.Null(origin.RestoredFrom);
        Assert.Equal(["nuget.org", "Contoso"], origin.Candidates);
        Assert.Equal("not restored: nuget.org or Contoso", origin.Describe());
    }

    [Fact]
    public void NotRestored_WithMappingOnlyTheMappedSourcesCount() {
        var locator = new PackageSourceLocator(Settings(mapped: true));

        // NuGet maps to the most specific pattern only.
        Assert.Equal("not restored: Contoso", locator.Locate("Contoso.Core", ["1.2.0"], [_packages]).Describe());
        Assert.Equal("not restored: nuget.org", locator.Locate("Serilog", ["4.0.0"], [_packages]).Describe());
    }

    [Fact]
    public void Urls_MapEachNameShownToItsSourceForTheLegend() {
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        Cache("Other.Lib", "2.0.0", "https://pkgs.elsewhere.test/v3/index.json");
        var locator = new PackageSourceLocator(Settings(mapped: false));

        Assert.Equal(ContosoFeed, locator.Locate("Contoso.Core", ["1.2.0"], [_packages]).Urls["Contoso"]);
        // An unconfigured source is shown by its host, so the legend needs its URL.
        Assert.Equal("https://pkgs.elsewhere.test/v3/index.json", locator.Locate("Other.Lib", ["2.0.0"], [_packages]).Urls["pkgs.elsewhere.test"]);
        var candidates = locator.Locate("Missing", ["1.0.0"], [_packages]).Urls;
        Assert.Equal(ContosoFeed, candidates["Contoso"]);
        Assert.Equal("https://api.nuget.org/v3/index.json", candidates["nuget.org"]);
    }

    [Fact]
    public void NotRestored_ProjectRestoreSourcesReplaceTheConfiguredOnesAndAdditionalOnesAreAdded() {
        var locator = new PackageSourceLocator(Settings(mapped: true));

        // RestoreSources: the mapping no longer applies; a configured URL is shown by its key, an unknown one by its host.
        var replaced = locator.Locate("Serilog", ["4.0.0"], [_packages], restoreSources: [ContosoFeed, "https://other.test/v3/index.json"]);
        Assert.Equal(["Contoso", "other.test"], replaced.Candidates);
        Assert.Equal("https://other.test/v3/index.json", replaced.Urls["other.test"]);

        // RestoreAdditionalProjectSources: on top of the mapped source.
        var added = locator.Locate("Serilog", ["4.0.0"], [_packages], additionalSources: ["/feeds/local"]);
        Assert.Equal(["nuget.org", "/feeds/local"], added.Candidates);
    }

    [Fact]
    public void RestoredWithoutRecordedSource_SaysSo() {
        Cache("Old.Lib", "1.0.0", null);
        var locator = new PackageSourceLocator(Settings(mapped: false));

        Assert.Equal("packages folder, source not recorded", locator.Locate("Old.Lib", ["1.0.0"], [_packages]).RestoredFrom);
    }
}
