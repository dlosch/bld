using bld.Models;

namespace bld.Services;

/// <summary>
/// Service for categorizing NuGet packages
/// </summary>
internal class NugetPackageCategorizer {
    
    // Official Microsoft .NET packages - these are part of the core .NET ecosystem
    private static readonly HashSet<string> OfficialDotNetPrefixes = new(StringComparer.OrdinalIgnoreCase) {
        "System.",
        "Microsoft.Extensions.",
        "Microsoft.AspNetCore.",
        "Microsoft.EntityFrameworkCore.",
        "Microsoft.Data.SqlClient",
        "Microsoft.Extensions.Configuration",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging",
        "Microsoft.AspNetCore.Authentication",
        "Microsoft.AspNetCore.Authorization",
        "Microsoft.Extensions.Http",
        "Microsoft.Extensions.Options"
    };

    // Exact matches for official .NET packages
    private static readonly HashSet<string> OfficialDotNetExact = new(StringComparer.OrdinalIgnoreCase) {
        "Microsoft.NETCore.App",
        "Microsoft.WindowsDesktop.App",
        "Microsoft.AspNetCore.App",
        "NETStandard.Library"
    };

    // High-trust third-party packages (popular, well-maintained packages)
    // This is a basic starter list - in a real implementation, this could be configurable
    private static readonly HashSet<string> TrustedThirdPartyPackages = new(StringComparer.OrdinalIgnoreCase) {
        "Newtonsoft.Json",
        "AutoMapper",
        "Serilog",
        "FluentValidation",
        "Swashbuckle.AspNetCore",
        "xunit",
        "NUnit",
        "MSTest.TestFramework",
        "Moq",
        "AutoFixture",
        "FluentAssertions",
        "Polly",
        "MediatR",
        "Dapper",
        "StackExchange.Redis",
        "Npgsql",
        "MongoDB.Driver",
        "IdentityModel",
        "CsvHelper",
        "EPPlus",
        "ImageSharp",
        "MailKit",
        "RestSharp",
        "HttpClientFactory",
        "Scrutor"
    };

    // Download count threshold for considering a package "trusted"
    private const long TrustedDownloadThreshold = 10_000_000; // 10 million downloads

    public NugetPackageCategory CategorizePackage(string packageName, long? downloadCount = null) {
        if (string.IsNullOrWhiteSpace(packageName)) {
            return NugetPackageCategory.Other;
        }

        // Check for exact matches of official .NET packages
        if (OfficialDotNetExact.Contains(packageName)) {
            return NugetPackageCategory.MicrosoftOfficial;
        }

        // Check for official .NET package prefixes
        if (OfficialDotNetPrefixes.Any(prefix => packageName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) {
            return NugetPackageCategory.MicrosoftOfficial;
        }

        // Check for other Microsoft packages
        if (packageName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)) {
            return NugetPackageCategory.MicrosoftNonOfficial;
        }

        // Check for known trusted packages
        if (TrustedThirdPartyPackages.Contains(packageName)) {
            return NugetPackageCategory.TrustedThirdParty;
        }

        // Check download count threshold if available
        if (downloadCount.HasValue && downloadCount.Value >= TrustedDownloadThreshold) {
            return NugetPackageCategory.TrustedThirdParty;
        }

        return NugetPackageCategory.Other;
    }

    public string GetCategoryDisplayName(NugetPackageCategory category) => category switch {
        NugetPackageCategory.MicrosoftOfficial => "Microsoft Official .NET Packages",
        NugetPackageCategory.MicrosoftNonOfficial => "Microsoft Non-Official Packages", 
        NugetPackageCategory.TrustedThirdParty => "Known Trusted Packages",
        NugetPackageCategory.Other => "Other Packages",
        _ => "Unknown"
    };
}