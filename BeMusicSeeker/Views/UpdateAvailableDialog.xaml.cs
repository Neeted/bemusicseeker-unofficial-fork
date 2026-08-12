using System;
using System.Windows;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public partial class UpdateAvailableDialog : ThemedWindow
{
    private readonly UpdateAvailableDialogViewModel viewModel;

    private readonly IExternalShellGateway externalShellGateway;

    internal UpdateAvailableDialog(
        UpdateCheckResult updateCheckResult,
        OperationProgressHubViewModel progressHub,
        IExternalShellGateway externalShellGateway)
    {
        InitializeComponent();
        viewModel = new UpdateAvailableDialogViewModel(updateCheckResult, progressHub);
        this.externalShellGateway = externalShellGateway
            ?? throw new ArgumentNullException(nameof(externalShellGateway));
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    internal UpdateAssetInfo SelectedAsset => viewModel.SelectedAsset;

    private void ApplyButtonClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanStartUpdate || viewModel.SelectedAsset == null)
        {
            return;
        }
        OpenReleasePage();
        DialogResult = true;
    }

    private void OpenReleasePageButtonClick(object sender, RoutedEventArgs e)
    {
        OpenReleasePage();
    }

    private void OpenReleasePage()
    {
        if (string.IsNullOrWhiteSpace(viewModel.ReleasePageUrl))
        {
            return;
        }

        externalShellGateway.Open(ExternalShellRequest.OpenUrl(viewModel.ReleasePageUrl));
    }
}
