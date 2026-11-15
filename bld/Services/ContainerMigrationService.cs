using bld.Infrastructure;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static bld.Infrastructure.DockerfileParser;

namespace bld.Services;

/// <summary>
/// Turns a Dockerfile into the equivalent .NET SDK container properties on the project it builds.
/// Only the final stage (and the stages it derives FROM) describe the image; the build stages are
/// what `dotnet publish /t:PublishContainer` replaces. Everything the SDK has no property for (RUN in
/// the runtime image, VOLUME, HEALTHCHECK, files copied from the build context) is reported instead
/// of silently dropped, and blocks the migration unless forced.
/// </summary>
internal sealed class ContainerMigrationService {
    private readonly IConsoleOutput _console;
    private readonly bool? _runAsRoot;
    private readonly bool _willWrite;
    private readonly Func<string, ProjectFacts, ProjectFacts>? _evaluate;
    // Files that may run `docker build` or compose, per repository root; read once per run.
    private readonly Dictionary<string, List<(string Path, string Text)>> _buildScripts = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="runAsRoot">
    /// What to write for a Dockerfile without USER: true writes ContainerUser=root, false leaves the SDK
    /// default, null asks (and leaves the default when it cannot ask).
    /// </param>
    /// <param name="willWrite">
    /// Whether the run will write (apply or interactive). Only a writing run asks "keep running as root?";
    /// a plain dry run defers the question and notes that --apply would ask it.
    /// </param>
    /// <param name="evaluate">
    /// Completes the facts read from the project file with the MSBuild evaluation (see
    /// <see cref="EvaluateProject"/>); null reads the project file only, which needs no MSBuild registration.
    /// </param>
    public ContainerMigrationService(IConsoleOutput console, bool? runAsRoot = null, bool willWrite = false, Func<string, ProjectFacts, ProjectFacts>? evaluate = null) {
        _console = console;
        _runAsRoot = runAsRoot;
        _willWrite = willWrite;
        _evaluate = evaluate;
    }

    internal sealed class MigrationPlan {
        public string DockerfilePath { get; init; } = string.Empty;
        public string? ProjectPath { get; set; }
        /// <summary>Set when nothing can be written: no project, already containerized, no FROM.</summary>
        public string? SkipReason { get; set; }
        /// <summary>Instructions the SDK cannot express; the migration would lose them.</summary>
        public List<string> Unsupported { get; } = new();
        public List<string> Notes { get; } = new();
        /// <summary>The PropertyGroup/ItemGroup to add, without layout whitespace.</summary>
        public List<XElement> Elements { get; } = new();
        /// <summary>Visual Studio container-tools properties and package the project carries.</summary>
        public List<string> DockerToolsReferences { get; } = new();
        /// <summary>Compose files, pipelines and scripts that build from the Dockerfile; they keep it from being deleted.</summary>
        public List<string> DockerfileReferences { get; } = new();
        /// <summary>The repository (or scan) root the references were searched under.</summary>
        public string? RepositoryRoot { get; set; }
        /// <summary>What <see cref="ApplyAsync"/> deleted: the Dockerfile and the .dockerignore that only served it.</summary>
        public List<string> Deleted { get; } = new();

        public bool CanApply(bool force) => SkipReason is null && (Unsupported.Count == 0 || force);
    }

    private static readonly string[] DockerToolsProperties = [
        "DockerDefaultTargetOS", "DockerfileContext", "DockerfileFile", "DockerfileTag", "DockerfileBuildArguments",
        "DockerfileRunArguments", "DockerfileRunEnvironmentFiles", "DockerfileFastModeStage", "DockerComposeProjectPath",
        "ContainerDevelopmentMode",
    ];
    private const string DockerToolsPackage = "Microsoft.VisualStudio.Azure.Containers.Tools.Targets";

    // Every property and item the SDK reads (Microsoft.NET.Build.Containers.targets); ContainerImageName is
    // the obsolete spelling of ContainerRepository the SDK still honors.
    private static readonly string[] ContainerProperties = [
        "ContainerBaseImage", "ContainerFamily", "ContainerRuntimeIdentifier", "ContainerRuntimeIdentifiers",
        "ContainerRepository", "ContainerImageName", "ContainerImageTag", "ContainerImageTags", "ContainerImageFormat",
        "ContainerRegistry", "ContainerArchiveOutputPath", "LocalRegistry",
        "ContainerWorkingDirectory", "ContainerUser", "ContainerAppCommandInstruction", "ContainerPublishInParallel",
    ];
    private static readonly string[] ContainerItems = [
        "ContainerPort", "ContainerEnvironmentVariable", "ContainerLabel", "ContainerEntrypoint", "ContainerEntrypointArgs",
        "ContainerAppCommand", "ContainerAppCommandArgs", "ContainerDefaultArgs",
    ];

