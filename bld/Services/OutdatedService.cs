using bld.Infrastructure;
using bld.Models;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

//record class NuGetRoot([property: JsonPropertyName("@id")] string Id, [property: JsonPropertyName("version")] string Version, [property: JsonPropertyName("resources")] List<NuGetResource> Resources);
internal record CatEntry(NuGetVersion Version, string[] Tfms);

internal record class Versions(List<string> versions) {
    public (NuGetVersion? nugetVersion, string? version) GetLatestVersion(bool allowPrerelease) {
        if (versions is null || versions.Count == 0) return (null, null);
        return versions
            .Select(v => NuGetVersion.TryParse(v, out var nv) ? (nv, v) : (null, v))
            .Where(v => v.nv is not null && (allowPrerelease || !v.nv.IsPrerelease))
            .OrderByDescending(v => v.nv)
            .FirstOrDefault();
    }
}

internal sealed class HttpVersionDelegatingHandler : DelegatingHandler {
    public HttpVersionDelegatingHandler(HttpMessageHandler innerHandler) : base(innerHandler) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        request.Version = new Version(2, 0);
        return base.SendAsync(request, cancellationToken);
    }
    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) {
        request.Version = new Version(2, 0);
        return base.Send(request, cancellationToken);
    }
}

internal sealed class NuGetHttpPkgService(NuGetHttpService _nugetHttpService, IConsoleOutput _console) : IAsyncDisposable {
    internal async Task<CatEntry?> GetLatestCompatible(string packageId, string? tfm, bool allowPrerelease = false, CancellationToken cancellationToken = default)
        => await _nugetHttpService.GetLatestCompatible(packageId, tfm, allowPrerelease, cancellationToken);

    internal async Task<IEnumerable<(string, CatEntry?)>> GetLatestCompatible(string packageId, IEnumerable<string> tfms, bool allowPrerelease = false, CancellationToken cancellationToken = default)
        => await _nugetHttpService.GetLatestCompatible(packageId, tfms, allowPrerelease, cancellationToken);

    public ValueTask DisposeAsync() {
        return ValueTask.CompletedTask;
    }
}

internal class NuGetHttpService(HttpClient _client, IConsoleOutput _consoleOutput) {
    internal static HttpClient CreateClient(IConsoleOutput console) {
        var handler = new HttpVersionDelegatingHandler(new HttpClientHandler {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
        });
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.TryParseAdd("Yabadabadoo");
        return client;
    }

    private async Task<NugetRegistrationIndex?> Fetch(string packageId, string? etag = default, DateTime? lastModified = default, CancellationToken cancellationToken = default) {
        var fullUrl = $"https://api.nuget.org/v3/registration5-semver1/{packageId.ToLowerInvariant()}/index.json";
        _consoleOutput.WriteDebug($"Fetching {fullUrl} ...");
        var response = default(HttpResponseMessage);
        try {
            var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
            response = _client.SendAsync(request, cancellationToken).GetAwaiter().GetResult();
            _consoleOutput.WriteInfo($"{response.StatusCode} {fullUrl}");
        }
        catch (Exception xcptn) {
            _consoleOutput.WriteWarning($"HTTP request to {fullUrl} failed: {xcptn.Message}");
            throw;
        }
        response.EnsureSuccessStatusCode();

        var allVersions = await response.Content.ReadFromJsonAsync<NugetRegistrationIndex>(cancellationToken);
        if (allVersions is null || allVersions.Items is null || allVersions.Items.Count == 0) return null;
        return allVersions;
    }

    private async Task<IEnumerable<CatEntry>> FetchEx2(string packageId, bool allowPrerelease = false, string? etag = default, DateTime? lastModified = default, CancellationToken cancellationToken = default) {
        var allVersions = await Fetch(packageId, etag, lastModified, cancellationToken);
        if (allVersions is null || allVersions.Items is null || !allVersions.Items.Any()) return Array.Empty<CatEntry>();

        var vers = new List<CatEntry>(allVersions.Items.Sum(page => page.Count));
        foreach (var item in allVersions.Items) {
            foreach (var ci in item.Items) {
                var nuVer = new NuGetVersion(ci.CatalogEntry.Version);
                if (!allowPrerelease && nuVer.IsPrerelease) continue;

                if (ci.CatalogEntry.DependencyGroups?.Any() ?? false) {
                    vers.Add(new CatEntry(nuVer, ci.CatalogEntry.DependencyGroups.Select(dg => dg.TargetFramework).ToArray()));
                }
            }
        }

        return vers;
    }

