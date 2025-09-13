using bld.Infrastructure;
using bld.Models;
using bld.Services.NuGet;

namespace bld.Tests;

public sealed class ReverseDependencyGraphServiceTests {
    
    [Fact]
    public void BuildReverseDependencyGraph_WithEmptyGraph_ReturnsEmptyAnalysis() {
        // Arrange
        var service = new ReverseDependencyGraphService(null); // Use null console for tests
        var forwardGraph = new PackageDependencyGraph {
            RootPackages = [],
            AllPackages = []
        };
        
        // Act
        var result = service.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages: false);
        
        // Assert
        Assert.Equal(0, result.TotalPackages);
        Assert.Equal(0, result.ExplicitPackages);
        Assert.Equal(0, result.TransitivePackages);
        Assert.True(result.ReverseNodes.Count == 0);
    }
    
    [Fact]
    public void BuildReverseDependencyGraph_WithSingleRootPackage_CorrectlyIdentifiesExplicitPackage() {
        // Arrange
        var service = new ReverseDependencyGraphService(null); // Use null console for tests
        var rootNode = new DependencyGraphNode {
            PackageId = "RootPackage",
            Version = "1.0.0",
            TargetFramework = "net8.0",
            Depth = 0,
            Dependencies = []
        };
        
        var forwardGraph = new PackageDependencyGraph {
            RootPackages = [rootNode],
            AllPackages = [new PackageReference {
                PackageId = "RootPackage",
                Version = "1.0.0",
                TargetFramework = "net8.0",
                IsRootPackage = true,
                Depth = 0
            }]
        };
        
        // Act
        var result = service.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages: false);
        
        // Assert
        Assert.Equal(1, result.TotalPackages);
        Assert.Equal(1, result.ExplicitPackages);
        Assert.Equal(0, result.TransitivePackages);
        
        var reverseNode = result.ReverseNodes.First();
        Assert.Equal("RootPackage", reverseNode.PackageId);
        Assert.True(reverseNode.IsExplicit);
        Assert.Equal(0, reverseNode.DependentPackages.Count);
    }
    
    [Fact]
    public void BuildReverseDependencyGraph_WithDependencies_CorrectlyBuildsDependentsList() {
        // Arrange
        var service = new ReverseDependencyGraphService(null); // Use null console for tests
        var childNode = new DependencyGraphNode {
            PackageId = "ChildPackage",
            Version = "2.0.0",
            TargetFramework = "net8.0",
            Depth = 1,
            Dependencies = []
        };
        
        var rootNode = new DependencyGraphNode {
            PackageId = "RootPackage",
            Version = "1.0.0",
            TargetFramework = "net8.0",
            Depth = 0,
            Dependencies = [childNode]
        };
        
        var forwardGraph = new PackageDependencyGraph {
            RootPackages = [rootNode],
            AllPackages = [
                new PackageReference {
                    PackageId = "RootPackage",
                    Version = "1.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = true,
                    Depth = 0
                },
                new PackageReference {
                    PackageId = "ChildPackage",
                    Version = "2.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = false,
                    Depth = 1
                }
            ]
        };
        
        // Act
        var result = service.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages: false);
        
        // Assert
        Assert.Equal(2, result.TotalPackages);
        Assert.Equal(1, result.ExplicitPackages);
        Assert.Equal(1, result.TransitivePackages);
        
        var childReverseNode = result.ReverseNodes.First(n => n.PackageId == "ChildPackage");
        Assert.False(childReverseNode.IsExplicit);
        Assert.Equal(1, childReverseNode.DependentPackages.Count);
        Assert.Equal("RootPackage", childReverseNode.DependentPackages[0].PackageId);
        
        var rootReverseNode = result.ReverseNodes.First(n => n.PackageId == "RootPackage");
        Assert.True(rootReverseNode.IsExplicit);
        Assert.Equal(0, rootReverseNode.DependentPackages.Count);
    }
    
    [Fact]
    public void BuildReverseDependencyGraph_WithFrameworkPackages_CanExcludeFrameworkPackages() {
        // Arrange
        var service = new ReverseDependencyGraphService(null); // Use null console for tests
        var microsoftNode = new DependencyGraphNode {
            PackageId = "Microsoft.Extensions.Logging",
            Version = "6.0.0",
            TargetFramework = "net8.0",
            Depth = 1,
            Dependencies = []
        };
        
        var systemNode = new DependencyGraphNode {
            PackageId = "System.Text.Json",
            Version = "6.0.0",
            TargetFramework = "net8.0",
            Depth = 1,
            Dependencies = []
        };
        
        var rootNode = new DependencyGraphNode {
            PackageId = "MyCustomPackage",
            Version = "1.0.0",
            TargetFramework = "net8.0",
            Depth = 0,
            Dependencies = [microsoftNode, systemNode]
        };
        
        var forwardGraph = new PackageDependencyGraph {
            RootPackages = [rootNode],
            AllPackages = [
                new PackageReference {
                    PackageId = "MyCustomPackage",
                    Version = "1.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = true,
                    Depth = 0
                },
                new PackageReference {
                    PackageId = "Microsoft.Extensions.Logging",
                    Version = "6.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = false,
                    Depth = 1
                },
                new PackageReference {
                    PackageId = "System.Text.Json",
                    Version = "6.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = false,
                    Depth = 1
                }
            ]
        };
        
        // Act
        var resultWithFramework = service.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages: false);
        var resultWithoutFramework = service.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages: true);
        
        // Assert
        Assert.Equal(3, resultWithFramework.TotalPackages);
        Assert.Equal(1, resultWithoutFramework.TotalPackages); // Only MyCustomPackage should remain
        
        var customPackageNode = resultWithoutFramework.ReverseNodes.First();
        Assert.Equal("MyCustomPackage", customPackageNode.PackageId);
        Assert.True(customPackageNode.IsExplicit);
    }
    
    [Fact]
    public void BuildReverseDependencyGraph_CalculatesMostReferencedPackagesCorrectly() {
        // Arrange
        var service = new ReverseDependencyGraphService(null); // Use null console for tests
        var sharedNode = new DependencyGraphNode {
            PackageId = "SharedPackage",
            Version = "1.0.0",
            TargetFramework = "net8.0",
            Depth = 1,
            Dependencies = []
        };
        
        var root1 = new DependencyGraphNode {
            PackageId = "Root1",
            Version = "1.0.0",
            TargetFramework = "net8.0",
            Depth = 0,
            Dependencies = [sharedNode]
        };
        
        var root2 = new DependencyGraphNode {
            PackageId = "Root2",
            Version = "1.0.0",
            TargetFramework = "net8.0",
            Depth = 0,
            Dependencies = [sharedNode]
        };
        
        var forwardGraph = new PackageDependencyGraph {
            RootPackages = [root1, root2],
            AllPackages = [
                new PackageReference {
                    PackageId = "Root1",
                    Version = "1.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = true,
                    Depth = 0
                },
                new PackageReference {
                    PackageId = "Root2",
                    Version = "1.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = true,
                    Depth = 0
                },
                new PackageReference {
                    PackageId = "SharedPackage",
                    Version = "1.0.0",
                    TargetFramework = "net8.0",
                    IsRootPackage = false,
                    Depth = 1
                }
            ]
        };
        
        // Act
        var result = service.BuildReverseDependencyGraph(forwardGraph, excludeFrameworkPackages: false);
        
        // Assert
        Assert.Equal(3, result.TotalPackages);
        Assert.Equal(2, result.ExplicitPackages);
        Assert.Equal(1, result.TransitivePackages);
        
        var mostReferenced = result.MostReferencedPackages.First();
        Assert.Equal("SharedPackage", mostReferenced.PackageId);
        Assert.Equal(2, mostReferenced.ReferenceCount);
    }
}