using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class CpmCommand : BaseCommand {

    private readonly Option<bool> _dryRunOption = new Option<bool>("--dry-run") {
        Description = "Show what would be changed without modifying files.",
        DefaultValueFactory = _ => true
    };

    private readonly Option<bool> _forceOption = new Option<bool>("--force") {
        Description = "Apply changes to create Directory.Packages.props and update project files.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _overwriteOption = new Option<bool>("--overwrite") {
        Description = "Overwrite existing Directory.Packages.props if it exists.",
        DefaultValueFactory = _ => false
    };

    public CpmCommand(IConsoleOutput console) : base("cpm", "Convert all projects in a solution to Central Package Management.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_dryRunOption);
        Add(_forceOption);
        Add(_overwriteOption);
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

        var dryRun = parseResult.GetValue(_dryRunOption);
        var force = parseResult.GetValue(_forceOption);
        var overwrite = parseResult.GetValue(_overwriteOption);

        if (force && dryRun) {
            dryRun = false; // Force overrides dry-run
        }

        Console.WriteInfo($"Converting projects to Central Package Management in: {rootPath}");
        Console.WriteInfo($"Mode: {(dryRun ? "Dry run" : "Apply changes")}");
        Console.WriteInfo($"Overwrite existing Directory.Packages.props: {overwrite}");

        try {
            var cpmService = new CpmService(Console, options);
            await cpmService.ConvertToCentralPackageManagementAsync(rootPath, !dryRun, overwrite, cancellationToken);
            
            Console.WriteInfo("Central Package Management conversion completed successfully.");
            return 0;
        }
        catch (Exception ex) {
            Console.WriteError($"Error converting to Central Package Management: {ex.Message}");
            return 1;
        }
    }
}