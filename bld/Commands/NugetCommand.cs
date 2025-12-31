using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class NugetCommand : BaseCommand {

    private readonly Option<string?> _whitelistBlacklistFileOption = new Option<string?>("--whitelist-blacklist-file", "--wbf") {
        Description = "Path to the whitelist/blacklist file containing package filtering rules.",
        DefaultValueFactory = _ => null
    };

    private readonly Option<bool> _aggregateOption = new Option<bool>("--aggregate", "--agg") {
        Description = "Display packages aggregated across all projects instead of per-project view (default: true).",
        DefaultValueFactory = _ => true
    };

    private readonly Option<bool> _noAggregateOption = new Option<bool>("--no-aggregate", "--no-agg") {
        Description = "Display packages per-project instead of aggregated view.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _showProjectsOption = new Option<bool>("--show-projects", "--sp") {
        Description = "In aggregate mode, show which projects reference each package (default: true).",
        DefaultValueFactory = _ => true
    };

    public NugetCommand(IConsoleOutput console) : base("nuget", "Analyze and categorize NuGet package references in projects.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_jsonOption);
        Add(_whitelistBlacklistFileOption);
        Add(_aggregateOption);
        Add(_noAggregateOption);
        Add(_showProjectsOption);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            JsonOutput = parseResult.GetValue(_jsonOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }

        base.Console = new SpectreConsoleOutput(options.LogLevel, options.JsonOutput);
        var whitelistBlacklistFile = parseResult.GetValue(_whitelistBlacklistFileOption);
        var aggregate = parseResult.GetValue(_aggregateOption);
        var noAggregate = parseResult.GetValue(_noAggregateOption);
        var showProjects = parseResult.GetValue(_showProjectsOption);

        // If --no-aggregate is specified, disable aggregation
        if (noAggregate) {
            aggregate = false;
        }

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            rootPath = Environment.CurrentDirectory;
        }

        var app = new NugetAnalysisApplication(base.Console);
        await app.InitAsync(options);
        var result = await app.RunAsync(new[] { rootPath }, options, whitelistBlacklistFile, aggregate, showProjects);

        if (options.JsonOutput) {
            Console.WriteJson(result);
        }

        return 0;
    }
}