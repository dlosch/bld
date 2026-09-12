using bld.Infrastructure;
using NuGet.Frameworks;
using NuGet.Versioning;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

[assembly: InternalsVisibleTo("bld.Tests")]
namespace bld.Services.NuGet;

public static class NugetMetadataService {

    public static HttpClient CreateHttpClient(NugetMetadataOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        var clientHandler = new HttpClientHandler {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        var client = new HttpClient(clientHandler);
        client.Timeout = options.HttpTimeout;
        if (!client.DefaultRequestHeaders.Contains("User-Agent")) {
            client.DefaultRequestHeaders.Add("User-Agent", "NugetMetadata/1.0.0");
        }
        client.DefaultRequestVersion = new Version(2, 0);
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return client;
    }

    private static FrameworkReducer _frameworkReducer = new FrameworkReducer();
    private static DefaultCompatibilityProvider _compatibilityProvider = new DefaultCompatibilityProvider();

    /// <param name="feed">Registration endpoint and credentials to use; null means nuget.org as configured in <paramref name="options"/>.</param>
    internal static async ValueTask<PackageVersionResult?> GetLatestVersionWithFrameworkCheckAsync(
        HttpClient httpClient,
        NugetMetadataOptions options,
        IConsoleOutput? logger,
        PackageVersionRequest request,
        PackageFeed? feed = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.PackageId))
            throw new ArgumentException("PackageId cannot be null or empty", nameof(request));

        // Every request goes through here so the feed's credentials also reach the page URLs the
        // registration index hands back.
        async Task<HttpResponseMessage> GetAsync(string url) {
            using var message = new HttpRequestMessage(HttpMethod.Get, url);
            if (feed?.Authorization is { } authorization) message.Headers.Authorization = authorization;
            return await httpClient.SendAsync(message, cancellationToken);
        }

