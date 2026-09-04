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
    // Assigned for the duration of a run so the write helpers can record failures that must affect
    // the exit code rather than only being printed.
    private ErrorSink? _errorSink;

    public OutdatedService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    internal static IReadOnlyList<string> SelectCompatibleTargetFrameworks(bool skipTfmCheck, PackageInfoContainer packageReferences) =>
        skipTfmCheck ? Array.Empty<string>() : packageReferences.Tfms.ToList();

    /// <summary>
    /// Package ids named by a project file's raw XML, regardless of any Condition. MSBuild evaluates
    /// one TFM and configuration at a time, so a reference inside
    /// &lt;ItemGroup Condition="'$(TargetFramework)'=='net472'"&gt; is invisible to the evaluated view -
    /// and the matching central PackageVersion then looks like an unused orphan.
    /// </summary>
    internal static IEnumerable<string> ReadDeclaredPackageIds(string projectPath) {
        XDocument doc;
        try {
            doc = XDocument.Load(projectPath);
        }
        catch {
            // Unreadable here is reported by the evaluation path; nothing to add.
            yield break;
        }

        foreach (var element in doc.ElementsNamed("PackageReference")) {
            // Include may name several packages ("A;B"); Update is the CPM-era spelling.
            var raw = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (var id in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                yield return id;
            }
        }
    }

    internal static bool IsSolutionFile(string path) {
        var ext = Path.GetExtension(path);
        return ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase);
    }

    internal enum ConflictChoice { IncludeDep, SkipPicker, AcceptRisk }

    // Pure helper: given the user's initial acceptance set, iterates until stable resolving
    // conflicts where an accepted package needs a higher version of a skipped package than what's
    // currently pinned. Returns the final accepted set (a mutated copy of the input).
    internal static HashSet<string> ResolveInteractivePicks(
        IEnumerable<string> initialAccepted,
        IReadOnlyDictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)> outdated,
        IReadOnlyDictionary<string, PackageVersionResult> metadata,
        Func<string, NuGetVersion, string, string, NuGetVersion, ConflictChoice> askConflict) {

        var accepted = new HashSet<string>(initialAccepted, StringComparer.OrdinalIgnoreCase);

        bool changed;
        do {
            changed = false;
            foreach (var pickerId in accepted.ToList()) {
                if (!accepted.Contains(pickerId)) continue; // removed mid-loop
                if (!outdated.ContainsKey(pickerId)) continue; // caller passed an id we don't track
                if (!metadata.TryGetValue(pickerId, out var meta) || meta?.Dependencies is null) continue;

                // Union dependencies across all TFM groups, keeping the strictest range per id.
                // Guard against nulls: NuGet catalog JSON can contain "dependencies": null on a
                // group, which System.Text.Json deserializes to a null property even with a `= []` default.
                var depRanges = new Dictionary<string, (string Raw, VersionRange Range)>(StringComparer.OrdinalIgnoreCase);
                foreach (var dg in meta.Dependencies.Values) {
                    if (dg?.Dependencies is null) continue;
                    foreach (var dep in dg.Dependencies) {
                        if (dep is null || string.IsNullOrEmpty(dep.PackageId) || string.IsNullOrEmpty(dep.Range)) continue;
                        if (!VersionRange.TryParse(dep.Range, out var range)) continue;
                        if (depRanges.TryGetValue(dep.PackageId, out var existing)) {
                            // Keep the strictest lower bound. Requiring *both* ranges to have a
                            // MinVersion meant an open-ended range seen first, e.g. "(, )" on one TFM
                            // group, discarded a real "[3.0.0, )" from another - so a genuine conflict
                            // was never reported to the user.
                            var newMin = range.MinVersion;
                            var oldMin = existing.Range.MinVersion;
                            if (newMin is { } && (oldMin is null || newMin > oldMin)) {
                                depRanges[dep.PackageId] = (dep.Range, range);
                            }
                        }
                        else {
                            depRanges[dep.PackageId] = (dep.Range, range);
                        }
                    }
                }

                foreach (var (depId, dep) in depRanges) {
                    if (!outdated.TryGetValue(depId, out var depVersions)) continue; // unknown to us
                    if (accepted.Contains(depId)) continue; // will be updated
                    if (dep.Range.Satisfies(depVersions.CurrentMin)) continue; // skip is safe

                    var choice = askConflict(pickerId, outdated[pickerId].Latest, depId, dep.Raw, depVersions.CurrentMin);
                    switch (choice) {
                        case ConflictChoice.IncludeDep:
                            accepted.Add(depId);
                            changed = true;
                            break;
                        case ConflictChoice.SkipPicker:
                            accepted.Remove(pickerId);
                            changed = true;
                            break;
                        case ConflictChoice.AcceptRisk:
                            break;
                    }
                    if (!accepted.Contains(pickerId)) break; // picker dropped — stop checking its other deps
                }
            }
        } while (changed);

        return accepted;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> CheckOutdatedPackagesAsync(string rootPath, bool updatePackages, bool skipTfmCheck, bool includePrerelease, bool listOrphans, bool commentOrphans, bool interactive, CancellationToken cancellationToken) {
        MSBuildService.RegisterMSBuildDefaults(_console, _options);

        _console.WriteRule("[bold blue]bld outdated (BETA)[/]");
        _console.WriteInfo("Checking for outdated packages...");

        var errorSink = new ErrorSink(_console);
        _errorSink = errorSink;
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var fileSystem = new FileSystem(_console, errorSink);
        var cache = new ProjCfgCache(_console);

        var stopwatch = Stopwatch.StartNew();

        var dirToPropsCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var propsContentCache = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        var allPackageReferences = new ConcurrentDictionary<string, PackageInfoContainer>(StringComparer.OrdinalIgnoreCase);

        // CPM file path -> (PackageId -> Version). Used to detect orphan entries (declared in
        // Directory.Packages.props but with no PackageReference anywhere in scope).
        var cpmFileEntries = new ConcurrentDictionary<string, ConcurrentDictionary<string, string?>>(StringComparer.OrdinalIgnoreCase);
        // Union of TFMs across all in-scope projects, used to constrain orphan version lookups.
        var allTfms = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        // True when at least one solution file (.sln/.slnx/.slnf) was discovered. Required to allow
        // --comment-orphans, because individual project inputs cannot see all consumers of the CPM
        // file and commenting an entry could break unseen projects.
        var isSolutionMode = false;
        // Package ids declared in project XML, including inside conditional ItemGroups that MSBuild
        // evaluation does not surface. Used to keep orphan detection from flagging live entries.
        var declaredPackageIds = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var evaluationFailures = 0;
        var metadataFailures = 0;

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism
        };

        try {
            var projParser = new ProjParser(_console, errorSink, _options);

            var allSlns = new ConcurrentBag<string>();
            await foreach (var sln in slnScanner.Enumerate(rootPath)) {
                allSlns.Add(sln);
                if (IsSolutionFile(sln)) isSolutionMode = true;
            }

            var allProjCfgs = new ConcurrentBag<ProjCfg>();
            await Parallel.ForEachAsync(allSlns, parallelOptions, async (sln, ct) => {
                await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                    if (cache.Add(projCfg)) {
                        allProjCfgs.Add(projCfg);
                    }
                }
            });

            // Walk ProjectReferences transitively so a single csproj input (or a slnx that omits a
            // referenced project) still picks up packages from projects it depends on. Children
            // inherit Configuration/Platform from the parent so config/platform-conditional
            // <ProjectReference> items evaluate the same way `dotnet build` would resolve them.
            var visitedProjectPaths = new HashSet<string>(allProjCfgs.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
            var refQueue = new Queue<ProjCfg>(allProjCfgs);
            while (refQueue.Count > 0) {
                var parent = refQueue.Dequeue();
                foreach (var refPath in projParser.GetProjectReferences(parent.Path, parent.Configuration, parent.Platform)) {
                    if (visitedProjectPaths.Add(refPath)) {
                        var newCfg = new ProjCfg(new Proj(refPath, null), parent.Configuration, parent.Platform);
                        refQueue.Enqueue(newCfg);
                        if (cache.Add(newCfg)) {
                            allProjCfgs.Add(newCfg);
                            _console.WriteDebug($"Discovered ProjectReference target: {refPath} [{parent.Configuration}|{parent.Platform}]");
                        }
                    }
                }
            }

            await _console.StartStatusAsync($"Analyzing {allProjCfgs.Count} project configurations...", async ctx => {
                var count = 0;
                var total = allProjCfgs.Count;

                await Parallel.ForEachAsync(allProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");

                    // Only process "Release" configuration as per spec
                    if (!string.Equals(projCfg.Configuration, "Release", StringComparison.OrdinalIgnoreCase)) return;

                    // Any throw here escaped Parallel.ForEachAsync, cancelling every project not yet
                    // scanned - and the run then carried on to report and even --apply against that
                    // partial view. Contain it per project and record the failure instead.
                    try {
                    // Read declared package ids straight from the project XML as well. MSBuild evaluates
                    // one TFM/configuration at a time, so items inside a conditional ItemGroup are absent
                    // from the evaluated view; without this they look like unreferenced CPM orphans.
                    foreach (var declared in ReadDeclaredPackageIds(projCfg.Path)) declaredPackageIds.TryAdd(declared, 0);

                    var refs = projParser.GetPackageReferences(projCfg);
                    if (refs is null) Interlocked.Increment(ref evaluationFailures);

                    if (refs is not null) {
                        if (refs.TargetFrameworks is { Length: > 0 }) {
                            foreach (var tfm in refs.TargetFrameworks) allTfms.TryAdd(tfm, 0);
                        }
                        if ((refs.UseCpm ?? false) && refs.PackageVersions is { Count: > 0 }) {
                            // Attribute each PackageVersion to the actual file where it was declared,
                            // so split CPM setups (Directory.Packages.props + imported props) report and
                            // edit the correct file on --apply / --comment-orphans.
                            var unattributed = 0;
                            foreach (var (id, entry) in refs.PackageVersions) {
                                var sourceFile = entry.SourceFile ?? refs.CpmFile;
                                if (string.IsNullOrEmpty(sourceFile)) {
                                    unattributed++;
                                    continue;
                                }
                                var dict = cpmFileEntries.GetOrAdd(sourceFile, _ => new ConcurrentDictionary<string, string?>(StringComparer.OrdinalIgnoreCase));
                                dict[id] = entry.Version;
                            }
                            if (unattributed > 0) {
                                _console.WriteWarning($"{projCfg.Path}: {unattributed} PackageVersion entries could not be attributed to a source file and will be skipped for orphan detection.");
                            }
                        }
                    }

                    if (refs?.PackageReferences is null || !refs.PackageReferences.Any()) {
                        _console.WriteDebug($"No references in {projCfg.Path}");
                        return;
                    }

                    var exnm = refs.PackageReferences.Select(re => {
                        // Point each PackageReference at the actual file that declares its PackageVersion,
                        // not just the project's primary CPM file. Falls back to CpmFile when no entry is
                        // tracked (e.g., VersionOverride-only refs).
                        string? propsPath = refs.CpmFile;
                        if (refs.PackageVersions is not null && refs.PackageVersions.TryGetValue(re.Key, out var entry)) {
                            propsPath = entry.SourceFile ?? refs.CpmFile;
                        }
                        return new PackageInfo {
                            Id = re.Key,
                            FromProps = refs.UseCpm ?? false,
                            TargetFramework = refs.TargetFramework,
                            TargetFrameworks = refs.TargetFrameworks,
                            ProjectPath = refs.Proj.Path,
                            PropsPath = propsPath,
                            Item = re.Value
                        };
                    });

                    var bad = exnm.Where(e => string.IsNullOrEmpty(e.Version)).ToList();
                    if (bad.Any()) _console.WriteWarning($"Project {projCfg.Path} has package references with no resolvable version: {string.Join(", ", bad.Select(b => b.Id))}");

                    foreach (var pkg in exnm) {
                        var list = allPackageReferences.GetOrAdd(pkg.Id, _ => new PackageInfoContainer());
                        list.Add(pkg);
                    }
                    }
                    catch (Exception ex) {
                        Interlocked.Increment(ref evaluationFailures);
                        errorSink.AddError("Failed to analyze project.", exception: ex, config: projCfg);
                        _console.WriteError($"Failed to analyze {projCfg.Path}: {ex.FormatMessage()}", ex);
                    }
                });
            });
        }
        catch (Exception ex) {
            Interlocked.Increment(ref evaluationFailures);
            _console.WriteException(ex);
        }

        if (allPackageReferences.Count == 0) {
            _console.WriteLine("No package references found.");
            return 0;
        }

        _console.WriteLine($"Found {allPackageReferences.Count} unique packages across {cache.Count} projects");

        var latestPerPackage = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        var outdatedPerPackage = new ConcurrentDictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>(StringComparer.OrdinalIgnoreCase);
        // NuGet metadata (with dependency manifest) cached per outdated package so the interactive
        // mode can detect transitive conflicts without re-querying NuGet.
        var packageMetadata = new ConcurrentDictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase);

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
                CompatibleTargetFrameworks = SelectCompatibleTargetFrameworks(skipTfmCheck, packageReference.Value)
            };

            var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, _console, request, ct);
            if (result is null) {
                // Count it: a network or feed outage made every lookup return null and the command
                // still exited 0, so CI read "no updates" as success.
                Interlocked.Increment(ref metadataFailures);
                _console.WriteWarning($"Failed to retrieve NuGet metadata for {request.PackageId}.");
                return;
            }

            try {
                var parsedVersions = packageReference.Value
                    .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
                    .Where(v => v is not null)
                    .ToList();
                if (parsedVersions.Count == 0) {
                    _console.WriteWarning($"No parseable versions found for {packageReference.Key}; skipping.");
                    return;
                }
                var currentMin = parsedVersions.Min()!;
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
                packageMetadata[packageReference.Key] = result!;
            }
            catch (Exception xcptn) {
                _console.WriteWarning($"Failed to parse version for {packageReference.Key}: {packageReference.Value.Tfm} {string.Join(',', result?.TargetFrameworkVersions?.Select(x => x.Key.GetShortFolderName()) ?? Array.Empty<string>())} {xcptn.FormatMessage()}");
            }
        });

        // Orphan CPM entries: PackageVersion items declared in a Directory.Packages.props but with
        // no matching PackageReference anywhere in scope. Detection is opt-in via --orphaned (list
        // only) or --comment-orphans (comment them out on --apply, sln/slnx only — see the apply
        // step below). The map is keyed by cpm file -> packageId -> (current, latest).
        var orphansToComment = new ConcurrentDictionary<string, ConcurrentDictionary<string, (string current, string latest)>>(StringComparer.OrdinalIgnoreCase);
        if (commentOrphans && !isSolutionMode) {
            _console.WriteWarning("--comment-orphans requested but input is not a solution (.sln/.slnx/.slnf). Orphans will only be listed, not commented out.");
        }
        var detectOrphans = listOrphans || commentOrphans;
        if (detectOrphans) {
            var orphanCandidates = new List<(string CpmFile, string PackageId, string? CurrentVersion)>();
            foreach (var (cpmFile, entries) in cpmFileEntries) {
                foreach (var (id, version) in entries) {
                    if (!allPackageReferences.ContainsKey(id) && !declaredPackageIds.ContainsKey(id)) {
                        orphanCandidates.Add((cpmFile, id, version));
                    }
                }
            }

            if (orphanCandidates.Count > 0) {
                var tfmList = skipTfmCheck ? Array.Empty<string>() : allTfms.Keys.ToArray();
                await Parallel.ForEachAsync(orphanCandidates, parallelOptions, async (orphan, ct) => {
                    var request = new PackageVersionRequest {
                        PackageId = orphan.PackageId,
                        AllowPrerelease = includePrerelease,
                        CompatibleTargetFrameworks = tfmList
                    };
                    var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, _console, request, ct);
                    if (result?.TargetFrameworkVersions is null || result.TargetFrameworkVersions.Count == 0) {
                        _console.WriteDebug($"No NuGet metadata for orphan {orphan.PackageId} in {orphan.CpmFile}");
                        return;
                    }
                    var latestStr = result.TargetFrameworkVersions.Values.First();
                    if (!NuGetVersion.TryParse(latestStr, out var latestVer)) return;
                    if (string.IsNullOrEmpty(orphan.CurrentVersion) || !NuGetVersion.TryParse(orphan.CurrentVersion, out var currentVer)) {
                        return;
                    }
                    if (latestVer > currentVer) {
                        var dict = orphansToComment.GetOrAdd(orphan.CpmFile, _ => new ConcurrentDictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase));
                        dict[orphan.PackageId] = (currentVer.ToString(), latestVer.ToString());
                    }
                });
            }
        }

        var willCommentOrphans = commentOrphans && isSolutionMode;
        if (willCommentOrphans && evaluationFailures > 0) {
            // A project we could not read contributes no package references, so everything only it
            // used looks orphaned. Commenting those out breaks the build we failed to inspect.
            _console.WriteWarning($"{evaluationFailures} project(s) could not be analyzed; not commenting out orphans. Fix those projects or re-run without --comment-orphans.");
            willCommentOrphans = false;
        }
        if (willCommentOrphans && orphansToComment.Count > 0) {
            _console.WriteWarning("Orphan detection only sees the projects in this input. A Directory.Packages.props shared with another solution may list entries that are used elsewhere.");
        }

        if (interactive && outdatedPerPackage.Count > 0) {
            _console.WriteRule("[bold yellow]Interactive update selection[/]");

            var sortedIds = outdatedPerPackage.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            var maxIdWidth = sortedIds.Max(id => id.Length);
            var prompt = new MultiSelectionPrompt<string>()
                .Title($"Select packages to update ({sortedIds.Length} outdated, all pre-selected):")
                .PageSize(Math.Min(20, Math.Max(5, sortedIds.Length)))
                .MoreChoicesText("[grey](move up/down to see more)[/]")
                .InstructionsText("[grey](press [blue]<space>[/] to toggle, [green]<enter>[/] to confirm)[/]")
                .UseConverter(id => {
                    var v = outdatedPerPackage[id];
                    return $"{id.PadRight(maxIdWidth)}  {v.CurrentMin} -> {v.Latest}";
                });

            foreach (var id in sortedIds) {
                prompt.AddChoice(id);
                prompt.Select(id);
            }

            var initial = _console.MultiPrompt(prompt);

            var picks = ResolveInteractivePicks(
                initial,
                outdatedPerPackage,
                packageMetadata,
                (pickerId, pickerLatest, depId, depRange, depCurrent) => {
                    _console.WriteWarning(
                        $"{pickerId} {pickerLatest} requires {depId} {depRange}, but {depId} was skipped at {depCurrent}.");
                    if (_console.Confirm($"  Include {depId} update too?", defaultValue: true)) return ConflictChoice.IncludeDep;
                    if (_console.Confirm($"  Skip {pickerId} as well?", defaultValue: false)) return ConflictChoice.SkipPicker;
                    return ConflictChoice.AcceptRisk;
                });

            var dropped = 0;
            foreach (var id in outdatedPerPackage.Keys.ToList()) {
                if (!picks.Contains(id)) {
                    outdatedPerPackage.TryRemove(id, out _);
                    dropped++;
                }
            }
            _console.WriteInfo($"Interactive selection: {picks.Count} package(s) selected, {dropped} skipped.");
        }

        if (outdatedPerPackage.Count == 0 && orphansToComment.IsEmpty) {
            // Only claim everything is up to date when we actually managed to look.
            if (metadataFailures > 0 || evaluationFailures > 0) {
                _console.WriteWarning($"Incomplete run: {evaluationFailures} project(s) failed to analyze and {metadataFailures} package lookup(s) failed. Results are not conclusive.");
            }
            else {
                _console.WriteLine("All packages are up to date!");
            }
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return ExitCode(errorSink, evaluationFailures, metadataFailures);
        }

        int maxMajorLength = outdatedPerPackage.Values
            .SelectMany(v => new[] {
                v.CurrentMin?.Major.ToString().Length ?? 0,
                v.Latest?.Major.ToString().Length ?? 0
            })
            .Concat(orphansToComment.Values.SelectMany(d => d.Values).SelectMany(v => new[] {
                NuGetVersion.TryParse(v.current, out var c) ? c.Major.ToString().Length : 0,
                NuGetVersion.TryParse(v.latest, out var l) ? l.Major.ToString().Length : 0
            }))
            .DefaultIfEmpty(0)
            .Max();

        if (outdatedPerPackage.Count > 0) {
            _console.WriteLine($"\nFound {outdatedPerPackage.Count} packages with available updates:");
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

        if (!orphansToComment.IsEmpty) {
            var totalOrphans = orphansToComment.Values.Sum(d => d.Count);
            var actionHint = willCommentOrphans
                ? "These will be commented out on --apply to prevent stale pins from breaking restore."
                : commentOrphans
                    ? "Listing only — --comment-orphans was set but requires a solution input to comment out."
                    : "Listing only — pass --comment-orphans (with a solution input) to comment them out on --apply.";
            _console.WriteLine($"\nFound {totalOrphans} orphan PackageVersion entry(ies) in Directory.Packages.props with no matching PackageReference and a newer version on NuGet. {actionHint}");
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

            foreach (var kvp in orphansToComment.OrderBy(kvp => kvp.Key)) {
                if (kvp.Value.IsEmpty) continue;

                var headerDescription = willCommentOrphans
                    ? "Orphan PackageVersion entries (no PackageReference uses them). Commented out on --apply."
                    : "Orphan PackageVersion entries (no PackageReference uses them). Report only.";
                _console.WriteHeader($"{kvp.Key}", headerDescription);
                if (_options.MarkdownOutput) {
                    var rows = kvp.Value
                        .OrderBy(kvp2 => kvp2.Key)
                        .Select(item => (IReadOnlyList<string?>)new[] {
                            item.Key,
                            item.Value.current,
                            item.Value.latest
                        });

                    MarkdownTableFormatter.Write(_console, "CPM orphan entries (markdown)", new[] { "Package", "Current", "Latest" }, rows);
                }
                else {
                    var table = new Table().Border(TableBorder.Rounded);
                    table.AddColumn(new TableColumn("Package").LeftAligned());
                    table.AddColumn(new TableColumn("current").LeftAligned());
                    table.AddColumn(new TableColumn("latest").LeftAligned());

                    foreach (var item in kvp.Value.OrderBy(kvp2 => kvp2.Key)) {
                        table.AddRow(
                            Markup.Escape(item.Key ?? ""),
                            FormatVersion(item.Value.current, maxMajorLength),
                            GetFormattedVersion(item.Value.current, item.Value.latest, maxMajorLength)
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

            // Build the set of all CPM files needing changes (updates and/or orphan comments).
            var cpmPaths = new HashSet<string>(propsUpdates.Keys, StringComparer.OrdinalIgnoreCase);
            if (willCommentOrphans) {
                foreach (var path in orphansToComment.Keys) cpmPaths.Add(path);
            }

            foreach (var propsPath in cpmPaths) {
                var updates = propsUpdates.TryGetValue(propsPath, out var u)
                    ? u
                    : (IReadOnlyDictionary<string, (string target, string? current)>)new Dictionary<string, (string, string?)>();
                var commentOut = (willCommentOrphans && orphansToComment.TryGetValue(propsPath, out var c))
                    ? (IReadOnlyCollection<string>)c.Keys.ToArray()
                    : Array.Empty<string>();
                // Report what was actually written. The previous message printed the intended count
                // regardless of whether any element matched, so "Updated 3 package(s)" was routine on
                // files where nothing changed at all.
                var applied = await UpdatePropsFileAsync(propsPath, updates, commentOut, cancellationToken);
                if (applied == 0) {
                    _console.WriteWarning($"No changes written to {propsPath}.");
                }
                else if (commentOut.Count > 0) {
                    _console.WriteLine($"Updated {applied} entr(ies) in {propsPath} (including {commentOut.Count} orphan(s) commented out)");
                }
                else {
                    _console.WriteLine($"Updated {applied} package(s) in {propsPath}");
                }
            }

            // Update project files
            foreach (var (projPath, updates) in projectUpdates) {
                foreach (var (pkg, v) in updates) {
                    if (await UpdatePackageVersionAsync(projPath, pkg, v, cancellationToken)) {
                        _console.WriteLine($"Updated {pkg} to {v.target} in {Path.GetFileName(projPath)}");
                    }
                    else {
                        _console.WriteWarning($"{pkg} was not updated in {Path.GetFileName(projPath)}.");
                    }
                }
            }
        }
        else {
            _console.WriteOutput("Use --apply to apply these changes.", default);
        }

        stopwatch.Stop();
        _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
        errorSink.WriteTo();

        return ExitCode(errorSink, evaluationFailures, metadataFailures);
    }

    /// <summary>
    /// Non-zero when anything prevented a complete answer, so a CI step cannot read a failed run as
    /// "no updates available".
    /// </summary>
    private static int ExitCode(ErrorSink errorSink, int evaluationFailures, int metadataFailures) =>
        errorSink.HasErrors || evaluationFailures > 0 || metadataFailures > 0 ? 1 : 0;

    internal async Task<int> UpdatePropsFileAsync(
        string propsPath,
        IReadOnlyDictionary<string, (string target, string? current)> updates,
        IReadOnlyCollection<string> commentOut,
        CancellationToken cancellationToken) {
        var applied = 0;
        try {
            var commentSet = commentOut is HashSet<string> hs && hs.Comparer == StringComparer.OrdinalIgnoreCase
                ? hs
                : new HashSet<string>(commentOut, StringComparer.OrdinalIgnoreCase);

            await XmlProjectFile.EditAsync(propsPath, doc => {
                var changed = false;
                // Materialize to a list because we mutate the tree (ReplaceWith on comment-outs).
                var packageVersionElements = doc.ElementsNamed("PackageVersion").ToList();
                foreach (var element in packageVersionElements) {
                    var include = element.Attribute("Include")?.Value;
                    if (include is null) continue;

                    if (commentSet.Contains(include)) {
                        var serialized = element.ToString(SaveOptions.DisableFormatting);
                        // "--" is illegal inside XML comments; pad it so the resulting comment parses.
                        var body = " " + serialized.Replace("--", "- -") + " ";
                        element.ReplaceWith(new XComment(body));
                        changed = true;
                        applied++;
                        continue;
                    }

                    if (updates.TryGetValue(include, out var newVersion)) {
                        var versionAttr = element.Attribute("Version");
                        var versionElement = element.ChildNamed("Version");
                        var currentValue = versionAttr?.Value ?? versionElement?.Value;
                        if (currentValue is null) continue;
                        if (!IsLiteralVersion(currentValue)) {
                            _console.WriteWarning($"Leaving {include} at '{currentValue}' in {propsPath}: floating versions, ranges and property references are not rewritten.");
                            continue;
                        }
                        if (currentValue == newVersion.target) continue;

                        if (versionAttr is { }) versionAttr.Value = newVersion.target;
                        else versionElement!.Value = newVersion.target;
                        changed = true;
                        applied++;
                    }
                }
                return changed;
            }, cancellationToken);
        }
        catch (Exception ex) {
            _errorSink?.AddError($"Failed to update {propsPath}.", exception: ex);
            _console.WriteError($"Failed to update {propsPath}: {ex.FormatMessage()}", ex);
            return 0;
        }
        return applied;
    }

    /// <summary>
    /// A version we may safely overwrite with a literal. Floating versions ("9.*"), ranges
    /// ("[9.0.0,10.0.0)") and property references ("$(XVersion)") were previously replaced with a
    /// concrete number, silently pinning a deliberately flexible reference.
    /// </summary>
    internal static bool IsLiteralVersion(string? version) {
        if (string.IsNullOrWhiteSpace(version)) return false;
        if (version.Contains('*') || version.Contains('$') || version.Contains('[') || version.Contains('(')) return false;
        return NuGetVersion.TryParse(version, out _);
    }

    private async Task<bool> UpdatePackageVersionAsync(string projectPath, string packageId, (string target, string? currentVersion, VersionReason reason) newVersion, CancellationToken cancellationToken) {
        try {
            return await XmlProjectFile.EditAsync(projectPath, doc => {
                var changed = false;
                var packageRefElements = doc.ElementsNamed("PackageReference")
                    .Where(e => string.Equals(e.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // The reported "current" version came from the evaluated project, which sees only the
                // items active for the evaluated TFM/configuration. Rewriting a condition-scoped item
                // would change a pin that never appeared in the report - e.g. bumping a net48-only
                // reference to a version that does not support net48.
                var conditioned = packageRefElements.Where(e => e.IsConditioned()).ToList();
                if (conditioned.Count > 0) {
                    _console.WriteWarning($"Skipping {conditioned.Count} conditional {packageId} reference(s) in {projectPath}; update them by hand.");
                }

                foreach (var element in packageRefElements.Except(conditioned)) {
                    // VersionOverride may be written as an attribute or as a child element; MSBuild
                    // metadata (which drove the reason) covers both, so only checking for the element
                    // meant the attribute form silently had its Version rewritten instead - leaving the
                    // override, which wins at restore, untouched.
                    var overrideAttr = element.Attribute("VersionOverride");
                    var overrideElement = element.ChildNamed("VersionOverride");
                    var versionAttr = element.Attribute("Version");
                    var versionElement = element.ChildNamed("Version");

                    var useOverride = VersionReason.VersionOverrideProj == newVersion.reason
                        && (overrideAttr is { } || overrideElement is { });

                    var currentValue = useOverride
                        ? overrideAttr?.Value ?? overrideElement?.Value
                        : versionAttr?.Value ?? versionElement?.Value;

                    if (currentValue is null) {
                        _console.WriteWarning($"No version to update for {packageId} in {projectPath}.");
                        continue;
                    }
                    if (!IsLiteralVersion(currentValue)) {
                        _console.WriteWarning($"Leaving {packageId} at '{currentValue}' in {projectPath}: floating versions, ranges and property references are not rewritten.");
                        continue;
                    }
                    if (currentValue == newVersion.target) continue;

                    if (useOverride) {
                        if (overrideAttr is { }) overrideAttr.Value = newVersion.target;
                        else overrideElement!.Value = newVersion.target;
                    }
                    else if (versionAttr is { }) versionAttr.Value = newVersion.target;
                    else versionElement!.Value = newVersion.target;

                    changed = true;
                }
                return changed;
            }, cancellationToken);
        }
        catch (Exception ex) {
            _errorSink?.AddError($"Failed to update {projectPath}.", exception: ex);
            _console.WriteError($"Failed to update {projectPath}: {ex.FormatMessage()}", ex);
            return false;
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
            var ci = StringComparer.OrdinalIgnoreCase;
            int hash = 17;
            hash = hash * 23 + (obj.Id is not null ? ci.GetHashCode(obj.Id) : 0);
            hash = hash * 23 + (obj.Version is not null ? ci.GetHashCode(obj.Version) : 0);
            hash = hash * 23 + (obj.ProjectPath is not null ? ci.GetHashCode(obj.ProjectPath) : 0);
            if (obj.TargetFrameworks != null) {
                foreach (var tfm in obj.TargetFrameworks) {
                    hash = hash * 23 + (tfm is not null ? ci.GetHashCode(tfm) : 0);
                }
            }
            hash = hash * 23 + (obj.PropsPath is not null ? ci.GetHashCode(obj.PropsPath) : 0);
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
