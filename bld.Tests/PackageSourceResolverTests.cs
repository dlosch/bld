using bld.Services.NuGet;
using NuGet.Configuration;
using NuGet.Frameworks;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace bld.Tests;

/// <summary>
/// Feeds from nuget.config: source list, package source mapping, credentials and service-index
/// resolution. The metadata client only ever knew api.nuget.org before.
/// </summary>
public class PackageSourceResolverTests : IDisposable {
    private const string NuGetOrgRegistration = "https://api.nuget.org/v3/registration5-gz-semver2/";
    private const string InternalIndex = "https://feed.contoso.test/v3/index.json";
    private const string InternalRegistration36 = "https://feed.contoso.test/v3/registrations/3.6.0/";
    private const string InternalRegistration34 = "https://feed.contoso.test/v3/registrations/3.4.0/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bld_psr_" + Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();

    public PackageSourceResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    /// <summary>Serves canned responses by URL and records every request, including its headers.</summary>
    private sealed class RecordingHandler : HttpMessageHandler {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new(StringComparer.OrdinalIgnoreCase);
        public List<HttpRequestMessage> Requests { get; } = new();

        public RecordingHandler Json(string url, string json) {
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return this;
        }

        public RecordingHandler Status(string url, HttpStatusCode status) {
            _routes[url] = () => new HttpResponseMessage(status);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Requests.Add(request);
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            return Task.FromResult(_routes.TryGetValue(url, out var route) ? route() : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static string ServiceIndex(params (string Type, string Id)[] resources) =>
        "{ \"version\": \"3.0.0\", \"resources\": [" +
        string.Join(",", resources.Select(r => $"{{ \"@id\": \"{r.Id}\", \"@type\": \"{r.Type}\" }}")) +
        "] }";

    private ISettings WriteConfig(string xml) {
        File.WriteAllText(Path.Combine(_root, "nuget.config"), xml);
        return Settings.LoadSpecificSettings(_root, "nuget.config");
    }

    private const string MappedConfig = """
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
            <add key="internal" value="https://feed.contoso.test/v3/index.json" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="nuget.org">
              <package pattern="*" />
            </packageSource>
            <packageSource key="internal">
              <package pattern="Contoso.*" />
            </packageSource>
          </packageSourceMapping>
          <packageSourceCredentials>
            <internal>
              <add key="Username" value="build" />
              <add key="ClearTextPassword" value="secret" />
            </internal>
          </packageSourceCredentials>
        </configuration>
        """;

    private PackageSourceResolver Resolver(ISettings settings, RecordingHandler handler, IReadOnlyList<string>? explicitSources = null, bool ignoreMapping = false) =>
        new(_console, new HttpClient(handler), settings, explicitSources, ignoreMapping, NuGetOrgRegistration);

    // ----- source selection ----------------------------------------------------------------------

    [Fact]
    public async Task NoConfiguredSources_FallsBackToNuGetOrg_WithoutServiceIndexRequest() {
        var handler = new RecordingHandler();
        var resolver = Resolver(NullSettings.Instance, handler);

        var feeds = await resolver.GetFeedsForAsync("Newtonsoft.Json", CancellationToken.None);

        var feed = Assert.Single(feeds);
        Assert.Equal("nuget.org", feed.Name);
        Assert.Equal(NuGetOrgRegistration, feed.RegistrationBaseUrl);
        Assert.Null(feed.Authorization);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task PackageSourceMapping_RoutesEachPackageToItsSource_AndResolvesRegistrationUrl() {
        var handler = new RecordingHandler().Json(InternalIndex, ServiceIndex(
            ("RegistrationsBaseUrl/3.4.0", InternalRegistration34),
            ("RegistrationsBaseUrl/3.6.0", InternalRegistration36),
            ("SearchQueryService", "https://feed.contoso.test/v3/query")));
        var resolver = Resolver(WriteConfig(MappedConfig), handler);

        var contoso = await resolver.GetFeedsForAsync("Contoso.Auth", CancellationToken.None);
        var external = await resolver.GetFeedsForAsync("Newtonsoft.Json", CancellationToken.None);

        var internalFeed = Assert.Single(contoso);
        Assert.Equal("internal", internalFeed.Name);
        Assert.Equal(InternalRegistration36, internalFeed.RegistrationBaseUrl);
        Assert.Equal("build", internalFeed.Username);
        Assert.Equal("secret", internalFeed.Password);

        var nugetOrg = Assert.Single(external);
        Assert.Equal("nuget.org", nugetOrg.Name);

        // The service index is fetched once and with the source's credentials.
        var indexRequest = Assert.Single(handler.Requests);
        Assert.Equal(InternalIndex, indexRequest.RequestUri!.AbsoluteUri);
        Assert.Equal("Basic", indexRequest.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task IgnoreSourceMapping_QueriesEverySource() {
        var handler = new RecordingHandler().Json(InternalIndex, ServiceIndex(("RegistrationsBaseUrl", InternalRegistration36)));
        var resolver = Resolver(WriteConfig(MappedConfig), handler, ignoreMapping: true);

        var feeds = await resolver.GetFeedsForAsync("Newtonsoft.Json", CancellationToken.None);

        Assert.Equal(new[] { "nuget.org", "internal" }, feeds.Select(f => f.Name));
    }

    [Fact]
    public async Task ExplicitSources_MatchConfiguredNameOrUrl_AndIgnoreMapping() {
        const string otherIndex = "https://other.test/v3/index.json";
        var handler = new RecordingHandler()
            .Json(InternalIndex, ServiceIndex(("RegistrationsBaseUrl", InternalRegistration36)))
            .Json(otherIndex, ServiceIndex(("RegistrationsBaseUrl/Versioned", "https://other.test/reg")));
        var resolver = Resolver(WriteConfig(MappedConfig), handler, explicitSources: new[] { "internal", otherIndex });

        var feeds = await resolver.GetFeedsForAsync("Newtonsoft.Json", CancellationToken.None);

        Assert.Equal(2, feeds.Count);
        Assert.Equal("internal", feeds[0].Name);
        Assert.Equal("build", feeds[0].Username);
        Assert.Equal("https://other.test/reg/", feeds[1].RegistrationBaseUrl);
        Assert.False(resolver.UsesSourceMapping);
    }

    [Fact]
    public async Task UnmappedPackage_YieldsNoFeed_AndWarnsOnce() {
        var config = MappedConfig.Replace("<package pattern=\"*\" />", "<package pattern=\"Newtonsoft.*\" />");
        var resolver = Resolver(WriteConfig(config), new RecordingHandler());

        var first = await resolver.GetFeedsForAsync("Serilog", CancellationToken.None);
        var second = await resolver.GetFeedsForAsync("Serilog", CancellationToken.None);

        Assert.Empty(first);
        Assert.Empty(second);
        Assert.Single(_console.Messages, m => m.Level == "Warning" && m.Message.Contains("packageSourceMapping"));
        Assert.True(resolver.IsUnmapped("Serilog"));
        Assert.False(resolver.IsUnmapped("Newtonsoft.Json"));
    }

    [Fact]
    public async Task IsUnmapped_IsFalseWithoutMapping() {
        var resolver = Resolver(NullSettings.Instance, new RecordingHandler());
        Assert.False(resolver.IsUnmapped("Anything"));

        var explicitSources = Resolver(WriteConfig(MappedConfig), new RecordingHandler(), explicitSources: new[] { "nuget.org" });
        Assert.False(explicitSources.IsUnmapped("Serilog"));
        await Task.CompletedTask;
    }

    // ----- unusable sources ----------------------------------------------------------------------

    [Fact]
    public async Task UnavailableSource_IsSkippedForTheRun_WithOneWarning() {
        var handler = new RecordingHandler().Status(InternalIndex, HttpStatusCode.ServiceUnavailable);
        var resolver = Resolver(WriteConfig(MappedConfig), handler, ignoreMapping: true);

        var first = await resolver.GetFeedsForAsync("A", CancellationToken.None);
        var second = await resolver.GetFeedsForAsync("B", CancellationToken.None);

        Assert.Equal(new[] { "nuget.org" }, first.Select(f => f.Name));
        Assert.Equal(new[] { "nuget.org" }, second.Select(f => f.Name));
        Assert.Single(handler.Requests);
        Assert.Single(_console.Messages, m => m.Level == "Warning" && m.Message.Contains("unavailable"));
    }

    [Fact]
    public async Task SourceWithoutRegistrationResource_IsSkipped() {
        var handler = new RecordingHandler().Json(InternalIndex, ServiceIndex(("SearchQueryService", "https://feed.contoso.test/v3/query")));
        var resolver = Resolver(WriteConfig(MappedConfig), handler);

        var feeds = await resolver.GetFeedsForAsync("Contoso.Auth", CancellationToken.None);

        Assert.Empty(feeds);
        Assert.Single(_console.Messages, m => m.Level == "Warning" && m.Message.Contains("RegistrationsBaseUrl"));
    }

    [Fact]
    public async Task UnusableSource_WarnsOnce_UnderParallelLookups() {
        var settings = WriteConfig("""
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="local" value="C:\packages" />
              </packageSources>
            </configuration>
            """);
        var resolver = Resolver(settings, new RecordingHandler());

        await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(() => resolver.GetFeedsForAsync($"Package{i}", CancellationToken.None))));

        Assert.Single(_console.Messages, m => m.Level == "Warning");
    }

    [Fact]
    public async Task V2AndLocalSources_AreSkipped() {
        var settings = WriteConfig("""
            <configuration>
              <packageSources>
                <clear />
                <add key="legacy" value="https://legacy.test/api/v2" protocolVersion="2" />
                <add key="local" value="C:\packages" />
              </packageSources>
            </configuration>
            """);
        var handler = new RecordingHandler();
        var resolver = Resolver(settings, handler);

        var feeds = await resolver.GetFeedsForAsync("A", CancellationToken.None);

        Assert.Empty(feeds);
        Assert.Empty(handler.Requests);
        Assert.Equal(2, _console.Messages.Count(m => m.Level == "Warning"));
    }

    // ----- metadata client honours the feed ------------------------------------------------------

    [Fact]
    public async Task MetadataClient_UsesFeedRegistrationBase_AndSendsCredentials() {
        var feed = new PackageFeed("internal", InternalRegistration36, "build", "secret");
        var indexUrl = InternalRegistration36 + "contoso.auth/index.json";
        var pageUrl = InternalRegistration36 + "contoso.auth/page0.json";
        var handler = new RecordingHandler()
            .Json(indexUrl, $$"""{ "count": 1, "items": [ { "@id": "{{pageUrl}}", "@type": "catalog:CatalogPage", "count": 1 } ] }""")
            .Json(pageUrl, """
                { "count": 1, "items": [ { "catalogEntry": { "id": "Contoso.Auth", "version": "3.1.0", "listed": true,
                  "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ] } } ] }
                """);

        var request = new PackageVersionRequest { PackageId = "Contoso.Auth", CompatibleTargetFrameworks = ["net8.0"] };
        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(new HttpClient(handler), new NugetMetadataOptions(), null, request, feed);

        Assert.NotNull(result);
        Assert.Contains("3.1.0", result!.TargetFrameworkVersions.Values);
        Assert.Equal(new[] { indexUrl, pageUrl }, handler.Requests.Select(r => r.RequestUri!.AbsoluteUri));
        var expected = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("build:secret")));
        Assert.All(handler.Requests, r => Assert.Equal(expected, r.Headers.Authorization));
    }

    [Fact]
    public async Task MetadataClient_WithoutFeed_StillQueriesNuGetOrg() {
        var handler = new RecordingHandler();
        var request = new PackageVersionRequest { PackageId = "My.Package", CompatibleTargetFrameworks = ["net8.0"] };

        await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(new HttpClient(handler), new NugetMetadataOptions(), null, request);

        var only = Assert.Single(handler.Requests);
        Assert.Equal(NuGetOrgRegistration + "my.package/index.json", only.RequestUri!.AbsoluteUri);
        Assert.Null(only.Headers.Authorization);
    }

    // ----- merging answers from several feeds ----------------------------------------------------

    private static PackageVersionResult Found(string version, string? outside = null) => new() {
        PackageId = "P",
        TargetFrameworkVersions = new Dictionary<NuGetFramework, string> { [NuGetFramework.Parse("net8.0")] = version },
        NewestOutsideFilter = outside,
    };

    private static PackageVersionResult WindowMiss(string outside) => new() {
        PackageId = "P",
        TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
        NewestOutsideFilter = outside,
        NoVersionWithinFilter = true,
    };

    [Fact]
    public void PickNewest_HighestVersionAcrossFeedsWins() {
        var best = NugetMetadataService.PickNewest(new[] { Found("2.0.0"), null, Found("3.5.0"), Found("3.4.9") });

        Assert.Equal("3.5.0", best!.TargetFrameworkVersions.Values.Single());
    }

    [Fact]
    public void PickNewest_HeldBackVersionIsTheNewestAnyFeedRejected() {
        var best = NugetMetadataService.PickNewest(new[] { Found("2.0.0", outside: "3.0.0"), Found("2.1.0"), WindowMiss("4.0.0") });

        Assert.Equal("2.1.0", best!.TargetFrameworkVersions.Values.Single());
        Assert.Equal("4.0.0", best.NewestOutsideFilter);
        Assert.False(best.NoVersionWithinFilter);
    }

    [Fact]
    public void PickNewest_WindowMissOnlyWhenNoFeedHasACandidate() {
        var miss = NugetMetadataService.PickNewest(new[] { null, WindowMiss("4.0.0"), WindowMiss("5.0.0") });

        Assert.True(miss!.NoVersionWithinFilter);
        Assert.Equal("5.0.0", miss.NewestOutsideFilter);
        Assert.Empty(miss.TargetFrameworkVersions);
    }

    [Fact]
    public void PickNewest_AllNull_IsNull() {
        Assert.Null(NugetMetadataService.PickNewest(new PackageVersionResult?[] { null, null }));
        Assert.Null(NugetMetadataService.PickNewest(Array.Empty<PackageVersionResult?>()));
    }
}
