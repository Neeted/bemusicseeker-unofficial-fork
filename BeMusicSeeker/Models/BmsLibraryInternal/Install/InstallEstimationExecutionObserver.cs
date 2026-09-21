using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Receives typed diagnostics for one install-estimation execution without
/// becoming part of the public library API.
/// </summary>
internal interface IInstallEstimationExecutionObserver
{
    /// <summary>
    /// Starts observing one evaluation work item. The returned scope is
    /// disposed when the evaluation finishes, including faulted evaluations.
    /// </summary>
    /// <param name="observation">Immutable work-item configuration.</param>
    /// <returns>A scope used to measure active work-item concurrency.</returns>
    IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation);

    /// <summary>
    /// Observes one immutable progress snapshot.
    /// </summary>
    /// <param name="observation">The progress snapshot to publish.</param>
    void ObserveProgress(InstallEstimationProgressObservation observation);

    /// <summary>
    /// Observes the currentness facts immediately before an attempt is applied.
    /// </summary>
    /// <param name="observation">The attempt and currentness snapshot.</param>
    void ObserveAttemptEvaluated(InstallEstimationAttemptEvaluatedObservation observation);
}

/// <summary>
/// Describes the scheduling policy and identity of one install-estimation work item.
/// </summary>
internal readonly record struct InstallEstimationWorkItemObservation(
    PendingInstallEstimateBatchSource Source,
    int OrderIndex,
    string DisplayName,
    int WorkItemDegree,
    int CandidateEvaluationDegree);

/// <summary>
/// Describes one install-estimation progress publication.
/// </summary>
internal readonly record struct InstallEstimationProgressObservation(
    bool IsActive,
    InstallEstimationProgressSource Source,
    int TotalWorkCount,
    int CompletedWorkCount,
    string CurrentDisplayName);

/// <summary>
/// Describes the currentness state observed immediately before one apply attempt.
/// </summary>
internal readonly record struct InstallEstimationAttemptEvaluatedObservation(
    PendingInstallEstimateBatchSource Source,
    int OrderIndex,
    string DisplayName,
    int Attempt,
    PendingInstallEstimateCurrentnessStamp CurrentnessStamp);
