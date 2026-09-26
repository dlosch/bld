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

        Assert.Equal($"#!/bin/sh\nset -u\nstatus=0\nrm -rf -- '{dir}' || status=1\nexit $status\n", script);
        Assert.DoesNotContain("\"", script);
    }

    [Fact]
    public void Bash_EscapesEmbeddedSingleQuote() {
        var writer = new LinuxBashBatchFileWriter();
        writer.Append("/tmp/it's here/bin");

        Assert.Contains(@"rm -rf -- '/tmp/it'\''s here/bin'", writer.GetResult());
    }

    [Fact]
    public void Bash_ScriptHasAShebangAndToleratesGoneFiles() {
        var writer = new LinuxBashBatchFileWriter();
        Assert.Equal("", writer.GetResult());

        writer.AppendFile("/pkgs/My.Lib.1.0.0.nupkg");
        var script = writer.GetResult();

        Assert.StartsWith("#!/bin/sh\n", script);
        Assert.Contains("rm -f -- '/pkgs/My.Lib.1.0.0.nupkg' || status=1\n", script);
        Assert.DoesNotContain("\r", script);
    }

    /// <summary>
    /// A cleanup removes what it can: one directory that cannot be deleted must not stop the lines after
    /// it (as set -e did), but the script still has to report the failure through its exit code.
    /// </summary>
    [Fact]
    public void Bash_ScriptKeepsGoingPastAFailureAndExitsNonZero() {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "bld_bfw_" + Guid.NewGuid().ToString("N"));
        var locked = Path.Combine(dir, "locked");
        var inner = Path.Combine(locked, "inner");
        var later = Path.Combine(dir, "later");
        Directory.CreateDirectory(inner);
        Directory.CreateDirectory(later);
        File.WriteAllText(Path.Combine(inner, "f"), "");
        // Removing entries from a directory without write permission fails, as for a root-owned one.
        File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try {
            var writer = new LinuxBashBatchFileWriter();
            writer.Append(inner);
            writer.Append(later);
            var script = Path.Combine(dir, "clean.sh");
            bld.Services.MarkDeleteResultBatchFileProcessor.WriteScript(script, writer.GetResult());

            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(script) { RedirectStandardError = true })!;
            process.WaitForExit();

            Assert.NotEqual(0, process.ExitCode);
            Assert.True(Directory.Exists(inner));
            Assert.False(Directory.Exists(later));
        }
        finally {
            File.SetUnixFileMode(locked, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Regression: the script was written 0644 when new, so ./clean.sh failed with "Permission denied".</summary>
    [Fact]
    public void Script_IsWrittenExecutable_NewOrOverwritten() {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(Path.GetTempPath(), "bld_bfw_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            var fresh = Path.Combine(dir, "clean.sh");
            bld.Services.MarkDeleteResultBatchFileProcessor.WriteScript(fresh, "#!/bin/sh\n");
            Assert.True(File.GetUnixFileMode(fresh).HasFlag(UnixFileMode.UserExecute));

            var existing = Path.Combine(dir, "old.sh");
            File.WriteAllText(existing, "old");
            File.SetUnixFileMode(existing, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            bld.Services.MarkDeleteResultBatchFileProcessor.WriteScript(existing, "#!/bin/sh\n");
            var mode = File.GetUnixFileMode(existing);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, mode);
            Assert.Equal("#!/bin/sh\n", File.ReadAllText(existing));
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("clean.sh", "./clean.sh")]
    [InlineData("out/clean.sh", "out/clean.sh")]
    [InlineData("/tmp/clean.sh", "/tmp/clean.sh")]
    public void RunCommand_PrefixesABareNameOnUnix(string path, string expected) {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(expected, bld.Services.MarkDeleteResultBatchFileProcessor.RunCommand(path));
    }

    /// <summary>
    /// Regression: the script relied on setlocal disabledelayedexpansion, which only covers !VAR!.
    /// cmd expands %TEMP% while parsing the line, so C:\build%TEMP%out\bin became another path.
    /// </summary>
    [Fact]
    public void Windows_DoublesPercentSoCmdCannotExpandVariables() {
        var writer = new WindowsBatchFileWriter();
        writer.Append(@"C:\build%TEMP%out\bin");
        writer.AppendFile(@"C:\pkgs\50%off.1.0.0.nupkg");
        var script = writer.GetResult();

        Assert.Contains("setlocal disabledelayedexpansion", script);
        Assert.Contains(@"rmdir /q /s ""C:\build%%TEMP%%out\bin""", script);
        Assert.Contains(@"del ""C:\pkgs\50%%off.1.0.0.nupkg""", script);
    }
}
