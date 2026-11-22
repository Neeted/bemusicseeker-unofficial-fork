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

/// <summary>表示フォントの解決とアプリのリソースへの適用を確認します。Arial、Yu Gothic UI、Meiryo UIがインストールされていることを前提にします。</summary>
[TestClass]
public sealed class AppFontServiceTests
{
    /// <summary>
    /// 用途ごとにArialの英字と、かなの代替字形を描き、未インストールの名前と空文字では用途ごとの既定だけで描くことを確認します。
    /// メニュー・状態表示・メッセージ用のシステムのキーは通常UIのフォントを使い、どちらのフォントもテーマの切替後に維持されます。
    /// </summary>
    /// <remarks>既定の代替がなくOSの代替フォントへ落ちる誤り、標準部品がシステムのフォントのまま残る誤り、配色辞書へ置いてテーマ切替で失う誤りを検出します。</remarks>
    [TestMethod]
    [DoNotParallelize]
    [DataRow(false, "Yu Gothic UI", "Meiryo UI")]
    [DataRow(true, "Meiryo UI", "Yu Gothic UI")]
    public void ApplyFontFamily_UsesSelectedFontThenDefaultAndSurvivesThemeChange(bool table, string defaultName, string otherDefaultName)
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            Application application = Application.Current;
            string originalTheme = BeMusicSeeker.Properties.Settings.Default.AppearanceTheme;
            AppFontRole role = table ? AppFontRole.Table : AppFontRole.Ui;
            string resourceKey = table ? "App.TableFontFamily" : "App.UiFontFamily";
            string otherResourceKey = table ? "App.UiFontFamily" : "App.TableFontFamily";
            using var snapshot = AppFontResourceSnapshot.Capture();
            try
            {
                string arial = GlyphFontProbe.FontUri(new FontFamily("Arial"), "A");
                string defaultLatin = GlyphFontProbe.FontUri(new FontFamily(defaultName), "A");
                string defaultKana = GlyphFontProbe.FontUri(new FontFamily(defaultName), "あ");
                Assert.AreNotEqual(arial, defaultLatin, $"Arialと{defaultName}を区別できる環境が前提です。");
                Assert.AreNotEqual(defaultKana, GlyphFontProbe.FontUri(new FontFamily(otherDefaultName), "あ"), "用途ごとの既定でかなの字形が異なる環境が前提です。");

                ResourceDictionary[] dictionaries = [.. application.Resources.MergedDictionaries];
                AppFontService.ApplyFontFamily(string.Empty, table ? AppFontRole.Ui : AppFontRole.Table);
                object other = application.Resources[otherResourceKey];
                AppFontService.ApplyFontFamily("Arial", role);
                CollectionAssert.AreEqual(dictionaries, application.Resources.MergedDictionaries.ToArray());
                var selected = (FontFamily)application.Resources[resourceKey];
                Assert.AreEqual(arial, GlyphFontProbe.FontUri(selected, "A"));
                Assert.AreEqual(defaultKana, GlyphFontProbe.FontUri(selected, "あ"));
                foreach (ResourceKey key in new[] { SystemFonts.MenuFontFamilyKey, SystemFonts.StatusFontFamilyKey, SystemFonts.MessageFontFamilyKey })
                {
                    Assert.AreSame(table ? other : selected, application.Resources[key]);
                }

                foreach (string theme in new[] { AppThemeService.Dark, AppThemeService.Light })
                {
                    AppThemeService.ApplyTheme(theme);
                    Assert.AreSame(selected, application.FindResource(resourceKey));
                    Assert.AreSame(other, application.FindResource(otherResourceKey));
                }

                foreach (string name in new[] { "BeMusicSeeker No Such Font", string.Empty })
                {
                    AppFontService.ApplyFontFamily(name, role);
                    var fallback = (FontFamily)application.Resources[resourceKey];
                    Assert.AreEqual(defaultLatin, GlyphFontProbe.FontUri(fallback, "A"), name);
                    Assert.AreEqual(defaultKana, GlyphFontProbe.FontUri(fallback, "あ"), name);
                    Assert.AreSame(table ? other : fallback, application.Resources[SystemFonts.MenuFontFamilyKey]);
                    Assert.AreSame(other, application.Resources[otherResourceKey]);
                }
            }
            finally
            {
                AppThemeService.ApplyTheme(originalTheme);
            }
        });
    }

    /// <summary>
    /// Arial、用途ごとの既定、空文字、未インストールの名前のどれから作ったフォントでも、一行の高さは文字サイズの1.27倍になります。
    /// 基線は第一候補の大文字の高さが行の上下中央に来る位置になり、空文字と未インストールの名前は用途ごとの既定の大文字の高さを使います。
    /// </summary>
    /// <remarks>フォントが申告する行の高さや基線をそのまま使う誤りと、どのフォントにも既定のフォントの大文字の高さを使う誤りを検出します。</remarks>
    [TestMethod]
    [DataRow(false, "Yu Gothic UI")]
    [DataRow(true, "Meiryo UI")]
    public void CreateFontFamily_FixesLineHeightAndCentersCapitalHeightForEveryFont(bool table, string defaultName)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const double size = 100d;
            static double CapsHeight(string name) => new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).CapsHeight;
            double arialCaps = CapsHeight("Arial"), defaultCaps = CapsHeight(defaultName);
            Assert.IsTrue(Math.Abs(arialCaps - defaultCaps) * size / 2d >= 0.5d, "二つのフォントの大文字の高さで基線を区別できる環境が前提です。");
            AppFontRole role = table ? AppFontRole.Table : AppFontRole.Ui;
            foreach ((FontFamily family, double capsHeight, string label) in new[]
            {
                (AppFontService.CreateFontFamily("Arial", role), arialCaps, "Arial"),
                (AppFontService.CreateFontFamily(defaultName, role), defaultCaps, defaultName),
                (AppFontService.CreateFontFamily(string.Empty, role), defaultCaps, "empty"),
                (AppFontService.CreateFontFamily("BeMusicSeeker No Such Font", role), defaultCaps, "uninstalled")
            })
            {
                var text = new FormattedText("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1d);
                Assert.AreEqual(1.27d * size, text.Height, 0.05d, label);
                Assert.AreEqual((1.27d + capsHeight) / 2d * size, text.Baseline, 0.05d, label);
            }
        });
    }

    /// <summary>
    /// 選択欄の候補を描くフォントは、Arialでは英字がArial、Arialに字形がないかなが用途ごとの既定になります。
    /// 空文字、未インストールの名前、記号フォント（Wingdings、Webdings、Symbol、Marlett）では用途ごとの既定だけになることを確認します。
    /// どの候補でも一行の高さは文字サイズの1.27倍で、基線は描画に使う第一候補の大文字の高さを行の上下中央に置きます。
    /// アプリ全体へ適用するフォントは、記号フォントでも選択されたフォントのままです。
    /// </summary>
    /// <remarks>
    /// 既定への代替がない誤り、記号フォントの名前を記号の字形で描く誤り、すべての候補を既定で描く誤り、
    /// 記号フォントの判定をアプリ全体の適用へ広げる誤りを検出します。上記の記号フォントがインストールされていることを前提にします。
    /// </remarks>
    [TestMethod]
    [DataRow(false, "Yu Gothic UI")]
    [DataRow(true, "Meiryo UI")]
    public void CreatePreviewFontFamily_UsesCandidateFontExceptSymbolFontsWithFixedLineMetrics(bool table, string defaultName)
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const double size = 100d;
            static double CapsHeight(string name) => new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).CapsHeight;
            string arial = GlyphFontProbe.FontUri(new FontFamily("Arial"), "A");
            string defaultLatin = GlyphFontProbe.FontUri(new FontFamily(defaultName), "A");
            string defaultKana = GlyphFontProbe.FontUri(new FontFamily(defaultName), "あ");
            double defaultCaps = CapsHeight(defaultName);
            AppFontRole role = table ? AppFontRole.Table : AppFontRole.Ui;
            Assert.AreNotEqual(arial, defaultLatin, $"Arialと{defaultName}を区別できる環境が前提です。");
            foreach ((string name, string expectedLatin, double capsHeight) in new[]
            {
                ("Arial", arial, CapsHeight("Arial")),
                (string.Empty, defaultLatin, defaultCaps),
                ("BeMusicSeeker No Such Font", defaultLatin, defaultCaps),
                ("Wingdings", defaultLatin, defaultCaps),
                ("Webdings", defaultLatin, defaultCaps),
                ("Symbol", defaultLatin, defaultCaps),
                ("Marlett", defaultLatin, defaultCaps)
            })
            {
                FontFamily preview = AppFontService.CreatePreviewFontFamily(name, role);
                Assert.AreEqual(expectedLatin, GlyphFontProbe.FontUri(preview, "A"), name);
                Assert.AreEqual(defaultKana, GlyphFontProbe.FontUri(preview, "あ"), name);
                var text = new FormattedText("H", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(preview, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), size, Brushes.Black, 1d);
                Assert.AreEqual(1.27d * size, text.Height, 0.05d, name);
                Assert.AreEqual((1.27d + capsHeight) / 2d * size, text.Baseline, 0.05d, name);
            }

            string wingdings = GlyphFontProbe.FontUri(new FontFamily("Wingdings"), "A");
            Assert.AreNotEqual(defaultLatin, wingdings, "Wingdingsがインストールされ、英字を自身の字形で描く環境が前提です。");
            Assert.AreEqual(wingdings, GlyphFontProbe.FontUri(AppFontService.CreateFontFamily("Wingdings", role), "A"));
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
                AppFontService.ApplyFontFamily("Arial", AppFontRole.Ui);
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
                    AppFontService.ApplyFontFamily(name, AppFontRole.Ui);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var expected = (FontFamily)Application.Current.Resources["App.UiFontFamily"];
                    Assert.AreEqual(GlyphFontProbe.FontUri(new FontFamily(name), "A"), GlyphFontProbe.FontUri(expected, "A"), name);
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
