using bld.Models;
using bld.Services;
using System.Text;
using System.Xml.Linq;

namespace bld.Tests;

public class CpmServiceTests {
    [Fact]
    public void ReadPackageReferences_ParsesVersionAndVersionOverridePatterns() {
        var doc = XDocument.Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="A" Version="1.0.0" />
                <PackageReference Include="B">
                  <Version>2.0.0</Version>
                </PackageReference>
                <PackageReference Include="C" VersionOverride="3.0.0" />
                <PackageReference Include="D">
                  <VersionOverride>4.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var refs = CpmService.ReadPackageReferences(doc).OrderBy(r => r.PackageId).ToList();

        Assert.Collection(refs,
            item => {
                Assert.Equal("A", item.PackageId);
                Assert.Equal("1.0.0", item.Version);
                Assert.False(item.IsVersionOverride);
            },
            item => {
                Assert.Equal("B", item.PackageId);
                Assert.Equal("2.0.0", item.Version);
                Assert.False(item.IsVersionOverride);
            },
            item => {
                Assert.Equal("C", item.PackageId);
                Assert.Equal("3.0.0", item.Version);
                Assert.True(item.IsVersionOverride);
            },
            item => {
                Assert.Equal("D", item.PackageId);
                Assert.Equal("4.0.0", item.Version);
                Assert.True(item.IsVersionOverride);
            });
    }

    [Fact]
    public void RemoveCentralizableVersionDeclarations_RemovesVersionButKeepsVersionOverride() {
        var doc = XDocument.Parse("""
            <Project>
              <ItemGroup>
                <PackageReference Include="A" Version="1.0.0" />
                <PackageReference Include="B">
                  <Version>2.0.0</Version>
                </PackageReference>
                <PackageReference Include="C" VersionOverride="3.0.0" />
                <PackageReference Include="D">
                  <VersionOverride>4.0.0</VersionOverride>
                </PackageReference>
              </ItemGroup>
            </Project>
            """);

        var modified = CpmService.RemoveCentralizableVersionDeclarations(doc);
        Assert.True(modified);

        var refs = doc.Descendants("PackageReference").ToDictionary(
            x => x.Attribute("Include")?.Value ?? string.Empty,
            x => x,
            StringComparer.OrdinalIgnoreCase);

        Assert.Null(refs["A"].Attribute("Version"));
        Assert.Empty(refs["A"].Elements("Version"));

        Assert.Null(refs["B"].Attribute("Version"));
        Assert.Empty(refs["B"].Elements("Version"));

        Assert.Equal("3.0.0", refs["C"].Attribute("VersionOverride")?.Value);
        Assert.Equal("4.0.0", refs["D"].Element("VersionOverride")?.Value);
    }

    [Fact]
    public void FindDirectoryPackagesPropsPaths_FindsNearestAndDistinctPropsFiles() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var solutionDir = Path.Combine(tempDir, "repo");
            var project1Dir = Path.Combine(solutionDir, "src", "A");
            var project2Dir = Path.Combine(solutionDir, "src", "B", "Nested");
            Directory.CreateDirectory(project1Dir);
            Directory.CreateDirectory(project2Dir);

            var rootProps = Path.Combine(solutionDir, "Directory.Packages.props");
            var nestedProps = Path.Combine(solutionDir, "src", "B", "Directory.Packages.props");
            File.WriteAllText(rootProps, "<Project />");
            File.WriteAllText(nestedProps, "<Project />");

            var project1 = Path.Combine(project1Dir, "A.csproj");
            var project2 = Path.Combine(project2Dir, "B.csproj");
            File.WriteAllText(project1, "<Project />");
            File.WriteAllText(project2, "<Project />");

            var paths = CpmService.FindDirectoryPackagesPropsPaths([project1, project2], solutionDir);

            Assert.Equal(2, paths.Count);
            Assert.Contains(Path.GetFullPath(rootProps), paths, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.GetFullPath(nestedProps), paths, StringComparer.OrdinalIgnoreCase);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdateProjectFileAsync_PreservesLineEndingsBomAndIndentation() {
        // CRLF + BOM (a typical Windows-authored .csproj), 4-space indentation, a blank
        // line, a comment, and both attribute- and element-form <Version> entries.
        var content =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <TargetFramework>net8.0</TargetFramework>\r\n" +
            "    </PropertyGroup>\r\n" +
            "\r\n" +
            "    <!-- third party -->\r\n" +
            "    <ItemGroup>\r\n" +
            "        <PackageReference Include=\"A\" Version=\"1.0.0\" />\r\n" +
            "        <PackageReference Include=\"B\">\r\n" +
            "            <Version>2.0.0</Version>\r\n" +
            "        </PackageReference>\r\n" +
            "    </ItemGroup>\r\n" +
            "</Project>\r\n";

        var tempFile = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}.csproj");
        await File.WriteAllTextAsync(tempFile, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try {
            var service = new CpmService(new TestConsole(), new CleaningOptions());
            await service.UpdateProjectFileAsync(tempFile, default);

            var bytes = await File.ReadAllBytesAsync(tempFile);
            var updated = await File.ReadAllTextAsync(tempFile);

            // BOM preserved.
            Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "UTF-8 BOM should be preserved");

            // CRLF preserved; no lone LF introduced.
            Assert.Contains("\r\n", updated);
            Assert.DoesNotContain("\n", updated.Replace("\r\n", ""));

            // Versions stripped for central management.
            Assert.DoesNotContain("Version=\"1.0.0\"", updated);
            Assert.DoesNotContain("<Version>2.0.0</Version>", updated);

            // Original layout preserved instead of re-indented to the default 2 spaces.
            Assert.Contains("        <TargetFramework>net8.0</TargetFramework>", updated);
            Assert.Contains("        <PackageReference Include=\"A\" />", updated);
            Assert.Contains("<!-- third party -->", updated);
        }
        finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }

    [Theory]
    [InlineData("2.0.0", "2.0.0-beta", 1)]                  // stable outranks its prerelease (was 0 via System.Version)
    [InlineData("2.0.0-beta", "2.0.0", -1)]
    [InlineData("1.0.0-preview.7", "1.0.0-preview.2", 1)]   // prerelease ordering (was 0)
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    public void CompareVersions_UsesSemVerOrdering(string a, string b, int expectedSign) {
        Assert.Equal(expectedSign, Math.Sign(CpmService.CompareVersions(a, b)));
    }
}
