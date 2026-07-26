using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ManagedDependencyOutputPolicyTests
{
    [TestMethod]
    public void ApplicationProjectUsesHostManagedDependencyLayout()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        XElement projectRoot = project.Root ?? throw new AssertFailedException("Application project XML has no root element.");

        Assert.AreEqual(
            "net10.0-windows",
            (string)projectRoot.Elements("PropertyGroup").Elements("TargetFramework").Single(),
            "The application must target the Windows .NET 10 runtime.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string)target.Attribute("Name"),
                "ApplyManagedDependencyOutputPolicy",
                StringComparison.Ordinal)),
            "The legacy libs relocation target must not remain in the project boundary.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string)target.Attribute("Name"),
                "RemoveLegacyManagedDependencyRootOutput",
                StringComparison.Ordinal)),
            "The legacy managed DLL deletion target must not remain in the project boundary.");

        XDocument config = XDocument.Load(Path.Combine(repositoryRoot, "app.config"));
        Assert.IsFalse(
            config.Descendants(XName.Get("probing", "urn:schemas-microsoft-com:asm.v1")).Any(),
            "The runtime must not depend on Framework private probing.");

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.exe")),
            "The Release application output must exist before this layout behavior test runs.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.deps.json")),
            "The host dependency graph must be emitted beside the application.");
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.runtimeconfig.json")),
            "The host runtime configuration must be emitted beside the application.");

        foreach (string dependencyName in new[]
        {
            "Livet.dll",
            "Newtonsoft.Json.dll",
            "SevenZipExtractor.dll"
        })
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(releaseOutputDirectory, dependencyName)),
                $"The host layout must place {dependencyName} beside the application.");
        }
    }

    private static string FindRepositoryRoot()
    {
        string? directoryPath = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(directoryPath))
        {
            if (File.Exists(Path.Combine(directoryPath, "BeMusicSeeker.sln")))
            {
                return directoryPath!;
            }

            directoryPath = Directory.GetParent(directoryPath)?.FullName;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static string ResolveReleaseOutputDirectory()
    {
        var testOutputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        string targetFramework = testOutputDirectory.Name;
        string? configuration = testOutputDirectory.Parent?.Name;
        string? platform = testOutputDirectory.Parent?.Parent?.Name;
        if (string.IsNullOrWhiteSpace(configuration) || string.IsNullOrWhiteSpace(platform))
        {
            throw new AssertFailedException("The test output path does not contain configuration and platform segments.");
        }

        return Path.Combine(FindRepositoryRoot(), "bin", platform, configuration, targetFramework);
    }
}
