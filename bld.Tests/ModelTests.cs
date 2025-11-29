using bld.Models;

namespace bld.Tests;

public class ProjectPropertiesTests {
    [Fact]
    public void Indexer_ReturnsValueWhenKeyExists() {
        // Arrange
        var properties = new ProjectProperties {
            Properties = new Dictionary<string, string> {
                { "OutDir", "/bin/Debug/" },
                { "TargetFramework", "net8.0" }
            }
        };

        // Act & Assert
        Assert.Equal("/bin/Debug/", properties["OutDir"]);
        Assert.Equal("net8.0", properties["TargetFramework"]);
    }

    [Fact]
    public void Indexer_ReturnsNullWhenKeyDoesNotExist() {
        // Arrange
        var properties = new ProjectProperties {
            Properties = new Dictionary<string, string>()
        };

        // Act & Assert
        Assert.Null(properties["NonExistentKey"]);
    }

    [Fact]
    public void PropertyAccessors_ReturnCorrectValues() {
        // Arrange
        var properties = new ProjectProperties {
            Properties = new Dictionary<string, string> {
                { "OutDir", "/bin/Debug/net8.0/" },
                { "BaseOutputPath", "/bin/" },
                { "BaseIntermediateOutputPath", "/obj/" },
                { "PackageOutputPath", "/packages/" },
                { "AssemblyName", "MyAssembly" },
                { "PackageId", "MyPackage" },
                { "ProjectName", "MyProject" },
                { "TargetFramework", "net8.0" },
                { "TargetFrameworks", "net6.0;net7.0;net8.0" }
            }
        };

        // Act & Assert
        Assert.Equal("/bin/Debug/net8.0/", properties.OutDir);
        Assert.Equal("/bin/", properties.BaseOutputPath);
        Assert.Equal("/obj/", properties.BaseIntermediateOutputPath);
        Assert.Equal("/packages/", properties.PackageOutputPath);
        Assert.Equal("MyAssembly", properties.AssemblyName);
        Assert.Equal("MyPackage", properties.PackageId);
        Assert.Equal("MyProject", properties.ProjectName);
        Assert.Equal("net8.0", properties.TargetFramework);
        Assert.Equal("net6.0;net7.0;net8.0", properties.TargetFrameworks);
    }

    [Fact]
    public void Empty_ReturnsEmptyProperties() {
        // Act
        var empty = ProjectProperties.Empty;

        // Assert
        Assert.NotNull(empty);
        Assert.Empty(empty.Properties);
        Assert.Null(empty.OutDir);
        Assert.Null(empty.TargetFramework);
    }
}

public class ProjCfgTests {
    [Fact]
    public void Path_ReturnsProjectPath() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var projCfg = new ProjCfg(proj, "Debug");

        // Act & Assert
        Assert.Equal("/path/to/project.csproj", projCfg.Path);
    }

    [Fact]
    public void ProjDir_ReturnsProjectDirectory() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var projCfg = new ProjCfg(proj, "Debug");

        // Act & Assert
        Assert.Equal("/path/to", projCfg.ProjDir);
    }

    [Fact]
    public void ConfigurationOrDefault_ReturnsConfigurationWhenSet() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var projCfg = new ProjCfg(proj, "Debug");

        // Act & Assert
        Assert.Equal("Debug", projCfg.ConfigurationOrDefault);
    }

    [Fact]
    public void ConfigurationOrDefault_ReturnsReleaseWhenNull() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var projCfg = new ProjCfg(proj, null);

        // Act & Assert
        Assert.Equal("Release", projCfg.ConfigurationOrDefault);
    }
}

public class ProjCfgEqualityComparerTests {
    private readonly ProjCfgEqualityComparer _comparer = new();

    [Fact]
    public void Equals_ReturnsTrueForSamePathAndConfiguration() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj1 = new Proj("/path/to/project.csproj", sln);
        var proj2 = new Proj("/path/to/project.csproj", sln);
        var cfg1 = new ProjCfg(proj1, "Debug");
        var cfg2 = new ProjCfg(proj2, "Debug");

