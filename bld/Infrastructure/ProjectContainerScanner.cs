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

    public static Task<List<string>> FindProjectFilesAsync(string rootPath, int maxDepth = 3) {
        var projects = new List<string>();
        
        if (!Directory.Exists(rootPath)) {
            return Task.FromResult(projects);
        }

        FindProjectFilesRecursive(rootPath, 0, maxDepth, projects);
        
        return Task.FromResult(projects);
    }

    private static void FindProjectFilesRecursive(string currentPath, int currentDepth, int maxDepth, List<string> projects) {
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
                
                FindProjectFilesRecursive(dir, currentDepth + 1, maxDepth, projects);
            }
        }
        catch (UnauthorizedAccessException) {
            // Skip directories we don't have access to
        }
        catch (Exception) {
            // Skip directories that cause other errors
        }
    }

    public static Task<ContainerProjectInfo?> ParseProjectAsync(string projectPath, Dictionary<string, string>? globalProperties = null) {
        if (!File.Exists(projectPath)) {
            return null;
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

            // Check if this project has container support
            bool hasContainerSupport = 
                publishProfile?.Equals("DefaultContainer", StringComparison.OrdinalIgnoreCase) == true ||
                enableSdkContainer?.Equals("true", StringComparison.OrdinalIgnoreCase) == true ||
                !string.IsNullOrEmpty(containerBaseImage) ||
                !string.IsNullOrEmpty(containerImage);

            if (!hasContainerSupport) {
                return null;
            }

            var projectName = project.GetPropertyValue("ProjectName");
            if (string.IsNullOrEmpty(projectName)) {
                projectName = Path.GetFileNameWithoutExtension(projectPath);
            }

            return Task.FromResult(new ContainerProjectInfo {
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
        catch (Exception) {
            // Failed to parse project - return null
            return null;
        }
    }
}
