namespace bld.Infrastructure;

/// <summary>
/// Parses Dockerfiles to extract configuration information. Backslash continuations are folded into
/// one logical line before parsing; heredocs and ARG/ENV substitution are still not interpreted.
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

        foreach (var line in JoinContinuations(lines)) {
            // Directive and argument may be separated by any whitespace, including a tab.
            var split = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length < 2) continue;
            var directive = split[0];
            var rest = split[1].Trim();

            if (directive.Equals("FROM", StringComparison.OrdinalIgnoreCase)) {
                // FROM [--platform=...] <image> [AS <stage>] - the flags are not the image name.
                var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(p => p.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();
                if (parts.Length >= 1) {
                    info.BaseImages.Add(parts[0]);

                    if (parts.Length >= 3 && parts[1].Equals("AS", StringComparison.OrdinalIgnoreCase)) {
                        info.Stages.Add(parts[2]);
                    }
                }
            }
            else if (directive.Equals("EXPOSE", StringComparison.OrdinalIgnoreCase)) {
                info.ExposedPorts.AddRange(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
            else if (directive.Equals("WORKDIR", StringComparison.OrdinalIgnoreCase)) {
                info.WorkDir = rest;
            }
            else if (directive.Equals("ENTRYPOINT", StringComparison.OrdinalIgnoreCase)) {
                info.EntryPoint = rest;
            }
            else if (directive.Equals("CMD", StringComparison.OrdinalIgnoreCase)) {
                info.Cmd = rest;
            }
        }

        return info;
    }

    /// <summary>
    /// Folds backslash-continued lines into single logical instructions. Dropping a continued line and
    /// then parsing the next physical line as a fresh instruction did not merely lose detail - it
    /// reported no ENTRYPOINT at all for the common `ENTRYPOINT ["dotnet", \` / `"app.dll"]` form.
    /// </summary>
    internal static IEnumerable<string> JoinContinuations(IEnumerable<string> lines) {
        var pending = (string?)null;

        foreach (var rawLine in lines) {
            var line = rawLine.Trim();

            if (pending is null && (line.Length == 0 || line.StartsWith('#'))) continue;
            // A comment inside a continuation is stripped by Docker as well.
            if (pending is { } && line.StartsWith('#')) continue;

            var continues = line.EndsWith('\\');
            if (continues) line = line[..^1].TrimEnd();

            pending = pending is null ? line : $"{pending} {line}".Trim();

            if (!continues) {
                if (pending.Length > 0) yield return pending;
                pending = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(pending)) yield return pending;
    }

    public static Task<List<string>> FindDockerfilesAsync(string rootPath, int maxDepth = 3, Action<string, Exception>? onError = null) {
        var dockerfiles = new List<string>();

        // A Dockerfile passed directly as the root used to yield nothing at all.
        if (File.Exists(rootPath)) {
            if (Path.GetFileName(rootPath).StartsWith("Dockerfile", StringComparison.OrdinalIgnoreCase)) dockerfiles.Add(rootPath);
            return Task.FromResult(dockerfiles);
        }

        if (!Directory.Exists(rootPath)) {
            return Task.FromResult(dockerfiles);
        }

        FindDockerfilesRecursive(rootPath, 0, maxDepth, dockerfiles, onError);

        return Task.FromResult(dockerfiles);
    }

    private static void FindDockerfilesRecursive(string currentPath, int currentDepth, int maxDepth, List<string> dockerfiles, Action<string, Exception>? onError) {
        if (currentDepth > maxDepth) {
            return;
        }

        try {
            // Look for files named exactly "Dockerfile" (case-insensitive)
            var files = Directory.EnumerateFiles(currentPath, "*", SearchOption.TopDirectoryOnly)
                .Where(f => Path.GetFileName(f).Equals("Dockerfile", StringComparison.OrdinalIgnoreCase));
            dockerfiles.AddRange(files);

            // Recurse into subdirectories
            var directories = Directory.GetDirectories(currentPath);
            foreach (var dir in directories) {
                // Skip common directories that shouldn't contain Dockerfiles at root
                var dirName = Path.GetFileName(dir);
                if (dirName is "bin" or "obj" or "node_modules" or ".git" or ".vs") {
                    continue;
                }
                
                FindDockerfilesRecursive(dir, currentDepth + 1, maxDepth, dockerfiles, onError);
            }
        }
        catch (Exception ex) {
            onError?.Invoke(currentPath, ex);
        }
    }
}
