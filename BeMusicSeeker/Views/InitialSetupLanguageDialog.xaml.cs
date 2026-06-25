using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace BeMusicSeeker.Views;

public partial class InitialSetupLanguageDialog : UserControl, IComponentConnector
{
    public InitialSetupLanguageDialog()
    {
        InitializeComponent();
    }

    private void ContinueToSettings(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow mainWindow)
        {
            throw new InvalidOperationException("Initial setup language dialog is not hosted by MainWindow.");
        }

        mainWindow.HideOverlayDialog(this);
        mainWindow.ShowSettingDialogOverlay();
    }
}
