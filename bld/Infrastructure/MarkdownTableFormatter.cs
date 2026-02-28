namespace bld.Infrastructure;

internal static class MarkdownTableFormatter {
    public static void Write(IConsoleOutput console, string caption, IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string?>> rows) {
        var materializedRows = rows.ToList();

        // Calculate column widths for aligned output
        var columnWidths = new int[headers.Count];
        for (int i = 0; i < headers.Count; i++) {
            columnWidths[i] = headers[i].Length;
        }
        foreach (var row in materializedRows) {
            for (int i = 0; i < headers.Count && i < row.Count; i++) {
                var cellLen = EscapeCell(row[i]).Length;
                if (cellLen > columnWidths[i]) columnWidths[i] = cellLen;
            }
        }

        static string PadCell(string value, int width) => value + new string(' ', Math.Max(0, width - value.Length));

        var lines = new List<string> {
            "| " + string.Join(" | ", headers.Select((h, i) => PadCell(EscapeCell(h), columnWidths[i]))) + " |",
            "| " + string.Join(" | ", columnWidths.Select(w => new string('-', Math.Max(3, w)))) + " |"
        };

        foreach (var row in materializedRows) {
            var cells = new List<string>();
            for (int i = 0; i < headers.Count; i++) {
                var value = i < row.Count ? EscapeCell(row[i]) : string.Empty;
                cells.Add(PadCell(value, columnWidths[i]));
            }
            lines.Add("| " + string.Join(" | ", cells) + " |");
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
