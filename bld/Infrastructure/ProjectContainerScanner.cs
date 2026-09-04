using bld.Models;
using Microsoft.Build.Evaluation;

namespace bld.Infrastructure;

/// <summary>
/// Scans projects for .NET SDK container build properties
/// </summary>
internal class ProjectContainerScanner {
    
    public record ContainerProjectInfo {
        public string ProjectPath { get; init; } = string.Empty;
        public string ProjectName { get; init; } = string.Empty;
        public bool HasContainerSupport { get; init; }
        public string? PublishProfile { get; init; }
        public string? ContainerBaseImage { get; init; }
        public string? ContainerImage { get; init; }
        public string? ContainerFamily { get; init; }
        public string? ContainerRegistry { get; init; }
        public bool EnableSdkContainerSupport { get; init; }
    }

    public static Task<List<string>> FindProjectFilesAsync(string rootPath, int maxDepth = 3, Action<string, Exception>? onError = null) {
        var projects = new List<string>();

        // Accept a project file directly, matching the documented "Root directory, .sln, or project file".
        if (File.Exists(rootPath)) {
            if (SlnScanner.IsProjectFile(rootPath)) projects.Add(rootPath);
            return Task.FromResult(projects);
        }

        if (!Directory.Exists(rootPath)) {
            return Task.FromResult(projects);
        }

        FindProjectFilesRecursive(rootPath, 0, maxDepth, projects, onError);

        return Task.FromResult(projects);
    }

    private static void FindProjectFilesRecursive(string currentPath, int currentDepth, int maxDepth, List<string> projects, Action<string, Exception>? onError) {
        if (currentDepth > maxDepth) {
            return;
        }

        try {
            // Look for .csproj files
            var files = Directory.GetFiles(currentPath, "*.csproj", SearchOption.TopDirectoryOnly);
            projects.AddRange(files);

            // Recurse into subdirectories
            var directories = Directory.GetDirectories(currentPath);
            foreach (var dir in directories) {
                // Skip common directories that shouldn't contain projects at root
                var dirName = Path.GetFileName(dir);
                if (dirName is "bin" or "obj" or "node_modules" or ".git" or ".vs" or "packages") {
                    continue;
                }

                FindProjectFilesRecursive(dir, currentDepth + 1, maxDepth, projects, onError);
            }
        }
        catch (Exception ex) {
            // Swallowing this silently truncated the whole subtree and looked like "nothing found".
            onError?.Invoke(currentPath, ex);
        }
    }

    /// <summary>
    /// Returns null both for "not a container project" and, previously, for "could not be evaluated",
    /// which made a repo-wide evaluation failure look like an empty result. <paramref name="onError"/>
    /// lets the caller tell the two apart.
    /// </summary>
    public static Task<ContainerProjectInfo?> ParseProjectAsync(string projectPath, Dictionary<string, string>? globalProperties = null, Action<string, Exception>? onError = null) {
        if (!File.Exists(projectPath)) {
            return Task.FromResult<ContainerProjectInfo?>(null);
        }

        try {
            var properties = globalProperties ?? new Dictionary<string, string>();
            
            using var projectCollection = new ProjectCollection();
            var project = new Project(projectPath, properties, null, projectCollection);

            var publishProfile = project.GetPropertyValue("PublishProfile");
            var enableSdkContainer = project.GetPropertyValue("EnableSdkContainerSupport");
            var containerBaseImage = project.GetPropertyValue("ContainerBaseImage");
            var containerImage = project.GetPropertyValue("ContainerImage");
            var containerFamily = project.GetPropertyValue("ContainerFamily");
            var containerRegistry = project.GetPropertyValue("ContainerRegistry");

            // Only include projects that will actually create containers
            // Check for PublishProfile=DefaultContainer OR ContainerBaseImage OR ContainerImage
            // Don't include projects with just EnableSdkContainerSupport as library projects may have this
            bool hasContainerSupport = 
                publishProfile?.Equals("DefaultContainer", StringComparison.OrdinalIgnoreCase) == true ||
                !string.IsNullOrEmpty(containerBaseImage) ||
                !string.IsNullOrEmpty(containerImage);

            if (!hasContainerSupport) {
                return Task.FromResult<ContainerProjectInfo?>(null);
            }

            var projectName = project.GetPropertyValue("ProjectName");
            if (string.IsNullOrEmpty(projectName)) {
                projectName = Path.GetFileNameWithoutExtension(projectPath);
            }

            return Task.FromResult<ContainerProjectInfo?>(new ContainerProjectInfo {
                ProjectPath = projectPath,
                ProjectName = projectName,
                HasContainerSupport = hasContainerSupport,
                PublishProfile = string.IsNullOrEmpty(publishProfile) ? null : publishProfile,
                ContainerBaseImage = string.IsNullOrEmpty(containerBaseImage) ? null : containerBaseImage,
                ContainerImage = string.IsNullOrEmpty(containerImage) ? null : containerImage,
                ContainerFamily = string.IsNullOrEmpty(containerFamily) ? null : containerFamily,
                ContainerRegistry = string.IsNullOrEmpty(containerRegistry) ? null : containerRegistry,
                EnableSdkContainerSupport = enableSdkContainer?.Equals("true", StringComparison.OrdinalIgnoreCase) == true
            });
        }
        catch (Exception ex) {
            onError?.Invoke(projectPath, ex);
            return Task.FromResult<ContainerProjectInfo?>(null);
        }
    }
}
