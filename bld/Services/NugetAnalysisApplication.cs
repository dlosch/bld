using bld.Infrastructure;
using bld.Models;
using Spectre.Console;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace bld.Services;

/// <summary>
/// Application for analyzing NuGet package references
/// </summary>
internal class NugetAnalysisApplication {
    private readonly IConsoleOutput _console;
    private readonly List<Err> _errors = new();
    private bool _isInitialized = false;

    public NugetAnalysisApplication(IConsoleOutput console) {
        _console = console;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public Task InitAsync(CleaningOptions options) {
        // Register MSBuild defaults before any MSBuild types are loaded
        MSBuildService.RegisterMSBuildDefaults(_console, options);
        _isInitialized = true;
        return Task.CompletedTask;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task RunAsync(string[] rootPaths, CleaningOptions options, string? whitelistBlacklistFile) {
        if (!_isInitialized) {
            throw new InvalidOperationException("Application not initialized. Call InitAsync first.");
        }

        using var msbuildService = new MSBuildService(_console);
        var errorSink = new ErrorSink(_console);
        var scanner = new SlnScanner(options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);
        var fileSystem = new FileSystem(_console, errorSink);
        
        // Parse whitelist/blacklist file if provided
        WhitelistBlacklistRules? whitelistBlacklistRules = null;
        if (!string.IsNullOrWhiteSpace(whitelistBlacklistFile)) {
            try {
                whitelistBlacklistRules = WhitelistBlacklistParser.ParseFile(whitelistBlacklistFile);
                _console.WriteInfo($"Loaded whitelist/blacklist rules from: {whitelistBlacklistFile}");
                _console.WriteDebug($"Whitelist patterns: {whitelistBlacklistRules.WhitelistPatterns.Count}");
                _console.WriteDebug($"Blacklist patterns: {whitelistBlacklistRules.BlacklistPatterns.Count}");
            }
            catch (Exception ex) {
                _console.WriteError($"Failed to parse whitelist/blacklist file: {ex.Message}");
                return;
            }
        }
        
        var categorizer = new NugetPackageCategorizer(whitelistBlacklistRules);
        var packageExtractor = new NugetPackageExtractor(_console, errorSink, categorizer);

        _console.WriteRule("[bold blue]NuGet Package Analysis[/]");

        var stopwatch = Stopwatch.StartNew();

        try {
            var allProjectAnalyses = new List<ProjectNugetAnalysis>();

            foreach (var rootPath in rootPaths) {
                await foreach (var sln in scanner.Enumerate(rootPath)) {
                    _console.WriteDebug($"Processing solution: {sln}");

                    await foreach (var projCfg in slnParser.ParseSolution(sln, fileSystem)) {
                        try {
                            var globalProperties = GetGlobalProperties(options);
                            var analysis = packageExtractor.AnalyzeProject(projCfg, globalProperties);
                            
                            if (analysis.Packages.Any()) {
                                allProjectAnalyses.Add(analysis);
                            }
                        }
                        catch (Exception ex) {
                            _console.WriteError($"Failed to analyze project {projCfg.Path}: {ex.Message}");
                        }
                    }
                }
            }

        // Display results with unique projects only (deduplicate by path)
        var uniqueAnalyses = allProjectAnalyses
            .GroupBy(a => a.ProjectPath)
            .Select(g => g.First())
            .ToList();
            
        await DisplayResults(uniqueAnalyses, categorizer);

        }
        finally {
            stopwatch.Stop();
            _console.WriteInfo($"Analysis completed in {stopwatch.Elapsed:mm\\:ss\\.fff}");

            if (_errors.Count > 0) {
                _console.WriteError($"Analysis completed with {_errors.Count} error(s).");
            }
        }
    }

    private static Dictionary<string, string> GetGlobalProperties(CleaningOptions options) {
        var dict = new Dictionary<string, string>();
        if (options.VSToolsPath is { }) {
            dict["VSToolsPath"] = options.VSToolsPath;
        }
        if (options.VSRootPath is { } && Directory.Exists(Path.Combine(options.VSRootPath, "MSBuild"))) {
            dict["MSBuildExtensionsPath"] = Path.Combine(options.VSRootPath, "MSBuild");
        }
        return dict;
    }

    private async Task DisplayResults(List<ProjectNugetAnalysis> analyses, NugetPackageCategorizer categorizer) {
        if (!analyses.Any()) {
            _console.WriteWarning("No projects with NuGet package references found.");
            return;
        }

        _console.WriteInfo($"Found {analyses.Count} project(s) with NuGet packages:");

        foreach (var analysis in analyses.OrderBy(a => a.ProjectName)) {
            await DisplayProjectAnalysis(analysis, categorizer);
        }

        // Summary
        var totalPackages = analyses.SelectMany(a => a.Packages).Count();
        var uniquePackages = analyses.SelectMany(a => a.Packages).Select(p => p.Name).Distinct().Count();
        
        _console.WriteRule("[bold green]Summary[/]");
        _console.WriteInfo($"Total packages across all projects: {totalPackages}");
        _console.WriteInfo($"Unique packages: {uniquePackages}");
    }

    private async Task DisplayProjectAnalysis(ProjectNugetAnalysis analysis, NugetPackageCategorizer categorizer) {
        var content = new List<string>();
        content.Add($"[dim]Path: {analysis.ProjectPath}[/]");
        content.Add($"[dim]Total packages: {analysis.Packages.Count}[/]");
        content.Add("");

        // Display packages by category
        await AddCategorySection(content, "Microsoft Official .NET Packages", analysis.MicrosoftOfficialPackages);
        await AddCategorySection(content, "Microsoft Non-Official Packages", analysis.MicrosoftNonOfficialPackages);
        await AddCategorySection(content, "Known Trusted Packages", analysis.TrustedThirdPartyPackages);
        await AddCategorySection(content, "Other Packages", analysis.OtherPackages);

        var panel = new Panel(string.Join("\n", content))
            .Header($"[bold blue]{analysis.ProjectName}[/]")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);
    }

    private async Task AddCategorySection(List<string> content, string categoryName, IEnumerable<NugetPackageInfo> packages) {
        var packageList = packages.ToList();
        if (!packageList.Any()) {
            return;
        }

        content.Add($"[bold yellow]{categoryName}:[/]");
        
        foreach (var package in packageList.OrderBy(p => p.Name)) {
            var packageInfo = $"• {package.Name} ({package.Version})";
            
            // Add coloring and pattern information based on whitelist/blacklist
            if (!string.IsNullOrWhiteSpace(package.BlacklistMatch)) {
                packageInfo = $"[red]{packageInfo} ({package.BlacklistMatch})[/]";
            }
            else if (!string.IsNullOrWhiteSpace(package.WhitelistMatch)) {
                packageInfo = $"[green]{packageInfo} ({package.WhitelistMatch})[/]";
            }
            
            content.Add(packageInfo);
        }
        content.Add("");
    }
}