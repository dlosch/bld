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

    public ContainerMigrationService(IConsoleOutput console) {
        _console = console;
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

        public bool CanApply(bool force) => SkipReason is null && (Unsupported.Count == 0 || force);
    }

    private static readonly string[] DockerToolsProperties = [
        "DockerDefaultTargetOS", "DockerfileContext", "DockerfileFile", "DockerfileTag", "DockerfileBuildArguments",
        "DockerfileRunArguments", "DockerfileRunEnvironmentFiles", "DockerfileFastModeStage", "DockerComposeProjectPath",
        "ContainerDevelopmentMode",
    ];
    private const string DockerToolsPackage = "Microsoft.VisualStudio.Azure.Containers.Tools.Targets";

    private static readonly string[] ContainerProperties = [
        "ContainerBaseImage", "ContainerImage", "ContainerRepository", "ContainerWorkingDirectory", "ContainerUser",
        "ContainerAppCommandInstruction", "ContainerFamily",
    ];
    private static readonly string[] ContainerItems = [
        "ContainerPort", "ContainerEnvironmentVariable", "ContainerLabel", "ContainerEntrypoint", "ContainerEntrypointArgs",
        "ContainerAppCommand", "ContainerAppCommandArgs", "ContainerDefaultArgs",
    ];

    private static readonly Regex ProjectTokenRegex = new(@"[^\s""'\[\],]+\.csproj", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DotnetCommandRegex = new(@"\bdotnet\s+(publish|build)\b(?<args>[^&|;]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DefaultImageRegex = new(@"^mcr\.microsoft\.com/dotnet/(?<family>aspnet|runtime|runtime-deps):(?<version>\d+\.\d+)$", RegexOptions.Compiled);

    public async Task<MigrationPlan> PlanAsync(string dockerfilePath, IReadOnlyList<string> projectFiles, CancellationToken cancellationToken) {
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

        // Fold the runtime stage chain, root stage first, the way Docker layers it.
        var values = new Dictionary<string, string>(info.GlobalArgs, StringComparer.Ordinal);
        var env = new List<(string Key, string Value)>();
        var labels = new List<(string Key, string Value)>();
        var ports = new List<(string Port, string Type)>();
        string? workDir = null, user = null;
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
                            if (!ports.Contains((port, type))) ports.Add((port, type));
                        }
                        break;
                    case "WORKDIR":
                        workDir = DockerfileSubstitution.Substitute(DockerfileSubstitution.Unquote(args), values, unresolved);
                        break;
                    case "USER":
                        user = DockerfileSubstitution.Substitute(DockerfileSubstitution.Unquote(args), values, unresolved);
                        break;
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

        if (IsSdkDefaultBaseImage(baseImage, project)) {
            plan.Notes.Add($"ContainerBaseImage not written: {baseImage} is what the SDK picks for {project.TargetFramework}, and an explicit pin would not follow a target framework change");
        }
        else {
            properties.Add(new XElement("ContainerBaseImage", EscapeProperty(baseImage)));
        }

        if (workDir is { } && !IsDefaultWorkDir(workDir)) {
            properties.Add(new XElement("ContainerWorkingDirectory", EscapeProperty(workDir)));
        }
        if (user is { }) {
            properties.Add(new XElement("ContainerUser", EscapeProperty(user)));
        }

        var entrypointIsDefault = entrypoint is { } && IsDefaultAppCommand(entrypoint, project);
        var cmdIsDefault = cmd is { } && IsDefaultAppCommand(cmd, project);
        if (entrypoint is { } && !entrypointIsDefault) {
            // CONTAINER2027: an entrypoint without ContainerAppCommandInstruction is an error. DefaultArgs
            // keeps the SDK's `dotnet app.dll` as CMD, None reproduces the Dockerfile verbatim.
            foreach (var part in entrypoint) items.Add(new XElement("ContainerEntrypoint", new XAttribute("Include", EscapeItemSpec(part))));
            properties.Add(new XElement("ContainerAppCommandInstruction", cmdIsDefault ? "DefaultArgs" : "None"));
            if (cmd is { } && !cmdIsDefault) {
                foreach (var part in cmd) items.Add(new XElement("ContainerDefaultArgs", new XAttribute("Include", EscapeItemSpec(part))));
            }
            if (cmd is null) plan.Notes.Add("ENTRYPOINT replaces the SDK's app command; the entrypoint must start the application itself");
        }
        else {
            if (entrypointIsDefault) plan.Notes.Add("ENTRYPOINT matches the SDK's default app command; not written");
            if (cmd is { } && !cmdIsDefault) {
                if (entrypoint is null) {
                    // No ENTRYPOINT means CMD is the command. None keeps the app command out of the image.
                    properties.Add(new XElement("ContainerAppCommandInstruction", "None"));
                    plan.Notes.Add("CMD without ENTRYPOINT is written as ContainerDefaultArgs with ContainerAppCommandInstruction=None; the image will not start the application by itself");
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

        plan.Elements.Add(properties);
        if (items.HasElements) plan.Elements.Add(items);

        foreach (var publish in publishes.Where(p => p.Verb == "publish")) {
            var extra = publish.ExtraArguments;
            if (extra.Count > 0) {
                plan.Notes.Add($"dotnet publish ran with `{string.Join(" ", extra)}`; pass the same arguments to `dotnet publish /t:PublishContainer` or set the properties in the project");
            }
        }
        if (unresolved.Count > 0) {
            plan.Notes.Add($"build args without a default were left as written: {string.Join(", ", unresolved)}");
        }
        if (plan.DockerToolsReferences.Count > 0) {
            plan.Notes.Add($"Visual Studio container tools settings present ({string.Join(", ", plan.DockerToolsReferences)}); removed only together with the Dockerfile");
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

    private sealed record PublishCommand(DockerStage Stage, string Verb, string? Project, string? Output, List<string> ExtraArguments);

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
                foreach (Match match in DotnetCommandRegex.Matches(run.Arguments)) {
                    var tokens = DockerfileSubstitution.Tokenize(DockerfileSubstitution.Substitute(match.Groups["args"].Value, values))
                        .Select(DockerfileSubstitution.Unquote).ToList();
                    string? project = null, output = null;
                    var extra = new List<string>();
                    for (var i = 0; i < tokens.Count; i++) {
                        var token = tokens[i];
                        var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                        if (token is "-o" or "--output" && next is { }) { output = next; i++; }
                        else if (token is "-c" or "--configuration" && next is { }) { i++; }
                        else if (token.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) { project = token; }
                        else if (token.StartsWith("--output=", StringComparison.Ordinal)) { output = token[9..]; }
                        else if (IsConfigurationSwitch(token)) { }
                        else { extra.Add(token); }
                    }
                    result.Add(new PublishCommand(stage, match.Groups[1].Value.ToLowerInvariant(), project, output, extra));
                }
            }
        }
        return result;
    }

    /// <summary>The configuration is the SDK's business at publish time, not something to carry over.</summary>
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
            if (dll is { }) candidates.Add(Path.GetFileNameWithoutExtension(dll) + ".csproj");
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

        var siblings = Directory.Exists(dockerfileDir) ? Directory.GetFiles(dockerfileDir, "*.csproj") : [];
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

    private static string NormalizeDir(string path) => path.Replace('\\', '/').TrimEnd('/');

    private static string StripRunFlags(string arguments) {
        var tokens = DockerfileSubstitution.Tokenize(arguments);
        return string.Join(' ', tokens.SkipWhile(t => t.StartsWith("--", StringComparison.Ordinal)));
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..77] + "...";

    // ---- Project analysis ----------------------------------------------------------------------

    private sealed record ProjectFacts(string? Sdk, string? TargetFramework, string AssemblyName, bool SelfContained, List<string> ExistingContainerSettings, List<string> DockerToolsReferences);

    private static async Task<ProjectFacts> ReadProjectAsync(string projectPath, CancellationToken cancellationToken) {
        XDocument doc;
        using (var stream = File.OpenRead(projectPath)) {
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }

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

        return new ProjectFacts(sdk, Property("TargetFramework"), assemblyName, selfContained, existing, tools);
    }

    /// <summary>
    /// Whether the image is the one the SDK would compute anyway (see ComputeDotnetBaseImageAndTag):
    /// aspnet for web projects, runtime for the rest, runtime-deps when self-contained, tagged with the
    /// target framework's version. Only then is leaving ContainerBaseImage out the same image.
    /// </summary>
    private static bool IsSdkDefaultBaseImage(string image, ProjectFacts project) {
        var match = DefaultImageRegex.Match(image);
        if (!match.Success || project.TargetFramework is null) return false;
        if (!project.TargetFramework.Equals("net" + match.Groups["version"].Value, StringComparison.OrdinalIgnoreCase)) return false;

        var family = match.Groups["family"].Value;
        if (project.SelfContained) return family == "runtime-deps";
        var isWeb = project.Sdk?.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase) == true;
        return family == (isWeb ? "aspnet" : "runtime");
    }

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

    private static string EscapeProperty(string value) => value.Replace("%", "%25").Replace("$(", "%24(").Replace("@(", "%40(");

    /// <summary>An Include is split on `;` and expanded as a glob, which no command-line argument wants.</summary>
    private static string EscapeItemSpec(string value) =>
        EscapeProperty(value).Replace(";", "%3B").Replace("*", "%2A").Replace("?", "%3F");

    private static string EscapeMetadata(string value) => EscapeProperty(value).Replace(";", "%3B");

    // ---- Project editing -----------------------------------------------------------------------

    /// <summary>The indent unit and newline of the file, read from the first indented child of Project.</summary>
    private static (string Indent, string Newline) DetectLayout(XElement project) {
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

    private static void RemoveWithLeadingWhitespace(XElement element) {
        if (element.PreviousNode is XText previous && string.IsNullOrWhiteSpace(previous.Value)) previous.Remove();
        element.Remove();
    }
}
