using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// アプリ内で有効化されている score source を表します。
/// </summary>
internal enum ActiveScoreSource
{
    None,
    Lr2,
    Beatoraja
}

/// <summary>
/// Score table のロード結果を、空のテーブルとロード失敗を区別して表します。
/// </summary>
internal enum ScoreTableLoadStatus
{
    /// <summary>
    /// 利用可能な score source が設定されていません。
    /// </summary>
    NotConfigured,

    /// <summary>
    /// 選択された score source のロードが完了しました。
    /// </summary>
    Loaded,

    /// <summary>
    /// 選択された score source のロードに失敗しました。
    /// </summary>
    Failed
}

internal sealed class ScoreTableLoadResult
{
    /// <summary>
    /// ロードできた LR2 score 行です。空であっても <see cref="Status"/> が <see cref="ScoreTableLoadStatus.Loaded"/> ならロード成功を表します。
    /// </summary>
    public List<BMSScore> Scores { get; } = [];

    /// <summary>
    /// ロードできた beatoraja score 行を SHA256 で引いた辞書です。
    /// </summary>
    public Dictionary<string, BMSScore> BeatorajaScoresBySha256 { get; } = new Dictionary<string, BMSScore>(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// ロード処理が選択した score source です。失敗時も選択された source を保持します。
    /// </summary>
    public ActiveScoreSource ActiveScoreSource { get; set; }

    /// <summary>
    /// score table のロード状態です。
    /// </summary>
    public ScoreTableLoadStatus Status { get; set; } = ScoreTableLoadStatus.NotConfigured;

    /// <summary>
    /// ロード失敗時の診断メッセージです。成功時と未設定時は空文字列です。
    /// </summary>
    public string FailureMessage { get; set; } = string.Empty;

    public int LR2Id { get; set; }

    public bool EnableDownloadLr2IrScoreAndDetectUnsent { get; set; }

    public bool ReadOnly { get; set; }

    public long DbLockWaitMs { get; set; }

    public Lr2PlayHistorySchemaCheckResult Lr2PlayHistorySchemaCheckResult { get; set; }
}
