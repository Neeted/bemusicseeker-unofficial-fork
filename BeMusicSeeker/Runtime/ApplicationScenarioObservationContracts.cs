#nullable enable
using System;
using System.Collections;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json.Serialization;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Runtime;

/// <summary>UIから開始する一回の操作の期待対象です。業務要求や再実行権限を含みません。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ScenarioKeywordTarget), "keyword")]
[JsonDerivedType(typeof(ScenarioCellEditTarget), "cell-edit")]
[JsonDerivedType(typeof(ScenarioInstallTarget), "install")]
[JsonDerivedType(typeof(ScenarioPlaylistDropTarget), "playlist-drop")]
[JsonDerivedType(typeof(ScenarioPlaylistFolderDeleteTarget), "playlist-folder-delete")]
[JsonDerivedType(typeof(ScenarioPlaylistEntryDeleteTarget), "playlist-entry-delete")]
internal abstract record ScenarioObservationTarget;
/// <summary>途中入力を除外して、一回だけ捕捉する最終検索語です。</summary>
internal sealed record ScenarioKeywordTarget(string Keyword) : ScenarioObservationTarget;
/// <summary>既存AutomationIdで生存行を捕捉するMEMO/FOLDER編集です。</summary>
internal sealed record ScenarioCellEditTarget(string RowAutomationId, string PropertyName, string Text, bool Commit) : ScenarioObservationTarget;
/// <summary>実受付が確保した元パスと照合する導入対象です。</summary>
internal sealed record ScenarioInstallTarget(ImmutableArray<string> Paths) : ScenarioObservationTarget;
/// <summary>通常譜面の生存行とドロップ先です。FolderName=nullは表のrootです。</summary>
internal sealed record ScenarioPlaylistDropTarget(ImmutableArray<string> RowAutomationIds, int PlaylistId, string? FolderName) : ScenarioObservationTarget;
/// <summary>登録時の生存表・folderを固定するツリー削除対象です。</summary>
internal sealed record ScenarioPlaylistFolderDeleteTarget(int PlaylistId, string FolderName) : ScenarioObservationTarget;
/// <summary>表示位置ではなく登録時の生存entryを固定する削除対象です。</summary>
internal sealed record ScenarioPlaylistEntryDeleteTarget(ImmutableArray<string> RowAutomationIds) : ScenarioObservationTarget;
/// <summary>購読と対象捕捉をDispatcherで完了してから応答する登録です。</summary>
internal sealed record ScenarioRegisterObservationCommand(long RequestId, ScenarioObservationTarget Target) : ScenarioCommand(RequestId);
/// <summary>元登録の通信番号を参照して実終端を一回だけ消費します。</summary>
internal sealed record ScenarioAwaitObservationCommand(long RequestId, long RegistrationRequestId) : ScenarioCommand(RequestId);
/// <summary>入力前・Await前の観測だけを破棄します。業務Taskを取り消しません。</summary>
internal sealed record ScenarioCancelObservationCommand(long RequestId, long RegistrationRequestId) : ScenarioCommand(RequestId);
/// <summary>投影・選択取得なしで一覧の現在状態を読みます。</summary>
internal sealed record ScenarioTableStateCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>Attachから捕捉した最初の起動同期だけを一回消費します。</summary>
internal sealed record ScenarioInitialPlaylistSyncCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>登録時の表示状態です。登録番号はこの応答のRequestIdです。</summary>
internal sealed record ScenarioObservationRegistered(long RequestId, ScenarioTableState TableState) : ScenarioMessage(RequestId);
/// <summary>観測破棄の応答です。</summary>
internal sealed record ScenarioObservationCanceled(long RequestId, long RegistrationRequestId) : ScenarioMessage(RequestId);
/// <summary>同一業務Taskの実終端です。正常Taskを保存成功に読み替えません。</summary>
internal sealed record ScenarioObservationCompleted(long RequestId, long RegistrationRequestId, ScenarioObservedResult Result) : ScenarioMessage(RequestId);
/// <summary>一覧を実体化せず取得した状態です。</summary>
internal sealed record ScenarioTableStateCompleted(long RequestId, ScenarioTableState State) : ScenarioMessage(RequestId);
/// <summary>現在のcontext・source型・世代・件数です。行と選択の値は含みません。</summary>
internal sealed record ScenarioTableState(string OperationSection, string SourceScope, string SourceType, long RowsGeneration, int Count, int RealizedRowCount);
/// <summary>実Task終端と、該当する操作だけが持つ実結果です。TaskFailure=nullは保存成功を意味しません。</summary>
internal sealed record ScenarioObservedResult(ScenarioObservationTarget Target, ScenarioFailure? TaskFailure,
    ScenarioObservedKeyword? Keyword = null, ScenarioDropCompleted? Install = null, ImmutableArray<ScenarioObservedRow>? Rows = null);
