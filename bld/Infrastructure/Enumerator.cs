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