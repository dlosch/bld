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

        // MaxConnectionsPerServer does not bound HTTP/2 streams, so the cap sits in front of the handler.
        var client = new HttpClient(new ThrottlingHandler(options.MaxParallelRequests) { InnerHandler = clientHandler });
        client.Timeout = options.HttpTimeout;
        if (!client.DefaultRequestHeaders.Contains("User-Agent")) {
            client.DefaultRequestHeaders.Add("User-Agent", "NugetMetadata/1.0.0");
        }
        client.DefaultRequestVersion = new Version(2, 0);
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return client;
    }

    /// <summary>Lets at most <see cref="NugetMetadataOptions.MaxParallelRequests"/> requests be in flight at once.</summary>
    private sealed class ThrottlingHandler(int maxParallelRequests) : DelegatingHandler {
        private readonly SemaphoreSlim _gate = new(Math.Max(1, maxParallelRequests));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            await _gate.WaitAsync(cancellationToken);
            try {
                return await base.SendAsync(request, cancellationToken);
            }
            finally {
                _gate.Release();
            }
        }
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

            // Parsed once: the request re-parses its TFM strings on every enumeration, and the walk
            // below enumerates them for every version it checks.
            var requestedFrameworks = request.CompatibleTargetFrameworksTyped.ToList();

            // A page that could not be read ends up empty rather than null, so the walk does not
            // ask for it a second time.
            async Task LoadPageAsync(CatalogPage2 pageItem) {
                var pageUrl = pageItem.Id;
                logger?.WriteDebug($"Requesting page {pageUrl} for {request.PackageId}");

                using var pageResponse = await GetAsync(pageUrl);
                if (!pageResponse.IsSuccessStatusCode) {
                    logger?.WriteInfo($"Failed to get page {pageUrl} for {request.PackageId}. Status: {pageResponse.StatusCode}");
                    pageItem.Items = [];
                    return;
                }

                var pageDetails2 = await pageResponse.Content.ReadFromJsonAsync<CatalogPage2>(CatalogJsonContext.Default.CatalogPage2, cancellationToken);
                pageItem.Items = pageDetails2?.Items ?? [];
                if (pageItem.Items.Count == 0) logger?.WriteDebug($"No items found in page {pageUrl} for {request.PackageId}");
            }

            // Baseline mode: instead of returning the first usable version, fill one slot per bump
            // class. The walk is newest-first, so the first hit for a class is the highest in it.
            var baseline = request.Baseline;

            // That walk goes down to the pinned version, which on a package with a long history means
            // several pages; fetching them one after the other made it as slow as the page count.
            // Every page that can hold a candidate is known from the index, so they are all requested
            // at once, and the prerelease retry below finds them loaded.
            if (baseline is not null) {
                var pending = new List<CatalogPage2>();
                for (int i = index.Items.Count - 1; i >= 0; i--) {
                    if (PageEndsBelow(index.Items[i], baseline)) break;
                    if (index.Items[i].Items is null) pending.Add(index.Items[i]);
                }
                if (pending.Count > 0) await Task.WhenAll(pending.Select(LoadPageAsync));
            }

            var allowPrerelease = request.AllowPrerelease;
            // Whether the feed lists any stable version at all, independent of framework matching.
            var sawListedStable = false;
            // Newest listed version rejected by VersionFilter, reported so the caller can tell the
            // user that an update exists outside the window they asked for.
            string? newestOutsideFilter = null;
            VersionCandidate? patchCandidate = null, minorCandidate = null, majorCandidate = null, currentCandidate = null;
