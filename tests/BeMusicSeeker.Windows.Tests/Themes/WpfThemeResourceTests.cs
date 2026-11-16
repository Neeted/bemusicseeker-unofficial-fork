using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Settings;
using Parago.Windows;

namespace BeMusicSeeker.Windows.Tests.Themes;

[TestClass]
public sealed class WpfThemeResourceTests
{
    /// <summary>利用者設定に依存しないテスト用Lightテーマの起動で意味別ブラシを解決できることを確認し、テーマ未適用による画面検証の不安定化を検出します。</summary>
    [TestMethod]
    public void Startup_AppliesLightComponentThemeWithSemanticResources()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current;
            Assert.IsNotNull(application);
            ResourceDictionary theme = application.Resources.MergedDictionaries.Single(dictionary =>
                string.Equals(
                    dictionary.Source?.OriginalString,
                    "/BeMusicSeeker;component/Themes/Light.xaml",
                    StringComparison.OrdinalIgnoreCase));

            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.BackgroundBrush"]);
            Assert.IsInstanceOfType<SolidColorBrush>(theme["App.TextBrush"]);
            Assert.AreEqual(AppThemeService.Light, BeMusicSeeker.Properties.Settings.Default.AppearanceTheme);
        });
    }

    /// <summary>設定・初期設定・プレイリスト・読込・進捗の実コンストラクターでテンプレートに意味別ブラシが届くことを確認し、表示前のコンポーネント資源接続漏れを検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void CompiledDialogSurfaces_ResolveSemanticBrushesOnConstructorOnlyControls()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var settingsWindow = new SettingsWindow(UndeclaredDialogs.Create());
            var settingsOperationRoot = (Grid)settingsWindow.FindName("settingDialogOperationGrid");
            AssertBrush(settingsOperationRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            AssertBrush(settingsWindow.Background, "App.DialogBackgroundBrush");
            var warningField = new SettingsField
            {
                Header = "header",
                ValidationMessage = "warning",
                Content = new TextBox(),
                Style = (Style)settingsWindow.FindResource(typeof(SettingsField))
            };
            warningField.ApplyTemplate();
            TextBlock warningText = FindVisualDescendantsInVisualTree<TextBlock>(warningField)
                .Single(textBlock => textBlock.Text == "warning");
            AssertBrush(warningText.Foreground, "App.WarningTextBrush");

            var initialSetupDialog = new InitialSetupLanguageDialog();
            Grid initialSetupRoot = FindVisualDescendants<Grid>(initialSetupDialog).Single();
            Rectangle overlay = FindVisualDescendants<Rectangle>(initialSetupDialog).Single();
            Border initialSetupSurface = FindVisualDescendants<Border>(initialSetupDialog).Single();
            AssertBrush(initialSetupRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            AssertBrush(overlay.Fill, "App.DialogOverlayBrush");
            AssertBrush(initialSetupSurface.Background, "App.DialogBackgroundBrush");
            AssertBrush(initialSetupSurface.BorderBrush, "App.DialogBorderBrush");

            var playlistPropertyDialog = new PlaylistPropertyDialog(UndeclaredDialogs.Create());
            var playlistPropertyRoot = (Grid)playlistPropertyDialog.Content;
            AssertBrush(playlistPropertyRoot.GetValue(TextElement.ForegroundProperty), "App.TextBrush");
            Assert.IsInstanceOfType<Style>(playlistPropertyRoot.Resources["App.Canonical.DialogActionStyle"]);

            var importDialog = new PlaylistImportDialog(new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy)), UndeclaredDialogs.Create());
            try
            {
                AssertBrush(importDialog.Background, "App.DialogBackgroundBrush");
                AssertBrush(importDialog.Foreground, "App.TextBrush");
                Assert.IsInstanceOfType<FrameworkElement>(importDialog.Content);
                Assert.IsInstanceOfType<Style>(importDialog.Resources["App.Canonical.DialogActionStyle"]);
            }
            finally { importDialog.Close(); }

            var pendingDeleteDialog = new PendingDeleteConfirmDialog();
            AssertBrush(pendingDeleteDialog.Background, "App.DialogBackgroundBrush");
            AssertBrush(pendingDeleteDialog.Foreground, "App.TextBrush");

            var progressDialog = new ProgressDialog(new ProgressDialogSettings());
            AssertBrush(progressDialog.Background, "App.DialogBackgroundBrush");
            AssertBrush(progressDialog.Foreground, "App.TextBrush");
            Assert.IsInstanceOfType<FrameworkElement>(progressDialog.Content);
            Assert.IsInstanceOfType<Style>(progressDialog.Resources["App.Canonical.DialogActionStyle"]);
        });
    }

    private static void AssertBrush(object value, string resourceKey)
    {
        Assert.IsInstanceOfType(value, typeof(SolidColorBrush), resourceKey + " must resolve to a SolidColorBrush.");
        Assert.AreSame(
            Application.Current.Resources[resourceKey],
            value,
            resourceKey + " must resolve through the application semantic resource.");
    }


    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (object childValue in LogicalTreeHelper.GetChildren(root))
        {
            if (childValue is not DependencyObject child)
            {
                continue;
            }

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

    private static IEnumerable<T> FindVisualDescendantsInVisualTree<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T descendant in FindVisualDescendantsInVisualTree<T>(child))
            {
                yield return descendant;
            }
        }
    }
}
