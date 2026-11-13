using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal enum PackageCatalogMutationPhase
{
    RefreshSuppressionStarted,
    RefreshSuppressionEnded
}

internal sealed class PackageCatalogMutationPhaseEventArgs : EventArgs
{
    internal PackageCatalogMutationPhaseEventArgs(PackageCatalogMutationPhase phase)
    {
        Phase = phase;
    }

    internal PackageCatalogMutationPhase Phase { get; }
}

internal interface IPackageCatalogStore
{
    /// <summary>受理済み操作の明示権限を物理変更へ渡す窓口を返します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    IPackageCatalogStore ForAcceptedOperation(LibraryFileMutationCapability capability) => this;

    /// <summary>区画全体の解除と必要な入力回収の実終端を返します。</summary>
    Task RemoveAllAsync(BMSLibrary library, PackageCatalogSection section);

    /// <summary>確定した対象の解除と必要な入力回収の実終端を返します。</summary>
    Task RemovePackagesAsync(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartPackage> packages);

    IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartOperationTarget> targets);

    bool IsSectionEmpty(BMSLibrary library, PackageCatalogSection section);
}

internal sealed class PackageCatalogMutationResult
{
    private PackageCatalogMutationResult(
        bool succeeded,
        Exception failure,
        bool shouldApplyView,
        PackageCatalogSection? emptySection,
        bool busy = false)
    {
        Busy = busy;
        Succeeded = succeeded;
        Failure = failure;
        ShouldApplyView = shouldApplyView;
        EmptySection = emptySection;
    }

    internal bool Succeeded { get; }

    internal Exception Failure { get; }

    internal bool ShouldApplyView { get; }

    internal PackageCatalogSection? EmptySection { get; }

    internal static PackageCatalogMutationResult Completed { get; } = new(true, null, true, null);

    internal static PackageCatalogMutationResult Rejected { get; } = new(false, null, false, null);

    /// <summary>
    /// 共通受付Lの使用中で、明示要求を確認・変更の前に拒否したことを示します。警告は所有者が一度だけ表示済みで、失敗を持たず、画面へ反映しません。
    /// </summary>
    internal bool Busy { get; }

    /// <summary>受付使用中による未受理です。成功・利用者の取消・失敗のいずれとも区別します。</summary>
    internal static PackageCatalogMutationResult RejectedBusy { get; } = new(false, null, false, null, busy: true);

    internal static PackageCatalogMutationResult CompletedFor(PackageCatalogSection? emptySection)
    {
        return new PackageCatalogMutationResult(true, null, true, emptySection);
    }

    internal static PackageCatalogMutationResult Failed(
        Exception failure,
        PackageCatalogSection? emptySection = null)
    {
        return new PackageCatalogMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            true,
            emptySection);
    }

    internal static PackageCatalogMutationResult FailedBeforeMutation(Exception failure)
    {
        return new PackageCatalogMutationResult(
            false,
            failure ?? throw new ArgumentNullException(nameof(failure)),
            false,
            null);
    }
}

