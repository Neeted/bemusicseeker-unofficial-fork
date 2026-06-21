using System;
using System.Diagnostics;
using System.Windows;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Views;

public partial class UpdateAvailableDialog : Window
{
    private readonly UpdateAvailableDialogViewModel viewModel;

    internal UpdateAvailableDialog(UpdateCheckResult updateCheckResult, MainWindowViewModel ownerViewModel)
    {
        InitializeComponent();
        viewModel = new UpdateAvailableDialogViewModel(updateCheckResult, ownerViewModel);
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }

    internal UpdateAssetInfo SelectedAsset => viewModel.SelectedAsset;

    private void ApplyButtonClick(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanApplyUpdateNow || viewModel.SelectedAsset == null)
        {
            return;
        }
        DialogResult = true;
    }

    private void OpenReleasePageButtonClick(object sender, RoutedEventArgs e)
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
