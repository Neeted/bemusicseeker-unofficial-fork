using System.Windows;
using System.Windows.Input;
using BeMusicSeeker.Views.Dialogs;

namespace BeMusicSeeker.Views;

/// <summary>保留復元の未確定失敗に対する三つの明示選択を提示します。閉鎖は初期化や終了の承認ではありません。</summary>
public partial class PendingPackageRestoreWindow : ThemedWindow
{
    /// <summary>元の復元・一覧初期化失敗の原因を表示します。</summary>
    /// <param name="cause">元の失敗が保持する原因の説明。</param>
    public PendingPackageRestoreWindow(string cause)
    {
        InitializeComponent();
        FailureCause.Text = cause;
    }

    /// <summary>実ボタンで選択した値です。×・EscではNoneを保ちます。</summary>
    internal PendingPackageRestoreChoice Choice { get; private set; }

    private void RetryClick(object sender, RoutedEventArgs e) => Accept(PendingPackageRestoreChoice.Retry);

    private void ResetClick(object sender, RoutedEventArgs e) => Accept(PendingPackageRestoreChoice.Reset);

    private void ExitClick(object sender, RoutedEventArgs e) => Accept(PendingPackageRestoreChoice.Exit);

    private void Accept(PendingPackageRestoreChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) { return; }
        e.Handled = true;
        Close();
    }
}
