using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;
using System.Collections.Concurrent;
using Spectre.Console;

namespace bld.Tests;

public class RecursiveDependencyResolverTests {
    
    [Fact]
    public async Task ResolveTransitiveDependencies_WithSimplePackage_ReturnsGraph() {
        // Arrange
        var options = new NugetMetadataOptions();
        using var httpClient = NugetMetadataService.CreateHttpClient(options);
        var resolver = new RecursiveDependencyResolver(httpClient, options, null); // Use null for logger in tests
        
        var resolutionOptions = new DependencyResolutionOptions {
            MaxDepth = 3,
            AllowPrerelease = false,
            TargetFrameworks = new[] { "net8.0" }
        };
        
        // Act
        var result = await resolver.ResolveTransitiveDependenciesAsync(
            new[] { "Newtonsoft.Json" }, 
            resolutionOptions);
        
        // Assert
        Assert.NotNull(result);
        Assert.NotEmpty(result.RootPackages);
        Assert.NotEmpty(result.AllPackages);
        
        var rootPackage = result.RootPackages.First();
        Assert.Equal("Newtonsoft.Json", rootPackage.PackageId);
        Assert.True(rootPackage.Depth == 0);
        
        // Should have at least the root package in the flat list
        Assert.Contains(result.AllPackages, p => p.PackageId == "Newtonsoft.Json" && p.IsRootPackage);
    }
    
    [Fact]
    public async Task ResolveTransitiveDependencies_WithMaxDepthLimit_RespectsLimit() {
        // Arrange
        var options = new NugetMetadataOptions();
        using var httpClient = NugetMetadataService.CreateHttpClient(options);
        var resolver = new RecursiveDependencyResolver(httpClient, options, null);
        
        var resolutionOptions = new DependencyResolutionOptions {
            MaxDepth = 1, // Very shallow to test limit
            AllowPrerelease = false,
            TargetFrameworks = new[] { "net8.0" }
        };
        
        // Act
        var result = await resolver.ResolveTransitiveDependenciesAsync(
            new[] { "Microsoft.Extensions.Logging" }, 
            resolutionOptions);
        
        // Assert
        Assert.NotNull(result);
        
        // All packages should have depth <= MaxDepth
        Assert.All(result.AllPackages, p => Assert.True(p.Depth <= resolutionOptions.MaxDepth));
    }
    
    [Fact]
    public async Task ResolveTransitiveDependencies_WithMultipleRootPackages_ReturnsAllRoots() {
        // Arrange
        var options = new NugetMetadataOptions();
        using var httpClient = NugetMetadataService.CreateHttpClient(options);
        var resolver = new RecursiveDependencyResolver(httpClient, options, null);
        
        var resolutionOptions = new DependencyResolutionOptions {
            MaxDepth = 2,
            AllowPrerelease = false,
            TargetFrameworks = new[] { "net8.0" }
        };
        
        var rootPackages = new[] { "Newtonsoft.Json", "System.Text.Json" };
        
        // Act
        var result = await resolver.ResolveTransitiveDependenciesAsync(
            rootPackages, 
            resolutionOptions);
        
        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.RootPackages.Count);
        
        foreach (var expectedPackage in rootPackages) {
            Assert.Contains(result.RootPackages, p => p.PackageId == expectedPackage);
            Assert.Contains(result.AllPackages, p => p.PackageId == expectedPackage && p.IsRootPackage);
        }
    }
}