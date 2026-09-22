using bld.Infrastructure;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using static bld.Services.ContainerMigrationService;

namespace bld.Services;

/// <summary>
/// Checks the SDK container settings a project carries against what Microsoft.NET.Build.Containers
/// reads, and rewrites the ones that have an exact equivalent: the obsolete ContainerImageName, a
/// ContainerBaseImage that is the image the SDK computes anyway or a family variant of it, deprecated
/// ContainerEntrypoint items where the app command reproduces them. Everything that needs a decision
/// (an invalid value, two settings that contradict each other) is reported only. The project file is
/// read as XML, so values that come from imports are unknown and left alone.
/// </summary>
internal sealed class ContainerValidationService {
    private readonly IConsoleOutput _console;

    public ContainerValidationService(IConsoleOutput console) {
        _console = console;
    }

    internal sealed class Finding {
        public string Setting { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
        /// <summary>What --apply writes, in words; null when the finding needs the user's decision.</summary>
        public string? Fix { get; init; }
        /// <summary>Applies the fix to a freshly loaded Project element; true when it changed something.</summary>
        internal Func<XElement, bool>? Apply { get; init; }
        /// <summary>Set when the user declined the fix; <see cref="ApplyAsync"/> leaves it alone.</summary>
        public bool Skip { get; set; }
    }

    internal sealed class Report {
        public string ProjectPath { get; init; } = string.Empty;
        public int SettingCount { get; init; }
        public List<Finding> Findings { get; } = new();
        public int Fixable => Findings.Count(f => f.Apply is { } && !f.Skip);
    }

    // Everything Microsoft.NET.Build.Containers.targets (11.0) reads from the project, plus the Visual
    // Studio container-tools property that shares the prefix.
    private static readonly HashSet<string> KnownSettings = new(StringComparer.Ordinal) {
        "ContainerAppCommand", "ContainerAppCommandArgs", "ContainerAppCommandInstruction", "ContainerArchiveOutputPath",
        "ContainerAuthors", "ContainerBaseDigest", "ContainerBaseImage", "ContainerBaseImageDigest", "ContainerBaseName",
        "ContainerBaseRegistry", "ContainerBaseTag", "ContainerCustomTasksAssembly", "ContainerCustomTasksFolder",
        "ContainerDefaultArgs", "ContainerDescription", "ContainerDocumentationUrl", "ContainerEntrypoint",
        "ContainerEntrypointArgs", "ContainerEnvironmentVariable", "ContainerEnvironmentVariables", "ContainerFamily",
        "ContainerGenerateLabels", "ContainerGenerateLabelsDotnetToolset", "ContainerGenerateLabelsImageAuthors",
        "ContainerGenerateLabelsImageBaseDigest", "ContainerGenerateLabelsImageBaseName", "ContainerGenerateLabelsImageCreated",
        "ContainerGenerateLabelsImageDescription", "ContainerGenerateLabelsImageDocumentation", "ContainerGenerateLabelsImageLicenses",
        "ContainerGenerateLabelsImageRevision", "ContainerGenerateLabelsImageSource", "ContainerGenerateLabelsImageTitle",
        "ContainerGenerateLabelsImageUrl", "ContainerGenerateLabelsImageVendor", "ContainerGenerateLabelsImageVersion",
        "ContainerImageFormat", "ContainerImageName", "ContainerImageTag", "ContainerImageTags", "ContainerInformationUrl",
        "ContainerLabel", "ContainerLicenseExpression", "ContainerPort", "ContainerPublishInParallel", "ContainerPushNoCache",
        "ContainerRegistry", "ContainerRepository", "ContainerRuntimeIdentifier", "ContainerRuntimeIdentifiers",
        "ContainerTaskFolderName", "ContainerTaskFramework", "ContainerTitle", "ContainerUser", "ContainerVendor",
        "ContainerVersion", "ContainerWorkingDirectory",
        "ContainerDevelopmentMode",
    };

