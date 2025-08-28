using System.Text.RegularExpressions;
using bld.Models;

namespace bld.Services;

/// <summary>
/// Service for parsing whitelist and blacklist files for NuGet packages
/// </summary>
internal class WhitelistBlacklistParser {
    
    /// <summary>
    /// Parse a whitelist/blacklist file
    /// </summary>
    /// <param name="filePath">Path to the file</param>
    /// <returns>Parsed whitelist and blacklist rules</returns>
    public static WhitelistBlacklistRules ParseFile(string filePath) {
        if (!File.Exists(filePath)) {
            return new WhitelistBlacklistRules();
        }

        var whitelist = new List<PackagePattern>();
        var blacklist = new List<PackagePattern>();
        var microsoft = new List<PackagePattern>();
        var trusted = new List<PackagePattern>();
        var currentSection = Section.None;

        try {
            var lines = File.ReadAllLines(filePath);
            
            foreach (var line in lines) {
                var trimmedLine = line.Trim();
                
                // Skip empty lines
                if (string.IsNullOrWhiteSpace(trimmedLine)) {
                    continue;
                }
                
                // Check for section headers
                if (trimmedLine.Equals("# whitelist", StringComparison.OrdinalIgnoreCase)) {
                    currentSection = Section.Whitelist;
                    continue;
                }
                
                if (trimmedLine.Equals("# blacklist", StringComparison.OrdinalIgnoreCase)) {
                    currentSection = Section.Blacklist;
                    continue;
                }
                
                if (trimmedLine.Equals("# microsoft", StringComparison.OrdinalIgnoreCase)) {
                    currentSection = Section.Microsoft;
                    continue;
                }
                
                if (trimmedLine.Equals("# trusted", StringComparison.OrdinalIgnoreCase)) {
                    currentSection = Section.Trusted;
                    continue;
                }
                
                // Skip comments (lines starting with #)
                if (trimmedLine.StartsWith('#')) {
                    continue;
                }
                
                // Parse the pattern (which may include version constraint)
                var pattern = ParsePattern(trimmedLine);
                
                // Add to appropriate list based on current section
                switch (currentSection) {
                    case Section.Whitelist:
                        whitelist.Add(pattern);
                        break;
                    case Section.Blacklist:
                        blacklist.Add(pattern);
                        break;
                    case Section.Microsoft:
                        microsoft.Add(pattern);
                        break;
                    case Section.Trusted:
                        trusted.Add(pattern);
                        break;
                }
            }
        }
        catch (Exception ex) {
            throw new InvalidOperationException($"Failed to parse whitelist/blacklist file '{filePath}': {ex.Message}", ex);
        }

        return new WhitelistBlacklistRules {
            WhitelistPatterns = whitelist,
            BlacklistPatterns = blacklist,
            MicrosoftPatterns = microsoft,
            TrustedPatterns = trusted
        };
    }
    
    /// <summary>
    /// Parse a pattern string that may include version constraint
    /// Format: PackageName or PackageName,>=9.0.8
    /// </summary>
    /// <param name="patternString">The pattern string to parse</param>
    /// <returns>Parsed PackagePattern</returns>
    private static PackagePattern ParsePattern(string patternString) {
        if (string.IsNullOrWhiteSpace(patternString)) {
            return new PackagePattern { OriginalPattern = patternString };
        }

        var parts = patternString.Split(',', 2, StringSplitOptions.TrimEntries);
        var packageName = parts[0];
        
        if (parts.Length == 1) {
            // No version constraint
            return new PackagePattern { 
                Name = packageName,
                OriginalPattern = patternString
            };
        }

        // Parse version constraint
        var versionConstraintString = parts[1];
        var versionConstraint = ParseVersionConstraint(versionConstraintString);
        
        return new PackagePattern {
            Name = packageName,
            VersionConstraint = versionConstraint,
            OriginalPattern = patternString
        };
    }
    
    /// <summary>
    /// Parse a version constraint string like ">=9.0.8", "=1.2.3", "<=2.0.0"
    /// </summary>
    /// <param name="constraintString">The constraint string to parse</param>
    /// <returns>Parsed VersionConstraint</returns>
    private static VersionConstraint? ParseVersionConstraint(string constraintString) {
        if (string.IsNullOrWhiteSpace(constraintString)) {
            return null;
        }

        constraintString = constraintString.Trim();
        
        VersionOperator versionOperator;
        string versionString;
        
        if (constraintString.StartsWith(">=")) {
            versionOperator = VersionOperator.GreaterThanOrEqual;
            versionString = constraintString.Substring(2).Trim();
        }
        else if (constraintString.StartsWith("<=")) {
            versionOperator = VersionOperator.LessThanOrEqual;
            versionString = constraintString.Substring(2).Trim();
        }
        else if (constraintString.StartsWith("=")) {
            versionOperator = VersionOperator.Equal;
            versionString = constraintString.Substring(1).Trim();
        }
        else {
            // If no operator specified, treat as exact match
            versionOperator = VersionOperator.Equal;
            versionString = constraintString;
        }

        // Parse the version, handling pre-release versions by extracting only the version part before any suffix
        var version = ParseVersionWithPreRelease(versionString);
        if (version != null) {
            return new VersionConstraint {
                Operator = versionOperator,
                Version = version
            };
        }

        throw new InvalidOperationException($"Invalid version constraint: '{constraintString}'. Expected format: '>=9.0.8', '=1.2.3', or '<=2.0.0'");
    }
    
