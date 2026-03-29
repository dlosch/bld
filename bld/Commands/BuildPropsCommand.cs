using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class BuildPropsCommand : BaseCommand {

    private readonly Option<string?> _propertiesOption = new Option<string?>("--properties") {
        Description = "Comma-separated list of properties to trace (e.g., TargetFramework,Nullable). When specified, only shows these properties.",
        DefaultValueFactory = _ => null
    };

    private readonly Option<bool> _listOption = new Option<bool>("--list", "-l") {
        Description = "Only list imported Directory.Build.props files in a tree structure, without showing property contents.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _noOverriddenOption = new Option<bool>("--no-overridden") {
        Description = "Hide properties that originate from Directory.Build.props but are overridden later in evaluation.",
        DefaultValueFactory = _ => false
    };

    public BuildPropsCommand(IConsoleOutput console)
        : base("build-props", "Analyze Directory.Build.props files and property provenance across projects. (BETA)", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_listOption);
        Add(_noOverriddenOption);
        Add(_propertiesOption);
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

        var rootPath = GetRootPath(parseResult);
        var listOnly = parseResult.GetValue(_listOption);
        var includeOverridden = !parseResult.GetValue(_noOverriddenOption);
        var propertiesFilter = parseResult.GetValue(_propertiesOption);
        var filterProperties = propertiesFilter?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try {
            var service = new BuildPropsService(Output, options);
            return await service.AnalyzeAsync(rootPath, filterProperties, options.MarkdownOutput, listOnly, includeOverridden, cancellationToken);
        }
        catch (Exception ex) {
            Output.WriteError($"Error analyzing Directory.Build.props: {ex.FormatMessage()}");
            return 1;
        }
    }
}
