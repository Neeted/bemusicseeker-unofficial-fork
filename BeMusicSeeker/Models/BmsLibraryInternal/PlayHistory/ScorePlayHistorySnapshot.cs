namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>通常成績と同じ取得操作で固定した全履歴です。失敗・正常0件も保持し、表示操作ではDBを再取得しません。</summary>
internal sealed class ScorePlayHistorySnapshot
{
    /// <summary>今回選択した取得元と、その読取り結果を一組として所有します。行・ノーツ・player時系列は再計算しません。</summary>
    internal ScorePlayHistorySnapshot(ActiveScoreSource source, string scoreDbPath, Lr2PlayHistoryReadResult lr2 = null, BeatorajaPlayHistoryReadResult beatoraja = null)
    {
        Source = source;
        ScoreDbPath = scoreDbPath ?? string.Empty;
        Lr2 = lr2;
        Beatoraja = beatoraja;
    }
    /// <summary>通常成績と共通の取得元です。</summary>
    internal ActiveScoreSource Source { get; }
    /// <summary>取得時点の成績DBパスです。</summary>
    internal string ScoreDbPath { get; }
    /// <summary>LR2の用途和集合で読んだ全rawとスキーマ診断です。</summary>
    internal Lr2PlayHistoryReadResult Lr2 { get; }
    /// <summary>beatorajaの全rawとplayer時系列です。</summary>
    internal BeatorajaPlayHistoryReadResult Beatoraja { get; }
}
