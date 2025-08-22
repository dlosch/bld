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

        Add(_logLevelOption);

        //Add(_logFileOption);

        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        //Add(_nupkgOption);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var errors = new List<string>();

        var options = new CleaningOptions {
            //DryRun = parseResult.GetValue(_dryRunOption), // If --delete is specified, disable dry run
            //OutputFile = parseResult.GetValue(_outputFileOption),
            //LogFile = parseResult.GetValue(_logFileOption),
            Delete = false, //TODO parseResult.GetValue(_deleteOption),
            //DeleteEmptyDirectories = parseResult.GetValue(_deleteEmptyDirs),
            //DeleteFiles = parseResult.GetValue(_deleteFilesOnly),
            CleanOnlyNonCurrentTfms = parseResult.GetValue(_nonCurrentOption),
            CleanObjDirectory = parseResult.GetValue(_objOption),
            CleanNupkgFiles = parseResult.GetValue(_nupkgOption),
            //Force = parseResult.GetValue(_forceOption),
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            //ConfirmLevel = parseResult.GetValue(_confirmLevelOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
        base.Console = new SpectreConsoleOutput(options.LogLevel);

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            // If no root is specified, use the current directory
            rootPath = Environment.CurrentDirectory;
        }

        var app = new CleaningApplication(base.Console, (a, b, c) => new MarkDeleteResultStatsProcessor(a, b, c));
        await app.InitAsync(options);
        await app.RunAsync(new[] { rootPath }, options);

        return 0;
    }
}
