using bld.Services.NuGet;
using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace bld.Tests;

public class NuGetMetadataServiceTests {
    private sealed class StaticResponseHandler : HttpMessageHandler {
        private readonly string _indexJson;
        private readonly string _pageJson;

        public StaticResponseHandler(string indexJson, string pageJson) {
            _indexJson = indexJson;
            _pageJson = pageJson;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var url = request.RequestUri?.AbsoluteUri ?? string.Empty;

            if (url.EndsWith("/index.json", StringComparison.OrdinalIgnoreCase)) {
                return Task.FromResult(CreateJsonResponse(_indexJson));
            }

            if (url.EndsWith("/page0.json", StringComparison.OrdinalIgnoreCase)) {
                return Task.FromResult(CreateJsonResponse(_pageJson));
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
}
