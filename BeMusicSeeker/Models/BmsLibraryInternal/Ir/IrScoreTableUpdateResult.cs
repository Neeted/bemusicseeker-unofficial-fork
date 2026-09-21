using System.Collections.Generic;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class IrScoreTableUpdateResult
{
    /// <summary>DB と live score を保持した取得失敗の分類。</summary>
    public IrScoreFailure Failure { get; set; }
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

    public bool MetadataUpdated { get; set; }

    public bool Skipped { get; set; }

    public string SkipReason { get; set; } = "unavailable";

    public bool Succeeded => ScoreTable != null;
}
