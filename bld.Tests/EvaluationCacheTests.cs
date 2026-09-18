using bld.Infrastructure;
using bld.Models;
using bld.Services;

namespace bld.Tests;

public class EvaluationCacheTests {

    private static (string Dir, string Project) CreateProject() {
        var dir = Path.Combine(Path.GetTempPath(), $"bld-evalcache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "App"));
        WritePackagesProps(dir, "12.0.1");
        var project = Path.Combine(dir, "App", "App.csproj");
        File.WriteAllText(project, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Newtonsoft.Json" />
              </ItemGroup>
            </Project>
            """);
        return (dir, project);
    }

    private static void WritePackagesProps(string dir, string version) {
        File.WriteAllText(Path.Combine(dir, "Directory.Packages.props"), $"""
            <Project>
              <PropertyGroup>
                <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
              </PropertyGroup>
              <ItemGroup>
                <PackageVersion Include="Newtonsoft.Json" Version="{version}" />
              </ItemGroup>
            </Project>
            """);
    }

    private static ProjParser Parser(string cacheDir, string tools = "tools-1") {
        var console = new TestConsole();
        MSBuildService.RegisterMSBuildDefaults(console, new CleaningOptions());
        return new ProjParser(console, new ErrorSink(console), new CleaningOptions()) {
            Cache = new EvaluationCache(cacheDir, tools, console)
        };
    }

    [Fact]
    public void SecondRun_AnswersFromTheCacheWithTheSameResult() {
        var (dir, project) = CreateProject();
        try {
            var cfg = new ProjCfg(new Proj(project, null), "Release");
            using var first = Parser(Path.Combine(dir, "cache"));
            var evaluated = first.GetPackageReferences(cfg)!;
            Assert.Equal(0, first.Cache!.Hits);
            Assert.Equal(1, first.Cache.Misses);
            Assert.Contains(evaluated.ContributingFiles, f => f.EndsWith("Directory.Packages.props", StringComparison.OrdinalIgnoreCase));

            using var second = Parser(Path.Combine(dir, "cache"));
            var cached = second.GetPackageReferences(cfg)!;

            Assert.Equal(1, second.Cache!.Hits);
            Assert.Equal(0, second.Cache.Misses);
            Assert.Equal("12.0.1", cached.PackageReferences["Newtonsoft.Json"].EffectiveVersion);
            Assert.Equal(PackageItemKind.PackageReference, cached.PackageReferences["Newtonsoft.Json"].Kind);
            Assert.Equal(evaluated.TargetFrameworks, cached.TargetFrameworks);
            Assert.Equal(evaluated.UseCpm, cached.UseCpm);
            Assert.Equal(evaluated.CpmFile, cached.CpmFile);
            Assert.Equal(evaluated.PackageVersions!["Newtonsoft.Json"].SourceFile, cached.PackageVersions!["Newtonsoft.Json"].SourceFile);
            Assert.Equal(evaluated.ProjectReferences, cached.ProjectReferences);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ChangedPackagesProps_MissesAndReEvaluates() {
        var (dir, project) = CreateProject();
        try {
            var cfg = new ProjCfg(new Proj(project, null), "Release");
            using (var first = Parser(Path.Combine(dir, "cache"))) first.GetPackageReferences(cfg);

            WritePackagesProps(dir, "13.0.1");
            using var second = Parser(Path.Combine(dir, "cache"));
            var refs = second.GetPackageReferences(cfg)!;

            Assert.Equal(0, second.Cache!.Hits);
            Assert.Equal(1, second.Cache.Misses);
            Assert.Equal("13.0.1", refs.PackageReferences["Newtonsoft.Json"].EffectiveVersion);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NewDirectoryBuildPropsAboveTheProject_ChangesTheKey() {
        var (dir, project) = CreateProject();
        try {
            var cfg = new ProjCfg(new Proj(project, null), "Release");
            using (var first = Parser(Path.Combine(dir, "cache"))) first.GetPackageReferences(cfg);

            // Did not exist when the entry was written, so no stored hash can notice it.
            File.WriteAllText(Path.Combine(dir, "Directory.Build.props"), "<Project />");
            using var second = Parser(Path.Combine(dir, "cache"));
            second.GetPackageReferences(cfg);

            Assert.Equal(0, second.Cache!.Hits);
            Assert.Equal(1, second.Cache.Misses);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DifferentTools_Miss() {
        var (dir, project) = CreateProject();
        try {
            var cfg = new ProjCfg(new Proj(project, null), "Release");
            using (var first = Parser(Path.Combine(dir, "cache"), tools: "sdk-9")) first.GetPackageReferences(cfg);

            using var second = Parser(Path.Combine(dir, "cache"), tools: "sdk-10");
            second.GetPackageReferences(cfg);

            Assert.Equal(1, second.Cache!.Misses);
        }
        finally {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ComputeKey_DependsOnPathPropertiesToolsAndProbeHits() {
        var props = new Dictionary<string, string> { ["Configuration"] = "Release" };
        var key = EvaluationCache.ComputeKey("/a/App.csproj", props, "t", new[] { "/a/Directory.Build.props" });

        Assert.Equal(key, EvaluationCache.ComputeKey("/a/App.csproj", props, "t", new[] { "/a/Directory.Build.props" }));
        Assert.NotEqual(key, EvaluationCache.ComputeKey("/b/App.csproj", props, "t", new[] { "/a/Directory.Build.props" }));
        Assert.NotEqual(key, EvaluationCache.ComputeKey("/a/App.csproj", new Dictionary<string, string> { ["Configuration"] = "Debug" }, "t", new[] { "/a/Directory.Build.props" }));
        Assert.NotEqual(key, EvaluationCache.ComputeKey("/a/App.csproj", props, "u", new[] { "/a/Directory.Build.props" }));
        Assert.NotEqual(key, EvaluationCache.ComputeKey("/a/App.csproj", props, "t", Array.Empty<string>()));
    }
}
