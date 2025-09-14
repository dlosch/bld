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

    private readonly Option<bool> _includeFrameworkOption = new Option<bool>("--include-framework") {
        Description = "Include framework packages (Microsoft.*/System.*/NETStandard.*) in reverse dependency analysis (excluded by default).",
        DefaultValueFactory = _ => false
    };

    private readonly Option<int> _maxDepthOption = new Option<int>("--max-depth") {
        Description = "Maximum depth to traverse in the dependency tree (default: 8).",
        DefaultValueFactory = _ => 8
    };

    public DepsGraphCommand(IConsoleOutput console) : base("deps", "Check for outdated NuGet packages and optionally update them to latest versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_maxDepthOption);
        Add(_applyOption);
        Add(_skipTfmCheckOption);
        Add(_prereleaseOption);
        Add(_reverseOption);
        Add(_includeFrameworkOption);
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
        var includeFramework = parseResult.GetValue(_includeFrameworkOption);
        var maxDepth = parseResult.GetValue(_maxDepthOption);

        var service = new OutdatedService(Console, options);
        
        if (showReverse) {
            // For reverse dependencies, we exclude framework packages by default (unless --include-framework is specified)
            var excludeFramework = !includeFramework;
            return await service.BuildReverseDependencyGraphAsync(rootValue, includePrerelease, excludeFramework, maxDepth, cancellationToken: cancellationToken);
        } else {
            return await service.BuildDependencyGraphAsync(rootValue, includePrerelease, maxDepth, cancellationToken: cancellationToken);
        }
    }
}
