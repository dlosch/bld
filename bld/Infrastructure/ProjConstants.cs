namespace bld.Infrastructure;

static class ProjConstants {

    /// <summary>
    /// Globs identifying source-of-truth files that must never sit inside a directory we delete.
    /// </summary>
    internal static readonly string[] ProjectAndSolutionGlobs = [
        "*.sln", "*.slnx", "*.slnf",
        "*.csproj", "*.fsproj", "*.vbproj", "*.sqlproj", "*.vcxproj",
    ];

    internal static string[] PropertyNames = [
        @"OutDir",
        @"BaseIntermediateOutputPath",
        @"BaseOutputPath",
        "ProjectName",

        @"TargetFramework",
        @"TargetFrameworks",
        //@"PublishTrimmed",
        //@"PublishAot",

        "UsingMicrosoftNETSdk",

        "IsPackable",

        "PackageOutputPath",
        "PackageId",
        "AssemblyName",

        // ContainerBaseImage
        // ContainerFamily
        // ContainerRuntimeIdentifier
        // ContainerRegistry
        // ContainerRepository
        // ContainerImageTag
        // ContainerImageTags

        //"ContainerBaseImage",
        //"ContainerFamily",
        //"ContainerRuntimeIdentifier",
        //"ContainerRegistry",
        //"ContainerRepository",
        //"ContainerImageTag",
        //"ContainerImageTags"
    ];
}
