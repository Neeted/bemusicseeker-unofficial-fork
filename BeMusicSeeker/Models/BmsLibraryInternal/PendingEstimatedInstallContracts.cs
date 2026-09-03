using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PendingEstimatedInstallCatalogPreparation
{
    internal IReadOnlyList<string> AffectedDirectories { get; init; } = [];

    internal ChartScanResult DirectoryScan { get; init; }
}

internal sealed class PendingEstimatedInstallPostGuardResult(
    int affectedCount,
    ExceptionDispatchInfo failure = null)
{
    internal int AffectedCount { get; } = affectedCount;

    internal void ThrowIfFailed()
    {
        failure?.Throw();
    }

}

internal sealed class EstimatedInstallDeferredFeedback
{
    private readonly List<EstimatedInstallFeedbackNotification> notifications = [];

    private readonly BufferedDialogService dialogService;

    internal EstimatedInstallDeferredFeedback()
    {
        dialogService = new BufferedDialogService(this);
    }

    internal IBmsLibraryDialogService DialogService => dialogService;

    internal void LogInstallPerformance(string message)
    {
        notifications.Add(EstimatedInstallFeedbackNotification.PerformanceLog(message));
    }

    internal void LogInstallWarning(Exception exception, string message)
    {
        notifications.Add(EstimatedInstallFeedbackNotification.WarningLog(exception, message));
    }

    internal void DeferDiagnosticEffect(Action effect)
    {
        if (effect != null)
        {
            notifications.Add(EstimatedInstallFeedbackNotification.DeferredAction(effect));
        }
    }

    internal void ShowEstimatedCleanupOnlyCompletedWarning(int cleanupOnlySucceeded)
    {
        notifications.Add(EstimatedInstallFeedbackNotification.CleanupOnlyWarning(cleanupOnlySucceeded));
    }

    internal IReadOnlyList<EstimatedInstallFeedbackNotification> DrainNotifications()
    {
        EstimatedInstallFeedbackNotification[] current = [.. notifications];
        notifications.Clear();
        return current;
    }

    private sealed class BufferedDialogService(EstimatedInstallDeferredFeedback owner) : IBmsLibraryDialogService
    {
        private readonly EstimatedInstallDeferredFeedback owner = owner;

        public UiDialogDefaultResult Show(
            string messageBoxText,
            string caption,
            UiDialogButton button,
            UiDialogIcon icon,
            UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            owner.notifications.Add(EstimatedInstallFeedbackNotification.Dialog(
                messageBoxText,
                caption,
                button,
                icon,
                defaultResult));
            return defaultResult;
        }
    }
}

internal enum EstimatedInstallFeedbackKind
{
    Dialog,
    CleanupOnlyWarning,
    PerformanceLog,
    WarningLog,
    DeferredAction
}

internal sealed class EstimatedInstallFeedbackNotification
{
    private EstimatedInstallFeedbackNotification(EstimatedInstallFeedbackKind kind)
    {
        Kind = kind;
    }

    internal EstimatedInstallFeedbackKind Kind { get; }

    internal string Message { get; private init; }

    internal string Caption { get; private init; }

    internal UiDialogButton Button { get; private init; }

    internal UiDialogIcon Icon { get; private init; }

    internal UiDialogDefaultResult DefaultResult { get; private init; }

    internal int CleanupOnlySucceeded { get; private init; }

    internal Exception Exception { get; private init; }

    internal Action Action { get; private init; }

    internal static EstimatedInstallFeedbackNotification Dialog(
        string message,
        string caption,
        UiDialogButton button,
        UiDialogIcon icon,
        UiDialogDefaultResult defaultResult)
    {
        return new EstimatedInstallFeedbackNotification(EstimatedInstallFeedbackKind.Dialog)
        {
            Message = message,
            Caption = caption,
            Button = button,
            Icon = icon,
            DefaultResult = defaultResult
        };
    }

    internal static EstimatedInstallFeedbackNotification CleanupOnlyWarning(int cleanupOnlySucceeded)
    {
        return new EstimatedInstallFeedbackNotification(EstimatedInstallFeedbackKind.CleanupOnlyWarning)
        {
            CleanupOnlySucceeded = cleanupOnlySucceeded
        };
    }

    internal static EstimatedInstallFeedbackNotification PerformanceLog(string message)
    {
        return new EstimatedInstallFeedbackNotification(EstimatedInstallFeedbackKind.PerformanceLog)
        {
            Message = message
        };
    }

    internal static EstimatedInstallFeedbackNotification WarningLog(Exception exception, string message)
    {
        return new EstimatedInstallFeedbackNotification(EstimatedInstallFeedbackKind.WarningLog)
        {
            Exception = exception,
            Message = message
        };
    }

    internal static EstimatedInstallFeedbackNotification DeferredAction(Action action)
    {
        return new EstimatedInstallFeedbackNotification(EstimatedInstallFeedbackKind.DeferredAction)
        {
            Action = action
        };
    }
}

internal sealed class EstimatedInstallBatchApplyContext
{
    public List<ChartFile> AddedCharts { get; } = [];

    public List<BMSFile> AddedBmsFiles { get; } = [];

