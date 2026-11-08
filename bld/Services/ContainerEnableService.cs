using bld.Infrastructure;
using Spectre.Console;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static bld.Services.ContainerMigrationService;

namespace bld.Services;

/// <summary>What the user chose last time, kept under <see cref="BldHome.Root"/> as container.json.</summary>
internal sealed record ContainerProfile(string? Family, List<string>? RuntimeIdentifiers, string? Registry, string? ImageTags);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ContainerProfile))]
internal partial class ContainerProfileJsonContext : JsonSerializerContext;

/// <summary>
/// `containerize --enable`: makes the apps without container settings ones `dotnet publish
/// /t:PublishContainer` builds an image for. EnableSdkContainerSupport in each project is all the SDK
/// needs. The settings every app in a repo shares (the image family, the platform, the registry, the
/// tags) have no default worth guessing, so they are asked for once, remembered in the user's profile
/// for the next repo, and written to Directory.Build.props when the repo has more than one app.
/// </summary>
internal sealed class ContainerEnableService {
    private readonly IConsoleOutput _console;
    private readonly Func<string, ProjectFacts, ProjectFacts>? _evaluate;

    /// <param name="evaluate">Completes the facts read from the project file with the MSBuild evaluation; null reads the file only.</param>
    public ContainerEnableService(IConsoleOutput console, Func<string, ProjectFacts, ProjectFacts>? evaluate = null) {
        _console = console;
        _evaluate = evaluate;
    }

    public const string PropsFileName = "Directory.Build.props";
    public static string ProfilePath => Path.Combine(BldHome.Root, "container.json");

    /// <summary>A variant of the images the SDK computes, appended to their tag by ContainerFamily.</summary>
    internal sealed record Family(string Name, string Description, bool Musl = false) {
        public bool IsDefault => Name.Length == 0;
    }

    /// <summary>
    /// The variants the official runtime images come in. The SDK accepts any tag suffix, so --family
    /// takes other names too; the picker offers these.
    /// </summary>
    internal static readonly IReadOnlyList<Family> Families = [
        new("", "Debian: what the SDK picks, glibc, shell and package manager"),
        new("alpine", "Alpine: small, musl-based, needs linux-musl-* runtime identifiers", Musl: true),
        new("noble", "Ubuntu 24.04"),
        new("noble-chiseled", "Ubuntu 24.04 chiseled: no shell, no package manager, non-root"),
        new("noble-chiseled-extra", "Ubuntu 24.04 chiseled with ICU and tzdata (globalization, time zones)"),
        new("azurelinux3.0", "Azure Linux 3.0"),
        new("azurelinux3.0-distroless", "Azure Linux 3.0 distroless"),
        new("azurelinux3.0-distroless-extra", "Azure Linux 3.0 distroless with ICU and tzdata"),
    ];

    internal static readonly IReadOnlyList<string> GlibcRuntimeIdentifiers = ["linux-x64", "linux-arm64", "linux-arm"];
    internal static readonly IReadOnlyList<string> MuslRuntimeIdentifiers = ["linux-musl-x64", "linux-musl-arm64", "linux-musl-arm"];

