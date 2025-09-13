using bld.Models;
using bld.Services;
using Microsoft.Build.Construction;

namespace bld.Infrastructure;

/// <summary>
/// Enhanced enumerator that can enumerate solution files and project files
/// based on the specified enumeration type, with parallel processing support
/// </summary>
internal class Enumerator(CleaningOptions Options, ErrorSink ErrorSink) {
    
    /// <summary>
    /// Enumerates files based on the specified enumeration type
    /// </summary>
    /// <param name="path">Base directory path to scan</param>
    /// <param name="enumerationType">Type of files to enumerate (Sln or Project)</param>
    /// <returns>Async enumerable of project file paths</returns>
    public async IAsyncEnumerable<string> EnumerateProjectPaths(string path, EnumerationType enumerationType) {
        if (string.IsNullOrWhiteSpace(path)) {
            yield break;
        }

        // Handle single file case
        if (File.Exists(path)) {
            if (enumerationType == EnumerationType.Sln && IsSolutionFile(path)) {
                await foreach (var projectPath in EnumerateProjectsFromSolution(path)) {
                    yield return projectPath;
                }
            }
            else if (enumerationType == EnumerationType.Project && IsProjectFile(path)) {
                yield return path;
            }
            yield break;
        }

        // Handle directory case
        var pathRooted = DirExt.EnsureRooted(path, Environment.CurrentDirectory);
        if (!Directory.Exists(pathRooted)) {
            ErrorSink.AddError($"Input path {path} (translated to {pathRooted}) not found.");
            yield break;
        }

        if (enumerationType == EnumerationType.Sln) {
            await foreach (var projectPath in EnumerateProjectsFromSolutions(pathRooted)) {
                yield return projectPath;
            }
        }
        else {
            await foreach (var projectPath in EnumerateProjectFiles(pathRooted)) {
                yield return projectPath;
            }
        }
    }

    /// <summary>
    /// Enumerates solution files in the specified directory and extracts all project paths
    /// </summary>
    private async IAsyncEnumerable<string> EnumerateProjectsFromSolutions(string directoryPath) {
        var solutionFiles = await GetSolutionFilesAsync(directoryPath);
        
        // Process solutions in parallel for better performance
        var tasks = solutionFiles.Select(async slnPath => {
            var projectPaths = new List<string>();
            await foreach (var projectPath in EnumerateProjectsFromSolution(slnPath)) {
                projectPaths.Add(projectPath);
            }
            return projectPaths;
        });
        
        var results = await Task.WhenAll(tasks);
        
        foreach (var projectPaths in results) {
            foreach (var projectPath in projectPaths) {
                yield return projectPath;
            }
        }
    }

    /// <summary>
    /// Gets all solution files from the directory
    /// </summary>
    private async Task<List<string>> GetSolutionFilesAsync(string directoryPath) {
        return await Task.Run(() => {
            var fileSearcher = Directory.EnumerateFiles(directoryPath, "*.sln?", new EnumerationOptions {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Win32,
                MaxRecursionDepth = Options.Depth,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            });

            return fileSearcher.Where(IsSolutionFile).ToList();
        });
    }

    /// <summary>
    /// Enumerates all project paths from a single solution file
    /// </summary>
    private async IAsyncEnumerable<string> EnumerateProjectsFromSolution(string slnPath) {
        SolutionFile? solution = null;
        var sln = new Sln(slnPath);
        
        try {
            solution = await Task.Run(() => SolutionFile.Parse(slnPath));
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to parse solution file.", exception: xcptn, sln: sln);
            yield break;
        }

        foreach (var project in solution.ProjectsInOrder
            .Where(p => File.Exists(p.AbsolutePath) && 
                       p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat &&
                       IsProjectFile(p.AbsolutePath))) {
            yield return project.AbsolutePath;
        }
    }

    /// <summary>
    /// Enumerates project files directly from the directory
    /// </summary>
    private async IAsyncEnumerable<string> EnumerateProjectFiles(string directoryPath) {
        var projectFiles = await GetProjectFilesAsync(directoryPath);
        
        foreach (var projectFile in projectFiles) {
            yield return projectFile;
        }
    }

