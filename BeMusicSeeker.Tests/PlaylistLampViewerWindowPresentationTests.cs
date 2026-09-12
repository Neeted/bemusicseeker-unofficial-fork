using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public void Viewer_playlistLastUpdateDisplaysLegacyLocalWallClockWithoutTimezoneConversion()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                DateTime playlistLastUpdated = new(2026, 8, 29, 10, 49, 7, DateTimeKind.Unspecified);
                CultureInfo[] testCultures =
                [
                    CultureInfo.GetCultureInfo("en-US"),
                    CultureInfo.GetCultureInfo("de-DE")
                ];
                Assert.AreNotEqual(
                    playlistLastUpdated.ToString("g", testCultures[0]),
                    playlistLastUpdated.ToString("g", testCultures[1]),
                    "the selected cultures must distinguish current-culture formatting from a fixed format");

                foreach (CultureInfo testCulture in testCultures)
                {
                    CultureInfo.CurrentCulture = testCulture;
                    CultureInfo.CurrentUICulture = testCulture;
                    AssertPlaylistLastUpdatePresentation(playlistLastUpdated, testCulture);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        });
    }

    [TestMethod]
    public void Viewer_playlistLastUpdatePresentationDoesNotConvertDateTime()
    {
        MethodInfo addStatisticsCards = typeof(PlaylistLampViewerViewModel).GetMethod(
            "AddStatisticsCards",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AssertFailedException("The playlist statistics-card builder is missing.");
        MethodInfo formatter = typeof(PlaylistLampViewerViewModel).GetMethod(
            "FormatTimestamp",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("The playlist last-update formatter is missing.");

        // PLV-TIME-01 prohibits timezone conversion. This compiled-semantic guard keeps
        // a UTC-configured host from masking conversion in either the card builder or formatter.
        foreach (MethodInfo method in new[] { addStatisticsCards, formatter })
        {
            MethodBase[] calledMethods = StartupLibraryConstructionTestSupport
                .EnumerateCalledMethods(method)
                .ToArray();
            Assert.IsFalse(
                calledMethods.Any(IsPlaylistLastUpdateTimeConversion),
                $"{method.Name} must not call timezone conversion APIs for playlist last-update values");
        }
    }

    [TestMethod]
    public void Viewer_playlistLastUpdateDisplaysUnavailableWhenRequestTimestampIsNull()
    {
        TestUiDispatcherHost.RunWindowTest(_ =>
        {
            var source = new FixedLampSource(CreateRequest(
                ActiveScoreSource.Beatoraja,
                playlistLastUpdated: null));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Null last-update fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                _ => { });
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp viewer null last-update first presentable result");

                Assert.IsNotNull(
                    viewModel.CurrentResult.Statistics.SourceLastUpdatedUtc,
                    "the fixture must retain a distinct score-source UTC timestamp");
                PlaylistLampViewerStatCardViewModel card = viewModel.StatisticsCards.Single(
                    candidate => candidate.Label == Resources.PlaylistLampViewer_playlist_last_update);
                Assert.AreEqual(Resources.PlaylistLampViewer_unavailable, card.Value);
            }
            finally
            {
                viewModel.Dispose();
            }
        });
    }

    [TestMethod]
    public void Viewer_historicalDatePicker_is_calendar_only_and_latest_recovers_the_window()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            DateTime today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
            DateTime earliest = today.AddDays(-2);
            DateTime selected = today.AddDays(-1);
            var source = new HistoricalLampSource();
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Historical date fixture",
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
            EventHandler<PlaylistLampAggregationResultChangedEventArgs> resultHandler = null;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp historical date first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                DatePicker picker = FindByAutomationId<DatePicker>(
                    window,
                    "PlaylistLampViewerAsOfDatePicker");
                DatePickerTextBox pickerTextBox = FindDescendants<DatePickerTextBox>(picker).Single();
                Assert.IsTrue(pickerTextBox.IsReadOnly, "the date text box must not allow free-form input");
                Assert.IsFalse(picker.IsEnabled, "zero history must disable historical date selection");
                Button latestBeforeHistory = FindByAutomationId<Button>(
                    window,
                    "PlaylistLampViewerLatestButton");
                Assert.IsTrue(latestBeforeHistory.IsEnabled, "Latest must remain usable with zero history");

                source.EnableHistory();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    session.RefreshAsync(),
                    "playlist lamp historical date range refresh");
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                Assert.IsTrue(picker.IsEnabled, "a non-empty provider history must enable the calendar");
                Assert.AreEqual(earliest, picker.DisplayDateStart);
                Assert.AreEqual(today, picker.DisplayDateEnd);

                var selectedAccepted = NewCompletion<PlaylistLampAggregationResult>();
                var latestAccepted = NewCompletion<PlaylistLampAggregationResult>();
                resultHandler = (_, args) =>
                {
                    if (args?.Result?.State != PlaylistLampViewerState.Ready)
                    {
                        return;
                    }
                    if (args.Result.Query.SelectedLocalDate == selected)
                    {
                        selectedAccepted.TrySetResult(args.Result);
                    }
                    else if (!args.Result.Query.SelectedLocalDate.HasValue)
                    {
                        latestAccepted.TrySetResult(args.Result);
                    }
                };
                session.ResultChanged += resultHandler;

                Task selectedQuery = source.ExpectQuery(selected);
                picker.SelectedDate = selected;
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    selectedQuery,
                    "playlist lamp historical calendar query");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    selectedAccepted.Task,
                    "playlist lamp historical selected result");
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                Assert.AreEqual(selected, picker.SelectedDate);
                Assert.AreEqual(selected, viewModel.SelectedAsOfDate);
                Assert.AreEqual(selected, session.Query.SelectedLocalDate);
                Assert.AreEqual(selected, session.Current.Query.SelectedLocalDate);

                Button latest = FindByAutomationId<Button>(window, "PlaylistLampViewerLatestButton");
                Task latestQuery = source.ExpectQuery(null);
                latest.Command.Execute(null);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    latestQuery,
                    "playlist lamp historical latest query");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    latestAccepted.Task,
                    "playlist lamp historical latest result");
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                Assert.IsNull(picker.SelectedDate);
                Assert.IsNull(viewModel.SelectedAsOfDate);
                Assert.IsNull(session.Query.SelectedLocalDate);
                Assert.AreEqual(PlaylistLampHistoricalSnapshotStatus.Latest, session.Current.HistoricalStatus);
                Assert.IsTrue(picker.IsEnabled, "Latest must retain a recoverable history range");
                Assert.AreEqual(earliest, picker.DisplayDateStart);
                Assert.AreEqual(today, picker.DisplayDateEnd);
            }
            finally
            {
                if (resultHandler != null)
                {
                    session.ResultChanged -= resultHandler;
                }
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
                    AssertFolderHalfGeometry(clearHalf, row, "PlaylistLampViewerClearFolderBarHost", window);
                    AssertFolderHalfGeometry(rankHalf, row, "PlaylistLampViewerRankFolderBarHost", window);
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
                AssertFolderBarsAlign(renderedRows);

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

                foreach (Border legend in FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend"))
                {
                    foreach (TextBlock textBlock in FindDescendants<TextBlock>(legend))
                    {
                        AssertContrastForeground(legend, textBlock);
                    }
                }
                foreach (Button button in segmentButtons)
                {
                    AssertContrastForeground(button, button);
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
                PlaylistLampViewerSegmentViewModel globalAssistSegment = viewModel.ClearSegments
                    .Single(segment => segment.CategoryKey == assistSegment.CategoryKey);
                Border assistLegend = FindDescendants<Border>(window)
                    .Single(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend"
                        && ReferenceEquals(border.DataContext, globalAssistSegment));
                Button globalAssistButton = FindDescendants<Button>(window)
                    .Single(button => ReferenceEquals(button.DataContext, globalAssistSegment));
                string assistResourceKey = "PlaylistLamp." + globalAssistSegment.PaletteKey;
                Assert.IsTrue(window.Resources.Contains(assistResourceKey));
                object originalAssistBrush = window.Resources[assistResourceKey];
                try
                {
                    SetViewerPaletteBrush(window, assistResourceKey, Color.FromRgb(24, 24, 24));
                    AssertControlledContrast(
                        assistLegend,
                        globalAssistButton,
                        Color.FromRgb(24, 24, 24),
                        Colors.White);

                    SetViewerPaletteBrush(window, assistResourceKey, Color.FromRgb(240, 240, 240));
                    AssertControlledContrast(
                        assistLegend,
                        globalAssistButton,
                        Color.FromRgb(240, 240, 240),
                        Colors.Black);

                    ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
                    TestUiDispatcherHost.Drain();

                    SetViewerPaletteBrush(window, assistResourceKey, Color.FromRgb(24, 24, 24));
                    AssertControlledContrast(
                        assistLegend,
                        assistButton,
                        Color.FromRgb(24, 24, 24),
                        Colors.White);

                    SetViewerPaletteBrush(window, assistResourceKey, Color.FromRgb(240, 240, 240));
                    AssertControlledContrast(
                        assistLegend,
                        assistButton,
                        Color.FromRgb(240, 240, 240),
                        Colors.Black);
                }
                finally
                {
                    window.Resources[assistResourceKey] = originalAssistBrush;
                    TestUiDispatcherHost.Drain();
                    window.UpdateLayout();
                }

                AssertContrastForeground(assistButton, assistButton);

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
                Assert.AreEqual(1290d, window.Width, 0.01d,
                    "the viewer default width is the contracted 1500-minus-210 DIP layout");
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

                AssertRenderedStatisticOrder(
                    window,
                    new[]
                    {
                        Resources.PlaylistLampViewer_total,
                        Resources.PlaylistLampViewer_owned,
                        Resources.PlaylistLampViewer_missing,
                        Resources.PlaylistLampViewer_ownership_rate,
                        Resources.PlaylistLampViewer_score_source,
                        Resources.PlaylistLampViewer_played,
                        Resources.PlaylistLampViewer_no_play,
                        Resources.PlaylistLampViewer_play_rate,
                        Resources.PlaylistLampViewer_average_ex_rate,
                        Resources.PlaylistLampViewer_clear_rate,
                        Resources.PlaylistLampViewer_playlist_last_update
                    });
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

                foreach (PlaylistLampViewerFolderRowViewModel row in viewModel.FolderRows)
                {
                    Assert.AreEqual(
                        row.Count.ToString("N0", CultureInfo.CurrentCulture),
                        row.CountText,
                        "folder counts use the localized numeric format without a unit");
                }

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
    public void Viewer_folderCountUsesLocalizedN0AndSharedCappedCountColumn()
    {
        const int initialCount = 1234;
        const int cappedCount = 12345;
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateCountWidthRequest(initialCount, "initial"));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Folder count fixture",
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
                    "playlist lamp viewer count width first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                PlaylistLampViewerFolderRowViewModel initialRow = viewModel.FolderRows
                    .Single(row => row.FolderName == "initial");
                Assert.AreEqual(
                    initialCount.ToString("N0", CultureInfo.CurrentCulture),
                    initialRow.CountText);
                TextBlock initialCountBlock = FindFolderCountBlock(window, initialRow);
                Grid initialClearHalf = FindByAutomationId<Grid>(
                    FindDescendants<Border>(window).Single(border =>
                        AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                        && border.DataContext == initialRow),
                    "PlaylistLampViewerClearFolderHalf");
                double expectedInitialWidth = MeasureRenderedText(initialCountBlock, initialRow.CountText);
                Assert.AreEqual(expectedInitialWidth, initialClearHalf.ColumnDefinitions[4].ActualWidth, 1d);
                Assert.IsTrue(FindFolderCountColumnWidths(window, viewModel)
                    .All(width => Math.Abs(width - expectedInitialWidth) <= 1d),
                    "both halves and every row must share the same live count column width");
                Assert.IsTrue(initialRow.CountText.Any(char.IsDigit));
                Assert.IsTrue(int.TryParse(
                    initialRow.CountText,
                    NumberStyles.Number,
                    CultureInfo.CurrentCulture,
                    out int parsedInitialCount));
                Assert.AreEqual(initialCount, parsedInitialCount);

                source.Replace(CreateCountWidthRequest(cappedCount, "capped"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAsync(),
                    "playlist lamp viewer count width live refresh");
                TestUiDispatcherHost.Drain();
                window.UpdateLayout();

                PlaylistLampViewerFolderRowViewModel cappedRow = viewModel.FolderRows
                    .Single(row => row.FolderName == "capped");
                Assert.AreEqual(
                    cappedCount.ToString("N0", CultureInfo.CurrentCulture),
                    cappedRow.CountText);
                Assert.IsTrue(int.TryParse(
                    cappedRow.CountText,
                    NumberStyles.Number,
                    CultureInfo.CurrentCulture,
                    out int parsedCappedCount));
                Assert.AreEqual(cappedCount, parsedCappedCount);
                TextBlock cappedCountBlock = FindFolderCountBlock(window, cappedRow);
                Grid cappedClearHalf = FindByAutomationId<Grid>(
                    FindDescendants<Border>(window).Single(border =>
                        AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                        && border.DataContext == cappedRow),
                    "PlaylistLampViewerClearFolderHalf");
                double renderedCapWidth = MeasureRenderedText(
                    cappedCountBlock,
                    9999.ToString("N0", CultureInfo.CurrentCulture));
                Assert.AreEqual(
                    renderedCapWidth,
                    cappedClearHalf.ColumnDefinitions[4].ActualWidth,
                    1d);
                Assert.IsTrue(cappedClearHalf.ColumnDefinitions[4].ActualWidth > expectedInitialWidth);
                Assert.IsTrue(FindFolderCountColumnWidths(window, viewModel)
                    .All(width => Math.Abs(width - renderedCapWidth) <= 1d),
                    "a refreshed count column remains shared across both halves and rows");

                foreach (PlaylistLampViewerFolderRowViewModel row in viewModel.FolderRows)
                {
                    Border rowElement = FindDescendants<Border>(window)
                        .Single(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                            && border.DataContext == row);
                    Grid clearHalf = FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerClearFolderHalf");
                    Grid rankHalf = FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerRankFolderHalf");
                    AssertFolderHalfGeometry(clearHalf, row, "PlaylistLampViewerClearFolderBarHost", window);
                    AssertFolderHalfGeometry(rankHalf, row, "PlaylistLampViewerRankFolderBarHost", window);
                }
                AssertFolderBarsAlign(
                    FindDescendants<Border>(window)
                        .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow")
                        .ToArray());
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
                AssertRenderedStatisticOrder(
                    window,
                    new[]
                    {
                        Resources.PlaylistLampViewer_total,
                        Resources.PlaylistLampViewer_owned,
                        Resources.PlaylistLampViewer_missing,
                        Resources.PlaylistLampViewer_ownership_rate,
                        Resources.PlaylistLampViewer_score_source,
                        Resources.PlaylistLampViewer_played,
                        Resources.PlaylistLampViewer_no_play,
                        Resources.PlaylistLampViewer_play_rate,
                        Resources.PlaylistLampViewer_average_ex_rate,
                        Resources.PlaylistLampViewer_clear_rate,
                        Resources.PlaylistLampViewer_playlist_last_update
                    });

                var degraded = CreateRequest(ActiveScoreSource.None);
                source.Replace(degraded);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAsync(),
                    "playlist lamp viewer degraded refresh");
                TestUiDispatcherHost.Drain();

                Assert.IsTrue(viewModel.IsDegraded);
                Assert.IsFalse(viewModel.IsGraphVisible);
                Assert.AreEqual(0, FindDescendants<Button>(window)
                    .Count(button => button.DataContext is PlaylistLampViewerSegmentViewModel));
                AssertRenderedStatisticOrder(
                    window,
                    new[]
                    {
                        Resources.PlaylistLampViewer_total,
                        Resources.PlaylistLampViewer_owned,
                        Resources.PlaylistLampViewer_missing,
                        Resources.PlaylistLampViewer_ownership_rate,
                        Resources.PlaylistLampViewer_score_source,
                        Resources.PlaylistLampViewer_playlist_last_update
                    });
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

    private static void AssertFolderHalfGeometry(
        Grid half,
        PlaylistLampViewerFolderRowViewModel row,
        string barHostAutomationId,
        Window coordinateRoot)
    {
        TextBlock label = FindFolderLabelBlock(half, row);
        Border bar = FindByAutomationId<Border>(half, barHostAutomationId);
        TextBlock count = FindFolderCountBlock(half, row);
        double labelRight = label.TranslatePoint(new Point(label.ActualWidth, 0), coordinateRoot).X;
        double barLeft = bar.TranslatePoint(new Point(0, 0), coordinateRoot).X;
        double barRight = bar.TranslatePoint(new Point(bar.ActualWidth, 0), coordinateRoot).X;
        double countLeft = count.TranslatePoint(new Point(0, 0), coordinateRoot).X;
        Assert.AreEqual(6d, barLeft - labelRight, 1d,
            "folder label and bar are separated by the explicit six-DIP spacer");
        Assert.AreEqual(6d, countLeft - barRight, 1d,
            "folder bar and count are separated by the explicit six-DIP spacer");
        Assert.IsTrue(count.ActualWidth > 0d, "folder count column must be arranged");
    }

    private static void AssertFolderBarsAlign(Border[] rows)
    {
        Assert.IsTrue(rows.Length > 0, "folder bar alignment requires at least one rendered row");
        Grid firstClearHalf = FindByAutomationId<Grid>(rows[0], "PlaylistLampViewerClearFolderHalf");
        Grid firstRankHalf = FindByAutomationId<Grid>(rows[0], "PlaylistLampViewerRankFolderHalf");
        Border firstClearBar = FindByAutomationId<Border>(firstClearHalf, "PlaylistLampViewerClearFolderBarHost");
        Border firstRankBar = FindByAutomationId<Border>(firstRankHalf, "PlaylistLampViewerRankFolderBarHost");
        double firstClearBarLeft = firstClearBar.TranslatePoint(new Point(0, 0), firstClearHalf).X;
        double firstRankBarLeft = firstRankBar.TranslatePoint(new Point(0, 0), firstRankHalf).X;
        foreach (Border row in rows.Skip(1))
        {
            Grid clearHalf = FindByAutomationId<Grid>(row, "PlaylistLampViewerClearFolderHalf");
            Grid rankHalf = FindByAutomationId<Grid>(row, "PlaylistLampViewerRankFolderHalf");
            Border clearBar = FindByAutomationId<Border>(clearHalf, "PlaylistLampViewerClearFolderBarHost");
            Border rankBar = FindByAutomationId<Border>(rankHalf, "PlaylistLampViewerRankFolderBarHost");
            double clearBarLeft = clearBar.TranslatePoint(new Point(0, 0), clearHalf).X;
            double rankBarLeft = rankBar.TranslatePoint(new Point(0, 0), rankHalf).X;
            Assert.AreEqual(firstClearBarLeft, clearBarLeft, 1d,
                "clear bars must share the live label/count column alignment across rows");
            Assert.AreEqual(firstRankBarLeft, rankBarLeft, 1d,
                "rank bars must share the live label/count column alignment across rows");
            Assert.AreEqual(firstClearBar.ActualWidth, clearBar.ActualWidth, 1d,
                "clear bars must share the live width across rows");
            Assert.AreEqual(firstRankBar.ActualWidth, rankBar.ActualWidth, 1d,
                "rank bars must share the live width across rows");
        }
        Assert.AreEqual(firstClearBarLeft, firstRankBarLeft, 1d,
            "clear and rank bars must start at the same aligned column");
        Assert.AreEqual(firstClearBar.ActualWidth, firstRankBar.ActualWidth, 1d,
            "clear and rank bars must share the same aligned width");
    }

    private static TextBlock FindFolderLabelBlock(
        DependencyObject root,
        PlaylistLampViewerFolderRowViewModel row)
        => FindDescendants<TextBlock>(root)
            .Single(textBlock => textBlock.DataContext == row && textBlock.Text == row.FolderName);

    private static TextBlock FindFolderCountBlock(
        DependencyObject root,
        PlaylistLampViewerFolderRowViewModel row)
        => FindDescendants<TextBlock>(root)
            .First(textBlock => textBlock.DataContext == row
                && textBlock.Text == row.CountText
                && AutomationProperties.GetAutomationId(textBlock) == "PlaylistLampViewerFolderCount");

    private static IEnumerable<double> FindFolderCountColumnWidths(
        Window window,
        PlaylistLampViewerViewModel viewModel)
    {
        foreach (PlaylistLampViewerFolderRowViewModel row in viewModel.FolderRows)
        {
            Border rowElement = FindDescendants<Border>(window)
                .Single(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                    && border.DataContext == row);
            yield return FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerClearFolderHalf")
                .ColumnDefinitions[4]
                .ActualWidth;
            yield return FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerRankFolderHalf")
                .ColumnDefinitions[4]
                .ActualWidth;
        }
    }

    private static void AssertContrastForeground(DependencyObject backgroundElement, Control foregroundElement)
    {
        Brush backgroundBrush = GetBackgroundBrush(backgroundElement);
        Assert.IsInstanceOfType(backgroundBrush, typeof(SolidColorBrush));
        Assert.IsInstanceOfType<SolidColorBrush>(foregroundElement.Foreground);
        var background = (SolidColorBrush)backgroundBrush;
        var foreground = (SolidColorBrush)foregroundElement.Foreground;
        Assert.AreEqual(255, foreground.Color.A, "filled text must use an opaque foreground brush");
        Assert.IsTrue(
            foreground.Color == Colors.Black || foreground.Color == Colors.White,
            "filled text must choose opaque black or white");
        double backgroundLuminance = RelativeLuminance(background.Color);
        double blackContrast = (backgroundLuminance + 0.05d) / 0.05d;
        double whiteContrast = 1.05d / (backgroundLuminance + 0.05d);
        Color expected = blackContrast >= whiteContrast ? Colors.Black : Colors.White;
        Assert.AreEqual(expected, foreground.Color,
            "filled text must choose the higher-contrast WCAG sRGB foreground");
    }

    private static void AssertContrastForeground(DependencyObject backgroundElement, TextBlock foregroundElement)
    {
        Brush backgroundBrush = GetBackgroundBrush(backgroundElement);
        Assert.IsInstanceOfType(backgroundBrush, typeof(SolidColorBrush));
        Assert.IsInstanceOfType<SolidColorBrush>(foregroundElement.Foreground);
        var background = (SolidColorBrush)backgroundBrush;
        var foreground = (SolidColorBrush)foregroundElement.Foreground;
        Assert.AreEqual(255, foreground.Color.A);
        Assert.IsTrue(foreground.Color == Colors.Black || foreground.Color == Colors.White);
        double backgroundLuminance = RelativeLuminance(background.Color);
        double blackContrast = (backgroundLuminance + 0.05d) / 0.05d;
        double whiteContrast = 1.05d / (backgroundLuminance + 0.05d);
        Color expected = blackContrast >= whiteContrast ? Colors.Black : Colors.White;
        Assert.AreEqual(expected, foreground.Color);
    }

    private static void SetViewerPaletteBrush(
        PlaylistLampViewerWindow window,
        string resourceKey,
        Color color)
    {
        window.Resources[resourceKey] = new SolidColorBrush(color);
        TestUiDispatcherHost.Drain();
        window.UpdateLayout();
        TestUiDispatcherHost.Drain();
        window.UpdateLayout();
    }

    private static void AssertControlledContrast(
        Border legend,
        Button segmentButton,
        Color expectedBackground,
        Color expectedForeground)
    {
        Assert.IsInstanceOfType(legend.Background, typeof(SolidColorBrush));
        Assert.IsInstanceOfType(segmentButton.Background, typeof(SolidColorBrush));
        var legendBrush = (SolidColorBrush)legend.Background;
        var segmentBrush = (SolidColorBrush)segmentButton.Background;
        Assert.AreEqual(expectedBackground, legendBrush.Color);
        Assert.AreEqual(expectedBackground, segmentBrush.Color);
        foreach (TextBlock label in FindDescendants<TextBlock>(legend))
        {
            AssertContrastForeground(legend, label);
            Assert.IsInstanceOfType(label.Foreground, typeof(SolidColorBrush));
            Assert.AreEqual(expectedForeground, ((SolidColorBrush)label.Foreground).Color);
        }
        AssertContrastForeground(segmentButton, segmentButton);
        Assert.IsInstanceOfType(segmentButton.Foreground, typeof(SolidColorBrush));
        Assert.AreEqual(expectedForeground, ((SolidColorBrush)segmentButton.Foreground).Color);
    }

    private static Brush GetBackgroundBrush(DependencyObject element)
        => element switch
        {
            Border border => border.Background,
            Control control => control.Background,
            _ => null
        };

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            double value = channel / 255d;
            return value <= 0.03928d
                ? value / 12.92d
                : Math.Pow((value + 0.055d) / 1.055d, 2.4d);
        }

        return 0.2126d * Linearize(color.R)
            + 0.7152d * Linearize(color.G)
            + 0.0722d * Linearize(color.B);
    }

    private static void AssertRenderedStatisticOrder(Window window, string[] expectedLabels)
    {
        Border[] cards = FindDescendants<Border>(window)
            .Where(border => border.DataContext is PlaylistLampViewerStatCardViewModel)
            .OrderBy(border => border.TranslatePoint(new Point(0, 0), window).Y)
            .ThenBy(border => border.TranslatePoint(new Point(0, 0), window).X)
            .ToArray();
        Assert.AreEqual(expectedLabels.Length, cards.Length);
        string[] actualLabels = cards
            .Select(card => ((PlaylistLampViewerStatCardViewModel)card.DataContext).Label)
            .ToArray();
        CollectionAssert.AreEqual(expectedLabels, actualLabels);
        double firstY = cards[0].TranslatePoint(new Point(0, 0), window).Y;
        Assert.IsTrue(cards.All(card =>
            Math.Abs(card.TranslatePoint(new Point(0, 0), window).Y - firstY) <= 1d),
            "all statistics cards must be presented in one visual row");
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

    private static TaskCompletionSource<T> NewCompletion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
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
        return CreateRequest(
            source,
            new DateTime(2026, 8, 28, 1, 0, 0, DateTimeKind.Utc));
    }

    private static PlaylistLampAggregationRequest CreateRequest(
        ActiveScoreSource source,
        DateTime? playlistLastUpdated)
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
            playlistLastUpdated);
    }

    private static bool IsPlaylistLastUpdateTimeConversion(MethodBase method)
    {
        if (method.DeclaringType == typeof(DateTimeOffset))
        {
            return true;
        }
        return method.DeclaringType == typeof(DateTime)
            && (method.Name == nameof(DateTime.ToLocalTime)
                || method.Name == nameof(DateTime.ToUniversalTime)
                || method.Name == nameof(DateTime.SpecifyKind));
    }

    private static void AssertPlaylistLastUpdatePresentation(
        DateTime playlistLastUpdated,
        CultureInfo testCulture)
    {
        var source = new FixedLampSource(CreateRequest(
            ActiveScoreSource.Beatoraja,
            playlistLastUpdated));
        var session = new PlaylistLampViewerSession("playlist", source);
        var viewModel = new PlaylistLampViewerViewModel(
            "playlist",
            "Local last-update fixture",
            session,
            TestUiDispatcherHost.Dispatcher,
            _ => { });
        try
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(
                viewModel.StartAndWaitForPresentableAsync(),
                "playlist lamp viewer local last-update first presentable result");

            PlaylistLampViewerStatCardViewModel card = viewModel.StatisticsCards.Single(
                candidate => candidate.Label == Resources.PlaylistLampViewer_playlist_last_update);
            Assert.AreEqual(
                playlistLastUpdated.ToString("g", testCulture),
                card.Value,
                "playlist.last_update is a legacy local wall-clock value and must not be timezone converted");
        }
        finally
        {
            viewModel.Dispose();
        }
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

    private static PlaylistLampAggregationRequest CreateCountWidthRequest(int entryCount, string folderName)
    {
        string smallFolderName = folderName + "-small";
        var score = new PlaylistLampScore(
            "count-width-hash",
            "count-width-sha",
            ClearType.HARD,
            RankType.A,
            50,
            50,
            100,
            1);
        var bySha256 = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
        {
            [score.Sha256] = score
        };
        PlaylistLampEntrySnapshot[] entries = Enumerable.Range(0, entryCount)
            .Select(index => new PlaylistLampEntrySnapshot(
                folderName,
                "count-width-entry-" + index.ToString(CultureInfo.InvariantCulture),
                true,
                md5: score.Hash,
                sha256: score.Sha256,
                resolvedMd5: score.Hash,
                resolvedSha256: score.Sha256))
            .Append(new PlaylistLampEntrySnapshot(
                smallFolderName,
                "count-width-small-entry",
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
            [folderName, smallFolderName],
            entries,
            scoreSnapshot,
            new DateTime(2026, 8, 28, 1, 0, 0, DateTimeKind.Utc));
    }

    private sealed class HistoricalLampSource : IPlaylistLampViewerDataSource
    {
        private readonly object stateGate = new();

        private EventHandler<PlaylistLampViewerSourceChangedEventArgs>? changed;

        private bool historyAvailable;

        private DateTime? expectedSelectedDate;

        private TaskCompletionSource<PlaylistLampViewerQuery>? expectedQuery;

        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed
        {
            add => changed += value;
            remove => changed -= value;
        }

        internal void EnableHistory()
        {
            lock (stateGate)
            {
                historyAvailable = true;
            }
        }

        internal Task ExpectQuery(DateTime? selectedDate)
        {
            TaskCompletionSource<PlaylistLampViewerQuery> completion = NewCompletion<PlaylistLampViewerQuery>();
            lock (stateGate)
            {
                if (expectedQuery != null)
                {
                    throw new InvalidOperationException("Only one historical query expectation may be pending.");
                }
                expectedSelectedDate = selectedDate;
                expectedQuery = completion;
            }
            return completion.Task;
        }

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            PlaylistLampViewerQuery query,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            cancellationToken.ThrowIfCancellationRequested();
            bool hasHistory;
            TaskCompletionSource<PlaylistLampViewerQuery> completion = null;
            lock (stateGate)
            {
                hasHistory = historyAvailable;
                if (expectedQuery != null
                    && Nullable.Equals(expectedSelectedDate, query.SelectedLocalDate))
                {
                    completion = expectedQuery;
                    expectedQuery = null;
                    expectedSelectedDate = null;
                }
            }
            completion?.TrySetResult(query);
            return ValueTask.FromResult(CreateRequest(query, hasHistory));
        }

        private static PlaylistLampAggregationRequest CreateRequest(
            PlaylistLampViewerQuery query,
            bool hasHistory)
        {
            DateTime today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
            string sha256 = "historical-date-sha256";
            var score = PlaylistLampScore.FromExScore(
                null,
                sha256,
                ClearType.CLEAR,
                100,
                100);
            var scoreSnapshot = new PlaylistLampScoreSnapshot(
                ActiveScoreSource.Beatoraja,
                ScoreTableLoadStatus.Loaded,
                1,
                1,
                null,
                scoresBySha256: new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
                {
                    [sha256] = score
                });
            var entry = new PlaylistLampEntrySnapshot(
                "folder",
                "historical-date-entry",
                true,
                sha256: sha256,
                resolvedSha256: sha256);
            return new PlaylistLampAggregationRequest(
                query.PlaylistId,
                ["folder"],
                [entry],
                scoreSnapshot,
                query: query,
                historicalDateRange: hasHistory
                    ? new PlaylistLampHistoricalDateRange(today.AddDays(-2), today)
                    : new PlaylistLampHistoricalDateRange(null, today),
                historicalStatus: hasHistory
                    ? (query.SelectedLocalDate.HasValue
                        ? PlaylistLampHistoricalSnapshotStatus.Available
                        : PlaylistLampHistoricalSnapshotStatus.Latest)
                    : PlaylistLampHistoricalSnapshotStatus.NoHistory);
        }
    }

    private sealed class FixedLampSource : IPlaylistLampViewerDataSource
    {
        private PlaylistLampAggregationRequest request;

        private EventHandler<PlaylistLampViewerSourceChangedEventArgs>? changed;

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
            PlaylistLampViewerQuery query,
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
