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
    public async Task<int> RunAsync(string[] rootPaths, CleaningOptions options, CancellationToken cancellationToken = default) {
        if (!_isInitialized) {
            throw new InvalidOperationException("Application not initialized. Call InitAsync first.");
        }

        using var msbuildService = new MSBuildService(_console);
        var errorSink = new ErrorSink(_console);
        var scanner = new SlnScanner(options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        using var projParser = new ProjParser(_console, errorSink, options);
        var fileSystem = new FileSystem(_console, errorSink);
        var cache = new ProjCfgCache(_console);

        var markDeleteProcessor = new MarkDeleteProcessor(_console, fileSystem, options, errorSink);
        var markDeleteStatsProcessor = processorFactory(_console, errorSink, options);

        _console.WriteRule("[bold blue]bld clean tool[/]");

        var stopwatch = Stopwatch.StartNew();

        var parallelOptions = new ParallelOptions {
            MaxDegreeOfParallelism = options.MaxDegreeOfParallelism
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
                    ctx.Status($"Evaluating projects: {current}/{total} ([bold]{Markup.Escape(Path.GetFileName(projCfg.Path))}[/])");

                    var properties = projParser.LoadProject(projCfg, ProjConstants.PropertyNames);
                    if (properties is null) {
                        _console.WriteWarning($"Error evaluating project properties for {projCfg.Path} and configuration {projCfg.Configuration}.");
                        return;
                    }

                    await markDeleteProcessor.ProcessAsync(projCfg, properties);
                });
            });

            await markDeleteProcessor.ProcessDirs();

            var res = markDeleteProcessor.GetResult();

            if (options.Interactive) {
                res = Pick(res, options);
                if (res is null) return 0;
            }

            // Run the processor before reporting: deletion failures are recorded in the sink and must
            // be included in both the error table and the exit code.
            await markDeleteStatsProcessor.ProcessAsync(res);

            stopwatch.Stop();

            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");

            errorSink.WriteTo();

            return errorSink.HasErrors ? 1 : 0;
        }
        catch (Exception ex) {
            _console.WriteException(ex);
            return 1;
        }
    }

    /// <summary>
    /// Lets the user choose from everything that was marked. Null when there is nothing to do
    /// afterwards: nothing marked, the picker cancelled, or nothing left checked. The picker only
    /// ever removes entries, so every path that survives it already passed the marking guards;
    /// <see cref="CleanSelection.Apply"/> is what holds that "only removes" to its word.
    /// </summary>
    private MarkDeleteResult? Pick(MarkDeleteResult result, CleaningOptions options) {
        var verb = options.Delete ? "delete" : "put in the script";
        if (!_console.CanPrompt) {
            throw new InvalidOperationException("--interactive needs an interactive terminal. Use --obj/--publish/--test-results without it instead.");
        }
        if (result.IsEmpty) {
            _console.WriteLine("No directories marked for deletion.");
            return null;
        }

        var model = CleanPickerModel.From(result, options);
        var total = model.Groups.SelectMany(g => g.Rows).Sum(r => r.Bytes);
        var what = result.Files.Count == 0
            ? $"{result.Directories.Count} directories"
            : $"{result.Directories.Count} directories and {result.Files.Count} package files";
        var title = $"[bold]Select what to {verb}[/] ({what}, {Markup.Escape(CleanPickerRenderer.Size(total))})";
        var outcome = _console.RunCleanPicker(model, title);
        if (outcome.Cancelled) {
            _console.WriteLine(options.Delete ? "Nothing deleted." : "Nothing written.");
            return null;
        }

        var kept = CleanSelection.Apply(result, outcome.Selected);
        if (kept.IsEmpty) {
            _console.WriteLine("Nothing selected; nothing " + (options.Delete ? "deleted." : "written."));
            return null;
        }
        return kept;
    }
}