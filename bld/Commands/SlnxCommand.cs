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

    private readonly Argument<string[]> _rootsArgument = new Argument<string[]>("roots")
    {
        Arity = ArgumentArity.ZeroOrMore,
        Description = "Root directories to scan for projects. Defaults to current directory if none specified.",
        Validators = {
            result => {
                var paths = result.GetValueOrDefault<string[]>() ?? Array.Empty<string>();
                foreach (var path in paths)
                {
                    if (!string.IsNullOrEmpty(path) && !Directory.Exists(path) && !File.Exists(path))
                    {
                        result.AddError($"{path} does not exist.");
                    }
                }
            }
        }
    };

    private readonly Option<int> _obsoleteThresholdOption = new Option<int>("--obsolete-threshold", "-t")
    {
        Description = "Threshold in months for considering projects obsolete based on last git commit. Default is 6 months.",
        DefaultValueFactory = _ => 6
    };

    public SlnxCommand(IConsoleOutput console) : base("slnx", "Create or update a .slnx file with all projects organized by type.", console)
    {
        Add(_rootOption);
        Add(_depthOption);
        Add(_outputOption);
        Add(_updateOption);
        Add(_obsoleteThresholdOption);
        Add(_logLevelOption);
        Add(_vsToolsPath);
        Add(_noResolveVsToolsPath);
        Add(_rootsArgument);
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

        var rootPaths = parseResult.GetValue(_rootsArgument) ?? Array.Empty<string>();
        var singleRootFromOption = parseResult.GetValue(_rootArgument) ?? parseResult.GetValue(_rootOption);
        
        // Combine argument roots with option root if specified
        var allRootPaths = new List<string>();
        if (rootPaths.Any())
        {
            allRootPaths.AddRange(rootPaths);
        }
        if (!string.IsNullOrWhiteSpace(singleRootFromOption))
        {
            allRootPaths.Add(singleRootFromOption);
        }
        
        // Default to current directory if no roots specified
        if (!allRootPaths.Any())
        {
            allRootPaths.Add(Environment.CurrentDirectory);
        }

        var outputFile = parseResult.GetValue(_outputOption);
        var updateExisting = parseResult.GetValue(_updateOption);
        var obsoleteThresholdMonths = parseResult.GetValue(_obsoleteThresholdOption);

        return await CreateSlnxFileAsync(allRootPaths, outputFile, updateExisting, obsoleteThresholdMonths, options);
    }

    private async Task<int> CreateSlnxFileAsync(List<string> rootPaths, string? outputFile, bool updateExisting, int obsoleteThresholdMonths, CleaningOptions options)
    {
        try
        {
            // Determine output file path - use first root path for location if not specified
            var primaryRootDir = Path.GetFullPath(rootPaths.First());
            if (string.IsNullOrEmpty(outputFile))
            {
                // When multiple roots, use current directory name for the solution name
                var currentDirName = Path.GetFileName(Environment.CurrentDirectory);
                outputFile = Path.Combine(Environment.CurrentDirectory, $"{currentDirName}.slnx");
            }
            else if (!Path.IsPathFullyQualified(outputFile))
            {
                outputFile = Path.Combine(Environment.CurrentDirectory, outputFile);
            }

            Console.WriteInfo($"Creating/updating slnx file: {outputFile}");
            foreach (var rootPath in rootPaths)
            {
                Console.WriteInfo($"Scanning for projects in: {Path.GetFullPath(rootPath)}");
            }

            // Discover all project files from all root paths
            var projectFiles = await DiscoverProjectFilesAsync(rootPaths, options);
            
            if (!projectFiles.Any())
            {
                Console.WriteWarning("No project files found.");
                return 0;
            }

            Console.WriteInfo($"Found {projectFiles.Count} project files");

            // Parse projects and categorize them
            var projectInfos = await ParseProjectsAsync(projectFiles, options);
            var categorizedProjects = CategorizeProjects(projectInfos, obsoleteThresholdMonths);

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

    private async Task<List<string>> DiscoverProjectFilesAsync(List<string> rootPaths, CleaningOptions options)
    {
        var projectFiles = new List<string>();
        var extensions = new[] { "*.csproj", "*.fsproj", "*.vbproj" };

        foreach (var rootPath in rootPaths)
        {
            var fullRootPath = Path.GetFullPath(rootPath);
            if (!Directory.Exists(fullRootPath))
            {
                Console.WriteWarning($"Root path does not exist: {fullRootPath}");
                continue;
            }

            foreach (var extension in extensions)
            {
                var files = Directory.EnumerateFiles(fullRootPath, extension, new EnumerationOptions
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
        }

        // Remove duplicates that might occur if root paths overlap
        return projectFiles.Distinct().ToList();
    }

    private DateTime? GetLastCommitDate(string projectPath)
    {
        try
        {
            var projectDir = Path.GetDirectoryName(projectPath);
            if (string.IsNullOrEmpty(projectDir))
                return null;

            // Find the git repository root by walking up the directory tree
            var gitRepoPath = FindGitRepository(projectDir);
            if (string.IsNullOrEmpty(gitRepoPath))
                return null;

            // Get the last commit date for files in the project directory and its subdirectories
            var relativePath = Path.GetRelativePath(gitRepoPath, projectDir).Replace('\\', '/');
            
            var processStartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"log -1 --format=%ct -- \"{relativePath}\"",
                WorkingDirectory = gitRepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(processStartInfo);
            if (process == null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();

            if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
            {
                // If no commits found for the specific path, get the last commit for the entire repo
                processStartInfo.Arguments = "log -1 --format=%ct";
                using var fallbackProcess = System.Diagnostics.Process.Start(processStartInfo);
                if (fallbackProcess == null)
                    return null;

                output = fallbackProcess.StandardOutput.ReadToEnd().Trim();
                fallbackProcess.WaitForExit();

                if (fallbackProcess.ExitCode != 0 || string.IsNullOrEmpty(output))
                    return null;
            }

            if (long.TryParse(output, out var unixTimestamp))
            {
                return DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).DateTime;
            }
        }
        catch (Exception ex)
        {
            Console.WriteVerbose($"Failed to get git commit date for {projectPath}: {ex.Message}");
        }

        return null;
    }

    private string? FindGitRepository(string startPath)
    {
        var currentPath = Path.GetFullPath(startPath);
        
        while (!string.IsNullOrEmpty(currentPath))
        {
            var gitPath = Path.Combine(currentPath, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return currentPath;
            }

            var parentPath = Path.GetDirectoryName(currentPath);
            if (parentPath == currentPath) // Reached root
                break;
            
            currentPath = parentPath;
        }

        return null;
    }

    private string GetObsoleteSubFolder(DateTime? lastCommitDate)
    {
        if (!lastCommitDate.HasValue)
            return "Unknown";

        var monthsOld = (int)((DateTime.Now - lastCommitDate.Value).TotalDays / 30.44); // Average days per month

        return monthsOld switch
        {
            < 6 => "Recent", // This shouldn't happen in obsolete group, but safety net
            >= 6 and < 12 => "6-12 Months",
            >= 12 and < 24 => "1-2 Years", 
            >= 24 and < 36 => "2-3 Years",
            >= 36 => "3+ Years"
        };
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

    private Dictionary<string, List<ProjectInfo>> CategorizeProjects(List<ProjectInfo> projects, int obsoleteThresholdMonths)
    {
        var categorized = new Dictionary<string, List<ProjectInfo>>();
        var obsoleteThreshold = DateTime.Now.AddMonths(-obsoleteThresholdMonths);

        foreach (var project in projects)
        {
            var lastCommitDate = GetLastCommitDate(project.ProjectPath);
            var isObsolete = lastCommitDate.HasValue && lastCommitDate.Value < obsoleteThreshold;

            string categoryKey;
            if (isObsolete)
            {
                var subFolder = GetObsoleteSubFolder(lastCommitDate);
                categoryKey = $"Obsolete/{subFolder}";
                Console.WriteVerbose($"Project {project.ProjectName} marked as obsolete (last commit: {lastCommitDate?.ToString("yyyy-MM-dd") ?? "unknown"})");
            }
            else
            {
                var type = project.SlnxProjectType;
                categoryKey = GetFolderName(type);
            }

            if (!categorized.ContainsKey(categoryKey))
            {
                categorized[categoryKey] = new List<ProjectInfo>();
            }
            categorized[categoryKey].Add(project);
        }

        return categorized;
    }

    private string GenerateSlnxContent(Dictionary<string, List<ProjectInfo>> categorizedProjects)
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

        // Sort categories: non-obsolete first, then obsolete
        var sortedCategories = categorizedProjects
            .OrderBy(kvp => kvp.Key.StartsWith("Obsolete/") ? 1 : 0)
            .ThenBy(kvp => kvp.Key);

        // Add projects organized by folders
        foreach (var category in sortedCategories)
        {
            var folderName = category.Key;
            
            if (category.Value.Count == 1 && folderName == "Other")
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

    private void DisplaySummary(Dictionary<string, List<ProjectInfo>> categorizedProjects)
    {
        Console.WriteInfo("\nProject Summary:");
        
        // Sort categories: non-obsolete first, then obsolete
        var sortedCategories = categorizedProjects
            .OrderBy(kvp => kvp.Key.StartsWith("Obsolete/") ? 1 : 0)
            .ThenBy(kvp => kvp.Key);
            
        foreach (var category in sortedCategories)
        {
            var folderName = category.Key;
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