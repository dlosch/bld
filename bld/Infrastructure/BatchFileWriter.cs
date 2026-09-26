using System.Text;

namespace bld.Infrastructure;

internal interface IBatchFileWriter {
    void Append(string dir);
    void AppendFile(string fileName);
    string GetResult();
}

internal static class BatchFileWriterFactory {
    public static IBatchFileWriter Create() {
        if (OperatingSystem.IsWindows()) return new WindowsBatchFileWriter();
        else return new LinuxBashBatchFileWriter();
    }
}

internal class WindowsBatchFileWriter : IBatchFileWriter {
    private readonly StringBuilder builder = new();

    public WindowsBatchFileWriter() {
        // disabledelayedexpansion keeps "!" literal. It does nothing for %VAR%, which cmd expands
        // while parsing the line, quotes or not; that is what Quote is for.
        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal disabledelayedexpansion");
    }

    public void Append(string dir) {
        builder.AppendLine($"rmdir /q /s {Quote(dir)}");
    }

    public void AppendFile(string fileName) {
        builder.AppendLine($"del {Quote(fileName)}");
    }

    /// <summary>
    /// A directory named "C:\build%TEMP%out" would otherwise expand to another path and the script
    /// would delete that one. In a batch file "%%" is a literal "%"; inside the quotes &amp;, |, &lt;, &gt;
    /// and ^ are literal already, and a Windows path cannot contain a quote.
    /// </summary>
    internal static string Quote(string path) => "\"" + path.Replace("%", "%%") + "\"";

    public string GetResult() => builder.ToString();
}


internal class LinuxBashBatchFileWriter : IBatchFileWriter {
    private readonly StringBuilder builder = new();

    /// <summary>
    /// Single-quotes a path for bash. Inside double quotes bash still expands $, ` and \, so a
    /// directory named `proj$(id -un)` produced a script that deleted a different path — or, with
    /// backticks, executed arbitrary commands. Single quotes suppress every expansion; the only
    /// character needing care is the single quote itself.
    /// </summary>
    internal static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    // The shebang makes ./clean.sh run under sh whatever shell starts it; single-quoted rm lines are
    // plain POSIX. A cleanup should remove what it can, so a failed rm (a locked or root-owned
    // directory) does not stop the lines after it the way set -e would; it is remembered and the
    // script exits non-zero at the end instead of reporting success. "--" keeps a path from being
    // read as an option.
    private const string Header = "#!/bin/sh\nset -u\nstatus=0\n";
    private const string Footer = "exit $status\n";

    public void Append(string dir) {
        builder.Append($"rm -rf -- {Quote(dir)} || status=1\n");
    }

    // -f: a package file that is already gone is not a failure.
    public void AppendFile(string fileName) {
        builder.Append($"rm -f -- {Quote(fileName)} || status=1\n");
    }

    public string GetResult() => builder.Length == 0 ? string.Empty : Header + builder + Footer;
}
