using bld.Models;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using System.Collections.Concurrent;

namespace bld.Infrastructure;

internal sealed class ProjectEnumerator(IFileSystem FileSystem) {
    public IEnumerable<ProjectInfo?> EnumerateEvaluatedProjects(IEnumerable<string> paths, CleaningOptions options) {
        var visitedInputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projects = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var results = new ConcurrentBag<ProjectInfo?>();

        var toProcess = new Stack<(string Path, ProcessingType Type, int Depth)>();
        var maxDepth = options.Depth > 0 ? options.Depth : int.MaxValue;

        static bool IsSlnExt(string? ext)
            => string.Equals(ext, ".sln", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".slnx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".slnf", StringComparison.OrdinalIgnoreCase);

        static bool IsProjExt(string? ext)
            => string.Equals(ext, ".csproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".fsproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".vbproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".vcxproj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(ext, ".sqlproj", StringComparison.OrdinalIgnoreCase);

        foreach (var path in paths ?? Array.Empty<string>()) {
            var fullPath = FileSystem.FullyQualifyPath(path);
            var isFile = File.Exists(fullPath);
            var isDir = Directory.Exists(fullPath);
            if (!isFile && !isDir) continue;
            if (!visitedInputs.Add(fullPath)) continue;

            if (isFile) {
                var ext = Path.GetExtension(fullPath);
                if (IsSlnExt(ext)) toProcess.Push((fullPath, ProcessingType.Solution, 0));
                else if (IsProjExt(ext)) toProcess.Push((fullPath, ProcessingType.Project, 0));
            }
            else {
                toProcess.Push((fullPath, ProcessingType.Directory, 0));
            }
        }

        var slnFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (toProcess.Count > 0) {
            var (p, type, depth) = toProcess.Pop();
            switch (type) {
                case ProcessingType.Solution:
                    if (slnFiles.Add(p)) {
                        foreach (var proj in ParseSolutionProjects(p)) {
                            projects.TryAdd(proj, 0);
                        }
                    }
                    break;
                case ProcessingType.Project:
                    projects.TryAdd(p, 0);
                    break;
                case ProcessingType.Directory:
                    if (depth >= maxDepth) break;
                    foreach (var sln in EnumerateSolutions(p)) toProcess.Push((sln, ProcessingType.Solution, depth + 1));
                    foreach (var sub in SafeEnum(() => Directory.EnumerateDirectories(p))) toProcess.Push((sub, ProcessingType.Directory, depth + 1));
                    break;
            }
        }

        var gp = BuildGlobalProperties(options);
        Parallel.ForEach(projects.Keys, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, projPath => {
            var info = EvaluateProject(projPath, gp);
            if (info is not null) results.Add(info);
        });

        return results.ToArray();

        // Local helpers
        static IEnumerable<string> EnumerateSolutions(string root) {
            foreach (var f in SafeEnum(() => Directory.EnumerateFiles(root, "*.sln", SearchOption.TopDirectoryOnly))) yield return f;
            foreach (var f in SafeEnum(() => Directory.EnumerateFiles(root, "*.slnx", SearchOption.TopDirectoryOnly))) yield return f;
            foreach (var f in SafeEnum(() => Directory.EnumerateFiles(root, "*.slnf", SearchOption.TopDirectoryOnly))) yield return f;
        }

        static IEnumerable<string> SafeEnum(Func<IEnumerable<string>> action) {
            try { return action(); } catch { return Array.Empty<string>(); }
        }

        static IEnumerable<string> ParseSolutionProjects(string slnPath) {
            var list = new List<string>();
            try {
                var sln = SolutionFile.Parse(slnPath);
                foreach (var p in sln.ProjectsInOrder) {
                    if (p.ProjectType != SolutionProjectType.KnownToBeMSBuildFormat) continue;
                    var ext = Path.GetExtension(p.AbsolutePath);
                    if (!IsProjExt(ext)) continue;
                    if (File.Exists(p.AbsolutePath)) list.Add(p.AbsolutePath);
                }
            }
            catch { }
            return list;
        }

        static Dictionary<string, string> BuildGlobalProperties(CleaningOptions opts) {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(opts.VSToolsPath)) dict["VSToolsPath"] = opts.VSToolsPath!;
            if (!string.IsNullOrEmpty(opts.VSRootPath) && Directory.Exists(Path.Combine(opts.VSRootPath!, "MSBuild")))
                dict["MSBuildExtensionsPath"] = Path.Combine(opts.VSRootPath!, "MSBuild");
            return dict;
        }

        static ProjectInfo? EvaluateProject(string projectPath, IReadOnlyDictionary<string, string> globalProps) {
            try {
                using var pc = new ProjectCollection();
                var project = new Project(projectPath, new Dictionary<string, string>(globalProps), null, pc);

                static string? Safe(string val) => string.IsNullOrWhiteSpace(val) ? null : val;
                static string? SafeDir(string val) {
                    var v = Safe(val);
                    if (v is null) return null;
                    if (Path.DirectorySeparatorChar != '\\') v = v.Replace('\\', Path.DirectorySeparatorChar);
                    return v;
                }

                var tfms = project.GetPropertyValue("TargetFrameworks");
                var tfmList = string.IsNullOrWhiteSpace(tfms)
                    ? Array.Empty<string>()
                    : tfms.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToArray();

                var info = new ProjectInfo {
                    ProjectPath = projectPath,
                    ProjectName = Safe(project.GetPropertyValue("ProjectName")),
                    AssemblyName = Safe(project.GetPropertyValue("AssemblyName")),
                    TargetFramework = Safe(project.GetPropertyValue("TargetFramework")),
                    TargetFrameworks = tfmList,
                    Configuration = Safe(project.GetPropertyValue("Configuration")),
                    Platform = Safe(project.GetPropertyValue("Platform")),
                    OutDir = SafeDir(project.GetPropertyValue("OutDir")),
                    BaseOutputPath = Safe(project.GetPropertyValue("BaseOutputPath")),
                    IntermediateOutputPath = Safe(project.GetPropertyValue("BaseIntermediateOutputPath")),
                    PackageOutputPath = Safe(project.GetPropertyValue("PackageOutputPath")),
                    PackageId = Safe(project.GetPropertyValue("PackageId")),
                    Properties = project.AllEvaluatedProperties.ToDictionary(p => p.Name, p => p.EvaluatedValue, StringComparer.OrdinalIgnoreCase),
                    HasDockerProperties = !string.IsNullOrEmpty(project.GetPropertyValue("ContainerImageName"))
                };

                return info;
            }
            catch {
                return null;
            }
        }
    }
}
