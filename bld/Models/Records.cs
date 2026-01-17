using System.Diagnostics.CodeAnalysis;

namespace bld.Models;


record class Sln(string Path);

record class Proj(string Path, Sln? Parent) {
    public string Dir => System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException($"Cannot get directory for {Path}");
}

record class ProjCfg(Proj Proj, string? Configuration, string? Platform = default) {
    public string Path => Proj.Path;
    public string ProjDir => Proj.Dir;

    // todo HIGH: default configuration should come from the solution/project
    public string ConfigurationOrDefault => Configuration ?? "Release";
}

internal sealed class ProjCfgEqualityComparer : IEqualityComparer<ProjCfg> {
    public bool Equals(ProjCfg? x, ProjCfg? y) {
        if (x is null || y is null) return false;
        if (ReferenceEquals(x, y)) return true;

        // Compare Path (case insensitive for Windows paths) and Configuration (case insensitive)
        if (!string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(x.ConfigurationOrDefault, y.ConfigurationOrDefault, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }
    public int GetHashCode([DisallowNull] ProjCfg obj) => HashCode.Combine(
        StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path),
        StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ConfigurationOrDefault));
}
