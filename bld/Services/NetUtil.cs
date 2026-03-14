namespace bld.Services;

/// <summary>
/// Utility class for .NET framework and target framework moniker validation
/// </summary>
internal class NetUtil {
    internal static NetUtil Instance { get; } = new();

    private NetUtil() {
        _validTfms = new HashSet<string>(ValidTfms, StringComparer.OrdinalIgnoreCase);
    }

    private readonly HashSet<string> _validTfms;

    internal static bool IsNet8OrHigher(string? tfm) {
        if (tfm == null) return false;
        if (tfm.StartsWith("net8", StringComparison.OrdinalIgnoreCase)) return true;
        if (tfm.StartsWith("net9", StringComparison.OrdinalIgnoreCase)) return true;
        if (tfm.StartsWith("net10", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    internal bool IsTfmName(string name, StringComparison defaultComparison) {
        if (defaultComparison == StringComparison.OrdinalIgnoreCase)
            return _validTfms.Contains(name);
        return ValidTfms.Any(x => 0 == string.Compare(name, x, defaultComparison));
    }

    private readonly string[] ValidTfms = new string[] {
        "netcoreapp1.0",
        "netcoreapp1.1",
        "netcoreapp2.0",
        "netcoreapp2.1",
        "netcoreapp2.2",
        "netcoreapp3.0",
        "netcoreapp3.1",
        "net5.0",
        "net6.0",
        "net7.0",
        "net8.0",
        "net9.0",
        "net10.0",
        "netstandard1.0",
        "netstandard1.1",
        "netstandard1.2",
        "netstandard1.3",
        "netstandard1.4",
        "netstandard1.5",
        "netstandard1.6",
        "netstandard2.0",
        "netstandard2.1",
        "net11",
        "net20",
        "net35",
        "net40",
        "net403",
        "net45",
        "net451",
        "net452",
        "net46",
        "net461",
        "net462",
        "net47",
        "net471",
        "net472",
        "net48",
        "net481"
    };
}