using bld.Infrastructure;
using bld.Models;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class ContainerizeCommand : BaseCommand {

    private readonly Option<bool> _listOnlyOption = new Option<bool>("--list", "-l") {
        Description = "Only list Dockerfiles without parsing details.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _projectsOption = new Option<bool>("--projects") {
        Description = "Scan for .NET projects with container build properties.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _allOption = new Option<bool>("--all", "-a") {
        Description = "Scan for both Dockerfiles and .NET container projects.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _migrateOption = new Option<bool>("--migrate", "-m") {
        Description = "Migrate each Dockerfile to SDK container properties (ContainerBaseImage, ContainerPort, ContainerEnvironmentVariable, ...) on the project it builds. Dry run unless --apply is given.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _applyOption = new Option<bool>("--apply") {
        Description = "With --migrate, write the properties into the project files (default is dry-run).",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _deleteDockerfileOption = new Option<bool>("--delete-dockerfile") {
        Description = "With --migrate --apply, delete the migrated Dockerfile and remove the Visual Studio container-tools settings (Docker* properties, Microsoft.VisualStudio.Azure.Containers.Tools.Targets) from the project.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _forceOption = new Option<bool>("--allow-unsupported", "--force") {
        Description = "With --migrate, migrate a Dockerfile even when its runtime stage has instructions the SDK cannot express (RUN, VOLUME, HEALTHCHECK, files copied from the build context). They are listed and dropped. --force is the old name for this option.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _validateOption = new Option<bool>("--validate") {
        Description = "Check the SDK container settings of each project: names the SDK does not read, the obsolete ContainerImageName, deprecated ContainerEntrypoint items, invalid values, a ContainerBaseImage the SDK computes anyway or a family variant of it (ContainerFamily). With --apply, rewrite the findings that have an exact equivalent.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _interactiveOption = new Option<bool>("--interactive", "-i") {
        Description = "With --migrate or --validate, ask per Dockerfile or finding and write the answers: whether to migrate a Dockerfile (and drop what the SDK cannot express, and delete it afterwards), which findings to fix. Writes without --apply.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _runAsRootOption = new Option<bool>("--run-as-root") {
        Description = "With --migrate, write ContainerUser=root for a Dockerfile without USER instead of asking. Without it the question is asked per Dockerfile; when it cannot be asked the SDK's default user (non-root app on .NET 8+ images) is kept.",
        DefaultValueFactory = _ => false
    };

    public ContainerizeCommand(IConsoleOutput console)
        : base("containerize", "Analyze Dockerfiles and .NET projects with SDK container build properties (PublishProfile=DefaultContainer, ContainerBaseImage, ContainerFamily, ContainerRepository, ...), or migrate Dockerfiles to those properties.", console) {
        Add(_markdownOption);
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_listOnlyOption);
        Add(_projectsOption);
        Add(_allOption);
        Add(_migrateOption);
        Add(_applyOption);
        Add(_deleteDockerfileOption);
        Add(_forceOption);
        Add(_runAsRootOption);
        Add(_validateOption);
        Add(_interactiveOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var logLevel = parseResult.GetValue(_logLevelOption);
        base.Output = new Services.SpectreConsoleOutput(logLevel);

        var rootPath = GetRootPath(parseResult);

        var depth = parseResult.GetValue(_depthOption);
        var listOnly = parseResult.GetValue(_listOnlyOption);
        var scanProjects = parseResult.GetValue(_projectsOption);
        var scanAll = parseResult.GetValue(_allOption);
        var markdownOutput = parseResult.GetValue(_markdownOption);

        var interactive = parseResult.GetValue(_interactiveOption);
        var migrate = parseResult.GetValue(_migrateOption);
        var validate = parseResult.GetValue(_validateOption);
        var apply = parseResult.GetValue(_applyOption);

        if (interactive) {
            if (!validate && !migrate) {
                Output.WriteError("--interactive needs --migrate or --validate.");
                return 1;
            }
            // Interactive mode prints prompts and writes answers; a markdown table cannot do both.
            if (markdownOutput) {
                Output.WriteError("--markdown cannot be combined with --interactive.");
                return 1;
            }
            if (!Output.CanPrompt) {
                Output.WriteError("--interactive needs an interactive terminal. Use --apply instead.");
                return 1;
            }
        }

        // The write-flags only do something in a migrate (or, for --apply, a validate) run; say so when
        // they are passed without their mode instead of ignoring them.
        if (!migrate) {
            if (apply && !validate) Output.WriteWarning("--apply has no effect without --migrate or --validate.");
            if (parseResult.GetValue(_deleteDockerfileOption)) Output.WriteWarning("--delete-dockerfile has no effect without --migrate.");
            if (parseResult.GetValue(_forceOption)) Output.WriteWarning("--allow-unsupported has no effect without --migrate.");
            if (parseResult.GetValue(_runAsRootOption)) Output.WriteWarning("--run-as-root has no effect without --migrate.");
        }

        if (validate) {
            return await ValidateAsync(rootPath, depth, apply, interactive, markdownOutput, cancellationToken);
        }

        if (migrate) {
            return await MigrateAsync(rootPath, depth,
                apply,
                deleteDockerfile: parseResult.GetValue(_deleteDockerfileOption),
                force: parseResult.GetValue(_forceOption),
                runAsRoot: parseResult.GetValue(_runAsRootOption) ? true : null,
                interactive, markdownOutput, cancellationToken);
        }

        // If --all is specified, scan both; otherwise respect individual flags
        var shouldScanDockerfiles = scanAll || !scanProjects;
        var shouldScanProjects = scanAll || scanProjects;

        // Initialize MSBuild if we need to scan projects
        var resolvedOptions = default(CleaningOptions);
        if (shouldScanProjects) {
            var options = new CleaningOptions {
                LogLevel = logLevel,
                VSToolsPath = parseResult.GetValue(_vsToolsPath),
                NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath)
            };

            if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
                options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
                options.VSRootPath = vsRoot;
            }

            Services.MSBuildService.RegisterMSBuildDefaults(Output, options);
            resolvedOptions = options;
        }

        Output.WriteInfo($"Scanning: {rootPath}");
        Output.WriteInfo($"Search depth: {depth}");
        Output.WriteLine("");

        bool foundAny = false;

        // Scan for .NET container projects
        if (shouldScanProjects) {
            var failures = 0;
            void ReportFailure(string path, Exception ex) {
                failures++;
                Output.WriteWarning($"Could not evaluate {path}: {ex.FormatMessage()}");
            }

            var projectFiles = await ProjectContainerScanner.FindProjectFilesAsync(rootPath, depth, ReportFailure);
            var containerProjects = new List<ProjectContainerScanner.ContainerProjectInfo>();

            // Prepare global properties for project evaluation. Reading the raw option here discarded
            // the VSToolsPath resolved above, so projects needing those targets failed to evaluate and
            // were silently reported as "not a container project".
            var globalProps = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(resolvedOptions?.VSToolsPath)) {
                globalProps["VSToolsPath"] = resolvedOptions.VSToolsPath;
            }
            if (!string.IsNullOrEmpty(resolvedOptions?.VSRootPath) && Directory.Exists(Path.Combine(resolvedOptions.VSRootPath, "MSBuild"))) {
                globalProps["MSBuildExtensionsPath"] = Path.Combine(resolvedOptions.VSRootPath, "MSBuild");
            }

            foreach (var projectFile in projectFiles) {
                cancellationToken.ThrowIfCancellationRequested();
                var projectInfo = await ProjectContainerScanner.ParseProjectAsync(projectFile, globalProps, ReportFailure);
                if (projectInfo != null) {
                    containerProjects.Add(projectInfo);
                }
            }

            if (failures > 0) {
                Output.WriteWarning($"{failures} project(s) could not be evaluated and were skipped; results may be incomplete.");
            }

            if (containerProjects.Count > 0) {
                foundAny = true;
                if (markdownOutput) {
                    var rows = containerProjects
                        .OrderBy(project => project.ProjectName, StringComparer.OrdinalIgnoreCase)
                        .Select(project => {
                            var relativePath = Path.GetRelativePath(rootPath, project.ProjectPath);
                            return (IReadOnlyList<string?>)new[] {
                                project.ProjectName,
                                relativePath,
                                project.PublishProfile ?? string.Empty,
                                project.ContainerBaseImage ?? string.Empty,
                                project.ContainerFamily ?? string.Empty,
                                project.ContainerRepository ?? string.Empty,
                                project.ContainerImageTag ?? string.Empty,
                                project.ContainerRegistry ?? string.Empty,
                                project.EnableSdkContainerSupport ? "Enabled" : string.Empty
                            };
                        });

                    MarkdownTableFormatter.Write(
                        Output,
                        ".NET container projects (markdown)",
                        new[] { "Project", "Path", "PublishProfile", "ContainerBaseImage", "ContainerFamily", "ContainerRepository", "ContainerImageTag", "ContainerRegistry", "SDKContainerSupport" },
                        rows);
                }
                else {
                    Output.WriteLine($"Found {containerProjects.Count} .NET Container Project(s):");
                    Output.WriteLine("");

                    foreach (var project in containerProjects) {
                        var relativePath = Path.GetRelativePath(rootPath, project.ProjectPath);
                        Output.WriteLine($"  • {project.ProjectName} ({relativePath})");

                        if (!listOnly) {
                            if (project.PublishProfile != null) {
                                Output.WriteLine($"    Publish Profile: {project.PublishProfile}");
                            }
                            
                            if (project.EnableSdkContainerSupport) {
                                Output.WriteLine($"    SDK Container Support: Enabled");
                            }

                            if (project.ContainerBaseImage != null) {
                                Output.WriteLine($"    Container Base Image: {project.ContainerBaseImage}");
                            }
                            
                            if (project.ContainerFamily != null) {
                                Output.WriteLine($"    Container Family: {project.ContainerFamily}");
                            }

                            if (project.ContainerRepository != null) {
                                Output.WriteLine($"    Container Repository: {project.ContainerRepository}");
                            }

                            if (project.ContainerImageTag != null) {
                                Output.WriteLine($"    Container Image Tag: {project.ContainerImageTag}");
                            }

                            if (project.ContainerRegistry != null) {
                                Output.WriteLine($"    Container Registry: {project.ContainerRegistry}");
                            }

                            Output.WriteLine("");
                        }
                    }
                }
            }
        }

        // Scan for Dockerfiles
        if (shouldScanDockerfiles) {
            var dockerfiles = await DockerfileParser.FindDockerfilesAsync(rootPath, depth,
                (path, ex) => Output.WriteWarning($"Could not scan {path}: {ex.FormatMessage()}"));

            if (dockerfiles.Count > 0) {
                foundAny = true;
                if (markdownOutput) {
                    if (listOnly) {
                        var rows = dockerfiles
                            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                            .Select(dockerfile => (IReadOnlyList<string?>)new[] {
                                Path.GetRelativePath(rootPath, dockerfile)
                            });
                        MarkdownTableFormatter.Write(Output, "Dockerfiles (markdown)", new[] { "Dockerfile" }, rows);
                    }
                    else {
                        var rows = new List<IReadOnlyList<string?>>();
                        foreach (var dockerfile in dockerfiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) {
                            var info = await DockerfileParser.ParseAsync(dockerfile);
                            rows.Add(new[] {
                                Path.GetRelativePath(rootPath, dockerfile),
                                string.Join(", ", info.BaseImages),
                                string.Join(", ", info.Stages),
                                string.Join(", ", info.ExposedPorts),
                                info.WorkDir ?? string.Empty,
                                info.EntryPoint ?? string.Empty,
                                info.Cmd ?? string.Empty,
                            });
                        }

                        MarkdownTableFormatter.Write(
                            Output,
                            "Dockerfiles (markdown)",
                            new[] { "Dockerfile", "Base Images", "Build Stages", "Exposed Ports", "Working Directory", "Entry Point", "CMD" },
                            rows);
                    }
                }
                else {
                    Output.WriteLine($"Found {dockerfiles.Count} Dockerfile(s):");
                    Output.WriteLine("");

                    foreach (var dockerfile in dockerfiles) {
                        var relativePath = Path.GetRelativePath(rootPath, dockerfile);
                        Output.WriteLine($"  • {relativePath}");

                        if (!listOnly) {
                            var info = await DockerfileParser.ParseAsync(dockerfile);

                            if (info.BaseImages.Any()) {
                                Output.WriteLine($"    Base Images: {string.Join(", ", info.BaseImages)}");
                            }

                            if (info.Stages.Any()) {
                                Output.WriteLine($"    Build Stages: {string.Join(", ", info.Stages)}");
                            }

                            if (info.ExposedPorts.Any()) {
                                Output.WriteLine($"    Exposed Ports: {string.Join(", ", info.ExposedPorts)}");
                            }

                            if (!string.IsNullOrEmpty(info.WorkDir)) {
                                Output.WriteLine($"    Working Directory: {info.WorkDir}");
                            }

                            if (!string.IsNullOrEmpty(info.EntryPoint)) {
                                Output.WriteLine($"    Entry Point: {info.EntryPoint}");
                            }

                            if (!string.IsNullOrEmpty(info.Cmd)) {
                                Output.WriteLine($"    CMD: {info.Cmd}");
                            }
                            
                            Output.WriteLine("");
                        }
                    }
                }
            }
        }

        if (!foundAny) {
            Output.WriteWarning("No Dockerfiles or container projects found.");
        }

        return 0;
    }

    /// <summary>
    /// Plans one migration per Dockerfile and, with --apply, writes it. The project files are read as
    /// XML, so this needs no MSBuild registration. Returns 1 when a write failed.
    /// </summary>
    private async Task<int> MigrateAsync(string rootPath, int depth, bool apply, bool deleteDockerfile, bool force, bool? runAsRoot, bool interactive, bool markdownOutput, CancellationToken cancellationToken) {
        if (deleteDockerfile && !apply && !interactive) {
            Output.WriteWarning("--delete-dockerfile has no effect without --apply.");
        }

        void ReportScanFailure(string path, Exception ex) => Output.WriteWarning($"Could not scan {path}: {ex.FormatMessage()}");

        // A project passed as root means "its Dockerfile"; a Dockerfile means that one; a directory
        // means every Dockerfile in it, matched against every project in it.
        List<string> dockerfiles;
        List<string> projectFiles;
        var searchRoot = Directory.Exists(rootPath) ? rootPath : Path.GetDirectoryName(rootPath)!;
        string Rel(string path) => Path.GetRelativePath(searchRoot, path);
        if (File.Exists(rootPath) && SlnScanner.IsProjectFile(rootPath)) {
            // Same discovery as the directory scan, limited to the project's own directory.
            dockerfiles = await DockerfileParser.FindDockerfilesAsync(searchRoot, 0, ReportScanFailure);
            projectFiles = [rootPath];
        }
        else {
            dockerfiles = await DockerfileParser.FindDockerfilesAsync(rootPath, depth, ReportScanFailure);
            projectFiles = await ProjectContainerScanner.FindProjectFilesAsync(searchRoot, depth, ReportScanFailure);
        }

        Output.WriteInfo($"Scanning: {rootPath}");
        Output.WriteInfo($"Mode: {(interactive ? "Interactive" : apply ? "Apply changes" : "Dry run")}");

        if (dockerfiles.Count == 0) {
            Output.WriteWarning("No Dockerfiles found.");
            return 0;
        }

        var service = new Services.ContainerMigrationService(Output, runAsRoot, willWrite: apply || interactive);
        var plans = new List<Services.ContainerMigrationService.MigrationPlan>();
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // The plain Dockerfile goes first, so it is the one that claims a project it shares with variants.
        foreach (var dockerfile in dockerfiles
                     .OrderBy(path => !Path.GetFileName(path).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase))
                     .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                var plan = await service.PlanAsync(dockerfile, projectFiles, cancellationToken);
                // Two Dockerfiles for one project (debug and release variants) cannot both win.
                if (plan.SkipReason is null && plan.ProjectPath is { } project) {
                    if (claimed.TryGetValue(project, out var first)) {
                        plan.SkipReason = $"project already migrated from {Rel(first)}";
                    }
                    else {
                        claimed[project] = dockerfile;
                    }
                }
                plans.Add(plan);
            }
            catch (Exception ex) {
                Output.WriteWarning($"Could not analyze {dockerfile}: {ex.FormatMessage()}");
            }
        }

        var failures = 0;
        var written = 0;
        var declined = 0;
        if (markdownOutput && !interactive) {
            var rows = plans.Select(plan => (IReadOnlyList<string?>)new[] {
                Rel(plan.DockerfilePath),
                plan.ProjectPath is { } ? Rel(plan.ProjectPath) : string.Empty,
                MigrationStatus(plan, force),
                string.Join("; ", plan.Elements.SelectMany(e => e.Elements()).Select(DescribeSetting)),
                string.Join("; ", plan.Unsupported),
                string.Join("; ", plan.Notes),
            });
            MarkdownTableFormatter.Write(Output, "Dockerfile migration (markdown)",
                new[] { "Dockerfile", "Project", "Status", "Settings", "Not migrated", "Notes" }, rows);
        }
        else {
            foreach (var plan in plans) {
                Output.WriteLine($"  • {Rel(plan.DockerfilePath)}");
                if (plan.ProjectPath is { }) Output.WriteLine($"    Project: {Rel(plan.ProjectPath)}");
                if (plan.SkipReason is { }) {
                    Output.WriteLine($"    Skipped: {plan.SkipReason}");
                }
                else {
                    Output.WriteLine(apply && !interactive && plan.CanApply(force) ? "    Adding:" : "    Would add:");
                    foreach (var element in plan.Elements) {
                        foreach (var line in element.ToString().Split('\n')) {
                            Output.WriteLine("      " + line.TrimEnd('\r'));
                        }
                    }
                }
                foreach (var item in plan.Unsupported) Output.WriteLine($"    Not migrated: {item}");
                foreach (var note in plan.Notes) Output.WriteLine($"    Note: {note}");
                if (plan.SkipReason is null && plan.Unsupported.Count > 0 && !force && !interactive) {
                    Output.WriteLine("    Skipped: the runtime image has instructions the SDK cannot express; pass --allow-unsupported to migrate without them.");
                }

                if (interactive && plan.SkipReason is null) {
                    // Each Dockerfile is its own decision: drop what the SDK cannot express, write, delete.
                    var forced = force || (plan.Unsupported.Count > 0
                        && Output.Confirm($"    Migrate anyway, dropping the {plan.Unsupported.Count} instruction(s) the SDK cannot express?", defaultValue: false));
                    if (!plan.CanApply(forced) || !Output.Confirm($"    Write these settings into {Rel(plan.ProjectPath!)}?", defaultValue: true)) {
                        declined++;
                    }
                    else {
                        var delete = deleteDockerfile || Output.Confirm($"    Delete {Rel(plan.DockerfilePath)} and the Visual Studio container-tools settings?", defaultValue: false);
                        if (await service.ApplyAsync(plan, delete, cancellationToken)) {
                            written++;
                            Output.WriteLine($"    ✓ Updated {Rel(plan.ProjectPath!)}{(delete ? $", deleted {Rel(plan.DockerfilePath)}" : "")}");
                        }
                        else {
                            failures++;
                        }
                    }
                }
                Output.WriteLine("");
            }
        }

        if (apply && !interactive) {
            foreach (var plan in plans.Where(p => p.CanApply(force))) {
                cancellationToken.ThrowIfCancellationRequested();
                if (await service.ApplyAsync(plan, deleteDockerfile, cancellationToken)) {
                    written++;
                    Output.WriteLine($"✓ Updated {Rel(plan.ProjectPath!)}{(deleteDockerfile ? $", deleted {Rel(plan.DockerfilePath)}" : "")}");
                }
                else {
                    failures++;
                }
            }
        }

        var ready = plans.Count(p => p.CanApply(force));
        var blocked = plans.Count(p => p.SkipReason is null && !p.CanApply(force));
        var skipped = plans.Count(p => p.SkipReason is { });
        Output.WriteLine("");
        Output.WriteLine(interactive
            ? $"Migrated {written} of {plans.Count} Dockerfile(s); {declined} declined, {skipped} skipped, {failures} failed."
            : apply
            ? $"Migrated {written} of {plans.Count} Dockerfile(s); {blocked} blocked by unsupported instructions, {skipped} skipped, {failures} failed."
            : $"{ready} of {plans.Count} Dockerfile(s) can be migrated; {blocked} blocked by unsupported instructions, {skipped} skipped. Pass --apply to write.");
        if (written > 0) {
            Output.WriteLine("Build the image with: dotnet publish <project> -c Release /t:PublishContainer");
        }

        return failures > 0 ? 1 : 0;
    }

    /// <summary>
    /// Reports the SDK container settings of every project under the root and, with --apply, writes
    /// the fixable findings. Projects without any container setting are not listed. Returns 1 when a
    /// write failed.
    /// </summary>
    private async Task<int> ValidateAsync(string rootPath, int depth, bool apply, bool interactive, bool markdownOutput, CancellationToken cancellationToken) {
        var searchRoot = Directory.Exists(rootPath) ? rootPath : Path.GetDirectoryName(rootPath)!;
        string Rel(string path) => Path.GetRelativePath(searchRoot, path);
        var projectFiles = await ProjectContainerScanner.FindProjectFilesAsync(rootPath, depth,
            (path, ex) => Output.WriteWarning($"Could not scan {path}: {ex.FormatMessage()}"));

        Output.WriteInfo($"Scanning: {rootPath}");
        Output.WriteInfo($"Mode: {(interactive ? "Interactive" : apply ? "Apply changes" : "Report")}");

        var service = new Services.ContainerValidationService(Output);
        var reports = new List<Services.ContainerValidationService.Report>();
        foreach (var projectFile in projectFiles.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            cancellationToken.ThrowIfCancellationRequested();
            try {
                if (await service.ValidateAsync(projectFile, cancellationToken) is { } report) reports.Add(report);
            }
            catch (Exception ex) {
                Output.WriteWarning($"Could not read {projectFile}: {ex.FormatMessage()}");
            }
        }

        if (reports.Count == 0) {
            Output.WriteWarning("No projects with SDK container settings found.");
            return 0;
        }

        var failures = 0;
        var written = 0;
        if (markdownOutput && !interactive) {
            var rows = reports.SelectMany(report => report.Findings.Count == 0
                ? new[] { (IReadOnlyList<string?>)new[] { Rel(report.ProjectPath), string.Empty, "ok", string.Empty } }
                : report.Findings.Select(f => (IReadOnlyList<string?>)new[] { Rel(report.ProjectPath), f.Setting, f.Message, f.Fix ?? string.Empty }));
            MarkdownTableFormatter.Write(Output, "Container settings (markdown)", new[] { "Project", "Setting", "Finding", "Fix" }, rows);
        }
        else {
            foreach (var report in reports) {
                Output.WriteLine($"  • {Rel(report.ProjectPath)}");
                if (report.Findings.Count == 0) {
                    Output.WriteLine($"    ok: {report.SettingCount} container setting(s), nothing to report");
                }
                foreach (var finding in report.Findings) {
                    Output.WriteLine($"    {finding.Setting}: {finding.Message}");
                    if (finding.Fix is null) continue;
                    if (interactive) {
                        finding.Skip = !Output.Confirm($"      Fix: {finding.Fix}?", defaultValue: true);
                    }
                    else {
                        Output.WriteLine($"      {(apply ? "Fixing" : "Fix")}: {finding.Fix}");
                    }
                }
                if (interactive && report.Fixable > 0) {
                    if (await service.ApplyAsync(report, cancellationToken)) {
                        written++;
                        Output.WriteLine($"    ✓ Updated {Rel(report.ProjectPath)}");
                    }
                    else {
                        failures++;
                    }
                }
                Output.WriteLine("");
            }
        }

        if (apply && !interactive) {
            foreach (var report in reports.Where(r => r.Fixable > 0)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (await service.ApplyAsync(report, cancellationToken)) {
                    written++;
                    Output.WriteLine($"✓ Updated {Rel(report.ProjectPath)}");
                }
                else {
                    failures++;
                }
            }
        }

        var findings = reports.Sum(r => r.Findings.Count);
        var fixable = reports.Sum(r => r.Fixable);
        Output.WriteLine("");
        Output.WriteLine(apply || interactive
            ? $"{reports.Count} project(s) with container settings; {findings} finding(s), {fixable} fixed in {written} project(s), {failures} failed."
            : $"{reports.Count} project(s) with container settings; {findings} finding(s), {fixable} fixable.{(fixable > 0 ? " Pass --apply to write." : string.Empty)}");

        return failures > 0 ? 1 : 0;
    }

    private static string MigrationStatus(Services.ContainerMigrationService.MigrationPlan plan, bool force) =>
        plan.SkipReason is { } ? $"skipped: {plan.SkipReason}"
        : plan.CanApply(force) ? "ready"
        : "blocked";

    private static string DescribeSetting(System.Xml.Linq.XElement element) {
        var include = element.Attribute("Include")?.Value;
        if (include is null) return $"{element.Name.LocalName}={element.Value}";
        var value = element.Attribute("Value")?.Value ?? element.Attribute("Type")?.Value;
        return value is null ? $"{element.Name.LocalName} {include}" : $"{element.Name.LocalName} {include}={value}";
    }
}
