//using XUnit.Framework;

using NuGet.Frameworks;
using Xunit.Abstractions;
using System.Reflection;

namespace bld.Tests;

public class DotNetTests(/*ITestOutputHelper Console*/) {
    [Fact]
    public void PathCombineLinux() {
        Assert.Throws<ArgumentNullException>(() => Path.Combine("/mnt/d/tests", null!, "child"));
    }

    [Fact]
    public void PathCombineWin() {
        Assert.Throws<ArgumentNullException>(() => Path.Combine("d:\\tests", null!, "child"));
    }
}

public class TfmCommandTests {
    [Theory]
    [InlineData("net5.0", true)]
    [InlineData("net6.0", true)]
    [InlineData("net7.0", true)]
    [InlineData("net8.0", true)]
    [InlineData("net9.0", true)]
    [InlineData("net10.0", true)]
    [InlineData("net5", true)]  // Single-digit version
    [InlineData("net6", true)]  // Single-digit version
    [InlineData("netstandard2.0", false)]
    [InlineData("netstandard2.1", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData("net48", false)]
    [InlineData("net472", false)]
    [InlineData("net461", false)]
    [InlineData("NET8.0", true)]  // Test case insensitivity
    [InlineData("  net7.0  ", true)]  // Test trimming
    [InlineData("", false)]
    [InlineData(null!, false)]
    public void IsDotNetCoreFramework_ShouldFilterCorrectly(string tfm, bool expected) {
        // Use reflection to call the private static method
        var tfmCommandType = typeof(bld.Commands.TfmCommand);
        var method = tfmCommandType.GetMethod("IsDotNetCoreFramework", 
            BindingFlags.NonPublic | BindingFlags.Static);
        
        Assert.NotNull(method);
        
        var result = (bool)method.Invoke(null, new object[] { tfm })!;
        Assert.Equal(expected, result);
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
