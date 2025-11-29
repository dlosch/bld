using bld.Infrastructure;

namespace bld.Tests;

public class DirExtTests {
    [Theory]
    [InlineData("/home/user/project/src", "/home/user/project", true)]
    [InlineData("/home/user/project/deep/nested/path", "/home/user/project", true)]
    [InlineData("/home/user/project", "/home/user/project", false)]  // Same path
    [InlineData("/home/user/other", "/home/user/project", false)]
    [InlineData(".", "/home/user/project", false)]
    [InlineData("..", "/home/user/project", false)]
    [InlineData("", "/home/user/project", false)]
    public void IsNestedBelow_ReturnsExpectedResult(string target, string baseDir, bool expected) {
        // Act
        var result = DirExt.IsNestedBelow(target, baseDir);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("relative/path", "/base", true)]
    [InlineData("/absolute/path", "/base", true)]
    [InlineData(".", "/base", true)]
    [InlineData("..", "/base", true)]
    [InlineData(null, "/base", false)]
    [InlineData("", "/base", false)]
    public void NormalizePath_ValidatesCorrectly(string? candidate, string baseDir, bool shouldBeValid) {
        // Act
        var result = DirExt.NormalizePath(candidate, baseDir, out var normalized);

        // Assert
        Assert.Equal(shouldBeValid, result);
        if (shouldBeValid) {
            Assert.NotNull(normalized);
        }
    }

    [Fact]
    public void EnsureRooted_RelativePathBecomesRooted() {
        // Arrange
        var relative = "relative/path";
        var baseDir = "/base";

        // Act
        var result = DirExt.EnsureRooted(relative, baseDir);

        // Assert
        Assert.True(Path.IsPathFullyQualified(result));
        Assert.Contains("relative", result);
        Assert.Contains("path", result);
    }

    [Fact]
    public void EnsureRooted_AbsolutePathRemainsAbsolute() {
        // Arrange
        var absolute = "/absolute/path";
        var baseDir = "/base";

        // Act
        var result = DirExt.EnsureRooted(absolute, baseDir);

        // Assert
        Assert.True(Path.IsPathFullyQualified(result));
    }

    [Fact]
    public void EnsureRooted_ThrowsForDevicePaths() {
        // This test only applies on Windows
        if (!OperatingSystem.IsWindows()) {
            return; // Skip on non-Windows platforms
        }

        // Act & Assert
        Assert.Throws<ArgumentException>(() => DirExt.EnsureRooted(@"\\.\COM1", @"C:\base"));
    }

    [Fact]
    public void Exists_ChecksDirectoryExistence() {
        // Arrange
        var tempDir = Path.Combine(Path.GetTempPath(), "bld_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try {
            // Act & Assert
            Assert.True(DirExt.Exists(tempDir));
            Assert.True(DirExt.Exists(tempDir + Path.DirectorySeparatorChar));
            Assert.False(DirExt.Exists(Path.Combine(tempDir, "nonexistent")));
        }
        finally {
            Directory.Delete(tempDir, true);
        }
    }
}

public class DirectoryInfoExtensionsTests : IAsyncLifetime {
    private string _tempDir = null!;

    public Task InitializeAsync() {
        _tempDir = Path.Combine(Path.GetTempPath(), "bld_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() {
        if (Directory.Exists(_tempDir)) {
            Directory.Delete(_tempDir, true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public void IsEmpty_ReturnsTrueForEmptyDirectory() {
        // Arrange
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.True(dirInfo.IsEmpty());
        Assert.False(dirInfo.IsNotEmpty());
    }

    [Fact]
    public void IsEmpty_ReturnsFalseWhenContainsFiles() {
        // Arrange
        File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "test");
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.False(dirInfo.IsEmpty());
        Assert.True(dirInfo.IsNotEmpty());
    }

    [Fact]
    public void IsEmpty_ReturnsFalseWhenContainsSubdirectories() {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "subdir"));
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.False(dirInfo.IsEmpty());
        Assert.True(dirInfo.IsNotEmpty());
    }

    [Fact]
    public void HasSubDir_FindsSubdirectory() {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.True(dirInfo.HasSubDir("obj"));
    }

    [Fact]
    public void HasSubDir_ReturnsFalseWhenNotFound() {
        // Arrange
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.False(dirInfo.HasSubDir("obj"));
    }

    [Fact]
    public void OnlyHasSubDirsOrSubset_ReturnsTrueForExactMatch() {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "obj"));
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.True(dirInfo.OnlyHasSubDirsOrSubset(true, "bin", "obj"));
    }

    [Fact]
    public void OnlyHasSubDirsOrSubset_ReturnsFalseWhenHasFiles() {
        // Arrange
        Directory.CreateDirectory(Path.Combine(_tempDir, "bin"));
        File.WriteAllText(Path.Combine(_tempDir, "test.txt"), "test");
        var dirInfo = new DirectoryInfo(_tempDir);

        // Act & Assert
        Assert.False(dirInfo.OnlyHasSubDirsOrSubset(true, "bin"));
    }
}
