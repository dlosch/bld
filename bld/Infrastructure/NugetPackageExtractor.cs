using bld.Models;
using bld.Services;
using bld.Services.NuGet;
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
    // nuget.config is looked up from the project directory, as restore does; one locator per directory.
    private readonly ConcurrentDictionary<string, Lazy<PackageSourceLocator>> _locators = new(DirExt.PathComparer);

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

                if (string.IsNullOrWhiteSpace(packageName)) {
                    continue;
                }

                // Under central package management a reference may pin its own version with
                // VersionOverride; that is the version restore uses, not the central one.
                var versionOverride = item.GetMetadataValue("VersionOverride");
                if (string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(versionOverride)) {
                    version = versionOverride;
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
            AddSources(project, projCfg, packages);
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
        var assetsPath = AssetsPath(project, projCfg);

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

    private static string AssetsPath(Project project, ProjCfg projCfg) {
        var extensionsPath = project.GetPropertyValue("MSBuildProjectExtensionsPath");
        if (string.IsNullOrWhiteSpace(extensionsPath)) extensionsPath = "obj";
        return ProjectAssetsReader.GetPath(DirExt.EnsureRooted(extensionsPath, projCfg.ProjDir));
    }

    /// <summary>
    /// Sets each package's <see cref="NugetPackageInfo.Origin"/>. The packages folders and the versions
    /// restore picked come from project.assets.json when there is one; before a restore the folders are
    /// RestorePackagesPath and the configured ones, and the version is the one the project names.
    /// </summary>
    private void AddSources(Project project, ProjCfg projCfg, List<NugetPackageInfo> packages) {
        PackageSourceLocator locator;
        try {
            locator = _locators.GetOrAdd(projCfg.ProjDir, dir => new Lazy<PackageSourceLocator>(() => new PackageSourceLocator(PackageSourceResolver.LoadSettings(dir)))).Value;
        }
        catch (Exception ex) {
            _console.WriteWarning($"Could not read the NuGet configuration for {projCfg.Path}: {ex.FormatMessage()}. Its package sources are not listed.");
            return;
        }

        IReadOnlyList<string> folders = Array.Empty<string>();
        IReadOnlyList<ResolvedPackage> resolved = Array.Empty<ResolvedPackage>();
        var assetsPath = AssetsPath(project, projCfg);
        try {
            if (File.Exists(assetsPath)) {
                var json = File.ReadAllText(assetsPath);
                folders = ProjectAssetsReader.ParsePackageFolders(json);
                resolved = ProjectAssetsReader.Parse(json);
            }
        }
        catch (Exception ex) {
            _console.WriteDebug($"Could not read {assetsPath}: {ex.FormatMessage()}");
        }
        if (folders.Count == 0) {
            var restorePackagesPath = project.GetPropertyValue("RestorePackagesPath");
            folders = string.IsNullOrWhiteSpace(restorePackagesPath)
                ? locator.DefaultPackageFolders
                : [DirExt.EnsureRooted(restorePackagesPath, projCfg.ProjDir), .. locator.DefaultPackageFolders];
        }

        // RestoreSources replaces the nuget.config sources for this project, RestoreAdditionalProjectSources
        // adds to them; both are ';'-separated and may be relative to the project.
        var restoreSources = SourceList(project.GetPropertyValue("RestoreSources"), projCfg.ProjDir);
        var additionalSources = SourceList(project.GetPropertyValue("RestoreAdditionalProjectSources"), projCfg.ProjDir);

        for (var i = 0; i < packages.Count; i++) {
            var package = packages[i];
            // A transitive line is one exact resolved version. A direct reference may name a range or a
            // floating version, so what restore picked for it (the assets file) is tried first.
            var versions = package.IsTransitive
                ? [package.Version]
                : resolved
                    .Where(r => string.Equals(r.Id, package.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Version)
                    .Append(package.Version);
            packages[i] = package with { Origin = locator.Locate(package.Name, versions, folders, restoreSources, additionalSources) };
        }
    }

    private static List<string> SourceList(string value, string projectDir) =>
        value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? s
                : DirExt.EnsureRooted(s, projectDir))
            .ToList();
}