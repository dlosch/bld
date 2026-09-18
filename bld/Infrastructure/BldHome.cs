namespace bld.Infrastructure;

/// <summary>
/// Where bld keeps state that outlives a run: <c>BLD_HOME</c> when set, otherwise the per-user
/// local application data folder (<c>%LOCALAPPDATA%\bld</c>, <c>~/.local/share/bld</c>).
/// </summary>
internal static class BldHome {
    public static string Root =>
        Environment.GetEnvironmentVariable("BLD_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "bld");

    public static string Cache => Path.Combine(Root, "cache");
}
