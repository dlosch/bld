using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class SbomCommand : BaseCommand {

    private readonly Option<string> _outputOption = new Option<string>("--output", "-o") {
        Description = "Output directory for SBOM files.",
        DefaultValueFactory = _ => "./sbom"
    };

    private readonly Option<string> _formatOption = new Option<string>("--format", "-f") {
        Description = "SBOM format (spdx, cyclonedx, both).",
        DefaultValueFactory = _ => "both"
    };

    private readonly Option<bool> _includeTestsOption = new Option<bool>("--include-tests") {
        Description = "Include test projects in SBOM generation.",
        DefaultValueFactory = _ => false
    };

    public SbomCommand(IConsoleOutput console) : base("sbom", "Create SBOM for output projects (executables, container images, nuget packages, dotnet tools).", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_outputOption);
        Add(_formatOption);
        Add(_includeTestsOption);
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

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath)) {
            rootPath = Environment.CurrentDirectory;
        }

        var outputPath = parseResult.GetValue(_outputOption) ?? "./sbom";
        var format = parseResult.GetValue(_formatOption) ?? "both";
        var includeTests = parseResult.GetValue(_includeTestsOption);

        Console.WriteInfo($"Generating SBOM in format '{format}' to '{outputPath}'");
        Console.WriteInfo($"Root path: {rootPath}");
        Console.WriteInfo($"Include tests: {includeTests}");

        try {
            var sbomService = new SbomService(Console, options);
            await sbomService.GenerateSbomAsync(rootPath, outputPath, format, includeTests, cancellationToken);
            
            Console.WriteInfo("SBOM generation completed successfully.");
            return 0;
        }
        catch (Exception ex) {
            Console.WriteError($"Error generating SBOM: {ex.Message}");
            return 1;
        }
    }
}