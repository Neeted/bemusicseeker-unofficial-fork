using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.Views;

namespace BeMusicSeeker.Windows.Tests.Themes;

/// <summary>表示フォントの解決とアプリのリソースへの適用を確認します。ArialとMeiryo UIがインストールされていることを前提にします。</summary>
[TestClass]
public sealed class AppFontServiceTests
{
    /// <summary>
    /// Arialを適用すると英字はArial、Arialに字形がないかなは既定のMeiryo UIで描かれ、未インストールの名前と空文字は既定だけで描かれることを確認します。
    /// メニュー・状態表示・メッセージ用のシステムのキーも同じフォントになり、テーマを切り替えても維持されます。
    /// </summary>
    /// <remarks>既定の代替がなくOSの代替フォントへ落ちる誤り、標準部品がシステムのフォントのまま残る誤り、配色辞書へ置いてテーマ切替で失う誤りを検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ApplyFontFamily_UsesSelectedFontThenDefaultAndSurvivesThemeChange()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            Application application = Application.Current;
            string originalTheme = BeMusicSeeker.Properties.Settings.Default.AppearanceTheme;
            using var snapshot = AppFontResourceSnapshot.Capture();
            try
            {
                string arial = GlyphFontUri(new FontFamily("Arial"), "A");
                string meiryoUiLatin = GlyphFontUri(new FontFamily("Meiryo UI"), "A");
                string meiryoUiKana = GlyphFontUri(new FontFamily("Meiryo UI"), "あ");
                Assert.AreNotEqual(arial, meiryoUiLatin, "ArialとMeiryo UIを区別できる環境が前提です。");

                ResourceDictionary[] dictionaries = [.. application.Resources.MergedDictionaries];
                AppFontService.ApplyFontFamily("Arial");
                CollectionAssert.AreEqual(dictionaries, application.Resources.MergedDictionaries.ToArray());
                var selected = (FontFamily)application.Resources[AppFontService.ResourceKey];
                Assert.AreEqual(arial, GlyphFontUri(selected, "A"));
                Assert.AreEqual(meiryoUiKana, GlyphFontUri(selected, "あ"));
                foreach (ResourceKey key in new[] { SystemFonts.MenuFontFamilyKey, SystemFonts.StatusFontFamilyKey, SystemFonts.MessageFontFamilyKey })
                {
                    Assert.AreSame(selected, application.Resources[key]);
                }

                AppThemeService.ApplyTheme(AppThemeService.Dark);
                Assert.AreSame(selected, application.FindResource(AppFontService.ResourceKey));
                AppThemeService.ApplyTheme(AppThemeService.Light);
                Assert.AreSame(selected, application.FindResource(AppFontService.ResourceKey));

                foreach (string name in new[] { "BeMusicSeeker No Such Font", string.Empty })
                {
                    AppFontService.ApplyFontFamily(name);
                    var fallback = (FontFamily)application.Resources[AppFontService.ResourceKey];
                    Assert.AreEqual(meiryoUiLatin, GlyphFontUri(fallback, "A"), name);
                    Assert.AreEqual(meiryoUiKana, GlyphFontUri(fallback, "あ"), name);
                    Assert.AreSame(fallback, application.Resources[SystemFonts.MenuFontFamilyKey]);
                }
            }
            finally
            {
                AppThemeService.ApplyTheme(originalTheme);
            }
        });
    }

    /// <summary>
    /// 表示中のリリースノートのウィンドウで、フォントをArialからMeiryo UIへ適用し直すたびに、ウィンドウ、継承する文字、文書、
    /// 明示スタイルのないメニューとツールチップが同じフォントへ追随し、文書の文字サイズは変わらないことを確認します。
    /// </summary>
    /// <remarks>静的な参照で再表示まで反映されない誤りと、標準スタイルがシステムのフォントを指定する部品の取りこぼしを検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    public void ApplyFontFamily_ShownWindowDocumentMenuAndToolTipFollow()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var snapshot = AppFontResourceSnapshot.Capture();
            var window = new ReleaseNotesWindow(UndeclaredDialogs.Create());
            var menuHost = new ThemedWindow { Width = 240, Height = 120 };
            var inherited = new TextBlock { Text = "inherited" };
            var menu = new Menu();
            menu.Items.Add(new MenuItem { Header = "menu" });
            menuHost.Content = new StackPanel { Children = { menu, inherited } };
            var toolTip = new ToolTip { Content = "tooltip", PlacementTarget = menuHost, Placement = PlacementMode.Bottom, StaysOpen = true };
            scope.TrackToolTip(toolTip);
            try
            {
                AppFontService.ApplyFontFamily("Arial");
                scope.ShowAndWaitForContentRendered(window);
                scope.ShowAndWaitForContentRendered(menuHost);
                // 標準スタイルの解決は実際の表示で起きるため、ToolTipを開いたまま観測する。
                var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                toolTip.Opened += (_, _) => opened.TrySetResult();
                toolTip.IsOpen = true;
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(opened.Task, "ToolTip.Opened");
                FlowDocument document = FindVisualDescendants<FlowDocumentScrollViewer>(window).Single().Document;
                double documentFontSize = document.FontSize;
                foreach (string name in new[] { "Arial", "Meiryo UI", "Arial" })
                {
                    AppFontService.ApplyFontFamily(name);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var expected = (FontFamily)Application.Current.Resources[AppFontService.ResourceKey];
                    Assert.IsTrue(expected.Source.StartsWith(name, StringComparison.Ordinal), expected.Source);
                    Assert.AreEqual(expected, window.FontFamily, name);
                    Assert.AreEqual(expected, document.FontFamily, name);
                    Assert.AreEqual(documentFontSize, document.FontSize, name);
                    Assert.AreEqual(expected, menuHost.FontFamily, name);
                    Assert.AreEqual(expected, inherited.FontFamily, name);
                    Assert.AreEqual(expected, menu.FontFamily, name);
                    Assert.AreEqual(expected, toolTip.FontFamily, name);
                }
            }
            finally
            {
                try
                {
                    if (toolTip.IsOpen)
                    {
                        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        toolTip.Closed += (_, _) => closed.TrySetResult();
                        toolTip.IsOpen = false;
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "ToolTip.Closed");
                    }
                }
                finally
                {
                    menuHost.Close();
                    window.Close();
                }
            }
        });
    }

    private static string GlyphFontUri(FontFamily family, string text)
    {
        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawText(
                new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 16d, Brushes.Black, 1d),
                new Point());
        }
        // 同じファイルに複数の書体を持つフォントを区別するため、書体番号を含むURI全体で比較する。
        return GlyphRuns(visual.Drawing).Single().GlyphTypeface.FontUri.ToString();
    }

    private static IEnumerable<GlyphRun> GlyphRuns(Drawing drawing)
    {
        return drawing switch
        {
            DrawingGroup group => group.Children.SelectMany(GlyphRuns),
            GlyphRunDrawing run => [run.GlyphRun],
            _ => []
        };
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in FindVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
