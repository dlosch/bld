using bld.Infrastructure;

namespace bld.Tests;

public class DockerfileParserTests : IAsyncLifetime {
    private readonly string _tempDir;

    public DockerfileParserTests() {
        _tempDir = Path.Combine(Path.GetTempPath(), "bld_test_" + Guid.NewGuid().ToString("N"));
    }

    public Task InitializeAsync() {
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
    public async Task ParseAsync_WithSimpleDockerfile_ParsesBaseImage() {
        // Arrange
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n");

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Single(info.BaseImages);
        Assert.Equal("mcr.microsoft.com/dotnet/aspnet:8.0", info.BaseImages[0]);
    }

    [Fact]
    public async Task ParseAsync_WithMultiStageDockerfile_ParsesAllStages() {
        // Arrange
        var dockerfile = @"
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /src/bin/Release/net8.0/publish .
ENTRYPOINT [""dotnet"", ""myapp.dll""]
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Equal(2, info.BaseImages.Count);
        Assert.Contains("mcr.microsoft.com/dotnet/sdk:8.0", info.BaseImages);
        Assert.Contains("mcr.microsoft.com/dotnet/aspnet:8.0", info.BaseImages);
        Assert.Equal(2, info.Stages.Count);
        Assert.Contains("build", info.Stages);
        Assert.Contains("runtime", info.Stages);
    }

    [Fact]
    public async Task ParseAsync_WithExposedPorts_ParsesPorts() {
        // Arrange
        var dockerfile = @"
FROM nginx:latest
EXPOSE 80
EXPOSE 443
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Equal(2, info.ExposedPorts.Count);
        Assert.Contains("80", info.ExposedPorts);
        Assert.Contains("443", info.ExposedPorts);
    }

    [Fact]
    public async Task ParseAsync_WithMultiplePortsOnOneLine_ParsesAllPorts() {
        // Arrange
        var dockerfile = @"
FROM nginx:latest
EXPOSE 80 443 8080
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Equal(3, info.ExposedPorts.Count);
        Assert.Contains("80", info.ExposedPorts);
        Assert.Contains("443", info.ExposedPorts);
        Assert.Contains("8080", info.ExposedPorts);
    }

    [Fact]
    public async Task ParseAsync_WithWorkdir_ParsesWorkdir() {
        // Arrange
        var dockerfile = @"
FROM node:18
WORKDIR /app
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Equal("/app", info.WorkDir);
    }

    [Fact]
    public async Task ParseAsync_WithEntrypoint_ParsesEntrypoint() {
        // Arrange
        var dockerfile = @"
FROM mcr.microsoft.com/dotnet/aspnet:8.0
ENTRYPOINT [""dotnet"", ""myapp.dll""]
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Equal("[\"dotnet\", \"myapp.dll\"]", info.EntryPoint);
    }

    [Fact]
    public async Task ParseAsync_WithCmd_ParsesCmd() {
        // Arrange
        var dockerfile = @"
FROM nginx:latest
CMD [""nginx"", ""-g"", ""daemon off;""]
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Equal("[\"nginx\", \"-g\", \"daemon off;\"]", info.Cmd);
    }

    [Fact]
    public async Task ParseAsync_WithComments_IgnoresComments() {
        // Arrange
        var dockerfile = @"
# This is a comment
FROM nginx:latest
# Another comment
EXPOSE 80
";
        var dockerfilePath = Path.Combine(_tempDir, "Dockerfile");
        await File.WriteAllTextAsync(dockerfilePath, dockerfile);

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Single(info.BaseImages);
        Assert.Single(info.ExposedPorts);
    }

    [Fact]
    public async Task ParseAsync_WithNonExistentFile_ReturnsEmptyInfo() {
        // Arrange
        var dockerfilePath = Path.Combine(_tempDir, "NonExistent", "Dockerfile");

        // Act
        var info = await DockerfileParser.ParseAsync(dockerfilePath);

        // Assert
        Assert.Empty(info.BaseImages);
        Assert.Empty(info.Stages);
        Assert.Empty(info.ExposedPorts);
    }

    [Fact]
    public async Task FindDockerfilesAsync_WithNestedDockerfiles_FindsAll() {
        // Arrange
        var subDir = Path.Combine(_tempDir, "subproject");
        Directory.CreateDirectory(subDir);
        
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Dockerfile"), "FROM alpine");
        await File.WriteAllTextAsync(Path.Combine(subDir, "Dockerfile"), "FROM nginx");

        // Act
        var dockerfiles = await DockerfileParser.FindDockerfilesAsync(_tempDir, 3);

        // Assert
        Assert.Equal(2, dockerfiles.Count);
    }

    [Fact]
    public async Task FindDockerfilesAsync_WithDepthLimit_RespectsLimit() {
        // Arrange
        var level1 = Path.Combine(_tempDir, "level1");
        var level2 = Path.Combine(level1, "level2");
        var level3 = Path.Combine(level2, "level3");
        Directory.CreateDirectory(level3);
        
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Dockerfile"), "FROM alpine");
        await File.WriteAllTextAsync(Path.Combine(level3, "Dockerfile"), "FROM nginx");

        // Act
        var dockerfiles = await DockerfileParser.FindDockerfilesAsync(_tempDir, 1);

        // Assert
        Assert.Single(dockerfiles); // Should only find the root Dockerfile
    }

    [Fact]
    public async Task FindDockerfilesAsync_SkipsBinAndObjDirectories() {
        // Arrange
        var binDir = Path.Combine(_tempDir, "bin");
        var objDir = Path.Combine(_tempDir, "obj");
        Directory.CreateDirectory(binDir);
        Directory.CreateDirectory(objDir);
        
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "Dockerfile"), "FROM alpine");
        await File.WriteAllTextAsync(Path.Combine(binDir, "Dockerfile"), "FROM nginx");
        await File.WriteAllTextAsync(Path.Combine(objDir, "Dockerfile"), "FROM node");

        // Act
        var dockerfiles = await DockerfileParser.FindDockerfilesAsync(_tempDir, 3);

        // Assert
        Assert.Single(dockerfiles); // Should only find the root Dockerfile
    }

    [Fact]
    public async Task FindDockerfilesAsync_WithNonExistentDirectory_ReturnsEmpty() {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir, "nonexistent");

        // Act
        var dockerfiles = await DockerfileParser.FindDockerfilesAsync(nonExistentPath, 3);

        // Assert
        Assert.Empty(dockerfiles);
    }
}
