namespace bld.Models;

/// <summary>
/// Represents a project reference dependency
/// </summary>
internal record ProjectReference {
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public bool IsResolved { get; init; } = true;
}

/// <summary>
/// Represents a NuGet package reference dependency
/// </summary>
internal record PackageReference {
    public string PackageId { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public bool IsPrivateAssets { get; init; } = false;
}

/// <summary>
/// Represents a project and its dependencies
/// </summary>
internal record DependencyInfo {
    public string ProjectPath { get; init; } = string.Empty;
    public string ProjectName { get; init; } = string.Empty;
    public string TargetFramework { get; init; } = string.Empty;
    public IReadOnlyList<ProjectReference> ProjectReferences { get; init; } = Array.Empty<ProjectReference>();
    public IReadOnlyList<PackageReference> PackageReferences { get; init; } = Array.Empty<PackageReference>();
}

/// <summary>
/// Represents a node in the dependency tree
/// </summary>
internal class DependencyNode {
    public DependencyInfo DependencyInfo { get; set; } = null!;
    public List<DependencyNode> ProjectDependencies { get; set; } = new();
    public List<PackageReference> PackageDependencies { get; set; } = new();
    
    // To prevent circular dependencies during tree traversal
    public bool IsVisiting { get; set; } = false;
}