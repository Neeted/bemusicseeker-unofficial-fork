using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Rendered presentation contracts for the playlist lamp viewer. The fixture uses the
/// shared WPF dispatcher and immutable aggregation inputs, so it does not depend on
/// application data, physical cursor input, or fixed sleeps.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistLampViewerWindowPresentationTests
{
    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new AssertFailedException("The shared WPF test application is unavailable.");
            if (!application.Resources.MergedDictionaries.Any(dictionary =>
                string.Equals(
                    dictionary.Source?.OriginalString,
                    "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml",
                    StringComparison.OrdinalIgnoreCase)))
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/BeMusicSeeker/Themes/CanonicalDialogStyles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
            }
        });
    }

    [TestMethod]
    public void PercentageFormatter_preservesTinyPositiveBoundarySemantics()
    {
        const int positiveCount = 1;
        const int denominator = 25001;
        double percentage = positiveCount * 100d / denominator;
        CultureInfo culture = CultureInfo.InvariantCulture;

        string formatted = PlaylistLampViewerPercentageFormatter.Format(percentage, culture);
        string formattedZero = PlaylistLampViewerPercentageFormatter.Format(0d, culture);
        string formattedMinimum = PlaylistLampViewerPercentageFormatter.Format(0.01d, culture);

        Assert.IsTrue(percentage > 0d && percentage < 0.005d);
        Assert.IsFalse(string.IsNullOrWhiteSpace(formatted));
        Assert.IsTrue(formatted.Contains("%", StringComparison.Ordinal));
        Assert.IsTrue(
            formatted.Any(character => char.IsDigit(character) && character != '0'),
            "a tiny positive percentage must retain a semantically nonzero numeric indication");
        Assert.AreNotEqual(
            formattedZero,
            formatted,
            "a positive percentage must not be rendered as the zero percentage");
        Assert.AreNotEqual(
            formattedMinimum,
            formatted,
            "a tiny positive percentage must not be clamped to the minimum displayed value");
    }

    [TestMethod]
    public void Viewer_rendersD2HalvesLegendsBoundedBarsAndAtomicInvokeState()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var requests = new List<PlaylistLampViewerNavigationRequest>();
            var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Presentation fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                requests.Add);
            var owner = new Window
            {
                Width = 480,
                Height = 320,
                ShowInTaskbar = false,
                Content = new Grid()
            };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                Assert.IsInstanceOfType<ThemedWindow>(window);
                Assert.AreSame(owner, window.Owner);
                CollectionAssert.AreEqual(
                    new[] { "MAX", "PERFECT", "FC", "EXHARD", "HARD", "NORMAL", "EASY", "ASSIST", "FAILED", "NP" },
                    viewModel.ClearSegments.Select(segment => segment.CategoryKey).ToArray());
                CollectionAssert.AreEqual(
                    new[] { "AAA", "AA", "A", "B", "C", "D", "E", "F", "NP" },
                    viewModel.RankSegments.Select(segment => segment.CategoryKey).ToArray());

                Border clearHost = FindByAutomationId<Border>(window, "PlaylistLampViewerClearGraphHost");
                Border rankHost = FindByAutomationId<Border>(window, "PlaylistLampViewerRankGraphHost");
                Assert.IsTrue(clearHost.ActualWidth > 0d);
                Assert.IsTrue(rankHost.ActualWidth > 0d);
                Assert.IsTrue(clearHost.TranslatePoint(new Point(0, 0), window).X
                    < rankHost.TranslatePoint(new Point(0, 0), window).X);

                string[] renderedClearCategories = FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend"
                        && border.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.Kind == PlaylistLampSegmentKind.Clear)
                    .Select(border => ((PlaylistLampViewerSegmentViewModel)border.DataContext).CategoryKey)
                    .ToArray();
                string[] renderedRankCategories = FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend"
                        && border.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.Kind == PlaylistLampSegmentKind.Rank)
                    .Select(border => ((PlaylistLampViewerSegmentViewModel)border.DataContext).CategoryKey)
                    .ToArray();
                CollectionAssert.AreEqual(viewModel.ClearSegments.Select(segment => segment.CategoryKey).ToArray(), renderedClearCategories);
                CollectionAssert.AreEqual(viewModel.RankSegments.Select(segment => segment.CategoryKey).ToArray(), renderedRankCategories);

                PlaylistLampViewerFolderRowViewModel emptyRow = viewModel.FolderRows.Single(row => row.FolderName == "empty");
                Assert.AreEqual(0, emptyRow.Count);
                Border emptyRowElement = FindDescendants<Border>(window)
                    .Single(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                        && border.DataContext == emptyRow);
                Assert.AreEqual(0, FindDescendants<Button>(emptyRowElement)
                    .Count(button => button.DataContext is PlaylistLampViewerSegmentViewModel));

                foreach (PlaylistLampViewerFolderRowViewModel row in viewModel.FolderRows)
                {
                    Border rowElement = FindDescendants<Border>(window)
                        .Single(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                            && border.DataContext == row);
                    Grid clearHalf = FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerClearFolderHalf");
                    Grid rankHalf = FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerRankFolderHalf");
                    Assert.AreEqual(
                        clearHalf.TranslatePoint(new Point(0, 0), window).Y,
                        rankHalf.TranslatePoint(new Point(0, 0), window).Y,
                        1d,
                        "paired clear/rank folder halves must share a row baseline");
                    AssertFolderHalfOrder(clearHalf, "PlaylistLampViewerClearFolderBarHost");
                    AssertFolderHalfOrder(rankHalf, "PlaylistLampViewerRankFolderBarHost");
                    Assert.AreEqual(0d, rowElement.BorderThickness.Bottom,
                        "folder rows must not render a separator");
                }

                Border[] renderedRows = FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow")
                    .OrderBy(border => border.TranslatePoint(new Point(0, 0), window).Y)
                    .ToArray();
                for (int index = 1; index < renderedRows.Length; index++)
                {
                    Border previousBar = FindByAutomationId<Border>(
                        renderedRows[index - 1],
                        "PlaylistLampViewerClearFolderBarHost");
                    Border currentBar = FindByAutomationId<Border>(
                        renderedRows[index],
                        "PlaylistLampViewerClearFolderBarHost");
                    double previousBottom = previousBar.TranslatePoint(
                        new Point(0, previousBar.ActualHeight), window).Y;
                    double currentTop = currentBar.TranslatePoint(new Point(0, 0), window).Y;
                    double gap = currentTop - previousBottom;
                    Assert.IsTrue(gap >= -1d, "adjacent folder bars must not overlap");
                    Assert.IsTrue(gap <= previousBar.ActualHeight / 2d + 1d,
                        "folder rows must use a compact vertical pitch");
                }

                Button[] segmentButtons = FindDescendants<Button>(window)
                    .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel)
                    .ToArray();
                Assert.IsTrue(segmentButtons.Length > 0, "A positive folder segment must be a rendered button.");
                foreach (Button button in segmentButtons)
                {
                    PlaylistLampViewerSegmentViewModel segment = (PlaylistLampViewerSegmentViewModel)button.DataContext;
                    Assert.IsTrue(segment.Count > 0 && segment.HasPositiveWidth);
                    Assert.IsTrue(button.ActualWidth > 0d,
                        $"positive segment button for {segment.CategoryKey} must have arranged width");
                    ContentPresenter container = FindAncestor<ContentPresenter>(button);
                    Assert.IsNotNull(container, "weighted segment must be hosted by an ItemsControl ContentPresenter");
                    Assert.IsTrue(container.ActualWidth > 0d,
                        $"positive segment container for {segment.CategoryKey} must have arranged width");
                }
                Color[] visibleSegmentColors = segmentButtons
                    .Select(button => button.Background as SolidColorBrush)
                    .Where(brush => brush != null && brush.Color.A > 0)
                    .Select(brush => brush.Color)
                    .Distinct()
                    .ToArray();
                Assert.IsTrue(visibleSegmentColors.Length >= 2,
                    "at least two positive semantic categories must have visible distinct brushes");

                foreach (Border host in FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border)
                        is "PlaylistLampViewerClearGraphHost"
                        or "PlaylistLampViewerRankGraphHost"
                        or "PlaylistLampViewerClearFolderBarHost"
                        or "PlaylistLampViewerRankFolderBarHost"))
                {
                    Button[] hostButtons = FindDescendants<Button>(host)
                        .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel)
                        .ToArray();
                    if (hostButtons.Length == 0)
                    {
                        continue;
                    }
                    double rightmost = hostButtons
                        .Max(button => button.TranslatePoint(new Point(button.ActualWidth, 0), host).X);
                    Assert.IsTrue(rightmost <= host.ActualWidth + 1d,
                        $"segment overflowed its host: {rightmost} > {host.ActualWidth}");
                    Assert.IsTrue(rightmost >= host.ActualWidth - 1d,
                        $"weighted segments did not cover host: {rightmost} < {host.ActualWidth}");
                }

                Border folderRowElement = FindDescendants<Border>(window)
                    .Single(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                        && border.DataContext is PlaylistLampViewerFolderRowViewModel row
                        && row.FolderName == "folder");
                Button assistButton = FindDescendants<Button>(
                    FindByAutomationId<Border>(folderRowElement, "PlaylistLampViewerClearFolderBarHost"))
                    .Single(button =>
                    button.DataContext is PlaylistLampViewerSegmentViewModel segment
                    && segment.CategoryKey == "ASSIST"
                    && segment.IsInvokable);
                var assistSegment = (PlaylistLampViewerSegmentViewModel)assistButton.DataContext;
                Assert.AreEqual(assistSegment.DetailText, AutomationProperties.GetName(assistButton));
                Assert.AreEqual(assistSegment.DetailText, assistButton.ToolTip?.ToString());
                Assert.IsTrue(assistSegment.DetailText.Contains(assistSegment.Count.ToString(), StringComparison.Ordinal));
                Assert.IsTrue(assistSegment.DetailText.Contains("%", StringComparison.Ordinal));
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(assistButton)
                    ?? throw new AssertFailedException("The positive segment button has no Automation peer.");
                Assert.AreEqual(assistSegment.DetailText, peer.GetName());
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                TestUiDispatcherHost.Drain();

                Assert.IsTrue(assistSegment.IsSelected);
                Assert.AreSame(assistSegment, viewModel.SelectedSegment);
                Assert.AreEqual(Resources.PlaylistLampViewer_selected, AutomationProperties.GetItemStatus(assistButton));
                Assert.AreEqual(1, requests.Count);
                Assert.AreEqual(PlaylistLampSegmentKind.Clear, requests[0].Kind);
                Assert.AreEqual(PlaylistLampClearCategory.ASSIST, requests[0].ClearCategory);
                Assert.AreEqual("folder", requests[0].FolderName);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                }
                viewModel.Dispose();
            }
        });
    }

    [TestMethod]
    public void Viewer_globalPositiveSegmentsAreInvokableAndRemainSelectedAfterAutomationInvoke()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var requests = new List<PlaylistLampViewerNavigationRequest>();
            var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Global invocation fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                requests.Add);
            var owner = new Window
            {
                Width = 480,
                Height = 320,
                ShowInTaskbar = false,
                Content = new Grid()
            };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer global invocation first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                Border clearHost = FindByAutomationId<Border>(window, "PlaylistLampViewerClearGraphHost");
                Border rankHost = FindByAutomationId<Border>(window, "PlaylistLampViewerRankGraphHost");
                Button clearButton = FindDescendants<Button>(clearHost)
                    .Single(button => button.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.CategoryKey == "MAX");
                Button rankButton = FindDescendants<Button>(rankHost)
                    .Single(button => button.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.CategoryKey == "AAA");

                Border[] graphHosts = FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border)
                        is "PlaylistLampViewerClearGraphHost"
                        or "PlaylistLampViewerRankGraphHost"
                        or "PlaylistLampViewerClearFolderBarHost"
                        or "PlaylistLampViewerRankFolderBarHost")
                    .ToArray();
                Assert.IsTrue(graphHosts.Length >= 4, "global and folder graph hosts must be rendered");
                foreach (Border host in graphHosts)
                {
                    Assert.AreEqual(0d, host.BorderThickness.Left);
                    Assert.AreEqual(0d, host.BorderThickness.Top);
                    Assert.AreEqual(0d, host.BorderThickness.Right);
                    Assert.AreEqual(0d, host.BorderThickness.Bottom);
                }
                foreach (Button button in FindDescendants<Button>(window)
                    .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel))
                {
                    Assert.AreEqual(0d, button.BorderThickness.Left);
                    Assert.AreEqual(0d, button.BorderThickness.Top);
                    Assert.AreEqual(0d, button.BorderThickness.Right);
                    Assert.AreEqual(0d, button.BorderThickness.Bottom);
                }

                // Regression oracle: global positive segments must be enabled and expose
                // the same Invoke pattern as folder segments. This assertion intentionally
                // fails on the pre-fix snapshot because core global segments have no folder.
                Assert.IsTrue(clearButton.IsEnabled, "the global clear segment must be invokable");
                Assert.IsTrue(rankButton.IsEnabled, "the global rank segment must be invokable");
                Assert.IsTrue(
                    ((PlaylistLampViewerSegmentViewModel)clearButton.DataContext).IsInvokable);
                Assert.IsTrue(
                    ((PlaylistLampViewerSegmentViewModel)rankButton.DataContext).IsInvokable);

                AutomationPeer clearPeer = UIElementAutomationPeer.CreatePeerForElement(clearButton)
                    ?? throw new AssertFailedException("The global clear segment has no Automation peer.");
                AutomationPeer rankPeer = UIElementAutomationPeer.CreatePeerForElement(rankButton)
                    ?? throw new AssertFailedException("The global rank segment has no Automation peer.");
                ((IInvokeProvider)clearPeer.GetPattern(PatternInterface.Invoke)).Invoke();
                TestUiDispatcherHost.Drain();
                var clearSegment = (PlaylistLampViewerSegmentViewModel)clearButton.DataContext;
                Assert.IsTrue(clearSegment.IsSelected);
                Assert.AreSame(clearSegment, viewModel.SelectedSegment);
                Assert.IsTrue(clearButton.BorderThickness.Left > 0d);
                Assert.AreEqual(FontWeights.Bold, clearButton.FontWeight);
                Assert.IsTrue(
                    FindDescendants<Button>(window)
                        .Where(button => button != clearButton
                            && button.DataContext is PlaylistLampViewerSegmentViewModel)
                        .All(button => button.BorderThickness.Left == 0d));

                ((IInvokeProvider)rankPeer.GetPattern(PatternInterface.Invoke)).Invoke();
                TestUiDispatcherHost.Drain();
                var rankSegment = (PlaylistLampViewerSegmentViewModel)rankButton.DataContext;
                Assert.IsTrue(rankSegment.IsSelected);
                Assert.IsFalse(clearSegment.IsSelected);
                Assert.AreSame(rankSegment, viewModel.SelectedSegment);
                Assert.IsTrue(rankButton.BorderThickness.Left > 0d);
                Assert.AreEqual(FontWeights.Bold, rankButton.FontWeight);
                Assert.AreEqual(0d, clearButton.BorderThickness.Left);
                Assert.IsTrue(window.IsVisible, "invoking a graph segment must keep the viewer open");
                Assert.AreEqual(2, requests.Count);
                Assert.IsTrue(requests.All(request =>
                    request.Scope == PlaylistLampViewerNavigationScope.Overall));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                }
                viewModel.Dispose();
            }
        });
    }

    [TestMethod]
    public void Viewer_widthAwareLabelsShowPercentageOnlyWhenTheSegmentCanContainIt()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateWidthAwareRequest());
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Width fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                _ => { });
            var owner = new Window
            {
                Width = 480,
                Height = 320,
                ShowInTaskbar = false,
                Content = new Grid()
            };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer width-aware first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                Border clearHost = FindByAutomationId<Border>(window, "PlaylistLampViewerClearGraphHost");
                Border rankHost = FindByAutomationId<Border>(window, "PlaylistLampViewerRankGraphHost");
                Button[] clearButtons = FindDescendants<Button>(clearHost)
                    .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel)
                    .ToArray();
                Button[] rankButtons = FindDescendants<Button>(rankHost)
                    .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel)
                    .ToArray();
                Assert.AreEqual(2, clearButtons.Length, "synthetic clear data must expose two positive categories");
                Assert.AreEqual(2, rankButtons.Length, "synthetic rank data must expose two positive categories");
                AssertWidthAwareLabels(clearButtons, window);
                AssertWidthAwareLabels(rankButtons, window);

                foreach (Button button in FindDescendants<Button>(window)
                    .Where(candidate => candidate.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.HasPositiveWidth))
                {
                    var segment = (PlaylistLampViewerSegmentViewModel)button.DataContext;
                    Assert.IsTrue(button.ActualWidth > 0d,
                        $"positive segment button for {segment.CategoryKey} must have arranged width");
                    Assert.AreEqual(segment.DetailText, button.ToolTip?.ToString());
                    Assert.AreEqual(segment.DetailText, AutomationProperties.GetName(button));
                    Assert.IsTrue(segment.DetailText.Contains(segment.Count.ToString(), StringComparison.Ordinal));
                    Assert.IsTrue(segment.DetailText.Contains("%", StringComparison.Ordinal));
                    AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(button)
                        ?? throw new AssertFailedException("A positive segment button has no Automation peer.");
                    Assert.AreEqual(segment.DetailText, peer.GetName());
                }

                AssertBoundedHost(clearHost);
                AssertBoundedHost(rankHost);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                }
                viewModel.Dispose();
            }
        });
    }

    [TestMethod]
    public void Viewer_defaultWidthPresentsCanonicalClearLegendOnOneRenderedRow()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Default legend fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                _ => { });
            var owner = new Window
            {
                Width = 480,
                Height = 320,
                ShowInTaskbar = false,
                Content = new Grid()
            };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer default legend first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                StackPanel clearHost = FindByAutomationId<StackPanel>(
                    window,
                    "PlaylistLampViewerClearHost");
                Border[] legends = FindDescendants<Border>(clearHost)
                    .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend"
                        && border.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.Kind == PlaylistLampSegmentKind.Clear)
                    .ToArray();
                Assert.AreEqual(10, legends.Length);
                Assert.IsTrue(legends.Any(legend =>
                    legend.DataContext is PlaylistLampViewerSegmentViewModel segment
                    && segment.CategoryKey == "NP"));
                double firstY = legends[0].TranslatePoint(new Point(0, 0), window).Y;
                Assert.IsTrue(legends.All(legend =>
                    Math.Abs(legend.TranslatePoint(new Point(0, 0), window).Y - firstY) <= 1d),
                    "the canonical clear legend must fit one rendered row at the default width");
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                }
                viewModel.Dispose();
            }
        });
    }

    [TestMethod]
    public void Viewer_folderLabelColumnMeasuresLongestNameCapsAndRefreshesLiveRows()
    {
        const string shortName = "A";
        const string mediumName = "Medium";
        const string longName = "A folder name intentionally longer than the viewer label cap";
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateFolderWidthRequest(shortName, mediumName));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Folder width fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                _ => { });
            var owner = new Window
            {
                Width = 480,
                Height = 320,
                ShowInTaskbar = false,
                Content = new Grid()
            };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer folder width first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                TextBlock[] initialMediumLabels = FindDescendants<TextBlock>(window)
                    .Where(textBlock => textBlock.DataContext is PlaylistLampViewerFolderRowViewModel
                        && textBlock.Text == mediumName)
                    .ToArray();
                Assert.AreEqual(2, initialMediumLabels.Length,
                    "clear and rank halves must share the folder label measurement");
                double mediumTextWidth = MeasureRenderedText(initialMediumLabels[0], mediumName);
                Assert.IsTrue(window.FolderLabelColumnWidth >= mediumTextWidth - 1d);
                Assert.IsTrue(window.FolderLabelColumnWidth <= mediumTextWidth + 1d);
                Assert.IsTrue(window.FolderLabelColumnWidth < 170d);
                Assert.IsTrue(initialMediumLabels.All(label =>
                    Math.Abs(label.ActualWidth - window.FolderLabelColumnWidth) <= 1d));

                source.Replace(CreateFolderWidthRequest(shortName, longName));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAsync(),
                    "playlist lamp viewer folder width live refresh");
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                TextBlock[] longLabels = FindDescendants<TextBlock>(window)
                    .Where(textBlock => textBlock.DataContext is PlaylistLampViewerFolderRowViewModel
                        && textBlock.Text == longName)
                    .ToArray();
                Assert.AreEqual(2, longLabels.Length);
                Assert.AreEqual(170d, window.FolderLabelColumnWidth, 0.1d);
                Assert.IsTrue(longLabels.All(label =>
                    Math.Abs(label.ActualWidth - 170d) <= 1d));
                Assert.IsTrue(longLabels.All(label => label.TextTrimming == TextTrimming.CharacterEllipsis));
                Assert.IsTrue(longLabels.All(label => label.ToolTip?.ToString() == longName));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                }
                viewModel.Dispose();
            }
        });
    }

    [TestMethod]
    public void Viewer_lr2OmitsMaxAndExhardAndDegradedResultHidesScoreSurface()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Lr2));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "LR2 fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                _ => { });
            var owner = new Window
            {
                Width = 480,
                Height = 320,
                ShowInTaskbar = false,
                Content = new Grid()
            };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer LR2 first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();

                Assert.IsFalse(viewModel.ClearSegments.Any(segment =>
                    segment.CategoryKey is "MAX" or "EXHARD"));
                Assert.IsTrue(viewModel.ClearSegments.Any(segment => segment.CategoryKey == "FC"));
                Assert.IsTrue(viewModel.ClearSegments.Any(segment => segment.CategoryKey == "HARD"));
                Assert.IsTrue(viewModel.IsGraphVisible);
                CollectionAssert.AreEqual(
                    new[] { Resources.PlaylistLampViewer_total, Resources.PlaylistLampViewer_owned, Resources.PlaylistLampViewer_missing,
                        Resources.PlaylistLampViewer_ownership_rate, Resources.PlaylistLampViewer_score_source, Resources.PlaylistLampViewer_playlist_last_update },
                    viewModel.SummaryCards.Select(card => card.Label).ToArray());
                CollectionAssert.AreEqual(
                    new[] { Resources.PlaylistLampViewer_played, Resources.PlaylistLampViewer_no_play, Resources.PlaylistLampViewer_play_rate,
                        Resources.PlaylistLampViewer_average_ex_rate, Resources.PlaylistLampViewer_clear_rate },
                    viewModel.ScoreCards.Select(card => card.Label).ToArray());

                var degraded = CreateRequest(ActiveScoreSource.None);
                source.Replace(degraded);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAsync(),
                    "playlist lamp viewer degraded refresh");
                TestUiDispatcherHost.Drain();

                Assert.IsTrue(viewModel.IsDegraded);
                Assert.IsFalse(viewModel.IsGraphVisible);
                Assert.IsFalse(viewModel.IsScoreCardsVisible);
                Assert.AreEqual(0, FindDescendants<Button>(window)
                    .Count(button => button.DataContext is PlaylistLampViewerSegmentViewModel));
                Assert.IsTrue(viewModel.SummaryCards.Any(card => card.Label == Resources.PlaylistLampViewer_total));
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }
                if (owner.IsVisible)
                {
                    owner.Close();
                }
                viewModel.Dispose();
            }
        });
    }

    private static void AssertFolderHalfOrder(Grid half, string barHostAutomationId)
    {
        DependencyObject[] children = Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(half))
            .Select(index => VisualTreeHelper.GetChild(half, index))
            .ToArray();
        Assert.IsTrue(children.Length >= 3, "folder half must contain label, bar, and count columns");
        Assert.IsInstanceOfType<TextBlock>(children[0]);
        Assert.IsInstanceOfType<Border>(children[1]);
        Assert.IsInstanceOfType<TextBlock>(children[2]);
        Assert.AreEqual(barHostAutomationId, AutomationProperties.GetAutomationId(children[1]));
    }

    private static void AssertBoundedHost(Border host)
    {
        Button[] hostButtons = FindDescendants<Button>(host)
            .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel)
            .ToArray();
        Assert.IsTrue(hostButtons.Length > 0, "a graph host must contain positive segments");
        double rightmost = hostButtons
            .Max(button => button.TranslatePoint(new Point(button.ActualWidth, 0), host).X);
        Assert.IsTrue(rightmost <= host.ActualWidth + 1d,
            $"segment overflowed its host: {rightmost} > {host.ActualWidth}");
        Assert.IsTrue(rightmost >= host.ActualWidth - 1d,
            $"weighted segments did not cover host: {rightmost} < {host.ActualWidth}");
    }

    private static void AssertWidthAwareLabels(Button[] buttons, Window coordinateRoot)
    {
        Button wideButton = buttons.Single(button =>
            ((PlaylistLampViewerSegmentViewModel)button.DataContext).Count == 203);
        Button narrowButton = buttons.Single(button =>
            ((PlaylistLampViewerSegmentViewModel)button.DataContext).Count == 1);
        Assert.IsTrue(wideButton.ActualWidth > narrowButton.ActualWidth,
            "the width-aware fixture must provide an actual wide and narrow segment");
        Assert.IsTrue(wideButton.ActualWidth > 0d);
        Assert.IsTrue(narrowButton.ActualWidth > 0d);

        string wideContent = wideButton.Content as string;
        Assert.IsFalse(string.IsNullOrWhiteSpace(wideContent),
            "a segment with enough width must expose an in-bar percentage");
        Assert.IsTrue(wideContent.Contains("%", StringComparison.Ordinal),
            "the wide in-bar text must be a visible percentage");
        Assert.IsTrue(wideContent != ((PlaylistLampViewerSegmentViewModel)wideButton.DataContext).DetailText,
            "the in-bar label must not expand to the full tooltip detail");
        TextBlock renderedWideLabel = FindDescendants<TextBlock>(wideButton)
            .SingleOrDefault(textBlock => textBlock.Text == wideContent);
        Assert.IsNotNull(renderedWideLabel,
            "the wide percentage must be present in the rendered button content");
        Point buttonOrigin = wideButton.TranslatePoint(new Point(0, 0), coordinateRoot);
        Point buttonEnd = wideButton.TranslatePoint(
            new Point(wideButton.ActualWidth, wideButton.ActualHeight),
            coordinateRoot);
        Point labelOrigin = renderedWideLabel.TranslatePoint(new Point(0, 0), coordinateRoot);
        Point labelEnd = renderedWideLabel.TranslatePoint(
            new Point(renderedWideLabel.ActualWidth, renderedWideLabel.ActualHeight),
            coordinateRoot);
        Assert.IsTrue(labelOrigin.X >= buttonOrigin.X - 1d
            && labelOrigin.Y >= buttonOrigin.Y - 1d
            && labelEnd.X <= buttonEnd.X + 1d
            && labelEnd.Y <= buttonEnd.Y + 1d,
            "the wide percentage label must remain inside its segment");
        Assert.IsTrue(narrowButton.Content is null
            || string.IsNullOrWhiteSpace(narrowButton.Content.ToString()),
            "a narrow segment must not render a clipped or ellipsized label");
    }

    private static double MeasureRenderedText(TextBlock textBlock, string value)
    {
        var typeface = new Typeface(
            textBlock.FontFamily,
            textBlock.FontStyle,
            textBlock.FontWeight,
            textBlock.FontStretch);
        var formattedText = new FormattedText(
            value,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            textBlock.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(textBlock).PixelsPerDip);
        return formattedText.WidthIncludingTrailingWhitespace;
    }

    private static T FindByAutomationId<T>(DependencyObject root, string automationId)
        where T : DependencyObject
    {
        return FindDescendants<T>(root)
            .Single(element => AutomationProperties.GetAutomationId(element) == automationId);
    }

    private static T FindAncestor<T>(DependencyObject element, string automationId)
        where T : DependencyObject
    {
        DependencyObject current = element;
        while (current != null)
        {
            if (current is T match && AutomationProperties.GetAutomationId(current) == automationId)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        DependencyObject current = VisualTreeHelper.GetParent(element);
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root == null)
        {
            yield break;
        }
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }
            foreach (T descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static PlaylistLampAggregationRequest CreateRequest(ActiveScoreSource source)
    {
        var max = new PlaylistLampScore(
            "hash-max",
            "sha-max",
            ClearType.MAX,
            RankType.MAX,
            100,
            0,
            100,
            3);
        var assist = new PlaylistLampScore(
            "hash-assist",
            "sha-assist",
            ClearType.L_ASSIST,
            RankType.A,
            50,
            50,
            100,
            1);
        var byHash = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
        {
            [max.Hash] = max,
            [assist.Hash] = assist
        };
        var bySha256 = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
        {
            [max.Sha256] = max,
            [assist.Sha256] = assist
        };
        var scoreSnapshot = new PlaylistLampScoreSnapshot(
            source,
            source == ActiveScoreSource.None ? ScoreTableLoadStatus.NotConfigured : ScoreTableLoadStatus.Loaded,
            1,
            1,
            new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
            source == ActiveScoreSource.Lr2 ? byHash : null,
            source == ActiveScoreSource.Beatoraja ? bySha256 : null);

        var entries = new[]
        {
            new PlaylistLampEntrySnapshot("folder", "max", true, md5: max.Hash, sha256: max.Sha256, resolvedMd5: max.Hash, resolvedSha256: max.Sha256),
            new PlaylistLampEntrySnapshot("folder", "assist", true, md5: assist.Hash, sha256: assist.Sha256, resolvedMd5: assist.Hash, resolvedSha256: assist.Sha256),
            new PlaylistLampEntrySnapshot("folder", "missing", false, md5: "hash-missing", sha256: "sha-missing"),
        };
        return new PlaylistLampAggregationRequest(
            "playlist",
            ["folder", "empty"],
            entries,
            scoreSnapshot,
            new DateTime(2026, 8, 28, 1, 0, 0, DateTimeKind.Utc));
    }

    private static PlaylistLampAggregationRequest CreateWidthAwareRequest()
    {
        PlaylistLampScore[] scores = Enumerable.Range(0, 204)
            .Select(index =>
            {
                string suffix = index.ToString();
                return new PlaylistLampScore(
                    "width-hash-" + suffix,
                    "width-sha-" + suffix,
                    index == 0 ? ClearType.L_ASSIST : ClearType.HARD,
                    index == 0 ? RankType.F : RankType.A,
                    50,
                    50,
                    100,
                    1);
            })
            .ToArray();
        var bySha256 = scores.ToDictionary(score => score.Sha256, StringComparer.OrdinalIgnoreCase);
        PlaylistLampEntrySnapshot[] entries = scores
            .Select((score, index) => new PlaylistLampEntrySnapshot(
                "wide",
                "entry-" + index,
                true,
                md5: score.Hash,
                sha256: score.Sha256,
                resolvedMd5: score.Hash,
                resolvedSha256: score.Sha256))
            .ToArray();
        var scoreSnapshot = new PlaylistLampScoreSnapshot(
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
            null,
            bySha256);
        return new PlaylistLampAggregationRequest(
            "playlist",
            ["wide"],
            entries,
            scoreSnapshot,
            new DateTime(2026, 8, 28, 1, 0, 0, DateTimeKind.Utc));
    }

    private static PlaylistLampAggregationRequest CreateFolderWidthRequest(params string[] folderNames)
    {
        var scores = (folderNames ?? [])
            .Select((folderName, index) => new PlaylistLampScore(
                "folder-width-hash-" + index.ToString(CultureInfo.InvariantCulture),
                "folder-width-sha-" + index.ToString(CultureInfo.InvariantCulture),
                ClearType.HARD,
                RankType.A,
                50,
                50,
                100,
                1))
            .ToArray();
        var bySha256 = scores.ToDictionary(score => score.Sha256, StringComparer.OrdinalIgnoreCase);
        PlaylistLampEntrySnapshot[] entries = (folderNames ?? [])
            .Select((folderName, index) => new PlaylistLampEntrySnapshot(
                folderName,
                "folder-width-entry-" + index.ToString(CultureInfo.InvariantCulture),
                true,
                md5: scores[index].Hash,
                sha256: scores[index].Sha256,
                resolvedMd5: scores[index].Hash,
                resolvedSha256: scores[index].Sha256))
            .ToArray();
        var scoreSnapshot = new PlaylistLampScoreSnapshot(
            ActiveScoreSource.Beatoraja,
            ScoreTableLoadStatus.Loaded,
            1,
            1,
            new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
            null,
            bySha256);
        return new PlaylistLampAggregationRequest(
            "playlist",
            folderNames ?? [],
            entries,
            scoreSnapshot,
            new DateTime(2026, 8, 28, 1, 0, 0, DateTimeKind.Utc));
    }

    private sealed class FixedLampSource : IPlaylistLampViewerDataSource
    {
        private PlaylistLampAggregationRequest request;

        private EventHandler<PlaylistLampViewerSourceChangedEventArgs> changed;

        internal FixedLampSource(PlaylistLampAggregationRequest request)
        {
            this.request = request;
        }

        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed
        {
            add => changed += value;
            remove => changed -= value;
        }

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            string playlistId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(request);
        }

        internal void Replace(PlaylistLampAggregationRequest request)
        {
            this.request = request;
        }
    }
}
