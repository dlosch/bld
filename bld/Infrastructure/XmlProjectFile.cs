using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace bld.Infrastructure;

/// <summary>
/// Loads an XML project/props file, lets the caller mutate it, then saves it back
/// while preserving the original formatting: indentation/whitespace, line-ending
/// style (CRLF vs LF), UTF-8 BOM, and any XML declaration. XML parsing normalizes
/// line breaks to LF, so the original newline style is restored on save. The write
/// is atomic (temp file + move) so a failure never leaves a truncated file.
/// </summary>
internal static class XmlProjectFile {
    /// <summary>
    /// Loads <paramref name="path"/>, invokes <paramref name="mutate"/>, and writes the
    /// file back only if the mutator reports a change (returns true). Returns whether the
    /// file was written.
    /// </summary>
    internal static async Task<bool> EditAsync(string path, Func<XDocument, bool> mutate, CancellationToken cancellationToken) {
        var originalBytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var hasBom = originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF;

        string originalText;
        try {
            // Decode strictly. The permissive decoder silently turned undecodable bytes into U+FFFD and
            // then wrote them back, permanently corrupting e.g. a Windows-1252 comment. Refusing to edit
            // is the safe outcome: the caller reports it and the file is left untouched.
            originalText = new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(originalBytes, hasBom ? 3 : 0, originalBytes.Length - (hasBom ? 3 : 0));
        }
        catch (DecoderFallbackException ex) {
            throw new InvalidOperationException(
                $"{path} is not valid UTF-8 and would be corrupted by rewriting it. Convert it to UTF-8 first.", ex);
        }
        var newline = originalText.Contains("\r\n") ? "\r\n" : "\n";

        XDocument doc;
        using (var reader = new StringReader(originalText)) {
            // PreserveWhitespace so only the nodes the caller changes differ on save.
            doc = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }

        if (!mutate(doc)) {
            return false;
        }

        // Capture any existing XML declaration verbatim. An XmlWriter over a StringBuilder
        // reports its backing encoding as UTF-16 and would rewrite the declaration's
        // encoding attribute, so we omit it from the writer and re-prepend the original.
        string? declaration = null;
        var trimmed = originalText.TrimStart();
        if (trimmed.StartsWith("<?xml", StringComparison.Ordinal)) {
            var end = trimmed.IndexOf("?>", StringComparison.Ordinal);
            if (end > 0) {
                declaration = trimmed.Substring(0, end + 2);
            }
        }

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, new XmlWriterSettings {
            // Don't re-indent (PreserveWhitespace already carries the layout). Newlines are
            // restored below; the declaration (if any) is handled separately above.
            Indent = false,
            OmitXmlDeclaration = true,
            NewLineHandling = NewLineHandling.None,
        })) {
            doc.Save(writer);
        }

        // XML load normalized newlines to LF; restore the file's original style and BOM.
        var body = sb.Replace("\r\n", "\n").Replace("\n", newline).ToString();
        // PreserveWhitespace already kept the whitespace that followed the declaration, so it is part of
        // `body`. Adding another newline here inserted one blank line per edit, accumulating on every run.
        var result = declaration is null ? body : declaration + body;

        var tempPath = path + ".bldtmp";
        try {
            await File.WriteAllTextAsync(tempPath, result, new UTF8Encoding(hasBom), cancellationToken);
            File.Move(tempPath, path, overwrite: true);
        }
        catch {
            if (File.Exists(tempPath)) {
                try { File.Delete(tempPath); } catch { /* best effort */ }
            }
            throw;
        }
        return true;
    }
}