    private static readonly string[] Instructions = ["DefaultArgs", "Entrypoint", "None"];
    private static readonly string[] ImageFormats = ["Docker", "OCI"];
    private static readonly string[] LocalRegistries = ["Docker", "Podman", "Wslc", "MacOSContainer"];
    private static readonly Regex TagPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex RepositoryPattern = new(@"^[a-z0-9]+([._-][a-z0-9]+)*(/[a-z0-9]+([._-][a-z0-9]+)*)*$", RegexOptions.Compiled);

    /// <summary>Null when the project has no SDK container setting at all.</summary>
    public async Task<Report?> ValidateAsync(string projectPath, CancellationToken cancellationToken) {
        XDocument doc;
        using (var stream = File.OpenRead(projectPath)) {
            doc = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        }
        if (doc.Root is not { } project) return null;

        var facts = await ReadProjectAsync(projectPath, cancellationToken);
        var settings = Settings(project).ToList();
        if (settings.Count == 0 && facts.ExistingContainerSettings.Count == 0) return null;

        var report = new Report { ProjectPath = projectPath, SettingCount = settings.Count };

        foreach (var name in settings.Select(e => e.Name.LocalName).Distinct(StringComparer.Ordinal)) {
            if (KnownSettings.Contains(name)) continue;
            var hint = name == "ContainerImage" ? "; the image name is ContainerRepository" : string.Empty;
            report.Findings.Add(new Finding { Setting = name, Message = $"not a property or item the SDK reads, so it has no effect{hint}" });
        }

        CheckImageName(project, report);
        CheckEntrypoint(project, report);
        CheckBaseImage(project, facts, report);
        CheckValues(project, report);

        return report;
    }

    /// <summary>Writes every fixable finding of the report that was not skipped into the project. Returns false when nothing was written.</summary>
    public async Task<bool> ApplyAsync(Report report, CancellationToken cancellationToken) {
        try {
            return await XmlProjectFile.EditAsync(report.ProjectPath, doc => {
                if (doc.Root is not { } project) return false;
                var changed = false;
                foreach (var finding in report.Findings) {
                    if (finding.Apply is { } apply && !finding.Skip) changed |= apply(project);
                }
                return changed;
            }, cancellationToken);
        }
        catch (Exception ex) {
            _console.WriteError($"Failed to update {report.ProjectPath}: {ex.FormatMessage()}", ex);
            return false;
        }
    }

    // ---- checks --------------------------------------------------------------------------------

    private static void CheckImageName(XElement project, Report report) {
        foreach (var element in Settings(project, "ContainerImageName")) {
            var value = element.Value.Trim();
            var repository = Literal(project, "ContainerRepository");
            var conditioned = element.IsConditioned();
            // The SDK copies ContainerImageName over ContainerRepository, so the obsolete one wins.
            if (repository is null && !Settings(project, "ContainerRepository").Any()) {
                report.Findings.Add(new Finding {
                    Setting = "ContainerImageName",
                    Message = "obsolete since .NET 8 (the SDK warns CONTAINER003); the property is ContainerRepository",
                    Fix = conditioned ? null : "rename to ContainerRepository",
                    Apply = conditioned ? null : p => Rename(Find(p, "ContainerImageName", value), "ContainerRepository"),
                });
            }
            else if (repository is { } && string.Equals(repository, value, StringComparison.Ordinal)) {
                report.Findings.Add(new Finding {
                    Setting = "ContainerImageName",
                    Message = "obsolete since .NET 8 and the same as ContainerRepository",
                    Fix = conditioned ? null : "remove",
                    Apply = conditioned ? null : p => Remove(Find(p, "ContainerImageName", value)),
                });
            }
            else {
                report.Findings.Add(new Finding {
                    Setting = "ContainerImageName",
                    Message = $"obsolete since .NET 8 and overrides ContainerRepository ({repository ?? "set elsewhere"}): the image is named {value}",
                });
            }
        }
    }

