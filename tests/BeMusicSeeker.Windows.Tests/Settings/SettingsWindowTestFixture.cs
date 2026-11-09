using System.Windows;
using System.Windows.Media;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>設定の実Window表示と論理・描画ツリー探索だけを共有します。</summary>
internal static class SettingsWindowTestFixture
{
    /// <summary>同じ表示scopeで設定Windowを表示し、描画完了を待ちます。</summary>
    internal static SettingsWindow OpenSettingsWindow(
        TestWindowPresentationScope windowTest,
        SettingsDialogViewModel dialog)
    {
        var window = new SettingsWindow(UndeclaredDialogs.Create()) { DataContext = dialog };
        windowTest.ShowAndWaitForContentRendered(window);
        return window;
    }

    /// <summary>同じ要素の再訪を避け、描画・論理ツリーの指定型の要素を列挙します。</summary>
    internal static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (current is T typedCurrent)
            {
                yield return typedCurrent;
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                {
                    pending.Push(VisualTreeHelper.GetChild(current, index));
                }
            }

            foreach (object logicalChild in LogicalTreeHelper.GetChildren(current))
            {
                if (logicalChild is DependencyObject dependencyObject)
                {
                    pending.Push(dependencyObject);
                }
            }
        }
    }
}

/// <summary>親子設定Windowの通常閉鎖と所有終了を同じ表示窓口へ接続します。</summary>
internal sealed class WindowClosingPresentationPort : ISettingDialogPresentationPort
{
    internal SettingsWindow? CurrentWindow { get; set; }

    public void OpenSettingsDialog(bool deferPresentation = false)
    {
    }

    public void OpenInitialSetupLanguageDialog()
    {
    }

    public void CloseSettingsDialog()
    {
        (CurrentWindow ?? throw new InvalidOperationException("No settings Window is active."))
            .CloseFromPresentation();
    }

    public Task CloseSettingsDialogAsync()
    {
        CloseSettingsDialog();
        return Task.CompletedTask;
    }

    public void RefreshAppearanceSelection()
    {
    }
}
