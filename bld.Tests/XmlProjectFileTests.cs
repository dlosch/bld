using bld.Infrastructure;
using System.Text;
using System.Xml.Linq;

namespace bld.Tests;

public class XmlProjectFileTests {

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"bld-xml-{Guid.NewGuid():N}.csproj");

    [Fact]
    public async Task EditAsync_PreservesCrlfBomIndentationAndComments() {
        // CRLF + UTF-8 BOM, 4-space indentation, a blank line and a comment.
        var content =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <TargetFramework>net8.0</TargetFramework>\r\n" +
            "    </PropertyGroup>\r\n" +
            "\r\n" +
            "    <!-- keep me -->\r\n" +
            "    <ItemGroup>\r\n" +
            "        <PackageReference Include=\"A\" Version=\"1.0.0\" />\r\n" +
            "    </ItemGroup>\r\n" +
            "</Project>\r\n";

        var path = TempFile();
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try {
            var written = await XmlProjectFile.EditAsync(path, doc => {
                doc.Descendants("TargetFramework").First().Value = "net9.0";
                return true;
            }, default);

            Assert.True(written);

            var bytes = await File.ReadAllBytesAsync(path);
            var text = await File.ReadAllTextAsync(path);

            Assert.True(bytes is [0xEF, 0xBB, 0xBF, ..], "UTF-8 BOM should be preserved");
            Assert.Contains("\r\n", text);
            Assert.DoesNotContain("\n", text.Replace("\r\n", "")); // no lone LF
            Assert.Contains("        <TargetFramework>net9.0</TargetFramework>", text); // change applied, indent kept
            Assert.Contains("<!-- keep me -->", text);
            Assert.Contains("        <PackageReference Include=\"A\" Version=\"1.0.0\" />", text); // untouched
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditAsync_DoesNotWriteWhenMutatorReportsNoChange() {
        var content = "<Project>\r\n  <PropertyGroup />\r\n</Project>\r\n";
        var path = TempFile();
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        try {
            var originalBytes = await File.ReadAllBytesAsync(path);

            var written = await XmlProjectFile.EditAsync(path, _ => false, default);

            Assert.False(written);
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path)); // byte-identical
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditAsync_PreservesExistingXmlDeclarationWithoutRewritingEncoding() {
        // A declared utf-8 encoding must NOT be rewritten to utf-16 by the StringBuilder writer.
        var content =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Project>\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";
        var path = TempFile();
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        try {
            var written = await XmlProjectFile.EditAsync(path, doc => {
                doc.Descendants("TargetFramework").First().Value = "net9.0";
                return true;
            }, default);

            Assert.True(written);
            var text = await File.ReadAllTextAsync(path);

            Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", text);
            Assert.DoesNotContain("utf-16", text);
            Assert.Contains("<TargetFramework>net9.0</TargetFramework>", text);
            Assert.DoesNotContain("\r\n", text); // LF-only file stays LF
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditAsync_RepeatedEditsDoNotAccumulateBlankLinesAfterDeclaration() {
        var content =
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
            "<Project>\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n";
        var path = TempFile();
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false));
        try {
            for (var i = 9; i <= 11; i++) {
                await XmlProjectFile.EditAsync(path, doc => {
                    doc.Descendants("TargetFramework").First().Value = $"net{i}.0";
                    return true;
                }, default);
            }

            var text = await File.ReadAllTextAsync(path);
            Assert.Equal(content.Replace("net8.0", "net11.0"), text);
            Assert.DoesNotContain("?>\n\n", text);
        }
        finally {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task EditAsync_RefusesToRewriteNonUtf8File() {
        // Windows-1252 'ü' (0xFC) is not valid UTF-8; rewriting it used to replace it with U+FFFD.
        var path = TempFile();
        var bytes = new List<byte>();
        bytes.AddRange(Encoding.ASCII.GetBytes("<Project>\n  <!-- M"));
        bytes.Add(0xFC);
        bytes.AddRange(Encoding.ASCII.GetBytes("ller -->\n  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>\n</Project>\n"));
        await File.WriteAllBytesAsync(path, bytes.ToArray());
        try {
            var original = await File.ReadAllBytesAsync(path);

            await Assert.ThrowsAsync<InvalidOperationException>(() => XmlProjectFile.EditAsync(path, doc => {
                doc.Descendants("TargetFramework").First().Value = "net9.0";
                return true;
            }, default));

            Assert.Equal(original, await File.ReadAllBytesAsync(path)); // untouched
        }
        finally {
            File.Delete(path);
        }
    }
}
