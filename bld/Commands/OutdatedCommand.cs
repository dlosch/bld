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

    private readonly Option<bool> _orphanedOption = new Option<bool>("--orphaned") {
        Description = "List PackageVersion entries in Directory.Packages.props with no matching PackageReference and a newer version on NuGet. Works for both project and solution input. Report-only.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _commentOrphansOption = new Option<bool>("--comment-orphans") {
        Description = "On --apply, comment out outdated orphan PackageVersion entries in Directory.Packages.props. Only honored when the input is a solution (.sln / .slnx / .slnf), since a single project cannot see all consumers of the CPM file. Implies --orphaned for reporting.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<MaxBump> _maxBumpOption = new Option<MaxBump>("--max-bump") {
        Description = "Largest version step to propose, relative to the version a package is pinned at now: 'major' (no cap), 'minor' (same major, no breaking change per SemVer) or 'patch' (same major and minor). Versions above the cap are reported in the 'held' column instead of being applied.",
        DefaultValueFactory = _ => MaxBump.Major
    };

    private readonly Option<string[]> _packageOption = new Option<string[]>("--package", "-p") {
        Description = "Only consider packages whose id matches one of these patterns. Supports '*' wildcards, is case-insensitive, may be repeated, and accepts ';'-separated lists. Default: all packages.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<string[]> _excludeOption = new Option<string[]>("--exclude") {
        Description = "Skip packages whose id matches one of these patterns. Same syntax as --package and applied after it.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<bool> _allowConflictsOption = new Option<bool>("--allow-conflicts") {
        Description = "Update packages even when a dependency they require stays at a version that does not satisfy their declared range. Without this, such packages are held back.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _verifyRestoreOption = new Option<bool>("--verify-restore") {
        Description = "After --apply, run 'dotnet restore' on the input and fail the command if NuGet reports errors. The only check that sees what NuGet actually resolves.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<string[]> _sourceOption = new Option<string[]>("--source") {
        Description = "Package source(s) to query: the name of a source in nuget.config or a v3 feed URL. May be repeated. Overrides nuget.config, including package source mapping. Default: the enabled sources from the nuget.config hierarchy, or nuget.org if there is none.",
        AllowMultipleArgumentsPerToken = true
    };

    private readonly Option<bool> _ignoreSourceMappingOption = new Option<bool>("--ignore-source-mapping") {
        Description = "Query every enabled source for every package instead of honoring the packageSourceMapping section of nuget.config.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _interactiveOption = new Option<bool>("--interactive", "-i") {
        Description = "Prompt yes/no for each outdated package before applying. If you skip a package that another picked package depends on at a higher version, the conflict is surfaced so you can include the dependency, skip the picker, or accept the risk. Implies --apply.",
        DefaultValueFactory = _ => false
    };

    public OutdatedCommand(IConsoleOutput console) : base("outdated", "Check for outdated NuGet packages and optionally update them to latest versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_applyOption);
        Add(_skipTfmCheckOption);
        Add(_prereleaseOption);
        Add(_orphanedOption);
        Add(_commentOrphansOption);
        Add(_interactiveOption);
        Add(_maxBumpOption);
        Add(_packageOption);
        Add(_excludeOption);
        Add(_allowConflictsOption);
        Add(_verifyRestoreOption);
        Add(_sourceOption);
        Add(_ignoreSourceMappingOption);
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
        var listOrphans = parseResult.GetValue(_orphanedOption);
        var commentOrphans = parseResult.GetValue(_commentOrphansOption);
        var interactive = parseResult.GetValue(_interactiveOption);
        if (interactive) applyUpdates = true;

        var maxBump = parseResult.GetValue(_maxBumpOption);
        var includePatterns = OutdatedService.SplitPatterns(parseResult.GetValue(_packageOption));
        var excludePatterns = OutdatedService.SplitPatterns(parseResult.GetValue(_excludeOption));
        var allowConflicts = parseResult.GetValue(_allowConflictsOption);
        var verifyRestore = parseResult.GetValue(_verifyRestoreOption);
        var sources = parseResult.GetValue(_sourceOption) ?? Array.Empty<string>();
        var ignoreSourceMapping = parseResult.GetValue(_ignoreSourceMappingOption);

        var service = new OutdatedService(Output, options);
        return await service.CheckOutdatedPackagesAsync(rootValue, applyUpdates, skipTfmCheck, includePrerelease, listOrphans, commentOrphans, interactive, maxBump, includePatterns, excludePatterns, allowConflicts, verifyRestore, sources, ignoreSourceMapping, cancellationToken);
    }
}