    internal async Task<IEnumerable<(string, CatEntry?)>> GetLatestCompatible(string packageId, IEnumerable<string> tfms, bool allowPrerelease = false, CancellationToken cancellationToken = default) {
        try {
            var list = await FetchEx2(packageId, allowPrerelease, cancellationToken: cancellationToken);

            var temp = tfms.Select(tfm => (tfm, list.Where(e => e.Tfms.Any(pkgTfm => IsCompatible(tfm, pkgTfm)))
                                                .OrderByDescending(e => e.Version)));
            foreach (var (tfm, entries) in temp) {
                _consoleOutput.WriteDebug($"TFM {tfm} => {string.Join(", ", entries.Select(e => e.Version.ToString()))}");
            }

            return tfms.Select(tfm => (tfm, list.Where(e => e.Tfms.Any(pkgTfm => IsCompatible(tfm, pkgTfm)))
                                                .OrderByDescending(e => e.Version)
                                                .FirstOrDefault()));
        }
        catch (Exception) {
            return Enumerable.Empty<(string, CatEntry?)>();
        }
    }

    static bool IsCompatible(string projectTfm, string packageTfm) {
        var project = NuGetFramework.Parse(projectTfm);   // e.g. "net10.0"
        var package = NuGetFramework.Parse(packageTfm);   // e.g. "net8.0"
        return DefaultCompatibilityProvider.Instance.IsCompatible(project, package);
    }

    internal async Task<CatEntry?> GetLatestCompatible(string packageId, string? tfm, bool allowPrerelease = false, CancellationToken cancellationToken = default) {
        try {
            var list = await FetchEx2(packageId, allowPrerelease, cancellationToken: cancellationToken);

            foreach (var entries in list) {
                _consoleOutput.WriteDebug($"TFM {tfm} => {entries.Version.ToString()}");
            }

            return list.Where(e => tfm is null || e.Tfms.Any(pkgTfm => IsCompatible(tfm, pkgTfm)))
                .OrderByDescending(e => e.Version)
                .FirstOrDefault();
        }
        catch (Exception) {
            return null;
        }
    }
}

