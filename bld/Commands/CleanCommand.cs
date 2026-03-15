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

    public CleanCommand(IConsoleOutput console) : base("clean", "Cleans solution / project build output (bin/obj etc.)", console) {
        Add(_rootOption);
        Add(_depthOption);

        Add(_nonCurrentOption);
        Add(_objOption);

        Add(_logLevelOption);

        Add(_outputFileOption);
    
        Add(_forceOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        Add(_parallelOption);
        Add(_concurrencyOption);

        Add(_deleteOption);
        Add(_confirmLevelOption);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            OutputFile = parseResult.GetValue(_outputFileOption),
            Delete = parseResult.GetValue(_deleteOption),
            CleanOnlyNonCurrentTfms = parseResult.GetValue(_nonCurrentOption),
            CleanObjDirectory = parseResult.GetValue(_objOption),
            CleanNupkgFiles = parseResult.GetValue(_nupkgOption),
            Force = parseResult.GetValue(_forceOption),
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            ConfirmLevel = parseResult.GetValue(_confirmLevelOption),
            Parallel = parseResult.GetValue(_parallelOption),
            MaxDegreeOfParallelism = parseResult.GetValue(_concurrencyOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
        base.Output = new SpectreConsoleOutput(options.LogLevel);

        var rootPath = GetRootPath(parseResult);

        var app = new CleaningApplication(base.Output
            , (a, b, c) => options.Delete
              ? new MarkDeleteResultDeleteProcessor(a, b, c)
             : new MarkDeleteResultBatchFileProcessor(a, b, c)

            );
        await app.InitAsync(options);
        await app.RunAsync(new[] { rootPath }, options, cancellationToken);

        return 0;
    }
}
