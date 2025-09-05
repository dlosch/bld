using bld.Infrastructure;
using bld.Models;
using Microsoft.Build.Evaluation;
using System.IO;

namespace bld.Services;

/// <summary>
/// Analyzes project dependencies and builds dependency trees
/// </summary>
internal sealed class DependencyAnalyzer(IConsoleOutput Console, ErrorSink ErrorSink, CleaningOptions Options) {
    
    private Dictionary<string, string> _globalProperties = default!;

    private Dictionary<string, string> GlobalProperties => _globalProperties ??=
        Options.VSToolsPath is null ?
        new Dictionary<string, string>()
        : Init(Options);

    private static Dictionary<string, string> Init(CleaningOptions Options) {
        var dict = new Dictionary<string, string>(2);
        if (Options.VSToolsPath is { }) dict["VSToolsPath"] = Options.VSToolsPath;
        if (Options.VSRootPath is { } && Directory.Exists(Path.Combine(Options.VSRootPath, "MSBuild"))) dict["MSBuildExtensionsPath"] = Path.Combine(Options.VSRootPath, "MSBuild");

        return dict;
    }

    /// <summary>
    /// Extracts dependency information from a project file
    /// </summary>
    internal DependencyInfo? ExtractDependencies(ProjCfg proj) {
        string projectPath = proj.Path;
        string configuration = proj.Configuration;

        using (var projectCollection = new ProjectCollection()) {
            var project = default(Project);

            var properties = new Dictionary<string, string>(GlobalProperties);
            properties["Configuration"] = configuration;
            try {
                project = new Project(projectPath, properties, null, projectCollection);
            }
            catch (Exception xcptn) {
                ErrorSink.AddError($"Failed to load project.", exception: xcptn, config: proj);
                Console.WriteError($"{projectPath} could not be parsed: {xcptn.Message}.");
                return default;
            }

            static string? Safe(string value) => value is string && !string.IsNullOrEmpty(value) ? value : default;

            // Extract project references
            var projectReferences = new List<ProjectReference>();
            var projectReferenceItems = project.GetItems("ProjectReference");
            foreach (var item in projectReferenceItems) {
                var refPath = item.EvaluatedInclude;
                var fullRefPath = Path.IsPathRooted(refPath) ? refPath : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(projectPath)!, refPath));
                
                var projectName = Path.GetFileNameWithoutExtension(refPath);
                if (item.HasMetadata("Name")) {
                    projectName = item.GetMetadataValue("Name");
                }

                projectReferences.Add(new ProjectReference {
                    ProjectPath = fullRefPath,
                    ProjectName = projectName,
                    IsResolved = File.Exists(fullRefPath)
                });
            }

            // Extract package references
            var packageReferences = new List<PackageReference>();
            var packageReferenceItems = project.GetItems("PackageReference");
            foreach (var item in packageReferenceItems) {
                var packageId = item.EvaluatedInclude;
                var version = item.GetMetadataValue("Version");
                
                // Handle central package management - if Version is empty, try to get it from PackageVersion items
                if (string.IsNullOrEmpty(version)) {
                    var packageVersionItems = project.GetItems("PackageVersion");
                    var packageVersionItem = packageVersionItems.FirstOrDefault(pv => string.Equals(pv.EvaluatedInclude, packageId, StringComparison.OrdinalIgnoreCase));
                    if (packageVersionItem != null) {
                        version = packageVersionItem.GetMetadataValue("Version");
                    }
                    
                    // If still empty, try to evaluate a property reference
                    if (string.IsNullOrEmpty(version)) {
                        // Some projects might use property references like $(SomePackageVersion)
                        version = project.GetPropertyValue($"{packageId}Version") ?? "Unknown";
                    }
                }
                
                var privateAssets = string.Equals(item.GetMetadataValue("PrivateAssets"), "all", StringComparison.OrdinalIgnoreCase);

                packageReferences.Add(new PackageReference {
                    PackageId = packageId,
                    Version = version ?? "Unknown",
                    IsPrivateAssets = privateAssets
                });
            }

            var info = new DependencyInfo {
                ProjectPath = projectPath,
                ProjectName = Safe(project.GetPropertyValue("ProjectName")) ?? Path.GetFileNameWithoutExtension(projectPath),
                TargetFramework = Safe(project.GetPropertyValue("TargetFramework")) ?? string.Empty,
                ProjectReferences = projectReferences,
                PackageReferences = packageReferences
            };

            return info;
        }
    }

    /// <summary>
    /// Builds a dependency tree starting from a root project
    /// </summary>
    internal DependencyNode? BuildDependencyTree(string rootProjectPath, bool includeNuget = true) {
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectCache = new Dictionary<string, DependencyInfo>(StringComparer.OrdinalIgnoreCase);

        return BuildDependencyTreeRecursive(rootProjectPath, visitedProjects, projectCache, includeNuget);
    }

    /// <summary>
    /// Builds dependency tree from all projects in a solution
    /// </summary>
    internal List<DependencyNode> BuildSolutionDependencyTree(IEnumerable<ProjCfg> projects, bool includeNuget = true) {
        var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectCache = new Dictionary<string, DependencyInfo>(StringComparer.OrdinalIgnoreCase);
        var rootNodes = new List<DependencyNode>();

        // First, cache all project dependencies
        foreach (var proj in projects) {
            var depInfo = ExtractDependencies(proj);
            if (depInfo != null && !projectCache.ContainsKey(depInfo.ProjectPath)) {
                projectCache[depInfo.ProjectPath] = depInfo;
            }
        }

        // Build trees for projects that aren't referenced by others (root projects)
        var allReferencedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var depInfo in projectCache.Values) {
            foreach (var projRef in depInfo.ProjectReferences) {
                allReferencedProjects.Add(projRef.ProjectPath);
            }
        }

        foreach (var proj in projects) {
            if (!allReferencedProjects.Contains(proj.Path)) {
                var rootNode = BuildDependencyTreeRecursive(proj.Path, visitedProjects, projectCache, includeNuget);
                if (rootNode != null) {
                    rootNodes.Add(rootNode);
                }
            }
        }

        return rootNodes;
    }

    private DependencyNode? BuildDependencyTreeRecursive(string projectPath, HashSet<string> visitedProjects, Dictionary<string, DependencyInfo> projectCache, bool includeNuget) {
        // Avoid circular dependencies
        if (visitedProjects.Contains(projectPath)) {
            return null;
        }

        visitedProjects.Add(projectPath);

        // Try to get from cache first
        DependencyInfo? depInfo = null;
        if (projectCache.TryGetValue(projectPath, out depInfo)) {
            // Use cached version
        }
        else {
            // Extract dependencies if not cached
            var projCfg = new ProjCfg(new Proj(projectPath, null), "Debug", null);
            depInfo = ExtractDependencies(projCfg);
        }

        if (depInfo == null) {
            visitedProjects.Remove(projectPath);
            return null;
        }

        var node = new DependencyNode {
            DependencyInfo = depInfo,
            PackageDependencies = includeNuget ? depInfo.PackageReferences.ToList() : new List<PackageReference>()
        };

        // Process project references recursively
        foreach (var projRef in depInfo.ProjectReferences) {
            if (projRef.IsResolved && File.Exists(projRef.ProjectPath)) {
                var childNode = BuildDependencyTreeRecursive(projRef.ProjectPath, visitedProjects, projectCache, includeNuget);
                if (childNode != null) {
                    node.ProjectDependencies.Add(childNode);
                }
            }
        }

        visitedProjects.Remove(projectPath);
        return node;
    }
}