    private static void CheckEntrypoint(XElement project, Report report) {
        var entrypoints = Settings(project, "ContainerEntrypoint").ToList();
        var entrypointArgs = Settings(project, "ContainerEntrypointArgs").ToList();
        if (entrypoints.Count == 0 && entrypointArgs.Count == 0) return;

        var setting = entrypoints.Count > 0 ? "ContainerEntrypoint" : "ContainerEntrypointArgs";
        var instruction = Literal(project, "ContainerAppCommandInstruction");
        var hasAppCommand = Settings(project, "ContainerAppCommand").Any() || Settings(project, "ContainerAppCommandArgs").Any();
        var conditioned = entrypoints.Concat(entrypointArgs).Any(e => e.IsConditioned()) || Settings(project, "ContainerAppCommandInstruction").Any(e => e.IsConditioned());

        // With None the entrypoint items are the whole ENTRYPOINT and ContainerDefaultArgs the CMD, which
        // is exactly what ContainerAppCommand[Args] with Entrypoint produces. In any other mode the SDK's
        // app command (dotnet App.dll) is the CMD behind the entrypoint; the rename would drop it.
        if (string.Equals(instruction, "None", StringComparison.OrdinalIgnoreCase) && !hasAppCommand && !conditioned) {
            report.Findings.Add(new Finding {
                Setting = setting,
                Message = "deprecated since .NET 8; ContainerAppCommand/ContainerAppCommandArgs with ContainerAppCommandInstruction=Entrypoint build the same ENTRYPOINT and CMD",
                Fix = "rename to ContainerAppCommand/ContainerAppCommandArgs and set ContainerAppCommandInstruction=Entrypoint",
                Apply = p => {
                    var changed = false;
                    foreach (var e in Settings(p, "ContainerEntrypoint").ToList()) changed |= Rename(e, "ContainerAppCommand");
                    foreach (var e in Settings(p, "ContainerEntrypointArgs").ToList()) changed |= Rename(e, "ContainerAppCommandArgs");
                    foreach (var e in Settings(p, "ContainerAppCommandInstruction")) { e.Value = "Entrypoint"; changed = true; }
                    return changed;
                },
            });
        }
        else {
            var mode = instruction is null ? "no ContainerAppCommandInstruction" : $"ContainerAppCommandInstruction={instruction}";
            report.Findings.Add(new Finding {
                Setting = setting,
                Message = $"deprecated since .NET 8; with {mode} the SDK keeps its app command (dotnet <App>.dll) as the CMD behind this entrypoint. To keep that, move the entrypoint to ContainerAppCommand with ContainerAppCommandInstruction=Entrypoint and the app command to ContainerDefaultArgs",
            });
        }
    }