        try {
            var registrationBase = feed?.RegistrationBaseUrl ?? options.RegistrationBaseUrl;
            if (!registrationBase.EndsWith('/')) registrationBase += "/";
            var indexUrl = $"{registrationBase}{request.PackageId.ToLowerInvariant()}/index.json";
            logger?.WriteDebug($"Getting registration index for package {request.PackageId} at {indexUrl}");

            var indexResponse = await GetAsync(indexUrl);

            if (!indexResponse.IsSuccessStatusCode) {
                if (indexResponse.StatusCode == System.Net.HttpStatusCode.NotFound) {
                    logger?.WriteInfo($"Package {request.PackageId} not found");
                    return null;
                }

                logger?.WriteInfo($"Failed to get registration index for {request.PackageId}. Status: {indexResponse.StatusCode}");
                return null;
            }

            var index = await indexResponse.Content.ReadFromJsonAsync<CatalogRoot2>(CatalogJsonContext.Default.CatalogRoot2, cancellationToken);

            if (index?.Items == null || !index.Items.Any()) {
                logger?.WriteDebug($"No pages found in registration index for {request.PackageId}");
                return null;
            }

            var allowPrerelease = request.AllowPrerelease;
            // Whether the feed lists any stable version at all, independent of framework matching.
            var sawListedStable = false;
            // Newest listed version rejected by VersionFilter, reported so the caller can tell the
            // user that an update exists outside the window they asked for.
            string? newestOutsideFilter = null;
retry:
            for (int i = index.Items.Count - 1; i >= 0; i--) {
                var pageItem = index.Items[i];

                if (pageItem.Items is null) {
                    var pageUrl = pageItem.Id;
                    logger?.WriteDebug($"Requesting page {pageUrl} for {request.PackageId}");

                    var pageResponse = await GetAsync(pageUrl);
                    if (!pageResponse.IsSuccessStatusCode) {
                        logger?.WriteInfo($"Failed to get page {pageUrl} for {request.PackageId}. Status: {pageResponse.StatusCode}");
                        continue;
                    }

                    var pageDetails2 = await pageResponse.Content.ReadFromJsonAsync<CatalogPage2>(CatalogJsonContext.Default.CatalogPage2, cancellationToken);
                    if (pageDetails2 is null || pageDetails2.Items is null || !pageDetails2.Items.Any()) {
                        logger?.WriteDebug($"No items found in page {pageUrl} for {request.PackageId}");
                        continue;
                    }

                    pageItem.Items = pageDetails2.Items;
                }

                var page = pageItem;

                if (page?.Items == null || !page.Items.Any())
                    continue;

                // Scan versions in page from bottom to top (latest first)
                for (int j = page.Items.Count - 1; j >= 0; j--) {
                    var versionItem = page.Items[j];

                    if (!versionItem.CatalogEntry.Listed)
                        continue;

                    var isPrerelease = NuGetVersion.TryParse(versionItem.CatalogEntry.Version, out var nugetVersion) && nugetVersion.IsPrerelease;
                    if (!isPrerelease) sawListedStable = true;
                    if (!allowPrerelease && isPrerelease)
                        continue;

                    // Version window (e.g. --max-bump minor). Applied before the framework check so a
                    // capped run does not pay for TFM matching on versions it can never propose.
                    // sawListedStable deliberately counts versions above the cap: the prerelease retry
                    // asks "does this package have any stable release at all", not "within the window".
                    if (request.VersionFilter is { } versionFilter) {
                        if (nugetVersion is null) continue;
                        if (!versionFilter(nugetVersion)) {
                            // Pages are walked newest-first, so the first rejection is the newest one.
                            newestOutsideFilter ??= versionItem.CatalogEntry.Version;
                            continue;
                        }
                    }

                    var supportedFrameworks = new Dictionary<NuGetFramework, string>(request.CompatibleTargetFrameworks?.Count ?? 1);
                    var dependencyGroups = new Dictionary<NuGetFramework, DependencyGroup>(request.CompatibleTargetFrameworks?.Count ?? 1);

                    if (!(request.CompatibleTargetFrameworks?.Any() ?? false)) {
                        supportedFrameworks[NuGetFramework.AnyFramework] = versionItem.CatalogEntry.Version;
                    }
                    else {
                        var bestMatchDependencyGroup = default(DependencyGroup);    
                        foreach (var reqFramework in request.CompatibleTargetFrameworksTyped) {
                            if (versionItem.CatalogEntry.DependencyGroups is null || !versionItem.CatalogEntry.DependencyGroups.Any()) {
                                logger?.WriteDebug($"Package {request.PackageId} version {versionItem.CatalogEntry.Version} has no dependency groups, assuming it supports all frameworks");
                                supportedFrameworks[reqFramework] = versionItem.CatalogEntry.Version;
                            }
                            else {
                                var reqNuGetFramework = reqFramework; // NuGetFramework.Parse(reqFramework);
                                var hasMatchingFramework = false;
                                if (versionItem.CatalogEntry.DependencyGroups.Count == 1) {
                                    var dg = versionItem.CatalogEntry.DependencyGroups.First();
                                    hasMatchingFramework = string.IsNullOrWhiteSpace(dg.TargetFramework)
                                        || _compatibilityProvider.IsCompatible(reqNuGetFramework, NuGetFramework.Parse(dg.TargetFramework))
                                        || reqFramework.Equals(NuGetFramework.AnyFramework);

                                    if (hasMatchingFramework) bestMatchDependencyGroup = dg;
                                }
                                else {
                                    var allTfms = versionItem.CatalogEntry.DependencyGroups
                                        .Select(dg => NuGetFramework.Parse(dg.TargetFramework));

                                    var bestMatch = _frameworkReducer.GetNearest(reqNuGetFramework, allTfms);
                                    if (bestMatch != null) {
                                        logger?.WriteDebug($"[{request.PackageId}@{reqFramework}] Best match for {reqFramework} is {bestMatch.GetShortFolderName()}");
                                        hasMatchingFramework = true;

                                        bestMatchDependencyGroup = versionItem.CatalogEntry.DependencyGroups.FirstOrDefault(dg => {
                                            if (string.IsNullOrWhiteSpace(dg.TargetFramework) && bestMatch.IsAny)
                                                return true;
                                            var dgFramework = NuGetFramework.Parse(dg.TargetFramework);
                                            return dgFramework.Equals(bestMatch);
                                        });
                                    }
                                }

                                if (hasMatchingFramework) {
                                    supportedFrameworks[reqFramework] = versionItem.CatalogEntry.Version;
                                    if (bestMatchDependencyGroup != null) dependencyGroups[reqFramework] = bestMatchDependencyGroup!;                                 
                                }
                            }
                        }
                    }

                    // Every requested framework must be supported, not merely one of them. Accepting a
                    // partial match let a solution mixing net472 and net9.0 be upgraded to a version
                    // that dropped net472, breaking the build the TFM check exists to protect.
                    var requestedCount = request.CompatibleTargetFrameworks?.Count ?? 0;
                    var satisfiesAll = requestedCount == 0
                        ? supportedFrameworks.Any()
                        : supportedFrameworks.Count >= requestedCount;

                    if (satisfiesAll) {
                        logger?.WriteDebug($"Found matching version {versionItem.CatalogEntry.Version} for {request.PackageId} with {supportedFrameworks.Count} supported frameworks");

                        return new PackageVersionResult {
                            PackageId = request.PackageId,
                            TargetFrameworkVersions = supportedFrameworks,
                            IsPrerelease = isPrerelease,
                            NewestOutsideFilter = newestOutsideFilter,

                            Dependencies = dependencyGroups
                        };
                    }

                    if (supportedFrameworks.Any()) {
                        var missing = request.CompatibleTargetFrameworksTyped.Where(f => !supportedFrameworks.ContainsKey(f));
                        logger?.WriteDebug($"Skipping {request.PackageId} {versionItem.CatalogEntry.Version}: no support for {string.Join(", ", missing.Select(f => f.GetShortFolderName()))}");
                    }
                }
            }

            // Retry allowing prerelease only for packages that genuinely have no stable release. The
            // old trigger was "no match for these frameworks", which also fires on a framework
            // mismatch - so a stable-only feed could still yield a prerelease that --apply then pinned.
            if (!allowPrerelease && !request.AllowPrerelease && !sawListedStable) {
                allowPrerelease = true;
                // The second pass sees a different candidate set, so anything recorded in the first
                // pass is not necessarily the newest rejected version any more.
                newestOutsideFilter = null;
                goto retry;
            }

            // A version window that excluded every candidate is a result, not a lookup failure. The
            // caller counts null as a feed outage and exits non-zero, which would turn "pinned at a
            // prerelease major with no stable release in that major" into a CI failure.
            if (newestOutsideFilter is not null) {
                logger?.WriteDebug($"No version within the requested window for {request.PackageId}; newest outside it is {newestOutsideFilter}");
                return new PackageVersionResult {
                    PackageId = request.PackageId,
                    TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
                    NewestOutsideFilter = newestOutsideFilter,
                    NoVersionWithinFilter = true
                };
            }

            logger?.WriteDebug($"No matching version found for {request.PackageId} with any of the requested frameworks");
            return null;
        }
        catch (Exception ex) {
            logger?.WriteError($"Error fetching package metadata for {request.PackageId}{(feed is null ? "" : $" from {feed.Name}")}: {ex.FormatMessage()}", ex);
            return null;
        }
    }

    /// <summary>
    /// Merges the per-feed answers for one package: the highest version wins, "nothing inside the
    /// --max-bump window" only counts when no feed had a candidate, and the held-back version is the
    /// newest any feed rejected. Null when no feed knows the package.
    /// </summary>
    internal static PackageVersionResult? PickNewest(IEnumerable<PackageVersionResult?> results) {
        PackageVersionResult? best = null;
        NuGetVersion? bestVersion = null;
        string? newestOutside = null;
        NuGetVersion? newestOutsideVersion = null;
        var sawWindowMiss = false;
        string? packageId = null;

        foreach (var result in results) {
            if (result is null) continue;
            packageId ??= result.PackageId;

            if (result.NewestOutsideFilter is { } outside && NuGetVersion.TryParse(outside, out var outsideVersion)
                && (newestOutsideVersion is null || outsideVersion > newestOutsideVersion)) {
                newestOutside = outside;
                newestOutsideVersion = outsideVersion;
            }

            if (result.NoVersionWithinFilter) {
                sawWindowMiss = true;
                continue;
            }

            var versionText = result.TargetFrameworkVersions.Values.FirstOrDefault();
            if (!NuGetVersion.TryParse(versionText, out var version)) continue;
            if (bestVersion is null || version > bestVersion) {
                best = result;
                bestVersion = version;
            }
        }

        if (best is not null) {
            return newestOutside is null ? best : best with { NewestOutsideFilter = newestOutside };
        }
        if (sawWindowMiss) {
            return new PackageVersionResult {
                PackageId = packageId ?? string.Empty,
                TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
                NewestOutsideFilter = newestOutside,
                NoVersionWithinFilter = true,
            };
        }
        return null;
    }
}