    /// <summary>
    /// Parse a version string that may contain pre-release suffixes
    /// </summary>
    /// <param name="versionString">Version string to parse</param>
    /// <returns>Parsed Version or null if invalid</returns>
    private static Version? ParseVersionWithPreRelease(string versionString) {
        if (string.IsNullOrWhiteSpace(versionString)) {
            return null;
        }

        // Extract the version part before any pre-release suffix (e.g., "2.0.0-beta7" -> "2.0.0")
        var versionPart = versionString.Split('-', 2)[0].Trim();
        
        if (Version.TryParse(versionPart, out var version)) {
            return version;
        }

        return null;
    }
    
    /// <summary>
    /// Check if a package name and version matches any pattern in the list
    /// </summary>
    /// <param name="packageName">Package name to check</param>
    /// <param name="packageVersion">Package version to check (optional)</param>
    /// <param name="patterns">List of patterns (supports wildcards and version constraints)</param>
    /// <returns>The matching pattern, or null if no match</returns>
    public static PackagePattern? FindMatchingPattern(string packageName, string? packageVersion, IEnumerable<PackagePattern> patterns) {
        if (string.IsNullOrWhiteSpace(packageName)) {
            return null;
        }
        
        foreach (var pattern in patterns) {
            if (IsMatch(packageName, packageVersion, pattern)) {
                return pattern;
            }
        }
        
        return null;
    }
    
    /// <summary>
    /// Check if a package name matches any pattern in the list (backward compatibility)
    /// </summary>
    /// <param name="packageName">Package name to check</param>
    /// <param name="patterns">List of patterns (supports wildcards)</param>
    /// <returns>The matching pattern, or null if no match</returns>
    public static string? FindMatchingPattern(string packageName, IEnumerable<string> patterns) {
        if (string.IsNullOrWhiteSpace(packageName)) {
            return null;
        }
        
        foreach (var pattern in patterns) {
            if (IsMatch(packageName, pattern)) {
                return pattern;
            }
        }
        
        return null;
    }
    
    
    /// <summary>
    /// Check if a package name and version matches a pattern (supports wildcards and version constraints)
    /// </summary>
    /// <param name="packageName">Package name to check</param>
    /// <param name="packageVersion">Package version to check (optional)</param>
    /// <param name="pattern">Pattern to match against</param>
    /// <returns>True if the package matches the pattern</returns>
    private static bool IsMatch(string packageName, string? packageVersion, PackagePattern pattern) {
        if (string.IsNullOrWhiteSpace(pattern.Name)) {
            return false;
        }
        
        // First check if package name matches
        if (!IsMatch(packageName, pattern.Name)) {
            return false;
        }
        
        // If no version constraint, name match is sufficient
        if (pattern.VersionConstraint == null) {
            return true;
        }
        
        // If version constraint exists but no package version provided, no match
        if (string.IsNullOrWhiteSpace(packageVersion)) {
            return false;
        }
        
        // Check version constraint
        var version = ParseVersionWithPreRelease(packageVersion);
        if (version != null) {
            return pattern.VersionConstraint.IsSatisfiedBy(version);
        }
        
        return false;
    }
    
    /// <summary>
    /// Check if a package name matches a pattern (supports wildcards)
    /// </summary>
    /// <param name="packageName">Package name to check</param>
    /// <param name="pattern">Pattern to match against (supports *)</param>
    /// <returns>True if the package matches the pattern</returns>
    private static bool IsMatch(string packageName, string pattern) {
        if (string.IsNullOrWhiteSpace(pattern)) {
            return false;
        }
        
        // If no wildcards, do exact comparison
        if (!pattern.Contains('*')) {
            return packageName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
        
        // Convert wildcard pattern to regex
        var regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(packageName, regexPattern, RegexOptions.IgnoreCase);
    }
    
    private enum Section {
        None,
        Whitelist,
        Blacklist,
        Microsoft,
        Trusted
    }
}

/// <summary>
/// Contains the parsed whitelist and blacklist rules
/// </summary>
internal record WhitelistBlacklistRules {
    public IReadOnlyList<PackagePattern> WhitelistPatterns { get; init; } = Array.Empty<PackagePattern>();
    public IReadOnlyList<PackagePattern> BlacklistPatterns { get; init; } = Array.Empty<PackagePattern>();
    public IReadOnlyList<PackagePattern> MicrosoftPatterns { get; init; } = Array.Empty<PackagePattern>();
    public IReadOnlyList<PackagePattern> TrustedPatterns { get; init; } = Array.Empty<PackagePattern>();
}