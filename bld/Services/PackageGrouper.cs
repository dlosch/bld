using NuGet.Versioning;

namespace bld.Services;

/// <summary>
/// How the interactive picker (and, on request, the report) groups packages.
/// </summary>
internal enum GroupBy {
    /// <summary>One flat list, as before grouping existed.</summary>
    None,
    /// <summary>Longest common id prefix that covers at least <c>--group-min</c> packages.</summary>
    Prefix,
    /// <summary>Three groups: patch, minor, major.</summary>
    Bump,
}

/// <summary>
/// Size of the step from the version a package is pinned at now to its update target.
/// </summary>
internal enum BumpKind {
    Patch,
    Minor,
    Major,
}

/// <summary>Grouping knobs, passed around as one value because they only ever travel together.</summary>
internal sealed record GroupingOptions(GroupBy GroupBy, int Depth, int MinSize, bool GroupReport) {
    internal static GroupingOptions Default { get; } = new(GroupBy.Prefix, 2, 2, false);
}

internal sealed record PackageGroup(string Name, IReadOnlyList<string> Ids);

/// <summary>
/// Pure grouping of package ids, kept free of Spectre so the picker layout is testable.
/// </summary>
internal static class PackageGrouper {
    internal const string OtherGroupName = "(other)";

    internal static BumpKind Classify(NuGetVersion current, NuGetVersion target) {
        if (target.Major != current.Major) return BumpKind.Major;
        if (target.Minor != current.Minor) return BumpKind.Minor;
        // Everything else - patch, revision, and a pure prerelease-to-release step - is a patch.
        return BumpKind.Patch;
    }

    /// <summary>
    /// Groups by the longest common id prefix that covers at least <paramref name="minSize"/>
    /// packages, so a release train like Microsoft.Extensions.* can be toggled in one keystroke.
    /// </summary>
    /// <param name="depth">Maximum prefix length in dot-separated segments.</param>
    /// <param name="minSize">Smallest group that is worth forming.</param>
    internal static IReadOnlyList<PackageGroup> GroupByPrefix(IEnumerable<string> ids, int depth = 2, int minSize = 2) {
        var all = ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (all.Count == 0) return Array.Empty<PackageGroup>();

        // Candidate prefixes are the first 1..depth segments of every id, plus an id that is itself a
        // proper prefix of another one (Serilog for Serilog.Sinks.File) - otherwise a package like
        // Serilog, which has no segment prefix of its own, could never join its family.
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in all) {
            var segments = id.Split('.');
            for (var n = 1; n <= Math.Min(segments.Length - 1, depth); n++) {
                counts.TryAdd(string.Join('.', segments.Take(n)), 0);
            }
            if (segments.Length <= depth && all.Any(other => other.Length > id.Length && IsUnder(other, id))) {
                counts.TryAdd(id, 0);
            }
        }

        foreach (var prefix in counts.Keys.ToList()) {
            counts[prefix] = all.Count(id => IsUnder(id, prefix));
        }

        var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<PackageGroup>();
        foreach (var prefix in counts.Keys
                     .OrderByDescending(p => p.Count(c => c == '.'))
                     .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            if (counts[prefix] < minSize) continue;
            var members = all.Where(id => !assigned.Contains(id) && IsUnder(id, prefix)).ToList();
            // Longer prefixes already claimed most of this one's packages. Forming it anyway would
            // produce a group below --group-min, so leave the rest to the next shorter prefix.
            if (members.Count < minSize) continue;
            foreach (var member in members) assigned.Add(member);
            groups.Add(new PackageGroup(prefix + ".*", members));
        }

        var result = groups.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var rest = all.Where(id => !assigned.Contains(id)).ToList();
        if (rest.Count > 0) result.Add(new PackageGroup(OtherGroupName, rest));
        return result;
    }

    /// <summary>
    /// Groups by bump class - "take all patches, look at the minors, leave the majors for later".
    /// Empty classes are omitted.
    /// </summary>
    internal static IReadOnlyList<PackageGroup> GroupByBump(IEnumerable<(string Id, NuGetVersion Current, NuGetVersion Target)> rows) {
        var byKind = new Dictionary<BumpKind, List<string>>();
        foreach (var row in rows) {
            var kind = Classify(row.Current, row.Target);
            if (!byKind.TryGetValue(kind, out var members)) byKind[kind] = members = new List<string>();
            members.Add(row.Id);
        }

        var result = new List<PackageGroup>();
        foreach (var kind in new[] { BumpKind.Patch, BumpKind.Minor, BumpKind.Major }) {
            if (!byKind.TryGetValue(kind, out var members)) continue;
            members.Sort(StringComparer.OrdinalIgnoreCase);
            result.Add(new PackageGroup(kind.ToString().ToLowerInvariant(), members));
        }
        return result;
    }

    /// <summary>Whether <paramref name="id"/> is <paramref name="prefix"/> itself or sits below it.</summary>
    private static bool IsUnder(string id, string prefix) {
        if (id.Length == prefix.Length) return string.Equals(id, prefix, StringComparison.OrdinalIgnoreCase);
        return id.Length > prefix.Length
            && id[prefix.Length] == '.'
            && id.AsSpan(0, prefix.Length).Equals(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