public record CatalogRoot2 {
    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<CatalogPage2> Items { get; init; } = [];
}

public record CatalogPage2 {
    [JsonPropertyName("@id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("@type")]
    public string Type { get; init; } = string.Empty;
    [JsonPropertyName("commitId")]
    public string CommitId { get; init; } = string.Empty;
    [JsonPropertyName("commitTimeStamp")]
    public string CommitTimeStamp { get; init; } = string.Empty;

    [JsonPropertyName("count")]
    public int Count { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<CatalogPageDetails2>? Items { get; set; } = default;
}


public record CatalogPageDetails2 {
    [JsonPropertyName("@id")]
    public string Id { get; init; } = string.Empty;
    [JsonPropertyName("@type")]
    public string Type { get; init; } = string.Empty;
    [JsonPropertyName("commitId")]
    public string CommitId { get; init; } = string.Empty;
    [JsonPropertyName("commitTimeStamp")]
    public string CommitTimeStamp { get; init; } = string.Empty;

    [JsonPropertyName("catalogEntry")]
    public CatalogEntry2 CatalogEntry { get; init; } = new();

    [JsonPropertyName("packageContent")]
    public string PackageContent { get; init; } = string.Empty;
    [JsonPropertyName("registration")]
    public string Registration { get; init; } = string.Empty;
}

public record CatalogEntry2 {
    [JsonPropertyName("@id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string PackageId { get; init; } = string.Empty;

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;
    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;
    [JsonPropertyName("description")]
    public string description { get; init; } = string.Empty;
    [JsonPropertyName("listed")]
    public bool Listed { get; init; } = false;
    [JsonPropertyName("packageContent")]
    public string packageContent { get; init; } = string.Empty;

    [JsonPropertyName("dependencyGroups")]
    public IReadOnlyList<DependencyGroup> DependencyGroups { get; init; } = [];

    [JsonPropertyName("published")]
    public DateTimeOffset Published { get; init; }
}

public record DependencyGroup {
    [JsonPropertyName("@id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("targetFramework")]
    public string TargetFramework { get; init; } = string.Empty;

    [JsonPropertyName("dependencies")]
    public IReadOnlyList<Dependency> Dependencies { get; init; } = [];
}

public record Dependency {
    [JsonPropertyName("@id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string PackageId { get; init; } = string.Empty;

    [JsonPropertyName("range")]
    public string Range { get; init; } = string.Empty;

    [JsonPropertyName("registration")]
    public string Registration { get; init; } = string.Empty;
}

[JsonSerializable(typeof(CatalogRoot2))]
[JsonSerializable(typeof(CatalogPage2))]
[JsonSerializable(typeof(CatalogPageDetails2))]
[JsonSerializable(typeof(CatalogEntry2))]
[JsonSerializable(typeof(DependencyGroup))]
[JsonSerializable(typeof(Dependency))]
public partial class CatalogJsonContext : JsonSerializerContext;

public record NugetMetadataOptions {
    public string BaseUrl { get; init; } = "https://api.nuget.org/v3-flatcontainer/";
    public string RegistrationBaseUrl { get; init; } = "https://api.nuget.org/v3/registration5-gz-semver2/";
    public string SearchQueryUrl { get; init; } = "https://azuresearch-usnc.nuget.org/query";
    public string PackageVersionsUrl { get; init; } = "https://api.nuget.org/v3-flatcontainer/";
    public TimeSpan CacheExpiration { get; init; } = TimeSpan.FromMinutes(30);
    public TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxParallelRequests { get; init; } = 10;
}

public record PackageVersionRequest {
    public required string PackageId { get; init; }
    public bool AllowPrerelease { get; init; }
    public bool IsPrivateAssets { get; init; } = false;

    /// <summary>
    /// Optional upper bound on the versions that may be proposed, e.g. "same major as the current
    /// pin" for <c>--max-bump minor</c>. Null means every listed version is a candidate.
    /// </summary>
    public Func<NuGetVersion, bool>? VersionFilter { get; init; }

    public required IReadOnlyList<string> CompatibleTargetFrameworks { get; init; }
    public IEnumerable<NuGetFramework> CompatibleTargetFrameworksTyped => CompatibleTargetFrameworks
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Select(tf => NuGetFramework.Parse(tf));

    public IEnumerable<string> CompatibleTargetFrameworksOrdered => CompatibleTargetFrameworksTyped
        .OrderDescending()
        .Select(tf => tf.GetShortFolderName());
        
}


public record PackageVersionResult {
    public required string PackageId { get; init; }
    public required Dictionary<NuGetFramework, string> TargetFrameworkVersions { get; init; }
    public bool IsPrerelease { get; init; }

    /// <summary>
    /// Newest listed version that <see cref="PackageVersionRequest.VersionFilter"/> rejected, or null
    /// when no filter was set or nothing was rejected. Informational only - it has not been checked
    /// for target framework compatibility.
    /// </summary>
    public string? NewestOutsideFilter { get; init; }

    /// <summary>
    /// True when <see cref="PackageVersionRequest.VersionFilter"/> excluded every candidate, so
    /// <see cref="TargetFrameworkVersions"/> is empty by design. Distinguishes "nothing to propose
    /// inside the window" from a failed lookup, which the caller must not treat the same way.
    /// </summary>
    public bool NoVersionWithinFilter { get; init; }

    public DateTime RetrievedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<NuGetFramework, DependencyGroup>? Dependencies { get; internal set; }
}
