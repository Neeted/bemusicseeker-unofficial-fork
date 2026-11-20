using System.Windows;
using System.Windows.Controls;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 設定画面の縦タブ・選択中のページ・見出し・本文のスクロール領域へ、テストが同じ経路で到達する補助です。
/// 画面の実体は <c>settingsNavigation</c> の <see cref="TabControl"/> だけで、ページは選択中のタブ項目の内容として得ます。
/// </summary>
internal static class SettingsTabs
{
    /// <summary>設定画面のカテゴリ選択を担う縦タブを返します。</summary>
    public static TabControl Navigation(FrameworkElement window)
        => (TabControl)window.FindName("settingsNavigation");

    /// <summary>選択中のカテゴリのタブ項目を返します。</summary>
    public static TabItem SelectedItem(FrameworkElement window)
        => (TabItem)Navigation(window).SelectedItem;

    /// <summary>選択中のカテゴリのページを返します。選択中のページは選択の時点で必ず作られています。</summary>
    public static object SelectedPage(FrameworkElement window)
        => SelectedItem(window).Content
            ?? throw new InvalidOperationException("選択中のカテゴリのページが作られていません。");

    /// <summary>呼び出した時点の選択中のページを <see cref="SettingsPageHost.Content"/> として読む窓口を返します。</summary>
    public static SettingsPageHost PageHost(FrameworkElement window) => new(Navigation(window));

    /// <summary>見出しの表示要素を返します。テンプレートの適用後に呼びます。</summary>
    public static TextBlock Header(FrameworkElement window)
        => (TextBlock)Navigation(window).Template.FindName("PART_PageHeader", Navigation(window));

    /// <summary>全カテゴリが共有する本文の縦スクロール領域を返します。</summary>
    public static ScrollViewer Body(FrameworkElement window)
        => ((ScrollingTabControl)Navigation(window)).PageScrollViewer
            ?? throw new InvalidOperationException("縦タブのテンプレートが適用されていません。");

    /// <summary>視覚木を親へたどり、最初に見つかる指定の型の祖先を返します。なければ null です。</summary>
    public static T? FindVisualAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (DependencyObject? current = System.Windows.Media.VisualTreeHelper.GetParent(element);
            current != null;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>選択中のタブ項目の見出しの文字列を返します。</summary>
    public static string? SelectedHeader(FrameworkElement window)
        => SelectedItem(window).Header as string;
}

/// <summary>タブの選択が変わっても、その時点の選択中のページを読み直す窓口です。</summary>
internal sealed class SettingsPageHost
{
    private readonly TabControl navigation;

    /// <summary>選択を読む縦タブを接続します。</summary>
    public SettingsPageHost(TabControl navigation) => this.navigation = navigation;

    /// <summary>選択中のタブ項目の内容（ページ）を返します。</summary>
    public object Content => ((TabItem)navigation.SelectedItem).Content
        ?? throw new InvalidOperationException("選択中のカテゴリのページが作られていません。");
}
