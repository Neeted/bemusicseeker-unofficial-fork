#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    private async Task<ScenarioDropCompleted> DropAsync(MainWindowViewModel model, ScenarioDropCommand request)
    {
        var terminal = new TaskCompletionSource<ScenarioDropCompleted>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Terminated(PackageInstallBatchTerminal result)
        {
            try
            {
                if (result.CommandResult is null && result.Failure is null)
                { throw new InvalidOperationException("The drop terminated without an executed result."); }
                terminal.TrySetResult(new ScenarioDropCompleted(
                    request.RequestId, result.BatchId, result.Generation, CaptureInstall(result.CommandResult),
                    result.Failure is null ? null : ScenarioFailure.FromException(result.Failure)));
            }
            catch (Exception failure)
            {
                // 観測値の変換失敗も要求へ返し、通知側の例外隔離で待機を残しません。
                terminal.TrySetException(failure);
            }
        }
        model.PackageInstallWorkflow.BatchTerminated += Terminated;
        try
        {
            DroppedInstallIngressAcquisitionResult acquired = model.PackageInstallWorkflow.AcquireAndTryEnqueueDroppedPaths(request.Paths);
            if (!acquired.Succeeded) { throw acquired.Exception ?? new InvalidOperationException("The drop was not admitted."); }
            Guid batchId = acquired.Request.BatchId;
            // idleだけでは成功にせず、同じ受付要求の実receiptと入力回収を両方待ちます。
            await Task.WhenAll(terminal.Task, model.PackageInstallWorkflow.WaitForIdleAsync()).WaitAsync(stopping.Token);
            ScenarioDropCompleted result = await terminal.Task;
            if (result.BatchId != batchId) { throw new InvalidOperationException("A different drop batch completed during observation."); }
            return result;
        }
        finally
        {
            model.PackageInstallWorkflow.BatchTerminated -= Terminated;
        }
    }

    private async Task<ScenarioPackageViewCompleted> NavigatePackagesAsync(MainWindowViewModel model, long requestId, bool pending)
    {
        await model.RegularChartList.NavigateInstallAsync(pending
            ? MainViewUpdateMode.PendingInstallFolderSelected : MainViewUpdateMode.NewlyInstalledFolderSelected).WaitAsync(stopping.Token);
        ChartOperationSourceScope scope = pending ? ChartOperationSourceScope.PendingPackage : ChartOperationSourceScope.NewlyInstalledPackage;
        ImmutableArray<ScenarioPackageChart>.Builder rows = ImmutableArray.CreateBuilder<ScenarioPackageChart>();
        foreach (object row in model.MainChartList.Rows)
        {
            if (!GridRowResolver.TryGetChartOperationTarget(row, scope, out ChartOperationTarget target))
            { throw new InvalidDataException("A package view row did not resolve to its operation target."); }
            rows.Add(new ScenarioPackageChart(CaptureChart(target.Chart), target.Chart.InstallDestination,
                [.. target.Chart.InstallDestinationSuggestions ?? []], [.. target.Chart.Warnings.Select(warning => warning.Kind.ToString())]));
        }
        return new ScenarioPackageViewCompleted(requestId, pending, model.MainChartList.RowsGeneration, rows.ToImmutable());
    }

    private async Task<ScenarioPendingCompleted> ApplyPendingAsync(MainWindowViewModel model, ScenarioPendingCommand request)
    {
        await NavigatePackagesAsync(model, request.RequestId, true);
        var targets = new List<ChartOperationTarget>();
        foreach (string path in request.ChartPaths)
        {
            ChartOperationTarget? selected = null;
            foreach (object row in model.MainChartList.Rows)
            {
                if (GridRowResolver.TryGetChartOperationTarget(row, ChartOperationSourceScope.PendingPackage, out ChartOperationTarget candidate)
                    && string.Equals(candidate.Chart.Path, path, StringComparison.OrdinalIgnoreCase))
                {
                    if (selected is not null) { throw new InvalidDataException("The pending chart identity is ambiguous."); }
                    selected = candidate;
                }
            }
            targets.Add(selected ?? throw new InvalidOperationException("The pending chart is no longer displayed: " + path));
        }
        if (targets.Count == 0) { throw new InvalidOperationException("At least one pending chart is required."); }
        ScenarioPendingMutation? mutation = null;
        switch (request.Action)
        {
            case ScenarioPendingAction.Clear:
                if (!PendingInstallDestinationClearRequest.TryCreate(targets, out PendingInstallDestinationClearRequest clear))
                { throw new InvalidOperationException("The selected pending charts cannot be cleared."); }
                await model.PendingPackages.ClearPendingAsync(clear).WaitAsync(stopping.Token);
                break;
            case ScenarioPendingAction.Search:
                await model.PendingPackages.SearchPendingAsync(PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch(targets))
                    .WaitAsync(stopping.Token);
                break;
            case ScenarioPendingAction.Set:
                if (targets.Count != 1 || string.IsNullOrWhiteSpace(request.Destination)
                    || !PendingInstallDestinationEditRequest.TryCreate(targets[0], out PendingInstallDestinationEditRequest edit))
                { throw new InvalidOperationException("One pending chart and a destination are required for the cell edit."); }
                await model.PendingPackages.SetPendingAsync(edit, request.Destination).WaitAsync(stopping.Token);
                break;
            case ScenarioPendingAction.Install:
            case ScenarioPendingAction.InstallAsNew:
                MainWindow mainWindow = window ?? throw new InvalidOperationException("The main window is unavailable.");
                PendingPackageMutationResult result = await (request.Action == ScenarioPendingAction.InstallAsNew
                    ? mainWindow.ForceInstallSelectedPendingChartsAsync(targets)
                    : mainWindow.ManualInstallSelectedPendingChartsAsync(targets)).WaitAsync(stopping.Token);
                mutation = new(result.Succeeded, result.HasInstallRefusal,
                    result.Failure is null ? null : ScenarioFailure.FromException(result.Failure), CaptureMutation(result.SessionReceipt));
                break;
            default: throw new InvalidDataException("Unsupported pending operation.");
        }
        // Clear/SearchのTaskは画面適用receiptではないため、通常の一覧移動を独立した操作として待ちます。
        return new ScenarioPendingCompleted(request.RequestId, request.Action, mutation,
            await NavigatePackagesAsync(model, request.RequestId, true));
    }

    private static ScenarioInstallResult? CaptureInstall(PackageInstallCommandResult? result) => result is null ? null : new(
        CaptureMutation(result.SessionReceipt) ?? throw new InvalidDataException("The install result has no initial receipt."),
        CaptureMutation(result.EstimatedInstallReceipt), result.HasRequiredFailure, result.SynchronizationIncomplete,
        result.UnprocessedPackages.Count,
        [.. result.WorkflowFailures.Concat(result.InputCleanupFailures.Select(failure => failure.Failure))
            .Concat(result.EstimationFailure is null ? [] : new[] { result.EstimationFailure }).Select(ScenarioFailure.FromException)]);

    private static ScenarioMutationReceipt? CaptureMutation(LibraryMutationSessionReceipt? receipt) => receipt is null ? null : new(
        receipt.DurableCommit, receipt.HasRequiredFailure, receipt.HasUnrestoredExistingDestination,
        receipt.ManualRecoveryRequired, receipt.CompletedWithCleanupFailure, receipt.DestinationTypeConflicts.Count,
        [.. receipt.ConfirmedTargets.Select(target => new ScenarioMutationTarget(target.SourcePath, target.DestinationPath))],
        [.. receipt.UnprocessedTargets.Select(target => new ScenarioMutationTarget(target.SourcePath, target.DestinationPath))],
        [.. new[] { receipt.PhysicalFailure, receipt.ApplyFailure, receipt.FinalizationFailure, receipt.CleanupFailure }
            .OfType<Exception>().Concat(receipt.ItemFailures.Select(item => item.Failure)).Select(ScenarioFailure.FromException)]);
}
