using NuGet.Versioning;

namespace bld.Models;

/// <summary>The MSBuild item a package comes from.</summary>
internal enum PackageItemKind {
    PackageReference,
    /// <summary>Declared in Directory.Packages.props and applied to every project (analyzers, build SDKs).</summary>
    GlobalPackageReference,
    /// <summary>Fetched into the package cache only, with exact bracketed versions; never referenced.</summary>
    PackageDownload,
}

/// <summary>
/// Information about a NuGet package reference
/// </summary>
internal record NugetPackageInfo {
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public NugetPackageCategory Category { get; init; }
    public PackageItemKind Kind { get; init; } = PackageItemKind.PackageReference;
    public string? ProjectPath { get; init; }
    public string? WhitelistMatch { get; init; }
    public string? BlacklistMatch { get; init; }
    public string? MicrosoftMatch { get; init; }
    public string? TrustedMatch { get; init; }

    /// <summary>Resolved through another package (from project.assets.json), not referenced by the project itself.</summary>
    public bool IsTransitive { get; init; }
    /// <summary>Target frameworks the package was resolved for; empty for direct references.</summary>
    public IReadOnlyList<string> TargetFrameworks { get; init; } = Array.Empty<string>();
    /// <summary>Packages that pull this one in (immediate parents); empty for direct references.</summary>
    public IReadOnlyList<string> RequestedBy { get; init; } = Array.Empty<string>();
    /// <summary>Where the package was or would be restored from; null when the NuGet configuration could not be read.</summary>
    public PackageOrigin? Origin { get; init; }
}

/// <summary>
/// The source a package was restored from (as restore recorded it), or, when it is not in a packages
/// folder yet, the configured sources restore would take it from.
/// </summary>
internal sealed record PackageOrigin(string? RestoredFrom, IReadOnlyList<string> Candidates) {
    /// <summary>The URL or path behind each source name used here, for the legend; empty when the name is the URL itself.</summary>
    public IReadOnlyDictionary<string, string> Urls { get; init; } = new Dictionary<string, string>();

    public string Describe() => RestoredFrom
        ?? (Candidates.Count == 0
            ? "not restored: no source mapped"
            : $"not restored: {string.Join(" or ", Candidates)}");
}

/// <summary>
/// Represents a package pattern with optional version constraint
/// </summary>
internal record PackagePattern {
    public string Name { get; init; } = string.Empty;
    public VersionConstraint? VersionConstraint { get; init; }

    /// <summary>
    /// The original pattern string from the configuration file
    /// </summary>
    public string OriginalPattern { get; init; } = string.Empty;
}

/// <summary>
/// Represents a version constraint with operator and version
/// </summary>
internal record VersionConstraint {
    public VersionOperator Operator { get; init; }
    // NuGet's own ordering: a prerelease sorts below its release (2.0.0-beta &lt; 2.0.0), four-part and
    // three-part forms compare as NuGet compares them. System.Version with a hand-rolled prerelease
    // decrement put 2.0.0-beta somewhere in 1.x.
    public NuGetVersion Version { get; init; } = new NuGetVersion(0, 0, 0);

    /// <summary>
    /// Check if a version satisfies this constraint
    /// </summary>
    public bool IsSatisfiedBy(NuGetVersion version) {
        var comparison = VersionComparer.VersionRelease.Compare(version, Version);
        return Operator switch {
            VersionOperator.Equal => comparison == 0,
            VersionOperator.GreaterThanOrEqual => comparison >= 0,
            VersionOperator.LessThanOrEqual => comparison <= 0,
            _ => false
        };
    }
}

/// <summary>
/// Version constraint operators
/// </summary>
internal enum VersionOperator {
    Equal,
    GreaterThanOrEqual,
    LessThanOrEqual
}

/// <summary>
/// Categories for NuGet packages
/// </summary>
internal enum NugetPackageCategory {
    MicrosoftOfficial,      // Official .NET packages (System.*, Microsoft.Extensions.*, etc.)
    MicrosoftNonOfficial,   // Microsoft packages that are not official .NET
    TrustedThirdParty,      // Known trusted packages (high download count or whitelisted)
    Other,                  // Everything else
    Blacklisted             // Matched by the rules file's blacklist (and not by its whitelist), whatever the prefix
}

/// <summary>
/// Analysis results for a single project
/// </summary>
internal record ProjectNugetAnalysis {
    public string ProjectPath { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    /// <summary>Direct references, plus transitive ones when the analysis ran with --transitive.</summary>
    public IReadOnlyList<NugetPackageInfo> Packages { get; init; } = Array.Empty<NugetPackageInfo>();

    public IEnumerable<NugetPackageInfo> DirectPackages => Packages.Where(p => !p.IsTransitive);
    public IEnumerable<NugetPackageInfo> TransitivePackages => Packages.Where(p => p.IsTransitive);

    public IEnumerable<NugetPackageInfo> MicrosoftOfficialPackages =>
        DirectPackages.Where(p => p.Category == NugetPackageCategory.MicrosoftOfficial);

    public IEnumerable<NugetPackageInfo> MicrosoftNonOfficialPackages =>
        DirectPackages.Where(p => p.Category == NugetPackageCategory.MicrosoftNonOfficial);

    public IEnumerable<NugetPackageInfo> TrustedThirdPartyPackages =>
        DirectPackages.Where(p => p.Category == NugetPackageCategory.TrustedThirdParty);

    public IEnumerable<NugetPackageInfo> OtherPackages =>
        DirectPackages.Where(p => p.Category == NugetPackageCategory.Other);

    public IEnumerable<NugetPackageInfo> BlacklistedPackages =>
        DirectPackages.Where(p => p.Category == NugetPackageCategory.Blacklisted);
}

/// <summary>
/// Represents an aggregated package occurrence across multiple projects
/// </summary>
internal record PackageOccurrence {
    public string ProjectName { get; init; } = string.Empty;
    public string ProjectPath { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? WhitelistMatch { get; init; }
    public string? BlacklistMatch { get; init; }
    public string? MicrosoftMatch { get; init; }
    public string? TrustedMatch { get; init; }
}

/// <summary>
/// Represents an aggregated package across multiple projects
/// </summary>
internal record AggregatedPackage {
    public string Name { get; init; } = string.Empty;
    public NugetPackageCategory Category { get; init; }
    public IReadOnlyList<PackageOccurrence> Occurrences { get; init; } = Array.Empty<PackageOccurrence>();
    /// <summary>True when no project references the package directly.</summary>
    public bool IsTransitive { get; init; }
    public IReadOnlyList<string> RequestedBy { get; init; } = Array.Empty<string>();
    public PackageItemKind Kind { get; init; } = PackageItemKind.PackageReference;
    /// <summary>Distinct <see cref="PackageOrigin.Describe"/> texts across the occurrences.</summary>
    public IReadOnlyList<string> Sources { get; init; } = Array.Empty<string>();
}