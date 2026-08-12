using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        StringAssert.Contains(releaseScript, "Assert-RemoteBranchMatchesLocalHead");
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
            "Assert-RemoteBranchMatchesLocalHead");
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

    [TestMethod]
    public void ReleaseHistory_UsesReleaseNotesWindowAsVersionSourceAndAboutStaysCompact()
    {
        string assemblyInfo = ReadRepositoryFile("Properties", "AssemblyInfo.cs");
        string releaseNotes = ReadRepositoryFile("BeMusicSeeker", "Views", "ReleaseNotesWindow.xaml");
        string about = ReadRepositoryFile("BeMusicSeeker", "Views", "Settings", "Pages", "AboutSettingsPage.xaml");
        string agents = ReadRepositoryFile("AGENTS.md");
        Match version = Regex.Match(assemblyInfo, "AssemblyInformationalVersion\\(\\\"([^\\\"]+)\\\"\\)");

        Assert.IsTrue(version.Success);
        StringAssert.Contains(releaseNotes, ">" + version.Groups[1].Value + ":<");
        StringAssert.Contains(releaseNotes, "0.1.0.0:");
        Assert.IsTrue(Regex.Matches(releaseNotes, @"<Paragraph FontWeight=\""Bold\"">[0-9]+(?:\.[0-9]+){3}:</Paragraph>").Count >= 20);
        Assert.IsFalse(about.Contains("FlowDocument", StringComparison.Ordinal));
        Assert.IsFalse(about.Contains("0.1.0.0:", StringComparison.Ordinal));
        StringAssert.Contains(agents, "BeMusicSeeker\\Views\\ReleaseNotesWindow.xaml");
        Assert.IsFalse(agents.Contains("BeMusicSeeker\\Views\\SettingsWindow.xaml`\n   - `Update_history", StringComparison.Ordinal));
        _ = XDocument.Parse(releaseNotes);
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