internal sealed class PackageCatalogWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly ChartMutationActivityOwner chartMutationActivity;
    private readonly IUiDialogService dialogs;
    private readonly Func<Func<Task<PackageCatalogMutationResult>>, Task<PackageCatalogMutationResult>> backgroundMutationRunner;
    private readonly IPackageCatalogStore store;

    internal event EventHandler<PackageCatalogMutationPhaseEventArgs> MutationPhasePublished;

    /// <summary>管理入力の回収前停止と、実終端まで待つ背景実行を明示して目録操作を構成します。</summary>
    internal PackageCatalogWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        ChartMutationActivityOwner chartMutationActivity,
        IUiDialogService dialogs,
        IChartMutationPlaybackPort playback,
        Func<Func<Task<PackageCatalogMutationResult>>, Task<PackageCatalogMutationResult>> backgroundMutationRunner,
        IPackageCatalogStore store = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.chartMutationActivity = chartMutationActivity ?? throw new ArgumentNullException(nameof(chartMutationActivity));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.backgroundMutationRunner = backgroundMutationRunner
            ?? throw new ArgumentNullException(nameof(backgroundMutationRunner));
        ArgumentNullException.ThrowIfNull(playback);
        this.store = store ?? new BmsLibraryPackageCatalogStore(playback.StopPlaybackForMutationAsync);
    }

    internal Task<PackageCatalogMutationResult> ClearAllAsync(PackageCatalogSection section)
    {
        ValidateSection(section);
        if (!TryEnterOperation(section, out IDisposable operationGate))
        {
            return RejectAdmissionAsync();
        }
        PackageCatalogMutationResult confirmation = ConfirmRemoval(
            section == PackageCatalogSection.Pending
                ? BeMusicSeeker.Properties.Resources.Msg_clear_all_pendings
                : BeMusicSeeker.Properties.Resources.Msg_clear_all_installed,
            "Package catalog clear-all confirmation");
        if (!confirmation.Succeeded)
        {
            operationGate.Dispose();
            return Task.FromResult(confirmation);
        }
        return ScheduleWithAcquiredGate(
            operationGate,
            () => ExecuteAsync(section, (library, operationStore) => operationStore.RemoveAllAsync(library, section), operationGate));
    }

    internal Task<PackageCatalogMutationResult> RemovePackageAsync(
        PackageCatalogSection section,
        ChartPackage package)
    {
        ValidateSection(section);
        if (package == null)
        {
            throw new ArgumentNullException(nameof(package));
        }
        if (!TryEnterOperation(section, out IDisposable operationGate))
        {
            return RejectAdmissionAsync();
        }
        if (section == PackageCatalogSection.Pending)
        {
            PackageCatalogMutationResult confirmation = ConfirmRemoval(
                BeMusicSeeker.Properties.Resources.Msg_clear_pendings
                + Environment.NewLine
                + Environment.NewLine
                + (package.DisplayTitle ?? string.Empty),
                "Pending package catalog entry removal confirmation");
            if (!confirmation.Succeeded)
            {
                operationGate.Dispose();
                return Task.FromResult(confirmation);
            }
        }
        return ScheduleWithAcquiredGate(
            operationGate,
            () => ExecuteAsync(section, (library, operationStore) => operationStore.RemovePackagesAsync(library, section, [package]), operationGate));
    }

    internal Task<PackageCatalogMutationResult> RemoveSelectionAsync(PackageCatalogRemovalRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        ValidateSection(request.Section);
        if (!TryEnterOperation(request.Section, out IDisposable operationGate))
        {
            return RejectAdmissionAsync();
        }
        PackageCatalogMutationResult confirmation = ConfirmRemoval(
            request.IsPending
                ? BeMusicSeeker.Properties.Resources.Msg_clear_selected_pendings
                : BeMusicSeeker.Properties.Resources.Msg_clear_selected_installed,
            "Selected package catalog entry removal confirmation");
        if (!confirmation.Succeeded)
        {
            operationGate.Dispose();
            return Task.FromResult(confirmation);
        }
        return ScheduleWithAcquiredGate(
            operationGate,
            () => ExecuteAsync(request.Section, (library, operationStore) =>
            {
                IReadOnlyList<ChartPackage> packages = store.ResolvePackages(
                    library,
                    request.Section,
                    request.Targets);
                return operationStore.RemovePackagesAsync(library, request.Section, packages);
            }, operationGate));
    }

    private async Task<PackageCatalogMutationResult> ExecuteAsync(
        PackageCatalogSection section,
        Func<BMSLibrary, IPackageCatalogStore, Task> mutation,
        IDisposable acquiredOperationGate)
    {
        IDisposable operationGate = acquiredOperationGate
            ?? throw new ArgumentNullException(nameof(acquiredOperationGate));
        BMSLibrary library;
        try
        {
            library = libraryProvider();
        }
        catch
        {
            operationGate.Dispose();
            throw;
        }
        if (library == null)
        {
            operationGate.Dispose();
            return PackageCatalogMutationResult.Completed;
        }
        using LibraryFileMutationCapability capability = chartFileOperations.CreateMutationCapability(operationGate);
        IPackageCatalogStore operationStore = store.ForAcceptedOperation(capability);
        IDisposable activityLease = null;
        bool suppressionStarted = false;
        bool mutationAttempted = false;
        PackageCatalogSection? emptySection = null;
        var failures = new List<ExceptionDispatchInfo>();
        try
        {
            activityLease = chartMutationActivity.Enter();
            suppressionStarted = true;
            PublishMutationPhase(PackageCatalogMutationPhase.RefreshSuppressionStarted);
            mutationAttempted = true;
            await mutation(library, operationStore).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
        finally
        {
            if (mutationAttempted)
            {
                CaptureCleanupFailure(
                    () =>
                    {
                        if (store.IsSectionEmpty(library, section))
                        {
                            emptySection = section;
                        }
                    },
                    failures);
            }
            if (suppressionStarted)
            {
                CaptureCleanupFailure(
                    () => PublishMutationPhase(PackageCatalogMutationPhase.RefreshSuppressionEnded),
                    failures);
            }
            if (operationGate != null)
            {
                CaptureCleanupFailure(operationGate.Dispose, failures);
            }
            if (activityLease != null)
            {
                CaptureCleanupFailure(activityLease.Dispose, failures);
            }
        }
        return failures.Count switch
        {
            0 => PackageCatalogMutationResult.CompletedFor(emptySection),
            1 => PackageCatalogMutationResult.Failed(failures[0].SourceException, emptySection),
            _ => PackageCatalogMutationResult.Failed(
                new AggregateException(failures.Select(failure => failure.SourceException)),
                emptySection),
        };
    }

    private Task<PackageCatalogMutationResult> ScheduleWithAcquiredGate(
        IDisposable operationGate,
        Func<Task<PackageCatalogMutationResult>> mutation)
    {
        try
        {
            Task<PackageCatalogMutationResult> task = backgroundMutationRunner(mutation);
            if (task == null)
            {
                operationGate.Dispose();
                return Task.FromResult(PackageCatalogMutationResult.FailedBeforeMutation(
                    new InvalidOperationException("Package catalog mutation runner returned no task.")));
            }
            return task;
        }
        catch (Exception exception)
        {
            operationGate.Dispose();
            return Task.FromResult(PackageCatalogMutationResult.FailedBeforeMutation(exception));
        }
    }

    private void PublishMutationPhase(PackageCatalogMutationPhase phase)
    {
        MutationPhasePublished?.Invoke(
            this,
            new PackageCatalogMutationPhaseEventArgs(phase));
    }

    private bool TryEnterOperation(
        PackageCatalogSection section,
        out IDisposable operationGate)
    {
        return chartFileOperations.TryEnter(out operationGate);
    }

    /// <summary>
    /// 共通受付Lを取得できなかった明示要求を確認・変更の前に終えます。終了要求で受付が閉じた場合は通知せず拒否を返し、
    /// それ以外の使用中は警告を一度だけ表示して <see cref="PackageCatalogMutationResult.RejectedBusy"/> を返します。警告の表示失敗は変更前の失敗です。
    /// </summary>
    private Task<PackageCatalogMutationResult> RejectAdmissionAsync()
    {
        return chartFileOperations.IsAdmissionClosed
            ? Task.FromResult(PackageCatalogMutationResult.Rejected)
            : ShowBusyWarningAsync();
    }

    private async Task<PackageCatalogMutationResult> ShowBusyWarningAsync()
    {
        try
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
                BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
                BeMusicSeeker.Properties.Resources.Warning)).ConfigureAwait(false);
            UiDialogRoute.ThrowIfNotShown(result, "Package catalog Busy notification");
            return PackageCatalogMutationResult.RejectedBusy;
        }
        catch (Exception exception)
        {
            return PackageCatalogMutationResult.FailedBeforeMutation(exception);
        }
    }

    private PackageCatalogMutationResult ConfirmRemoval(string message, string routeName)
    {
        try
        {
            UiDialogResult result = dialogs.ConfirmAsync(new UiConfirmationRequest(
                message,
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel)).GetAwaiter().GetResult();
            if (result == null)
            {
                return PackageCatalogMutationResult.FailedBeforeMutation(
                    new InvalidOperationException(routeName + " returned no result."));
            }
            return result.Status switch
            {
                UiDialogStatus.Accepted => PackageCatalogMutationResult.Completed,
                UiDialogStatus.Rejected or UiDialogStatus.CancelledByUser => PackageCatalogMutationResult.Rejected,
                UiDialogStatus.ClosedByUser => result.IsPositive
                    ? PackageCatalogMutationResult.Completed
                    : PackageCatalogMutationResult.Rejected,
                _ => PackageCatalogMutationResult.FailedBeforeMutation(
                    new InvalidOperationException(
                        routeName + " could not be displayed (" + result.Status + ").",
                        result.Exception)),
            };
        }
        catch (Exception exception)
        {
            return PackageCatalogMutationResult.FailedBeforeMutation(exception);
        }
    }

    private static void CaptureCleanupFailure(Action cleanup, List<ExceptionDispatchInfo> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            failures.Add(ExceptionDispatchInfo.Capture(ex));
        }
    }

    private static void ValidateSection(PackageCatalogSection section)
    {
        if (section != PackageCatalogSection.Pending
            && section != PackageCatalogSection.Installed)
        {
            throw new ArgumentOutOfRangeException(nameof(section), section, "Unsupported package catalog section.");
        }
    }
}

