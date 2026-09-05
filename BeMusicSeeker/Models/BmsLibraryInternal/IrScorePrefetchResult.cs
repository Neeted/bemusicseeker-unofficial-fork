using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>取得失敗を空の正常スコアと区別する終端分類です。</summary>
internal enum IrScoreFailure
{
    /// <summary>取得成功、または失敗なし。</summary>
    None,
    /// <summary>通信、解析などの失敗。</summary>
    Unavailable,
    /// <summary>本文受信までの期限超過。</summary>
    TimedOut,
    /// <summary>アプリケーション終了による中止。</summary>
    Cancelled
}

internal sealed class IrScorePrefetchResult
{
    /// <summary>再取得せず後続へ伝播する取得失敗の分類。</summary>
    public IrScoreFailure Failure { get; set; }
    public int Lr2Id { get; set; }

    public List<LR2IRScore> ScoreTable { get; } = [];

    public string ScoreDigestSha256 { get; set; }

    public long XmlFetchMs { get; set; }

    public long XmlParseMs { get; set; }

    public long DigestMs { get; set; }

    public int ParsedRows { get; set; }

    public string FailureReason { get; set; }

    /// <summary>正常に取得・解析できた結果だけを適用可能とします。</summary>
    public bool Succeeded => Failure == IrScoreFailure.None && string.IsNullOrWhiteSpace(FailureReason) && ScoreDigestSha256 != null;
}
