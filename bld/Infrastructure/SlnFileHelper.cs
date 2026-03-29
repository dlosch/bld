using Microsoft.Build.Construction;
using System.Text.Json;

namespace bld.Infrastructure;

/// <summary>
/// Wraps SolutionFile.Parse to handle solution files that reference
/// unsupported .vcproj projects (pre-MSBuild format), and .slnf
/// (solution filter) files that restrict which projects are included.
/// </summary>
internal static class SlnFileHelper {
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Result of parsing a .sln/.slnf/.slnx file.
    /// When <see cref="ProjectFilter"/> is non-null only projects whose
    /// absolute path is in the set should be processed.
    /// </summary>
    internal record SlnParseResult(SolutionFile Solution, HashSet<string>? ProjectFilter);

    /// <summary>
    /// Parses any supported solution format. For .slnf files the parent .sln
    /// is parsed and a project filter is returned so callers only process the
    /// projects listed in the filter file.
    /// </summary>
    internal static SlnParseResult ParseWithFilter(string path) {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase))
            return ParseSlnf(fullPath);
        return new SlnParseResult(Parse(fullPath), null);
    }

    internal static IEnumerable<ProjectInSolution> EnumerateIncludedMSBuildProjects(string path) {
        var result = ParseWithFilter(path);
        return EnumerateIncludedMSBuildProjects(result);
    }

    internal static IEnumerable<ProjectInSolution> EnumerateIncludedMSBuildProjects(SlnParseResult result) {
        foreach (var project in result.Solution.ProjectsInOrder) {
            if (project.ProjectType != SolutionProjectType.KnownToBeMSBuildFormat)
                continue;
            if (string.IsNullOrWhiteSpace(project.AbsolutePath))
                continue;
            if (result.ProjectFilter is not null && !result.ProjectFilter.Contains(project.AbsolutePath))
                continue;
            yield return project;
        }
    }

    internal static SolutionFile Parse(string slnPath) {
        return SolutionFile.Parse(slnPath);
    }

    /// <summary>
    /// Parses a .slnf (solution filter) file.  The JSON contains a path to
    /// the parent .sln and an array of project paths (relative to the .sln
    /// directory) that should be included.
    /// </summary>
    private static SlnParseResult ParseSlnf(string slnfPath) {
        var slnfFullPath = Path.GetFullPath(slnfPath);
        var slnfDir = Path.GetDirectoryName(slnfFullPath)!;

        using var stream = File.OpenRead(slnfFullPath);
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;

        var solutionElement = root.GetProperty("solution");
        var slnPath = solutionElement.GetProperty("path").GetString()
            ?? throw new InvalidOperationException($"Missing 'solution.path' in {slnfFullPath}");

        var slnFullPath = ResolvePathFromSlnf(slnPath, slnfDir);
        var solution = Parse(slnFullPath);

        HashSet<string>? projectFilter = null;
        if (solutionElement.TryGetProperty("projects", out var projectsElement)) {
            var slnDir = Path.GetDirectoryName(slnFullPath)!;
            projectFilter = new HashSet<string>(PathComparer);

            foreach (var projectElement in projectsElement.EnumerateArray()) {
                var projectPath = projectElement.GetString();
                if (string.IsNullOrWhiteSpace(projectPath)) continue;
                projectFilter.Add(ResolvePathFromSlnf(projectPath, slnDir));
            }
        }

        return new SlnParseResult(solution, projectFilter);
    }

    private static bool IsWindowsDriveAbsolutePath(string path) =>
        path.Length >= 3
        && char.IsAsciiLetter(path[0])
        && path[1] == ':'
        && (path[2] == '\\' || path[2] == '/');

    private static string NormalizeSlashes(string path) =>
        Path.DirectorySeparatorChar == '\\'
            ? path.Replace('/', '\\')
            : path.Replace('\\', '/');

    private static string ResolvePathFromSlnf(string path, string baseDirectory) {
        if (IsWindowsDriveAbsolutePath(path)) {
            if (OperatingSystem.IsWindows())
                return Path.GetFullPath(NormalizeSlashes(path));

            var drive = char.ToLowerInvariant(path[0]);
            var relative = path[2..].TrimStart('\\', '/');
            var converted = $"/mnt/{drive}";
            if (!string.IsNullOrEmpty(relative))
                converted = $"{converted}/{relative.Replace('\\', '/')}";
            return Path.GetFullPath(converted);
        }

        var normalized = NormalizeSlashes(path);

        if (!OperatingSystem.IsWindows()
            && path.Length > 0
            && path[0] == '\\'
            && !path.StartsWith(@"\\", StringComparison.Ordinal)) {
            normalized = normalized.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        if (Path.IsPathRooted(normalized))
            return Path.GetFullPath(normalized);

        return Path.GetFullPath(Path.Combine(baseDirectory, normalized));
    }
}
