using bld.Infrastructure;
using bld.Models;
// using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class ContainerizeCommand : BaseCommand {

    // private readonly Option<bool> _applyOption = new Option<bool>("--apply") {
    //     Description = "Apply changes to project files (default is dry-run).",
    //     DefaultValueFactory = _ => false
    // };

    // public ContainerizeCommand(IConsoleOutput console) : base("containerize", "Parse Dockerfiles and convert to .NET SDK container build properties.", console) {
    //     Add(_rootOption);
    //     Add(_depthOption);
    //     Add(_applyOption);
    //     Add(_logLevelOption);
    //     Add(_vsToolsPath);
    //     Add(_noResolveVsToolsPath);
    private readonly Option<bool> _listOnlyOption = new Option<bool>("--list", "-l") {
        Description = "Only list Dockerfiles without parsing details.",
        DefaultValueFactory = _ => false
    };

    public ContainerizeCommand(IConsoleOutput console) 
        : base("containerize", "Analyze and display information about Dockerfiles in the project.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_listOnlyOption);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        // var options = new CleaningOptions {
        //     LogLevel = parseResult.GetValue(_logLevelOption),
        //     Depth = parseResult.GetValue(_depthOption),
        //     VSToolsPath = parseResult.GetValue(_vsToolsPath),
        //     NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
        // };

        // if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
        //     options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
        //     options.VSRootPath = vsRoot;
        // }
        // base.Console = new SpectreConsoleOutput(options.LogLevel);
        var logLevel = parseResult.GetValue(_logLevelOption);
        Console = new Services.SpectreConsoleOutput(logLevel);

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            rootPath = Environment.CurrentDirectory;
        }

//         var apply = parseResult.GetValue(_applyOption);

//         Console.WriteInfo($"Containerizing projects in: {rootPath}");
//         Console.WriteInfo($"Mode: {(apply ? "Apply changes" : "Dry run")}");

//         try {
//             var containerizeService = new ContainerizeService(Console, options);
//             await containerizeService.ContainerizeProjectsAsync(rootPath, apply, cancellationToken);

//             Console.WriteInfo("Containerization process completed successfully.");
//             return 0;
//         }
//         catch (Exception ex) {
//             Console.WriteError($"Error containerizing projects: {ex.Message}");
//             return 1;
//         }
//     }
// }

        var depth = parseResult.GetValue(_depthOption);
        var listOnly = parseResult.GetValue(_listOnlyOption);

        Console.WriteInfo($"Searching for Dockerfiles in: {rootPath}");
        Console.WriteInfo($"Search depth: {depth}");
        Console.WriteInfo("");

        var dockerfiles = await DockerfileParser.FindDockerfilesAsync(rootPath, depth);

        if (dockerfiles.Count == 0) {
            Console.WriteWarning("No Dockerfiles found.");
            return 0;
        }

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

        return 0;
    }
}
