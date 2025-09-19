namespace bld.Infrastructure;

static class ProjConstants {

    internal static string[] PropertyNames = [
        @"OutDir",
        @"BaseIntermediateOutputPath",
        @"BaseOutputPath",
        "ProjectName",

        @"TargetFramework",
        @"TargetFrameworks",
        //@"PublishTrimmed",
        //@"PublishAot",

        "UsingMicrosoftNETSdk", // true or empty

        // todo
        "IsPackable",

        "PackageOutputPath",
        "PackageId",
        "AssemblyName",
        
        // Additional properties for slnx project type detection
        "OutputType",
        "Sdk",
        "UseWPF",
        "UseWindowsForms",

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