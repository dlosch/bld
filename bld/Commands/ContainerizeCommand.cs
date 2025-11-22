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
        : base("containerize", "Analyze and display information about Dockerfiles and container projects.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_listOnlyOption);
        Add(_projectsOption);
        Add(_allOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var logLevel = parseResult.GetValue(_logLevelOption);
        Console = new Services.SpectreConsoleOutput(logLevel);

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            rootPath = Environment.CurrentDirectory;
        }

        var depth = parseResult.GetValue(_depthOption);
        var listOnly = parseResult.GetValue(_listOnlyOption);
        var scanProjects = parseResult.GetValue(_projectsOption);
        var scanAll = parseResult.GetValue(_allOption);

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

            Services.MSBuildService.RegisterMSBuildDefaults(Console, options);
        }

        Console.WriteInfo($"Scanning: {rootPath}");
        Console.WriteInfo($"Search depth: {depth}");
        Console.WriteInfo("");

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
                Console.WriteInfo($"Found {containerProjects.Count} .NET Container Project(s):");
                Console.WriteInfo("");

                foreach (var project in containerProjects) {
                    var relativePath = Path.GetRelativePath(rootPath, project.ProjectPath);
                    Console.WriteInfo($"  • {project.ProjectName} ({relativePath})");

                    if (!listOnly) {
                        if (project.PublishProfile != null) {
                            Console.WriteInfo($"    Publish Profile: {project.PublishProfile}");
                        }
                        
                        if (project.EnableSdkContainerSupport) {
                            Console.WriteInfo($"    SDK Container Support: Enabled");
                        }

                        if (project.ContainerBaseImage != null) {
                            Console.WriteInfo($"    Container Base Image: {project.ContainerBaseImage}");
                        }
                        
                        if (project.ContainerImage != null) {
                            Console.WriteInfo($"    Container Image: {project.ContainerImage}");
                        }
                        
                        if (project.ContainerFamily != null) {
                            Console.WriteInfo($"    Container Family: {project.ContainerFamily}");
                        }

                        if (project.ContainerRegistry != null) {
                            Console.WriteInfo($"    Container Registry: {project.ContainerRegistry}");
                        }

                        Console.WriteInfo("");
                    }
                }
            }
        }

        // Scan for Dockerfiles
        if (shouldScanDockerfiles) {
            var dockerfiles = await DockerfileParser.FindDockerfilesAsync(rootPath, depth);

            if (dockerfiles.Count > 0) {
                foundAny = true;
                Console.WriteInfo($"Found {dockerfiles.Count} Dockerfile(s):");
                Console.WriteInfo("");

                foreach (var dockerfile in dockerfiles) {
                    var relativePath = Path.GetRelativePath(rootPath, dockerfile);
                    Console.WriteInfo($"  • {relativePath}");
                    
                    if (!listOnly) {
                        var info = await DockerfileParser.ParseAsync(dockerfile);
                        
                        if (info.BaseImages.Any()) {
                            Console.WriteInfo($"    Base Images: {string.Join(", ", info.BaseImages)}");
                        }
                        
                        if (info.Stages.Any()) {
                            Console.WriteInfo($"    Build Stages: {string.Join(", ", info.Stages)}");
                        }
                        
                        if (info.ExposedPorts.Any()) {
                            Console.WriteInfo($"    Exposed Ports: {string.Join(", ", info.ExposedPorts)}");
                        }
                        
                        if (!string.IsNullOrEmpty(info.WorkDir)) {
                            Console.WriteInfo($"    Working Directory: {info.WorkDir}");
                        }
                        
                        if (!string.IsNullOrEmpty(info.EntryPoint)) {
                            Console.WriteInfo($"    Entry Point: {info.EntryPoint}");
                        }
                        
                        if (!string.IsNullOrEmpty(info.Cmd)) {
                            Console.WriteInfo($"    CMD: {info.Cmd}");
                        }
                        
                        Console.WriteInfo("");
                    }
                }
            }
        }

        if (!foundAny) {
            Console.WriteWarning("No Dockerfiles or container projects found.");
        }

        return 0;
    }
}
