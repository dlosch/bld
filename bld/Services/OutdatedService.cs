#define NUGET_PROJECT
using bld.Infrastructure;
using bld.Models;
using Microsoft.Extensions.Caching.Memory;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using NugetMetadata.Configuration;
using NugetMetadata.Models;
using NugetMetadata.Services;
using Spectre.Console;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;
#if FALSE
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


    internal async Task<CatEntry?> GetLatestCompatible(string packageId, string? tfm, bool alloPrerelease = false, CancellationToken cancellationToken = default)
        => await _nugetHttpService.GetLatestCompatible(packageId, tfm, alloPrerelease, cancellationToken);

    internal async Task<IEnumerable<(string, CatEntry?)>> GetLatestCompatible(string packageId, IEnumerable<string> tfms, bool alloPrerelease = false, CancellationToken cancellationToken = default)
        => await _nugetHttpService.GetLatestCompatible(packageId, tfms, alloPrerelease, cancellationToken);

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
        //var client = new HttpClient();
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
            //response = await _client.SendAsync(request, cancellationToken);
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

    private async IAsyncEnumerable<CatEntry> FetchEx(string packageId, bool allowPrerelease = false, string? etag = default, DateTime? lastModified = default, CancellationToken cancellationToken = default) {
        var allVersions = await Fetch(packageId, etag, lastModified, cancellationToken);
        if (allVersions is null || allVersions.Items is null || !allVersions.Items.Any()) yield break;
        //var vers = new List<CatEntry>(allVersions.Items.Sum(page => page.Count));

        foreach (var item in allVersions.Items) {
            foreach (var ci in item.Items) {
                var nuVer = new NuGetVersion(ci.CatalogEntry.Version);
                if (!allowPrerelease && nuVer.IsPrerelease) continue;

                if (ci.CatalogEntry.DependencyGroups?.Any() ?? false) {
                    yield return new CatEntry(nuVer, ci.CatalogEntry.DependencyGroups.Select(dg => dg.TargetFramework).ToArray());
                }
            }
        }
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

    internal IAsyncEnumerable<CatEntry> GetVersionList(string packageId, bool alloPrerelease = false, CancellationToken cancellationToken = default) => FetchEx(packageId, alloPrerelease, default, default, cancellationToken);

    internal async Task<IEnumerable<(string, CatEntry?)>> GetLatestCompatible(string packageId, IEnumerable<string> tfms, bool alloPrerelease = false, CancellationToken cancellationToken = default) {
        try {
            var list = await FetchEx2(packageId, alloPrerelease, cancellationToken: cancellationToken);


            var temp = tfms.Select(tfm => (tfm, list.Where(e => e.Tfms.Any(pkgTfm => IsCompatible(tfm, pkgTfm)))
                                                .OrderByDescending(e => e.Version)));
            foreach (var (tfm, entries) in temp) {
                _consoleOutput.WriteDebug($"TFM {tfm} => {string.Join(", ", entries.Select(e => e.Version.ToString()))}");
            }


            return tfms.Select(tfm => (tfm, list.Where(e => e.Tfms.Any(pkgTfm => IsCompatible(tfm, pkgTfm)))
                                                .OrderByDescending(e => e.Version)
                                                .FirstOrDefault()));
        }
        catch (Exception ex) {
            //console?.WriteWarning($"HTTP request to {url} failed: {ex.Message}");
            return null;
        }

    }

    static bool IsCompatible(string projectTfm, string packageTfm) {
        var project = NuGetFramework.Parse(projectTfm);   // e.g. "net10.0"
        var package = NuGetFramework.Parse(packageTfm);   // e.g. "net8.0"
        return DefaultCompatibilityProvider.Instance.IsCompatible(project, package);
    }
    static NuGetFramework? GetBestCompatible(string projectTfm, IEnumerable<string> packageTfms) {
        var reducer = new FrameworkReducer();
        var project = NuGetFramework.Parse(projectTfm);
        var packageFrameworks = packageTfms.Select(NuGetFramework.Parse);
        return reducer.GetNearest(project, packageFrameworks); // null => incompatible
    }

    internal async Task<CatEntry?> GetLatestCompatible(string packageId, string? tfm, bool alloPrerelease = false, CancellationToken cancellationToken = default) {
        try {
            // https://api.nuget.org/v3/registration5-semver1/microsoft.data.sqlclient/index.json
            //await foreach (var ver in GetVersionList(packageId, alloPrerelease, cancellationToken)) {

            //}
            var list = await FetchEx2(packageId, alloPrerelease, cancellationToken: cancellationToken);


            foreach (var entries in list) {
                _consoleOutput.WriteDebug($"TFM {tfm} => {entries.Version.ToString()}");
            }



            return list.Where(e => tfm is null || e.Tfms.Any(pkgTfm => IsCompatible(tfm, pkgTfm)))
                .OrderByDescending(e => e.Version)
                .FirstOrDefault();

            //// Enumerate
            //await foreach (var entry in 
            //    .Where(e => e.Version is not null)
            //    .WithCancellation(cancellationToken)) {
            //    // use entry
            //    if (IsCompatible(tfm ?? "net10.0", entry.Tfms.FirstOrDefault() ?? "")) {
            //        return entry.Version.ToString();
            //    }
            //}

            //var fullUrl = $"https://api.nuget.org/v3/registration5-semver1/{packageId.ToLowerInvariant()}/index.json";
            //var response = await _client.GetAsync(fullUrl, cancellationToken);
            //response.EnsureSuccessStatusCode();

            //var allVersions = await response.Content.ReadFromJsonAsync<NugetRegistrationIndex>(cancellationToken);
            //if (allVersions is null || allVersions.Items is null || allVersions.Items.Count == 0) return null;

            //var vers = new List<CatEntry>();
            //foreach (var item in allVersions.Items) {
            //    foreach (var ci in item.Items) {
            //        var ver = new CatEntry( new NuGetVersion(ci.CatalogEntry.Version));
            //        vers.Add(ver);
            //        foreach (var dep in ci.CatalogEntry.DependencyGroups) {
            //            ver.Add(dep.TargetFramework);
            //        }
            //    }
            //}

            //var filtered = vers
            //    .Where(v => alloPrerelease || !v.Version.IsPrerelease)
            //    .Where(v => tfm is null || v.Tfms.Any(x => IsCompatible(tfm, x)))
            //    .OrderByDescending(v => v.Version)
            //    .FirstOrDefault();

            //return filtered?.Version.ToString();

            ////var versionListUrl = $"https://api.nuget.org/v3-flatcontainer/{packageId.ToLowerInvariant()}/index.json";

            ////var response = await _client.GetAsync(versionListUrl, cancellationToken);
            ////response.EnsureSuccessStatusCode();

            ////var allVersions = await response.Content.ReadFromJsonAsync<Versions>(cancellationToken);

            ////// https://api.nuget.org/v3/registration5-semver1/newtonsoft.json/index.json


            ////if (allVersions is null) return null;
            ////var (nugetVersion, version) = allVersions.GetLatestVersion(alloPrerelease));
            ////if (nugetVersion is null) return null;
            ////// https://api.nuget.org/v3/registration5-gz-semver2/microsoft.extensions.http/page/10.0.0-preview.7.25380.108/10.0.0-preview.7.25380.108.json

            ////// https://api.nuget.org/v3/registration5-semver1/newtonsoft.json/13.0.3.json => https://api.nuget.org/v3/catalog0/data/2023.03.08.07.46.17/newtonsoft.json.13.0.3.json
            ////// https://api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/page/13.0.3/13.0.3.json
            ////// "https://api.nuget.org/v3/registration5-gz-semver2/microsoft.extensions.http/page/10.0.0-preview.7.25380.108/10.0.0-preview.7.25380.108.json
            ////var versionMetadataUrl = $"https://api.nuget.org/v3/registration5-semver1/{packageId.ToLowerInvariant()}/{nugetVersion}.json";
        }
        catch (Exception ex) {
            //console?.WriteWarning($"HTTP request to {url} failed: {ex.Message}");
            return null;
        }
    }
}
#endif
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

    /*
     * XDocument version pseudocode
     *  enmerate all ProjCfg from solution.
     *      we only process the "Release" configuration (there would normally be two "Debug" and "Release")
     *      use XDocument to load the project file and extract any PackageReference elements
     *          check if the Version attribute is present, if so, extract (Include, Version)
     *          else, try locate the directory.packages.props file. Starting from the directory of the project file, move up the directory tree, stop when we reach the first directory.packages.props. or, stop at the root.
     *              you should cache all known directory.packages.props files and annotate the ProjCfg with the path to the file (nullable).
     *              
     *      regarding the version updates. EVERN if we find more than one directory.packages.props file, we always use the same version for each package in all directory.packages.props files.
     *          but: a package only gets written to a directory.packages.props file if it was originally referenced from a project that is associated with that directory.packages.props file.
     */
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

        // Caches for Directory.Packages.props discovery and content
        var dirToPropsCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var propsContentCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        // Aggregate all package references across projects
        //var allPackageReferences = new Dictionary<string, List<PackageInfo>>(StringComparer.OrdinalIgnoreCase);
        var allPackageReferences = new Dictionary<string, PackageInfoContainer>(StringComparer.OrdinalIgnoreCase);
        var projectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        //// Local helper: find nearest Directory.Packages.props walking up from project directory
        //string? FindNearestProps(string projectPath) {
        //    var dir = Path.GetDirectoryName(projectPath);
        //    while (!string.IsNullOrEmpty(dir)) {
        //        if (dirToPropsCache.TryGetValue(dir!, out var cached)) return cached;
        //        var candidate = Path.Combine(dir!, "Directory.Packages.props");
        //        if (File.Exists(candidate)) {
        //            dirToPropsCache[dir!] = candidate;
        //            return candidate;
        //        }
        //        dirToPropsCache[dir!] = null; // remember miss
        //        dir = Path.GetDirectoryName(dir);
        //    }
        //    return null;
        //}

        //// Local helper: load props content as map id->version (cached)
        //async Task<Dictionary<string, string>> LoadPropsMapAsync(string propsPath) {
        //    if (propsContentCache.TryGetValue(propsPath, out var map)) return map;
        //    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        //    try {
        //        using var stream = File.OpenRead(propsPath);
        //        var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        //        foreach (var el in doc.Descendants("PackageVersion")) {
        //            var inc = el.Attribute("Include")?.Value;
        //            var ver = el.Attribute("Version")?.Value;
        //            if (!string.IsNullOrEmpty(inc) && !string.IsNullOrEmpty(ver)) dict[inc] = ver;
        //        }
        //    }
        //    catch (Exception ex) {
        //        _console.WriteWarning($"Failed to parse {propsPath}: {ex.Message}");
        //    }
        //    propsContentCache[propsPath] = dict;
        //    return dict;
        //}

        try {
            var packageRefs = new PackageInfoContainer(); // new List<PackageInfo>();
            var projParser = new ProjParser(_console, errorSink, _options);

            await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                await _console.StartStatusAsync($"Processing solution {slnPath}", async ctx => {
                    var currentProject = default(string);
                    await foreach (var projCfg in slnParser.ParseSolution(slnPath, fileSystem)) {
                        // Only process "Release" configuration as per spec
                        // todo 20250830 aggregate
                        if (!string.Equals(projCfg.Configuration, "Release", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!cache.Add(projCfg)) continue; // de-dupe project/configs

                        var refs = projParser.GetPackageReferences(projCfg);
                        _console.WriteDebug($"{projCfg.Proj.Path} {refs.TargetFramework} cpm? {refs.UseCpm} [{refs.CpmFile}]");
                        //foreach (var item in refs.PackageReferences) {
                        //    _console.WriteDebug($"\tREF {item.Key} {item.Value}");
                        //}
                        //if (refs.PackageVersions is not null)
                        //    foreach (var item in refs.PackageVersions) {
                        //        _console.WriteDebug($"\tVER {item.Key} {item.Value}");
                        //    }


                        var exnm = refs.PackageReferences.Select(re => new PackageInfo {
                            Id = re.Key,
                            FromProps = refs.UseCpm ?? false,
                            TargetFramework = refs.TargetFramework,
                            ProjectPath = refs.Proj.Path,
                            PropsPath = refs.CpmFile,
                            Version = re.Value ?? (refs.UseCpm == true && refs.PackageVersions is not null && refs.PackageVersions.TryGetValue(re.Key, out var v) ? v : null)
                        });

                        var bad = exnm.Where(e => string.IsNullOrEmpty(e.Version)).ToList();
                        if (bad.Any()) _logger.LogWarning($"Project {projCfg.Path} has package references with no resolvable version: {string.Join(", ", bad.Select(b => b.Id))}");
                        packageRefs.AddRange(exnm);

                        //packageRefs.Add(new PackageInfo {
                        //    Id = include,
                        //    Version = version!,
                        //    ProjectPath = projectPath,
                        //    TargetFramework = projectTfm,
                        //    PropsPath = propsPath,
                        //    FromProps = fromProps
                        //});

                        //if (currentProject is null || !string.Equals(currentProject, projCfg.Path, StringComparison.OrdinalIgnoreCase)) {
                        //    currentProject = projCfg.Path;
                        //    _console.WriteDebug($"Processing project: {projCfg.Path}");
                        //    ctx.Status($"Processing project: {projCfg.Path}");
                        //}

                        //var projectPath = projCfg.Path;
                        //projectFiles.Add(projectPath);

                        //// XDocument-based extraction
                        //try {
                        //    using var stream = File.OpenRead(projectPath);
                        //    var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

                        //    var targetFramework = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
                        //    var targetFrameworks = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value;
                        //    var projectTfm = targetFramework ?? targetFrameworks?.Split(';').FirstOrDefault()?.Trim();

                        //    foreach (var pr in doc.Descendants("PackageReference")) {
                        //        var include = pr.Attribute("Include")?.Value;
                        //        Console.WriteLine(include);
                        //        if (string.IsNullOrWhiteSpace(include)) continue;

                        //        string? version = pr.Attribute("Version")?.Value ?? pr.Element("Version")?.Value;
                        //        string? propsPath = null;
                        //        bool fromProps = false;

                        //        if (string.IsNullOrEmpty(version)) {
                        //            propsPath = FindNearestProps(projectPath);
                        //            Console.WriteLine(propsPath);
                        //            if (!string.IsNullOrEmpty(propsPath)) {
                        //                var map = await LoadPropsMapAsync(propsPath);
                        //                if (map.TryGetValue(include, out var v)) {
                        //                    version = v;
                        //                    fromProps = true;
                        //                }
                        //            }
                        //        }
                        //        else {
                        //            // still annotate the nearest props for later association, even if direct
                        //            propsPath = FindNearestProps(projectPath);
                        //        }

                        //        if (!string.IsNullOrEmpty(version)) {
                        //            Console.WriteLine($"{include} {version}");
                        //            packageRefs.Add(new PackageInfo {
                        //                Id = include,
                        //                Version = version!,
                        //                ProjectPath = projectPath,
                        //                TargetFramework = projectTfm,
                        //                PropsPath = propsPath,
                        //                FromProps = fromProps
                        //            });
                        //        }
                        //    }
                        //}
                        //catch (Exception ex) {
                        //    _console.WriteWarning($"Failed to parse {projectPath}: {ex.Message}");
                        //}

                        foreach (var pkg in packageRefs) {
                            if (!allPackageReferences.TryGetValue(pkg.Id, out var list)) {
                                list = new PackageInfoContainer(); // new List<PackageInfo>();
                                allPackageReferences[pkg.Id] = list;
                            }
                            list.Add(pkg);
                        }
                    }
                });
            }
        }
        catch (Exception ex) {
            _console.WriteException(ex);
        }

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {projectFiles.Count} projects");

        // Determine latest versions per package and prepare updates
        var packageSource = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var metadataResource = await packageSource.GetResourceAsync<PackageMetadataResource>(cancellationToken);

        var latestPerPackage = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var outdatedPerPackage = new Dictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>(StringComparer.OrdinalIgnoreCase);

