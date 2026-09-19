using System;
using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalProgramArgumentTemplateTests
{
    [TestMethod]
    public void ParseQuotedAndEmbeddedPlaceholdersPreservesExactTokens()
    {
        AssertExpanded(
            "--file=\"{filePath}\" \"--title=quoted value\"",
            @"C:\Songs\alpha chart.bms",
            @"--file=C:\Songs\alpha chart.bms",
            "--title=quoted value");
    }

    [TestMethod]
    public void ParseKeepsEmptyQuotedArgumentAndSingleQuotesAsLiteralCharacters()
    {
        AssertExpanded(
            "--empty \"\" '{filePath}'",
            @"C:\Songs\alpha.bms",
            "--empty",
            string.Empty,
            @"'C:\Songs\alpha.bms'");
    }

    [TestMethod]
    public void ParseHandlesBackslashBeforeQuoteUsingWindowsParityRules()
    {
        AssertExpanded(
            """odd\"{filePath} even\\"tail" suffix""",
            @"C:\Songs\alpha.bms",
            """odd"C:\Songs\alpha.bms""",
            @"even\tail",
            "suffix");
    }

    [TestMethod]
    public void ExpandPreservesUnicodeSpacesAndTrailingBackslash()
    {
        AssertExpanded(
            "--path=\"{filePath}\"",
            @"C:\譜面 データ\終端\",
            @"--path=C:\譜面 データ\終端\");
    }

    [TestMethod]
    public void ParseAllowsRepeatedFilePathPlaceholdersAndKeepsEachToken()
    {
        AssertExpanded(
            "--before={filePath} --after=\"{filePath}\"",
            @"C:\Songs\alpha chart.bms",
            @"--before=C:\Songs\alpha chart.bms",
            @"--after=C:\Songs\alpha chart.bms");
    }

    [TestMethod]
    public void ParseRejectsMissingUnknownAndUnbalancedPlaceholdersOrQuotes()
    {
        (string Source, ExternalProgramArgumentTemplateErrorKind Cause)[] invalidTemplates =
        [
            ("--file", ExternalProgramArgumentTemplateErrorKind.MissingFilePathPlaceholder),
            ("--file={FilePath}", ExternalProgramArgumentTemplateErrorKind.UnknownPlaceholder),
            ("--file={other}", ExternalProgramArgumentTemplateErrorKind.UnknownPlaceholder),
            ("--file={filePath", ExternalProgramArgumentTemplateErrorKind.UnbalancedPlaceholder),
            ("--file=filePath}", ExternalProgramArgumentTemplateErrorKind.UnbalancedPlaceholder),
            ("--file={filePath,{filePath} }", ExternalProgramArgumentTemplateErrorKind.UnbalancedPlaceholder),
            ("--file=\"{filePath}", ExternalProgramArgumentTemplateErrorKind.UnbalancedDoubleQuote)
        ];

        foreach ((string source, ExternalProgramArgumentTemplateErrorKind expectedCause) in invalidTemplates)
        {
            bool parsed = ExternalProgramArgumentTemplate.TryParse(
                source,
                out _,
                out ExternalProgramArgumentTemplateErrorKind actualCause,
                out string error);
            Assert.IsFalse(parsed, source);
            Assert.AreEqual(expectedCause, actualCause, source);
            Assert.IsFalse(string.IsNullOrWhiteSpace(error), source);
        }
    }

    [TestMethod]
    public void ParseRejectsNullSource()
    {
        Assert.IsFalse(ExternalProgramArgumentTemplate.TryParse(null, out _, out string error));
        Assert.IsFalse(string.IsNullOrWhiteSpace(error));
    }

    private static void AssertExpanded(string source, string filePath, params string[] expected)
    {
        Assert.IsTrue(
            ExternalProgramArgumentTemplate.TryParse(source, out ExternalProgramArgumentTemplate template, out string error),
            error);
        string[] actual = template.Expand(filePath).ToArray();
        CollectionAssert.AreEqual(expected, actual, "Actual tokens: " + string.Join(" | ", actual));
    }
}
