using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using NuGet.Frameworks;
using NuGet.Versioning;
using Spectre.Console;
using System.Collections.Concurrent;
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

        var allPackageReferences = new ConcurrentDictionary<string, PackageInfoContainer>(StringComparer.OrdinalIgnoreCase);

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = _options.Parallel ? _options.MaxDegreeOfParallelism : 1
        };

        try {
            var projParser = new ProjParser(_console, errorSink, _options);

            var allSlns = new ConcurrentBag<string>();
            await foreach (var sln in slnScanner.Enumerate(rootPath)) {
                allSlns.Add(sln);
            }

            var allProjCfgs = new ConcurrentBag<ProjCfg>();
            await Parallel.ForEachAsync(allSlns, parallelOptions, async (sln, ct) => {
                await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                    if (cache.Add(projCfg)) {
                        allProjCfgs.Add(projCfg);
                    }
                }
            });

            await _console.StartStatusAsync($"Analyzing {allProjCfgs.Count} project configurations...", async ctx => {
                var count = 0;
                var total = allProjCfgs.Count;

                await Parallel.ForEachAsync(allProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Path.GetFileName(projCfg.Path)}[/])");

                    // Only process "Release" configuration as per spec
                    if (!string.Equals(projCfg.Configuration, "Release", StringComparison.OrdinalIgnoreCase)) return;

                    var refs = projParser.GetPackageReferences(projCfg);

                    if (refs?.PackageReferences is null || !refs.PackageReferences.Any()) {
                        _console.WriteDebug($"No references in {projCfg.Path}");
                        return;
                    }

                    var exnm = refs.PackageReferences.Select(re => new PackageInfo {
                        Id = re.Key,
                        FromProps = refs.UseCpm ?? false,
                        TargetFramework = refs.TargetFramework,
                        TargetFrameworks = refs.TargetFrameworks,
                        ProjectPath = refs.Proj.Path,
                        PropsPath = refs.CpmFile,
                        Item = re.Value
                    });

                    var bad = exnm.Where(e => string.IsNullOrEmpty(e.Version)).ToList();
                    if (bad.Any()) _console.WriteWarning($"Project {projCfg.Path} has package references with no resolvable version: {string.Join(", ", bad.Select(b => b.Id))}");

                    foreach (var pkg in exnm) {
                        var list = allPackageReferences.GetOrAdd(pkg.Id, _ => new PackageInfoContainer());
                        list.Add(pkg);
                    }
                });
            });
        }
        catch (Exception ex) {
            _console.WriteException(ex);
        }

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {cache.Count} projects");

        var latestPerPackage = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var outdatedPerPackage = new ConcurrentDictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>(StringComparer.OrdinalIgnoreCase);

        var options = new NugetMetadataOptions { MaxParallelRequests = parallelOptions.MaxDegreeOfParallelism /* configure */ };
        using var client = NugetMetadataService.CreateHttpClient(options);

        await Parallel.ForEachAsync(allPackageReferences, parallelOptions, async (packageReference, ct) => {

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
                        targetVer = result?.TargetFrameworkVersions?[packageReference.Value.Select(u => NuGetFramework.Parse(u.TargetFramework)).First()];
                    }
                }



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

                outdatedPerPackage.AddOrUpdate(
                    packageReference.Key,
                    key => (currentMin, NuGetVersion.Parse(targetVer)),
                    (key, existing) => {
                        // Always keep the lowest currentMin and highest Latest
                        var newLatest = NuGetVersion.Parse(targetVer);
                        var minCurrent = existing.CurrentMin < currentMin ? existing.CurrentMin : currentMin;
                        var maxLatest = existing.Latest > newLatest ? existing.Latest : newLatest;
                        return (minCurrent, maxLatest);
                    }
                );
            }
            catch (Exception xcptn) {
                _console.WriteWarning($"Failed to parse version for {packageReference.Key}: {packageReference.Value.Tfm} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key.GetShortFolderName()) ?? Array.Empty<string>())} {xcptn.FormatMessage()}");
            }
        });

        if (outdatedPerPackage.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return 0;
        }

        int maxMajorLength = outdatedPerPackage.Values
            .SelectMany(v => new[] { 
                v.CurrentMin?.Major.ToString().Length ?? 0, 
                v.Latest?.Major.ToString().Length ?? 0 
            })
            .DefaultIfEmpty(0)
            .Max();

        {
            _console.WriteInfo($"\nFound {outdatedPerPackage.Count} packages with available updates:");
            if (_options.MarkdownOutput) {
                var rows = outdatedPerPackage
                    .OrderBy(k => k.Key)
                    .Select(kvp => (IReadOnlyList<string?>)new[] {
                        kvp.Key,
                        PlainVersion(kvp.Value.CurrentMin),
                        PlainVersion(kvp.Value.Latest)
                    });

                MarkdownTableFormatter.Write(_console, "Outdated packages (markdown)", new[] { "PackageId", "Current", "Latest" }, rows);
            }
            else {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn(new TableColumn("PackageId").LeftAligned());
                table.AddColumn(new TableColumn("current").LeftAligned());
                table.AddColumn(new TableColumn("latest").LeftAligned());

                foreach (var kvp in outdatedPerPackage.OrderBy(k => k.Key)) {
                    table.AddRow(
                        Markup.Escape(kvp.Key ?? ""),
                        FormatVersion(kvp.Value.CurrentMin, maxMajorLength),
                        GetFormattedVersion(kvp.Value.CurrentMin, kvp.Value.Latest, maxMajorLength)
                    );
                }
                _console.WriteTable(table);
            }
        }

        // Prepare batch updates: props file -> (package -> version) and project -> (package -> version)
        var propsUpdates = new Dictionary<string, Dictionary<string, (string target, string? current)>>(StringComparer.OrdinalIgnoreCase);
        var projectUpdates = new Dictionary<string, Dictionary<string, (string target,string? current,VersionReason reason)>>(StringComparer.OrdinalIgnoreCase);

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
                else {
                    if (!projectUpdates.TryGetValue(usage.ProjectPath, out var pmap)) {
                        pmap = new Dictionary<string, (string,string?,VersionReason)>(StringComparer.OrdinalIgnoreCase);
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

        {
           
            foreach (var kvp in propsUpdates.OrderBy(kvp => kvp.Key)) {
                if (!kvp.Value.Any()) continue;

                _console.WriteHeader($"{kvp.Key}", "Version upgrades to central package management file.");
                if (_options.MarkdownOutput) {
                    var rows = kvp.Value
                        .OrderBy(kvp2 => kvp2.Key)
                        .Select(item => (IReadOnlyList<string?>)new[] {
                            item.Key,
                            item.Value.current,
                            item.Value.target
                        });

                    MarkdownTableFormatter.Write(_console, "CPM updates (markdown)", new[] { "Package", "Current", "Target" }, rows);
                }
                else {
                    var table = new Table().Border(TableBorder.Rounded);
                    table.AddColumn(new TableColumn("Package").LeftAligned());
                    table.AddColumn(new TableColumn("current").LeftAligned());
                    table.AddColumn(new TableColumn("target").LeftAligned());

                    foreach (var item in kvp.Value.OrderBy(kvp2 => kvp2.Key)) {
                        table.AddRow(
                            Markup.Escape(item.Key ?? ""),
                            FormatVersion(item.Value.current, maxMajorLength),
                            GetFormattedVersion(item.Value.current, item.Value.target, maxMajorLength)
                        );
                    }

                    _console.WriteTable(table);
                }
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

                if (_options.MarkdownOutput) {
                    var rows = kvp.Value
                        .OrderBy(kvp2 => kvp2.Key)
                        .Select(item => (IReadOnlyList<string?>)new[] {
                            item.Key,
                            item.Value.current,
                            item.Value.target,
                            Reason(item.Value.reason)
                        });

                    MarkdownTableFormatter.Write(_console, "Project updates (markdown)", new[] { "Package", "Current", "Target", "Reason" }, rows);
                }
                else {
                    foreach (var item in kvp.Value.OrderBy(kvp2 => kvp2.Key)) {
                        table.AddRow(
                            Markup.Escape(item.Key ?? ""),
                            FormatVersion(item.Value.current, maxMajorLength),
                            GetFormattedVersion(item.Value.current, item.Value.target, maxMajorLength),
                            Markup.Escape(Reason(item.Value.reason))
                        );
                    }

                    _console.WriteTable(table);
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
            _console.WriteError($"Failed to update {propsPath}: {ex.FormatMessage()}");
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
            _console.WriteError($"Failed to update {projectPath}: {ex.FormatMessage()}");
        }
    }

    private static string FormatVersion(NuGetVersion? ver, int maxMajorLength) {
        if (ver == null) return "".PadLeft(maxMajorLength);
        var full = ver.ToFullString();
        var major = ver.Major.ToString();
        var paddedMajor = major.PadLeft(maxMajorLength);
        var rest = full.Substring(major.Length);
        var str = Markup.Escape(paddedMajor + rest);
        return ver.IsPrerelease ? $"[italic]{str}[/]" : str;
    }

    private static string FormatVersion(string? version, int maxMajorLength) {
        if (string.IsNullOrEmpty(version)) return "".PadLeft(maxMajorLength);
        if (NuGetVersion.TryParse(version, out var ver)) return FormatVersion(ver, maxMajorLength);
        return Markup.Escape(version.PadLeft(maxMajorLength));
    }

    private static string GetFormattedVersion(NuGetVersion? current, NuGetVersion? latest, int maxMajorLength) {
        if (latest == null) return "".PadLeft(maxMajorLength);
        var latestFull = latest.ToFullString();
        var majorStr = latest.Major.ToString();
        var paddedMajor = majorStr.PadLeft(maxMajorLength);
        var restOfLatest = latestFull.Substring(majorStr.Length);

        if (current == null) return FormatVersion(latest, maxMajorLength);

        string result;
        if (latest.Major > current.Major) {
            result = $"[red]{Markup.Escape(paddedMajor + restOfLatest)}[/]";
        }
        else if (latest.Minor > current.Minor) {
            result = $"{Markup.Escape(paddedMajor)}[yellow]{Markup.Escape(restOfLatest)}[/]";
        }
        else if (latest.Patch > current.Patch) {
            int firstDot = latestFull.IndexOf('.');
            int secondDot = firstDot != -1 ? latestFull.IndexOf('.', firstDot + 1) : -1;
            if (secondDot != -1) {
                string prefix = latestFull.Substring(majorStr.Length, secondDot - majorStr.Length + 1);
                string rest = latestFull.Substring(secondDot + 1);
                result = $"{Markup.Escape(paddedMajor)}{Markup.Escape(prefix)}[green]{Markup.Escape(rest)}[/]";
            }
            else {
                result = $"[green]{Markup.Escape(paddedMajor + restOfLatest)}[/]";
            }
        }
        else if (latest > current) {
            result = $"[blue]{Markup.Escape(paddedMajor + restOfLatest)}[/]";
        }
        else {
            result = Markup.Escape(paddedMajor + restOfLatest);
        }

        if (latest.IsPrerelease) {
            result = $"[italic]{result}[/]";
        }

        return result;
    }

    private static string GetFormattedVersion(string? current, string? latest, int maxMajorLength) {
        if (string.IsNullOrEmpty(latest)) return "".PadLeft(maxMajorLength);
        if (!NuGetVersion.TryParse(latest, out var latestVer)) return Markup.Escape(latest.PadLeft(maxMajorLength));
        if (string.IsNullOrEmpty(current) || !NuGetVersion.TryParse(current, out var currentVer)) return FormatVersion(latestVer, maxMajorLength);
        return GetFormattedVersion(currentVer, latestVer, maxMajorLength);
    }

    private static string PlainVersion(NuGetVersion? version) => version?.ToFullString() ?? string.Empty;

    internal class PackageInfoContainer : IEnumerable<OutdatedService.PackageInfo> {
        private readonly HashSet<PackageInfo> _items = new(new PackageInfoComparer());
        private readonly HashSet<NuGetFramework> _tfms = new();

        internal void Add(PackageInfo item) {
            lock (_items) {
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
                _items.Add(item);
            }
        }

        internal void AddRange(IEnumerable<PackageInfo> exnm) {
            foreach (var item in exnm) Add(item);
        }

        public IEnumerable<string> Tfms {
            get {
                lock (_items) {
                    return _tfms.Select(nuTfm => nuTfm.GetShortFolderName()).ToList();
                }
            }
        }

        public string? Tfm {
            get {
                lock (_items) {
                    return _tfms.Count == 1 ? _tfms.First().GetShortFolderName() : default;
                }
            }
        }

        public IEnumerator<PackageInfo> GetEnumerator() {
            List<PackageInfo> snapshot;
            lock (_items) {
                snapshot = _items.ToList();
            }
            return snapshot.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal sealed class PackageInfoComparer : IEqualityComparer<PackageInfo> {
        public bool Equals(PackageInfo? x, PackageInfo? y) {
            if (ReferenceEquals(x, y)) return true;
            if (x is null || y is null) return false;
            return string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Version, y.Version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.ProjectPath, y.ProjectPath, StringComparison.OrdinalIgnoreCase)
                && ((x.TargetFrameworks is null && y.TargetFrameworks is null) ||
                    (x.TargetFrameworks != null && y.TargetFrameworks != null &&
                     x.TargetFrameworks.SequenceEqual(y.TargetFrameworks, StringComparer.OrdinalIgnoreCase)))
                && string.Equals(x.PropsPath, y.PropsPath, StringComparison.OrdinalIgnoreCase)
                && x.FromProps == y.FromProps;
        }

        public int GetHashCode(PackageInfo obj) {
            if (obj is null) return 0;
            int hash = 17;
            hash = hash * 23 + (obj.Id?.ToLowerInvariant().GetHashCode() ?? 0);
            hash = hash * 23 + (obj.Version?.ToLowerInvariant().GetHashCode() ?? 0);
            hash = hash * 23 + (obj.ProjectPath?.ToLowerInvariant().GetHashCode() ?? 0);
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


}

internal enum VersionReason {
    PackageReferenceProj,
    VersionOverrideProj,

    PackageVersionCpm,
}