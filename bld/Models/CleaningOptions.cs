namespace bld.Models;

internal record CleaningOptions {

    internal string Filter => "*.sln?";

    public bool UseVSInstance { get; init; } = false;

    public bool CleanOnlyNonCurrentTfms { get; init; } = false;
    public bool CleanObjDirectory { get; init; } = true;
    public bool KeepRestoreArtifacts { get; init; } = false;
    public bool Force { get; init; } = false;
    public LogLevel LogLevel { get; init; } = LogLevel.Warning;
    public int Depth { get; init; } = 4;
    public bool Delete { get; internal set; }


    internal Predicate<string> FileNameFilter { get; init; } = FilterSupportedSlnFileFormats;
    public string? OutputFile { get; internal set; }
    public string? VSToolsPath { get; internal set; }
    public string? VSRootPath { get; internal set; }
    public bool NoResolveVSToolsPath { get; internal set; } = false;
    public ConfirmLevel? ConfirmLevel { get; internal set; }

    public int MaxDegreeOfParallelism { get; init; } = Environment.ProcessorCount >> 1;
    public bool MarkdownOutput { get; init; } = false;

    private static bool FilterSupportedSlnFileFormats(string file) =>
        file.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".slnf", StringComparison.OrdinalIgnoreCase)
        || file.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
}

internal enum LogLevel {
    Debug,
    Verbose,
    Info,
    Warning,
    Error
}

internal enum ConfirmLevel {
    None, // none

    Sln,
    Project,
    Directory,
}