using bld.Models;
using bld.Services;
using Microsoft.Build.Evaluation;
using System.Collections.Concurrent;

namespace bld.Infrastructure;

/// <summary>
/// Service for extracting NuGet package references from MSBuild projects
/// </summary>
internal sealed class NugetPackageExtractor {
    private readonly IConsoleOutput _console;
    private readonly ErrorSink _errorSink;
    private readonly NugetPackageCategorizer _categorizer;
    private readonly ConcurrentDictionary<(string Path, string? Configuration), ProjectNugetAnalysis> _analysisCache = new();

    public NugetPackageExtractor(IConsoleOutput console, ErrorSink errorSink, NugetPackageCategorizer categorizer) {
        _console = console;
        _errorSink = errorSink;
        _categorizer = categorizer;
    }

    /// <summary>
    /// Extracts NuGet package references from a project
    /// </summary>
    public IReadOnlyList<NugetPackageInfo> ExtractPackageReferences(ProjCfg projCfg, Dictionary<string, string> globalProperties) {
        var (packages, _) = AnalyzeProjectInternal(projCfg, globalProperties);
        return packages.AsReadOnly();
    }

    /// <summary>
    /// Loads central package versions from Directory.Packages.props
    /// </summary>
    private Dictionary<string, string> LoadCentralPackageVersions(string projectPath, ProjectCollection projectCollection, Dictionary<string, string> properties) {
        var centralVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try {
            // Look for Directory.Packages.props in the project directory and parent directories
            var currentDir = Path.GetDirectoryName(projectPath);
            while (currentDir != null) {
                var centralPackagesFile = Path.Combine(currentDir, "Directory.Packages.props");
                if (File.Exists(centralPackagesFile)) {
                    var centralProject = new Project(centralPackagesFile, properties, null, projectCollection);
                    var packageVersionItems = centralProject.GetItems("PackageVersion");

                    foreach (var item in packageVersionItems) {
                        var packageName = item.EvaluatedInclude;
                        var version = item.GetMetadataValue("Version");
                        if (!string.IsNullOrWhiteSpace(packageName) && !string.IsNullOrWhiteSpace(version)) {
                            centralVersions[packageName] = version;
                        }
                    }
                    break; // Found it, no need to look further
                }
                currentDir = Path.GetDirectoryName(currentDir);
            }
        }
        catch (Exception ex) {
            _console.WriteDebug($"Could not load central package versions: {ex.Message}");
        }

        return centralVersions;
    }

    /// <summary>
    /// Analyzes a project and returns complete package analysis
    /// </summary>
    public ProjectNugetAnalysis AnalyzeProject(ProjCfg projCfg, Dictionary<string, string> globalProperties) {
        var configuration = projCfg.Configuration ?? "Release";
        var key = (projCfg.Path, configuration);

        if (_analysisCache.TryGetValue(key, out var cached)) {
            return cached;
        }

        var (packages, projectName) = AnalyzeProjectInternal(projCfg, globalProperties);
        var analysis = new ProjectNugetAnalysis {
            ProjectPath = projCfg.Path,
            ProjectName = projectName,
            Packages = packages
        };

        _analysisCache[key] = analysis;
        return analysis;
    }

    private (List<NugetPackageInfo> Packages, string ProjectName) AnalyzeProjectInternal(ProjCfg projCfg, Dictionary<string, string> globalProperties) {
        var packages = new List<NugetPackageInfo>();
        var projectName = Path.GetFileNameWithoutExtension(projCfg.Path);

        using var projectCollection = new ProjectCollection();

        var properties = new Dictionary<string, string>(globalProperties);
        properties["Configuration"] = projCfg.Configuration ?? "Release";

        try {
            var project = new Project(projCfg.Path, properties, null, projectCollection);

            // Load Directory.Packages.props if it exists for centrally managed versions
            var centralVersions = LoadCentralPackageVersions(projCfg.Path, projectCollection, properties);

            // Get PackageReference items
            var packageReferenceItems = project.GetItems("PackageReference");

            foreach (var item in packageReferenceItems) {
                var packageName = item.EvaluatedInclude;
                var version = item.GetMetadataValue("Version");
                // todo VersionOverride

                // If no direct version, check centrally managed packages
                if (string.IsNullOrWhiteSpace(version) && centralVersions.ContainsKey(packageName)) {
                    version = centralVersions[packageName];
                }

                if (string.IsNullOrWhiteSpace(packageName)) {
                    continue;
                }

                var category = _categorizer.CategorizePackage(packageName, version);
                var (whitelistMatch, blacklistMatch, microsoftMatch, trustedMatch) = _categorizer.GetAllMatches(packageName, version);

                packages.Add(new NugetPackageInfo {
                    Name = packageName,
                    Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version,
                    Category = category,
                    ProjectPath = projCfg.Path,
                    WhitelistMatch = whitelistMatch,
                    BlacklistMatch = blacklistMatch,
                    MicrosoftMatch = microsoftMatch,
                    TrustedMatch = trustedMatch
                });
            }

            var name = project.GetPropertyValue("ProjectName");
            if (!string.IsNullOrWhiteSpace(name)) {
                projectName = name;
            }
        }
        catch (Exception ex) {
            _errorSink.AddError($"Failed to extract package references from project.", exception: ex, config: projCfg);
            _console.WriteError($"Could not extract packages from {projCfg.Path}: {ex.Message}");
        }

        return (packages, projectName);
    }
}