/// <summary>業務呼出し直前に捕捉した行のdomain値です。完了後の位置から再解決しません。</summary>
internal sealed record ScenarioObservedRow(string Kind, string? Path, string? Md5, string? Sha256, int? PlaylistId, string? Folder);
/// <summary>検索Taskの実結果と終端時の採用表示を別々に保持します。</summary>
internal sealed record ScenarioObservedKeyword(long ViewRequestId, long RowsGeneration, bool WasCommitted, long DisplayRequestId, long DisplayRowsGeneration);
/// <summary>実起動同期のreceiptと、その外側Task終端を合わせた事実です。</summary>
internal sealed record ScenarioInitialPlaylistSyncCompleted(long RequestId, string Reason, int Version, bool Succeeded, bool WasSkipped,
    ScenarioFailure? TaskFailure) : ScenarioMessage(RequestId);

/// <summary>業務呼出し前の入力と表示を捕捉します。読取り専用の参照であり再実行機能はありません。</summary>
internal sealed record ScenarioUiOperationInput(string Family, IList Source, MainChartListOperationContext Context, long RowsGeneration,
    ImmutableArray<ScenarioUiRow> Rows, BMSTable? Table = null, PlaylistFolderNode? Folder = null, string? FolderName = null,
    int? PlaylistId = null, MainChartListCellEditEndedEventArgs? Edit = null)
{
    /// <summary>既存操作が既に取得した入力だけを複写し、追加の選択取得を行いません。</summary>
    internal static ScenarioUiOperationInput? Capture(MainWindowViewModel model, string family, ImmutableArray<object> rows,
        BMSTable? table = null, PlaylistFolderNode? folder = null, MainChartListCellEditEndedEventArgs? edit = null)
        => !model.HasScenarioOperationObservers ? null : new(family, model.MainChartList.Rows, model.MainChartList.CurrentOperationContext, model.MainChartList.RowsGeneration,
            [.. rows.Select(ScenarioUiRow.Capture)], table, folder, folder?.FolderName, table?.playlist_id, edit);
}
/// <summary>実受付の変更不能な値です。可変requestを観測者へ渡しません。</summary>
internal sealed record ScenarioInstallStarted(Guid BatchId, ImmutableArray<string> OriginalPaths);

/// <summary>元行の生存識別・親と、呼出し前のdomain値を分離して保持します。</summary>
internal sealed record ScenarioUiRow(object Identity, BMSTable? Parent, ScenarioObservedRow Facts)
{
    /// <summary>既存操作の行snapshotだけから入力値を捕捉します。</summary>
    internal static ScenarioUiRow Capture(object row) => row switch
    {
        PlaylistDetailRow detail => new(detail.Entry, detail.Entry.parent,
            new("PlaylistEntry", null, detail.Entry.md5, detail.Entry.sha256, detail.Entry.playlist_id, detail.Entry.folder)),
        LibraryChartRow chart => new(chart.Chart.Token, null,
            new(chart.Chart.Kind.ToString(), chart.Chart.Path, chart.Chart.Md5, chart.Chart.Sha256, null, null)),
        _ => new(row, null, new("Unsupported", null, null, null, null, null))
    };
}
