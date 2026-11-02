#nullable enable
using System;
using System.Collections.Immutable;

namespace BeMusicSeeker.Runtime;

/// <summary>通常Dropと同じ入力確保・受付へ渡す実パスです。</summary>
internal sealed record ScenarioDropCommand(long RequestId, ImmutableArray<string> Paths) : ScenarioCommand(RequestId);
/// <summary>通常の保留または新規導入一覧へ移動し、実表示を取得します。</summary>
internal sealed record ScenarioPackageViewCommand(long RequestId, bool Pending) : ScenarioCommand(RequestId);
/// <summary>実保留行から選ぶ対象と既存の導入先操作です。ExpectInstallFailureReportは消失した明示先の実報告に限る一回OK入力です。</summary>
internal sealed record ScenarioPendingCommand(long RequestId, ScenarioPendingAction Action,
    ImmutableArray<string> ChartPaths, string? Destination = null, bool ExpectInstallFailureReport = false) : ScenarioCommand(RequestId);
/// <summary>保留一覧で提供する固定操作だけを列挙します。</summary>
internal enum ScenarioPendingAction
{
    /// <summary>選択対象の導入先を解除します。</summary>
    Clear,
    /// <summary>明示的に導入先を推定します。</summary>
    Search,
    /// <summary>実在する既知の所持先を指定します。</summary>
    Set,
    /// <summary>通常の手動導入と必要表示を行います。</summary>
    Install,
    /// <summary>通常の新規インストールを明示的に実行します。</summary>
    InstallAsNew
}

/// <summary>同じ受付BatchIdの実終端です。初段と推定後の確定事実を分離します。</summary>
internal sealed record ScenarioDropCompleted(long RequestId, Guid BatchId, long Generation,
    ScenarioInstallResult? Result, ScenarioFailure? Failure) : ScenarioMessage(RequestId);
/// <summary>明示的な一覧移動の実表示終端です。</summary>
internal sealed record ScenarioPackageViewCompleted(long RequestId, bool Pending, long RowsGeneration,
    ImmutableArray<ScenarioPackageChart> Rows) : ScenarioMessage(RequestId);
/// <summary>実保留操作後に通常一覧へ再移動した観測です。導入以外はMutationを生成しません。</summary>
internal sealed record ScenarioPendingCompleted(long RequestId, ScenarioPendingAction Action,
    ScenarioPendingMutation? Mutation, ScenarioPackageViewCompleted View) : ScenarioMessage(RequestId);
/// <summary>実PackageEntryから読み取った譜面と導入先です。</summary>
internal sealed record ScenarioPackageChart(ScenarioChart Chart, string? Destination,
    ImmutableArray<string> Suggestions, ImmutableArray<string> WarningKinds);
/// <summary>元の導入要求結果から値を写します。正常Taskや確定だけを成功へ読み替えません。</summary>
internal sealed record ScenarioInstallResult(ScenarioMutationReceipt Initial, ScenarioMutationReceipt? Estimated,
    bool HasRequiredFailure, bool SynchronizationIncomplete, int UnprocessedPackageCount,
    ImmutableArray<ScenarioFailure> Failures);
/// <summary>手動導入の既存結果をそのまま伝え、受付拒否と失敗を区別します。</summary>
internal sealed record ScenarioPendingMutation(bool Succeeded, bool HasInstallRefusal, ScenarioFailure? Failure,
    ScenarioMutationReceipt? Session);
/// <summary>一操作の確定・未処理・故障と回復状態を通信に固定します。</summary>
internal sealed record ScenarioMutationReceipt(bool DurableCommit, bool HasRequiredFailure,
    bool HasUnrestoredExistingDestination, bool ManualRecoveryRequired, bool CompletedWithCleanupFailure,
    int DestinationConflictCount, ImmutableArray<ScenarioMutationTarget> ConfirmedTargets,
    ImmutableArray<ScenarioMutationTarget> UnprocessedTargets, ImmutableArray<ScenarioFailure> Failures);
/// <summary>候補と混同しない、既存receiptの元パスと宛先の組です。</summary>
internal sealed record ScenarioMutationTarget(string SourcePath, string DestinationPath);
