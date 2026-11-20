using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BeMusicSeeker.TestSupport.Wpf;

namespace BeMusicSeeker.Tests;


[TestClass]
[TestCategory("Windows.Shell")]
public sealed class MainWindowContextMenuResourceTests
{

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
                AssertRestingBackgroundAndThumb(scroller, track.Orientation == Orientation.Vertical ? vertical : horizontal, track);
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

            // 既存用は無効時にバー全体を減光し、通常時の背景は非表示のまま保つ。
            vertical.IsEnabled = false;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(((FrameworkElement)vertical.Template.FindName("ScrollBarRoot", vertical)).Opacity < 1d);
            Assert.AreEqual(0d, ((Border)vertical.Template.FindName("TrackChrome", vertical)).Opacity);
            vertical.IsEnabled = true;

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
            var simpleHorizontal = (ScrollBar)scroller.Template.FindName("PART_HorizontalScrollBar", scroller);
            Assert.AreEqual(16d, simpleVertical.ActualWidth, 0.01d);
            Assert.AreEqual(16d, simpleHorizontal.ActualHeight, 0.01d);
            // Simple辞書を共通定義より先に読んでも、遅延解決された同じテンプレートが両軸へ実体化される。
            foreach (ScrollBar simpleBar in new[] { simpleVertical, simpleHorizontal })
            {
                simpleBar.ApplyTemplate();
                var simpleTrack = (Track)simpleBar.Template.FindName("PART_Track", simpleBar);
                Assert.IsNotNull(simpleTrack);
                AssertRestingBackgroundAndThumb(scroller, simpleBar, simpleTrack);
            }

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

