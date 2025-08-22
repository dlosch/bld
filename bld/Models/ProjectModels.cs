namespace bld.Models;

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