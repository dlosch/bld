using bld.Models;

namespace bld.Services;

internal record class DirResult(DirectoryInfo Directory, IReadOnlyList<Dir> References, CleanCategory Category = CleanCategory.Bin);

/// <summary>
/// One file the run would delete. Only package output is cleaned per file: the directory it lands
/// in (a local feed, artifacts/package/) holds other projects' packages, so the directory is never
/// a candidate, only this project's own .nupkg/.snupkg files in it.
/// </summary>
internal record class FileResult(FileInfo File, IReadOnlyList<Dir> References, CleanCategory Category);

internal record class MarkDeleteResult(List<DirResult> Directories) {
    public List<FileResult> Files { get; init; } = new();
    public bool IsEmpty => Directories.Count == 0 && Files.Count == 0;
}

internal interface IMarkDeleteResultProcessor {
    Task ProcessAsync(MarkDeleteResult result);
}
