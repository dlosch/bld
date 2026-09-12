using bld.Infrastructure;
using NuGet.Configuration;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bld.Services.NuGet;

/// <summary>
/// A package source resolved to the registration endpoint bld's metadata client reads from, plus the
/// credentials nuget.config declares for it.
/// </summary>
internal sealed record PackageFeed(string Name, string RegistrationBaseUrl, string? Username = null, string? Password = null) {
    /// <summary>Basic auth, which is what Azure Artifacts, GitHub Packages and most private feeds accept for a PAT.</summary>
    public AuthenticationHeaderValue? Authorization => string.IsNullOrEmpty(Username)
        ? null
        : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}")));

    // Never print the credentials.
    public override string ToString() => Name;
}

/// <summary>
/// Turns the NuGet configuration (nuget.config hierarchy, package source mapping, credentials) into the
/// feeds to query for a package. Previously the metadata client only ever talked to api.nuget.org, so
/// internal packages on a private feed were reported as "not found" and never updated.
/// </summary>
internal sealed class PackageSourceResolver {
    // Newest first. The versioned resources differ only in what the registration blobs contain; any of
    // them serves the index/page shape the client parses.
    private static readonly string[] RegistrationResourceTypes = [
        "RegistrationsBaseUrl/3.6.0",
        "RegistrationsBaseUrl/3.4.0",
        "RegistrationsBaseUrl/Versioned",
        "RegistrationsBaseUrl",
    ];

    private readonly IConsoleOutput? _console;
    private readonly HttpClient _httpClient;
    private readonly string _defaultRegistrationBaseUrl;
    private readonly IReadOnlyList<PackageSource> _sources;
    private readonly PackageSourceMapping? _mapping;
    // Lazy so a source is resolved (and warned about) once even when the per-package lookups run in
    // parallel; ConcurrentDictionary.GetOrAdd may invoke a plain factory several times under contention.
    private readonly ConcurrentDictionary<string, Lazy<Task<PackageFeed?>>> _resolved = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _unmappedWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="settings">Use <see cref="LoadSettings"/> for the real hierarchy; tests pass a specific file or <see cref="NullSettings"/>.</param>
    /// <param name="explicitSources">--source values: names of configured sources or URLs. When given, mapping is ignored.</param>
    /// <param name="defaultRegistrationBaseUrl">Registration endpoint of nuget.org, used without a service-index round trip.</param>
    public PackageSourceResolver(IConsoleOutput? console, HttpClient httpClient, ISettings settings, IReadOnlyList<string>? explicitSources, bool ignoreSourceMapping, string defaultRegistrationBaseUrl) {
        _console = console;
        _httpClient = httpClient;
        _defaultRegistrationBaseUrl = defaultRegistrationBaseUrl;

        var configured = new PackageSourceProvider(settings).LoadPackageSources().Where(s => s.IsEnabled).ToList();

        if (explicitSources is { Count: > 0 }) {
            _sources = explicitSources
                .Select(s => configured.FirstOrDefault(c => string.Equals(c.Name, s, StringComparison.OrdinalIgnoreCase)) ?? new PackageSource(s))
                .ToList();
            _mapping = null;
            return;
        }

        // No configured source at all (no nuget.config anywhere): behave as before and use nuget.org.
        _sources = configured.Count > 0
            ? configured
            : [new PackageSource(NuGetConstants.V3FeedUrl, "nuget.org")];

        var mapping = PackageSourceMapping.GetPackageSourceMapping(settings);
        _mapping = !ignoreSourceMapping && mapping.IsEnabled ? mapping : null;
    }

    /// <summary>The nuget.config hierarchy as NuGet itself sees it from <paramref name="rootDirectory"/> (repo, user, machine).</summary>
    public static ISettings LoadSettings(string rootDirectory) => Settings.LoadDefaultSettings(rootDirectory);

    public IReadOnlyList<PackageSource> Sources => _sources;

    public bool UsesSourceMapping => _mapping is not null;

    /// <summary>True when package source mapping is on and assigns <paramref name="packageId"/> to no source at all.</summary>
    public bool IsUnmapped(string packageId) =>
        _mapping is not null && (_mapping.GetConfiguredPackageSources(packageId) is not { Count: > 0 });

    public string Describe() =>
        string.Join(", ", _sources.Select(s => $"{s.Name} ({s.Source})")) + (_mapping is null ? "" : " [package source mapping enabled]");

