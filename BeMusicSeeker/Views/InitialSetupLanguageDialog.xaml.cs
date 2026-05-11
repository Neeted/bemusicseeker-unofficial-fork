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
        Visibility = Visibility.Hidden;
        if (Parent is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                if (child is SettingDialog settingDialog)
                {
                    settingDialog.Visibility = Visibility.Visible;
                    break;
                }
            }
        }
    }
}
