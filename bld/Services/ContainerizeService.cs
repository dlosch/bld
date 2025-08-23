using bld.Infrastructure;
using bld.Models;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace bld.Services;

internal class ContainerizeService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public ContainerizeService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    public async Task ContainerizeProjectsAsync(string rootPath, bool applyChanges, CancellationToken cancellationToken) {
        _console.WriteInfo("Starting containerization process...");

        // Discover solutions and projects
        var errorSink = new ErrorSink(_console);
        var slnScanner = new SlnScanner(_options, errorSink);
        var slnParser = new SlnParser(_console, errorSink);

        var projectsWithDockerfiles = new List<(ProjectInfo Project, string DockerfilePath)>();

        await foreach (var slnPath in slnScanner.Enumerate(rootPath)) {
            _console.WriteVerbose($"Processing solution: {slnPath}");
            
            await foreach (var projCfg in slnParser.ParseSolution(slnPath)) {
                var projParser = new ProjParser(_console, errorSink, _options);
                var projectInfo = projParser.LoadProject(projCfg, Array.Empty<string>());
                
                if (projectInfo != null) {
                    var projectDir = Path.GetDirectoryName(projectInfo.ProjectPath);
                    if (projectDir != null) {
                        var dockerfilePath = FindDockerfile(projectDir);
                        if (dockerfilePath != null) {
                            projectsWithDockerfiles.Add((projectInfo, dockerfilePath));
                        }
                    }
                }
            }
        }

        _console.WriteInfo($"Found {projectsWithDockerfiles.Count} projects with Dockerfiles");

