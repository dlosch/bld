using bld.Infrastructure;
using bld.Models;
using NuGet.Frameworks;
using NuGet.Versioning;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class TfmService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public TfmService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    /// <param name="maxBump">Cap for <paramref name="updatePackages"/>, as in <c>outdated --max-bump</c>.</param>
    /// <param name="policies">Package policies for <paramref name="updatePackages"/>; null applies none.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> MigrateTargetFrameworkAsync(string rootPath, List<string> fromTfms, string toTfm, bool applyChanges, bool updatePackages, MaxBump maxBump, PolicyService? policies, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);

        // One sink for the whole run: solution and project load failures used to be collected and
        // then dropped, so a project that failed to parse simply went missing from the migration.
        var errorSink = new ErrorSink(_console);
        var exitCode = await MigrateCoreAsync(rootPath, fromTfms, toTfm, applyChanges, updatePackages, maxBump, policies, errorSink, cancellationToken);
        errorSink.WriteTo();
        return errorSink.HasErrors ? 1 : exitCode;
    }

    private async Task<int> MigrateCoreAsync(string rootPath, List<string> fromTfms, string toTfm, bool applyChanges, bool updatePackages, MaxBump maxBump, PolicyService? policies, ErrorSink errorSink, CancellationToken cancellationToken) {
        var fromTfmsDisplay = string.Join(", ", fromTfms);
        _console.WriteInfo($"Migrating projects from {fromTfmsDisplay} to {toTfm}...");

        var projectsToMigrate = new ConcurrentBag<ProjectMigrationInfo>();
        var eolTfms = await GetEolTfmsAsync(cancellationToken);
        // One parser for the run: its project collections cache the SDK import chain across projects.
        using var projParser = new ProjParser(_console, errorSink, _options);

        // Display EOL TFMs information
        var eolFromTfms = fromTfms.Where(tfm => IsEolTfm(tfm, eolTfms)).ToList();
        if (eolFromTfms.Count > 0) {
            _console.WriteWarning($"End-of-life target frameworks detected: {string.Join(", ", eolFromTfms)}");
        }

        // Check if the root path is a direct project file
        if (File.Exists(rootPath) && SlnScanner.IsProjectFile(rootPath)) {
            _console.WriteVerbose($"Processing direct project file: {rootPath}");
            var migrationInfo = await AnalyzeProjectForMigrationAsync(projParser, rootPath, fromTfms, toTfm, eolTfms, cancellationToken);

            if (migrationInfo != null) {
                projectsToMigrate.Add(migrationInfo);
            }
        }
        else {
            // Use the existing solution-based logic
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
                    ctx.Status($"Analyzing projects: {current}/{total} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");

                    var migrationInfo = await AnalyzeProjectForMigrationAsync(projParser, projCfg.Path, fromTfms, toTfm, eolTfms, cancellationToken);

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

        // A project appears once per evaluated configuration; collapse to one entry per file
        // so it is reported, written, and counted exactly once across preview and apply.
        var distinctProjects = projectsToMigrate
            .GroupBy(p => p.ProjectPath)
            .Select(g => g.First())
            .ToList();

        _console.WriteLine($"Found {distinctProjects.Count} projects to migrate from {fromTfmsDisplay} to {toTfm}");

        var migrated = 0;
        var notMigrated = 0;
        if (applyChanges) {
            // Step 1: Update target frameworks. Count what was actually written - the previous code
            // printed "Updated X" and a final "Migrated N projects" regardless of whether any element
            // matched, so a no-op run looked identical to a successful one.
            foreach (var project in distinctProjects) {
                var written = project.UsesTargetFrameworks
                    ? await UpdateProjectTargetFrameworksAsync(project, fromTfms, toTfm, eolTfms, cancellationToken)
                    : await UpdateProjectTargetFrameworkAsync(project.ProjectPath, project.CurrentTfm, toTfm, eolTfms, cancellationToken);

                if (written) {
                    migrated++;
                    if (!project.UsesTargetFrameworks) _console.WriteLine($"Updated {Path.GetFileName(project.ProjectPath)} to {toTfm}");
                }
                else {
                    notMigrated++;
                    _console.WriteWarning($"{Path.GetFileName(project.ProjectPath)} was not migrated.");
                }
            }

            if (notMigrated > 0) {
                _console.WriteWarning($"Migration finished: {migrated} project(s) updated to {toTfm}, {notMigrated} left unchanged.");
                if (updatePackages) _console.WriteWarning("--update-packages skipped: the packages would be checked against a partly migrated solution. Fix the projects above and run `bld outdated --apply` on the same input.");
                return 1;
            }
            _console.WriteLine($"Migration complete! Migrated {migrated} projects to {toTfm}");

            // Step 2 (opt-in): update packages through `outdated --apply` over the same input. It
            // runs after the frameworks are written, so the evaluation it checks compatibility
            // against already shows the new target. That gives the migration the full outdated
            // behaviour - framework check, bump cap, policies, dependency check, central package
            // management - instead of the latest-stable bump it used to do on its own.
            if (updatePackages) {
                _console.WriteRule("[bold yellow]Package updates for the migrated frameworks[/]");
                var outdated = new OutdatedService(_console, _options) { JournalCommand = "tfm --update-packages" };
                var packageExit = await outdated.CheckOutdatedPackagesAsync(
                    rootPath, updatePackages: true, skipTfmCheck: false, includePrerelease: false,
                    listOrphans: false, commentOrphans: false, interactive: false, maxBump,
                    Array.Empty<(string, MaxBump)>(), Array.Empty<string>(), Array.Empty<string>(),
                    allowConflicts: false, verifyRestore: false, Array.Empty<string>(), ignoreSourceMapping: false,
                    GroupingOptions.Default, Preselect.All, evalCache: false, policies, ignorePolicy: false, cancellationToken);
                if (packageExit != 0) return packageExit;
            }
        }
        else {
            var actualMigrated = distinctProjects.Where(project => {
                if (project.UsesTargetFrameworks) {
                    var currentTfms = project.CurrentTfm.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();
                    var newTfms = GetUpdatedTfms(currentTfms, fromTfms, toTfm, eolTfms);
                    return !Enumerable.SequenceEqual(currentTfms.OrderBy(t => t), newTfms.OrderBy(t => t), StringComparer.OrdinalIgnoreCase);
                }
                else {
                    // Match the writer's eligibility rules so the dry run cannot advertise a migration
                    // that --apply then declines to perform.
                    return WillUpdateSingleTfm(project.CurrentTfm, toTfm, eolTfms);
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
                        var newTfmsList = GetUpdatedTfms(currentTfms, fromTfms, toTfm, eolTfms);

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
                        var newTfmsList = GetUpdatedTfms(currentTfms, fromTfms, toTfm, eolTfms);

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
            if (updatePackages) {
                // Checking packages now would test them against the frameworks the projects still
                // have; the real check runs against the new target after the migration is written.
                _console.WriteLine($"With --apply, --update-packages then runs `outdated --apply` against {toTfm} on the same input.");
            }
        }

        return 0;
    }

    private async Task<ProjectMigrationInfo?> AnalyzeProjectForMigrationAsync(ProjParser projParser, string projectPath, List<string> fromTfms, string toTfm, ISet<string> eolTfms, CancellationToken cancellationToken) {
        try {
            // Use ProjParser to load project properties (this handles variable evaluation)
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


                return new ProjectMigrationInfo {
                    ProjectPath = projectPath,
                    CurrentTfm = tfmValue,
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
                var updatedTfms = GetUpdatedTfms(tfms, fromTfms, toTfm, eolTfms);
                if (Enumerable.SequenceEqual(tfms.OrderBy(t => t), updatedTfms.OrderBy(t => t), StringComparer.OrdinalIgnoreCase)) {
                    return null;
                }

                var tfmsValue = string.Join(";", tfms);

                return new ProjectMigrationInfo {
                    ProjectPath = projectPath,
                    CurrentTfm = tfmsValue,
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
                var updatedTfms = GetUpdatedTfms(tfms, fromTfms, toTfm, eolTfms);
                if (Enumerable.SequenceEqual(tfms.OrderBy(t => t), updatedTfms.OrderBy(t => t), StringComparer.OrdinalIgnoreCase)) {
                    return null;
                }

                var tfmsValue = string.Join(";", tfms);

                return new ProjectMigrationInfo {
                    ProjectPath = projectPath,
                    CurrentTfm = tfmsValue,
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

    /// <summary>
    /// Whether a single-target project is actually eligible. The dry run used only "current != target"
    /// while the writer additionally required this, so the preview listed migrations that --apply then
    /// silently declined to make while still reporting success.
    /// </summary>
    internal bool WillUpdateSingleTfm(string fromTfm, string toTfm, ISet<string> eolTfms) =>
        !fromTfm.Equals(toTfm, StringComparison.OrdinalIgnoreCase)
        && (IsEolTfm(fromTfm, eolTfms) || ShouldUpdateTfm(fromTfm, toTfm));

    private async Task<bool> UpdateProjectTargetFrameworkAsync(string projectPath, string fromTfm, string toTfm, ISet<string> eolTfms, CancellationToken cancellationToken) {
        if (!WillUpdateSingleTfm(fromTfm, toTfm, eolTfms)) {
            _console.WriteVerbose($"Skipping {Path.GetFileName(projectPath)}: {fromTfm} -> {toTfm} is not a supported migration.");
            return false;
        }

        try {
            return await XmlProjectFile.EditAsync(projectPath, doc => {
                var targetFrameworkElement = FindTargetFrameworkProperty(doc, "TargetFramework", fromTfm);
                if (targetFrameworkElement is null) {
                    _console.WriteWarning($"No unambiguous <TargetFramework> to update in {Path.GetFileName(projectPath)}; it may be conditional or set in an imported file.");
                    return false;
                }

                targetFrameworkElement.Value = toTfm;
                return true;
            }, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.FormatMessage()}", ex);
            return false;
        }
    }
    private async Task<bool> UpdateProjectTargetFrameworksAsync(ProjectMigrationInfo project, List<string> fromTfms, string toTfm, ISet<string> eolTfms, CancellationToken cancellationToken) {
        try {
            var written = await XmlProjectFile.EditAsync(project.ProjectPath, doc => {
                var targetFrameworksElement = FindTargetFrameworkProperty(doc, "TargetFrameworks", project.CurrentTfm);
                if (targetFrameworksElement == null) {
                    _console.WriteWarning($"No unambiguous <TargetFrameworks> to update in {Path.GetFileName(project.ProjectPath)}; it may be conditional or set in an imported file.");
                    return false;
                }

                var currentTfms = targetFrameworksElement.Value.Split(';').Select(t => t.Trim()).Where(t => !string.IsNullOrEmpty(t)).ToList();

                // The preview was computed from the evaluated list; recomputing from raw XML that still
                // contains a property reference produces a different result (e.g. keeping an EOL TFM
                // that came in via $(TargetFrameworks)). Leave those for a human.
                if (currentTfms.Any(t => t.Contains("$("))) {
                    _console.WriteWarning($"Skipping {Path.GetFileName(project.ProjectPath)}: <TargetFrameworks> contains a property reference ({targetFrameworksElement.Value}).");
                    return false;
                }

                var newTfms = GetUpdatedTfms(currentTfms, fromTfms, toTfm, eolTfms);
                var newTargetFrameworksValue = string.Join(";", newTfms);

                _console.WriteLine($"\nProject: {Path.GetFileName(project.ProjectPath)}");
                _console.WriteLine($"Current TargetFrameworks: {string.Join("; ", currentTfms)}");
                _console.WriteLine($"New TargetFrameworks: {string.Join("; ", newTfms)}");

                // --apply is the user's consent (matching the single-target path); previously this
                // prompted inside EditAsync, which threw in non-interactive/CI runs and was then
                // misreported as a file-write failure.
                targetFrameworksElement.Value = newTargetFrameworksValue;
                return true;
            }, cancellationToken);

            if (written) {
                _console.WriteLine($"✓ Updated {Path.GetFileName(project.ProjectPath)} TargetFrameworks");
            }
            return written;
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {project.ProjectPath}: {ex.FormatMessage()}", ex);
            return false;
        }
    }

    // Computes the resulting TFM list. EOL TFMs are dropped. With an explicit --from, matched source
    // TFMs are *replaced* by toTfm (a real migration, consistent with the single-target path); without
    // --from (auto-detect) toTfm is added when it is newer, preserving the existing TFMs.
    internal List<string> GetUpdatedTfms(List<string> currentTfms, List<string> fromTfms, string toTfm, ISet<string> eolTfms) {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tfm in currentTfms) {
            // Match --from *before* dropping end-of-life frameworks. The other order discarded the very
            // TFM the user asked to migrate, so "--from net6.0 --to net10.0" on <net6.0;net7.0> produced
            // an empty list and wrote <TargetFrameworks></TargetFrameworks>.
            if (fromTfms.Count > 0 && fromTfms.Any(f => tfm.Equals(f, StringComparison.OrdinalIgnoreCase))) {
                if (seen.Add(toTfm)) result.Add(toTfm); // replace the matched source TFM with the target
                continue;
            }

            if (IsEolTfm(tfm, eolTfms)) continue; // drop end-of-life frameworks

            if (seen.Add(tfm)) result.Add(tfm);
        }

        // Auto-detect / EOL-only path: add the target when it is newer than everything kept.
        if (!seen.Contains(toTfm) && IsNewerThanAny(toTfm, result)) {
            result.Add(toTfm);
        }

        // Never produce an empty list: dropping every entry as end-of-life would otherwise leave the
        // project with no target framework at all, which does not build.
        if (result.Count == 0) {
            result.Add(toTfm);
        }

        return result;
    }

    /// <summary>
    /// The property element MSBuild would actually have evaluated: a direct child of a PropertyGroup,
    /// carrying no Condition, and (when known) holding the value we reported. Taking the first match in
    /// document order instead rewrote an inactive conditional PropertyGroup, or even the TargetFramework
    /// metadata of a ProjectReference item, while leaving the live property untouched.
    /// </summary>
    private XElement? FindTargetFrameworkProperty(XDocument doc, string localName, string? expectedValue) {
        var candidates = doc.ElementsNamed(localName)
            .Where(e => string.Equals(e.Parent?.Name.LocalName, "PropertyGroup", StringComparison.Ordinal))
            .Where(e => !e.IsConditioned())
            .ToList();

        if (candidates.Count == 0) return null;
        if (expectedValue is null) return candidates.Count == 1 ? candidates[0] : null;

        var exact = candidates.Where(e => string.Equals(e.Value.Trim(), expectedValue.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count == 1) return exact[0];
        if (exact.Count > 1) return null;
        return candidates.Count == 1 ? candidates[0] : null;
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
        public bool UsesTargetFrameworks { get; set; }
        public List<string> TargetFrameworksToUpdate { get; set; } = new();
    }
}