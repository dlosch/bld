using bld.Infrastructure;
using bld.Models;

namespace bld.Tests;

public class ProjCfgCacheTests {

    [Fact]
    public void Add_ShouldReturnTrue_ForDifferentConfigurations() {
        // Arrange
        var console = new TestConsole();
        var cache = new ProjCfgCache(console);
        var proj = new Proj("C:\\Project1.csproj", null);
        var debugCfg = new ProjCfg(proj, "Debug");
        var releaseCfg = new ProjCfg(proj, "Release");

        // Act
        var result1 = cache.Add(debugCfg);
        var result2 = cache.Add(releaseCfg);

        // Assert
        Assert.True(result1);
        Assert.True(result2);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Add_ShouldReturnFalse_ForSameConfiguration() {
        // Arrange
        var console = new TestConsole();
        var cache = new ProjCfgCache(console);
        var proj = new Proj("C:\\Project1.csproj", null);
        var cfg1 = new ProjCfg(proj, "Debug");
        var cfg2 = new ProjCfg(proj, "Debug");

        // Act
        var result1 = cache.Add(cfg1);
        var result2 = cache.Add(cfg2);

        // Assert
        Assert.True(result1);
        Assert.False(result2);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Add_ShouldReturnFalse_ForSameConfigurationDifferentCasing() {
        // Arrange
        var console = new TestConsole();
        var cache = new ProjCfgCache(console);
        var proj = new Proj("C:\\Project1.csproj", null);
        var cfg1 = new ProjCfg(proj, "Debug");
        var cfg2 = new ProjCfg(proj, "debug");

        // Act
        var result1 = cache.Add(cfg1);
        var result2 = cache.Add(cfg2);

        // Assert
        Assert.True(result1);
        Assert.False(result2);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Add_ShouldReturnFalse_ForNullVsReleaseConfiguration() {
        // Arrange
        var console = new TestConsole();
        var cache = new ProjCfgCache(console);
        var proj = new Proj("C:\\Project1.csproj", null);
        var cfg1 = new ProjCfg(proj, null);
        var cfg2 = new ProjCfg(proj, "Release");

        // Act
        var result1 = cache.Add(cfg1);
        var result2 = cache.Add(cfg2);

        // Assert
        Assert.True(result1);
        Assert.False(result2, "Null configuration should be treated the same as 'Release' string configuration");
        Assert.Equal(1, cache.Count);
    }
}