internal sealed class BmsLibraryPackageCatalogStore : IPackageCatalogStore
{
    private readonly LibraryFileMutationCapability capability;
    private readonly Func<Task> stopPlaybackForCleanup;

    /// <summary>管理入力の回収前に待つ再生停止と、借用する受理済み権限を目録窓口へ接続します。元leaseは呼出元が実終端まで所有します。</summary>
    internal BmsLibraryPackageCatalogStore(Func<Task> stopPlaybackForCleanup, LibraryFileMutationCapability capability = null)
    {
        this.stopPlaybackForCleanup = stopPlaybackForCleanup ?? throw new ArgumentNullException(nameof(stopPlaybackForCleanup));
        this.capability = capability;
    }

    /// <summary>受理済み操作の明示権限を物理変更へ渡す窓口を返します。呼出元が実処理・後片付けまで元leaseを保持します。</summary>
    public IPackageCatalogStore ForAcceptedOperation(LibraryFileMutationCapability capability) => new BmsLibraryPackageCatalogStore(stopPlaybackForCleanup, capability);

    public Task RemoveAllAsync(BMSLibrary library, PackageCatalogSection section)
    {
        if (section == PackageCatalogSection.Pending)
        {
            return library.RemovePendingPackagesAllAsync(stopPlaybackForCleanup, capability);
        }
        else
        {
            library.RemoveInstalledPackageRecordsAll();
            return Task.CompletedTask;
        }
    }

