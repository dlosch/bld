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
        // GetFullPath turns the '/' in "Other/Other.csproj" into the platform separator, as the paths
        // the service finds by enumerating directories have it; the test compares the two.
        var path = Path.GetFullPath(Path.Combine(_root, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
        return path;
    }

    // willWrite: true matches a --apply/-i run, where the no-USER "keep root?" question is asked.
    private async Task<ContainerMigrationService.MigrationPlan> PlanAsync(string dockerfile, params string[] projects) =>
        await new ContainerMigrationService(_console, willWrite: true).PlanAsync(dockerfile, projects, default);

    private async Task<ContainerMigrationService.MigrationPlan> PlanAsync(bool? runAsRoot, string dockerfile, params string[] projects) =>
        await new ContainerMigrationService(_console, runAsRoot, willWrite: true).PlanAsync(dockerfile, projects, default);

    private static XElement Group(ContainerMigrationService.MigrationPlan plan, string name) =>
        Assert.Single(plan.Elements, e => e.Name.LocalName == name && e.Attribute("Condition") is null);

    private static string? Property(ContainerMigrationService.MigrationPlan plan, string name) =>
        Group(plan, "PropertyGroup").Element(name)?.Value;

    /// <summary>A property of the group that only applies to `dotnet publish`, which carries the Dockerfile's publish switches.</summary>
    private static string? PublishProperty(ContainerMigrationService.MigrationPlan plan, string name) =>
        plan.Elements.SingleOrDefault(e => e.Attribute("Condition")?.Value == "'$(_IsPublishing)' == 'true'")?.Element(name)?.Value;

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
        Assert.Empty(Items(plan, "ContainerAppCommand"));
        // No USER and no answer: the SDK's non-root default stays, and the plan says so.
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Contains(plan.Notes, n => n.Contains("no USER"));

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

        Assert.Equal("linux-musl-x64", PublishProperty(plan, "RuntimeIdentifier"));
        Assert.Equal("true", PublishProperty(plan, "UseAppHost"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("dotnet publish ran with"));
        Assert.Contains(plan.Notes, n => n.Contains("apt-get"));
    }

    /// <summary>Regression: a relative WORKDIR replaced the one before it instead of resolving against it.</summary>
    [Fact]
    public async Task Plan_ResolvesARelativeWorkdirAgainstThePreviousOne() {
        var project = Write("Svc/Svc.csproj", WebProject);
        var dockerfile = Write("Svc/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base\n" +
            "WORKDIR /data\n" +
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish Svc.csproj -o /out\n" +
            "FROM base AS final\n" +
            "WORKDIR bin\n" +
            "COPY --from=build /out/ .\n" +
            "ENTRYPOINT [\"dotnet\", \"/data/bin/Svc.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Equal("/data/bin", Property(plan, "ContainerWorkingDirectory"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("relative to the base image"));
    }

    [Theory]
    [InlineData("/app/../lib", "/lib")]
    [InlineData("/app/./bin/", "/app/bin")]
    [InlineData("/../..", "/")]
    [InlineData("/data", "/data")]
    public void NormalizeUnixPath_ResolvesDotSegmentsLikeDocker(string path, string expected) =>
        Assert.Equal(expected, bld.Services.ContainerMigrationService.NormalizeUnixPath(path));

    [Fact]
    public async Task Plan_NotesAFirstWorkdirThatIsRelativeToTheBaseImage() {
        var project = Write("Svc/Svc.csproj", WebProject);
        var dockerfile = Write("Svc/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish Svc.csproj -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final\n" +
            "WORKDIR srv\n" +
            "COPY --from=build /out/ .\n" +
            "ENTRYPOINT [\"dotnet\", \"Svc.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Contains(plan.Notes, n => n.Contains("WORKDIR srv is relative to the base image"));
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
        // The alpine variant of the SDK's own image: ContainerFamily keeps the variant without pinning the version.
        Assert.Null(Property(plan, "ContainerBaseImage"));
        Assert.Equal("alpine", Property(plan, "ContainerFamily"));
        // USER $APP_UID is the SDK's default user, not a user to pin, and not an unresolved build arg.
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Equal("/data", Property(plan, "ContainerWorkingDirectory"));

        var ports = Items(plan, "ContainerPort");
        Assert.Equal("udp", ports[0].Attribute("Type")?.Value);
        Assert.Equal("9090", ports[1].Attribute("Include")?.Value);
        Assert.Contains(plan.Notes, n => n.Contains("USER $APP_UID is the SDK's default user"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("build args without a default"));
    }

    // ContainerEntrypoint is deprecated since .NET 8: a custom ENTRYPOINT is the app command with the
    // Entrypoint instruction, CMD the default args. CMD alone stays None so it remains overridable.
    [Theory]
    [InlineData("ENTRYPOINT [\"./entrypoint.sh\"]\nCMD [\"dotnet\", \"App.dll\"]\n", "Entrypoint", "./entrypoint.sh", "dotnet|App.dll")]
    [InlineData("ENTRYPOINT [\"./entrypoint.sh\"]\n", "Entrypoint", "./entrypoint.sh", "")]
    [InlineData("ENTRYPOINT [\"./entrypoint.sh\", \"--wait\"]\nCMD [\"--serve\", \"a;b\"]\n", "Entrypoint", "./entrypoint.sh|--wait", "--serve|a%3Bb")]
    [InlineData("ENTRYPOINT ./run.sh --x\n", "Entrypoint", "/bin/sh|-c|./run.sh --x", "")]
    [InlineData("CMD [\"dotnet\", \"App.dll\", \"--migrate\"]\n", "None", "", "dotnet|App.dll|--migrate")]
    public async Task Plan_MapsEntrypointAndCmdToSdkInstructions(string tail, string instruction, string appCommand, string defaultArgs) {
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
        Assert.Equal(appCommand, string.Join('|', Items(plan, "ContainerAppCommand").Select(e => e.Attribute("Include")?.Value)));
        Assert.Equal(defaultArgs, string.Join('|', Items(plan, "ContainerDefaultArgs").Select(e => e.Attribute("Include")?.Value)));
        Assert.Empty(Items(plan, "ContainerEntrypoint"));
        Assert.Empty(Items(plan, "ContainerEntrypointArgs"));
    }

    [Fact]
    public async Task Plan_NoUser_AsksWhetherToKeepRoot() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        _console.ConfirmAnswers.Enqueue(true);
        var plan = await PlanAsync(dockerfile, project);
        Assert.Equal("root", Property(plan, "ContainerUser"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("no USER"));

        _console.ConfirmAnswers.Enqueue(false);
        plan = await PlanAsync(dockerfile, project);
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Contains(plan.Notes, n => n.Contains("no USER"));

        // --run-as-root answers for every Dockerfile; without a terminal the SDK default stays.
        plan = await PlanAsync(runAsRoot: true, dockerfile, project);
        Assert.Equal("root", Property(plan, "ContainerUser"));

        _console.CanPrompt = false;
        _console.ConfirmAnswers.Enqueue(true);
        plan = await PlanAsync(dockerfile, project);
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Single(_console.ConfirmAnswers);

        // An explicit USER is never asked about.
        var withUser = Write("App/Dockerfile.user", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nUSER app\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");
        _console.CanPrompt = true;
        plan = await PlanAsync(runAsRoot: true, withUser, project);
        Assert.Equal("app", Property(plan, "ContainerUser"));
    }

    // The standard Visual Studio / `dotnet new` template line since .NET 8. APP_UID is an ENV of the
    // Microsoft base images, never defined in the Dockerfile; it must not become a ContainerUser.
    [Theory]
    [InlineData("USER $APP_UID\n")]
    [InlineData("USER ${APP_UID}\n")]
    public async Task Plan_UserAppUid_IsTheSdkDefaultAndNotWritten(string userLine) {
        var project = Write("Api/Api.csproj", WebProject);
        var dockerfile = Write("Api/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base\n" +
            "WORKDIR /app\n" +
            "EXPOSE 8080\n" +
            userLine +
            "\n" +
            "FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "RUN dotnet publish \"Api.csproj\" -c Release -o /app/publish\n" +
            "\n" +
            "FROM base AS final\n" +
            "WORKDIR /app\n" +
            "COPY --from=build /app/publish .\n" +
            "ENTRYPOINT [\"dotnet\", \"Api.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Contains(plan.Notes, n => n.Contains("USER $APP_UID is the SDK's default user"));
        // Not listed as an unresolved build arg, and not asked about as a no-USER image.
        Assert.DoesNotContain(plan.Notes, n => n.Contains("build args without a default"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("no USER"));
        Assert.Empty(_console.ConfirmPrompts);
    }

    /// <summary>A4.2: a plain dry run writes nothing, so it must not ask "keep running as root?".</summary>
    [Fact]
    public async Task Plan_DryRun_NoUser_DoesNotPromptAndDefersToApply() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        // willWrite: false is the dry run; CanPrompt stays true to prove the mode, not the terminal, decides.
        var plan = await new ContainerMigrationService(_console, willWrite: false).PlanAsync(dockerfile, [project], default);

        Assert.Empty(_console.ConfirmPrompts);
        Assert.Null(Property(plan, "ContainerUser"));
        Assert.Contains(plan.Notes, n => n == "no USER: --apply asks whether to keep root; --run-as-root answers yes");
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk.Web", "mcr.microsoft.com/dotnet/aspnet:8.0-noble-chiseled", "noble-chiseled", null)]
    [InlineData("Microsoft.NET.Sdk.Web", "mcr.microsoft.com/dotnet/aspnet:8.0-alpine3.20", "alpine3.20", null)]
    [InlineData("Microsoft.NET.Sdk", "mcr.microsoft.com/dotnet/runtime:8.0-alpine", "alpine", null)]
    // Another repository than the SDK would pick, another version, or a Windows tag: the pin stays.
    [InlineData("Microsoft.NET.Sdk.Web", "mcr.microsoft.com/dotnet/runtime:8.0-alpine", null, "mcr.microsoft.com/dotnet/runtime:8.0-alpine")]
    [InlineData("Microsoft.NET.Sdk.Web", "mcr.microsoft.com/dotnet/aspnet:7.0-alpine", null, "mcr.microsoft.com/dotnet/aspnet:7.0-alpine")]
    [InlineData("Microsoft.NET.Sdk.Web", "mcr.microsoft.com/dotnet/aspnet:8.0-nanoserver-ltsc2022", null, "mcr.microsoft.com/dotnet/aspnet:8.0-nanoserver-ltsc2022")]
    public async Task Plan_PrefersContainerFamilyOverPinningTheSdkImage(string sdk, string image, string? family, string? baseImage) {
        var project = Write("App/App.csproj", WebProject.Replace("Microsoft.NET.Sdk.Web", sdk));
        var dockerfile = Write("App/Dockerfile", $"FROM {image}\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Equal(family, Property(plan, "ContainerFamily"));
        Assert.Equal(baseImage, Property(plan, "ContainerBaseImage"));
    }

    [Theory]
    [InlineData("<ContainerRuntimeIdentifiers>linux-x64;linux-arm64</ContainerRuntimeIdentifiers>", "ContainerRuntimeIdentifiers")]
    [InlineData("<ContainerImageTags>1.0;latest</ContainerImageTags>", "ContainerImageTags")]
    [InlineData("<ContainerImageName>app</ContainerImageName>", "ContainerImageName")]
    [InlineData("<LocalRegistry>Podman</LocalRegistry>", "LocalRegistry")]
    public async Task Plan_SkipsProjectWithAnySdkContainerProperty(string property, string expected) {
        var project = Write("App/App.csproj", WebProject.Replace("</PropertyGroup>", property + "</PropertyGroup>"));
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(dockerfile, project);

        Assert.Contains(expected, plan.SkipReason);
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
            "        <ContainerFamily>noble</ContainerFamily>\r\n" +
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

    private const string Net9WebProject =
        "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n" +
        "  <PropertyGroup>\n" +
        "    <TargetFramework>net9.0</TargetFramework>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";

    /// <summary>
    /// Regression: the self-contained switch only became a note, so runtime-deps was pinned while the
    /// SDK published framework-dependent onto it - an image without a .NET runtime, reported ready.
    /// </summary>
    [Fact]
    public async Task Plan_SelfContainedPublish_TakesTheSwitchesIntoAPublishOnlyGroup() {
        var project = Write("Svc/Svc.csproj", Net9WebProject);
        var dockerfile = Write("Svc/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build\n" +
            "WORKDIR /src\n" +
            "COPY . .\n" +
            "RUN dotnet publish Svc.csproj -c Release -r linux-musl-x64 --self-contained -p:PublishTrimmed=true --no-restore -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/runtime-deps:9.0-alpine\n" +
            "WORKDIR /app\n" +
            "COPY --from=build /out .\n" +
            "ENTRYPOINT [\"./Svc\"]\n");

        var plan = await PlanAsync(true, dockerfile, project);

        Assert.True(plan.CanApply(force: false));
        // runtime-deps is the SDK's own pick for a self-contained publish, so the family is enough.
        Assert.Null(Property(plan, "ContainerBaseImage"));
        Assert.Equal("alpine", Property(plan, "ContainerFamily"));
        Assert.Equal("linux-musl-x64", PublishProperty(plan, "RuntimeIdentifier"));
        Assert.Equal("true", PublishProperty(plan, "SelfContained"));
        Assert.Equal("true", PublishProperty(plan, "PublishTrimmed"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("dotnet publish ran with"));
    }

    [Theory]
    [InlineData("-r:linux-x64 --sc false", "RuntimeIdentifier", "linux-x64")]
    [InlineData("--runtime=linux-arm64", "RuntimeIdentifier", "linux-arm64")]
    [InlineData("--self-contained false", "SelfContained", "false")]
    [InlineData("--no-self-contained", "SelfContained", "false")]
    [InlineData("-p:InvariantGlobalization=true -p:TieredPGO=false", "TieredPGO", "false")]
    [InlineData("/p:UseAppHost=false", "UseAppHost", "false")]
    [InlineData("--property:PublishReadyToRun=true", "PublishReadyToRun", "true")]
    public async Task Plan_MapsPublishSwitchesToProperties(string switches, string property, string expected) {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            $"RUN dotnet publish App.csproj {switches} -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Equal(expected, PublishProperty(plan, property));
        Assert.Null(PublishProperty(plan, "Configuration"));
    }

    [Fact]
    public async Task Plan_KeepsUnresolvedPublishSwitchesAsANote() {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            "ARG RID\n" +
            "RUN dotnet publish App.csproj -r $RID -p:Version=$VERSION --arch x64 -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Null(PublishProperty(plan, "RuntimeIdentifier"));
        Assert.Null(PublishProperty(plan, "Version"));
        Assert.Contains(plan.Notes, n => n.Contains("dotnet publish ran with `-r $RID -p:Version=$VERSION --arch x64`"));
    }

    /// <summary>
    /// Regression: the publish line was cut at every `;`, `&amp;` and `|`, quoted or not, so
    /// `"-p:A=1;B=2"` lost B silently. The shell line is split into its commands at the unquoted
    /// operators only, and the exec form is read as the one command it is.
    /// </summary>
    [Theory]
    [InlineData("RUN cd /src && dotnet publish App.csproj \"-p:PublishTrimmed=true;InvariantGlobalization=true\" -o /out 2>&1 | tee publish.log; echo done\n")]
    [InlineData("RUN dotnet restore; dotnet publish App.csproj '-p:PublishTrimmed=true;InvariantGlobalization=true' -o /out || exit 1\n")]
    [InlineData("RUN [\"dotnet\", \"publish\", \"App.csproj\", \"-p:PublishTrimmed=true;InvariantGlobalization=true\", \"-o\", \"/out\"]\n")]
    public async Task Plan_SplitsTheShellLineAtUnquotedOperatorsOnly(string run) {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            run +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Equal("true", PublishProperty(plan, "PublishTrimmed"));
        Assert.Equal("true", PublishProperty(plan, "InvariantGlobalization"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("dotnet publish ran with"));
    }

    /// <summary>One pair of a -p switch that cannot be written keeps the whole switch out, as a note, instead of half of it in the project.</summary>
    [Fact]
    public async Task Plan_APropertySwitchWithOneUnresolvablePair_IsKeptOutAsAWhole() {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            "RUN dotnet publish App.csproj \"-p:PublishTrimmed=true;Version=$VERSION\" -p:InvariantGlobalization=true -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Null(PublishProperty(plan, "PublishTrimmed"));
        Assert.Equal("true", PublishProperty(plan, "InvariantGlobalization"));
        Assert.Contains(plan.Notes, n => n.Contains("dotnet publish ran with `-p:PublishTrimmed=true;Version=$VERSION`"));
    }

    /// <summary>Redirections belong to the shell, not to dotnet; a bare operator takes its target with it, a glued one leaves the word.</summary>
    [Theory]
    [InlineData("-o /out 2>&1")]
    [InlineData("-o /out &> publish.log")]
    [InlineData("-o /out > publish.log 2>&1")]
    [InlineData("-o /out>/dev/null")]
    [InlineData("-o /out 2>> errors.log < /dev/null")]
    public async Task Plan_IgnoresTheShellsRedirections(string tail) {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            $"RUN dotnet publish App.csproj -r linux-x64 {tail}\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Null(plan.SkipReason);
        Assert.Empty(plan.Unsupported);
        Assert.Equal("linux-x64", PublishProperty(plan, "RuntimeIdentifier"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("dotnet publish ran with"));
    }

    /// <summary>
    /// Regression: `dotnet` was only recognized as a bare word, so a Dockerfile calling it by path
    /// lost the publish switches silently and the image was published framework-dependent.
    /// </summary>
    [Theory]
    [InlineData("RUN /usr/share/dotnet/dotnet publish App.csproj -r linux-x64 --self-contained -o /out\n")]
    [InlineData("RUN ./dotnet publish App.csproj -r linux-x64 --self-contained -o /out\n")]
    [InlineData("RUN [\"/usr/bin/dotnet\", \"publish\", \"App.csproj\", \"-r\", \"linux-x64\", \"--self-contained\", \"-o\", \"/out\"]\n")]
    public async Task Plan_FindsDotnetCalledByPath(string run) {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            run +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Equal("linux-x64", PublishProperty(plan, "RuntimeIdentifier"));
        Assert.Equal("true", PublishProperty(plan, "SelfContained"));
    }

    /// <summary>
    /// Regression: the redirections were taken out after the quotes, so a quoted `&gt;` or `&lt;` cut
    /// the argument and `-p:Foo="A&gt;B"` wrote `A` into the project.
    /// </summary>
    [Theory]
    [InlineData("\"-p:Foo=A>B\"", "A>B")]
    [InlineData("'-p:Foo=A<B'", "A<B")]
    [InlineData("\"-p:Foo=A>B;Bar=1\"", "A>B")]
    public async Task Plan_KeepsAnOperatorInsideQuotes(string property, string expected) {
        var project = Write("App/App.csproj", Net9WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            $"RUN dotnet publish App.csproj {property} -o /out > publish.log 2>&1\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Equal(expected, PublishProperty(plan, "Foo"));
        Assert.DoesNotContain(plan.Notes, n => n.Contains("dotnet publish ran with"));
    }

    [Theory]
    [InlineData("a && b || c | d; e & f", new[] { "a", "b", "c", "d", "e", "f" })]
    [InlineData("dotnet publish \"-p:A=1;B=2\" 'x;y' -o /out 2>&1", new[] { "dotnet publish \"-p:A=1;B=2\" 'x;y' -o /out 2>&1" })]
    [InlineData("echo \\; still one; two", new[] { "echo \\; still one", "two" })]
    [InlineData("one\ntwo\n", new[] { "one", "two" })]
    // A backslash is literal inside single quotes, so the quote closes; inside double quotes it escapes.
    [InlineData("echo 'a\\' && dotnet publish", new[] { "echo 'a\\'", "dotnet publish" })]
    [InlineData("echo \"a\\\" && b\"; c", new[] { "echo \"a\\\" && b\"", "c" })]
    public void SplitShellCommands(string script, string[] expected) =>
        Assert.Equal(expected, DockerfileSubstitution.SplitShellCommands(script));

    [Theory]
    [InlineData("a 'b\\' c", new[] { "a", "'b\\'", "c" })]
    [InlineData("a \"b\\\" c\" d", new[] { "a", "\"b\\\" c\"", "d" })]
    public void Tokenize_ReadsABackslashAsLiteralInSingleQuotesOnly(string text, string[] expected) =>
        Assert.Equal(expected, DockerfileSubstitution.Tokenize(text));

    /// <summary>
    /// Regression: up to the .NET 7 SDK, -r without --self-contained published self-contained. Taken
    /// as framework-dependent, runtime-deps was pinned and the image had no runtime.
    /// </summary>
    [Theory]
    [InlineData("6.0", "true")]
    [InlineData("7.0-alpine", "true")]
    [InlineData("8.0", null)]
    public async Task Plan_RuntimeIdentifierWithoutSelfContained_FollowsTheSdkImagesDefault(string sdkTag, string? selfContained) {
        var project = Write("App/App.csproj", Net9WebProject.Replace("net9.0", "net8.0"));
        var dockerfile = Write("App/Dockerfile",
            $"FROM mcr.microsoft.com/dotnet/sdk:{sdkTag} AS build\n" +
            "RUN dotnet publish App.csproj -r linux-x64 -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/runtime-deps:8.0\n" +
            "COPY --from=build /out .\n" +
            "ENTRYPOINT [\"./App\"]\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Equal("linux-x64", PublishProperty(plan, "RuntimeIdentifier"));
        Assert.Equal(selfContained, PublishProperty(plan, "SelfContained"));
        if (selfContained is { }) {
            // Self-contained, runtime-deps is the SDK's own pick: no pin.
            Assert.Null(Property(plan, "ContainerBaseImage"));
            Assert.Contains(plan.Notes, n => n.Contains("the default up to the .NET 7 SDK"));
        }
        else {
            Assert.Equal("mcr.microsoft.com/dotnet/runtime-deps:8.0", Property(plan, "ContainerBaseImage"));
            Assert.DoesNotContain(plan.Notes, n => n.Contains("the default up to the .NET 7 SDK"));
        }
    }

    /// <summary>Regression: `EXPOSE 8000-8003` was written as one ContainerPort, which takes a single port.</summary>
    [Fact]
    public async Task Plan_ExpandsSmallPortRangesAndReportsWhatIsNotAPort() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "EXPOSE 8000-8003/udp 9000-9999 $PORT 443\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(false, dockerfile, project);

        var ports = Items(plan, "ContainerPort");
        Assert.Equal(["8000", "8001", "8002", "8003", "443"], ports.Select(p => p.Attribute("Include")?.Value));
        Assert.All(ports.Take(4), p => Assert.Equal("udp", p.Attribute("Type")?.Value));
        Assert.Contains(plan.Notes, n => n.Contains("EXPOSE 9000-9999 not written"));
        Assert.Contains(plan.Notes, n => n.Contains("EXPOSE $PORT not written"));
    }

    /// <summary>Regression: only .csproj was recognized, so F# and VB Dockerfiles were skipped.</summary>
    [Fact]
    public async Task Plan_ResolvesFSharpAndVisualBasicProjects() {
        var fsproj = Write("Fs/Fs.fsproj", Net9WebProject);
        var fsDockerfile = Write("Fs/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build\n" +
            "RUN dotnet publish Fs/Fs.fsproj -o /out\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "COPY --from=build /out .\n" +
            "ENTRYPOINT [\"dotnet\", \"Fs.dll\"]\n");
        var vbproj = Write("Vb/Vb.vbproj", Net9WebProject);
        var vbDockerfile = Write("Vb/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:9.0\n" +
            "ENTRYPOINT [\"dotnet\", \"Other.dll\"]\n");

        var found = await ProjectContainerScanner.FindProjectFilesAsync(_root);
        Assert.Equal([fsproj, vbproj], found.Order());

        Assert.Equal(fsproj, (await PlanAsync(false, fsDockerfile, [.. found])).ProjectPath);
        // Neither the publish line nor the ENTRYPOINT names it: the only project next to the Dockerfile.
        Assert.Equal(vbproj, (await PlanAsync(false, vbDockerfile, [.. found])).ProjectPath);
    }

    [Fact]
    public async Task Plan_MultiTargetingProject_UsesTheFrameworkOfTheImage() {
        var project = Write("App/App.csproj", WebProject.Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFrameworks>net8.0;net9.0</TargetFrameworks>"));
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:9.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        var plan = await PlanAsync(false, dockerfile, project);

        Assert.Null(Property(plan, "ContainerBaseImage"));
        Assert.Contains(plan.Notes, n => n.Contains("dotnet publish -f net9.0 /t:PublishContainer"));
    }

    /// <summary>
    /// Regression: the project file was read as XML only, so a TargetFramework from Directory.Build.props
    /// was unknown (the SDK default image got pinned) and inherited container settings were not seen.
    /// </summary>
    [Fact]
    public async Task Plan_Evaluated_SeesDirectoryBuildPropsAndInheritedSettings() {
        Write("Directory.Build.props",
            "<Project>\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "    <ContainerRegistry>registry.example.com</ContainerRegistry>\n" +
            "    <ContainerFamily>alpine</ContainerFamily>\n" +
            "  </PropertyGroup>\n" +
            "  <ItemGroup>\n" +
            "    <ContainerPort Include=\"8080\" />\n" +
            "  </ItemGroup>\n" +
            "</Project>\n");
        var project = Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />\n");
        var dockerfile = Write("App/Dockerfile",
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "EXPOSE 8080 9090\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        MSBuildService.RegisterMSBuildDefaults(_console, new bld.Models.CleaningOptions());
        var service = new ContainerMigrationService(_console, runAsRoot: false, willWrite: true,
            evaluate: (path, facts) => ContainerMigrationService.EvaluateProject(path, facts, new Dictionary<string, string>()));
        var plan = await service.PlanAsync(dockerfile, [project], default);

        Assert.Null(plan.SkipReason);
        // The inherited family would turn the Dockerfile's aspnet:8.0 into the alpine image.
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:8.0", Property(plan, "ContainerBaseImage"));
        Assert.Contains(plan.Notes, n => n.Contains("ContainerFamily=alpine, which would replace"));
        // 8080 already comes from Directory.Build.props; only the other port is written.
        Assert.Equal(["9090"], Items(plan, "ContainerPort").Select(p => p.Attribute("Include")?.Value));
        Assert.Contains(plan.Notes, n => n.Contains("ContainerRegistry") && n.Contains("Directory.Build.props"));
    }

    [Fact]
    public async Task Plan_Evaluated_TargetFrameworkFromDirectoryBuildPropsKeepsTheSdkDefaultImage() {
        Write("Directory.Build.props", "<Project><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>\n");
        var project = Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />\n");
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        MSBuildService.RegisterMSBuildDefaults(_console, new bld.Models.CleaningOptions());
        var service = new ContainerMigrationService(_console, runAsRoot: false, willWrite: true,
            evaluate: (path, facts) => ContainerMigrationService.EvaluateProject(path, facts, new Dictionary<string, string>()));
        var plan = await service.PlanAsync(dockerfile, [project], default);

        Assert.Null(Property(plan, "ContainerBaseImage"));
        Assert.Contains(plan.Notes, n => n.Contains("is what the SDK picks for net8.0"));
    }

    /// <summary>Regression: --delete-dockerfile removed a Dockerfile that docker-compose still built from.</summary>
    [Theory]
    [InlineData("docker-compose.yml", "services:\n  app:\n    build:\n      context: .\n      dockerfile: App/Dockerfile\n")]
    [InlineData("docker-compose.yml", "services:\n  app:\n    build: ./App\n")]
    [InlineData(".github/workflows/ci.yml", "steps:\n  - run: docker build -f App/Dockerfile -t app .\n")]
    [InlineData("App/build.sh", "docker build -t app .\n")]
    public async Task Apply_DeleteDockerfile_KeepsADockerfileThatIsStillUsed(string file, string content) {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");
        var user = Write(file, content);

        var plan = await new ContainerMigrationService(_console, runAsRoot: false).PlanAsync(dockerfile, [project], default, _root);
        Assert.Equal([user], plan.DockerfileReferences);

        Assert.True(await new ContainerMigrationService(_console).ApplyAsync(plan, deleteDockerfile: true, default));

        Assert.True(File.Exists(dockerfile));
        Assert.Empty(plan.Deleted);
        Assert.Contains("EnableSdkContainerSupport", await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task Plan_IgnoresFilesThatNameAnotherDockerfile() {
        var project = Write("App/App.csproj", WebProject);
        var dockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");
        Write("docker-compose.yml", "services:\n  other:\n    build:\n      context: .\n      dockerfile: Other/Dockerfile\n");
        Write("App/Properties/launchSettings.json", "{ \"profiles\": { \"Container (Dockerfile)\": { \"commandName\": \"Docker\" } } }\n");

        var plan = await new ContainerMigrationService(_console, runAsRoot: false).PlanAsync(dockerfile, [project], default, _root);

        Assert.Empty(plan.DockerfileReferences);
    }

    [Fact]
    public async Task Apply_DeleteDockerfile_RemovesTheDockerignoresOnlyItUsed() {
        var app = Write("App/App.csproj", WebProject);
        var appDockerfile = Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"App.dll\"]\n");
        var own = Write("App/Dockerfile.dockerignore", "bin/\n");
        var shared = Write(".dockerignore", "**/bin\n");
        var worker = Write("Worker/Worker.csproj", WebProject);
        var workerDockerfile = Write("Worker/Dockerfile", "FROM mcr.microsoft.com/dotnet/aspnet:8.0\nENTRYPOINT [\"dotnet\", \"Worker.dll\"]\n");
        var service = new ContainerMigrationService(_console, runAsRoot: false);

        var first = await service.PlanAsync(appDockerfile, [app, worker], default, _root);
        Assert.True(await service.ApplyAsync(first, deleteDockerfile: true, default));

        // Worker/Dockerfile still builds with the shared .dockerignore.
        Assert.Equal([appDockerfile, own], first.Deleted);
        Assert.True(File.Exists(shared));

        var second = await service.PlanAsync(workerDockerfile, [app, worker], default, _root);
        Assert.True(await service.ApplyAsync(second, deleteDockerfile: true, default));

        Assert.Equal([workerDockerfile, shared], second.Deleted);
        Assert.False(File.Exists(shared));
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
