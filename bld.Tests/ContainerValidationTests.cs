using bld.Services;
using System.Text;

namespace bld.Tests;

public class ContainerValidationTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bld-validate-{Guid.NewGuid():N}");
    private readonly TestConsole _console = new();

    public ContainerValidationTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string relativePath, string content) {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Project(string sdk, string tfm, string properties, string items = "") =>
        $"<Project Sdk=\"{sdk}\">\n" +
        "  <PropertyGroup>\n" +
        $"    <TargetFramework>{tfm}</TargetFramework>\n" +
        properties +
        "  </PropertyGroup>\n" +
        (items.Length > 0 ? "  <ItemGroup>\n" + items + "  </ItemGroup>\n" : string.Empty) +
        "</Project>\n";

    private async Task<ContainerValidationService.Report?> ValidateAsync(string project) =>
        await new ContainerValidationService(_console).ValidateAsync(project, default);

    private static ContainerValidationService.Finding Single(ContainerValidationService.Report? report, string setting) =>
        Assert.Single(report!.Findings, f => f.Setting == setting);

    [Fact]
    public async Task Validate_ProjectWithoutContainerSettings_IsNull() {
        var project = Write("Lib/Lib.csproj", Project("Microsoft.NET.Sdk", "net8.0", "    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\n"));

        Assert.Null(await ValidateAsync(project));
    }

    [Fact]
    public async Task Validate_PinnedFamilyVariantWithPlatform_BecomesContainerFamily() {
        // The image the SDK computes for an AOT net10.0 project, with a family and a platform suffix.
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk.Web", "net10.0",
            "    <PublishAot>true</PublishAot>\n" +
            "    <ContainerBaseImage>mcr.microsoft.com/dotnet/runtime-deps:10.0-azurelinux3.0-distroless-extra-amd64</ContainerBaseImage>\n"));

        var report = await ValidateAsync(project);

        var finding = Single(report, "ContainerBaseImage");
        Assert.Equal("replace with ContainerFamily=azurelinux3.0-distroless-extra", finding.Fix);
        Assert.Contains("-amd64 platform then follows the RuntimeIdentifier", finding.Message);

        Assert.True(await new ContainerValidationService(_console).ApplyAsync(report!, default));
        Assert.Equal(Project("Microsoft.NET.Sdk.Web", "net10.0",
            "    <PublishAot>true</PublishAot>\n" +
            "    <ContainerFamily>azurelinux3.0-distroless-extra</ContainerFamily>\n"), await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task Validate_PinnedSdkDefaultImage_IsRemoved() {
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0",
            "    <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0</ContainerBaseImage>\n" +
            "    <ContainerWorkingDirectory>/app</ContainerWorkingDirectory>\n"));

        var report = await ValidateAsync(project);

        Assert.Equal("remove", Single(report, "ContainerBaseImage").Fix);
        Assert.Equal("remove", Single(report, "ContainerWorkingDirectory").Fix);
        Assert.True(await new ContainerValidationService(_console).ApplyAsync(report!, default));
        Assert.Equal(Project("Microsoft.NET.Sdk.Web", "net8.0", string.Empty), await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task Apply_SkippedFinding_IsLeftAlone() {
        // --interactive declines per finding; the rest of the report is still written.
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0",
            "    <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0</ContainerBaseImage>\n" +
            "    <ContainerWorkingDirectory>/app</ContainerWorkingDirectory>\n"));
        var report = await ValidateAsync(project);
        Single(report, "ContainerBaseImage").Skip = true;

        Assert.Equal(1, report!.Fixable);
        Assert.True(await new ContainerValidationService(_console).ApplyAsync(report, default));
        Assert.Equal(Project("Microsoft.NET.Sdk.Web", "net8.0",
            "    <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0</ContainerBaseImage>\n"), await File.ReadAllTextAsync(project));
    }

    [Theory]
    // Another repository or version than the SDK would pick: reported, not rewritten.
    [InlineData("<ContainerBaseImage>mcr.microsoft.com/dotnet/runtime:8.0-alpine</ContainerBaseImage>", "ContainerBaseImage", "pins runtime")]
    [InlineData("<ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:9.0-alpine</ContainerBaseImage>", "ContainerBaseImage", "pins version 9.0")]
    // A family next to a pin is ignored by the SDK.
    [InlineData("<ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0-alpine</ContainerBaseImage><ContainerFamily>noble</ContainerFamily>", "ContainerFamily", "ignored while ContainerBaseImage is set")]
    [InlineData("<ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0</ContainerBaseImage><ContainerFamily>alpine</ContainerFamily>", "ContainerBaseImage", "would activate ContainerFamily=alpine")]
    [InlineData("<ContainerImageName>app</ContainerImageName><ContainerRepository>other</ContainerRepository>", "ContainerImageName", "overrides ContainerRepository")]
    [InlineData("<ContainerAppCommandInstruction>Default</ContainerAppCommandInstruction>", "ContainerAppCommandInstruction", "not one of DefaultArgs, Entrypoint, None")]
    [InlineData("<ContainerImageFormat>oci-index</ContainerImageFormat>", "ContainerImageFormat", "not one of Docker, OCI")]
    [InlineData("<LocalRegistry>Rancher</LocalRegistry>", "LocalRegistry", "not one of Docker, Podman, Wslc, MacOSContainer")]
    [InlineData("<ContainerImageTag>1.0;latest</ContainerImageTag>", "ContainerImageTag", "belongs in ContainerImageTags")]
    [InlineData("<ContainerImageTags>1.0;-beta</ContainerImageTags>", "ContainerImageTags", "'-beta' is not a valid tag")]
    [InlineData("<ContainerRepository>My.App</ContainerRepository>", "ContainerRepository", "not a valid image name")]
    [InlineData("<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers><ContainerRuntimeIdentifiers>linux-x64;linux-arm64</ContainerRuntimeIdentifiers>", "ContainerRuntimeIdentifiers", "linux-arm64 not in RuntimeIdentifiers")]
    [InlineData("<ContainerImage>app</ContainerImage>", "ContainerImage", "not a property or item the SDK reads")]
    public async Task Validate_ReportsWithoutFix(string properties, string setting, string message) {
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0", "    " + properties + "\n"));

        var report = await ValidateAsync(project);

        var finding = Single(report, setting);
        Assert.Contains(message, finding.Message);
        Assert.Null(finding.Fix);
    }

    [Fact]
    public async Task Validate_ObsoleteImageName_IsRenamed() {
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0", "    <ContainerImageName>my-app</ContainerImageName>\n"));

        var report = await ValidateAsync(project);

        Assert.Equal("rename to ContainerRepository", Single(report, "ContainerImageName").Fix);
        Assert.True(await new ContainerValidationService(_console).ApplyAsync(report!, default));
        Assert.Equal(Project("Microsoft.NET.Sdk.Web", "net8.0", "    <ContainerRepository>my-app</ContainerRepository>\n"), await File.ReadAllTextAsync(project));
    }

    [Fact]
    public async Task Validate_DeprecatedEntrypoint_IsRewrittenOnlyWhenTheAppCommandIsExact() {
        var items = "    <ContainerEntrypoint Include=\"./entrypoint.sh\" />\n    <ContainerEntrypointArgs Include=\"--wait\" />\n";
        var exact = Write("Exact/Exact.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0",
            "    <ContainerAppCommandInstruction>None</ContainerAppCommandInstruction>\n", items));

        var report = await ValidateAsync(exact);

        Assert.NotNull(Single(report, "ContainerEntrypoint").Fix);
        Assert.True(await new ContainerValidationService(_console).ApplyAsync(report!, default));
        Assert.Equal(Project("Microsoft.NET.Sdk.Web", "net8.0",
            "    <ContainerAppCommandInstruction>Entrypoint</ContainerAppCommandInstruction>\n",
            "    <ContainerAppCommand Include=\"./entrypoint.sh\" />\n    <ContainerAppCommandArgs Include=\"--wait\" />\n"), await File.ReadAllTextAsync(exact));

        // Without None the SDK's app command is the CMD behind the entrypoint; a rename would drop it.
        var inexact = Write("Inexact/Inexact.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0", string.Empty, items));
        report = await ValidateAsync(inexact);
        var finding = Single(report, "ContainerEntrypoint");
        Assert.Null(finding.Fix);
        Assert.Contains("ContainerDefaultArgs", finding.Message);
    }

    [Fact]
    public async Task Validate_ConditionedSettings_AreReportedButNotRewritten() {
        var project = Write("App/App.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk.Web\">\n" +
            "  <PropertyGroup>\n" +
            "    <TargetFramework>net8.0</TargetFramework>\n" +
            "  </PropertyGroup>\n" +
            "  <PropertyGroup Condition=\"'$(Configuration)' == 'Release'\">\n" +
            "    <ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:8.0-alpine</ContainerBaseImage>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");

        var report = await ValidateAsync(project);

        var finding = Single(report, "ContainerBaseImage");
        Assert.Contains("alpine variant", finding.Message);
        Assert.Null(finding.Fix);
        Assert.Equal(0, report!.Fixable);
    }

    [Fact]
    public async Task Validate_CleanProject_HasNoFindings() {
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk.Web", "net8.0",
            "    <ContainerFamily>alpine</ContainerFamily>\n" +
            "    <ContainerRepository>my-app</ContainerRepository>\n" +
            "    <ContainerImageTags>1.0;latest</ContainerImageTags>\n" +
            "    <ContainerRegistry>registry.example.com:5000</ContainerRegistry>\n" +
            "    <ContainerUser>root</ContainerUser>\n",
            "    <ContainerPort Include=\"8080\" />\n    <ContainerPort Include=\"53\" Type=\"udp\" />\n"));

        var report = await ValidateAsync(project);

        Assert.NotNull(report);
        Assert.Empty(report.Findings);
        Assert.Equal(7, report.SettingCount);
    }
}
