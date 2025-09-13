using bld.Infrastructure;
using bld.Models;
using NuGet.Frameworks;

namespace bld.Services.NuGet;

/// <summary>
/// Service for building comprehensive NuGet dependency graphs from project package references
/// </summary>
internal class DependencyGraphService {
    private readonly IConsoleOutput _console;
    private readonly NugetMetadataOptions _options;
    
    public DependencyGraphService(IConsoleOutput console, NugetMetadataOptions? options = null) {
        _console = console ?? throw new ArgumentNullException(nameof(console));
        _options = options ?? new NugetMetadataOptions();
    }

    /// <summary>
    /// Builds a complete dependency graph from package references discovered by OutdatedService
    /// </summary>
    /// <param name="allPackageReferences">Package references from OutdatedService.CheckOutdatedPackagesAsync</param>
    /// <param name="includePrerelease">Whether to include prerelease packages</param>
    /// <param name="maxDepth">Maximum depth to traverse (default: 5)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Complete dependency graph with both tree and flat representations</returns>
    public async Task<PackageDependencyGraph> BuildDependencyGraphAsync(
        Dictionary<string, OutdatedService.PackageInfoContainer> allPackageReferences,
        bool includePrerelease = false,
        int maxDepth = 5,
        CancellationToken cancellationToken = default) {
        
        ArgumentNullException.ThrowIfNull(allPackageReferences);
        
        _console.WriteInfo($"Building dependency graph for {allPackageReferences.Count} packages...");
        
        // Extract unique target frameworks from all packages
        var targetFrameworks = allPackageReferences.Values
            .SelectMany(container => container.Tfms)
            .Distinct()
            .ToList();
        
        //if (!targetFrameworks.Any()) {
        //    targetFrameworks.Add("net8.0"); // Default fallback
        //}
        
        _console.WriteDebug($"Target frameworks: {string.Join(", ", targetFrameworks)}");
        
        var resolutionOptions = new DependencyResolutionOptions {
            MaxDepth = maxDepth,
            AllowPrerelease = includePrerelease,
            TargetFrameworks = targetFrameworks.Select(tf => new NuGetFramework(tf)).ToList()
        };
        
        using var httpClient = NugetMetadataService.CreateHttpClient(_options);
        var resolver = new RecursiveDependencyResolver(httpClient, _options, _console);
        
        // Get root package IDs
        var rootPackageIds = allPackageReferences.Keys.ToList();
        
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        
        var dependencyGraph = await resolver.ResolveTransitiveDependenciesAsync(
            rootPackageIds,
            resolutionOptions,
            allPackageReferences,
            cancellationToken);
        
        stopwatch.Stop();
        
        _console.WriteInfo($"Dependency graph built in {stopwatch.Elapsed.TotalSeconds:F2} seconds");
        _console.WriteInfo($"Found {dependencyGraph.TotalPackageCount} total packages (max depth: {dependencyGraph.MaxDepth})");
        
        if (dependencyGraph.UnresolvedPackages.Any()) {
            _console.WriteWarning($"{dependencyGraph.UnresolvedPackages.Count} packages could not be resolved:");
            foreach (var unresolved in dependencyGraph.UnresolvedPackages.Take(10)) {
                _console.WriteWarning($"  - {unresolved.PackageId}: {unresolved.Reason}");
            }
            if (dependencyGraph.UnresolvedPackages.Count > 10) {
                _console.WriteWarning($"  ... and {dependencyGraph.UnresolvedPackages.Count - 10} more");
            }
        }
        
        return dependencyGraph;
    }

    /// <summary>
    /// Analyzes the dependency graph for interesting patterns and statistics
    /// </summary>
    public DependencyGraphAnalysis AnalyzeDependencyGraph(PackageDependencyGraph graph) {
        ArgumentNullException.ThrowIfNull(graph);
        
        // Find most common dependencies (packages that appear in many dependency trees)
        var packageFrequency = new Dictionary<string, int>();
        foreach (var package in graph.AllPackages.Where(p => !p.IsRootPackage)) {
            packageFrequency[package.PackageId] = packageFrequency.GetValueOrDefault(package.PackageId, 0) + 1;
        }
        
        var mostCommonDependencies = packageFrequency
            .OrderByDescending(kvp => kvp.Value)
            .Take(10)
            .Select(kvp => new DependencyFrequency { PackageId = kvp.Key, Frequency = kvp.Value })
            .ToList();
        
        // Find packages by depth
        var packagesByDepth = graph.AllPackages
            .GroupBy(p => p.Depth)
            .ToDictionary(g => g.Key, g => g.Count());
        
        // Find Microsoft vs third-party packages
        var microsoftPackages = graph.AllPackages.Where(p => 
            p.PackageId.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            p.PackageId.StartsWith("System.", StringComparison.OrdinalIgnoreCase)).ToList();
        
        var microsoftCount = microsoftPackages.Count;
        var thirdPartyCount = graph.TotalPackageCount - microsoftCount;
        
        // Find potential version conflicts (same package with different versions)
        var versionConflicts = graph.AllPackages
            .GroupBy(p => p.PackageId)
            .Where(g => g.Select(p => p.Version).Distinct().Count() > 1)
            .Select(g => new VersionConflict {
                PackageId = g.Key,
                Versions = g.Select(p => p.Version).Distinct().ToList()
            })
            .ToList();
        
        return new DependencyGraphAnalysis {
            TotalPackages = graph.TotalPackageCount,
            RootPackages = graph.RootPackages.Count,
            MaxDepth = graph.MaxDepth,
            UnresolvedPackages = graph.UnresolvedPackages.Count,
            MicrosoftPackages = microsoftCount,
            ThirdPartyPackages = thirdPartyCount,
            MostCommonDependencies = mostCommonDependencies,
            PackagesByDepth = packagesByDepth,
            VersionConflicts = versionConflicts
        };
    }
}

/// <summary>
/// Analysis results for a dependency graph
/// </summary>
internal record DependencyGraphAnalysis {
    public int TotalPackages { get; init; }
    public int RootPackages { get; init; }
    public int MaxDepth { get; init; }
    public int UnresolvedPackages { get; init; }
    public int MicrosoftPackages { get; init; }
    public int ThirdPartyPackages { get; init; }
    
    public IReadOnlyList<DependencyFrequency> MostCommonDependencies { get; init; } = [];
    public IReadOnlyDictionary<int, int> PackagesByDepth { get; init; } = new Dictionary<int, int>();
    public IReadOnlyList<VersionConflict> VersionConflicts { get; init; } = [];
}

/// <summary>
/// Represents how frequently a dependency appears across different packages
/// </summary>
internal record DependencyFrequency {
    public required string PackageId { get; init; }
    public int Frequency { get; init; }
}

/// <summary>
/// Represents a version conflict where the same package appears with different versions
/// </summary>
internal record VersionConflict {
    public required string PackageId { get; init; }
    public required IReadOnlyList<string> Versions { get; init; }
}