#if NUGET_PROJECT
        var options = new NugetMetadataOptions { MaxParallelRequests = 12 /* configure */ };
        var client = NugetMetadataService.CreateHttpClient(options);
        //var logger = /* your logger */;
        //var request = new PackageVersionRequest { /* configure */ };

        await  Parallel.ForEachAsync(allPackageReferences, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (packageReference, ct) => {
            _console.WriteDebug($"Processing package {packageReference.Key}...{packageReference.Value.Tfm}");
           
            var request = new PackageVersionRequest {
                PackageId = packageReference.Key,
                AllowPrerelease = includePrerelease,
                CompatibleTargetFrameworks =  packageReference.Value.Tfms.ToList() //  [packageReference.Value.Tfm]
                //packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework).ToString()).Distinct().ToList()
                //TargetFrameworks = packageReference.Value.Select(pr => pr.TargetFramework).Where(t => !string.IsNullOrEmpty(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };


            //var result = await NugetMetadataService.GetLatestVersionWithFrameworkAsync(client, options, default, packageReference.Key, packageReference.Value.Tfm, includePrerelease, cancellationToken);
            _console.WriteInfo($"{request.ToString()}");
            var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, default, request);
            //var result = await NugetMetadataService.GetLatestVersionAsync(client, options, default, request);
            //_console.WriteVerbose($"Package {packageReference.Key}: {string.Join(", ", result?.TargetFrameworkVersions?.Select(kvp => $"{kvp.Key}=>{kvp.Value}"))} (from {string.Join(", ", request?.CompatibleTargetFrameworks)})");
            _console.WriteVerbose($"Package {packageReference.Key}: {result is { }} {result?.ToString()}");
            //_console.WriteVerbose($"Package {packageReference.Key}: {result?.PackageId}");
            //_console.WriteVerbose($"Package {packageReference.Key}: {result?.IsPrerelease}");
            //_console.WriteVerbose($"Package {packageReference.Key}: {(packageReference.Value.Tfm is { } ? result?.TargetFrameworkVersions?[packageReference.Value.Tfm] : "")}");

            var currentMin = packageReference.Value
                .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
                .Where(v => v is not null)!
                .Min()!;

            try {
                var targetVer = result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework).GetShortFolderName()).FirstOrDefault()];
                if (targetVer is null) {
                    _console.WriteWarning($"No compatible version found for {packageReference.Key} {packageReference.Value.Tfm} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key) ?? Array.Empty<string>())}");
                    return;
                }
                if (!NuGetVersion.TryParse(targetVer, out var latestVer)) {
                    _console.WriteWarning($"Failed to parse version for {packageReference.Key}: {targetVer}");
                    return;
                }
                if(currentMin >= latestVer) {
                    _console.WriteDebug($"Package {packageReference.Key} is up to date ({currentMin} >= {latestVer})");
                    return;
                }

                outdatedPerPackage[packageReference.Key] = (currentMin, NuGetVersion.Parse(result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework).GetShortFolderName()).FirstOrDefault()]));

            }
            catch (Exception xcptn) {
                _console.WriteWarning($"Failed to parse version for {packageReference.Key}: {packageReference.Value.Tfm} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key) ?? Array.Empty<string>())} {xcptn.Message}");
                //throw;
            }
        });

        
