using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

/// <summary>ノート警告再確認の明示要求の結果です。</summary>
internal enum ZeroNoteRecheckStatus
{
    /// <summary>ライブラリ未接続、または終了要求で受付が閉じたため、通知せずに開始しませんでした。</summary>
    NotStarted,

    /// <summary>受理した再確認が必須公開まで完了しました。</summary>
    Completed,

    /// <summary>共通受付Lの使用中で副作用前に拒否し、受付使用中の警告を一度だけ表示しました。</summary>
    RejectedBusy,
}

/// <summary>
/// ノート警告再確認の実行確定時にLを判定し、入力捕捉から必須公開まで保持します。
/// 利用者の明示要求がLの使用中で拒否されたときは、この所有者が受付使用中の警告を一度だけ表示します。
/// </summary>
internal sealed class ZeroNoteMaintenanceWorkflowOwner
{
    private readonly Func<BMSLibrary> libraryProvider;
    private readonly ChartFileOperationSynchronizer chartFileOperations;
    private readonly IUiDialogService dialogs;
    private readonly Action<BMSLibrary, LibraryFileMutationCapability> recheck;

    internal ZeroNoteMaintenanceWorkflowOwner(
        Func<BMSLibrary> libraryProvider,
        ChartFileOperationSynchronizer chartFileOperations,
        IUiDialogService dialogs,
        Action<BMSLibrary, LibraryFileMutationCapability> recheck = null)
    {
        this.libraryProvider = libraryProvider ?? throw new ArgumentNullException(nameof(libraryProvider));
        this.chartFileOperations = chartFileOperations ?? throw new ArgumentNullException(nameof(chartFileOperations));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.recheck = recheck ?? ((library, capability) => library.RecheckZeroNoteWarnings(capability));
    }

    /// <summary>
    /// 入力捕捉・worker起動より前にLを判定し、受理した再確認は公開終端まで待ちます。
    /// ライブラリ未接続と受付の閉鎖は通知せず <see cref="ZeroNoteRecheckStatus.NotStarted"/>、
    /// Lの使用中は警告を一度だけ表示して <see cref="ZeroNoteRecheckStatus.RejectedBusy"/> を返します。予約や自動再試行はしません。
    /// </summary>
    internal async Task<ZeroNoteRecheckStatus> RecheckAsync()
    {
        BMSLibrary library = libraryProvider();
        if (library == null) { return ZeroNoteRecheckStatus.NotStarted; }
        if (!chartFileOperations.TryEnter(out IDisposable admission))
        {
            if (chartFileOperations.IsAdmissionClosed || library.IsShutdownRequested)
            {
                return ZeroNoteRecheckStatus.NotStarted;
            }
            UiDialogResult result = await dialogs.ShowMessageAsync(UiMessageRequest.CreateWarning(
                BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy,
                BeMusicSeeker.Properties.Resources.Warning)).ConfigureAwait(false);
            UiDialogRoute.ThrowIfNotShown(result, "Zero-note recheck Busy notification");
            return ZeroNoteRecheckStatus.RejectedBusy;
        }
        using (admission)
        using (LibraryFileMutationCapability capability = chartFileOperations.CreateMutationCapability(admission))
        {
            await Task.Run(() => recheck(library, capability)).ConfigureAwait(false);
            return ZeroNoteRecheckStatus.Completed;
        }
    }
}
