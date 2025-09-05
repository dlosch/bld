using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.IO;

namespace bld.Commands;

internal sealed class DepsCommand : BaseCommand {

    private readonly Option<bool> _nugetOption = new Option<bool>("--nuget") {
        Description = "Include NuGet package references in the dependency tree.",
        DefaultValueFactory = _ => true
    };

    private readonly Option<string?> _projectOption = new Option<string?>("--project", "-p") {
        Description = "Specific project file to analyze. If not provided, analyzes all projects in the solution.",
        Validators = {
            v => {
                var path = v.GetValueOrDefault<string?>();
                if (path is not null && !File.Exists(path)) {
                    v.AddError($"Project file {path} does not exist.");
                }
            }
        }
    };

    public DepsCommand(IConsoleOutput console) : base("deps", "Show dependency tree for projects in a solution.", console) {
        Add(_rootOption);
        Add(_depthOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_nugetOption);
        Add(_projectOption);
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

        var specificProject = parseResult.GetValue(_projectOption);
        var includeNuget = parseResult.GetValue(_nugetOption);

        // Initialize MSBuild
        MSBuildService.RegisterMSBuildDefaults(base.Console, options);

        var errorSink = new ErrorSink(base.Console);
        var dependencyAnalyzer = new DependencyAnalyzer(base.Console, errorSink, options);

        try {
            if (!string.IsNullOrEmpty(specificProject)) {
                // Analyze specific project
                await AnalyzeSpecificProject(specificProject, dependencyAnalyzer, includeNuget);
            }
            else {
                // Analyze solution
                await AnalyzeSolution(rootPath, options, errorSink, dependencyAnalyzer, includeNuget);
            }
        }
        catch (Exception ex) {
            base.Console.WriteError($"Error analyzing dependencies: {ex.Message}");
            return 1;
        }

        return 0;
    }

    private Task AnalyzeSpecificProject(string projectPath, DependencyAnalyzer analyzer, bool includeNuget) {
        base.Console.WriteInfo($"Analyzing dependencies for project: {Path.GetFileName(projectPath)}");
        base.Console.WriteInfo("");

        var dependencyTree = analyzer.BuildDependencyTree(projectPath, includeNuget);
        if (dependencyTree != null) {
            PrintDependencyTree(dependencyTree, 0, includeNuget);
        }
        else {
            base.Console.WriteWarning($"Could not analyze project: {projectPath}");
        }
        return Task.CompletedTask;
    }

    private async Task AnalyzeSolution(string rootPath, CleaningOptions options, ErrorSink errorSink, DependencyAnalyzer analyzer, bool includeNuget) {
        var scanner = new SlnScanner(options, errorSink);
        var slnParser = new SlnParser(base.Console, errorSink);

        bool foundSolutions = false;
        await foreach (var slnPath in scanner.Enumerate(rootPath)) {
            foundSolutions = true;
            var sln = new Sln(slnPath);
            base.Console.WriteInfo($"Analyzing dependencies for solution: {Path.GetFileName(sln.Path)}");
            base.Console.WriteInfo("");

            var projects = new List<ProjCfg>();
            await foreach (var proj in slnParser.ParseSolution(sln.Path)) {
                // Take only Debug configuration for dependency analysis
                if (string.Equals(proj.Configuration, "Debug", StringComparison.OrdinalIgnoreCase)) {
                    projects.Add(proj);
                }
            }

            var dependencyTrees = analyzer.BuildSolutionDependencyTree(projects, includeNuget);
            
            if (dependencyTrees.Any()) {
                foreach (var tree in dependencyTrees) {
                    PrintDependencyTree(tree, 0, includeNuget);
                    base.Console.WriteInfo("");
                }
            }
            else {
                base.Console.WriteWarning($"No dependencies found in solution: {sln.Path}");
            }
        }

        if (!foundSolutions) {
            base.Console.WriteWarning($"No solution files found in: {rootPath}");
        }
    }

    private void PrintDependencyTree(DependencyNode node, int depth, bool includeNuget, HashSet<string>? visited = null) {
        visited ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Prevent infinite recursion in case of circular references
        if (visited.Contains(node.DependencyInfo.ProjectPath)) {
            var circularIndent = new string(' ', depth * 2);
            base.Console.WriteInfo($"{circularIndent}├── {node.DependencyInfo.ProjectName} [CIRCULAR REFERENCE]");
            return;
        }

        visited.Add(node.DependencyInfo.ProjectPath);

        var indent = new string(' ', depth * 2);
        var projectName = node.DependencyInfo.ProjectName;
        var targetFramework = !string.IsNullOrEmpty(node.DependencyInfo.TargetFramework) 
            ? $" ({node.DependencyInfo.TargetFramework})" 
            : "";

        if (depth == 0) {
            base.Console.WriteInfo($"📦 {projectName}{targetFramework}");
        }
        else {
            base.Console.WriteInfo($"{indent}├── {projectName}{targetFramework}");
        }

        // Print project dependencies
        foreach (var projectDep in node.ProjectDependencies) {
            PrintDependencyTree(projectDep, depth + 1, includeNuget, visited);
        }

        // Print package dependencies
        if (includeNuget && node.PackageDependencies.Any()) {
            foreach (var packageDep in node.PackageDependencies.OrderBy(p => p.PackageId)) {
                var packageIndent = new string(' ', (depth + 1) * 2);
                var privateAssets = packageDep.IsPrivateAssets ? " [Private]" : "";
                base.Console.WriteInfo($"{packageIndent}├── 📎 {packageDep.PackageId} v{packageDep.Version}{privateAssets}");
            }
        }

        visited.Remove(node.DependencyInfo.ProjectPath);
    }
}