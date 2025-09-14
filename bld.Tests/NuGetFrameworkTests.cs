//using XUnit.Framework;

using bld.Infrastructure;
using NuGet.Frameworks;
using Xunit.Abstractions;

namespace bld.Tests;

public class DotNetTests(ITestOutputHelper Console) {
    [Fact]
    public void PathCombineLinux() {
        Assert.Throws<ArgumentNullException>(() => Path.Combine("/mnt/d/tests", null!, "child"));
    }

    [Fact]
    public void PathCombineWin() {
        Assert.Throws<ArgumentNullException>(() => Path.Combine("d:\\tests", null!, "child"));
    }
}
public class NuGetFrameworkTests(ITestOutputHelper Console) {

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
            "net10.0",
            "net10",
            "net100",
            "net9",
            "net9000",
            "net472x",
        };

    [Fact]
    public void Test1() {


        foreach (var tfm in tfms) {
            var framework = NuGetFramework.Parse(tfm);
            string fx = framework.Framework;
            string normalizedTfm = framework.GetShortFolderName();

            Console.WriteLine($"Original: {tfm}, Fx '{fx}' Standard: '{normalizedTfm}'");

            // Test that our normalization handles the net100 -> net10.0 case
            Assert.NotEqual("net100", normalizedTfm);

        }
    }
}
