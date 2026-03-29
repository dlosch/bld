using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.Reflection;
using System.Xml.Linq;

namespace bld.Tests;

public class OutdatedServiceTests {

    [Fact]
    public void SelectCompatibleTargetFrameworks_RespectsSkipFlag() {
        var packageRefs = new OutdatedService.PackageInfoContainer();
        packageRefs.Add(new OutdatedService.PackageInfo {
            Id = "Example.Package",
            Item = new Pkg("Example.Package", "1.0.0"),
            ProjectPath = "Example.csproj",
            TargetFramework = "net8.0",
            TargetFrameworks = ["net8.0", "net10.0"]
        });

        var withCheck = OutdatedService.SelectCompatibleTargetFrameworks(skipTfmCheck: false, packageRefs);
        var skipped = OutdatedService.SelectCompatibleTargetFrameworks(skipTfmCheck: true, packageRefs);

        Assert.Contains("net8.0", withCheck, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("net10.0", withCheck, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(skipped);
    }

    [Fact]
    public async Task UpdatePackageVersionAsync_MatchesPackageIdCaseInsensitively() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-outdated-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projectPath = Path.Combine(tempDir, "Sample.csproj");
            await File.WriteAllTextAsync(projectPath, """
                <Project>
                  <ItemGroup>
                    <PackageReference Include="Newtonsoft.Json" Version="13.0.1" />
                  </ItemGroup>
                </Project>
                """);

            var service = new OutdatedService(new TestConsole(), new CleaningOptions());
            var method = typeof(OutdatedService).GetMethod("UpdatePackageVersionAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);

            var updateTask = (Task?)method!.Invoke(service, [
                projectPath,
                "newtonsoft.json",
                (target: "14.0.0", currentVersion: (string?)"13.0.1", reason: VersionReason.PackageReferenceProj),
                CancellationToken.None
            ]);

            Assert.NotNull(updateTask);
            await updateTask!;

            var doc = XDocument.Load(projectPath);
            var updatedVersion = doc.Descendants("PackageReference")
                .Single()
                .Attribute("Version")?
                .Value;

            Assert.Equal("14.0.0", updatedVersion);
        }
        finally {
            if (Directory.Exists(tempDir)) {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
