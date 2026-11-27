using System;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

/// <summary>
/// GitHubのスター依頼の画面です。利用者の選択を <see cref="Choice"/> に持ち、閉じる操作とEscでは初期値のあとでを返します。
/// </summary>
public partial class GitHubStarPromptDialog : ThemedWindow
{
    /// <summary>スターを付けに行く、このフォーク版のリポジトリのURLです。</summary>
    internal const string RepositoryUrl = "https://github.com/Neeted/bemusicseeker-unofficial-fork";

    private readonly IExternalShellGateway externalShellGateway;

    private readonly IUiDialogService dialogs;

    private bool opening;

    /// <summary>リンクの起動と、起動失敗を一度案内する通知先を受けて画面を作ります。</summary>
    /// <param name="externalShellGateway">リポジトリを既定のブラウザで開くshell窓口。</param>
    /// <param name="dialogs">起動失敗の通知先。null は許されません。</param>
    internal GitHubStarPromptDialog(IExternalShellGateway externalShellGateway, IUiDialogService dialogs)
    {
        InitializeComponent();
        this.externalShellGateway = externalShellGateway
            ?? throw new ArgumentNullException(nameof(externalShellGateway));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
    }

    /// <summary>利用者の選択です。ボタンで選ぶまではあとでです。</summary>
    internal GitHubStarPromptChoice Choice { get; private set; } = GitHubStarPromptChoice.Later;

    private async void StarButtonClick(object sender, RoutedEventArgs e)
    {
        if (opening)
        {
            return;
        }
        opening = true;
        try
        {
            externalShellGateway.Open(ExternalShellRequest.OpenUrl(RepositoryUrl));
        }
        catch (Exception exception)
        {
            TryLogWarning(exception, "github_star_prompt repository launch failed");
            await NotifyLaunchFailureAsync(exception);
            // 開けなかった場合は依頼を終えず、あとでと同じ扱いで閉じます。
            DialogResult = false;
            return;
        }
        Choice = GitHubStarPromptChoice.Star;
        DialogResult = true;
    }

    private void NeverButtonClick(object sender, RoutedEventArgs e)
    {
        if (opening)
        {
            return;
        }
        Choice = GitHubStarPromptChoice.Decline;
        DialogResult = true;
    }

    private async Task NotifyLaunchFailureAsync(Exception exception)
    {
        try
        {
            UiDialogResult notification = await dialogs.ShowMessageAsync(new UiMessageRequest(
                BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
                BeMusicSeeker.Properties.Resources.Error,
                MessageBoxButton.OK,
                MessageBoxImage.Hand,
                MessageBoxResult.OK,
                owner: this));
            UiDialogRoute.ThrowIfNotShown(notification, "GitHub star prompt launch failure notification");
        }
        catch (Exception notificationFailure)
        {
            // 通知自身の失敗は診断だけに残し、再通知しません。
            TryLogWarning(notificationFailure, "github_star_prompt launch failure notification failed");
        }
    }

    private static void TryLogWarning(Exception exception, string context)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, context); }
        catch { /* 任意の案内の診断の失敗を再通知しません。 */ }
    }
}
