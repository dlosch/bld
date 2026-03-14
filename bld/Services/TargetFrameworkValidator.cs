using System.Diagnostics.CodeAnalysis;

namespace bld.Services;

/// <summary>
/// Validates target framework monikers and determines current vs non-current frameworks
/// </summary>
internal class TargetFrameworkValidator {
    private readonly HashSet<string> _validTfms;
    private static readonly StringComparison DefaultComparison = StringComparison.OrdinalIgnoreCase;

    public TargetFrameworkValidator() {
        _validTfms = new HashSet<string>(ValidTfms, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Known valid target framework monikers
    /// </summary>
    private static readonly string[] ValidTfms =
    [
        // .NET Core
        "netcoreapp1.0",
        "netcoreapp1.1",
        "netcoreapp2.0",
        "netcoreapp2.1",
        "netcoreapp2.2",
        "netcoreapp3.0",
        "netcoreapp3.1",

        // .NET 5+
        "net5.0",
        "net6.0",
        "net7.0",
        "net8.0",
        "net9.0",
        "net10.0",

        // .NET Standard
        "netstandard1.0",
        "netstandard1.1",
        "netstandard1.2",
        "netstandard1.3",
        "netstandard1.4",
        "netstandard1.5",
        "netstandard1.6",
        "netstandard2.0",
        "netstandard2.1",

        // .NET Framework
        "net11",
        "net20",
        "net35",
        "net40",
        "net403",
        "net45",
        "net451",
        "net452",
        "net46",
        "net461",
        "net462",
        "net47",
        "net471",
        "net472",
        "net48",
        "net481"
    ];

    /// <summary>
    /// Checks if a target framework moniker is valid/known
    /// </summary>
    public bool IsValidTfm([NotNullWhen(true)] string? tfm) {
        if (string.IsNullOrEmpty(tfm)) return false;
        return _validTfms.Contains(tfm);
    }

    /// <summary>
    /// Checks if a target framework is .NET 8.0 or higher
    /// </summary>
    public bool IsNet8OrHigher([NotNullWhen(true)] string? tfm) {
        if (string.IsNullOrEmpty(tfm)) return false;

        return tfm.StartsWith("net8", DefaultComparison) ||
               tfm.StartsWith("net9", DefaultComparison) ||
               tfm.StartsWith("net10", DefaultComparison);
    }

    /// <summary>
    /// Gets the current target framework by detecting the runtime version
    /// </summary>
    public string GetCurrentTargetFramework() {
        var version = Environment.Version;
        return $"net{version.Major}.{version.Minor}";
    }

    /// <summary>
    /// Determines if a target framework is considered "current"
    /// </summary>
    public bool IsCurrentTfm(string? tfm) {
        if (string.IsNullOrEmpty(tfm)) return false;

        var currentTfm = GetCurrentTargetFramework();
        return string.Equals(tfm, currentTfm, DefaultComparison);
    }

    /// <summary>
    /// Filters target frameworks to only non-current ones
    /// </summary>
    public IEnumerable<string> FilterNonCurrentTfms(IEnumerable<string> targetFrameworks) {
        var currentTfm = GetCurrentTargetFramework();
        return targetFrameworks.Where(tfm => !string.Equals(tfm, currentTfm, DefaultComparison));
    }

    /// <summary>
    /// Checks if a project has Docker properties (for .NET 8+)
    /// </summary>
    public bool HasDockerProperties(Dictionary<string, string> properties) {
        var dockerPropertyNames = new[]
        {
            "ContainerBaseImage",
            "ContainerFamily",
            "ContainerRuntimeIdentifier",
            "ContainerRegistry",
            "ContainerRepository",
            "ContainerImageTag",
            "ContainerImageTags"
        };

        return dockerPropertyNames.Any(propName =>
            properties.TryGetValue(propName, out var val) && !string.IsNullOrWhiteSpace(val));
    }
}
