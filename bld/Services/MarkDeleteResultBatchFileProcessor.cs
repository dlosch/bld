using bld.Infrastructure;
using bld.Models;
using Spectre.Console;

namespace bld.Services;

internal class MarkDeleteResultBatchFileProcessor : IMarkDeleteResultProcessor {
    private readonly IConsoleOutput _console;
    private readonly ErrorSink _errorSink;
    private readonly CleaningOptions _options;
    private readonly EnumerationOptions _enumerateFiles;

    public MarkDeleteResultBatchFileProcessor(IConsoleOutput console, ErrorSink errorSink, CleaningOptions options) {
        _console = console;
        _errorSink = errorSink;
        _options = options;

        _enumerateFiles = new EnumerationOptions { MatchType = MatchType.Simple, RecurseSubdirectories = true, ReturnSpecialDirectories = false, IgnoreInaccessible = true };
    }

    public Task ProcessAsync(MarkDeleteResult result) {

        var writer = BatchFileWriterFactory.Create();

        (long Bytes, int Count) GetSize(DirectoryInfo dirInfo) {
            var affectedFiles = dirInfo.EnumerateFiles("*", _enumerateFiles);
            // Materialize to avoid double enumeration
            var files = affectedFiles.ToArray();
            return (files.Sum(a => a.Length), files.Length);
        }

        if (result.IsEmpty) {
            _console.WriteLine("No directories marked for deletion.");
            return Task.CompletedTask;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Files").RightAligned());
        table.AddColumn(new TableColumn("Size (KiB)").RightAligned());
        table.AddColumn(new TableColumn("Size (MiB)").RightAligned());
        table.AddColumn(new TableColumn("Directory").LeftAligned());

        long totalBytes = 0L;
        int totalFiles = 0;

        foreach (var kvp in result.Directories.OrderBy(k => k.Directory.FullName)) {
            var path = kvp.Directory;
            if (path is null) continue;

            if (!path.Exists) continue;
            var (bytes, count) = GetSize(path);
            totalBytes += bytes;
            totalFiles += count;

            writer.Append(path.FullName);
            table.AddRow(
                count.ToString(),
                (bytes / 1024d).ToString("N0"),
                (bytes / 1024d / 1024d).ToString("N2"),
                Markup.Escape(path.FullName)
                );
        }

        // Package files are deleted one by one; their directory is shared and stays.
        foreach (var entry in result.Files.OrderBy(f => f.File.FullName)) {
            var file = entry.File;
            file.Refresh();
            if (!file.Exists) continue;
            totalBytes += file.Length;
            totalFiles += 1;

            writer.AppendFile(file.FullName);
            table.AddRow(
                "1",
                (file.Length / 1024d).ToString("N0"),
                (file.Length / 1024d / 1024d).ToString("N2"),
                Markup.Escape(file.FullName)
                );
        }

        if (totalFiles == 0 && totalBytes == 0) {
            _console.WriteLine("No files found in marked directories.");
        }
        else {
            // Summary row
            table.AddEmptyRow();
            table.AddRow(

                totalFiles.ToString(),
                (totalBytes / 1024d).ToString("N0"),
                (totalBytes / 1024d / 1024d).ToString("N2")
                , "[bold]Total[/]");
            _console.WriteTable(table);
        }

        if (writer.GetResult() is string batch && batch.Length > 0) {
            if (_options.OutputFile is { } && (!File.Exists(_options.OutputFile) || _console.Confirm($"File exists: {_options.OutputFile}. Overwrite?"))) {
                _console.WriteInfo($"Writing batch file {_options.OutputFile}");
                WriteScript(_options.OutputFile, batch);
                _console.WriteLine($"Wrote {_options.OutputFile}. Review it, then run: {RunCommand(_options.OutputFile)}");
            }
            else {
                _console.WriteOutput("Results", batch);
            }
        }
        return Task.CompletedTask;
    }

    private const UnixFileMode Executable =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    /// <summary>
    /// Writes the script executable on Unix. A new file used to get 0666 minus the umask - 0644, never
    /// executable - and an overwritten one kept whatever mode it had, so ./clean.sh worked only sometimes.
    /// 0755 is still reduced by the umask. An existing file keeps its mode and gains the execute bits
    /// where it has the read bits. On a Windows drive under WSL without the metadata mount option the
    /// mode cannot change at all; every file shows as executable there anyway.
    /// </summary>
    internal static void WriteScript(string path, string content) {
        if (OperatingSystem.IsWindows()) {
            File.WriteAllText(path, content);
            return;
        }

        var existed = File.Exists(path);
        using (var stream = new FileStream(path, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = Executable }))
        using (var writer = new StreamWriter(stream)) {
            writer.Write(content);
        }
        if (existed) {
            var mode = File.GetUnixFileMode(path);
            if (mode.HasFlag(UnixFileMode.UserRead)) mode |= UnixFileMode.UserExecute;
            if (mode.HasFlag(UnixFileMode.GroupRead)) mode |= UnixFileMode.GroupExecute;
            if (mode.HasFlag(UnixFileMode.OtherRead)) mode |= UnixFileMode.OtherExecute;
            File.SetUnixFileMode(path, mode);
        }
    }

    /// <summary>How to start the script from the current directory: a bare name needs ./ on Unix.</summary>
    internal static string RunCommand(string path) =>
        OperatingSystem.IsWindows() || Path.IsPathRooted(path) || path.Contains('/') ? path : "./" + path;
}
