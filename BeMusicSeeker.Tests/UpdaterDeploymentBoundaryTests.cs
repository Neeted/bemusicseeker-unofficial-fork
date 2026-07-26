using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class UpdaterDeploymentBoundaryTests
{
    [TestMethod]
    public void ApplicationProjectOwnsUpdaterBuildAndRootDeploymentBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument applicationProject = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        XElement projectRoot = applicationProject.Root
            ?? throw new AssertFailedException("Application project XML has no root element.");

        XElement updaterReference = projectRoot
            .Descendants("ProjectReference")
            .SingleOrDefault(reference => string.Equals(
                (string)reference.Attribute("Include"),
                @"BeMusicSeeker.Updater\BeMusicSeeker.Updater.csproj",
                StringComparison.OrdinalIgnoreCase))
            ?? throw new AssertFailedException("The application project must build the updater project.");

        Assert.AreEqual("false", (string)updaterReference.Attribute("ReferenceOutputAssembly"));
        Assert.AreEqual("all", (string)updaterReference.Attribute("PrivateAssets"));
        Assert.IsNull(
            projectRoot
                .Descendants("Reference")
                .SingleOrDefault(reference => string.Equals(
                    (string)reference.Attribute("Include"),
                    "System.Deployment",
                    StringComparison.OrdinalIgnoreCase)),
            "The unused System.Deployment reference must not remain in the application project.");

        XElement copyTarget = projectRoot
            .Elements("Target")
            .SingleOrDefault(target => string.Equals(
                (string)target.Attribute("Name"),
                "CopyUpdaterToAppOutput",
                StringComparison.Ordinal))
            ?? throw new AssertFailedException("The updater deployment target is missing.");

        Assert.AreEqual("Build", (string)copyTarget.Attribute("AfterTargets"));
        StringAssert.Contains(copyTarget.ToString(SaveOptions.DisableFormatting), "BeMusicSeeker.Updater.exe");
        StringAssert.Contains(copyTarget.ToString(SaveOptions.DisableFormatting), "DestinationFolder=\"$(OutDir)\"");
        Assert.IsNotNull(
            copyTarget.Descendants("Error").SingleOrDefault(),
            "The build must fail when the updater output is missing.");

        XDocument updaterProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "BeMusicSeeker.Updater",
            "BeMusicSeeker.Updater.csproj"));
        XElement updaterRoot = updaterProject.Root
            ?? throw new AssertFailedException("Updater project XML has no root element.");
        Assert.AreEqual("net472", (string)updaterRoot.Descendants("TargetFramework").Single());
        Assert.AreEqual("x64", (string)updaterRoot.Descendants("PlatformTarget").Single());

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.Updater.exe")),
            "The Release application output must contain the updater at the deployment root.");
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "BeMusicSeeker.Updater.exe")),
            "The updater is a root deployment artifact, not a managed dependency under libs.");
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
