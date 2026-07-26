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
    public void ApplicationProjectUsesLibsAsTheManagedDependencyOutputBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        XElement projectRoot = project.Root ?? throw new AssertFailedException("Application project XML has no root element.");

        XElement policy = projectRoot
            .Elements("Target")
            .SingleOrDefault(target => string.Equals(
                (string)target.Attribute("Name"),
                "ApplyManagedDependencyOutputPolicy",
                StringComparison.Ordinal))
            ?? throw new AssertFailedException("Managed dependency output policy target is missing.");

        StringAssert.Contains(
            (string)policy.Attribute("BeforeTargets"),
            "_CopyFilesMarkedCopyLocal");
        StringAssert.Contains(
            (string)policy.Attribute("AfterTargets"),
            "ResolveAssemblyReferences");
        Assert.IsNotNull(
            policy.Descendants("ReferenceCopyLocalPaths")
                .SingleOrDefault(),
            "The output policy must apply to the resolved managed dependency graph.");
        Assert.AreEqual(
            "libs\\",
            (string)policy.Descendants("DestinationSubDirectory").Single(),
            "Managed dependencies must be copied under the portable libs directory.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string)target.Attribute("Name"),
                "RelocateManagedDependenciesToLibs",
                StringComparison.Ordinal)),
            "The old copy-then-relocate target must not remain in the project boundary.");

        XDocument config = XDocument.Load(Path.Combine(repositoryRoot, "app.config"));
        XElement probing = config
            .Descendants(XName.Get("probing", "urn:schemas-microsoft-com:asm.v1"))
            .SingleOrDefault()
            ?? throw new AssertFailedException("Application probing policy is missing.");
        Assert.AreEqual("libs", (string)probing.Attribute("privatePath"));

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.exe")),
            "The Release application output must exist before this layout behavior test runs.");
        Assert.AreEqual(
            0,
            Directory.GetFiles(releaseOutputDirectory, "*.dll", SearchOption.TopDirectoryOnly).Length,
            "Managed assemblies must not remain in the application output root.");
        string managedDependencyDirectory = Path.Combine(releaseOutputDirectory, "libs");
        foreach (string dependencyName in new[]
        {
            "Livet.dll",
            "Newtonsoft.Json.dll",
            "SevenZipExtractor.dll",
            "OggVorbis.NET64.dll"
        })
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(managedDependencyDirectory, dependencyName)),
                $"The Release output must place {dependencyName} under libs.");
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
