using System.Text.RegularExpressions;

namespace bld.Infrastructure;

/// <summary>
/// Parses Dockerfiles to extract configuration information.
/// Note: This parser has limited support for multi-line directives (those ending with backslash).
/// Multi-line directives are currently skipped for simplicity.
/// </summary>
internal class DockerfileParser {
    public record DockerfileInfo {
        public string FilePath { get; init; } = string.Empty;
        public List<string> BaseImages { get; init; } = new();
        public List<string> Stages { get; init; } = new();
        public List<string> ExposedPorts { get; init; } = new();
        public string? WorkDir { get; set; }
        public string? EntryPoint { get; set; }
        public string? Cmd { get; set; }
    }

    public static async Task<DockerfileInfo> ParseAsync(string filePath) {
        var info = new DockerfileInfo { FilePath = filePath };
        
        if (!File.Exists(filePath)) {
            return info;
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        
        foreach (var rawLine in lines) {
            var line = rawLine.Trim();
            
            // Skip comments and empty lines
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) {
                continue;
            }

            // Handle line continuations - skip for now as they require multi-line processing
            // This is a known limitation: multi-line directives are not fully parsed
            if (line.EndsWith('\\')) {
                continue;
            }

            // Parse FROM directive
            if (line.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)) {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) {
                    var image = parts[1];
                    info.BaseImages.Add(image);
                    
                    // Check for stage name (FROM image AS stage)
                    if (parts.Length >= 4 && parts[2].Equals("AS", StringComparison.OrdinalIgnoreCase)) {
                        info.Stages.Add(parts[3]);
                    }
                }
            }
            // Parse EXPOSE directive
            else if (line.StartsWith("EXPOSE ", StringComparison.OrdinalIgnoreCase)) {
                var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i < parts.Length; i++) {
                    info.ExposedPorts.Add(parts[i]);
                }
            }
            // Parse WORKDIR directive
            else if (line.StartsWith("WORKDIR ", StringComparison.OrdinalIgnoreCase)) {
                var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) {
                    info.WorkDir = parts[1];
                }
            }
            // Parse ENTRYPOINT directive
            else if (line.StartsWith("ENTRYPOINT ", StringComparison.OrdinalIgnoreCase)) {
                var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) {
                    info.EntryPoint = parts[1];
                }
            }
            // Parse CMD directive
            else if (line.StartsWith("CMD ", StringComparison.OrdinalIgnoreCase)) {
                var parts = line.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) {
                    info.Cmd = parts[1];
                }
            }
        }

        return info;
    }

    public static Task<List<string>> FindDockerfilesAsync(string rootPath, int maxDepth = 3) {
        var dockerfiles = new List<string>();
        
        if (!Directory.Exists(rootPath)) {
            return Task.FromResult(dockerfiles);
        }

        FindDockerfilesRecursive(rootPath, 0, maxDepth, dockerfiles);
        
        return Task.FromResult(dockerfiles);
    }

    private static void FindDockerfilesRecursive(string currentPath, int currentDepth, int maxDepth, List<string> dockerfiles) {
        if (currentDepth > maxDepth) {
            return;
        }

        try {
            // Look for files named Dockerfile or matching Dockerfile.*
            var files = Directory.GetFiles(currentPath, "Dockerfile*", SearchOption.TopDirectoryOnly);
            dockerfiles.AddRange(files);

            // Recurse into subdirectories
            var directories = Directory.GetDirectories(currentPath);
            foreach (var dir in directories) {
                // Skip common directories that shouldn't contain Dockerfiles at root
                var dirName = Path.GetFileName(dir);
                if (dirName is "bin" or "obj" or "node_modules" or ".git" or ".vs") {
                    continue;
                }
                
                FindDockerfilesRecursive(dir, currentDepth + 1, maxDepth, dockerfiles);
            }
        }
        catch (UnauthorizedAccessException) {
            // Skip directories we don't have access to
        }
        catch (Exception) {
            // Skip directories that cause other errors
        }
    }
}
