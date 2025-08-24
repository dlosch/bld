using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Xml.Linq;

namespace bld.Commands;

internal sealed class TfmCommand : BaseCommand {

    private readonly Option<string> _fromOption = new Option<string>("--from") {
        Description = "Source target framework (e.g., net8.0). If not specified, will be auto-detected from project files."
    };

    private readonly Option<string> _toOption = new Option<string>("--to") {
        Description = "Target framework to migrate to (e.g., net9.0)."
    };

    private readonly Option<bool> _applyOption = new Option<bool>("--apply") {
        Description = "Apply changes (default is dry-run).",
        DefaultValueFactory = _ => false
    };

    public TfmCommand(IConsoleOutput console) : base("tfm", "Migrate TargetFramework/TargetFrameworks between versions.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_fromOption);
        Add(_toOption);
        Add(_applyOption);
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
        var apply = parseResult.GetValue(_applyOption);

        // Auto-detect highest SDK version if --to is not specified
        if (string.IsNullOrEmpty(to)) {
            to = await DetectHighestSdkVersionAsync();
            if (string.IsNullOrEmpty(to)) {
                Console.WriteError("Could not auto-detect highest SDK version. Please specify --to parameter.");
                return 1;
            }
            Console.WriteInfo($"Auto-detected target framework: {to}");
        }

        // Auto-detect --from if not specified
        if (string.IsNullOrEmpty(from)) {
            from = await DetectSourceFrameworkAsync(rootPath);
            if (string.IsNullOrEmpty(from)) {
                Console.WriteError("Could not auto-detect source framework. Projects have multiple TargetFrameworks or no consistent TargetFramework. Please specify --from parameter.");
                return 1;
            }
            Console.WriteInfo($"Auto-detected source framework: {from}");
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

    private async Task<string?> DetectSourceFrameworkAsync(string rootPath) {
        try {
            // Initialize MSBuild first for SlnScanner/SlnParser
            var tempOptions = new CleaningOptions();
            MSBuildInitializer.Initialize(Console, tempOptions);
            
            var errorSink = new ErrorSink(Console);
            var projParser = new ProjParser(Console, errorSink, tempOptions);

            var targetFrameworks = new List<string>();

            // Check if the root path is a direct .csproj file
            if (File.Exists(rootPath) && Path.GetExtension(rootPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) {
                try {
                    var proj = new Proj(rootPath, null);
                    var projCfg = new ProjCfg(proj, null, null); // No specific configuration
                    var projectInfo = projParser.LoadProject(projCfg, Array.Empty<string>());
                    
                    if (projectInfo == null) {
                        Console.WriteVerbose($"Could not load project {rootPath}");
                        return null;
                    }

                    // Check TargetFramework first (single framework)
                    if (!string.IsNullOrEmpty(projectInfo.TargetFramework)) {
                        // Skip if it contains variables (variables that weren't resolved would still contain $())
                        if (projectInfo.TargetFramework.Contains("$(") && projectInfo.TargetFramework.Contains(")")) {
                            Console.WriteVerbose($"Skipping {Path.GetFileName(rootPath)} - TargetFramework contains unresolved variable: {projectInfo.TargetFramework}");
                            return null;
                        }
                        return projectInfo.TargetFramework.Trim();
                    } else if (projectInfo.TargetFrameworks.Count > 0) {
                        // If TargetFrameworks exists (multiple), we can't auto-detect
                        var tfmsValue = string.Join(";", projectInfo.TargetFrameworks);
                        Console.WriteVerbose($"Project {Path.GetFileName(rootPath)} has multiple TargetFrameworks: {tfmsValue}");
                        return null; // Require explicit --from when TargetFrameworks is used
                    }
                }
                catch (Exception ex) {
                    Console.WriteVerbose($"Could not read {rootPath}: {ex.Message}");
                    return null;
                }
            } else {
                // Use the existing solution-based logic
                var slnScanner = new SlnScanner(tempOptions, errorSink);
                var slnParser = new SlnParser(Console, errorSink);

                await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                    await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                        try {
                            // Create a ProjCfg without specific Configuration to load project properties
                            var projForLoading = new ProjCfg(projCfg.Proj, null, projCfg.Platform);
                            var projectInfo = projParser.LoadProject(projForLoading, Array.Empty<string>());
                            
                            if (projectInfo == null) {
                                Console.WriteVerbose($"Could not load project {projCfg.Path}");
                                continue;
                            }

                            // Check TargetFramework first (single framework)
                            if (!string.IsNullOrEmpty(projectInfo.TargetFramework)) {
                                // Skip if it contains variables (variables are already evaluated by MSBuild)
                                // But check if the result looks like a variable that wasn't resolved
                                if (projectInfo.TargetFramework.Contains("$(") && projectInfo.TargetFramework.Contains(")")) {
                                    Console.WriteVerbose($"Skipping {Path.GetFileName(projCfg.Path)} - TargetFramework contains unresolved variable: {projectInfo.TargetFramework}");
                                    continue;
                                }
                                targetFrameworks.Add(projectInfo.TargetFramework.Trim());
                            } else if (projectInfo.TargetFrameworks.Count > 0) {
                                // If TargetFrameworks exists (multiple), we can't auto-detect
                                var tfmsValue = string.Join(";", projectInfo.TargetFrameworks);
                                Console.WriteVerbose($"Project {Path.GetFileName(projCfg.Path)} has multiple TargetFrameworks: {tfmsValue}");
                                return null; // Require explicit --from when TargetFrameworks is used
                            }
                        }
                        catch (Exception ex) {
                            Console.WriteVerbose($"Could not read {projCfg.Path}: {ex.Message}");
                        }
                    }
                }

                if (targetFrameworks.Count == 0) {
                    return null;
                }

                // Check if all projects use the same target framework
                var distinctFrameworks = targetFrameworks.Distinct().ToList();
                if (distinctFrameworks.Count == 1) {
                    return distinctFrameworks[0];
                }

                Console.WriteVerbose($"Found multiple target frameworks: {string.Join(", ", distinctFrameworks)}");
                return null; // Multiple different frameworks found
            }

            return null;
        }
        catch (Exception ex) {
            Console.WriteVerbose($"Error detecting source framework: {ex.Message}");
            return null;
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