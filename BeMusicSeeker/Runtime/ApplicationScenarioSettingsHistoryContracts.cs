#nullable enable
using System.Collections.Immutable;

namespace BeMusicSeeker.Runtime;

/// <summary>実設定画面で操作する既知の節と確定・取消の組です。</summary>
internal enum ScenarioSettingsSectionAction { Inspect, AddRootsCancel, AddRootsCommit, OutputCancel, OutputCommit, DetailsCancel, DetailsRestart, InstallHistorySchema, BeatorajaRootCommit, BeatorajaScoreCommit, Lr2ScoreCommit }
/// <summary>節の実入力です。任意propertyや任意methodの呼出しを許しません。</summary>
internal sealed record ScenarioSettingsSectionCommand(long RequestId, ScenarioSettingsSectionAction Action,
    string Path = "", string ConfigPath = "") : ScenarioCommand(RequestId);
/// <summary>環境変更が実再起動確認に到達するまで待ちます。</summary>
internal sealed record ScenarioEnvironmentConfirmationCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>環境の実確認への肯定入力です。ACKを書き終えた後に回答を解放します。</summary>
internal sealed record ScenarioEnvironmentAnswerCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>候補採用と節の終端を実設定・fresh provider・実表示から観測した結果です。</summary>
internal sealed record ScenarioSettingsSectionCompleted(long RequestId, ScenarioSettingsSectionObservation Settings,
    ScenarioLibrarySnapshot Library) : ScenarioMessage(RequestId);
/// <summary>設定画面の候補・閉鎖後の公開値・新providerの読取り値を別々に保持します。</summary>
internal sealed record ScenarioSettingsSectionObservation(ImmutableArray<string> CandidateRoots, string CandidateOutput,
    string CandidateSongDb, string CandidateConfig, ImmutableArray<string> Roots, string Output, string SongDb,
    string Config, double FontSize, string SavedRoots, string SavedOutput, string SavedSongDb, string SavedConfig);

/// <summary>履歴の実期間選択、検索、成績再取得、通常・重複一覧移動、ランプ表示の固定入力です。</summary>
internal enum ScenarioHistoryAction { ReloadScores, PeriodDay, Keyword, Library, Duplicates, Lamp, PeriodRecent7Days }
/// <summary>履歴の通常入口へ渡す一回の入力です。期間日はローカル日付、最近7日の基準は外部書込み後に観測したUnix秒です。</summary>
internal sealed record ScenarioHistoryCommand(long RequestId, ScenarioHistoryAction Action, string Text = "", int TableId = 0) : ScenarioCommand(RequestId);
/// <summary>実表示Taskの要求識別と採用後の履歴・集計、実Windowのランプ結果です。</summary>
internal sealed record ScenarioHistoryCompleted(long RequestId, long ViewRequestId, long KeywordRevision,
    string Provider, ImmutableArray<ScenarioHistoryRow> Rows, long FinalizedRows, int ClearUpdateCount,
    bool PlayCountAvailable, bool PlaytimeAvailable, long PlaytimeSeconds, ImmutableArray<string> Paths,
    string LampState, int LampTotal, int? LampPlayed, ImmutableArray<ScenarioLampSegment> LampSegments) : ScenarioMessage(RequestId);
/// <summary>実履歴行のraw identity、所持解決と成績差分です。</summary>
internal sealed record ScenarioHistoryRow(string Provider, string SourcePath, long HistoryId, long PlayedAt,
    string Hash, string? Path, bool Finalized, int PlaycountDelta, int? NewExscore, int? NewBp, string? NewClear);
/// <summary>実ランプ集計の区分と件数です。</summary>
internal sealed record ScenarioLampSegment(string Category, int Count);
