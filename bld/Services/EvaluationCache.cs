using bld.Infrastructure;
using bld.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bld.Services;

/// <summary>
/// Skips the MSBuild evaluation of a project configuration when nothing that fed the last one has
/// changed. An entry records every file the evaluation read outside the SDK, with a hash each, and
/// the extracted result; a hit means every hash still matches. Opt-in, because MSBuild properties
/// coming from the environment are invisible to it.
/// </summary>
internal sealed class EvaluationCache {
    // Files MSBuild picks up by existence alone. A new one changes the key, which is the only way to
    // notice a file that did not exist when the entry was written.
    private static readonly string[] ProbedFileNames = { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" };

    private readonly string _directory;
    private readonly string _tools;
    private readonly IConsoleOutput? _console;
    private int _hits;
    private int _misses;

    /// <param name="directory">Where the entries live.</param>
    /// <param name="tools">Identity of the MSBuild that evaluates; part of every key.</param>
    public EvaluationCache(string directory, string tools, IConsoleOutput? console) {
        _directory = directory;
        _tools = tools;
        _console = console;
    }

    public int Hits => _hits;
    public int Misses => _misses;

    public ProjectPackageReferenceInfo? TryGet(ProjCfg proj, IReadOnlyDictionary<string, string> globalProperties) {
        // Never throws: a truncated entry or a file that cannot be hashed right now is a miss, not
        // an evaluation failure - the plain evaluation that follows would have succeeded.
        try {
            var path = EntryPath(proj, globalProperties);
            if (!File.Exists(path)) return Miss($"no entry for {proj.Path} [{proj.Configuration}]");
            var entry = JsonSerializer.Deserialize(File.ReadAllBytes(path), EvaluationCacheJsonContext.Default.EvaluationCacheEntry);
            if (entry?.Files is null || entry.PackageReferences is null || entry.ProjectReferences is null || entry.TargetFrameworks is null) {
                return Miss($"empty entry for {proj.Path}");
            }

            foreach (var file in entry.Files) {
                if (!string.Equals(HashFile(file.Path), file.Sha256, StringComparison.Ordinal)) {
                    return Miss($"{file.Path} changed since {proj.Path} was last evaluated");
                }
            }

            var info = new ProjectPackageReferenceInfo(
                proj,
                entry.TargetFrameworks,
                entry.UseCpm,
                entry.CpmFile,
                entry.PackageReferences.ToDictionary(p => p.Id, p => new Pkg(p.Id, p.Version, p.VersionOverride, p.CpmVersion, p.Kind), StringComparer.OrdinalIgnoreCase),
                entry.PackageVersions?.ToDictionary(v => v.Id, v => new PackageVersionEntry(v.Version, v.SourceFile), StringComparer.OrdinalIgnoreCase),
                entry.ProjectReferences,
                entry.Files.Select(f => f.Path).ToList());
            Interlocked.Increment(ref _hits);
            _console?.WriteDebug($"Evaluation from cache: {proj.Path} [{proj.Configuration}]");
            return info;
        }
        catch (Exception ex) {
            return Miss($"unreadable entry for {proj.Path}: {ex.FormatMessage()}");
        }
    }

    /// <summary>Never throws: a cache that cannot be written only costs the next run an evaluation.</summary>
    public void Store(ProjectPackageReferenceInfo info, IReadOnlyDictionary<string, string> globalProperties) {
        string? temp = null;
        try {
            var entry = new EvaluationCacheEntry(
                info.Proj.Path,
                globalProperties.ToDictionary(kv => kv.Key, kv => kv.Value),
                _tools,
                ProbeHits(info.Proj.Path),
                info.ContributingFiles.Select(f => new CachedFile(f, HashFile(f) ?? string.Empty)).ToList(),
                info.TargetFrameworks,
                info.UseCpm,
                info.CpmFile,
                info.PackageReferences.Values.Select(p => new CachedPackage(p.Id, p.Version, p.VersionOverride, p.CpmVersion, p.Kind)).ToList(),
                info.PackageVersions?.Select(kv => new CachedPackageVersion(kv.Key, kv.Value.Version, kv.Value.SourceFile)).ToList(),
                info.ProjectReferences.ToList(),
                DateTimeOffset.UtcNow);

            Directory.CreateDirectory(_directory);
            var path = EntryPath(info.Proj, globalProperties);
            temp = path + ".bldtmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry, EvaluationCacheJsonContext.Default.EvaluationCacheEntry));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) {
            _console?.WriteDebug($"Could not cache the evaluation of {info.Proj.Path}: {ex.FormatMessage()}");
            if (temp is { } && File.Exists(temp)) {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private ProjectPackageReferenceInfo? Miss(string reason) {
        Interlocked.Increment(ref _misses);
        _console?.WriteDebug($"Evaluation cache miss: {reason}");
        return null;
    }

    private string EntryPath(ProjCfg proj, IReadOnlyDictionary<string, string> globalProperties) =>
        Path.Combine(_directory, ComputeKey(proj.Path, globalProperties, _tools, ProbeHits(proj.Path)) + ".json");

    internal static string ComputeKey(string projectPath, IReadOnlyDictionary<string, string> globalProperties, string tools, IEnumerable<string> probeHits) {
        var input = new StringBuilder()
            .Append(projectPath).Append('\n')
            .Append(tools).Append('\n');
        foreach (var (key, value) in globalProperties.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)) {
            input.Append(key).Append('=').Append(value).Append('\n');
        }
        foreach (var hit in probeHits) input.Append(hit).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.ToString()))).ToLowerInvariant();
    }

    /// <summary>
    /// The Directory.Build.props / .targets / Directory.Packages.props files that exist between the
    /// project and the root, plus the restore-generated props next to the project.
    /// </summary>
    internal static List<string> ProbeHits(string projectPath) {
        var hits = new List<string>();
        var directory = Path.GetDirectoryName(projectPath);
        var projectFile = Path.GetFileName(projectPath);
        foreach (var generated in new[] { $"obj/{projectFile}.nuget.g.props", $"obj/{projectFile}.nuget.g.targets" }) {
            var candidate = Path.Combine(directory ?? string.Empty, generated);
            if (File.Exists(candidate)) hits.Add(candidate);
        }
        while (!string.IsNullOrEmpty(directory)) {
            foreach (var name in ProbedFileNames) {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) hits.Add(candidate);
            }
            directory = Path.GetDirectoryName(directory);
        }
        return hits;
    }

    /// <summary>Null when the file is gone, which never equals a stored hash.</summary>
    private static string? HashFile(string path) {
        if (!File.Exists(path)) return null;
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }
}

// The key inputs are stored in clear text too, so an entry can be understood without recomputing them.
internal sealed record EvaluationCacheEntry(
    string ProjectPath,
    Dictionary<string, string> GlobalProperties,
    string Tools,
    List<string> ProbeHits,
    List<CachedFile> Files,
    string[] TargetFrameworks,
    bool? UseCpm,
    string? CpmFile,
    List<CachedPackage> PackageReferences,
    List<CachedPackageVersion>? PackageVersions,
    List<string> ProjectReferences,
    DateTimeOffset WrittenAt);

internal sealed record CachedFile(string Path, string Sha256);

internal sealed record CachedPackage(
    string Id,
    string? Version,
    string? VersionOverride,
    string? CpmVersion,
    [property: JsonConverter(typeof(JsonStringEnumConverter<PackageItemKind>))] PackageItemKind Kind);

internal sealed record CachedPackageVersion(string Id, string? Version, string? SourceFile);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(EvaluationCacheEntry))]
internal partial class EvaluationCacheJsonContext : JsonSerializerContext;
