using bld.Models;

namespace bld.Infrastructure;

/// <summary>
/// Abstraction for MSBuild operations
/// </summary>
internal interface IMSBuildService {
    Task<ProjectProperties?> GetProjectPropertiesAsync(string projectPath, string? configuration = null, string? platform = null, params string[] properties);
}