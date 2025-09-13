using NuGet.Frameworks;

namespace bld.Infrastructure;

/// <summary>
/// Extensions and utilities for NuGetFramework to handle normalization issues
/// </summary>
internal static class NuGetFrameworkExtensions {
    /// <summary>
    /// Gets the short folder name for a NuGetFramework, with fixes for known issues like net100 -> net10.0
    /// </summary>
    /// <param name="framework">The framework to get the short name for</param>
    /// <returns>Properly formatted short folder name</returns>
    public static string GetNormalizedShortFolderName(this NuGetFramework framework) {
        if (framework == null) return string.Empty;
        
        var shortName = framework.GetShortFolderName();
        
        // Handle the specific case where net100 should be net10.0
        // This happens when .NET Core version is parsed as 10.0 (hypothetical future version)
        if (shortName == "net100" && framework.Framework == FrameworkConstants.FrameworkIdentifiers.NetCoreApp) {
            return "net10.0";
        }
        
        // Handle other similar cases that might arise
        if (shortName.StartsWith("net") && shortName.Length == 6 && char.IsDigit(shortName[3])) {
            // Pattern: net### where ### is a three-digit number that should be formatted as #.0
            if (int.TryParse(shortName.Substring(3), out var version) && version >= 100) {
                var major = version / 10;
                var minor = version % 10;
                if (minor == 0) {
                    return $"net{major}.0";
                }
            }
        }
        
        return shortName;
    }
}