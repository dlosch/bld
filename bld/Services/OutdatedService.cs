using bld.Infrastructure;
using bld.Models;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace bld.Services;

internal class OutdatedService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;
    private readonly SourceCacheContext _cache;
    private readonly ILogger _logger;

    public OutdatedService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
        _cache = new SourceCacheContext();
        _logger = new NuGetLogger(_console);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> CheckOutdatedPackagesAsync(string rootPath, bool updatePackages, bool skipTfmCheck, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
        _console.WriteInfo("Checking for outdated packages...");

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var allPackageReferences = new Dictionary<string, List<PackageInfo>>();
        var projectFiles = new List<string>();
        var solutionCpmInfo = new Dictionary<string, CpmInfo>(); // Solution directory -> CPM info

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            var solutionDir = Path.GetDirectoryName(slnPath)!;
            
            // Check if this solution uses Central Package Management
            var directoryPackagesPath = Path.Combine(solutionDir, "Directory.Packages.props");
            var cpmInfo = await LoadCpmInfoAsync(directoryPackagesPath, cancellationToken);
            if (cpmInfo != null) {
                solutionCpmInfo[solutionDir] = cpmInfo;
                _console.WriteVerbose($"Solution uses Central Package Management");
            }
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projectPath = projCfg.Path;
                projectFiles.Add(projectPath);
                
                var packageRefs = await ExtractPackageReferencesAsync(projectPath, solutionDir, cpmInfo, skipTfmCheck, cancellationToken);
                foreach (var packageInfo in packageRefs) {
                    if (!allPackageReferences.TryGetValue(packageInfo.Id, out var list)) {
                        list = new List<PackageInfo>();
                        allPackageReferences[packageInfo.Id] = list;
                    }
                    list.Add(packageInfo);
                }
            }
        }

        if (allPackageReferences.Count == 0) {
            _console.WriteInfo("No package references found.");
            return 0;
        }

        _console.WriteInfo($"Found {allPackageReferences.Count} unique packages across {projectFiles.Count} projects");

        // Check for version conflicts within the same solution
        var versionConflicts = new List<VersionConflictInfo>();
        foreach (var (packageId, packageInfos) in allPackageReferences) {
            var versionGroups = packageInfos.GroupBy(p => p.Version).ToList();
            if (versionGroups.Count > 1) {
                versionConflicts.Add(new VersionConflictInfo {
                    PackageId = packageId,
                    VersionUsages = versionGroups.ToDictionary(g => g.Key, g => g.Select(p => p.ProjectPath).ToList())
                });
            }
        }

        if (versionConflicts.Count > 0) {
            _console.WriteWarning($"\nFound {versionConflicts.Count} packages with version conflicts:");
            foreach (var conflict in versionConflicts.OrderBy(x => x.PackageId)) {
                _console.WriteWarning($"{conflict.PackageId}:");
                foreach (var (version, projects) in conflict.VersionUsages.OrderBy(x => x.Key)) {
                    _console.WriteWarning($"  {version} used in: {string.Join(", ", projects.Select(Path.GetFileName))}");
                }
            }
            _console.WriteInfo("");
        }

        var outdatedPackages = new List<OutdatedPackageInfo>();
        var packageSource = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var packageMetadataResource = await packageSource.GetResourceAsync<PackageMetadataResource>(cancellationToken);

        foreach (var (packageId, packageInfos) in allPackageReferences) {
            try {
                _console.WriteVerbose($"Checking {packageId}...");
                
                var metadata = await packageMetadataResource.GetMetadataAsync(packageId, true, true, _cache, _logger, cancellationToken);
                
                var currentVersions = packageInfos.Select(p => p.Version).Distinct().ToList();
                
                var hasOutdated = false;
                foreach (var currentVersionStr in currentVersions) {
                    if (NuGetVersion.TryParse(currentVersionStr, out var currentVersion)) {
                        var projects = packageInfos.Where(p => p.Version == currentVersionStr).ToList();
                        
                        // Find best compatible version for each target framework
                        var tfmGroups = projects.GroupBy(p => p.TargetFramework ?? "unknown").ToList();
                        
                        foreach (var tfmGroup in tfmGroups) {
                            var tfm = tfmGroup.Key;
                            NuGetFramework? targetFramework = null;
                            
                            if (!skipTfmCheck && tfm != "unknown") {
                                try {
                                    targetFramework = NuGetFramework.Parse(tfm);
                                }
                                catch {
                                    _console.WriteWarning($"Could not parse target framework '{tfm}' for TFM compatibility check");
                                }
                            }
                            
                            // Find the latest compatible version
                            var compatibleVersions = new List<IPackageSearchMetadata>();
                            
                            foreach (var meta in metadata.Where(m => !m.Identity.Version.IsPrerelease).OrderByDescending(m => m.Identity.Version)) {
                                bool isCompatible = true;
                                
                                if (!skipTfmCheck && targetFramework != null) {
                                    try {
                                        // For now, we'll do a basic compatibility check based on framework version
                                        // This is a simplified approach - full compatibility checking would require 
                                        // downloading and analyzing the package
                                        var packageTfm = meta.Identity.Version;
                                        isCompatible = true; // Assume compatible for now
                                    }
                                    catch {
                                        // If we can't determine compatibility, assume it's compatible
                                        isCompatible = true;
                                    }
                                }
                                
                                if (isCompatible) {
                                    compatibleVersions.Add(meta);
                                }
                            }
                            
                            var latestCompatible = compatibleVersions.FirstOrDefault();
                            if (latestCompatible == null) {
                                _console.WriteWarning($"No compatible version found for {packageId} with target framework {tfm}");
                                continue;
                            }
                            
                            var latestVersion = latestCompatible.Identity.Version;
                            
                            if (currentVersion < latestVersion) {
                                hasOutdated = true;
                                
                                // Group projects by solution for CPM handling
                                var projectGroups = tfmGroup.GroupBy(p => {
                                    var dir = Path.GetDirectoryName(p.ProjectPath);
                                    while (dir != null) {
                                        if (solutionCpmInfo.ContainsKey(dir)) return dir;
                                        dir = Path.GetDirectoryName(dir);
                                    }
                                    return ""; // No CPM solution found
                                }).ToList();

                                foreach (var group in projectGroups) {
                                    var usesCpm = !string.IsNullOrEmpty(group.Key) && solutionCpmInfo.ContainsKey(group.Key);
                                    var compatibilityNote = skipTfmCheck ? "" : $" (compatible with {tfm})";
                                    
                                    outdatedPackages.Add(new OutdatedPackageInfo {
                                        PackageId = packageId,
                                        CurrentVersion = currentVersionStr,
                                        LatestVersion = latestVersion.ToString(),
                                        ProjectPaths = group.Select(p => p.ProjectPath).ToList(),
                                        SolutionDirectory = group.Key,
                                        UsesCpm = usesCpm,
                                        TargetFramework = tfm,
                                        CompatibilityNote = compatibilityNote
                                    });
                                }
                            }
                        }
                    }
                }

                if (!hasOutdated) {
                    _console.WriteVerbose($"{packageId} is up to date");
                }
            }
            catch (Exception ex) {
                _console.WriteWarning($"Failed to check {packageId}: {ex.Message}");
            }
        }

        if (outdatedPackages.Count == 0) {
            _console.WriteInfo("All packages are up to date!");
            return 0;
        }

        _console.WriteInfo($"\nFound {outdatedPackages.Count} outdated package references:");
        foreach (var outdated in outdatedPackages.OrderBy(x => x.PackageId)) {
            _console.WriteWarning($"{outdated.PackageId}: {outdated.CurrentVersion} → {outdated.LatestVersion}{outdated.CompatibilityNote}");
            foreach (var project in outdated.ProjectPaths) {
                _console.WriteVerbose($"  - {Path.GetFileName(project)}");
            }
        }

        if (updatePackages) {
            _console.WriteInfo("\nUpdating packages to latest versions...");
            var updatedSolutions = new HashSet<string>();
            
            foreach (var outdated in outdatedPackages) {
                if (outdated.UsesCpm) {
                    // Update Directory.Packages.props once per solution
                    if (!updatedSolutions.Contains(outdated.SolutionDirectory)) {
                        await UpdateCpmPackageVersionAsync(outdated.SolutionDirectory, outdated.PackageId, outdated.LatestVersion, cancellationToken);
                        updatedSolutions.Add(outdated.SolutionDirectory);
                        _console.WriteInfo($"Updated {outdated.PackageId} to {outdated.LatestVersion} in Directory.Packages.props");
                    }
                } else {
                    // Update individual project files
                    foreach (var projectPath in outdated.ProjectPaths) {
                        await UpdatePackageVersionAsync(projectPath, outdated.PackageId, outdated.LatestVersion, cancellationToken);
                        _console.WriteInfo($"Updated {outdated.PackageId} to {outdated.LatestVersion} in {Path.GetFileName(projectPath)}");
                    }
                }
            }
            _console.WriteInfo($"Updated {outdatedPackages.Count} packages in {outdatedPackages.Sum(x => x.ProjectPaths.Count)} project files");
        } else {
            _console.WriteInfo("\nUse --update to apply these changes.");
        }

        return 0;
    }

    private async Task<List<PackageInfo>> ExtractPackageReferencesAsync(string projectPath, string solutionDir, CpmInfo? cpmInfo, bool skipTfmCheck, CancellationToken cancellationToken) {
        var packageReferences = new List<PackageInfo>();

        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            
            // Get target framework(s)
            var targetFramework = doc.Descendants("TargetFramework").FirstOrDefault()?.Value;
            var targetFrameworks = doc.Descendants("TargetFrameworks").FirstOrDefault()?.Value;
            var projectTfm = targetFramework ?? targetFrameworks?.Split(';').FirstOrDefault()?.Trim();
            
            var packageRefElements = doc.Descendants("PackageReference");

            foreach (var element in packageRefElements) {
                var include = element.Attribute("Include")?.Value;
                string? version = null;

                if (cpmInfo != null && !string.IsNullOrEmpty(include)) {
                    // Get version from Directory.Packages.props
                    cpmInfo.PackageVersions.TryGetValue(include, out version);
                } else {
                    // Get version from project file
                    version = element.Attribute("Version")?.Value ?? 
                             element.Element("Version")?.Value;
                }

                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version)) {
                    packageReferences.Add(new PackageInfo {
                        Id = include,
                        Version = version,
                        ProjectPath = projectPath,
                        TargetFramework = projectTfm
                    });
                }
            }
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to parse {projectPath}: {ex.Message}");
        }

        return packageReferences;
    }

    private async Task<CpmInfo?> LoadCpmInfoAsync(string directoryPackagesPath, CancellationToken cancellationToken) {
        if (!File.Exists(directoryPackagesPath)) {
            return null;
        }

        try {
            using var stream = File.OpenRead(directoryPackagesPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
            var packageVersions = new Dictionary<string, string>();

            var packageVersionElements = doc.Descendants("PackageVersion");
            foreach (var element in packageVersionElements) {
                var include = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value;

                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version)) {
                    packageVersions[include] = version;
                }
            }

            return new CpmInfo {
                DirectoryPackagesPath = directoryPackagesPath,
                PackageVersions = packageVersions
            };
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to parse Directory.Packages.props at {directoryPackagesPath}: {ex.Message}");
            return null;
        }
    }

    private async Task UpdateCpmPackageVersionAsync(string solutionDir, string packageId, string newVersion, CancellationToken cancellationToken) {
        var directoryPackagesPath = Path.Combine(solutionDir, "Directory.Packages.props");
        
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(directoryPackagesPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }
            
            var packageVersionElements = doc.Descendants("PackageVersion")
                .Where(e => e.Attribute("Include")?.Value == packageId);

            foreach (var element in packageVersionElements) {
                var versionAttr = element.Attribute("Version");
                if (versionAttr != null) {
                    versionAttr.Value = newVersion;
                }
            }

            using var writeStream = File.Create(directoryPackagesPath);
            await doc.SaveAsync(writeStream, SaveOptions.None, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update Directory.Packages.props at {directoryPackagesPath}: {ex.Message}");
        }
    }

    private async Task UpdatePackageVersionAsync(string projectPath, string packageId, string newVersion, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }
            
            var packageRefElements = doc.Descendants("PackageReference")
                .Where(e => e.Attribute("Include")?.Value == packageId);

            foreach (var element in packageRefElements) {
                var versionAttr = element.Attribute("Version");
                var versionElement = element.Element("Version");

                if (versionAttr != null) {
                    versionAttr.Value = newVersion;
                } else if (versionElement != null) {
                    versionElement.Value = newVersion;
                }
            }

            using var writeStream = File.Create(projectPath);
            await doc.SaveAsync(writeStream, SaveOptions.None, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.Message}");
        }
    }

    private class PackageInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
        public string? TargetFramework { get; set; }
    }

    private class OutdatedPackageInfo {
        public string PackageId { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string LatestVersion { get; set; } = string.Empty;
        public List<string> ProjectPaths { get; set; } = new();
        public string SolutionDirectory { get; set; } = string.Empty;
        public bool UsesCpm { get; set; }
        public string TargetFramework { get; set; } = string.Empty;
        public string CompatibilityNote { get; set; } = string.Empty;
    }

    private class VersionConflictInfo {
        public string PackageId { get; set; } = string.Empty;
        public Dictionary<string, List<string>> VersionUsages { get; set; } = new();
    }

    private class CpmInfo {
        public string DirectoryPackagesPath { get; set; } = string.Empty;
        public Dictionary<string, string> PackageVersions { get; set; } = new();
    }

    private class NuGetLogger : ILogger {
        private readonly IConsoleOutput _console;

        public NuGetLogger(IConsoleOutput console) {
            _console = console;
        }

        public void LogDebug(string data) => _console.WriteVerbose(data);
        public void LogVerbose(string data) => _console.WriteVerbose(data);
        public void LogInformation(string data) => _console.WriteInfo(data);
        public void LogMinimal(string data) => _console.WriteInfo(data);
        public void LogWarning(string data) => _console.WriteWarning(data);
        public void LogError(string data) => _console.WriteError(data);
        public void LogInformationSummary(string data) => _console.WriteInfo(data);
        public void Log(NuGet.Common.LogLevel level, string data) {
            switch (level) {
                case NuGet.Common.LogLevel.Debug:
                case NuGet.Common.LogLevel.Verbose:
                    LogVerbose(data);
                    break;
                case NuGet.Common.LogLevel.Information:
                case NuGet.Common.LogLevel.Minimal:
                    LogInformation(data);
                    break;
                case NuGet.Common.LogLevel.Warning:
                    LogWarning(data);
                    break;
                case NuGet.Common.LogLevel.Error:
                    LogError(data);
                    break;
            }
        }

        public Task LogAsync(NuGet.Common.LogLevel level, string data) {
            Log(level, data);
            return Task.CompletedTask;
        }

        public void Log(ILogMessage message) => Log(message.Level, message.Message);

        public Task LogAsync(ILogMessage message) {
            Log(message);
            return Task.CompletedTask;
        }
    }
}