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
    /// Whether <paramref name="candidate"/> is inside the update window allowed by
    /// <paramref name="bump"/>, relative to the currently pinned <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Compares version components rather than testing against an upper bound, because a prerelease
    /// sorts *below* its release: "candidate &lt; 9.0.0" would let 9.0.0-preview.1 through under
    /// <c>--max-bump minor --pre</c>, which is exactly the major bump the caller ruled out.
    /// </remarks>
    internal static bool WithinBump(NuGetVersion current, NuGetVersion candidate, MaxBump bump) => bump switch {
        MaxBump.Minor => candidate.Major == current.Major,
        MaxBump.Patch => candidate.Major == current.Major && candidate.Minor == current.Minor,
        _ => true
    };

    /// <summary>
    /// Applies the <c>--package</c> / <c>--exclude</c> wildcard patterns. An empty include list means
    /// "everything"; exclude is applied afterwards and wins.
    /// </summary>
    internal static HashSet<string> SelectByFilter(IEnumerable<string> ids, IReadOnlyList<string> include, IReadOnlyList<string> exclude) {
        var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids) {
            if (include.Count > 0 && WhitelistBlacklistParser.FindMatchingPattern(id, include) is null) continue;
            if (exclude.Count > 0 && WhitelistBlacklistParser.FindMatchingPattern(id, exclude) is not null) continue;
            selected.Add(id);
        }
        return selected;
    }

    /// <summary>
    /// Flattens repeated option values and semicolon-separated lists into one pattern list, so
    /// <c>-p A -p "B;C"</c> and <c>-p A B C</c> mean the same thing.
    /// </summary>
    internal static IReadOnlyList<string> SplitPatterns(string[]? raw) {
        if (raw is null || raw.Length == 0) return Array.Empty<string>();
        return raw
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToList();
    }

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
    // conflicts where an accepted package needs a higher version of a dependency than the version
    // that dependency will actually end up at. Returns the final accepted set (a mutated copy of the
    // input).
    //
    // Scope (tier A): only dependencies that are themselves direct references in scope are checked -
    // an id is resolvable when it appears in `outdated` or in `currentPins`. Transitive chains
    // (P needs X needs D) and upper bounds declared by packages that are not being updated are not
    // followed; `--verify-restore` is the only complete answer.
    internal static HashSet<string> ResolveInteractivePicks(
        IEnumerable<string> initialAccepted,
        IReadOnlyDictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)> outdated,
        IReadOnlyDictionary<string, PackageVersionResult> metadata,
        Func<string, NuGetVersion, string, string, NuGetVersion, bool, ConflictChoice> askConflict,
        IReadOnlyDictionary<string, NuGetVersion>? currentPins = null) {

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
                    // The version this dependency will actually end up at: its update target when it
                    // is being updated too, otherwise the version it stays pinned at. A dependency
                    // that is already accepted is not automatically safe - --max-bump can cap its
                    // target below what the picker needs.
                    NuGetVersion effective;
                    bool depAlreadyIncluded;
                    var isOutdated = outdated.TryGetValue(depId, out var depVersions);
                    if (isOutdated && accepted.Contains(depId)) {
                        effective = depVersions.Latest;
                        depAlreadyIncluded = true;
                    }
                    else if (isOutdated) {
                        effective = depVersions.CurrentMin;
                        depAlreadyIncluded = false;
                    }
                    else if (currentPins is not null && currentPins.TryGetValue(depId, out var pinned)) {
                        // Up to date, or filtered out before we got here: it stays where it is.
                        effective = pinned;
                        depAlreadyIncluded = false;
                    }
                    else {
                        continue; // not a direct reference in scope - out of tier A's reach
                    }

                    if (dep.Range.Satisfies(effective)) continue; // safe either way

                    var choice = askConflict(pickerId, outdated[pickerId].Latest, depId, dep.Raw, effective, depAlreadyIncluded);
                    switch (choice) {
                        // Including only helps when the dependency has an update available that is
                        // not already selected; otherwise treat the answer as accept-risk.
                        case ConflictChoice.IncludeDep when isOutdated && !depAlreadyIncluded:
                            accepted.Add(depId);
                            changed = true;
                            break;
                        case ConflictChoice.SkipPicker:
                            accepted.Remove(pickerId);
                            changed = true;
                            break;
                        default:
                            break;
                    }
                    if (!accepted.Contains(pickerId)) break; // picker dropped — stop checking its other deps
                }
            }
        } while (changed);

        return accepted;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> CheckOutdatedPackagesAsync(string rootPath, bool updatePackages, bool skipTfmCheck, bool includePrerelease, bool listOrphans, bool commentOrphans, bool interactive, MaxBump maxBump, IReadOnlyList<string> includePatterns, IReadOnlyList<string> excludePatterns, bool allowConflicts, bool verifyRestore, IReadOnlyList<string> sources, bool ignoreSourceMapping, CancellationToken cancellationToken) {
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
        // Packages with a newer version that --max-bump refused. Reported so a capped run does not
        // read as "up to date". Kept apart from outdatedPerPackage so a held-back-only package never
        // reaches the picker, the dependency check or the apply path as if it had an update.
        var heldPerPackage = new ConcurrentDictionary<string, (NuGetVersion CurrentMin, string Held)>(StringComparer.OrdinalIgnoreCase);
        // Lowest pin per package across every usage in scope, including packages that are up to date.
        // The dependency check needs it to know where a package it is not updating will end up.
        var currentPins = new ConcurrentDictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        // NuGet metadata (with dependency manifest) cached per outdated package so the interactive
        // mode can detect transitive conflicts without re-querying NuGet.
        var packageMetadata = new ConcurrentDictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase);

        var options = new NugetMetadataOptions { MaxParallelRequests = parallelOptions.MaxDegreeOfParallelism /* configure */ };
        using var client = NugetMetadataService.CreateHttpClient(options);

        // Feeds come from the nuget.config hierarchy seen from the input, not from the working directory,
        // so `bld outdated path/to/Other.sln` uses that repo's sources.
        PackageSourceResolver sourceResolver;
        try {
            var settingsRoot = Directory.Exists(rootPath) ? rootPath : Path.GetDirectoryName(rootPath) ?? rootPath;
            sourceResolver = new PackageSourceResolver(_console, client, PackageSourceResolver.LoadSettings(settingsRoot), sources, ignoreSourceMapping, options.RegistrationBaseUrl);
        }
        catch (Exception ex) {
            _console.WriteError($"Could not read the NuGet configuration: {ex.FormatMessage()}");
            return 1;
        }
        _console.WriteDebug($"Package sources: {sourceResolver.Describe()}");

        // One package, every feed that may serve it, merged to the newest answer. A feed that is down
        // was dropped by the resolver with a warning; if that leaves nothing, the lookup fails like an
        // outage does today. A package that source mapping assigns to no source is not a failure of
        // this tool - the resolver already warned - so it is skipped without touching the exit code.
        async Task<(PackageVersionResult? Result, bool Skipped)> QueryFeedsAsync(PackageVersionRequest request, CancellationToken ct) {
            if (sourceResolver.IsUnmapped(request.PackageId)) return (null, true);

            var feeds = await sourceResolver.GetFeedsForAsync(request.PackageId, ct);
            if (feeds.Count == 0) {
                _console.WriteWarning($"No usable package source for {request.PackageId}.");
                return (null, false);
            }
            var results = new List<PackageVersionResult?>(feeds.Count);
            foreach (var feed in feeds) {
                results.Add(await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, _console, request, feed, ct));
            }
            return (NugetMetadataService.PickNewest(results), false);
        }

        await Parallel.ForEachAsync(allPackageReferences, parallelOptions, async (packageReference, ct) => {

            if (packageReference.Value is null || !packageReference.Value.Any()) {
                _console.WriteWarning($"No references found for package {packageReference.Key}");
                return;
            }

            // The lowest pin in scope has to be known before the request is built: --max-bump is
            // expressed relative to it, and the feed walk applies that cap while scanning.
            var parsedVersions = packageReference.Value
                .Select(u => NuGetVersion.TryParse(u.Version, out var v) ? v : null)
                .Where(v => v is not null)
                .ToList();
            if (parsedVersions.Count == 0) {
                _console.WriteWarning($"No parseable versions found for {packageReference.Key}; skipping.");
                return;
            }
            var currentMin = parsedVersions.Min()!;
            currentPins[packageReference.Key] = currentMin;

            // A PackageDownload is fetched into the cache, not referenced, so it has no framework to be
            // compatible with. Checking it against the project's TFM held back tooling packages that
            // ship no lib/ folder at all.
            var downloadOnly = packageReference.Value.All(u => u.Item.Kind == PackageItemKind.PackageDownload);

            var request = new PackageVersionRequest {
                PackageId = packageReference.Key,
                AllowPrerelease = includePrerelease,
                CompatibleTargetFrameworks = downloadOnly ? Array.Empty<string>() : SelectCompatibleTargetFrameworks(skipTfmCheck, packageReference.Value),
                VersionFilter = maxBump == MaxBump.Major ? null : v => WithinBump(currentMin, v, maxBump)
            };

            var (result, skipped) = await QueryFeedsAsync(request, ct);
            if (skipped) return;
            if (result is null) {
                // Count it: a network or feed outage made every lookup return null and the command
                // still exited 0, so CI read "no updates" as success.
                Interlocked.Increment(ref metadataFailures);
                _console.WriteWarning($"Failed to retrieve NuGet metadata for {request.PackageId}.");
                return;
            }

            if (result.NewestOutsideFilter is { } heldVersion) {
                heldPerPackage[packageReference.Key] = (currentMin, heldVersion);
            }

            // The window excluded everything, so there is no version to compare against. Reported as
            // held above; not a failure, and not something to run the target-version logic over.
            if (result.NoVersionWithinFilter) {
                _console.WriteDebug($"No version within the --max-bump window for {packageReference.Key}; newest outside it is {result.NewestOutsideFilter}.");
                return;
            }

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
                    var (result, _) = await QueryFeedsAsync(request, ct);
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

        // --package / --exclude. A pre-selection, like the interactive picker: metadata was fetched
        // for every package regardless, so the dependency check below can still see where an
        // excluded package stays pinned. Orphan detection is deliberately untouched.
        var excludedByFilter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (includePatterns.Count > 0 || excludePatterns.Count > 0) {
            var candidateIds = outdatedPerPackage.Keys.Concat(heldPerPackage.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var selected = SelectByFilter(candidateIds, includePatterns, excludePatterns);

            foreach (var id in candidateIds) {
                if (selected.Contains(id)) continue;
                excludedByFilter.Add(id);
                outdatedPerPackage.TryRemove(id, out _);
                heldPerPackage.TryRemove(id, out _);
            }

            if (selected.Count == 0) {
                var patternText = string.Join(", ", includePatterns.Concat(excludePatterns.Select(p => "!" + p)));
                _console.WriteWarning($"No outdated package matched the filter ({patternText}).");
            }
            else {
                _console.WriteInfo($"Filter: {selected.Count} of {candidateIds.Count} outdated package(s) selected, {excludedByFilter.Count} excluded.");
            }
        }

        // Dependency consistency check for non-interactive runs. --interactive resolves the same
        // conflicts through its own prompts below, so running both would ask and warn twice.
        var conflictHeldBack = 0;
        if (!interactive && outdatedPerPackage.Count > 0) {
            // The warning is all the user gets, so the reason has to match the branch that produced
            // it. Order matters: a package can be both capped and dropped, and the proximate reason
            // it stays where it is, is the one closest to this run's decisions.
            string HoldReason(string depId, bool depAlreadyIncluded) {
                if (depAlreadyIncluded) return "its own update target is still too low";
                if (excludedByFilter.Contains(depId)) return "excluded by --package/--exclude";
                // Still in the map at callback time: every outdated package starts accepted here, so
                // not being accepted means an earlier conflict in this same pass dropped it.
                if (outdatedPerPackage.ContainsKey(depId)) return "held back earlier in this run";
                if (heldPerPackage.ContainsKey(depId)) return "capped by --max-bump";
                return "no newer version available";
            }

            var picks = ResolveInteractivePicks(
                outdatedPerPackage.Keys.ToList(),
                outdatedPerPackage,
                packageMetadata,
                (pickerId, pickerLatest, depId, depRange, depCurrent, depAlreadyIncluded) => {
                    var action = allowConflicts
                        ? "Updating anyway (--allow-conflicts)."
                        : $"Holding {pickerId} back.";
                    var verb = depAlreadyIncluded ? "only reaches" : "stays at";
                    _console.WriteWarning(
                        $"{pickerId} {pickerLatest} requires {depId} {depRange}, but {depId} {verb} {depCurrent} ({HoldReason(depId, depAlreadyIncluded)}). {action}");
                    return allowConflicts ? ConflictChoice.AcceptRisk : ConflictChoice.SkipPicker;
                },
                currentPins);

            foreach (var id in outdatedPerPackage.Keys.ToList()) {
                if (!picks.Contains(id)) {
                    outdatedPerPackage.TryRemove(id, out _);
                    heldPerPackage.TryRemove(id, out _);
                    conflictHeldBack++;
                }
            }
            if (conflictHeldBack > 0) {
                _console.WriteLine($"{conflictHeldBack} package(s) held back due to dependency conflicts; pass --allow-conflicts to update anyway.");
            }
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
                (pickerId, pickerLatest, depId, depRange, depCurrent, depAlreadyIncluded) => {
                    _console.WriteWarning(
                        $"{pickerId} {pickerLatest} requires {depId} {depRange}, but {depId} stays at {depCurrent}.");
                    // Offering "include it too" only makes sense when there is an unselected update
                    // to include: a dependency that is already selected, filtered out or up to date
                    // has nothing left to add.
                    var canInclude = !depAlreadyIncluded && outdatedPerPackage.ContainsKey(depId);
                    if (canInclude && _console.Confirm($"  Include {depId} update too?", defaultValue: true)) return ConflictChoice.IncludeDep;
                    if (_console.Confirm($"  Skip {pickerId} as well?", defaultValue: false)) return ConflictChoice.SkipPicker;
                    return ConflictChoice.AcceptRisk;
                },
                currentPins);

            var dropped = 0;
            foreach (var id in outdatedPerPackage.Keys.ToList()) {
                if (!picks.Contains(id)) {
                    outdatedPerPackage.TryRemove(id, out _);
                    heldPerPackage.TryRemove(id, out _);
                    dropped++;
                }
            }
            _console.WriteInfo($"Interactive selection: {picks.Count} package(s) selected, {dropped} skipped.");
        }

        if (outdatedPerPackage.Count == 0 && heldPerPackage.IsEmpty && orphansToComment.IsEmpty) {
            // Only claim everything is up to date when we actually managed to look.
            if (metadataFailures > 0 || evaluationFailures > 0) {
                _console.WriteWarning($"Incomplete run: {evaluationFailures} project(s) failed to analyze and {metadataFailures} package lookup(s) failed. Results are not conclusive.");
            }
            else if (conflictHeldBack > 0) {
                // Updates existed; they were all withheld. Saying "up to date" here would be a lie.
                _console.WriteLine($"Nothing left to update: all {conflictHeldBack} candidate(s) were held back by the dependency check.");
            }
            else {
                _console.WriteLine("All packages are up to date!");
            }
            stopwatch.Stop();
            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
            errorSink.WriteTo();
            return ExitCode(errorSink, evaluationFailures, metadataFailures);
        }

        // One row per package that has either an update inside the window or a version the cap held
        // back. A held-back-only package shows current == latest, so the apply path (guarded by
        // HasVersionUpdate) leaves it alone.
        var reportRows = outdatedPerPackage.Keys.Concat(heldPerPackage.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id => {
                var held = heldPerPackage.TryGetValue(id, out var h) ? h.Held : null;
                if (outdatedPerPackage.TryGetValue(id, out var v)) return (Id: id, v.CurrentMin, v.Latest, Held: held);
                var pinned = heldPerPackage[id].CurrentMin;
                return (Id: id, CurrentMin: pinned, Latest: pinned, Held: held);
            })
            .ToList();

        int maxMajorLength = reportRows
            .SelectMany(v => new[] {
                v.CurrentMin?.Major.ToString().Length ?? 0,
                v.Latest?.Major.ToString().Length ?? 0,
                NuGetVersion.TryParse(v.Held, out var h) ? h.Major.ToString().Length : 0
            })
            .Concat(orphansToComment.Values.SelectMany(d => d.Values).SelectMany(v => new[] {
                NuGetVersion.TryParse(v.current, out var c) ? c.Major.ToString().Length : 0,
                NuGetVersion.TryParse(v.latest, out var l) ? l.Major.ToString().Length : 0
            }))
            .DefaultIfEmpty(0)
            .Max();

        if (reportRows.Count > 0) {
            _console.WriteLine(outdatedPerPackage.Count > 0
                ? $"\nFound {outdatedPerPackage.Count} packages with available updates:"
                : $"\nNo updates available within --max-bump {maxBump.ToString().ToLowerInvariant()}, but newer versions exist:");
            if (_options.MarkdownOutput) {
                var rows = reportRows
                    .Select(row => (IReadOnlyList<string?>)new[] {
                        row.Id,
                        PlainVersion(row.CurrentMin),
                        PlainVersion(row.Latest),
                        row.Held ?? string.Empty
                    });

                MarkdownTableFormatter.Write(_console, "Outdated packages (markdown)", new[] { "PackageId", "Current", "Latest", "Held" }, rows);
            }
            else {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn(new TableColumn("PackageId").LeftAligned());
                table.AddColumn(new TableColumn("current").LeftAligned());
                table.AddColumn(new TableColumn("latest").LeftAligned());
                table.AddColumn(new TableColumn("held").LeftAligned());

                foreach (var row in reportRows) {
                    table.AddRow(
                        Markup.Escape(row.Id ?? ""),
                        FormatVersion(row.CurrentMin, maxMajorLength),
                        GetFormattedVersion(row.CurrentMin, row.Latest, maxMajorLength),
                        row.Held is null ? "" : Markup.Escape(row.Held)
                    );
                }
                _console.WriteTable(table);
            }

            if (!heldPerPackage.IsEmpty) {
                _console.WriteLine($"{heldPerPackage.Count} package(s) have newer versions held back by --max-bump {maxBump.ToString().ToLowerInvariant()}.");
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
                        if (item.Kind == PackageItemKind.PackageDownload) return VersionReason.PackageDownloadProj;
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
                    VersionReason.PackageDownloadProj => "PackageDownload in project file",
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

            if (verifyRestore) {
                _console.WriteInfo("\nVerifying the update with dotnet restore...");
                var restoreErrors = await RunRestoreAsync(rootPath, cancellationToken);
                if (restoreErrors.Count == 0) {
                    _console.WriteLine("Restore succeeded after update.");
                }
                else {
                    foreach (var line in restoreErrors) {
                        errorSink.AddError(line);
                        _console.WriteError(line);
                    }
                    _console.WriteWarning("Restore failed. The updated files were left in place - inspect them with git diff and revert what you do not want.");
                }
            }
        }
        else {
            if (verifyRestore) {
                _console.WriteWarning("--verify-restore only applies together with --apply; nothing was written, so there is nothing to verify.");
            }
            _console.WriteOutput("Use --apply to apply these changes.", default);
        }

        stopwatch.Stop();
        _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");
        errorSink.WriteTo();

        return ExitCode(errorSink, evaluationFailures, metadataFailures);
    }

    /// <summary>
    /// NuGet error lines from restore output, deduplicated and in the order they appeared. Anything
    /// else (warnings, MSBuild chatter, progress) is left to the debug log.
    /// </summary>
    internal static IReadOnlyList<string> ParseRestoreErrors(IEnumerable<string> outputLines) {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var line in outputLines) {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.IndexOf("error NU", StringComparison.OrdinalIgnoreCase) < 0) continue;
            var trimmed = line.Trim();
            if (seen.Add(trimmed)) errors.Add(trimmed);
        }
        return errors;
    }

    /// <summary>
    /// Runs <c>dotnet restore</c> against the command's input and returns the reasons it failed, or
    /// an empty list when it succeeded. This is the only check that sees what NuGet actually
    /// resolves - the in-process dependency check covers direct references only.
    /// </summary>
    /// <remarks>
    /// Every failure path returns a line rather than recording it directly, so the caller stays the
    /// single place that reports and counts them. An empty result means success and nothing else -
    /// returning empty on a caught exception made the caller print "Restore succeeded" after a
    /// failure (e.g. dotnet missing from PATH).
    /// </remarks>
    private async Task<IReadOnlyList<string>> RunRestoreAsync(string input, CancellationToken cancellationToken) {
        var startInfo = new ProcessStartInfo("dotnet") {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("restore");
        startInfo.ArgumentList.Add(input);
        startInfo.ArgumentList.Add("--nologo");

        var lines = new List<string>();
        try {
            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += Collect;
            process.ErrorDataReceived += Collect;

            void Collect(object _, DataReceivedEventArgs e) {
                if (e.Data is null) return;
                lock (lines) lines.Add(e.Data);
                _console.WriteDebug(e.Data);
            }

            if (!process.Start()) {
                return new[] { "Could not start 'dotnet restore'; the update was not verified." };
            }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken);

            List<string> snapshot;
            lock (lines) snapshot = lines.ToList();
            var errors = ParseRestoreErrors(snapshot);

            // A non-zero exit with no parseable NU line still means restore failed; do not report success.
            if (errors.Count == 0 && process.ExitCode != 0) {
                return new[] { $"dotnet restore exited with code {process.ExitCode}. Re-run it manually for the full output." };
            }
            return errors;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // Not a restore result: we never learned whether the update is sound. Report it as a
            // failure so neither the console message nor the exit code claims success.
            return new[] { $"Failed to run 'dotnet restore': {ex.FormatMessage()}" };
        }
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
                // GlobalPackageReference carries its own Version and is updated in place; it is never
                // an orphan (it applies to every project), so only PackageVersion is commented out.
                var packageVersionElements = doc.ElementsNamed("PackageVersion")
                    .Concat(doc.ElementsNamed("GlobalPackageReference"))
                    .ToList();
                foreach (var element in packageVersionElements) {
                    var include = element.Attribute("Include")?.Value;
                    if (include is null) continue;

                    if (commentSet.Contains(include) && element.Name.LocalName == "PackageVersion") {
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

    internal async Task<bool> UpdatePackageVersionAsync(string projectPath, string packageId, (string target, string? currentVersion, VersionReason reason) newVersion, CancellationToken cancellationToken) {
        if (newVersion.reason == VersionReason.PackageDownloadProj) {
            return await UpdatePackageDownloadAsync(projectPath, packageId, newVersion.target, newVersion.currentVersion, cancellationToken);
        }

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

    /// <summary>
    /// PackageDownload versions are exact ranges, "[8.0.0]", and one item may list several separated by
    /// ';'. Only the entry reported as current moves; the others are deliberate pins of older versions.
    /// </summary>
    private async Task<bool> UpdatePackageDownloadAsync(string projectPath, string packageId, string target, string? current, CancellationToken cancellationToken) {
        if (!NuGetVersion.TryParse(current, out var currentVersion)) {
            _console.WriteWarning($"No parseable current version for PackageDownload {packageId} in {projectPath}; not updated.");
            return false;
        }

        try {
            return await XmlProjectFile.EditAsync(projectPath, doc => {
                var changed = false;
                var elements = doc.ElementsNamed("PackageDownload")
                    .Where(e => string.Equals(e.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var element in elements) {
                    if (element.IsConditioned()) {
                        _console.WriteWarning($"Skipping conditional PackageDownload {packageId} in {projectPath}; update it by hand.");
                        continue;
                    }

                    var versionAttr = element.Attribute("Version");
                    var versionElement = element.ChildNamed("Version");
                    var value = versionAttr?.Value ?? versionElement?.Value;
                    if (value is null) continue;

                    var parts = value.Split(';').Select(p => p.Trim()).ToList();
                    var index = parts.FindIndex(p => VersionRange.TryParse(p, out var range) && range.MinVersion is { } min && min == currentVersion);
                    if (index < 0) {
                        _console.WriteWarning($"PackageDownload {packageId} in {projectPath} has no entry for {current} (found '{value}'); not updated.");
                        continue;
                    }

                    parts[index] = $"[{target}]";
                    var updated = string.Join(";", parts);
                    if (updated == value) continue;

                    if (versionAttr is { }) versionAttr.Value = updated;
                    else versionElement!.Value = updated;
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
    PackageDownloadProj,

    PackageVersionCpm,
}

/// <summary>
/// How far a package may be moved by <c>--apply</c>, relative to the version it is pinned at now.
/// </summary>
internal enum MaxBump {
    /// <summary>No cap - the newest compatible version wins (the default).</summary>
    Major,
    /// <summary>Highest version with the same major, so no breaking change per SemVer.</summary>
    Minor,
    /// <summary>Highest version with the same major and minor.</summary>
    Patch,
}
