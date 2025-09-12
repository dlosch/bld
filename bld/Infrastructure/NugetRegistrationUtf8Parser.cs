using System.Text;
using System.Text.Json;

namespace bld.Infrastructure;

/// <summary>
/// Streaming parser for NuGet registration JSON using Utf8JsonReader.
/// Extracts catalogEntry.version and dependencyGroups[*].targetFramework for each package item.
/// </summary>
internal static class NugetRegistrationUtf8Parser {

    /// <summary>
    /// Result for a single catalogEntry.
    /// </summary>
    internal sealed record CatalogInfo(string Version, IReadOnlyList<string> TargetFrameworks);

    /// <summary>
    /// Parses the provided UTF-8 JSON and returns one entry per catalogEntry encountered.
    /// </summary>
    public static IReadOnlyList<CatalogInfo> ExtractCatalogInfos(ReadOnlySpan<byte> jsonUtf8) {
        var reader = new Utf8JsonReader(jsonUtf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var results = new List<CatalogInfo>();

        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("catalogEntry")) {
                // advance to catalogEntry value (should be StartObject)
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.StartObject) {
                    if (TryParseCatalogEntry(ref reader, out var info)) {
                        results.Add(info);
                    }
                }
                else {
                    // Skip non-object value (defensive)
                    SkipValue(ref reader);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Convenience overload accepting a string (assumed UTF-8 content).
    /// </summary>
    public static IReadOnlyList<CatalogInfo> ExtractCatalogInfos(string json) => ExtractCatalogInfos(Encoding.UTF8.GetBytes(json));

    private static bool TryParseCatalogEntry(ref Utf8JsonReader reader, out CatalogInfo info) {
        // reader is positioned on StartObject of catalogEntry
        string version = string.Empty;
        var tfms = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.EndObject) {
                info = new CatalogInfo(version, tfms.ToArray());
                return true;
            }

            if (reader.TokenType != JsonTokenType.PropertyName) {
                continue;
            }

            if (reader.ValueTextEquals("version")) {
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.String) {
                    version = reader.GetString() ?? string.Empty;
                }
                else {
                    // non-string version - skip
                    SkipValue(ref reader);
                }
                continue;
            }

            if (reader.ValueTextEquals("dependencyGroups")) {
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.StartArray) {
                    // iterate groups
                    while (reader.Read()) {
                        if (reader.TokenType == JsonTokenType.EndArray) break;
                        if (reader.TokenType == JsonTokenType.StartObject) {
                            ParseDependencyGroup(ref reader, tfms);
                        }
                        else {
                            SkipValue(ref reader);
                        }
                    }
                }
                else {
                    SkipValue(ref reader);
                }
                continue;
            }

            // Unknown property on catalogEntry – skip its value
            if (!reader.Read()) break;
            SkipValue(ref reader);
        }

        info = new CatalogInfo(version, tfms.ToArray());
        return false;
    }

    private static void ParseDependencyGroup(ref Utf8JsonReader reader, HashSet<string> tfms) {
        // reader on StartObject
        while (reader.Read()) {
            if (reader.TokenType == JsonTokenType.EndObject) return;
            if (reader.TokenType != JsonTokenType.PropertyName) continue;

            if (reader.ValueTextEquals("targetFramework")) {
                if (!reader.Read()) break;
                if (reader.TokenType == JsonTokenType.String) {
                    var tfm = reader.GetString();
                    if (!string.IsNullOrEmpty(tfm)) tfms.Add(tfm);
                }
                else {
                    SkipValue(ref reader);
                }
                continue;
            }

            // Skip other properties (e.g., dependencies)
            if (!reader.Read()) break;
            SkipValue(ref reader);
        }
    }

    private static void SkipValue(ref Utf8JsonReader reader) {
        // reader is positioned on the first token of a value
        switch (reader.TokenType) {
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray: {
                int depth = 0;
                do {
                    if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) depth++;
                    else if (reader.TokenType is JsonTokenType.EndObject or JsonTokenType.EndArray) depth--;
                } while (depth > 0 && reader.Read());
                break;
            }
            default:
                // primitives are already on the value; nothing else to do
                break;
        }
    }
}
