using System;
using System.Diagnostics;
using System.Windows;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateAvailableDialogViewModel viewModel;

    internal UpdateAvailableDialog(UpdateCheckResult updateCheckResult, OperationProgressHubViewModel progressHub)
    {
        InitializeComponent();
        viewModel = new UpdateAvailableDialogViewModel(updateCheckResult, progressHub);
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

        Process.Start(new ProcessStartInfo(viewModel.ReleasePageUrl)
        {
            UseShellExecute = true
        });
    }
}
