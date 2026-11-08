using bld.Services;
using System.Text;
using static bld.Services.ContainerEnableService;

namespace bld.Tests;

/// <summary>
/// containerize --enable: a project without container settings gets what `dotnet publish
/// /t:PublishContainer` needs; the settings every app shares are asked once, remembered in the profile
/// and written to Directory.Build.props or the projects.
/// </summary>
public class ContainerEnableTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bld-enable-{Guid.NewGuid():N}");
    private readonly TestConsole _console = new();

    public ContainerEnableTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private string Write(string relativePath, string content) {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private static string Project(string sdk, string tfm, string properties = "", string items = "") =>
        $"<Project Sdk=\"{sdk}\">\n" +
        "  <PropertyGroup>\n" +
        $"    <TargetFramework>{tfm}</TargetFramework>\n" +
        properties +
        "  </PropertyGroup>\n" +
        (items.Length > 0 ? "  <ItemGroup>\n" + items + "  </ItemGroup>\n" : string.Empty) +
        "</Project>\n";

    private const string WebApp = "Microsoft.NET.Sdk.Web";

    private SharedProps Shared() => FindSharedProps(_root);

    private async Task<Plan> PlanAsync(string project) =>
        await new ContainerEnableService(_console).PlanAsync(project, Shared(), default);

    private static Choices Choose(string family = "", string registry = "", string tags = "", params string[] rids) {
        var choices = new Choices { Family = family, Registry = registry, ImageTags = tags };
        choices.RuntimeIdentifiers.AddRange(rids);
        return choices;
    }

    private static string? Setting(Plan plan, Choices choices, string name, bool sharedInProps = false) =>
        plan.Elements(choices, sharedInProps).Element(name)?.Value;

    [Fact]
    public async Task Plan_WebApp_NeedsOnlyTheSwitch() {
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0")));

        Assert.Null(plan.SkipReason);
        Assert.Equal(["EnableSdkContainerSupport"], plan.Elements(new Choices(), false).Elements().Select(e => e.Name.LocalName));
        Assert.Empty(plan.Warnings(new Choices(), false));
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk", "    <OutputType>Exe</OutputType>\n")]
    [InlineData("Microsoft.NET.Sdk.Worker", "")]
    public async Task Plan_ConsoleAndWorkerApps_AreApps(string sdk, string properties) {
        var plan = await PlanAsync(Write("App/App.csproj", Project(sdk, "net9.0", properties)));

        Assert.Null(plan.SkipReason);
    }

    [Theory]
    [InlineData("Microsoft.NET.Sdk", "net8.0", "", "", "a library")]
    [InlineData("Microsoft.NET.Sdk", "net8.0", "    <OutputType>WinExe</OutputType>\n", "", "a desktop app")]
    [InlineData("Microsoft.NET.Sdk", "net8.0", "    <OutputType>Exe</OutputType>\n", "    <PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17.0.0\" />\n", "a test project")]
    [InlineData("Microsoft.NET.Sdk", "net8.0", "    <OutputType>Exe</OutputType>\n    <IsTestProject>true</IsTestProject>\n", "", "a test project")]
    [InlineData(WebApp, "net8.0", "    <ContainerFamily>alpine</ContainerFamily>\n", "", "already has container settings (ContainerFamily)")]
    [InlineData(WebApp, "net8.0", "    <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\n", "", "already enabled")]
    [InlineData("Microsoft.NET.Sdk.BlazorWebAssembly", "net8.0", "", "", "Blazor WebAssembly")]
    [InlineData("Microsoft.NET.Sdk", "net48", "    <OutputType>Exe</OutputType>\n", "", "targets net48")]
    [InlineData("Microsoft.NET.Sdk", "netcoreapp3.1", "    <OutputType>Exe</OutputType>\n", "", "targets netcoreapp3.1")]
    [InlineData("Microsoft.NET.Sdk", "net5.0", "    <OutputType>Exe</OutputType>\n", "", "targets net5.0")]
    public async Task Plan_SkipsWhatCannotRunInAContainer(string sdk, string tfm, string properties, string items, string reason) {
        var plan = await PlanAsync(Write("P/P.csproj", Project(sdk, tfm, properties, items)));

        Assert.NotNull(plan.SkipReason);
        Assert.Contains(reason, plan.SkipReason);
    }

    [Fact]
    public async Task Plan_OldStyleProject_IsSkipped() {
        var plan = await PlanAsync(Write("Old/Old.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n  </PropertyGroup>\n</Project>\n"));

        Assert.Contains("not an SDK-style project", plan.SkipReason);
    }

    [Fact]
    public async Task Plan_FamilyAndSeveralRids_MakeAMultiPlatformImage() {
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0")));
        var choices = Choose("alpine", rids: ["linux-musl-x64", "linux-musl-arm64"]);

        Assert.Equal("alpine", Setting(plan, choices, "ContainerFamily"));
        Assert.Null(Setting(plan, choices, "ContainerRuntimeIdentifier"));
        Assert.Equal("linux-musl-x64;linux-musl-arm64", Setting(plan, choices, "ContainerRuntimeIdentifiers"));
        // ContainerRuntimeIdentifiers must be among the project's runtime identifiers.
        Assert.Equal("linux-musl-x64;linux-musl-arm64", Setting(plan, choices, "RuntimeIdentifiers"));
        Assert.Contains(plan.Warnings(choices, false), w => w.Contains("RuntimeIdentifiers is written"));

        // Shared in Directory.Build.props, the project still needs its own RuntimeIdentifiers.
        Assert.Equal(["EnableSdkContainerSupport", "RuntimeIdentifiers"], plan.Elements(choices, true).Elements().Select(e => e.Name.LocalName));
    }

    [Fact]
    public async Task Plan_DeclaredRuntimeIdentifiersThatCoverTheImage_AreNotWrittenAgain() {
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0", "    <RuntimeIdentifiers>linux-x64;linux-arm64;win-x64</RuntimeIdentifiers>\n")));
        var choices = Choose(rids: ["linux-x64", "linux-arm64"]);

        Assert.Equal("linux-x64;linux-arm64", Setting(plan, choices, "ContainerRuntimeIdentifiers"));
        Assert.Null(Setting(plan, choices, "RuntimeIdentifiers"));
        Assert.Empty(plan.Warnings(choices, false));
    }

    /// <summary>MSBuild syntax in the tags is what the user means (a date stamp, the version), so it is written as given, not escaped.</summary>
    [Fact]
    public async Task Plan_AllSharedSettings_AreWrittenIntoTheProjectWithoutProps() {
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0")));
        var choices = Choose("noble-chiseled", " registry.example.com:5000 ", "$([System.DateTime]::Now.ToString('yyyy.M.d'));latest;", "linux-arm64");

        Assert.Equal("noble-chiseled", Setting(plan, choices, "ContainerFamily"));
        Assert.Equal("linux-arm64", Setting(plan, choices, "ContainerRuntimeIdentifier"));
        Assert.Equal("registry.example.com:5000", Setting(plan, choices, "ContainerRegistry"));
        // The trailing ';' would make the SDK tag with an empty name.
        Assert.Equal("$([System.DateTime]::Now.ToString('yyyy.M.d'));latest", Setting(plan, choices, "ContainerImageTags"));
        Assert.Equal(["EnableSdkContainerSupport"], plan.Elements(choices, true).Elements().Select(e => e.Name.LocalName));
    }

    [Theory]
    [InlineData("latest;", "latest")]
    [InlineData(" ; $(Version) ;; latest ; ", "$(Version);latest")]
    [InlineData(";", "")]
    public void ImageTags_DropEmptyEntries(string typed, string written) => Assert.Equal(written, new Choices { ImageTags = typed }.ImageTags);

    [Theory]
    [InlineData("alpine", "linux-x64", "musl-based")]
    [InlineData("", "linux-musl-x64", "need the alpine (musl) images")]
    [InlineData("noble", "linux-musl-arm64", "need the alpine (musl) images")]
    [InlineData("ubuntu-custom", "linux-x64", "not one of the official image families")]
    public void Warnings_FamilyAndRidDisagreeingOnLibc(string family, string rid, string warning) {
        Assert.Contains(Warnings(Choose(family, rids: rid)), w => w.Contains(warning));
    }

    [Fact]
    public async Task Plan_Notes_SelfContainedMultiTargetingAndDockerfile() {
        var project = Write("App/App.csproj", Project("Microsoft.NET.Sdk", "net8.0", "    <OutputType>Exe</OutputType>\n    <PublishAot>true</PublishAot>\n").Replace("<TargetFramework>net8.0</TargetFramework>", "<TargetFrameworks>net8.0;net9.0</TargetFrameworks>"));
        Write("App/Dockerfile", "FROM mcr.microsoft.com/dotnet/runtime-deps:9.0\n");

        var plan = await PlanAsync(project);

        Assert.Null(plan.SkipReason);
        Assert.Contains(plan.Notes, n => n.Contains("targets net8.0;net9.0"));
        Assert.Contains(plan.Notes, n => n.Contains("a Dockerfile sits next to the project"));
        Assert.Contains(plan.Warnings(Choose(rids: "linux-x64"), false), w => w.Contains("dotnet publish -r linux-x64 /t:PublishContainer"));
    }

    [Fact]
    public async Task Plan_Net6_IsEnabledWithANote() {
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net6.0")));

        Assert.Null(plan.SkipReason);
        Assert.Contains(plan.Notes, n => n.Contains("net6.0 is out of support"));
    }

    [Fact]
    public async Task Plan_WhatDirectoryBuildPropsAlreadySets_IsInheritedAndNotWrittenAgain() {
        Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <ContainerFamily>alpine</ContainerFamily>\n    <ContainerRegistry>ghcr.io</ContainerRegistry>\n  </PropertyGroup>\n</Project>\n");
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0")));
        var shared = Shared();

        Assert.True(shared.Exists);
        Assert.Equal("alpine", shared.Existing.Family);
        Assert.Equal("ghcr.io", shared.Existing.Registry);
        Assert.True(plan.ImportsSharedProps);

        // The same value is not repeated; a different one overrides it in the project, or replaces it in the props.
        var same = Choose("alpine", "ghcr.io", rids: "linux-musl-x64");
        Assert.Equal(["EnableSdkContainerSupport", "ContainerRuntimeIdentifier"], plan.Elements(same, false).Elements().Select(e => e.Name.LocalName));
        Assert.Contains(plan.Warnings(same, false), w => w.Contains("ContainerFamily=alpine already comes from ../Directory.Build.props"));
        var other = Choose("noble");
        Assert.Equal("noble", Setting(plan, other, "ContainerFamily"));
        Assert.Contains(plan.Warnings(other, false), w => w.Contains("ContainerFamily=noble overrides alpine from ../Directory.Build.props"));
        Assert.Contains(plan.Warnings(other, true), w => w.Contains("ContainerFamily=alpine from ../Directory.Build.props is replaced"));
        Assert.Equal([("ContainerFamily", "noble")], PropsChanges(shared, other));
        Assert.Empty(PropsChanges(shared, Choose("alpine", "ghcr.io")));
    }

    [Fact]
    public void FindSharedProps_MalformedFile_IsReportedNotThrownRaw() {
        Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n</Project>\n");

        var ex = Assert.Throws<InvalidDataException>(() => Shared());

        Assert.Contains("Directory.Build.props", ex.Message);
    }

    [Fact]
    public async Task Plan_ProjectWithItsOwnProps_GetsTheSettingsItself() {
        Write("Directory.Build.props", "<Project />\n");
        Write("Svc/Directory.Build.props", "<Project />\n");
        var own = await PlanAsync(Write("Svc/Svc.csproj", Project(WebApp, "net8.0")));
        var importing = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0")));
        var choices = Choose("alpine");

        Assert.False(own.ImportsSharedProps);
        Assert.True(importing.ImportsSharedProps);
        Assert.Equal("alpine", Setting(own, choices, "ContainerFamily", sharedInProps: true));
        Assert.Null(Setting(importing, choices, "ContainerFamily", sharedInProps: true));
        Assert.Contains(own.Warnings(choices, true), w => w.Contains("Directory.Build.props of its own"));
    }

    /// <summary>Evaluated, a project whose own Directory.Build.props imports the shared one is known to get the shared settings.</summary>
    [Fact]
    public async Task Plan_Evaluated_OwnPropsThatImportTheSharedOne_CountAsImporting() {
        Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <ContainerRegistry>ghcr.io</ContainerRegistry>\n  </PropertyGroup>\n</Project>\n");
        Write("Svc/Directory.Build.props", "<Project>\n  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />\n</Project>\n");
        Write("Own/Directory.Build.props", "<Project />\n");
        var importing = Write("Svc/Svc.csproj", Project(WebApp, "net8.0"));
        var own = Write("Own/Own.csproj", Project(WebApp, "net8.0"));

        MSBuildService.RegisterMSBuildDefaults(_console, new bld.Models.CleaningOptions());
        var service = new ContainerEnableService(_console, (path, facts) => ContainerMigrationService.EvaluateProject(path, facts, new Dictionary<string, string>()));
        var shared = Shared();
        var importingPlan = await service.PlanAsync(importing, shared, default);
        var ownPlan = await service.PlanAsync(own, shared, default);

        Assert.True(importingPlan.ImportsSharedProps);
        Assert.Equal("ghcr.io", importingPlan.Inherited["ContainerRegistry"].Value);
        Assert.False(ownPlan.ImportsSharedProps);
        Assert.Empty(ownPlan.Inherited);
    }

    [Fact]
    public async Task Apply_WritesTheGroupInTheFileLayout() {
        var project = Write("Api/Api.csproj",
            "\uFEFF<Project Sdk=\"Microsoft.NET.Sdk.Web\">\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <TargetFramework>net8.0</TargetFramework>\r\n" +
            "    </PropertyGroup>\r\n" +
            "</Project>\r\n");
        var plan = await PlanAsync(project);

        Assert.True(await new ContainerEnableService(_console).ApplyAsync(plan, Choose("alpine", rids: "linux-musl-x64"), false, default));

        var text = await File.ReadAllTextAsync(project);
        Assert.StartsWith("\uFEFF<Project", text);
        Assert.EndsWith(
            "    </PropertyGroup>\r\n" +
            "\r\n" +
            "    <!-- SDK container publishing: dotnet publish -c Release /t:PublishContainer -->\r\n" +
            "    <PropertyGroup>\r\n" +
            "        <EnableSdkContainerSupport>true</EnableSdkContainerSupport>\r\n" +
            "        <ContainerFamily>alpine</ContainerFamily>\r\n" +
            "        <ContainerRuntimeIdentifier>linux-musl-x64</ContainerRuntimeIdentifier>\r\n" +
            "    </PropertyGroup>\r\n" +
            "</Project>\r\n", text);

        // Enabled once, the project is skipped the next time.
        Assert.Contains("already has container settings (ContainerFamily, ContainerRuntimeIdentifier)", (await PlanAsync(project)).SkipReason);
    }

    [Fact]
    public async Task ApplyProps_CreatesTheFileWithTheSharedSettings() {
        var shared = Shared();
        Assert.False(shared.Exists);

        Assert.True(await new ContainerEnableService(_console).ApplyPropsAsync(shared, Choose("noble-chiseled", "ghcr.io", rids: ["linux-x64", "linux-arm64"]), default));

        Assert.Equal(
            "<Project>\n" +
            "\n" +
            "  <!-- SDK container publishing: shared by every app below; each one sets EnableSdkContainerSupport -->\n" +
            "  <PropertyGroup>\n" +
            "    <ContainerFamily>noble-chiseled</ContainerFamily>\n" +
            "    <ContainerRuntimeIdentifiers>linux-x64;linux-arm64</ContainerRuntimeIdentifiers>\n" +
            "    <ContainerRegistry>ghcr.io</ContainerRegistry>\n" +
            "  </PropertyGroup>\n" +
            "\n" +
            "</Project>\n", await File.ReadAllTextAsync(Path.Combine(_root, "Directory.Build.props")));
        Assert.Equal("noble-chiseled", Shared().Existing.Family);
        Assert.Equal(["linux-x64", "linux-arm64"], Shared().Existing.RuntimeIdentifiers);
    }

    [Fact]
    public async Task ApplyProps_ReplacesInPlaceAndAppendsTheRest() {
        Write("Directory.Build.props",
            "<Project>\n" +
            "  <PropertyGroup>\n" +
            "    <Company>Contoso</Company>\n" +
            "    <ContainerFamily>alpine</ContainerFamily>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n");

        Assert.True(await new ContainerEnableService(_console).ApplyPropsAsync(Shared(), Choose("noble", "ghcr.io"), default));

        Assert.Equal(
            "<Project>\n" +
            "  <PropertyGroup>\n" +
            "    <Company>Contoso</Company>\n" +
            "    <ContainerFamily>noble</ContainerFamily>\n" +
            "  </PropertyGroup>\n" +
            "\n" +
            "  <!-- SDK container publishing: shared by every app below; each one sets EnableSdkContainerSupport -->\n" +
            "  <PropertyGroup>\n" +
            "    <ContainerRegistry>ghcr.io</ContainerRegistry>\n" +
            "  </PropertyGroup>\n" +
            "</Project>\n", await File.ReadAllTextAsync(Path.Combine(_root, "Directory.Build.props")));

        // Nothing left to change: no write.
        Assert.False(await new ContainerEnableService(_console).ApplyPropsAsync(Shared(), Choose("noble", "ghcr.io"), default));
    }

    [Fact]
    public void Profile_RoundTripsAndIgnoresABrokenFile() {
        var path = Path.Combine(_root, "profile", "container.json");
        var service = new ContainerEnableService(_console);
        Assert.Null(service.LoadProfile(path));

        SaveProfile(path, Choose("alpine", "ghcr.io", "$(Version)", "linux-musl-x64", "linux-musl-arm64"));
        var loaded = service.LoadProfile(path);

        Assert.NotNull(loaded);
        Assert.Equal("alpine", loaded.Family);
        Assert.Equal(["linux-musl-x64", "linux-musl-arm64"], loaded.RuntimeIdentifiers);
        Assert.Equal("ghcr.io", loaded.Registry);
        Assert.Equal("$(Version)", loaded.ImageTags);

        File.WriteAllText(path, "{ not json");
        Assert.Null(service.LoadProfile(path));
        Assert.Contains(_console.Messages, m => m.Level == "Warning" && m.Message.Contains("saved defaults are ignored"));
    }

    [Fact]
    public void AskChoices_AsksEverythingOnce_WithTheDefaultsFirst_AndWhetherToShare() {
        var defaults = Choose("alpine", "ghcr.io", rids: "linux-musl-arm64");
        _console.PromptAnswers.Enqueue(Families.Single(f => f.Name == "noble"));
        _console.PromptAnswers.Enqueue("several");
        _console.MultiPromptAnswer = prompt => new List<string> { "linux-x64", "linux-arm64" };
        _console.TextAnswers.Enqueue("registry.example.com");
        _console.TextAnswers.Enqueue("$(Version);latest");
        _console.ConfirmAnswers.Enqueue(true);

        var (choices, shared) = new ContainerEnableService(_console).AskChoices(defaults, new Given(false, false, false, false, false), Shared(), appCount: 2, sharedInPropsDefault: true);

        Assert.Equal("noble", choices.Family);
        Assert.Equal(["linux-x64", "linux-arm64"], choices.RuntimeIdentifiers);
        Assert.Equal("registry.example.com", choices.Registry);
        Assert.Equal("$(Version);latest", choices.ImageTags);
        Assert.True(shared);
        Assert.Equal(["Prompt", "Prompt", "Ask", "Ask"], _console.Messages.Where(m => m.Level is "Prompt" or "Ask").Select(m => m.Level));
        Assert.Contains(_console.ConfirmPrompts, p => p.Contains("Directory.Build.props") && p.Contains("2 app(s)"));
        // The defaults were untouched.
        Assert.Equal("alpine", defaults.Family);
    }

    [Fact]
    public void AskChoices_EnterEverywhere_KeepsTheDefaults() {
        var defaults = Choose("alpine", "ghcr.io", "latest", "linux-musl-x64");
        _console.PromptAnswers.Enqueue(Families.Single(f => f.Name == "alpine"));
        _console.PromptAnswers.Enqueue("linux-musl-x64");

        var (choices, shared) = new ContainerEnableService(_console).AskChoices(defaults, new Given(false, false, false, false, false), Shared(), appCount: 1, sharedInPropsDefault: false);

        Assert.Equal("alpine", choices.Family);
        Assert.Equal(["linux-musl-x64"], choices.RuntimeIdentifiers);
        Assert.Equal("ghcr.io", choices.Registry);
        Assert.Equal("latest", choices.ImageTags);
        Assert.False(shared);
    }

    [Fact]
    public void AskChoices_GivenOptionsAreNotAsked_AndNothingChosenIsNotShared() {
        _console.PromptAnswers.Enqueue(Families[0]);
        _console.PromptAnswers.Enqueue("default");

        var (choices, shared) = new ContainerEnableService(_console).AskChoices(new Choices(), new Given(false, false, true, true, false), Shared(), appCount: 3, sharedInPropsDefault: true);

        Assert.True(choices.IsEmpty);
        Assert.False(shared);
        Assert.DoesNotContain(_console.Messages, m => m.Level == "Ask");
        Assert.Empty(_console.ConfirmPrompts);

        _console.Messages.Clear();
        var (given, sharedGiven) = new ContainerEnableService(_console).AskChoices(Choose("noble"), new Given(true, true, true, true, true), Shared(), appCount: 3, sharedInPropsDefault: true);
        Assert.Equal("noble", given.Family);
        Assert.True(sharedGiven);
        Assert.DoesNotContain(_console.Messages, m => m.Level is "Prompt" or "Ask");
    }

    [Fact]
    public async Task AskProject_NoMeansNo() {
        var plan = await PlanAsync(Write("Api/Api.csproj", Project(WebApp, "net8.0")));
        _console.ConfirmAnswers.Enqueue(false);

        Assert.False(new ContainerEnableService(_console).AskProject(plan, "Api/Api.csproj"));
        Assert.True(new ContainerEnableService(_console).AskProject(plan, "Api/Api.csproj"));
    }

    [Theory]
    [InlineData("linux-x64", true)]
    [InlineData("linux-musl-arm64", true)]
    [InlineData("osx.12-arm64", true)]
    [InlineData("linux", false)]
    [InlineData("Linux-X64", false)]
    public void IsRuntimeIdentifier(string value, bool expected) => Assert.Equal(expected, ContainerEnableService.IsRuntimeIdentifier(value));
}
