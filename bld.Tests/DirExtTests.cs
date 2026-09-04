using bld.Infrastructure;

namespace bld.Tests;

/// <summary>
/// IsNestedBelow backs the guards that decide whether a directory may be recursively deleted,
/// so its boundary behaviour is safety-critical.
/// </summary>
public class DirExtTests {

    private static string Abs(params string[] parts) =>
        Path.GetFullPath(Path.Combine([OperatingSystem.IsWindows() ? @"C:\" : "/", .. parts]));

    [Fact]
    public void IsNestedBelow_TrueForRealChild() {
        Assert.True(DirExt.IsNestedBelow(Abs("foo", "bar", "obj"), Abs("foo", "bar")));
    }

    [Fact]
    public void IsNestedBelow_FalseForSiblingSharingPrefix() {
        // "/foo/bar2" is not below "/foo/bar" - the old length-only test said it was, which
        // silently skipped legitimate output directories.
        Assert.False(DirExt.IsNestedBelow(Abs("foo", "bar2"), Abs("foo", "bar")));
    }

    [Fact]
    public void IsNestedBelow_FalseForSamePath() {
        Assert.False(DirExt.IsNestedBelow(Abs("foo", "bar"), Abs("foo", "bar")));
    }

    [Fact]
    public void IsNestedBelow_IgnoresTrailingSeparator() {
        Assert.True(DirExt.IsNestedBelow(Abs("foo", "bar", "obj") + Path.DirectorySeparatorChar, Abs("foo", "bar") + Path.DirectorySeparatorChar));
        Assert.False(DirExt.IsNestedBelow(Abs("foo", "bar") + Path.DirectorySeparatorChar, Abs("foo", "bar")));
    }

    [Fact]
    public void IsNestedBelow_MatchesFilesystemCaseRules() {
        var target = Abs("Src", "App", "obj");
        var baseDir = OperatingSystem.IsWindows() ? Abs("src", "app") : Abs("Src", "App");

        // On Windows a case-differing OutDir must still be recognised as nested (the guard must not
        // fail open); on Linux the paths are genuinely different directories.
        Assert.True(DirExt.IsNestedBelow(target, baseDir));

        if (!OperatingSystem.IsWindows()) {
            Assert.False(DirExt.IsNestedBelow(target, Abs("src", "app")));
        }
    }
}
