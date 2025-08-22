using System.Diagnostics.CodeAnalysis;

namespace bld.Models;


record class Sln(string Path);

record class Proj(string Path, Sln? Parent) {
    public string Dir => System.IO.Path.GetDirectoryName(Path) ?? throw new InvalidOperationException($"Cannot get directory for {Path}");
}

record class ProjCfg(Proj Proj, string Configuration, string? Platform = default) {
    public string Path => Proj.Path;
    public string ProjDir => Proj.Dir;
}

internal sealed class ProjCfgEqualityComparer : IEqualityComparer<ProjCfg> {
    public bool Equals(ProjCfg? x, ProjCfg? y) {
        if (x is null || y is null) return false;
        if (ReferenceEquals(x, y)) return true;

        // Compare Path (case sensitive) and Configuration (case insensitive)
        if (x.Path != y.Path) return false;
        if (0 != string.Compare(x.Configuration, y.Configuration, StringComparison.OrdinalIgnoreCase)) return false;

        return true;
    }
    public int GetHashCode([DisallowNull] ProjCfg obj) => HashCode.Combine(obj.Path, obj.Configuration?.ToLower(), obj.Platform?.ToLower());
}
