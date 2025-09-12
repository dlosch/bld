using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class CleanupCommand : BaseCommand {

    private readonly Option<bool> _updateOption = new Option<bool>("--update", "-u") {
        Description = "Remove redundant package references instead of just analyzing them.",
        DefaultValueFactory = _ => false
    };

    public CleanupCommand(IConsoleOutput console) : base("cleanup", "Analyze and optionally remove redundant package references that are transitive or not required.", console) {
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

        var rootValue = parseResult.GetValue(_rootOption) ?? parseResult.GetValue(_rootArgument);
        if (string.IsNullOrEmpty(rootValue)) {
            rootValue = Directory.GetCurrentDirectory();
        }

        var removeRedundant = parseResult.GetValue(_updateOption);

        var service = new CleanupService(Console, options);
        return await service.AnalyzePackageReferencesAsync(rootValue, removeRedundant, cancellationToken);
    }
}