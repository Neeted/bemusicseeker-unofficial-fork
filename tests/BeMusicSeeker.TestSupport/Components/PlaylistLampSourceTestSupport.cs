namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistLampSourceTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static void InsertLr2SourceHistory(SQLite.SQLiteConnection db, string hash, DateTime date, int oldExScore)
        // LR2保存形式のHARDは4です。アプリ内ClearTypeの値6と区別します。
        => db.Execute("INSERT INTO bms_lr2_play_history (hash, played_at, finalized, score_write_type, old_playcount, new_playcount, playcount_delta, old_clear, old_op_history, old_exscore, old_totalnotes) VALUES (?, ?, 1, 'update', 2, 3, 1, 4, 0, ?, 100)", hash, LocalNoonUnixSeconds(date), oldExScore);

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static long LocalNoonUnixSeconds(DateTime date)
        => new DateTimeOffset(DateTime.SpecifyKind(date.Date.AddHours(12), DateTimeKind.Local)).ToUnixTimeSeconds();

}
