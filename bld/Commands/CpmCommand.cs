using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;

namespace bld.Commands;

internal sealed class CpmCommand : BaseCommand {

    private readonly Option<bool> _applyOption = new Option<bool>("--apply") {
        Description = "Apply changes to create Directory.Packages.props and update project files.",
        DefaultValueFactory = _ => false
    };

    private readonly Option<bool> _overwriteOption = new Option<bool>("--overwrite") {
        Description = "Overwrite existing Directory.Packages.props if it exists.",
        DefaultValueFactory = _ => false
    };

    public CpmCommand(IConsoleOutput console) : base("cpm", "Convert all projects in a solution to Central Package Management.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_applyOption);
        Add(_overwriteOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);

        Add(_parallelOption);
        Add(_concurrencyOption);

        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken) {
        var options = new CleaningOptions {
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
            Parallel = parseResult.GetValue(_parallelOption),
            MaxDegreeOfParallelism = parseResult.GetValue(_concurrencyOption),
            MarkdownOutput = parseResult.GetValue(_markdownOption),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
        base.Output = new SpectreConsoleOutput(options.LogLevel);

        var rootPath = GetRootPath(parseResult);

        var apply = parseResult.GetValue(_applyOption);
        var overwrite = parseResult.GetValue(_overwriteOption);

        Output.WriteInfo($"Converting projects to Central Package Management in: {rootPath}");
        Output.WriteInfo($"Mode: {(apply ? "Apply changes" : "Dry run")}");
        Output.WriteInfo($"Overwrite existing Directory.Packages.props: {overwrite}");

        try {
            var cpmService = new CpmService(Output, options);
            await cpmService.ConvertToCentralPackageManagementAsync(rootPath, apply, overwrite, cancellationToken);

            Output.WriteLine("Central Package Management conversion completed successfully.");
            return 0;
        }
        catch (Exception ex) {
            Output.WriteError($"Error converting to Central Package Management: {ex.FormatMessage()}");
            return 1;
        }
    }
}