# Recursive NuGet Dependency Graph Feature

This document describes the new recursive NuGet dependency graph functionality added to the `bld` tool, which was implemented based on the existing `NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync` logic.

## Overview

The new dependency graph feature recursively enumerates packages in `DependencyGroups` using version ranges to determine all transitively referenced packages. It provides both a hierarchical graph structure and a flat list of all packages in the dependency tree.

## Key Features

### Efficient Caching
- **Request Caching**: Packages are cached to avoid duplicate NuGet API requests
- **Pre-population**: Existing package references from `OutdatedService` are pre-cached to minimize redundant network calls
- **Smart Deduplication**: The same package referenced multiple times is fetched only once

### Graph Structure
- **Tree Representation**: `DependencyGraphNode` objects form a hierarchical tree
- **Flat List**: All packages are also provided in a flattened `PackageReference` collection
- **Metadata Rich**: Each package includes version, target framework, depth, prerelease status, and more

### Performance Optimizations
- **Parallel Processing**: Uses `Parallel.ForEachAsync` for concurrent package resolution
- **Depth Limiting**: Configurable maximum depth to prevent infinite traversal
- **Cycle Detection**: Built-in cycle detection to handle circular dependencies

## Architecture

### Core Components

#### 1. `DependencyGraphModels.cs`
Contains the data models for representing dependency graphs:

```csharp
// Represents a single node in the dependency tree
internal record DependencyGraphNode {
    public string PackageId { get; init; }
    public string Version { get; init; }
    public string TargetFramework { get; init; }
    public IReadOnlyList<DependencyGraphNode> Dependencies { get; init; }
    public int Depth { get; init; }
    // ... more properties
}

// Complete dependency graph with both tree and flat representations
internal record PackageDependencyGraph {
    public IReadOnlyList<DependencyGraphNode> RootPackages { get; init; }
    public IReadOnlyList<PackageReference> AllPackages { get; init; }
    public IReadOnlyList<UnresolvedPackage> UnresolvedPackages { get; init; }
    // ... analysis properties
}
```

#### 2. `RecursiveDependencyResolver.cs`
The core service that performs recursive dependency resolution:

```csharp
internal class RecursiveDependencyResolver {
    // Resolves all transitive dependencies with caching and cycle detection
    public async Task<PackageDependencyGraph> ResolveTransitiveDependenciesAsync(
        IEnumerable<string> rootPackageIds,
        DependencyResolutionOptions options,
        Dictionary<string, OutdatedService.PackageInfoContainer>? existingPackageReferences = null,
        CancellationToken cancellationToken = default)
}
```

#### 3. `DependencyGraphService.cs`
High-level service that orchestrates dependency graph building and analysis:

```csharp
internal class DependencyGraphService {
    // Builds comprehensive dependency graph from OutdatedService package references
    public async Task<PackageDependencyGraph> BuildDependencyGraphAsync(
        Dictionary<string, OutdatedService.PackageInfoContainer> allPackageReferences,
        bool includePrerelease = false,
        int maxDepth = 5,
        CancellationToken cancellationToken = default)
        
    // Analyzes the graph for patterns and statistics
    public DependencyGraphAnalysis AnalyzeDependencyGraph(PackageDependencyGraph graph)
}
```

#### 4. `OutdatedServiceExtensions.cs`
Extension methods that integrate with the existing `OutdatedService`:

```csharp
// Extension method for Dictionary<string, PackageInfoContainer>
public static async Task<PackageDependencyGraph> BuildAndShowDependencyGraphAsync(
    this Dictionary<string, OutdatedService.PackageInfoContainer> allPackageReferences,
    IConsoleOutput console,
    bool includePrerelease = false,
    int maxDepth = 5,
    bool showAnalysis = true,
    CancellationToken cancellationToken = default)
```

## Usage Examples

### Basic Usage in OutdatedService

The new functionality integrates seamlessly with the existing `OutdatedService`:

```csharp
// In OutdatedService.cs - new method added
public async Task<int> BuildDependencyGraphAsync(
    string rootPath, 
    bool includePrerelease = false, 
    int maxDepth = 5, 
    bool showAnalysis = true,
    string? exportPath = null,
    CancellationToken cancellationToken = default)
{
    // ... discover packages (similar to CheckOutdatedPackagesAsync)
    
    // Build dependency graph using extension method
    var dependencyGraph = await allPackageReferences.BuildAndShowDependencyGraphAsync(
        _console, 
        includePrerelease, 
        maxDepth, 
        showAnalysis, 
        cancellationToken);
        
    // Export if requested
    if (!string.IsNullOrEmpty(exportPath)) {
        await dependencyGraph.ExportDependencyGraphAsync(exportPath, "json", _console);
    }
    
    return 0;
}
```

