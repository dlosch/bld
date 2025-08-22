using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class CleanCommand : BaseCommand {

    private readonly Option<bool> _forceOption = new Option<bool>("--force") {
        Description = "Do not ask for confirmation (requires explicit root).",
        DefaultValueFactory = _ => false
    };

    private readonly Option<string> _outputFileOption = new Option<string>("--output-file", "-o") {
        Description = "Path to the output file.",
        DefaultValueFactory = _ => (OperatingSystem.IsWindows() ? "clean.cmd" : "clean.sh")
    };

    private readonly Option<bool> _deleteOption = new Option<bool>("--delete") {
        Description = "Actually delete files.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<ConfirmLevel?> _confirmLevelOption = new Option<ConfirmLevel?>("--confirm") {
        Description = "Confirmation Level for deletion.",
        DefaultValueFactory = _ => ConfirmLevel.Directory
    };

    //private readonly Option<bool> _deleteEmptyDirs = new Option<bool>("--delete-empty-directories") {
    //    Description = "Also delete empty directories (in addition to regular logic).",
    //    DefaultValueFactory = _ => false
    //};

    //private readonly Option<ConfirmLevel> _confirmOption = new Option<ConfirmLevel>("--confirm") {
    //    Description = "Confirmation scope (Force|Sln|Proj|Dir). --force implies Force.",
    //    DefaultValueFactory = _ => ConfirmLevel.Directory
    //};

    public CleanCommand(IConsoleOutput console) : base("clean", "Cleans solution / project build output (bin/obj etc.)", console) {
        Add(_rootOption);
        Add(_depthOption);

        Add(_nonCurrentOption);
        Add(_objOption);

        Add(_logLevelOption);

        Add(_outputFileOption);
        //Add(_logFileOption);

        Add(_forceOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        Add(_deleteOption);
        //Add(_deleteEmptyDirs);
        //Add(_confirmOption);
        //Add(_nupkgOption);

        //Add(_confirmLevelOption);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var errors = new List<string>();

        var options = new CleaningOptions {
            //DryRun = parseResult.GetValue(_dryRunOption), // If --delete is specified, disable dry run
            OutputFile = parseResult.GetValue(_outputFileOption),
            //LogFile = parseResult.GetValue(_logFileOption),
            Delete = parseResult.GetValue(_deleteOption),
            //DeleteEmptyDirectories = parseResult.GetValue(_deleteEmptyDirs),
            //DeleteFiles = parseResult.GetValue(_deleteFilesOnly),
            CleanOnlyNonCurrentTfms = parseResult.GetValue(_nonCurrentOption),
            CleanObjDirectory = parseResult.GetValue(_objOption),
            CleanNupkgFiles = parseResult.GetValue(_nupkgOption),
            Force = parseResult.GetValue(_forceOption),
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            ConfirmLevel = parseResult.GetValue(_confirmLevelOption),
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

        var app = new CleaningApplication(base.Console
            , (a, b, c) => options.Delete
              ? new MarkDeleteResultDeleteProcessor(a, b, c)
             : new MarkDeleteResultBatchFileProcessor(a, b, c)

            );
        await app.InitAsync(options);
        await app.RunAsync(new[] { rootPath }, options);

        return 0;
    }
}
