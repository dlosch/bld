namespace bld.Models;

/// <summary>
/// Directory types for cleaning operations
/// </summary>
internal enum DirType {
    OutDir,
    BaseOutputPath,
    BaseIntermediateOutputPath,
    /// <summary>artifacts/bin/&lt;project&gt;/ — pivot subdirectories are the candidates.</summary>
    ArtifactsBin,
    /// <summary>artifacts/publish/&lt;project&gt;/ — pivot subdirectories are the candidates.</summary>
    ArtifactsPublish,
    PublishDir,
    PackageOutputPath,
    /// <summary>TestResults/ next to a project or its solution, where `dotnet test` writes .trx files and coverage.</summary>
    TestResults,
}

/// <summary>What a marked directory holds, as the interactive picker and its flags group it.</summary>
internal enum CleanCategory {
    Bin,
    Obj,
    Publish,
    Package,
    TestResults,
}

internal static class CleanCategories {
    internal static CleanCategory Of(DirType type) => type switch {
        DirType.BaseIntermediateOutputPath => CleanCategory.Obj,
        DirType.PublishDir or DirType.ArtifactsPublish => CleanCategory.Publish,
        DirType.PackageOutputPath => CleanCategory.Package,
        DirType.TestResults => CleanCategory.TestResults,
        _ => CleanCategory.Bin,
    };

    internal static string Label(CleanCategory category) => category switch {
        CleanCategory.Bin => "bin",
        CleanCategory.Obj => "obj",
        CleanCategory.Publish => "publish",
        CleanCategory.Package => "package",
        _ => "test",
    };
}

/// <summary>
/// Project types supported by the cleaner
/// </summary>
internal enum ProjectType {
    Unknown,
    Csproj,
    CsprojWeb,
    CsprojLegacy,
    Fsproj,
    Vbproj,
    Sqlproj,
    Vcxproj,
}

/// <summary>
/// Represents a directory with associated project information for cleaning
/// </summary>
internal record class Dir(
    List<(string Path, DirType Type)> AbsPath,
    Dictionary<string, string?> AbsProjPath,
    HashSet<string> Configs,
    HashSet<string> Tfms,
    HashSet<string> AbsParentPath) {
    internal bool IsProcessed = false;
    internal void SetProcessed() => IsProcessed = true;
    internal ProjectType ProjType => GetProjectType(AbsProjPath.FirstOrDefault().Key);

    private static ProjectType GetProjectType(string? projectFileAbsPath) {
        if (projectFileAbsPath == null) return ProjectType.Unknown;
        switch (Path.GetExtension(projectFileAbsPath).ToLowerInvariant()) {
            case ".csproj": return ProjectType.Csproj;
            case ".fsproj": return ProjectType.Fsproj;
            case ".vbproj": return ProjectType.Vbproj;
            case ".sqlproj": return ProjectType.Sqlproj;
            case ".vcxproj": return ProjectType.Vcxproj;
            default: return ProjectType.Unknown;
        }
    }

#if DEBUG
    public override string ToString() {
        return $"Dir: {string.Join(", ", AbsPath.Select(p => p.Path))}, Proj: {string.Join(", ", AbsProjPath.Select(p => p.Key))}, Configs: {string.Join(", ", Configs)}, Tfms: {string.Join(", ", Tfms)}";
    }
#endif
}

/// <summary>
/// Stats structure for tracking deletion operations
/// </summary>
internal record struct Stats(string Sln, string[]? Configurations = default, int TotalDirectories = 0, long TotalSize = 0L, int TotalFsiEntryCount = 0) {
    internal long TotalSizeMiB => TotalSize / (1024 * 1024);

    internal void Add(Stats stats) {
        TotalDirectories += stats.TotalDirectories;
        TotalSize += stats.TotalSize;
        TotalFsiEntryCount += stats.TotalFsiEntryCount;
    }
}