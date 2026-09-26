using bld.Services.NuGet;
using NuGet.Configuration;
using System.IO.Compression;
using System.Xml.Linq;

namespace bld.Tests;

/// <summary>
/// clean --keep-private-packages: packages restored from a source other than nuget.org are copied from the
/// packages folder into a folder feed, and nuget.offline.config points their sources at it.
/// </summary>
public class PrivatePackageBackupTests : IDisposable {
    private const string ContosoFeed = "https://feed.contoso.test/v3/index.json";
    private const string UnconfiguredFeed = "https://pkgs.unconfigured.test/v3/index.json";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bld_ppb_" + Guid.NewGuid().ToString("N"));
    private readonly string _packages;
    private readonly string _backup;
    private readonly string _configPath;
    private readonly TestConsole _console = new();

    public PrivatePackageBackupTests() {
        _packages = Path.Combine(_root, "global-packages");
        _backup = Path.Combine(_root, PrivatePackageBackup.DefaultDirectoryName);
        _configPath = Path.Combine(_root, PrivatePackageBackup.ConfigFileName);
        Directory.CreateDirectory(_packages);
    }

    public void Dispose() {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort cleanup */ }
    }

    private const string NuGetConfig = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
            <add key="Contoso" value="{ContosoFeed}" />
            <add key="Unused" value="https://unused.test/v3/index.json" />
          </packageSources>
          <packageSourceCredentials>
            <Contoso>
              <add key="Username" value="me" />
              <add key="ClearTextPassword" value="secret" />
            </Contoso>
          </packageSourceCredentials>
        </configuration>
        """;

    private const string MappedNuGetConfig = $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
            <add key="Contoso" value="{ContosoFeed}" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="nuget.org"><package pattern="*" /></packageSource>
            <packageSource key="Contoso"><package pattern="Contoso.*" /></packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    private ISettings Settings(string config) {
        File.WriteAllText(Path.Combine(_root, "nuget.config"), config);
        return NuGet.Configuration.Settings.LoadSpecificSettings(_root, "nuget.config");
    }

    /// <summary>A package as restore leaves it in the global packages folder: lower-case layout, a real nupkg, .nupkg.metadata.</summary>
    private void Cache(string id, string version, string? source) {
        var directory = Path.Combine(_packages, id.ToLowerInvariant(), version);
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
        var sourceJson = source is null ? "" : $", \"source\": \"{source.Replace("\\", "\\\\")}\"";
        File.WriteAllText(Path.Combine(directory, ".nupkg.metadata"), $$"""{ "version": 2, "contentHash": "x"{{sourceJson}} }""");
    }

    private string Assets(params (string Id, string Version)[] packages) {
        var entries = string.Join(",", packages.Select(p => $$"""  "{{p.Id}}/{{p.Version}}": { "type": "package" }"""));
        var path = Path.Combine(_root, "obj", "project.assets.json");
        var packageFolder = (_packages + Path.DirectorySeparatorChar).Replace("\\", "\\\\");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""
            {
              "version": 3,
              "targets": { "net8.0": { {{entries}} } },
              "packageFolders": { "{{packageFolder}}": {} },
              "project": { "frameworks": { "net8.0": { "dependencies": {} } } }
            }
            """);
        return path;
    }

    private XElement Config() => XDocument.Load(_configPath).Root!;

    private static List<(string Key, string Value)> SourcesOf(XElement config) =>
        config.Element("packageSources")!.Elements("add").Select(e => ((string)e.Attribute("key")!, (string)e.Attribute("value")!)).ToList();

    [Fact]
    public void Copies_only_packages_restored_from_other_sources_than_nuget_org() {
        Cache("Newtonsoft.Json", "13.0.3", "https://api.nuget.org/v3/index.json");
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        Cache("Other.Lib", "2.0.0", UnconfiguredFeed);
        var assets = Assets(("Newtonsoft.Json", "13.0.3"), ("Contoso.Core", "1.2.0"), ("Other.Lib", "2.0.0"));

        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, Settings(NuGetConfig));

        Assert.True(File.Exists(Path.Combine(_backup, "Contoso", "contoso.core.1.2.0.nupkg")));
        Assert.True(File.Exists(Path.Combine(_backup, "pkgs.unconfigured.test", "other.lib.2.0.0.nupkg")));
        Assert.Equal(2, Directory.GetFiles(_backup, "*.nupkg", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Writes_a_config_that_points_the_private_sources_at_their_folders() {
        Cache("Newtonsoft.Json", "13.0.3", "https://api.nuget.org/v3/index.json");
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        Cache("Other.Lib", "2.0.0", UnconfiguredFeed);
        var assets = Assets(("Newtonsoft.Json", "13.0.3"), ("Contoso.Core", "1.2.0"), ("Other.Lib", "2.0.0"));

        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, Settings(NuGetConfig));

        var config = Config();
        Assert.NotNull(config.Element("packageSources")!.Element("clear"));
        Assert.Equal(
            [("nuget.org", "https://api.nuget.org/v3/index.json"), ("Contoso", ".nuget-private/Contoso"), ("pkgs.unconfigured.test", ".nuget-private/pkgs.unconfigured.test")],
            SourcesOf(config));
        // The feed is gone for good; its credentials have no place in the file.
        Assert.Null(config.Element("packageSourceCredentials"));
        Assert.DoesNotContain("secret", File.ReadAllText(_configPath));
        Assert.Contains(_console.Messages, m => m.Message.Contains("Unused"));
    }

    [Fact]
    public void The_written_config_reads_back_as_NuGet_sees_it() {
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        var assets = Assets(("Contoso.Core", "1.2.0"));

        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, Settings(NuGetConfig));

        var offline = NuGet.Configuration.Settings.LoadSpecificSettings(_root, PrivatePackageBackup.ConfigFileName);
        var sources = new PackageSourceProvider(offline).LoadPackageSources().ToList();
        var contoso = Assert.Single(sources, s => s.Name == "Contoso");
        Assert.Equal(Path.Combine(_backup, "Contoso"), Path.TrimEndingDirectorySeparator(contoso.Source));
        Assert.Contains(sources, s => s.Name == "nuget.org");
    }

    [Fact]
    public void Carries_package_source_mapping_over_and_maps_what_no_pattern_covers() {
        Cache("Newtonsoft.Json", "13.0.3", "https://api.nuget.org/v3/index.json");
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        Cache("Other.Lib", "2.0.0", UnconfiguredFeed);
        var assets = Assets(("Newtonsoft.Json", "13.0.3"), ("Contoso.Core", "1.2.0"), ("Other.Lib", "2.0.0"));

        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, Settings(MappedNuGetConfig));

        var offline = NuGet.Configuration.Settings.LoadSpecificSettings(_root, PrivatePackageBackup.ConfigFileName);
        var mapping = PackageSourceMapping.GetPackageSourceMapping(offline);
        Assert.True(mapping.IsEnabled);
        Assert.Contains("Contoso", mapping.GetConfiguredPackageSources("Contoso.Other"));
        Assert.Contains("pkgs.unconfigured.test", mapping.GetConfiguredPackageSources("Other.Lib"));
        Assert.Contains("nuget.org", mapping.GetConfiguredPackageSources("Newtonsoft.Json"));
    }

    [Fact]
    public void A_rerun_after_restoring_from_the_folder_feed_keeps_it() {
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        var assets = Assets(("Contoso.Core", "1.2.0"));
        var settings = Settings(NuGetConfig);
        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, settings);

        // Caches cleared, restored through nuget.offline.config: the recorded source is now the folder.
        Directory.Delete(_packages, recursive: true);
        Cache("Contoso.Core", "1.2.0", Path.Combine(_backup, "Contoso"));
        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, settings);

        Assert.Single(Directory.GetFiles(_backup, "*.nupkg", SearchOption.AllDirectories));
        Assert.Contains(("Contoso", ".nuget-private/Contoso"), SourcesOf(Config()));
    }

    [Fact]
    public void Reports_what_it_cannot_keep() {
        Cache("NoSource.Lib", "1.0.0", source: null);
        var assets = Assets(("NoSource.Lib", "1.0.0"), ("NotCached.Lib", "1.0.0"));

        new PrivatePackageBackup(_console).Run([assets, Path.Combine(_root, "missing", "project.assets.json")], _backup, _configPath, Settings(NuGetConfig));

        var warnings = _console.Messages.Where(m => m.Level == "Warning").Select(m => m.Message).ToList();
        Assert.Contains(warnings, w => w.Contains("no project.assets.json"));
        Assert.Contains(warnings, w => w.Contains("NotCached.Lib 1.0.0"));
        Assert.Contains(warnings, w => w.Contains("NoSource.Lib 1.0.0"));
        Assert.False(Directory.Exists(_backup));
    }

    /// <summary>
    /// A real package from nuget.org, restored through a mirror: the recorded source is private, the file
    /// is nuget.org's. xunit.abstractions is one of this test project's own dependencies, so restore has
    /// put it in the packages folder.
    /// </summary>
    [Fact]
    public void Skips_nuget_org_packages_that_came_through_a_mirror() {
        var globalPackages = SettingsUtility.GetGlobalPackagesFolder(NuGet.Configuration.Settings.LoadDefaultSettings(null));
        var signed = Path.Combine(globalPackages, "xunit.abstractions", "2.0.3", "xunit.abstractions.2.0.3.nupkg");
        Assert.True(PrivatePackageBackup.IsSignedByNuGetOrg(signed));

        var directory = Path.Combine(_packages, "xunit.abstractions", "2.0.3");
        Directory.CreateDirectory(directory);
        File.Copy(signed, Path.Combine(directory, "xunit.abstractions.2.0.3.nupkg"));
        File.WriteAllText(Path.Combine(directory, ".nupkg.metadata"), $$"""{ "version": 2, "contentHash": "x", "source": "{{ContosoFeed}}" }""");
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        var assets = Assets(("xunit.abstractions", "2.0.3"), ("Contoso.Core", "1.2.0"));

        new PrivatePackageBackup(_console).Run([assets], _backup, _configPath, Settings(NuGetConfig));

        Assert.Equal(["contoso.core.1.2.0.nupkg"], Directory.GetFiles(_backup, "*.nupkg", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.Contains(_console.Messages, m => m.Message.Contains("nuget.org's own files"));
    }

    [Fact]
    public void An_unsigned_package_is_not_taken_for_nuget_orgs() {
        Cache("Contoso.Core", "1.2.0", ContosoFeed);
        Assert.False(PrivatePackageBackup.IsSignedByNuGetOrg(Path.Combine(_packages, "contoso.core", "1.2.0", "contoso.core.1.2.0.nupkg")));
    }

    [Theory]
    [InlineData("https://api.nuget.org/v3/index.json", true)]
    [InlineData("/usr/share/dotnet/library-packs", true)]
    [InlineData(@"C:\Program Files\dotnet\library-packs", true)]
    [InlineData(@"C:\Program Files (x86)\Microsoft SDKs\NuGetPackages\", true)]
    [InlineData("https://pkgs.dev.azure.com/contoso/_packaging/feed/nuget/v3/index.json", false)]
    [InlineData(@"\\fileserver\nuget", false)]
    public void Tells_public_sources_from_private_ones(string source, bool expected) =>
        Assert.Equal(expected, PrivatePackageBackup.IsPublic(source));
}
