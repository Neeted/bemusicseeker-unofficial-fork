using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests;

/// <summary>
/// メニュー項目の見出しが、実際に描画される文字列としてどう表示されるかを、表示したWindowの描画要素から読み取る補助です。
/// アクセスキーの解釈は描画要素の側で行われるため、見出しの値ではなく、描画された文字列とアクセスキーの有無を観測します。
/// </summary>
internal static class MenuHeaderRenderProbe
{
    /// <summary>
    /// 本番のApp.xamlと同じく、既定のメニュー項目スタイルを含む共通の見た目をアプリケーション資源へ加えて実行します。
    /// 共有のテスト用Applicationはこれらを持たないため、MainWindowの構築より前に加え、終了時に外します。
    /// </summary>
    internal static void WithApplicationStyles(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new InvalidOperationException("The shared WPF application host is unavailable.");
            ResourceDictionary[] dictionaries =
            [
                new ResourceDictionary { Source = new Uri("/BeMusicSeeker;component/Themes/Simple Styles.xaml", UriKind.RelativeOrAbsolute) },
                new ResourceDictionary { Source = new Uri("/BeMusicSeeker;component/Themes/Light.xaml", UriKind.RelativeOrAbsolute) },
            ];
            for (int index = 0; index < dictionaries.Length; index++)
            {
                application.Resources.MergedDictionaries.Insert(index, dictionaries[index]);
            }
            try { action(); }
            finally
            {
                foreach (ResourceDictionary dictionary in dictionaries) { application.Resources.MergedDictionaries.Remove(dictionary); }
            }
        });
    }

    /// <summary>
    /// 項目を一つのWindowへ並べて表示し、各項目の見出しとして描画された文字列を返します。
    /// 表示のスタイルは指定したWindowの資源（既定のメニュー項目スタイルを含む）をそのまま使います。
    /// </summary>
    /// <param name="scope">表示と回収を所有する範囲。</param>
    /// <param name="styleSource">スタイルの資源を持つ元のWindow。</param>
    /// <param name="items">親を持たない項目。</param>
    internal static IReadOnlyList<string> RenderHeaderTexts(TestWindowPresentationScope scope, Window styleSource, IReadOnlyList<MenuItem> items)
    {
        var panel = new StackPanel();
        foreach (MenuItem item in items) { panel.Children.Add(item); }
        var host = new Window { Resources = styleSource.Resources, Content = panel, Width = 700, Height = 500 };
        scope.ShowAndWaitForContentRendered(host);
        try
        {
            var texts = new List<string>();
            foreach (MenuItem item in items)
            {
                item.ApplyTemplate();
                ContentPresenter headerHost = item.Template.FindName("HeaderHost", item) as ContentPresenter
                    ?? throw new AssertFailedException("見出しの描画部品がありません。");
                TextBlock text = FindTextBlock(headerHost) ?? throw new AssertFailedException("見出しの文字列が描画されていません。");
                texts.Add(new TextRange(text.ContentStart, text.ContentEnd).Text);
            }
            return texts;
        }
        finally
        {
            panel.Children.Clear();
            host.Content = null;
        }
    }

    /// <summary>見出しがアクセスキーとして解釈される描画（AccessText）なら、その<see cref="AccessText.AccessKey"/>を返します。解釈されない描画ではnullです。</summary>
    internal static char? FindAccessKey(TestWindowPresentationScope scope, Window styleSource, MenuItem item)
    {
        var panel = new StackPanel();
        panel.Children.Add(item);
        var host = new Window { Resources = styleSource.Resources, Content = panel, Width = 400, Height = 200 };
        scope.ShowAndWaitForContentRendered(host);
        try
        {
            item.ApplyTemplate();
            ContentPresenter headerHost = item.Template.FindName("HeaderHost", item) as ContentPresenter
                ?? throw new AssertFailedException("見出しの描画部品がありません。");
            return FindAccessText(headerHost)?.AccessKey;
        }
        finally
        {
            panel.Children.Clear();
            host.Content = null;
        }
    }

    /// <summary>項目の元データから、親のItemContainerStyleを適用した入れ物を生成して返します。</summary>
    internal static MenuItem GenerateContainer(ItemsControl parent, int index)
    {
        var generator = (IItemContainerGenerator)parent.ItemContainerGenerator;
        using (generator.StartAt(generator.GeneratorPositionFromIndex(index), GeneratorDirection.Forward, allowStartAtRealizedItem: true))
        {
            var container = (MenuItem)generator.GenerateNext(out _);
            generator.PrepareItemContainer(container);
            return container;
        }
    }

    private static AccessText? FindAccessText(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is AccessText access) { return access; }
            if (FindAccessText(child) is { } nested) { return nested; }
        }
        return null;
    }

    private static TextBlock? FindTextBlock(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock text) { return text; }
            if (FindTextBlock(child) is { } nested) { return nested; }
        }
        return null;
    }
}
