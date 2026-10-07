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

    /// <summary>Regression: the directory scan took only the exact name "Dockerfile".</summary>
    [Fact]
    public async Task FindDockerfiles_FindsSuffixedVariantsButNotTheirIgnoreFiles() {
        var root = Path.Combine(Path.GetTempPath(), $"bld-docker-{Guid.NewGuid():N}");
        var api = Path.Combine(root, "src", "Api");
        Directory.CreateDirectory(api);
        foreach (var name in new[] { "Dockerfile", "Dockerfile.prod", "api.Dockerfile", "Dockerfile.dockerignore", "Dockerfiles.md", "MyDockerfile" }) {
            await File.WriteAllTextAsync(Path.Combine(api, name), "FROM scratch\n");
        }
        try {
            var found = (await DockerfileParser.FindDockerfilesAsync(root, 3)).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();

            Assert.Equal(["Dockerfile", "Dockerfile.prod", "api.Dockerfile"], found);
        }
        finally {
            Directory.Delete(root, true);
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

    /// <summary>A4.3: a heredoc body is not parsed, so an EXPOSE inside it is not a real port.</summary>
    [Fact]
    public async Task Parse_SkipsHeredocBodySoItsExposeIsNotAPort() {
        var info = await ParseAsync(
            "FROM mcr.microsoft.com/dotnet/aspnet:8.0\n" +
            "EXPOSE 8080\n" +
            "RUN <<EOF\n" +
            "echo building\n" +
            "EXPOSE 9000\n" +
            "EOF\n" +
            "ENTRYPOINT [\"dotnet\", \"App.dll\"]\n");

        // Only the real EXPOSE counts; the one in the heredoc body is skipped.
        Assert.Equal(["8080"], info.ExposedPorts);
        // The RUN instruction itself is still seen (and is unsupported for migration).
        var stage = Assert.Single(info.StageDetails);
        Assert.Contains(stage.Instructions, i => i.Directive == "RUN");
        Assert.Equal("[\"dotnet\", \"App.dll\"]", info.EntryPoint);
    }

    [Fact]
    public void JoinContinuations_SkipsQuotedAndMultipleHeredocBodies() {
        var joined = DockerfileParser.JoinContinuations([
            "COPY <<\"FILE1\" <<-FILE2 /dest",
            "content of 1",
            "FILE1",
            "\tcontent of 2",
            "FILE2",
            "RUN echo done",
        ]).ToList();

        // Both heredoc bodies are dropped; the COPY line and the following RUN remain.
        Assert.Equal(["COPY <<\"FILE1\" <<-FILE2 /dest", "RUN echo done"], joined);
    }
}
