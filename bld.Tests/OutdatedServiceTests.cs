using bld.Infrastructure;
using bld.Services;
using bld.Services.NuGet;
using Spectre.Console;

namespace bld.Tests;

/// <summary>
/// Tests for PackageInfoComparer null-safety and correctness.
/// </summary>
public class PackageInfoComparerTests {
    private readonly OutdatedService.PackageInfoComparer _comparer = new();

    private static OutdatedService.PackageInfo MakeInfo(string id, string version, string projectPath, string[]? tfms, string? propsPath = null, bool fromProps = false) {
        return new OutdatedService.PackageInfo {
            Id = id,
            Item = new Pkg(id, version),
            ProjectPath = projectPath,
            TargetFramework = tfms?.FirstOrDefault()!,
            TargetFrameworks = tfms!,
            PropsPath = propsPath,
            FromProps = fromProps,
        };
    }

    [Fact]
    public void Equals_BothNullTargetFrameworks_ReturnsTrue() {
        var a = MakeInfo("Pkg", "1.0.0", "/proj.csproj", null);
        var b = MakeInfo("Pkg", "1.0.0", "/proj.csproj", null);
        Assert.True(_comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_OneNullOneNonNullTargetFrameworks_ReturnsFalse() {
        var a = MakeInfo("Pkg", "1.0.0", "/proj.csproj", null);
        var b = MakeInfo("Pkg", "1.0.0", "/proj.csproj", ["net8.0"]);
        Assert.False(_comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_SameTargetFrameworks_ReturnsTrue() {
        var a = MakeInfo("Pkg", "1.0.0", "/proj.csproj", ["net8.0", "net9.0"]);
        var b = MakeInfo("Pkg", "1.0.0", "/proj.csproj", ["net8.0", "net9.0"]);
        Assert.True(_comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_DifferentTargetFrameworks_ReturnsFalse() {
        var a = MakeInfo("Pkg", "1.0.0", "/proj.csproj", ["net8.0"]);
        var b = MakeInfo("Pkg", "1.0.0", "/proj.csproj", ["net9.0"]);
        Assert.False(_comparer.Equals(a, b));
    }

    [Fact]
    public void Equals_NullEntries_ReturnsFalse() {
        var a = MakeInfo("Pkg", "1.0.0", "/proj.csproj", ["net8.0"]);
        Assert.False(_comparer.Equals(a, null));
        Assert.False(_comparer.Equals(null, a));
    }

    [Fact]
    public void Equals_BothNull_ReturnsTrue_ViaReferenceEquals() {
        // Both null references should return true (via ReferenceEquals)
        Assert.True(_comparer.Equals(null, null));
    }

    [Fact]
    public void GetHashCode_NullTargetFrameworks_DoesNotThrow() {
        var info = MakeInfo("Pkg", "1.0.0", "/proj.csproj", null);
        var hash = _comparer.GetHashCode(info);
        Assert.NotEqual(0, hash);
    }
}

/// <summary>
/// Tests for MarkdownTableFormatter output formatting.
/// </summary>
public class MarkdownTableFormatterTests {
    private class RecordingConsole : IConsoleOutput {
        public List<(string Caption, string? Content)> Outputs { get; } = new();
        public void WriteInfo(string message) { }
        public void WriteWarning(string message) { }
        public void WriteError(string message, Exception? exception = default) { }
        public void WriteDebug(string message) { }
        public void WriteVerbose(string message) { }
        public void WriteTable(Table table) { }
        public void WriteRule(string title) { }
        public bool Confirm(string message, bool defaultValue = false) => false;
        public T Prompt<T>(SelectionPrompt<T> prompt) where T : notnull => default!;
        public void StartProgress(string description, Action<ProgressContext> action) { }
        public Task StartProgressAsync(string description, Func<ProgressContext, Task> action) => Task.CompletedTask;
        public Task StartStatusAsync(string description, Func<StatusContext, Task> action) => Task.CompletedTask;
        public void WriteException(Exception exception) { }
        public void WriteOutput(string caption, string? content = default) => Outputs.Add((caption, content));
        public void WriteHeader(string caption, string? additionaltext = default) { }
    }

    [Fact]
    public void Write_ProducesAlignedColumns() {
        var console = new RecordingConsole();
        var headers = new[] { "Package", "Current", "Latest" };
        var rows = new List<IReadOnlyList<string?>> {
            new[] { "Short", "1.0.0", "2.0.0" },
            new[] { "VeryLongPackageName", "1.0.0", "10.0.0" },
        };

        MarkdownTableFormatter.Write(console, "test", headers, rows);

        Assert.Single(console.Outputs);
        var content = console.Outputs[0].Content!;
        var lines = content.Split(Environment.NewLine);

        // Header, separator, and 2 data rows
        Assert.Equal(4, lines.Length);

        // All lines should have the same pipe-delimited structure
        foreach (var line in lines) {
            Assert.StartsWith("|", line);
            Assert.EndsWith("|", line);
        }

        // Separator line should use dashes
        Assert.Contains("---", lines[1]);
    }

    [Fact]
    public void Write_EscapesPipeCharacters() {
        var console = new RecordingConsole();
        var headers = new[] { "Col1" };
        var rows = new List<IReadOnlyList<string?>> {
            new[] { "value|with|pipes" },
        };

        MarkdownTableFormatter.Write(console, "test", headers, rows);

        var content = console.Outputs[0].Content!;
        Assert.Contains("value\\|with\\|pipes", content);
    }

    [Fact]
    public void Write_HandlesEmptyCells() {
        var console = new RecordingConsole();
        var headers = new[] { "A", "B", "C" };
        var rows = new List<IReadOnlyList<string?>> {
            new[] { "x", null, "" },
        };

        MarkdownTableFormatter.Write(console, "test", headers, rows);

        Assert.Single(console.Outputs);
        Assert.NotNull(console.Outputs[0].Content);
    }
}

/// <summary>
/// Tests for NuGetMetadataService framework-check version resolution.
/// </summary>
public class NuGetMetadataServiceTests {

    [Theory]
    [InlineData("Newtonsoft.Json", "net8.0")]
    [InlineData("Newtonsoft.Json", "net10.0")]
    public async Task GetLatestVersion_FindsStableVersionForCommonPackages(string packageId, string tfm) {
        var options = new NugetMetadataOptions { MaxParallelRequests = 1 };
        using var client = NugetMetadataService.CreateHttpClient(options);

        var request = new PackageVersionRequest {
            PackageId = packageId,
            AllowPrerelease = false,
            CompatibleTargetFrameworks = [tfm]
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, null, request);

        Assert.NotNull(result);
        Assert.NotEmpty(result.TargetFrameworkVersions);
        Assert.False(result.IsPrerelease, "Should find a stable version for Newtonsoft.Json");
    }

    [Theory]
    [InlineData("Microsoft.Extensions.Logging", "net8.0")]
    public async Task GetLatestVersion_DoesNotSkipStableVersions(string packageId, string tfm) {
        var options = new NugetMetadataOptions { MaxParallelRequests = 1 };
        using var client = NugetMetadataService.CreateHttpClient(options);

        var request = new PackageVersionRequest {
            PackageId = packageId,
            AllowPrerelease = false,
            CompatibleTargetFrameworks = [tfm]
        };

        var result = await NugetMetadataService.GetLatestVersionWithFrameworkCheckAsync(client, options, null, request);

        Assert.NotNull(result);
        Assert.NotEmpty(result.TargetFrameworkVersions);
        // The result should be stable (not prerelease), proving we didn't prematurely switch to prerelease
        Assert.False(result.IsPrerelease, "Should prefer stable versions over prerelease");
    }
}
