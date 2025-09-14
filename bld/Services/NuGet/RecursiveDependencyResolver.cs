using bld.Infrastructure;
using bld.Models;
using NuGet.Frameworks;
using NuGet.Versioning;
using System.Collections.Concurrent;

namespace bld.Services.NuGet;

/// <summary>
/// Resolves NuGet package dependencies recursively, building a complete dependency graph
/// </summary>
internal class RecursiveDependencyResolver {
    private readonly HttpClient _httpClient;
    private readonly NugetMetadataOptions _options;
    private readonly IConsoleOutput? _logger;
    
    // Cache for package version results to avoid duplicate requests
    private readonly ConcurrentDictionary<string, PackageVersionResult?> _packageCache = new();
    
    // Set to track packages currently being resolved to detect cycles
    private readonly HashSet<string> _resolvingPackages = new();

    public RecursiveDependencyResolver(HttpClient httpClient, NugetMetadataOptions options, IConsoleOutput? logger = null) {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// Resolves all transitive dependencies for the given root packages
    /// </summary>
    public async Task<PackageDependencyGraph> ResolveTransitiveDependenciesAsync(
        IEnumerable<string> rootPackageIds,
        DependencyResolutionOptions options,
        Dictionary<string, PackageInfoContainer>? existingPackageReferences = null,
        CancellationToken cancellationToken = default) {
        
        ArgumentNullException.ThrowIfNull(rootPackageIds);
        ArgumentNullException.ThrowIfNull(options);

        // Pre-populate cache with existing package references to avoid duplicate fetches
        if (existingPackageReferences != null) {
            await PrePopulateCacheAsync(existingPackageReferences, options, cancellationToken);
        }

        var rootNodes = new List<DependencyGraphNode>();
        var allPackages = new ConcurrentBag<PackageReference>();
        var unresolvedPackages = new ConcurrentBag<UnresolvedPackage>();
        var maxDepth = 0;

        // Process each root package
        await Parallel.ForEachAsync(rootPackageIds, new ParallelOptions {
            MaxDegreeOfParallelism = _options.MaxParallelRequests,
            CancellationToken = cancellationToken
        }, async (rootPackageId, ct) => {
            try {
                var rootNode = await ResolvePackageRecursivelyAsync(
                    rootPackageId, 
                    versionRange: null, 
                    options, 
                    depth: 0, 
                    ct);

                if (rootNode != null) {
                    lock (rootNodes) {
                        rootNodes.Add(rootNode);
                    }
                    
                    // Flatten the tree and collect all packages
                    var flatPackages = new List<PackageReference>();
                    CollectAllPackages(rootNode, flatPackages, true);
                    
                    foreach (var pkg in flatPackages) {
                        allPackages.Add(pkg);
                        if (pkg.Depth > maxDepth) {
                            maxDepth = pkg.Depth;
                        }
                    }
                } else {
                    unresolvedPackages.Add(new UnresolvedPackage {
                        PackageId = rootPackageId,
                        TargetFramework = options.TargetFrameworks.FirstOrDefault() ?? NuGetFramework.AnyFramework,
                        Reason = "Failed to resolve root package",
                        Depth = 0
                    });
                }
            }
            catch (Exception ex) {
                _logger?.WriteError($"Failed to resolve dependencies for {rootPackageId}: {ex.Message}");
                unresolvedPackages.Add(new UnresolvedPackage {
                    PackageId = rootPackageId,
                    TargetFramework = options.TargetFrameworks.FirstOrDefault() ?? NuGetFramework.AnyFramework,
                    Reason = $"Exception: {ex.Message}",
                    Depth = 0
                });
            }
        });

        // Deduplicate packages by PackageId + Version + TargetFramework
        var uniquePackages = allPackages
            .GroupBy(p => new { p.PackageId, p.Version, p.TargetFramework })
            .Select(g => g.OrderBy(p => p.Depth).First()) // Keep the one with minimum depth
            .OrderBy(p => p.PackageId)
            .ThenBy(p => p.Depth)
            .ToList();

        return new PackageDependencyGraph {
            RootPackages = rootNodes.OrderBy(n => n.PackageId).ToList(),
            AllPackages = uniquePackages,
            UnresolvedPackages = unresolvedPackages.OrderBy(u => u.PackageId).ToList(),
            MaxDepth = maxDepth
        };
    }

    /// <summary>
    /// Pre-populate cache with existing package references from OutdatedService
    /// </summary>
    private async Task PrePopulateCacheAsync(
        Dictionary<string, PackageInfoContainer> existingPackageReferences,
        DependencyResolutionOptions options,
        CancellationToken cancellationToken) {
        
        _logger?.WriteDebug($"Pre-populating cache with {existingPackageReferences.Count} existing package references");
        
        await Parallel.ForEachAsync(existingPackageReferences, new ParallelOptions {
            MaxDegreeOfParallelism = _options.MaxParallelRequests,
            CancellationToken = cancellationToken
        }, async (kvp, ct) => {
            var packageId = kvp.Key;
            var packageContainer = kvp.Value;
            
            // Create cache key
            var cacheKey = CreateCacheKey(packageId, options.TargetFrameworks);
            
            // If not already cached, fetch and cache it
            if (!_packageCache.ContainsKey(cacheKey)) {
                try {
                    var request = new PackageVersionRequest {
                        PackageId = packageId,
                        AllowPrerelease = options.AllowPrerelease,
                        CompatibleTargetFrameworks = options.TargetFrameworks.Select(tf => tf.GetShortFolderName()).ToList()
                    };
                    
                    var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(
                        _httpClient, _options, _logger, request, ct);
                    
                    _packageCache.TryAdd(cacheKey, result);
                    _logger?.WriteDebug($"Cached package metadata for {packageId}");
                }
                catch (Exception ex) {
                    _logger?.WriteError($"Failed to pre-cache package {packageId}: {ex.Message}");
                    _packageCache.TryAdd(cacheKey, null);
                }
            }
        });
    }

    /// <summary>
    /// Recursively resolves a package and all its dependencies
    /// </summary>
    private async Task<DependencyGraphNode?> ResolvePackageRecursivelyAsync(
        string packageId,
        string? versionRange,
        DependencyResolutionOptions options,
        int depth,
        CancellationToken cancellationToken) {
        
        // Check depth limit
        if (depth > options.MaxDepth) {
            _logger?.WriteWarning($"Maximum depth {options.MaxDepth} reached for package {packageId}");
            return null;
        }

        // Check for cycles
        var resolutionKey = $"{packageId}@{depth}";
        lock (_resolvingPackages) {
            if (_resolvingPackages.Contains(packageId)) {
                _logger?.WriteWarning($"Cycle detected for package {packageId} at depth {depth}");
                return options.StopOnCycles ? null : null;
            }
            _resolvingPackages.Add(packageId);
        }

        try {
            // Try to get from cache first
            var cacheKey = CreateCacheKey(packageId, options.TargetFrameworks);
            
            if (!_packageCache.TryGetValue(cacheKey, out var packageResult)) {
                var request = new PackageVersionRequest {
                    PackageId = packageId,
                    AllowPrerelease = options.AllowPrerelease,
                    CompatibleTargetFrameworks = options.TargetFrameworks.Select(tf => tf.GetShortFolderName()).ToList()
                };
                
                packageResult = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(
                    _httpClient, _options, _logger, request, cancellationToken);
                
                _packageCache.TryAdd(cacheKey, packageResult);
            }

            if (packageResult == null) {
                _logger?.WriteWarning($"Could not resolve package {packageId}");
                return null;
            }

            // Get the best target framework and its version
            var targetFramework = options.TargetFrameworks.FirstOrDefault() ?? NuGetFramework.AnyFramework;
            if (!packageResult.TargetFrameworkVersions.TryGetValue(targetFramework, out var version)) {
                // Try with the first available framework
                var firstFramework = packageResult.TargetFrameworkVersions.FirstOrDefault();
                if (firstFramework.Key != null) {
                    targetFramework = firstFramework.Key;
                    version = firstFramework.Value;
                } else {
                    _logger?.WriteWarning($"No compatible version found for {packageId}");
                    return null;
                }
            }

            // Check version range compatibility if specified
            if (!string.IsNullOrEmpty(versionRange)) {
                var parsedVersionRange = VersionRange.Parse(versionRange);
                var parsedVersion = NuGetVersion.Parse(version);
                if (!parsedVersionRange.Satisfies(parsedVersion)) {
                    _logger?.WriteDebug($"Version {version} of {packageId} does not satisfy range {versionRange}");
                    // Still continue, but log the issue
                }
            }

            // Get dependency group for this target framework
            DependencyGroup? dependencyGroup = null;
            packageResult.Dependencies?.TryGetValue(targetFramework, out dependencyGroup);

            var childDependencies = new List<DependencyGraphNode>();

            // Resolve child dependencies
            if (dependencyGroup?.Dependencies != null && dependencyGroup.Dependencies.Any()) {
                var childTasks = dependencyGroup.Dependencies.Select(async dep => {
                    return await ResolvePackageRecursivelyAsync(
                        dep.PackageId,
                        dep.Range,
                        options,
                        depth + 1,
                        cancellationToken);
                });

                var resolvedChildren = await Task.WhenAll(childTasks);
                childDependencies.AddRange(resolvedChildren.Where(child => child != null)!);
            }

            return new DependencyGraphNode {
                PackageId = packageId,
                Version = version,
                TargetFramework = targetFramework.GetShortFolderName(),
                IsPrerelease = packageResult.IsPrerelease,
                Dependencies = childDependencies,
                DependencyGroup = dependencyGroup,
                VersionRange = versionRange,
                Depth = depth,
                RetrievedAt = packageResult.RetrievedAt
            };
        }
        finally {
            // Remove from resolving set
            lock (_resolvingPackages) {
                _resolvingPackages.Remove(packageId);
            }
        }
    }

    /// <summary>
    /// Recursively collects all packages from a dependency node into a flat list
    /// </summary>
    private void CollectAllPackages(DependencyGraphNode node, List<PackageReference> packages, bool isRoot = false) {
        packages.Add(new PackageReference {
            PackageId = node.PackageId,
            Version = node.Version,
            TargetFramework = node.TargetFramework,
            IsPrerelease = node.IsPrerelease,
            IsRootPackage = isRoot,
            Depth = node.Depth,
            VersionRange = node.VersionRange,
            RetrievedAt = node.RetrievedAt
        });

        foreach (var child in node.Dependencies) {
            CollectAllPackages(child, packages, false);
        }
    }

    /// <summary>
    /// Creates a cache key for package lookup
    /// </summary>
    private static string CreateCacheKey(string packageId, IReadOnlyList<NuGetFramework> targetFrameworks) {
        var frameworksKey = !targetFrameworks.Any() ? "any" : string.Join(",", targetFrameworks.Select(f => f.GetShortFolderName()).OrderBy(f => f));
        return $"{packageId}|{frameworksKey}";
    }
}