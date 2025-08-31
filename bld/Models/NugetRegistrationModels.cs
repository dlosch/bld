using System.Text.Json;
using System.Text.Json.Serialization;

namespace bld.Models;

/// <summary>
/// Strong-typed models for a NuGet V3 registration index (registration5-semver1) document
/// </summary>
internal record class NugetRegistrationIndex {
    [JsonPropertyName("@id")] public string Self { get; init; } = string.Empty;
    [JsonPropertyName("@type")] public JsonElement Type { get; init; }
    public string CommitId { get; init; } = string.Empty;
    public DateTimeOffset CommitTimeStamp { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<NugetRegistrationPage> Items { get; init; } = Array.Empty<NugetRegistrationPage>();

    // JSON-LD context is not typically needed for consumers; keep as JsonElement to be flexible
    [JsonPropertyName("@context")] public JsonElement Context { get; init; }
}

/// <summary>
/// A registration catalog page (contains a range of versions and package items)
/// </summary>
internal record class NugetRegistrationPage {
    [JsonPropertyName("@id")] public string Self { get; init; } = string.Empty;
    [JsonPropertyName("@type")] public JsonElement Type { get; init; }
    public string CommitId { get; init; } = string.Empty;
    public DateTimeOffset CommitTimeStamp { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<NugetPackageItem> Items { get; init; } = Array.Empty<NugetPackageItem>();
    public string Parent { get; init; } = string.Empty;
    public string Lower { get; init; } = string.Empty;
    public string Upper { get; init; } = string.Empty;
}

/// <summary>
/// A single package entry on a registration page.
/// </summary>
internal record class NugetPackageItem {
    [JsonPropertyName("@id")] public string Self { get; init; } = string.Empty;
    [JsonPropertyName("@type")] public string Type { get; init; } = string.Empty; // typically "Package"
    public string CommitId { get; init; } = string.Empty;
    public DateTimeOffset CommitTimeStamp { get; init; }
    public NugetCatalogEntry CatalogEntry { get; init; } = new();
    public string PackageContent { get; init; } = string.Empty;
    public string Registration { get; init; } = string.Empty;
}

/// <summary>
/// The catalog entry for a specific package version.
/// </summary>
internal record class NugetCatalogEntry {
    [JsonPropertyName("@id")] public string Self { get; init; } = string.Empty;
    [JsonPropertyName("@type")] public string Type { get; init; } = string.Empty; // "PackageDetails"

    public string Authors { get; init; } = string.Empty;
    public IReadOnlyList<NugetPackageDependencyGroup> DependencyGroups { get; init; } = Array.Empty<NugetPackageDependencyGroup>();
    public string Description { get; init; } = string.Empty;
    public string IconUrl { get; init; } = string.Empty;
    [JsonPropertyName("id")] public string PackageId { get; init; } = string.Empty;
    public string Language { get; init; } = string.Empty;
    public string LicenseExpression { get; init; } = string.Empty;
    public string LicenseUrl { get; init; } = string.Empty;
    public string ReadmeUrl { get; init; } = string.Empty;
    public bool Listed { get; init; }
    public string MinClientVersion { get; init; } = string.Empty;
    public string PackageContent { get; init; } = string.Empty;
    public string ProjectUrl { get; init; } = string.Empty;
    public DateTimeOffset Published { get; init; }
    public bool RequireLicenseAcceptance { get; init; }
    public string Summary { get; init; } = string.Empty;
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public string Title { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;

    // Some documents contain additional fields we don't model; capture them losslessly if needed.
    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; init; } = new();
}

/// <summary>
/// Represents dependency groups for target frameworks.
/// </summary>
internal record class NugetPackageDependencyGroup {
    [JsonPropertyName("@id")] public string Self { get; init; } = string.Empty;
    [JsonPropertyName("@type")] public string Type { get; init; } = string.Empty; // "PackageDependencyGroup"
    public IReadOnlyList<NugetPackageDependency> Dependencies { get; init; } = Array.Empty<NugetPackageDependency>();
    public string TargetFramework { get; init; } = string.Empty;
}

/// <summary>
/// A single dependency entry inside a dependency group.
/// </summary>
internal record class NugetPackageDependency {
    [JsonPropertyName("@id")] public string Self { get; init; } = string.Empty;
    [JsonPropertyName("@type")] public string Type { get; init; } = string.Empty; // "PackageDependency"
    [JsonPropertyName("id")] public string PackageId { get; init; } = string.Empty;
    public string Range { get; init; } = string.Empty;
    public string Registration { get; init; } = string.Empty;
}

/// <summary>
/// System.Text.Json source-generation context for the registration index.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(NugetRegistrationIndex))]
internal partial class BldJsonContext : JsonSerializerContext { }
