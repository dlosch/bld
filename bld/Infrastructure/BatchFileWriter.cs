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

    public void Append(string dir) {
        if (dir.Contains('"')) builder.AppendLine($"# rm -rf \"{dir}\"");
        else builder.AppendLine($"rm -rf \"{dir}\"");
    }

    public void AppendFile(string fileName) {
        if (fileName.Contains('"')) builder.AppendLine($"# rm \"{fileName}\"");
        else builder.AppendLine($"rm \"{fileName}\"");
    }

    public string GetResult() => builder.ToString();
}
