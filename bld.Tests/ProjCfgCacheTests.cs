using bld.Infrastructure;
using bld.Models;
using Xunit;
using Spectre.Console;

namespace bld.Tests;

public class ProjCfgCacheTests {
    private class DummyConsole : IConsoleOutput {
        public void WriteLine(string message) { }
        public void WriteInfo(string message) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message, Exception? exception = default) { }
        public void WriteDebug(string message) { }
        public void WriteVerbose(string message) { }
        public void WriteTable(Table table) { }
        public void WriteRule(string title) { }
        public bool Confirm(string message, bool defaultValue = false) => defaultValue;
        public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => default!;
        public void StartProgress(string description, Action<ProgressContext> action) { }
        public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => Task.CompletedTask;
        public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => Task.CompletedTask;
        public void WriteException(Exception exception) { }
        public void WriteOutput(string caption, string? content = default) { }
        public void WriteHeader(string caption, string? additionaltext = default) { }
    }

    [Fact]
    public void Add_ShouldReturnTrue_ForDifferentConfigurations() {
        // Arrange
        var console = new DummyConsole();
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
        var console = new DummyConsole();
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
        var console = new DummyConsole();
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
        var console = new DummyConsole();
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