        // Act & Assert
        Assert.True(_comparer.Equals(cfg1, cfg2));
    }

    [Fact]
    public void Equals_ReturnsTrueForCaseInsensitiveConfiguration() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var cfg1 = new ProjCfg(proj, "Debug");
        var cfg2 = new ProjCfg(proj, "DEBUG");

        // Act & Assert
        Assert.True(_comparer.Equals(cfg1, cfg2));
    }

    [Fact]
    public void Equals_ReturnsFalseForDifferentPaths() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj1 = new Proj("/path/to/project1.csproj", sln);
        var proj2 = new Proj("/path/to/project2.csproj", sln);
        var cfg1 = new ProjCfg(proj1, "Debug");
        var cfg2 = new ProjCfg(proj2, "Debug");

        // Act & Assert
        Assert.False(_comparer.Equals(cfg1, cfg2));
    }

    [Fact]
    public void Equals_ReturnsFalseForDifferentConfigurations() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var cfg1 = new ProjCfg(proj, "Debug");
        var cfg2 = new ProjCfg(proj, "Release");

        // Act & Assert
        Assert.False(_comparer.Equals(cfg1, cfg2));
    }

    [Fact]
    public void Equals_ReturnsFalseForNullInputs() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var cfg = new ProjCfg(proj, "Debug");

        // Act & Assert
        Assert.False(_comparer.Equals(cfg, null));
        Assert.False(_comparer.Equals(null, cfg));
        Assert.False(_comparer.Equals(null, null));
    }

    [Fact]
    public void Equals_ReturnsTrueForSameReference() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);
        var cfg = new ProjCfg(proj, "Debug");

        // Act & Assert
        Assert.True(_comparer.Equals(cfg, cfg));
    }

    [Fact]
    public void GetHashCode_ReturnsSameHashForEqualObjects() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj1 = new Proj("/path/to/project.csproj", sln);
        var proj2 = new Proj("/path/to/project.csproj", sln);
        var cfg1 = new ProjCfg(proj1, "Debug");
        var cfg2 = new ProjCfg(proj2, "DEBUG"); // Different case

        // Act
        var hash1 = _comparer.GetHashCode(cfg1);
        var hash2 = _comparer.GetHashCode(cfg2);

        // Assert
        Assert.Equal(hash1, hash2);
    }
}

public class ProjTests {
    [Fact]
    public void Dir_ReturnsParentDirectory() {
        // Arrange
        var sln = new Sln("/path/to/solution.sln");
        var proj = new Proj("/path/to/project.csproj", sln);

        // Act & Assert
        Assert.Equal("/path/to", proj.Dir);
    }

    [Fact]
    public void Dir_ThrowsForRootPath() {
        // Arrange
        var sln = new Sln("/solution.sln");
        
        // Act & Assert - On Unix, root path "/" would be the parent, but that should work
        var proj = new Proj("/project.csproj", sln);
        Assert.Equal("/", proj.Dir);
    }
}

public class ProjectInfoTests {
    [Fact]
    public void DefaultValues_AreCorrect() {
        // Arrange & Act
        var info = new ProjectInfo();

        // Assert
        Assert.Equal(string.Empty, info.ProjectPath);
        Assert.Null(info.ProjectName);
        Assert.Null(info.AssemblyName);
        Assert.Null(info.TargetFramework);
        Assert.Empty(info.TargetFrameworks);
        Assert.Null(info.Configuration);
        Assert.Null(info.Platform);
        Assert.Null(info.IntermediateOutputPath);
        Assert.Null(info.PackageOutputPath);
        Assert.Null(info.PackageId);
        Assert.Empty(info.Properties);
        Assert.False(info.HasDockerProperties);
        Assert.Null(info.OutDir);
        Assert.Null(info.BaseOutputPath);
    }

    [Fact]
    public void WithInitializer_SetsValues() {
        // Arrange & Act
        var info = new ProjectInfo {
            ProjectPath = "/path/to/project.csproj",
            ProjectName = "MyProject",
            TargetFramework = "net8.0",
            TargetFrameworks = new[] { "net7.0", "net8.0" },
            Configuration = "Debug"
        };

        // Assert
        Assert.Equal("/path/to/project.csproj", info.ProjectPath);
        Assert.Equal("MyProject", info.ProjectName);
        Assert.Equal("net8.0", info.TargetFramework);
        Assert.Equal(2, info.TargetFrameworks.Count);
        Assert.Equal("Debug", info.Configuration);
    }
}
