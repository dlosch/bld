using System.Text.Json;

namespace bld.Infrastructure;

/// <summary>A package NuGet resolved for a project, merged across the project's target frameworks.</summary>
internal sealed record ResolvedPackage(
    string Id,
    string Version,
    IReadOnlyList<string> TargetFrameworks,
    bool IsDirect,
    /// <summary>Packages whose dependency lists contain this one (the immediate parents).</summary>
    IReadOnlyList<string> RequestedBy);

/// <summary>
/// Reads the resolved package graph from project.assets.json, the file `dotnet restore` writes under
/// MSBuildProjectExtensionsPath. MSBuild evaluation only knows the direct PackageReference items; the
/// transitive closure exists nowhere else without re-running restore.
/// </summary>
internal static class ProjectAssetsReader {
    public const string FileName = "project.assets.json";

    public static string GetPath(string projectExtensionsPath) => Path.Combine(projectExtensionsPath, FileName);

    public static IReadOnlyList<ResolvedPackage>? TryRead(string path) =>
        File.Exists(path) ? Parse(File.ReadAllText(path)) : null;

    /// <summary>
    /// "targets" holds one graph per framework (and per runtime identifier, which repeats the framework's
    /// packages); "project/frameworks" lists the direct dependencies per target alias. Project references
    /// appear with type "project" and are left out.
    /// </summary>
    public static IReadOnlyList<ResolvedPackage> Parse(string json) {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        // framework name (as used by "targets") -> alias (as the project spells it), and alias -> direct ids
        var aliasByFramework = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directByAlias = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("project", out var project) && project.TryGetProperty("frameworks", out var frameworks) && frameworks.ValueKind == JsonValueKind.Object) {
            foreach (var framework in frameworks.EnumerateObject()) {
                var alias = framework.Name;
                var name = framework.Value.TryGetProperty("framework", out var fw) && fw.ValueKind == JsonValueKind.String ? fw.GetString()! : alias;
                aliasByFramework[name] = alias;

                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (framework.Value.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object) {
                    foreach (var dependency in dependencies.EnumerateObject()) ids.Add(dependency.Name);
                }
                directByAlias[alias] = ids;
            }
        }

        var merged = new Dictionary<(string Id, string Version), (List<string> Tfms, HashSet<string> RequestedBy, bool IsDirect)>();
        var keyComparer = StringComparer.OrdinalIgnoreCase;

        if (!root.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Object) {
            return Array.Empty<ResolvedPackage>();
        }

        foreach (var target in targets.EnumerateObject()) {
            // "net8.0" or "net8.0/win-x64"
            var frameworkName = target.Name.Split('/', 2)[0];
            var alias = aliasByFramework.TryGetValue(frameworkName, out var a) ? a : frameworkName;
            var directIds = directByAlias.TryGetValue(alias, out var d) ? d : new HashSet<string>(keyComparer);

            var parents = new Dictionary<string, HashSet<string>>(keyComparer);
            var entries = new List<(string Id, string Version, JsonElement Value)>();
            foreach (var entry in target.Value.EnumerateObject()) {
                var slash = entry.Name.IndexOf('/');
                if (slash <= 0) continue;
                var id = entry.Name[..slash];
                var version = entry.Name[(slash + 1)..];
                entries.Add((id, version, entry.Value));

                if (entry.Value.TryGetProperty("dependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Object) {
                    foreach (var dependency in dependencies.EnumerateObject()) {
                        if (!parents.TryGetValue(dependency.Name, out var set)) parents[dependency.Name] = set = new HashSet<string>(keyComparer);
                        set.Add(id);
                    }
                }
            }

            foreach (var (id, version, value) in entries) {
                var type = value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (!string.Equals(type, "package", StringComparison.OrdinalIgnoreCase)) continue;

                var key = (id, version);
                if (!merged.TryGetValue(key, out var existing)) {
                    // Keys are compared ordinally; normalise casing through the first occurrence.
                    var match = merged.Keys.FirstOrDefault(k => keyComparer.Equals(k.Id, id) && keyComparer.Equals(k.Version, version));
                    if (match != default) {
                        key = match;
                        existing = merged[key];
                    }
                    else {
                        existing = (new List<string>(), new HashSet<string>(keyComparer), false);
                        merged[key] = existing;
                    }
                }

                if (!existing.Tfms.Contains(alias, keyComparer)) existing.Tfms.Add(alias);
                if (parents.TryGetValue(id, out var requestedBy)) existing.RequestedBy.UnionWith(requestedBy);
                merged[key] = (existing.Tfms, existing.RequestedBy, existing.IsDirect || directIds.Contains(id));
            }
        }

        return merged
            .OrderBy(kvp => kvp.Key.Id, keyComparer)
            .ThenBy(kvp => kvp.Key.Version, keyComparer)
            .Select(kvp => new ResolvedPackage(
                kvp.Key.Id,
                kvp.Key.Version,
                kvp.Value.Tfms,
                kvp.Value.IsDirect,
                kvp.Value.RequestedBy.OrderBy(p => p, keyComparer).ToList()))
            .ToList();
    }
}
