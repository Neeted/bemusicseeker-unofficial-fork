using System;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 推定の評価・適用・終端を、公開ライブラリAPIを増やさず型付き診断で観測します。
/// </summary>
internal interface IInstallEstimationExecutionObserver
{
    /// <summary>
    /// 一評価の設定と寿命を観測します。返したscopeは評価の成功・失敗を問わず終端で破棄します。
    /// </summary>
    /// <param name="observation">変更不能な一評価の設定。</param>
    /// <returns>同時評価の寿命を測るscope。</returns>
    IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation);

    /// <summary>
    /// 変更不能な進捗を観測します。
    /// </summary>
    /// <param name="observation">公開する進捗。</param>
    void ObserveProgress(InstallEstimationProgressObservation observation);

    /// <summary>
    /// 適用済みの評価結果を通知します。
    /// </summary>
    /// <param name="observation">同一操作内で適用した対象。</param>
    void ObserveResultApplied(InstallEstimationAppliedObservation observation);

    /// <summary>評価済み結果と適用の境界を観測し、評価Taskの寿命を診断します。</summary>
    void ObserveEvaluationCompleted(InstallEstimationAppliedObservation observation) { }

    /// <summary>取消・評価失敗を観測し、未適用兄弟の回収へ移ったことを通知します。</summary>
    void ObserveDispatchStopped() { }
}

/// <summary>
/// 一評価の実行並列度と対象識別を示します。
/// </summary>
internal readonly record struct InstallEstimationWorkItemObservation(
    PendingInstallEstimateBatchSource Source,
    int OrderIndex,
    string DisplayName,
    int WorkItemDegree,
    int CandidateEvaluationDegree);

/// <summary>
/// 推定の進捗公開値を示します。
/// </summary>
internal readonly record struct InstallEstimationProgressObservation(
    bool IsActive,
    InstallEstimationProgressSource Source,
    int TotalWorkCount,
    int CompletedWorkCount,
    string CurrentDisplayName);

/// <summary>一回の評価結果が同じ受付内で適用された対象を示します。</summary>
internal readonly record struct InstallEstimationAppliedObservation(
    PendingInstallEstimateBatchSource Source,
    int OrderIndex,
    string DisplayName);
