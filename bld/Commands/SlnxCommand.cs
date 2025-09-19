using bld.Infrastructure;
using bld.Models;
using bld.Services;
using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text;
using System.Xml.Linq;

namespace bld.Commands;

internal sealed class SlnxCommand : BaseCommand
{
    private readonly Option<string?> _outputOption = new Option<string?>("--output", "-o")
    {
        Description = "Output slnx file name. Defaults to directory name with .slnx extension.",
        DefaultValueFactory = _ => null
    };

    private readonly Option<bool> _updateOption = new Option<bool>("--update", "-u")
    {
        Description = "Update existing slnx file if it exists.",
        DefaultValueFactory = _ => true
    };

    public SlnxCommand(IConsoleOutput console) : base("slnx", "Create or update a .slnx file with all projects organized by type.", console)
    {
        Add(_rootOption);
        Add(_depthOption);
        Add(_outputOption);
        Add(_updateOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_rootArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var options = new CleaningOptions
        {
            Delete = false,
            CleanOnlyNonCurrentTfms = false,
            CleanObjDirectory = false,
            CleanNupkgFiles = false,
            LogLevel = parseResult.GetValue(_logLevelOption),
            Depth = parseResult.GetValue(_depthOption),
            VSToolsPath = parseResult.GetValue(_vsToolsPath),
            NoResolveVSToolsPath = parseResult.GetValue(_noResolveVsToolsPath),
        };

        if (!options.NoResolveVSToolsPath && string.IsNullOrEmpty(options.VSToolsPath))
        {
            options.VSToolsPath = TryResolveVSToolsPath(out var vsRoot);
            options.VSRootPath = vsRoot;
        }

        base.Console = new SpectreConsoleOutput(options.LogLevel);

        var rootPath = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            rootPath = Environment.CurrentDirectory;
        }

        var outputFile = parseResult.GetValue(_outputOption);
        var updateExisting = parseResult.GetValue(_updateOption);

        return await CreateSlnxFileAsync(rootPath, outputFile, updateExisting, options);
    }

    private async Task<int> CreateSlnxFileAsync(string rootPath, string? outputFile, bool updateExisting, CleaningOptions options)
    {
        try
        {
            // Determine output file path
            var rootDir = Path.GetFullPath(rootPath);
            if (string.IsNullOrEmpty(outputFile))
            {
                var dirName = Path.GetFileName(rootDir);
                outputFile = Path.Combine(rootDir, $"{dirName}.slnx");
            }
            else if (!Path.IsPathFullyQualified(outputFile))
            {
                outputFile = Path.Combine(rootDir, outputFile);
            }

            Console.WriteInfo($"Creating/updating slnx file: {outputFile}");
            Console.WriteInfo($"Scanning for projects in: {rootDir}");

            // Discover all project files
            var projectFiles = await DiscoverProjectFilesAsync(rootDir, options);
            
            if (!projectFiles.Any())
            {
                Console.WriteWarning("No project files found.");
                return 0;
            }

            Console.WriteInfo($"Found {projectFiles.Count} project files");

            // Parse projects and categorize them
            var projectInfos = await ParseProjectsAsync(projectFiles, options);
            var categorizedProjects = CategorizeProjects(projectInfos);

            // Generate slnx content
            var slnxContent = GenerateSlnxContent(categorizedProjects);

            // Check if file exists and whether to update
            if (File.Exists(outputFile) && !updateExisting)
            {
                Console.WriteWarning($"File {outputFile} already exists. Use --update to overwrite.");
                return 1;
            }

            // Write the file
            await File.WriteAllTextAsync(outputFile, slnxContent);
            Console.WriteInfo($"Successfully created {outputFile}");

            // Display summary
            DisplaySummary(categorizedProjects);

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteError($"Error creating slnx file: {ex.Message}");
            return 1;
        }
    }

    private async Task<List<string>> DiscoverProjectFilesAsync(string rootPath, CleaningOptions options)
    {
        var projectFiles = new List<string>();
        var extensions = new[] { "*.csproj", "*.fsproj", "*.vbproj" };

        foreach (var extension in extensions)
        {
            var files = Directory.EnumerateFiles(rootPath, extension, new EnumerationOptions
            {
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                MatchType = MatchType.Win32,
                MaxRecursionDepth = options.Depth,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = false
            });

            projectFiles.AddRange(files);
        }

        return projectFiles;
    }