internal class OutdatedService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;
    private readonly SourceCacheContext _cache;
    private readonly ILogger _logger;

    public OutdatedService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
        _cache = new SourceCacheContext();
        _logger = new NuGetLogger(_console);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> CheckOutdatedPackagesAsync(string rootPath, bool updatePackages, bool skipTfmCheck, bool includePrerelease, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded – same pattern as CleaningApplication
        MSBuildService.RegisterMSBuildDefaults(_console, _options);

        _console.WriteRule("[bold blue]bld outdated (BETA)[/]");
        _console.WriteInfo("Checking for outdated packages...");

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var fileSystem = new FileSystem(_console, errorSink);
        var cache = new ProjCfgCache(_console);

        var stopwatch = Stopwatch.StartNew();

        // Step 1: Query all package references and package versions from all projects
        var allPackageReferences = new Dictionary<string, List<PackageInfo>>(StringComparer.OrdinalIgnoreCase);
        var projectsProcessed = 0;

        try {
            var projParser = new ProjParser(_console, errorSink, _options);

            await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                await _console.StartStatusAsync($"Processing solution {slnPath}", async ctx => {
                    await foreach (var projCfg in slnParser.ParseSolution(slnPath, fileSystem)) {
                        // Only process "Release" configuration as per spec
                        if (!string.Equals(projCfg.Configuration, "Release", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!cache.Add(projCfg)) continue; // de-dupe project/configs

                        projectsProcessed++;
                        ctx.Status($"Processing project {projectsProcessed}: {Path.GetFileName(projCfg.Path)}");

                        var refs = projParser.GetPackageReferences(projCfg);
                        if (refs == null) continue;

                        _console.WriteDebug($"{projCfg.Path} TFM:{refs.TargetFramework} CPM:{refs.UseCpm} [{refs.CpmFile}]");

                        // Convert to PackageInfo objects for aggregation
                        foreach (var packageRef in refs.PackageReferences) {
                            var packageId = packageRef.Key;
                            var version = packageRef.Value;

                            // If no version in PackageReference, try to get it from PackageVersion (CPM)
                            if (string.IsNullOrEmpty(version) && refs.UseCpm == true && refs.PackageVersions?.TryGetValue(packageId, out var cpmVersion) == true) {
                                version = cpmVersion;
                            }

                            // Skip packages without a version
                            if (string.IsNullOrEmpty(version)) {
                                _console.WriteWarning($"Package {packageId} in {projCfg.Path} has no version - skipping");
                                continue;
                            }

                            var packageInfo = new PackageInfo {
                                Id = packageId,
                                Version = version,
                                ProjectPath = projCfg.Path,
                                TargetFramework = refs.TargetFramework,
                                PropsPath = refs.CpmFile,
                                FromProps = refs.UseCpm ?? false
                            };

                            if (!allPackageReferences.TryGetValue(packageId, out var list)) {
                                list = new List<PackageInfo>();
                                allPackageReferences[packageId] = list;
                            }
                            list.Add(packageInfo);
                        }
                    }
                });
            }
        }
        catch (Exception ex) {
            _console.WriteException(ex);
            return 1;
        }

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {projectsProcessed} projects");

        // Step 2: Create aggregated view of all current package versions
        var packageSummary = CreatePackageVersionSummary(allPackageReferences);
        DisplayPackageSummary(packageSummary);

        // Step 3: Query NuGet for latest versions
        var outdatedPackages = await QueryLatestVersionsAsync(packageSummary, includePrerelease, cancellationToken);

        if (outdatedPackages.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return 0;
        }

        _console.WriteInfo($"\nFound {outdatedPackages.Count} packages with available updates:");
        foreach (var kvp in outdatedPackages.OrderBy(k => k.Key)) {
            _console.WriteWarning($"{kvp.Key}: {kvp.Value.CurrentMin} → {kvp.Value.Latest}");
        }

        // Step 4: If --apply is specified, apply the package versions
        if (updatePackages) {
            await ApplyPackageUpdatesAsync(allPackageReferences, outdatedPackages, cancellationToken);
        }
        else {
            _console.WriteInfo("\nUse --apply to apply these changes.");
        }

        stopwatch.Stop();
        _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
        errorSink.WriteTo();

        return 0;
    }

    private Dictionary<string, PackageVersionSummary> CreatePackageVersionSummary(Dictionary<string, List<PackageInfo>> allPackageReferences) {
        var summary = new Dictionary<string, PackageVersionSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var (packageId, usages) in allPackageReferences) {
            var versions = usages.Select(u => u.Version).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var targetFrameworks = usages.Select(u => u.TargetFramework).Where(tfm => !string.IsNullOrEmpty(tfm)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var projectCount = usages.Select(u => u.ProjectPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var usesCpm = usages.Any(u => u.FromProps);

            summary[packageId] = new PackageVersionSummary {
                PackageId = packageId,
                CurrentVersions = versions!,
                TargetFrameworks = targetFrameworks!,
                ProjectCount = projectCount,
                UsesCentralPackageManagement = usesCpm,
                Usages = usages
            };
        }

        return summary;
    }

    private void DisplayPackageSummary(Dictionary<string, PackageVersionSummary> packageSummary) {
        _console.WriteInfo("\nPackage version summary:");
        foreach (var (packageId, summary) in packageSummary.OrderBy(kvp => kvp.Key)) {
            var versionsText = string.Join(", ", summary.CurrentVersions);
            var tfmsText = string.Join(", ", summary.TargetFrameworks);
            var cpmText = summary.UsesCentralPackageManagement ? " (CPM)" : "";
            _console.WriteDebug($"{packageId}: {versionsText} [{tfmsText}] ({summary.ProjectCount} projects){cpmText}");
        }
    }

    private async Task<Dictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>> QueryLatestVersionsAsync(
        Dictionary<string, PackageVersionSummary> packageSummary, 
        bool includePrerelease, 
        CancellationToken cancellationToken) {
        
        var outdatedPackages = new Dictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>(StringComparer.OrdinalIgnoreCase);
        var nugetHttpService = new NuGetHttpService(NuGetHttpService.CreateClient(_console), _console);
        await using var svc = new NuGetHttpPkgService(nugetHttpService, _console);

        var parallelOptions = new ParallelOptions { 
            MaxDegreeOfParallelism = Environment.ProcessorCount, 
            CancellationToken = cancellationToken 
        };

        await Parallel.ForEachAsync(packageSummary, parallelOptions, async (kvp, ct) => {
            var packageId = kvp.Key;
            var summary = kvp.Value;

            try {
                // Get the primary target framework for compatibility checking
                var primaryTfm = summary.TargetFrameworks.FirstOrDefault() ?? "net8.0";
                
                var latest = await svc.GetLatestCompatible(packageId, primaryTfm, includePrerelease, ct);
                if (latest == null) {
                    _console.WriteWarning($"No compatible version found for {packageId}");
                    return;
                }

                // Find the minimum current version used (for display)
                var currentVersions = summary.CurrentVersions
                    .Select(v => NuGetVersion.TryParse(v, out var parsedVersion) ? parsedVersion : null)
                    .Where(v => v is not null)!
                    .ToList();

                if (currentVersions.Count == 0) {
                    _console.WriteWarning($"No valid versions found for {packageId}");
                    return;
                }

                var currentMin = currentVersions.Min()!;
                if (currentMin < latest.Version) {
                    _console.WriteDebug($"Package {packageId} can be updated from {currentMin} to {latest.Version}");
                    lock (outdatedPackages) {
                        outdatedPackages[packageId] = (currentMin, latest.Version);
                    }
                }
            }
            catch (Exception ex) {
                _console.WriteWarning($"Failed to check updates for {packageId}: {ex.Message}");
            }
        });

        return outdatedPackages;
    }

    private async Task ApplyPackageUpdatesAsync(
        Dictionary<string, List<PackageInfo>> allPackageReferences,
        Dictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)> outdatedPackages,
        CancellationToken cancellationToken) {
        
        _console.WriteInfo("\nApplying package updates using Microsoft.Build.Evaluation API...");

        // Group updates by props files and project files
        var propsUpdates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var projectUpdates = new Dictionary<string, List<ProjectPackageUpdate>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (packageId, versions) in outdatedPackages) {
            var newVersion = versions.Latest.ToString();
            foreach (var usage in allPackageReferences[packageId]) {
                if (usage.FromProps && !string.IsNullOrEmpty(usage.PropsPath)) {
                    // Update Directory.Packages.props
                    if (!propsUpdates.TryGetValue(usage.PropsPath, out var propsMap)) {
                        propsMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        propsUpdates[usage.PropsPath] = propsMap;
                    }
                    propsMap[packageId] = newVersion;
                }
                else if (!usage.FromProps) {
                    // Update project file
                    if (!projectUpdates.TryGetValue(usage.ProjectPath, out var projectList)) {
                        projectList = new List<ProjectPackageUpdate>();
                        projectUpdates[usage.ProjectPath] = projectList;
                    }
                    projectList.Add(new ProjectPackageUpdate(packageId, newVersion));
                }
            }
        }

        // Apply Directory.Packages.props updates using MSBuild API
        foreach (var (propsPath, updates) in propsUpdates) {
            await UpdatePropsFileWithMSBuildAsync(propsPath, updates, cancellationToken);
            _console.WriteInfo($"Updated {updates.Count} package(s) in {Path.GetFileName(propsPath)}");
        }

        // Apply project file updates using ProjParser.SetPackageReferences
        var projParser = new ProjParser(_console, new ErrorSink(_console), _options);
        foreach (var (projectPath, updates) in projectUpdates) {
            var proj = new Proj(projectPath, null); // No parent solution
            var projCfg = new ProjCfg(proj, "Release"); // Use Release configuration
            var packageReferences = updates.ToDictionary(u => u.PackageId, u => (string?)u.NewVersion, StringComparer.OrdinalIgnoreCase);
            
            var updateInfo = new ProjectPackageReferenceInfo(
                projCfg, 
                null, // TargetFramework not needed for updates
                false, // Not using CPM for project-level updates
                null, // No CPM file
                packageReferences,
                null // No PackageVersions
            );

            projParser.SetPackageReferences(projCfg, updateInfo);
            _console.WriteInfo($"Updated {updates.Count} package(s) in {Path.GetFileName(projectPath)}");
        }
    }

    private async Task UpdatePropsFileWithMSBuildAsync(string propsPath, IReadOnlyDictionary<string, string> updates, CancellationToken cancellationToken) {
        try {
            // For Directory.Packages.props, we'll use XML manipulation since it's a props file, not a project file
            XDocument doc;
            using (var readStream = File.OpenRead(propsPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var packageVersionElements = doc.Descendants("PackageVersion");
            foreach (var element in packageVersionElements) {
                var include = element.Attribute("Include")?.Value;
                if (include is null) continue;
                if (updates.TryGetValue(include, out var newVersion)) {
                    var versionAttr = element.Attribute("Version");
                    if (versionAttr != null) versionAttr.Value = newVersion;
                }
            }

            using var writeStream = File.Create(propsPath);
            using var writer = XmlWriter.Create(writeStream, new XmlWriterSettings {
                Indent = true,
                OmitXmlDeclaration = true,
                Encoding = System.Text.Encoding.UTF8,
                Async = true
            });
            await doc.SaveAsync(writer, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {propsPath}: {ex.Message}");
        }
    }

    internal class PackageInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string? TargetFramework { get; set; }
        public string? PropsPath { get; set; }
        public bool FromProps { get; set; }
    }

    private class PackageVersionSummary {
        public string PackageId { get; set; } = string.Empty;
        public List<string> CurrentVersions { get; set; } = new();
        public List<string> TargetFrameworks { get; set; } = new();
        public int ProjectCount { get; set; }
        public bool UsesCentralPackageManagement { get; set; }
        public List<PackageInfo> Usages { get; set; } = new();
    }

    private record ProjectPackageUpdate(string PackageId, string NewVersion);

    private class NuGetLogger : ILogger {
        private readonly IConsoleOutput _console;

        public NuGetLogger(IConsoleOutput console) {
            _console = console;
        }

        public void LogDebug(string data) => _console.WriteVerbose(data);
        public void LogVerbose(string data) => _console.WriteVerbose(data);
        public void LogInformation(string data) => _console.WriteInfo(data);
        public void LogMinimal(string data) => _console.WriteInfo(data);
        public void LogWarning(string data) => _console.WriteWarning(data);
        public void LogError(string data) => _console.WriteError(data);
        public void LogInformationSummary(string data) => _console.WriteInfo(data);
        public void Log(NuGet.Common.LogLevel level, string data) {
            switch (level) {
                case NuGet.Common.LogLevel.Debug:
                case NuGet.Common.LogLevel.Verbose:
                    LogVerbose(data);
                    break;
                case NuGet.Common.LogLevel.Information:
                case NuGet.Common.LogLevel.Minimal:
                    LogInformation(data);
                    break;
                case NuGet.Common.LogLevel.Warning:
                    LogWarning(data);
                    break;
                case NuGet.Common.LogLevel.Error:
                    LogError(data);
                    break;
            }
        }

        public Task LogAsync(NuGet.Common.LogLevel level, string data) {
            Log(level, data);
            return Task.CompletedTask;
        }

        public void Log(ILogMessage message) => Log(message.Level, message.Message);

        public Task LogAsync(ILogMessage message) {
            Log(message);
            return Task.CompletedTask;
        }
    }
}