    /// <summary>
    /// Gets all project files from the directory
    /// </summary>
    private async Task<List<string>> GetProjectFilesAsync(string directoryPath) {
        return await Task.Run(() => {
            var patterns = new[] { "*.csproj", "*.vbproj", "*.sqlproj", "*.fsproj", "*.vcxproj" };
            var projectFiles = new List<string>();

            foreach (var pattern in patterns) {
                var fileSearcher = Directory.EnumerateFiles(directoryPath, pattern, new EnumerationOptions {
                    IgnoreInaccessible = true,
                    MatchCasing = MatchCasing.CaseInsensitive,
                    MatchType = MatchType.Win32,
                    MaxRecursionDepth = Options.Depth,
                    RecurseSubdirectories = true,
                    ReturnSpecialDirectories = false
                });

                projectFiles.AddRange(fileSearcher);
            }

            return projectFiles.Distinct().ToList();
        });
    }

    /// <summary>
    /// Checks if a file is a supported solution file format
    /// </summary>
    private static bool IsSolutionFile(string filePath) {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension == ".sln" || extension == ".slnf" || extension == ".slnx";
    }

    /// <summary>
    /// Checks if a file is a supported MSBuild project file format
    /// </summary>
    private static bool IsProjectFile(string filePath) {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return extension switch {
            ".csproj" => true,
            ".vbproj" => true,
            ".sqlproj" => true,
            ".fsproj" => true,
            ".vcxproj" => true,
            _ => false
        };
    }

    /// <summary>
    /// Enumerates project configurations based on the specified enumeration type
    /// This method performs the same logic as SlnParser.ParseSolution, extracting
    /// project configurations from solution files or creating default configurations for projects
    /// </summary>
    /// <param name="path">Base directory path to scan</param>
    /// <param name="enumerationType">Type of files to enumerate (Sln or Project)</param>
    /// <param name="createDefaultDebugConfiguration">Whether to create a default Debug configuration when no configurations are found</param>
    /// <returns>Async enumerable of project configurations</returns>
    public async IAsyncEnumerable<ProjCfg> EnumerateProjCfg(string path, EnumerationType enumerationType, bool createDefaultDebugConfiguration = true) {
        if (string.IsNullOrWhiteSpace(path)) {
            yield break;
        }

        // Handle single file case
        if (File.Exists(path)) {
            if (enumerationType == EnumerationType.Sln && IsSolutionFile(path)) {
                await foreach (var projCfg in EnumerateProjCfgFromSolution(path, createDefaultDebugConfiguration)) {
                    yield return projCfg;
                }
            }
            else if (enumerationType == EnumerationType.Project && IsProjectFile(path)) {
                await foreach (var projCfg in CreateDefaultProjCfgForProject(path, createDefaultDebugConfiguration)) {
                    yield return projCfg;
                }
            }
            yield break;
        }

        // Handle directory case
        var pathRooted = DirExt.EnsureRooted(path, Environment.CurrentDirectory);
        if (!Directory.Exists(pathRooted)) {
            ErrorSink.AddError($"Input path {path} (translated to {pathRooted}) not found.");
            yield break;
        }

        if (enumerationType == EnumerationType.Sln) {
            await foreach (var projCfg in EnumerateProjCfgFromSolutions(pathRooted, createDefaultDebugConfiguration)) {
                yield return projCfg;
            }
        }
        else {
            await foreach (var projCfg in EnumerateProjCfgFromProjects(pathRooted, createDefaultDebugConfiguration)) {
                yield return projCfg;
            }
        }
    }

    /// <summary>
    /// Enumerates solution files in the specified directory and extracts all project configurations
    /// </summary>
    private async IAsyncEnumerable<ProjCfg> EnumerateProjCfgFromSolutions(string directoryPath, bool createDefaultDebugConfiguration) {
        var solutionFiles = await GetSolutionFilesAsync(directoryPath);
        
        // Process solutions sequentially to maintain order (like SlnParser.ParseSolution does)
        foreach (var slnPath in solutionFiles) {
            await foreach (var projCfg in EnumerateProjCfgFromSolution(slnPath, createDefaultDebugConfiguration)) {
                yield return projCfg;
            }
        }
    }

