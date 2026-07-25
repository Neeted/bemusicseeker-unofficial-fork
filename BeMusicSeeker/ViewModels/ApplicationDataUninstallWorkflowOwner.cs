using System;
using System.Threading.Tasks;
using System.Windows;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.ViewModels;

internal enum ApplicationDataUninstallOutcome
{
    NotStarted,
    Blocked,
    Cancelled,
    Failed,
    Completed
}

internal sealed class ApplicationDataUninstallRequest
{
    internal ApplicationDataUninstallRequest(
        bool isWorkspaceReady,
        bool isLibraryOperationInProgress,
        string songDbPath)
    {
        IsWorkspaceReady = isWorkspaceReady;
        IsLibraryOperationInProgress = isLibraryOperationInProgress;
        SongDbPath = songDbPath;
    }

    internal bool IsWorkspaceReady { get; }

    internal bool IsLibraryOperationInProgress { get; }

    internal string SongDbPath { get; }
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

    internal bool ShouldCloseApplication => Outcome == ApplicationDataUninstallOutcome.Completed;

    internal static ApplicationDataUninstallResult NotStarted { get; } =
        new(ApplicationDataUninstallOutcome.NotStarted);

    internal static ApplicationDataUninstallResult Blocked { get; } =
        new(ApplicationDataUninstallOutcome.Blocked);

    internal static ApplicationDataUninstallResult Cancelled { get; } =
        new(ApplicationDataUninstallOutcome.Cancelled);

    internal static ApplicationDataUninstallResult Completed { get; } =
        new(ApplicationDataUninstallOutcome.Completed);

    internal static ApplicationDataUninstallResult Failed(Exception failure)
    {
        return new(ApplicationDataUninstallOutcome.Failed, failure ?? throw new ArgumentNullException(nameof(failure)));
    }
}

/// <summary>
/// application-owned data uninstall の確認、durable operation、terminal notification を所有します。
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

    internal async Task<ApplicationDataUninstallResult> RunAsync(ApplicationDataUninstallRequest request)
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
                "application data uninstall blocked notification");
            return ApplicationDataUninstallResult.Blocked;
        }

        UiDialogResult confirmation = await dialogs.ConfirmAsync(new UiConfirmationRequest(
            "BeMusicSeekerのデータをLR2データベースから削除します。"
                + Environment.NewLine
                + "続行した場合この操作を取り消しすることは出来ません。"
                + Environment.NewLine
                + "必要に応じて事前にバックアップを取得してください。"
                + Environment.NewLine
                + Environment.NewLine
                + "続行しますか？",
            "確認",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel));
        UiDialogRoute.ThrowIfNotShown(confirmation, "application data uninstall confirmation");
        if (!confirmation.IsAccepted)
        {
            return ApplicationDataUninstallResult.Cancelled;
        }

        try
        {
            await Task.Run(() => store.Uninstall(request.SongDbPath));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync(
                BeMusicSeeker.Properties.Resources.Msg_failed_uninstall + Environment.NewLine + Environment.NewLine + ex.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxImage.Hand,
                "application data uninstall failure notification");
            return ApplicationDataUninstallResult.Failed(ex);
        }

        await ShowMessageAsync(
            BeMusicSeeker.Properties.Resources.Msg_success_uninstall,
            BeMusicSeeker.Properties.Resources.Success,
            MessageBoxImage.Asterisk,
            "application data uninstall success notification");
        await ShowMessageAsync(
            "アプリケーションを終了します。",
            "確認",
            MessageBoxImage.Question,
            "application data uninstall exit notification");
        return ApplicationDataUninstallResult.Completed;
    }

    private async Task ShowMessageAsync(string message, string caption, MessageBoxImage icon, string routeName)
    {
        UiDialogResult result = await dialogs.ShowMessageAsync(new UiMessageRequest(
            message,
            caption,
            MessageBoxButton.OK,
            icon,
            MessageBoxResult.OK));
        UiDialogRoute.ThrowIfNotShown(result, routeName);
    }
}
