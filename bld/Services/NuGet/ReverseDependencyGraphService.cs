using bld.Infrastructure;
using bld.Models;

namespace bld.Services.NuGet;

/// <summary>
/// Service for building reverse dependency graphs from forward dependency graphs
/// </summary>
internal sealed class ReverseDependencyGraphService {
    private readonly IConsoleOutput? _console;
    
    public ReverseDependencyGraphService(IConsoleOutput? console) {
        _console = console; // Allow null for testing
    }
    
    /// <summary>
    /// Builds a reverse dependency graph from a forward dependency graph
    /// </summary>
    /// <param name="forwardGraph">The forward dependency graph</param>
    /// <param name="excludeFrameworkPackages">Whether to exclude Microsoft/System/NETStandard packages</param>
    /// <returns>Reverse dependency analysis</returns>
    public ReverseDependencyAnalysis BuildReverseDependencyGraph(
        PackageDependencyGraph forwardGraph,
        bool excludeFrameworkPackages = false) {
        
        ArgumentNullException.ThrowIfNull(forwardGraph);
        
        var reverseMapping = new Dictionary<string, ReverseDependencyNode>();
        var explicitPackages = new HashSet<string>();
        
        // Track explicit (root) packages
        foreach (var rootNode in forwardGraph.RootPackages) {
            explicitPackages.Add(rootNode.PackageId);
        }
        
        // Build reverse mappings from all packages
        foreach (var package in forwardGraph.AllPackages) {
            if (excludeFrameworkPackages && IsFrameworkPackage(package.PackageId)) {
                continue;
            }
            
            // Ensure the package exists in reverse mapping
            if (!reverseMapping.TryGetValue(package.PackageId, out var reverseNode)) {
                reverseNode = new ReverseDependencyNode {
                    PackageId = package.PackageId,
                    Version = package.Version,
                    TargetFramework = package.TargetFramework,
                    IsExplicit = explicitPackages.Contains(package.PackageId),
                    IsFrameworkPackage = IsFrameworkPackage(package.PackageId),
                    DependentPackages = new List<PackageReference>(),
                    DependencyPaths = new List<string>()
                };
                reverseMapping[package.PackageId] = reverseNode;
            }
        }
        
        // Build reverse dependencies by walking the forward graph
        foreach (var rootNode in forwardGraph.RootPackages) {
            BuildReverseMappingsRecursive(rootNode, null, reverseMapping, excludeFrameworkPackages, new List<string>());
        }
        
        // Calculate statistics
        var analysis = new ReverseDependencyAnalysis {
            ReverseNodes = reverseMapping.Values.ToList(),
            TotalPackages = reverseMapping.Count,
            ExplicitPackages = reverseMapping.Values.Count(n => n.IsExplicit),
            TransitivePackages = reverseMapping.Values.Count(n => !n.IsExplicit),
            FrameworkPackages = reverseMapping.Values.Count(n => n.IsFrameworkPackage),
            MostReferencedPackages = reverseMapping.Values
                .OrderByDescending(n => n.DependentPackages.Count)
                .Take(10)
                .ToList(),
            LeafPackages = reverseMapping.Values
                .Where(n => n.DependentPackages.Count == 0)
                .OrderBy(n => n.PackageId)
                .ToList()
        };
        
        return analysis;
    }
    
    /// <summary>
    /// Recursively builds reverse dependency mappings
    /// </summary>
    private void BuildReverseMappingsRecursive(
        DependencyGraphNode currentNode,
        DependencyGraphNode? parentNode,
        Dictionary<string, ReverseDependencyNode> reverseMapping,
        bool excludeFrameworkPackages,
        List<string> currentPath) {
        
        var newPath = new List<string>(currentPath) { currentNode.PackageId };
        
        // If this node has a parent, add the parent as a dependent
        if (parentNode != null) {
            if (reverseMapping.TryGetValue(currentNode.PackageId, out var reverseNode)) {
                // Add parent as a dependent if not already present
                if (!reverseNode.DependentPackages.Any(d => d.PackageId == parentNode.PackageId)) {
                    reverseNode.DependentPackages.Add(new PackageReference {
                        PackageId = parentNode.PackageId,
                        Version = parentNode.Version,
                        TargetFramework = parentNode.TargetFramework,
                        IsRootPackage = parentNode.Depth == 0, // Root packages have depth 0
                        Depth = parentNode.Depth,
                        IsPrerelease = parentNode.IsPrerelease,
                        VersionRange = parentNode.VersionRange
                    });
                }
                
                // Add dependency path
                var pathString = string.Join(" → ", newPath);
                if (!reverseNode.DependencyPaths.Contains(pathString)) {
                    reverseNode.DependencyPaths.Add(pathString);
                }
            }
        }
        
        // Continue with dependencies
        foreach (var dependency in currentNode.Dependencies) {
            if (!excludeFrameworkPackages || !IsFrameworkPackage(dependency.PackageId)) {
                BuildReverseMappingsRecursive(dependency, currentNode, reverseMapping, excludeFrameworkPackages, newPath);
            }
        }
    }
    
    /// <summary>
    /// Determines if a package is a framework package
    /// </summary>
    private static bool IsFrameworkPackage(string packageId) {
        var lowerId = packageId.ToLowerInvariant();
        return lowerId.StartsWith("microsoft.") ||
               lowerId.StartsWith("system.") ||
               lowerId.StartsWith("netstandard.") ||
               lowerId.StartsWith("runtime.") ||
               lowerId.StartsWith("internal.aspnetcore.") ||
               lowerId == "netstandard.library";
    }
}

/// <summary>
/// Analysis results for reverse dependency graph
/// </summary>
internal sealed class ReverseDependencyAnalysis {
    public List<ReverseDependencyNode> ReverseNodes { get; set; } = new();
    public int TotalPackages { get; set; }
    public int ExplicitPackages { get; set; }
    public int TransitivePackages { get; set; }
    public int FrameworkPackages { get; set; }
    public List<ReverseDependencyNode> MostReferencedPackages { get; set; } = new();
    public List<ReverseDependencyNode> LeafPackages { get; set; } = new();
}

/// <summary>
/// Represents a node in the reverse dependency graph
/// </summary>
internal sealed class ReverseDependencyNode {
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string TargetFramework { get; set; } = string.Empty;
    public bool IsExplicit { get; set; }
    public bool IsFrameworkPackage { get; set; }
    public List<PackageReference> DependentPackages { get; set; } = new();
    public List<string> DependencyPaths { get; set; } = new();
    
    public int ReferenceCount => DependentPackages.Count;
}