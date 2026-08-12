using System.Windows;

namespace BeMusicSeeker.Views;

public partial class PendingDeleteConfirmDialog : ThemedWindow
{
    public bool DeleteFolderWhenNoBmsChecked { get; private set; } = true;

    public PendingDeleteConfirmDialog()
    {
        InitializeComponent();
    }

    private void OkButtonClick(object sender, RoutedEventArgs e)
    {
        DeleteFolderWhenNoBmsChecked = checkBoxDeleteContainingFolder.IsChecked != false;
        DialogResult = true;
    }
}
