using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace bld.Services.NuGet;

/// <summary>
/// `nuget keep`: finds the projects under the root, asks MSBuild where each one's project.assets.json
/// is, and hands them to <see cref="PrivatePackageBackup"/>. The step `clean --keep-private-packages`
/// runs before it cleans, on its own.
/// </summary>
internal sealed class PrivatePackageKeepApplication(IConsoleOutput console) {

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task InitAsync(CleaningOptions options) {
        // Before any MSBuild type is loaded.
        MSBuildService.RegisterMSBuildDefaults(console, options);
        return Task.CompletedTask;
    }

    /// <returns>1 when nothing was found or a solution or project could not be evaluated, otherwise 0.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task<int> RunAsync(string rootPath, CleaningOptions options, string backupDirectory, string configPath, CancellationToken cancellationToken) {
        using var msbuildService = new MSBuildService(console);
        var errorSink = new ErrorSink(console);
        var scanner = new SlnScanner(options, errorSink);
        var slnParser = new SlnParser(console, errorSink);
        using var projParser = new ProjParser(console, errorSink, options);
        var fileSystem = new FileSystem(console, errorSink);
        var cache = new ProjCfgCache(console);

        console.WriteRule("[bold blue]bld nuget keep (BETA)[/]");

        var projCfgs = new List<ProjCfg>();
        await foreach (var sln in scanner.Enumerate(rootPath).WithCancellation(cancellationToken)) {
            await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem).WithCancellation(cancellationToken)) {
                if (cache.Add(projCfg)) projCfgs.Add(projCfg);
            }
        }
        if (projCfgs.Count == 0) {
            console.WriteError($"No solution or project found under {rootPath}.");
            return 1;
        }

        var assetsFiles = new ConcurrentBag<string>();
        await console.StartStatusAsync($"Evaluating {projCfgs.Count} project configurations...", async ctx => {
            var count = 0;
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = options.MaxDegreeOfParallelism, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(projCfgs, parallelOptions, (projCfg, _) => {
                ctx?.Status($"Evaluating projects: {Interlocked.Increment(ref count)}/{projCfgs.Count} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");
                // A failed evaluation is already in the error sink; the same lookup as clean --keep-private-packages.
                if (projParser.LoadProject(projCfg, ProjConstants.PropertyNames) is { } properties) {
                    assetsFiles.Add(ProjectAssetsReader.GetPath(properties.Properties.GetValueOrDefault("MSBuildProjectExtensionsPath"), projCfg.ProjDir));
                }
                return ValueTask.CompletedTask;
            });
        });

        var settings = PackageSourceResolver.LoadSettings(Path.GetDirectoryName(configPath)!);
        new PrivatePackageBackup(console).Run(assetsFiles, backupDirectory, configPath, settings);

        errorSink.WriteTo();
        return errorSink.HasErrors ? 1 : 0;
    }
}