    // win-x64, linux-musl-arm64, osx.12-arm64
    private static readonly Regex RuntimeIdentifierRegex = new(@"^[a-z][a-z0-9.]*(-[a-z0-9]+)+$", RegexOptions.Compiled);
    private static readonly Regex DotnetFrameworkRegex = new(@"^net(?<major>\d+)\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    internal static bool IsRuntimeIdentifier(string value) => RuntimeIdentifierRegex.IsMatch(value);

    internal static bool IsMusl(string runtimeIdentifier) => runtimeIdentifier.Contains("-musl-", StringComparison.OrdinalIgnoreCase);

    internal static Family? FindFamily(string? name) =>
        name is null ? null : Families.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    // ---- The shared settings ------------------------------------------------------------------

    /// <summary>
    /// The settings every app in a repo shares. Empty values mean the SDK's default. They are written
    /// as given, MSBuild syntax included: `$(Version)` or a property function in the tags is what the
    /// user means, not text to protect from evaluation.
    /// </summary>
    internal sealed class Choices {
        private string _family = string.Empty;
        private string _registry = string.Empty;
        private string _imageTags = string.Empty;

        /// <summary>The ContainerFamily; empty leaves the SDK's default image.</summary>
        public string Family { get => _family; set => _family = value.Trim(); }
        /// <summary>One: ContainerRuntimeIdentifier. Several: ContainerRuntimeIdentifiers, a multi-platform image. None: the SDK's default.</summary>
        public List<string> RuntimeIdentifiers { get; } = new();
        /// <summary>ContainerRegistry; empty pushes to the local daemon.</summary>
        public string Registry { get => _registry; set => _registry = value.Trim(); }
        /// <summary>ContainerImageTags, `;`-separated; empty is the SDK's `latest`. Empty entries (a trailing `;`) are dropped, since the SDK would try to tag with nothing.</summary>
        public string ImageTags {
            get => _imageTags;
            set => _imageTags = string.Join(";", value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        public bool IsEmpty => Family.Length == 0 && RuntimeIdentifiers.Count == 0 && Registry.Length == 0 && ImageTags.Length == 0;
        public bool Musl => FindFamily(Family)?.Musl == true || IsMusl(Family);

        /// <summary>The properties as written, by name; the ones that are empty are left out.</summary>
        public IEnumerable<(string Name, string Value)> Properties() {
            if (Family.Length > 0) yield return ("ContainerFamily", Family);
            if (RuntimeIdentifiers.Count == 1) yield return ("ContainerRuntimeIdentifier", RuntimeIdentifiers[0]);
            if (RuntimeIdentifiers.Count > 1) yield return ("ContainerRuntimeIdentifiers", string.Join(";", RuntimeIdentifiers));
            if (Registry.Length > 0) yield return ("ContainerRegistry", Registry);
            if (ImageTags.Length > 0) yield return ("ContainerImageTags", ImageTags);
        }

        public Choices Clone() {
            var copy = new Choices { Family = Family, Registry = Registry, ImageTags = ImageTags };
            copy.RuntimeIdentifiers.AddRange(RuntimeIdentifiers);
            return copy;
        }

        /// <summary>Takes what <paramref name="other"/> sets, keeping the rest.</summary>
        public void Overlay(Choices other) {
            if (other.Family.Length > 0) Family = other.Family;
            if (other.RuntimeIdentifiers.Count > 0) {
                RuntimeIdentifiers.Clear();
                RuntimeIdentifiers.AddRange(other.RuntimeIdentifiers);
            }
            if (other.Registry.Length > 0) Registry = other.Registry;
            if (other.ImageTags.Length > 0) ImageTags = other.ImageTags;
        }

        /// <summary>The unconditioned Container* properties of a project or props file, as choices.</summary>
        public static Choices Read(XDocument doc) {
            string Value(string name) => doc.ElementsNamed(name).LastOrDefault(e => !e.IsConditioned())?.Value.Trim() ?? string.Empty;
            var choices = new Choices { Family = Value("ContainerFamily"), Registry = Value("ContainerRegistry"), ImageTags = Value("ContainerImageTags") };
            var several = Value("ContainerRuntimeIdentifiers");
            var one = Value("ContainerRuntimeIdentifier");
            choices.RuntimeIdentifiers.AddRange(several.Length > 0
                ? several.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : one.Length > 0 ? [one] : []);
            return choices;
        }
    }

    /// <summary>The Directory.Build.props the projects under the root import, or the one to create there.</summary>
    internal sealed record SharedProps(string Path, bool Exists, Choices Existing);

    /// <summary>The nearest Directory.Build.props at or above <paramref name="directory"/>, as MSBuild finds it.</summary>
    internal static string? NearestProps(string directory) {
        for (var dir = Path.GetFullPath(directory); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
            var props = Path.Combine(dir, PropsFileName);
            if (File.Exists(props)) return props;
        }
        return null;
    }

    /// <exception cref="InvalidDataException">The props file the projects import is not well-formed XML.</exception>
    public static SharedProps FindSharedProps(string rootDirectory) {
        if (NearestProps(rootDirectory) is { } existing) {
            try {
                return new SharedProps(existing, true, Choices.Read(XDocument.Load(existing)));
            }
            catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException) {
                throw new InvalidDataException($"Could not read {existing}: {ex.Message}", ex);
            }
        }
        return new SharedProps(Path.Combine(Path.GetFullPath(rootDirectory), PropsFileName), false, new Choices());
    }

    // ---- The profile --------------------------------------------------------------------------

    /// <summary>The choices of the last interactive run; null when there is none. A file that cannot be read is reported and ignored.</summary>
    public Choices? LoadProfile(string path) {
        if (!File.Exists(path)) return null;
        try {
            var profile = JsonSerializer.Deserialize(File.ReadAllText(path), ContainerProfileJsonContext.Default.ContainerProfile);
            if (profile is null) return null;
            var choices = new Choices { Family = profile.Family ?? string.Empty, Registry = profile.Registry ?? string.Empty, ImageTags = profile.ImageTags ?? string.Empty };
            choices.RuntimeIdentifiers.AddRange((profile.RuntimeIdentifiers ?? []).Where(IsRuntimeIdentifier));
            return choices;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) {
            _console.WriteWarning($"Could not read {path}: {ex.Message}; the saved defaults are ignored.");
            return null;
        }
    }

    public static void SaveProfile(string path, Choices choices) {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var profile = new ContainerProfile(choices.Family, [.. choices.RuntimeIdentifiers], choices.Registry, choices.ImageTags);
        var temp = path + ".bldtmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(profile, ContainerProfileJsonContext.Default.ContainerProfile));
        File.Move(temp, path, overwrite: true);
    }

    // ---- The plan per project -----------------------------------------------------------------

    internal sealed class Plan {
        public required string ProjectPath { get; init; }
        public required ProjectFacts Project { get; set; }
        /// <summary>Why the project cannot (or need not) be enabled; null when it can.</summary>
        public string? SkipReason { get; set; }
        /// <summary>What the reader should know, independent of the choices.</summary>
        public List<string> Notes { get; } = new();
        /// <summary>The Container* properties the project already gets from an import, with the file; nothing is written for those.</summary>
        public Dictionary<string, (string Value, string File)> Inherited { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>The project's own RuntimeIdentifiers list; ContainerRuntimeIdentifiers must be among them.</summary>
        public List<string> DeclaredRuntimeIdentifiers { get; } = new();
        /// <summary>
        /// Whether the shared settings reach this project through the shared Directory.Build.props.
        /// False for a project with a Directory.Build.props of its own that does not import it.
        /// </summary>
        public bool ImportsSharedProps { get; set; } = true;

        /// <summary>The group to write into the project: the switch, and the shared settings unless they go to Directory.Build.props.</summary>
        public XElement Elements(Choices choices, bool sharedInProps) {
            var group = new XElement("PropertyGroup", new XElement("EnableSdkContainerSupport", "true"));
            if (!sharedInProps || !ImportsSharedProps) {
                foreach (var (name, value) in choices.Properties()) {
                    if (Inherited.TryGetValue(name, out var inherited) && inherited.Value == value) continue;
                    group.Add(new XElement(name, value));
                }
            }
            if (choices.RuntimeIdentifiers.Count > 1 && MissingDeclaredRuntimeIdentifiers(choices).Any()) {
                group.Add(new XElement("RuntimeIdentifiers", string.Join(";", DeclaredRuntimeIdentifiers.Union(choices.RuntimeIdentifiers, StringComparer.OrdinalIgnoreCase))));
            }
            return group;
        }

        /// <summary>What the choices imply for this project.</summary>
        public IEnumerable<string> Warnings(Choices choices, bool sharedInProps) {
            if (choices.RuntimeIdentifiers.Count > 1 && MissingDeclaredRuntimeIdentifiers(choices).Any()) {
                yield return "RuntimeIdentifiers is written: ContainerRuntimeIdentifiers must be among the project's runtime identifiers, so restore now covers these runtimes as well";
            }
            if (Project.SelfContained && choices.RuntimeIdentifiers.Count == 1) {
                yield return $"the project publishes self-contained, which needs the runtime identifier at publish time: dotnet publish -r {choices.RuntimeIdentifiers[0]} /t:PublishContainer";
            }
            if (sharedInProps && !ImportsSharedProps) {
                yield return $"the project has a {PropsFileName} of its own that does not import the shared one; the settings are written into the project";
            }
            foreach (var (name, value) in choices.Properties()) {
                if (Inherited.TryGetValue(name, out var inherited)) {
                    yield return inherited.Value == value
                        ? $"{name}={value} already comes from {inherited.File}; not written"
                        : sharedInProps && ImportsSharedProps
                            ? $"{name}={inherited.Value} from {inherited.File} is replaced"
                            : $"{name}={value} overrides {inherited.Value} from {inherited.File}";
                }
            }
        }

        private IEnumerable<string> MissingDeclaredRuntimeIdentifiers(Choices choices) =>
            choices.RuntimeIdentifiers.Where(r => !DeclaredRuntimeIdentifiers.Contains(r, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>What the family and the runtime identifiers imply for each other, independent of the project.</summary>
    public static IEnumerable<string> Warnings(Choices choices) {
        var musl = choices.RuntimeIdentifiers.Where(IsMusl).ToList();
        var glibc = choices.RuntimeIdentifiers.Where(r => !IsMusl(r)).ToList();
        if (choices.Musl && glibc.Count > 0) {
            yield return $"the {choices.Family} images are musl-based, so {string.Join(", ", glibc)} will not run on them; use {string.Join(", ", glibc.Select(r => r.Replace("linux-", "linux-musl-")))}";
        }
        if (!choices.Musl && musl.Count > 0) {
            yield return $"{string.Join(", ", musl)} need the alpine (musl) images; the {(choices.Family.Length > 0 ? choices.Family : "default Debian")} images are glibc-based";
        }
        if (choices.Family.Length > 0 && FindFamily(choices.Family) is null) {
            yield return $"{choices.Family} is not one of the official image families ({string.Join(", ", Families.Where(f => !f.IsDefault).Select(f => f.Name))}); written as given";
        }
    }

    /// <param name="shared">The props file the shared settings would go to; what it already sets counts as inherited when the project is not evaluated.</param>
    public async Task<Plan> PlanAsync(string projectPath, SharedProps shared, CancellationToken cancellationToken) {
        XDocument doc;
        using (var stream = File.OpenRead(projectPath)) {
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }
        string? Property(string name) {
            var value = doc.ElementsNamed(name).LastOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        bool IsTrue(string name) => Property(name)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        var project = ReadProject(doc, projectPath);
        var plan = new Plan { ProjectPath = projectPath, Project = project };
        plan.DeclaredRuntimeIdentifiers.AddRange(Property("RuntimeIdentifiers")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []);

        var sdk = doc.Root?.Attribute("Sdk")?.Value;
        if (sdk is null) {
            plan.SkipReason = "not an SDK-style project; SDK container publishing needs one";
            return plan;
        }
        if (project.ExistingContainerSettings.Count > 0) {
            plan.SkipReason = $"already has container settings ({string.Join(", ", project.ExistingContainerSettings)}); --validate checks them";
            return plan;
        }
        if (IsTrue("EnableSdkContainerSupport")) {
            plan.SkipReason = "container publishing is already enabled (EnableSdkContainerSupport)";
            return plan;
        }
        if (sdk.Equals("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase)) {
            plan.SkipReason = "a Blazor WebAssembly app is static files served by a host, not a process to run in a container";
            return plan;
        }
        if (IsTrue("IsTestProject") || doc.ElementsNamed("PackageReference").Any(e => string.Equals(e.Attribute("Include")?.Value, "Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase))) {
            plan.SkipReason = "a test project";
            return plan;
        }
        var outputType = Property("OutputType");
        var isExe = project.IsWeb
            || sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase)
            || outputType?.Equals("Exe", StringComparison.OrdinalIgnoreCase) == true;
        if (outputType?.Equals("WinExe", StringComparison.OrdinalIgnoreCase) == true) {
            plan.SkipReason = "a desktop app (OutputType WinExe)";
            return plan;
        }
        if (!isExe) {
            plan.SkipReason = "a library (no OutputType Exe); only an app runs in a container";
            return plan;
        }

        var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var evaluated = false;
        if (_evaluate is { }) {
            try {
                project = _evaluate(projectPath, project);
                plan.Project = project;
                evaluated = true;
                foreach (var setting in project.InheritedSettings.Where(s => s.Include is null)) {
                    plan.Inherited[setting.Name] = (setting.Value, Path.GetRelativePath(projectDir, setting.File));
                }
            }
            catch (Exception ex) {
                plan.Notes.Add($"could not evaluate the project ({ex.FormatMessage()}); read its file only, so a target framework from {PropsFileName} is unknown");
            }
        }
        // MSBuild imports the nearest Directory.Build.props only; one further up reaches the project
        // when that one imports it, which the evaluation knows. Without it, and for a file still to be
        // created, the nearest one decides: the projects are below the root, so a nearest file other
        // than the shared one is their own.
        var nearest = NearestProps(projectDir);
        plan.ImportsSharedProps = shared.Exists
            ? evaluated
                ? project.ImportedFiles.Contains(shared.Path, DirExt.PathComparer)
                : nearest is { } && DirExt.PathComparer.Equals(nearest, shared.Path)
            : nearest is null;
        if (!evaluated && shared.Exists && plan.ImportsSharedProps) {
            foreach (var (name, value) in shared.Existing.Properties()) {
                plan.Inherited[name] = (value, Path.GetRelativePath(projectDir, shared.Path));
            }
        }

        var framework = project.TargetFramework ?? (project.TargetFrameworks.Count == 1 ? project.TargetFrameworks[0] : null);
        if (framework is null && project.TargetFrameworks.Count > 1) {
            plan.Notes.Add($"the project targets {string.Join(";", project.TargetFrameworks)}; publish the container for one of them: dotnet publish -f <framework> /t:PublishContainer");
            framework = project.TargetFrameworks[^1];
        }
        if (framework is null) {
            plan.Notes.Add("target framework unknown (not in the project file); SDK container publishing needs .NET 6 or later");
        }
        else if (DotnetFrameworkRegex.Match(framework) is { Success: true } match) {
            var major = int.Parse(match.Groups["major"].Value);
            if (major < 6) {
                plan.SkipReason = $"targets {framework}; SDK container publishing needs .NET 6 or later";
                return plan;
            }
            if (major < 8) {
                plan.Notes.Add($"{framework} is out of support, and the built-in container publishing needs the .NET 8 SDK or later on the building machine (older SDKs need the Microsoft.NET.Build.Containers package)");
            }
        }
        else {
            plan.SkipReason = $"targets {framework}; SDK container publishing needs .NET 6 or later";
            return plan;
        }

        if (Directory.Exists(projectDir) && Directory.EnumerateFiles(projectDir).Select(Path.GetFileName).Any(n => DockerfileParser.IsDockerfileName(n!))) {
            plan.Notes.Add("a Dockerfile sits next to the project; --migrate carries its settings over instead of starting from the SDK defaults");
        }
        return plan;
    }

    // ---- The questions ------------------------------------------------------------------------

    /// <summary>Which of the shared settings the command line fixed; those are not asked.</summary>
    internal sealed record Given(bool Family, bool RuntimeIdentifiers, bool Registry, bool ImageTags, bool SharedInProps);

    /// <summary>
    /// Asks once for the settings every app shares, each starting at its default (the profile, then
    /// what Directory.Build.props sets, then the command line), and whether they go to
    /// Directory.Build.props. Returns the choices and that answer.
    /// </summary>
    public (Choices Choices, bool SharedInProps) AskChoices(Choices defaults, Given given, SharedProps shared, int appCount, bool sharedInPropsDefault) {
        var choices = defaults.Clone();

        if (!given.Family) {
            var families = Families.ToList();
            if (defaults.Family.Length > 0 && FindFamily(defaults.Family) is null) families.Insert(1, new Family(defaults.Family, "as set before"));
            var current = families.FirstOrDefault(f => f.Name.Equals(defaults.Family, StringComparison.OrdinalIgnoreCase)) ?? families[0];
            var prompt = new SelectionPrompt<Family>()
                .Title("Base image family (ContainerFamily)?")
                .UseConverter(f => Markup.Escape($"{(f.IsDefault ? "default" : f.Name)}: {f.Description}{(f == current ? " (your default)" : string.Empty)}"))
                .AddChoices(families.OrderBy(f => f != current));
            choices.Family = _console.Prompt(prompt).Name;
        }

        if (!given.RuntimeIdentifiers) {
            const string Default = "default";
            const string Several = "several";
            var available = choices.Musl ? MuslRuntimeIdentifiers : GlibcRuntimeIdentifiers;
            var current = defaults.RuntimeIdentifiers.Count switch {
                0 => Default,
                1 when available.Contains(defaults.RuntimeIdentifiers[0]) => defaults.RuntimeIdentifiers[0],
                1 => Default,
                _ => Several,
            };
            var prompt = new SelectionPrompt<string>()
                .Title("Runtime identifier (the image's platform)?")
                .UseConverter(c => Markup.Escape((c switch {
                    Default => "default: the project's RuntimeIdentifier, else linux-x64",
                    Several => "several: one multi-platform image (ContainerRuntimeIdentifiers)",
                    _ => c,
                }) + (c == current ? " (your default)" : string.Empty)))
                .AddChoices(new[] { Default }.Concat(available).Append(Several).OrderBy(c => c != current));
            var picked = _console.Prompt(prompt);
            choices.RuntimeIdentifiers.Clear();
            if (picked == Several) {
                var multi = new MultiSelectionPrompt<string>().Title("Platforms of the image?").Required().UseConverter(Markup.Escape);
                foreach (var rid in available) {
                    var item = multi.AddChoice(rid);
                    if (defaults.RuntimeIdentifiers.Contains(rid, StringComparer.OrdinalIgnoreCase)) item.Select();
                }
                choices.RuntimeIdentifiers.AddRange(_console.MultiPrompt(multi));
            }
            else if (picked != Default) {
                choices.RuntimeIdentifiers.Add(picked);
            }
        }

        if (!given.Registry) {
            choices.Registry = _console.Ask("Registry to push to (ContainerRegistry; empty: none, the image goes to the local daemon)?", defaults.Registry);
        }
        if (!given.ImageTags) {
            choices.ImageTags = _console.Ask("Image tags (ContainerImageTags, ';'-separated, $(Version) allowed; empty: the SDK's latest)?", defaults.ImageTags);
        }

        var sharedInProps = given.SharedInProps
            ? sharedInPropsDefault
            : !choices.IsEmpty && _console.Confirm(
                $"Write these settings to {shared.Path} ({(shared.Exists ? "exists" : "new")}), where every project below gets them, instead of each of the {appCount} app(s)?",
                defaultValue: sharedInPropsDefault);
        return (choices, sharedInProps);
    }

    public bool AskProject(Plan plan, string displayName) =>
        _console.Confirm($"    Enable container publishing for {displayName}?", defaultValue: true);

    // ---- Writing ------------------------------------------------------------------------------

    /// <summary>Appends the group to the project file in its own layout. Returns false when nothing was written.</summary>
    public async Task<bool> ApplyAsync(Plan plan, Choices choices, bool sharedInProps, CancellationToken cancellationToken) {
        try {
            var written = await XmlProjectFile.EditAsync(plan.ProjectPath, doc => {
                var project = doc.ElementsNamed("Project").FirstOrDefault() ?? doc.Root;
                if (project is null) {
                    _console.WriteWarning($"{plan.ProjectPath} has no <Project> element; nothing written.");
                    return false;
                }
                Append(project, " SDK container publishing: dotnet publish -c Release /t:PublishContainer ", plan.Elements(choices, sharedInProps));
                return true;
            }, cancellationToken);
            if (!written) _console.WriteWarning($"{plan.ProjectPath}: nothing written.");
            return written;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _console.WriteError($"Could not update {plan.ProjectPath}: {ex.FormatMessage()}");
            return false;
        }
    }

    /// <summary>The shared settings Directory.Build.props does not have yet, or has with another value.</summary>
    public static List<(string Name, string Value)> PropsChanges(SharedProps shared, Choices choices) =>
        choices.Properties().Where(p => !shared.Existing.Properties().Contains(p)).ToList();

    /// <summary>
    /// Writes the shared settings to Directory.Build.props: a value it already sets unconditioned is
    /// replaced in place, the rest go into a new group at the end; a missing file is created with just
    /// that group. Returns false when nothing was written.
    /// </summary>
    public async Task<bool> ApplyPropsAsync(SharedProps shared, Choices choices, CancellationToken cancellationToken) {
        var changes = PropsChanges(shared, choices);
        if (changes.Count == 0) return false;
        try {
            if (!shared.Exists) {
                var group = new XElement("PropertyGroup", changes.Select(c => new XElement(c.Name, c.Value)));
                var text = "<Project>\n\n  <!-- SDK container publishing: shared by every app below; each one sets EnableSdkContainerSupport -->\n  "
                    + WithLayout(group, "  ", "\n", 1) + "\n\n</Project>\n";
                await File.WriteAllTextAsync(shared.Path, text, new UTF8Encoding(false), cancellationToken);
                return true;
            }
            return await XmlProjectFile.EditAsync(shared.Path, doc => {
                var project = doc.ElementsNamed("Project").FirstOrDefault() ?? doc.Root;
                if (project is null) {
                    _console.WriteWarning($"{shared.Path} has no <Project> element; nothing written.");
                    return false;
                }
                var added = new XElement("PropertyGroup");
                foreach (var (name, value) in changes) {
                    if (doc.ElementsNamed(name).LastOrDefault(e => !e.IsConditioned()) is { } existing) existing.Value = value;
                    else added.Add(new XElement(name, value));
                }
                if (added.HasElements) Append(project, " SDK container publishing: shared by every app below; each one sets EnableSdkContainerSupport ", added);
                return true;
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            _console.WriteError($"Could not update {shared.Path}: {ex.FormatMessage()}");
            return false;
        }
    }

    /// <summary>A blank line, the comment and the group before the closing tag, in the file's layout.</summary>
    private static void Append(XElement project, string comment, XElement group) {
        var (indent, newline) = DetectLayout(project);
        var nodes = new List<XNode> {
            new XText(newline + newline + indent),
            new XComment(comment),
            new XText(newline + indent),
            WithLayout(group, indent, newline, 1),
        };
        if (project.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value)) {
            trailing.AddBeforeSelf(nodes);
        }
        else {
            nodes.Add(new XText(newline));
            project.Add(nodes);
        }
    }
}
