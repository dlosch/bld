using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class OutdatedCommand : BaseCommand {

    private readonly Option<bool> _applyOption = new Option<bool>("--apply") {
        Description = "Apply package updates instead of just checking.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _skipTfmCheckOption = new Option<bool>("--skip-tfm-check") {
        Description = "Skip target framework compatibility checking when suggesting package updates.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _prereleaseOption = new Option<bool>("--prerelease", "--pre") {
        Description = "Include prerelease versions of NuGet packages.",
        DefaultValueFactory = _ => false
    };

    public OutdatedCommand(IConsoleOutput console) : base("outdated", "Check for outdated NuGet packages and optionally update them to latest versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_applyOption);
        Add(_skipTfmCheckOption);
        Add(_prereleaseOption);
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
            MarkdownOutput = parseResult.GetValue(_markdownOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }

        base.Output = new SpectreConsoleOutput(options.LogLevel);

        var rootValue = GetRootPath(parseResult);

        var applyUpdates = parseResult.GetValue(_applyOption);
        var skipTfmCheck = parseResult.GetValue(_skipTfmCheckOption);
        var includePrerelease = parseResult.GetValue(_prereleaseOption);

        var service = new OutdatedService(Output, options);
        return await service.CheckOutdatedPackagesAsync(rootValue, applyUpdates, skipTfmCheck, includePrerelease, cancellationToken);
    }
}