#nullable enable
using System;
using System.Collections.Immutable;
using System.Text.Json.Serialization;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Runtime;

/// <summary>明示test sessionから既存操作へ送る固定要求です。番号は通信の相関だけに使います。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "command")]
[JsonDerivedType(typeof(ScenarioSnapshotCommand), "snapshot")]
[JsonDerivedType(typeof(ScenarioKeywordCommand), "keyword")]
[JsonDerivedType(typeof(ScenarioReloadCommand), "reload")]
[JsonDerivedType(typeof(ScenarioCloseCommand), "close")]
[JsonDerivedType(typeof(ScenarioDropCommand), "drop")]
[JsonDerivedType(typeof(ScenarioPackageViewCommand), "package-view")]
[JsonDerivedType(typeof(ScenarioPendingCommand), "pending")]
[JsonDerivedType(typeof(ScenarioRegisterObservationCommand), "observe-register")]
[JsonDerivedType(typeof(ScenarioAwaitObservationCommand), "observe-await")]
[JsonDerivedType(typeof(ScenarioCancelObservationCommand), "observe-cancel")]
[JsonDerivedType(typeof(ScenarioTableStateCommand), "table-state")]
[JsonDerivedType(typeof(ScenarioInitialPlaylistSyncCommand), "initial-playlist-sync")]
internal abstract record ScenarioCommand(long RequestId);

/// <summary>現在の実catalogと表示を読取り、変更不能な値として返します。</summary>
internal sealed record ScenarioSnapshotCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>通常の検索setterへ渡す入力です。行を直接作りません。</summary>
internal sealed record ScenarioKeywordCommand(long RequestId, string Keyword) : ScenarioCommand(RequestId);
/// <summary>通常の差分リロードを要求します。</summary>
internal sealed record ScenarioReloadCommand(long RequestId) : ScenarioCommand(RequestId);
/// <summary>実MainWindowのCloseを要求します。受付をprocess終了成功とは扱いません。</summary>
internal sealed record ScenarioCloseCommand(long RequestId) : ScenarioCommand(RequestId);

/// <summary>実処理の結果と観測事実だけを伝える固定応答です。</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "message")]
[JsonDerivedType(typeof(ScenarioConnected), "connected")]
[JsonDerivedType(typeof(ScenarioStartupCompleted), "startup")]
[JsonDerivedType(typeof(ScenarioSnapshotCompleted), "snapshot")]
[JsonDerivedType(typeof(ScenarioKeywordCompleted), "keyword")]
[JsonDerivedType(typeof(ScenarioReloadCompleted), "reload")]
[JsonDerivedType(typeof(ScenarioCloseRequested), "close")]
[JsonDerivedType(typeof(ScenarioCommandFailed), "failure")]
[JsonDerivedType(typeof(ScenarioDropCompleted), "drop")]
[JsonDerivedType(typeof(ScenarioPackageViewCompleted), "package-view")]
[JsonDerivedType(typeof(ScenarioPendingCompleted), "pending")]
[JsonDerivedType(typeof(ScenarioObservationRegistered), "observe-registered")]
[JsonDerivedType(typeof(ScenarioObservationCanceled), "observe-canceled")]
[JsonDerivedType(typeof(ScenarioObservationCompleted), "observe-completed")]
[JsonDerivedType(typeof(ScenarioTableStateCompleted), "table-state")]
[JsonDerivedType(typeof(ScenarioInitialPlaylistSyncCompleted), "initial-playlist-sync")]
internal abstract record ScenarioMessage(long RequestId);

/// <summary>pipe接続だけの通知です。初期化・操作可能の通知ではありません。</summary>
internal sealed record ScenarioConnected(int ProcessId) : ScenarioMessage(0);
/// <summary>ContentRenderedから始めた実初期化の結果です。</summary>
internal sealed record ScenarioStartupCompleted(ScenarioInitializationResult Result) : ScenarioMessage(0);
/// <summary>指定要求で読んだ実データです。</summary>
internal sealed record ScenarioSnapshotCompleted(long RequestId, ScenarioLibrarySnapshot Snapshot) : ScenarioMessage(RequestId);
/// <summary>検索要求の実終端と採用された表示世代です。</summary>
internal sealed record ScenarioKeywordCompleted(long RequestId, string Keyword, long ViewRequestId, long RowsGeneration,
    ScenarioLibrarySnapshot Snapshot) : ScenarioMessage(RequestId);
/// <summary>差分操作の受付・実初期化結果と必要表示の終端後のデータです。</summary>
internal sealed record ScenarioReloadCompleted(long RequestId, ScenarioInitializationResult? Result,
    ScenarioLibrarySnapshot Snapshot) : ScenarioMessage(RequestId);
/// <summary>Closeを実Windowへ配送した事実です。最終終了は所有processから別に観測します。</summary>
internal sealed record ScenarioCloseRequested(long RequestId) : ScenarioMessage(RequestId);
/// <summary>当該要求の実例外です。起動Taskの例外はRequestId=0で通知します。</summary>
internal sealed record ScenarioCommandFailed(long RequestId, ScenarioFailure Failure) : ScenarioMessage(RequestId);

/// <summary>既存初期化結果の保存・公開事実を維持し、通信へ渡します。</summary>
internal sealed record ScenarioInitializationResult(StartupInitializationOutcome Outcome, long OperationToken,
    bool CompletionPublished, bool IsInitializationCompleted, ScenarioFailure? Failure);
/// <summary>例外の型・原因・stackを診断へ渡し、正常な結果へ読み替えません。</summary>
internal sealed record ScenarioFailure(string Type, string Message, string? StackTrace)
{
    /// <summary>観測した実例外を通信可能な診断へ変換します。</summary>
    internal static ScenarioFailure FromException(Exception failure) =>
        new(failure.GetType().FullName ?? failure.GetType().Name, failure.Message, failure.ToString());
}
/// <summary>実catalogと実表示の別々の集合を保持します。</summary>
internal sealed record ScenarioLibrarySnapshot(ImmutableArray<ScenarioChart> Catalog, ImmutableArray<ScenarioChart> Visible,
    long ViewRequestId, long RowsGeneration);
/// <summary>譜面の実identityと表示・利用者列の読取り値です。</summary>
internal sealed record ScenarioChart(string Path, string Md5, string Sha256, string Title, string Format,
    string? Tag, int? Favorite, int? AddDate);
