using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;

namespace BeMusicSeeker.ViewModels;

internal enum ApplicationDataUninstallOutcome
{
    NotStarted,
    Blocked,
    Cancelled,
    Approved,
    Failed,
    Completed
}

internal sealed class ApplicationDataUninstallRequest
{
    /// <summary>既存撤去確認へ設定の終了案内を統合するため、受付時の状態と確認本文を捕捉します。</summary>
    internal ApplicationDataUninstallRequest(
        bool isWorkspaceReady,
        bool isLibraryOperationInProgress, string confirmationMessage = null)
    {
        ConfirmationMessage = confirmationMessage;
        IsWorkspaceReady = isWorkspaceReady;
        IsLibraryOperationInProgress = isLibraryOperationInProgress;
    }

    /// <summary>設定候補の破棄案内を含めた既存撤去確認。省略時は通常の撤去確認です。</summary>
    internal string ConfirmationMessage { get; }

    internal bool IsWorkspaceReady { get; }

    internal bool IsLibraryOperationInProgress { get; }

}

internal sealed class ApplicationDataUninstallResult
{
    private ApplicationDataUninstallResult(ApplicationDataUninstallOutcome outcome, Exception failure = null)
    {
        Outcome = outcome;
        Failure = failure;
    }

    internal ApplicationDataUninstallOutcome Outcome { get; }

    internal Exception Failure { get; }

    internal static ApplicationDataUninstallResult NotStarted { get; } =
        new(ApplicationDataUninstallOutcome.NotStarted);

    internal static ApplicationDataUninstallResult Blocked { get; } =
        new(ApplicationDataUninstallOutcome.Blocked);

    internal static ApplicationDataUninstallResult Cancelled { get; } =
        new(ApplicationDataUninstallOutcome.Cancelled);

    internal static ApplicationDataUninstallResult Completed { get; } =
        new(ApplicationDataUninstallOutcome.Completed);

    internal static ApplicationDataUninstallResult Approved { get; } =
        new(ApplicationDataUninstallOutcome.Approved);

    internal static ApplicationDataUninstallResult Failed(Exception failure)
    {
        return new(ApplicationDataUninstallOutcome.Failed, failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

/// <summary>
/// 管理データ撤去の確認と、通常終了の実終端後に行う削除・通知を所有します。
/// </summary>
internal sealed class ApplicationDataUninstallWorkflowOwner
{
    private readonly IUiDialogService dialogs;

    private readonly IApplicationDataUninstallStore store;

    internal ApplicationDataUninstallWorkflowOwner(IUiDialogService dialogs, IApplicationDataUninstallStore store)
    {
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>設定操作の受付内では確認だけを行い、削除はShellの終端段階へ引き渡します。</summary>
    internal async Task<ApplicationDataUninstallResult> ConfirmAsync(ApplicationDataUninstallRequest request)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.IsWorkspaceReady)
        {
            return ApplicationDataUninstallResult.NotStarted;
        }
        if (request.IsLibraryOperationInProgress)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_settings_apply_blocked_during_initialization,
                BeMusicSeeker.Properties.Resources.Warning,
                MessageBoxImage.Exclamation,
                "application data uninstall blocked notification", dialogs);
            return ApplicationDataUninstallResult.Blocked;
        }

        UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            request.ConfirmationMessage ?? BeMusicSeeker.Properties.Resources.Confirm_UninstallApplicationData,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel));
        UiDialogRoute.ThrowIfNotShown(confirmation, "application data uninstall confirmation");
        if (!confirmation.IsAccepted)
        {
            return ApplicationDataUninstallResult.Cancelled;
        }

        return ApplicationDataUninstallResult.Approved;
    }

    /// <summary>全実処理と要求元設定操作の終端後に撤去し、閉じた設定画面に依存しない窓口で結果を通知します。</summary>
    internal async Task<ApplicationDataUninstallResult> CompleteAsync(string songDbPath, IUiDialogService terminalDialogs)
    {
        ArgumentNullException.ThrowIfNull(terminalDialogs);
        try
        {
            await Task.Run(() => store.Uninstall(songDbPath));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_failed_uninstall + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "application data uninstall failure notification", terminalDialogs);
            return ApplicationDataUninstallResult.Failed(ex);
        }

        await ShowMessageAsync(
            BeMusicSeeker.Properties.Resources.Msg_success_uninstall,
            BeMusicSeeker.Properties.Resources.Success,
            MessageBoxImage.Asterisk,
            "application data uninstall success notification", terminalDialogs);
        await ShowMessageAsync(
            BeMusicSeeker.Properties.Resources.Msg_ApplicationWillExit,
            BeMusicSeeker.Properties.Resources.Confirm,
            MessageBoxImage.Question,
            "application data uninstall exit notification", terminalDialogs);
        return ApplicationDataUninstallResult.Completed;
    }

    private static async Task ShowMessageAsync(string message, string caption, MessageBoxImage icon, string routeName, IUiDialogService notificationDialogs)
    {
        UiDialogResult result = await notificationDialogs.ShowMessageAsync(new UiMessageRequest(
            message,
            caption,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK));
        UiDialogRoute.ThrowIfNotShown(result, routeName);
    }
}