    /// <summary>
    /// Enumerates all project configurations from a single solution file
    /// This method replicates the logic from SlnParser.ParseSolution
    /// </summary>
    private async IAsyncEnumerable<ProjCfg> EnumerateProjCfgFromSolution(string slnPath, bool createDefaultDebugConfiguration) {
        SolutionFile? solution = null;
        var sln = new Sln(slnPath);
        
        try {
            solution = await Task.Run(() => SolutionFile.Parse(slnPath));
        }
        catch (Exception xcptn) {
            ErrorSink.AddError($"Failed to parse solution file.", exception: xcptn, sln: sln);
            yield break;
        }

        // Process projects in order, similar to SlnParser.ParseSolution
        foreach (var project in solution.ProjectsInOrder
            .Where(p => File.Exists(p.AbsolutePath) && 
                       p.ProjectType == SolutionProjectType.KnownToBeMSBuildFormat &&
                       IsProjectFile(p.AbsolutePath))) {
            
            var fullyQualifiedPath = project.AbsolutePath;
            var queryPlatform = false;
            
            // Determine if we need to query platform based on project type (same logic as SlnParser)
            switch (Path.GetExtension(fullyQualifiedPath)!.ToLowerInvariant()) {
                case ".csproj":
                case ".fsproj":
                case ".sqlproj":
                case ".vbproj": // old proj do not have the <tfm>
                    queryPlatform = false;
                    break;
                case ".vcxproj":
                    queryPlatform = true;
                    break;
                default:
                    continue;
            }

            var proj = new Proj(fullyQualifiedPath, sln);
            
            if (project.ProjectConfigurations is { }) {
                // Extract configurations from solution, similar to SlnParser.ParseSolution
                foreach (var cfg in project.ProjectConfigurations
                      .Select(x => (x.Value?.ConfigurationName, queryPlatform ? x.Value?.PlatformName : null))
                            .Where(x => x.ConfigurationName is not null)
                            .Distinct()) {
                    var projCfg = new ProjCfg(proj, cfg.ConfigurationName!, cfg.Item2);
                    yield return projCfg;
                }
            }
            else {
                // Create default configurations when none are found, same as SlnParser
                if (createDefaultDebugConfiguration) {
                    var projCfg = new ProjCfg(proj, "Debug", null);
                    yield return projCfg;
                }
                var projCfgRelease = new ProjCfg(proj, "Release", null);
                yield return projCfgRelease;
            }
        }
    }

    /// <summary>
    /// Enumerates project files directly from the directory and creates default configurations
    /// </summary>
    private async IAsyncEnumerable<ProjCfg> EnumerateProjCfgFromProjects(string directoryPath, bool createDefaultDebugConfiguration) {
        var projectFiles = await GetProjectFilesAsync(directoryPath);
        
        foreach (var projectFile in projectFiles) {
            await foreach (var projCfg in CreateDefaultProjCfgForProject(projectFile, createDefaultDebugConfiguration)) {
                yield return projCfg;
            }
        }
    }

    /// <summary>
    /// Creates default project configurations for a single project file
    /// </summary>
    private async IAsyncEnumerable<ProjCfg> CreateDefaultProjCfgForProject(string projectPath, bool createDefaultDebugConfiguration) {
        // Since we're not parsing from a solution, we create a standalone project
        var proj = new Proj(projectPath, null);
        
        // Create default configurations
        if (createDefaultDebugConfiguration) {
            yield return new ProjCfg(proj, "Debug", null);
        }
        yield return new ProjCfg(proj, "Release", null);
        
        await Task.CompletedTask; // Make this async for consistency
    }
}

/// <summary>
/// Extension methods for async enumerable operations
/// </summary>
internal static class AsyncEnumerableExtensions {
    public static async Task<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source) {
        var list = new List<T>();
        await foreach (var item in source) {
            list.Add(item);
        }
        return list;
    }
}