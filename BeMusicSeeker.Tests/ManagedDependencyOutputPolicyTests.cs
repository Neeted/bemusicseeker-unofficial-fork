using System;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        Assert.IsFalse(config.Descendants("startup").Any(), "Framework supportedRuntime selection must not remain in the .NET 10 app config.");
        Assert.IsFalse(config.Descendants("runtime").Any(), "Framework runtime switches must not remain in the .NET 10 app config.");

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
        string dependencyGraph = File.ReadAllText(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.deps.json"));
        StringAssert.Contains(dependencyGraph, "\"Newtonsoft.Json/13.0.4\"");

        foreach (string dependencyName in new[]
        {
            "Livet.dll",
            "Newtonsoft.Json.dll",
            "NLog.dll",
            "SevenZipExtractor.dll"
        })
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(releaseOutputDirectory, dependencyName)),
                $"The host layout must place {dependencyName} beside the application.");
        }

        foreach (string removedAddonName in new[] { "NLog.Database.dll", "NLog.WindowsEventLog.dll" })
        {
            Assert.IsFalse(
                File.Exists(Path.Combine(releaseOutputDirectory, removedAddonName)),
                $"The NLog 6 host layout must not deploy removed target package {removedAddonName}.");
        }

        XElement nlogReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string)reference.Attribute("Include"), "NLog", StringComparison.Ordinal));
        Assert.IsNull(nlogReference.Attribute("Version"));
        Assert.IsFalse(
            projectRoot.Elements("ItemGroup").Elements("PackageReference").Any(reference =>
                string.Equals((string)reference.Attribute("Include"), "NLog.Database", StringComparison.Ordinal) ||
                string.Equals((string)reference.Attribute("Include"), "NLog.WindowsEventLog", StringComparison.Ordinal)),
            "NLog 6 core must be the only NLog package reference.");
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "NLog.dll")),
            "The tracked legacy NLog binary must not remain beside the SDK project.");

        XElement newtonsoftReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string)reference.Attribute("Include"), "Newtonsoft.Json", StringComparison.Ordinal));
        Assert.IsNull(newtonsoftReference.Attribute("Version"));
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "Newtonsoft.Json.dll")),
            "Newtonsoft.Json must be supplied by the SDK package output, not a tracked HintPath binary.");

        XElement resourcesReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string)reference.Attribute("Include"), "System.Resources.Extensions", StringComparison.Ordinal));
        Assert.IsNull(resourcesReference.Attribute("Version"));
        Assert.IsFalse(
            File.Exists(Path.Combine(repositoryRoot, "libs", "System.Resources.Extensions.dll")),
            "System.Resources.Extensions must not be supplied by a tracked HintPath binary; the .NET 10 WindowsDesktop runtime pack supplies the publish asset.");

        XElement configurationReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string)reference.Attribute("Include"), "System.Configuration.ConfigurationManager", StringComparison.Ordinal));
        Assert.IsNull(configurationReference.Attribute("Version"));

        string lockFile = File.ReadAllText(Path.Combine(repositoryRoot, "packages.lock.json"));
        StringAssert.Contains(lockFile, "\"System.Configuration.ConfigurationManager\":");
        StringAssert.Contains(lockFile, "\"requested\": \"[10.0.10, )\"");
        using JsonDocument lockDocument = JsonDocument.Parse(lockFile);
        Assert.IsTrue(
            lockDocument.RootElement.GetProperty("dependencies").TryGetProperty("net10.0-windows7.0/win-x64", out _),
            "The lock file must retain the win-x64 target graph used by self-contained publish.");
    }

    [TestMethod]
    public void TestProjectOwnsLockedTestHostDependencyGraph()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument project = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"));
        XElement projectRoot = project.Root ?? throw new AssertFailedException("Test project XML has no root element.");

        Assert.AreEqual(
            "true",
            (string)projectRoot.Elements("PropertyGroup").Elements("RestorePackagesWithLockFile").Single(),
            "The test project must own a packages.lock.json for deterministic testhost restore.");
        XElement testSdkReference = projectRoot
            .Elements("ItemGroup")
            .Elements("PackageReference")
            .Single(reference => string.Equals((string)reference.Attribute("Include"), "Microsoft.NET.Test.Sdk", StringComparison.Ordinal));
        Assert.IsNull(testSdkReference.Attribute("Version"));

        string lockPath = Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "packages.lock.json");
        Assert.IsTrue(File.Exists(lockPath), "The test project lock file must be tracked beside its project.");
        using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllText(lockPath));
        JsonElement dependencies = lockDocument.RootElement.GetProperty("dependencies");
        JsonElement targetDependencies = dependencies.GetProperty("net10.0-windows7.0");
        JsonElement sdk = targetDependencies.GetProperty("Microsoft.NET.Test.Sdk");
        Assert.AreEqual("18.8.1", sdk.GetProperty("resolved").GetString());
        Assert.AreEqual("Direct", sdk.GetProperty("type").GetString());
        Assert.IsTrue(
            dependencies.TryGetProperty("net10.0-windows7.0/win-x64", out _),
            "The test lock file must retain the win-x64 target graph used by the solution restore.");
    }

    [TestMethod]
    public void PackageVersionsAreCentrallyOwnedAndAnalyzersStayOutOfRuntimeOutput()
    {
        string repositoryRoot = FindRepositoryRoot();
        var expectedVersions = new[]
        {
            new { Id = "Microsoft.NET.Test.Sdk", Version = "18.8.1" },
            new { Id = "MSTest.TestAdapter", Version = "3.6.4" },
            new { Id = "MSTest.TestFramework", Version = "3.6.4" },
            new { Id = "NLog", Version = "6.1.4" },
            new { Id = "Newtonsoft.Json", Version = "13.0.4" },
            new { Id = "Roslynator.Analyzers", Version = "4.15.0" },
            new { Id = "Roslynator.CodeAnalysis.Analyzers", Version = "4.15.0" },
            new { Id = "Roslynator.Formatting.Analyzers", Version = "4.15.0" },
            new { Id = "System.Configuration.ConfigurationManager", Version = "10.0.10" },
            new { Id = "System.Resources.Extensions", Version = "10.0.10" }
        }.ToDictionary(item => item.Id, item => item.Version, StringComparer.Ordinal);

        XDocument centralPackages = XDocument.Load(Path.Combine(repositoryRoot, "Directory.Packages.props"));
        XElement centralRoot = centralPackages.Root ?? throw new AssertFailedException("Central package props has no root element.");
        Assert.AreEqual(
            "true",
            (string)centralRoot.Elements("PropertyGroup").Elements("ManagePackageVersionsCentrally").Single(),
            "Central package management must be enabled for the migration dependency graph.");
        var centralVersions = centralRoot
            .Elements("ItemGroup")
            .Elements("PackageVersion")
            .ToDictionary(
                package => (string)package.Attribute("Include")!,
                package => (string)package.Attribute("Version")!,
                StringComparer.Ordinal);
        CollectionAssert.AreEquivalent(expectedVersions.Keys.ToArray(), centralVersions.Keys.ToArray());
        foreach (var expected in expectedVersions)
        {
            Assert.AreEqual(expected.Value, centralVersions[expected.Key]);
        }

        var projectPackages = new[]
        {
            new
            {
                ProjectPath = Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"),
                LockPath = Path.Combine(repositoryRoot, "packages.lock.json"),
                PackageIds = new[]
                {
                    "Newtonsoft.Json",
                    "NLog",
                    "Roslynator.Analyzers",
                    "Roslynator.CodeAnalysis.Analyzers",
                    "Roslynator.Formatting.Analyzers",
                    "System.Configuration.ConfigurationManager",
                    "System.Resources.Extensions"
                }
            },
            new
            {
                ProjectPath = Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "BeMusicSeeker.Tests.csproj"),
                LockPath = Path.Combine(repositoryRoot, "BeMusicSeeker.Tests", "packages.lock.json"),
                PackageIds = new[]
                {
                    "Microsoft.NET.Test.Sdk",
                    "MSTest.TestAdapter",
                    "MSTest.TestFramework"
                }
            }
        };

        foreach (var projectPackage in projectPackages)
        {
            XDocument project = XDocument.Load(projectPackage.ProjectPath);
            XElement projectRoot = project.Root ?? throw new AssertFailedException("Package project has no root element.");
            foreach (XElement packageReference in projectRoot.Elements("ItemGroup").Elements("PackageReference"))
            {
                string packageId = (string)packageReference.Attribute("Include")!;
                Assert.IsTrue(
                    expectedVersions.ContainsKey(packageId),
                    $"Package {packageId} must be declared by Directory.Packages.props.");
                Assert.IsNull(
                    packageReference.Attribute("Version"),
                    $"Package {packageId} must not carry a project-local version.");
            }

            Assert.IsTrue(File.Exists(projectPackage.LockPath), $"Lock file is missing: {projectPackage.LockPath}");
            using JsonDocument lockDocument = JsonDocument.Parse(File.ReadAllText(projectPackage.LockPath));
            JsonElement target = lockDocument.RootElement.GetProperty("dependencies").GetProperty("net10.0-windows7.0");
            foreach (string packageId in projectPackage.PackageIds)
            {
                Assert.IsTrue(target.TryGetProperty(packageId, out JsonElement dependency), $"Lock entry is missing: {packageId}");
                Assert.AreEqual("Direct", dependency.GetProperty("type").GetString());
                Assert.AreEqual(expectedVersions[packageId], dependency.GetProperty("resolved").GetString());
            }
        }

        XDocument appProject = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        foreach (string analyzerId in new[]
        {
            "Roslynator.Analyzers",
            "Roslynator.CodeAnalysis.Analyzers",
            "Roslynator.Formatting.Analyzers"
        })
        {
            XElement analyzerReference = appProject
                .Root!
                .Elements("ItemGroup")
                .Elements("PackageReference")
                .Single(reference => string.Equals((string)reference.Attribute("Include"), analyzerId, StringComparison.Ordinal));
            Assert.AreEqual("all", (string)analyzerReference.Element("PrivateAssets"));
            Assert.AreEqual(
                "runtime; build; native; contentfiles; analyzers; buildtransitive",
                (string)analyzerReference.Element("IncludeAssets"));
        }

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsFalse(
            Directory.EnumerateFiles(releaseOutputDirectory, "Roslynator*.dll", SearchOption.AllDirectories).Any(),
            "Analyzer assemblies must not be copied to the application runtime output.");
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