    private static readonly Regex ProjectTokenRegex = new(@"[^\s""'\[\],]+\.(?:cs|fs|vb)proj\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    internal static bool IsDotnetProject(string path) =>
        ProjectExtensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    // <repo>:<version>[-<family>]: the family is what ContainerFamily appends to the tag the SDK computes.
    private static readonly Regex DefaultImageRegex = new(@"^mcr\.microsoft\.com/dotnet/(?<repo>aspnet|runtime|runtime-deps):(?<version>\d+\.\d+)(-(?<family>[a-z0-9.-]+))?$", RegexOptions.Compiled);

    /// <param name="searchRoot">Where compose files and pipelines that build from the Dockerfile are searched when it is not inside a git repository.</param>
    public async Task<MigrationPlan> PlanAsync(string dockerfilePath, IReadOnlyList<string> projectFiles, CancellationToken cancellationToken, string? searchRoot = null) {
        var plan = new MigrationPlan { DockerfilePath = dockerfilePath };
        var info = Parse(await File.ReadAllLinesAsync(dockerfilePath, cancellationToken), dockerfilePath);

        if (info.StageDetails.Count == 0) {
            plan.SkipReason = "no FROM instruction";
            return plan;
        }

        var chain = ResolveChain(info, out var baseRef);
        var unresolved = new SortedSet<string>(StringComparer.Ordinal);
        var baseImage = DockerfileSubstitution.Substitute(baseRef, info.GlobalArgs, unresolved);

        var publishes = FindPublishCommands(info);
        plan.ProjectPath = ResolveProject(info, publishes, projectFiles, dockerfilePath);
        if (plan.ProjectPath is null) {
            plan.SkipReason = "could not tell which project it builds";
            return plan;
        }

        var project = await ReadProjectAsync(plan.ProjectPath, cancellationToken);
        if (project.ExistingContainerSettings.Count > 0) {
            plan.SkipReason = $"already has container settings ({string.Join(", ", project.ExistingContainerSettings)})";
            return plan;
        }
        plan.DockerToolsReferences.AddRange(project.DockerToolsReferences);
        if (_evaluate is { }) {
            try {
                project = _evaluate(plan.ProjectPath, project);
            }
            catch (Exception ex) {
                plan.Notes.Add($"could not evaluate the project ({ex.FormatMessage()}); read its file only, so values from Directory.Build.props and other imports are unknown");
            }
        }

        // What `dotnet publish` was told on the command line decides the image as much as the project
        // does: --self-contained needs runtime-deps, -r picks the platform.
        var dockerfileDir = Path.GetDirectoryName(dockerfilePath) ?? string.Empty;
        var publish = MergePublishCommands(publishes.Where(p => p.Verb == "publish"
            && (p.Project is null || string.Equals(ResolveProjectToken(p.Project, projectFiles, dockerfileDir), plan.ProjectPath, StringComparison.OrdinalIgnoreCase))));
        project = ApplyPublishSwitches(project, publish, baseImage, plan);

        // Fold the runtime stage chain, root stage first, the way Docker layers it.
        var values = new Dictionary<string, string>(info.GlobalArgs, StringComparer.Ordinal);
        var env = new List<(string Key, string Value)>();
        var labels = new List<(string Key, string Value)>();
        var ports = new List<(string Port, string Type)>();
        string? workDir = null, user = null;
        // USER $APP_UID (the .NET 8+ template line) is the SDK's own default user, not a user to pin.
        var appUidUser = false;
        List<string>? entrypoint = null, cmd = null;

        foreach (var stage in chain) {
            // Docker resets an inherited CMD when a stage sets ENTRYPOINT, but keeps a CMD written
            // earlier in the same stage.
            var cmdSetInStage = false;
            foreach (var instruction in stage.Instructions) {
                var args = instruction.Arguments;
                switch (instruction.Directive) {
                    case "ARG":
                        foreach (var (key, value) in DockerfileSubstitution.ParseKeyValues(args)) {
                            if (value is { }) values[key] = DockerfileSubstitution.Substitute(value, values, unresolved);
                        }
                        break;
                    case "ENV":
                        foreach (var (key, value) in DockerfileSubstitution.ParseKeyValues(args)) {
                            var resolved = DockerfileSubstitution.Substitute(value ?? string.Empty, values, unresolved);
                            values[key] = resolved;
                            env.RemoveAll(e => e.Key == key);
                            env.Add((key, resolved));
                        }
                        break;
                    case "LABEL":
                        foreach (var (key, value) in DockerfileSubstitution.ParseKeyValues(args)) {
                            var resolved = DockerfileSubstitution.Substitute(value ?? string.Empty, values, unresolved);
                            labels.RemoveAll(l => l.Key == key);
                            labels.Add((key, resolved));
                        }
                        break;
                    case "MAINTAINER":
                        // Docker does not expand variables in MAINTAINER, so the text is taken as written.
                        labels.RemoveAll(l => l.Key == "maintainer");
                        labels.Add(("maintainer", args));
                        break;
                    case "EXPOSE":
                        foreach (var token in DockerfileSubstitution.Substitute(args, values, unresolved).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) {
                            var slash = token.IndexOf('/');
                            var port = slash < 0 ? token : token[..slash];
                            var type = slash < 0 ? "tcp" : token[(slash + 1)..].ToLowerInvariant();
                            foreach (var single in ExpandPorts(port, plan.Notes)) {
                                if (!ports.Contains((single, type))) ports.Add((single, type));
                            }
                        }
                        break;
                    case "WORKDIR": {
                        // Docker resolves a relative WORKDIR against the one before it: /app then bin
                        // is /app/bin. The first one is relative to the base image's, which is not known
                        // here (/app on the .NET 8+ Microsoft images), so that one stays as written.
                        var next = DockerfileSubstitution.Substitute(DockerfileSubstitution.Unquote(args), values, unresolved);
                        if (next.StartsWith('/')) {
                            workDir = NormalizeUnixPath(next);
                        }
                        else if (workDir is { }) {
                            workDir = NormalizeUnixPath(workDir.TrimEnd('/') + "/" + next);
                        }
                        else {
                            workDir = next;
                            plan.Notes.Add($"WORKDIR {next} is relative to the base image's working directory; check ContainerWorkingDirectory");
                        }
                        break;
                    }
                    case "USER": {
                        // APP_UID is an ENV of the Microsoft base images, never defined in the Dockerfile,
                        // so it would stay unresolved and write a user that does not exist. The line is the
                        // SDK's default user; recognise it instead of substituting (so it is not reported
                        // as an unresolved build arg either).
                        var raw = DockerfileSubstitution.Unquote(args);
                        if (raw is "$APP_UID" or "${APP_UID}") {
                            user = null;
                            appUidUser = true;
                        }
                        else {
                            user = DockerfileSubstitution.Substitute(raw, values, unresolved);
                            appUidUser = false;
                        }
                        break;
                    }
                    case "ENTRYPOINT":
                        entrypoint = DockerfileSubstitution.ParseCommand(args);
                        if (!cmdSetInStage) cmd = null;
                        break;
                    case "CMD":
                        cmd = DockerfileSubstitution.ParseCommand(args);
                        cmdSetInStage = true;
                        break;
                    case "COPY":
                        if (!IsPublishOutputCopy(info, publishes, args, values, out var why)) {
                            plan.Unsupported.Add($"COPY {args} ({why})");
                        }
                        break;
                    case "HEALTHCHECK" when args.Equals("NONE", StringComparison.OrdinalIgnoreCase):
                        break;
                    case "RUN" or "ADD" or "VOLUME" or "HEALTHCHECK" or "SHELL" or "STOPSIGNAL" or "ONBUILD":
                        plan.Unsupported.Add($"{instruction.Directive} {Shorten(args)}");
                        break;
                }
            }
        }

        // Build stages that install tooling (clang for AOT, node for a SPA) describe the host the SDK
        // now builds on; say so, since that host is no longer the image.
        foreach (var stage in info.StageDetails.Except(chain)) {
            var setup = stage.Instructions.Where(i => i.Directive == "RUN")
                .Select(i => StripRunFlags(i.Arguments))
                .Where(c => !c.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase) && !c.StartsWith("echo ", StringComparison.OrdinalIgnoreCase))
                .Select(Shorten)
                .ToList();
            if (setup.Count > 0) {
                plan.Notes.Add($"build stage {stage.Name ?? stage.Index.ToString()} set up its environment with `{string.Join("` and `", setup)}`; the SDK now builds on the host, which needs the same tooling");
            }
        }

        var properties = new XElement("PropertyGroup");
        var items = new XElement("ItemGroup");

        // Console projects need the opt-in on older SDKs; the current SDK implies it for anything that
        // is not a library. Writing it is harmless either way.
        properties.Add(new XElement("EnableSdkContainerSupport", "true"));

        var family = SdkImageFamily(baseImage, project, out var architecture);
        if (family is null) {
            properties.Add(new XElement("ContainerBaseImage", EscapeProperty(baseImage)));
        }
        else if (family.Length == 0) {
            plan.Notes.Add($"ContainerBaseImage not written: {baseImage} is what the SDK picks for {project.TargetFramework}, and an explicit pin would not follow a target framework change");
        }
        else {
            // The SDK appends the family to the tag it computes, so this is the same image as the pin
            // would be, but the version still follows the target framework.
            properties.Add(new XElement("ContainerFamily", EscapeProperty(family)));
            plan.Notes.Add($"ContainerBaseImage not written: {baseImage} is the {family} variant of what the SDK picks for {project.TargetFramework}; ContainerFamily keeps the variant and follows a target framework change");
        }
        if (architecture is { }) {
            plan.Notes.Add($"{baseImage} names the {architecture} platform of a multi-platform tag; the SDK takes the platform from the RuntimeIdentifier (default: the SDK's own architecture on Linux), set ContainerRuntimeIdentifier to pin it");
        }

        if (workDir is { } && !IsDefaultWorkDir(workDir)) {
            properties.Add(new XElement("ContainerWorkingDirectory", EscapeProperty(workDir)));
        }
        if (user is { }) {
            properties.Add(new XElement("ContainerUser", EscapeProperty(user)));
        }
        else if (appUidUser) {
            plan.Notes.Add("USER $APP_UID is the SDK's default user (`app` on .NET 8+ Microsoft images); not written");
        }
        else {
            // Without USER the Dockerfile's image ran as root; the SDK runs .NET 8+ Microsoft images as
            // the non-root `app` user instead. That is the better default, but not a silent one. Only a
            // run that writes asks; a dry run would write nothing, so it defers the question to --apply.
            bool? root = _runAsRoot;
            if (root is null && _willWrite) {
                root = _console.CanPrompt && _console.Confirm($"{dockerfilePath} has no USER instruction, so its image ran as root. Keep running as root?", defaultValue: false);
            }
            if (root == true) {
                properties.Add(new XElement("ContainerUser", "root"));
            }
            else if (root == false) {
                plan.Notes.Add("no USER in the Dockerfile: the SDK's default user applies (non-root `app` on .NET 8+ Microsoft images); set ContainerUser=root to keep running as root");
            }
            else {
                plan.Notes.Add("no USER: --apply asks whether to keep root; --run-as-root answers yes");
            }
        }

        var entrypointIsDefault = entrypoint is { } && IsDefaultAppCommand(entrypoint, project);
        var cmdIsDefault = cmd is { } && IsDefaultAppCommand(cmd, project);
        if (entrypoint is { } && !entrypointIsDefault) {
            // ContainerEntrypoint is deprecated since .NET 8. With the Entrypoint instruction the app
            // command is the ENTRYPOINT and ContainerDefaultArgs the CMD, which is the Dockerfile verbatim.
            foreach (var part in entrypoint) items.Add(new XElement("ContainerAppCommand", new XAttribute("Include", EscapeItemSpec(part))));
            properties.Add(new XElement("ContainerAppCommandInstruction", "Entrypoint"));
            if (cmd is { }) {
                foreach (var part in cmd) items.Add(new XElement("ContainerDefaultArgs", new XAttribute("Include", EscapeItemSpec(part))));
            }
            else {
                plan.Notes.Add("ENTRYPOINT replaces the SDK's app command; the entrypoint must start the application itself");
            }
        }
        else {
            if (entrypointIsDefault) plan.Notes.Add("ENTRYPOINT matches the SDK's default app command; not written");
            if (cmd is { } && !cmdIsDefault) {
                if (entrypoint is null) {
                    // No ENTRYPOINT means CMD is the command. None keeps the app command out of the image,
                    // so CMD stays overridable exactly as `docker run <image> <command>` had it.
                    properties.Add(new XElement("ContainerAppCommandInstruction", "None"));
                    plan.Notes.Add("CMD without ENTRYPOINT is written as ContainerDefaultArgs with ContainerAppCommandInstruction=None: the image has no ENTRYPOINT and the SDK's app command is not added");
                }
                foreach (var part in cmd) items.Add(new XElement("ContainerDefaultArgs", new XAttribute("Include", EscapeItemSpec(part))));
            }
            else if (cmdIsDefault) {
                plan.Notes.Add("CMD matches the SDK's default app command; not written");
            }
        }

        foreach (var (port, type) in ports) {
            var element = new XElement("ContainerPort", new XAttribute("Include", port));
            if (type != "tcp") element.Add(new XAttribute("Type", type));
            items.Add(element);
        }
        foreach (var (key, value) in env) {
            items.Add(new XElement("ContainerEnvironmentVariable", new XAttribute("Include", EscapeItemSpec(key)), new XAttribute("Value", EscapeMetadata(value))));
        }
        foreach (var (key, value) in labels) {
            items.Add(new XElement("ContainerLabel", new XAttribute("Include", EscapeItemSpec(key)), new XAttribute("Value", EscapeMetadata(value))));
        }

        ReconcileInheritedSettings(properties, items, project, baseImage, plan);

        plan.Elements.Add(properties);
        if (items.HasElements) plan.Elements.Add(items);
        if (PublishPropertyGroup(publish) is { } publishGroup) plan.Elements.Add(publishGroup);

        if (publish.ExtraArguments.Count > 0) {
            plan.Notes.Add($"dotnet publish ran with `{string.Join(" ", publish.ExtraArguments)}`; pass the same arguments to `dotnet publish /t:PublishContainer` or set the properties in the project");
        }
        if (unresolved.Count > 0) {
            plan.Notes.Add($"build args without a default were left as written: {string.Join(", ", unresolved)}");
        }
        if (plan.DockerToolsReferences.Count > 0) {
            plan.Notes.Add($"Visual Studio container tools settings present ({string.Join(", ", plan.DockerToolsReferences)}); removed only together with the Dockerfile");
        }

        plan.RepositoryRoot = FindRepositoryRoot(dockerfileDir) ?? searchRoot ?? dockerfileDir;
        plan.DockerfileReferences.AddRange(FindDockerfileReferences(dockerfilePath, plan.RepositoryRoot));
        if (plan.DockerfileReferences.Count > 0) {
            plan.Notes.Add($"{Path.GetFileName(dockerfilePath)} is used by {string.Join(", ", plan.DockerfileReferences.Select(r => Path.GetRelativePath(plan.RepositoryRoot, r)))}; it is kept even with --delete-dockerfile");
        }

        return plan;
    }

