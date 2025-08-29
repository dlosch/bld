using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class ContainerizeCommand : BaseCommand {

    private readonly Option<bool> _updateOption = new Option<bool>("--update", "-u") {
        Description = "Apply changes to project files (default is dry-run).",
        DefaultValueFactory = _ => false
    };

    public ContainerizeCommand(IConsoleOutput console) : base("containerize", "Parse Dockerfiles and convert to .NET SDK container build properties.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_updateOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
        base.Console = new SpectreConsoleOutput(options.LogLevel);

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            rootPath = Environment.CurrentDirectory;
        }

        var update = parseResult.GetValue(_updateOption);

        Console.WriteInfo($"Containerizing projects in: {rootPath}");
        Console.WriteInfo($"Mode: {(update ? "Apply changes" : "Dry run")}");

        try {
            var containerizeService = new ContainerizeService(Console, options);
            await containerizeService.ContainerizeProjectsAsync(rootPath, update, cancellationToken);
            
            Console.WriteInfo("Containerization process completed successfully.");
            return 0;
        }
        catch (Exception ex) {
            Console.WriteError($"Error containerizing projects: {ex.Message}");
            return 1;
        }
    }
}