    /// <summary>
    /// The feeds that may serve <paramref name="packageId"/>: every enabled source, or only the ones package
    /// source mapping assigns to it. Sources that cannot be used (unreachable, v2, local) are dropped with a
    /// warning the first time they are seen.
    /// </summary>
    public async Task<IReadOnlyList<PackageFeed>> GetFeedsForAsync(string packageId, CancellationToken cancellationToken) {
        var sources = _sources;
        if (_mapping is not null) {
            var names = _mapping.GetConfiguredPackageSources(packageId);
            if (names is null || names.Count == 0) {
                if (_unmappedWarned.TryAdd(packageId, 0)) {
                    _console?.WriteWarning($"Package {packageId} matches no packageSourceMapping pattern in nuget.config; NuGet would not restore it from any source.");
                }
                return Array.Empty<PackageFeed>();
            }
            sources = _sources.Where(s => names.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        var feeds = new List<PackageFeed>(sources.Count);
        foreach (var source in sources) {
            var feed = await _resolved.GetOrAdd(source.Source, _ => new Lazy<Task<PackageFeed?>>(() => ResolveAsync(source, cancellationToken))).Value;
            if (feed is not null) feeds.Add(feed);
        }
        return feeds;
    }

    private async Task<PackageFeed?> ResolveAsync(PackageSource source, CancellationToken cancellationToken) {
        if (!source.IsHttp) {
            _console?.WriteWarning($"Package source {source.Name} ({source.Source}) is not an HTTP feed and is skipped.");
            return null;
        }
        // PackageSource.ProtocolVersion defaults to 2 even for v3 feeds unless nuget.config says otherwise;
        // NuGet itself tells them apart by the service index suffix.
        if (!source.Source.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
            _console?.WriteWarning($"Package source {source.Name} ({source.Source}) is not a NuGet v3 feed (no index.json), which is not supported; skipped.");
            return null;
        }

        string? username = null, password = null;
        if (source.Credentials is { } credentials) {
            username = credentials.Username;
            try {
                password = credentials.Password;
            }
            catch (Exception ex) {
                // Encrypted passwords can only be decrypted on the Windows account that stored them.
                _console?.WriteWarning($"Could not read the credentials for package source {source.Name} ({ex.FormatMessage()}); querying it without authentication.");
                username = null;
            }
        }

        if (string.Equals(source.Source.TrimEnd('/'), NuGetConstants.V3FeedUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) {
            return new PackageFeed(source.Name, _defaultRegistrationBaseUrl, username, password);
        }

        try {
            using var message = new HttpRequestMessage(HttpMethod.Get, source.Source);
            if (username is not null) {
                message.Headers.Authorization = new PackageFeed(source.Name, "", username, password).Authorization;
            }
            using var response = await _httpClient.SendAsync(message, cancellationToken);
            response.EnsureSuccessStatusCode();
            var index = await response.Content.ReadFromJsonAsync(ServiceIndexJsonContext.Default.ServiceIndex, cancellationToken);

            var registration = RegistrationResourceTypes
                .Select(type => index?.Resources?.FirstOrDefault(r => string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase))?.Id)
                .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));

            if (registration is null) {
                _console?.WriteWarning($"Package source {source.Name} ({source.Source}) advertises no RegistrationsBaseUrl resource; skipped.");
                return null;
            }

            if (!registration.EndsWith('/')) registration += "/";
            _console?.WriteDebug($"Package source {source.Name}: registration base {registration}");
            return new PackageFeed(source.Name, registration, username, password);
        }
        catch (JsonException) {
            _console?.WriteWarning($"Package source {source.Name} ({source.Source}) did not return a NuGet v3 service index; skipped.");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _console?.WriteWarning($"Package source {source.Name} ({source.Source}) is unavailable and is skipped for this run: {ex.FormatMessage()}");
            return null;
        }
    }
}

internal sealed record ServiceIndex([property: JsonPropertyName("resources")] List<ServiceResource>? Resources);

internal sealed record ServiceResource(
    [property: JsonPropertyName("@id")] string? Id,
    [property: JsonPropertyName("@type")] string? Type);

[JsonSerializable(typeof(ServiceIndex))]
internal partial class ServiceIndexJsonContext : JsonSerializerContext;