#else
        var nugetHttpService = new NuGetHttpService(NuGetHttpService.CreateClient(_console), _console);
        await using var svc = new NuGetHttpPkgService(nugetHttpService, _console);

        var pp = new ParallelOptions { MaxDegreeOfParallelism = 1 /*Environment.ProcessorCount*/, CancellationToken = cancellationToken };
        Parallel.ForEach(allPackageReferences, pp, async (kvp) => {
            var packageId = kvp.Key;
            var usages = kvp.Value;

            var tfm = usages?.Select(u => NuGetFramework.Parse(u.TargetFramework)).OrderBy(x => x).FirstOrDefault().ToString();
            //if (usages.DistinctBy(x => x.TargetFramework).Count() <= 1) {
            //var latest = await svc.GetLatestCompatible(packageId, usages.Select(u => u.TargetFramework).FirstOrDefault(), includePrerelease, cancellationToken);

            //}
            //else {
            //    var latest2 = await svc.GetLatestCompatible(packageId, usages.Select(u => u.TargetFramework), includePrerelease, cancellationToken);

            //}
            var latest = await svc.GetLatestCompatible(packageId, tfm, includePrerelease, cancellationToken);
            if (latest is null) return;

            latestPerPackage[packageId] = latest.Version;
            // Find the minimum current version used (for display)
            var currentMin = usages
                .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
                .Where(v => v is not null)!
                .Min()!;
            if (currentMin < latest.Version) {
                _console.WriteDebug($"Package {packageId} can be updated from {currentMin} to {latest.Version}");
                lock (outdatedPerPackage) {
                    outdatedPerPackage[packageId] = (currentMin, latest.Version);
                }
            }
        });
        //foreach (var (packageId, usages) in allPackageReferences) {
