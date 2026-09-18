using bld.Services.NuGet;
using NuGet.Frameworks;
using NuGet.Versioning;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace bld.Tests;

public class NuGetMetadataServiceTests {
    private sealed class StaticResponseHandler : HttpMessageHandler {
        private readonly string _indexJson;
        private readonly IReadOnlyDictionary<string, string> _pagesBySuffix;

        public StaticResponseHandler(string indexJson, string pageJson)
            : this(indexJson, new Dictionary<string, string> { ["/page0.json"] = pageJson }) {
        }

        // pagesBySuffix: page body per URL suffix, e.g. "/page1.json".
        public StaticResponseHandler(string indexJson, IReadOnlyDictionary<string, string> pagesBySuffix) {
            _indexJson = indexJson;
            _pagesBySuffix = pagesBySuffix;
        }

        /// <summary>Every URL the walk asked for, so a test can assert what it did *not* fetch.</summary>
        public List<string> RequestedUrls { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;
            lock (RequestedUrls) RequestedUrls.Add(url);

            if (url.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase)) {
                return Task.FromResult(CreateJsonResponse(_indexJson));
            }

            foreach (var (suffix, json) in _pagesBySuffix) {
                if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                    return Task.FromResult(CreateJsonResponse(json));
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        private static HttpResponseMessage CreateJsonResponse(string json) {
            var response = new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }

    [Fact]
    public async Task GetLatestVersionWithFrameworkCheckAsync_PrefersOlderStableBeforePrereleaseRetry() {
        const string indexJson = """
            {
              "count": 1,
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
                  "@type": "catalog:CatalogPage",
                  "count": 3
                }
              ]
            }
            """;

        const string pageJson = """
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
              "@type": "catalog:CatalogPage",
              "count": 3,
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/1.0.0.json",
                  "@type": "Package",
                  "catalogEntry": {
                    "id": "My.Package",
                    "version": "1.0.0",
                    "listed": true,
                    "dependencyGroups": [
                      {
                        "targetFramework": "net8.0",
                        "dependencies": []
                      }
                    ]
                  }
                },
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/2.0.0.json",
                  "@type": "Package",
                  "catalogEntry": {
                    "id": "My.Package",
                    "version": "2.0.0",
                    "listed": true,
                    "dependencyGroups": [
                      {
                        "targetFramework": "net9.0",
                        "dependencies": []
                      }
                    ]
                  }
                },
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/2.1.0-beta.json",
                  "@type": "Package",
                  "catalogEntry": {
                    "id": "My.Package",
                    "version": "2.1.0-beta",
                    "listed": true,
                    "dependencyGroups": [
                      {
                        "targetFramework": "net8.0",
                        "dependencies": []
                      }
                    ]
                  }
                }
              ]
            }
            """;

        using var client = new HttpClient(new StaticResponseHandler(indexJson, pageJson));
        var options = new NugetMetadataOptions();
        var request = new PackageVersionRequest {
            PackageId = "My.Package",
            AllowPrerelease = false,
            CompatibleTargetFrameworks = ["net8.0"]
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, logger: null, request);

        Assert.NotNull(result);
        Assert.False(result!.IsPrerelease);
        Assert.Contains("1.0.0", result.TargetFrameworkVersions.Values);
    }

    private const string CapIndexJson = """
        {
          "count": 1,
          "items": [
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
              "@type": "catalog:CatalogPage",
              "count": 3
            }
          ]
        }
        """;

    private const string CapPageJson = """
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
          "@type": "catalog:CatalogPage",
          "count": 3,
          "items": [
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/8.0.0.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package",
                "version": "8.0.0",
                "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/8.4.0.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package",
                "version": "8.4.0",
                "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/9.0.0.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package",
                "version": "9.0.0",
                "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task GetLatestVersionWithFrameworkCheckAsync_WithoutVersionFilter_TakesTheNewestVersion() {
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, CapPageJson));
        var request = new PackageVersionRequest {
            PackageId = "My.Package",
            AllowPrerelease = false,
            CompatibleTargetFrameworks = ["net8.0"]
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, request);

        Assert.NotNull(result);
        Assert.Contains("9.0.0", result!.TargetFrameworkVersions.Values);
        Assert.Null(result.NewestOutsideFilter);
    }

    [Fact]
    public async Task GetLatestVersionWithFrameworkCheckAsync_VersionFilterCapsTheResultAndReportsWhatItSkipped() {
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, CapPageJson));
        var request = new PackageVersionRequest {
            PackageId = "My.Package",
            AllowPrerelease = false,
            CompatibleTargetFrameworks = ["net8.0"],
            VersionFilter = v => v.Major == 8
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, request);

        Assert.NotNull(result);
        Assert.Contains("8.4.0", result!.TargetFrameworkVersions.Values);
        Assert.DoesNotContain("9.0.0", result.TargetFrameworkVersions.Values);
        Assert.Equal("9.0.0", result.NewestOutsideFilter);
        Assert.False(result.NoVersionWithinFilter);
    }

    [Fact]
    public async Task GetLatestVersionWithFrameworkCheckAsync_EmptyVersionWindowIsAResultNotALookupFailure() {
        // Nothing in the feed satisfies the window. Returning null here would be read as a feed
        // outage by the caller, which counts it as a failure and exits non-zero.
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, CapPageJson));
        var request = new PackageVersionRequest {
            PackageId = "My.Package",
            AllowPrerelease = false,
            CompatibleTargetFrameworks = ["net8.0"],
            VersionFilter = v => v.Major == 42
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, request);

        Assert.NotNull(result);
        Assert.True(result!.NoVersionWithinFilter);
        Assert.Equal("9.0.0", result.NewestOutsideFilter);
        Assert.Empty(result.TargetFrameworkVersions);
    }

    [Fact]
    public async Task GetLatestVersionWithFrameworkCheckAsync_StillReturnsNullWhenTheFeedHasNothingUsable() {
        // No filter involved: every listed version targets net8.0, which net472 cannot consume. That
        // is still a genuine miss and must stay null, so the "nothing in window" path does not
        // swallow real lookup problems.
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, CapPageJson));
        var request = new PackageVersionRequest {
            PackageId = "My.Package",
            AllowPrerelease = false,
            CompatibleTargetFrameworks = ["net472"]
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, request);

        Assert.Null(result);
    }

    private const string TrainPageJson = """
        {
          "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
          "@type": "catalog:CatalogPage",
          "count": 5,
          "items": [
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/1.2.3.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package", "version": "1.2.3", "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/1.2.5.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package", "version": "1.2.5", "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/1.4.0.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package", "version": "1.4.0", "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/2.0.0.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package", "version": "2.0.0", "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            },
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/2.1.0-beta.json",
              "@type": "Package",
              "catalogEntry": {
                "id": "My.Package", "version": "2.1.0-beta", "listed": true,
                "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ]
              }
            }
          ]
        }
        """;

    private static PackageVersionRequest TrainRequest(string baseline, bool prerelease = false, string tfm = "net8.0") => new() {
        PackageId = "My.Package",
        AllowPrerelease = prerelease,
        CompatibleTargetFrameworks = [tfm],
        Baseline = NuGetVersion.Parse(baseline)
    };

    [Fact]
    public async Task Baseline_CollectsTheHighestCompatibleVersionPerBumpClass() {
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, TrainPageJson));

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, TrainRequest("1.2.3"));

        Assert.NotNull(result);
        Assert.NotNull(result!.Candidates);
        Assert.Equal("1.2.5", result.Candidates!.Patch?.Version);
        Assert.Equal("1.4.0", result.Candidates.Minor?.Version);
        Assert.Equal("2.0.0", result.Candidates.Major?.Version);
        // The result's own version stays the highest one, so callers that know nothing about
        // candidates behave exactly as before.
        Assert.Contains("2.0.0", result.TargetFrameworkVersions.Values);
    }

    [Fact]
    public async Task Baseline_PrereleaseIsOnlyACandidateWhenAskedFor() {
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, TrainPageJson));

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(
            client, new NugetMetadataOptions(), logger: null, TrainRequest("1.2.3", prerelease: true));

        Assert.Equal("2.1.0-beta", result!.Candidates!.Major?.Version);
        Assert.Equal("1.4.0", result.Candidates.Minor?.Version);
    }

    [Fact]
    public async Task Baseline_AnUpToDatePackageStillReportsItsOwnVersion() {
        // Returning null here would be counted as a failed lookup by the caller.
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, TrainPageJson));

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, TrainRequest("2.0.0"));

        Assert.NotNull(result);
        Assert.Equal("2.0.0", result!.Candidates!.Major?.Version);
        Assert.Equal("2.0.0", result.Candidates.Minor?.Version);
        Assert.Equal("2.0.0", result.Candidates.Patch?.Version);
        Assert.Equal("2.0.0", result.Candidates.Current?.Version);
    }

    [Fact]
    public async Task Baseline_KeepsThePinnedVersionsManifestEvenWhenEverySlotAboveItIsFilled() {
        // 1.2.3 is below the highest patch, so it fills no slot; its manifest is still needed by the
        // reverse conflict check, and only the pinned version's manifest is kept.
        const string emptyDeps = "\"dependencies\": []";
        var pinEntry = TrainPageJson.IndexOf("\"version\": \"1.2.3\"", StringComparison.Ordinal);
        var pinDeps = TrainPageJson.IndexOf(emptyDeps, pinEntry, StringComparison.Ordinal);
        var page = TrainPageJson.Remove(pinDeps, emptyDeps.Length)
            .Insert(pinDeps, "\"dependencies\": [ { \"id\": \"Other\", \"range\": \"[1.0.0, 2.0.0)\" } ]");
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, page));

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, TrainRequest("1.2.3"));

        var current = result!.Candidates!.Current;
        Assert.NotNull(current);
        Assert.Equal("1.2.3", current!.Version);
        var dep = Assert.Single(current.Dependencies![NuGetFramework.Parse("net8.0")].Dependencies);
        Assert.Equal("Other", dep.PackageId);
        Assert.Equal("[1.0.0, 2.0.0)", dep.Range);
        Assert.Equal("1.2.5", result.Candidates.Patch?.Version);
    }

    [Fact]
    public async Task Baseline_APinTheFeedDoesNotListHasNoCurrentManifest() {
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, TrainPageJson));

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, TrainRequest("1.2.4"));

        Assert.Null(result!.Candidates!.Current);
        Assert.Equal("1.2.5", result.Candidates.Patch?.Version);
        Assert.Equal("2.0.0", result.Candidates.Major?.Version);
    }

    [Fact]
    public async Task Baseline_VersionsWithoutSupportForTheRequestedFrameworkAreNoCandidates() {
        using var client = new HttpClient(new StaticResponseHandler(CapIndexJson, TrainPageJson));

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(
            client, new NugetMetadataOptions(), logger: null, TrainRequest("1.2.3", tfm: "net472"));

        Assert.Null(result);
    }

    [Fact]
    public async Task Baseline_DoesNotFetchRegistrationPagesThatEndBelowThePin() {
        // Two pages; only the newer one can hold candidates for a pin of 1.4.0. Fetching the old one
        // would be pure waste on a package with a long history.
        const string pagedIndex = """
            {
              "count": 2,
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page1.json",
                  "@type": "catalog:CatalogPage",
                  "lower": "0.1.0", "upper": "0.9.0", "count": 1
                },
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
                  "@type": "catalog:CatalogPage",
                  "lower": "1.2.3", "upper": "2.1.0-beta", "count": 5
                }
              ]
            }
            """;
        var handler = new StaticResponseHandler(pagedIndex, TrainPageJson);
        using var client = new HttpClient(handler);

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, TrainRequest("1.4.0"));

        Assert.Equal("2.0.0", result!.Candidates!.Major?.Version);
        Assert.DoesNotContain(handler.RequestedUrls, url => url.EndsWith("page1.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Baseline_FetchesEveryPageAboveThePinOnceAndWalksAcrossThem() {
        // The candidates for a pin of 1.2.3 are spread over two pages: patch and minor on the older
        // one, major on the newer one. Both are needed, each exactly once.
        const string pagedIndex = """
            {
              "count": 2,
              "items": [
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page0.json",
                  "@type": "catalog:CatalogPage",
                  "lower": "1.2.3", "upper": "1.4.0", "count": 3
                },
                {
                  "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/page1.json",
                  "@type": "catalog:CatalogPage",
                  "lower": "2.0.0", "upper": "2.1.0-beta", "count": 2
                }
              ]
            }
            """;
        static string Page(string id, params string[] versions) => $$"""
            {
              "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/{{id}}.json",
              "@type": "catalog:CatalogPage",
              "count": {{versions.Length}},
              "items": [ {{string.Join(",", versions.Select(v => $$"""
                { "@id": "https://api.nuget.org/v3/registration5-gz-semver2/my.package/{{v}}.json", "@type": "Package",
                  "catalogEntry": { "id": "My.Package", "version": "{{v}}", "listed": true,
                    "dependencyGroups": [ { "targetFramework": "net8.0", "dependencies": [] } ] } }
                """))}} ]
            }
            """;
        var handler = new StaticResponseHandler(pagedIndex, new Dictionary<string, string> {
            ["/page0.json"] = Page("page0", "1.2.3", "1.2.5", "1.4.0"),
            ["/page1.json"] = Page("page1", "2.0.0", "2.1.0-beta")
        });
        using var client = new HttpClient(handler);

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, new NugetMetadataOptions(), logger: null, TrainRequest("1.2.3"));

        Assert.Equal("1.2.5", result!.Candidates!.Patch?.Version);
        Assert.Equal("1.4.0", result.Candidates.Minor?.Version);
        Assert.Equal("2.0.0", result.Candidates.Major?.Version);
        Assert.Equal(1, handler.RequestedUrls.Count(url => url.EndsWith("page0.json", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, handler.RequestedUrls.Count(url => url.EndsWith("page1.json", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PickNewest_MergesEachCandidateClassAcrossFeeds() {
        static PackageVersionResult Feed(string? patch, string? minor, string? major) => new() {
            PackageId = "My.Package",
            TargetFrameworkVersions = new Dictionary<NuGetFramework, string> { [NuGetFramework.AnyFramework] = major ?? "0.0.0" },
            Candidates = new PackageVersionCandidates {
                Patch = patch is null ? null : new VersionCandidate { Version = patch, TargetFrameworkVersions = [] },
                Minor = minor is null ? null : new VersionCandidate { Version = minor, TargetFrameworkVersions = [] },
                Major = major is null ? null : new VersionCandidate { Version = major, TargetFrameworkVersions = [] }
            }
        };

        // One feed has the newer patch inside the current minor, the other the newer major.
        var merged = NugetMetadataService.PickNewest(new[] { Feed("1.2.9", "1.4.0", "1.4.0"), Feed("1.2.5", "1.4.0", "2.0.0") });

        Assert.Equal("1.2.9", merged!.Candidates!.Patch?.Version);
        Assert.Equal("1.4.0", merged.Candidates.Minor?.Version);
        Assert.Equal("2.0.0", merged.Candidates.Major?.Version);
    }

    [Fact]
    public void PickNewest_TakesTheCurrentManifestFromWhicheverFeedListsThePin() {
        static PackageVersionResult Feed(string major, string? current) => new() {
            PackageId = "My.Package",
            TargetFrameworkVersions = new Dictionary<NuGetFramework, string> { [NuGetFramework.AnyFramework] = major },
            Candidates = new PackageVersionCandidates {
                Major = new VersionCandidate { Version = major, TargetFrameworkVersions = [] },
                Current = current is null ? null : new VersionCandidate { Version = current, TargetFrameworkVersions = [] }
            }
        };

        // The feed with the newest version does not list the pinned one; the other does.
        var merged = NugetMetadataService.PickNewest(new[] { Feed("2.0.0", null), Feed("1.4.0", "1.2.3") });

        Assert.Equal("2.0.0", merged!.Candidates!.Major?.Version);
        Assert.Equal("1.2.3", merged.Candidates.Current?.Version);
    }
}
