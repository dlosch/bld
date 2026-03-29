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

    private readonly Option<bool> _projectsOption = new Option<bool>("--projects", "-p") {
        Description = "Scan for .NET projects with container build properties.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _allOption = new Option<bool>("--all", "-a") {
        Description = "Scan for both Dockerfiles and .NET container projects.",
        DefaultValueFactory = _ => false
    };

    public ContainerizeCommand(IConsoleOutput console) 
        : base("containerize", "Analyze Dockerfiles and .NET projects with SDK container build properties (PublishProfile=DefaultContainer, ContainerBaseImage, or ContainerImage).", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_listOnlyOption);
        Add(_projectsOption);
        Add(_allOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_concurrencyOption);
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

        // If --all is specified, scan both; otherwise respect individual flags
        var shouldScanDockerfiles = scanAll || !scanProjects;
        var shouldScanProjects = scanAll || scanProjects;

        // Initialize MSBuild if we need to scan projects
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
        }

        Output.WriteInfo($"Scanning: {rootPath}");
        Output.WriteInfo($"Search depth: {depth}");
        Output.WriteLine("");

        bool foundAny = false;

        // Scan for .NET container projects
        if (shouldScanProjects) {
            var projectFiles = await ProjectContainerScanner.FindProjectFilesAsync(rootPath, depth);
            var containerProjects = new List<ProjectContainerScanner.ContainerProjectInfo>();

            // Prepare global properties for project evaluation
            var globalProps = new Dictionary<string, string>();
            var vsToolsPath = parseResult.GetValue(_vsToolsPath);
            if (!string.IsNullOrEmpty(vsToolsPath)) {
                globalProps["VSToolsPath"] = vsToolsPath;
            }

            foreach (var projectFile in projectFiles) {
                var projectInfo = await ProjectContainerScanner.ParseProjectAsync(projectFile, globalProps);
                if (projectInfo != null) {
                    containerProjects.Add(projectInfo);
                }
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
                                project.ContainerImage ?? string.Empty,
                                project.ContainerFamily ?? string.Empty,
                                project.ContainerRegistry ?? string.Empty,
                                project.EnableSdkContainerSupport ? "Enabled" : string.Empty
                            };
                        });

                    MarkdownTableFormatter.Write(
                        Output,
                        ".NET container projects (markdown)",
                        new[] { "Project", "Path", "PublishProfile", "ContainerBaseImage", "ContainerImage", "ContainerFamily", "ContainerRegistry", "SDKContainerSupport" },
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
                            
                            if (project.ContainerImage != null) {
                                Output.WriteLine($"    Container Image: {project.ContainerImage}");
                            }
                            
                            if (project.ContainerFamily != null) {
                                Output.WriteLine($"    Container Family: {project.ContainerFamily}");
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
            var dockerfiles = await DockerfileParser.FindDockerfilesAsync(rootPath, depth);

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
}
