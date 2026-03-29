using bld.Infrastructure;
using bld.Models;
using bld.Services;
using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;
using System.Runtime.CompilerServices;
using Xunit.Abstractions;

namespace bld.Tests;

public class SlnfTests(ITestOutputHelper Output) {

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EnsureMSBuild() =>
        MSBuildService.RegisterMSBuildDefaults(new TestConsole(Output), new CleaningOptions());

    [MethodImpl(MethodImplOptions.NoInlining)]
    [Fact]
    public void ParseWithFilter_SlnfFile_ReturnsOnlyFilteredProjects() {
        EnsureMSBuild();
        RunSlnfFilterTest();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [Fact(Skip = "actual path to .slnf file from dotnet/aspnetcore in a specific version hardcoded.")]
    public void ParseSlnf_ShouldReturnAllProjects() {
        EnsureMSBuild();
        ParseSlnf_ShouldReturnAllProjects_Impl();
    }

    private void ParseSlnf_ShouldReturnAllProjects_Impl() {

        // Point this to your Solution Filter file
        string slnfPath = @"d:\GITHUB\dotnet\aspnetcore\src\Caching\Caching.slnf";

        // 2. Parse the .slnf file
        // MSBuild handles the JSON parsing internally and scopes the result.
        SolutionFile solution = SolutionFile.Parse(slnfPath);

        // 3. Extract the actual buildable projects.
        // The SolutionFile parses Solution Folders as "projects" too, so we filter them out.
        var filteredProjects = solution.ProjectsInOrder
            .Where(p => p.ProjectType != SolutionProjectType.SolutionFolder)
            .ToList();

        Assert.Equal(579, filteredProjects.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    [Fact(Skip = "actual path to .slnf file from dotnet/aspnetcore in a specific version hardcoded.")]
    public void ParseSlnf_ShouldReturnFilteredProjects() {
        EnsureMSBuild();
        ParseSlnf_ShouldReturnFilteredProjects_Impl();
    }
    public void ParseSlnf_ShouldReturnFilteredProjects_Impl() {

        // Point this to your Solution Filter file
        string slnfPath = @"d:\GITHUB\dotnet\aspnetcore\src\Caching\Caching.slnf";

        // 2. Parse the .slnf file
        // MSBuild handles the JSON parsing internally and scopes the result.
        var result = SlnFileHelper.ParseWithFilter(slnfPath);
        var solution = result.Solution;

        // 3. Extract the actual buildable projects.
        // The SolutionFile parses Solution Folders as "projects" too, so we filter them out.
        var filteredProjects = solution.ProjectsInOrder
            .Where(p => p.ProjectType != SolutionProjectType.SolutionFolder && result.ProjectFilter.Contains(p.AbsolutePath))
            .ToList();

        Assert.Equal(6, filteredProjects.Count);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunSlnfFilterTest() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-slnf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projADir = Path.Combine(tempDir, "ProjectA");
            var projBDir = Path.Combine(tempDir, "ProjectB");
            Directory.CreateDirectory(projADir);
            Directory.CreateDirectory(projBDir);

            var projAPath = Path.Combine(projADir, "ProjectA.csproj");
            var projBPath = Path.Combine(projBDir, "ProjectB.csproj");
            File.WriteAllText(projAPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            File.WriteAllText(projBPath, """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net8.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);

            var slnPath = Path.Combine(tempDir, "Test.sln");
            var guidA = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var guidB = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var slnGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            File.WriteAllText(slnPath, $"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                # Visual Studio Version 17
                Project("{slnGuid}") = "ProjectA", "ProjectA\ProjectA.csproj", "{guidA}"
                EndProject
                Project("{slnGuid}") = "ProjectB", "ProjectB\ProjectB.csproj", "{guidB}"
                EndProject
                """);

            var slnfPath = Path.Combine(tempDir, "Test.slnf");
            File.WriteAllText(slnfPath, $$"""
                {
                  "solution": {
                    "path": "Test.sln",
                    "projects": [
                      "ProjectA\\ProjectA.csproj"
                    ]
                  }
                }
                """);

            // Full .sln — both projects, no filter
            var fullResult = SlnFileHelper.ParseWithFilter(slnPath);
            Assert.Null(fullResult.ProjectFilter);
            var allProjects = fullResult.Solution.ProjectsInOrder.Select(p => p.ProjectName).ToList();
            Output.WriteLine($"Full .sln projects: {string.Join(", ", allProjects)}");
            Assert.Contains("ProjectA", allProjects);
            Assert.Contains("ProjectB", allProjects);

            // .slnf — filter should only include ProjectA
            var filteredResult = SlnFileHelper.ParseWithFilter(slnfPath);
            Assert.NotNull(filteredResult.ProjectFilter);
            Output.WriteLine($"Filter contains {filteredResult.ProjectFilter!.Count} project(s):");
            foreach (var p in filteredResult.ProjectFilter)
                Output.WriteLine($"  {p}");

            Assert.Single(filteredResult.ProjectFilter);
            Assert.Contains(filteredResult.ProjectFilter,
                p => p.EndsWith("ProjectA.csproj", StringComparison.OrdinalIgnoreCase));

            // Applying the filter to solution projects should exclude ProjectB
            var filteredProjects = filteredResult.Solution.ProjectsInOrder
                .Where(p => filteredResult.ProjectFilter.Contains(p.AbsolutePath))
                .Select(p => p.ProjectName)
                .ToList();
            Output.WriteLine($"Filtered projects: {string.Join(", ", filteredProjects)}");
            Assert.Single(filteredProjects);
            Assert.Equal("ProjectA", filteredProjects[0]);
        }
        finally {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ParseWithFilter_RegularSlnFile_ReturnsNullFilter() {
        EnsureMSBuild();
        RunRegularSlnTest();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunRegularSlnTest() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-slnf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projDir = Path.Combine(tempDir, "MyProj");
            Directory.CreateDirectory(projDir);
            File.WriteAllText(Path.Combine(projDir, "MyProj.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                </Project>
                """);

            var slnPath = Path.Combine(tempDir, "Test.sln");
            var guid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var slnGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            File.WriteAllText(slnPath, $"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{slnGuid}") = "MyProj", "MyProj\MyProj.csproj", "{guid}"
                EndProject
                """);

            var result = SlnFileHelper.ParseWithFilter(slnPath);
            Assert.NotNull(result.Solution);
            Assert.Null(result.ProjectFilter);
        }
        finally {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void SlnParser_WithSlnf_OnlyYieldsFilteredProjects() {
        EnsureMSBuild();
        RunSlnParserFilterTest();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void RunSlnParserFilterTest() {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bld-slnf-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try {
            var projADir = Path.Combine(tempDir, "A");
            var projBDir = Path.Combine(tempDir, "B");
            Directory.CreateDirectory(projADir);
            Directory.CreateDirectory(projBDir);

            File.WriteAllText(Path.Combine(projADir, "A.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(projBDir, "B.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
                </Project>
                """);

            var slnPath = Path.Combine(tempDir, "Test.sln");
            var guidA = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var guidB = Guid.NewGuid().ToString("B").ToUpperInvariant();
            var slnGuid = Guid.NewGuid().ToString("B").ToUpperInvariant();
            File.WriteAllText(slnPath, $"""
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{slnGuid}") = "A", "A\A.csproj", "{guidA}"
                EndProject
                Project("{slnGuid}") = "B", "B\B.csproj", "{guidB}"
                EndProject
                """);

            var slnfPath = Path.Combine(tempDir, "Test.slnf");
            File.WriteAllText(slnfPath, """
                {
                  "solution": {
                    "path": "Test.sln",
                    "projects": [ "A\\A.csproj" ]
                  }
                }
                """);

            var console = new TestConsole(Output);
            var errorSink = new ErrorSink(console);
            var parser = new SlnParser(console, errorSink);

            var projects = new List<string>();
            var enumerable = parser.ParseSolution(slnfPath);
            var enumerator = enumerable.GetAsyncEnumerator();
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult()) {
                projects.Add(enumerator.Current.Path);
            }

            Output.WriteLine($"SlnParser yielded {projects.Count} project path(s):");
            foreach (var p in projects)
                Output.WriteLine($"  {p}");

            Assert.All(projects, p => Assert.Contains("A.csproj", p));
            Assert.DoesNotContain(projects, p => p.Contains("B.csproj"));
        }
        finally {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