#endif

        //_console.WriteDebug($"Checking updates for {packageId} {usages}...");
        //try {
        //    var metadata = await metadataResource.GetMetadataAsync(packageId, true, true, _cache, _logger, cancellationToken);
        //    var versionFilter = includePrerelease ?
        //        metadata.OrderByDescending(m => m.Identity.Version) :
        //        metadata.Where(m => !m.Identity.Version.IsPrerelease).OrderByDescending(m => m.Identity.Version);

        //    // Choose latest version compatible with at least one TFM among usages (basic heuristic if skipTfmCheck is false)
        //    NuGetVersion? latest = null;
        //    foreach (var meta in versionFilter) {
        //        latest = meta.Identity.Version;
        //        _console.WriteDebug($"Considering {packageId} {latest}...");
        //        if (!skipTfmCheck) {
        //            // basic check against first parseable TFM among usages
        //            var tfm = usages.Select(u => u.TargetFramework).FirstOrDefault(t => !string.IsNullOrEmpty(t));
        //            if (tfm is string s) {
        //                try {
        //                    var nfw = NuGetFramework.Parse(s);
        //                    if (!await IsPackageCompatibleWithFrameworkAsync(meta, nfw, packageId, cancellationToken)) continue;
        //                }
        //                catch { /* ignore parse issues */ }
        //            }
        //        }
        //        break;
        //    }

        //    if (latest is null) continue;
        //    latestPerPackage[packageId] = latest;

        //    // Find the minimum current version used (for display)
        //    var currentMin = usages
        //        .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
        //        .Where(v => v is not null)!
        //        .Min()!;

        //    if (currentMin < latest) {
        //        _console.WriteDebug($"Package {packageId} can be updated from {currentMin} to {latest}");
        //        outdatedPerPackage[packageId] = (currentMin, latest);
        //    }
        //}
        //catch (Exception ex) {
        //    _console.WriteWarning($"Failed to query {packageId}: {ex.Message}");
        //}
        //}

        if (outdatedPerPackage.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return 0;
        }

        _console.WriteInfo($"\nFound {outdatedPerPackage.Count} packages with available updates:");
        foreach (var kvp in outdatedPerPackage.OrderBy(k => k.Key)) {
            _console.WriteWarning($"{kvp.Key}: {kvp.Value.CurrentMin} → {kvp.Value.Latest}");
        }

        // Prepare batch updates: props file -> (package -> version) and project -> (package -> version)
        var propsUpdates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var projectUpdates = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (packageId, versions) in outdatedPerPackage) {
            var latest = versions.Latest.ToString();
            foreach (var usage in allPackageReferences[packageId]) {
                // Only update entries that contributed their version (direct ref or props)
                if (usage.FromProps && !string.IsNullOrEmpty(usage.PropsPath)) {
                    var propsPath = usage.PropsPath!
;
                    if (!propsUpdates.TryGetValue(propsPath, out var map)) {
                        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        propsUpdates[propsPath] = map;
                    }
                    map[packageId] = latest;
                }
                else if (!usage.FromProps) {
                    if (!projectUpdates.TryGetValue(usage.ProjectPath, out var pmap)) {
                        pmap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        projectUpdates[usage.ProjectPath] = pmap;
                    }
                    pmap[packageId] = latest;
                }
            }
        }

        if (updatePackages) {
            _console.WriteInfo("\nUpdating packages to latest versions...");

            // Update all props files in one pass per file
            foreach (var (propsPath, updates) in propsUpdates) {
                await UpdatePropsFileAsync(propsPath, updates, cancellationToken);
                _console.WriteInfo($"Updated {updates.Count} package(s) in {propsPath}");
            }

            // Update project files
            foreach (var (projPath, updates) in projectUpdates) {
                foreach (var (pkg, v) in updates) {
                    await UpdatePackageVersionAsync(projPath, pkg, v, cancellationToken);
                    _console.WriteInfo($"Updated {pkg} to {v} in {Path.GetFileName(projPath)}");
                }
            }
        }
        else {
            _console.WriteInfo("\nUse --update to apply these changes.");
        }

        stopwatch.Stop();
        _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
        errorSink.WriteTo();

        return 0;
    }

    private async Task UpdatePropsFileAsync(string propsPath, IReadOnlyDictionary<string, string> updates, CancellationToken cancellationToken) {
        try {
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

    private async Task UpdatePackageVersionAsync(string projectPath, string packageId, string newVersion, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var packageRefElements = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value == packageId);

            foreach (var element in packageRefElements) {
                var versionAttr = element.Attribute("Version");
                var versionElement = element.Element("Version");

                if (versionAttr != null) {
                    versionAttr.Value = newVersion;
                }
                else if (versionElement != null) {
                    versionElement.Value = newVersion;
                }
            }

            using var writeStream = File.Create(projectPath);
            using var writer = XmlWriter.Create(writeStream, new XmlWriterSettings {
                Indent = true,
                OmitXmlDeclaration = true,
                Encoding = System.Text.Encoding.UTF8,
                Async = true
            });
            await doc.SaveAsync(writer, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.Message}");
        }
    }

    // Add IEnumerable<PackageInfo> implementation to allow foreach on PackageInfoContainer
    internal class PackageInfoContainer : IEnumerable<OutdatedService.PackageInfo> {
        private readonly List<PackageInfo> _items = new();
        internal void Add(PackageInfo item) {
            if (item.TargetFramework is { }) {
                var nuTfm = NuGetFramework.Parse(item.TargetFramework);
                _tfms.Add(nuTfm);

            }
            _items.Add(item);
        }

        internal void AddRange(IEnumerable<PackageInfo> exnm) {
            foreach (var item in exnm) Add(item);
        }

        public IEnumerable<string> Tfms => _tfms.Select(nuTfm => nuTfm.GetShortFolderName());
        public string? Tfm => _tfms.Count() == 1 ? _tfms.First().GetShortFolderName(): default;
        private readonly HashSet<NuGetFramework> _tfms = new();

        public IEnumerator<PackageInfo> GetEnumerator() => _items.GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _items.GetEnumerator();
    }

    internal class PackageInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string? TargetFramework { get; set; }
        public string? PropsPath { get; set; }
        public bool FromProps { get; set; }
    }

    private class VersionConflictInfo {
        public string PackageId { get; set; } = string.Empty;
        public Dictionary<string, List<string>> VersionUsages { get; set; } = new();
    }

    private class CpmInfo {
        public string DirectoryPackagesPath { get; set; } = string.Empty;
        public Dictionary<string, string> PackageVersions { get; set; } = new();
    }

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

    private Task<bool> IsPackageCompatibleWithFrameworkAsync(IPackageSearchMetadata packageMetadata, NuGetFramework targetFramework, string packageId, CancellationToken cancellationToken) {
        try {
            var packageVersion = packageMetadata.Identity.Version;

            if (packageId.StartsWith("Microsoft.AspNetCore") || packageId.StartsWith("Microsoft.Extensions")) {
                if (packageVersion.Major >= 9) {
                    var net9 = NuGetFramework.Parse("net9.0");
                    return Task.FromResult(IsFrameworkCompatible(targetFramework, net9));
                }
                if (packageVersion.Major >= 8) {
                    var net8 = NuGetFramework.Parse("net8.0");
                    return Task.FromResult(IsFrameworkCompatible(targetFramework, net8));
                }
                if (packageVersion.Major >= 7) {
                    var net7 = NuGetFramework.Parse("net7.0");
                    return Task.FromResult(IsFrameworkCompatible(targetFramework, net7));
                }
                if (packageVersion.Major >= 6) {
                    var net6 = NuGetFramework.Parse("net6.0");
                    return Task.FromResult(IsFrameworkCompatible(targetFramework, net6));
                }
            }

            if (targetFramework.Framework == ".NETCoreApp" && targetFramework.Version < new Version(5, 0)) {
                if (packageVersion.Major > 5) {
                    return Task.FromResult(false);
                }
            }

            return Task.FromResult(true);
        }
        catch (Exception ex) {
            _console.WriteVerbose($"Error checking compatibility for {packageId}: {ex.Message}");
            return Task.FromResult(true);
        }
    }

    private bool IsFrameworkCompatible(NuGetFramework currentFramework, NuGetFramework requiredFramework) {
        // Check if current framework is compatible with or higher than required framework
        if (currentFramework.Framework != requiredFramework.Framework) {
            return false;
        }

        // For .NET Core/.NET 5+ compatibility
        if (currentFramework.Framework == ".NETCoreApp") {
            return currentFramework.Version >= requiredFramework.Version;
        }

        // For .NET Framework compatibility 
        if (currentFramework.Framework == ".NETFramework") {
            return currentFramework.Version >= requiredFramework.Version;
        }

        // For .NET Standard compatibility (more complex, simplified here)
        if (currentFramework.Framework == ".NETStandard") {
            return currentFramework.Version >= requiredFramework.Version;
        }

        return true; // Default to compatible for unknown frameworks
    }
}