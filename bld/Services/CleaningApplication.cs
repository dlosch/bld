using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;

namespace bld.Services;

/// <summary>
/// Main application orchestrator
/// </summary>
internal class CleaningApplication(IConsoleOutput _console, Func<IConsoleOutput, ErrorSink, CleaningOptions, IMarkDeleteResultProcessor> processorFactory) {
    private bool _isInitialized = false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task InitAsync(CleaningOptions options) {
        // this must be called before any other MSBuild Type is loaded.
        MSBuildService.RegisterMSBuildDefaults(_console, options);
        _isInitialized = true;
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task RunAsync(string[] rootPaths, CleaningOptions options, CancellationToken cancellationToken = default) {
        if (!_isInitialized) {
            throw new InvalidOperationException("Application not initialized. Call InitAsync first.");
        }

        using var msbuildService = new MSBuildService(_console);
        var errorSink = new ErrorSink(_console);
        var scanner = new SlnScanner(options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var projParser = new ProjParser(_console, errorSink, options);
        var fileSystem = new FileSystem(_console, errorSink);
        var cache = new ProjCfgCache(_console);

        var markDeleteProcessor = new MarkDeleteProcessor(_console, fileSystem, options, errorSink);
        var markDeleteStatsProcessor = processorFactory(_console, errorSink, options);

        _console.WriteRule("[bold blue]bld clean tool[/]");

        var stopwatch = Stopwatch.StartNew();

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = options.Parallel ? options.MaxDegreeOfParallelism : 1
        };

        try {
            var allSlns = new ConcurrentBag<string>();
            await Parallel.ForEachAsync(rootPaths, parallelOptions, async (rootPath, ct) => {
                await foreach (var sln in scanner.Enumerate(rootPath)) {
                    allSlns.Add(sln);
                }
            });

            var allProjCfgs = new ConcurrentBag<ProjCfg>();
            await Parallel.ForEachAsync(allSlns, parallelOptions, async (sln, ct) => {
                await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                    if (cache.Add(projCfg)) {
                        allProjCfgs.Add(projCfg);
                    }
                }
            });

            await _console.StartStatusAsync($"Evaluating {allProjCfgs.Count} project configurations...", async ctx => {
                var count = 0;
                var total = allProjCfgs.Count;

                await Parallel.ForEachAsync(allProjCfgs, parallelOptions, async (projCfg, ct) => {
                    var current = Interlocked.Increment(ref count);
                    ctx.Status($"Evaluating projects: {current}/{total} ([bold]{Path.GetFileName(projCfg.Path)}[/])");

                    var properties = projParser.LoadProject(projCfg, ProjConstants.PropertyNames);
                    if (properties is null) {
                        _console.WriteWarning($"Error evaluating project properties for {projCfg.Path} and configuration {projCfg.Configuration}.");
                        return;
                    }

                    await markDeleteProcessor.ProcessAsync(projCfg, properties);
                });
            });

            await markDeleteProcessor.ProcessDirs();

            stopwatch.Stop();

            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");

            errorSink.WriteTo();

            var res = markDeleteProcessor.GetResult();
            await markDeleteStatsProcessor.ProcessAsync(res);
        }
        catch (Exception ex) {
            _console.WriteException(ex);
        }
    }
}