    private static void CheckBaseImage(XElement project, ProjectFacts facts, Report report) {
        var familyValue = Literal(project, "ContainerFamily");
        var familySet = Settings(project, "ContainerFamily").Any();

        foreach (var element in Settings(project, "ContainerBaseImage")) {
            var image = element.Value.Trim();
            if (image.Contains("$(")) continue;
            var conditioned = element.IsConditioned() || Settings(project, "ContainerFamily").Any(e => e.IsConditioned());

            var family = SdkImageFamily(image, facts, out var architecture);
            if (family is null) {
                if (TryParseSdkImage(image, out var repo, out var version, out _) && facts.TargetFramework is { }) {
                    // A Microsoft image, but not the one the SDK would pick: say what ContainerFamily would change.
                    var expected = SdkImageName(facts);
                    if (expected is { } && !string.Equals(repo, SdkImageRepository(facts), StringComparison.Ordinal)) {
                        report.Findings.Add(new Finding { Setting = "ContainerBaseImage", Message = $"pins {repo}; the SDK would pick {expected} for this project, so ContainerFamily cannot stand in for it" });
                    }
                    else if (expected is { } && !facts.TargetFramework.Equals("net" + version, StringComparison.OrdinalIgnoreCase)) {
                        report.Findings.Add(new Finding { Setting = "ContainerBaseImage", Message = $"pins version {version} while the project targets {facts.TargetFramework}; the SDK would pick {expected}" });
                    }
                }
                if (familySet) {
                    report.Findings.Add(new Finding { Setting = "ContainerFamily", Message = $"ignored while ContainerBaseImage is set ({image})" });
                }
                continue;
            }

            var platform = architecture is { } ? $"; the -{architecture} platform then follows the RuntimeIdentifier (set ContainerRuntimeIdentifier to pin it)" : string.Empty;
            // A platform suffix is the one part ContainerFamily cannot carry: dropping it hands the
            // architecture to the RuntimeIdentifier, so the image follows the building machine. That is
            // a decision, not an exact equivalent, and --apply does not make it.
            var fixable = !conditioned && architecture is null;
            if (family.Length == 0) {
                if (familySet && !string.IsNullOrEmpty(familyValue)) {
                    report.Findings.Add(new Finding { Setting = "ContainerBaseImage", Message = $"{image} is what the SDK picks for {facts.TargetFramework}, but removing it would activate ContainerFamily={familyValue}, a different image" });
                    continue;
                }
                report.Findings.Add(new Finding {
                    Setting = "ContainerBaseImage",
                    Message = $"{image} is what the SDK picks for {facts.TargetFramework}; the pin only stops the image from following a target framework change{platform}",
                    Fix = fixable ? "remove" : null,
                    Apply = fixable ? p => Remove(Find(p, "ContainerBaseImage", image)) : null,
                });
                continue;
            }

            if (!familySet) {
                report.Findings.Add(new Finding {
                    Setting = "ContainerBaseImage",
                    Message = $"{image} is the {family} variant of what the SDK picks for {facts.TargetFramework}; ContainerFamily={family} keeps the variant and follows a target framework change{platform}",
                    Fix = fixable ? $"replace with ContainerFamily={family}" : null,
                    Apply = !fixable ? null : p => {
                        var e = Find(p, "ContainerBaseImage", image);
                        if (e is null) return false;
                        Rename(e, "ContainerFamily");
                        e.Value = family;
                        return true;
                    },
                });
            }
            else if (string.Equals(familyValue, family, StringComparison.OrdinalIgnoreCase)) {
                report.Findings.Add(new Finding {
                    Setting = "ContainerBaseImage",
                    Message = $"{image} is what ContainerFamily={familyValue} already selects for {facts.TargetFramework}{platform}",
                    Fix = fixable ? "remove" : null,
                    Apply = fixable ? p => Remove(Find(p, "ContainerBaseImage", image)) : null,
                });
            }
            else {
                report.Findings.Add(new Finding { Setting = "ContainerFamily", Message = $"ignored while ContainerBaseImage is set, which selects the {family} variant instead of {familyValue ?? "the configured one"}" });
            }
        }
    }

