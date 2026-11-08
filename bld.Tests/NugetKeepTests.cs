using bld.Commands;
using bld.Models;
using bld.Services.NuGet;
using System.IO.Compression;

namespace bld.Tests;

/// <summary>
/// `nuget keep`: the step behind `clean --keep-private-packages` on its own. The subcommand places the
/// feed and the config as `clean` does, and the application finds project.assets.json through MSBuild.
/// </summary>
public class NugetKeepTests : IDisposable {
    private const string ContosoFeed = "https://feed.contoso.test/v3/index.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bld_keep_" + Guid.NewGuid().ToString("N"));
    private readonly TestConsole _console = new();

    public NugetKeepTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    [Fact]
    public void Keep_IsASubcommandOfNuget_ThatPlacesTheFeedAndTheConfigNextToTheRoot() {
        var nuget = new NugetCommand(_console);
        var keep = Assert.IsType<NugetKeepCommand>(Assert.Single(nuget.Subcommands));

        // "keep" after nuget is the subcommand, not nuget's root argument.
        var parsed = new System.CommandLine.RootCommand { nuget }.Parse(["nuget", "keep", _root]);
        Assert.Empty(parsed.Errors);
        Assert.Same(keep, parsed.CommandResult.Command);
        var (backup, config) = keep.ResolvePaths(parsed);
        Assert.Equal(Path.Combine(_root, PrivatePackageBackup.DefaultDirectoryName), backup);
        Assert.Equal(Path.Combine(_root, PrivatePackageBackup.ConfigFileName), config);

        // A project file as root means its directory; --dir is resolved like any path on the command line.
        var project = Path.Combine(_root, "App.csproj");
        File.WriteAllText(project, "<Project />");
        parsed = new System.CommandLine.RootCommand { nuget }.Parse(["nuget", "keep", "--dir", "feed", project]);
        Assert.Empty(parsed.Errors);
        (backup, config) = keep.ResolvePaths(parsed);
        Assert.Equal(Path.GetFullPath("feed"), backup);
        Assert.Equal(Path.Combine(_root, PrivatePackageBackup.ConfigFileName), config);
    }

    [Fact]
    public async Task Run_FindsTheAssetsFilesThroughMSBuildAndKeepsThePrivatePackages() {
        var packages = Path.Combine(_root, "global-packages");
        Cache(packages, "Contoso.Core", "1.2.0", ContosoFeed);
        Cache(packages, "Newtonsoft.Json", "13.0.3", "https://api.nuget.org/v3/index.json");
        File.WriteAllText(Path.Combine(_root, "nuget.config"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
                <add key="Contoso" value="{ContosoFeed}" />
              </packageSources>
            </configuration>
            """);
        var projectDir = Path.Combine(_root, "App");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n");
        Assets(Path.Combine(projectDir, "obj", "project.assets.json"), packages, ("Contoso.Core", "1.2.0"), ("Newtonsoft.Json", "13.0.3"));

        var options = new CleaningOptions { MaxDegreeOfParallelism = 1 };
        var app = new PrivatePackageKeepApplication(_console);
        await app.InitAsync(options);
        var backup = Path.Combine(_root, PrivatePackageBackup.DefaultDirectoryName);
        var config = Path.Combine(_root, PrivatePackageBackup.ConfigFileName);

        var exit = await app.RunAsync(_root, options, backup, config, default);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(backup, "Contoso", "contoso.core.1.2.0.nupkg")));
        Assert.Single(Directory.GetFiles(backup, "*.nupkg", SearchOption.AllDirectories));
        Assert.True(File.Exists(config));
        Assert.Contains(_console.Messages, m => m.Message.Contains("Kept 1 private package(s) from Contoso"));
    }

    [Fact]
    public async Task Run_WithoutAProjectFails() {
        var app = new PrivatePackageKeepApplication(_console);
        await app.InitAsync(new CleaningOptions());

        var exit = await app.RunAsync(_root, new CleaningOptions(), Path.Combine(_root, ".nuget-private"), Path.Combine(_root, "nuget.offline.config"), default);

        Assert.Equal(1, exit);
        Assert.Contains(_console.Messages, m => m.Level == "Error" && m.Message.Contains("No solution or project"));
    }

    /// <summary>A package as restore leaves it in the global packages folder: lower-case layout, a real nupkg, .nupkg.metadata.</summary>
    private static void Cache(string packages, string id, string version, string source) {
        var directory = Path.Combine(packages, id.ToLowerInvariant(), version);
        Directory.CreateDirectory(directory);
        using (var zip = ZipFile.Open(Path.Combine(directory, $"{id.ToLowerInvariant()}.{version}.nupkg"), ZipArchiveMode.Create)) {
            using var writer = new StreamWriter(zip.CreateEntry($"{id}.nuspec").Open());
            writer.Write($"""
                <?xml version="1.0" encoding="utf-8"?>
                <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
                  <metadata><id>{id}</id><version>{version}</version><authors>a</authors><description>d</description></metadata>
                </package>
                """);
        }
        File.WriteAllText(Path.Combine(directory, ".nupkg.metadata"), $$"""{ "version": 2, "contentHash": "x", "source": "{{source}}" }""");
    }

    private static void Assets(string path, string packages, params (string Id, string Version)[] entries) {
        var targets = string.Join(",", entries.Select(p => $$"""  "{{p.Id}}/{{p.Version}}": { "type": "package" }"""));
        var packageFolder = (packages + Path.DirectorySeparatorChar).Replace("\\", "\\\\");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "version": 3,
              "targets": { "net8.0": { {{targets}} } },
              "packageFolders": { "{{packageFolder}}": {} },
              "project": { "frameworks": { "net8.0": { "dependencies": {} } } }
            }
            """);
    }
}
