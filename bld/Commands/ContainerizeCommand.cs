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

    public ContainerizeCommand(IConsoleOutput console) 
        : base("containerize", "Analyze and display information about Dockerfiles in the project.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_listOnlyOption);
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
