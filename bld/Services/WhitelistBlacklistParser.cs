using System.Text.RegularExpressions;

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

        var whitelist = new List<string>();
        var blacklist = new List<string>();
        var microsoft = new List<string>();
        var trusted = new List<string>();
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
                
                // Add to appropriate list based on current section
                switch (currentSection) {
                    case Section.Whitelist:
                        whitelist.Add(trimmedLine);
                        break;
                    case Section.Blacklist:
                        blacklist.Add(trimmedLine);
                        break;
                    case Section.Microsoft:
                        microsoft.Add(trimmedLine);
                        break;
                    case Section.Trusted:
                        trusted.Add(trimmedLine);
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
    /// Check if a package name matches any pattern in the list
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
    public IReadOnlyList<string> WhitelistPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BlacklistPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> MicrosoftPatterns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> TrustedPatterns { get; init; } = Array.Empty<string>();
}