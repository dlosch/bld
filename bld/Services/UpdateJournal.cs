using bld.Infrastructure;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bld.Services;

/// <summary>Which XML slot an <c>outdated --apply</c> run rewrote.</summary>
internal enum EditKind {
    /// <summary>PackageVersion or GlobalPackageReference in Directory.Packages.props.</summary>
    PackageVersion,
    /// <summary>PackageReference Version in a project file.</summary>
    PackageReference,
    /// <summary>PackageReference VersionOverride in a project file.</summary>
    VersionOverride,
    /// <summary>One bracketed entry of a PackageDownload version list.</summary>
    PackageDownload,
    /// <summary>A PackageVersion element replaced by a comment; <c>From</c> is the element, <c>To</c> is null.</summary>
    OrphanComment,
}

/// <summary>One change a run made: the file, the package as spelled there, and the values before and after.</summary>
internal sealed record JournalEdit(string File, string Package, EditKind Kind, string From, string? To);

/// <summary>Everything one run wrote, so <c>bld outdated undo</c> can take it back.</summary>
internal sealed record JournalEntry(string Input, DateTimeOffset WrittenAt, string Bld, string Command, List<JournalEdit> Edits);

/// <summary>An entry together with the file it lives in, so it can be rewritten or deleted after an undo.</summary>
internal sealed record JournalRun(string Path, JournalEntry Entry);

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalEntry))]
internal partial class JournalJsonContext : JsonSerializerContext;

/// <summary>
/// The undo journal: one file per run that changed something, under
/// <c>&lt;root&gt;/&lt;sha256(input)&gt;/&lt;timestamp&gt;.json</c>. Only the newest
/// <see cref="Keep"/> runs per input are kept.
/// </summary>
internal sealed class UpdateJournal {
    internal const int Keep = 20;

    public static string DefaultRoot => Path.Combine(BldHome.Root, "history");

    private readonly string _root;

    public UpdateJournal(string root) {
        _root = root;
    }

    public string DirectoryFor(string input) => Path.Combine(_root, Key(input));

    /// <summary>
    /// The input's full path without a trailing separator, case-folded where the file system is:
    /// the same input reached through a relative path, `repo/` instead of `repo`, or on Windows a
    /// different casing must land in the same journal, while on Linux `Repo` and `repo` are two.
    /// </summary>
    internal static string Key(string input) {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        if (CaseInsensitiveFileSystem) path = path.ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant();
    }

    internal static bool CaseInsensitiveFileSystem => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>Writes the entry and prunes the input's journal down to <see cref="Keep"/> runs. Returns the file path.</summary>
    public string Write(JournalEntry entry) {
        var directory = DirectoryFor(entry.Input);
        Directory.CreateDirectory(directory);
        // The file name is the sort key, so a collision moves the stamp forward instead of adding a
        // suffix that would sort the later run before the earlier one.
        var stamp = entry.WrittenAt.ToUniversalTime();
        var path = Path.Combine(directory, FileName(stamp));
        while (File.Exists(path)) {
            stamp = stamp.AddMilliseconds(1);
            path = Path.Combine(directory, FileName(stamp));
        }
        Save(path, entry);

        foreach (var stale in EntryFiles(directory).Skip(Keep)) {
            try { File.Delete(stale); } catch { /* best effort */ }
        }
        return path;
    }

    /// <summary>The input's runs, newest first. A file that cannot be read is reported and skipped.</summary>
    public IReadOnlyList<JournalRun> Load(string input, IConsoleOutput console) {
        var directory = DirectoryFor(input);
        if (!Directory.Exists(directory)) return Array.Empty<JournalRun>();

        var runs = new List<JournalRun>();
        foreach (var path in EntryFiles(directory)) {
            try {
                var entry = JsonSerializer.Deserialize(File.ReadAllText(path), JournalJsonContext.Default.JournalEntry);
                if (entry is null || entry.Edits is null) {
                    console.WriteWarning($"Skipping unreadable undo journal entry {path}.");
                    continue;
                }
                runs.Add(new JournalRun(path, entry));
            }
            catch (Exception ex) when (ex is JsonException or IOException) {
                console.WriteWarning($"Skipping unreadable undo journal entry {path}: {ex.FormatMessage()}");
            }
        }
        return runs;
    }

    /// <summary>Replaces an entry with what is left of it after a partial undo.</summary>
    public static void Rewrite(string path, JournalEntry entry) => Save(path, entry);

    public static void Delete(string path) {
        if (File.Exists(path)) File.Delete(path);
    }

    private static string FileName(DateTimeOffset stamp) => stamp.ToString("yyyyMMdd-HHmmss-fff") + "Z.json";

    /// <summary>Entry files newest first; the timestamp is the file name, so ordinal order is time order.</summary>
    private static IEnumerable<string> EntryFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.json").OrderByDescending(Path.GetFileName, StringComparer.Ordinal);

    private static void Save(string path, JournalEntry entry) {
        var temp = path + ".bldtmp";
        try {
            File.WriteAllText(temp, JsonSerializer.Serialize(entry, JournalJsonContext.Default.JournalEntry));
            File.Move(temp, path, overwrite: true);
        }
        catch {
            if (File.Exists(temp)) {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
            throw;
        }
    }
}
