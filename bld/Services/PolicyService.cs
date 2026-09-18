using bld.Infrastructure;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace bld.Services;

/// <summary>
/// A persistent form of <c>--max-bump-for</c>: the packages matching <paramref name="Match"/> are
/// never proposed a step bigger than <paramref name="MaxBump"/>. Same wildcard syntax as
/// <c>--package</c>.
/// </summary>
internal sealed record PolicyRule(
    string Match,
    [property: JsonConverter(typeof(JsonStringEnumConverter<MaxBump>))] MaxBump MaxBump,
    string? Reason,
    DateOnly? Since);

internal sealed record PolicyFile(List<PolicyRule> Rules);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PolicyFile))]
internal partial class PolicyJsonContext : JsonSerializerContext;

/// <summary>
/// The user's package policies, read from and written to <c>policy.json</c> under
/// <see cref="BldHome.Root"/>. Rules apply to every <c>outdated</c> run without further options;
/// <c>--max-bump-for</c> overrides them for one run, <c>--ignore-policy</c> switches them off.
/// </summary>
internal sealed class PolicyService {
    public static string DefaultPath => Path.Combine(BldHome.Root, "policy.json");

    public string FilePath { get; }
    public List<PolicyRule> Rules { get; }

    private PolicyService(string path, List<PolicyRule> rules) {
        FilePath = path;
        Rules = rules;
    }

    /// <summary>
    /// A missing file is an empty policy. A file that cannot be read is an error, not an empty
    /// policy: silently ignoring a broken rule set would apply updates the user chose to forbid.
    /// </summary>
    /// <exception cref="InvalidDataException">The file exists but is not a valid policy file.</exception>
    public static PolicyService Load(string path) {
        if (!File.Exists(path)) return new PolicyService(path, new List<PolicyRule>());
        try {
            var file = JsonSerializer.Deserialize(File.ReadAllText(path), PolicyJsonContext.Default.PolicyFile);
            var rules = (file?.Rules ?? new List<PolicyRule>()).Where(r => !string.IsNullOrWhiteSpace(r?.Match)).ToList();
            return new PolicyService(path, rules);
        }
        catch (JsonException ex) {
            throw new InvalidDataException($"Could not read the package policy file {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Adds a rule, or replaces the one with the same match.</summary>
    public void Set(string match, MaxBump maxBump, string? reason) {
        Rules.RemoveAll(r => r.Match.Equals(match, StringComparison.OrdinalIgnoreCase));
        Rules.Add(new PolicyRule(match, maxBump, reason, DateOnly.FromDateTime(DateTime.Today)));
    }

    /// <summary>Removes the rule with this match; returns whether there was one.</summary>
    public bool Remove(string match) => Rules.RemoveAll(r => r.Match.Equals(match, StringComparison.OrdinalIgnoreCase)) > 0;

    /// <summary>
    /// The rule that applies to a package id: the most specific match (non-wildcard characters),
    /// ties going to the rule listed last, like <c>--max-bump-for</c>. Null without a match.
    /// </summary>
    public static PolicyRule? Find(string id, IReadOnlyList<PolicyRule> rules) {
        PolicyRule? best = null;
        var bestSpecificity = -1;
        foreach (var rule in rules) {
            if (!OutdatedService.Matches(id, rule.Match)) continue;
            var specificity = rule.Match.Count(c => c != '*');
            if (specificity < bestSpecificity) continue;
            bestSpecificity = specificity;
            best = rule;
        }
        return best;
    }

    public void Save() {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".bldtmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new PolicyFile(Rules), PolicyJsonContext.Default.PolicyFile));
        File.Move(temp, FilePath, overwrite: true);
    }
}
