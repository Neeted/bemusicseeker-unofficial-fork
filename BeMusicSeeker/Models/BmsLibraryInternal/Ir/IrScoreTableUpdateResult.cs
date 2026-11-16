using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScoreTableUpdateResult
{
    /// <summary>取得、DB読取りまたは保存の失敗分類。失敗結果は現在のスコアへ採用しません。</summary>
    public IrScoreFailure Failure { get; set; }
    /// <summary>適用可能な成績。正常空を含み、取得・DB読取り・保存失敗時はnullです。</summary>
    public List<LR2IRScore> ScoreTable { get; set; }

    public long XmlFetchMs { get; set; }

    public long XmlParseMs { get; set; }

    public long DigestMs { get; set; }

    public bool PrefetchUsed { get; set; }

    public long PrefetchWaitMs { get; set; }

    public long PrefetchXmlFetchMs { get; set; }

    public long PrefetchXmlParseMs { get; set; }

    public long PrefetchDigestMs { get; set; }

    public long DbLoadMs { get; set; }

    public long DbLockWaitMs { get; set; }

    public long DbReplaceMs { get; set; }

    public int ParsedRows { get; set; }

    public int LoadedRows { get; set; }

    /// <summary>対象IDの刷新履歴の保存が成功した場合だけtrueです。同内容・同metadataの無保存ではfalseです。</summary>
    public bool MetadataUpdated { get; set; }

    public bool Skipped { get; set; }

    public string SkipReason { get; set; } = "unavailable";

    /// <summary>正常空を含む、採用可能な成績がある場合だけ成功です。</summary>
    public bool Succeeded => ScoreTable != null;
}
