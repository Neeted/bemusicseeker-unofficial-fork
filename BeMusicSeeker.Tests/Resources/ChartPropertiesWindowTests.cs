using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <remarks>実Windowと共有Applicationの資源を使う表示・標準コマンド境界だけを非並列で確認します。解析条件とTask寿命の組合せは画面なしのテストへ分担します。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class ChartPropertiesWindowTests
{
    /// <summary>同じ実Windowの言語変更で確認基準・理由・なし・形式説明のBindingを更新し、元パスとメタデータを保ちます。三分類の右下16 DIP保護・読取専用選択・標準コピー・Esc閉鎖も維持します。</summary>
    [TestMethod]
    public void Window_DisplaysReadOnlyCategoriesSupportsNormalCopyAndClosesWithEscape()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo? previousCulture = Resources.Culture;
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            using var model = new ChartPropertiesViewModel(ChartTestValues.Empty() with { Path = "chart.bms" }, () =>
            {
                entered.TrySetResult();
                release.Wait();
                return new ChartResourceDiagnostics("Title", "Artist",
                    [new ChartResourceDiagnosticIssue(new ChartResourceDefinition("#WAV", "01", ChartResourceReference.Parse("missing.wav", ChartResourceKind.Audio)), ChartResourceDiagnosticReason.Missing)],
                    [], []);
            }, new ChartResourcePlacement(ChartResourcePlacementBasis.FirstCandidate, @"C:\Candidate"));
            var window = new ChartPropertiesWindow(model);
            Task? operation = null;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                scope.ShowAndWaitForContentRendered(window);
                operation = model.LoadAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    TestUiDispatcherHost.AwaitNotificationAsync(entered.Task, operation, "chart-properties-window-read"), "chart-properties-window-read");
                Assert.AreEqual(Visibility.Collapsed, ((TextBox)window.FindName("readStatus")).Visibility);
                Assert.AreEqual(0d, ((TextBox)window.FindName("readStatus")).DesiredSize.Height);
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "chart-properties-window-result");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Collapsed, ((TextBox)window.FindName("readStatus")).Visibility);
                Assert.AreEqual(0d, ((TextBox)window.FindName("readStatus")).DesiredSize.Height);
                foreach (string culture in new[] { "ja-JP", "en-US", "ja-JP" })
                {
                    ResourceService.Current.ChangeCulture(culture);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.ChartPropertiesFirstCandidate, ((TextBox)window.FindName("valueVerificationBasis")).Text);
                    Assert.AreEqual("#WAV01 missing.wav (" + Resources.ChartPropertiesMissing + ")", ((TextBox)window.FindName("listIssues")).Text);
                    var unused = (TextBox)window.FindName("listUnusedDefinitions");
                    var undefined = (TextBox)window.FindName("listUndefinedReferences");
                    Assert.AreEqual(Resources.None, unused.Text);
                    Assert.AreEqual(Resources.None, undefined.Text);
                    Assert.AreEqual(Resources.ChartPropertiesUnusedDescription, ((Grid)unused.Parent).Children.OfType<TextBlock>().Single().Text);
                    Assert.AreEqual(Resources.ChartPropertiesUndefinedDescription, ((Grid)undefined.Parent).Children.OfType<TextBlock>().Single().Text);
                    Assert.AreEqual("Title", ((TextBox)window.FindName("valueTitle")).Text);
                    Assert.AreEqual("Artist", ((TextBox)window.FindName("valueArtist")).Text);
                    Assert.AreEqual("chart.bms", ((TextBox)window.FindName("valuePath")).Text);
                    Assert.AreEqual(@"C:\Candidate", ((TextBox)window.FindName("valueVerificationPath")).Text);
                }
                Assert.AreEqual(ResizeMode.CanResize, window.ResizeMode);
                Assert.AreEqual("Title", ((TextBox)window.FindName("valueTitle")).Text);
                Assert.AreEqual("Artist", ((TextBox)window.FindName("valueArtist")).Text);
                Assert.AreEqual("chart.bms", ((TextBox)window.FindName("valuePath")).Text);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.ChartPropertiesFirstCandidate, ((TextBox)window.FindName("valueVerificationBasis")).Text);
                Assert.AreEqual(@"C:\Candidate", ((TextBox)window.FindName("valueVerificationPath")).Text);
                foreach (string name in new[] { "valueTitle", "valueArtist", "valuePath", "valueVerificationBasis", "valueVerificationPath" })
                {
                    var value = (TextBox)window.FindName(name);
                    Assert.IsTrue(value.IsReadOnly);
                    ApplicationCommands.SelectAll.Execute(null, value);
                    Assert.AreEqual(value.Text, value.SelectedText);
                    Assert.IsTrue(ApplicationCommands.Copy.CanExecute(null, value));
                }
                var navigation = (ListBox)window.FindName("categoryNavigation");
                string[] names = ["listIssues", "listUnusedDefinitions", "listUndefinedReferences"];
                string[] expected = ["missing.wav", Resources.None, Resources.None];
                for (int i = 0; i < names.Length; i++)
                {
                    navigation.SelectedIndex = i;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    var text = (TextBox)window.FindName(names[i]);
                    Assert.IsTrue(text.IsVisible);
                    Assert.IsTrue(text.IsReadOnly);
                    Assert.AreSame(text.FindResource("ResourceList"), text.Style, "三分類は同じ保護域を持つ入力styleへ接続する。");
                    Assert.AreEqual(16d, text.Padding.Right);
                    Assert.AreEqual(16d, text.Padding.Bottom);
                    Assert.AreEqual(VerticalAlignment.Top, text.VerticalContentAlignment);
                    Assert.IsTrue(text.GetRectFromCharacterIndex(0).Top < text.ActualHeight / 2);
                    Assert.IsTrue(text.Focusable);
                    StringAssert.Contains(text.Text, expected[i]);
                    Assert.IsTrue(ApplicationCommands.SelectAll.CanExecute(null, text));
                    ApplicationCommands.SelectAll.Execute(null, text);
                    Assert.AreEqual(text.Text, text.SelectedText);
                    Assert.IsTrue(ApplicationCommands.Copy.CanExecute(null, text));
                }
                var source = PresentationSource.FromVisual(window);
                Assert.IsNotNull(source);
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Escape)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                });
                Assert.IsFalse(window.IsVisible);
            }
            finally
            {
                window.Close();
                model.Close();
                release.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation ?? Task.CompletedTask, "chart-properties-window-finally");
                ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                Resources.Culture = previousCulture;
            }
        });
    }
    /// <summary>失敗欄のBindingは同じ実Windowの言語変更で再翻訳され、例外本文を表示しません。枠付きの読取専用欄と標準の全選択・コピーを保ち、開始済みTaskを回収します。</summary>
    [TestMethod]
    public void Window_ShowsBorderedSelectableFailureOnlyAfterReadFailure()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo? previousCulture = Resources.Culture;
            using var model = new ChartPropertiesViewModel(ChartTestValues.Empty(), () => throw new IOException("private failure"));
            var window = new ChartPropertiesWindow(model);
            Task? operation = null;
            try
            {
                ResourceService.Current.ChangeCulture("ja-JP");
                var status = (TextBox)window.FindName("readStatus");
                Assert.AreEqual(Visibility.Collapsed, status.Visibility);
                scope.ShowAndWaitForContentRendered(window);
                operation = model.LoadAsync();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "chart-properties-failure-result");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(status.IsVisible);
                Assert.IsTrue(status.IsReadOnly);
                Assert.IsTrue(status.BorderThickness.Left > 0);
                Assert.AreEqual(((TextBox)window.FindName("valuePath")).BorderThickness, status.BorderThickness);
                foreach (string culture in new[] { "ja-JP", "en-US", "ja-JP" })
                {
                    ResourceService.Current.ChangeCulture(culture);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(Resources.ChartPropertiesFailed, status.Text);
                    Assert.IsFalse(status.Text.Contains("private failure", System.StringComparison.Ordinal));
                    Assert.IsTrue(status.IsVisible);
                }
                ApplicationCommands.SelectAll.Execute(null, status);
                Assert.AreEqual(status.Text, status.SelectedText);
                Assert.IsTrue(ApplicationCommands.Copy.CanExecute(null, status));
            }
            finally
            {
                window.Close();
                model.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation ?? Task.CompletedTask, "chart-properties-failure-finally");
                ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                Resources.Culture = previousCulture;
            }
        });
    }

}