### Direct Usage

You can also use the components directly:

```csharp
// Create resolver
var options = new NugetMetadataOptions();
using var httpClient = NugetMetadataService.CreateHttpClient(options);
var resolver = new RecursiveDependencyResolver(httpClient, options, console);

// Configure resolution
var resolutionOptions = new DependencyResolutionOptions {
    MaxDepth = 5,
    AllowPrerelease = false,
    TargetFrameworks = new[] { "net8.0" }
};

// Resolve dependencies
var dependencyGraph = await resolver.ResolveTransitiveDependenciesAsync(
    rootPackageIds, 
    resolutionOptions, 
    existingPackageReferences, // Pre-populate cache
    cancellationToken);

// Analyze results
var graphService = new DependencyGraphService(console);
var analysis = graphService.AnalyzeDependencyGraph(dependencyGraph);
```

## Integration with Existing Code

### How it leverages GetLatestVersionWithFrameworkCheckAsync

The implementation reuses the existing NuGet metadata retrieval logic:

1. **Same API Calls**: Uses `NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync` for all package lookups
2. **Framework Compatibility**: Leverages the same framework compatibility logic with `FrameworkReducer` and `DefaultCompatibilityProvider`
3. **Dependency Groups**: Recursively processes the `Dependencies` property from `PackageVersionResult.Dependencies`
4. **Version Ranges**: Respects version range constraints from `Dependency.Range` when resolving child packages

### Cache Integration with OutdatedService

The resolver intelligently integrates with `OutdatedService.allPackageReferences`:

```csharp
// Pre-populate cache with existing package references
private async Task PrePopulateCacheAsync(
    Dictionary<string, OutdatedService.PackageInfoContainer> existingPackageReferences,
    DependencyResolutionOptions options,
    CancellationToken cancellationToken)
{
    // For each existing package, fetch and cache its metadata
    // This ensures packages already discovered by OutdatedService are not fetched again
}
```

## Output and Analysis

### Console Output
The functionality provides rich console output including:
- Progress indicators during resolution
- Summary tables showing package counts and statistics
- Dependency analysis with most common packages
- Version conflict detection and reporting
- Performance metrics

### Export Formats
Dependency graphs can be exported in multiple formats:
- **JSON**: Complete graph structure with all metadata
- **CSV**: Flat list of packages with key properties
- **DOT**: GraphViz format for visualization

### Analysis Features
- **Package Distribution**: Microsoft vs third-party package breakdown
- **Depth Analysis**: Distribution of packages by dependency depth
- **Common Dependencies**: Most frequently referenced packages across the tree
- **Version Conflicts**: Detection of packages with multiple versions
- **Unresolved Packages**: Tracking of packages that couldn't be resolved

## Performance Characteristics

### Efficient Network Usage
- **Caching**: Aggressive caching prevents duplicate API calls
- **Parallel Processing**: Concurrent resolution of independent packages
- **Pre-population**: Reuses existing OutdatedService lookups

### Memory Efficiency
- **Deduplication**: Packages appearing multiple times are stored once in the flat list
- **Streaming**: Processes packages as they're discovered rather than loading everything upfront
- **Disposal**: Proper disposal of HTTP clients and resources

### Scalability
- **Depth Limiting**: Prevents runaway recursion in complex dependency trees
- **Cycle Detection**: Handles circular dependencies gracefully
- **Configurable Limits**: Adjustable parallelism and depth limits

## Error Handling

The implementation includes comprehensive error handling:
- **Network Failures**: Graceful handling of API timeouts and failures
- **Invalid Packages**: Tracking of packages that cannot be resolved
- **Version Conflicts**: Detection and reporting without stopping resolution
- **Circular Dependencies**: Cycle detection with configurable behavior

## Future Enhancements

Potential areas for future improvement:
- **Caching Persistence**: Save cache to disk for subsequent runs
- **Incremental Updates**: Only resolve changed packages
- **Visualization**: Built-in graph visualization capabilities
- **Conflict Resolution**: Automatic resolution of version conflicts
- **Policy Engine**: Configurable policies for dependency selection

## Testing

Basic unit tests are provided in `RecursiveDependencyResolverTests.cs`:
- Resolution of simple packages
- Depth limiting validation  
- Multiple root package handling
- Cache effectiveness verification

Integration tests could be added to validate:
- Real-world dependency trees
- Performance characteristics
- Export functionality
- Analysis accuracy