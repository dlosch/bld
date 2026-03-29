using bld.Infrastructure;
using bld.Models;
using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class TfmService : IDisposable {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;
    private readonly SourceCacheContext _cache;
    private readonly ILogger _logger;
    private bool _disposed;

    public TfmService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
        _cache = new SourceCacheContext();
        _logger = new NuGetLogger(_console);
    }

    public void Dispose() {
        if (!_disposed) {
            _cache.Dispose();
            _disposed = true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> MigrateTargetFrameworkAsync(string rootPath, List<string> fromTfms, string toTfm, bool applyChanges, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);

        var fromTfmsDisplay = string.Join(", ", fromTfms);
        _console.WriteInfo($"Migrating projects from {fromTfmsDisplay} to {toTfm}...");

        var projectsToMigrate = new ConcurrentBag<ProjectMigrationInfo>();
        var eolTfms = await GetEolTfmsAsync(cancellationToken);

        // Display EOL TFMs information
        var eolFromTfms = fromTfms.Where(tfm => IsEolTfm(tfm, eolTfms)).ToList();
        if (eolFromTfms.Count > 0) {
            _console.WriteWarning($"End-of-life target frameworks detected: {string.Join(", ", eolFromTfms)}");
        }

        // Check if the root path is a direct project file
        if (File.Exists(rootPath) && SlnScanner.IsProjectFile(rootPath)) {
            _console.WriteVerbose($"Processing direct project file: {rootPath}");
            var migrationInfo = await AnalyzeProjectForMigrationAsync(rootPath, fromTfms, toTfm, eolTfms, cancellationToken);

            if (migrationInfo != null) {
                projectsToMigrate.Add(migrationInfo);
            }
        }
        else {
            // Use the existing solution-based logic
            var errorSink = new ErrorSink(_console);
            var slnScanner = new SlnScanner(_options, errorSink);
            var slnParser = new SlnParser(_console, errorSink);
            var fileSystem = new FileSystem(_console, errorSink);
            var cache = new ProjCfgCache(_console);

            var parallelOptions = new ParallelOptions {
                MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism
            };

            var allSlns = new ConcurrentBag<string>();
            await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                allSlns.Add(slnPath);
            }

            var allProjCfgs = new ConcurrentBag<ProjCfg>();
            await Parallel.ForEachAsync(allSlns, parallelOptions, async (slnPath, ct) => {
                await foreach (var projCfg in slnParser.ParseSolution(slnPath, fileSystem)) {
                    if (cache.Add(projCfg)) {
                        allProjCfgs.Add(projCfg);
                    }
                }
            });

            await _console.StartStatusAsync($"Analyzing {allProjCfgs.Count} project configurations...", async ctx => {
                var count = 0;
                var total = allProjCfgs.Count;

                await Parallel.ForEachAsync(allProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Path.GetFileName(projCfg.Path)}[/])");

                    var migrationInfo = await AnalyzeProjectForMigrationAsync(projCfg.Path, fromTfms, toTfm, eolTfms, cancellationToken);

                    if (migrationInfo != null) {
                        projectsToMigrate.Add(migrationInfo);
                    }
                });
            });
        }

        if (projectsToMigrate.Count == 0) {
            _console.WriteLine($"No projects to migrate found.");
            return 0;
        }

        _console.WriteLine($"Found {projectsToMigrate.Count} projects to migrate from {fromTfmsDisplay} to {toTfm}");

        if (applyChanges) {
            // Step 1: Update target frameworks
            foreach (var project in projectsToMigrate) {
                if (project.UsesTargetFrameworks) {
                    await UpdateProjectTargetFrameworksAsync(project, toTfm, eolTfms, cancellationToken);
                }
                else {
                    await UpdateProjectTargetFrameworkAsync(project.ProjectPath, project.CurrentTfm, toTfm, eolTfms, cancellationToken);
                    _console.WriteLine($"Updated {Path.GetFileName(project.ProjectPath)} to {toTfm}");
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
                        _console.WriteLine($"    → Recommended: {issue.RecommendedVersion}");
                    }
                    else {
                        _console.WriteError($"    → No compatible version found for {toTfm}");
                    }
                }

                // Update packages with compatible versions
                var updatedPackages = 0;
                foreach (var issue in compatibilityIssues.Where(i => !string.IsNullOrEmpty(i.RecommendedVersion))) {
                    await UpdatePackageVersionInProjectAsync(issue.ProjectPath, issue.PackageId, issue.RecommendedVersion!, cancellationToken);
                    _console.WriteLine($"Updated {issue.PackageId} to {issue.RecommendedVersion} in {Path.GetFileName(issue.ProjectPath)}");
                    updatedPackages++;
                }

                if (updatedPackages > 0) {
                    _console.WriteLine($"Updated {updatedPackages} packages for {toTfm} compatibility");
                }
            }
            else {
                _console.WriteLine("All packages are compatible with the new target framework");
            }

            _console.WriteLine($"Migration complete! Migrated {projectsToMigrate.Count} projects to {toTfm}");
        }
        else {
            var actualMigrated = projectsToMigrate.Where(project => {
                if (project.UsesTargetFrameworks) {
                    var currentTfms = project.CurrentTfm.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    var newTfms = GetUpdatedTfms(currentTfms, toTfm, eolTfms);
                    return !Enumerable.SequenceEqual(currentTfms.OrderBy(t => t), newTfms.OrderBy(t => t), StringComparer.OrdinalIgnoreCase);
                }
                else {
                    return !project.CurrentTfm.Equals(toTfm, StringComparison.OrdinalIgnoreCase);
                }
            }).ToList();

            if (actualMigrated.Count == 0) {
                _console.WriteLine("Dry run - no projects require target framework changes.");
                return 0;
            }

            _console.WriteLine("Dry run - showing what would be migrated:");

            var uniqueProjects = actualMigrated
                .GroupBy(p => p.ProjectPath)
                .Select(g => g.First())
                .OrderBy(p => Path.GetFileName(p.ProjectPath));

            if (_options.MarkdownOutput) {
                var rows = uniqueProjects.Select(project => {
                    string oldTfm;
                    string newTfm;

                    if (project.UsesTargetFrameworks) {
                        var currentTfms = project.CurrentTfm.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                        var newTfmsList = GetUpdatedTfms(currentTfms, toTfm, eolTfms);

                        oldTfm = string.Join(", ", currentTfms.Select(t =>
                            IsEolTfm(t, eolTfms) ? $"{t} (EOL)" : t));
                        newTfm = string.Join(", ", newTfmsList);
                    }
                    else {
                        oldTfm = IsEolTfm(project.CurrentTfm, eolTfms)
                            ? $"{project.CurrentTfm} (EOL)"
                            : project.CurrentTfm;
                        newTfm = toTfm;
                    }

                    return (IReadOnlyList<string?>)new[] {
                        Path.GetFileName(project.ProjectPath),
                        oldTfm,
                        newTfm
                    };
                });

                MarkdownTableFormatter.Write(_console, "TFM migration dry-run (markdown)", new[] { "Project", "Old TFM", "New TFM" }, rows);
            }
            else {
                var table = new Table();
                table.AddColumn("Project");
                table.AddColumn("Old TFM");
                table.AddColumn("New TFM");

                foreach (var project in uniqueProjects) {
                    var projectName = Markup.Escape(Path.GetFileName(project.ProjectPath));
                    string oldTfm;
                    string newTfm;

                    if (project.UsesTargetFrameworks) {
                        var currentTfms = project.CurrentTfm.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                        var newTfmsList = GetUpdatedTfms(currentTfms, toTfm, eolTfms);

                        oldTfm = string.Join(", ", currentTfms.Select(t =>
                            IsEolTfm(t, eolTfms) ? $"[red]{Markup.Escape(t)} [[EOL]][/]" : Markup.Escape(t)));
                        newTfm = string.Join(", ", newTfmsList.Select(Markup.Escape));
                    }
                    else {
                        oldTfm = IsEolTfm(project.CurrentTfm, eolTfms)
                            ? $"[red]{Markup.Escape(project.CurrentTfm)} [[EOL]][/]"
                            : Markup.Escape(project.CurrentTfm);
                        newTfm = Markup.Escape(toTfm);
                    }

                    table.AddRow(projectName, oldTfm, newTfm);
                }

                _console.WriteTable(table);
            }
            _console.WriteLine("\nUse --apply to perform the migration.");
        }

        return 0;
    }

    private async Task<ProjectMigrationInfo?> AnalyzeProjectForMigrationAsync(string projectPath, List<string> fromTfms, string toTfm, ISet<string> eolTfms, CancellationToken cancellationToken) {
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

                // Check if it matches any of the from TFMs or should be updated due to EOL/newer TFM
                bool matches = fromTfms.Count == 0 ?
                    IsDirectPredecessor(tfmValue, toTfm) :
                    fromTfms.Any(f => tfmValue.Equals(f, StringComparison.OrdinalIgnoreCase));

                var shouldUpdate = matches || IsEolTfm(tfmValue, eolTfms) || ShouldUpdateTfm(tfmValue, toTfm);

                if (!shouldUpdate) {
                    return null;
                }

                // If already at target framework and not EOL, nothing to do
                if (tfmValue.Equals(toTfm, StringComparison.OrdinalIgnoreCase) && !IsEolTfm(tfmValue, eolTfms)) {
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

                // For TargetFrameworks, determine which ones should be updated (for reporting)
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

                var shouldUpdate = tfmsToUpdate.Count > 0
                    || tfms.Any(t => IsEolTfm(t, eolTfms))
                    || IsNewerThanAny(toTfm, tfms);

                if (!shouldUpdate) {
                    return null;
                }

                // Verify if GetUpdatedTfms actually makes changes
                var updatedTfms = GetUpdatedTfms(tfms, toTfm, eolTfms);
                if (Enumerable.SequenceEqual(tfms.OrderBy(t => t), updatedTfms.OrderBy(t => t), StringComparer.OrdinalIgnoreCase)) {
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

                var shouldUpdate = tfmsToUpdate.Count > 0
                    || tfms.Any(t => IsEolTfm(t, eolTfms))
                    || IsNewerThanAny(toTfm, tfms);

                if (!shouldUpdate) {
                    return null;
                }

                // Verify if GetUpdatedTfms actually makes changes
                var updatedTfms = GetUpdatedTfms(tfms, toTfm, eolTfms);
                if (Enumerable.SequenceEqual(tfms.OrderBy(t => t), updatedTfms.OrderBy(t => t), StringComparer.OrdinalIgnoreCase)) {
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
            _console.WriteWarning($"Failed to analyze {projectPath}: {ex.FormatMessage()}");
            return null;
        }
    }

    private async Task UpdateProjectTargetFrameworkAsync(string projectPath, string fromTfm, string toTfm, ISet<string> eolTfms, CancellationToken cancellationToken) {
        try {
            XDocument doc;
            using (var readStream = File.OpenRead(projectPath)) {
                doc = await XDocument.LoadAsync(readStream, LoadOptions.PreserveWhitespace, cancellationToken);
            }

            var targetFrameworkElement = doc.Descendants("TargetFramework").FirstOrDefault();

            if (targetFrameworkElement != null && targetFrameworkElement.Value.Equals(fromTfm, StringComparison.OrdinalIgnoreCase)) {
                // If current TFM is EOL or the target is newer, update to target
                if (IsEolTfm(fromTfm, eolTfms) || ShouldUpdateTfm(fromTfm, toTfm)) {
                    targetFrameworkElement.Value = toTfm;
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
            _console.WriteError($"Failed to update {projectPath}: {ex.FormatMessage()}");
        }
    }
    private async Task UpdateProjectTargetFrameworksAsync(ProjectMigrationInfo project, string toTfm, ISet<string> eolTfms, CancellationToken cancellationToken) {
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
            var newTfms = GetUpdatedTfms(currentTfms, toTfm, eolTfms);

            var newTargetFrameworksValue = string.Join(";", newTfms);

            _console.WriteLine($"\nProject: {Path.GetFileName(project.ProjectPath)}");
            _console.WriteLine($"Current TargetFrameworks: {string.Join("; ", currentTfms)}");
            _console.WriteLine($"New TargetFrameworks: {string.Join("; ", newTfms)}");

            // Prompt for confirmation
            bool confirmed = _console.Confirm("Apply this change?", false);

            if (!confirmed) {
                _console.WriteLine($"Cancelled update for {Path.GetFileName(project.ProjectPath)}");
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

            _console.WriteLine($"✓ Updated {Path.GetFileName(project.ProjectPath)} TargetFrameworks");
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {project.ProjectPath}: {ex.FormatMessage()}");
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
                _console.WriteWarning($"Failed to check compatibility for {package.Id}: {ex.FormatMessage()}");
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
            _console.WriteError($"Failed to update {projectPath}: {ex.FormatMessage()}");
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
            _console.WriteWarning($"Failed to extract package references from {projectPath}: {ex.FormatMessage()}");
        }

        return packageReferences;
    }

    private List<string> GetUpdatedTfms(List<string> currentTfms, string toTfm, ISet<string> eolTfms) {
        // Remove EOL TFMs
        var filteredTfms = currentTfms
            .Where(tfm => !IsEolTfm(tfm, eolTfms))
            .ToList();

        // If a newer TFM is available, add it (do not replace existing TFMs)
        var alreadyHasToTfm = filteredTfms.Any(tfm => tfm.Equals(toTfm, StringComparison.OrdinalIgnoreCase));
        if (!alreadyHasToTfm && IsNewerThanAny(toTfm, filteredTfms)) {
            filteredTfms.Add(toTfm);
        }

        return filteredTfms;
    }

    private static bool IsEolTfm(string tfm, ISet<string> eolTfms) {
        if (string.IsNullOrWhiteSpace(tfm)) return false;
        var normalized = tfm.Trim().ToLowerInvariant();
        return eolTfms.Contains(normalized);
    }

    private bool IsNewerThanAny(string candidateTfm, IEnumerable<string> existingTfms) {
        if (!TryParseTfmVersion(candidateTfm, out var candidateType, out var candidateVersion)) return false;
        var foundComparable = false;
        foreach (var tfm in existingTfms) {
            if (!TryParseTfmVersion(tfm, out var type, out var version)) continue;
            if (type != candidateType) continue;
            foundComparable = true;
            if (candidateVersion <= version) return false;
        }

        return foundComparable;
    }

    private async Task<HashSet<string>> GetEolTfmsAsync(CancellationToken cancellationToken) {
        const string releasesIndexUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/releases-index.json";
        try {
            using var client = new HttpClient();
            var index = await client.GetFromJsonAsync<ReleasesIndex>(releasesIndexUrl, cancellationToken);

            if (index?.Channels is null) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var eolTfms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var today = DateTime.UtcNow.Date;

            foreach (var channel in index.Channels) {
                if (string.IsNullOrWhiteSpace(channel.ChannelVersion)) continue;

                var isEol = string.Equals(channel.SupportPhase, "eol", StringComparison.OrdinalIgnoreCase)
                    || (channel.EolDate.HasValue && channel.EolDate.Value.Date <= today);

                if (!isEol) continue;

                // Map channel version to TFM (net5+ => netX.Y, netcoreapp for <5)
                if (Version.TryParse(channel.ChannelVersion, out var version)) {
                    var tfm = version.Major >= 5
                        ? $"net{version.Major}.{version.Minor}"
                        : $"netcoreapp{version.Major}.{version.Minor}";
                    eolTfms.Add(tfm.ToLowerInvariant());
                }
            }

            return eolTfms;
        }
        catch (Exception ex) {
            _console.WriteWarning($"Failed to load .NET release metadata: {ex.FormatMessage()}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
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

    private sealed record ReleasesIndex(
        [property: JsonPropertyName("releases-index")] List<ReleaseChannel> Channels
    );

    private sealed record ReleaseChannel(
        [property: JsonPropertyName("channel-version")] string ChannelVersion,
        [property: JsonPropertyName("support-phase")] string? SupportPhase,
        [property: JsonPropertyName("eol-date")] DateTime? EolDate
    );

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