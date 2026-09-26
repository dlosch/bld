using bld.Infrastructure;
using bld.Models;
using bld.Services;
using bld.Services.NuGet;
using System.CommandLine;

namespace bld.Commands;

internal sealed class CleanCommand : BaseCommand {

    private readonly Option<bool> _forceOption = new Option<bool>("--force") {
        Description = "Do not ask for confirmation (requires explicit root).",
        DefaultValueFactory = _ => false
    };

    private readonly Option<string> _outputFileOption = new Option<string>("--output-file", "-o") {
        Description = "Write a deletion script instead of deleting (clean.cmd or clean.sh when no path is given).",
        Arity = ArgumentArity.ZeroOrOne,
        DefaultValueFactory = _ => (OperatingSystem.IsWindows() ? "clean.cmd" : "clean.sh")
    };

    private readonly Option<bool> _deleteOption = new Option<bool>("--delete") {
        Description = "Delete without the picker, asking per directory unless --force is given.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<ConfirmLevel?> _confirmLevelOption = new Option<ConfirmLevel?>("--confirm") {
        Description = "Confirmation Level for deletion.",
        DefaultValueFactory = _ => ConfirmLevel.Directory
    };

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

    private readonly Option<bool> _publishOption = new Option<bool>("--publish") {
        Description = "Also clean publish output (PublishDir) and pack output (PackageOutputPath), including artifacts/publish and artifacts/package in the artifacts layout.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _testResultsOption = new Option<bool>("--test-results") {
        Description = "Also clean TestResults directories (dotnet test output: .trx, coverage) next to each project and its solution.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _interactiveOption = new Option<bool>("--interactive", "-i") {
        Description = "Pick the directories from a list grouped by project: b/o/p/g/t toggle bin, obj, publish, package and test results for every project on the top line or for one project on its line, space toggles a directory, enter confirms. --obj/--publish/--test-results only decide what starts out checked. Deletes what was picked after one confirmation; pass --output-file to write the script instead. On by default unless --delete or --output-file is given; needs an interactive terminal.",
        DefaultValueFactory = _ => true
    };

    private readonly Option<bool> _keepPrivatePackagesOption = new Option<bool>("--keep-private-packages") {
        Description = $"Before cleaning, copy every package restored from a source other than nuget.org from the NuGet packages folder into a folder feed and write {PrivatePackageBackup.ConfigFileName} next to the root, pointing those sources at it. Keeps the repo restorable after the private feed is gone and the caches are cleared. Needs a prior restore.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<string> _privatePackagesDirOption = new Option<string>("--private-packages-dir") {
        Description = $"Folder for --keep-private-packages (default: {PrivatePackageBackup.DefaultDirectoryName} in the root directory).",
    };

    public CleanCommand(IConsoleOutput console) : base("clean", "Cleans solution / project build output (bin/obj etc.)", console) {
        Add(_rootOption);
        Add(_depthOption);

        Add(_nonCurrentOption);
        Add(_objOption);
        Add(_keepAssetsOption);
        Add(_publishOption);
        Add(_testResultsOption);
        Add(_interactiveOption);
        Add(_keepPrivatePackagesOption);
        Add(_privatePackagesDirOption);

        Add(_logLevelOption);

        Add(_outputFileOption);
    
        Add(_forceOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        Add(_concurrencyOption);

        Add(_deleteOption);
        Add(_confirmLevelOption);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        // The script is only written when asked for, and asking for it or for a direct --delete is
        // what turns the picker off, unless --interactive is given explicitly as well.
        var writeScript = parseResult.GetResult(_outputFileOption) is { Implicit: false };
        var delete = parseResult.GetValue(_deleteOption);
        var interactive = parseResult.GetResult(_interactiveOption) is { Implicit: false }
            ? parseResult.GetValue(_interactiveOption)
            : !writeScript && !delete;

        // The config goes next to the root so relative feed paths and nuget.config lookup start there;
        // a solution file as root means its directory.
        var rootPath = GetRootPath(parseResult);
        var rootDirectory = File.Exists(rootPath) ? Path.GetDirectoryName(rootPath)! : rootPath;
        var keepPrivatePackages = parseResult.GetValue(_keepPrivatePackagesOption) || parseResult.GetValue(_privatePackagesDirOption) is not null;

        var options = new CleaningOptions {
            OutputFile = writeScript ? parseResult.GetValue(_outputFileOption) : null,
            Delete = delete,
            CleanOnlyNonCurrentTfms = parseResult.GetValue(_nonCurrentOption),
            CleanObjDirectory = parseResult.GetValue(_objOption),
            KeepRestoreArtifacts = parseResult.GetValue(_keepAssetsOption),
            CleanPublishDirectory = parseResult.GetValue(_publishOption),
            CleanTestResults = parseResult.GetValue(_testResultsOption),
            Interactive = interactive,
            PrivatePackagesDirectory = keepPrivatePackages
                ? Path.GetFullPath(parseResult.GetValue(_privatePackagesDirOption) ?? Path.Combine(rootDirectory, PrivatePackageBackup.DefaultDirectoryName))
                : null,
            OfflineConfigPath = keepPrivatePackages ? Path.Combine(rootDirectory, PrivatePackageBackup.ConfigFileName) : null,
            Force = parseResult.GetValue(_forceOption),
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            ConfirmLevel = parseResult.GetValue(_confirmLevelOption),
            MaxDegreeOfParallelism = parseResult.GetValue(_concurrencyOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
        base.Output = new SpectreConsoleOutput(options.LogLevel);

        if (options.Force && !HasExplicitRoot(parseResult)) {
            Output.WriteError("--force requires an explicit root path via --root/-r or positional root argument.");
            return 1;
        }

        if (options.Interactive && !Output.CanPrompt) {
            Output.WriteError("The directory picker needs an interactive terminal. Pass --delete to delete or --output-file to write a deletion script instead.");
            return 1;
        }
        // A bare -o takes the next token as its path, so `clean -o <root>` would otherwise clean the
        // current directory and try to write the script over the root.
        if (writeScript && Directory.Exists(options.OutputFile)) {
            Output.WriteError($"--output-file points at a directory: {options.OutputFile}. Put the root before -o or give the script a file name.");
            return 1;
        }
        if (!options.Interactive && !options.Delete && !writeScript) {
            Output.WriteError("Nothing to do without the picker. Pass --delete to delete or --output-file to write a deletion script.");
            return 1;
        }
        // Picking directories is a deletion flow: the picker chooses, one question confirms the
        // whole selection. Asking for an output file explicitly still writes the script instead.
        if (options.Interactive && !writeScript) {
            options.Delete = true;
        }
        // The picker plus that one question are the confirmation, so nothing is asked per
        // directory afterwards. An explicit --confirm still wins.
        if (options.Interactive && parseResult.GetResult(_confirmLevelOption)?.Implicit != false) {
            options.ConfirmLevel = ConfirmLevel.None;
        }

        var app = new CleaningApplication(base.Output
            , (a, b, c) => options.Delete
              ? new MarkDeleteResultDeleteProcessor(a, b, c)
             : new MarkDeleteResultBatchFileProcessor(a, b, c)

            );
        await app.InitAsync(options);
        return await app.RunAsync(new[] { rootPath }, options, cancellationToken);
    }
}