    private static void CheckValues(XElement project, Report report) {
        CheckChoice(project, report, "ContainerAppCommandInstruction", Instructions);
        CheckChoice(project, report, "ContainerImageFormat", ImageFormats);
        CheckChoice(project, report, "LocalRegistry", LocalRegistries);

        var tag = Literal(project, "ContainerImageTag");
        var tags = Literal(project, "ContainerImageTags");
        if (tag is { } && tag.Contains(';')) {
            report.Findings.Add(new Finding { Setting = "ContainerImageTag", Message = "holds several tags; a list belongs in ContainerImageTags" });
        }
        else if (tag is { } && !TagPattern.IsMatch(tag)) {
            report.Findings.Add(new Finding { Setting = "ContainerImageTag", Message = $"'{tag}' is not a valid tag: up to 128 letters, digits, '_', '.' and '-', starting with a letter, digit or '_'" });
        }
        foreach (var t in (tags ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            if (!TagPattern.IsMatch(t)) report.Findings.Add(new Finding { Setting = "ContainerImageTags", Message = $"'{t}' is not a valid tag: up to 128 letters, digits, '_', '.' and '-', starting with a letter, digit or '_'" });
        }
        if (tag is { } && tags is { }) {
            report.Findings.Add(new Finding { Setting = "ContainerImageTag", Message = "set together with ContainerImageTags; only the list is used" });
        }

        var repository = Literal(project, "ContainerRepository");
        if (repository is { } && !RepositoryPattern.IsMatch(repository)) {
            report.Findings.Add(new Finding { Setting = "ContainerRepository", Message = $"'{repository}' is not a valid image name: lowercase letters, digits, '.', '_' and '-' in '/'-separated segments that start with a letter or digit" });
        }

        foreach (var port in Settings(project, "ContainerPort")) {
            var include = port.Attribute("Include")?.Value ?? string.Empty;
            if (include.Contains("$(")) continue;
            foreach (var number in include.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                if (!int.TryParse(number, out var n) || n is < 1 or > 65535) {
                    report.Findings.Add(new Finding { Setting = "ContainerPort", Message = $"'{number}' is not a port number (1-65535)" });
                }
            }
            var type = port.Attribute("Type")?.Value;
            if (type is { } && !type.Contains("$(") && type is not ("tcp" or "udp")) {
                report.Findings.Add(new Finding { Setting = "ContainerPort", Message = $"Type '{type}' is not tcp or udp" });
            }
        }

        var containerRids = Literal(project, "ContainerRuntimeIdentifiers");
        var rids = Literal(project, "RuntimeIdentifiers");
        if (containerRids is { } && rids is { }) {
            var allowed = rids.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var missing = containerRids.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(r => !allowed.Contains(r, StringComparer.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0) {
                report.Findings.Add(new Finding { Setting = "ContainerRuntimeIdentifiers", Message = $"{string.Join(", ", missing)} not in RuntimeIdentifiers ({rids}); the SDK requires a subset" });
            }
        }

        foreach (var element in Settings(project, "ContainerWorkingDirectory")) {
            var value = element.Value.Trim();
            if (value.TrimEnd('/') != "/app") continue;
            var conditioned = element.IsConditioned();
            report.Findings.Add(new Finding {
                Setting = "ContainerWorkingDirectory",
                Message = $"{value} is the SDK default",
                Fix = conditioned ? null : "remove",
                Apply = conditioned ? null : p => Remove(Find(p, "ContainerWorkingDirectory", value)),
            });
        }
    }

    private static void CheckChoice(XElement project, Report report, string name, string[] allowed) {
        var value = Literal(project, name);
        if (value is { } && !allowed.Contains(value, StringComparer.OrdinalIgnoreCase)) {
            report.Findings.Add(new Finding { Setting = name, Message = $"'{value}' is not one of {string.Join(", ", allowed)}" });
        }
    }

    // ---- XML helpers ---------------------------------------------------------------------------

    /// <summary>Properties and items with the Container prefix (LocalRegistry has none, so it is asked for by name).</summary>
    private static IEnumerable<XElement> Settings(XElement project) =>
        project.Descendants().Where(e => e.Name.LocalName.StartsWith("Container", StringComparison.Ordinal) && e.Parent?.Name.LocalName is "PropertyGroup" or "ItemGroup");

    private static IEnumerable<XElement> Settings(XElement project, string name) =>
        project.ElementsNamed(name).Where(e => e.Parent?.Name.LocalName is "PropertyGroup" or "ItemGroup");

    /// <summary>The last unconditioned literal value of a property; null when unset or not a literal.</summary>
    private static string? Literal(XElement project, string name) {
        var value = Settings(project, name).Where(e => !e.IsConditioned()).LastOrDefault()?.Value.Trim();
        return string.IsNullOrEmpty(value) || value.Contains("$(") ? null : value;
    }

    private static XElement? Find(XElement project, string name, string value) =>
        Settings(project, name).FirstOrDefault(e => !e.IsConditioned() && string.Equals(e.Value.Trim(), value, StringComparison.Ordinal));

    private static bool Rename(XElement? element, string name) {
        if (element is null) return false;
        element.Name = element.Name.Namespace + name;
        return true;
    }

    private static bool Remove(XElement? element) {
        if (element is null) return false;
        RemoveWithLeadingWhitespace(element);
        return true;
    }
}
