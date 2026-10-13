using System;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Ribbit.Logging;

namespace BeMusicSeeker.Views;

public partial class UpdateAvailableDialog : ThemedWindow
{
    private readonly UpdateAvailableDialogViewModel viewModel;

    private readonly IExternalShellGateway externalShellGateway;

    private readonly IUiDialogService dialogs;

    /// <summary>更新案内とリンク起動を所有し、起動失敗を同じ所有通知先で一度案内する画面を作ります。</summary>
    /// <param name="updateCheckResult">表示する更新確認結果。</param>
    /// <param name="progressHub">更新開始の可否に使う既存起動進捗。</param>
    /// <param name="externalShellGateway">正確な配布ページを開くshell窓口。</param>
    /// <param name="dialogs">所有通知先。省略時は共通coordinatorを使います。</param>
    internal UpdateAvailableDialog(
        UpdateCheckResult updateCheckResult,
        OperationProgressHubViewModel progressHub,
        IExternalShellGateway externalShellGateway,
        IUiDialogService dialogs = null)
    {
        InitializeComponent();
        viewModel = new UpdateAvailableDialogViewModel(updateCheckResult, progressHub);
        this.externalShellGateway = externalShellGateway
            ?? throw new ArgumentNullException(nameof(externalShellGateway));
        this.dialogs = dialogs ?? new UiDialogCoordinator();
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    internal UpdateAssetInfo SelectedAsset => viewModel.SelectedAsset;

    private async void ApplyButtonClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanStartUpdate || viewModel.SelectedAsset == null)
        {
            return;
        }
        if (await OpenReleasePageAsync())
        {
            DialogResult = true;
        }
    }

    private async void OpenReleasePageButtonClick(object sender, RoutedEventArgs e)
    {
        await OpenReleasePageAsync();
    }

    private async Task<bool> OpenReleasePageAsync()
    {
        if (string.IsNullOrWhiteSpace(viewModel.ReleasePageUrl))
        {
            return false;
        }
        try
        {
            externalShellGateway.Open(ExternalShellRequest.OpenUrl(viewModel.ReleasePageUrl));
            return true;
        }
        catch (Exception exception)
        {
            TryLogLaunchFailure(exception);
            try
            {
                UiDialogResult notification = await dialogs.ShowMessageAsync(new UiMessageRequest(
                    BeMusicSeeker.Properties.Resources.Msg_error_unexpected + Environment.NewLine + exception.Message,
                    BeMusicSeeker.Properties.Resources.Error,
                    MessageBoxButton.OK,
                    MessageBoxImage.Hand,
                    MessageBoxResult.OK,
                    owner: this));
                UiDialogRoute.ThrowIfNotShown(notification, "Update release page failure notification");
            }
            catch (Exception notificationFailure) { TryLogLaunchFailure(notificationFailure); }
            return false;
        }
    }

    private static void TryLogLaunchFailure(Exception exception)
    {
        try { NLogWrapper.FileLogger?.Warn(exception, "update release page launch"); }
        catch { /* 任意通知の診断失敗を再通知しません。 */ }
    }
}
