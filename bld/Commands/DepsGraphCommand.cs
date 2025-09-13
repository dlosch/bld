using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class DepsGraphCommand : BaseCommand {

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

    private readonly Option<bool> _reverseOption = new Option<bool>("--reverse") {
        Description = "Display reverse dependency graph showing which packages depend on each package.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _excludeFrameworkOption = new Option<bool>("--exclude-framework") {
        Description = "Exclude framework packages (Microsoft.*/System.*/NETStandard.*) from reverse dependency analysis.",
        DefaultValueFactory = _ => false
    };

    public DepsGraphCommand(IConsoleOutput console) : base("deps", "Check for outdated NuGet packages and optionally update them to latest versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_applyOption);
        Add(_skipTfmCheckOption);
        Add(_prereleaseOption);
        Add(_reverseOption);
        Add(_excludeFrameworkOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }

        base.Console = new SpectreConsoleOutput(options.LogLevel);

        var rootValue = parseResult.GetValue(_rootOption) ?? parseResult.GetValue(_rootArgument);
        if (string.IsNullOrEmpty(rootValue)) {
            rootValue = Directory.GetCurrentDirectory();
        }

        var applyUpdates = parseResult.GetValue(_applyOption);
        var skipTfmCheck = parseResult.GetValue(_skipTfmCheckOption);
        var includePrerelease = parseResult.GetValue(_prereleaseOption);
        var showReverse = parseResult.GetValue(_reverseOption);
        var excludeFramework = parseResult.GetValue(_excludeFrameworkOption);

        var service = new OutdatedService(Console, options);
        
        if (showReverse) {
            return await service.BuildReverseDependencyGraphAsync(rootValue, includePrerelease, excludeFramework, cancellationToken: cancellationToken);
        } else {
            return await service.BuildDependencyGraphAsync(rootValue, includePrerelease, cancellationToken: cancellationToken);
        }
    }
}
