using bld.Infrastructure;
using bld.Models;
using bld.Services;
using NuGet.Versioning;
using System.CommandLine;

namespace bld.Commands;

internal sealed class TfmCommand : BaseCommand {

    private readonly Option<string> _fromOption = new Option<string>("--from") {
        Description = "Source target framework(s) (e.g., net8.0 or net8.0,net9.0 for multiple). If not specified, will be auto-detected from project files."
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

        var from = parseResult.GetValue(_fromOption);
        var to = parseResult.GetValue(_toOption);
        var apply = parseResult.GetValue(_applyOption);

        // Auto-detect highest SDK version if --to is not specified
        if (string.IsNullOrEmpty(to)) {
            to = await DetectHighestSdkVersionAsync();
            if (string.IsNullOrEmpty(to)) {
                Output.WriteError("Could not auto-detect highest SDK version. Please specify --to parameter.");
                return 1;
            }
            Output.WriteInfo($"Auto-detected target framework: {to}");
        }

        // Auto-detect --from if not specified
        List<string> fromTfms;
        if (string.IsNullOrEmpty(from)) {
            from = await DetectSourceFrameworksAsync(rootPath);
            if (string.IsNullOrEmpty(from)) {
                Output.WriteError("Could not auto-detect source framework. Projects have multiple TargetFrameworks or no consistent TargetFramework. Please specify --from parameter.");
                return 1;
            }
            fromTfms = from.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Output.WriteInfo($"Auto-detected source framework(s): {from}");
        }
        else {
            fromTfms = from.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        Output.WriteInfo($"Migrating projects from {string.Join(", ", fromTfms)} to {to} in: {rootPath}");
        Output.WriteInfo($"Mode: {(apply ? "Apply changes" : "Dry run")}");

        try {
            using var tfmService = new TfmService(Output, options);
            return await tfmService.MigrateTargetFrameworkAsync(rootPath, fromTfms, to, apply, cancellationToken);
        }
        catch (Exception ex) {
            Output.WriteError($"Error migrating target frameworks: {ex.FormatMessage()}");
            return 1;
        }
    }

    private async Task<string?> DetectSourceFrameworksAsync(string rootPath) {
        try {
            // Initialize MSBuild first for SlnScanner/SlnParser
            var tempOptions = new CleaningOptions();
            MSBuildInitializer.Initialize(Output, tempOptions);

            var errorSink = new ErrorSink(Output);
            var projParser = new ProjParser(Output, errorSink, tempOptions);

            var targetFrameworks = new List<string>();
            var cache = new ProjCfgCache(Output);

            // Check if the root path is a direct .csproj file
            if (File.Exists(rootPath) && Path.GetExtension(rootPath).Equals(".csproj", StringComparison.OrdinalIgnoreCase)) {
                try {
                    var proj = new Proj(rootPath, null);
                    var projCfg = new ProjCfg(proj, null, null); // No specific configuration
                    var projectInfo = projParser.LoadProject(projCfg, Array.Empty<string>());

                    if (projectInfo == null) {
                        Output.WriteVerbose($"Could not load project {rootPath}");
                        return null;
                    }

                    // Check TargetFramework first (single framework)
                    if (!string.IsNullOrEmpty(projectInfo.TargetFramework)) {
                        // Skip if it contains variables (variables that weren't resolved would still contain $())
                        if (projectInfo.TargetFramework.Contains("$(") && projectInfo.TargetFramework.Contains(")")) {
                            Output.WriteVerbose($"Skipping {Path.GetFileName(rootPath)} - TargetFramework contains unresolved variable: {projectInfo.TargetFramework}");
                            return null;
                        }
                        // Filter to only .NET (Core) frameworks
                        if (IsDotNetCoreFramework(projectInfo.TargetFramework.Trim())) {
                            return projectInfo.TargetFramework.Trim();
                        }
                        return null;
                    }
                    else if (projectInfo.TargetFrameworks.Count > 0) {
                        // Collect all .NET (Core) frameworks from TargetFrameworks
                        var tfmsValue = string.Join(";", projectInfo.TargetFrameworks);
                        Output.WriteVerbose($"Project {Path.GetFileName(rootPath)} has multiple TargetFrameworks: {tfmsValue}");
                        var dotnetCoreFrameworks = projectInfo.TargetFrameworks
                            .Select(f => f.Trim())
                            .Where(IsDotNetCoreFramework)
                            .Distinct()
                            .ToList();
                        if (dotnetCoreFrameworks.Count > 0) {
                            return string.Join(",", dotnetCoreFrameworks);
                        }
                        return null;
                    }
                }
                catch (Exception ex) {
                    Output.WriteVerbose($"Could not read {rootPath}: {ex.FormatMessage()}");
                    return null;
                }
            }
            else {
                // Use the existing solution-based logic
                var slnScanner = new SlnScanner(tempOptions, errorSink);
                var slnParser = new SlnParser(Output, errorSink);

                await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
                    await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                        try {
                            if (!cache.Add(projCfg)) {
                                continue;
                            }

                            // Create a ProjCfg without specific Configuration to load project properties
                            var projForLoading = new ProjCfg(projCfg.Proj, null, projCfg.Platform);
                            var projectInfo = projParser.LoadProject(projForLoading, Array.Empty<string>());

                            if (projectInfo == null) {
                                Output.WriteVerbose($"Could not load project {projCfg.Path}");
                                continue;
                            }

                            // Check TargetFramework first (single framework)
                            if (!string.IsNullOrEmpty(projectInfo.TargetFramework)) {
                                // Skip if it contains variables (variables are already evaluated by MSBuild)
                                // But check if the result looks like a variable that wasn't resolved
                                if (projectInfo.TargetFramework.Contains("$(") && projectInfo.TargetFramework.Contains(")")) {
                                    Output.WriteVerbose($"Skipping {Path.GetFileName(projCfg.Path)} - TargetFramework contains unresolved variable: {projectInfo.TargetFramework}");
                                    continue;
                                }
                                targetFrameworks.Add(projectInfo.TargetFramework.Trim());
                            }
                            else if (projectInfo.TargetFrameworks.Count > 0) {
                                // Collect all frameworks from TargetFrameworks for auto-detection
                                var tfmsValue = string.Join(";", projectInfo.TargetFrameworks);
                                Output.WriteVerbose($"Project {Path.GetFileName(projCfg.Path)} has multiple TargetFrameworks: {tfmsValue}");
                                targetFrameworks.AddRange(projectInfo.TargetFrameworks.Select(f => f.Trim()));
                            }
                        }
                        catch (Exception ex) {
                            Output.WriteVerbose($"Could not read {projCfg.Path}: {ex.FormatMessage()}");
                        }
                    }
                }

                if (targetFrameworks.Count == 0) {
                    return null;
                }

                // Filter to only include .NET (Core) frameworks (net5.0, net6.0, etc.) - exclude netstandard, netcoreapp, and .NET Framework
                var dotnetCoreFrameworks = targetFrameworks.Where(IsDotNetCoreFramework).Distinct().ToList();
                
                if (dotnetCoreFrameworks.Count == 0) {
                    Output.WriteVerbose($"No .NET (Core) frameworks found. Found: {string.Join(", ", targetFrameworks.Distinct())}");
                    return null;
                }

                // Return all .NET (Core) frameworks found (can be multiple)
                var distinctAllFrameworks = targetFrameworks.Distinct().ToList();
                if (distinctAllFrameworks.Count > dotnetCoreFrameworks.Count) {
                    Output.WriteVerbose($"Auto-detected source framework(s): {string.Join(", ", dotnetCoreFrameworks)} (ignoring non-.NET frameworks like netstandard)");
                }
                
                // Return comma-separated list of frameworks
                return string.Join(",", dotnetCoreFrameworks);
            }

            return null;
        }
        catch (Exception ex) {
            Output.WriteVerbose($"Error detecting source framework: {ex.FormatMessage()}");
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
                Output.WriteVerbose("Failed to list installed SDKs");
                return null;
            }

