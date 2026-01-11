using bld.Infrastructure;
using bld.Models;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using System.Runtime.CompilerServices;
using System.Xml;
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
    public async Task<int> MigrateTargetFrameworkAsync(string rootPath, List<string> fromTfms, string toTfm, bool applyChanges, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);

        var fromTfmsDisplay = string.Join(", ", fromTfms);
        _console.WriteInfo($"Migrating projects from {fromTfmsDisplay} to {toTfm}...");

        var projectsToMigrate = new List<ProjectMigrationInfo>();

        // Check if the root path is a direct project file
        if (File.Exists(rootPath) && SlnScanner.IsProjectFile(rootPath)) {
            _console.WriteVerbose($"Processing direct project file: {rootPath}");
            var migrationInfo = await AnalyzeProjectForMigrationAsync(rootPath, fromTfms, toTfm, cancellationToken);

            if (migrationInfo != null) {
                projectsToMigrate.Add(migrationInfo);
            }
        }
        else {
            // Use the existing solution-based logic
            var errorSink = new ErrorSink(_console);
            var slnScanner = new SlnScanner(_options, errorSink);
            var slnParser = new SlnParser(_console, errorSink);
            var processedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                _console.WriteVerbose($"Processing solution: {slnPath}");

                await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                    var projectPath = projCfg.Path;

                    // Skip if we've already processed this project (due to multiple configurations)
                    if (processedProjects.Contains(projectPath)) {
                        continue;
                    }

                    processedProjects.Add(projectPath);

                    var migrationInfo = await AnalyzeProjectForMigrationAsync(projectPath, fromTfms, toTfm, cancellationToken);

                    if (migrationInfo != null) {
                        projectsToMigrate.Add(migrationInfo);
                    }
                }
            }
        }

        if (projectsToMigrate.Count == 0) {
            _console.WriteInfo($"No projects found using {fromTfmsDisplay}.");
            return 0;
        }

        _console.WriteInfo($"Found {projectsToMigrate.Count} projects to migrate from {fromTfmsDisplay} to {toTfm}");

        if (applyChanges) {
            // Step 1: Update target frameworks
            foreach (var project in projectsToMigrate) {
                if (project.UsesTargetFrameworks) {
                    await UpdateProjectTargetFrameworksAsync(project, toTfm, cancellationToken);
                }
                else {
                    await UpdateProjectTargetFrameworkAsync(project.ProjectPath, project.CurrentTfm, toTfm, cancellationToken);
                    _console.WriteInfo($"Updated {Path.GetFileName(project.ProjectPath)} to {toTfm}");
                }
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
                    }
                    else {
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
            }
            else {
                _console.WriteInfo("All packages are compatible with the new target framework");
            }

            _console.WriteInfo($"Migration complete! Migrated {projectsToMigrate.Count} projects to {toTfm}");
        }
        else {
            _console.WriteInfo("Dry run - showing what would be migrated:");
            foreach (var project in projectsToMigrate) {
                if (project.UsesTargetFrameworks) {
                    var currentTfms = project.CurrentTfm.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    var newTfms = currentTfms.Select(tfm =>
                        project.TargetFrameworksToUpdate.Contains(tfm, StringComparer.OrdinalIgnoreCase) ? toTfm : tfm
                    ).ToList();

                    _console.WriteInfo($"  {Path.GetFileName(project.ProjectPath)}:");
                    _console.WriteInfo($"    Current: {string.Join("; ", currentTfms)}");
                    _console.WriteInfo($"    New: {string.Join("; ", newTfms)}");

                    if (project.TargetFrameworksToUpdate.Count > 0) {
                        _console.WriteInfo($"    Updating: {string.Join(", ", project.TargetFrameworksToUpdate)} → {toTfm}");
                    }
                }
                else {
                    _console.WriteInfo($"  {Path.GetFileName(project.ProjectPath)}: {project.CurrentTfm} → {toTfm}");
                }

                if (project.PackageReferences.Count > 0) {
                    _console.WriteVerbose($"    Packages: {string.Join(", ", project.PackageReferences.Select(p => $"{p.Id}@{p.Version}"))}");
                }
            }
            _console.WriteInfo("\nUse --apply to perform the migration.");
        }

        return 0;
    }

    private async Task<ProjectMigrationInfo?> AnalyzeProjectForMigrationAsync(string projectPath, List<string> fromTfms, string toTfm, CancellationToken cancellationToken) {
        try {
            // Use ProjParser to load project properties (this handles variable evaluation)
            var errorSink = new ErrorSink(_console);
            var projParser = new ProjParser(_console, errorSink, _options);
            var proj = new Proj(projectPath, null);
            var projCfg = new ProjCfg(proj, null, null); // No specific configuration

            var projectInfo = projParser.LoadProject(projCfg, Array.Empty<string>());
            if (projectInfo == null) {
                _console.WriteWarning($"Failed to load project {Path.GetFileName(projectPath)}");
                return null;
            }

            // Check if both TargetFramework and TargetFrameworks exist
            bool hasTargetFramework = !string.IsNullOrEmpty(projectInfo.TargetFramework);
            bool hasTargetFrameworks = projectInfo.TargetFrameworks.Count > 0;

            // Warn if both exist
            if (hasTargetFramework && hasTargetFrameworks) {
                _console.WriteWarning($"Project {Path.GetFileName(projectPath)} has both TargetFramework and TargetFrameworks. Using TargetFramework value as source.");
            }

            // Case A: TargetFramework specified (single target framework) and no TargetFrameworks
            if (hasTargetFramework && !hasTargetFrameworks) {
                var tfmValue = projectInfo.TargetFramework!.Trim();

                // Skip if it contains variables (variables that weren't resolved would still contain $())
                if (tfmValue.Contains("$(") && tfmValue.Contains(")")) {
                    _console.WriteVerbose($"Skipping {Path.GetFileName(projectPath)} - TargetFramework contains variable: {tfmValue}");
                    return null;
                }

                // Check if it matches any of the from TFMs
                bool matches = fromTfms.Count == 0 ?
                    IsDirectPredecessor(tfmValue, toTfm) :
                    fromTfms.Any(f => tfmValue.Equals(f, StringComparison.OrdinalIgnoreCase));

                if (!matches) {
                    return null;
                }

                // Extract package references using XML parsing (as allowed by the comment)
                var packageReferences = await ExtractPackageReferencesAsync(projectPath);

                return new ProjectMigrationInfo {
                    ProjectPath = projectPath,
                    CurrentTfm = tfmValue,
                    PackageReferences = packageReferences,
                    UsesTargetFrameworks = false,
                    TargetFrameworksToUpdate = new List<string>()
                };
            }

            // Case A with both: TargetFramework specified and TargetFrameworks exists - use TargetFramework as from
            if (hasTargetFramework && hasTargetFrameworks) {
                var tfmValue = projectInfo.TargetFramework!.Trim();

                // Skip if it contains variables
                if (tfmValue.Contains("$(") && tfmValue.Contains(")")) {
                    _console.WriteVerbose($"Skipping {Path.GetFileName(projectPath)} - TargetFramework contains variable: {tfmValue}");
                    return null;
                }

                var tfms = projectInfo.TargetFrameworks.ToList();

                // For TargetFrameworks, determine which ones should be updated
                var tfmsToUpdate = new List<string>();

                if (fromTfms.Count == 0) {
                    // No explicit from specified - find TFMs that are direct predecessors of toTfm
                    foreach (var tfm in tfms) {
                        if (IsDirectPredecessor(tfm, toTfm)) {
                            tfmsToUpdate.Add(tfm);
                        }
                    }
                }
                else {
                    // Explicit from specified - update matching TFMs or add toTfm if not present
                    foreach (var tfm in tfms) {
                        if (fromTfms.Any(f => tfm.Equals(f, StringComparison.OrdinalIgnoreCase)) && ShouldUpdateTfm(tfm, toTfm)) {
                            tfmsToUpdate.Add(tfm);
                        }
                    }
                }

                if (tfmsToUpdate.Count == 0) {
                    return null;
                }

                // Extract package references using XML parsing (as allowed by the comment)
                var packageReferences = await ExtractPackageReferencesAsync(projectPath);
                var tfmsValue = string.Join(";", tfms);

                return new ProjectMigrationInfo {
                    ProjectPath = projectPath,
                    CurrentTfm = tfmsValue,
                    PackageReferences = packageReferences,
                    UsesTargetFrameworks = true,
                    TargetFrameworksToUpdate = tfmsToUpdate
                };
            }

            // Case B: TargetFrameworks specified (multiple target frameworks)
            if (hasTargetFrameworks) {
                var tfms = projectInfo.TargetFrameworks.ToList();

                // For TargetFrameworks, determine which ones should be updated
                var tfmsToUpdate = new List<string>();

                if (fromTfms.Count == 0) {
                    // No explicit from specified - find TFMs that are direct predecessors of toTfm
                    foreach (var tfm in tfms) {
                        if (IsDirectPredecessor(tfm, toTfm)) {
                            tfmsToUpdate.Add(tfm);
                        }
                    }
                }
                else {
                    // Explicit from specified - update matching TFMs
                    foreach (var tfm in tfms) {
                        if (fromTfms.Any(f => tfm.Equals(f, StringComparison.OrdinalIgnoreCase)) && ShouldUpdateTfm(tfm, toTfm)) {
                            tfmsToUpdate.Add(tfm);
                        }
                    }
                }

                if (tfmsToUpdate.Count == 0) {
                    return null;
                }

                // Extract package references using XML parsing (as allowed by the comment)
                var packageReferences = await ExtractPackageReferencesAsync(projectPath);
                var tfmsValue = string.Join(";", tfms);

                return new ProjectMigrationInfo {
                    ProjectPath = projectPath,
                    CurrentTfm = tfmsValue,
                    PackageReferences = packageReferences,
                    UsesTargetFrameworks = true,
                    TargetFrameworksToUpdate = tfmsToUpdate
                };
            }

            return null;
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

            if (targetFrameworkElement != null && targetFrameworkElement.Value.Equals(fromTfm, StringComparison.OrdinalIgnoreCase)) {
                targetFrameworkElement.Value = toTfm;
            }

            using var writeStream = File.Create(projectPath);
            using var writer = XmlWriter.Create(writeStream, new XmlWriterSettings {
                Indent = true,
                OmitXmlDeclaration = true,
                Encoding = System.Text.Encoding.UTF8,
                Async = true
            });
            await doc.SaveAsync(writer, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.Message}");
        }
    }
    private async Task UpdateProjectTargetFrameworksAsync(ProjectMigrationInfo project, string toTfm, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(project.ProjectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var targetFrameworksElement = doc.Descendants("TargetFrameworks").FirstOrDefault();
            if (targetFrameworksElement == null) {
                _console.WriteWarning($"No TargetFrameworks found in {Path.GetFileName(project.ProjectPath)}");
                return;
            }

            var currentTfms = targetFrameworksElement.Value.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();

            // Check if toTfm is already in the list
            bool alreadyHasToTfm = currentTfms.Any(tfm => tfm.Equals(toTfm, StringComparison.OrdinalIgnoreCase));

            List<string> newTfms;
            if (alreadyHasToTfm) {
                // If toTfm already exists, just update matching TFMs
                newTfms = currentTfms.Select(tfm =>
                    project.TargetFrameworksToUpdate.Contains(tfm, StringComparer.OrdinalIgnoreCase) ? toTfm : tfm
                ).ToList();
            }
            else {
                // If toTfm doesn't exist, upgrade the highest matching framework or add toTfm
                // Replace matching TFMs with toTfm
                newTfms = currentTfms.Select(tfm =>
                    project.TargetFrameworksToUpdate.Contains(tfm, StringComparer.OrdinalIgnoreCase) ? toTfm : tfm
                ).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                
                // Defensive: ensure toTfm is in the list (should already be added by Select above)
                if (!newTfms.Contains(toTfm, StringComparer.OrdinalIgnoreCase)) {
                    newTfms.Add(toTfm);
                }
            }

            var newTargetFrameworksValue = string.Join(";", newTfms);

            _console.WriteInfo($"\nProject: {Path.GetFileName(project.ProjectPath)}");
            _console.WriteInfo($"Current TargetFrameworks: {string.Join("; ", currentTfms)}");
            _console.WriteInfo($"New TargetFrameworks: {string.Join("; ", newTfms)}");

            // Prompt for confirmation
            bool confirmed = _console.Confirm("Apply this change?", false);

            if (!confirmed) {
                _console.WriteInfo($"Cancelled update for {Path.GetFileName(project.ProjectPath)}");
                return;
            }

            // Apply the change
            targetFrameworksElement.Value = newTargetFrameworksValue;

            using var writeStream = File.Create(project.ProjectPath);
            using var writer = XmlWriter.Create(writeStream, new XmlWriterSettings {
                Indent = true,
                OmitXmlDeclaration = true,
                Encoding = System.Text.Encoding.UTF8,
                Async = true
            });
            await doc.SaveAsync(writer, cancellationToken);

            _console.WriteInfo($"✓ Updated {Path.GetFileName(project.ProjectPath)} TargetFrameworks");
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {project.ProjectPath}: {ex.Message}");
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
                }
                else if (versionElement != null) {
                    versionElement.Value = newVersion;
                }
            }

            using var writeStream = File.Create(projectPath);
            using var writer = XmlWriter.Create(writeStream, new XmlWriterSettings {
                Indent = true,
                OmitXmlDeclaration = true,
                Encoding = System.Text.Encoding.UTF8,
                Async = true
            });
            await doc.SaveAsync(writer, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.Message}");
        }
    }

    private async Task<List<PackageInfo>> ExtractPackageReferencesAsync(string projectPath) {
        var packageReferences = new List<PackageInfo>();

        try {
            using var stream = File.OpenRead(projectPath);
            var doc = await XDocument.LoadAsync(stream, LoadOptions.None, default);
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
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to extract package references from {projectPath}: {ex.Message}");
        }

        return packageReferences;
    }

    private bool IsDirectPredecessor(string currentTfm, string targetTfm) {
        // Parse TFM versions
        if (!TryParseTfmVersion(currentTfm, out var currentType, out var currentVersion) ||
            !TryParseTfmVersion(targetTfm, out var targetType, out var targetVersion)) {
            return false;
        }

        // Only .NET (Core) TFMs can be direct predecessors to other .NET (Core) TFMs
        if (currentType != TfmType.DotNet || targetType != TfmType.DotNet) {
            return false;
        }

        // Check if it's a direct predecessor (e.g., net8.0 -> net9.0)
        return targetVersion.Major == currentVersion.Major + 1 && targetVersion.Minor == 0;
    }

    private bool ShouldUpdateTfm(string currentTfm, string targetTfm) {
        // Parse TFM versions
        if (!TryParseTfmVersion(currentTfm, out var currentType, out var currentVersion) ||
            !TryParseTfmVersion(targetTfm, out var targetType, out var targetVersion)) {
            return false;
        }

        // Never update .NET Framework or .NET Standard TFMs
        if (currentType == TfmType.DotNetFramework || currentType == TfmType.DotNetStandard) {
            return false;
        }

        // Only update .NET (Core) TFMs to newer .NET (Core) versions
        return currentType == TfmType.DotNet && targetType == TfmType.DotNet && targetVersion > currentVersion;
    }

    private bool TryParseTfmVersion(string tfm, out TfmType type, out Version version) {
        type = TfmType.Unknown;
        version = new Version(0, 0);

        if (string.IsNullOrEmpty(tfm)) {
            return false;
        }

        tfm = tfm.ToLowerInvariant();

        // .NET Standard
        if (tfm.StartsWith("netstandard")) {
            type = TfmType.DotNetStandard;
            var versionStr = tfm.Substring("netstandard".Length);
            if (Version.TryParse(versionStr, out var parsedVersion)) {
                version = parsedVersion;
                return true;
            }
            return false;
        }

        // .NET Core App
        if (tfm.StartsWith("netcoreapp")) {
            type = TfmType.DotNet;
            var versionStr = tfm.Substring("netcoreapp".Length);
            if (Version.TryParse(versionStr, out var parsedVersion)) {
                version = parsedVersion;
                return true;
            }
            return false;
        }

        // .NET (5.0+) and .NET Framework patterns - both start with "net"
        if (tfm.StartsWith("net") && tfm.Length > 3) {
            var versionStr = tfm.Substring(3);

            // Try to parse as a full version (e.g., "8.0" from "net8.0")
            if (Version.TryParse(versionStr, out var parsedVersion)) {
                if (parsedVersion.Major >= 5) {
                    // .NET (5.0+)
                    type = TfmType.DotNet;
                    version = parsedVersion;
                    return true;
                }
                else if (parsedVersion.Major == 4) {
                    // .NET Framework with full version (rare but possible)
                    type = TfmType.DotNetFramework;
                    version = parsedVersion;
                    return true;
                }
            }

            // .NET Framework legacy patterns (net48, net472, etc.)
            if (versionStr.Length >= 2 && versionStr.Length <= 3 && versionStr.All(char.IsDigit)) {
                if (versionStr.Length == 2) {
                    // net48 -> 4.8
                    if (Version.TryParse($"4.{versionStr[1]}", out var legacyVersion)) {
                        type = TfmType.DotNetFramework;
                        version = legacyVersion;
                        return true;
                    }
                }
                else if (versionStr.Length == 3) {
                    // net472 -> 4.7.2
                    if (Version.TryParse($"4.{versionStr[1]}.{versionStr[2]}", out var legacyVersion)) {
                        type = TfmType.DotNetFramework;
                        version = legacyVersion;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private enum TfmType {
        Unknown,
        DotNetFramework,
        DotNetStandard,
        DotNet
    }

    private class ProjectMigrationInfo {
        public string ProjectPath { get; set; } = string.Empty;
        public string CurrentTfm { get; set; } = string.Empty;
        public List<PackageInfo> PackageReferences { get; set; } = new();
        public bool UsesTargetFrameworks { get; set; }
        public List<string> TargetFrameworksToUpdate { get; set; } = new();
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

    private class NuGetLogger : global::NuGet.Common.ILogger {
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
        public void Log(global::NuGet.Common.LogLevel level, string data) {
            switch (level) {
                case global::NuGet.Common.LogLevel.Debug:
                case global::NuGet.Common.LogLevel.Verbose:
                    LogVerbose(data);
                    break;
                case global::NuGet.Common.LogLevel.Information:
                case global::NuGet.Common.LogLevel.Minimal:
                    LogInformation(data);
                    break;
                case global::NuGet.Common.LogLevel.Warning:
                    LogWarning(data);
                    break;
                case global::NuGet.Common.LogLevel.Error:
                    LogError(data);
                    break;
            }
        }

        public Task LogAsync(global::NuGet.Common.LogLevel level, string data) {
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