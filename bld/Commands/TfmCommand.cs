using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class TfmCommand : BaseCommand {

    private readonly Option<string> _fromOption = new Option<string>("--from") {
        Description = "Source target framework (e.g., net8.0)."
    };

    private readonly Option<string> _toOption = new Option<string>("--to") {
        Description = "Target framework to migrate to (e.g., net9.0)."
    };

    private readonly Option<bool> _applyOption = new Option<bool>("--apply") {
        Description = "Apply changes (default is dry-run).",
        DefaultValueFactory = _ => false
    };

    public TfmCommand(IConsoleOutput console) : base("tfm", "Migrate TargetFramework/TargetFrameworks between versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_fromOption);
        Add(_toOption);
        Add(_applyOption);
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

        var from = parseResult.GetValue(_fromOption);
        var to = parseResult.GetValue(_toOption);
        var apply = parseResult.GetValue(_applyOption);

        if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to)) {
            Console.WriteError("Both --from and --to parameters are required.");
            return 1;
        }

        Console.WriteInfo($"Migrating projects from {from} to {to} in: {rootPath}");
        Console.WriteInfo($"Mode: {(apply ? "Apply changes" : "Dry run")}");

        try {
            var tfmService = new TfmService(Console, options);
            return await tfmService.MigrateTargetFrameworkAsync(rootPath, from, to, apply, cancellationToken);
        }
        catch (Exception ex) {
            Console.WriteError($"Error migrating target frameworks: {ex.Message}");
            return 1;
        }
    }
}