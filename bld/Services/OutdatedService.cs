using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using NuGet.Frameworks;
using NuGet.Versioning;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
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
    // Non-null only while files are being written; the helpers append what they changed, and the
    // run stores it for `bld outdated undo`.
    private List<JournalEdit>? _journal;

    public OutdatedService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    /// <summary>Where the undo journal is written. Tests point it at a temp directory.</summary>
    internal UpdateJournal Journal { get; set; } = new(UpdateJournal.DefaultRoot);

    /// <summary>The command name recorded in the journal; null means the outdated command itself.</summary>
    internal string? JournalCommand { get; set; }

    internal static string BldVersion =>
        typeof(OutdatedService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(OutdatedService).Assembly.GetName().Version?.ToString()
        ?? "unknown";

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
    /// The candidate a cap selects. The classes are cumulative, so <c>minor</c> already means "the
    /// highest version with the same major"; the fallbacks only matter when the pinned version is
    /// not listed on the feed at all and a class stayed empty.
    /// </summary>
    internal static VersionCandidate? CandidateForBump(PackageVersionCandidates candidates, MaxBump bump) => bump switch {
        MaxBump.Patch => candidates.Patch,
        MaxBump.Minor => candidates.Minor ?? candidates.Patch,
        _ => candidates.Major ?? candidates.Minor ?? candidates.Patch
    };

    /// <summary>Whether a package id matches one <c>--package</c>-style wildcard pattern.</summary>
    internal static bool Matches(string id, string pattern) =>
        WhitelistBlacklistParser.FindMatchingPattern(id, new[] { pattern }) is not null;

    /// <summary>
    /// Parses <c>--max-bump-for "&lt;pattern&gt;=&lt;level&gt;"</c> values, accepting the same
    /// repetition and ';'-separated lists as <c>--package</c>.
    /// </summary>
    /// <exception cref="FormatException">An entry has no '=' or names an unknown level.</exception>
    internal static IReadOnlyList<(string Pattern, MaxBump Level)> ParseBumpOverrides(string[]? raw) {
        var overrides = new List<(string, MaxBump)>();
        foreach (var entry in SplitPatterns(raw)) {
            var separator = entry.LastIndexOf('=');
            if (separator <= 0 || separator == entry.Length - 1) {
                throw new FormatException($"--max-bump-for expects \"<pattern>=<major|minor|patch>\", got \"{entry}\".");
            }
            var level = entry[(separator + 1)..].Trim();
            if (!Enum.TryParse<MaxBump>(level, ignoreCase: true, out var parsed)) {
                throw new FormatException($"--max-bump-for: unknown level \"{level}\" in \"{entry}\". Use major, minor or patch.");
            }
            overrides.Add((entry[..separator].Trim(), parsed));
        }
        return overrides;
    }

    /// <summary>
    /// The cap that applies to one package: the most specific matching <c>--max-bump-for</c>
    /// pattern, or <paramref name="global"/> when none matches. Specificity is the number of
    /// non-wildcard characters, so "Microsoft.Extensions.*" beats "Microsoft.*"; ties go to the
    /// pattern given last.
    /// </summary>
    internal static MaxBump EffectiveBump(string id, MaxBump global, IReadOnlyList<(string Pattern, MaxBump Level)> overrides) =>
        ResolveBump(id, global, overrides, Array.Empty<PolicyRule>()).Level;

    internal enum BumpSource { Global, Override, Policy }

    /// <summary>
    /// The cap that applies to one package and where it comes from: a <c>--max-bump-for</c> pattern
    /// beats a policy rule, which beats <c>--max-bump</c>. A policy is the more specific statement,
    /// so a global <c>--max-bump major</c> does not lift it; only an override or
    /// <c>--ignore-policy</c> does.
    /// </summary>
    internal static (MaxBump Level, BumpSource Source, PolicyRule? Rule) ResolveBump(string id, MaxBump global, IReadOnlyList<(string Pattern, MaxBump Level)> overrides, IReadOnlyList<PolicyRule> policies) {
        var overriding = PolicyService.Find(id, overrides.Select(o => new PolicyRule(o.Pattern, o.Level, null, null)).ToList());
        if (overriding is not null) return (overriding.MaxBump, BumpSource.Override, null);
        var rule = PolicyService.Find(id, policies);
        if (rule is not null) return (rule.MaxBump, BumpSource.Policy, rule);
        return (global, BumpSource.Global, null);
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
    // Scope (tier A): only direct references in scope are checked - an id is resolvable when it
    // appears in `outdated` or in `currentPins`. Two directions:
    //  - forward: an accepted package's target declares a range on another direct reference, and
    //    the version that reference ends up at is outside it;
    //  - reverse (only with `currentManifests` and `askBlocked`): a direct reference that stays where
    //    it is declares a range on an accepted package, and the target is outside it.
    // Transitive chains (P needs X needs D) are not followed; `--verify-restore` is the only
    // complete answer.
    internal static HashSet<string> ResolveInteractivePicks(
        IEnumerable<string> initialAccepted,
        IReadOnlyDictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)> outdated,
        IReadOnlyDictionary<string, PackageVersionResult> metadata,
        Func<string, NuGetVersion, string, string, NuGetVersion, bool, ConflictChoice> askConflict,
        IReadOnlyDictionary<string, NuGetVersion>? currentPins = null,
        IReadOnlyDictionary<string, Dictionary<NuGetFramework, DependencyGroup>>? currentManifests = null,
        Func<string, NuGetVersion, string, NuGetVersion, string, bool, ConflictChoice>? askBlocked = null) {

        var accepted = new HashSet<string>(initialAccepted, StringComparer.OrdinalIgnoreCase);

        bool changed;
        do {
            changed = false;
            foreach (var pickerId in accepted.ToList()) {
                if (!accepted.Contains(pickerId)) continue; // removed mid-loop
                if (!outdated.ContainsKey(pickerId)) continue; // caller passed an id we don't track

                if (currentManifests is not null && askBlocked is not null) {
                    var target = outdated[pickerId].Latest;
                    foreach (var (blockerId, manifest) in currentManifests.OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)) {
                        if (blockerId.Equals(pickerId, StringComparison.OrdinalIgnoreCase)) continue;
                        // A blocker that moves too is judged by its target's manifest in the forward
                        // pass, not by the manifest of the version it is leaving.
                        if (accepted.Contains(blockerId)) continue;
                        if (!DeclaredRanges(manifest).TryGetValue(pickerId, out var ranges)) continue;
                        var failing = ranges.FirstOrDefault(r => !r.Range.Satisfies(target));
                        if (failing.Range is null) continue;

                        NuGetVersion blockerVersion;
                        var blockerOutdated = outdated.TryGetValue(blockerId, out var blockerVersions);
                        if (blockerOutdated) blockerVersion = blockerVersions.CurrentMin;
                        else if (currentPins is not null && currentPins.TryGetValue(blockerId, out var pinned)) blockerVersion = pinned;
                        else continue; // not a direct reference in scope

                        var choice = askBlocked(pickerId, target, blockerId, blockerVersion, failing.Raw, blockerOutdated);
                        switch (choice) {
                            case ConflictChoice.IncludeDep when blockerOutdated:
                                accepted.Add(blockerId);
                                changed = true;
                                break;
                            case ConflictChoice.SkipPicker:
                                accepted.Remove(pickerId);
                                changed = true;
                                break;
                            default:
                                break;
                        }
                        if (!accepted.Contains(pickerId)) break;
                    }
                    if (!accepted.Contains(pickerId)) continue;
                }

                if (!metadata.TryGetValue(pickerId, out var meta) || meta?.Dependencies is null) continue;

                foreach (var (depId, ranges) in DeclaredRanges(meta.Dependencies)) {
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

                    // Every framework has to be satisfied by the one version the dependency ends up
                    // at, so each group's range counts; the first one that fails is the one reported.
                    var failing = ranges.FirstOrDefault(r => !r.Range.Satisfies(effective));
                    if (failing.Range is null) continue; // safe either way

                    var choice = askConflict(pickerId, outdated[pickerId].Latest, depId, failing.Raw, effective, depAlreadyIncluded);
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

    // The ranges a manifest declares per dependency id, one entry per distinct range text across
    // the TFM groups. Merging them into one range lost upper bounds: "(, )" on one group and
    // "[3.0.0, 4.0.0)" on another is two constraints, not one. Guards against nulls because NuGet
    // catalog JSON can contain "dependencies": null on a group, which System.Text.Json deserializes
    // to a null property even with a `= []` default.
    private static Dictionary<string, List<(string Raw, VersionRange Range)>> DeclaredRanges(Dictionary<NuGetFramework, DependencyGroup> manifest) {
        var result = new Dictionary<string, List<(string Raw, VersionRange Range)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var dg in manifest.Values) {
            if (dg?.Dependencies is null) continue;
            foreach (var dep in dg.Dependencies) {
                if (dep is null || string.IsNullOrEmpty(dep.PackageId) || string.IsNullOrEmpty(dep.Range)) continue;
                if (!VersionRange.TryParse(dep.Range, out var range)) continue;
                if (!result.TryGetValue(dep.PackageId, out var ranges)) result[dep.PackageId] = ranges = new();
                if (ranges.Any(r => r.Raw == dep.Range)) continue;
                ranges.Add((dep.Range, range));
            }
        }
        return result;
    }

    private static bool IsPreselected(BumpKind bump, Preselect preselect) => preselect switch {
        Preselect.All => true,
        Preselect.NoMajor => bump != BumpKind.Major,
        Preselect.Patch => bump == BumpKind.Patch,
        _ => false
    };

    /// <summary>
    /// Everything the picker shows, as data: which groups exist, which rows they hold, and what is
    /// pre-selected. Built without Spectre so the layout can be asserted in tests.
    /// </summary>
    /// <summary>
    /// Every target offered for one package: the one the cap picked plus each candidate class above
    /// the current pin, ascending and deduplicated.
    /// </summary>
    internal static IReadOnlyList<PickerTarget> TargetsFor(NuGetVersion current, NuGetVersion chosen, PackageVersionCandidates? candidates) {
        var versions = new List<NuGetVersion> { chosen };
        foreach (var candidate in new[] { candidates?.Patch, candidates?.Minor, candidates?.Major }) {
            if (candidate is null) continue;
            if (!NuGetVersion.TryParse(candidate.Version, out var version)) continue;
            if (version <= current) continue; // not an update
            versions.Add(version);
        }
        return versions
            .Distinct()
            .OrderBy(v => v)
            .Select(v => new PickerTarget(v, PackageGrouper.Classify(current, v)))
            .ToList();
    }

    internal static PickerModel BuildPickerModel(
        IReadOnlyDictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)> outdated,
        GroupingOptions grouping,
        Preselect preselect,
        IReadOnlyDictionary<string, PackageVersionCandidates>? candidates = null,
        IReadOnlySet<string>? neverPreselect = null,
        IReadOnlyList<PolicyRule>? policies = null) {

        var rows = new Dictionary<string, PickerRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, versions) in outdated) {
            var bump = PackageGrouper.Classify(versions.CurrentMin, versions.Latest);
            var targets = TargetsFor(versions.CurrentMin, versions.Latest,
                candidates is not null && candidates.TryGetValue(id, out var c) ? c : null);
            var defaultTarget = targets.ToList().FindIndex(t => t.Version.Equals(versions.Latest));
            // A package the cap excluded on purpose starts unchecked no matter what --preselect says:
            // it is only in the list so the user can reach it, not to be taken by default.
            var preselected = IsPreselected(bump, preselect) && !(neverPreselect?.Contains(id) ?? false);
            var rule = policies is null ? null : PolicyService.Find(id, policies);
            rows[id] = new PickerRow(id, versions.CurrentMin, targets, Math.Max(0, defaultTarget), preselected, rule?.Match, rule?.MaxBump);
        }

        var groups = grouping.GroupBy switch {
            GroupBy.Prefix => PackageGrouper.GroupByPrefix(rows.Keys, grouping.Depth, grouping.MinSize),
            GroupBy.Bump => PackageGrouper.GroupByBump(rows.Values.Select(r => (r.Id, r.Current, r.Target))),
            // One nameless group: the picker renders it without a group row, exactly as before.
            _ => new[] { new PackageGroup(string.Empty, rows.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()) }
        };

        return new PickerModel(groups
            .Select(g => new PickerGroup(g.Name, g.Ids.Select(id => rows[id]).ToList()))
            .ToList());
    }

    /// <summary>The candidate holding a specific version, so its dependency manifest can be found.</summary>
    internal static VersionCandidate? CandidateWithVersion(PackageVersionCandidates candidates, NuGetVersion version) {
        foreach (var candidate in new[] { candidates.Patch, candidates.Minor, candidates.Major }) {
            if (candidate is null) continue;
            if (NuGetVersion.TryParse(candidate.Version, out var parsed) && parsed.Equals(version)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// The <c>--max-bump-for</c> class a repeated run needs for one package, or null when the cap in
    /// force picks the chosen version by itself. Lowering counts as much as raising: taking the patch
    /// under a major cap is only reproducible with an explicit override.
    /// </summary>
    internal static BumpKind? RepeatOverride(NuGetVersion current, NuGetVersion chosen, PackageVersionCandidates? candidates, MaxBump cap) {
        var defaultTarget = candidates is null ? null : CandidateForBump(candidates, cap);
        if (defaultTarget is not null && NuGetVersion.TryParse(defaultTarget.Version, out var version) && version.Equals(chosen)) return null;
        return PackageGrouper.Classify(current, chosen);
    }

    /// <summary>
    /// Group assignment for the report table. A held-back-only row has no update target, so its
    /// held version stands in for one when grouping by bump class.
    /// </summary>
    internal static IReadOnlyList<PackageGroup> BuildReportGroups(
        IEnumerable<(string Id, NuGetVersion CurrentMin, NuGetVersion Latest, string? Held)> rows,
        GroupingOptions grouping) {

        var list = rows.ToList();
        return grouping.GroupBy switch {
            GroupBy.Prefix => PackageGrouper.GroupByPrefix(list.Select(r => r.Id), grouping.Depth, grouping.MinSize),
            GroupBy.Bump => PackageGrouper.GroupByBump(list.Select(r => (r.Id, r.CurrentMin, ReportTarget(r)))),
            _ => new[] { new PackageGroup(string.Empty, list.Select(r => r.Id).ToList()) }
        };

        static NuGetVersion ReportTarget((string Id, NuGetVersion CurrentMin, NuGetVersion Latest, string? Held) row) =>
            row.Latest > row.CurrentMin || row.Held is null || !NuGetVersion.TryParse(row.Held, out var held)
                ? row.Latest
                : held;
    }

    /// <summary>
    /// The command line that reproduces the picker's selection without any prompt. A fully selected
    /// prefix group collapses to one <c>-p "&lt;prefix&gt;.*"</c>.
    /// </summary>
    /// <remarks>
    /// This reproduces the <em>selection</em>, not necessarily the result: a later run can see newer
    /// versions than this one did.
    /// </remarks>
    internal static string BuildRepeatCommand(
        string input,
        IReadOnlyList<PickerGroup> groups,
        ISet<string> chosen,
        IReadOnlyList<(string Pattern, MaxBump Level)> patternOverrides,
        IReadOnlyDictionary<string, BumpKind> bumpOverrides,
        MaxBump globalBump,
        bool prerelease,
        IReadOnlyList<string> sources) {

        var parts = new List<string> { "bld", "outdated", QuoteArgument(input), "--apply" };
        if (prerelease) parts.Add("--prerelease");
        foreach (var source in sources) {
            parts.Add("--source");
            parts.Add(QuoteArgument(source));
        }

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups) {
            var selected = group.Rows.Where(r => chosen.Contains(r.Id)).ToList();
            if (selected.Count == 0) continue;
            // "(other)" and the bump groups are not patterns, so they always resolve to single ids.
            if (selected.Count == group.Rows.Count && group.Name.EndsWith(".*", StringComparison.Ordinal)) {
                parts.Add("-p");
                parts.Add(QuoteArgument(group.Name));
                // The pattern needs a dot after the prefix, so the family's bare package (Serilog in
                // Serilog.*) has to be named on its own.
                var prefix = group.Name[..^2];
                foreach (var row in selected) {
                    if (row.Id.Equals(prefix, StringComparison.OrdinalIgnoreCase)) {
                        parts.Add("-p");
                        parts.Add(QuoteArgument(row.Id));
                    }
                    covered.Add(row.Id);
                }
                continue;
            }
            foreach (var row in selected) {
                parts.Add("-p");
                parts.Add(QuoteArgument(row.Id));
                covered.Add(row.Id);
            }
        }

        // Released held versions were never rows in the main picker, so name them explicitly.
        foreach (var id in chosen.Where(id => !covered.Contains(id)).OrderBy(id => id, StringComparer.OrdinalIgnoreCase)) {
            parts.Add("-p");
            parts.Add(QuoteArgument(id));
        }

        if (globalBump != MaxBump.Major) {
            parts.Add("--max-bump");
            parts.Add(globalBump.ToString().ToLowerInvariant());
        }
        // The run's own pattern overrides first, so that a per-package override below wins the tie
        // when both name the same id.
        foreach (var (pattern, level) in patternOverrides) {
            parts.Add("--max-bump-for");
            parts.Add(QuoteArgument($"{pattern}={level.ToString().ToLowerInvariant()}"));
        }
        foreach (var (id, bump) in bumpOverrides.OrderBy(o => o.Key, StringComparer.OrdinalIgnoreCase)) {
            parts.Add("--max-bump-for");
            parts.Add(QuoteArgument($"{id}={bump.ToString().ToLowerInvariant()}"));
        }

        return string.Join(' ', parts);
    }

    // '*' and '?' are quoted too: an unquoted -p Serilog.* would be glob-expanded by the shell
    // before bld ever sees the pattern.
    private static readonly char[] ShellUnsafeCharacters = { ' ', '\t', '"', '\'', '&', '|', '<', '>', '^', '(', ')', ';', '*', '?' };

    internal static string QuoteArgument(string value) =>
        value.Length == 0 || value.IndexOfAny(ShellUnsafeCharacters) >= 0
            ? "\"" + value.Replace("\"", "\\\"") + "\""
            : value;

    /// <summary>
    /// Counts rather than the name of the <c>--preselect</c> mode: rows above the cap are never
    /// pre-selected, so "all pre-selected" would be a lie on exactly the run where it matters.
    /// </summary>
    /// <summary>"3 package(s) have newer versions held back: 2 by --max-bump minor, 1 by policy."</summary>
    internal static string HeldSummary(IReadOnlyList<BumpSource> sources, MaxBump globalBump) {
        var parts = new List<string>();
        var byGlobal = sources.Count(s => s == BumpSource.Global);
        var byOverride = sources.Count(s => s == BumpSource.Override);
        var byPolicy = sources.Count(s => s == BumpSource.Policy);
        if (byGlobal > 0) parts.Add($"{byGlobal} by --max-bump {globalBump.ToString().ToLowerInvariant()}");
        if (byOverride > 0) parts.Add($"{byOverride} by --max-bump-for");
        if (byPolicy > 0) parts.Add($"{byPolicy} by policy");
        return $"{sources.Count} package(s) have newer versions held back: {string.Join(", ", parts)}.";
    }

    internal static string PickerTitle(int count, int heldCount, int preselectedCount, string capDescription) {
        var held = heldCount > 0 ? $", {heldCount} above {capDescription}" : string.Empty;
        return $"[bold]Select packages to update[/] [grey]{Markup.Escape($"({count} listed{held}; {preselectedCount} pre-selected)")}[/]";
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> CheckOutdatedPackagesAsync(string rootPath, bool updatePackages, bool skipTfmCheck, bool includePrerelease, bool listOrphans, bool commentOrphans, bool interactive, MaxBump maxBump, IReadOnlyList<(string Pattern, MaxBump Level)> bumpOverrides, IReadOnlyList<string> includePatterns, IReadOnlyList<string> excludePatterns, bool allowConflicts, bool verifyRestore, IReadOnlyList<string> sources, bool ignoreSourceMapping, GroupingOptions grouping, Preselect preselect, bool evalCache, PolicyService? policies, bool ignorePolicy, CancellationToken cancellationToken) {
        // Before anything is evaluated or fetched: a picker we cannot show makes the whole run
        // pointless, and --interactive implies --apply.
        if (interactive && !_console.CanPrompt) {
            _console.WriteError("--interactive needs an interactive terminal. Use --apply with -p/--exclude and --max-bump instead.");
            return 1;
        }

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
            using var projParser = new ProjParser(_console, errorSink, _options);
            if (evalCache) {
                projParser.Cache = new EvaluationCache(Path.Combine(BldHome.Cache, "eval"), ProjParser.ToolsIdentity, _console);
                _console.WriteDebug($"Evaluation cache: {Path.Combine(BldHome.Cache, "eval")}");
            }

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

            // Only the Release configuration is analyzed, so only that one is evaluated. Its
            // ProjectReferences come out of the same evaluation as the PackageReferences and are
            // followed transitively, one level at a time, so a single csproj input (or a slnx that
            // omits a referenced project) still picks up packages from projects it depends on.
            // Children inherit Configuration/Platform from the parent so config/platform-conditional
            // <ProjectReference> items evaluate the same way `dotnet build` would resolve them.
            // Walking the references used to be a second, sequential evaluation of every
            // configuration before the analysis even started.
            var visitedProjectPaths = new HashSet<string>(allProjCfgs.Select(p => p.Path), StringComparer.OrdinalIgnoreCase);
            var frontier = allProjCfgs
                .Where(p => string.Equals(p.Configuration, "Release", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var evaluationWatch = Stopwatch.StartNew();
            var evaluated = 0;

            await _console.StartStatusAsync($"Analyzing {frontier.Count} project configurations...", async ctx => {
                while (frontier.Count > 0) {
                    var discovered = new ConcurrentBag<ProjCfg>();
                    var total = evaluated + frontier.Count;

                    await Parallel.ForEachAsync(frontier, parallelOptions, async (projCfg, ct) => {
                        var current = Interlocked.Increment(ref evaluated);
                        ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");

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
                                foreach (var refPath in refs.ProjectReferences) {
                                    bool isNew;
                                    lock (visitedProjectPaths) isNew = visitedProjectPaths.Add(refPath);
                                    if (!isNew) continue;
                                    var child = new ProjCfg(new Proj(refPath, null), projCfg.Configuration, projCfg.Platform);
                                    if (cache.Add(child)) {
                                        discovered.Add(child);
                                        _console.WriteDebug($"Discovered ProjectReference target: {refPath} [{projCfg.Configuration}|{projCfg.Platform}]");
                                    }
                                }

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

                    frontier = discovered.ToList();
                }
            });
            _console.WriteInfo($"Evaluated {evaluated} project configuration(s) in {evaluationWatch.Elapsed}");
            if (projParser.Cache is { } evaluationCache) {
                _console.WriteLine($"Evaluation from cache: {evaluationCache.Hits} of {evaluationCache.Hits + evaluationCache.Misses} project configuration(s).");
            }
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
        // Which cap held each of them, so the summary can say "2 by policy" rather than name one cap.
        var heldBy = new ConcurrentDictionary<string, BumpSource>(StringComparer.OrdinalIgnoreCase);
        var policyRules = ignorePolicy || policies is null ? new List<PolicyRule>() : policies.Rules;
        if (ignorePolicy && policies is { Rules.Count: > 0 }) _console.WriteInfo($"--ignore-policy: {policies.Rules.Count} policy rule(s) not applied.");
        // Lowest pin per package across every usage in scope, including packages that are up to date.
        // The dependency check needs it to know where a package it is not updating will end up.
        var currentPins = new ConcurrentDictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
        // NuGet metadata (with dependency manifest) cached per outdated package so the interactive
        // mode can detect transitive conflicts without re-querying NuGet.
        var packageMetadata = new ConcurrentDictionary<string, PackageVersionResult>(StringComparer.OrdinalIgnoreCase);
        // Every target the feeds offer per package (highest patch / minor / major), so the picker can
        // move a package between them without another lookup.
        var candidatesPerPackage = new ConcurrentDictionary<string, PackageVersionCandidates>(StringComparer.OrdinalIgnoreCase);
        // The dependency manifest of the version each package is pinned at now, for every package
        // the feeds know, so the conflict check can see what a package that stays put requires of
        // the ones that move.
        var currentManifests = new ConcurrentDictionary<string, Dictionary<NuGetFramework, DependencyGroup>>(StringComparer.OrdinalIgnoreCase);

        // The lookups are I/O bound, and --concurrency is sized for MSBuild evaluation: on a laptop
        // it defaults to 4, which fetched a few hundred packages in as many sequential rounds. The
        // same number caps the requests in flight, whatever the number of feeds per package.
        // --concurrency 1 means sequential, and a rate-limited feed is one reason to ask for it.
        var concurrency = parallelOptions.MaxDegreeOfParallelism;
        var metadataParallelism = new ParallelOptions { MaxDegreeOfParallelism = concurrency == 1 ? 1 : Math.Max(concurrency, 16) };
        var options = new NugetMetadataOptions { MaxParallelRequests = metadataParallelism.MaxDegreeOfParallelism };
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
            // Every feed at once: with a private feed next to nuget.org, asking them one after the
            // other doubled the time per package.
            var results = await Task.WhenAll(feeds.Select(feed =>
                NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, _console, request, feed, ct).AsTask()));
            return (NugetMetadataService.PickNewest(results), false);
        }

        var metadataWatch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(allPackageReferences, metadataParallelism, async (packageReference, ct) => {

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

            // --max-bump-for or a policy rule can lower or raise the cap for this package specifically.
            var (packageBump, bumpSource, policyRule) = ResolveBump(packageReference.Key, maxBump, bumpOverrides, policyRules);

            // Baseline instead of a version filter: the walk collects the highest compatible version
            // per bump class in one pass, so the cap picks from that set rather than constraining the
            // fetch. That is what lets the picker offer alternatives, and it guarantees every option
            // has passed the target framework check.
            var request = new PackageVersionRequest {
                PackageId = packageReference.Key,
                AllowPrerelease = includePrerelease,
                CompatibleTargetFrameworks = downloadOnly ? Array.Empty<string>() : SelectCompatibleTargetFrameworks(skipTfmCheck, packageReference.Value),
                Baseline = currentMin
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
            if (result.Candidates is not { } candidates || candidates.IsEmpty) {
                _console.WriteInfo($"No compatible version found for {packageReference.Key} {packageReference.Value.Tfm}.");
                return;
            }

            candidatesPerPackage[packageReference.Key] = candidates;
            if (candidates.Current?.Dependencies is { } currentManifest) currentManifests[packageReference.Key] = currentManifest;

            var target = CandidateForBump(candidates, packageBump);
            var highest = candidates.Highest;

            // Anything above the cap is reported as held, whether or not the cap itself yielded an
            // update. Unlike before, this version has been target framework checked, so releasing it
            // in the picker is safe.
            if (highest is not null && NuGetVersion.TryParse(highest.Version, out var highestVer) && highestVer > currentMin
                && (target is null || !string.Equals(target.Version, highest.Version, StringComparison.OrdinalIgnoreCase))) {
                heldPerPackage[packageReference.Key] = (currentMin, highest.Version);
                heldBy[packageReference.Key] = bumpSource;
                if (policyRule is not null) {
                    var reason = string.IsNullOrWhiteSpace(policyRule.Reason) ? string.Empty : $" ({policyRule.Reason})";
                    _console.WriteInfo($"{packageReference.Key} held at {packageBump.ToString().ToLowerInvariant()} by policy '{policyRule.Match}'{reason}.");
                }
            }

            if (target is null) {
                _console.WriteDebug($"No version within the bump cap for {packageReference.Key}; newest compatible is {highest?.Version}.");
                return;
            }
            if (!NuGetVersion.TryParse(target.Version, out var latestVer)) {
                _console.WriteInfo($"Failed to parse version for {packageReference.Key}: {target.Version}");
                return;
            }
            if (currentMin >= latestVer) {
                _console.WriteDebug($"Package {packageReference.Key} is up to date ({currentMin} >= {latestVer})");
                return;
            }

            outdatedPerPackage.AddOrUpdate(
                packageReference.Key,
                key => (currentMin, latestVer),
                (key, existing) => {
                    // Always keep the lowest currentMin and highest Latest
                    var minCurrent = existing.CurrentMin < currentMin ? existing.CurrentMin : currentMin;
                    var maxLatest = existing.Latest > latestVer ? existing.Latest : latestVer;
                    return (minCurrent, maxLatest);
                }
            );
            // The dependency manifest differs per version, and the conflict check has to see the one
            // belonging to the target actually proposed.
            packageMetadata[packageReference.Key] = result with { Dependencies = target.Dependencies };
        });
        _console.WriteInfo($"Fetched metadata for {allPackageReferences.Count} package(s) in {metadataWatch.Elapsed}");

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

        // With --max-bump-for or policy rules in play there is no single cap left to name, so the
        // messages describe it generically instead of quoting a level that only applies to some packages.
        var capDescription = bumpOverrides.Count > 0 || policyRules.Count > 0
            ? "the bump cap"
            : $"--max-bump {maxBump.ToString().ToLowerInvariant()}";

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
                if (heldPerPackage.ContainsKey(depId)) return $"capped by {capDescription}";
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
                currentPins,
                currentManifests,
                (pickerId, pickerTarget, blockerId, blockerVersion, range, _) => {
                    var action = allowConflicts
                        ? "Updating anyway (--allow-conflicts)."
                        : $"Holding {pickerId} back.";
                    _console.WriteWarning(
                        $"{pickerId} {pickerTarget} breaks {blockerId}, which stays at {blockerVersion} ({HoldReason(blockerId, false)}) and requires {pickerId} {range}. {action}");
                    return allowConflicts ? ConflictChoice.AcceptRisk : ConflictChoice.SkipPicker;
                });

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

        if (interactive && (outdatedPerPackage.Count > 0 || !heldPerPackage.IsEmpty)) {
            _console.WriteRule("[bold yellow]Interactive update selection[/]");

            // Packages the cap held back are rows too, with only the version above the cap to offer.
            // They start unchecked; taking one is the same decision as the old "release individually"
            // prompt, just reachable with the same keys as everything else.
            var pickerInput = new Dictionary<string, (NuGetVersion CurrentMin, NuGetVersion Latest)>(outdatedPerPackage, StringComparer.OrdinalIgnoreCase);
            var heldOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, entry) in heldPerPackage) {
                if (pickerInput.ContainsKey(id)) continue;
                if (!NuGetVersion.TryParse(entry.Held, out var heldVersion)) continue;
                pickerInput[id] = (entry.CurrentMin, heldVersion);
                heldOnly.Add(id);
            }

            var model = BuildPickerModel(pickerInput, grouping, preselect, candidatesPerPackage, heldOnly, policyRules);
            var pickerGroups = model.Groups;
            var preselectedCount = model.Groups.SelectMany(g => g.Rows).Count(r => r.Preselected);
            var outcome = _console.RunPicker(model, PickerTitle(pickerInput.Count, heldOnly.Count, preselectedCount, capDescription));
            if (outcome.Cancelled) {
                _console.WriteLine("Nothing written.");
                stopwatch.Stop();
                errorSink.WriteTo();
                return ExitCode(errorSink, evaluationFailures, metadataFailures);
            }

            // Policy edits made in the picker are written before anything else happens, so they
            // survive even if the run stops at a conflict prompt or a failed write.
            if (outcome.PolicyChanges.Count > 0) {
                if (policies is null) {
                    _console.WriteWarning($"{outcome.PolicyChanges.Count} policy change(s) from the picker were not saved: no policy store.");
                }
                else {
                    foreach (var (match, level) in outcome.PolicyChanges) {
                        if (level is { } cap) policies.Set(match, cap, "set in bld outdated --interactive");
                        else policies.Remove(match);
                    }
                    policies.Save();
                    _console.WriteLine($"Saved {outcome.PolicyChanges.Count} policy change(s) to {policies.FilePath}.");
                    if (!ignorePolicy) policyRules = policies.Rules;
                }
            }

            // Whatever the picker settled on replaces what the cap proposed, including targets above
            // it. The dependency manifest has to follow the chosen version, not the default one.
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chosenTargets = new Dictionary<string, NuGetVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, target) in outcome.Selected) {
                accepted.Add(id);
                chosenTargets[id] = target;
                var current = pickerInput[id].CurrentMin;
                outdatedPerPackage[id] = (current, target);
                // A held version stays in the report unless the chosen target reaches it: taking the
                // in-cap update does not make the newer major go away.
                if (heldPerPackage.TryGetValue(id, out var held) && NuGetVersion.TryParse(held.Held, out var heldVersion) && target >= heldVersion) {
                    heldPerPackage.TryRemove(id, out _);
                }
                if (candidatesPerPackage.TryGetValue(id, out var packageCandidates)
                    && CandidateWithVersion(packageCandidates, target) is { } candidate) {
                    packageMetadata[id] = new PackageVersionResult {
                        PackageId = id,
                        TargetFrameworkVersions = candidate.TargetFrameworkVersions,
                        Dependencies = candidate.Dependencies,
                        Candidates = packageCandidates
                    };
                }
            }

            var picks = ResolveInteractivePicks(
                accepted,
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
                currentPins,
                currentManifests,
                (pickerId, pickerTarget, blockerId, blockerVersion, range, canInclude) => {
                    _console.WriteWarning(
                        $"{pickerId} {pickerTarget} breaks {blockerId}, which stays at {blockerVersion} and requires {pickerId} {range}.");
                    // Including the blocker's own update may lift the bound; whether it does is
                    // checked against that update's manifest in the next pass.
                    if (canInclude && _console.Confirm($"  Include {blockerId} update too?", defaultValue: true)) return ConflictChoice.IncludeDep;
                    if (_console.Confirm($"  Skip {pickerId} instead?", defaultValue: false)) return ConflictChoice.SkipPicker;
                    return ConflictChoice.AcceptRisk;
                });

            var dropped = 0;
            foreach (var id in outdatedPerPackage.Keys.ToList()) {
                if (!picks.Contains(id)) {
                    outdatedPerPackage.TryRemove(id, out _);
                    heldPerPackage.TryRemove(id, out _);
                    dropped++;
                }
            }
            _console.WriteInfo($"Interactive selection: {picks.Count} package(s) selected, {dropped} skipped.");

            if (picks.Count > 0) {
                // A target other than the one the cap would pick needs a per-package override to
                // be reproducible, whether it lies above or below the cap.
                var overrides = new Dictionary<string, BumpKind>(StringComparer.OrdinalIgnoreCase);
                foreach (var id in picks) {
                    if (!chosenTargets.TryGetValue(id, out var target)) continue;
                    if (!pickerInput.TryGetValue(id, out var versions)) continue;
                    candidatesPerPackage.TryGetValue(id, out var packageCandidates);
                    // Against the policies as they are now saved: a rule set in this picker applies to the repeated run.
                    if (RepeatOverride(versions.CurrentMin, target, packageCandidates, ResolveBump(id, maxBump, bumpOverrides, policyRules).Level) is { } bump) overrides[id] = bump;
                }
                _console.WriteLine("To repeat without prompts:");
                _console.WriteLine("  " + BuildRepeatCommand(rootPath, pickerGroups, picks, bumpOverrides, overrides, maxBump, includePrerelease, sources));
            }
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
            // Only an explicit --group-by groups the report; without it the output stays byte-identical.
            var reportGroups = grouping.GroupReport && grouping.GroupBy != GroupBy.None
                ? BuildReportGroups(reportRows, grouping)
                : null;
            var rowsById = reportRows.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
            var rendered = reportGroups is null
                ? reportRows.Select(row => (Group: (string?)null, Row: row)).ToList()
                : reportGroups.SelectMany(g => g.Ids.Select(id => (Group: (string?)g.Name, Row: rowsById[id]))).ToList();

            _console.WriteLine(outdatedPerPackage.Count > 0
                ? $"\nFound {outdatedPerPackage.Count} packages with available updates:"
                : $"\nNo updates available within {capDescription}, but newer versions exist:");
            if (_options.MarkdownOutput) {
                var headers = reportGroups is null
                    ? new[] { "PackageId", "Current", "Latest", "Held" }
                    : new[] { "PackageId", "Current", "Latest", "Held", "Group" };
                var rows = rendered
                    .Select(entry => (IReadOnlyList<string?>)(reportGroups is null
                        ? new[] {
                            entry.Row.Id,
                            PlainVersion(entry.Row.CurrentMin),
                            PlainVersion(entry.Row.Latest),
                            entry.Row.Held ?? string.Empty
                        }
                        : new[] {
                            entry.Row.Id,
                            PlainVersion(entry.Row.CurrentMin),
                            PlainVersion(entry.Row.Latest),
                            entry.Row.Held ?? string.Empty,
                            entry.Group ?? string.Empty
                        }));

                MarkdownTableFormatter.Write(_console, "Outdated packages (markdown)", headers, rows);
            }
            else {
                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn(new TableColumn("PackageId").LeftAligned());
                table.AddColumn(new TableColumn("current").LeftAligned());
                table.AddColumn(new TableColumn("latest").LeftAligned());
                table.AddColumn(new TableColumn("held").LeftAligned());

                string? renderedGroup = null;
                foreach (var entry in rendered) {
                    if (reportGroups is not null && entry.Group != renderedGroup) {
                        renderedGroup = entry.Group;
                        table.AddRow($"[grey]{Markup.Escape(renderedGroup ?? string.Empty)}[/]");
                    }
                    table.AddRow(
                        Markup.Escape(entry.Row.Id ?? ""),
                        FormatVersion(entry.Row.CurrentMin, maxMajorLength),
                        GetFormattedVersion(entry.Row.CurrentMin, entry.Row.Latest, maxMajorLength),
                        entry.Row.Held is null ? "" : Markup.Escape(entry.Row.Held)
                    );
                }
                _console.WriteTable(table);
            }

            if (!heldPerPackage.IsEmpty) {
                _console.WriteLine(HeldSummary(heldPerPackage.Keys.Select(id => heldBy.TryGetValue(id, out var s) ? s : BumpSource.Global).ToList(), maxBump));
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
            BeginJournal();

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

            RecordJournal(rootPath, interactive ? "outdated --interactive" : "outdated --apply");

            if (verifyRestore) {
                _console.WriteInfo("\nVerifying the update with dotnet restore...");
                var restoreErrors = await RunRestoreAsync(_console, rootPath, cancellationToken);
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
    internal static async Task<IReadOnlyList<string>> RunRestoreAsync(IConsoleOutput console, string input, CancellationToken cancellationToken) {
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
                console.WriteDebug(e.Data);
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
    /// <summary>Starts collecting edits for the journal; the apply block calls this before it writes.</summary>
    internal void BeginJournal() => _journal = new List<JournalEdit>();

    /// <summary>Adds what a helper changed to the run's journal, once the file is actually on disk.</summary>
    private void Journaled(List<JournalEdit> edits) {
        if (_journal is null || edits.Count == 0) return;
        foreach (var edit in edits) {
            if (!_journal.Contains(edit)) _journal.Add(edit);
        }
    }

    /// <summary>
    /// Stores the run's edits for <c>bld outdated undo</c>. A failure here is a warning: the files
    /// are already written, and losing the undo record must not turn a successful update into an error.
    /// </summary>
    internal void RecordJournal(string input, string defaultCommand) {
        var edits = _journal;
        _journal = null;
        if (edits is null || edits.Count == 0) return;
        try {
            var path = Journal.Write(new JournalEntry(Path.GetFullPath(input), DateTimeOffset.UtcNow, BldVersion, JournalCommand ?? defaultCommand, edits));
            _console.WriteInfo($"Recorded {edits.Count} change(s) for undo: {path}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            _console.WriteWarning($"Could not record the changes for undo: {ex.FormatMessage()}");
        }
    }

    private static int ExitCode(ErrorSink errorSink, int evaluationFailures, int metadataFailures) =>
        errorSink.HasErrors || evaluationFailures > 0 || metadataFailures > 0 ? 1 : 0;

    internal async Task<int> UpdatePropsFileAsync(
        string propsPath,
        IReadOnlyDictionary<string, (string target, string? current)> updates,
        IReadOnlyCollection<string> commentOut,
        CancellationToken cancellationToken) {
        var applied = 0;
        var pending = new List<JournalEdit>();
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
                        pending.Add(new JournalEdit(propsPath, include, EditKind.OrphanComment, serialized, null));
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
                        pending.Add(new JournalEdit(propsPath, include, EditKind.PackageVersion, currentValue, newVersion.target));
                        changed = true;
                        applied++;
                    }
                }
                return changed;
            }, cancellationToken);
            Journaled(pending);
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
            var pending = new List<JournalEdit>();
            var written = await XmlProjectFile.EditAsync(projectPath, doc => {
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

                    pending.Add(new JournalEdit(projectPath, element.Attribute("Include")!.Value, useOverride ? EditKind.VersionOverride : EditKind.PackageReference, currentValue, newVersion.target));
                    changed = true;
                }
                return changed;
            }, cancellationToken);
            if (written) Journaled(pending);
            return written;
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
            var pending = new List<JournalEdit>();
            var written = await XmlProjectFile.EditAsync(projectPath, doc => {
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

                    var previous = parts[index];
                    parts[index] = $"[{target}]";
                    var updated = string.Join(";", parts);
                    if (updated == value) continue;

                    if (versionAttr is { }) versionAttr.Value = updated;
                    else versionElement!.Value = updated;
                    pending.Add(new JournalEdit(projectPath, element.Attribute("Include")!.Value, EditKind.PackageDownload, previous, parts[index]));
                    changed = true;
                }
                return changed;
            }, cancellationToken);
            if (written) Journaled(pending);
            return written;
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

/// <summary>Which rows the interactive picker starts out with a check mark on.</summary>
internal enum Preselect {
    /// <summary>Everything, as before the option existed.</summary>
    All,
    /// <summary>Everything except major bumps.</summary>
    NoMajor,
    /// <summary>Patch bumps only.</summary>
    Patch,
    /// <summary>Nothing.</summary>
    None,
}

/// <summary>One version a package can be moved to, and the size of that step.</summary>
internal sealed record PickerTarget(NuGetVersion Version, BumpKind Bump);

/// <summary>
/// A package in the picker with every target the feeds offer for it, ordered ascending.
/// <paramref name="DefaultTarget"/> is the one the bump cap picked; the user can move along the list
/// without another lookup.
/// </summary>
/// <param name="PolicyMatch">The match of the policy rule that applies to this package, if any.</param>
/// <param name="PolicyLevel">The cap that rule imposes.</param>
/// <param name="Note">Grey text after the row, e.g. why an undo row cannot be taken.</param>
/// <param name="Locked">The row is shown but cannot be selected.</param>
/// <param name="NowLabel">Drawn in place of <paramref name="Current"/> when set, for a state that is not a version.</param>
internal sealed record PickerRow(string Id, NuGetVersion Current, IReadOnlyList<PickerTarget> Targets, int DefaultTarget, bool Preselected, string? PolicyMatch = null, MaxBump? PolicyLevel = null, string? Note = null, bool Locked = false, string? NowLabel = null) {
    internal PickerTarget Default => Targets[DefaultTarget];
    internal NuGetVersion Target => Default.Version;
    internal BumpKind Bump => Default.Bump;
    internal string Now => NowLabel ?? Current.ToString();
}

internal sealed record PickerGroup(string Name, IReadOnlyList<PickerRow> Rows);

/// <summary>What the picker is choosing: updates to apply, or journal edits to revert.</summary>
internal enum PickerMode {
    Update,
    /// <summary>One fixed target per row, no bump classes, no policies; keys that change a target do nothing.</summary>
    Revert,
}

internal sealed record PickerModel(IReadOnlyList<PickerGroup> Groups, PickerMode Mode = PickerMode.Update);

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
