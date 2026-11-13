using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>起動の必須出力の結果です。LR2全体同期に必要な生成面と、出力を止めた表の不変の事実を返します。</summary>
/// <param name="PreparedSurface">LR2全体同期に必要な、完全な生成面。</param>
/// <param name="StoppedOutputs">必須出力が止めた表（衝突した表、未登録の追加基点を使う表）。受付を保持したまま通知せず、呼出元が受付の解放後に通知します。</param>
internal sealed record PlaylistStartupOutputsResult(
    Lr2SongDbSyncPreparedDataSurface PreparedSurface,
    IReadOnlyList<PlaylistStoppedOutput> StoppedOutputs);
