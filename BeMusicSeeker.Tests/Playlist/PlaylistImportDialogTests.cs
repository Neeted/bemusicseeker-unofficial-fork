using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>URI入力の追記と実JSON picker接続・取消・応答性を検証する。</summary>
/// <remarks>compiledボタンからpicker要求と入力へ渡す接続を実Controlで確認する。表示領域だけ実Window表示を使い、ダウンロード・登録は取込み所有者へ分担する。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistImportDialogTests
{
    /// <summary>既存URIへ空白を除いた新URIを改行で追加し、空入力と空白だけの追加を区別する。既存内容の上書きと空行の増殖を検出する。</summary>
    [TestMethod]
    public void AppendUriInputLine_AppendsToEndWithNewLine()
    {
        using var vm = new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy));
        vm.UriInput = "https://example.com/first.json";
        vm.AppendInputLine("  file:///C:/tables/second.json  ");
        Assert.AreEqual("https://example.com/first.json" + Environment.NewLine + "file:///C:/tables/second.json", vm.UriInput);
        vm.UriInput = "";
        vm.AppendInputLine("file:///C:/tables/first.json");
        Assert.AreEqual("file:///C:/tables/first.json", vm.UriInput);
        vm.UriInput = "existing";
        vm.AppendInputLine(" ");
        Assert.AreEqual("existing", vm.UriInput);
    }

    /// <summary>標準と拡大フォントの実描画でURI入力と全footerが領域内に収まり重ならない。文字拡大と短文/大量長文を跨いで右下16 DIP保護・枠内2 DIP・末尾文字の非遮蔽を確認する。</summary>
    [DoNotParallelize]
    [TestMethod]
    public void Presentation_ContainsUriInputAboveFooterAtDefaultAndFontScale()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            PlaylistImportDialog dialog = CreateDialog();
            PlaylistImportDialog host = dialog;
            ((ListBox)dialog.FindName("importNavigation")).SelectedIndex = 2;
            try
            {
                windowTest.ShowAndWaitForContentRendered(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                var content = (FrameworkElement)dialog.Content;
                Button[] footerButtons = FindVisualDescendants<Button>(dialog)
                    .Where(button => button.TemplatedParent == null)
                    .Where(button => button.Name is "openLocalFileButton" or "addUriButton" or "startImportButton" or "cancelImportButton")
                    .ToArray();
                Assert.IsTrue(footerButtons.Length > 0);

                // 標準相当の文字サイズと3倍の拡大で、入力と全操作が領域内に収まることを確認する。
                foreach (double fontSize in new[] { 12d, 36d })
                {
                    TextElement.SetFontSize(dialog, fontSize);
                    host.UpdateLayout();
                    foreach (string text in new[] { "https://example.test/short.json", string.Join(Environment.NewLine,
                        Enumerable.Range(0, 80).Select(index => "https://example.test/" + new string('x', 180) + index + ".json")) })
                    {
                        SetText(input, text);
                        host.UpdateLayout();
                        Assert.AreEqual(16d, input.Padding.Right);
                        Assert.AreEqual(16d, input.Padding.Bottom);
                        var viewer = (ScrollViewer)input.Template.FindName("PART_ContentHost", input);
                        Assert.AreEqual(input.Padding, viewer.Padding, "入力余白は内部hostへ一度だけ転送する。");
                        if (input.LineCount > 1)
                        {
                            input.ScrollToEnd();
                            input.ScrollToHorizontalOffset(double.MaxValue);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            host.UpdateLayout();
                            var vertical = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
                            var horizontal = (ScrollBar)viewer.Template.FindName("PART_HorizontalScrollBar", viewer);
                            Assert.AreEqual(Visibility.Visible, vertical.Visibility);
                            Assert.AreEqual(Visibility.Visible, horizontal.Visibility);
                            foreach (ScrollBar bar in new[] { vertical, horizontal })
                            {
                                bar.ApplyTemplate();
                                var track = (Track)bar.Template.FindName("PART_Track", bar);
                                track.Thumb.ApplyTemplate();
                                var chrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                                Rect chromeBounds = GetVisualBounds(input, chrome);
                                Assert.AreEqual(2d, bar.Orientation == Orientation.Vertical
                                    ? input.ActualWidth - input.BorderThickness.Right - chromeBounds.Right
                                    : input.ActualHeight - input.BorderThickness.Bottom - chromeBounds.Bottom, 0.25d);
                            }
                            Rect lastCharacter = input.GetRectFromCharacterIndex(input.Text.Length - 1, true);
                            Assert.IsFalse(lastCharacter.IsEmpty);
                            Assert.IsTrue(lastCharacter.Width >= 0d && lastCharacter.Height > 0d);
                            Assert.IsTrue(lastCharacter.Left >= input.BorderThickness.Left - 0.25d && lastCharacter.Top >= input.BorderThickness.Top - 0.25d);
                            Assert.IsTrue(lastCharacter.Right <= GetVisualBounds(input, vertical).Left + 0.25d,
                                "末尾文字は縦バー入力領域の手前に露出する。");
                            Assert.IsTrue(lastCharacter.Bottom <= GetVisualBounds(input, horizontal).Top + 0.25d,
                                "末尾文字は横バー入力領域の手前に露出する。");
                        }
                    }

                    Assert.IsTrue(content.ActualWidth > 0d && content.ActualHeight > 0d);
                    AssertVisualBoundsInside(content, input);
                    foreach (Button footerButton in footerButtons)
                    {
                        AssertVisualBoundsInside(content, footerButton);
                    }

                    Rect inputBounds = GetVisualBounds(content, input);
                    double footerTop = footerButtons
                        .Select(button => GetVisualBounds(content, button).Top)
                        .Min();
                    Assert.IsTrue(
                        inputBounds.Bottom <= footerTop + 0.5d,
                        $"フォント{fontSize}で入力領域{inputBounds}がfooter上端{footerTop}を越えない。");
                }
                TextElement.SetFontSize(dialog, 12d);
                SetText(input, string.Join(Environment.NewLine, Enumerable.Range(0, 200).Select(i => "invalid URI line " + i)));
                ((Button)dialog.FindName("addUriButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                host.UpdateLayout();
                foreach (Button footerButton in footerButtons)
                {
                    AssertVisualBoundsInside(content, footerButton);
                }

                Assert.IsTrue(input.ActualHeight > 0d, "大量の不正行を表示中も入力と下部操作へ到達できる。");
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>compiledボタンは正しい所有WindowとJSON限定条件をpickerへ一回渡し、受理パスを既存URIへ追記する。誤った選択形式・所有者・入力先への配送を検出する。</summary>
    [TestMethod]
    public void OpenLocalFile_UsesInjectedJsonPickerAndAppendsAcceptedPathWithoutShowingModal()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            const string selectedPath = @"C:\tables\accepted.json";
            var dialogs = new RecordingLoadPlaylistDialogService(
                new UiFilePickerResult(UiDialogStatus.Accepted, [selectedPath]));
            PlaylistImportDialog dialog = CreateDialog(dialogs);
            PlaylistImportDialog host = dialog;
            ((ListBox)dialog.FindName("importNavigation")).SelectedIndex = 2;
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                SetText(input, "https://example.test/first.json");

                ((Button)dialog.FindName("openLocalFileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual(
                    "https://example.test/first.json" + Environment.NewLine + selectedPath,
                    input.Text);
                Assert.AreEqual(1, dialogs.FileRequests.Count);
                Assert.AreEqual(".json", dialogs.FileRequests[0].DefaultExtension);
                Assert.AreEqual(BeMusicSeeker.Properties.Resources.Json_file_exts, dialogs.FileRequests[0].Filter);
                // 翻訳される表示名とは別に、JSONだけを選択する形式指定を検証する。
                string[] filterParts = dialogs.FileRequests[0].Filter.Split('|');
                Assert.AreEqual(2, filterParts.Length);
                Assert.AreEqual("*.json", filterParts[1]);
                Assert.AreSame(host, dialogs.FileRequests[0].Owner);
                Assert.AreEqual(0, dialogs.MessageRequestCount);
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>pickerの取消と閉鎖は既存入力を保持し、メッセージ表示を起こさない。非受理結果での追記や誤通知を検出する。</summary>
    [DataTestMethod]
    // 明示取消とWindowの閉鎖はどちらも受理パスなしとして入力を維持する。
    [DataRow(UiDialogStatus.CancelledByUser)]
    [DataRow(UiDialogStatus.ClosedByUser)]
    public void OpenLocalFile_CancelOrClosedLeavesExistingInputUnchanged(object statusValue)
    {
        var status = (UiDialogStatus)statusValue;
        TestUiDispatcherHost.Invoke(() =>
        {
            var dialogs = new RecordingLoadPlaylistDialogService(new UiFilePickerResult(status));
            PlaylistImportDialog dialog = CreateDialog(dialogs);
            PlaylistImportDialog host = dialog;
            ((ListBox)dialog.FindName("importNavigation")).SelectedIndex = 2;
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                SetText(input, "existing input");

                ((Button)dialog.FindName("openLocalFileButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

                Assert.AreEqual("existing input", input.Text);
                Assert.AreEqual(0, dialogs.MessageRequestCount);
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>picker失敗は元例外を保持して呼出元へ返し、既存入力と通知件数を変えない。失敗を取消へ隠す処理と二重通知を検出する。</summary>
    [TestMethod]
    public void OpenLocalFile_FailedPickerPropagatesCauseWithoutMutatingInputOrShowingModal()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var sentinel = new IOException("picker sentinel");
            var dialogs = new RecordingLoadPlaylistDialogService(
                new UiFilePickerResult(UiDialogStatus.Failed, error: sentinel));
            PlaylistImportDialog dialog = CreateDialog(dialogs);
            PlaylistImportDialog host = dialog;
            ((ListBox)dialog.FindName("importNavigation")).SelectedIndex = 2;
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                SetText(input, "existing input");

                InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(
                    () => dialog.HandleOpenLocalFileAsync().GetAwaiter().GetResult());

                Assert.AreSame(sentinel, failure.InnerException);
                Assert.AreEqual("existing input", input.Text);
                Assert.AreEqual(0, dialogs.MessageRequestCount);
            }
            finally
            {
                host.Close();
            }
        });
    }

    /// <summary>picker gate中もDispatcher処理を進め、完了後にだけ受理パスを追記する。UI同期待ちと早期追記を検出する。</summary>
    [TestMethod]
    public void OpenLocalFileAsync_AwaitsPickerWithoutBlockingDispatcherAndAppliesCompletion()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var completion = new TaskCompletionSource<UiFilePickerResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var dialogs = new RecordingLoadPlaylistDialogService(
                new UiFilePickerResult(UiDialogStatus.CancelledByUser))
            {
                FileTask = completion.Task
            };
            PlaylistImportDialog dialog = CreateDialog(dialogs);
            PlaylistImportDialog host = dialog;
            ((ListBox)dialog.FindName("importNavigation")).SelectedIndex = 2;
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            Task? route = null;
            try
            {
                Materialize(host);
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                SetText(input, "existing input");
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(host.Dispatcher));

                route = dialog.HandleOpenLocalFileAsync();

                Assert.IsFalse(route.IsCompleted);
                bool dispatcherWorkCompleted = false;
                DispatcherOperation dispatcherWork = host.Dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => dispatcherWorkCompleted = true));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(dispatcherWork.Task, "URI-picker-dispatcher-work");
                Assert.IsTrue(dispatcherWorkCompleted);

                completion.TrySetResult(new UiFilePickerResult(
                    UiDialogStatus.Accepted,
                    [@"C:\tables\pending.json"]));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "URI-picker-route-terminal");

                Assert.AreEqual(
                    "existing input" + Environment.NewLine + @"C:\tables\pending.json",
                    input.Text);
            }
            finally
            {
                try
                {
                    completion.TrySetResult(new UiFilePickerResult(UiDialogStatus.CancelledByUser));
                    if (route != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(route, "URI-picker-finally-route"); }
                    host.Close();
                }
                finally
                {
                    SynchronizationContext.SetSynchronizationContext(previousContext);
                }
            }
        });
    }

    /// <summary>実三タブの選択と混在入力の有効行を共通一覧へ反映し、原文と無効行案内をBindingで保持します。追加では受付を呼ばず、開始時の一回だけ選択順のURIを渡します。</summary>
    [TestMethod]
    public void CompiledTabs_AddAllSourcesToOneOrderedSubmission()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            IReadOnlyList<Uri>? submitted = null;
            int calls = 0;
            var vm = new PlaylistImportDialogViewModel(uris => { submitted = uris; calls++; return new(PlaylistImportStartStatus.Busy); });
            var external = new PlaylistImportCandidate(new("https://example.test/external"), "External", Tag1: "Category");
            vm.UpdateCatalog([external], PlaylistImportCatalogState.Loaded);
            var dialog = new PlaylistImportDialog(vm, new RecordingLoadPlaylistDialogService(new(UiDialogStatus.CancelledByUser)));
            try
            {
                scope.ShowAndWaitForContentRendered(dialog);
                SetText((TextBox)dialog.FindName("searchInput"), "https://example.test/external");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var externalTree = (TreeView)dialog.FindName("externalCandidates");
                ToggleCandidate(externalTree, external.Uri);
                var navigation = (ListBox)dialog.FindName("importNavigation");
                navigation.SelectedIndex = 1;
                vm.BuiltInGroups[0].IsExpanded = true;
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var builtInUri = new Uri("bmseeker:table.estimation?type=easy");
                ToggleCandidate((TreeView)dialog.FindName("builtInCandidates"), builtInUri);
                navigation.SelectedIndex = 2;
                const string mixedInput = "https://example.test/direct\r\nnot a uri";
                var input = (TextBox)dialog.FindName("textBoxURIInput");
                SetText(input, mixedInput);
                ((Button)dialog.FindName("addUriButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var selected = (ListBox)dialog.FindName("selectedCandidates");
                Assert.AreEqual(3, selected.Items.Count);
                Assert.AreEqual(mixedInput, input.Text);
                var message = (TextBlock)dialog.FindName("operationMessage");
                StringAssert.Contains(message.Text, "not a uri");
                Assert.IsFalse(message.Text.Contains("https://example.test/direct", StringComparison.Ordinal));
                Assert.AreEqual(0, calls);
                ((Button)dialog.FindName("startImportButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.AreEqual(1, calls);
                Assert.IsNotNull(submitted);
                CollectionAssert.AreEqual(new[] { external.Uri.AbsoluteUri, builtInUri.AbsoluteUri, "https://example.test/direct" }, submitted.Select(u => u.AbsoluteUri).ToArray());
                Assert.IsTrue(dialog.IsVisible, "Busyの画面は選択を保って再操作できる。");
            }
            finally { dialog.Close(); vm.Dispose(); }
        });
    }

    /// <summary>共通受付の全結果を実Windowの実行ボタンへ返し、StartedだけCloseし、それ以外では理由と選択を残します。対象ゼロでも受付理由を到達可能にします。</summary>
    [DataTestMethod]
    [DataRow(PlaylistImportStartStatus.Started)]
    [DataRow(PlaylistImportStartStatus.Busy)]
    [DataRow(PlaylistImportStartStatus.NotReady)]
    [DataRow(PlaylistImportStartStatus.ShuttingDown)]
    [DataRow(PlaylistImportStartStatus.NoTargets)]
    public void StartButton_OnlyStartedClosesAndRefusalsKeepSelection(object statusValue)
    {
        var status = (PlaylistImportStartStatus)statusValue;
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var vm = new PlaylistImportDialogViewModel(_ => new(status, status == PlaylistImportStartStatus.Started ? 1 : 0, 7));
            vm.SetSelected(new(new("https://example.test/selected"), "Selected"), true);
            var dialog = new PlaylistImportDialog(vm, new RecordingLoadPlaylistDialogService(new(UiDialogStatus.CancelledByUser)));
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Closed += (_, _) => closed.TrySetResult();
            try
            {
                scope.ShowAndWaitForContentRendered(dialog);
                var start = (Button)dialog.FindName("startImportButton");
                Assert.IsTrue(start.IsEnabled);
                start.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (status == PlaylistImportStartStatus.Started)
                {
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "import-started-closed");
                    Assert.IsNull(dialog.DataContext);
                }
                else
                {
                    Assert.IsTrue(dialog.IsVisible);
                    Assert.AreEqual(1, vm.SelectedCount);
                    Assert.AreEqual(vm.Message, ((TextBlock)dialog.FindName("operationMessage")).Text);
                    Assert.IsFalse(string.IsNullOrEmpty(vm.Message));
                    if (status == PlaylistImportStartStatus.NoTargets)
                    {
                        StringAssert.Contains(vm.Message, "7");
                    }
                }
            }
            finally { dialog.Close(); }
        });
    }

    /// <summary>カテゴリ本文・余白・矢印の開閉と実チェックToggleを分離します。実フィルターから外部・内蔵の葉と空分類の行全体を隠し、再表示後の開閉・共有選択・順序・件数を保ちます。</summary>
    [TestMethod]
    public void CompiledCategoryRows_SeparateExpansionFromAggregateToggle()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var vm = new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy));
            var first = new PlaylistImportCandidate(new("https://example.test/first"), "First", Tag1: "Category");
            PlaylistImportCandidate second = first with { Uri = new("https://example.test/second"), Name = "Second" };
            vm.UpdateCatalog([first, second], PlaylistImportCatalogState.Loaded);
            vm.SetSelected(first, true);
            var dialog = new PlaylistImportDialog(vm, new RecordingLoadPlaylistDialogService(new(UiDialogStatus.CancelledByUser)));
            try
            {
                scope.ShowAndWaitForContentRendered(dialog);
                var tree = (TreeView)dialog.FindName("externalCandidates");
                var container = (TreeViewItem)tree.ItemContainerGenerator.ContainerFromIndex(0);
                var row = (Border)container.Template.FindName("CandidateRowChrome", container);
                Assert.IsFalse(vm.ExternalGroups[0].IsExpanded, "葉だけの外部分類は初期折畳み。");
                TextBlock label = FindVisualDescendants<TextBlock>(row).Single(x => x.Name == "candidateName");
                RaiseRowMouseDown(label);
                Assert.IsTrue(vm.ExternalGroups[0].IsExpanded); Assert.AreEqual(1, vm.SelectedCount);
                DependencyObject hit = row.InputHitTest(new Point(row.ActualWidth - row.Padding.Right / 2, row.ActualHeight / 2)) as DependencyObject ?? row;
                RaiseRowMouseDown(hit as UIElement ?? row);
                Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.AreEqual(1, vm.SelectedCount);
                var arrow = (ToggleButton)container.Template.FindName("ExpansionToggle", container);
                ToggleControl(arrow);
                Assert.IsTrue(vm.ExternalGroups[0].IsExpanded); Assert.AreEqual(1, vm.SelectedCount);
                CheckBox check = FindVisualDescendants<CheckBox>(row).Single();
                Assert.IsNull(check.IsChecked);
                ToggleControl(check); TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(true, check.IsChecked); Assert.AreEqual(2, vm.SelectedCount); Assert.IsTrue(vm.ExternalGroups[0].IsExpanded);
                ToggleControl(check); TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(false, check.IsChecked); Assert.AreEqual(0, vm.SelectedCount); Assert.IsTrue(vm.ExternalGroups[0].IsExpanded);
                var navigation = (ListBox)dialog.FindName("importNavigation");
                navigation.SelectedIndex = 1;
                TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                var builtInTree = (TreeView)dialog.FindName("builtInCandidates");
                CheckBox builtInCheck = FindVisualDescendants<CheckBox>(builtInTree).First(x => x.DataContext is PlaylistImportItem { IsGroup: true });
                ToggleControl(builtInCheck); TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(true, vm.BuiltInGroups[0].SelectionState);
                PlaylistImportCandidate[] builtIns = vm.BuiltInGroups.SelectMany(x => x.Children).Select(x => x.Candidate ?? throw new AssertFailedException("内蔵候補が必要です。")).ToArray();
                var hidden = new PlaylistImportCandidate(new("https://example.test/hidden"), "All same", Tag1: "Hidden category");
                var after = new PlaylistImportCandidate(new("https://example.test/after"), "After", Tag1: "After category");
                PlaylistImportCandidate alias = builtIns[0] with { Name = "Alias", NameKey = null, Tag1 = "Category" };
                vm.UpdateCatalog([first, second, alias, hidden, after], PlaylistImportCatalogState.Loaded);
                vm.ExternalGroups[1].IsExpanded = true;
                vm.BuiltInGroups[1].IsExpanded = false;
                vm.SetSelected(first, true); vm.SetSelected(hidden, true);
                string[] selection = vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray();
                List<PlaylistImportExistingTable> registered = [new("All same", first.Uri, null), new(vm.Text(builtIns[1].NameKey ?? ""), builtIns[0].Uri, null)];
                registered.AddRange(builtIns.Skip(4).Select(x => new PlaylistImportExistingTable(vm.Text(x.NameKey ?? ""), null, null)));
                vm.UpdateExisting(registered);
                // 照合更新で件数文言の内訳が変わるため、その後のフィルター操作の基準を取ります。
                string counts = vm.CountsText;
                var hideUrls = (CheckBox)dialog.FindName("hideUrlsCheck");
                var hideNames = (CheckBox)dialog.FindName("hideNamesCheck");
                foreach ((bool urls, bool names) in new[] { (true, false), (true, true), (false, true), (false, false) })
                {
                    if (hideUrls.IsChecked != urls) { ToggleControl(hideUrls); }
                    if (hideNames.IsChecked != names) { ToggleControl(hideNames); }
                    navigation.SelectedIndex = 0; TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                    AssertCandidateVisibility(tree, first.Uri, !urls);
                    AssertCandidateVisibility(tree, second.Uri, true);
                    AssertCandidateVisibility(tree, alias.Uri, !urls);
                    TreeViewItem hiddenGroup = FindGroupContainer(tree, "Hidden category");
                    Assert.AreEqual(names ? Visibility.Collapsed : Visibility.Visible, hiddenGroup.Visibility);
                    Assert.IsTrue(vm.ExternalGroups[0].IsExpanded && vm.ExternalGroups[1].IsExpanded);
                    TreeViewItem category = FindGroupContainer(tree, "Category");
                    TreeViewItem afterGroup = FindGroupContainer(tree, "After category");
                    if (names)
                    {
                        Assert.AreEqual(0d, hiddenGroup.DesiredSize.Height, "空分類は行全体の高さを取らない。");
                        Assert.AreEqual(GetVisualBounds(tree, category).Bottom, GetVisualBounds(tree, afterGroup).Top, 0.5);
                    }
                    else { AssertCandidateVisibility(tree, hidden.Uri, true); }
                    TreeViewItem aliasContainer = FindVisualDescendants<TreeViewItem>(tree).Single(x => x.DataContext is PlaylistImportItem { Candidate: { } c } && c.Uri == alias.Uri);
                    Assert.IsTrue(aliasContainer.DataContext is PlaylistImportItem { IsSelected: true }, "非表示中も内蔵と同じURIの外部掲載に共通選択を保つ。");
                    if (!urls)
                    {
                        // 初回からCollapsedの行はテンプレート未生成になり得るため、実チェックのBindingは表示時に確認します。
                        CheckBox aliasCheck = FindVisualDescendants<CheckBox>(aliasContainer).Single();
                        Assert.IsTrue(aliasCheck.IsVisible);
                        Assert.AreEqual(true, aliasCheck.IsChecked);
                    }
                    navigation.SelectedIndex = 1; TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                    AssertCandidateVisibility(builtInTree, builtIns[0].Uri, !urls);
                    AssertCandidateVisibility(builtInTree, builtIns[1].Uri, !names);
                    AssertCandidateVisibility(builtInTree, builtIns[2].Uri, true);
                    TreeViewItem recommendation = FindGroupContainer(builtInTree, vm.BuiltInGroups[1].Name);
                    Assert.AreEqual(names ? Visibility.Collapsed : Visibility.Visible, recommendation.Visibility);
                    if (names) { Assert.AreEqual(0d, recommendation.DesiredSize.Height); }
                    Assert.IsFalse(vm.BuiltInGroups[1].IsExpanded, "全子孫の非表示と再表示でも手動折畳みを保つ。");
                    Assert.IsTrue(vm.IsSelected(first.Uri) && vm.IsSelected(builtIns[0].Uri) && vm.IsSelected(hidden.Uri));
                    CollectionAssert.AreEqual(selection, vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
                    Assert.AreEqual(counts, vm.CountsText);
                    Assert.AreEqual(selection.Length, ((ListBox)dialog.FindName("selectedCandidates")).Items.Count);
                }
            }
            finally { dialog.Close(); }
        });
    }

    /// <summary>compiled検索Bindingの500ms猶予を実入力と通知待機で確認し、連続入力の最後だけを適用します。手動UpdateSourceや固定待機で猶予を迂回しません。</summary>
    [TestMethod]
    public void CompiledSearch_DelaysAppliedQueryUntilLastEdit()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var vm = new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy));
            var dialog = new PlaylistImportDialog(vm, new RecordingLoadPlaylistDialogService(new(UiDialogStatus.CancelledByUser)));
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
            {
                if (e.PropertyName == nameof(vm.SearchText) && vm.SearchText == "last query") { applied.TrySetResult(); }
            }
            vm.PropertyChanged += Changed;
            try
            {
                scope.ShowAndWaitForContentRendered(dialog);
                var search = (TextBox)dialog.FindName("searchInput");
                BindingExpression binding = search.GetBindingExpression(TextBox.TextProperty) ?? throw new AssertFailedException("検索Bindingが必要です。");
                Assert.AreEqual(500, binding.ParentBinding.Delay);
                Assert.AreEqual(UpdateSourceTrigger.PropertyChanged, binding.ParentBinding.UpdateSourceTrigger);
                search.SetCurrentValue(TextBox.TextProperty, "first query");
                search.SetCurrentValue(TextBox.TextProperty, "last query");
                Assert.AreEqual("", vm.SearchText);
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(applied.Task, "import-search-last-edit");
                Assert.AreEqual("last query", vm.SearchText);
            }
            finally { vm.PropertyChanged -= Changed; dialog.Close(); }
        });
    }

    /// <summary>標準・最小サイズの左右一行配置、短い注意の右寄せ、全文ToolTipと全幅選択・多数選択の削除ボタン非遮蔽を確認します。同じ表示で明→暗→明の実描画色を意味付き資源へ照合します。</summary>
    [TestMethod]
    public void Presentation_UsesSingleLineRowsAndLiveThemeAcrossBothCatalogs()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            ResourceDictionary[] previous = Application.Current.Resources.MergedDictionaries.ToArray();
            using var vm = new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy));
            string longName = string.Concat(Enumerable.Repeat("Long candidate name ", 16));
            var candidate = new PlaylistImportCandidate(new("https://example.test/long"), longName, Tag1: "Category");
            vm.UpdateCatalog([candidate, candidate with { Uri = new("https://example.test/other"), Name = "Other" }], PlaylistImportCatalogState.Loaded);
            vm.ExternalGroups[0].IsExpanded = true;
            string builtInName = vm.BuiltInGroups[0].Children[0].Name;
            vm.UpdateExisting([new(longName, null, null), new(builtInName, null, null)]);
            vm.SetSelected(candidate, true);
            var dialog = new PlaylistImportDialog(vm, new RecordingLoadPlaylistDialogService(new(UiDialogStatus.CancelledByUser)));
            try
            {
                scope.ShowAndWaitForContentRendered(dialog);
                var navigation = (ListBox)dialog.FindName("importNavigation");
                var selected = (ListBox)dialog.FindName("selectedCandidates"); selected.SelectedIndex = 0;
                foreach (Size size in new[] { new Size(920, 680), new Size(760, 500) })
                {
                    dialog.Width = size.Width; dialog.Height = size.Height;
                    foreach (int page in new[] { 0, 1 })
                    {
                        navigation.SelectedIndex = page; TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                        var tree = (TreeView)dialog.FindName(page == 0 ? "externalCandidates" : "builtInCandidates");
                        Assert.IsTrue(FindVisualDescendants<TreeViewItem>(tree).Any(x => x.IsVisible && x.DataContext is PlaylistImportItem { IsLeaf: true }), "実葉を表示してから配置を確認する。");
                        foreach (Grid line in FindVisualDescendants<Grid>(tree).Where(g => g.Children.OfType<TextBlock>().Any(x => x.Name == "candidateName")))
                        {
                            TextBlock name = line.Children.OfType<TextBlock>().Single(x => x.Name == "candidateName");
                            TextBlock status = line.Children.OfType<TextBlock>().Single(x => x.Name == "candidateStatus");
                            AssertOneLine(line, name, status, null);
                        }
                        Grid selectedLine = FindVisualDescendants<Grid>(selected).Single(g => g.Children.OfType<TextBlock>().Any(x => x.Name == "selectedName"));
                        AssertOneLine(selectedLine, selectedLine.Children.OfType<TextBlock>().Single(x => x.Name == "selectedName"), selectedLine.Children.OfType<TextBlock>().Single(x => x.Name == "selectedStatus"), selectedLine.Children.OfType<Button>().Single());
                        Rect counts = GetVisualBounds(dialog, (TextBlock)dialog.FindName("selectionCounts"));
                        Assert.IsTrue(counts.Top >= GetVisualBounds(dialog, selected).Bottom);
                        Rect clear = GetVisualBounds(dialog, (Button)dialog.FindName("clearSelectedButton"));
                        Assert.IsTrue(counts.Right <= clear.Left && counts.Top < clear.Bottom && counts.Bottom > clear.Top);
                        Assert.AreEqual(0d, ((ScrollViewer)dialog.FindName("operationMessageHost")).ActualHeight);
                        var filters = (StackPanel)dialog.FindName("candidateFilters");
                        FrameworkElement[] filterChecks = filters.Children.Cast<FrameworkElement>().ToArray();
                        Assert.IsTrue(GetVisualBounds(filters, filterChecks[0]).Right <= GetVisualBounds(filters, filterChecks[1]).Left);
                        if (page == 0)
                        {
                            Rect search = GetVisualBounds(dialog, (TextBox)dialog.FindName("searchInput"));
                            Rect all = GetVisualBounds(dialog, (CheckBox)dialog.FindName("selectAllMatchingCheck"));
                            Assert.IsTrue(search.Right <= all.Left && all.Top < search.Bottom && all.Bottom > search.Top);
                            Assert.AreEqual(0d, ((TextBlock)dialog.FindName("catalogStatus")).ActualHeight);
                            var searchLabel = (TextBlock)dialog.FindName("searchLabel");
                            Assert.IsTrue(searchLabel.IsVisible && searchLabel.Text.Length > 0);
                            Assert.IsTrue(GetVisualBounds(dialog, searchLabel).Bottom <= search.Top);
                            Rect filterBounds = GetVisualBounds(dialog, filters);
                            Assert.IsTrue(search.Bottom <= filterBounds.Top && filterBounds.Bottom <= GetVisualBounds(dialog, tree).Top);
                        }
                    }
                }
                navigation.SelectedIndex = 0; TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                var externalTree = (TreeView)dialog.FindName("externalCandidates");
                foreach (string theme in new[] { AppThemeService.Light, AppThemeService.Dark, AppThemeService.Light })
                {
                    AppThemeService.ApplyTheme(theme); TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                    var treeChrome = (Border)externalTree.Template.FindName("TreeChrome", externalTree);
                    var listChrome = (Border)selected.Template.FindName("ListChrome", selected);
                    AssertBrush(dialog, "App.ControlBackgroundBrush", treeChrome.Background); AssertBrush(dialog, "App.BorderBrush", treeChrome.BorderBrush);
                    Assert.AreEqual(listChrome.BorderThickness, treeChrome.BorderThickness);
                    foreach (TextBlock text in FindVisualDescendants<TextBlock>(externalTree).Where(x => x.Name is "candidateName" or "candidateStatus"))
                    { AssertBrush(dialog, text.Name == "candidateStatus" ? "App.SubtleTextBrush" : "App.TextBrush", text.Foreground); }
                    foreach (System.Windows.Shapes.Path arrow in FindVisualDescendants<System.Windows.Shapes.Path>(externalTree).Where(x => x.Name is "ExpandedGlyph" or "CollapsedGlyph"))
                    { AssertBrush(dialog, "App.TextBrush", arrow.Fill); }
                    CheckBox group = FindVisualDescendants<CheckBox>(externalTree).First(c => c.DataContext is PlaylistImportItem { IsGroup: true });
                    Assert.IsNull(group.IsChecked);
                    AssertBrush(dialog, "App.AccentBrush", ((Border)group.Template.FindName("CheckChrome", group)).Background);
                    var mixed = (System.Windows.Shapes.Path)group.Template.FindName("IndeterminateGlyph", group);
                    Assert.AreEqual(Visibility.Visible, mixed.Visibility); AssertBrush(dialog, "App.AccentForegroundBrush", mixed.Stroke);
                    CheckBox leaf = FindVisualDescendants<CheckBox>(externalTree).Single(c => c.DataContext is PlaylistImportItem { Candidate: { } cnd } && cnd.Uri == candidate.Uri);
                    AssertBrush(dialog, "App.AccentForegroundBrush", ((System.Windows.Shapes.Path)leaf.Template.FindName("CheckGlyph", leaf)).Stroke);
                    AssertBrush(dialog, "App.AccentBrush", ((Border)leaf.Template.FindName("CheckChrome", leaf)).Background);
                    foreach (TextBlock text in FindVisualDescendants<TextBlock>(selected).Where(x => x.Name is "selectedName" or "selectedStatus"))
                    { AssertBrush(dialog, text.Name == "selectedStatus" ? "App.SubtleTextBrush" : "App.TextBrush", text.Foreground); }
                    TreeViewItem leafContainer = FindVisualDescendants<TreeViewItem>(externalTree).Single(x => x.DataContext is PlaylistImportItem { Candidate: { } cnd } && cnd.Uri == candidate.Uri);
                    var leafChrome = (Border)leafContainer.Template.FindName("CandidateRowChrome", leafContainer);
                    var selectedContainer = (ListBoxItem)selected.ItemContainerGenerator.ContainerFromIndex(0);
                    var selectedChrome = (Border)selectedContainer.Template.FindName("ItemChrome", selectedContainer);
                    AssertBrush(dialog, "App.ControlSelectedBrush", leafChrome.Background); AssertBrush(dialog, "App.ControlSelectedBrush", selectedChrome.Background);
                    Assert.IsTrue(GetVisualBounds(externalTree, leafChrome).Right >= externalTree.ActualWidth - externalTree.BorderThickness.Right - 0.5, "選択色が右端へ届く。");
                    navigation.SelectedIndex = 1; TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                    var builtInTree = (TreeView)dialog.FindName("builtInCandidates");
                    AssertBrush(dialog, "App.ControlBackgroundBrush", ((Border)builtInTree.Template.FindName("TreeChrome", builtInTree)).Background);
                    foreach (TextBlock text in FindVisualDescendants<TextBlock>(builtInTree).Where(x => x.Name is "candidateName" or "candidateStatus"))
                    { AssertBrush(dialog, text.Name == "candidateStatus" ? "App.SubtleTextBrush" : "App.TextBrush", text.Foreground); }
                    navigation.SelectedIndex = 0; TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                }
                // 多数選択の末尾行でも削除操作が縦バーの16 DIP入力域へ入らない。
                PlaylistImportCandidate[] overflowCandidates = Enumerable.Range(0, 40).Select(index =>
                    new PlaylistImportCandidate(new Uri($"https://example.test/overflow/{index}"), $"Selected {index}", Tag1: "Overflow")).ToArray();
                vm.UpdateCatalog([candidate, .. overflowCandidates], PlaylistImportCatalogState.Loaded);
                foreach (PlaylistImportCandidate item in overflowCandidates) { vm.SetSelected(item, true); }
                TestUiDispatcherHost.ProcessQueuedPresentation();
                dialog.UpdateLayout();
                ScrollViewer selectedViewer = FindVisualDescendants<ScrollViewer>(selected).Single();
                Assert.IsTrue(selectedViewer.ScrollableHeight > 0d);
                selected.ScrollIntoView(selected.Items[selected.Items.Count - 1]);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                var selectedBar = (ScrollBar)selectedViewer.Template.FindName("PART_VerticalScrollBar", selectedViewer);
                Assert.AreEqual(Visibility.Visible, selectedBar.Visibility);
                Button[] removeButtons = FindVisualDescendants<Button>(selected).Where(button => button.Name == "removeCandidate" && button.IsVisible).ToArray();
                Assert.IsTrue(removeButtons.Length > 0);
                foreach (Button remove in removeButtons)
                {
                    Assert.IsTrue(GetVisualBounds(selectedViewer, remove).Right <= GetVisualBounds(selectedViewer, selectedBar).Left + 0.25d,
                        "削除ボタンはバーの操作域に隠れない。");
                }
            }
            finally
            {
                dialog.Close();
                Application.Current.Resources.MergedDictionaries.Clear();
                foreach (ResourceDictionary dictionary in previous) { Application.Current.Resources.MergedDictionaries.Add(dictionary); }
            }
        });
    }

    /// <summary>実バーの位置指定・小移動とホイールを通し、offsetに対応する同URI行の画素移動とextent・範囲・つまみ比率の安定を確認します。折畳み後は新しい内容で同じ保証を確認します。</summary>
    [TestMethod]
    public void Scrolling_MovesContinuouslyWithoutChangingExtentOrThumbRatio()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            using var vm = new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy));
            PlaylistImportCandidate[] catalog = new[] { ("Tall", 90), ("Middle", 35), ("Last", 65) }
                .SelectMany(group => Enumerable.Range(0, group.Item2).Select(i => new PlaylistImportCandidate(
                    new Uri("https://example.test/" + group.Item1 + "/" + i), group.Item1 + " candidate " + i, Tag1: group.Item1))).ToArray();
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            foreach (PlaylistImportItem group in vm.ExternalGroups) { group.IsExpanded = true; }
            var dialog = new PlaylistImportDialog(vm, new RecordingLoadPlaylistDialogService(new(UiDialogStatus.CancelledByUser)));
            try
            {
                scope.ShowAndWaitForContentRendered(dialog);
                var tree = (TreeView)dialog.FindName("externalCandidates");
                ScrollViewer viewer = FindVisualDescendants<ScrollViewer>(tree).Single();
                var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
                var track = (Track)bar.Template.FindName("PART_Track", bar);
                Assert.IsTrue(viewer.ScrollableHeight > viewer.ViewportHeight && track.Thumb.ActualHeight > 0);
                double originalExtent = viewer.ExtentHeight;
                VerifyContentPositions();
                vm.ExternalGroups[0].IsExpanded = false;
                TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                Assert.IsTrue(viewer.ExtentHeight < originalExtent, "折畳みによる内容高の変化は許容する。");
                VerifyContentPositions();

                void VerifyContentPositions()
                {
                    double extent = viewer.ExtentHeight, range = viewer.ScrollableHeight;
                    double ratio = track.Thumb.ActualHeight / track.ActualHeight;
                    Assert.IsTrue(range > viewer.ViewportHeight && ratio > 0 && ratio < 1);
                    TreeViewItem[] groups = FindVisualDescendants<TreeViewItem>(tree)
                        .Where(x => x.IsVisible && x.DataContext is PlaylistImportItem { IsGroup: true, IsExpanded: true }).ToArray();
                    TreeViewItem firstGroup = groups[0];
                    double groupHeight = firstGroup.ActualHeight;
                    Assert.IsTrue(groupHeight > viewer.ViewportHeight);
                    // 上端、分類内、分類境界、末尾付近。位置指定も実バーをtargetとした標準コマンドで通します。
                    double boundary = firstGroup.TransformToAncestor(viewer).Transform(new Point()).Y + viewer.VerticalOffset + groupHeight;
                    foreach (double target in new[] { 0d, groupHeight / 2, boundary, range - viewer.ViewportHeight / 2 })
                    {
                        ExecuteBarCommand(ScrollBar.ScrollToVerticalOffsetCommand, Math.Clamp(target, 0, range - viewer.ViewportHeight / 4));
                        TreeViewItem candidate = FindVisualDescendants<TreeViewItem>(tree).First(x => x.IsVisible
                            && x.DataContext is PlaylistImportItem { IsLeaf: true }
                            && GetVisualBounds(viewer, x).Top > viewer.ViewportHeight / 4
                            && GetVisualBounds(viewer, x).Bottom < viewer.ViewportHeight * 3 / 4);
                        Uri uri = ((PlaylistImportItem)candidate.DataContext).Candidate?.Uri ?? throw new AssertFailedException("実葉URIが必要です。");
                        double oldOffset = viewer.VerticalOffset, oldTop = GetVisualBounds(viewer, candidate).Top;
                        ExecuteBarCommand(ScrollBar.LineDownCommand, null);
                        double delta = viewer.VerticalOffset - oldOffset;
                        Assert.IsTrue(delta > 0 && delta < groupHeight, "小移動で分類全体の高さへ飛ばない。");
                        Assert.IsTrue(((PlaylistImportItem)candidate.DataContext).Candidate?.Uri == uri);
                        Assert.AreEqual(delta, oldTop - GetVisualBounds(viewer, candidate).Top, 0.5, "offsetと同URI行の画素移動量が対応する。");
                        AssertStableMetrics();
                    }
                    ExecuteBarCommand(ScrollBar.ScrollToVerticalOffsetCommand, 0d);
                    TreeViewItem wheelCandidate = FindVisualDescendants<TreeViewItem>(tree).First(x => x.IsVisible
                        && x.DataContext is PlaylistImportItem { IsLeaf: true }
                        && GetVisualBounds(viewer, x).Top > viewer.ViewportHeight / 4);
                    double wheelOffset = viewer.VerticalOffset, wheelTop = GetVisualBounds(viewer, wheelCandidate).Top;
                    viewer.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.MouseWheelEvent });
                    TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                    double wheelDelta = viewer.VerticalOffset - wheelOffset;
                    Assert.IsTrue(wheelDelta > 0 && wheelDelta < groupHeight, "ホイールも分類全体の高さに吸着しない。");
                    Assert.AreEqual(wheelDelta, wheelTop - GetVisualBounds(viewer, wheelCandidate).Top, 0.5);
                    AssertStableMetrics();

                    void AssertStableMetrics()
                    {
                        Assert.AreEqual(extent, viewer.ExtentHeight, 0.5);
                        Assert.AreEqual(range, viewer.ScrollableHeight, 0.5);
                        Assert.AreEqual(range, bar.Maximum, 0.5);
                        Assert.AreEqual(viewer.ViewportHeight, bar.ViewportSize, 0.5);
                        Assert.AreEqual(viewer.VerticalOffset, bar.Value, 0.5);
                        Assert.AreEqual(ratio, track.Thumb.ActualHeight / track.ActualHeight, 0.001);
                    }
                }
                void ExecuteBarCommand(RoutedCommand command, object? parameter)
                {
                    Assert.IsTrue(command.CanExecute(parameter, bar), "実バーのスクロール操作へ到達できる。");
                    command.Execute(parameter, bar);
                    TestUiDispatcherHost.ProcessQueuedPresentation(); dialog.UpdateLayout();
                }
            }
            finally { dialog.Close(); }
        });
    }

    private static TreeViewItem FindGroupContainer(TreeView tree, string name) => FindVisualDescendants<TreeViewItem>(tree)
        .Single(x => x.DataContext is PlaylistImportItem { IsGroup: true } item && item.Name == name);

    private static void AssertCandidateVisibility(TreeView tree, Uri uri, bool expected)
    {
        TreeViewItem leaf = FindVisualDescendants<TreeViewItem>(tree).Single(x => x.DataContext is PlaylistImportItem { Candidate: { } candidate } && candidate.Uri == uri);
        Assert.AreEqual(expected ? Visibility.Visible : Visibility.Collapsed, leaf.Visibility, uri.AbsoluteUri);
        Assert.AreEqual(expected, leaf.IsVisible, uri.AbsoluteUri);
        if (!expected) { Assert.AreEqual(0d, leaf.DesiredSize.Height, uri.AbsoluteUri); }
    }

    private static void AssertOneLine(Grid line, TextBlock name, TextBlock status, Button? remove)
    {
        Assert.AreEqual(TextWrapping.NoWrap, name.TextWrapping); Assert.AreEqual(TextTrimming.CharacterEllipsis, name.TextTrimming);
        Assert.AreEqual(name.Text, name.ToolTip);
        Assert.IsTrue(line.ActualWidth > 0 && name.ActualHeight > 0);
        Rect nameBounds = GetVisualBounds(line, name);
        if (status.Visibility == Visibility.Visible)
        {
            Rect statusBounds = GetVisualBounds(line, status);
            Assert.IsTrue(nameBounds.Right <= statusBounds.Left + 0.5);
            Assert.IsTrue(nameBounds.Top < statusBounds.Bottom && statusBounds.Top < nameBounds.Bottom);
            Assert.IsTrue(statusBounds.Right <= (remove == null ? line.ActualWidth : GetVisualBounds(line, remove).Left) + 0.5);
        }
        if (remove != null) { Assert.IsTrue(nameBounds.Right <= GetVisualBounds(line, remove).Left + 0.5); }
    }

    private static void AssertBrush(FrameworkElement owner, string key, Brush actual)
    {
        Assert.IsInstanceOfType<SolidColorBrush>(actual);
        Assert.AreEqual(((SolidColorBrush)owner.FindResource(key)).Color, ((SolidColorBrush)actual).Color, key);
    }

    private static void RaiseRowMouseDown(UIElement target) => target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });

    private static void ToggleControl(ToggleButton control)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(control) ?? throw new AssertFailedException("Toggle peerが必要です。");
        IToggleProvider provider = peer.GetPattern(PatternInterface.Toggle) as IToggleProvider ?? throw new AssertFailedException("実Toggleが必要です。");
        provider.Toggle();
    }

    private static void SetText(TextBox input, string text)
    {
        input.SetCurrentValue(TextBox.TextProperty, text);
        input.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private static void ToggleCandidate(TreeView tree, Uri uri)
    {
        tree.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
        CheckBox check = FindVisualDescendants<CheckBox>(tree).Single(c => c.DataContext is PlaylistImportItem { Candidate: { } candidate } && candidate.Uri.AbsoluteUri == uri.AbsoluteUri);
        ToggleControl(check);
    }

    private static PlaylistImportDialog CreateDialog(IUiDialogService? dialogs = null) => new(
        new PlaylistImportDialogViewModel(_ => new(PlaylistImportStartStatus.Busy)),
        dialogs ?? new RecordingLoadPlaylistDialogService(new UiFilePickerResult(UiDialogStatus.CancelledByUser)));

    private static void Materialize(Window host)
    {
        host.Measure(new Size(920, 680));
        host.Arrange(new Rect(0, 0, 920, 680));
        host.UpdateLayout();
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void AssertVisualBoundsInside(Visual ancestor, FrameworkElement descendant)
    {
        Rect bounds = GetVisualBounds(ancestor, descendant);
        var ancestorBounds = new Rect(0d, 0d, ((FrameworkElement)ancestor).ActualWidth, ((FrameworkElement)ancestor).ActualHeight);
        const double tolerance = 0.5d;
        Assert.IsTrue(
            bounds.Left >= ancestorBounds.Left - tolerance
                && bounds.Top >= ancestorBounds.Top - tolerance
                && bounds.Right <= ancestorBounds.Right + tolerance
                && bounds.Bottom <= ancestorBounds.Bottom + tolerance,
            $"{descendant.Name}の領域{bounds}が内容領域{ancestorBounds}からはみ出している。");
    }

    private static Rect GetVisualBounds(Visual ancestor, FrameworkElement descendant)
        => descendant.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(0d, 0d, descendant.ActualWidth, descendant.ActualHeight));

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

    private sealed class RecordingLoadPlaylistDialogService : IUiDialogService
    {
        private readonly UiFilePickerResult fileResult;

        internal RecordingLoadPlaylistDialogService(UiFilePickerResult fileResult)
        {
            this.fileResult = fileResult;
        }

        internal List<UiFilePickerRequest> FileRequests { get; } = [];

        internal Task<UiFilePickerResult>? FileTask { get; set; }

        internal int MessageRequestCount { get; private set; }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            MessageRequestCount++;
            throw new InvalidOperationException("ローカルファイル選択は別のモーダル通知を表示しない。");
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default)
        {
            FileRequests.Add(request);
            return FileTask ?? Task.FromResult(fileResult);
        }

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