    public Task RemovePackagesAsync(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartPackage> packages)
    {
        if (section == PackageCatalogSection.Pending)
        {
            return library.RemovePendingPackagesAsync(packages, stopPlaybackForCleanup, capability);
        }
        else
        {
            library.RemoveInstalledPackageRecords(packages);
            return Task.CompletedTask;
        }
    }

    /// <summary>行の項目参照が現在所属するパッケージを返します。同tokenの別所属や退役行を同じ所属として扱いません。</summary>
    public IReadOnlyList<ChartPackage> ResolvePackages(
        BMSLibrary library,
        PackageCatalogSection section,
        IReadOnlyList<ChartOperationTarget> targets)
    {
        IEnumerable<ChartPackage> source = section == PackageCatalogSection.Pending
            ? library.ChartPackagesPending
            : library.ChartPackagesInstalled;
        source ??= Enumerable.Empty<ChartPackage>();
        return [.. targets
            .Select(target => source.FirstOrDefault(package => ContainsChartTarget(package, target)))
            .Where(package => package != null)
            .Distinct()];
    }

    public bool IsSectionEmpty(BMSLibrary library, PackageCatalogSection section)
    {
        if (library == null)
        {
            return false;
        }
        return section == PackageCatalogSection.Pending
            ? library.ChartPackagesPending?.Count == 0
            : library.ChartPackagesInstalled?.Count == 0;
    }

    private static bool ContainsChartTarget(ChartPackage package, ChartOperationTarget target)
    {
        // 同じ所持譜面を共有する別所属へ、退役済みの行や所属のない要求を結び直しません。
        return package != null && target?.PackageEntry != null
            && (package.ChartEntries ?? []).Any(entry => ReferenceEquals(entry, target.PackageEntry));
    }
}
