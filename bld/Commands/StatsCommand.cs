using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class StatsCommand : BaseCommand {

    public StatsCommand(IConsoleOutput console) : base("stats", "Compute statistics.", console) {
        Add(_rootOption);
        Add(_depthOption);

        Add(_nonCurrentOption);
        Add(_objOption);
        Add(_jsonOption);

        Add(_logLevelOption);

        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var errors = new List<string>();

        var options = new CleaningOptions {
            Delete = false, //TODO parseResult.GetValue(_deleteOption),
            CleanOnlyNonCurrentTfms = parseResult.GetValue(_nonCurrentOption),
            CleanObjDirectory = parseResult.GetValue(_objOption),
            CleanNupkgFiles = parseResult.GetValue(_nupkgOption),
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

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            // If no root is specified, use the current directory
            rootPath = Environment.CurrentDirectory;
        }

        var app = new CleaningApplication(base.Console, (a, b, c) => new MarkDeleteResultStatsProcessor(a, b, c));
        await app.InitAsync(options);
        var result = await app.RunAsync(new[] { rootPath }, options);

        if (options.JsonOutput) {
            Console.WriteJson(result);
        }

        return 0;
    }
}
