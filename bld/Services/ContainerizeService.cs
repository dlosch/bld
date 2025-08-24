using bld.Infrastructure;
using bld.Models;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace bld.Services;

internal class ContainerizeService {
    private readonly IConsoleOutput _console;
    private readonly CleaningOptions _options;

    public ContainerizeService(IConsoleOutput console, CleaningOptions options) {
        _console = console;
        _options = options;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public async Task ContainerizeProjectsAsync(string rootPath, bool applyChanges, CancellationToken cancellationToken) {
        // Initialize MSBuild before any Microsoft.Build.* types are loaded
        MSBuildInitializer.Initialize(_console, _options);
        
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
        
        for (int i = 0; i < lines.Length; i++) {
            var trimmedLine = lines[i].Trim();
            
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

            // Parse LABEL instructions - support both single-line and multi-line with trailing \
            if (trimmedLine.StartsWith("LABEL ", StringComparison.OrdinalIgnoreCase)) {
                var labelContent = trimmedLine.Substring(6); // Remove "LABEL "
                
                // Handle multi-line labels with trailing \
                while (labelContent.EndsWith("\\") && i + 1 < lines.Length) {
                    labelContent = labelContent.Substring(0, labelContent.Length - 1).Trim(); // Remove trailing \
                    i++;
                    var nextLine = lines[i].Trim();
                    labelContent += " " + nextLine;
                }
                
                ParseLabelContent(labelContent, properties);
            }

            // Parse ENV instructions
            if (trimmedLine.StartsWith("ENV ", StringComparison.OrdinalIgnoreCase)) {
                var envContent = trimmedLine.Substring(4); // Remove "ENV "
                
                // Handle multi-line ENV with trailing \
                while (envContent.EndsWith("\\") && i + 1 < lines.Length) {
                    envContent = envContent.Substring(0, envContent.Length - 1).Trim(); // Remove trailing \
                    i++;
                    var nextLine = lines[i].Trim();
                    envContent += " " + nextLine;
                }
                
                ParseEnvContent(envContent, properties);
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
    
    private void ParseLabelContent(string labelContent, Dictionary<string, string> properties) {
        // Parse multiple key=value pairs in a single LABEL instruction
        // Support both LABEL key1=value1 key2=value2 and LABEL key="value"
        var pairs = new List<(string key, string value)>();
        
        // Simple parsing - split by spaces but respect quoted values
        var parts = new List<string>();
        var currentPart = "";
        bool inQuotes = false;
        
        for (int i = 0; i < labelContent.Length; i++) {
            var c = labelContent[i];
            
            if (c == '"' && (i == 0 || labelContent[i - 1] != '\\')) {
                inQuotes = !inQuotes;
                continue;
            }
            
            if (c == ' ' && !inQuotes && !string.IsNullOrEmpty(currentPart)) {
                parts.Add(currentPart);
                currentPart = "";
                continue;
            }
            
            currentPart += c;
        }
        
        if (!string.IsNullOrEmpty(currentPart)) {
            parts.Add(currentPart);
        }
        
        // Parse each part as key=value
        foreach (var part in parts) {
            var equalIndex = part.IndexOf('=');
            if (equalIndex > 0) {
                var key = part.Substring(0, equalIndex).Trim();
                var value = part.Substring(equalIndex + 1).Trim().Trim('"');
                
                // Convert OCI and common labels to container properties
                switch (key.ToLowerInvariant()) {
                    case "org.opencontainers.image.title":
                        properties["ContainerImageName"] = value;
                        break;
                    case "org.opencontainers.image.description":
                        properties["ContainerDescription"] = value;
                        break;
                    case "org.opencontainers.image.version":
                        properties["ContainerImageTag"] = value;
                        break;
                    case "org.opencontainers.image.authors":
                        properties["ContainerAuthor"] = value;
                        break;
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
                        if (!properties.ContainsKey("ContainerLabel")) {
                            properties["ContainerLabel"] = $"{key}={value}";
                        } else {
                            properties["ContainerLabel"] += $";{key}={value}";
                        }
                        break;
                }
            }
        }
    }
    
    private void ParseEnvContent(string envContent, Dictionary<string, string> properties) {
        // Parse ENV key=value or ENV key value pairs
        var envVars = new List<(string key, string value)>();
        
        // Try to parse as key=value first
        if (envContent.Contains('=')) {
            var pairs = envContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var pair in pairs) {
                var equalIndex = pair.IndexOf('=');
                if (equalIndex > 0) {
                    var key = pair.Substring(0, equalIndex).Trim();
                    var value = pair.Substring(equalIndex + 1).Trim().Trim('"');
                    envVars.Add((key, value));
                }
            }
        } else {
            // Parse as ENV key value format
            var parts = envContent.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2) {
                envVars.Add((parts[0].Trim(), parts[1].Trim().Trim('"')));
            }
        }
        
        // Convert ENV variables to container environment variables
        foreach (var (key, value) in envVars) {
            if (!properties.ContainsKey("ContainerEnvironmentVariable")) {
                properties["ContainerEnvironmentVariable"] = $"{key}={value}";
            } else {
                properties["ContainerEnvironmentVariable"] += $";{key}={value}";
            }
        }
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

        // Save the updated project file without XML declaration
        await using var stream = new FileStream(projectPath, FileMode.Create, FileAccess.Write);
        using var writer = XmlWriter.Create(stream, new XmlWriterSettings {
            Indent = true,
            OmitXmlDeclaration = true,
            Encoding = System.Text.Encoding.UTF8,
            Async = true
        });
        await doc.SaveAsync(writer, cancellationToken);
    }
}