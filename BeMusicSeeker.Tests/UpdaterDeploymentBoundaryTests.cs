using System;
using System.Diagnostics;
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
        StringAssert.Contains(copyTarget.ToString(SaveOptions.DisableFormatting), "BeMusicSeeker.Updater.dll");
        Assert.IsFalse(copyTarget.ToString(SaveOptions.DisableFormatting).Contains("net472", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(copyTarget.ToString(SaveOptions.DisableFormatting), "DestinationFolder=\"$(OutDir)\"");
        Assert.IsNotNull(
            copyTarget.Descendants("Error").SingleOrDefault(),
            "The build must fail when the updater output is missing.");
        XElement updaterOutputDirectoryProperties = copyTarget
            .Descendants("_UpdaterBuildOutputDirectory")
            .SingleOrDefault(property => string.Equals(
                (string)property.Attribute("Condition"),
                "'$(Platform)' == 'x64'",
                StringComparison.Ordinal))
            ?? throw new AssertFailedException("The x64 updater output path is missing.");
        StringAssert.Contains(updaterOutputDirectoryProperties.Value, "bin\\x64\\$(Configuration)\\$(TargetFramework)");
        XElement defaultUpdaterOutputDirectoryProperties = copyTarget
            .Descendants("_UpdaterBuildOutputDirectory")
            .SingleOrDefault(property => string.Equals(
                (string)property.Attribute("Condition"),
                "'$(Platform)' != 'x64'",
                StringComparison.Ordinal))
            ?? throw new AssertFailedException("The default-platform updater output path is missing.");
        Assert.IsFalse(defaultUpdaterOutputDirectoryProperties.Value.Contains("AnyCPU", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(defaultUpdaterOutputDirectoryProperties.Value, "bin\\$(Configuration)\\$(TargetFramework)");

        XDocument updaterProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "BeMusicSeeker.Updater",
            "BeMusicSeeker.Updater.csproj"));
        XElement updaterRoot = updaterProject.Root
            ?? throw new AssertFailedException("Updater project XML has no root element.");
        Assert.AreEqual("net10.0-windows", (string)updaterRoot.Descendants("TargetFramework").Single());
        Assert.AreEqual("x64", (string)updaterRoot.Descendants("PlatformTarget").Single());

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        Assert.IsTrue(
            File.Exists(Path.Combine(releaseOutputDirectory, "BeMusicSeeker.Updater.exe")),
            "The Release application output must contain the updater at the deployment root.");
        foreach (string companionFileName in new[]
        {
            "BeMusicSeeker.Updater.dll",
            "BeMusicSeeker.Updater.deps.json",
            "BeMusicSeeker.Updater.runtimeconfig.json"
        })
        {
            Assert.IsTrue(
                File.Exists(Path.Combine(releaseOutputDirectory, companionFileName)),
                "The Release application output must contain the updater companion payload at the deployment root: " + companionFileName);
        }
        Assert.IsFalse(
            File.Exists(Path.Combine(releaseOutputDirectory, "libs", "BeMusicSeeker.Updater.exe")),
            "The updater is a root deployment artifact, not a managed dependency under libs.");
    }

    [TestMethod]
    public void PortablePackageLayoutValidatorAcceptsReleaseOutput()
    {
        string repositoryRoot = FindRepositoryRoot();
        string validatorPath = Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1");
        Assert.IsTrue(File.Exists(validatorPath), "The release layout validator must be present.");

        string releaseOutputDirectory = ResolveReleaseOutputDirectory();
        string stagingDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ReleaseLayoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(releaseOutputDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(releaseOutputDirectory, sourcePath);
            string topLevelName = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (topLevelName.Equals("config", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("log", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destinationPath = Path.Combine(stagingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        ProcessStartInfo CreateValidatorStartInfo()
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add(
                $". '{EscapePowerShellLiteral(validatorPath)}'; "
                + $"Assert-PortableStagingLayout -targetStagingDirectory '{EscapePowerShellLiteral(stagingDirectory)}' -requiresMetadataArchive:$false");
            return startInfo;
        }

        try
        {
            using Process process = Process.Start(CreateValidatorStartInfo())
                ?? throw new AssertFailedException("pwsh could not be started for package layout validation.");
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.AreEqual(0, process.ExitCode, output + Environment.NewLine + error);

            foreach (string forbiddenPath in new[]
            {
                "config",
                "log",
                "libs/SevenZipExtractor.dll",
                "libs/OggVorbis.NET64.dll",
                "libs/x64/sqlite3.dll",
                "BeMusicSeeker.exe.config",
                "libs/Bass.Net.dll",
                "libs/DynamicJson.dll",
                "libs/IniLibrary.dll",
                "libs/Livet.dll",
                "libs/Livet.Extensions.dll",
                "libs/MetroRadiance.Chrome.dll",
                "libs/MetroRadiance.Core.dll",
                "libs/MetroRadiance.dll",
                "libs/Microsoft.Expression.Drawing.dll",
                "libs/Microsoft.Expression.Effects.dll",
                "libs/Microsoft.Expression.Interactions.dll",
                "libs/Microsoft.WindowsAPICodePack.dll",
                "libs/Microsoft.WindowsAPICodePack.Shell.dll",
                "libs/Newtonsoft.Json.dll",
                "libs/NLog.Database.dll",
                "libs/NLog.dll",
                "libs/NLog.WindowsEventLog.dll",
                "libs/QuickConverter.dll",
                "libs/SgmlReaderDll.dll",
                "libs/System.Collections.Immutable.dll",
                "libs/System.Resources.Extensions.dll",
                "libs/System.Memory.dll",
                "libs/System.Buffers.dll",
                "libs/System.Numerics.Vectors.dll",
                "libs/System.Runtime.CompilerServices.Unsafe.dll",
                "libs/System.Windows.Interactivity.dll",
                "libs/sqlite.net.dll",
                "x86/sqlite3.dll",
                "x86/user-added.dll",
                "x86/7z.dll",
                "x86/bass.dll",
                "x86/bass_fx.dll",
                "x86/bassasio.dll",
                "x86/bassenc.dll",
                "x86/bassmix.dll",
                "x86/basswasapi.dll",
                "libs/x86/sqlite3.dll",
                "libs/x86/user-added.dll",
                "libs/x86/7z.dll",
                "libs/x86/bass.dll",
                "libs/x86/bass_fx.dll",
                "libs/x86/bassasio.dll",
                "libs/x86/bassenc.dll",
                "libs/x86/bassmix.dll",
                "libs/x86/basswasapi.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll",
                "OggVorbis.NET.dll"
            })
            {
                string normalizedPath = forbiddenPath.Replace('/', Path.DirectorySeparatorChar);
                string fullPath = Path.Combine(stagingDirectory, normalizedPath);
                bool isDirectory = forbiddenPath is "config" or "log";
                if (isDirectory)
                {
                    Directory.CreateDirectory(fullPath);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    File.WriteAllText(fullPath, "forbidden-layout-entry");
                }

                using Process rejectedProcess = Process.Start(CreateValidatorStartInfo())
                    ?? throw new AssertFailedException("pwsh could not be started for the negative package layout validation.");
                string rejectedOutput = rejectedProcess.StandardOutput.ReadToEnd();
                string rejectedError = rejectedProcess.StandardError.ReadToEnd();
                rejectedProcess.WaitForExit();
                Assert.AreNotEqual(0, rejectedProcess.ExitCode, rejectedOutput + Environment.NewLine + rejectedError);
                string expectedForbiddenPath = forbiddenPath switch
                {
                    _ when forbiddenPath.StartsWith("x86/", StringComparison.OrdinalIgnoreCase) => "x86",
                    _ when forbiddenPath.StartsWith("libs/x86/", StringComparison.OrdinalIgnoreCase) => "libs/x86",
                    _ => forbiddenPath
                };
                StringAssert.Contains(rejectedError, expectedForbiddenPath);
                if (isDirectory)
                {
                    Directory.Delete(fullPath, recursive: true);
                }
                else
                {
                    File.Delete(fullPath);
                }

                string emptyParentPath = Path.GetDirectoryName(fullPath)!;
                while (!string.Equals(emptyParentPath, stagingDirectory, StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(emptyParentPath)
                    && !Directory.EnumerateFileSystemEntries(emptyParentPath).Any())
                {
                    Directory.Delete(emptyParentPath);
                    emptyParentPath = Path.GetDirectoryName(emptyParentPath)!;
                }
            }
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
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
