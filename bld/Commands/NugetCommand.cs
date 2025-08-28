using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class NugetCommand : BaseCommand {

    private readonly Option<bool> _includeDownloadCountsOption = new Option<bool>("--include-download-counts", "--downloads") {
        Description = "Include NuGet download counts in the output (requires internet access).",
        DefaultValueFactory = _ => false
    };

    public NugetCommand(IConsoleOutput console) : base("nuget", "Analyze and categorize NuGet package references in projects.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_includeDownloadCountsOption);
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
        var includeDownloadCounts = parseResult.GetValue(_includeDownloadCountsOption);

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            rootPath = Environment.CurrentDirectory;
        }

        var app = new NugetAnalysisApplication(base.Console);
        await app.InitAsync(options);
        await app.RunAsync(new[] { rootPath }, options, includeDownloadCounts);

        return 0;
    }
}