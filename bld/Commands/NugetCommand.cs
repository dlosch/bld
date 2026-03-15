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
        Description = "Display packages aggregated across all projects instead of per-project view.",
        DefaultValueFactory = _ => true
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

        Add(_parallelOption);
        Add(_concurrencyOption);

        Add(_whitelistBlacklistFileOption);
        Add(_aggregateOption);
        Add(_showProjectsOption);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            Parallel = parseResult.GetValue(_parallelOption),
            MaxDegreeOfParallelism = parseResult.GetValue(_concurrencyOption),
            MarkdownOutput = parseResult.GetValue(_markdownOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }

        base.Output = new SpectreConsoleOutput(options.LogLevel);
        var whitelistBlacklistFile = parseResult.GetValue(_whitelistBlacklistFileOption);
        var aggregate = parseResult.GetValue(_aggregateOption);
        var showProjects = parseResult.GetValue(_showProjectsOption);

        var rootPath = GetRootPath(parseResult);

        var app = new NugetAnalysisApplication(base.Output);
        await app.InitAsync(options);
        await app.RunAsync(new[] { rootPath }, options, whitelistBlacklistFile, aggregate, showProjects, options.MarkdownOutput);

        return 0;
    }
}