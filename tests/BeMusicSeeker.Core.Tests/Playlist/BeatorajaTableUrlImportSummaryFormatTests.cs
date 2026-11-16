using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

/// <summary>beatoraja の表URL取込みの要約の本文の形（件数の段落、警告・失敗の節、標本5件と「- ...」）を固定します。</summary>
/// <remarks>
/// 保証する範囲は、承認済みの既存の表示の形です。標本の数や節の順を仕様で変えるときに、期待値とともに退役させます。
/// 期待値はリソースと改行から組み立て、整形の実装を再利用しません。
/// </remarks>
[TestClass]
public sealed class BeatorajaTableUrlImportSummaryFormatTests
{
    private static readonly string NewLine = Environment.NewLine;

    /// <summary>
    /// 保証：失敗も警告もない要約は件数の一段落だけで、注意の種類にならない。
    /// 失敗がないのに節や空行を足す誤りと、注意の種類にする誤りを検出する。
    /// </summary>
    [TestMethod]
    public void FormatMessage_WithoutFailuresOrWarnings_IsOnlyTheCountParagraph()
    {
        var summary = new BeatorajaTableUrlImportSummary(
        [
            BeatorajaTableUrlImportOutcome.Existing(new Uri("https://example.com/a"), "A"),
            BeatorajaTableUrlImportOutcome.Imported(new Uri("https://example.com/b"), "B"),
            BeatorajaTableUrlImportOutcome.RestoredFromBmt(new Uri("https://example.com/c"), "C"),
        ]);

        Assert.AreEqual(string.Format(Resources.Beatoraja_table_url_import_result_summary_format, 1, 1, 1, 0, 0), summary.FormatMessage());
        Assert.IsFalse(summary.HasFailureOrWarning);
    }

    /// <summary>
    /// 保証：警告と失敗が混在する場合は、警告の節、失敗の節の順に、空行の後に見出しを置いて続け、各行の末尾も改行にする。
    /// 節の順の入替えと、例外メッセージの括弧の欠落を検出する。
    /// </summary>
    [TestMethod]
    public void FormatMessage_WithWarningsAndFailures_AppendsWarningSectionThenFailureSection()
    {
        var summary = new BeatorajaTableUrlImportSummary(
        [
            BeatorajaTableUrlImportOutcome.Imported(new Uri("https://example.com/a"), "A"),
            BeatorajaTableUrlImportOutcome.Warning(new Uri("https://example.com/a"), "A", new InvalidOperationException("reference failed")),
            BeatorajaTableUrlImportOutcome.Failed(new Uri("https://example.com/b"), new InvalidOperationException("load failed")),
        ]);

        string expected = string.Format(Resources.Beatoraja_table_url_import_result_summary_format, 0, 1, 0, 1, 1)
            + NewLine + NewLine + Resources.Beatoraja_table_url_import_result_warning_header + NewLine
            + "- A (reference failed)" + NewLine
            + NewLine + NewLine + Resources.Beatoraja_table_url_import_result_failed_header + NewLine
            + "- https://example.com/b (load failed)" + NewLine;
        Assert.AreEqual(expected, summary.FormatMessage());
        Assert.IsTrue(summary.HasFailureOrWarning);
    }

    /// <summary>
    /// 保証：標本は一つの節に最大5件で、6件以上なら最後に「- ...」を足す。5件ちょうどでは足さない。
    /// 標本の件数の取違えと、省略記号の有無の誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow(5, false)]
    [DataRow(6, true)]
    public void FormatMessage_LimitsSamplesToFivePerSection(int failureCount, bool ellipsis)
    {
        var summary = new BeatorajaTableUrlImportSummary(
            Enumerable.Range(1, failureCount).Select(index =>
                BeatorajaTableUrlImportOutcome.Failed(new Uri("https://example.com/f" + index), new InvalidOperationException("e" + index))));

        string expected = string.Format(Resources.Beatoraja_table_url_import_result_summary_format, 0, 0, 0, failureCount, 0)
            + NewLine + NewLine + Resources.Beatoraja_table_url_import_result_failed_header + NewLine
            + string.Concat(Enumerable.Range(1, 5).Select(index => "- https://example.com/f" + index + " (e" + index + ")" + NewLine))
            + (ellipsis ? "- ..." + NewLine : string.Empty);
        Assert.AreEqual(expected, summary.FormatMessage());
    }

    /// <summary>
    /// 保証：名前は表名、なければ URI の文字列、それもなければ入力の文字列の順に最初の値を使い、
    /// 空白だけの例外メッセージでは括弧を付けない。名前の代替順と括弧の条件の誤りを検出する。
    /// </summary>
    [TestMethod]
    public void FormatMessage_UsesTableNameThenUriThenRawUrlAndOmitsBlankExceptionMessage()
    {
        var summary = new BeatorajaTableUrlImportSummary(
        [
            BeatorajaTableUrlImportOutcome.Warning(new Uri("https://example.com/warn"), "Named", new InvalidOperationException("detail")),
            BeatorajaTableUrlImportOutcome.Failed(new Uri("https://example.com/by-uri"), new InvalidOperationException("  ")),
            BeatorajaTableUrlImportOutcome.Failed("not a url", new InvalidOperationException("bad input")),
        ]);

        string expected = string.Format(Resources.Beatoraja_table_url_import_result_summary_format, 0, 0, 0, 2, 1)
            + NewLine + NewLine + Resources.Beatoraja_table_url_import_result_warning_header + NewLine
            + "- Named (detail)" + NewLine
            + NewLine + NewLine + Resources.Beatoraja_table_url_import_result_failed_header + NewLine
            + "- https://example.com/by-uri" + NewLine
            + "- not a url (bad input)" + NewLine;
        Assert.AreEqual(expected, summary.FormatMessage());
    }
}