    /// <summary>
    /// 通常状態の背景が非表示のまま7 DIPの長手帯として外端に固定され、つまみ3 DIPの両側に各2 DIPが残ることを実矩形で確認します。
    /// 外枠補正は0のため、背景の外端は所属ScrollViewerの内縁と一致します。
    /// </summary>
    private static void AssertRestingBackgroundAndThumb(ScrollViewer owner, ScrollBar bar, Track track)
    {
        bool isVertical = bar.Orientation == Orientation.Vertical;
        var background = (Border)bar.Template.FindName("TrackChrome", bar);
        track.Thumb.ApplyTemplate();
        var thumb = (Border)track.Thumb.Template.FindName("ThumbChrome", track.Thumb);
        Assert.AreEqual(0d, background.Opacity, "既存用の通常背景は非表示。");
        Rect barBounds = bar.TransformToAncestor(owner).TransformBounds(new Rect(bar.RenderSize));
        Rect backgroundBounds = background.TransformToAncestor(owner).TransformBounds(new Rect(background.RenderSize));
        Rect thumbBounds = thumb.TransformToAncestor(owner).TransformBounds(new Rect(thumb.RenderSize));
        if (isVertical)
        {
            Assert.AreEqual(7d, backgroundBounds.Width, 0.25d);
            Assert.AreEqual(3d, thumbBounds.Width, 0.25d);
            Assert.AreEqual(owner.ActualWidth, backgroundBounds.Right, 0.25d, "背景の外端は所属枠の内縁に固定する。");
            Assert.AreEqual(barBounds.Height, backgroundBounds.Height, 0.25d, "背景は長手方向の帯で、つまみの長さだけを囲わない。");
            Assert.AreEqual(2d, thumbBounds.Left - backgroundBounds.Left, 0.25d);
            Assert.AreEqual(2d, backgroundBounds.Right - thumbBounds.Right, 0.25d);
        }
        else
        {
            Assert.AreEqual(7d, backgroundBounds.Height, 0.25d);
            Assert.AreEqual(3d, thumbBounds.Height, 0.25d);
            Assert.AreEqual(owner.ActualHeight, backgroundBounds.Bottom, 0.25d, "背景の外端は所属枠の内縁に固定する。");
            Assert.AreEqual(barBounds.Width, backgroundBounds.Width, 0.25d, "背景は長手方向の帯で、つまみの長さだけを囲わない。");
            Assert.AreEqual(2d, thumbBounds.Top - backgroundBounds.Top, 0.25d);
            Assert.AreEqual(2d, backgroundBounds.Bottom - thumbBounds.Bottom, 0.25d);
        }
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
    /// <summary>共通の上部タブを実表示し、選択項目だけの下端線・フォーカス枠・無枠の本文・Automationの単一選択（外せない・無効項目は選べない）を確認して、旧ListBox型や既定テンプレートの残存を検出します。</summary>
    [TestMethod]
    [DoNotParallelize] // 共有Dispatcherの実Window一覧を表示scopeが所有する。
    public void CanonicalTopTabs_UseSingleSelectionAndStandardAutomation()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var host = new StackPanel();
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/Light.xaml"));
            host.Resources.MergedDictionaries.Add(CreateResourceDictionary(
                "/BeMusicSeeker;component/Themes/CanonicalControls.xaml"));
            TextBlock[] contentTexts = new[]
            {
                new TextBlock { Text = "General content" },
                new TextBlock { Text = "Folder content" },
                new TextBlock { Text = "Custom content" }
            };
            var tabs = new BeMusicSeeker.Views.NavigationTabControl
            {
                Width = 360,
                Height = 140,
                Style = (Style)host.Resources["App.Canonical.TopTabControlStyle"]
            };
            string[] headers = ["General", "Folder", "Custom Folder"];
            for (int index = 0; index < headers.Length; index++)
            {
                tabs.Items.Add(new TabItem { Header = headers[index], Content = contentTexts[index] });
            }

            tabs.SelectedIndex = 0;
            host.Children.Add(tabs);
            var window = new Window
            {
                Width = 420,
                Height = 220,
                Content = host
            };

            windowTest.ShowAndWaitForContentRendered(window);
            tabs.ApplyTemplate();
            tabs.UpdateLayout();
            Assert.AreEqual(0, tabs.SelectedIndex);
            Panel itemPanel = FindVisualDescendants<Panel>(tabs)
                .Single(panel => panel.IsItemsHost && panel.Children.OfType<TabItem>().Count() == tabs.Items.Count);
            Assert.IsTrue(itemPanel is StackPanel { Orientation: Orientation.Horizontal }, "上部タブは横一列に並ぶ（折り返しは専用の共通スタイルだけ）。");
            Assert.AreEqual(KeyboardNavigationMode.Contained, KeyboardNavigation.GetDirectionalNavigation(itemPanel));
            Assert.AreEqual(KeyboardNavigationMode.Once, KeyboardNavigation.GetTabNavigation(itemPanel), "タブ列は一度のTabで出入りし、項目ごとにTab停止しない。");
            TabItem[] items = tabs.Items.Cast<TabItem>().ToArray();
            foreach (TabItem item in items)
            {
                item.ApplyTemplate();
                Assert.IsNotNull(item.Template.FindName("SelectionIndicator", item));
                Assert.IsTrue(item.Focusable, "タブ項目はフォーカスを受けて選択される。");
                Assert.IsTrue(item.IsTabStop);
                // 標準のテーマのスタイルは明示したStyleの下でも背景・枠を与え続けるため、共通スタイルが無効にしていることを確かめる。
                Assert.IsNull(item.Background, "タブ項目は標準テーマの背景を持たない。");
                Assert.IsNull(item.BorderBrush, "タブ項目は標準テーマの枠を持たない。");
                Assert.AreEqual(new Thickness(0), item.BorderThickness);
                FocusVisualAssert.AssertKeyboardFocusRing(item);
                FocusVisualAssert.AssertNoFocusConditionedTriggers(item);
                Assert.IsTrue(
                    FindVisualDescendants<Border>(item).All(border => border.CornerRadius == new CornerRadius(0)),
                    "タブ項目は直角です。");
            }

            FocusVisualAssert.AssertNoFocusConditionedTriggers(tabs);
            Assert.IsNull(tabs.FocusVisualStyle, "枠は項目が持ち、タブ全体には重ねない。");
            Assert.IsNull(tabs.Background, "タブ全体は標準テーマの背景を持たない。");
            Assert.IsNull(tabs.BorderBrush, "タブ全体は標準テーマの枠を持たない。");
            Assert.AreEqual(new Thickness(0), tabs.BorderThickness);
            Color pageForeground = ((SolidColorBrush)contentTexts[0].Foreground).Color;
            Assert.AreEqual(((SolidColorBrush)host.Resources["App.TextBrush"]).Color, pageForeground, "本文の文字色は標準テーマの前景ではなくアプリの文字色を継承する。");
            Assert.AreEqual(FontWeights.Normal, contentTexts[0].FontWeight, "選択項目の太字は本文へ継承されない。");
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)items[0].Template.FindName("SelectionIndicator", items[0])).Visibility);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)items[1].Template.FindName("SelectionIndicator", items[1])).Visibility);

            TabNavigationAssert.AssertSingleRequiredSelection(tabs, () => [tabs.SelectedItem, tabs.SelectedContent]);
            TabNavigationAssert.SelectByAutomation(tabs, 1);
            Assert.AreSame(contentTexts[1], tabs.SelectedContent);
            Assert.AreEqual(
                Visibility.Collapsed,
                ((Border)items[0].Template.FindName("SelectionIndicator", items[0])).Visibility,
                "選択項目だけが下端の線を持つ。");
            Assert.AreEqual(
                Visibility.Visible,
                ((Border)items[1].Template.FindName("SelectionIndicator", items[1])).Visibility);
            TabNavigationAssert.AssertSingleRequiredSelection(tabs, () => [tabs.SelectedItem, tabs.SelectedContent]);

            items[2].IsEnabled = false;
            TestUiDispatcherHost.ProcessQueuedPresentation();
            var disabledNavigationChrome = (Border)items[2].Template.FindName("NavigationItemChrome", items[2]);
            Assert.IsTrue(disabledNavigationChrome.Opacity < 1d);
            AutomationPeer tabsPeer = UIElementAutomationPeer.CreatePeerForElement(tabs);
            AutomationPeer disabledPeer = tabsPeer.GetChildren()
                .Single(peer => peer.GetAutomationControlType() == AutomationControlType.TabItem && !peer.IsEnabled());
            var disabledSelection = (ISelectionItemProvider)disabledPeer.GetPattern(PatternInterface.SelectionItem);
            Assert.IsNotNull(disabledSelection);
            bool selectionRejected = false;
            try
            {
                disabledSelection.Select();
            }
            catch (ElementNotEnabledException)
            {
                selectionRejected = true;
            }

            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.AreEqual(1, tabs.SelectedIndex, "無効な項目は選べない。");
            Assert.IsTrue(
                selectionRejected || !disabledSelection.IsSelected,
                "A disabled top-tab item must reject or ignore public Automation selection.");

            // 本文は枠も余白も描かず、余白はタブ列の周囲だけに足す。
            ContentPresenter contentHost = FindVisualDescendants<ContentPresenter>(tabs)
                .Single(presenter => presenter.Name == "PART_SelectedContentHost");
            Assert.IsFalse(
                FindVisualDescendants<Border>(contentHost).Any(border =>
                    border.BorderThickness.Left > 0d
                    || border.BorderThickness.Top > 0d
                    || border.BorderThickness.Right > 0d
                    || border.BorderThickness.Bottom > 0d
                    || border.CornerRadius.TopLeft > 0d
                    || border.CornerRadius.TopRight > 0d
                    || border.CornerRadius.BottomRight > 0d
                    || border.CornerRadius.BottomLeft > 0d),
                "The shared top-tab content area must be unframed.");
            TextBlock selectedText = contentTexts[1];
            Point defaultOrigin = selectedText.TransformToAncestor(contentHost).Transform(new Point());
            Assert.AreEqual(0d, defaultOrigin.X, 0.25d, "本文は余白を二重に足さない。");
            Assert.AreEqual(0d, defaultOrigin.Y, 0.25d);
            Assert.AreEqual(tabs.ActualWidth, contentHost.ActualWidth, 0.25d, "本文の領域はタブの幅いっぱいに広がる。");
            Assert.AreEqual(contentHost.ActualWidth, selectedText.ActualWidth, 0.25d, "本文は幅いっぱいに伸びる。");
            double contentTopBefore = contentHost.TransformToAncestor(tabs).Transform(new Point()).Y;
            tabs.Padding = new Thickness(7, 9, 11, 13);
            tabs.UpdateLayout();
            Point hostOrigin = contentHost.TransformToAncestor(tabs).Transform(new Point());
            Assert.AreEqual(0d, hostOrigin.X, 0.25d, "Paddingはタブ列の周囲の余白で、本文を横へずらさない。");
            Assert.AreEqual(contentTopBefore + 9d + 13d, hostOrigin.Y, 0.25d, "Paddingはタブ列の上下に足される。");
            Point firstItemOrigin = items[0].TransformToAncestor(tabs).Transform(new Point());
            Assert.AreEqual(7d, firstItemOrigin.X, 0.25d);
            Assert.AreEqual(9d, firstItemOrigin.Y, 0.25d);
        });
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
