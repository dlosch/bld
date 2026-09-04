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
        // Without this, cmd expands %VAR% inside the quoted path: a directory named "50%off%20"
        // would be rewritten to "5020" and the script would delete a different directory.
        builder.AppendLine("@echo off");
        builder.AppendLine("setlocal disabledelayedexpansion");
    }

    public void Append(string dir) {
        builder.AppendLine($"rmdir /q /s \"{dir}\"");
    }

    public void AppendFile(string fileName) {
        builder.AppendLine($"del \"{fileName}\"");
    }

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

    public void Append(string dir) {
        builder.AppendLine($"rm -rf {Quote(dir)}");
    }

    public void AppendFile(string fileName) {
        builder.AppendLine($"rm {Quote(fileName)}");
    }

    public string GetResult() => builder.ToString();
}
