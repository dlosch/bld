using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class OutdatedService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public OutdatedService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
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

        var dirToPropsCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var propsContentCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var allPackageReferences = new Dictionary<string, PackageInfoContainer>(StringComparer.OrdinalIgnoreCase);
        var projectFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                        var exnm = refs.PackageReferences.Select(re => new PackageInfo {
                            Id = re.Key,
                            FromProps = refs.UseCpm ?? false,
                            TargetFramework = refs.TargetFramework,
                            ProjectPath = refs.Proj.Path,
                            PropsPath = refs.CpmFile,
                            Version = re.Value ?? (refs.UseCpm == true && refs.PackageVersions is not null && refs.PackageVersions.TryGetValue(re.Key, out var v) ? v : null)
                        });

                        var bad = exnm.Where(e => string.IsNullOrEmpty(e.Version)).ToList();
                        if (bad.Any()) _console.WriteWarning($"Project {projCfg.Path} has package references with no resolvable version: {string.Join(", ", bad.Select(b => b.Id))}");
                        packageRefs.AddRange(exnm);

                        foreach (var pkg in packageRefs) {
                            if (!allPackageReferences.TryGetValue(pkg.Id, out var list)) {
                                list = new PackageInfoContainer();
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

        var options = new NugetMetadataOptions { MaxParallelRequests = 12 /* configure */ };
        var client = NugetMetadataService.CreateHttpClient(options);

        await Parallel.ForEachAsync(allPackageReferences, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (packageReference, ct) => {

            var request = new PackageVersionRequest {
                PackageId = packageReference.Key,
                AllowPrerelease = includePrerelease,
                CompatibleTargetFrameworks = packageReference.Value.Tfms.ToList() //  [packageReference.Value.Tfm]
            };

            var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, _console, request);

            var currentMin = packageReference.Value
                .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
                .Where(v => v is not null)!
                .Min()!;

            try {
                var targetVer = result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework).GetShortFolderName()).FirstOrDefault()];
                if (targetVer is null) {
                    _console.WriteInfo($"No compatible version found for {packageReference.Key} {packageReference.Value.Tfm} {result?.ToString()} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key) ?? Array.Empty<string>())}");
                    return;
                }
                if (!NuGetVersion.TryParse(targetVer, out var latestVer)) {
                    _console.WriteInfo($"Failed to parse version for {packageReference.Key}: {targetVer}");
                    return;
                }
                if (currentMin >= latestVer) {
                    _console.WriteDebug($"Package {packageReference.Key} is up to date ({currentMin} >= {latestVer})");
                    return;
                }

                outdatedPerPackage[packageReference.Key] = (currentMin, NuGetVersion.Parse(result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework).GetShortFolderName()).FirstOrDefault()]));
            }
            catch (Exception xcptn) {
                _console.WriteWarning($"Failed to parse version for {packageReference.Key}: {packageReference.Value.Tfm} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key) ?? Array.Empty<string>())} {xcptn.Message}");
            }
        });

        if (outdatedPerPackage.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return 0;
        }

        _console.WriteInfo($"\nFound {outdatedPerPackage.Count} packages with available updates:");
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("PackageId").LeftAligned());
        table.AddColumn(new TableColumn("current").LeftAligned());
        table.AddColumn(new TableColumn("latest").LeftAligned());

        foreach (var kvp in outdatedPerPackage.OrderBy(k => k.Key)) {
            table.AddRow(
                Markup.Escape(kvp.Key),
                Markup.Escape(kvp.Value.CurrentMin.ToFullString()),
                Markup.Escape(kvp.Value.Latest.ToFullString())
            );
            //_console.WriteWarning($"{kvp.Key}: {kvp.Value.CurrentMin} → {kvp.Value.Latest}");
        }
        _console.WriteTable(table);

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
        public string? Tfm => _tfms.Count() == 1 ? _tfms.First().GetShortFolderName() : default;
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


}