using bld.Infrastructure;
using bld.Services;
using System.Text;
using System.Xml.Linq;

namespace bld.Tests;

public class ContainerMigrationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bld-migrate-{Guid.NewGuid():N}");
    private readonly TestConsole _console = new();

    public ContainerMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private const string WebProject =
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n" +
        "  <PropertyGroup>\n" +
        "    <TargetFramework>net8.0</TargetFramework>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";

    private string Write(string relativePath, string content, Encoding? encoding = null) {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private async Task<ContainerMigrationService.MigrationPlan> PlanAsync(string dockerfile, params string[] projects) =>
        await new ContainerMigrationService(_console).PlanAsync(dockerfile, projects, default);

    private static XElement Group(ContainerMigrationService.MigrationPlan plan, string name) =>
        Assert.Single(plan.Elements, e => e.Name.LocalName == name);

    private static string? Property(ContainerMigrationService.MigrationPlan plan, string name) =>
        Group(plan, "PropertyGroup").Element(name)?.Value;

    private static List<XElement> Items(ContainerMigrationService.MigrationPlan plan, string name) =>
        plan.Elements.Where(e => e.Name.LocalName == "ItemGroup").SelectMany(g => g.Elements(name)).ToList();

    [Fact]
    public async Task Plan_VisualStudioDockerfile_WritesPortsAndEnvButNotSdkDefaults() {
        // The template Visual Studio generates: aspnet base, /app, dotnet App.dll - all SDK defaults.
        var project = Write("Api/Api.csproj", WebProject);
        var dockerfile = Write("Api/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base\n" +
            "WORKDIR /app\n" +
            "EXPOSE 5000\n" +
            "\n" +
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "WORKDIR /src\n" +
            "COPY [\"Api.csproj\", \"./\"]\n" +
            "RUN dotnet restore \"Api.csproj\"\n" +
            "COPY . .\n" +
            "RUN dotnet build \"Api.csproj\" -c Release -o /app/build\n" +
            "\n" +
            "FROM build AS publish\n" +
            "RUN dotnet publish \"Api.csproj\" -c Release -o /app/publish\n" +
            "\n" +
            "FROM base AS final\n" +
            "WORKDIR /app\n" +
            "COPY --from=publish /app/publish .\n" +
            "ENV ASPNETCORE_URLS=http://+:5000\n" +
            "ENTRYPOINT [\"dotnet\", \"Api.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Equal(project, plan.ProjectPath);
        Assert.Empty(plan.Unsupported);
        Assert.Equal("true", Property(plan, "EnableSdkContainerSupport"));
        Assert.Null(Property(plan, "ContainerBaseImage"));
        Assert.Null(Property(plan, "ContainerWorkingDirectory"));
        Assert.Null(Property(plan, "ContainerAppCommandInstruction"));
        Assert.Empty(Items(plan, "ContainerEntrypoint"));

        var port = Assert.Single(Items(plan, "ContainerPort"));
        Assert.Equal("5000", port.Attribute("Include")?.Value);
        Assert.Null(port.Attribute("Type"));

        var env = Assert.Single(Items(plan, "ContainerEnvironmentVariable"));
        Assert.Equal("ASPNETCORE_URLS", env.Attribute("Include")?.Value);
        Assert.Equal("http://+:5000", env.Attribute("Value")?.Value);

        Assert.Contains(plan.Notes, n => n.Contains("ContainerBaseImage not written"));
        Assert.Contains(plan.Notes, n => n.Contains("ENTRYPOINT matches"));
    }

    [Fact]
    public async Task Plan_ResolvesBuildArgsLabelsAndApphostEntrypoint() {
        // Hand-written AOT Dockerfile: the final stage derives from ${BASE_IMAGE}, not from `base`, so
        // base's USER must not leak in; the ARG default feeds FROM and the LABEL.
        var project = Write("Gk.Api/Gk.Api.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>$(NetVersion)</TargetFramework>\n" +
            "    <PublishAot>true</PublishAot>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");
        var dockerfile = Write("Gk.Api/Dockerfile",
            "ARG BASE_IMAGE=mcr.microsoft.com/dotnet/nightly/runtime-deps:8.0-alpine-aot\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base\n" +
            "USER app\n" +
            "WORKDIR /app\n" +
            "EXPOSE 8080\n" +
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN apt-get update \\\n" +
            "    && apt-get install -y clang zlib1g-dev\n" +
            "ARG RUNTIME_ID=linux-musl-x64\n" +
            "WORKDIR /src\n" +
            "COPY [\"Gk.Api/Gk.Api.csproj\", \"Gk.Api/\"]\n" +
            "COPY [\"Gk.Shared/Gk.Shared.csproj\", \"Gk.Shared/\"]\n" +
            "RUN --mount=type=cache,target=/root/.nuget dotnet restore -r $RUNTIME_ID \"./Gk.Api/./Gk.Api.csproj\"\n" +
            "COPY . .\n" +
            "WORKDIR \"/src/Gk.Api\"\n" +
            "FROM build AS publish\n" +
            "ARG RUNTIME_ID=linux-musl-x64\n" +
            "RUN dotnet publish -r $RUNTIME_ID \"./Gk.Api.csproj\" -c Release -o /app/publish /p:UseAppHost=true\n" +
            "FROM ${BASE_IMAGE} AS final\n" +
            "LABEL com.example.baseimage=\"${BASE_IMAGE}\" \\\n" +
            "      com.example.project=\"Sample Project\"\n" +
            "WORKDIR /app\n" +
            "EXPOSE 8080\n" +
            "EXPOSE 8081\n" +
            "COPY --from=publish /app/publish .\n" +
            "ENTRYPOINT [\"./Gk.Api\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Empty(plan.Unsupported);
        Assert.Equal("mcr.microsoft.com/dotnet/nightly/runtime-deps:8.0-alpine-aot", Property(plan, "ContainerBaseImage"));
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Empty(Items(plan, "ContainerEntrypoint"));
        Assert.Equal(["8080", "8081"], Items(plan, "ContainerPort").Select(p => p.Attribute("Include")?.Value));

        var labels = Items(plan, "ContainerLabel");
        Assert.Equal(2, labels.Count);
        Assert.Equal("mcr.microsoft.com/dotnet/nightly/runtime-deps:8.0-alpine-aot", labels[0].Attribute("Value")?.Value);
        Assert.Equal("Sample Project", labels[1].Attribute("Value")?.Value);

        Assert.Contains(plan.Notes, n => n.Contains("-r linux-musl-x64 /p:UseAppHost=true"));
        Assert.Contains(plan.Notes, n => n.Contains("apt-get"));
    }

    [Fact]
    public async Task Plan_InheritsUserWorkdirAndPortsFromParentStage() {
        var project = Write("Svc/Svc.csproj", WebProject);
        var dockerfile = Write("Svc/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS base\n" +
            "USER $APP_UID\n" +
            "WORKDIR /data\n" +
            "EXPOSE 8080/udp 9090\n" +
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish Svc.csproj -o /out\n" +
            "FROM base AS final\n" +
            "COPY --from=build /out/ .\n" +
            "ENTRYPOINT [\"dotnet\", \"/data/Svc.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Empty(plan.Unsupported);
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:8.0-alpine", Property(plan, "ContainerBaseImage"));
        Assert.Equal("$APP_UID", Property(plan, "ContainerUser"));
        Assert.Equal("/data", Property(plan, "ContainerWorkingDirectory"));

        var ports = Items(plan, "ContainerPort");
        Assert.Equal("udp", ports[0].Attribute("Type")?.Value);
        Assert.Equal("9090", ports[1].Attribute("Include")?.Value);
        Assert.Contains(plan.Notes, n => n.Contains("APP_UID"));
    }

    [Theory]
    [InlineData("ENTRYPOINT [\"./entrypoint.sh\"]\nCMD [\"dotnet\", \"App.dll\"]\n", "DefaultArgs", "./entrypoint.sh", "")]
    [InlineData("ENTRYPOINT [\"./entrypoint.sh\"]\n", "None", "./entrypoint.sh", "")]
    [InlineData("ENTRYPOINT [\"./entrypoint.sh\", \"--wait\"]\nCMD [\"--serve\", \"a;b\"]\n", "None", "./entrypoint.sh|--wait", "--serve|a%3Bb")]
    [InlineData("ENTRYPOINT ./run.sh --x\n", "None", "/bin/sh|-c|./run.sh --x", "")]
    [InlineData("CMD [\"dotnet\", \"App.dll\", \"--migrate\"]\n", "None", "", "dotnet|App.dll|--migrate")]
    public async Task Plan_MapsEntrypointAndCmdToSdkInstructions(string tail, string instruction, string entrypoint, string defaultArgs) {
        var project = Write("App/App.csproj", WebProject.Replace("Sdk.Web", "Sdk"));
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish App.csproj -o /app/publish\n" +
            "FROM mcr.microsoft.com/dotnet/runtime:8.0\n" +
            "COPY --from=build /app/publish /app\n" +
            tail);

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Equal(instruction, Property(plan, "ContainerAppCommandInstruction"));
        Assert.Equal(entrypoint, string.Join('|', Items(plan, "ContainerEntrypoint").Select(e => e.Attribute("Include")?.Value)));
        Assert.Equal(defaultArgs, string.Join('|', Items(plan, "ContainerDefaultArgs").Select(e => e.Attribute("Include")?.Value)));
    }

    [Fact]
    public async Task Plan_EntrypointKeepsACmdFromTheSameStageAndDropsAnInheritedOne() {
        var project = Write("App/App.csproj", WebProject);
        var sameStage = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base\n" +
            "CMD [\"--inherited\"]\n" +
            "FROM base AS final\n" +
            "CMD [\"--urls\", \"http://+:80\"]\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(sameStage, project);

        Assert.Equal(["--urls", "http://+:80"], Items(plan, "ContainerDefaultArgs").Select(e => e.Attribute("Include")?.Value));

        var inherited = Write("App/Dockerfile.inherited",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base\n" +
            "CMD [\"--inherited\"]\n" +
            "FROM base AS final\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        plan = await PlanAsync(inherited, project);

        Assert.Empty(Items(plan, "ContainerDefaultArgs"));
    }

    [Fact]
    public async Task Plan_AcceptsExecFormCopyOfThePublishOutput() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish App.csproj -o /app/publish\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "COPY --from=build --chown=app:app [\"/app/publish\", \"/app\"]\n" +
            "COPY --from=build [\"/app/publish\"]\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        var unsupported = Assert.Single(plan.Unsupported);
        Assert.Contains("no source and destination", unsupported);
    }

    [Fact]
    public async Task Plan_UnquotesUserAndLabelKeys() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "USER \"app\"\n" +
            "LABEL \"com.example.vendor\"=\"ACME\"\n" +
            "MAINTAINER ${AUTHOR}\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Equal("app", Property(plan, "ContainerUser"));
        var labels = Items(plan, "ContainerLabel");
        Assert.Equal("com.example.vendor", labels[0].Attribute("Include")?.Value);
        Assert.Equal("ACME", labels[0].Attribute("Value")?.Value);
        // Docker does not expand MAINTAINER, so neither does the migration.
        Assert.Equal("${AUTHOR}", labels[1].Attribute("Value")?.Value);
    }

    [Fact]
    public async Task Plan_DefaultCmdWithoutEntrypointWritesNothing() {
        var project = Write("App/App.csproj", WebProject.Replace("Sdk.Web", "Sdk"));
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish App.csproj -o /app/publish\n" +
            "FROM mcr.microsoft.com/dotnet/runtime:8.0\n" +
            "COPY --from=build /app/publish /app\n" +
            "CMD [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(Property(plan, "ContainerAppCommandInstruction"));
        Assert.Null(Property(plan, "ContainerBaseImage"));
        Assert.Single(plan.Elements); // only the PropertyGroup with EnableSdkContainerSupport
    }

    [Fact]
    public async Task Plan_ReportsRuntimeInstructionsTheSdkCannotExpress() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish App.csproj -o /app/publish\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "RUN apt-get update && apt-get install -y curl\n" +
            "COPY --from=build /app/publish /app\n" +
            "COPY --from=build /src/tools /app/tools\n" +
            "COPY appsettings.Production.json /app/\n" +
            "VOLUME /app/data\n" +
            "HEALTHCHECK CMD curl -f http://localhost/health\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Equal(5, plan.Unsupported.Count);
        Assert.Contains(plan.Unsupported, u => u.StartsWith("RUN apt-get"));
        Assert.Contains(plan.Unsupported, u => u.Contains("/src/tools is not the publish output"));
        Assert.Contains(plan.Unsupported, u => u.Contains("build context"));
        Assert.Contains(plan.Unsupported, u => u.StartsWith("VOLUME"));
        Assert.Contains(plan.Unsupported, u => u.StartsWith("HEALTHCHECK"));
        Assert.DoesNotContain(plan.Unsupported, u => u.Contains("/app/publish"));
        Assert.False(plan.CanApply(force: false));
        Assert.True(plan.CanApply(force: true));
    }

    [Fact]
    public async Task Plan_ResolvesProjectFromSolutionLevelDockerfile() {
        // Two projects with the same file name; the publish line's path decides.
        var wrong = Write("legacy/Api/Api.csproj", WebProject);
        var right = Write("src/Api/Api.csproj", WebProject);
        var worker = Write("src/Worker/Worker.csproj", WebProject);
        var dockerfile = Write("Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "COPY [\"src/Api/Api.csproj\", \"src/Api/\"]\n" +
            "COPY [\"src/Worker/Worker.csproj\", \"src/Worker/\"]\n" +
            "RUN dotnet publish \"src/Api/Api.csproj\" -o /app/publish\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "COPY --from=build /app/publish /app\n" +
            "ENTRYPOINT [\"dotnet\", \"Api.dll\"]\n");

        var plan = await PlanAsync(dockerfile, wrong, right, worker);

        Assert.Equal(right, plan.ProjectPath);
    }

    [Fact]
    public async Task Plan_FallsBackToEntrypointDllAndSiblingProject() {
        var project = Write("App/App.csproj", WebProject);
        var byDll = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "COPY bin/Release/net8.0/publish /app\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(byDll, project);
        Assert.Equal(project, plan.ProjectPath);

        // No project named anywhere: the only .csproj beside the Dockerfile is the answer.
        var other = Write("Other/Other.csproj", WebProject);
        var sibling = Write("Other/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"./start.sh\"]\n");
        plan = await PlanAsync(sibling, project, other);
        Assert.Equal(other, plan.ProjectPath);

        var orphan = Write("Dockerfile", "FROM alpine\n");
        plan = await PlanAsync(orphan, project, other);
        Assert.Null(plan.ProjectPath);
        Assert.Contains("which project", plan.SkipReason);
    }

    [Fact]
    public async Task Plan_SkipsProjectThatAlreadyHasContainerSettings() {
        var project = Write("App/App.csproj", WebProject.Replace("</PropertyGroup>", "<ContainerRepository>app</ContainerRepository></PropertyGroup>"));
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Contains("ContainerRepository", plan.SkipReason);
        Assert.Empty(plan.Elements);
    }

    [Fact]
    public async Task Apply_WritesGroupsInFileLayoutAndKeepsDockerfile() {
        var project = Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <TargetFramework>net8.0</TargetFramework>\r\n" +
            "        <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>\r\n" +
            "    </PropertyGroup>\r\n" +
            "\r\n" +
            "</Project>\r\n", new UTF8Encoding(true));
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0-noble\n" +
            "EXPOSE 8080\n" +
            "ENV Serilog__MinimumLevel=Warning\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");
        var plan = await PlanAsync(dockerfile, project);

        var written = await new ContainerMigrationService(_console).ApplyAsync(plan, deleteDockerfile: false, default);

        Assert.True(written);
        Assert.True(File.Exists(dockerfile));
        var text = await File.ReadAllTextAsync(project);
        Assert.Equal(
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <TargetFramework>net8.0</TargetFramework>\r\n" +
            "        <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>\r\n" +
            "    </PropertyGroup>\r\n" +
            "\r\n" +
            "    <!-- Container image settings migrated from Dockerfile -->\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\r\n" +
            "        <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0-noble</ContainerBaseImage>\r\n" +
            "    </PropertyGroup>\r\n" +
            "    <ItemGroup>\r\n" +
            "        <ContainerPort Include=\"8080\" />\r\n" +
            "        <ContainerEnvironmentVariable Include=\"Serilog__MinimumLevel\" Value=\"Warning\" />\r\n" +
            "    </ItemGroup>\r\n" +
            "\r\n" +
            "</Project>\r\n", text);
        Assert.True((await File.ReadAllBytesAsync(project)) is [0xEF, 0xBB, 0xBF, ..]);
    }

    [Fact]
    public async Task Apply_DeleteDockerfile_RemovesToolsSettingsAndFile() {
        var project = Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>\n" +
            "    <DockerfileContext>..\\..</DockerfileContext>\n" +
            "  </PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <PackageReference Include=\"Microsoft.VisualStudio.Azure.Containers.Tools.Targets\" Version=\"1.21.0\" />\n" +
            "  </ItemGroup>\n" +
            "  <ItemGroup>\n" +
            "    <PackageReference Include=\"Serilog\" Version=\"4.0.0\" />\n" +
            "    <None Include=\"Dockerfile\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n");
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");
        var plan = await PlanAsync(dockerfile, project);
        Assert.Equal(3, plan.DockerToolsReferences.Count);

        var written = await new ContainerMigrationService(_console).ApplyAsync(plan, deleteDockerfile: true, default);

        Assert.True(written);
        Assert.False(File.Exists(dockerfile));
        var text = await File.ReadAllTextAsync(project);
        Assert.Equal(
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <PackageReference Include=\"Serilog\" Version=\"4.0.0\" />\n" +
            "  </ItemGroup>\n" +
            "\n" +
            "  <!-- Container image settings migrated from Dockerfile -->\n" +
            "  <PropertyGroup>\n" +
            "    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n", text);
    }

    [Theory]
    [InlineData("${IMAGE}", "img:1")]
    [InlineData("$IMAGE-suffix", "img:1-suffix")]
    [InlineData("${MISSING:-fallback}", "fallback")]
    [InlineData("${IMAGE:+set}", "set")]
    [InlineData("${MISSING:+set}", "")]
    [InlineData("\\$IMAGE", "$IMAGE")]
    [InlineData("$APP_UID", "$APP_UID")]
    public void Substitute_ExpandsDockerfileVariableForms(string input, string expected) {
        var unresolved = new HashSet<string>();
        var result = DockerfileSubstitution.Substitute(input, new Dictionary<string, string> { ["IMAGE"] = "img:1" }, unresolved);

        Assert.Equal(expected, result);
        Assert.Equal(input.Contains("APP_UID"), unresolved.Contains("APP_UID"));
    }

    [Fact]
    public void ParseKeyValues_HandlesQuotedAndLegacyForms() {
        Assert.Equal([("A", "1"), ("B", "two words"), ("C", "x=y")],
            DockerfileSubstitution.ParseKeyValues("A=1 B=\"two words\" C='x=y'"));
        Assert.Equal([("PATH", "/usr/bin:$PATH extra")], DockerfileSubstitution.ParseKeyValues("PATH /usr/bin:$PATH extra"));
        Assert.Equal([("NAME", null)], DockerfileSubstitution.ParseKeyValues("NAME"));
    }
}
