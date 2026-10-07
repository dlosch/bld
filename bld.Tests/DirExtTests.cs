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

    // ----- EmptyParentCandidates (A1.4) ----------------------------------------------------------

    [Fact]
    public void EmptyParentCandidates_WalksUpToButNotIncludingTheBinRoot() {
        var proj = Abs("repo", "App");
        var marked = Abs("repo", "App", "bin", "Debug", "net8.0");
        var parents = DirExt.EmptyParentCandidates(marked, new[] { proj });
        Assert.Equal(new[] { Abs("repo", "App", "bin", "Debug") }, parents);
    }

    [Fact]
    public void EmptyParentCandidates_StopsAtTheObjRoot() {
        var proj = Abs("repo", "App");
        var marked = Abs("repo", "App", "obj", "Debug", "net8.0");
        var parents = DirExt.EmptyParentCandidates(marked, new[] { proj });
        Assert.Equal(new[] { Abs("repo", "App", "obj", "Debug") }, parents);
    }

    [Fact]
    public void EmptyParentCandidates_StopsAtTheConfigurationDirectory() {
        // The whole bin/Debug is the marked directory, so bin (the root) is all that is above it.
        var proj = Abs("repo", "App");
        var marked = Abs("repo", "App", "bin", "Debug");
        Assert.Empty(DirExt.EmptyParentCandidates(marked, new[] { proj }));
    }

    [Fact]
    public void EmptyParentCandidates_DoesNotClimbIntoAnArtifactsRootOrAboveTheProject() {
        // The artifacts pivot sits at artifacts/bin/App/Debug_net8.0; its parent is the artifacts root,
        // which is not below the project directory, so nothing is offered for removal.
        var proj = Abs("repo", "src", "App");
        var marked = Abs("repo", "artifacts", "bin", "App", "Debug_net8.0");
        Assert.Empty(DirExt.EmptyParentCandidates(marked, new[] { proj }));
    }

    // ----- MeasureTree (A1.5) --------------------------------------------------------------------

    [Fact]
    public void MeasureTree_DoesNotFollowASymlinkOutOfTheTree() {
        if (OperatingSystem.IsWindows()) return; // junctions need elevation; the Linux link is enough.
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bld_measure_" + Guid.NewGuid().ToString("N"))).FullName;
        try {
            var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
            File.WriteAllBytes(Path.Combine(outside, "big.bin"), new byte[4096]);

            var bin = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            File.WriteAllBytes(Path.Combine(bin, "app.dll"), new byte[10]);
            Directory.CreateSymbolicLink(Path.Combine(bin, "link"), outside);

            var (bytes, count) = new DirectoryInfo(bin).MeasureTree();
            // Only app.dll is counted; the symlinked tree (big.bin) is skipped.
            Assert.Equal(10, bytes);
            Assert.Equal(1, count);
        }
        finally {
            Directory.Delete(root, recursive: true);
        }
    }
}
