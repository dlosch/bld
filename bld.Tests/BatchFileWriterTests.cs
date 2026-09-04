using bld.Infrastructure;

namespace bld.Tests;

/// <summary>
/// The generated script is executed by the user against real directories, so a path that the
/// shell re-interprets means deleting something other than what was previewed.
/// </summary>
public class BatchFileWriterTests {

    [Theory]
    [InlineData("/home/u/proj$(id -un)/bin")]
    [InlineData("/home/u/proj`whoami`/bin")]
    [InlineData("/home/u/proj$HOME/bin")]
    [InlineData(@"/home/u/back\slash/bin")]
    public void Bash_QuotesPathsSoTheShellCannotExpandThem(string dir) {
        var writer = new LinuxBashBatchFileWriter();
        writer.Append(dir);
        var script = writer.GetResult();

        Assert.Equal($"rm -rf '{dir}'\n", script.Replace("\r\n", "\n"));
        Assert.DoesNotContain("\"", script);
    }

    [Fact]
    public void Bash_EscapesEmbeddedSingleQuote() {
        var writer = new LinuxBashBatchFileWriter();
        writer.Append("/tmp/it's here/bin");

        Assert.Contains(@"rm -rf '/tmp/it'\''s here/bin'", writer.GetResult());
    }

    [Fact]
    public void Windows_DisablesVariableExpansion() {
        var writer = new WindowsBatchFileWriter();
        writer.Append(@"C:\50%off%20\bin");
        var script = writer.GetResult();

        // Without disabling expansion cmd rewrites %off% to nothing and deletes C:\5020\bin.
        Assert.Contains("setlocal disabledelayedexpansion", script);
        Assert.Contains(@"rmdir /q /s ""C:\50%off%20\bin""", script);
    }
}
