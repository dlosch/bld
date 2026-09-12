using bld.Infrastructure;
using System.Text;

namespace bld.Tests;

/// <summary>
/// global.json handling for `bld tfm`: which file applies, whether its pin can build the target TFM,
/// and a rewrite that touches only sdk.version.
/// </summary>
public class GlobalJsonFileTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bld_gj_" + Guid.NewGuid().ToString("N"));

    public GlobalJsonFileTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public void Find_WalksUpToTheNearestGlobalJson() {
        var nested = Path.Combine(_root, "src", "App");
        Directory.CreateDirectory(nested);
        var expected = Path.Combine(_root, "global.json");
        File.WriteAllText(expected, "{}");

        Assert.Equal(expected, GlobalJsonFile.Find(nested));

        var closer = Path.Combine(_root, "src", "global.json");
        File.WriteAllText(closer, "{}");
        Assert.Equal(closer, GlobalJsonFile.Find(nested));
    }

    [Fact]
    public void Read_ReturnsSdkSection_OrNullWithoutOne() {
        var path = Path.Combine(_root, "global.json");
        File.WriteAllText(path, """
            {
              // comment
              "sdk": { "version": "8.0.100", "rollForward": "latestFeature", "allowPrerelease": true },
              "msbuild-sdks": { "MSBuild.Sdk.Extras": "3.0.44" },
            }
            """);

        var sdk = GlobalJsonFile.Read(path);

        Assert.NotNull(sdk);
        Assert.Equal("8.0.100", sdk!.Version);
        Assert.Equal("latestFeature", sdk.RollForward);
        Assert.True(sdk.AllowPrerelease);

        File.WriteAllText(path, """{ "msbuild-sdks": { } }""");
        Assert.Null(GlobalJsonFile.Read(path));
    }

    [Theory]
    [InlineData("net10.0", 10)]
    [InlineData("net8.0-windows", 8)]
    [InlineData("netstandard2.0", null)]
    [InlineData("net48", null)]
    public void RequiredSdkMajor_ParsesModernTfmsOnly(string tfm, int? expected) {
        Assert.Equal(expected, GlobalJsonFile.RequiredSdkMajor(tfm));
    }

    [Theory]
    [InlineData(null, null, "Ok")]
    [InlineData("10.0.100", null, "Ok")]
    [InlineData("11.0.100", "disable", "Ok")]
    [InlineData("8.0.100", null, "Blocks")]
    [InlineData("8.0.100", "latestPatch", "Blocks")]
    [InlineData("8.0.100", "latestFeature", "Blocks")]
    [InlineData("8.0.100", "latestMinor", "Blocks")]
    [InlineData("8.0.100", "disable", "Blocks")]
    [InlineData("8.0.100", "major", "MayRollForward")]
    [InlineData("8.0.100", "latestMajor", "Ok")]
    public void Evaluate_DecidesByPinnedMajorAndRollForward(string? version, string? rollForward, string expected) {
        var (verdict, reason) = GlobalJsonFile.Evaluate(new GlobalJsonSdk(version, rollForward, null), requiredMajor: 10);

        Assert.Equal(Enum.Parse<GlobalJsonVerdict>(expected), verdict);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task WriteVersion_ChangesOnlyTheVersion_AndKeepsIndentationLineEndingsAndBom() {
        var path = Path.Combine(_root, "global.json");
        var original = "{\r\n    \"sdk\": {\r\n        \"version\": \"8.0.100\",\r\n        \"rollForward\": \"latestFeature\"\r\n    },\r\n    \"msbuild-sdks\": {\r\n        \"MSBuild.Sdk.Extras\": \"3.0.44\"\r\n    }\r\n}\r\n";
        await File.WriteAllBytesAsync(path, Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(original)).ToArray());

        await GlobalJsonFile.WriteVersionAsync(path, "10.0.301", default);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes.Take(3));
        var text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.Equal(original.Replace("8.0.100", "10.0.301"), text);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public async Task WriteVersion_TwoSpaceIndentWithLf_IsPreserved() {
        var path = Path.Combine(_root, "global.json");
        var original = "{\n  \"sdk\": {\n    \"version\": \"8.0.100\"\n  }\n}\n";
        await File.WriteAllTextAsync(path, original);

        await GlobalJsonFile.WriteVersionAsync(path, "10.0.301", default);

        Assert.Equal(original.Replace("8.0.100", "10.0.301"), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteVersion_KeepsCommentsAndOtherVersionProperties() {
        var path = Path.Combine(_root, "global.json");
        var original = """
            {
              // keep me
              "sdk": {
                "version": "8.0.100", /* and me */
                "rollForward": "latestFeature"
              },
              "msbuild-sdks": {
                "MSBuild.Sdk.Extras": { "version": "3.0.44" }
              }
            }
            """;
        await File.WriteAllTextAsync(path, original);

        await GlobalJsonFile.WriteVersionAsync(path, "10.0.301", default);

        Assert.Equal(original.Replace("\"8.0.100\"", "\"10.0.301\""), await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task WriteVersion_WithoutSdkVersion_Throws() {
        var path = Path.Combine(_root, "global.json");
        await File.WriteAllTextAsync(path, "{ \"sdk\": { \"rollForward\": \"latestMajor\" }, \"msbuild-sdks\": { \"X\": \"1.0.0\" } }");

        await Assert.ThrowsAsync<InvalidOperationException>(() => GlobalJsonFile.WriteVersionAsync(path, "10.0.301", default));
        Assert.Equal("{ \"sdk\": { \"rollForward\": \"latestMajor\" }, \"msbuild-sdks\": { \"X\": \"1.0.0\" } }", await File.ReadAllTextAsync(path));
    }
}
