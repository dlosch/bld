using bld.Infrastructure;
using bld.Models;
using Spectre.Console;

namespace bld.Services;

internal class MarkDeleteResultStatsProcessor : IMarkDeleteResultProcessor {
    private readonly IConsoleOutput _console;
    private readonly ErrorSink _errorSink;
    private readonly CleaningOptions _options;
    private readonly EnumerationOptions _enumerateFiles;

    public MarkDeleteResultStatsProcessor(IConsoleOutput console, ErrorSink errorSink, CleaningOptions options) {
        _console = console;
        _errorSink = errorSink;
        _options = options;

        _enumerateFiles = new EnumerationOptions { MatchType = MatchType.Simple, MaxRecursionDepth = 10 /*options.Depth*/, RecurseSubdirectories = true, ReturnSpecialDirectories = false, IgnoreInaccessible = false };
    }

    public Task ProcessAsync(MarkDeleteResult result) {

        (long Bytes, int Count) GetSize(DirectoryInfo dirInfo) {
            var affectedFiles = dirInfo.EnumerateFiles("*", _enumerateFiles);
            // Materialize to avoid double enumeration
            var files = affectedFiles.ToArray();
            return (files.Sum(a => a.Length), files.Length);
        }

        if (!result.Directories.Any()) {
            _console.WriteLine("No directories marked for deletion.");
            return Task.CompletedTask;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("Files").RightAligned());
        table.AddColumn(new TableColumn("Size (KiB)").RightAligned());
        table.AddColumn(new TableColumn("Size (MiB)").RightAligned());
        table.AddColumn(new TableColumn("Directory").LeftAligned());
        table.AddColumn(new TableColumn("TFMs").LeftAligned());
        var markdownRows = new List<IReadOnlyList<string?>>();

        long totalBytes = 0L;
        int totalFiles = 0;

        foreach (var kvp in result.Directories.OrderBy(k => k.Directory.FullName)) {
            var path = kvp.Directory;
            if (path is null) continue;

            if (!path.Exists) continue;
            var (bytes, count) = GetSize(path);
            if (count == 0 && bytes == 0) continue; // skip empty
            totalBytes += bytes;
            totalFiles += count;

            table.AddRow(
                count.ToString(),
                (bytes / 1024d).ToString("N0"),
                (bytes / 1024d / 1024d).ToString("N2"),
                Markup.Escape(path.FullName),
                "" + string.Join(", ", kvp.References?.SelectMany(d => d.Tfms).Distinct() ?? Array.Empty<string>()) + " "
                );

            markdownRows.Add(new[] {
                count.ToString(),
                (bytes / 1024d).ToString("N0"),
                (bytes / 1024d / 1024d).ToString("N2"),
                path.FullName,
                string.Join(", ", kvp.References?.SelectMany(d => d.Tfms).Distinct() ?? Array.Empty<string>())
            });
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

            if (_options.MarkdownOutput) {
                markdownRows.Add(new[] {
                    totalFiles.ToString(),
                    (totalBytes / 1024d).ToString("N0"),
                    (totalBytes / 1024d / 1024d).ToString("N2"),
                    "Total",
                    string.Empty
                });

                MarkdownTableFormatter.Write(_console, "Stats (markdown)", new[] { "Files", "Size (KiB)", "Size (MiB)", "Directory", "TFMs" }, markdownRows);
            }
            else {
                _console.WriteTable(table);
            }
        }

        return Task.CompletedTask;
    }
}
