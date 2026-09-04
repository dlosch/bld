using bld.Infrastructure;

namespace bld.Tests;

public class NuGetAssemblyResolverTests {
    private static Version? VersionOf(string path) => path switch {
        "bundled/NuGet.Frameworks.dll" => new Version(7, 9, 0, 0),
        "sdk/NuGet.Frameworks.dll" => new Version(7, 6, 0, 0),
        "newer-sdk/NuGet.Frameworks.dll" => new Version(8, 0, 0, 0),
        _ => null
    };

    [Fact]
    public void PicksBundledWhenSdkIsOlder() {
        var picked = NuGetAssemblyResolver.PickNewest(["bundled/NuGet.Frameworks.dll", "sdk/NuGet.Frameworks.dll"], VersionOf);
        Assert.Equal("bundled/NuGet.Frameworks.dll", picked);
    }

    [Fact]
    public void PicksSdkWhenSdkIsNewer() {
        var picked = NuGetAssemblyResolver.PickNewest(["bundled/NuGet.Frameworks.dll", "newer-sdk/NuGet.Frameworks.dll"], VersionOf);
        Assert.Equal("newer-sdk/NuGet.Frameworks.dll", picked);
    }

    [Fact]
    public void PicksFirstOnTie() {
        var picked = NuGetAssemblyResolver.PickNewest(["bundled/NuGet.Frameworks.dll", "bundled/NuGet.Frameworks.dll"], VersionOf);
        Assert.Equal("bundled/NuGet.Frameworks.dll", picked);
    }

    [Fact]
    public void ReturnsNullWithoutCandidates() {
        Assert.Null(NuGetAssemblyResolver.PickNewest([], VersionOf));
    }
}