    /// <summary>
    /// Writes the plan's elements into the project. With <paramref name="deleteDockerfile"/> the
    /// Dockerfile and the Visual Studio container-tools settings that only make sense with it go too.
    /// </summary>
    public async Task<bool> ApplyAsync(MigrationPlan plan, bool deleteDockerfile, CancellationToken cancellationToken) {
        if (plan.ProjectPath is null) return false;
        var projectPath = plan.ProjectPath;
        var projectDir = Path.GetDirectoryName(projectPath) ?? string.Empty;
        var relativeDockerfile = Path.GetRelativePath(projectDir, plan.DockerfilePath).Replace('\\', '/');
        if (deleteDockerfile && plan.DockerfileReferences.Count > 0) {
            // Deleting it would break the compose file or pipeline that still builds from it; the
            // Visual Studio settings stay with it.
            _console.WriteWarning($"Kept {plan.DockerfilePath}: used by {string.Join(", ", plan.DockerfileReferences)}.");
            deleteDockerfile = false;
        }

        try {
            var written = await XmlProjectFile.EditAsync(projectPath, doc => {
                var project = doc.ElementsNamed("Project").FirstOrDefault() ?? doc.Root;
                if (project is null) {
                    _console.WriteWarning($"{projectPath} has no <Project> element; nothing written.");
                    return false;
                }

                var (indent, newline) = DetectLayout(project);
                // A blank line, the comment, then each group on its own line at the file's indent.
                var nodes = new List<XNode> {
                    new XText(newline + newline + indent),
                    new XComment($" Container image settings migrated from {relativeDockerfile} "),
                };
                foreach (var element in plan.Elements) {
                    nodes.Add(new XText(newline + indent));
                    nodes.Add(WithLayout(element, indent, newline, 1));
                }

                // Keep the file's closing layout: insert before the whitespace that precedes </Project>.
                if (project.LastNode is XText trailing && string.IsNullOrWhiteSpace(trailing.Value)) {
                    trailing.AddBeforeSelf(nodes);
                }
                else {
                    nodes.Add(new XText(newline));
                    project.Add(nodes);
                }

                if (deleteDockerfile) RemoveDockerToolsSettings(project, relativeDockerfile);
                return true;
            }, cancellationToken);

            if (!written) return false;

            if (deleteDockerfile) {
                File.Delete(plan.DockerfilePath);
                plan.Deleted.Add(plan.DockerfilePath);
                foreach (var ignore in OrphanedDockerignores(plan.DockerfilePath, plan.RepositoryRoot)) {
                    File.Delete(ignore);
                    plan.Deleted.Add(ignore);
                }
            }
            return true;
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {projectPath}: {ex.FormatMessage()}", ex);
            return false;
        }
    }

    // ---- Dockerfile analysis -------------------------------------------------------------------

    /// <summary>The final stage and every stage it derives FROM, root first; the root's FROM is the base image.</summary>
    private static List<DockerStage> ResolveChain(DockerfileInfo info, out string baseRef) {
        var chain = new List<DockerStage>();
        var stage = info.StageDetails[^1];
        while (true) {
            chain.Insert(0, stage);
            var parent = FindStage(info, stage.BaseRef);
            if (parent is null || chain.Contains(parent)) {
                baseRef = stage.BaseRef;
                return chain;
            }
            stage = parent;
        }
    }

