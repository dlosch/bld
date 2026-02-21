namespace bld.Infrastructure;

internal static class MarkdownTableFormatter {
    public static void Write(IConsoleOutput console, string caption, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows) {
        var lines = new List<string> {
            "|" + string.Join("|", headers.Select(EscapeCell)) + "|",
            "|" + string.Join("|", headers.Select(_ => "---")) + "|"
        };

        foreach (var row in rows) {
            var cells = row.Select(EscapeCell).ToList();
            while (cells.Count < headers.Count) {
                cells.Add(string.Empty);
            }
            lines.Add("|" + string.Join("|", cells.Take(headers.Count)) + "|");
        }

        console.WriteOutput(caption, string.Join(Environment.NewLine, lines));
    }

    private static string EscapeCell(string? value) {
        if (string.IsNullOrEmpty(value)) {
            return string.Empty;
        }

        return value
            .Replace("|", "\\|")
            .Replace("\r\n", "<br>")
            .Replace("\n", "<br>")
            .Replace("\r", "<br>");
    }
}
