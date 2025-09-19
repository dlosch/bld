namespace bld.Models;

/// <summary>
/// Enhanced project types for solution organization
/// </summary>
public enum SlnxProjectType
{
    Unknown,
    Web,
    Console,
    Library,
    NuGet,
    Tests,
    WPF,
    WinForms,
    Blazor,
    Worker,
    Function
}

/// <summary>
/// Information about a project
/// </summary>
internal record ProjectInfo {

    public string ProjectPath { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public string? AssemblyName { get; init; }
    public string? TargetFramework { get; init; }
    public IReadOnlyList<string> TargetFrameworks { get; init; } = Array.Empty<string>();
    public string? Configuration { get; init; }
    public string? Platform { get; init; }
    //public string? OutputPath { get; init; }
    public string? IntermediateOutputPath { get; init; }
    public string? PackageOutputPath { get; init; }
    public string? PackageId { get; init; }
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
    public bool HasDockerProperties { get; init; }
    public string? OutDir { get; internal set; }
    public string? BaseOutputPath { get; internal set; }
    
    /// <summary>
    /// Determines the SlnxProjectType based on project properties
    /// </summary>
    public SlnxProjectType SlnxProjectType => DetermineProjectType();
    
    private SlnxProjectType DetermineProjectType()
    {
        var outputType = Properties.TryGetValue("OutputType", out var ot) ? ot?.ToLowerInvariant() : "";
        var sdk = Properties.TryGetValue("Sdk", out var s) ? s : "";
        var usingMicrosoftNETSdk = Properties.TryGetValue("UsingMicrosoftNETSdk", out var ms) ? ms : "";
        var isPackable = Properties.TryGetValue("IsPackable", out var pack) && pack?.ToLowerInvariant() == "true";
        var useWpf = Properties.TryGetValue("UseWPF", out var wpf) && wpf?.ToLowerInvariant() == "true";
        var useWinForms = Properties.TryGetValue("UseWindowsForms", out var wf) && wf?.ToLowerInvariant() == "true";
        var targetFramework = TargetFramework ?? "";
        
        // Check for test projects first (most specific)
        if (IsTestProject()) return SlnxProjectType.Tests;
        
        // Check SDK types (most specific)
        if (sdk?.Contains("Microsoft.NET.Sdk.Web") == true || sdk?.Contains("Web") == true)
        {
            return IsBlazorProject() ? SlnxProjectType.Blazor : SlnxProjectType.Web;
        }
        
        if (sdk?.Contains("Microsoft.NET.Sdk.Worker") == true) return SlnxProjectType.Worker;
        if (sdk?.Contains("Microsoft.Azure.Functions") == true) return SlnxProjectType.Function;
        
        // Check UI frameworks
        if (useWpf) return SlnxProjectType.WPF;
        if (useWinForms) return SlnxProjectType.WinForms;
        
        // Check for packable projects (NuGet packages) - but only if it's also a library
        if (isPackable && (outputType == "library" || string.IsNullOrEmpty(outputType))) 
            return SlnxProjectType.NuGet;
        
        // Check output type
        switch (outputType)
        {
            case "exe":
            case "winexe":
                return SlnxProjectType.Console;
            case "library":
            case "":  // Default for SDK-style projects without explicit OutputType is library
                return SlnxProjectType.Library;
            default:
                return SlnxProjectType.Unknown;
        }
    }
    
    private bool IsTestProject()
    {
        var projectName = ProjectName?.ToLowerInvariant() ?? "";
        var assemblyName = AssemblyName?.ToLowerInvariant() ?? "";
        var projectPath = ProjectPath?.ToLowerInvariant() ?? "";
        
        // Check for explicit test project property
        if (Properties.TryGetValue("IsTestProject", out var isTest) && isTest?.ToLowerInvariant() == "true")
            return true;
        
        // Check project name patterns - be more specific
        if (projectName.EndsWith(".tests") || projectName.EndsWith(".test") ||
            assemblyName.EndsWith(".tests") || assemblyName.EndsWith(".test"))
        {
            return true;
        }
        
        // Check path patterns - be more specific
        if (projectPath.Contains("/test/") || projectPath.Contains("\\test\\") ||
            projectPath.Contains("/tests/") || projectPath.Contains("\\tests\\"))
        {
            return true;
        }
        
        return false;
    }
    
    private bool IsBlazorProject()
    {
        // Simple check - would need more sophisticated detection in real implementation
        var projectName = ProjectName?.ToLowerInvariant() ?? "";
        return projectName.Contains("blazor");
    }
}

/// <summary>
/// MSBuild project properties
/// </summary>
internal record ProjectProperties {
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    public string? this[string key] => Properties.TryGetValue(key, out var value) ? value : null;

    public string? OutDir => this["OutDir"];
    //public string? OutputPath => this["OutputPath"];
    public string? BaseOutputPath => this["BaseOutputPath"];
    public string? BaseIntermediateOutputPath => this["BaseIntermediateOutputPath"];
    public string? PackageOutputPath => this["PackageOutputPath"];
    public string? AssemblyName => this["AssemblyName"];
    public string? PackageId => this["PackageId"];
    public string? ProjectName => this["ProjectName"];
    public string? TargetFramework => this["TargetFramework"];
    public string? TargetFrameworks => this["TargetFrameworks"];

    public static ProjectProperties Empty => new();
}