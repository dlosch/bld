using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class StatsCommand : BaseCommand {

    private readonly Option<bool> _nonCurrentOption = new Option<bool>("--non-current", "--noncurrent", "-nc") {
        Description = "Only clean directories for non-current target frameworks.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _objOption = new Option<bool>("--obj", "-obj") {
        Description = "Also clean BaseIntermediateOutputPath (obj folder).",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _keepAssetsOption = new Option<bool>("--keep-assets") {
        Description = "When cleaning obj, preserve NuGet restore artifacts (project.assets.json etc.) and only delete build output subdirectories.",
        DefaultValueFactory = _ => false
    };

    public StatsCommand(IConsoleOutput console) : base("stats", "Compute statistics.", console) {
        Add(_rootOption);
        Add(_depthOption);

        Add(_nonCurrentOption);
        Add(_objOption);
        Add(_keepAssetsOption);

        Add(_logLevelOption);

        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        Add(_parallelOption);
        Add(_concurrencyOption);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            Delete = false,
            CleanOnlyNonCurrentTfms = parseResult.GetValue(_nonCurrentOption),
            CleanObjDirectory = parseResult.GetValue(_objOption),
            KeepRestoreArtifacts = parseResult.GetValue(_keepAssetsOption),
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

        var rootPath = GetRootPath(parseResult);

        var app = new CleaningApplication(base.Output, (a, b, c) => new MarkDeleteResultStatsProcessor(a, b, c));
        await app.InitAsync(options);
        await app.RunAsync(new[] { rootPath }, options, cancellationToken);

        return 0;
    }
}
