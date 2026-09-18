namespace bld.Infrastructure;

/// <summary>
/// Parses Dockerfiles to extract configuration information. Backslash continuations are folded into
/// one logical line before parsing; heredocs are still not interpreted, and ARG/ENV substitution is
/// left to the caller (see <see cref="DockerfileSubstitution"/>).
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

        /// <summary>Every build stage in file order, with the instructions it contains.</summary>
        public List<DockerStage> StageDetails { get; init; } = new();

        /// <summary>
        /// ARG defaults declared before the first FROM. They are the only values available for
        /// substitution in FROM lines.
        /// </summary>
        public Dictionary<string, string> GlobalArgs { get; init; } = new(StringComparer.Ordinal);
    }

    /// <summary>One instruction as written, directive upper-cased, arguments verbatim.</summary>
    public sealed record DockerInstruction(string Directive, string Arguments);

    /// <summary>
    /// A FROM ... AS block. <see cref="BaseRef"/> is the raw FROM argument without flags; it may name
    /// another stage instead of an image.
    /// </summary>
    public sealed record DockerStage(int Index, string BaseRef, string? Name) {
        public List<DockerInstruction> Instructions { get; } = new();
    }

    public static async Task<DockerfileInfo> ParseAsync(string filePath) {
        var info = new DockerfileInfo { FilePath = filePath };

        if (!File.Exists(filePath)) {
            return info;
        }

        var lines = await File.ReadAllLinesAsync(filePath);
        Parse(info, lines);
        return info;
    }

    /// <summary>Parses already-read lines; lets tests and the migration avoid a temp file.</summary>
    internal static DockerfileInfo Parse(IEnumerable<string> lines, string filePath = "") {
        var info = new DockerfileInfo { FilePath = filePath };
        Parse(info, lines);
        return info;
    }

    private static void Parse(DockerfileInfo info, IEnumerable<string> lines) {
        DockerStage? stage = null;

        foreach (var line in JoinContinuations(lines)) {
            // Directive and argument may be separated by any whitespace, including a tab.
            var split = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length < 2) continue;
            var directive = split[0].ToUpperInvariant();
            var rest = split[1].Trim();

            if (directive == "FROM") {
                // FROM [--platform=...] <image> [AS <stage>] - the flags are not the image name.
                var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                    .SkipWhile(p => p.StartsWith("--", StringComparison.Ordinal))
                    .ToArray();
                if (parts.Length >= 1) {
                    info.BaseImages.Add(parts[0]);

                    string? name = null;
                    if (parts.Length >= 3 && parts[1].Equals("AS", StringComparison.OrdinalIgnoreCase)) {
                        name = parts[2];
                        info.Stages.Add(name);
                    }

                    stage = new DockerStage(info.StageDetails.Count, parts[0], name);
                    info.StageDetails.Add(stage);
                }
                continue;
            }

            if (stage is null) {
                // Only ARG is legal before the first FROM; anything else is a Dockerfile error Docker
                // would reject, so ignoring it here is harmless.
                if (directive == "ARG") {
                    foreach (var (key, value) in DockerfileSubstitution.ParseKeyValues(rest)) {
                        if (value is { }) info.GlobalArgs[key] = value;
                    }
                }
                continue;
            }

            stage.Instructions.Add(new DockerInstruction(directive, rest));

            if (directive == "EXPOSE") {
                info.ExposedPorts.AddRange(rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            }
            else if (directive == "WORKDIR") {
                info.WorkDir = rest;
            }
            else if (directive == "ENTRYPOINT") {
                info.EntryPoint = rest;
            }
            else if (directive == "CMD") {
                info.Cmd = rest;
            }
        }
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

/// <summary>
/// The small subset of Dockerfile argument syntax the migration needs: `key=value` lists as used by
/// ARG/ENV/LABEL (quoted values, the legacy `ENV key value` form), exec-form JSON arrays, and
/// `$VAR`/`${VAR}`/`${VAR:-default}` substitution.
/// </summary>
internal static class DockerfileSubstitution {

    /// <summary>
    /// Splits `k=v k2="v 2"` into pairs. A single token without `=` followed by more text is the legacy
    /// `ENV key value with spaces` form. A key without a value (a bare `ARG NAME`) yields null.
    /// </summary>
    internal static List<(string Key, string? Value)> ParseKeyValues(string arguments) {
        var tokens = Tokenize(arguments);
        var result = new List<(string, string?)>();
        if (tokens.Count == 0) return result;

        if (!tokens[0].Contains('=')) {
            // Legacy form: everything after the key is the value, quotes kept as written.
            var rest = arguments.Length > tokens[0].Length ? arguments[tokens[0].Length..].Trim() : null;
            result.Add((tokens[0], rest is { Length: > 0 } ? Unquote(rest) : null));
            return result;
        }

        foreach (var token in tokens) {
            // A quoted key (`LABEL "com.example.vendor"="ACME"`) may contain `=` inside the quotes.
            var eq = token.StartsWith('"') || token.StartsWith('\'')
                ? token.IndexOf('=', Math.Max(1, token.IndexOf(token[0], 1)))
                : token.IndexOf('=');
            if (eq < 0) {
                result.Add((Unquote(token), null));
                continue;
            }
            result.Add((Unquote(token[..eq]), Unquote(token[(eq + 1)..])));
        }
        return result;
    }

    /// <summary>
    /// ENTRYPOINT/CMD/RUN arguments as a command vector. The exec form `["a", "b"]` maps to its
    /// elements; the shell form is what Docker runs through `/bin/sh -c`, so it becomes that vector.
    /// </summary>
    internal static List<string> ParseCommand(string arguments) {
        var trimmed = arguments.Trim();
        if (trimmed.StartsWith('[')) {
            try {
                var parts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(trimmed);
                if (parts is { }) return parts;
            }
            catch (System.Text.Json.JsonException) {
                // Not valid JSON: Docker treats it as the shell form too.
            }
        }
        return ["/bin/sh", "-c", trimmed];
    }

    /// <summary>Tokenizes on whitespace, keeping quoted spans (with their quotes) together.</summary>
    internal static List<string> Tokenize(string text) {
        var tokens = new List<string>();
        var current = new System.Text.StringBuilder();
        var quote = '\0';

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (quote != '\0') {
                current.Append(c);
                if (c == '\\' && i + 1 < text.Length) { current.Append(text[++i]); }
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '"' or '\'') { quote = c; current.Append(c); continue; }
            if (c == '\\' && i + 1 < text.Length) { current.Append(text[++i]); continue; }
            if (char.IsWhiteSpace(c)) {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    internal static string Unquote(string value) {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) {
            value = value[1..^1];
        }
        return value.Replace("\\\"", "\"").Replace("\\ ", " ");
    }

    /// <summary>
    /// Expands `$NAME`, `${NAME}`, `${NAME:-default}` and `${NAME:+alternative}`. Names without a value
    /// stay as written and are reported through <paramref name="unresolved"/>, so the caller can say
    /// which build args the migrated project no longer sees.
    /// </summary>
    internal static string Substitute(string text, IReadOnlyDictionary<string, string> values, ISet<string>? unresolved = null) {
        if (!text.Contains('$')) return text;

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '$') { sb.Append('$'); i++; continue; }
            if (c != '$' || i + 1 >= text.Length) { sb.Append(c); continue; }

            string name;
            string? fallback = null;
            var alternative = false;
            int end;
            if (text[i + 1] == '{') {
                end = text.IndexOf('}', i + 2);
                if (end < 0) { sb.Append(c); continue; }
                var inner = text[(i + 2)..end];
                var modifier = inner.IndexOf(":-", StringComparison.Ordinal);
                if (modifier < 0) modifier = inner.IndexOf(":+", StringComparison.Ordinal);
                if (modifier >= 0) {
                    alternative = inner[modifier + 1] == '+';
                    fallback = inner[(modifier + 2)..];
                    name = inner[..modifier];
                }
                else {
                    name = inner;
                }
            }
            else {
                end = i + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                if (end == i + 1) { sb.Append(c); continue; }
                name = text[(i + 1)..end];
                end--;
            }

            if (values.TryGetValue(name, out var value)) {
                sb.Append(alternative ? fallback : value);
            }
            else if (fallback is { } && !alternative) {
                sb.Append(fallback);
            }
            else if (!alternative) {
                unresolved?.Add(name);
                sb.Append(text, i, end - i + 1);
            }
            i = end;
        }
        return sb.ToString();
    }
}
