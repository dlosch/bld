using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using NuGet.Frameworks;
using NuGet.Versioning;
using Spectre.Console;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
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

        try {
            var projParser = new ProjParser(_console, errorSink, _options);

            await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                await _console.StartStatusAsync($"Processing solution {slnPath}", async ctx => {
                    await foreach (var projCfg in slnParser.ParseSolution(slnPath, fileSystem)) {
                        var packageRefs = new PackageInfoContainer(); // new List<PackageInfo>();
                        // Only process "Release" configuration as per spec
                        // todo 20250830 aggregate
                        if (!string.Equals(projCfg.Configuration, "Release", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!cache.Add(projCfg)) continue; // de-dupe project/configs

                        var refs = projParser.GetPackageReferences(projCfg);

                        if (refs?.PackageReferences is null || !refs.PackageReferences.Any()) {
                            _console.WriteDebug($"No references in {projCfg.Path}");
                            continue;
                        }

                        var exnm = refs.PackageReferences.Select(re => new PackageInfo {
                            Id = re.Key,
                            FromProps = refs.UseCpm ?? false,
                            TargetFramework = refs.TargetFramework,
                            TargetFrameworks = refs.TargetFrameworks,
                            ProjectPath = refs.Proj.Path,
                            PropsPath = refs.CpmFile,
                            Item = re.Value
                            //, Version = re.Value ?? (refs.UseCpm == true && refs.PackageVersions is not null && refs.PackageVersions.TryGetValue(re.Key, out var v) ? v : null)
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

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {cache.Count} projects");

        // Determine latest versions per package and prepare updates
        //var packageSource = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        //var metadataResource = await packageSource.GetResourceAsync<PackageMetadataResource>(cancellationToken);

        var latestPerPackage = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var outdatedPerPackage = new Dictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>(StringComparer.OrdinalIgnoreCase);

        var options = new NugetMetadataOptions { MaxParallelRequests = Environment.ProcessorCount /* configure */ };
        var client = NugetMetadataService.CreateHttpClient(options);

        await Parallel.ForEachAsync(allPackageReferences, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, async (packageReference, ct) => {

            if (packageReference.Value is null || !packageReference.Value.Any()) {
                _console.WriteWarning($"No references found for package {packageReference.Key}");
                return;
            }

            var request = new PackageVersionRequest {
                PackageId = packageReference.Key,
                AllowPrerelease = includePrerelease,
                CompatibleTargetFrameworks = packageReference.Value.Tfms.ToList() //  [packageReference.Value.Tfm]
            };

            var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, _console, request);
            if (result is null) {
                _console.WriteWarning($"Failed to retrieve NuGet metadata for {request.PackageId}.");
                return;
            }

            var currentMin = packageReference.Value
                .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
                .Where(v => v is not null)!
                .Min()!;

            try {
                var targetVer = default(string?);
                if (request.CompatibleTargetFrameworks is { } && request.CompatibleTargetFrameworks.Count > 1) {
                    foreach (var item in request.CompatibleTargetFrameworksTyped) {
                        var curVer = default(string?);
                        var exists = result?.TargetFrameworkVersions?.TryGetValue(item, out curVer) ?? false;
                        //if (!exists) Debugger.Break();

                        if (curVer is not null && targetVer is not null && 0 != string.Compare(curVer, targetVer, StringComparison.OrdinalIgnoreCase)) {
                            _console.WriteWarning($"Package {packageReference.Key} has multiple target framework versions: {targetVer} vs {curVer} for {string.Join(',', request.CompatibleTargetFrameworks)}");
                        }

                        targetVer ??= curVer;
                    }
                }
                else {
                    if (result.TargetFrameworkVersions.Values.Distinct().Count() == 1) {
                        targetVer = result.TargetFrameworkVersions.Values.First();
                    }

                    else {
                        targetVer = result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework)
                        //.GetShortFolderName())
                        ).First()];
                    }
                }

                //var targetVersions = packageReference.Value.SelectMany(u => u.TargetFrameworks).Select(NuGetFramework.Parse).Select(x => x.GetShortFolderName()).Distinct();
                //if (targetVersions.Count() > 1) {
                //    Debugger.Break();
                //}


                if (targetVer is null) {
                    _console.WriteInfo($"No compatible version found for {packageReference.Key} {packageReference.Value.Tfm} {result?.ToString()} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key.GetShortFolderName()) ?? Array.Empty<string>())}");
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

                outdatedPerPackage[packageReference.Key] = (currentMin
                , NuGetVersion.Parse(targetVer)
                //, NuGetVersion.Parse(result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework).GetShortFolderName()).FirstOrDefault()])
                );
            }
            catch (Exception xcptn) {
                _console.WriteWarning($"Failed to parse version for {packageReference.Key}: {packageReference.Value.Tfm} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key.GetShortFolderName()) ?? Array.Empty<string>())} {xcptn.Message}");
            }
        });

        if (outdatedPerPackage.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return 0;
        }

        {
            _console.WriteInfo($"\nFound {outdatedPerPackage.Count} packages with available updates:");
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn(new TableColumn("PackageId").LeftAligned());
            table.AddColumn(new TableColumn("current").LeftAligned());
            table.AddColumn(new TableColumn("latest").LeftAligned());

            foreach (var kvp in outdatedPerPackage.OrderBy(k => k.Key)) {
                table.AddRow(
                    Markup.Escape(kvp.Key ?? ""),
                    Markup.Escape(kvp.Value.CurrentMin?.ToFullString() ?? ""),
                    Markup.Escape(kvp.Value.Latest?.ToFullString() ?? "")
                );
                //_console.WriteWarning($"{kvp.Key}: {kvp.Value.CurrentMin} → {kvp.Value.Latest}");
            }
            _console.WriteTable(table);
        }

        // Prepare batch updates: props file -> (package -> version) and project -> (package -> version)
        var propsUpdates = new Dictionary<string, Dictionary<string, (string target, string? current)>>(StringComparer.OrdinalIgnoreCase);
        var projectUpdates = new Dictionary<string, Dictionary<string, (string target, string? current, VersionReason reason)>>(StringComparer.OrdinalIgnoreCase);

        static bool HasVersionUpdate(string latest, string current) {
            if (string.IsNullOrWhiteSpace(current)) return true;
            if (NuGetVersion.TryParse(latest, out var latestVer) && NuGetVersion.TryParse(current, out var currentVer)) {
                return latestVer > currentVer;
            }
            return !string.Equals(latest, current, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var (packageId, versions) in outdatedPerPackage) {
            if (versions.Latest is null) {
                _console.WriteWarning($"No latest version found for {packageId}");
                continue;
            }
            var latest = versions.Latest.ToString();
            foreach (var usage in allPackageReferences[packageId]) {
                // Only update entries that contributed their version (direct ref or props)
                var fromProps = !usage.CustomVersion && usage.FromProps && !string.IsNullOrEmpty(usage.PropsPath);
                if (fromProps) {
                    var propsPath = usage.PropsPath!
;
                    if (!propsUpdates.TryGetValue(propsPath, out var map)) {
                        map = new Dictionary<string, (string target, string? current)>(StringComparer.OrdinalIgnoreCase);
                        propsUpdates[propsPath] = map;
                    }
                    if (HasVersionUpdate(latest, usage.Item.EffectiveVersion)) map[packageId] = (latest, usage.Item.EffectiveVersion);
                }
                //else if (!usage.FromProps) {
                else {
                    if (!projectUpdates.TryGetValue(usage.ProjectPath, out var pmap)) {
                        pmap = new Dictionary<string, (string, string?, VersionReason)>(StringComparer.OrdinalIgnoreCase);
                        projectUpdates[usage.ProjectPath] = pmap;
                    }
                    static VersionReason Reason(Pkg item) {
                        if (item.VersionOverride is not null) return VersionReason.VersionOverrideProj;
                        if (item.Version is not null) return VersionReason.PackageReferenceProj;
                        return VersionReason.PackageVersionCpm;
                    }
                    if (HasVersionUpdate(latest, usage.Item.EffectiveVersion)) pmap[packageId] = (latest, usage.Item.EffectiveVersion, Reason(usage.Item));
                }
            }
        }

        //////////
        ///
        {

            foreach (var kvp in propsUpdates.OrderBy(kvp => kvp.Key)) {
                if (!kvp.Value.Any()) continue;

                _console.WriteHeader($"{kvp.Key}", "Version upgrades to central package management file.");
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn(new TableColumn("Package").LeftAligned());
                table.AddColumn(new TableColumn("current").LeftAligned());
                table.AddColumn(new TableColumn("target").LeftAligned());

                foreach (var item in kvp.Value.OrderBy(kvp2 => kvp2.Key)) {
                    table.AddRow(
                        Markup.Escape(item.Key ?? ""),
                        Markup.Escape(item.Value.current ?? ""),
                        Markup.Escape(item.Value.target ?? "")
                    );
                }

                _console.WriteTable(table);
            }
        }
        {
            foreach (var kvp in projectUpdates.OrderBy(kvp => kvp.Key)) {
                if (!kvp.Value.Any()) continue;

                _console.WriteHeader($"{kvp.Key}", "Version upgrades to project file.");
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn(new TableColumn("Package").LeftAligned());
                table.AddColumn(new TableColumn("current").LeftAligned());
                table.AddColumn(new TableColumn("target").LeftAligned());
                table.AddColumn(new TableColumn("reason").LeftAligned());

                static string Reason(VersionReason vr) => vr switch {
                    VersionReason.PackageReferenceProj => "Version in PackageReference in project file",
                    VersionReason.VersionOverrideProj => "VersionOverride in project file",
                    VersionReason.PackageVersionCpm => "Central package management.",
                    _ => ""
                };

                foreach (var item in kvp.Value.OrderBy(kvp2 => kvp2.Key)) {
                    table.AddRow(
                        Markup.Escape(item.Key ?? ""),
                        Markup.Escape(item.Value.current ?? ""),
                        Markup.Escape(item.Value.target ?? ""),
                        Markup.Escape(Reason(item.Value.reason))
                    );
                }

                _console.WriteTable(table);
            }
        }

        ///////////////////////////////////////////
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
            _console.WriteOutput("Use --apply to apply these changes.", default);
        }

        stopwatch.Stop();
        _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
        errorSink.WriteTo();

        return 0;
    }

    private async Task UpdatePropsFileAsync(string propsPath, IReadOnlyDictionary<string, (string target, string? current)> updates, CancellationToken cancellationToken) {
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
                    if (versionAttr != null) versionAttr.Value = newVersion.target;
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

    private async Task UpdatePackageVersionAsync(string projectPath, string packageId, (string target, string? currentVersion, VersionReason reason) newVersion, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var packageRefElements = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value == packageId);

            foreach (var element in packageRefElements) {
                if (VersionReason.VersionOverrideProj == newVersion.reason) {
                    var verOverrideElem = element.Element("VersionOverride");
                    if (verOverrideElem != null) {
                        verOverrideElem.Value = newVersion.target;
                        continue;
                    }
                    else {
                        // Fallback to Version element if VersionOverride not found
                        _console.WriteWarning($"Expected VersionOverride element for {packageId} in {projectPath} not found. Falling back to Version element.");
                    }
                }
                else {
                    var versionAttr = element.Attribute("Version");
                    var versionElement = element.Element("Version");

                    if (versionAttr != null) {
                        versionAttr.Value = newVersion.target;
                    }
                    else if (versionElement != null) {
                        versionElement.Value = newVersion.target;
                    }
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

}

internal class PackageInfoContainer : IEnumerable<PackageInfo> {
    private readonly HashSet<PackageInfo> _items = new(new PackageInfoComparer());
    internal void Add(PackageInfo item) {
        if (item.TargetFrameworks is { } && item.TargetFrameworks.Length > 0) {
            for (int odx = 0; odx < item.TargetFrameworks.Length; odx++) {
                var nuTfm = NuGetFramework.Parse(item.TargetFrameworks[odx]);
                _tfms.Add(nuTfm);
            }
        }
        else if (item.TargetFramework is { }) {
            var nuTfm = NuGetFramework.Parse(item.TargetFramework);
            _tfms.Add(nuTfm);

        }
        var added = _items.Add(item);
        if (!added) {
        }
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

internal sealed class PackageInfoComparer : IEqualityComparer<PackageInfo> {
    public bool Equals(PackageInfo? x, PackageInfo? y) {
        if (ReferenceEquals(x, y)) return true;
        if (x is null || y is null) return false;
        return string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Version, y.Version, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ProjectPath, y.ProjectPath, StringComparison.OrdinalIgnoreCase)
            &&
                //string.Equals(x.TargetFramework, y.TargetFramework, StringComparison.OrdinalIgnoreCase)
                //&& ((x.TargetFrameworks == null && y.TargetFrameworks == null) ||
                (x.TargetFrameworks != null && y.TargetFrameworks != null &&
                 x.TargetFrameworks.SequenceEqual(y.TargetFrameworks, StringComparer.OrdinalIgnoreCase))
            //)
            && string.Equals(x.PropsPath, y.PropsPath, StringComparison.OrdinalIgnoreCase)
            && x.FromProps == y.FromProps;
    }

    public int GetHashCode(PackageInfo obj) {
        if (obj is null) return 0;
        int hash = 17;
        hash = hash * 23 + (obj.Id?.ToLowerInvariant().GetHashCode() ?? 0);
        hash = hash * 23 + (obj.Version?.ToLowerInvariant().GetHashCode() ?? 0);
        hash = hash * 23 + (obj.ProjectPath?.ToLowerInvariant().GetHashCode() ?? 0);
        //hash = hash * 23 + (obj.TargetFramework?.ToLowerInvariant().GetHashCode() ?? 0);
        if (obj.TargetFrameworks != null) {
            foreach (var tfm in obj.TargetFrameworks) {
                hash = hash * 23 + (tfm?.ToLowerInvariant().GetHashCode() ?? 0);
            }
        }
        hash = hash * 23 + (obj.PropsPath?.ToLowerInvariant().GetHashCode() ?? 0);
        hash = hash * 23 + obj.FromProps.GetHashCode();
        return hash;
    }
}

internal record class PackageInfo {
    public string Id { get; set; } = string.Empty;
    public Pkg Item { get; set; } = default!;

    public string Version => Item.EffectiveVersion; // { get; set; } = string.Empty;

    public string ProjectPath { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = default!;
    public string[] TargetFrameworks { get; set; } = default!;
    public string? PropsPath { get; set; }
    public bool FromProps { get; set; }

    public bool CustomVersion => !string.IsNullOrWhiteSpace(Item.Version) || !string.IsNullOrWhiteSpace(Item.VersionOverride);
}


internal enum VersionReason {
    PackageReferenceProj,
    VersionOverrideProj,

    PackageVersionCpm,
}