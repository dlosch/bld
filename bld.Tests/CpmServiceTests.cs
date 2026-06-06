using bld.Models;
using bld.Services;
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
    public async Task UpdateProjectFileAsync_RemovesVersionsButPreservesExistingFormatting() {
        // 4-space-per-level indentation, a blank line and a comment: all of which a
        // reformatting (Indent=true) save would destroy.
        var original =
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
            "    <PropertyGroup>\n" +
            "        <TargetFramework>net8.0</TargetFramework>\n" +
            "    </PropertyGroup>\n" +
            "\n" +
            "    <!-- third party -->\n" +
            "    <ItemGroup>\n" +
            "        <PackageReference Include=\"A\" Version=\"1.0.0\" />\n" +
            "        <PackageReference Include=\"B\" Version=\"2.0.0\" />\n" +
            "    </ItemGroup>\n" +
            "</Project>";

        var tempFile = Path.Combine(Path.GetTempPath(), $"bld-cpm-{Guid.NewGuid():N}.csproj");
        await File.WriteAllTextAsync(tempFile, original);
        try {
            var service = new CpmService(new TestConsole(), new CleaningOptions());
            await service.UpdateProjectFileAsync(tempFile, default);

            var updated = await File.ReadAllTextAsync(tempFile);

            // Versions stripped for central management.
            Assert.DoesNotContain("Version=\"1.0.0\"", updated);
            Assert.DoesNotContain("Version=\"2.0.0\"", updated);

            // Original layout preserved instead of re-indented to the default 2 spaces.
            Assert.Contains("        <TargetFramework>net8.0</TargetFramework>", updated);
            Assert.Contains("        <PackageReference Include=\"A\" />", updated);
            Assert.Contains("        <PackageReference Include=\"B\" />", updated);
            Assert.Contains("<!-- third party -->", updated);
        }
        finally {
            if (File.Exists(tempFile)) {
                File.Delete(tempFile);
            }
        }
    }
}
