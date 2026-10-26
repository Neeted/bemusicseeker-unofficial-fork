using System.Linq;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ExternalProgramArgumentTemplateTests
{
    /// <summary>引用符と埋込placeholderを展開して空白入りパスを一引数に保ち、固定文字列と引数境界を壊しません。</summary>
    [TestMethod]
    public void ParseQuotedAndEmbeddedPlaceholdersPreservesExactTokens()
    {
        AssertExpanded(
            "--file=\"{filePath}\" \"--title=quoted value\"",
            @"C:\Songs\alpha chart.bms",
            @"--file=C:\Songs\alpha chart.bms",
            "--title=quoted value");
    }

    /// <summary>空の二重引用引数と単引用文字を保持し、空引数の削除やUnix式引用としての誤解釈を防ぎます。</summary>
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

    /// <summary>引用内の二重引用符対を一文字として保持し、ラベルから引用符が欠落する誤りを検出します。</summary>
    [TestMethod]
    public void ParsePreservesDoubledQuoteInsideQuotedArgument()
    {
        AssertParsedAndExpanded(
            "--label \"A\"\"B\" \"{filePath}\"",
            ["--label", "A\"B", "{filePath}"],
            ["--label", "A\"B", @"C:\譜面 dir\alpha.bms"]);
    }

    /// <summary>引用内で連続する二組の引用符対と後続空白を保持し、途中で引用状態を抜ける誤りを検出します。</summary>
    [TestMethod]
    public void ParsePreservesConsecutiveDoubledQuotesAndFollowingSpace()
    {
        AssertParsedAndExpanded(
            "\"A\"\"\"\" B\" \"{filePath}\"",
            ["A\"\" B", "{filePath}"],
            ["A\"\" B", @"C:\譜面 dir\alpha.bms"]);
    }

    /// <summary>引用外の空の引用区間は文字を増やさず前後を連結し、全ての引用符対を文字へ変換する誤りを検出します。</summary>
    [TestMethod]
    public void ParseKeepsUnquotedEmptyQuoteSegmentWithoutLiteralQuote()
    {
        AssertParsedAndExpanded(
            "A\"\"B \"{filePath}\"",
            ["AB", "{filePath}"],
            ["AB", @"C:\譜面 dir\alpha.bms"]);
    }

    /// <summary>引用符四文字の一文字引数と二文字の空引数を区別し、文字や空引数の欠落を検出します。</summary>
    [TestMethod]
    public void ParseDistinguishesQuotedLiteralQuoteFromEmptyArgument()
    {
        AssertParsedAndExpanded(
            "\"\"\"\" \"\" \"{filePath}\"",
            ["\"", string.Empty, "{filePath}"],
            ["\"", string.Empty, @"C:\譜面 dir\alpha.bms"]);
    }

    /// <summary>引用内の偶数backslashを半数へ畳んだ後も引用符対を保持し、別分岐での文字欠落や引用未閉鎖の誤判定を検出します。</summary>
    [TestMethod]
    public void ParsePreservesDoubledQuoteAfterEvenBackslashes()
    {
        AssertParsedAndExpanded(
            "\"A\\\\\"\"B\" \"{filePath}\"",
            ["A\\\"B", "{filePath}"],
            ["A\\\"B", @"C:\譜面 dir\alpha.bms"]);
    }

    /// <summary>引用符直前のbackslashを偶奇で解釈し、Windows起動で渡す文字と引数境界を保持します。</summary>
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

    /// <summary>日本語・空白・末尾backslashのパスを一引数のまま展開し、escapeで末尾を欠落させません。</summary>
    [TestMethod]
    public void ExpandPreservesUnicodeSpacesAndTrailingBackslash()
    {
        AssertExpanded(
            "--path=\"{filePath}\"",
            @"C:\譜面 データ\終端\",
            @"--path=C:\譜面 データ\終端\");
    }

    /// <summary>同じplaceholderが複数引数にあっても各出現を展開し、引用の有無でパスを分割しません。</summary>
    [TestMethod]
    public void ParseAllowsRepeatedFilePathPlaceholdersAndKeepsEachToken()
    {
        AssertExpanded(
            "--before={filePath} --after=\"{filePath}\"",
            @"C:\Songs\alpha chart.bms",
            @"--before=C:\Songs\alpha chart.bms",
            @"--after=C:\Songs\alpha chart.bms");
    }

    /// <summary>placeholder欠落・未知名・括弧不整合・引用未閉鎖を別の失敗原因で返し、不正templateを起動可能にしません。</summary>
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

    /// <summary>未指定templateを診断付きで拒否し、空の正常引数へ置き換えません。</summary>
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

    private static void AssertParsedAndExpanded(string source, string[] expectedTokens, string[] expectedExpanded)
    {
        Assert.IsTrue(
            ExternalProgramArgumentTemplate.TryParse(source, out ExternalProgramArgumentTemplate template, out string error),
            error);
        CollectionAssert.AreEqual(expectedTokens, template.Tokens.ToArray(), source);
        CollectionAssert.AreEqual(expectedExpanded, template.Expand(@"C:\譜面 dir\alpha.bms").ToArray(), source);
    }
}
