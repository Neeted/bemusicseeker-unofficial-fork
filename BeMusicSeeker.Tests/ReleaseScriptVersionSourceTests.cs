using System;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ReleaseScriptVersionSourceTests
{
    [TestMethod]
    public void ReleaseScript_UsesAssemblyInformationalVersionAsReleaseVersionSource()
    {
        string releaseScript = ReadRepositoryFile("scripts", "release.ps1");

        StringAssert.Contains(releaseScript, "function Get-AppVersion");
        StringAssert.Contains(releaseScript, "function Write-PublicVersionText");
        StringAssert.Contains(releaseScript, "Write-PublicVersionText $context");
        StringAssert.Contains(releaseScript, "Assert-HeadMatchesReleaseTag $context.Tag");
        StringAssert.Contains(releaseScript, "Assert-RawReleaseFilesPublished $context");
        StringAssert.Contains(releaseScript, "AssemblyInformationalVersion");
        AssertNoLocalVersionTxtRead(releaseScript, "release.ps1");

        string publishDraft = ExtractFunction(releaseScript, "Invoke-PublishDraft");
        AssertContainsInOrder(
            publishDraft,
            "Assert-RemoteTagMatchesLocal $context.Tag",
            "Assert-HeadMatchesReleaseTag $context.Tag",
            "Assert-ReleaseAssetsMatchLocal $context",
            "gh release edit $context.Tag --draft=false",
            "git push origin \"HEAD:refs/heads/$publicBranch\"",
            "Assert-RawReleaseFilesPublished $context");
    }

    [TestMethod]
    public void PublishScript_GeneratesPublicVersionTxtFromAssemblyVersion()
    {
        string publishScript = ReadRepositoryFile("scripts", "publish.ps1");

        StringAssert.Contains(publishScript, "Get-AppVersion");
        StringAssert.Contains(publishScript, "WriteAllText($publicVersionPath, $version, $utf8NoBom)");
        AssertNoLocalVersionTxtRead(publishScript, "publish.ps1");
        AssertNoVersionTxtCopy(publishScript, "publish.ps1");
    }

    private static void AssertNoLocalVersionTxtRead(string script, string scriptName)
    {
        Assert.IsFalse(
            Regex.IsMatch(script, @"(?im)\bGet-Content\b[^\r\n]*\bversion\.txt\b", RegexOptions.CultureInvariant),
            $"{scriptName} must not read version.txt as a version source.");
        Assert.IsFalse(
            Regex.IsMatch(script, @"(?im)\bTest-Path\b[^\r\n]*\bversion\.txt\b", RegexOptions.CultureInvariant),
            $"{scriptName} must not require version.txt as a release precondition.");
    }

    private static void AssertNoVersionTxtCopy(string script, string scriptName)
    {
        Assert.IsFalse(
            Regex.IsMatch(script, @"(?im)\bCopy-Item\b[^\r\n]*\bversion\.txt\b", RegexOptions.CultureInvariant),
            $"{scriptName} must generate public version.txt instead of copying the development repository file.");
        Assert.IsFalse(
            Regex.IsMatch(script, @"(?is)\$files\s*=\s*@\([^\)]*\bversion\.txt\b", RegexOptions.CultureInvariant),
            $"{scriptName} must not keep version.txt in the copied file list.");
    }

    private static string ExtractFunction(string script, string functionName)
    {
        string marker = "function " + functionName;
        int startIndex = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.AreNotEqual(-1, startIndex, $"Function was not found: {functionName}");

        int nextFunctionIndex = script.IndexOf("\nfunction ", startIndex + marker.Length, StringComparison.Ordinal);
        return nextFunctionIndex >= 0
            ? script.Substring(startIndex, nextFunctionIndex - startIndex)
            : script.Substring(startIndex);
    }

    private static void AssertContainsInOrder(string text, params string[] fragments)
    {
        int previousIndex = -1;
        foreach (string fragment in fragments)
        {
            int index = text.IndexOf(fragment, previousIndex + 1, StringComparison.Ordinal);
            Assert.AreNotEqual(-1, index, $"Expected fragment was not found after index {previousIndex}: {fragment}");
            previousIndex = index;
        }
    }

    private static string ReadRepositoryFile(params string[] relativePathParts)
    {
        return File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. relativePathParts]));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
