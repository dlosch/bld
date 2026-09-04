using bld.Infrastructure;

namespace bld.Tests;

public class DockerfileParserTests {

    private static async Task<DockerfileParser.DockerfileInfo> ParseAsync(string content) {
        var path = Path.Combine(Path.GetTempPath(), $"bld-docker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, "Dockerfile");
        await File.WriteAllTextAsync(file, content);
        try {
            return await DockerfileParser.ParseAsync(file);
        }
        finally {
            Directory.Delete(path, true);
        }
    }

    [Fact]
    public async Task Parse_IgnoresFromFlagsAndKeepsStageName() {
        // Multi-arch Dockerfiles are the common case; --platform was reported as the base image
        // and the stage name was lost entirely.
        var info = await ParseAsync(
            "FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build\n" +
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final\n");

        Assert.Equal(["mcr.microsoft.com/dotnet/sdk:8.0", "mcr.microsoft.com/dotnet/aspnet:8.0"], info.BaseImages);
        Assert.Equal(["build", "final"], info.Stages);
    }

    [Fact]
    public async Task Parse_JoinsContinuationLines() {
        var info = await ParseAsync(
            "FROM alpine\n" +
            "ENTRYPOINT [\"dotnet\", \\\n" +
            "  \"app.dll\"]\n");

        Assert.Equal("[\"dotnet\", \"app.dll\"]", info.EntryPoint);
    }

    [Fact]
    public async Task Parse_HandlesTabSeparatedDirectives() {
        var info = await ParseAsync("FROM\tubuntu:22.04\nEXPOSE\t8080 9090\n");

        Assert.Equal(["ubuntu:22.04"], info.BaseImages);
        Assert.Equal(["8080", "9090"], info.ExposedPorts);
    }

    [Fact]
    public async Task Parse_SkipsCommentsIncludingInsideContinuations() {
        var info = await ParseAsync(
            "# leading comment\n" +
            "FROM alpine\n" +
            "WORKDIR /app\n");

        Assert.Equal(["alpine"], info.BaseImages);
        Assert.Equal("/app", info.WorkDir);
    }

    [Fact]
    public void JoinContinuations_FoldsTrailingBackslashes() {
        var joined = DockerfileParser.JoinContinuations(["RUN a \\", "  b \\", "  c", "CMD [\"x\"]"]).ToList();

        Assert.Equal(["RUN a b c", "CMD [\"x\"]"], joined);
    }
}
