namespace bld.Models;

/// <summary>
/// Information about a NuGet package reference
/// </summary>
public record NugetPackageInfo {
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public NugetPackageCategory Category { get; init; }
    public string? ProjectPath { get; init; }
    public string? WhitelistMatch { get; init; }
    public string? BlacklistMatch { get; init; }
    public string? MicrosoftMatch { get; init; }
    public string? TrustedMatch { get; init; }
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
    public Version Version { get; init; } = new Version();

    /// <summary>
    /// Check if a version satisfies this constraint
    /// </summary>
    public bool IsSatisfiedBy(Version version) {
        var comparison = version.CompareTo(Version);
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
public enum NugetPackageCategory {
    MicrosoftOfficial,      // Official .NET packages (System.*, Microsoft.Extensions.*, etc.)
    MicrosoftNonOfficial,   // Microsoft packages that are not official .NET
    TrustedThirdParty,      // Known trusted packages (high download count or whitelisted)
    Other                   // Everything else
}

/// <summary>
/// Analysis results for a single project
/// </summary>
public record ProjectNugetAnalysis {
    public string ProjectPath { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public IReadOnlyList<NugetPackageInfo> Packages { get; init; } = Array.Empty<NugetPackageInfo>();

    public IEnumerable<NugetPackageInfo> MicrosoftOfficialPackages =>
        Packages.Where(p => p.Category == NugetPackageCategory.MicrosoftOfficial);

    public IEnumerable<NugetPackageInfo> MicrosoftNonOfficialPackages =>
        Packages.Where(p => p.Category == NugetPackageCategory.MicrosoftNonOfficial);

    public IEnumerable<NugetPackageInfo> TrustedThirdPartyPackages =>
        Packages.Where(p => p.Category == NugetPackageCategory.TrustedThirdParty);

    public IEnumerable<NugetPackageInfo> OtherPackages =>
        Packages.Where(p => p.Category == NugetPackageCategory.Other);
}

/// <summary>
/// Represents an aggregated package occurrence across multiple projects
/// </summary>
public record PackageOccurrence {
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
}