        foreach (var (project, dockerfilePath) in projectsWithDockerfiles) {
            await ProcessProjectDockerfileAsync(project, dockerfilePath, applyChanges, cancellationToken);
        }
    }

    private static string? FindDockerfile(string projectDir) {
        var dockerfilePaths = new[] {
            Path.Combine(projectDir, "Dockerfile"),
            Path.Combine(projectDir, "dockerfile"),
            Path.Combine(projectDir, "Dockerfile.production"),
            Path.Combine(projectDir, "Dockerfile.prod")
        };

        return dockerfilePaths.FirstOrDefault(File.Exists);
    }

    private async Task ProcessProjectDockerfileAsync(ProjectInfo project, string dockerfilePath, bool applyChanges, CancellationToken cancellationToken) {
        _console.WriteInfo($"Processing project: {project.ProjectName ?? Path.GetFileNameWithoutExtension(project.ProjectPath)}");
        _console.WriteVerbose($"  Project path: {project.ProjectPath}");
        _console.WriteVerbose($"  Dockerfile path: {dockerfilePath}");

        try {
            var dockerfileContent = await File.ReadAllTextAsync(dockerfilePath, cancellationToken);
            var containerProperties = ParseDockerfile(dockerfileContent);

            if (containerProperties.Any()) {
                _console.WriteInfo($"  Found {containerProperties.Count} container properties to convert");
                
                if (applyChanges) {
                    await ApplyContainerPropertiesToProjectAsync(project.ProjectPath, containerProperties, cancellationToken);
                    _console.WriteInfo($"  Updated project file with container properties");
                } else {
                    _console.WriteInfo("  Container properties that would be added:");
                    foreach (var prop in containerProperties) {
                        _console.WriteInfo($"    {prop.Key} = {prop.Value}");
                    }
                }
            } else {
                _console.WriteInfo("  No convertible container properties found");
            }
        }
        catch (Exception ex) {
            _console.WriteError($"  Error processing {project.ProjectPath}: {ex.Message}");
        }
    }

    private Dictionary<string, string> ParseDockerfile(string dockerfileContent) {
        var properties = new Dictionary<string, string>();
        var lines = dockerfileContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines) {
            var trimmedLine = line.Trim();
            
            // Parse FROM instruction to get base image
            if (trimmedLine.StartsWith("FROM ", StringComparison.OrdinalIgnoreCase)) {
                var fromMatch = Regex.Match(trimmedLine, @"FROM\s+([^\s]+)", RegexOptions.IgnoreCase);
                if (fromMatch.Success) {
                    var baseImage = fromMatch.Groups[1].Value;
                    
                    // Convert common .NET base images to container properties
                    if (baseImage.Contains("mcr.microsoft.com/dotnet/runtime")) {
                        properties["ContainerBaseImage"] = baseImage;
                    } else if (baseImage.Contains("mcr.microsoft.com/dotnet/aspnet")) {
                        properties["ContainerBaseImage"] = baseImage;
                    } else if (baseImage.Contains("alpine")) {
                        properties["ContainerBaseImage"] = baseImage;
                    } else {
                        properties["ContainerBaseImage"] = baseImage;
                    }
                }
            }

            // Parse LABEL instructions
            if (trimmedLine.StartsWith("LABEL ", StringComparison.OrdinalIgnoreCase)) {
                var labelMatch = Regex.Match(trimmedLine, @"LABEL\s+([^=]+)=(.+)", RegexOptions.IgnoreCase);
                if (labelMatch.Success) {
                    var key = labelMatch.Groups[1].Value.Trim();
                    var value = labelMatch.Groups[2].Value.Trim().Trim('"');
                    
                    // Convert common labels to container properties
                    switch (key.ToLowerInvariant()) {
                        case "version":
                            properties["ContainerImageTag"] = value;
                            break;
                        case "description":
                            properties["ContainerDescription"] = value;
                            break;
                        case "maintainer":
                        case "author":
                            properties["ContainerAuthor"] = value;
                            break;
                        default:
                            // Add custom labels
                            properties[$"ContainerLabel"] = $"{key}={value}";
                            break;
                    }
                }
            }

            // Parse EXPOSE instructions
            if (trimmedLine.StartsWith("EXPOSE ", StringComparison.OrdinalIgnoreCase)) {
                var exposeMatch = Regex.Match(trimmedLine, @"EXPOSE\s+(\d+)", RegexOptions.IgnoreCase);
                if (exposeMatch.Success) {
                    var port = exposeMatch.Groups[1].Value;
                    properties["ContainerPort"] = port;
                }
            }

            // Parse WORKDIR instructions
            if (trimmedLine.StartsWith("WORKDIR ", StringComparison.OrdinalIgnoreCase)) {
                var workdirMatch = Regex.Match(trimmedLine, @"WORKDIR\s+(.+)", RegexOptions.IgnoreCase);
                if (workdirMatch.Success) {
                    var workdir = workdirMatch.Groups[1].Value.Trim();
                    properties["ContainerWorkingDirectory"] = workdir;
                }
            }
        }

        return properties;
    }

    private async Task ApplyContainerPropertiesToProjectAsync(string projectPath, Dictionary<string, string> containerProperties, CancellationToken cancellationToken) {
        var doc = XDocument.Load(projectPath);
        var projectElement = doc.Root;

        if (projectElement == null) {
            throw new InvalidOperationException("Invalid project file structure");
        }

        // Find or create a PropertyGroup for container properties
        var containerPropertyGroup = projectElement
            .Elements("PropertyGroup")
            .FirstOrDefault(pg => pg.Elements().Any(e => e.Name.LocalName.StartsWith("Container")));

        if (containerPropertyGroup == null) {
            containerPropertyGroup = new XElement("PropertyGroup");
            containerPropertyGroup.Add(new XComment(" Container Properties "));
            projectElement.Add(containerPropertyGroup);
        }

        // Add container properties
        foreach (var property in containerProperties) {
            var existingProperty = containerPropertyGroup.Element(property.Key);
            if (existingProperty == null) {
                containerPropertyGroup.Add(new XElement(property.Key, property.Value));
            } else {
                existingProperty.Value = property.Value;
            }
        }

        // Save the updated project file
        await using var stream = new FileStream(projectPath, FileMode.Create, FileAccess.Write);
        await doc.SaveAsync(stream, SaveOptions.None, cancellationToken);
    }
}