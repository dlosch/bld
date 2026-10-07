using bld.Models;
using NuGet.Configuration;
using NuGet.Versioning;
using System.Collections.Concurrent;

namespace bld.Services.NuGet;

/// <summary>
/// Where a package comes from. Once restored, that is the source restore recorded in the package's
/// .nupkg.metadata: nuget.config alone cannot tell, since a package may be on several feeds. Before a
/// restore it is the sources restore may take it from - the project's RestoreSources when it sets
/// them, otherwise the ones package source mapping assigns to it, or every enabled source, any of
/// which may serve it. RestoreAdditionalProjectSources are added in either case.
/// </summary>
internal sealed class PackageSourceLocator {
    private readonly List<PackageSource> _sources;
    private readonly PackageSourceMapping? _mapping;

    // The packages folder is shared by every project in the run, so a package is looked up once, not
    // once per project referencing it. Keyed by folder, id and version; the files do not change mid-run.
    private static readonly ConcurrentDictionary<string, (string Nupkg, string? Source)?> _located = new(StringComparer.OrdinalIgnoreCase);

    public PackageSourceLocator(ISettings settings) {
        _sources = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();
        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        _mapping = mapping.IsEnabled ? mapping : null;
        DefaultPackageFolders = [SettingsUtility.GetGlobalPackagesFolder(settings), .. SettingsUtility.GetFallbackPackageFolders(settings)];
    }

    /// <summary>The global packages folder and the fallback folders, where restore extracts without a RestorePackagesPath.</summary>
    public IReadOnlyList<string> DefaultPackageFolders { get; }

    /// <param name="versions">Versions to look for, in order; unparsable ones (ranges, floating versions) are skipped.</param>
    /// <param name="packageFolders">The folders restore extracts into, first match wins.</param>
    /// <param name="restoreSources">The project's RestoreSources, which replace the configured sources (and skip the mapping).</param>
    /// <param name="additionalSources">The project's RestoreAdditionalProjectSources.</param>
    public PackageOrigin Locate(string id, IEnumerable<string?> versions, IReadOnlyList<string> packageFolders,
        IReadOnlyList<string>? restoreSources = null, IReadOnlyList<string>? additionalSources = null) {
        foreach (var text in versions) {
            if (!NuGetVersion.TryParse(text, out var version)) continue;
            if (LocateCached(packageFolders, id, version) is not { } cached) continue;
            if (string.IsNullOrEmpty(cached.Source)) {
                return new PackageOrigin("packages folder, source not recorded", Array.Empty<string>());
            }
            var (name, url) = NameOf(cached.Source);
            return new PackageOrigin(name, Array.Empty<string>()) { Urls = UrlsOf([(name, url)]) };
        }
        var candidates = Candidates(id, restoreSources ?? Array.Empty<string>(), additionalSources ?? Array.Empty<string>());
        return new PackageOrigin(null, candidates.Select(c => c.Name).ToList()) { Urls = UrlsOf(candidates) };
    }

    private static (string Nupkg, string? Source)? LocateCached(IReadOnlyList<string> packageFolders, string id, NuGetVersion version) {
        foreach (var folder in packageFolders) {
            var hit = _located.GetOrAdd($"{folder}|{id}|{version.ToNormalizedString()}", _ => PrivatePackageBackup.Locate([folder], id, version));
            if (hit is not null) return hit;
        }
        return null;
    }

    private IReadOnlyList<(string Name, string? Url)> Candidates(string id, IReadOnlyList<string> restoreSources, IReadOnlyList<string> additionalSources) {
        IEnumerable<(string Name, string? Url)> sources;
        if (restoreSources.Count > 0) {
            // RestoreSources replaces the configured list; the mapping keys cannot refer to its entries.
            sources = restoreSources.Select(NameOf);
        }
        else if (_mapping is not null) {
            var mapped = _mapping.GetConfiguredPackageSources(id) ?? Array.Empty<string>();
            sources = _sources.Where(s => mapped.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).Select(s => (s.Name, (string?)s.Source));
        }
        else {
            // No configured source at all: restore falls back to nuget.org.
            sources = _sources.Count > 0
                ? _sources.Select(s => (s.Name, (string?)s.Source))
                : [("nuget.org", NuGetConstants.V3FeedUrl)];
        }
        return sources.Concat(additionalSources.Select(NameOf)).DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// The source's key in nuget.config and its configured URL. When no configured source matches (a
    /// package another repo pulled into the shared packages folder), the host stands in for the key so
    /// the line stays short, with the full URL in the legend; a folder is shown as the path itself.
    /// </summary>
    private (string Name, string? Url) NameOf(string source) {
        if (_sources.FirstOrDefault(s => PrivatePackageBackup.SameSource(s.Source, source)) is { } configured) return (configured.Name, configured.Source);
        if (PrivatePackageBackup.IsNuGetOrg(source)) return ("nuget.org", source);
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && !uri.IsFile ? (uri.Host, source) : (source, null);
    }

    private static Dictionary<string, string> UrlsOf(IEnumerable<(string Name, string? Url)> sources) =>
        sources.Where(s => s.Url is not null && s.Url != s.Name)
            .DistinctBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(s => s.Name, s => s.Url!, StringComparer.OrdinalIgnoreCase);
}