            // todo possibly better to use SemVer package
            // Parse output to find highest version
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var versions = new List<NuGetVersion>();

            foreach (var line in lines) {
                // Example line: "8.0.100 [C:\Program Files\dotnet\sdk]"
                var parts = line.Split(' ');
                if (parts.Length > 0 && NuGetVersion.TryParse(parts[0], out var version)) {
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
            Output.WriteVerbose($"Error detecting SDK versions: {ex.FormatMessage()}");
            return null;
        }
    }

    /// <summary>
    /// Checks if a TFM is a .NET (Core) framework (net5.0, net6.0, net7.0, etc.)
    /// Excludes netstandard, netcoreapp, and .NET Framework versions like net48, net472.
    /// </summary>
    private static bool IsDotNetCoreFramework(string tfm) {
        if (string.IsNullOrEmpty(tfm)) {
            return false;
        }

        tfm = tfm.ToLowerInvariant().Trim();

        // Must start with "net" and have more characters
        if (!tfm.StartsWith("net") || tfm.Length <= 3) {
            return false;
        }

        // Exclude netstandard and netcoreapp
        if (tfm.StartsWith("netstandard") || tfm.StartsWith("netcoreapp")) {
            return false;
        }

        var versionPart = tfm.Substring(3);

        // Match net\d(\.\d)? pattern (e.g., net5.0, net6.0, net7.0, net8.0, net9.0)
        // Also handle single-digit versions like net5, net6
        // Version should be in format X.Y or X where X >= 5
        if (Version.TryParse(versionPart, out var version)) {
            // .NET (Core) 5.0 and above
            return version.Major >= 5;
        }
        
        // Handle single-digit versions (e.g., "net5", "net6")
        // BUT: "net48", "net472" are .NET Framework versions (2-3 digits), not .NET (Core)
        // .NET (Core) single-digit would be just "net5", "net6", etc. (exactly 1 digit)
        if (versionPart.Length == 1 && int.TryParse(versionPart, out var majorVersion)) {
            return majorVersion >= 5;
        }

        return false;
    }
}