using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace bld.Infrastructure;

internal sealed record GlobalJsonSdk(string? Version, string? RollForward, bool? AllowPrerelease);

internal enum GlobalJsonVerdict {
    /// <summary>No pin, or the pinned SDK already satisfies the target.</summary>
    Ok,
    /// <summary>rollForward may cross to the required major, but only if the pinned SDK is not installed.</summary>
    MayRollForward,
    /// <summary>The pin keeps the SDK below what the target framework needs.</summary>
    Blocks,
}

/// <summary>
/// The global.json that governs a project tree: which one applies, whether its SDK pin can build a
/// given target framework, and a rewrite of the pin that keeps the file's formatting.
/// </summary>
internal static class GlobalJsonFile {
    public const string FileName = "global.json";

    /// <summary>The first global.json walking up from <paramref name="startDirectory"/>; the SDK resolves it the same way.</summary>
    public static string? Find(string startDirectory) {
        var dir = new DirectoryInfo(startDirectory);
        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, FileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public static GlobalJsonSdk? Read(string path) {
        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("sdk", out var sdk) || sdk.ValueKind != JsonValueKind.Object) return null;

        string? Str(string name) => sdk.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        bool? Bool(string name) => sdk.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) ? v.GetBoolean() : null;

        return new GlobalJsonSdk(Str("version"), Str("rollForward"), Bool("allowPrerelease"));
    }

    private static readonly Regex _tfmMajor = new(@"^net(\d+)\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Major SDK version a modern TFM needs: net10.0 builds with a 10.x SDK or newer.</summary>
    public static int? RequiredSdkMajor(string tfm) =>
        _tfmMajor.Match(tfm) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    /// <summary>
    /// Whether the pin lets the SDK reach <paramref name="requiredMajor"/>. Only latestMajor always
    /// crosses a major boundary; "major" (and latestMajor) roll forward only when the pinned version is
    /// missing, and every other policy stays inside the pinned major.
    /// </summary>
    public static (GlobalJsonVerdict Verdict, string Reason) Evaluate(GlobalJsonSdk sdk, int requiredMajor) {
        if (string.IsNullOrWhiteSpace(sdk.Version)) return (GlobalJsonVerdict.Ok, "no SDK version pinned");

        var pinnedMajor = sdk.Version.Split('.')[0];
        if (!int.TryParse(pinnedMajor, out var major)) return (GlobalJsonVerdict.Ok, $"SDK version '{sdk.Version}' is not a plain version");
        if (major >= requiredMajor) return (GlobalJsonVerdict.Ok, $"SDK {sdk.Version} already covers net{requiredMajor}.x");

        var policy = sdk.RollForward ?? "latestPatch";
        if (string.Equals(policy, "latestMajor", StringComparison.OrdinalIgnoreCase)) {
            return (GlobalJsonVerdict.Ok, $"rollForward {policy} uses the newest installed SDK");
        }
        if (string.Equals(policy, "major", StringComparison.OrdinalIgnoreCase)) {
            return (GlobalJsonVerdict.MayRollForward, $"rollForward {policy} only reaches a {requiredMajor}.x SDK when SDK {sdk.Version} is not installed");
        }
        return (GlobalJsonVerdict.Blocks, $"pins SDK {sdk.Version} with rollForward {policy}; net{requiredMajor}.0 requires a {requiredMajor}.x SDK");
    }

    // The "version" string inside the "sdk" object. The sdk object holds only scalars, so stopping at
    // the first '}' is safe; re-serializing through JsonNode would drop comments and reformat the file.
    private static readonly Regex _sdkVersion = new(
        @"(""sdk""\s*:\s*\{[^}]*?""version""\s*:\s*"")([^""]*)("")",
        RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites sdk.version and nothing else: a textual replacement of the value, so comments,
    /// indentation, line endings and BOM survive and the diff is one line. Atomic (temp file + move).
    /// </summary>
    public static async Task WriteVersionAsync(string path, string newVersion, CancellationToken cancellationToken) {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = Encoding.UTF8.GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));

        var match = _sdkVersion.Match(text);
        if (!match.Success) throw new InvalidOperationException($"{path} has no \"sdk\".\"version\" to rewrite.");
        if (_sdkVersion.Matches(text).Count > 1) throw new InvalidOperationException($"{path} has more than one \"sdk\".\"version\"; edit it by hand.");

        var output = text[..match.Groups[2].Index] + newVersion + text[(match.Groups[2].Index + match.Groups[2].Length)..];

        // Prove the result still parses before touching the file.
        using (JsonDocument.Parse(output, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })) { }

        var tempPath = path + ".tmp";
        await File.WriteAllBytesAsync(tempPath, (hasBom ? Encoding.UTF8.GetPreamble() : Array.Empty<byte>()).Concat(Encoding.UTF8.GetBytes(output)).ToArray(), cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }
}
