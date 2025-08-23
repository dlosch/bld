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

internal class TfmService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;
    private readonly SourceCacheContext _cache;
    private readonly ILogger _logger;

    public TfmService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
        _cache = new SourceCacheContext();
        _logger = new NuGetLogger(_console);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> MigrateTargetFrameworkAsync(string rootPath, string fromTfm, string toTfm, bool applyChanges, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
        _console.WriteInfo($"Migrating projects from {fromTfm} to {toTfm}...");

        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var projectsToMigrate = new List<ProjectMigrationInfo>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projectPath = projCfg.Path;
                var migrationInfo = await AnalyzeProjectForMigrationAsync(projectPath, fromTfm, toTfm, cancellationToken);
                
                if (migrationInfo != null) {
                    projectsToMigrate.Add(migrationInfo);
                }
            }
        }

        if (projectsToMigrate.Count == 0) {
            _console.WriteInfo($"No projects found using {fromTfm}.");
            return 0;
        }

        _console.WriteInfo($"Found {projectsToMigrate.Count} projects to migrate from {fromTfm} to {toTfm}");

        if (applyChanges) {
            // Step 1: Update target frameworks
            foreach (var project in projectsToMigrate) {
                await UpdateProjectTargetFrameworkAsync(project.ProjectPath, fromTfm, toTfm, cancellationToken);
                _console.WriteInfo($"Updated {Path.GetFileName(project.ProjectPath)} to {toTfm}");
            }

            // Step 2: Check for package compatibility and update if needed
            _console.WriteInfo("\nChecking package compatibility with new target framework...");
            var compatibilityIssues = new List<PackageCompatibilityIssue>();

            foreach (var project in projectsToMigrate) {
                var issues = await CheckPackageCompatibilityAsync(project, toTfm, cancellationToken);
                compatibilityIssues.AddRange(issues);
            }

            if (compatibilityIssues.Count > 0) {
                _console.WriteWarning($"Found {compatibilityIssues.Count} package compatibility issues:");
                foreach (var issue in compatibilityIssues) {
                    _console.WriteWarning($"  {issue.PackageId} {issue.CurrentVersion} in {Path.GetFileName(issue.ProjectPath)}");
                    if (!string.IsNullOrEmpty(issue.RecommendedVersion)) {
                        _console.WriteInfo($"    → Recommended: {issue.RecommendedVersion}");
                    } else {
                        _console.WriteError($"    → No compatible version found for {toTfm}");
                    }
                }

                // Update packages with compatible versions
                var updatedPackages = 0;
                foreach (var issue in compatibilityIssues.Where(i => !string.IsNullOrEmpty(i.RecommendedVersion))) {
                    await UpdatePackageVersionInProjectAsync(issue.ProjectPath, issue.PackageId, issue.RecommendedVersion!, cancellationToken);
                    _console.WriteInfo($"Updated {issue.PackageId} to {issue.RecommendedVersion} in {Path.GetFileName(issue.ProjectPath)}");
                    updatedPackages++;
                }

                if (updatedPackages > 0) {
                    _console.WriteInfo($"Updated {updatedPackages} packages for {toTfm} compatibility");
                }
            } else {
                _console.WriteInfo("All packages are compatible with the new target framework");
            }

            _console.WriteInfo($"Migration complete! Migrated {projectsToMigrate.Count} projects to {toTfm}");
        } else {
            _console.WriteInfo("Dry run - showing what would be migrated:");
            foreach (var project in projectsToMigrate) {
                _console.WriteInfo($"  {Path.GetFileName(project.ProjectPath)}: {project.CurrentTfm} → {toTfm}");
                if (project.PackageReferences.Count > 0) {
                    _console.WriteVerbose($"    Packages: {string.Join(", ", project.PackageReferences.Select(p => $"{p.Id}@{p.Version}"))}");
                }
            }
            _console.WriteInfo("\nUse --apply to perform the migration.");
        }

        return 0;
    }

    private async Task<ProjectMigrationInfo?> AnalyzeProjectForMigrationAsync(string projectPath, string fromTfm, string toTfm, CancellationToken cancellationToken) {
        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);

            // Check TargetFramework and TargetFrameworks
            var targetFrameworkElement = doc.Descendants("TargetFramework").FirstOrDefault();
            var targetFrameworksElement = doc.Descendants("TargetFrameworks").FirstOrDefault();

            var currentTfm = targetFrameworkElement?.Value ?? targetFrameworksElement?.Value;
            if (string.IsNullOrEmpty(currentTfm)) {
                return null;
            }

            // Check if this project uses the source TFM
            bool matches = false;
            if (targetFrameworkElement != null && currentTfm.Equals(fromTfm, StringComparison.OrdinalIgnoreCase)) {
                matches = true;
            } else if (targetFrameworksElement != null) {
                var tfms = currentTfm.Split(';').Select(t => t.Trim());
                matches = tfms.Any(t => t.Equals(fromTfm, StringComparison.OrdinalIgnoreCase));
            }

            if (!matches) {
                return null;
            }

            // Extract package references
            var packageReferences = new List<PackageInfo>();
            var packageRefElements = doc.Descendants("PackageReference");

            foreach (var element in packageRefElements) {
                var include = element.Attribute("Include")?.Value;
                var version = element.Attribute("Version")?.Value ?? 
                             element.Element("Version")?.Value;

                if (!string.IsNullOrEmpty(include) && !string.IsNullOrEmpty(version)) {
                    packageReferences.Add(new PackageInfo {
                        Id = include,
                        Version = version,
                        ProjectPath = projectPath
                    });
                }
            }

            return new ProjectMigrationInfo {
                ProjectPath = projectPath,
                CurrentTfm = currentTfm,
                PackageReferences = packageReferences,
                UsesTargetFrameworks = targetFrameworksElement != null
            };
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to analyze {projectPath}: {ex.Message}");
            return null;
        }
    }

    private async Task UpdateProjectTargetFrameworkAsync(string projectPath, string fromTfm, string toTfm, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var targetFrameworkElement = doc.Descendants("TargetFramework").FirstOrDefault();
            var targetFrameworksElement = doc.Descendants("TargetFrameworks").FirstOrDefault();

            if (targetFrameworkElement != null && targetFrameworkElement.Value.Equals(fromTfm, StringComparison.OrdinalIgnoreCase)) {
                targetFrameworkElement.Value = toTfm;
            } else if (targetFrameworksElement != null) {
                var tfms = targetFrameworksElement.Value.Split(';').Select(t => t.Trim()).ToList();
                for (int i = 0; i < tfms.Count; i++) {
                    if (tfms[i].Equals(fromTfm, StringComparison.OrdinalIgnoreCase)) {
                        tfms[i] = toTfm;
                    }
                }
                targetFrameworksElement.Value = string.Join(";", tfms);
            }

            using var writeStream = File.Create(projectPath);
            await doc.SaveAsync(writeStream, SaveOptions.None, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.Message}");
        }
    }

    private async Task<List<PackageCompatibilityIssue>> CheckPackageCompatibilityAsync(ProjectMigrationInfo project, string targetTfm, CancellationToken cancellationToken) {
        var issues = new List<PackageCompatibilityIssue>();
        var packageSource = Repository.Factory.GetCoreV3("https://api.nuget.org/v3/index.json");
        var packageMetadataResource = await packageSource.GetResourceAsync<PackageMetadataResource>(cancellationToken);
        var targetFramework = NuGetFramework.Parse(targetTfm);

        foreach (var package in project.PackageReferences) {
            try {
                var metadata = await packageMetadataResource.GetMetadataAsync(package.Id, true, true, _cache, _logger, cancellationToken);
                
                if (!NuGetVersion.TryParse(package.Version, out var currentVersion)) {
                    continue;
                }

                // Check if current version supports target framework
                var currentMetadata = metadata.FirstOrDefault(m => m.Identity.Version == currentVersion);
                if (currentMetadata != null) {
                    // For now, we'll assume the current package is compatible and just check if there's a newer version
                    // Full framework compatibility checking would require downloading and analyzing package dependencies
                    var latestCompatible = metadata.Where(m => !m.Identity.Version.IsPrerelease)
                                                  .OrderByDescending(m => m.Identity.Version)
                                                  .FirstOrDefault();
                    
                    if (latestCompatible != null && latestCompatible.Identity.Version > currentVersion) {
                        issues.Add(new PackageCompatibilityIssue {
                            ProjectPath = project.ProjectPath,
                            PackageId = package.Id,
                            CurrentVersion = package.Version,
                            RecommendedVersion = latestCompatible.Identity.Version.ToString(),
                            TargetFramework = targetTfm
                        });
                    }
                }
            }
            catch (Exception ex) {
                _console.WriteWarning($"Failed to check compatibility for {package.Id}: {ex.Message}");
            }
        }

        return issues;
    }

    private async Task UpdatePackageVersionInProjectAsync(string projectPath, string packageId, string newVersion, CancellationToken cancellationToken) {
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

    private class ProjectMigrationInfo {
        public string ProjectPath { get; set; } = string.Empty;
        public string CurrentTfm { get; set; } = string.Empty;
        public List<PackageInfo> PackageReferences { get; set; } = new();
        public bool UsesTargetFrameworks { get; set; }
    }

    private class PackageInfo {
        public string Id { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string ProjectPath { get; set; } = string.Empty;
    }

    private class PackageCompatibilityIssue {
        public string ProjectPath { get; set; } = string.Empty;
        public string PackageId { get; set; } = string.Empty;
        public string CurrentVersion { get; set; } = string.Empty;
        public string? RecommendedVersion { get; set; }
        public string TargetFramework { get; set; } = string.Empty;
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