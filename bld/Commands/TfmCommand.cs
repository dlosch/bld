using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace bld.Commands;

internal sealed class TfmCommand : BaseCommand {

    private readonly Option<string> _fromOption = new Option<string>("--from") {
        Description = "Source target framework (e.g., net8.0)."
    };

    private readonly Option<string> _toOption = new Option<string>("--to") {
        Description = "Target framework to migrate to (e.g., net9.0)."
    };

    private readonly Option<bool> _updateOption = new Option<bool>("--update", "-u") {
        Description = "Apply changes (default is dry-run).",
        DefaultValueFactory = _ => false
    };

    public TfmCommand(IConsoleOutput console) : base("tfm", "Migrate TargetFramework/TargetFrameworks between versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_fromOption);
        Add(_toOption);
        Add(_updateOption);
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

        var from = parseResult.GetValue(_fromOption);
        var to = parseResult.GetValue(_toOption);
        var apply = parseResult.GetValue(_updateOption);

        if (string.IsNullOrEmpty(from)) {
            Console.WriteError("The --from parameter is required.");
            return 1;
        }

        // Auto-detect highest SDK version if --to is not specified
        if (string.IsNullOrEmpty(to)) {
            to = await DetectHighestSdkVersionAsync();
            if (string.IsNullOrEmpty(to)) {
                Console.WriteError("Could not auto-detect highest SDK version. Please specify --to parameter.");
                return 1;
            }
            Console.WriteInfo($"Auto-detected target framework: {to}");
        }

        Console.WriteInfo($"Migrating projects from {from} to {to} in: {rootPath}");
        Console.WriteInfo($"Mode: {(apply ? "Apply changes" : "Dry run")}");

        try {
            var tfmService = new TfmService(Console, options);
            return await tfmService.MigrateTargetFrameworkAsync(rootPath, from, to, apply, cancellationToken);
        }
        catch (Exception ex) {
            Console.WriteError($"Error migrating target frameworks: {ex.Message}");
            return 1;
        }
    }

    private async Task<string?> DetectHighestSdkVersionAsync() {
        try {
            // Run 'dotnet --list-sdks' to get installed SDKs
            var process = new System.Diagnostics.Process {
                StartInfo = new System.Diagnostics.ProcessStartInfo {
                    FileName = "dotnet",
                    Arguments = "--list-sdks",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) {
                Console.WriteVerbose("Failed to list installed SDKs");
                return null;
            }

            // Parse output to find highest version
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var versions = new List<Version>();

            foreach (var line in lines) {
                // Example line: "8.0.100 [C:\Program Files\dotnet\sdk]"
                var parts = line.Split(' ');
                if (parts.Length > 0 && Version.TryParse(parts[0], out var version)) {
                    versions.Add(version);
                }
            }

            if (versions.Count == 0) {
                return null;
            }

            var highest = versions.Max();
            return highest != null ? $"net{highest.Major}.{highest.Minor}" : null;
        }
        catch (Exception ex) {
            Console.WriteVerbose($"Error detecting SDK versions: {ex.Message}");
            return null;
        }
    }
}