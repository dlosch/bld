using bld.Infrastructure;
using bld.Models;
using bld.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace bld.Mcp;

/// <summary>
/// MCP tools for the bld CLI - exposes bld commands to agentic workflows.
/// </summary>
[McpServerToolType]
public class BldMcpTools {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _baseOptions;

    public BldMcpTools() {
        _baseOptions = new CleaningOptions { JsonOutput = true, LogLevel = LogLevel.Warning };
        _console = new SpectreConsoleOutput(_baseOptions.LogLevel, _baseOptions.JsonOutput);
    }

    [McpServerTool(Name = "bld_tfm_analyze", ReadOnly = true)]
    [Description("Analyzes .NET projects and detects their current Target Framework(s). Returns a list of projects with their TFMs.")]
    public async Task<TfmService.TfmMigrationResult> AnalyzeTargetFrameworks(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);
        MSBuildInitializer.Initialize(_console, options);

        var tfmService = new TfmService(_console, options);
        // Pass empty fromTfms to just analyze without migration
        return await tfmService.MigrateTargetFrameworkAsync(root, [], "", applyChanges: false, cancellationToken);
    }

    [McpServerTool(Name = "bld_tfm_migrate", Destructive = true)]
    [Description("Migrates .NET projects from one Target Framework to another. Use --apply to actually modify files.")]
    public async Task<TfmService.TfmMigrationResult> MigrateTargetFramework(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Source TFM(s) to migrate from, comma-separated (e.g., 'net8.0' or 'net7.0,net8.0')")] string from,
        [Description("Target TFM to migrate to (e.g., 'net9.0')")] string to,
        [Description("Apply changes (true) or dry-run (false)")] bool apply = false,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);
        MSBuildInitializer.Initialize(_console, options);

        var fromTfms = from.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var tfmService = new TfmService(_console, options);
        return await tfmService.MigrateTargetFrameworkAsync(root, fromTfms, to, applyChanges: apply, cancellationToken);
    }

    [McpServerTool(Name = "bld_nuget_analyze", ReadOnly = true)]
    [Description("Analyzes NuGet package references across all projects in a solution. Categorizes packages and shows which projects use each package.")]
    public async Task<NugetAnalysisApplication.NugetAnalysisResult> AnalyzeNuGetPackages(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Show aggregated view across all projects")] bool aggregate = true,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);
        MSBuildInitializer.Initialize(_console, options);

        var app = new NugetAnalysisApplication(_console);
        await app.InitAsync(options);
        return await app.RunAsync([root], options, whitelistBlacklistFile: null, aggregate: aggregate, showProjects: true);
    }

    [McpServerTool(Name = "bld_outdated_check", ReadOnly = true)]
    [Description("Checks for outdated NuGet packages and lists available updates with compatibility information.")]
    public async Task<OutdatedService.OutdatedAnalysisResult> CheckOutdatedPackages(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Include prerelease versions")] bool prerelease = false,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);

        var outdatedService = new OutdatedService(_console, options);
        return await outdatedService.CheckOutdatedPackagesAsync(root, updatePackages: false, skipTfmCheck: false, includePrerelease: prerelease, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "bld_outdated_update", Destructive = true)]
    [Description("Updates outdated NuGet packages to their latest versions.")]
    public async Task<OutdatedService.OutdatedAnalysisResult> UpdateOutdatedPackages(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Include prerelease versions")] bool prerelease = false,
        [Description("Skip target framework compatibility checking")] bool skipTfmCheck = false,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);

        var outdatedService = new OutdatedService(_console, options);
        return await outdatedService.CheckOutdatedPackagesAsync(root, updatePackages: true, skipTfmCheck: skipTfmCheck, includePrerelease: prerelease, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "bld_cpm_analyze", ReadOnly = true)]
    [Description("Analyzes a solution for Central Package Management (CPM) conversion. Shows what changes would be made without applying them.")]
    public async Task<CpmService.CpmConversionResult> AnalyzeCpmConversion(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);

        var cpmService = new CpmService(_console, options);
        return await cpmService.ConvertToCentralPackageManagementAsync(root, applyChanges: false, overwrite: false, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "bld_cpm_convert", Destructive = true)]
    [Description("Converts a solution to Central Package Management (CPM) by creating Directory.Packages.props and updating project files.")]
    public async Task<CpmService.CpmConversionResult> ConvertToCpm(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Overwrite existing Directory.Packages.props if it exists")] bool overwrite = false,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with { };
        ResolveVSToolsPath(options);

        var cpmService = new CpmService(_console, options);
        return await cpmService.ConvertToCentralPackageManagementAsync(root, applyChanges: true, overwrite: overwrite, cancellationToken: cancellationToken);
    }

    [McpServerTool(Name = "bld_clean_analyze", ReadOnly = true)]
    [Description("Analyzes build output directories (bin/obj) and calculates statistics about what would be cleaned.")]
    public async Task<MarkDeleteResult> AnalyzeCleanTargets(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Also analyze intermediate output (obj folder)")] bool includeObj = false,
        [Description("Only analyze directories for non-current target frameworks")] bool nonCurrentOnly = false,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with {
            CleanObjDirectory = includeObj,
            CleanOnlyNonCurrentTfms = nonCurrentOnly,
            Delete = false
        };
        ResolveVSToolsPath(options);

        var app = new CleaningApplication(_console, (a, b, c) => new MarkDeleteResultStatsProcessor(a, b, c));
        await app.InitAsync(options);
        return await app.RunAsync([root], options);
    }

    [McpServerTool(Name = "bld_clean_execute", Destructive = true)]
    [Description("Deletes build output directories (bin/obj) for the specified solution or project.")]
    public async Task<MarkDeleteResult> ExecuteClean(
        [Description("Path to solution file (.sln/.slnx) or project directory")] string root,
        [Description("Also clean intermediate output (obj folder)")] bool includeObj = false,
        [Description("Only clean directories for non-current target frameworks")] bool nonCurrentOnly = false,
        CancellationToken cancellationToken = default) {
        var options = _baseOptions with {
            CleanObjDirectory = includeObj,
            CleanOnlyNonCurrentTfms = nonCurrentOnly,
            Delete = true
        };
        ResolveVSToolsPath(options);

        var app = new CleaningApplication(_console, (a, b, c) => new MarkDeleteResultDeleteProcessor(a, b, c));
        await app.InitAsync(options);
        return await app.RunAsync([root], options);
    }

    private static void ResolveVSToolsPath(CleaningOptions options) {
        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath)) {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }
    }

    private static string? TryResolveVSToolsPath(out string? vsRoot) {
        vsRoot = default;
        var ver = Environment.GetEnvironmentVariable("VisualStudioVersion");
        vsRoot = Environment.GetEnvironmentVariable("VSINSTALLDIR");
        if (!string.IsNullOrWhiteSpace(vsRoot) && Directory.Exists(vsRoot)) {
            var toolsPath = Path.Combine(vsRoot, $"MSBuild\\Microsoft\\VisualStudio\\v{ver ?? "17.0"}");
            if (Directory.Exists(toolsPath)) {
                return toolsPath;
            }
        }

        var paths = MSBuildHelper.GetVS15Locations();
        if (paths is not null && paths.Any()) {
            foreach (var p in paths) {
                vsRoot = p;
                var toolsPath = Path.Combine(p, "MSBuild", "Microsoft", "VisualStudio", $"v{ver ?? "17.0"}");
                if (Directory.Exists(toolsPath)) {
                    return toolsPath;
                }
            }
        }

        return default;
    }
}