    private static DockerStage? FindStage(DockerfileInfo info, string reference) {
        if (int.TryParse(reference, out var index)) {
            return index >= 0 && index < info.StageDetails.Count ? info.StageDetails[index] : null;
        }
        return info.StageDetails.FirstOrDefault(s => s.Name is { } && s.Name.Equals(reference, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// One `dotnet publish`/`dotnet build` line: the project and output it names, and the switches that
    /// change what is published (framework, runtime identifier, self-contained, -p properties).
    /// </summary>
    private sealed record PublishCommand(DockerStage Stage, string Verb, string? Project, string? Output, List<string> ExtraArguments) {
        public string? Framework { get; set; }
        public string? RuntimeIdentifier { get; set; }
        public bool? SelfContained { get; set; }
        /// <summary>SelfContained came from the SDK's old default (see <see cref="ImpliesSelfContained"/>), not from a switch.</summary>
        public bool ImplicitSelfContained { get; set; }
        public List<(string Name, string Value)> Properties { get; } = new();
    }

    private static readonly Regex SdkImageVersionRegex = new(@"dotnet/(?:nightly/)?sdk:(?<major>\d+)\.\d+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Up to the .NET 7 SDK, `-r` without `--self-contained` published self-contained (with warning
    /// NETSDK1179); the .NET 8 SDK turned that default around. A Dockerfile building on an older SDK
    /// image meant self-contained, so the migration has to say so explicitly.
    /// </summary>
    internal static bool ImpliesSelfContained(string sdkImage) =>
        SdkImageVersionRegex.Match(sdkImage) is { Success: true } match && int.Parse(match.Groups["major"].Value) < 8;

    // Switches that change how, not what, is published.
    private static readonly string[] IgnoredPublishSwitches = ["--no-restore", "--no-build", "--nologo", "--no-dependencies"];
    private static readonly string[] PropertySwitches = ["-p:", "/p:", "--property:", "-property:", "/property:"];

    private static List<PublishCommand> FindPublishCommands(DockerfileInfo info) {
        var values = new Dictionary<string, string>(info.GlobalArgs, StringComparer.Ordinal);
        foreach (var stage in info.StageDetails) {
            foreach (var arg in stage.Instructions.Where(i => i.Directive is "ARG" or "ENV")) {
                foreach (var (key, value) in DockerfileSubstitution.ParseKeyValues(arg.Arguments)) {
                    if (value is { } && !values.ContainsKey(key)) values[key] = value;
                }
            }
        }

        var result = new List<PublishCommand>();
        foreach (var stage in info.StageDetails) {
            foreach (var run in stage.Instructions.Where(i => i.Directive == "RUN")) {
                foreach (var (verb, tokens) in DotnetCommands(run.Arguments, values)) {
                    string? project = null, output = null;
                    var extra = new List<string>();
                    var command = new PublishCommand(stage, verb, null, null, extra);
                    for (var i = 0; i < tokens.Count; i++) {
                        var token = tokens[i];
                        var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                        if (TryOption(tokens, ref i, ["-o", "--output"], out var value)) { output = value; }
                        else if (TryOption(tokens, ref i, ["-c", "--configuration"], out _)) { }
                        // A build arg without a value stays `$NAME`; it cannot be written, only reported.
                        else if (TryOption(tokens, ref i, ["-f", "--framework"], out value)) {
                            if (value.Contains('$')) extra.Add($"-f {value}"); else command.Framework = value;
                        }
                        else if (TryOption(tokens, ref i, ["-r", "--runtime"], out value)) {
                            if (value.Contains('$')) extra.Add($"-r {value}"); else command.RuntimeIdentifier = value;
                        }
                        else if (token is "--self-contained" or "--sc") {
                            // The value is optional: `--self-contained` alone means true.
                            if (next is { } && bool.TryParse(next, out var sc)) { command.SelfContained = sc; i++; }
                            else command.SelfContained = true;
                        }
                        else if (TryOption(tokens, ref i, ["--self-contained", "--sc"], out value) && bool.TryParse(value, out var scValue)) { command.SelfContained = scValue; }
                        else if (token == "--no-self-contained") { command.SelfContained = false; }
                        else if (IsDotnetProject(token)) { project = token; }
                        else if (IsConfigurationSwitch(token) || IgnoredPublishSwitches.Contains(token)) { }
                        else if (PropertySwitches.FirstOrDefault(s => token.StartsWith(s, StringComparison.OrdinalIgnoreCase)) is { } prefix) {
                            AddProperties(command, token, token[prefix.Length..]);
                        }
                        else if (token == "-p" && next is { } && next.Contains('=')) { AddProperties(command, $"{token} {next}", next); i++; }
                        else { extra.Add(token); }
                    }
                    if (command.RuntimeIdentifier is { } && command.SelfContained is null
                        && ImpliesSelfContained(DockerfileSubstitution.Substitute(stage.BaseRef, values))) {
                        command.SelfContained = true;
                        command.ImplicitSelfContained = true;
                    }
                    result.Add(command with { Project = project, Output = output });
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Every `dotnet publish`/`dotnet build` in a RUN line, with its arguments as unquoted tokens. The
    /// shell form is split into its commands first (`cd src && dotnet publish ... ; echo done`), with
    /// build args expanded as the shell would; the exec form is one command and, as in Docker, gets no
    /// expansion. A `dotnet` preceded by another word (`exec`, `time`) still counts.
    /// </summary>
    private static IEnumerable<(string Verb, List<string> Arguments)> DotnetCommands(string runArguments, IReadOnlyDictionary<string, string> values) {
        var vector = DockerfileSubstitution.ParseCommand(runArguments);
        // Redirections are the shell's, so they go while the quotes are still there to tell `"A>B"` apart from `>log`.
        var commands = vector is ["/bin/sh", "-c", var script]
            ? DockerfileSubstitution.SplitShellCommands(script)
                .Select(c => WithoutRedirections(DockerfileSubstitution.Tokenize(DockerfileSubstitution.Substitute(c, values))).Select(DockerfileSubstitution.Unquote).ToList())
            : [vector];
        foreach (var tokens in commands) {
            var dotnet = tokens.FindIndex(IsDotnet);
            if (dotnet < 0 || dotnet + 1 >= tokens.Count) continue;
            var verb = tokens[dotnet + 1].ToLowerInvariant();
            if (verb is "publish" or "build") yield return (verb, tokens[(dotnet + 2)..]);
        }
    }

    /// <summary>`dotnet` by name or by path (`/usr/share/dotnet/dotnet`), as a Dockerfile that does not rely on PATH calls it.</summary>
    private static bool IsDotnet(string token) =>
        token.Equals("dotnet", StringComparison.OrdinalIgnoreCase) || token.EndsWith("/dotnet", StringComparison.OrdinalIgnoreCase);

    // A redirection, possibly glued to the word before it (`publish>&2`, `/out>log`): `>out`, `2>>log`,
    // `2>&1`, `&>/dev/null`, `<in`; a bare operator (`> out`, `2> log`, `&> log`) takes the next token.
    private static readonly Regex RedirectionRegex = new(@"^(?<word>[^<>&]*?)(?<op>\d*>>?&?|&>>?|<)(?<target>.*)$", RegexOptions.Compiled);

    /// <summary>The shell's redirections are not arguments of dotnet; they would only end up as "unresolved".</summary>
    private static List<string> WithoutRedirections(List<string> tokens) {
        var result = new List<string>();
        for (var i = 0; i < tokens.Count; i++) {
            var match = RedirectionRegex.Match(MaskQuoted(tokens[i]));
            if (!match.Success) { result.Add(tokens[i]); continue; }
            if (match.Groups["word"].Length > 0) result.Add(tokens[i][..match.Groups["word"].Length]);
            // `>&1` names its target in the operator; `>` and `&>` without one take the next token.
            if (match.Groups["target"].Length == 0 && !match.Groups["op"].Value.EndsWith('&')) i++;
        }
        return result;
    }

    /// <summary>
    /// The token with its quoted spans (quotes included) blanked out, at the same length, so the regex
    /// only sees the operators the shell would: `"-p:A=x>y"` is one argument, `>"out dir"` a redirection.
    /// </summary>
    private static string MaskQuoted(string token) {
        if (token.IndexOfAny(['"', '\'']) < 0) return token;
        var chars = token.ToCharArray();
        var quote = '\0';
        for (var i = 0; i < chars.Length; i++) {
            var c = chars[i];
            if (quote == '\0') {
                if (c is '"' or '\'') { quote = c; chars[i] = '_'; }
                continue;
            }
            chars[i] = '_';
            if (c == '\\' && quote == '"' && i + 1 < chars.Length) chars[++i] = '_';
            else if (c == quote) quote = '\0';
        }
        return new string(chars);
    }

    /// <summary>`-r x`, `-r:x` and `--runtime=x` alike; advances past a separate value.</summary>
    private static bool TryOption(List<string> tokens, ref int index, string[] names, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value) {
        var token = tokens[index];
        foreach (var name in names) {
            if (token == name && index + 1 < tokens.Count) {
                value = tokens[++index];
                return true;
            }
            if (token.Length > name.Length + 1 && token.StartsWith(name, StringComparison.Ordinal) && token[name.Length] is '=' or ':') {
                value = token[(name.Length + 1)..];
                return true;
            }
        }
        value = null;
        return false;
    }

    private static readonly Regex PropertyNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>
    /// `-p:A=1;B=2`: each pair a property, unless it is the configuration. One pair that cannot be
    /// written (a build arg without a value, a name MSBuild would not take) keeps the whole switch out,
    /// as a note; writing half of it would leave the project and the note contradicting each other.
    /// </summary>
    private static void AddProperties(PublishCommand command, string token, string assignments) {
        var pairs = new List<(string Name, string Value)>();
        foreach (var assignment in assignments.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
            var eq = assignment.IndexOf('=');
            var name = eq > 0 ? assignment[..eq].Trim() : string.Empty;
            var value = eq > 0 ? assignment[(eq + 1)..].Trim() : string.Empty;
            if (name.Equals("Configuration", StringComparison.OrdinalIgnoreCase)) continue;
            if (!PropertyNameRegex.IsMatch(name) || value.Contains('$')) {
                command.ExtraArguments.Add(token);
                return;
            }
            pairs.Add((name, value));
        }
        command.Properties.AddRange(pairs);
    }

    /// <summary>The configuration is the SDK's business at publish time, not something to carry over.</summary>

    /// <summary>The publish lines of one project folded into one, later switches winning as they would on a rerun.</summary>
    private static PublishCommand MergePublishCommands(IEnumerable<PublishCommand> commands) {
        var merged = new PublishCommand(null!, "publish", null, null, new List<string>());
        foreach (var command in commands) {
            merged.Framework = command.Framework ?? merged.Framework;
            merged.RuntimeIdentifier = command.RuntimeIdentifier ?? merged.RuntimeIdentifier;
            merged.SelfContained = command.SelfContained ?? merged.SelfContained;
            merged.ImplicitSelfContained = command.SelfContained is { } ? command.ImplicitSelfContained : merged.ImplicitSelfContained;
            foreach (var (name, value) in command.Properties) {
                merged.Properties.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                merged.Properties.Add((name, value));
            }
            foreach (var extra in command.ExtraArguments.Where(e => !merged.ExtraArguments.Contains(e))) merged.ExtraArguments.Add(extra);
        }
        return merged;
    }

    /// <summary>
    /// The project as `dotnet publish` saw it in the Dockerfile: a switch that makes it self-contained
    /// (or AOT) means runtime-deps is the SDK's image, not a pin; -f picks the framework of a
    /// multi-targeting project, which the SDK needs to compute an image at all.
    /// </summary>
    private static ProjectFacts ApplyPublishSwitches(ProjectFacts project, PublishCommand publish, string baseImage, MigrationPlan plan) {
        string? Property(string name) => publish.Properties.LastOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        bool IsTrue(string name) => string.Equals(Property(name), "true", StringComparison.OrdinalIgnoreCase);

        var selfContained = publish.SelfContained
            ?? (Property("SelfContained") is { } value ? value.Equals("true", StringComparison.OrdinalIgnoreCase) : project.SelfContained);
        selfContained |= IsTrue("PublishSelfContained") || IsTrue("PublishAot");

        var framework = publish.Framework ?? project.TargetFramework ?? (project.TargetFrameworks.Count == 1 ? project.TargetFrameworks[0] : null);
        if (project.TargetFrameworks.Count > 1) {
            // The image's version names the framework the Dockerfile ran, when -f did not.
            framework ??= TryParseSdkImage(baseImage, out _, out var version, out _)
                ? project.TargetFrameworks.FirstOrDefault(t => t.Equals("net" + version, StringComparison.OrdinalIgnoreCase))
                : null;
            var all = string.Join(";", project.TargetFrameworks);
            plan.Notes.Add(framework is { }
                ? $"the project targets {all}; publish the container for {framework}: dotnet publish -f {framework} /t:PublishContainer"
                : $"the project targets {all}; dotnet publish /t:PublishContainer needs -f <framework>, and the image is pinned since the SDK's pick depends on it");
        }

        if (publish.ImplicitSelfContained) {
            plan.Notes.Add($"the Dockerfile's SDK published self-contained for -r {publish.RuntimeIdentifier} without --self-contained (the default up to the .NET 7 SDK); SelfContained=true is written to keep that, since the .NET 8 SDK defaults to framework-dependent");
        }
        if (publish.RuntimeIdentifier is { } || publish.SelfContained is { } || publish.Properties.Count > 0) {
            plan.Notes.Add("the Dockerfile's publish switches are written in a PropertyGroup for publishing only ('$(_IsPublishing)' == 'true'), so build and run on the host are unchanged");
        }

        return project with {
            TargetFramework = framework,
            SelfContained = selfContained,
            AssemblyName = Property("AssemblyName") ?? project.AssemblyName,
        };
    }

    /// <summary>
    /// The publish switches as properties. `dotnet publish` sets _IsPublishing, so they apply to
    /// `dotnet publish /t:PublishContainer` as they did in the Dockerfile, but not to a build or run
    /// on the developer's machine (a linux RuntimeIdentifier there would break F5).
    /// </summary>
    private static XElement? PublishPropertyGroup(PublishCommand publish) {
        var group = new XElement("PropertyGroup", new XAttribute("Condition", "'$(_IsPublishing)' == 'true'"));
        if (publish.RuntimeIdentifier is { } rid) group.Add(new XElement("RuntimeIdentifier", EscapeProperty(rid)));
        if (publish.SelfContained is { } selfContained) group.Add(new XElement("SelfContained", selfContained ? "true" : "false"));
        foreach (var (name, value) in publish.Properties) group.Add(new XElement(name, EscapeProperty(value)));
        return group.HasElements ? group : null;
    }

    private static bool IsConfigurationSwitch(string token) =>
        token.StartsWith("-c:", StringComparison.Ordinal)
        || token.StartsWith("--configuration=", StringComparison.Ordinal)
        || token.StartsWith("-p:Configuration=", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("/p:Configuration=", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The project a Dockerfile builds is the one it publishes; failing that the one it builds, the
    /// one whose dll the ENTRYPOINT runs, the only .csproj it mentions, or the only .csproj next to it.
    /// </summary>
    private static string? ResolveProject(DockerfileInfo info, List<PublishCommand> publishes, IReadOnlyList<string> projectFiles, string dockerfilePath) {
        var candidates = new List<string>();
        foreach (var verb in new[] { "publish", "build" }) {
            candidates.AddRange(publishes.Where(p => p.Verb == verb && p.Project is { }).Select(p => p.Project!));
        }

        var entrypoint = info.EntryPoint ?? info.Cmd;
        if (entrypoint is { }) {
            var parts = DockerfileSubstitution.ParseCommand(entrypoint);
            var dll = parts.FirstOrDefault(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
            if (dll is { }) candidates.AddRange(ProjectExtensions.Select(e => Path.GetFileNameWithoutExtension(dll) + e));
        }

        var mentioned = info.StageDetails.SelectMany(s => s.Instructions)
            .SelectMany(i => ProjectTokenRegex.Matches(i.Arguments).Select(m => m.Value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (mentioned.Count == 1) candidates.Add(mentioned[0]);

        var dockerfileDir = Path.GetDirectoryName(dockerfilePath) ?? string.Empty;
        foreach (var candidate in candidates) {
            var resolved = ResolveProjectToken(candidate, projectFiles, dockerfileDir);
            if (resolved is { }) return resolved;
        }

        var siblings = Directory.Exists(dockerfileDir) ? Directory.GetFiles(dockerfileDir, "*.*proj").Where(IsDotnetProject).ToArray() : [];
        return siblings.Length == 1 ? siblings[0] : null;
    }

    private static string? ResolveProjectToken(string token, IReadOnlyList<string> projectFiles, string dockerfileDir) {
        var normalized = string.Join('/', token.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s != "."));
        var fileName = Path.GetFileName(normalized);

        var byName = projectFiles.Where(p => Path.GetFileName(p).Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        if (byName.Count > 1) {
            var byPath = byName.Where(p => p.Replace('\\', '/').EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase)).ToList();
            if (byPath.Count == 1) return byPath[0];
        }

        // The build context is usually a parent of the Dockerfile's directory (VS puts the Dockerfile in
        // the project and builds from the solution folder), so try the token against each ancestor.
        var dir = dockerfileDir;
        for (var level = 0; level < 6 && !string.IsNullOrEmpty(dir); level++) {
            var probe = Path.GetFullPath(Path.Combine(dir, normalized));
            if (File.Exists(probe)) return probe;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static bool IsPublishOutputCopy(DockerfileInfo info, List<PublishCommand> publishes, string arguments, IReadOnlyDictionary<string, string> values, out string why) {
        // Flags come first; what follows is either the exec form `["src", "dest"]` (which may contain
        // whitespace) or whitespace-separated operands.
        string? from = null;
        var rest = arguments.TrimStart();
        while (rest.StartsWith("--", StringComparison.Ordinal)) {
            var end = rest.IndexOfAny([' ', '\t']);
            var flag = end < 0 ? rest : rest[..end];
            if (flag.StartsWith("--from=", StringComparison.Ordinal)) from = flag[7..];
            rest = end < 0 ? string.Empty : rest[end..].TrimStart();
        }
        var operands = rest.StartsWith('[') ? DockerfileSubstitution.ParseCommand(rest) : DockerfileSubstitution.Tokenize(rest);
        if (operands.Count < 2) {
            why = "has no source and destination";
            return false;
        }

        if (from is null) {
            why = "copies files from the build context into the image";
            return false;
        }
        var stage = FindStage(info, DockerfileSubstitution.Substitute(from, values));
        if (stage is null) {
            why = $"copies from image {from}";
            return false;
        }

        var outputs = new HashSet<string>(StringComparer.Ordinal);
        var chain = new List<DockerStage>();
        for (var s = stage; s is { } && !chain.Contains(s); s = FindStage(info, s.BaseRef)) chain.Add(s);
        foreach (var publish in publishes.Where(p => chain.Contains(p.Stage) && p.Output is { })) {
            outputs.Add(NormalizeDir(publish.Output!));
        }
        if (outputs.Count == 0) {
            why = $"stage {from} has no dotnet publish/build output to copy";
            return false;
        }

        var sources = operands.Take(operands.Count - 1).Select(s => NormalizeDir(DockerfileSubstitution.Substitute(DockerfileSubstitution.Unquote(s), values))).ToList();
        var foreign = sources.Where(s => !outputs.Contains(s)).ToList();
        if (foreign.Count > 0) {
            why = $"{string.Join(", ", foreign)} is not the publish output of stage {from}";
            return false;
        }
        why = string.Empty;
        return true;
    }

    // A range Docker accepts but ContainerPort does not; larger ones are more likely a mistake than ports to publish one by one.
    private const int MaxExpandedPortRange = 16;

    /// <summary>
    /// ContainerPort takes one port number per item. EXPOSE also takes a range (8000-8010), expanded
    /// here when small, and a build arg that has no value, which cannot be written at all.
    /// </summary>
    internal static IEnumerable<string> ExpandPorts(string port, List<string> notes) {
        if (int.TryParse(port, out var single) && single is > 0 and <= 65535) return [single.ToString()];

        var dash = port.IndexOf('-');
        if (dash > 0 && int.TryParse(port[..dash], out var first) && int.TryParse(port[(dash + 1)..], out var last)
            && first is > 0 and <= 65535 && last is > 0 and <= 65535 && first <= last) {
            var count = last - first + 1;
            if (count <= MaxExpandedPortRange) return Enumerable.Range(first, count).Select(p => p.ToString());
            notes.Add($"EXPOSE {port} not written: a range of {count} ports; ContainerPort takes one port per item, add the ones the app listens on");
            return [];
        }

        notes.Add($"EXPOSE {port} not written: not a port number; ContainerPort takes one port per item");
        return [];
    }

    private static string NormalizeDir(string path) => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Resolves "." and ".." in an absolute container path the way Docker does for WORKDIR: /app/../lib
    /// is /lib, and ".." at the root stays at the root. Path.GetFullPath would apply the host's rules.
    /// </summary>
    internal static string NormalizeUnixPath(string path) {
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
            if (segment == ".") continue;
            if (segment == "..") {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join('/', segments);
    }

    private static string StripRunFlags(string arguments) {
        var tokens = DockerfileSubstitution.Tokenize(arguments);
        return string.Join(' ', tokens.SkipWhile(t => t.StartsWith("--", StringComparison.Ordinal)));
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";

    // ---- Project analysis ----------------------------------------------------------------------

    /// <param name="IsWeb">References Microsoft.AspNetCore.App, so the SDK picks the aspnet image.</param>
    /// <param name="TargetFrameworks">The TargetFrameworks list of a multi-targeting project; empty otherwise.</param>
    /// <param name="ExistingContainerSettings">Container settings written in the project file itself.</param>
    internal sealed record ProjectFacts(bool IsWeb, string? TargetFramework, IReadOnlyList<string> TargetFrameworks, string AssemblyName, bool SelfContained, List<string> ExistingContainerSettings, List<string> DockerToolsReferences) {
        /// <summary>Container settings the project gets from an import (Directory.Build.props, ...); known only when evaluated.</summary>
        public List<InheritedSetting> InheritedSettings { get; init; } = new();
        /// <summary>The repository's files the evaluation imported (Directory.Build.props, .props of its own); empty when not evaluated.</summary>
        public List<string> ImportedFiles { get; init; } = new();
    }

    /// <summary>A container property (<paramref name="Include"/> null) or item from <paramref name="File"/>; for an item, Value is its Value or Type metadata.</summary>
    internal sealed record InheritedSetting(string Name, string? Include, string Value, string File);

    internal static async Task<ProjectFacts> ReadProjectAsync(string projectPath, CancellationToken cancellationToken) {
        XDocument doc;
        using (var stream = File.OpenRead(projectPath)) {
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }
        return ReadProject(doc, projectPath);
    }

    /// <summary>The facts the project file alone states; see <see cref="EvaluateProject"/> for the rest.</summary>
    internal static ProjectFacts ReadProject(XDocument doc, string projectPath) {
        var sdk = doc.Root?.Attribute("Sdk")?.Value;
        string? Property(string name) {
            var value = doc.ElementsNamed(name).LastOrDefault()?.Value.Trim();
            return string.IsNullOrEmpty(value) || value.Contains("$(") ? null : value;
        }
        bool IsTrue(string name) => Property(name)?.Equals("true", StringComparison.OrdinalIgnoreCase) == true;

        var assemblyName = Property("AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath);
        var selfContained = IsTrue("SelfContained") || IsTrue("PublishSelfContained") || IsTrue("PublishAot");

        var existing = ContainerProperties.Concat(ContainerItems).Where(n => doc.ElementsNamed(n).Any()).ToList();
        if (Property("PublishProfile")?.Equals("DefaultContainer", StringComparison.OrdinalIgnoreCase) == true) existing.Add("PublishProfile");

        var tools = DockerToolsProperties.Where(n => doc.ElementsNamed(n).Any()).ToList();
        if (doc.ElementsNamed("PackageReference").Any(e => string.Equals(e.Attribute("Include")?.Value, DockerToolsPackage, StringComparison.OrdinalIgnoreCase))) {
            tools.Add(DockerToolsPackage);
        }

        var isWeb = sdk?.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true
            || doc.ElementsNamed("FrameworkReference").Any(e => string.Equals(e.Attribute("Include")?.Value, "Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));
        var frameworks = Property("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];

        return new ProjectFacts(isWeb, Property("TargetFramework"), frameworks, assemblyName, selfContained, existing, tools);
    }

    /// <summary>
    /// Completes <paramref name="fromFile"/> with the MSBuild evaluation: the target framework, SDK,
    /// assembly name and self-contained switches wherever they are set (Directory.Build.props, an
    /// imported .props), and the container settings the project inherits from such files. What the
    /// project file itself contains (existing settings, Visual Studio tools) stays as read, since that
    /// is what the migration edits. Needs a registered MSBuild.
    /// </summary>
    internal static ProjectFacts EvaluateProject(string projectPath, ProjectFacts fromFile, IDictionary<string, string> globalProperties) {
        using var collection = new Microsoft.Build.Evaluation.ProjectCollection();
        var project = new Microsoft.Build.Evaluation.Project(projectPath, globalProperties, null, collection);

        string? Value(string name) => project.GetPropertyValue(name).Trim() is { Length: > 0 } value ? value : null;
        bool IsTrue(string name) => string.Equals(Value(name), "true", StringComparison.OrdinalIgnoreCase);

        // The SDK and NuGet packages define container defaults of their own; only the repository's
        // files count as settings someone wrote.
        var toolRoots = new[] { "NetCoreRoot", "DOTNET_HOST_PATH", "MSBuildExtensionsPath", "MSBuildBinPath", "MSBuildSDKsPath", "NuGetPackageRoot" }
            .Select(Value).OfType<string>()
            .Select(p => Path.GetFullPath(File.Exists(p) ? Path.GetDirectoryName(p)! : p))
            .ToList();
        bool IsUserFile(string file) =>
            !string.Equals(file, projectPath, StringComparison.OrdinalIgnoreCase)
            && !toolRoots.Any(root => file.StartsWith(root, StringComparison.OrdinalIgnoreCase));

        var inherited = new List<InheritedSetting>();
        foreach (var name in ContainerProperties) {
            var property = project.GetProperty(name);
            if (property is { IsImported: true, Xml: { } xml } && IsUserFile(xml.ContainingProject.FullPath)) {
                inherited.Add(new InheritedSetting(name, null, property.EvaluatedValue, xml.ContainingProject.FullPath));
            }
        }
        foreach (var name in ContainerItems) {
            foreach (var item in project.GetItems(name).Where(i => i.IsImported && IsUserFile(i.Xml.ContainingProject.FullPath))) {
                var value = name == "ContainerPort"
                    ? (item.GetMetadataValue("Type") is { Length: > 0 } type ? type.ToLowerInvariant() : "tcp")
                    : item.GetMetadataValue("Value");
                inherited.Add(new InheritedSetting(name, item.EvaluatedInclude, value, item.Xml.ContainingProject.FullPath));
            }
        }

        var frameworks = Value("TargetFrameworks")?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        // A multi-targeting project's outer evaluation has no TargetFramework. One that is set anyway
        // (a Directory.Build.props next to a TargetFrameworks list) makes the project build only that.
        var framework = Value("TargetFramework");
        var isWeb = fromFile.IsWeb || IsTrue("UsingMicrosoftNETSdkWeb")
            || project.GetItems("FrameworkReference").Any(i => i.EvaluatedInclude.Equals("Microsoft.AspNetCore.App", StringComparison.OrdinalIgnoreCase));

        return fromFile with {
            IsWeb = isWeb,
            TargetFramework = framework,
            TargetFrameworks = framework is null ? frameworks : [],
            AssemblyName = Value("AssemblyName") ?? fromFile.AssemblyName,
            SelfContained = IsTrue("SelfContained") || IsTrue("PublishSelfContained") || IsTrue("PublishAot"),
            InheritedSettings = inherited,
            ImportedFiles = project.Imports.Select(i => i.ImportedProject.FullPath).Where(IsUserFile).Distinct(DirExt.PathComparer).ToList(),
        };
    }

    // The settings the Dockerfile decides by leaving them out: an inherited value replaces that default.
    private static readonly string[] DefaultedByDockerfile = ["ContainerUser", "ContainerWorkingDirectory", "ContainerAppCommandInstruction", "ContainerRuntimeIdentifier"];

    /// <summary>
    /// Adjusts what is written to the settings the project already inherits: a value an import already
    /// sets is not written again, one that differs is written (the project file wins over
    /// Directory.Build.props) and noted. An inherited ContainerBaseImage or ContainerFamily would replace
    /// the image the Dockerfile ran on, so then the image is pinned explicitly.
    /// </summary>
    private static void ReconcileInheritedSettings(XElement properties, XElement items, ProjectFacts project, string baseImage, MigrationPlan plan) {
        if (project.InheritedSettings.Count == 0) return;
        var projectDir = Path.GetDirectoryName(plan.ProjectPath) ?? string.Empty;
        string From(InheritedSetting setting) => Path.GetRelativePath(projectDir, setting.File);

        plan.Notes.Add($"inherits container settings: {string.Join(", ", project.InheritedSettings.GroupBy(From).Select(g => $"{string.Join(", ", g.Select(s => s.Name).Distinct())} from {g.Key}"))}");

        var inherited = project.InheritedSettings.Where(s => s.Include is null).ToDictionary(s => s.Name);
        var image = inherited.GetValueOrDefault("ContainerBaseImage");
        var family = inherited.GetValueOrDefault("ContainerFamily");
        if (properties.Element("ContainerBaseImage") is null && (image is { } || family is { })) {
            var writtenFamily = properties.Element("ContainerFamily");
            if (image is { } && image.Value == baseImage) {
                writtenFamily?.Remove();
                plan.Notes.Add($"ContainerBaseImage={baseImage} already comes from {From(image)}; not written");
            }
            else if (image is null && writtenFamily?.Value == family!.Value) {
                writtenFamily.Remove();
                plan.Notes.Add($"ContainerFamily={family.Value} already comes from {From(family)}; not written");
            }
            else {
                // Either import would put the project on another image than the Dockerfile's.
                writtenFamily?.Remove();
                properties.Element("EnableSdkContainerSupport")!.AddAfterSelf(new XElement("ContainerBaseImage", EscapeProperty(baseImage)));
                var other = image ?? family!;
                plan.Notes.Add($"ContainerBaseImage written: {From(other)} sets {other.Name}={other.Value}, which would replace {baseImage}");
            }
            inherited.Remove("ContainerBaseImage");
            inherited.Remove("ContainerFamily");
        }

        foreach (var setting in inherited.Values) {
            var written = properties.Element(setting.Name);
            if (written is { } && written.Value == EscapeProperty(setting.Value)) {
                written.Remove();
                plan.Notes.Add($"{setting.Name}={setting.Value} already comes from {From(setting)}; not written");
            }
            else if (written is { }) {
                plan.Notes.Add($"{setting.Name} overrides {setting.Value} from {From(setting)}");
            }
            else if (DefaultedByDockerfile.Contains(setting.Name)) {
                plan.Notes.Add($"{setting.Name}={setting.Value} from {From(setting)} applies to this project too; the Dockerfile left it at the default");
            }
        }

        foreach (var setting in project.InheritedSettings.Where(s => s.Include is { })) {
            var written = items.Elements(setting.Name).FirstOrDefault(e => e.Attribute("Include")?.Value == EscapeItemSpec(setting.Include!));
            if (written is null) continue;
            var value = setting.Name == "ContainerPort" ? written.Attribute("Type")?.Value ?? "tcp" : written.Attribute("Value")?.Value;
            if (value == (setting.Name == "ContainerPort" ? setting.Value : EscapeMetadata(setting.Value))) {
                written.Remove();
                plan.Notes.Add($"{setting.Name} {setting.Include} already comes from {From(setting)}; not written");
            }
            else {
                plan.Notes.Add($"{setting.Name} {setting.Include} is also set by {From(setting)}, to {setting.Value}");
            }
        }
    }

    // ---- Dockerfile references -----------------------------------------------------------------

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", "node_modules", ".git", ".vs", "packages" };
    // Compose files, pipelines (.yml), devcontainer.json, and the scripts that run `docker build`.
    private static readonly HashSet<string> BuildScriptExtensions = new(StringComparer.OrdinalIgnoreCase) { ".yml", ".yaml", ".json", ".sh", ".ps1", ".psm1", ".cmd", ".bat", ".mk", ".cake" };
    private static readonly HashSet<string> BuildScriptNames = new(StringComparer.OrdinalIgnoreCase) { "Jenkinsfile", "Makefile", "Taskfile", "justfile" };
    private const long MaxBuildScriptSize = 1 << 20;

    /// <summary>The directory holding .git above <paramref name="directory"/>, or null outside a repository.</summary>
    internal static string? FindRepositoryRoot(string directory) {
        for (var dir = directory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
            var git = Path.Combine(dir, ".git");
            if (Directory.Exists(git) || File.Exists(git)) return dir;
        }
        return null;
    }

    /// <summary>
    /// The files under <paramref name="root"/> that build from the Dockerfile: they name its path
    /// (relative to themselves or to the root, where pipelines run), or, for a file named plainly
    /// `Dockerfile`, use its directory as the build context (`build: ./Api`, `docker build Api`). A
    /// false positive only keeps the Dockerfile.
    /// </summary>
    internal List<string> FindDockerfileReferences(string dockerfilePath, string root) {
        if (!_buildScripts.TryGetValue(root, out var scripts)) {
            scripts = new List<(string, string)>();
            CollectBuildScripts(root, scripts);
            _buildScripts[root] = scripts;
        }

        var dockerfileDir = Path.GetDirectoryName(dockerfilePath) ?? string.Empty;
        var isDefaultName = Path.GetFileName(dockerfilePath) == "Dockerfile";
        // The patterns depend on the script's directory only; the scripts of one directory share them.
        var pathPatterns = new Dictionary<string, Regex>(StringComparer.Ordinal);
        var contextPatterns = new Dictionary<string, Regex>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var (path, text) in scripts) {
            foreach (var baseDir in new[] { Path.GetDirectoryName(path) ?? root, root }.Distinct(StringComparer.OrdinalIgnoreCase)) {
                var file = Path.GetRelativePath(baseDir, dockerfilePath).Replace('\\', '/');
                var dir = Path.GetRelativePath(baseDir, dockerfileDir).Replace('\\', '/');
                if (!pathPatterns.TryGetValue(file, out var pathPattern)) pathPatterns[file] = pathPattern = PathPattern(file);
                if (!contextPatterns.TryGetValue(dir, out var contextPattern)) contextPatterns[dir] = contextPattern = ContextPattern(dir);
                if (pathPattern.IsMatch(text) || (isDefaultName && contextPattern.IsMatch(text))) {
                    result.Add(path);
                    break;
                }
            }
        }
        return result;
    }

    private static void CollectBuildScripts(string directory, List<(string Path, string Text)> scripts) {
        try {
            foreach (var file in Directory.EnumerateFiles(directory)) {
                var name = Path.GetFileName(file);
                if (!BuildScriptExtensions.Contains(Path.GetExtension(name)) && !BuildScriptNames.Contains(name)) continue;
                if (new FileInfo(file).Length > MaxBuildScriptSize) continue;
                scripts.Add((file, File.ReadAllText(file)));
            }
            foreach (var dir in Directory.EnumerateDirectories(directory)) {
                if (!SkippedDirectories.Contains(Path.GetFileName(dir))) CollectBuildScripts(dir, scripts);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // An unreadable folder cannot reference the Dockerfile in a way that matters here.
        }
    }

    /// <summary>The relative path as a whole token, `./` optional, either slash.</summary>
    private static Regex PathPattern(string relativePath) =>
        new($@"(?<![\w./\\-])(?:\./)?{SlashAgnostic(relativePath)}(?![\w./\\-])", RegexOptions.IgnoreCase);

    /// <summary>A compose `build:`/`context:` line or a `docker build` command whose context is the directory.</summary>
    private static Regex ContextPattern(string relativeDir) {
        var dir = relativeDir == "." ? @"\." : $@"(?:\./)?{SlashAgnostic(relativeDir)}";
        return new($@"(?:^\s*-?\s*(?:build|context)\s*:\s*[""']?{dir}[/\\]?[""']?\s*$|\bdocker(?:\s+buildx)?\s+build\b[^\r\n]*\s[""']?{dir}[/\\]?[""']?(?=\s|$))",
            RegexOptions.IgnoreCase | RegexOptions.Multiline);
    }

    private static string SlashAgnostic(string path) => Regex.Escape(path).Replace("/", @"[/\\]");

    /// <summary>
    /// The .dockerignore files that served only the deleted Dockerfile: its own (`Dockerfile.dockerignore`)
    /// and the nearest plain `.dockerignore` up to the root, once no Dockerfile or compose file is left
    /// below that one's directory.
    /// </summary>
    private static List<string> OrphanedDockerignores(string dockerfilePath, string? root) {
        var result = new List<string>();
        var own = dockerfilePath + ".dockerignore";
        if (File.Exists(own)) result.Add(own);

        var stop = root is { } ? Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) : null;
        for (var dir = Path.GetDirectoryName(dockerfilePath); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir)) {
            var shared = Path.Combine(dir, ".dockerignore");
            if (File.Exists(shared)) {
                if (!HasOtherBuildInputs(dir)) result.Add(shared);
                break;
            }
            if (stop is null || string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), stop, StringComparison.OrdinalIgnoreCase)) break;
        }
        return result;
    }

    private static bool HasOtherBuildInputs(string directory) {
        try {
            if (Directory.EnumerateFiles(directory).Select(Path.GetFileName)
                    .Any(n => DockerfileParser.IsDockerfileName(n!) || IsComposeFile(n!))) {
                return true;
            }
            return Directory.EnumerateDirectories(directory)
                .Where(d => !SkippedDirectories.Contains(Path.GetFileName(d)))
                .Any(HasOtherBuildInputs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return true;
        }
    }

    private static bool IsComposeFile(string name) =>
        (name.StartsWith("docker-compose", StringComparison.OrdinalIgnoreCase) || name.StartsWith("compose", StringComparison.OrdinalIgnoreCase))
        && (name.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Null unless the image is the one the SDK would compute anyway (see ComputeDotnetBaseImageAndTag):
    /// aspnet for web projects, runtime for the rest, runtime-deps when self-contained, tagged with the
    /// target framework's version. Then the empty string for exactly that image, or the family suffix of
    /// the tag (`alpine`, `noble-chiseled`), which ContainerFamily reproduces. Windows tags stay explicit:
    /// the SDK would still target a Linux runtime identifier with them. A platform suffix (`-amd64`,
    /// `-arm64v8`) names one platform of the multi-platform tag; it is returned separately, since the
    /// SDK takes the platform from the runtime identifier.
    /// </summary>
    internal static string? SdkImageFamily(string image, ProjectFacts project, out string? architecture) {
        architecture = null;
        var match = DefaultImageRegex.Match(image);
        if (!match.Success || project.TargetFramework is null) return null;
        if (!project.TargetFramework.Equals("net" + match.Groups["version"].Value, StringComparison.OrdinalIgnoreCase)) return null;
        if (match.Groups["repo"].Value != SdkImageRepository(project)) return null;

        var family = match.Groups["family"].Value;
        if (family.Contains("nanoserver", StringComparison.OrdinalIgnoreCase) || family.Contains("windowsservercore", StringComparison.OrdinalIgnoreCase)) return null;
        var arch = ArchitectureSuffix.Match(family);
        if (arch.Success) {
            architecture = arch.Groups["arch"].Value;
            family = family[..arch.Index];
        }
        return family;
    }

    /// <summary>The repository the SDK picks (see ComputeDotnetBaseImageAndTag), or null when the pinned image is not one of them.</summary>
    internal static string SdkImageRepository(ProjectFacts project) {
        return project.SelfContained ? "runtime-deps" : project.IsWeb ? "aspnet" : "runtime";
    }

    /// <summary>The image the SDK would compute for the project without ContainerBaseImage, for messages; null when the version is unknown.</summary>
    internal static string? SdkImageName(ProjectFacts project) =>
        project.TargetFramework is { } tfm && tfm.StartsWith("net", StringComparison.OrdinalIgnoreCase) && tfm.Length > 3 && char.IsDigit(tfm[3])
            ? $"mcr.microsoft.com/dotnet/{SdkImageRepository(project)}:{tfm[3..].Split('-')[0]}"
            : null;

    /// <summary>Parses a pinned image into the parts ContainerFamily can and cannot stand in for.</summary>
    internal static bool TryParseSdkImage(string image, out string repo, out string version, out string family) {
        var match = DefaultImageRegex.Match(image);
        repo = match.Groups["repo"].Value;
        version = match.Groups["version"].Value;
        family = match.Groups["family"].Value;
        return match.Success;
    }

    private static readonly Regex ArchitectureSuffix = new(@"(^|-)(?<arch>amd64|arm64v8|arm32v7)$", RegexOptions.Compiled);

    private static bool IsDefaultWorkDir(string workDir) =>
        workDir.TrimEnd('/', '\\').Equals("/app", StringComparison.Ordinal)
        || workDir.TrimEnd('/', '\\').Equals(@"C:\app", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// `dotnet App.dll` (framework-dependent) or `./App` (self-contained apphost) in any path spelling is
    /// exactly what the SDK's ContainerAppCommand defaults to, so writing it would only pin the obvious.
    /// </summary>
    private static bool IsDefaultAppCommand(List<string> command, ProjectFacts project) {
        if (command.Count == 2 && command[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase)) {
            return FileNameOf(command[1]).Equals(project.AssemblyName + ".dll", StringComparison.OrdinalIgnoreCase);
        }
        if (command.Count == 1) {
            var name = FileNameOf(command[0]);
            return name.Equals(project.AssemblyName, StringComparison.OrdinalIgnoreCase)
                || name.Equals(project.AssemblyName + ".exe", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string FileNameOf(string path) => path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;

    // ---- MSBuild escaping ----------------------------------------------------------------------

    internal static string EscapeProperty(string value) => value.Replace("%", "%25").Replace("$(", "%24(").Replace("@(", "%40(");

    /// <summary>An Include is split on `;` and expanded as a glob, which no command-line argument wants.</summary>
    private static string EscapeItemSpec(string value) =>
        EscapeProperty(value).Replace(";", "%3B").Replace("*", "%2A").Replace("?", "%3F");

    private static string EscapeMetadata(string value) => EscapeProperty(value).Replace(";", "%3B");

    // ---- Project editing -----------------------------------------------------------------------

    /// <summary>The indent unit and newline of the file, read from the first indented child of Project.</summary>
    internal static (string Indent, string Newline) DetectLayout(XElement project) {
        var newline = "\n";
        foreach (var text in project.Nodes().OfType<XText>()) {
            if (!string.IsNullOrWhiteSpace(text.Value)) continue;
            if (text.NextNode is not XElement) continue;
            var lastBreak = text.Value.LastIndexOf('\n');
            if (lastBreak < 0) continue;
            var indent = text.Value[(lastBreak + 1)..];
            if (indent.Length > 0) return (indent, newline);
        }
        return ("  ", newline);
    }

    /// <summary>Copies an element, adding the whitespace nodes that make it sit at <paramref name="level"/>.</summary>
    internal static XElement WithLayout(XElement source, string indent, string newline, int level) {
        var copy = new XElement(source.Name, source.Attributes());
        if (!source.HasElements) {
            if (!string.IsNullOrEmpty(source.Value)) copy.Value = source.Value;
            return copy;
        }
        var inner = string.Concat(Enumerable.Repeat(indent, level + 1));
        foreach (var child in source.Elements()) {
            copy.Add(new XText(newline + inner));
            copy.Add(WithLayout(child, indent, newline, level + 1));
        }
        copy.Add(new XText(newline + string.Concat(Enumerable.Repeat(indent, level))));
        return copy;
    }

    private static void RemoveDockerToolsSettings(XElement project, string relativeDockerfile) {
        var doomed = new List<XElement>();
        foreach (var name in DockerToolsProperties) doomed.AddRange(project.ElementsNamed(name));
        doomed.AddRange(project.ElementsNamed("PackageReference")
            .Where(e => string.Equals(e.Attribute("Include")?.Value, DockerToolsPackage, StringComparison.OrdinalIgnoreCase)));
        foreach (var itemType in new[] { "None", "Content" }) {
            doomed.AddRange(project.ElementsNamed(itemType).Where(e => {
                var include = e.Attribute("Include")?.Value ?? e.Attribute("Update")?.Value;
                return include is { } && NormalizeDir(include).Equals(relativeDockerfile, StringComparison.OrdinalIgnoreCase);
            }));
        }

        foreach (var element in doomed) {
            var parent = element.Parent;
            RemoveWithLeadingWhitespace(element);
            // A group left with nothing but whitespace is noise; comments keep it.
            if (parent is { } && parent != project && !parent.Nodes().Any(n => n is XElement or XComment)) {
                RemoveWithLeadingWhitespace(parent);
            }
        }
    }

    internal static void RemoveWithLeadingWhitespace(XElement element) {
        if (element.PreviousNode is XText previous && string.IsNullOrWhiteSpace(previous.Value)) previous.Remove();
        element.Remove();
    }
}
