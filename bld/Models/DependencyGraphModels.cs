using NuGet.Versioning;
using bld.Services.NuGet;
using NuGet.Frameworks;

namespace bld.Models;

/// <summary>
/// Represents a node in the dependency graph containing package information and its dependencies
/// </summary>
internal record DependencyGraphNode {
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string TargetFramework { get; init; }
    public bool IsPrerelease { get; init; }
    public DateTime RetrievedAt { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Direct dependencies of this package
    /// </summary>
    public IReadOnlyList<DependencyGraphNode> Dependencies { get; init; } = [];
    
    /// <summary>
    /// The dependency group information used to resolve this node
    /// </summary>
    public DependencyGroup? DependencyGroup { get; init; }
    
    /// <summary>
    /// Version range constraint from parent (if this node is a dependency)
    /// </summary>
    public string? VersionRange { get; init; }
    
    /// <summary>
    /// Depth in the dependency tree (0 = root package)
    /// </summary>
    public int Depth { get; init; }
}

/// <summary>
/// Represents the complete dependency graph with both tree structure and flat list
/// </summary>
internal record PackageDependencyGraph {
    /// <summary>
    /// Root packages (packages directly referenced by projects)
    /// </summary>
    public IReadOnlyList<DependencyGraphNode> RootPackages { get; init; } = [];
    
    /// <summary>
    /// Flat list of all packages found in the dependency tree (including roots)
    /// </summary>
    public IReadOnlyList<PackageReference> AllPackages { get; init; } = [];
    
    /// <summary>
    /// Packages that were requested but could not be resolved
    /// </summary>
    public IReadOnlyList<UnresolvedPackage> UnresolvedPackages { get; init; } = [];
    
    /// <summary>
    /// Total number of unique packages resolved
    /// </summary>
    public int TotalPackageCount => AllPackages.Count;
    
    /// <summary>
    /// Maximum depth of the dependency tree
    /// </summary>
    public int MaxDepth { get; init; }
}

/// <summary>
/// Represents a package reference with metadata for the flat list
/// </summary>
internal record PackageReference {
    public required string PackageId { get; init; }
    public required string Version { get; init; }
    public required string TargetFramework { get; init; }
    public bool IsPrerelease { get; init; }
    public bool IsRootPackage { get; init; }
    public int Depth { get; init; }
    public string? VersionRange { get; init; }
    public DateTime RetrievedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Represents a package that could not be resolved
/// </summary>
internal record UnresolvedPackage {
    public required string PackageId { get; init; }
    public string? VersionRange { get; init; }
    public required NuGetFramework TargetFramework { get; init; }
    public required string Reason { get; init; }
    public int Depth { get; init; }
}

/// <summary>
/// Options for dependency graph resolution
/// </summary>
internal record DependencyResolutionOptions {
    /// <summary>
    /// Maximum depth to traverse in the dependency tree (default: 10)
    /// </summary>
    public int MaxDepth { get; init; } = 10;
    
    /// <summary>
    /// Whether to include prerelease packages in resolution
    /// </summary>
    public bool AllowPrerelease { get; init; }
    
    /// <summary>
    /// Cache expiration time for package lookups
    /// </summary>
    public TimeSpan CacheExpiration { get; init; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// Whether to stop resolution when a cycle is detected
    /// </summary>
    public bool StopOnCycles { get; init; } = true;
    
    /// <summary>
    /// Target frameworks to resolve dependencies for
    /// </summary>
    public required IReadOnlyList<NuGetFramework> TargetFrameworks { get; init; }
}

/// <summary>
/// Represents vulnerability information for a NuGet package
/// </summary>
internal record PackageVulnerability {
    public required string PackageId { get; init; }
    public required string AffectedVersionRange { get; init; }
    public required string AdvisoryUrl { get; init; }
    public required string Severity { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public DateTime PublishedDate { get; init; }
    public string? CvssScore { get; init; }
}

/// <summary>
/// Enhanced dependency analysis with vulnerability and conflict detection
/// </summary>
internal record EnhancedDependencyAnalysis {
    public int TotalPackages { get; init; }
    public int ExplicitPackages { get; init; }
    public int TransitivePackages { get; init; }
    public int MaxDepth { get; init; }
    public int UnresolvedPackages { get; init; }
    public int MicrosoftPackages { get; init; }
    public int ThirdPartyPackages { get; init; }
    public int VulnerablePackages { get; init; }
    
    public IReadOnlyList<DependencyFrequency> MostCommonDependencies { get; init; } = [];
    public IReadOnlyDictionary<int, int> PackagesByDepth { get; init; } = new Dictionary<int, int>();
    public IReadOnlyList<VersionConflict> VersionConflicts { get; init; } = [];
    public IReadOnlyList<VersionIncompatibility> VersionIncompatibilities { get; init; } = [];
    public IReadOnlyList<PackageVulnerability> Vulnerabilities { get; init; } = [];
}

/// <summary>
/// Represents a version incompatibility where package versions may not work together
/// </summary>
internal record VersionIncompatibility {
    public required string PackageId { get; init; }
    public required IReadOnlyList<string> IncompatibleVersions { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Enhanced package reference with vulnerability and conflict information
/// </summary>
internal record EnhancedPackageReference : PackageReference {
    /// <summary>
    /// Whether this package is explicitly referenced (vs. transitive)
    /// </summary>
    public bool IsExplicit { get; init; }
    
    /// <summary>
    /// Vulnerability information for this package
    /// </summary>
    public IReadOnlyList<PackageVulnerability> Vulnerabilities { get; init; } = [];
    
    /// <summary>
    /// Version conflicts this package participates in
    /// </summary>
    public IReadOnlyList<string> ConflictingVersions { get; init; } = [];
    
    /// <summary>
    /// Whether this package has any security vulnerabilities
    /// </summary>
    public bool HasVulnerabilities => Vulnerabilities.Any();
    
    /// <summary>
    /// Whether this package has version conflicts
    /// </summary>
    public bool HasVersionConflicts => ConflictingVersions.Any();
}