namespace bld.Models;

/// <summary>
/// Information about a NuGet package reference
/// </summary>
internal record NugetPackageInfo {
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public NugetPackageCategory Category { get; init; }
    public long? DownloadCount { get; init; }
    public string? ProjectPath { get; init; }
}

/// <summary>
/// Categories for NuGet packages
/// </summary>
internal enum NugetPackageCategory {
    MicrosoftOfficial,      // Official .NET packages (System.*, Microsoft.Extensions.*, etc.)
    MicrosoftNonOfficial,   // Microsoft packages that are not official .NET
    TrustedThirdParty,      // Known trusted packages (high download count or whitelisted)
    Other                   // Everything else
}

/// <summary>
/// Analysis results for a single project
/// </summary>
internal record ProjectNugetAnalysis {
    public string ProjectPath { get; init; } = string.Empty;
    public string? ProjectName { get; init; }
    public IReadOnlyList<NugetPackageInfo> Packages { get; init; } = Array.Empty<NugetPackageInfo>();
    
    public IEnumerable<NugetPackageInfo> MicrosoftOfficialPackages => 
        Packages.Where(p => p.Category == NugetPackageCategory.MicrosoftOfficial);
    
    public IEnumerable<NugetPackageInfo> MicrosoftNonOfficialPackages => 
        Packages.Where(p => p.Category == NugetPackageCategory.MicrosoftNonOfficial);
    
    public IEnumerable<NugetPackageInfo> TrustedThirdPartyPackages => 
        Packages.Where(p => p.Category == NugetPackageCategory.TrustedThirdParty);
    
    public IEnumerable<NugetPackageInfo> OtherPackages => 
        Packages.Where(p => p.Category == NugetPackageCategory.Other);
}