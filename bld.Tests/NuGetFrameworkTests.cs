//using XUnit.Framework;

using NuGet.Frameworks;
using Xunit.Abstractions;

namespace bld.Tests;

public class DotNetTests(ITestOutputHelper Console) {
    [Fact]
    public void PathCombineLinux() {
        Assert.Throws<ArgumentNullException>(() => Path.Combine("/mnt/d/tests", null, "child"));
    }

    [Fact]
    public void PathCombineWin() {
        Assert.Throws<ArgumentNullException>(() => Path.Combine("d:\\tests", null, "child"));
    }
}
public class NuGetFrameworkTests(ITestOutputHelper Console) {
    [Fact]
    public void Test1() {
        string[] tfms = new[]
        {
            ".NETStandard,Version=v2.0",
            ".NETFramework,Version=v4.7.2",
            ".NETCoreApp,Version=v8.0",
            "netstandard2.1",
            "net6.0",


            ".NETFramework4.6.2",
            "net8.0",
            "net9.0",
            "net9",
            "net9000",
            "net472x",
        };

        foreach (var tfm in tfms) {
            var framework = NuGetFramework.Parse(tfm);
            string normalizedTfm = framework.GetShortFolderName();
            Console.WriteLine($"Original: {tfm}, Normalized: {normalizedTfm}");
        }
    }
}
