using bld.Infrastructure;
using bld.Models;
using bld.Services;
using bld.Services.NuGet;
using System.CommandLine;

namespace bld.Commands;

internal sealed class NugetKeepCommand : BaseCommand {

    private readonly Option<string> _dirOption = new Option<string>("--dir") {
        Description = $"Folder feed to copy the packages into (default: {PrivatePackageBackup.DefaultDirectoryName} in the root directory). {PrivatePackageBackup.ConfigFileName} is always written in the root directory.",
    };

    public NugetKeepCommand(IConsoleOutput console) : base("keep",
        $"Keep the packages the projects restored from a source other than nuget.org in a folder feed inside the repo and write {PrivatePackageBackup.ConfigFileName} next to it, so they still restore after the private feed is gone and the NuGet caches are cleared: dotnet restore --configfile {PrivatePackageBackup.ConfigFileName}. Which source a package came from is what restore recorded in its .nupkg.metadata; a file nuget.org signed is not kept even when a mirror served it. Needs a prior restore. The same step as clean --keep-private-packages, without the cleaning.",
        console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_dirOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_concurrencyOption);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            MaxDegreeOfParallelism = parseResult.GetValue(_concurrencyOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
        base.Output = new SpectreConsoleOutput(options.LogLevel);

        var (backupDirectory, configPath) = ResolvePaths(parseResult);
        var app = new PrivatePackageKeepApplication(Output);
        await app.InitAsync(options);
        return await app.RunAsync(GetRootPath(parseResult), options, backupDirectory, configPath, cancellationToken);
    }

    /// <summary>
    /// The folder feed and the config, as `clean --keep-private-packages` places them: the config goes
    /// next to the root so relative feed paths and the nuget.config lookup start there; a solution or
    /// project file as root means its directory.
    /// </summary>
    internal (string BackupDirectory, string ConfigPath) ResolvePaths(ParseResult parseResult) {
        var rootPath = GetRootPath(parseResult);
        var rootDirectory = File.Exists(rootPath) ? Path.GetDirectoryName(rootPath)! : rootPath;
        var backupDirectory = Path.GetFullPath(parseResult.GetValue(_dirOption) ?? Path.Combine(rootDirectory, PrivatePackageBackup.DefaultDirectoryName));
        return (backupDirectory, Path.Combine(rootDirectory, PrivatePackageBackup.ConfigFileName));
    }
}
