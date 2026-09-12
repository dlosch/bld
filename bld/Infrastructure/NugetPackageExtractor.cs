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
    /// <param name="includeTransitive">Also list the packages resolved through others, read from project.assets.json.</param>
    public ProjectNugetAnalysis AnalyzeProject(ProjCfg projCfg, Dictionary<string, string> globalProperties, bool includeTransitive = false) {
        var configuration = projCfg.Configuration ?? "Release";
        var key = (projCfg.Path, configuration);

        if (_analysisCache.TryGetValue(key, out var cached)) {
            return cached;
        }

        var (packages, projectName) = AnalyzeProjectInternal(projCfg, globalProperties, includeTransitive);
        var analysis = new ProjectNugetAnalysis {
            ProjectPath = projCfg.Path,
            ProjectName = projectName,
            Packages = packages
        };

        _analysisCache[key] = analysis;
        return analysis;
    }

    private (List<NugetPackageInfo> Packages, string ProjectName) AnalyzeProjectInternal(ProjCfg projCfg, Dictionary<string, string> globalProperties, bool includeTransitive) {
        var packages = new List<NugetPackageInfo>();
        var projectName = Path.GetFileNameWithoutExtension(projCfg.Path);

        using var projectCollection = new ProjectCollection();

        var properties = new Dictionary<string, string>(globalProperties);
        properties["Configuration"] = projCfg.Configuration ?? "Release";

        try {
            var project = new Project(projCfg.Path, properties, null, projectCollection);

            // Load Directory.Packages.props if it exists for centrally managed versions
            var centralVersions = LoadCentralPackageVersions(projCfg.Path, projectCollection, properties);

            // GlobalPackageReference items show up as PackageReference items with an empty Version
            // (NuGet.targets adds them); the version lives on the GlobalPackageReference item itself.
            var globalVersions = project.GetItems("GlobalPackageReference")
                .DistinctBy(g => g.EvaluatedInclude, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.EvaluatedInclude, g => g.GetMetadataValue("Version"), StringComparer.OrdinalIgnoreCase);

            // Get PackageReference items
            var packageReferenceItems = project.GetItems("PackageReference");

            foreach (var item in packageReferenceItems) {
                var packageName = item.EvaluatedInclude;
                var version = item.GetMetadataValue("Version");
                // todo VersionOverride

                if (string.IsNullOrWhiteSpace(packageName)) {
                    continue;
                }

                var kind = globalVersions.ContainsKey(packageName) ? PackageItemKind.GlobalPackageReference : PackageItemKind.PackageReference;
                if (string.IsNullOrWhiteSpace(version) && kind == PackageItemKind.GlobalPackageReference) {
                    version = globalVersions[packageName];
                }

                // If no direct version, check centrally managed packages
                if (string.IsNullOrWhiteSpace(version) && centralVersions.ContainsKey(packageName)) {
                    version = centralVersions[packageName];
                }

                AddPackage(packages, projCfg, packageName, version, kind);
            }

            foreach (var item in project.GetItems("PackageDownload")) {
                var packageName = item.EvaluatedInclude;
                if (string.IsNullOrWhiteSpace(packageName)) continue;
                if (packages.Any(p => string.Equals(p.Name, packageName, StringComparison.OrdinalIgnoreCase))) continue;

                var raw = item.GetMetadataValue("Version");
                var version = ProjParser.HighestExactVersion(raw)?.ToString() ?? raw;
                AddPackage(packages, projCfg, packageName, version, PackageItemKind.PackageDownload);
            }

            var name = project.GetPropertyValue("ProjectName");
            if (!string.IsNullOrWhiteSpace(name)) {
                projectName = name;
            }

            if (includeTransitive) {
                AddTransitivePackages(project, projCfg, projectName, packages);
            }
        }
        catch (Exception ex) {
            _errorSink.AddError($"Failed to extract package references from project.", exception: ex, config: projCfg);
            _console.WriteError($"Could not extract packages from {projCfg.Path}: {ex.FormatMessage()}");
        }

        return (packages, projectName);
    }

    private void AddPackage(List<NugetPackageInfo> packages, ProjCfg projCfg, string packageName, string? version, PackageItemKind kind) {
        var category = _categorizer.CategorizePackage(packageName, version);
        var (whitelistMatch, blacklistMatch, microsoftMatch, trustedMatch) = _categorizer.GetAllMatches(packageName, version);

        packages.Add(new NugetPackageInfo {
            Name = packageName,
            Version = string.IsNullOrWhiteSpace(version) ? "Unknown" : version,
            Category = category,
            Kind = kind,
            ProjectPath = projCfg.Path,
            WhitelistMatch = whitelistMatch,
            BlacklistMatch = blacklistMatch,
            MicrosoftMatch = microsoftMatch,
            TrustedMatch = trustedMatch
        });
    }

    /// <summary>
    /// Appends the packages restore resolved on top of the direct references. The assets file lives under
    /// MSBuildProjectExtensionsPath (obj/ by default, artifacts/obj/&lt;project&gt;/ in the artifacts layout).
    /// </summary>
    private void AddTransitivePackages(Project project, ProjCfg projCfg, string projectName, List<NugetPackageInfo> packages) {
        var extensionsPath = project.GetPropertyValue("MSBuildProjectExtensionsPath");
        if (string.IsNullOrWhiteSpace(extensionsPath)) extensionsPath = "obj";
        var assetsPath = ProjectAssetsReader.GetPath(DirExt.EnsureRooted(extensionsPath, projCfg.ProjDir));

        IReadOnlyList<ResolvedPackage>? resolved;
        try {
            resolved = ProjectAssetsReader.TryRead(assetsPath);
        }
        catch (Exception ex) {
            _console.WriteWarning($"Could not read {assetsPath}: {ex.FormatMessage()}. Only direct references are listed for {projectName}.");
            return;
        }

        if (resolved is null) {
            _console.WriteWarning($"No {ProjectAssetsReader.FileName} for {projectName} (expected {assetsPath}); run dotnet restore first. Only direct references are listed.");
            return;
        }

        var directIds = packages.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var package in resolved) {
            if (package.IsDirect || directIds.Contains(package.Id)) continue;

            var category = _categorizer.CategorizePackage(package.Id, package.Version);
            var (whitelistMatch, blacklistMatch, microsoftMatch, trustedMatch) = _categorizer.GetAllMatches(package.Id, package.Version);

            packages.Add(new NugetPackageInfo {
                Name = package.Id,
                Version = package.Version,
                Category = category,
                ProjectPath = projCfg.Path,
                WhitelistMatch = whitelistMatch,
                BlacklistMatch = blacklistMatch,
                MicrosoftMatch = microsoftMatch,
                TrustedMatch = trustedMatch,
                IsTransitive = true,
                TargetFrameworks = package.TargetFrameworks,
                RequestedBy = package.RequestedBy,
            });
        }
    }
}