retry:
            patchCandidate = minorCandidate = majorCandidate = currentCandidate = null;
            for (int i = index.Items.Count - 1; i >= 0; i--) {
                var pageItem = index.Items[i];

                // Pages are ordered oldest-first and we walk them backwards, so once a page ends
                // below the pin every remaining one does too. Without this a package pinned at an
                // old version would pull down every registration page just to find its own version,
                // which the "stop at the first match" walk never had to do.
                if (baseline is not null && PageEndsBelow(pageItem, baseline)) {
                    logger?.WriteDebug($"Skipping registration page ending at {pageItem.Upper} for {request.PackageId}: below the pinned {baseline}");
                    break;
                }

                if (pageItem.Items is null) await LoadPageAsync(pageItem);

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

                    // Anything below the pin is not a target. The baseline itself stays a candidate,
                    // so a package that is already current still reports a version rather than
                    // looking like a failed lookup.
                    var isBaseline = false;
                    if (baseline is not null) {
                        if (nugetVersion is null || nugetVersion < baseline) continue;
                        // The pinned version is looked at even when every slot above it is filled: its
                        // dependency manifest is what the reverse conflict check reads when this
                        // package stays where it is.
                        isBaseline = nugetVersion == baseline;
                        if (!isBaseline && IsSlotTaken(nugetVersion, baseline, patchCandidate, minorCandidate, majorCandidate)) continue;
                    }

                    var supportedFrameworks = new Dictionary<NuGetFramework, string>(request.CompatibleTargetFrameworks?.Count ?? 1);
                    var dependencyGroups = new Dictionary<NuGetFramework, DependencyGroup>(request.CompatibleTargetFrameworks?.Count ?? 1);

                    if (!(request.CompatibleTargetFrameworks?.Any() ?? false)) {
                        supportedFrameworks[NuGetFramework.AnyFramework] = versionItem.CatalogEntry.Version;
                    }
                    else {
                        var bestMatchDependencyGroup = default(DependencyGroup);    
                        foreach (var reqFramework in requestedFrameworks) {
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

                    if (isBaseline) {
                        currentCandidate = new VersionCandidate {
                            Version = versionItem.CatalogEntry.Version,
                            TargetFrameworkVersions = supportedFrameworks,
                            Dependencies = dependencyGroups,
                            IsPrerelease = isPrerelease
                        };
                    }

                    if (satisfiesAll) {
                        logger?.WriteDebug($"Found matching version {versionItem.CatalogEntry.Version} for {request.PackageId} with {supportedFrameworks.Count} supported frameworks");

                        if (baseline is null) {
                            return new PackageVersionResult {
                                PackageId = request.PackageId,
                                TargetFrameworkVersions = supportedFrameworks,
                                IsPrerelease = isPrerelease,
                                NewestOutsideFilter = newestOutsideFilter,

                                Dependencies = dependencyGroups
                            };
                        }

                        var candidate = new VersionCandidate {
                            Version = versionItem.CatalogEntry.Version,
                            TargetFrameworkVersions = supportedFrameworks,
                            Dependencies = dependencyGroups,
                            IsPrerelease = isPrerelease
                        };
                        majorCandidate ??= candidate;
                        if (nugetVersion!.Major == baseline.Major) minorCandidate ??= candidate;
                        if (nugetVersion.Major == baseline.Major && nugetVersion.Minor == baseline.Minor) patchCandidate ??= candidate;

                        // Nothing left to learn from older versions once every class is filled and the
                        // pin's own manifest is in hand. A pin the feed does not list walks on to the
                        // end of the loaded pages, which costs no request: IsSlotTaken skips the rest.
                        if (patchCandidate is not null && minorCandidate is not null && majorCandidate is not null && currentCandidate is not null) {
                            goto done;
                        }
                        continue;
                    }

                    if (supportedFrameworks.Any()) {
                        var missing = requestedFrameworks.Where(f => !supportedFrameworks.ContainsKey(f));
                        logger?.WriteDebug($"Skipping {request.PackageId} {versionItem.CatalogEntry.Version}: no support for {string.Join(", ", missing.Select(f => f.GetShortFolderName()))}");
                    }
                }
            }

done:
            if (baseline is not null) {
                // The prerelease retry still applies: a package with no stable release at all has to
                // be looked at again before we call it unknown.
                if (majorCandidate is null && !allowPrerelease && !request.AllowPrerelease && !sawListedStable) {
                    allowPrerelease = true;
                    newestOutsideFilter = null;
                    goto retry;
                }
                if (majorCandidate is null) {
                    // The feed knows the package but nothing at or above the pin fits the frameworks
                    // (typically the pin itself no longer does, after a target framework migration).
                    // That is an answer, not a failed lookup, so it is not null.
                    logger?.WriteDebug($"No version at or above {baseline} for {request.PackageId} with any of the requested frameworks");
                    return new PackageVersionResult {
                        PackageId = request.PackageId,
                        TargetFrameworkVersions = new Dictionary<NuGetFramework, string>(),
                        NewestOutsideFilter = newestOutsideFilter,
                        Candidates = new PackageVersionCandidates { Current = currentCandidate }
                    };
                }
                var highest = majorCandidate;
                return new PackageVersionResult {
                    PackageId = request.PackageId,
                    TargetFrameworkVersions = highest.TargetFrameworkVersions,
                    IsPrerelease = highest.IsPrerelease,
                    Dependencies = highest.Dependencies,
                    Candidates = new PackageVersionCandidates {
                        Patch = patchCandidate,
                        Minor = minorCandidate,
                        Major = majorCandidate,
                        Current = currentCandidate
                    }
                };
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

    /// <summary>Whether a registration page ends below the pin, so it cannot hold a candidate.</summary>
    private static bool PageEndsBelow(CatalogPage2 page, NuGetVersion baseline) =>
        page.Upper is { } upper && NuGetVersion.TryParse(upper, out var pageUpper) && pageUpper < baseline;

    /// <summary>
    /// Whether every bump class this version could fill is already taken, so the TFM check - the
    /// expensive part of the walk - can be skipped for it.
    /// </summary>
    private static bool IsSlotTaken(NuGetVersion version, NuGetVersion baseline, VersionCandidate? patch, VersionCandidate? minor, VersionCandidate? major) {
        if (major is null) return false;
        if (version.Major != baseline.Major) return true; // only ever fills the major slot
        if (minor is null) return false;
        if (version.Minor != baseline.Minor) return true; // only ever fills the minor slot
        return patch is not null;
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
        var all = results.Where(r => r is not null).Select(r => r!).ToList();

        foreach (var result in all) {
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
            // Each class is merged on its own: one feed can have the newest major while another has
            // the newest patch inside the current minor.
            if (all.Any(r => r.Candidates is not null)) {
                best = best with {
                    Candidates = new PackageVersionCandidates {
                        Patch = HighestCandidate(all, c => c.Patch),
                        Minor = HighestCandidate(all, c => c.Minor),
                        Major = HighestCandidate(all, c => c.Major),
                        // The same version has the same manifest on every feed that lists it.
                        Current = all.Select(r => r.Candidates?.Current).FirstOrDefault(c => c is not null)
                    }
                };
            }
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
        // Every feed that knows the package found nothing compatible: pass that on as empty
        // candidates, so the caller reports it instead of counting a failed lookup.
        if (all.FirstOrDefault(r => r.Candidates is { IsEmpty: true }) is { } known) {
            return known with {
                NewestOutsideFilter = newestOutside,
                Candidates = new PackageVersionCandidates { Current = all.Select(r => r.Candidates?.Current).FirstOrDefault(c => c is not null) }
            };
        }
        return null;
    }

    private static VersionCandidate? HighestCandidate(IEnumerable<PackageVersionResult> results, Func<PackageVersionCandidates, VersionCandidate?> slot) {
        VersionCandidate? best = null;
        NuGetVersion? bestVersion = null;
        foreach (var result in results) {
            if (result.Candidates is null) continue;
            if (slot(result.Candidates) is not { } candidate) continue;
            if (!NuGetVersion.TryParse(candidate.Version, out var version)) continue;
            if (bestVersion is null || version > bestVersion) {
                best = candidate;
                bestVersion = version;
            }
        }
        return best;
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

    /// <summary>Lowest version on this page. Present on the registration index, absent on a page body.</summary>
    [JsonPropertyName("lower")]
    public string? Lower { get; init; }

    /// <summary>Highest version on this page.</summary>
    [JsonPropertyName("upper")]
    public string? Upper { get; init; }

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

    /// <summary>
    /// The version the package is pinned at now. When set, the walk does not stop at the newest
    /// usable version but keeps going until it has the highest compatible version per bump class
    /// (see <see cref="PackageVersionResult.Candidates"/>), so the caller can offer a choice
    /// instead of a single target. Null keeps the old "first match wins" behaviour.
    /// </summary>
    public NuGetVersion? Baseline { get; init; }

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

    /// <summary>
    /// Highest compatible version per bump class relative to
    /// <see cref="PackageVersionRequest.Baseline"/>, or null when no baseline was requested. Every
    /// candidate has passed the same listing, prerelease and target framework checks as
    /// <see cref="TargetFrameworkVersions"/>, so any of them is safe to apply.
    /// </summary>
    public PackageVersionCandidates? Candidates { get; init; }

    public DateTime RetrievedAt { get; init; } = DateTime.UtcNow;
    public Dictionary<NuGetFramework, DependencyGroup>? Dependencies { get; internal set; }
}

/// <summary>
/// One version the caller may pick, with the metadata that belongs to <em>that</em> version - the
/// dependency manifest differs per version, and the conflict check has to see the one for the
/// target actually chosen.
/// </summary>
public record VersionCandidate {
    public required string Version { get; init; }
    public required Dictionary<NuGetFramework, string> TargetFrameworkVersions { get; init; }
    public Dictionary<NuGetFramework, DependencyGroup>? Dependencies { get; init; }
    public bool IsPrerelease { get; init; }
}

/// <summary>
/// The three targets a package can be moved to, cumulative: <see cref="Patch"/> is the highest
/// version sharing the baseline's major and minor, <see cref="Minor"/> the highest sharing its
/// major, <see cref="Major"/> the highest overall. A class is null when the feed has nothing
/// usable in it; all three can be the baseline itself, which is how "up to date" looks.
/// </summary>
public record PackageVersionCandidates {
    public VersionCandidate? Patch { get; init; }
    public VersionCandidate? Minor { get; init; }
    public VersionCandidate? Major { get; init; }

    /// <summary>
    /// The pinned version itself, kept for its dependency manifest rather than as a target. Null
    /// when the feed does not list that version, or lists it as unlisted or as a prerelease the
    /// request did not allow.
    /// </summary>
    public VersionCandidate? Current { get; init; }

    public VersionCandidate? Highest => Major ?? Minor ?? Patch;

    public bool IsEmpty => Patch is null && Minor is null && Major is null;
}
