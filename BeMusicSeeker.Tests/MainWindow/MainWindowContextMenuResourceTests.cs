using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Update;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class MainWindowContextMenuResourceTests
{
    /// <summary>設定不正・操作不可・Web起動・実行体欠損・譜面欠損・起動失敗を専用リソースへ変換し内部詳細は出さないことを確認し、利用者通知の分類誤りや生例外の露出を検出します。</summary>
    [TestMethod]
    public void ConfiguredExternalActionFailureUsesTypedLocalizedMessage()
    {
        foreach ((ExternalConfiguredActionFailureKind kind, string expected) in new[]
        {
            (ExternalConfiguredActionFailureKind.InvalidSettings, Resources.RightClick_external_settings_invalid),
            (ExternalConfiguredActionFailureKind.ActionUnavailable, Resources.RightClick_external_action_unavailable),
            (ExternalConfiguredActionFailureKind.WebLaunchFailed, Resources.RightClick_external_web_launch_failed),
            (ExternalConfiguredActionFailureKind.ProgramExecutableMissing, Resources.RightClick_external_program_executable_missing),
            (ExternalConfiguredActionFailureKind.ProgramChartMissing, Resources.RightClick_external_program_chart_missing),
            (ExternalConfiguredActionFailureKind.ProgramLaunchFailed, Resources.RightClick_external_program_launch_failed)
        })
        {
            string message = MainWindow.GetConfiguredExternalActionFailureMessage(
                ExternalConfiguredActionResult.Failure(kind, "raw parser or exception detail"));

            Assert.AreEqual(expected, message, kind.ToString());
            Assert.IsFalse(message.Contains("raw parser or exception detail", StringComparison.Ordinal));
        }
    }

    /// <summary>解決済み履歴から関連付けパスと履歴scope・OpenFile能力を得ることを確認し、履歴操作から所持譜面への接続漏れを検出します。</summary>
    [TestMethod]
    public void PlayHistoryContextMenuOwnerCreatesAssociatedTargetForResolvedRow()
    {
        PlayHistoryRow row = CreateResolvedPlayHistoryRow();
        PlayHistoryWorkflowOwner owner = new();

        Assert.IsTrue(owner.TryCreateContextMenuAction(
            row,
            PlayHistoryContextMenuActionKind.OpenAssociated,
            out PlayHistoryContextMenuAction associatedAction));
        Assert.IsFalse(string.IsNullOrWhiteSpace(associatedAction.Path));
        Assert.IsTrue(owner.TryCreateAssociatedChartOperationTarget(
            row,
            out ChartOperationTarget target));
        Assert.AreEqual(ChartOperationSourceScope.PlayHistory, target.SourceScope);
        Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.OpenFile));
    }

    /// <summary>解析失敗表示で有効MD5を選んだ場合だけ削除項目を出すことを確認し、別表示や空識別子から失敗記録を削除する誤りを検出します。</summary>
    [TestMethod]
    public void ChartInfoParseFailureContextMenu_RequiresFailureViewAndValidHash()
    {
        Assert.IsTrue(MainWindow.ShouldShowChartInfoParseFailureRemovalMenu(true, [new string('a', 32)]));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenu(false, [new string('a', 32)]));
        Assert.IsFalse(MainWindow.ShouldShowChartInfoParseFailureRemovalMenu(true, [" "]));
    }

    /// <summary>bmsonの共通検査能力でも通常表示では項目を出し、除外表示と非対応能力では出さないことを確認し、BMS専用条件を共通検査へ誤適用する誤りを検出します。</summary>
    [TestMethod]
    public void ResourceHealthContextMenu_AllowsBmsonOnlyChartTargets()
    {
        ChartOperationTarget bmsonTarget = CreateContextMenuTarget(
            ChartFileKind.Bmson,
            ChartOperationCapabilities.RunResourceHealthCheck);
        ChartOperationTarget bmsOnlyTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.RunBmsEncodingFix);

        Assert.IsTrue(MainWindow.ShouldShowResourceHealthContextMenu(false, [bmsonTarget]));
        Assert.IsFalse(MainWindow.ShouldShowResourceHealthContextMenu(true, [bmsonTarget]));
        Assert.IsFalse(MainWindow.ShouldShowResourceHealthContextMenu(false, [bmsOnlyTarget]));
    }

    /// <summary>保留表示の空選択ではメニュー行を対象にして導入と所持操作を区別することを確認し、右クリック対象の欠落や表示文脈の混同を検出します。</summary>
    [TestMethod]
    public void ChartContextMenuStateBuilder_UsesRowTargetAsSelectionFallback()
    {
        ChartOperationTarget rowTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.OpenFile);

        ChartContextMenuState state = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow: false,
            isPendingSelected: true,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: []));

        Assert.IsTrue(state.IsInstallListSelected);
        Assert.IsFalse(state.IsPlaylistContext);
        Assert.AreEqual(1, state.SelectedTargets.Count);
        Assert.AreSame(rowTarget, state.SelectedTargets[0]);
        Assert.IsFalse(state.HasBmsonSelection);
        Assert.IsTrue(state.HasBmsSelection);
        Assert.IsFalse(state.CanMoveSelectedFiles);
        Assert.IsFalse(state.CanConvertToAudio);
        Assert.IsTrue(state.CanDeleteInstallPackages);
    }

    /// <summary>プレイリスト行のメニューでも既存bmson選択を優先し表文脈の禁止操作を適用することを確認し、右クリック行による既存選択の上書きを検出します。</summary>
    [TestMethod]
    public void ChartContextMenuStateBuilder_PreservesSelectionWhenItExists()
    {
        ChartOperationTarget rowTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.OpenFile);
        ChartOperationTarget selectedTarget = CreateContextMenuTarget(
            ChartFileKind.Bmson,
            ChartOperationCapabilities.RunResourceHealthCheck);

        ChartContextMenuState state = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow: true,
            isPendingSelected: false,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: [selectedTarget]));

        Assert.IsFalse(state.IsInstallListSelected);
        Assert.IsTrue(state.IsPlaylistContext);
        Assert.AreEqual(1, state.SelectedTargets.Count);
        Assert.AreSame(selectedTarget, state.SelectedTargets[0]);
        Assert.IsTrue(state.HasResourceHealthTarget);
        Assert.IsFalse(state.CanShowResourceHealthMenu);
        Assert.IsTrue(state.HasBmsonSelection);
        Assert.IsFalse(state.HasBmsSelection);
        Assert.IsFalse(state.CanAutoRenameFolders);
        Assert.IsFalse(state.CanFixEncoding);
    }

    /// <summary>所持BMSの能力から検査・移動・削除・名前修正・変換を許可して導入削除は許可しないことを確認し、能力とメニュー操作の対応誤りを検出します。</summary>
    [TestMethod]
    public void ChartContextMenuStateBuilder_ResolvesCapabilityPolicy()
    {
        ChartOperationTarget rowTarget = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UseLr2Ir
                | ChartOperationCapabilities.RunResourceHealthCheck
                | ChartOperationCapabilities.UpdateInstallDestination
                | ChartOperationCapabilities.RemoveFromLibrary
                | ChartOperationCapabilities.RenameInvalidExtension
                | ChartOperationCapabilities.UseScoreViewer);

        ChartContextMenuState state = ChartContextMenuStateBuilder.Build(new ChartContextMenuRequest(
            isPlaylistRow: false,
            isPendingSelected: false,
            isInstalledSelected: false,
            isPlaylistSelected: false,
            rowTarget: rowTarget,
            selectedTargets: [rowTarget]));

        Assert.IsTrue(state.HasResourceHealthTarget);
        Assert.IsTrue(state.CanShowResourceHealthMenu);
        Assert.IsTrue(state.CanMoveSelectedFiles);
        Assert.IsTrue(state.CanDeleteFiles);
        Assert.IsTrue(state.CanRenameInvalidExtension);
        Assert.IsTrue(state.CanShowFolderViewSeparator);
        Assert.IsTrue(state.CanAutoRenameFolders);
        Assert.IsTrue(state.CanFixEncoding);
        Assert.IsTrue(state.CanConvertToAudio);
        Assert.IsFalse(state.CanDeleteInstallPackages);
    }

    /// <summary>混在選択の非譜面行と能力不足の行を除き、OpenFile対象の元譜面を保つことを確認し、選択解決が対象外の行を配送する誤りを検出します。</summary>
    [TestMethod]
    public void ChartOperationTargetSelectionResolver_FiltersRowsByCapability()
    {
        var row = LibraryChartRow.FromChartFile(CreateContextMenuBmsFile());

        List<ChartOperationTarget> openFileTargets = ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            [row, new object()],
            ChartOperationSourceScope.Library,
            ChartOperationCapabilities.OpenFile));
        List<ChartOperationTarget> pendingInstallTargets = ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            [row],
            ChartOperationSourceScope.Library,
            ChartOperationCapabilities.UpdateInstallDestination));

        Assert.AreEqual(1, openFileTargets.Count);
        Assert.AreSame(row.Chart, openFileTargets[0].Chart);
        Assert.AreEqual(0, pendingInstallTargets.Count);
    }

    /// <summary>欠損行を含む選択要求を拒否することを確認し、壊れた選択を黙って部分成功へ変える誤りを検出します。</summary>
    [TestMethod]
    public void ChartOperationTargetSelectionResolver_RejectsNullRows()
    {
        Assert.ThrowsException<ArgumentException>(() => ChartOperationTargetSelectionResolver.Resolve(new ChartOperationTargetSelectionRequest(
            [null!],
            ChartOperationSourceScope.Library)));
    }

    /// <summary>保留の空対象と導入済みの欠損対象を拒否することを確認し、不完全な要求でカタログ削除を開始する誤りを検出します。</summary>
    [TestMethod]
    public void PackageCatalogRemovalRequest_RejectsEmptyAndMissingTargets()
    {
        Assert.ThrowsException<ArgumentException>(() => PackageCatalogRemovalRequest.CreatePending([]));
        Assert.ThrowsException<ArgumentException>(() => PackageCatalogRemovalRequest.CreateInstalled([null!]));
    }

    /// <summary>強制導入の空対象と手動導入の欠損対象を拒否することを確認し、対象不明の導入要求を受け付ける誤りを検出します。</summary>
    [TestMethod]
    public void PendingInstallPackageOperationRequest_RejectsEmptyAndMissingTargets()
    {
        Assert.ThrowsException<ArgumentException>(() => PendingInstallPackageOperationRequest.CreateForceInstall([]));
        Assert.ThrowsException<ArgumentException>(() => PendingInstallPackageOperationRequest.CreateManualInstall([null!]));
    }

    /// <summary>単独譜面を実パスのentryへ変換してパッケージ対象と区別し、不正要求拒否を保つことを確認し、探索対象の誤配送を検出します。</summary>
    [TestMethod]
    public void PendingInstallDestinationSearchRequest_MaterializesLooseTargets()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        var installSearch = PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([target]);

        Assert.IsTrue(installSearch.HasTargets);
        Assert.AreEqual(1, installSearch.SelectedRowCount);
        Assert.AreEqual(0, installSearch.PackageTargets.Count);
        Assert.AreEqual(1, installSearch.LooseEntries.Count);
        Assert.AreEqual(target.Chart.Path, installSearch.LooseEntries[0].Chart.Path);
        Assert.ThrowsException<ArgumentException>(() => PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([]));
        Assert.ThrowsException<ArgumentException>(() => PendingInstallDestinationSearchRequest.CreateInstallDestinationSearch([null!]));
    }

    /// <summary>単独譜面を実パスのentryへ変換し空・欠損選択は解除要求を作らないことを確認し、対象変換漏れや空操作の受付を検出します。</summary>
    [TestMethod]
    public void PendingInstallDestinationClearRequest_TryCreateMaterializesLooseTargets()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(PendingInstallDestinationClearRequest.TryCreate([target], out PendingInstallDestinationClearRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual(0, request.PackageTargets.Count);
        Assert.AreEqual(1, request.LooseEntries.Count);
        Assert.AreEqual(target.Chart.Path, request.LooseEntries[0].Chart.Path);
        Assert.IsFalse(PendingInstallDestinationClearRequest.TryCreate([], out PendingInstallDestinationClearRequest emptyRequest));
        Assert.IsNull(emptyRequest);
        Assert.IsFalse(PendingInstallDestinationClearRequest.TryCreate([null!], out PendingInstallDestinationClearRequest nullRequest));
        Assert.IsNull(nullRequest);
    }

    /// <summary>単独譜面の編集要求は元譜面を保って必要時にentry化し欠損は拒否することを確認し、編集対象の取り違えを検出します。</summary>
    [TestMethod]
    public void PendingInstallDestinationEditRequest_TryCreatePreservesLazyLooseTarget()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.UpdateInstallDestination);

        Assert.IsTrue(PendingInstallDestinationEditRequest.TryCreate(target, out PendingInstallDestinationEditRequest request));

        Assert.IsTrue(request.HasTarget);
        Assert.IsNull(request.PackageEntry);
        Assert.AreSame(target.Chart, request.ChartFile);
        PackageChartEntry entry = request.GetOrCreateChartEntry();
        Assert.IsNotNull(entry);
        Assert.AreEqual(target.Chart.Path, entry.Chart.Path);
        Assert.IsFalse(PendingInstallDestinationEditRequest.TryCreate(null, out PendingInstallDestinationEditRequest nullRequest));
        Assert.IsNull(nullRequest);
    }

    /// <summary>修正対象の元譜面から実パスのentryを生成し空選択は要求を作らないことを確認し、導入済み位置修正の対象変換漏れを検出します。</summary>
    [TestMethod]
    public void RepairInstalledLocationRequest_TryCreatePreservesRepairTargets()
    {
        ChartOperationTarget target = CreateContextMenuTarget(
            ChartFileKind.Bms,
            ChartOperationCapabilities.RepairInstalledLocation);

        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest request));

        Assert.IsTrue(request.HasTargets);
        Assert.AreEqual(1, request.Targets.Count);
        Assert.AreSame(target, request.Targets[0]);
        Assert.IsFalse(request.HasInstallDestination);
        request.MaterializeRepairEntries();
        Assert.AreEqual(1, request.RepairEntries.Count);
        Assert.AreEqual(target.Chart.Path, request.RepairEntries[0].Chart.Path);
        Assert.IsFalse(RepairInstalledLocationRequest.TryCreate([], out RepairInstalledLocationRequest emptyRequest));
        Assert.IsNull(emptyRequest);
    }

    /// <remarks>共有compiledテンプレートから標準Automationと実コントロール操作が届く境界をWPFで確認します。操作可否の判断網羅は下位ケースが担います。</remarks>
    /// <summary>縦横scrollbarを実領域と同期し、Automationと行/ページ操作が各方向へ動くことを確認し、共有テンプレートによる標準操作の接続漏れを検出します。同一本文4状態と一覧/候補/menuの枠内2 DIPも確認します。</summary>
    [TestMethod]
    [DoNotParallelize] // 実Window/Popupと共有Applicationの表示scopeを準備から回収まで所有する。
    public void CanonicalScrollViewer_MaterializesSharedScrollBarsAndPreservesBehavior()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var host = new Grid();
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/Simple Styles.xaml"));
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/Light.xaml"));
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/CanonicalControls.xaml"));

            var scroller = new ScrollViewer
            {
                Width = 180,
                Height = 120,
                Content = new Border { Width = 600, Height = 4200 },
                Style = (Style)host.Resources["App.Canonical.ScrollViewerStyle"],
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                CanContentScroll = false,
                IsHitTestVisible = false
            };
            host.Children.Add(scroller);
            var window = new Window
            {
                Width = 220,
                Height = 160,
                Content = host
            };

            windowTest.ShowAndWaitForContentRendered(window);
            scroller.ApplyTemplate();
            var vertical = (ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
            var horizontal = (ScrollBar)scroller.Template.FindName("PART_HorizontalScrollBar", scroller);
            Assert.IsNotNull(vertical);
            Assert.IsNotNull(horizontal);
            Assert.AreEqual(Orientation.Vertical, vertical.Orientation);
            Assert.AreEqual(Orientation.Horizontal, horizontal.Orientation);
            Assert.AreEqual(Visibility.Visible, vertical.Visibility);
            Assert.AreEqual(Visibility.Visible, horizontal.Visibility);
            Assert.AreEqual(scroller.ViewportHeight, vertical.ViewportSize, 0.01d);
            Assert.AreEqual(scroller.ViewportWidth, horizontal.ViewportSize, 0.01d);
            Assert.AreEqual(scroller.ScrollableHeight, vertical.Maximum, 0.01d);
            Assert.AreEqual(scroller.ScrollableWidth, horizontal.Maximum, 0.01d);

            vertical.ApplyTemplate();
            horizontal.ApplyTemplate();
            var verticalTrack = (Track)vertical.Template.FindName("PART_Track", vertical);
            var horizontalTrack = (Track)horizontal.Template.FindName("PART_Track", horizontal);
            Assert.IsNotNull(verticalTrack);
            Assert.IsNotNull(horizontalTrack);
            Assert.AreEqual(Orientation.Vertical, verticalTrack.Orientation);
            Assert.AreEqual(Orientation.Horizontal, horizontalTrack.Orientation);
            Assert.IsTrue(vertical.ActualWidth > 0d && vertical.ActualHeight > 0d);
            Assert.IsTrue(horizontal.ActualWidth > 0d && horizontal.ActualHeight > 0d);

            Assert.AreEqual(16d, vertical.ActualWidth, 0.01d);
            Assert.AreEqual(16d, horizontal.ActualHeight, 0.01d);
            Rect verticalBounds = vertical.TransformToAncestor(scroller).TransformBounds(new Rect(vertical.RenderSize));
            Rect horizontalBounds = horizontal.TransformToAncestor(scroller).TransformBounds(new Rect(horizontal.RenderSize));
            Assert.IsTrue(horizontalBounds.Right <= verticalBounds.Left + 0.01d);
            foreach (Track track in new[] { verticalTrack, horizontalTrack })
            {
                track.Thumb.ApplyTemplate();
                var chrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
                Assert.AreEqual(3d, track.Orientation == Orientation.Vertical ? chrome.ActualWidth : chrome.ActualHeight, 0.01d);
                Rect chromeBounds = chrome.TransformToAncestor(scroller).TransformBounds(new Rect(chrome.RenderSize));
                Assert.AreEqual(2d, track.Orientation == Orientation.Vertical
                    ? scroller.ActualWidth - chromeBounds.Right
                    : scroller.ActualHeight - chromeBounds.Bottom, 0.25d, "つまみは所属枠の内側2 DIPに配置する。");
                Assert.AreEqual(track.Orientation == Orientation.Vertical ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                    chrome.HorizontalAlignment);
                if (track.Orientation == Orientation.Horizontal) { Assert.AreEqual(VerticalAlignment.Bottom, chrome.VerticalAlignment); }
                foreach (RepeatButton pageButton in new[] { track.DecreaseRepeatButton, track.IncreaseRepeatButton })
                {
                    pageButton.ApplyTemplate();
                    Border pageChrome = FindVisualDescendants<Border>(pageButton).Single();
                    Assert.IsInstanceOfType<SolidColorBrush>(pageChrome.Background);
                    Assert.AreEqual((byte)0, ((SolidColorBrush)pageChrome.Background).Color.A);
                }
                Assert.IsTrue((track.Orientation == Orientation.Vertical ? track.Thumb.ActualHeight : track.Thumb.ActualWidth) >= 28d);
                // hoverはScrollBar全体、dragはThumb自身から取り、外端を固定して内側だけへ拡張する。
                TriggerBase[] triggers = track.Thumb.Template.Triggers.Cast<TriggerBase>().ToArray();
                DataTrigger hover = triggers.OfType<DataTrigger>().Single();
                var hoverBinding = (System.Windows.Data.Binding)hover.Binding;
                Assert.AreEqual("IsMouseOver", hoverBinding.Path.Path);
                Assert.AreEqual(typeof(ScrollBar), hoverBinding.RelativeSource.AncestorType);
                Trigger drag = triggers.OfType<Trigger>().Single(trigger => trigger.Property == Thumb.IsDraggingProperty);
                DependencyProperty thickness = track.Orientation == Orientation.Vertical ? FrameworkElement.WidthProperty : FrameworkElement.HeightProperty;
                foreach (SetterBaseCollection setters in new[] { hover.Setters, drag.Setters })
                {
                    Assert.AreEqual(true, setters.Cast<Setter>().Any(setter => setter.TargetName == "ThumbChrome" && setter.Property == thickness && Equals(setter.Value, 8d)));
                }
                Assert.IsFalse(FindVisualDescendants<RepeatButton>(scroller).Any(button =>
                    ReferenceEquals(button.Command, ScrollBar.LineUpCommand) || ReferenceEquals(button.Command, ScrollBar.LineDownCommand)
                    || ReferenceEquals(button.Command, ScrollBar.LineLeftCommand) || ReferenceEquals(button.Command, ScrollBar.LineRightCommand)));
            }
            var scrollPeer = new ScrollViewerAutomationPeer(scroller);
            var scrollProvider = (IScrollProvider)scrollPeer.GetPattern(PatternInterface.Scroll);
            Assert.IsNotNull(scrollProvider);
            Assert.IsTrue(scrollProvider.VerticallyScrollable);
            Assert.IsTrue(scrollProvider.HorizontallyScrollable);
            Assert.IsFalse(scroller.CanContentScroll);
            double initialVerticalOffset = scroller.VerticalOffset;
            double initialHorizontalOffset = scroller.HorizontalOffset;
            scrollProvider.Scroll(ScrollAmount.SmallIncrement, ScrollAmount.SmallIncrement);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(scroller.VerticalOffset > initialVerticalOffset);
            Assert.IsTrue(scroller.HorizontalOffset > initialHorizontalOffset);
            Assert.AreEqual(scroller.VerticalOffset, vertical.Value, 0.01d);
            Assert.AreEqual(scroller.HorizontalOffset, horizontal.Value, 0.01d);

            AssertScrollCommandBehavior(
                scroller,
                vertical,
                verticalTrack,
                Orientation.Vertical,
                "Canonical context vertical scrollbar");
            AssertScrollCommandBehavior(
                scroller,
                horizontal,
                horizontalTrack,
                Orientation.Horizontal,
                "Canonical context horizontal scrollbar");

            vertical.IsEnabled = false;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(vertical.Opacity < 1d);

            var content = (Border)scroller.Content;
            var presenter = (ScrollContentPresenter)scroller.Template.FindName("PART_ScrollContentPresenter", scroller);
            foreach ((double Width, double Height, bool Vertical, bool Horizontal) state in new[]
            {
                (Width: 60d, Height: 40d, Vertical: false, Horizontal: false),
                (Width: 60d, Height: 420d, Vertical: true, Horizontal: false),
                (Width: 600d, Height: 40d, Vertical: false, Horizontal: true),
                (Width: 600d, Height: 420d, Vertical: true, Horizontal: true)
            })
            {
                content.Width = state.Width;
                content.Height = state.Height;
                scroller.UpdateLayout();
                Assert.AreEqual(180d, presenter.ActualWidth, 0.01d);
                Assert.AreEqual(120d, presenter.ActualHeight, 0.01d);
                Assert.AreEqual(state.Vertical ? Visibility.Visible : Visibility.Collapsed, vertical.Visibility);
                Assert.AreEqual(state.Horizontal ? Visibility.Visible : Visibility.Collapsed, horizontal.Visibility);
            }
            // 同じ外寸・同じ折返し本文のまま、表示だけを4状態へ変えても内容領域を縮めない。
            scroller.Padding = new Thickness(11, 7, 13, 9);
            var wrapping = new TextBlock { Text = string.Join(" ", Enumerable.Repeat("wrapping content", 20)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top };
            var sameContent = new Grid { Width = 260, Height = 420 };
            sameContent.Children.Add(wrapping);
            scroller.Content = sameContent;
            double? wrappingHeight = null;
            foreach ((bool Vertical, bool Horizontal) state in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                scroller.VerticalScrollBarVisibility = state.Vertical ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
                scroller.HorizontalScrollBarVisibility = state.Horizontal ? ScrollBarVisibility.Visible : ScrollBarVisibility.Hidden;
                scroller.UpdateLayout();
                Assert.AreEqual(156d, presenter.ActualWidth, 0.01d);
                Assert.AreEqual(104d, presenter.ActualHeight, 0.01d);
                Assert.AreEqual(260d, sameContent.ActualWidth, 0.01d);
                Assert.AreEqual(420d, sameContent.ActualHeight, 0.01d);
                Assert.AreEqual(state.Vertical ? Visibility.Visible : Visibility.Collapsed, vertical.Visibility);
                Assert.AreEqual(state.Horizontal ? Visibility.Visible : Visibility.Collapsed, horizontal.Visibility);
                if (wrappingHeight.HasValue) { Assert.AreEqual(wrappingHeight.Value, wrapping.ActualHeight, 0.01d); }
                wrappingHeight = wrapping.ActualHeight;
            }
            scroller.Content = content;
            scroller.Padding = new Thickness(0);
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            scroller.Width = 20d;
            scroller.Height = 20d;
            scroller.UpdateLayout();
            foreach (Track track in new[] { verticalTrack, horizontalTrack })
            {
                Assert.IsTrue(track.Thumb.IsVisible);
                Rect thumbBounds = track.Thumb.TransformToAncestor(scroller).TransformBounds(new Rect(track.Thumb.RenderSize));
                Assert.IsTrue(thumbBounds.Left >= -0.01d && thumbBounds.Top >= -0.01d);
                Assert.IsTrue(thumbBounds.Right <= 20.01d && thumbBounds.Bottom <= 20.01d);
            }
            scroller.Width = 180d;
            scroller.Height = 120d;
            scroller.Style = (Style)host.Resources["SimpleScrollViewer"];
            scroller.ApplyTemplate();
            scroller.UpdateLayout();
            var simplePresenter = (ScrollContentPresenter)scroller.Template.FindName("PART_ScrollContentPresenter", scroller);
            var simpleVertical = (ScrollBar)scroller.Template.FindName("PART_VerticalScrollBar", scroller);
            Assert.AreEqual(180d, simplePresenter.ActualWidth, 0.01d);
            Assert.AreEqual(120d, simplePresenter.ActualHeight, 0.01d);
            Assert.AreEqual(16d, simpleVertical.ActualWidth, 0.01d);
            simpleVertical.ApplyTemplate();
            Assert.IsNotNull(simpleVertical.Template.FindName("PART_Track", simpleVertical));

            scroller.Visibility = Visibility.Collapsed;
            var list = new ListBox
            {
                Width = 180,
                Height = 100,
                ItemsSource = Enumerable.Range(0, 30),
                Style = (Style)host.Resources["SimpleListBox"]
            };
            host.Children.Add(list);
            window.UpdateLayout();
            Border listFrame = FindVisualDescendants<Border>(list).First();
            AssertFrameThumbInset(listFrame, FindVisualDescendants<ScrollViewer>(list).Single());
            list.Visibility = Visibility.Collapsed;
            var combo = new ComboBox
            {
                Width = 180,
                Height = 32,
                MaxDropDownHeight = 100,
                ItemsSource = Enumerable.Range(0, 30),
                Style = (Style)host.Resources["SimpleComboBox"]
            };
            host.Children.Add(combo);
            window.UpdateLayout();
            var popup = (Popup)combo.Template.FindName("Popup", combo);
            // 開閉や実フォーカスを要求せず、実ComboBoxが生成したPopup本文を有限の配置領域へ測定する。
            var popupRoot = (FrameworkElement)popup.Child;
            popupRoot.Measure(new Size(180, 100));
            popupRoot.Arrange(new Rect(0, 0, 180, 100));
            popupRoot.UpdateLayout();
            var dropDownFrame = (Border)combo.Template.FindName("DropDownBorder", combo);
            AssertFrameThumbInset(dropDownFrame, FindVisualDescendants<ScrollViewer>(dropDownFrame).Single());

            // menuとsubmenuはOS入力に依存せず、実controlのcompiled内容を同じ局所サイズへ配置して枠との関係を測る。
            var menu = new ContextMenu { Width = 180, MaxHeight = 100, Style = (Style)host.Resources["SimpleContextMenu"] };
            menu.Resources.MergedDictionaries.Add(host.Resources);
            var submenuHeader = new MenuItem { Header = "Submenu", Style = (Style)host.Resources["SimpleMenuItem"] };
            foreach (int index in Enumerable.Range(0, 30))
            {
                menu.Items.Add(new MenuItem { Header = $"Item {index}" });
                submenuHeader.Items.Add(new MenuItem { Header = $"Child {index}" });
            }
            menu.Items.Insert(0, submenuHeader);
            menu.ApplyTemplate();
            menu.Measure(new Size(180, 100));
            menu.Arrange(new Rect(0, 0, 180, 100));
            menu.UpdateLayout();
            Border menuFrame = FindVisualDescendants<Border>(menu).First();
            AssertFrameThumbInset(menuFrame, FindVisualDescendants<ScrollViewer>(menuFrame).Single());
            submenuHeader.ApplyTemplate();
            var submenuPopup = (Popup)submenuHeader.Template.FindName("SubMenuPopup", submenuHeader);
            var submenuRoot = (FrameworkElement)submenuPopup.Child;
            submenuRoot.MaxHeight = 100;
            submenuRoot.Measure(new Size(180, 100));
            submenuRoot.Arrange(new Rect(0, 0, 180, 100));
            submenuRoot.UpdateLayout();
            var submenuFrame = (Border)submenuHeader.Template.FindName("SubMenuBorder", submenuHeader);
            AssertFrameThumbInset(submenuFrame, FindVisualDescendants<ScrollViewer>(submenuFrame).Single());

        });
    }

    private static void AssertFrameThumbInset(Border frame, ScrollViewer viewer)
    {
        viewer.ApplyTemplate();
        viewer.UpdateLayout();
        Assert.AreSame(viewer.FindResource("App.Canonical.ScrollViewerTemplate"), viewer.Template,
            "所属枠内の実ScrollViewerへ共通overlayを解決する。");
        var bar = (ScrollBar)viewer.Template.FindName("PART_VerticalScrollBar", viewer);
        Assert.IsNotNull(bar, $"{frame.Name}: vertical scrollbar must materialize.");
        Assert.AreEqual(Visibility.Visible, bar.Visibility);
        bar.ApplyTemplate();
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        track.Thumb.ApplyTemplate();
        var chrome = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
        Rect bounds = chrome.TransformToAncestor(frame).TransformBounds(new Rect(chrome.RenderSize));
        Assert.IsTrue(frame.ActualWidth > 0d && viewer.ActualWidth > 0d && chrome.ActualWidth > 0d,
            $"実枠と本文とつまみを配置してから測る: frame={frame.Name}:{frame.RenderSize}, viewer={viewer.RenderSize}, chrome={bounds}。");
        Assert.AreEqual(2d, frame.ActualWidth - frame.BorderThickness.Right - bounds.Right, 0.25d,
            $"所属枠内縁から測り旧外Margin残留を検出する: frame={frame.Name}:{frame.RenderSize}, border={frame.BorderThickness}, viewer={viewer.RenderSize}, chrome={bounds}。");
    }

    private static void AssertScrollCommandBehavior(
        ScrollViewer viewer,
        ScrollBar scrollbar,
        Track track,
        Orientation orientation,
        string description)
    {
        RoutedCommand lineStartCommand;
        RoutedCommand lineEndCommand;
        RoutedCommand pageStartCommand;
        RoutedCommand pageEndCommand;
        if (orientation == Orientation.Vertical)
        {
            lineStartCommand = ScrollBar.LineUpCommand;
            lineEndCommand = ScrollBar.LineDownCommand;
            pageStartCommand = ScrollBar.PageUpCommand;
            pageEndCommand = ScrollBar.PageDownCommand;
        }
        else
        {
            lineStartCommand = ScrollBar.LineLeftCommand;
            lineEndCommand = ScrollBar.LineRightCommand;
            pageStartCommand = ScrollBar.PageLeftCommand;
            pageEndCommand = ScrollBar.PageRightCommand;
        }

        FrameworkElement pageStart = FindScrollCommandAffordance(
            track,
            pageStartCommand,
            description + " page-start");
        FrameworkElement pageEnd = FindScrollCommandAffordance(
            track,
            pageEndCommand,
            description + " page-end");

        double lineStartOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        lineStartCommand.Execute(null, scrollbar);
        TestUiDispatcherHost.ProcessQueuedPresentation();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, orientation) < lineStartOffset,
            description + " line-start command must decrease the viewer offset.");

        double lineEndOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        lineEndCommand.Execute(null, scrollbar);
        TestUiDispatcherHost.ProcessQueuedPresentation();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, orientation) > lineEndOffset,
            description + " line-end command must increase the viewer offset.");

        double pageStartOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        InvokeScrollAffordance(pageStart, description + " page-start");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        viewer.UpdateLayout();
        Assert.IsTrue(
            GetScrollOffset(viewer, orientation) < pageStartOffset,
            description + " page-start command must decrease the viewer offset.");

        double pageEndOffset = SetScrollOffsetToInterior(viewer, orientation, description);
        InvokeScrollAffordance(pageEnd, description + " page-end");
        TestUiDispatcherHost.ProcessQueuedPresentation();
        viewer.UpdateLayout();
        double finalOffset = GetScrollOffset(viewer, orientation);
        Assert.IsTrue(
            finalOffset > pageEndOffset,
            description + " page-end command must increase the viewer offset.");
        Assert.AreEqual(
            finalOffset,
            scrollbar.Value,
            0.01d,
            description + " scrollbar value must follow its viewer offset.");
    }

    private static FrameworkElement FindScrollCommandAffordance(
        DependencyObject root,
        RoutedCommand command,
        string description)
    {
        FrameworkElement[] candidates = FindVisualDescendants<FrameworkElement>(root)
            .Where(element => element is ICommandSource source && ReferenceEquals(source.Command, command))
            .ToArray();
        Assert.AreEqual(
            1,
            candidates.Length,
            description + " must expose exactly one materialized command affordance.");
        FrameworkElement affordance = candidates[0];
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(affordance)
            ?? throw new AssertFailedException(description + " command affordance must expose an Automation peer.");
        Assert.IsNotNull(
            peer.GetPattern(PatternInterface.Invoke),
            description + " command affordance must expose Invoke automation.");
        return affordance;
    }

    private static double SetScrollOffsetToInterior(
        ScrollViewer viewer,
        Orientation orientation,
        string description)
    {
        double maximum = orientation == Orientation.Vertical
            ? viewer.ScrollableHeight
            : viewer.ScrollableWidth;
        Assert.IsTrue(maximum > 0d, description + " must expose a positive scroll extent.");
        if (orientation == Orientation.Vertical)
        {
            viewer.ScrollToVerticalOffset(maximum / 2d);
        }
        else
        {
            viewer.ScrollToHorizontalOffset(maximum / 2d);
        }

        TestUiDispatcherHost.ProcessQueuedPresentation();
        viewer.UpdateLayout();
        double offset = GetScrollOffset(viewer, orientation);
        Assert.IsTrue(
            offset > 0d && offset < maximum,
            description + " must invoke affordances from a proven non-boundary offset.");
        return offset;
    }

    private static double GetScrollOffset(ScrollViewer viewer, Orientation orientation)
        => orientation == Orientation.Vertical ? viewer.VerticalOffset : viewer.HorizontalOffset;

    private static void InvokeScrollAffordance(FrameworkElement affordance, string description)
    {
        Assert.IsTrue(affordance.IsEnabled, description + " command affordance must be enabled.");
        // 境界ではページ領域が0になるため、呼出元が内部位置へ移した後の実ヒットを確認する。
        Assert.IsTrue(affordance.ActualWidth > 0d && affordance.ActualHeight > 0d);
        Assert.IsNotNull(VisualTreeHelper.HitTest(affordance, new Point(affordance.ActualWidth / 2d, affordance.ActualHeight / 2d)));
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(affordance)
            ?? throw new AssertFailedException(description + " command affordance must expose an Automation peer.");
        IInvokeProvider invokeProvider = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider
            ?? throw new AssertFailedException(description + " command affordance must expose Invoke automation.");
        invokeProvider.Invoke();
    }

    /// <remarks>共有compiledテンプレートから標準Automationと実コントロール操作が届く境界をWPFで確認します。操作可否の判断網羅は下位ケースが担います。</remarks>
    /// <summary>実ナビゲーションのAutomationで単一選択を変更し、無効項目と内容配置を保つことを確認し、標準選択操作や無枠内容hostの接続漏れを検出します。</summary>
    [TestMethod]
    [DoNotParallelize] // 共有Dispatcherの実Window一覧を表示scopeが所有する。
    public void CanonicalTopNavigation_UsesSingleSelectionAndStandardAutomation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var host = new StackPanel();
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/Light.xaml"));
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/CanonicalControls.xaml"));
            var navigation = new ListBox
            {
                Width = 360,
                Height = 56,
                ItemsSource = new[] { "General", "Folder", "Custom Folder" },
                SelectedIndex = 0,
                Style = (Style)host.Resources["App.Canonical.TopNavigationStyle"]
            };
            var contentText = new TextBlock { Text = "General content" };
            var content = new ContentControl
            {
                Height = 80,
                Content = contentText,
                Style = (Style)host.Resources["App.Canonical.TopNavigationContentStyle"]
            };
            host.Children.Add(navigation);
            host.Children.Add(content);
            var window = new Window
            {
                Width = 420,
                Height = 180,
                Content = host
            };

            windowTest.ShowAndWaitForContentRendered(window);
            navigation.ApplyTemplate();
            navigation.UpdateLayout();
            Assert.AreEqual(SelectionMode.Single, navigation.SelectionMode);
            Assert.AreEqual(0, navigation.SelectedIndex);
            StackPanel itemPanel = FindVisualDescendants<StackPanel>(navigation)
                .Single(panel => panel.Children.OfType<ListBoxItem>().Count() == navigation.Items.Count);
            Assert.AreEqual(Orientation.Horizontal, itemPanel.Orientation);
            ListBoxItem[] items = navigation.Items
                .Cast<object>()
                .Select((_, index) => (ListBoxItem)navigation.ItemContainerGenerator.ContainerFromIndex(index))
                .ToArray();
            Assert.IsTrue(items.All(item => item is not null));
            foreach (ListBoxItem item in items)
            {
                item.ApplyTemplate();
                Assert.IsNotNull(item.Template.FindName("SelectionIndicator", item));
                Assert.IsNotNull(item.Template.FindName("FocusBorder", item));
                Assert.IsTrue(item.Focusable);
            }
            Assert.AreEqual(
                KeyboardNavigationMode.Continue,
                KeyboardNavigation.GetDirectionalNavigation(navigation));
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)items[0].Template.FindName("SelectionIndicator", items[0])).Visibility);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)items[1].Template.FindName("SelectionIndicator", items[1])).Visibility);

            var navigationPeer = new ListBoxAutomationPeer(navigation);
            var selectionProvider = (ISelectionProvider)navigationPeer.GetPattern(PatternInterface.Selection);
            Assert.IsNotNull(selectionProvider);
            Assert.IsFalse(selectionProvider.CanSelectMultiple);
            Assert.AreEqual(1, selectionProvider.GetSelection().Length);
            AutomationPeer folderPeer = navigationPeer.GetChildren()
                .Single(peer => peer.GetName() == "Folder");
            var folderSelection = (ISelectionItemProvider)folderPeer.GetPattern(PatternInterface.SelectionItem);
            Assert.IsNotNull(folderSelection);
            folderSelection.Select();
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(1, navigation.SelectedIndex);
            Assert.IsTrue(folderSelection.IsSelected);

            items[2].IsEnabled = false;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            var disabledNavigationChrome = (Border)items[2].Template.FindName("NavigationItemChrome", items[2]);
            Assert.IsTrue(disabledNavigationChrome.Opacity < 1d);
            content.ApplyTemplate();
            content.UpdateLayout();
            Assert.IsFalse(content.Focusable);
            Assert.IsFalse(
                FindVisualDescendants<Border>(content).Any(border =>
                    border.BorderThickness.Left > 0d
                    || border.BorderThickness.Top > 0d
                    || border.BorderThickness.Right > 0d
                    || border.BorderThickness.Bottom > 0d
                    || border.CornerRadius.TopLeft > 0d
                    || border.CornerRadius.TopRight > 0d
                    || border.CornerRadius.BottomRight > 0d
                    || border.CornerRadius.BottomLeft > 0d),
                "The shared top-navigation content role must be an unframed host.");
            Assert.AreEqual(new Thickness(0), content.Padding, "既定では本文余白を所有しない。");
            Point defaultOrigin = contentText.TransformToAncestor(content).Transform(new Point());
            Assert.AreEqual(0d, defaultOrigin.X, 0.25d);
            Assert.AreEqual(0d, defaultOrigin.Y, 0.25d);
            content.Padding = new Thickness(7, 9, 11, 13);
            content.UpdateLayout();
            Point contentOrigin = contentText.TransformToAncestor(content).Transform(new Point());
            Assert.AreEqual(content.Padding.Left, contentOrigin.X, 0.25d);
            Assert.AreEqual(content.Padding.Top, contentOrigin.Y, 0.25d);
            Assert.AreEqual(
                content.ActualWidth - content.Padding.Left - content.Padding.Right,
                contentText.ActualWidth,
                0.25d,
                "The unframed content host must preserve horizontal padding and stretch alignment.");
        });
    }

    /// <summary>作業領域高からメニュー最大高を計算しNaNは有限高へ補正することを確認し、異常な作業領域値で表示範囲を壊す誤りを検出します。</summary>
    [TestMethod]
    public void MenuMaxHeightConverter_UsesWorkAreaAndNormalizesInvalidHeight()
    {
        var converter = new MenuMaxHeightConverter();

        Assert.AreEqual(576d, converter.Convert(768d, typeof(double), null, CultureInfo.InvariantCulture));
        Assert.AreEqual(160d, converter.Convert(double.NaN, typeof(double), null, CultureInfo.InvariantCulture));
    }

    /// <summary>LR2非対応の長い既存standalone rootを読込と再保存で保持することを確認し、LR2制約で単独モードの設定を失う誤りを検出します。</summary>
    [TestMethod]
    public void StandaloneRootDeserialization_PreservesExistingLr2IncompatibleRoot()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_StandaloneLong_" + Guid.NewGuid().ToString("N"));
        string longRoot = BuildLongDirectoryPath(tempRoot, "root");
        LongPathFileSystem.CreateDirectory(longRoot);

        try
        {
            IReadOnlyList<string> deserialized = SettingsDialogViewModel.DeserializeStandaloneBmsRootPaths(longRoot);
            string serialized = SettingsDialogViewModel.SerializeStandaloneBmsRootPaths(deserialized);

            Assert.IsFalse(Lr2CompatibilityEvaluator.IsLegacyRootPathCompatible(longRoot));
            CollectionAssert.Contains(deserialized.ToList(), longRoot);
            StringAssert.Contains(serialized, longRoot);
        }
        finally
        {
            if (LongPathFileSystem.DirectoryExists(tempRoot))
            {
                LongPathFileSystem.DeleteDirectory(tempRoot, recursive: true);
            }
        }
    }

    /// <summary>終了中のdurable更新失敗だけを保留しhandshake失敗は保留しないことを確認し、通知前のreceipt承認や別分類の失敗隠蔽を検出します。</summary>
    [TestMethod]
    public void StartupUpdateFailurePresentationDefersReceiptWhenShellIsClosing()
    {
        var receiptException = new UpdateFailureReceiptException(
            "durable update failure",
            () => { });

        bool shouldDefer = MainWindow.ShouldDeferStartupUpdateFailurePresentation(
            shellClosing: true,
            updateShutdownPreparationFailed: false,
            receiptException);

        Assert.IsTrue(shouldDefer);
        receiptException.DeferAcknowledge();
        Assert.IsFalse(receiptException.ShouldAcknowledgeAfterPresentation);
        Assert.IsFalse(MainWindow.ShouldDeferStartupUpdateFailurePresentation(
            shellClosing: true,
            updateShutdownPreparationFailed: false,
            new UpdaterLaunchFailureException("ready handshake failed")));
    }

    /// <summary>NaN・両無限・非正値・下限未満を補正し下限と任意有効幅は保つことを確認し、不正保存幅による配置破壊や利用者幅の上書きを検出します。</summary>
    [TestMethod]
    public void SidebarTreeViewWidthPolicy_NormalizesInvalidPersistedValues()
    {
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.NaN));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.PositiveInfinity));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(double.NegativeInfinity));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(-1d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(0d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, Settings.NormalizeTreeViewWidth(Settings.MinTreeViewWidth - 1d));
        Assert.AreEqual(Settings.MinTreeViewWidth, Settings.NormalizeTreeViewWidth(Settings.MinTreeViewWidth));
        Assert.AreEqual(250d, Settings.NormalizeTreeViewWidth(250d));
        Assert.AreEqual(320d, Settings.NormalizeTreeViewWidth(320d));
    }

    /// <summary>旧不正幅を読込で補正し、書込では補正値と有効下限を保存することを確認し、設定読込/書込の一方だけを補正する誤りを検出します。</summary>
    [TestMethod]
    public void SidebarTreeViewWidthSetting_PropertyNormalizesBackingValue()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

        settings["TreeViewWidth"] = 0d;
        Assert.AreEqual(Settings.DefaultTreeViewWidth, settings.TreeViewWidth);

        settings.TreeViewWidth = 0d;
        Assert.AreEqual(Settings.DefaultTreeViewWidth, (double)settings["TreeViewWidth"]);

        settings.TreeViewWidth = Settings.MinTreeViewWidth;
        Assert.AreEqual(Settings.MinTreeViewWidth, (double)settings["TreeViewWidth"]);
    }

    /// <summary>保存Darkを読み、未知テーマはLightへ補正し小文字darkは正式名へ保存することを確認し、テーマ設定の互換正規化漏れを検出します。</summary>
    [TestMethod]
    public void AppearanceThemeSetting_NormalizesValues()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

        settings["AppearanceTheme"] = "Dark";
        Assert.AreEqual(AppThemeService.Dark, settings.AppearanceTheme);

        settings["AppearanceTheme"] = "Unknown";
        Assert.AreEqual(AppThemeService.Light, settings.AppearanceTheme);

        settings.AppearanceTheme = "dark";
        Assert.AreEqual(AppThemeService.Dark, (string)settings["AppearanceTheme"]);
    }

    /// <summary>文字・行・見出しの旧NaN/無限値を補正し範囲外書込を下限/上限へ収めることを確認し、異常設定が描画へ届く誤りを検出します。</summary>
    [TestMethod]
    public void CustomTableAppearanceSettings_NormalizeValues()
    {
        Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

        settings["CustomTableFontSize"] = double.NaN;
        settings["CustomTableRowHeight"] = double.PositiveInfinity;
        settings["CustomTableHeaderHeight"] = double.NegativeInfinity;

        Assert.AreEqual(Settings.DefaultCustomTableFontSize, settings.CustomTableFontSize);
        Assert.AreEqual(Settings.DefaultCustomTableRowHeight, settings.CustomTableRowHeight);
        Assert.AreEqual(Settings.DefaultCustomTableHeaderHeight, settings.CustomTableHeaderHeight);

        settings.CustomTableFontSize = Settings.MinCustomTableFontSize - 1d;
        settings.CustomTableRowHeight = Settings.MaxCustomTableRowHeight + 1d;
        settings.CustomTableHeaderHeight = Settings.MinCustomTableHeaderHeight - 1d;

        Assert.AreEqual(Settings.MinCustomTableFontSize, (double)settings["CustomTableFontSize"]);
        Assert.AreEqual(Settings.MaxCustomTableRowHeight, (double)settings["CustomTableRowHeight"]);
        Assert.AreEqual(Settings.MinCustomTableHeaderHeight, (double)settings["CustomTableHeaderHeight"]);
    }

    /// <summary>LightとDarkの全資源キー集合が一致することを確認し、テーマ切替で同じ画面に必要な資源が欠ける誤りを検出します。</summary>
    [TestMethod]
    public void ThemeResourceDictionaries_KeepLightAndDarkKeyParity()
    {
        string root = FindRepositoryRoot();
        string light = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Themes", "Light.xaml"));
        string dark = File.ReadAllText(Path.Combine(root, "BeMusicSeeker", "Themes", "Dark.xaml"));

        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] lightKeys = XDocument.Parse(light).Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value ?? string.Empty)
            .Where(key => key.Length > 0)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        string[] darkKeys = XDocument.Parse(dark).Descendants()
            .Select(element => element.Attribute(xaml + "Key")?.Value ?? string.Empty)
            .Where(key => key.Length > 0)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToArray();
        CollectionAssert.AreEqual(lightKeys, darkKeys, "Light and dark theme dictionaries must have exact key parity.");
    }

    /// <summary>実測列幅・現在幅・前保存値の順に有効幅を保存し全無効なら補正することを確認し、実測を無視して古い幅や不正幅を保存する誤りを検出します。</summary>
    [TestMethod]
    public void SidebarTreeViewWidthSavePolicy_UsesMeasuredColumnWhenValid()
    {
        Assert.AreEqual(240d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(240d, 250d, 300d));
        Assert.AreEqual(260d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(double.NaN, 260d, 300d));
        Assert.AreEqual(320d, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(0d, double.NaN, 320d));
        Assert.AreEqual(Settings.DefaultTreeViewWidth, MainWindowViewSettingsPolicy.ResolveTreeViewWidthForSave(0d, double.NaN, 0d));
    }

    /// <summary>フォルダとhtm/htmlはブラウザー対象としarchiveと専用解析の配布ページは除くことを確認し、取得可能ページをブラウザーへ逃がす誤りを検出します。</summary>
    [TestMethod]
    public void PlaylistUrlDownload_BrowserFallbackRecognizesPageUrls()
    {
        Assert.IsTrue(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/folder/")));
        Assert.IsTrue(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/index.htm")));
        Assert.IsTrue(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/index.html")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://example.invalid/package.zip")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://venue.bmssearch.net/event/1/1")));
        Assert.IsFalse(PlaylistUrlAcquisitionWorkflow.IsBrowserFallbackDownloadUri(new Uri("https://bmssearch.net/bmses/1")));
    }

    /// <summary>Driveの三形式とDropbox共有を取得URLへ変換し似た別hostは変えないことを確認し、互換URL欠落や別サイトの誤変換を検出します。</summary>
    [TestMethod]
    public void PlaylistUrlDownload_NormalizesCloudStorageShareUrls()
    {
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://drive.google.com/file/d/abc123/view?usp=sharing")).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://drive.google.com/open?id=abc123&usp=sharing")).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://docs.google.com/uc?export=download&id=abc123")).ToString());
        Assert.AreEqual(
            "https://www.dropbox.com/scl/fi/token/package.zip?rlkey=key&dl=1",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://www.dropbox.com/scl/fi/token/package.zip?rlkey=key&dl=0")).ToString());
        Assert.AreEqual(
            "https://notdropbox.com/scl/fi/token/package.zip?dl=0",
            PlaylistUrlAcquisitionWorkflow.NormalizeDownloadUri(new Uri("https://notdropbox.com/scl/fi/token/package.zip?dl=0")).ToString());
    }

    /// <summary>Drive・MediaFire・イベント/BMS SEARCHのHTMLと埋込データから個別本体を選び全体package・非対応ページ・危険なscheme/偽hostを除くことを確認し、配布先の誤解析を検出します。</summary>
    [TestMethod]
    public void PlaylistUrlDownload_ResolvesKnownSharedDownloadPages()
    {
        const string googleDriveWarningHtml = """
            <html><body>
            <form id="download-form" method="get" action="/download">
              <input type="hidden" name="id" value="abc123">
              <input type="hidden" name="confirm" value="token">
              <input type="submit" name="submit" value="download">
            </form>
            </body></html>
            """;
        const string mediaFireHtml = """
            <html><body>
            <a id="downloadButton" href="https://download123.mediafire.com/abc/package.zip">Download</a>
            </body></html>
            """;
        const string manbowHtml = """
            <html><body>
            <Th>DownLoadAddress</Th><td><a href="https://example.invalid/body.zip">Download</a></td>
            </body></html>
            """;
        const string venueHtml = """
            <html><body>
            <a href="https://example.invalid/readme.html">info</a>
            <a href="https://drive.google.com/file/d/abc123/view?usp=sharing">Download</a>
            </body></html>
            """;
        const string bmsSearchHtml = """
            <html><body>
            <a href="/help.html">help</a>
            <a href="/archives/package.lzh">archive</a>
            </body></html>
            """;
        const string bmsSearchNextHtml = """
            <html><body>
            <script>self.__next_f.push([1,"7:{\"title\":\"\uD83D\uDE00\",\"downloads\":[{\"url\":\"https://anonymous.bms.ms/data/body.zip\",\"description\":\"\"}],\"relatedLinks\":[]}"])</script>
            </body></html>
            """;
        const string venueNextHtml = """
            <html><body>
            <script>self.__next_f.push([1,"2c:{\"description\":\"3.5型\",\"downloadURL\":\"https://anonymous.bms.ms/data/itsfree_battle.7z\",\"type\":\"CORE\"}"])</script>
            </body></html>
            """;
        const string venueMixedPackageHtml = """
            <html><body>
            <script>self.__next_f.push([1,"16:{\"downloadURL\":\"https://drive.google.com/file/d/eventPackage/view?usp=sharing\",\"description\":\"イベント全体\"}\n2c:{\"description\":\"通常版\",\"downloadURL\":\"https://drive.google.com/file/d/entryCore/view?usp=sharing\",\"type\":\"CORE\"}"])</script>
            </body></html>
            """;
        const string venueTypeFirstCoreHtml = """
            <html><body>
            <script>self.__next_f.push([1,"16:{\"downloadURL\":\"https://drive.google.com/file/d/eventPackage/view?usp=sharing\",\"description\":\"イベント全体\"}\n2c:{\"description\":\"通常版\",\"type\":\"CORE\",\"downloadURL\":\"https://drive.google.com/file/d/entryTypeFirst/view?usp=sharing\"}"])</script>
            </body></html>
            """;
        const string venuePackageAnchorHtml = """
            <html><body>
            <a href="https://drive.google.com/file/d/eventPackage/view?usp=sharing">イベント全体</a>
            <a href="https://drive.google.com/file/d/entryAnchor/view?usp=sharing">通常版</a>
            <script>self.__next_f.push([1,"16:{\"downloadURL\":\"https://drive.google.com/file/d/eventPackage/view?usp=sharing\",\"description\":\"イベント全体\"}"])</script>
            </body></html>
            """;
        const string venueMediaFireHtml = """
            <html><body>
            <a href="https://www.mediafire.com/file/abc/package.zip/file">Download</a>
            </body></html>
            """;
        const string mediaFireArchiveNamedLandingHtml = """
            <html><body>
            <a id="downloadButton" class="input popsok" href="https://download123.mediafire.com/token/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z">Download</a>
            </body></html>
            """;
        const string googleDriveUnsafeActionHtml = """
            <html><body>
            <form id="download-form" method="get" action="file:///C:/secret.zip">
              <input type="hidden" name="id" value="abc123">
            </form>
            </body></html>
            """;
        const string mediaFireUnsafeHostHtml = """
            <html><body>
            <a id="downloadButton" href="https://evilmediafire.com/abc/package.zip">Download</a>
            </body></html>
            """;
        const string manbowUnsafeSchemeHtml = """
            <html><body>
            <Th>DownLoadAddress</Th><td><a href="file:///C:/secret.zip">Download</a></td>
            </body></html>
            """;
        const string venueUnsafeHostHtml = """
            <html><body>
            <a href="https://evil.example.invalid/index.html">not a direct archive</a>
            </body></html>
            """;
        const string venueSourcePageOnlyHtml = """
            <html><body>
            <a href="https://bmssearch.net/bmses/2uLp8a8bJYLmrx">BMS SEARCH page</a>
            </body></html>
            """;
        const string venueGoogleDriveFolderOnlyHtml = """
            <html><body>
            <a href="https://drive.google.com/drive/folders/folder123">Google Drive folder</a>
            </body></html>
            """;

        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&confirm=token",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"), googleDriveWarningHtml).ToString());
        Assert.AreEqual(
            "https://download123.mediafire.com/abc/package.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://www.mediafire.com/file/abc/package.zip/file"), mediaFireHtml).ToString());
        Assert.AreEqual(
            "https://example.invalid/body.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"), manbowHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=abc123&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/event/1/1"), venueHtml).ToString());
        Assert.AreEqual(
            "https://bmssearch.net/archives/package.lzh",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://bmssearch.net/bmses/1"), bmsSearchHtml).ToString());
        Assert.AreEqual(
            "https://anonymous.bms.ms/data/body.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://bmssearch.net/bmses/2uLp8a8bJYLmrx"), bmsSearchNextHtml).ToString());
        Assert.AreEqual(
            "https://anonymous.bms.ms/data/itsfree_battle.7z",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueNextHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=entryCore&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/bmstukuru2025/80"), venueMixedPackageHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=entryTypeFirst&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/bmstukuru2025/80"), venueTypeFirstCoreHtml).ToString());
        Assert.AreEqual(
            "https://drive.usercontent.google.com/download?id=entryAnchor&export=download",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/bmstukuru2025/80"), venuePackageAnchorHtml).ToString());
        Assert.AreEqual(
            "https://www.mediafire.com/file/abc/package.zip/file",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueMediaFireHtml).ToString());
        Assert.AreEqual(
            "https://download123.mediafire.com/token/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z",
            PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://www.mediafire.com/file/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z"), mediaFireArchiveNamedLandingHtml).ToString());
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download"), googleDriveUnsafeActionHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://www.mediafire.com/file/abc/package.zip/file"), mediaFireUnsafeHostHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://evilmediafire.com/file/abc/package.zip/file"), mediaFireHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://manbow.nothing.sh/event/event.cgi?action=More_def&num=1&event=1"), manbowUnsafeSchemeHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/event/1/1"), venueUnsafeHostHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueSourcePageOnlyHtml));
        Assert.IsNull(PlaylistUrlAcquisitionWorkflow.ResolveSharedDownloadPageUri(new Uri("https://venue.bmssearch.net/freebattle/30"), venueGoogleDriveFolderOnlyHtml));
    }

    /// <summary>UTF-8拡張filenameとLatin1として渡されたUTF-8日本語名を実名へ戻すことを確認し、取得package名の文字化けを検出します。</summary>
    [TestMethod]
    public void PlaylistUrlDownload_ResolvesContentDispositionFileName()
    {
        Assert.AreEqual(
            "BMSをたくさん作るぜ'25_250126更新分.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveContentDispositionFileName("attachment; filename*=UTF-8''BMS%E3%82%92%E3%81%9F%E3%81%8F%E3%81%95%E3%82%93%E4%BD%9C%E3%82%8B%E3%81%9C%2725_250126%E6%9B%B4%E6%96%B0%E5%88%86.zip"));

        string mojibake = Encoding.GetEncoding("ISO-8859-1").GetString(Encoding.UTF8.GetBytes("BMSをたくさん作るぜ'25_250126更新分.zip"));
        Assert.AreEqual(
            "BMSをたくさん作るぜ'25_250126更新分.zip",
            PlaylistUrlAcquisitionWorkflow.ResolveContentDispositionFileName("attachment; filename=\"" + mojibake + "\""));
    }

    /// <summary>DriveとMediaFireの共有/解決URLを揮発tokenによらない同一キーへ変換することを確認し、同じpackageを別取得として受け付ける誤りを検出します。</summary>
    [TestMethod]
    public void PlaylistUrlDownload_CreatesStableResolvedDownloadKeys()
    {
        Assert.AreEqual(
            "gdrive:abc123",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://drive.usercontent.google.com/download?id=abc123&export=download&confirm=t&uuid=volatile")));
        Assert.AreEqual(
            "gdrive:abc123",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://drive.google.com/file/d/abc123/view?usp=sharing")));
        Assert.AreEqual(
            "mediafire:ar4aa9p35amxfer",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://download123.mediafire.com/token/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z")));
        Assert.AreEqual(
            "mediafire:ar4aa9p35amxfer",
            PlaylistUrlAcquisitionWorkflow.CreatePlaylistUrlDownloadKey(new Uri("https://www.mediafire.com/file/ar4aa9p35amxfer/%5BULTIMATE-lopears%5D-miyako.7z/file")));
    }

    private static PlayHistoryRow CreateResolvedPlayHistoryRow(string hash = "cccccccccccccccccccccccccccccccc")
    {
        string sha256 = new string('d', 64);
        ChartFile file = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(
        [
            hash,
            "Resolved Play History",
            "",
            "Artist",
            "",
            "",
            "",
            "C:\\BMS\\play-history-resolved.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]));
        file = file with { Md5 = hash, Sha256 = sha256 };
        PlayHistoryProjectionIndex projectionIndex = PlayHistoryCatalogTestSupport.Create(
            [file],
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [hash] = sha256 });
        PlayHistoryProjectionResult projected = PlayHistoryRow.ProjectLr2Rows(
            new Lr2PlayHistoryReadResult(
                PlayHistorySourceProfile.Lr2("score.db"),
                [
                    new Lr2PlayHistoryRecord
                    {
                        history_id = 2,
                        hash = hash,
                        played_at = 1000,
                        finalized = 1,
                        score_write_type = "update",
                        new_playcount = 1,
                        playcount_delta = 1,
                        new_exscore = 100,
                        new_totalnotes = 100
                    }
                ],
                [],
                Lr2PlayHistorySchemaStatus.Installed),
            projectionIndex);

        return projected.Rows.Single();
    }

    private static ChartFile CreateContextMenuBmsFile()
    {
        ChartFile file = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.FromRawSongValues(
        [
            "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "Context Menu BMS",
            "",
            "Artist",
            "",
            "",
            "",
            "C:\\BMS\\context-menu.bms",
            "",
            "Folder",
            "",
            "",
            "",
            "",
            "12",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            "",
            ""
        ]));
        file = file with { Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" };
        return file;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BeMusicSeeker.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private static ChartOperationTarget CreateContextMenuTarget(ChartFileKind kind, ChartOperationCapabilities capabilities)
    {
        string path = kind == ChartFileKind.Bmson ? @"C:\Charts\chart.bmson" : @"C:\Charts\chart.bms";
        var chart = new ChartFile(kind, path, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", new string('b', 64), "Title", "Title", "Artist", string.Empty, "Charts", string.Empty, "7", 7, 1, null);
        return new ChartOperationTarget(
            chart,
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    private static string BuildLongDirectoryPath(string tempDirectoryPath, string leafName)
    {
        string path = tempDirectoryPath;
        for (int i = 0; path.Length < 285; i++)
        {
            path = Path.Combine(path, "segment_" + i.ToString("00") + "_" + new string('a', 32));
        }
        return Path.Combine(path, leafName);
    }

    private static ResourceDictionary CreateResourceDictionary(string source)
    {
        return new ResourceDictionary
        {
            Source = new Uri(source, UriKind.RelativeOrAbsolute)
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
