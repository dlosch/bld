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

        var service = new OutdatedService(Output, options);
        return await service.CheckOutdatedPackagesAsync(rootValue, applyUpdates, skipTfmCheck, includePrerelease, listOrphans, commentOrphans, interactive, cancellationToken);
    }
}