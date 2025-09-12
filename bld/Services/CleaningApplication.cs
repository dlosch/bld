using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace bld.Services;

/// <summary>
/// Main application orchestrator
/// </summary>
internal class CleaningApplication(IConsoleOutput _console, Func<IConsoleOutput, ErrorSink, CleaningOptions, IMarkDeleteResultProcessor> processorFactory) {
    private bool _isInitialized = false;

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task InitAsync(CleaningOptions options) {
        //_console = new SpectreConsoleOutput(options.LogLevel);
        // this must be called before any other MSBuild Type is loaded.
        // JIT might change that behavior
        MSBuildService.RegisterMSBuildDefaults(_console, options);
        _isInitialized = true;
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task RunAsync(string[] rootPaths, CleaningOptions options) {
        if (!_isInitialized) {
            //await InitAsync(options);
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

        try {
            foreach (var rootPath in rootPaths) {
                // todo check for csproj
                await foreach (var sln in scanner.Enumerate(rootPath)) {
                    await _console.StartStatusAsync($"Processing solution {sln}", async ctx => {
                        var curProj = default(string);
                        await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                            if (!cache.Add(projCfg)) continue;

                            if (curProj is null || curProj != projCfg.Path) {
                                curProj = projCfg.Path;
                                ctx.Status($"Processing project: {projCfg.Path}");
                            }

                            var properties = projParser.LoadProject(projCfg, ProjConstants.PropertyNames);
                            if (properties is null) {
                                _console.WriteWarning($"Error evaluating project properties for {projCfg.Path} and configuration {projCfg.Configuration}.");
                                continue;
                            }

                            await markDeleteProcessor.ProcessAsync(projCfg, properties);
                        }
                    });
                }
            }

            await markDeleteProcessor.ProcessDirs();

            stopwatch.Stop();

            _console.WriteInfo($"Total elapsed time: {stopwatch.Elapsed}");

            errorSink.WriteTo();

            //await markDeleteProcessor.DumpDirs();
            var res = markDeleteProcessor.GetResult();
            await markDeleteStatsProcessor.ProcessAsync(res);
        }
        catch (Exception ex) {
            _console.WriteException(ex);
        }
    }
}