    public HashSet<string> AffectedDirectories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddInstalledTargets(ChartStorageTargetSet addedTargets, string destinationDirectory)
    {
        AddedCharts.AddRange((addedTargets?.Charts ?? []).Where(chart => chart != null));
        AddedBmsFiles.AddRange(addedTargets?.BmsFiles ?? []);
        AddAffectedDirectory(destinationDirectory);
        foreach (ChartFile addedChart in addedTargets?.Charts ?? [])
        {
            AddAffectedDirectory(DirectoryExt.GetDirectoryNameSimple(addedChart.Path));
        }
    }

    private void AddAffectedDirectory(string directoryPath)
    {
        if (!string.IsNullOrWhiteSpace(directoryPath))
        {
            AffectedDirectories.Add(directoryPath);
        }
    }
}

internal sealed class PendingEstimatedInstallCollectionApplyResult
{
    public int Before { get; set; }

    public int Changed { get; set; }

    public int After { get; set; }
}

internal sealed class PendingEstimatedInstallExecutionReceipt
{
    internal Stopwatch TotalStopwatch { get; init; }

    internal PendingInstallBatchPlan InstallPlan { get; init; }

    internal PendingInstallBatchResult BatchResult { get; init; }

    internal EstimatedInstallBatchApplyContext BatchApplyContext { get; init; }

    internal PendingEstimatedInstallCollectionApplyResult PendingApplyResult { get; init; }

    internal PendingEstimatedInstallCollectionApplyResult InstalledApplyResult { get; init; }

    /// <summary>
    /// Existing package-collection publication deferral.  The scope is kept
    /// alive until the outer file-mutation lease has released, then disposed
    /// by the command owner so DB apply and UI publication cannot overlap.
    /// </summary>
    internal IDisposable CollectionPublicationScope { get; init; }

    internal PendingEstimatedInstallPostGuardResult MaintenanceReceipt { get; set; }

    internal Action MaintenancePublication { get; set; }

    internal PendingEstimatedInstallPostGuardResult InlineChartInfoReceipt { get; set; }

    internal Action InlineChartInfoPublication { get; set; }

    internal long LibraryStateApplyMs { get; init; }

    internal long PendingApplyMs { get; init; }

    internal long InstalledApplyMs { get; init; }

    internal bool DeletePendingPackageSourceAfterInstall { get; init; }

    internal bool IsSkipped => BatchResult == null;
}

internal sealed class PendingEstimatedInstallExecutionContext
{
    private readonly List<IDisposable> packageEntryNotificationDeferrals = [];

    internal EstimatedInstallDeferredFeedback DeferredFeedback { get; } = new();

    internal void DeferPackageEntryNotifications(IEnumerable<ChartPackage> packages)
    {
        foreach (PackageChartEntry entry in (packages ?? [])
            .Where(package => package != null)
            .SelectMany(package => package.ChartEntries ?? [])
            .Where(entry => entry != null)
            .Distinct())
        {
            packageEntryNotificationDeferrals.Add(entry.DeferPropertyChangedNotifications());
        }
    }

    internal void PublishDeferredPackageEntryNotifications()
    {
        List<Exception> failures = [];
        for (int index = packageEntryNotificationDeferrals.Count - 1; index >= 0; index--)
        {
            try
            {
                packageEntryNotificationDeferrals[index]?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        packageEntryNotificationDeferrals.Clear();
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Deferred package-entry notification publication failed.",
                failures);
        }
    }
}

internal sealed class PendingEstimatedInstallMutationLease : IDisposable
{
    private IDisposable[] guards;

    private PendingEstimatedInstallMutationLease(IDisposable[] guards)
    {
        this.guards = guards ?? [];
    }

    internal static PendingEstimatedInstallMutationLease Acquire(params Func<IDisposable>[] guardFactories)
    {
        var acquired = new List<IDisposable>();
        try
        {
            foreach (Func<IDisposable> guardFactory in guardFactories ?? [])
            {
                acquired.Add(guardFactory?.Invoke());
            }
            return new PendingEstimatedInstallMutationLease([.. acquired]);
        }
        catch (Exception acquisitionException)
        {
            List<Exception> cleanupFailures = DisposeAll(acquired);
            if (cleanupFailures.Count == 0)
            {
                throw;
            }

            var failures = new List<Exception> { acquisitionException };
            failures.AddRange(cleanupFailures);
            throw new AggregateException(
                "Pending estimated-install mutation lease acquisition and cleanup failed.",
                failures);
        }
    }

    public void Dispose()
    {
        IDisposable[] current = Interlocked.Exchange(ref guards, null);
        if (current == null)
        {
            return;
        }

        List<Exception> failures = DisposeAll(current);
        if (failures.Count > 0)
        {
            throw new AggregateException("Pending estimated-install mutation lease disposal failed.", failures);
        }
    }

    private static List<Exception> DisposeAll(IEnumerable<IDisposable> disposables)
    {
        List<Exception> failures = [];
        IDisposable[] current = [.. disposables ?? []];
        for (int index = current.Length - 1; index >= 0; index--)
        {
            try
            {
                current[index]?.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
        return failures;
    }
}
