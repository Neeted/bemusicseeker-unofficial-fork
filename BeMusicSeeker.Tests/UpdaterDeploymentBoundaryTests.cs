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
    public void ApplicationProjectUsesDedicatedUpdaterPublishBoundary()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument applicationProject = XDocument.Load(Path.Combine(repositoryRoot, "BeMusicSeeker.csproj"));
        XElement projectRoot = applicationProject.Root
            ?? throw new AssertFailedException("Application project XML has no root element.");

        Assert.IsFalse(
            projectRoot.Descendants("ProjectReference").Any(reference => string.Equals(
                (string)reference.Attribute("Include"),
                @"BeMusicSeeker.Updater\BeMusicSeeker.Updater.csproj",
                StringComparison.OrdinalIgnoreCase)),
            "The main app publish must not inherit the updater build output.");
        Assert.IsFalse(
            projectRoot.Elements("Target").Any(target => string.Equals(
                (string)target.Attribute("Name"),
                "CopyUpdaterToAppOutput",
                StringComparison.Ordinal)),
            "The build output must not be the updater deployment boundary.");
        Assert.IsNull(
            projectRoot
                .Descendants("Reference")
                .SingleOrDefault(reference => string.Equals(
                    (string)reference.Attribute("Include"),
            "System.Deployment",
            StringComparison.OrdinalIgnoreCase)),
            "The unused System.Deployment reference must not remain in the application project.");

        XDocument updaterProject = XDocument.Load(Path.Combine(
            repositoryRoot,
            "BeMusicSeeker.Updater",
            "BeMusicSeeker.Updater.csproj"));
        XElement updaterRoot = updaterProject.Root
            ?? throw new AssertFailedException("Updater project XML has no root element.");
        Assert.AreEqual("net10.0-windows", (string)updaterRoot.Descendants("TargetFramework").Single());
        Assert.AreEqual("x64", (string)updaterRoot.Descendants("PlatformTarget").Single());

        string updaterProfilePath = Path.Combine(
            repositoryRoot,
            "BeMusicSeeker.Updater",
            "Properties",
            "PublishProfiles",
            "WinX64SelfContainedSingleFile.pubxml");
        XDocument updaterProfile = XDocument.Load(updaterProfilePath);
        AssertProfileValue(updaterProfile, "RuntimeIdentifier", "win-x64");
        AssertProfileValue(updaterProfile, "SelfContained", "true");
        AssertProfileValue(updaterProfile, "PublishSingleFile", "true");
        AssertProfileValue(updaterProfile, "IncludeNativeLibrariesForSelfExtract", "true");
        AssertProfileValue(updaterProfile, "PublishTrimmed", "false");
        AssertProfileValue(updaterProfile, "PublishReadyToRun", "false");

        string selectedAppProfilePath = Path.Combine(
            repositoryRoot,
            "Properties",
            "PublishProfiles",
            "WinX64SelfContained.pubxml");
        XDocument selectedAppProfile = XDocument.Load(selectedAppProfilePath);
        AssertProfileValue(selectedAppProfile, "RuntimeIdentifier", "win-x64");
        AssertProfileValue(selectedAppProfile, "SelfContained", "true");
        AssertProfileValue(selectedAppProfile, "PublishSingleFile", "true");
        AssertProfileValue(selectedAppProfile, "IncludeNativeLibrariesForSelfExtract", "false");
        AssertProfileValue(selectedAppProfile, "IncludeAllContentForSelfExtract", "false");
        AssertProfileValue(selectedAppProfile, "PublishTrimmed", "false");
        AssertProfileValue(selectedAppProfile, "PublishReadyToRun", "true");
        AssertProfileValue(selectedAppProfile, "PublishReadyToRunComposite", "false");
        AssertProfileValue(selectedAppProfile, "EnableCompressionInSingleFile", "false");
        Assert.IsFalse(
            File.Exists(Path.Combine(
                repositoryRoot,
                "Properties",
                "PublishProfiles",
                "WinX64SelfContainedSingleFile.pubxml")),
            "The superseded main-app profile must not remain.");
    }

    [TestMethod]
    [TestCategory("ReleaseAcceptance")]
    public void PortablePackageLayoutValidatorAcceptsSelfContainedPublishOutput()
    {
        string repositoryRoot = FindRepositoryRoot();
        string validatorPath = Path.Combine(repositoryRoot, "scripts", "portable-package-layout.ps1");
        Assert.IsTrue(File.Exists(validatorPath), "The release layout validator must be present.");

        string appPublishDirectory = ResolveSelfContainedPublishDirectory("BMS_SCD_APP_PUBLISH_ROOT", "app");
        string updaterPublishDirectory = ResolveSelfContainedPublishDirectory("BMS_SCD_UPDATER_PUBLISH_ROOT", "updater");
        string stagingDirectory = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_ReleaseLayoutTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        foreach (string sourcePath in Directory.EnumerateFiles(appPublishDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(appPublishDirectory, sourcePath);
            string topLevelName = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (topLevelName.Equals("config", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("data", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("log", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("logs", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("update_backup", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("update_work", StringComparison.OrdinalIgnoreCase)
                || topLevelName.Equals("imported_metadata", StringComparison.OrdinalIgnoreCase)
                || relativePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith("BeMusicSeeker.Updater.", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destinationPath = Path.Combine(stagingDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }
        string updaterPath = Path.Combine(updaterPublishDirectory, "BeMusicSeeker.Updater.exe");
        Assert.IsTrue(File.Exists(updaterPath), "The self-contained updater publish output must exist.");
        File.Copy(updaterPath, Path.Combine(stagingDirectory, "BeMusicSeeker.Updater.exe"));

        ProcessStartInfo CreateValidatorStartInfo(string? command = null)
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
            startInfo.ArgumentList.Add(command
                ?? $". '{EscapePowerShellLiteral(validatorPath)}'; "
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

            string[] forbiddenPaths =
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
                "x86/7z.dll",
                "x86/bass.dll",
                "x86/bass_fx.dll",
                "x86/bassasio.dll",
                "x86/bassenc.dll",
                "x86/bassmix.dll",
                "x86/basswasapi.dll",
                "libs/x86/sqlite3.dll",
                "libs/x86/7z.dll",
                "libs/x86/bass.dll",
                "libs/x86/bass_fx.dll",
                "libs/x86/bassasio.dll",
                "libs/x86/bassenc.dll",
                "libs/x86/bassmix.dll",
                "libs/x86/basswasapi.dll",
                "Bass.Net.dll",
                "x64/OggVorbis.NET64.dll",
                "x64/7z.dll",
                "x64/bass.dll",
                "x64/bass_fx.dll",
                "x64/bassasio.dll",
                "x64/bassenc.dll",
                "x64/bassmix.dll",
                "x64/basswasapi.dll",
                "OggVorbis.NET.dll"
            };
            var forbiddenCases = forbiddenPaths
                .Select(path => (
                    Path: path,
                    IsDirectory: path is "config" or "log",
                    MembershipPath: path,
                    FailurePath: path.StartsWith("libs/x86/", StringComparison.OrdinalIgnoreCase)
                        ? "libs/x86"
                        : path.StartsWith("x86/", StringComparison.OrdinalIgnoreCase)
                            ? "x86"
                            : path))
                .Concat(new[]
                {
                    (Path: "x86/user-added.dll", IsDirectory: false, MembershipPath: "x86", FailurePath: "x86"),
                    (Path: "libs/x86/user-added.dll", IsDirectory: false, MembershipPath: "libs/x86", FailurePath: "libs/x86")
                })
                .ToArray();
            string forbiddenCaseExpression = "@("
                + string.Join(
                    ",",
                    forbiddenCases.Select(testCase =>
                        "[pscustomobject]@{"
                        + "Path='" + EscapePowerShellLiteral(testCase.Path) + "';"
                        + "IsDirectory=$" + testCase.IsDirectory.ToString().ToLowerInvariant() + ";"
                        + "MembershipPath='" + EscapePowerShellLiteral(testCase.MembershipPath) + "';"
                        + "FailurePath='" + EscapePowerShellLiteral(testCase.FailurePath) + "'"
                        + "}"))
                + ")";
            string negativeValidatorCommand =
                $". '{EscapePowerShellLiteral(validatorPath)}'; "
                + $"$root = '{EscapePowerShellLiteral(stagingDirectory)}'; "
                + $"$forbiddenCases = {forbiddenCaseExpression}; "
                + "$policyPaths = @(Get-PortableForbiddenPaths); "
                + "foreach ($case in $forbiddenCases) { "
                + "if ($policyPaths -notcontains $case.MembershipPath) { throw \"Forbidden path is missing from policy: $($case.MembershipPath)\" }; "
                + "$fullPath = Join-PortablePackageRelativePath $root $case.Path; "
                + "$parentPath = Split-Path -Parent $fullPath; "
                + "if ($case.IsDirectory) { [IO.Directory]::CreateDirectory($fullPath) | Out-Null } "
                + "else { [IO.Directory]::CreateDirectory($parentPath) | Out-Null; [IO.File]::WriteAllText($fullPath, 'forbidden-layout-entry') }; "
                + "$failureMessage = $null; "
                + "try { Assert-PortableStagingLayout -targetStagingDirectory $root -requiresMetadataArchive:$false } catch { $failureMessage = $_.Exception.Message }; "
                + "if ([string]::IsNullOrWhiteSpace($failureMessage)) { throw \"Validator accepted forbidden path: $($case.Path)\" }; "
                + "if ($failureMessage.IndexOf($case.FailurePath, [StringComparison]::OrdinalIgnoreCase) -lt 0) { "
                + "throw \"Validator rejected $($case.Path) for an unexpected reason: $failureMessage\" "
                + "}; "
                + "if ($case.IsDirectory) { [IO.Directory]::Delete($fullPath, $true) } else { [IO.File]::Delete($fullPath) }; "
                + "while ($parentPath -ne $root -and [IO.Directory]::Exists($parentPath) -and [IO.Directory]::GetFileSystemEntries($parentPath).Length -eq 0) { "
                + "[IO.Directory]::Delete($parentPath); "
                + "$parentPath = Split-Path -Parent $parentPath "
                + "} "
                + "}";

            using Process rejectedProcess = Process.Start(CreateValidatorStartInfo(negativeValidatorCommand))
                ?? throw new AssertFailedException("pwsh could not be started for the negative package layout validation.");
            string rejectedOutput = rejectedProcess.StandardOutput.ReadToEnd();
            string rejectedError = rejectedProcess.StandardError.ReadToEnd();
            rejectedProcess.WaitForExit();
            Assert.AreEqual(0, rejectedProcess.ExitCode, rejectedOutput + Environment.NewLine + rejectedError);
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

    private static void AssertProfileValue(XDocument profile, string propertyName, string expectedValue)
    {
        XElement property = profile
            .Descendants()
            .SingleOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal))
            ?? throw new AssertFailedException($"Publish profile property is missing: {propertyName}");
        Assert.AreEqual(expectedValue, property.Value, propertyName);
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

    private static string ResolveSelfContainedPublishDirectory(string environmentVariableName, string childDirectoryName)
    {
        string configuredPath = Environment.GetEnvironmentVariable(environmentVariableName);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            Assert.Inconclusive($"Self-contained publish verification requires {environmentVariableName} for {childDirectoryName}.");
        }

        string path = configuredPath!;
        if (!Directory.Exists(path))
        {
            Assert.Inconclusive("Self-contained publish output is missing: " + path);
        }
        return path;
    }
}