    private async Task<List<ProjectInfo>> ParseProjectsAsync(List<string> projectFiles, CleaningOptions options)
    {
        var projectInfos = new List<ProjectInfo>();
        var errorSink = new ErrorSink(Console);
        var parser = new ProjParser(Console, errorSink, options);

        foreach (var projectFile in projectFiles)
        {
            try
            {
                // Try MSBuild parsing first
                var proj = new Proj(projectFile, null);
                var projCfg = new ProjCfg(proj, "Debug"); // Use Debug configuration as default
                var projectInfo = parser.LoadProject(projCfg, ProjConstants.PropertyNames);
                
                if (projectInfo != null)
                {
                    projectInfos.Add(projectInfo);
                    Console.WriteVerbose($"Parsed project via MSBuild: {projectInfo.ProjectName ?? Path.GetFileNameWithoutExtension(projectFile)}");
                }
                else
                {
                    // Fallback to simple parsing
                    var fallbackInfo = await ParseProjectSimpleAsync(projectFile);
                    if (fallbackInfo != null)
                    {
                        projectInfos.Add(fallbackInfo);
                        Console.WriteVerbose($"Parsed project via fallback: {fallbackInfo.ProjectName ?? Path.GetFileNameWithoutExtension(projectFile)}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteWarning($"MSBuild parsing failed for {projectFile}: {ex.Message}");
                
                // Fallback to simple parsing
                try
                {
                    var fallbackInfo = await ParseProjectSimpleAsync(projectFile);
                    if (fallbackInfo != null)
                    {
                        projectInfos.Add(fallbackInfo);
                        Console.WriteVerbose($"Parsed project via fallback: {fallbackInfo.ProjectName ?? Path.GetFileNameWithoutExtension(projectFile)}");
                    }
                }
                catch (Exception fallbackEx)
                {
                    Console.WriteWarning($"Both MSBuild and fallback parsing failed for {projectFile}: {fallbackEx.Message}");
                }
            }
        }

        return projectInfos;
    }

    private async Task<ProjectInfo?> ParseProjectSimpleAsync(string projectPath)
    {
        var content = await File.ReadAllTextAsync(projectPath);
        var projectName = Path.GetFileNameWithoutExtension(projectPath);
        
        // Extract basic properties from XML
        var properties = new Dictionary<string, string>();
        
        // Simple regex patterns to extract key properties
        var patterns = new Dictionary<string, string>
        {
            ["OutputType"] = @"<OutputType>([^<]+)</OutputType>",
            ["Sdk"] = @"<Project\s+Sdk=""([^""]+)""",
            ["TargetFramework"] = @"<TargetFramework>([^<]+)</TargetFramework>",
            ["TargetFrameworks"] = @"<TargetFrameworks>([^<]+)</TargetFrameworks>",
            ["UseWPF"] = @"<UseWPF>([^<]+)</UseWPF>",
            ["UseWindowsForms"] = @"<UseWindowsForms>([^<]+)</UseWindowsForms>",
            ["IsPackable"] = @"<IsPackable>([^<]+)</IsPackable>",
            ["AssemblyName"] = @"<AssemblyName>([^<]+)</AssemblyName>",
            ["ProjectName"] = @"<ProjectName>([^<]+)</ProjectName>",
        };

        foreach (var pattern in patterns)
        {
            var match = System.Text.RegularExpressions.Regex.Match(content, pattern.Value, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
            {
                properties[pattern.Key] = match.Groups[1].Value.Trim();
            }
        }

        // Set defaults
        if (!properties.ContainsKey("ProjectName"))
            properties["ProjectName"] = projectName;
        
        if (!properties.ContainsKey("AssemblyName"))
            properties["AssemblyName"] = projectName;

        var targetFramework = properties.TryGetValue("TargetFramework", out var tf) ? tf : "";
        var targetFrameworks = properties.TryGetValue("TargetFrameworks", out var tfs) ? 
            tfs.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList() : 
            (string.IsNullOrEmpty(targetFramework) ? new List<string>() : new List<string> { targetFramework });

        return new ProjectInfo
        {
            ProjectPath = projectPath,
            ProjectName = properties.GetValueOrDefault("ProjectName", projectName),
            AssemblyName = properties.GetValueOrDefault("AssemblyName", projectName),
            TargetFramework = targetFramework,
            TargetFrameworks = targetFrameworks,
            Configuration = "Debug",
            Properties = properties
        };
    }

    private Dictionary<SlnxProjectType, List<ProjectInfo>> CategorizeProjects(List<ProjectInfo> projects)
    {
        var categorized = new Dictionary<SlnxProjectType, List<ProjectInfo>>();

        foreach (var project in projects)
        {
            var type = project.SlnxProjectType;
            if (!categorized.ContainsKey(type))
            {
                categorized[type] = new List<ProjectInfo>();
            }
            categorized[type].Add(project);
        }

        return categorized;
    }

    private string GenerateSlnxContent(Dictionary<SlnxProjectType, List<ProjectInfo>> categorizedProjects)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("Solution",
                new XElement("Configurations",
                    new XElement("Platform", new XAttribute("Name", "Any CPU")),
                    new XElement("Platform", new XAttribute("Name", "x64")),
                    new XElement("Platform", new XAttribute("Name", "x86"))
                )
            )
        );

        var solutionElement = doc.Root!;

        // Add projects organized by folders
        foreach (var category in categorizedProjects.OrderBy(kvp => kvp.Key.ToString()))
        {
            var folderName = GetFolderName(category.Key);
            
            if (category.Value.Count == 1 && category.Key == SlnxProjectType.Unknown)
            {
                // Don't create a folder for single unknown projects
                var project = category.Value.First();
                solutionElement.Add(new XElement("Project", new XAttribute("Path", GetRelativeProjectPath(project.ProjectPath))));
            }
            else
            {
                var folderPath = $"/{folderName}/";
                var folderElement = new XElement("Folder", new XAttribute("Name", folderPath));
                
                foreach (var project in category.Value.OrderBy(p => p.ProjectName ?? Path.GetFileNameWithoutExtension(p.ProjectPath)))
                {
                    folderElement.Add(new XElement("Project", new XAttribute("Path", GetRelativeProjectPath(project.ProjectPath))));
                }
                
                solutionElement.Add(folderElement);
            }
        }

        return doc.ToString();
    }

    private string GetFolderName(SlnxProjectType type)
    {
        return type switch
        {
            SlnxProjectType.Web => "Web",
            SlnxProjectType.Console => "Console",
            SlnxProjectType.Library => "Libraries",
            SlnxProjectType.NuGet => "NuGet Packages",
            SlnxProjectType.Tests => "Tests",
            SlnxProjectType.WPF => "WPF Applications",
            SlnxProjectType.WinForms => "WinForms Applications",
            SlnxProjectType.Blazor => "Blazor Applications",
            SlnxProjectType.Worker => "Worker Services",
            SlnxProjectType.Function => "Azure Functions",
            _ => "Other"
        };
    }

    private string GetRelativeProjectPath(string projectPath)
    {
        var currentDir = Environment.CurrentDirectory;
        var relativePath = Path.GetRelativePath(currentDir, projectPath);
        return relativePath.Replace('\\', '/'); // Use forward slashes for consistency
    }

    private void DisplaySummary(Dictionary<SlnxProjectType, List<ProjectInfo>> categorizedProjects)
    {
        Console.WriteInfo("\nProject Summary:");
        foreach (var category in categorizedProjects.OrderBy(kvp => kvp.Key.ToString()))
        {
            var folderName = GetFolderName(category.Key);
            var count = category.Value.Count;
            Console.WriteInfo($"  {folderName}: {count} project{(count == 1 ? "" : "s")}");
            
            foreach (var project in category.Value.OrderBy(p => p.ProjectName ?? Path.GetFileNameWithoutExtension(p.ProjectPath)))
            {
                var name = project.ProjectName ?? Path.GetFileNameWithoutExtension(project.ProjectPath);
                Console.WriteVerbose($"    - {name}");
            }
        }
    }
}