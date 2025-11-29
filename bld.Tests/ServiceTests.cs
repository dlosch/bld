using bld.Services;

namespace bld.Tests;

public class NetUtilTests {
    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net9.0", true)]
    [InlineData("net10.0", true)]
    [InlineData("NET8.0", true)]  // Case insensitive
    [InlineData("net7.0", false)]
    [InlineData("net6.0", false)]
    [InlineData("netcoreapp3.1", false)]
    [InlineData("netstandard2.0", false)]
    [InlineData("net48", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsNet8OrHigher_ReturnsExpectedResult(string? tfm, bool expected) {
        // Act
        var result = NetUtil.IsNet8OrHigher(tfm);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net9.0", true)]
    [InlineData("net10.0", true)]
    [InlineData("netstandard2.0", true)]
    [InlineData("netcoreapp3.1", true)]
    [InlineData("net48", true)]
    [InlineData("net472", true)]
    [InlineData("invalid", false)]
    [InlineData("net99.0", false)]
    public void IsTfmName_ReturnsExpectedResult(string name, bool expected) {
        // Act
        var result = NetUtil.Instance.IsTfmName(name, StringComparison.OrdinalIgnoreCase);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("NET8.0", StringComparison.OrdinalIgnoreCase, true)]
    [InlineData("NET8.0", StringComparison.Ordinal, false)]
    [InlineData("net8.0", StringComparison.Ordinal, true)]
    public void IsTfmName_RespectsComparison(string name, StringComparison comparison, bool expected) {
        // Act
        var result = NetUtil.Instance.IsTfmName(name, comparison);

        // Assert
        Assert.Equal(expected, result);
    }
}

public class TargetFrameworkValidatorTests {
    private readonly TargetFrameworkValidator _validator = new();

    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net9.0", true)]
    [InlineData("netstandard2.0", true)]
    [InlineData("netcoreapp3.1", true)]
    [InlineData("net48", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("invalid", false)]
    [InlineData("net99.0", false)]
    public void IsValidTfm_ReturnsExpectedResult(string? tfm, bool expected) {
        // Act
        var result = _validator.IsValidTfm(tfm);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net9.0", true)]
    [InlineData("net10.0", true)]
    [InlineData("net8.0-windows", true)]  // Starts with net8
    [InlineData("net7.0", false)]
    [InlineData("net6.0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsNet8OrHigher_ReturnsExpectedResult(string? tfm, bool expected) {
        // Act
        var result = _validator.IsNet8OrHigher(tfm);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void GetCurrentTargetFramework_ReturnsNet8() {
        // Act
        var result = _validator.GetCurrentTargetFramework();

        // Assert
        Assert.Equal("net8.0", result);
    }

    [Theory]
    [InlineData("net8.0", true)]
    [InlineData("net7.0", false)]
    [InlineData("net9.0", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsCurrentTfm_ReturnsExpectedResult(string? tfm, bool expected) {
        // Act
        var result = _validator.IsCurrentTfm(tfm);

        // Assert
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FilterNonCurrentTfms_FiltersOutCurrentTfm() {
        // Arrange
        var tfms = new[] { "net6.0", "net7.0", "net8.0", "net9.0" };

        // Act
        var result = _validator.FilterNonCurrentTfms(tfms).ToList();

        // Assert
        Assert.Equal(3, result.Count);
        Assert.Contains("net6.0", result);
        Assert.Contains("net7.0", result);
        Assert.Contains("net9.0", result);
        Assert.DoesNotContain("net8.0", result);
    }

    [Theory]
    [MemberData(nameof(DockerPropertiesTestData))]
    public void HasDockerProperties_ReturnsExpectedResult(Dictionary<string, string> properties, bool expected) {
        // Act
        var result = _validator.HasDockerProperties(properties);

        // Assert
        Assert.Equal(expected, result);
    }

    public static TheoryData<Dictionary<string, string>, bool> DockerPropertiesTestData => new()
    {
        { new Dictionary<string, string> { { "ContainerBaseImage", "mcr.microsoft.com/dotnet/aspnet:8.0" } }, true },
        { new Dictionary<string, string> { { "ContainerFamily", "alpine" } }, true },
        { new Dictionary<string, string> { { "ContainerRegistry", "docker.io" } }, true },
        { new Dictionary<string, string> { { "ContainerRepository", "myapp" } }, true },
        { new Dictionary<string, string> { { "ContainerImageTag", "latest" } }, true },
        { new Dictionary<string, string> { { "SomeOtherProperty", "value" } }, false },
        { new Dictionary<string, string>(), false },
        { new Dictionary<string, string> { { "ContainerBaseImage", "" } }, false },
        { new Dictionary<string, string> { { "ContainerBaseImage", "  " } }, false },
    };
}
