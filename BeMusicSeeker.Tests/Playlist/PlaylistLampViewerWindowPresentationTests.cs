using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Localization;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>
/// ランプ集計から実 WPF 表示・Automation・日付と表示モード操作への接続を確認します。
/// 集計値の条件網羅は集計・履歴スナップショットの下位ケースが担当します。
/// </summary>
/// <remarks>実測幅、仮想化、動的ブラシ、compiled binding は実 Window が必要です。共有 Application の辞書を補うため非並列にします。</remarks>
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
                    "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml",
                    StringComparison.OrdinalIgnoreCase)))
            {
                application.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri(
                        "/BeMusicSeeker;component/Themes/CanonicalDialogStyles.xaml",
                        UriKind.RelativeOrAbsolute)
                });
            }
        });
    }

    /// <summary>未選択／選択済みの同じ日付部品と開いたCalendarがアプリ言語とテーマへ追従し、月日再解析・日付通知・取得・表示モード変更を起こさず、日付文字/DateHintの余白を一回だけ適用することを確認します。</summary>
    /// <remarks>実PART_TextBox、Popup、Calendarの標準セルとボタン配送を観測するためWPFを使います。UIスレッドの文化圏と共有テーマを所有・復元し、実OSフォーカスは合否にしません。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Viewer_datePresentationTracksApplicationCultureWithoutChangingTheQuery(bool serviceCreatedFirst)
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo previousResourceCulture = Resources.Culture;
            ResourceDictionary[] previousDictionaries = Application.Current.Resources.MergedDictionaries.ToArray();
            PlaylistLampViewerViewModel? viewModel = null;
            Window? owner = null;
            PlaylistLampViewerWindow? window = null;
            var languageBackups = new Dictionary<string, byte[]?>();
            try
            {
                // 追加JSONも本番の探索・言語選択へ通す。書込みはテスト出力配下だけに限定する。
                string languageDirectory = ApplicationPathPolicy.Current.LanguageDirectory;
                Assert.AreEqual(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lang")), Path.GetFullPath(languageDirectory), true);
                Directory.CreateDirectory(languageDirectory);
                foreach (string language in new[] { "th-TH", "ar-SA" })
                {
                    string path = Path.Combine(languageDirectory, language + ".json");
                    languageBackups.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
                    File.WriteAllText(path, "{\"_language_name\":\"Date regression " + language + "\"}");
                    JsonLanguageCatalog.Invalidate(language);
                }
                JsonLanguageCatalog.InvalidateLanguages();
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
                ResourceService? service = serviceCreatedFirst ? new ResourceService() : null;
                // 起動時にResourceServiceより後からResources.Cultureを設定する経路もキャッシュせず読む。
                Resources.Culture = CultureInfo.GetCultureInfo("fr-FR");
                service ??= new ResourceService();
                Assert.AreEqual("fr-FR", service.DisplayCulture.Name);
                Resources.Culture = null;
                Assert.AreEqual(CultureInfo.CurrentUICulture, service.DisplayCulture);
                ResourceService.Current.ChangeCulture("en-US");
                AppThemeService.ApplyTheme(AppThemeService.Light);
                var selected = new DateTime(2026, 4, 5);
                PlaylistLampAggregationRequest request = CreateRequest(ActiveScoreSource.Beatoraja);
                var source = new FixedLampSource(new PlaylistLampAggregationRequest(request.PlaylistId,
                    request.FolderOrder, request.Entries, request.ScoreSnapshot,
                    historicalDateRange: new PlaylistLampHistoricalDateRange(new DateTime(2020, 1, 1), new DateTime(2030, 12, 31)),
                    historicalStatus: PlaylistLampHistoricalSnapshotStatus.Available));
                var session = new PlaylistLampViewerSession("playlist", source);
                viewModel = new PlaylistLampViewerViewModel("playlist", "Date presentation", session,
                    TestUiDispatcherHost.Dispatcher, _ => { });
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.StartAndWaitForPresentableAsync(), "date presentation initial result");
                owner = new Window { Content = new Grid(), Width = 480, Height = 320 };
                scope.ShowAndWaitForContentRendered(owner);
                window = new PlaylistLampViewerWindow(owner, viewModel);
                scope.ShowAndWaitForContentRendered(window);
                SelectDisplayMode(window, true);
                DatePicker picker = FindByAutomationId<DatePicker>(window, "PlaylistLampViewerAsOfDatePicker");
                var editor = (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
                var hint = (TextBlock)editor.Template.FindName("DateHint", editor);
                var button = (Button)picker.Template.FindName("PART_Button", picker);
                var popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                scope.TrackPopup(popup);
                var calendar = (System.Windows.Controls.Calendar)popup.Child;
                Assert.IsTrue(editor.IsReadOnly);
                void OpenCalendar()
                {
                    var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    int openCount = 0;
                    RoutedEventHandler onOpened = (_, _) => { openCount++; opened.TrySetResult(); };
                    picker.CalendarOpened += onOpened;
                    try
                    {
                        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(opened.Task, "localized calendar button actual open");
                    }
                    finally { picker.CalendarOpened -= onOpened; }
                    Assert.AreEqual(1, openCount);
                }
                var hintCharacterPositions = new Dictionary<string, Point>();
                foreach (DateTime? date in new DateTime?[] { null, selected })
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(session.UpdateSelectedAsOfDateAsync(date), "date presentation query selection");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    picker.DisplayDate = selected;
                    picker.IsDropDownOpen = true;
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    calendar.DisplayMode = CalendarMode.Month;
                    window.UpdateLayout();
                    calendar.UpdateLayout();
                    var item = (CalendarItem)calendar.Template.FindName("PART_CalendarItem", calendar);
                    var header = (Button)item.Template.FindName("PART_HeaderButton", item);
                    var monthView = (Grid)item.Template.FindName("PART_MonthView", item);
                    int captures = source.CaptureCount;
                    int dateChanges = 0;
                    EventHandler<SelectionChangedEventArgs> onDateChanged = (_, _) => dateChanges++;
                    picker.SelectedDateChanged += onDateChanged;
                    try
                    {
                        int themeIndex = 0;
                        // en/frの月日交換、週開始が同じ言語間、非Gregorianのth-THと環境の既定暦に従うar-SAを確認する。
                        foreach (string language in new[] { "en-US", "fr-FR", "en-US", "ja-JP", "ko-KR", "zh-CN", "zh-TW", "th-TH", "ar-SA", "fr-FR" })
                        {
                            ResourceService.Current.ChangeCulture(language);
                            AppThemeService.ApplyTheme(themeIndex++ % 2 == 0 ? AppThemeService.Light : AppThemeService.Dark);
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            var culture = CultureInfo.GetCultureInfo(language);
                            // 書式の期待値は補助処理を複写せず、標準DatePickerの確定済み日付表示から得る。
                            var standardPicker = new DatePicker { Language = XmlLanguage.GetLanguage(language), SelectedDateFormat = DatePickerFormat.Short, SelectedDate = date };
                            Assert.AreEqual(standardPicker.Text, editor.Text);
                            var displayCulture = (CultureInfo)culture.Clone();
                            if (language == "th-TH")
                            {
                                Assert.IsFalse(culture.Calendar is GregorianCalendar, "th-THで非Gregorianの既定暦を検証する条件を保つ。");
                            }
                            if (culture.Calendar is not GregorianCalendar)
                            {
                                displayCulture.DateTimeFormat.Calendar = new GregorianCalendar(GregorianCalendarTypes.Localized);
                            }
                            Assert.AreEqual(editor.Text, picker.Text);
                            Assert.AreEqual(Resources.DatePicker_choose_date, hint.Text);
                            Assert.AreEqual(date.HasValue ? Visibility.Collapsed : Visibility.Visible, hint.Visibility);
                            var textHost = (ScrollViewer)editor.Template.FindName("PART_ContentHost", editor);
                            Assert.AreEqual(editor.Padding, textHost.Padding, "日付入力の余白はWPF転送だけで所有する。");
                            if (date.HasValue)
                            {
                                Rect character = editor.GetRectFromCharacterIndex(0);
                                Point hintPosition = hintCharacterPositions[language];
                                Assert.AreEqual(hintPosition.X, character.Left, 0.25d,
                                    "DateHintと選択済日付の実文字位置を同じ入力座標で合わせる。");
                                Assert.AreEqual(hintPosition.Y, character.Top, 0.25d);
                            }
                            else
                            {
                                Rect character = hint.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                                hintCharacterPositions[language] = hint.TranslatePoint(character.TopLeft, editor);
                            }
                            Assert.AreEqual(Resources.DatePicker_open_calendar, button.ToolTip);
                            Assert.AreEqual(Resources.DatePicker_open_calendar, AutomationProperties.GetName(button));
                            Assert.AreEqual(standardPicker.FirstDayOfWeek, calendar.FirstDayOfWeek);
                            Assert.AreEqual(selected.ToString("Y", displayCulture), header.Content);
                            string[] expectedDays = Enumerable.Range(0, 7).Select(index =>
                                culture.DateTimeFormat.ShortestDayNames[((int)culture.DateTimeFormat.FirstDayOfWeek + index) % 7]).ToArray();
                            string[] actualDays = monthView.Children.OfType<TextBlock>().Take(7).Select(text => text.Text).ToArray();
                            CollectionAssert.AreEqual(expectedDays, actualDays);
                            Assert.AreSame(calendar, popup.Child);
                            Assert.AreSame(item, calendar.Template.FindName("PART_CalendarItem", calendar));
                            Assert.IsTrue(popup.IsOpen);
                            Assert.AreEqual(CalendarMode.Month, calendar.DisplayMode);
                            Assert.AreEqual(selected, picker.DisplayDate);
                            Assert.AreEqual(date, picker.SelectedDate);
                            Assert.AreEqual(date, calendar.SelectedDate);
                            Assert.AreEqual(date, viewModel.SelectedAsOfDate);
                            Assert.AreEqual(date, session.Query.SelectedLocalDate);
                            Assert.IsTrue(viewModel.IsCountMode);
                            Assert.AreEqual(0, dateChanges);
                            Assert.AreEqual(captures, source.CaptureCount);
                            Assert.AreEqual("ja-JP", CultureInfo.CurrentCulture.Name);
                            Assert.AreEqual("ja-JP", CultureInfo.CurrentUICulture.Name);
                            Assert.AreEqual(((SolidColorBrush)picker.FindResource("App.ControlBackgroundBrush")).Color, ((SolidColorBrush)picker.Background).Color);
                            Assert.AreEqual(((SolidColorBrush)picker.FindResource("App.PopupBackgroundBrush")).Color, ((SolidColorBrush)item.Background).Color);
                            Assert.AreEqual(((SolidColorBrush)picker.FindResource("App.BorderBrush")).Color, ((SolidColorBrush)item.BorderBrush).Color);
                            CalendarDayButton day = FindDescendants<CalendarDayButton>(calendar).First(candidate => !candidate.IsInactive && candidate.IsEnabled);
                            Assert.AreEqual(((SolidColorBrush)picker.FindResource(day.IsSelected ? "App.AccentForegroundBrush" : "App.TextBrush")).Color, ((SolidColorBrush)day.Foreground).Color);
                            // DatePickerが再解析する標準LostFocusとボタンの開く経路でも日付・取得を維持する。
                            editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, editor));
                            picker.IsDropDownOpen = false;
                            OpenCalendar();
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            Assert.AreEqual(date, picker.SelectedDate);
                            Assert.AreEqual(date, viewModel.SelectedAsOfDate);
                            Assert.AreEqual(date, session.Query.SelectedLocalDate);
                            Assert.AreEqual(0, dateChanges);
                            Assert.AreEqual(captures, source.CaptureCount);
                            Assert.IsTrue(editor.IsReadOnly);
                        }
                        calendar.DisplayMode = CalendarMode.Year;
                        picker.IsDropDownOpen = false;
                        DatePickerTextBox previousEditor = editor;
                        ControlTemplate originalTemplate = picker.Template;
                        Assert.IsTrue(picker.IsLoaded);
                        picker.Template = null;
                        picker.ApplyTemplate();
                        picker.Template = originalTemplate;
                        picker.ApplyTemplate();
                        window.UpdateLayout();
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.IsTrue(picker.IsLoaded, "親を再LoadedせずにPARTを再生成する条件を保つ。");
                        editor = (DatePickerTextBox)picker.Template.FindName("PART_TextBox", picker);
                        Assert.AreNotSame(previousEditor, editor);
                        Assert.IsTrue(editor.IsLoaded);
                        hint = (TextBlock)editor.Template.FindName("DateHint", editor);
                        button = (Button)picker.Template.FindName("PART_Button", picker);
                        popup = (Popup)picker.Template.FindName("PART_Popup", picker);
                        scope.TrackPopup(popup);
                        Assert.AreSame(calendar, popup.Child);
                        picker.IsEnabled = false;
                        picker.IsEnabled = true;
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        var recreatedStandard = new DatePicker { Language = picker.Language, SelectedDateFormat = DatePickerFormat.Short, SelectedDate = date };
                        Assert.AreEqual(recreatedStandard.Text, editor.Text);
                        Assert.AreEqual(editor.Text, picker.Text);
                        Assert.IsTrue(editor.IsReadOnly);
                        Assert.AreEqual(selected, picker.DisplayDate);
                        Assert.AreEqual(CalendarMode.Year, calendar.DisplayMode);
                        Assert.AreEqual(date, picker.SelectedDate);
                        Assert.AreEqual(date, viewModel.SelectedAsOfDate);
                        Assert.AreEqual(date, session.Query.SelectedLocalDate);
                        if (date.HasValue) { Assert.IsTrue(session.Query.SelectedLocalDate is { Kind: DateTimeKind.Unspecified }); }
                        Assert.IsTrue(viewModel.IsCountMode);
                        Assert.AreEqual(0, dateChanges);
                        Assert.AreEqual(captures, source.CaptureCount);
                        editor.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent, editor));
                        ResourceService.Current.ChangeCulture("en-US");
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreEqual(CalendarMode.Year, calendar.DisplayMode);
                        Assert.AreEqual(selected, calendar.DisplayDate);
                        Assert.AreEqual(0, dateChanges);
                        picker.IsDropDownOpen = false;
                        OpenCalendar();
                        Assert.AreEqual(date, picker.SelectedDate);
                        Assert.AreEqual(0, dateChanges);
                        Assert.AreEqual(captures, source.CaptureCount);
                        if (date.HasValue)
                        {
                            // 多数の言語更新後も実Calendarのセルから一回だけ選択・取得する。
                            DateTime next = selected.AddDays(1);
                            var dayAccepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                            EventHandler<PlaylistLampAggregationResultChangedEventArgs> onDayAccepted = (_, args) =>
                            {
                                if (args.Result.Query.SelectedLocalDate == next) { dayAccepted.TrySetResult(); }
                            };
                            session.ResultChanged += onDayAccepted;
                            try
                            {
                                calendar.UpdateLayout();
                                CalendarDayButton nextDay = FindDescendants<CalendarDayButton>(calendar)
                                    .Single(day => day.DataContext is DateTime value && value == next);
                                nextDay.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, nextDay));
                                TestUiDispatcherHost.AwaitTaskOnDispatcher(dayAccepted.Task, "localized calendar day actual result");
                                TestUiDispatcherHost.ProcessQueuedPresentation();
                                Assert.AreEqual(next, picker.SelectedDate);
                                Assert.AreEqual(1, dateChanges);
                                Assert.AreEqual(captures + 1, source.CaptureCount);
                            }
                            finally { session.ResultChanged -= onDayAccepted; }
                        }
                    }
                    finally
                    {
                        picker.SelectedDateChanged -= onDateChanged;
                        picker.IsDropDownOpen = false;
                    }
                }
            }
            finally
            {
                window?.Close();
                owner?.Close();
                if (viewModel != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "date presentation drain"); }
                foreach ((string path, byte[]? contents) in languageBackups)
                {
                    if (contents == null) { File.Delete(path); }
                    else { File.WriteAllBytes(path, contents); }
                    JsonLanguageCatalog.Invalidate(Path.GetFileNameWithoutExtension(path));
                }
                JsonLanguageCatalog.InvalidateLanguages();
                ResourceService.Current.ChangeCulture("ja-JP");
                Resources.Culture = previousResourceCulture;
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                Application.Current.Resources.MergedDictionaries.Clear();
                foreach (ResourceDictionary dictionary in previousDictionaries) { Application.Current.Resources.MergedDictionaries.Add(dictionary); }
            }
        });
    }

    /// <summary>製品配置を未表示の実HWNDへ適用し、最新Owner、状態別の中央基準、実DPIとWPF外形寸法の接続を確認します。任意の異DPIモニター間移動は保証しません。</summary>
    [TestMethod]
    public void Viewer_initialPlacementUsesCurrentOwnerAndSynchronizesHiddenNativeWindow()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            var session = new PlaylistLampViewerSession("playlist", new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja)));
            var viewModel = new PlaylistLampViewerViewModel("playlist", "配置", session, TestUiDispatcherHost.Dispatcher, _ => { });
            var owner = new Window { Width = 600, Height = 400, Left = 100, Top = 100 };
            nint ownerHandle = new WindowInteropHelper(owner).EnsureHandle();
            var window = new PlaylistLampViewerWindow(owner, viewModel);
            try
            {
                // scopeによるタスクバー非表示・画面外配置の上書きより前に製品設定を確認します。
                Assert.IsTrue(window.ShowInTaskbar);
                Assert.AreEqual(WindowStartupLocation.Manual, window.WindowStartupLocation);
                Assert.IsTrue(GetWindowRectForPlacement(ownerHandle, out PlacementNativeRect originalOwner));
                Assert.IsTrue(SetWindowPosForPlacement(ownerHandle, 0, originalOwner.Left + 137, originalOwner.Top + 71,
                    originalOwner.Right - originalOwner.Left, originalOwner.Bottom - originalOwner.Top, 0x214));
                foreach (WindowState state in new[] { WindowState.Normal, WindowState.Maximized, WindowState.Minimized })
                {
                    owner.WindowState = state;
                    window.PrepareInitialPlacement();
                    nint handle = new WindowInteropHelper(window).Handle;
                    Assert.AreNotEqual(nint.Zero, handle);
                    Assert.IsFalse(window.IsVisible);
                    Assert.IsFalse(IsWindowVisibleForPlacement(handle));
                    Assert.IsFalse(window.IsLoaded);
                    Assert.IsTrue(GetWindowRectForPlacement(handle, out PlacementNativeRect native));
                    Assert.IsTrue(GetWindowRectForPlacement(ownerHandle, out PlacementNativeRect ownerRect));
                    var info = new PlacementMonitorInfo { Size = Marshal.SizeOf<PlacementMonitorInfo>() };
                    Assert.IsTrue(GetMonitorInfoForPlacement(MonitorFromWindowForPlacement(ownerHandle, 2), ref info));
                    double scale = GetDpiForWindowForPlacement(handle) / 96d;
                    Assert.IsTrue(scale > 0);
                    Rect expected = WindowPlacementCalculator.Calculate(info.Work.Rect,
                        state == WindowState.Normal ? ownerRect.Rect : info.Work.Rect, new Size(1290, 820), scale);
                    Assert.AreEqual(expected, native.Rect);
                    Assert.AreEqual(native.Rect.Width / scale, window.Width, 1d / scale);
                    Assert.AreEqual(native.Rect.Height / scale, window.Height, 1d / scale);
                    HwndSource hwndSource = HwndSource.FromHwnd(handle) ?? throw new AssertFailedException("HwndSourceがありません。");
                    Assert.IsNull(hwndSource.RootVisual);
                    Assert.AreEqual(scale, hwndSource.CompositionTarget.TransformToDevice.M11, 0.0001);
                }
            }
            finally
            {
                window.Close();
                owner.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "未表示配置のセッション回収");
            }
        });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementNativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rect Rect => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PlacementMonitorInfo
    {
        internal int Size;
        internal PlacementNativeRect Monitor, Work;
        internal uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRectForPlacement(nint handle, out PlacementNativeRect rect);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPosForPlacement(nint handle, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisibleForPlacement(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
    private static extern uint GetDpiForWindowForPlacement(nint handle);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")]
    private static extern nint MonitorFromWindowForPlacement(nint handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoForPlacement(nint monitor, ref PlacementMonitorInfo info);

    /// <summary>丸めでゼロになるほど小さい正割合を入力し、ゼロ表示や最小値への切上げで実割合を失う誤りを検出します。</summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("en-US")]
    [DataRow("de-DE")]
    [DataRow("fr-FR")]
    [DataRow("th-TH")]
    public void PercentageFormatter_preservesTinyPositiveBoundarySemantics(string cultureName)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);
            const int positiveCount = 1;
            const int denominator = 25001;
            double percentage = positiveCount * 100d / denominator;
            string formatted = PlaylistLampViewerPercentageFormatter.Format(percentage);
            string formattedZero = PlaylistLampViewerPercentageFormatter.Format(0d);
            string formattedMinimum = PlaylistLampViewerPercentageFormatter.Format(0.01d);
            Assert.AreEqual("0.00%", formattedZero);
            Assert.AreEqual("0.01%", formattedMinimum);

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
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    /// <summary>壁時計の最終更新を異文化でも固定Gregorianの分粒度で表示し、暦変更や時刻変換を検出します。</summary>
    [TestMethod]
    public void Viewer_playlistLastUpdateDisplaysLegacyLocalWallClockWithoutTimezoneConversion()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            try
            {
                DateTime playlistLastUpdated = new(2026, 8, 29, 10, 49, 7, DateTimeKind.Unspecified);
                CultureInfo[] testCultures =
                [
                    CultureInfo.GetCultureInfo("en-US"),
                    CultureInfo.GetCultureInfo("de-DE"),
                    CultureInfo.GetCultureInfo("th-TH")
                ];
                Assert.AreNotEqual(
                    playlistLastUpdated.ToString("g", testCultures[0]),
                    playlistLastUpdated.ToString("g", testCultures[1]),
                    "the selected cultures must distinguish current-culture formatting from a fixed format");

                foreach (CultureInfo testCulture in testCultures)
                {
                    CultureInfo.CurrentCulture = testCulture;
                    CultureInfo.CurrentUICulture = testCulture;
                    AssertPlaylistLastUpdatePresentation(playlistLastUpdated);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
            }
        });
    }

    /// <summary>日時の補助項目生成と整形の compiled 呼出しに時刻変換がないことを確認し、UTC ホストで見えない契約違反を検出します。</summary>
    [TestMethod]
    public void Viewer_playlistLastUpdatePresentationDoesNotConvertDateTime()
    {
        MethodInfo refreshStatistics = typeof(PlaylistLampViewerViewModel).GetMethod(
            "RefreshStatistics",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new AssertFailedException("The playlist statistics builder is missing.");
        MethodInfo formatter = typeof(PlaylistLampViewerViewModel).GetMethod(
            "FormatTimestamp",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new AssertFailedException("The playlist last-update formatter is missing.");

        // UTC のホストでは時刻変換の誤追加が表示値に現れないため、補助項目生成と整形の compiled 呼出しも確認します。
        foreach (MethodInfo method in new[] { refreshStatistics, formatter })
        {
            MethodBase[] calledMethods = StartupLibraryConstructionTestSupport
                .EnumerateCalledMethods(method)
                .ToArray();
            Assert.IsFalse(
                calledMethods.Any(IsPlaylistLastUpdateTimeConversion),
                $"{method.Name} must not call timezone conversion APIs for playlist last-update values");
        }
    }

    /// <summary>譜面表日時が欠損しスコア日時だけある場合に利用不可を表示し、別の日時で穴埋めする誤りを検出します。</summary>
    [TestMethod]
    public void Viewer_playlistLastUpdateDisplaysUnavailableWhenRequestTimestampIsNull()
    {
        TestUiDispatcherHost.Invoke(() =>
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
                PlaylistLampViewerStatistic card = viewModel.SupplementaryStatistics.Single(
                    candidate => candidate.Label == Resources.PlaylistLampViewer_playlist_last_update);
                Assert.AreEqual(Resources.PlaylistLampViewer_unavailable, card.Value);
            }
            finally
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>履歴なしから履歴ありへ再有効化しても読取り専用を保ち、日付選択と最新復帰の範囲・選択要求・表示モードを失う接続不備を検出します。</summary>
    [TestMethod]
    public void Viewer_historicalDatePicker_is_calendar_only_and_latest_recovers_the_window()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
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
            EventHandler<PlaylistLampAggregationResultChangedEventArgs>? resultHandler = null;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    viewModel.StartAndWaitForPresentableAsync(),
                    "playlist lamp historical date first presentable result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                SelectDisplayMode(window, true);
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
                    session.UpdateSelectedAsOfDateAsync(earliest),
                    "playlist lamp historical date range capture");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                Assert.IsTrue(picker.IsEnabled, "a non-empty provider history must enable the calendar");
                Assert.IsTrue(pickerTextBox.IsReadOnly, "履歴なしから再有効化されてもカレンダー専用入力を維持する。");
                Assert.AreEqual(earliest, picker.DisplayDateStart);
                Assert.AreEqual(today, picker.DisplayDateEnd);

                TaskCompletionSource<PlaylistLampAggregationResult> selectedAccepted = NewCompletion<PlaylistLampAggregationResult>();
                TaskCompletionSource<PlaylistLampAggregationResult> latestAccepted = NewCompletion<PlaylistLampAggregationResult>();
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                Assert.IsTrue(viewModel.IsCountMode);
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                Assert.IsTrue(viewModel.IsCountMode);
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>正の区間と空フォルダを描画し、凡例・棒の配送、幅の非重複、動的背景に対する読める文字色、Automation の一回配送と選択反映を確認します。</summary>
    [TestMethod]
    public void Viewer_rendersD2HalvesLegendsBoundedBarsAndAtomicInvokeState()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var requests = new List<PlaylistLampViewerNavigationRequest>();
            var navigationApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Presentation fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                request =>
                {
                    requests.Add(request);
                    navigationApplied.TrySetResult();
                });
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                Assert.AreSame(owner, window.Owner);

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
                }
                AssertFolderBarsAlign(renderedRows);

                Button[] segmentButtons = FindDescendants<Button>(window)
                    .Where(button => button.DataContext is PlaylistLampViewerSegmentViewModel)
                    .ToArray();
                Assert.IsTrue(segmentButtons.Length > 0, "A positive folder segment must be a rendered button.");
                foreach (Button button in segmentButtons)
                {
                    var segment = (PlaylistLampViewerSegmentViewModel)button.DataContext;
                    Assert.IsTrue(segment.Count > 0 && segment.HasPositiveWidth);
                    Assert.IsTrue(button.ActualWidth > 0d,
                        $"positive segment button for {segment.CategoryKey} must have arranged width");
                    ContentPresenter? container = FindAncestor<ContentPresenter>(button);
                    Assert.IsNotNull(container, "weighted segment must be hosted by an ItemsControl ContentPresenter");
                    Assert.IsTrue(container!.ActualWidth > 0d,
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
                    .Select(brush => brush!.Color)
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
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(navigationApplied.Task, "lamp-folder-navigation-applied");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

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
                    TestUiDispatcherHost.ProcessQueuedPresentation();
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>全体 clear と rank を Automation で順に実行し、フォルダがない区間を拒否する誤りや選択更新・全体要求配送の欠落を検出します。</summary>
    [TestMethod]
    public void Viewer_globalPositiveSegmentsAreInvokableAndRemainSelectedAfterAutomationInvoke()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var requests = new List<PlaylistLampViewerNavigationRequest>();
            var clearNavigationApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var rankNavigationApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja));
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewModel = new PlaylistLampViewerViewModel(
                "playlist",
                "Global invocation fixture",
                session,
                TestUiDispatcherHost.Dispatcher,
                request =>
                {
                    requests.Add(request);
                    if (request.Kind == PlaylistLampSegmentKind.Clear)
                    {
                        clearNavigationApplied.TrySetResult();
                    }
                    else
                    {
                        rankNavigationApplied.TrySetResult();
                    }
                });
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                Border clearHost = FindByAutomationId<Border>(window, "PlaylistLampViewerClearGraphHost");
                Border rankHost = FindByAutomationId<Border>(window, "PlaylistLampViewerRankGraphHost");
                Button clearButton = FindDescendants<Button>(clearHost)
                    .Single(button => button.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.CategoryKey == "MAX");
                Button rankButton = FindDescendants<Button>(rankHost)
                    .Single(button => button.DataContext is PlaylistLampViewerSegmentViewModel segment
                        && segment.CategoryKey == "AAA");

                // 全体セグメントも、フォルダの有無で無効化せず Automation の Invoke を配送します。
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(clearNavigationApplied.Task, "lamp-global-clear-navigation-applied");
                TestUiDispatcherHost.ProcessQueuedPresentation();
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(rankNavigationApplied.Task, "lamp-global-rank-navigation-applied");
                TestUiDispatcherHost.ProcessQueuedPresentation();
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>幅の広い区間と狭い区間で割合・件数を切り替え、文字のはみ出しと全文 tooltip・Automation 情報の欠落を検出します。</summary>
    [TestMethod]
    public void Viewer_widthAwareLabelsShowSelectedModeOnlyWhenTheSegmentCanContainIt()
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
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
                foreach (bool countMode in new[] { false, true, false })
                {
                    SelectDisplayMode(window, countMode);
                    AssertWidthAwareLabels(clearButtons, window, countMode);
                    AssertWidthAwareLabels(rankButtons, window, countMode);
                    AssertBoundedHost(clearHost);
                    AssertBoundedHost(rankHost);
                }

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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>名前を短いものから長いものへ更新し、両側の共有幅を再評価しない誤りと省略した名前の全文 tooltip 欠落を検出します。</summary>
    [TestMethod]
    public void Viewer_folderLabelColumnRefreshesSharedWidthAndPreservesFullLongName()
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
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
                Assert.IsTrue(initialMediumLabels.All(label =>
                    Math.Abs(label.ActualWidth - window.FolderLabelColumnWidth) <= 1d));

                foreach (PlaylistLampViewerFolderRowViewModel row in viewModel.FolderRows)
                {
                    Assert.AreEqual(
                        row.Count.ToString("0", CultureInfo.InvariantCulture),
                        row.CountText,
                        "folder counts use the localized numeric format without a unit");
                }

                source.Replace(CreateFolderWidthRequest(shortName, longName));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    session.UpdateSelectedAsOfDateAsync(session.Query.SelectedLocalDate.HasValue ? null : DateTime.Today.AddDays(-1)),
                    "playlist lamp viewer folder width explicit date capture");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                TextBlock[] longLabels = FindDescendants<TextBlock>(window)
                    .Where(textBlock => textBlock.DataContext is PlaylistLampViewerFolderRowViewModel
                        && textBlock.Text == longName)
                    .ToArray();
                Assert.AreEqual(2, longLabels.Length);
                Assert.IsTrue(window.FolderLabelColumnWidth > mediumTextWidth);
                Assert.IsTrue(window.FolderLabelColumnWidth < MeasureRenderedText(longLabels[0], longName));
                Assert.IsTrue(longLabels.All(label =>
                    Math.Abs(label.ActualWidth - window.FolderLabelColumnWidth) <= 1d));
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>件数を桁の増える値へ更新し、桁区切りなしの固定件数表示と全行・両側の実配置列の共通幅更新、ラベル・棒との重なりを確認します。</summary>
    [TestMethod]
    public void Viewer_folderCountUsesUngroupedDigitsAndRefreshesSharedCountColumn()
    {
        // 桁区切りなしの4桁は既に9999の幅と同じになるため、上限未満の3桁から更新する。
        const int initialCount = 123;
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
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                PlaylistLampViewerFolderRowViewModel initialRow = viewModel.FolderRows
                    .Single(row => row.FolderName == "initial");
                Assert.AreEqual(
                    "123",
                    initialRow.CountText);
                TextBlock initialCountBlock = FindFolderCountBlock(window, initialRow);
                double expectedInitialWidth = MeasureRenderedText(initialCountBlock, initialRow.CountText);
                Assert.AreEqual(expectedInitialWidth, initialCountBlock.ActualWidth, 1d);
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
                    session.UpdateSelectedAsOfDateAsync(session.Query.SelectedLocalDate.HasValue ? null : DateTime.Today.AddDays(-1)),
                    "playlist lamp viewer count width explicit date capture");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();

                PlaylistLampViewerFolderRowViewModel cappedRow = viewModel.FolderRows
                    .Single(row => row.FolderName == "capped");
                Assert.AreEqual(
                    "12345",
                    cappedRow.CountText);
                Assert.IsTrue(int.TryParse(
                    cappedRow.CountText,
                    NumberStyles.Number,
                    CultureInfo.CurrentCulture,
                    out int parsedCappedCount));
                Assert.AreEqual(cappedCount, parsedCappedCount);
                // 列の上限を超える件数でも、文字の描画幅ではなく全行が共有する配置列を比較します。
                double refreshedWidth = FindFolderCountColumnWidths(window, viewModel).First();
                Assert.IsTrue(refreshedWidth > expectedInitialWidth);
                Assert.AreEqual(MeasureRenderedText(initialCountBlock, "9999"), refreshedWidth, 1d);
                Assert.IsTrue(FindFolderCountColumnWidths(window, viewModel)
                    .All(width => Math.Abs(width - refreshedWidth) <= 1d),
                    "更新後も両側と全フォルダの件数列を同じ幅に揃えます。");

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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>統計の内容幅・群単位の折返しと、同じ凡例・全体／フォルダ区間の言語追従を確認し、再取得・再集計・投影の再作成や選択の喪失、破棄後の通知を検出します。</summary>
    /// <remarks>compiled Bindingと実Automationを観測するため同じWindowを使用します。追加JSON、文化圏、共有テーマと購読を非並列scope内で所有・復元し、日付・集計計算・割合の一般境界は既存ケースへ分担します。</remarks>
    [TestMethod]
    public void Viewer_statisticsUseContentWidthsAndWrapWholeGroupsWhileTrackingLanguageAndTheme()
    {
        TestUiDispatcherHost.RunWindowTest(scope =>
        {
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
            CultureInfo previousResourceCulture = Resources.Culture;
            ResourceDictionary[] previousDictionaries = Application.Current.Resources.MergedDictionaries.ToArray();
            PlaylistLampViewerViewModel? viewModel = null;
            PlaylistLampViewerWindow? window = null;
            Window? owner = null;
            PlaylistLampViewerSession? session = null;
            var languageBackups = new Dictionary<string, byte[]?>();
            var collections = new List<INotifyCollectionChanged>();
            var observedSegments = new Dictionary<PlaylistLampViewerSegmentViewModel, HashSet<string>>();
            int collectionChanges = 0;
            int resultChanges = 0;
            int parentChanges = 0;
            NotifyCollectionChangedEventHandler collectionHandler = (_, _) => collectionChanges++;
            EventHandler<PlaylistLampAggregationResultChangedEventArgs> resultHandler = (_, _) => resultChanges++;
            PropertyChangedEventHandler parentHandler = (_, _) => parentChanges++;
            PropertyChangedEventHandler segmentHandler = (sender, args) =>
            {
                if (sender is PlaylistLampViewerSegmentViewModel segment && args.PropertyName is { } property)
                {
                    observedSegments[segment].Add(property);
                }
            };
            try
            {
                // 同梱言語ではカテゴリ名が共通なので、異なる有効な追加翻訳でLabelの固定を識別する。
                string languageDirectory = ApplicationPathPolicy.Current.LanguageDirectory;
                Assert.AreEqual(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "lang")), Path.GetFullPath(languageDirectory), true);
                Directory.CreateDirectory(languageDirectory);
                string languagePath = Path.Combine(languageDirectory, "de-DE.json");
                languageBackups.Add(languagePath, File.Exists(languagePath) ? File.ReadAllBytes(languagePath) : null);
                File.WriteAllText(languagePath, """
                    {"_language_name":"Lamp regression", "PlaylistLampViewer_clear_max":"Maximum regression", "PlaylistLampViewer_rank_aaa":"Triple A regression"}
                    """);
                JsonLanguageCatalog.Invalidate("de-DE");
                JsonLanguageCatalog.InvalidateLanguages();
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ja-JP");
                ResourceService.Current.ChangeCulture("ja-JP");
                var source = new FixedLampSource(CreateRequest(ActiveScoreSource.Beatoraja));
                var executor = new LargeStatisticsBuildExecutor();
                session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
                var requests = new List<PlaylistLampViewerNavigationRequest>();
                viewModel = new PlaylistLampViewerViewModel("playlist", "Statistics presentation", session, TestUiDispatcherHost.Dispatcher, requests.Add);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.StartAndWaitForPresentableAsync(), "statistics presentation result");
                owner = new Window { Content = new Grid(), Width = 480, Height = 320 };
                scope.ShowAndWaitForContentRendered(owner);
                window = new PlaylistLampViewerWindow(owner, viewModel);
                scope.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                PlaylistLampAggregationResult result = viewModel.CurrentResult;
                ObservableCollection<PlaylistLampViewerSegmentViewModel> clearSegments = viewModel.ClearSegments;
                ObservableCollection<PlaylistLampViewerSegmentViewModel> rankSegments = viewModel.RankSegments;
                ObservableCollection<PlaylistLampViewerFolderRowViewModel> folderRows = viewModel.FolderRows;
                PlaylistLampViewerFolderRowViewModel[] rows = folderRows.ToArray();
                (ObservableCollection<PlaylistLampViewerSegmentViewModel> ClearSegments, ObservableCollection<PlaylistLampViewerSegmentViewModel> RankSegments)[] folderCollections = rows.Select(row => (row.ClearSegments, row.RankSegments)).ToArray();
                PlaylistLampViewerSegmentViewModel[] segments = clearSegments.Concat(rankSegments)
                    .Concat(rows.SelectMany(row => row.ClearSegments.Concat(row.RankSegments))).ToArray();
                Button[] buttons = FindDescendants<Button>(window).Where(button =>
                    AutomationProperties.GetAutomationId(button) == "PlaylistLampViewerSegment").ToArray();
                Assert.AreEqual(8, buttons.Length, "全体とフォルダそれぞれのclear/rankの正値を捕捉する");
                Assert.AreEqual(4, buttons.Count(button =>
                    button.DataContext is PlaylistLampViewerSegmentViewModel segment && segment.Count == 25000),
                    "全体とフォルダのclear/rankそれぞれで十分幅のあるNPの表示文字を確認する");
                object[] buttonSegments = buttons.Select(button => button.DataContext).ToArray();
                TextBlock[] legendLabels = FindDescendants<Border>(window)
                    .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend")
                    .Select(border => FindDescendants<TextBlock>(border).First()).ToArray();
                Assert.AreEqual(clearSegments.Count + rankSegments.Count, legendLabels.Length);
                object[] legendSegments = legendLabels.Select(label => label.DataContext).ToArray();
                var selected = (PlaylistLampViewerSegmentViewModel)buttons.First(button =>
                    button.DataContext is PlaylistLampViewerSegmentViewModel segment && segment.CategoryKey == "MAX").DataContext;
                selected.Invoke();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                int captures = source.CaptureCount;
                int builds = executor.BuildCount;
                int requestCount = requests.Count;
                collections.AddRange(new INotifyCollectionChanged[] { clearSegments, rankSegments, folderRows });
                foreach ((ObservableCollection<PlaylistLampViewerSegmentViewModel> ClearSegments, ObservableCollection<PlaylistLampViewerSegmentViewModel> RankSegments) pair in folderCollections) { collections.Add(pair.ClearSegments); collections.Add(pair.RankSegments); }
                foreach (INotifyCollectionChanged collection in collections) { collection.CollectionChanged += collectionHandler; }
                foreach (PlaylistLampViewerSegmentViewModel segment in segments)
                {
                    observedSegments.Add(segment, new HashSet<string>());
                    segment.PropertyChanged += segmentHandler;
                }
                session.ResultChanged += resultHandler;
                viewModel.PropertyChanged += parentHandler;

                void AssertSegmentLanguage(bool expectedCountMode)
                {
                    foreach (PlaylistLampViewerSegmentViewModel segment in segments)
                    {
                        string label = Resources.ResourceManager.GetString("PlaylistLampViewer_"
                            + (segment.Kind == PlaylistLampSegmentKind.Clear ? "clear_" : "rank_")
                            + segment.CategoryKey.ToLowerInvariant(), Resources.Culture)
                            ?? throw new AssertFailedException("区間の期待ラベル資源がない");
                        string percentage = segment.Denominator == 0 ? Resources.PlaylistLampViewer_unavailable
                            : segment.Count == 1 ? string.Format(CultureInfo.InvariantCulture,
                                Resources.PlaylistLampViewer_percentage_less_than_format,
                                string.Format(CultureInfo.InvariantCulture, Resources.PlaylistLampViewer_percentage_format, 0.01d))
                            : string.Format(CultureInfo.InvariantCulture, Resources.PlaylistLampViewer_percentage_format,
                                segment.Count == 25000 ? 100d : 0d);
                        string detail = string.Format(CultureInfo.InvariantCulture,
                            segment.Denominator == 0 ? Resources.PlaylistLampViewer_segment_unavailable_detail_format
                                : Resources.PlaylistLampViewer_segment_detail_adaptive_format,
                            label, segment.Count, percentage);
                        Assert.AreEqual(label, segment.Label);
                        Assert.AreEqual(percentage, segment.PercentageText);
                        Assert.AreEqual(detail, segment.DetailText);
                        Assert.AreEqual(detail, segment.AutomationName);
                        Assert.AreEqual(segment == selected ? Resources.PlaylistLampViewer_selected
                            : Resources.PlaylistLampViewer_not_selected, segment.SelectionStatus);
                        foreach (string property in new[] { "Label", "PercentageText", "DetailText", "AutomationName", "SelectionStatus" })
                        {
                            Assert.IsTrue(observedSegments[segment].Contains(property), "非表示・算出不可の区間も更新する: " + property);
                        }
                        foreach (Button button in buttons.Where(button => ReferenceEquals(button.DataContext, segment)))
                        {
                            Assert.AreEqual(detail, button.ToolTip);
                            if (segment.Count == 25000)
                            {
                                Assert.AreEqual(expectedCountMode ? "25000" : percentage, button.Content,
                                    "同じボタンの割合文字列は言語変更だけで更新し、曲数と区別する");
                            }
                            var peer = new ButtonAutomationPeer(button);
                            Assert.AreEqual(detail, peer.GetName());
                            Assert.AreEqual(segment.SelectionStatus, peer.GetItemStatus());
                        }
                        foreach (TextBlock legend in legendLabels.Where(legend => ReferenceEquals(legend.DataContext, segment)))
                        {
                            Assert.AreEqual(label, legend.Text);
                        }
                    }
                    Assert.AreSame(clearSegments, viewModel.ClearSegments);
                    Assert.AreSame(rankSegments, viewModel.RankSegments);
                    Assert.AreSame(folderRows, viewModel.FolderRows);
                    CollectionAssert.AreEqual(rows, folderRows.ToArray());
                    for (int index = 0; index < rows.Length; index++)
                    {
                        Assert.AreSame(folderCollections[index].ClearSegments, rows[index].ClearSegments);
                        Assert.AreSame(folderCollections[index].RankSegments, rows[index].RankSegments);
                    }
                    CollectionAssert.AreEqual(segments, clearSegments.Concat(rankSegments)
                        .Concat(rows.SelectMany(row => row.ClearSegments.Concat(row.RankSegments))).ToArray());
                    CollectionAssert.AreEqual(buttons, FindDescendants<Button>(window).Where(button =>
                        AutomationProperties.GetAutomationId(button) == "PlaylistLampViewerSegment").ToArray());
                    CollectionAssert.AreEqual(buttonSegments, buttons.Select(button => button.DataContext).ToArray());
                    CollectionAssert.AreEqual(legendLabels, FindDescendants<Border>(window)
                        .Where(border => AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerLegend")
                        .Select(border => FindDescendants<TextBlock>(border).First()).ToArray());
                    CollectionAssert.AreEqual(legendSegments, legendLabels.Select(label => label.DataContext).ToArray());
                    Assert.AreSame(selected, viewModel.SelectedSegment);
                    Assert.IsTrue(selected.IsSelected);
                    Assert.AreSame(result, viewModel.CurrentResult);
                    Assert.AreSame(result, session.Current);
                    Assert.AreEqual(captures, source.CaptureCount);
                    Assert.AreEqual(builds, executor.BuildCount);
                    Assert.AreEqual(requestCount, requests.Count);
                    Assert.AreEqual(0, resultChanges);
                    Assert.AreEqual(0, collectionChanges);
                }

                // 区間の言語契約は割合／曲数の代表だけで確認し、統計の幅×テーマ行列とは重ねない。
                foreach ((string language, bool countMode) in new[] { ("de-DE", false), ("fr-FR", false), ("en-US", true), ("ja-JP", false) })
                {
                    SelectDisplayMode(window, countMode);
                    foreach (HashSet<string> notifications in observedSegments.Values) { notifications.Clear(); }
                    ResourceService.Current.ChangeCulture(language);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();
                    Assert.AreEqual(countMode, viewModel.IsCountMode);
                    AssertSegmentLanguage(countMode);
                }
                // frのコロン前空白とzhの全角コロンを含む翻訳書式を、表示と実Automation peerまで確認する。
                foreach (string language in new[] { "ja-JP", "en-US", "fr-FR", "zh-CN", "ja-JP" })
                {
                    ResourceService.Current.ChangeCulture(language);
                    foreach (string theme in new[] { AppThemeService.Light, AppThemeService.Dark })
                    {
                        AppThemeService.ApplyTheme(theme);
                        foreach (double width in new[] { 1290d, 820d })
                        {
                            window.Width = width;
                            window.Height = width == 1290d ? 820 : 560;
                            TestUiDispatcherHost.ProcessQueuedPresentation();
                            window.UpdateLayout();
                            ItemsControl statistics = FindByAutomationId<ItemsControl>(window, "PlaylistLampViewerStatistics");
                            ItemsControl[] groups = FindDescendants<ItemsControl>(statistics)
                                .Where(item => AutomationProperties.GetAutomationId(item) == "PlaylistLampViewerStatisticGroup").ToArray();
                            Assert.AreEqual(3, groups.Length);
                            double[] tops = groups.Select(group => group.TranslatePoint(new Point(), statistics).Y).ToArray();
                            if (language == "ja-JP" && width == 1290d)
                            {
                                Assert.IsTrue(tops.All(top => Math.Abs(top - tops[0]) < 1), "既定日本語の9項目は一段に収まる");
                            }
                            if (width == 820d)
                            {
                                Assert.IsTrue(tops.Max() > tops.Min() + 1, "最小幅の大数値は群単位で折り返す");
                            }
                            Rect[] bounds = groups.Select(group => new Rect(group.TranslatePoint(new Point(), statistics), group.RenderSize)).ToArray();
                            for (int first = 0; first < bounds.Length; first++)
                            {
                                for (int second = first + 1; second < bounds.Length; second++)
                                {
                                    var intersection = Rect.Intersect(bounds[first], bounds[second]);
                                    Assert.IsTrue(intersection.IsEmpty || intersection.Width < 1 || intersection.Height < 1, "群同士は重ならない");
                                }
                            }
                            foreach (ItemsControl group in groups)
                            {
                                FrameworkElement[] items = FindDescendants<StackPanel>(group)
                                    .Where(item => AutomationProperties.GetAutomationId(item) == "PlaylistLampViewerStatistic").ToArray();
                                Assert.AreEqual(((IReadOnlyList<PlaylistLampViewerStatistic>)group.DataContext).Count, items.Length);
                                double previousRight = 0;
                                for (int index = 0; index < items.Length; index++)
                                {
                                    FrameworkElement item = items[index];
                                    Point origin = item.TranslatePoint(new Point(), statistics);
                                    Point end = item.TranslatePoint(new Point(item.ActualWidth, item.ActualHeight), statistics);
                                    Assert.AreEqual(tops[Array.IndexOf(groups, group)], origin.Y, 1, "群内では折り返さない");
                                    Assert.IsTrue(origin.X >= previousRight - 1 && end.X <= statistics.ActualWidth + 1, "重なりや右端のクリップがない");
                                    previousRight = end.X;
                                    Border[] separators = FindDescendants<Border>(item).Where(border => border.Visibility == Visibility.Visible).ToArray();
                                    Assert.AreEqual(index == 0 ? 0 : 1, separators.Length, "群の先頭や末尾に余分な線を残さない");
                                    foreach (Border separator in separators)
                                    {
                                        Assert.AreEqual(((SolidColorBrush)window.FindResource("App.BorderBrush")).Color, ((SolidColorBrush)separator.Background).Color);
                                    }
                                    TextBlock[] blocks = FindDescendants<TextBlock>(item).ToArray();
                                    Assert.AreEqual(2, blocks.Length);
                                    Assert.IsTrue(blocks[1].FontSize > blocks[0].FontSize);
                                    Assert.IsTrue(blocks[1].FontWeight.ToOpenTypeWeight() > blocks[0].FontWeight.ToOpenTypeWeight());
                                    foreach (TextBlock block in blocks)
                                    {
                                        Assert.AreEqual(TextWrapping.NoWrap, block.TextWrapping);
                                        Assert.AreEqual(TextTrimming.None, block.TextTrimming);
                                        Assert.IsTrue(block.ActualWidth + block.Margin.Left + block.Margin.Right + 1 >= block.DesiredSize.Width
                                            && block.ActualHeight + block.Margin.Top + block.Margin.Bottom + 1 >= block.DesiredSize.Height, "ラベルと値は途中で切れない");
                                        Assert.AreEqual(TextAlignment.Left, block.TextAlignment);
                                    }
                                    Assert.AreEqual(((SolidColorBrush)window.FindResource("App.SubtleTextBrush")).Color, ((SolidColorBrush)blocks[0].Foreground).Color);
                                    Assert.AreEqual(((SolidColorBrush)window.FindResource("App.TextBrush")).Color, ((SolidColorBrush)blocks[1].Foreground).Color);
                                }
                            }
                            AssertRenderedStatisticMembership(window, new[] { Resources.PlaylistLampViewer_total, Resources.PlaylistLampViewer_owned,
                                Resources.PlaylistLampViewer_missing, Resources.PlaylistLampViewer_ownership_rate, Resources.PlaylistLampViewer_played,
                                Resources.PlaylistLampViewer_no_play, Resources.PlaylistLampViewer_play_rate, Resources.PlaylistLampViewer_average_ex_rate,
                                Resources.PlaylistLampViewer_clear_rate, Resources.PlaylistLampViewer_score_source, Resources.PlaylistLampViewer_playlist_last_update });
                            Assert.AreSame(result, viewModel.CurrentResult);
                            CollectionAssert.AreEqual(segments[..clearSegments.Count], viewModel.ClearSegments.ToArray());
                            Assert.AreEqual(captures, source.CaptureCount);
                            Assert.AreEqual(1, executor.BuildCount);
                        }
                    }
                }
                window.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "closed statistics presentation drain");
                parentChanges = 0;
                foreach (HashSet<string> notifications in observedSegments.Values) { notifications.Clear(); }
                ResourceService.Current.ChangeCulture("en-US");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(0, parentChanges, "実閉鎖と破棄の終端後には親へ通知しない");
                Assert.IsTrue(observedSegments.Values.All(notifications => notifications.Count == 0), "破棄済みの区間へ言語通知しない");
            }
            finally
            {
                window?.Close();
                owner?.Close();
                if (viewModel != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "statistics presentation drain"); }
                if (viewModel != null) { viewModel.PropertyChanged -= parentHandler; }
                if (session != null) { session.ResultChanged -= resultHandler; }
                foreach (PlaylistLampViewerSegmentViewModel segment in observedSegments.Keys) { segment.PropertyChanged -= segmentHandler; }
                foreach (INotifyCollectionChanged collection in collections) { collection.CollectionChanged -= collectionHandler; }
                foreach ((string path, byte[]? contents) in languageBackups)
                {
                    if (contents == null) { File.Delete(path); }
                    else { File.WriteAllBytes(path, contents); }
                    JsonLanguageCatalog.Invalidate(Path.GetFileNameWithoutExtension(path));
                }
                JsonLanguageCatalog.InvalidateLanguages();
                ResourceService.Current.ChangeCulture(previousResourceCulture?.Name ?? previousUiCulture.Name);
                Resources.Culture = previousResourceCulture;
                CultureInfo.CurrentCulture = previousCulture;
                CultureInfo.CurrentUICulture = previousUiCulture;
                Application.Current.Resources.MergedDictionaries.Clear();
                foreach (ResourceDictionary dictionary in previousDictionaries) { Application.Current.Resources.MergedDictionaries.Add(dictionary); }
            }
        });
    }

    /// <summary>LR2 固有カテゴリを表示後にスコア未構成へ切り替え、不要カテゴリやスコア面の残存、復帰時の件数モード喪失を検出します。</summary>
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
                TestUiDispatcherHost.ProcessQueuedPresentation();

                SelectDisplayMode(window, true);
                Assert.IsFalse(viewModel.ClearSegments.Any(segment =>
                    segment.CategoryKey is "MAX" or "EXHARD"));
                Assert.IsTrue(viewModel.ClearSegments.Any(segment => segment.CategoryKey == "FC"));
                Assert.IsTrue(viewModel.ClearSegments.Any(segment => segment.CategoryKey == "HARD"));
                Assert.IsTrue(viewModel.IsGraphVisible);
                AssertRenderedStatisticMembership(
                    window,
                    new[]
                    {
                        Resources.PlaylistLampViewer_total,
                        Resources.PlaylistLampViewer_owned,
                        Resources.PlaylistLampViewer_missing,
                        Resources.PlaylistLampViewer_ownership_rate,
                        Resources.PlaylistLampViewer_played,
                        Resources.PlaylistLampViewer_no_play,
                        Resources.PlaylistLampViewer_play_rate,
                        Resources.PlaylistLampViewer_average_ex_rate,
                        Resources.PlaylistLampViewer_clear_rate,
                        Resources.PlaylistLampViewer_score_source,
                        Resources.PlaylistLampViewer_playlist_last_update
                    });

                PlaylistLampAggregationRequest degraded = CreateRequest(ActiveScoreSource.None);
                source.Replace(degraded);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    session.UpdateSelectedAsOfDateAsync(session.Query.SelectedLocalDate.HasValue ? null : DateTime.Today.AddDays(-1)),
                    "playlist lamp viewer degraded date capture");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.IsTrue(viewModel.IsCountMode);
                RadioButton percentageMode = FindByAutomationId<RadioButton>(window, "PlaylistLampViewerPercentageMode");
                RadioButton countMode = FindByAutomationId<RadioButton>(window, "PlaylistLampViewerCountMode");
                Assert.IsFalse(percentageMode.IsEnabled);
                Assert.IsFalse(countMode.IsEnabled);
                viewModel.IsPercentageMode = true;
                Assert.IsTrue(viewModel.IsCountMode, "unavailable scores must reject mode changes");
                Assert.IsTrue(viewModel.IsDegraded);
                Assert.IsFalse(viewModel.IsGraphVisible);
                Assert.AreEqual(0, FindDescendants<Button>(window)
                    .Count(button => button.DataContext is PlaylistLampViewerSegmentViewModel));
                AssertRenderedStatisticMembership(
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
                source.Replace(CreateRequest(ActiveScoreSource.Lr2));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(session.UpdateSelectedAsOfDateAsync(session.Query.SelectedLocalDate.HasValue ? null : DateTime.Today.AddDays(-1)), "LR2 score recovery");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                Assert.IsTrue(viewModel.IsCountMode);
                Assert.IsTrue(countMode.IsEnabled);
                Assert.IsTrue(viewModel.IsGraphVisible);
                Assert.IsFalse(viewModel.ClearSegments.Any(segment => segment.CategoryKey is "MAX" or "EXHARD"));
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>画面外の最大フォルダを含む共通件数尺度を切り替え、選択・スクロール・既存投影を保って再捕捉と再集計をせず、非整数DPI通知でも同じ状態を保って幅不変のラベルを再評価し、明示日付変更で新入力を捕捉したときだけ尺度を再計算することを確認します。</summary>
    [TestMethod]
    public void Viewer_countModeUsesAllNormalFoldersAndPreservesProjectionUntilDataRefresh()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateScaleRequest(100));
            var executor = new CountingLampBuildExecutor();
            var session = new PlaylistLampViewerSession("playlist", source, buildExecutor: executor);
            var requests = new List<PlaylistLampViewerNavigationRequest>();
            var viewModel = new PlaylistLampViewerViewModel("playlist", "共通尺度と長いタイトルの表示確認", session,
                TestUiDispatcherHost.Dispatcher, requests.Add);
            var owner = new Window { Width = 480, Height = 320, ShowInTaskbar = false, Content = new Grid() };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewModel) { Height = 560 };
            int collectionChanges = 0;
            int resultChanges = 0;
            NotifyCollectionChangedEventHandler collectionHandler = (_, _) => collectionChanges++;
            EventHandler<PlaylistLampAggregationResultChangedEventArgs> resultHandler = (_, _) => resultChanges++;
            var collections = new List<INotifyCollectionChanged>();
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.StartAndWaitForPresentableAsync(), "common scale initial result");
                windowTest.ShowAndWaitForContentRendered(window);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                SelectDisplayMode(window, false);
                Assert.AreEqual(150, viewModel.CurrentResult.Statistics.TotalCount);
                Assert.IsFalse(viewModel.FolderRows.Any(row => row.FolderName == "[NO SONG]"));
                Assert.AreEqual(0, viewModel.FolderRows.Single(row => row.FolderName == "empty").Count);
                IReadOnlyList<PlaylistLampViewerStatistic>[] statisticGroups = viewModel.StatisticGroups.ToArray();
                PlaylistLampViewerStatistic[] statistics = statisticGroups.SelectMany(group => group).Concat(viewModel.SupplementaryStatistics).ToArray();
                PlaylistLampViewerFolderRowViewModel[] rows = viewModel.FolderRows.ToArray();
                PlaylistLampViewerSegmentViewModel[] segments = rows.SelectMany(row => row.ClearSegments.Concat(row.RankSegments))
                    .Concat(viewModel.ClearSegments).Concat(viewModel.RankSegments).ToArray();
                collections.AddRange(new INotifyCollectionChanged[] { viewModel.FolderRows, viewModel.ClearSegments,
                    viewModel.RankSegments, viewModel.StatisticGroups, viewModel.SupplementaryStatistics });
                foreach (PlaylistLampViewerFolderRowViewModel? row in rows)
                {
                    collections.Add(row.ClearSegments);
                    collections.Add(row.RankSegments);
                }
                foreach (INotifyCollectionChanged collection in collections)
                {
                    collection.CollectionChanged += collectionHandler;
                }

                session.ResultChanged += resultHandler;
                Border small = FindRenderedFolder(window, "small");
                AssertFolderOccupancy(small, 1d);
                Assert.IsFalse(FindDescendants<Border>(window).Any(border =>
                    AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
                    && border.DataContext is PlaylistLampViewerFolderRowViewModel row && row.FolderName == "maximum"),
                    "the scale must include the maximum row before it is materialized");
                Button hardButton = FindDescendants<Button>(small).Single(button =>
                    button.DataContext is PlaylistLampViewerSegmentViewModel segment && segment.CategoryKey == "HARD");
                var selected = (PlaylistLampViewerSegmentViewModel)hardButton.DataContext;
                selected.Invoke();
                ScrollViewer scroll = FindByAutomationId<ScrollViewer>(window, "PlaylistLampViewerFolderScrollViewer");
                scroll.ScrollToVerticalOffset(1d);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                Assert.IsTrue(scroll.VerticalOffset > 0d);
                double offset = scroll.VerticalOffset;
                PlaylistLampAggregationResult current = viewModel.CurrentResult;
                int captures = source.CaptureCount;
                int builds = executor.BuildCount;
                foreach (bool count in new[] { true, false, true })
                {
                    SelectDisplayMode(window, count);
                    Assert.AreEqual(count, viewModel.IsCountMode);
                    Assert.AreEqual(!count, FindByAutomationId<RadioButton>(window, "PlaylistLampViewerPercentageMode").IsChecked);
                    small = FindRenderedFolder(window, "small");
                    AssertFolderOccupancy(small, count ? 0.5d : 1d);
                    AssertSegmentWidth(small, "HARD", 20d / (count ? 100d : 50d));
                    AssertSegmentWidth(small, "A", 10d / (count ? 100d : 50d));
                    string expectedPercentage = string.Format(CultureInfo.InvariantCulture,
                        Resources.PlaylistLampViewer_percentage_format, 40d);
                    Button hard = FindDescendants<Button>(small).Single(button =>
                        button.DataContext is PlaylistLampViewerSegmentViewModel segment && segment.CategoryKey == "HARD");
                    Assert.IsTrue((hard.ToolTip as string ?? throw new AssertFailedException("A segment tooltip is missing.")).Contains(expectedPercentage, StringComparison.Ordinal));
                    AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(hard)
                        ?? throw new AssertFailedException("A segment automation peer is missing.");
                    Assert.IsTrue(peer.GetName().Contains(expectedPercentage, StringComparison.Ordinal));
                    Assert.AreEqual(count ? 20.ToString("0", CultureInfo.InvariantCulture) : expectedPercentage, hard.Content);
                    AssertOverallLabel(window, count, 100);
                    Assert.AreSame(current, viewModel.CurrentResult);
                    Assert.AreSame(collections[0], viewModel.FolderRows);
                    Assert.AreSame(collections[1], viewModel.ClearSegments);
                    Assert.AreSame(collections[2], viewModel.RankSegments);
                    Assert.AreSame(collections[3], viewModel.StatisticGroups);
                    Assert.AreSame(collections[4], viewModel.SupplementaryStatistics);
                    CollectionAssert.AreEqual(statisticGroups, viewModel.StatisticGroups.ToArray());
                    CollectionAssert.AreEqual(statistics, viewModel.StatisticGroups.SelectMany(group => group).Concat(viewModel.SupplementaryStatistics).ToArray());
                    for (int index = 0; index < rows.Length; index++)
                    {
                        Assert.AreSame(collections[5 + index * 2], rows[index].ClearSegments);
                        Assert.AreSame(collections[6 + index * 2], rows[index].RankSegments);
                    }
                    CollectionAssert.AreEqual(rows, viewModel.FolderRows.ToArray());
                    CollectionAssert.AreEqual(segments, viewModel.FolderRows.SelectMany(row => row.ClearSegments.Concat(row.RankSegments))
                        .Concat(viewModel.ClearSegments).Concat(viewModel.RankSegments).ToArray());
                    Assert.AreSame(selected, viewModel.SelectedSegment);
                    Assert.IsTrue(selected.IsSelected);
                    Assert.AreEqual(offset, scroll.VerticalOffset);
                    Assert.AreEqual(captures, source.CaptureCount);
                    Assert.AreEqual(builds, executor.BuildCount);
                    Assert.AreEqual(0, resultChanges);
                    Assert.AreEqual(0, collectionChanges);
                    Assert.AreEqual(1, requests.Count);
                }
                // 論理幅を変えず、実MultiBindingのDPI入力だけでラベルを再評価します。
                Button dpiButton = FindDescendants<Button>(FindByAutomationId<Border>(window, "PlaylistLampViewerClearGraphHost")).First();
                double unchangedWidth = dpiButton.ActualWidth;
                MethodInfo dpiChanged = typeof(PlaylistLampViewerWindow).GetMethod("OnDpiChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new AssertFailedException("DPI通知境界がありません。");
                foreach (double density in new[] { 1.25d, 1d })
                {
                    dpiButton.SetCurrentValue(ContentControl.ContentProperty, "DPI再評価待ち");
                    dpiChanged.Invoke(window, [new DpiScale(window.PixelsPerDip, window.PixelsPerDip), new DpiScale(density, density)]);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();
                    Assert.AreEqual(unchangedWidth, dpiButton.ActualWidth, 0.01d);
                    Assert.AreEqual(density, window.PixelsPerDip);
                    var dpiSegment = (PlaylistLampViewerSegmentViewModel)dpiButton.DataContext;
                    Assert.AreEqual(dpiSegment.Count.ToString("0", CultureInfo.InvariantCulture), dpiButton.Content);
                    Assert.AreSame(current, viewModel.CurrentResult);
                    CollectionAssert.AreEqual(rows, viewModel.FolderRows.ToArray());
                    CollectionAssert.AreEqual(segments, viewModel.FolderRows.SelectMany(row => row.ClearSegments.Concat(row.RankSegments))
                        .Concat(viewModel.ClearSegments).Concat(viewModel.RankSegments).ToArray());
                    Assert.AreSame(selected, viewModel.SelectedSegment);
                    Assert.AreEqual(offset, scroll.VerticalOffset);
                    Assert.IsTrue(viewModel.IsCountMode);
                    Assert.AreEqual(captures, source.CaptureCount);
                    Assert.AreEqual(builds, executor.BuildCount);
                    Assert.AreEqual(0, resultChanges);
                    Assert.AreEqual(0, collectionChanges);
                    Assert.AreEqual(1, requests.Count);
                }
                scroll.ScrollToEnd();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                Border maximum = FindRenderedFolder(window, "maximum");
                Button maximumButton = FindDescendants<Button>(maximum).First();
                double maximumWidth = maximumButton.ActualWidth;
                foreach (bool count in new[] { false, true })
                {
                    SelectDisplayMode(window, count);
                    AssertFolderOccupancy(maximum, 1d);
                    Assert.AreEqual(maximumWidth, maximumButton.ActualWidth, 1d);
                    Assert.AreEqual(count ? 100.ToString("0", CultureInfo.InvariantCulture)
                        : string.Format(CultureInfo.InvariantCulture, Resources.PlaylistLampViewer_percentage_format, 100d), maximumButton.Content);
                    AssertOverallLabel(window, count, 100);
                }
                source.Replace(CreateScaleRequest(200));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(session.UpdateSelectedAsOfDateAsync(session.Query.SelectedLocalDate.HasValue ? null : DateTime.Today.AddDays(-1)), "common scale changed maximum");
                TestUiDispatcherHost.ProcessQueuedPresentation();
                scroll.ScrollToHome();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                window.UpdateLayout();
                Assert.IsTrue(viewModel.IsCountMode);
                Assert.AreEqual(200d, viewModel.FolderBarScale);
                small = FindRenderedFolder(window, "small");
                AssertFolderOccupancy(small, 0.25d);
                AssertSegmentWidth(small, "HARD", 20d / 200d);
                AssertSegmentWidth(small, "A", 10d / 200d);
            }
            finally
            {
                session.ResultChanged -= resultHandler;
                foreach (INotifyCollectionChanged collection in collections)
                {
                    collection.CollectionChanged -= collectionHandler;
                }

                if (window.IsVisible)
                {
                    window.Close();
                }

                if (owner.IsVisible)
                {
                    owner.Close();
                }

                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    /// <summary>二つの Window に明示的な割合・件数操作を与え、一方の切替や終了が他方の表示モードを書き換える誤りを検出します。</summary>
    [TestMethod]
    public void Viewer_displayModeIsIndependentAcrossWindows()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var owner = new Window { Width = 480, Height = 320, ShowInTaskbar = false, Content = new Grid() };
            var viewers = new List<PlaylistLampViewerViewModel>();
            var windows = new List<PlaylistLampViewerWindow>();
            windowTest.ShowAndWaitForContentRendered(owner);
            try
            {
                for (int index = 0; index < 2; index++)
                {
                    var session = new PlaylistLampViewerSession("playlist", new FixedLampSource(CreateWidthAwareRequest()));
                    var viewer = new PlaylistLampViewerViewModel("playlist", "Independent window", session,
                        TestUiDispatcherHost.Dispatcher, _ => { });
                    viewers.Add(viewer);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewer.StartAndWaitForPresentableAsync(), "independent viewer result");
                    var window = new PlaylistLampViewerWindow(owner, viewer);
                    windows.Add(window);
                    windowTest.ShowAndWaitForContentRendered(window);
                    SelectDisplayMode(window, false);
                    if (index == 0)
                    {
                        SelectDisplayMode(window, true);
                    }

                    if (index == 1)
                    {
                        Assert.IsTrue(viewers[0].IsCountMode);
                        SelectDisplayMode(window, true);
                        SelectDisplayMode(windows[0], false);
                        Assert.IsTrue(viewer.IsCountMode);
                        window.Close();
                    }
                }
                Assert.IsTrue(viewers[1].IsCountMode);
                Assert.IsTrue(viewers[0].IsPercentageMode);
            }
            finally
            {
                foreach (PlaylistLampViewerWindow window in windows)
                {
                    if (window.IsVisible)
                    {
                        window.Close();
                    }
                }

                if (owner.IsVisible)
                {
                    owner.Close();
                }

                foreach (PlaylistLampViewerViewModel viewer in viewers)
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(viewer.DisposeAsync().AsTask(), "lamp presentation session actual drain");
                }
            }
        });
    }

    /// <summary>ゼロ件フォルダとフォルダなしへ更新し、件数モードとゼロ尺度を保ち、偽の操作区間や非有限の幅を作る誤りを検出します。</summary>
    [TestMethod]
    public void Viewer_countModeRetainsZeroRowsAndHandlesNoFoldersAfterRefresh()
    {
        TestUiDispatcherHost.RunWindowTest(windowTest =>
        {
            var source = new FixedLampSource(CreateWidthAwareRequest());
            var session = new PlaylistLampViewerSession("playlist", source);
            var viewer = new PlaylistLampViewerViewModel("playlist", "Empty scale", session,
                TestUiDispatcherHost.Dispatcher, _ => { });
            var owner = new Window { Width = 480, Height = 320, ShowInTaskbar = false, Content = new Grid() };
            windowTest.ShowAndWaitForContentRendered(owner);
            var window = new PlaylistLampViewerWindow(owner, viewer);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewer.StartAndWaitForPresentableAsync(), "empty scale initial result");
                windowTest.ShowAndWaitForContentRendered(window);
                SelectDisplayMode(window, true);
                // ゼロ件行を保持する場合と、行そのものがない場合の双方を確認します。
                foreach (string[] folders in new[] { new[] { "zero-one", "zero-two" }, Array.Empty<string>() })
                {
                    source.Replace(new PlaylistLampAggregationRequest("playlist", folders, [], CreateWidthAwareRequest().ScoreSnapshot));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(session.UpdateSelectedAsOfDateAsync(session.Query.SelectedLocalDate.HasValue ? null : DateTime.Today.AddDays(-1)), "empty scale refresh");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    window.UpdateLayout();
                    Assert.IsTrue(viewer.IsCountMode);
                    Assert.AreEqual(0d, viewer.FolderBarScale);
                    Assert.AreEqual(folders.Length, viewer.FolderRows.Count);
                    Assert.IsTrue(viewer.FolderRows.All(row => row.Count == 0));
                    Assert.AreEqual(0, FindDescendants<Button>(window).Count(button =>
                        button.DataContext is PlaylistLampViewerSegmentViewModel));
                    Assert.IsTrue(FindDescendants<PlaylistLampWeightedStackPanel>(window).All(panel =>
                        double.IsFinite(panel.ActualWidth) && panel.ActualWidth >= 0d));
                }
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

                TestUiDispatcherHost.AwaitTaskOnDispatcher(viewer.DisposeAsync().AsTask(), "lamp presentation session actual drain");
            }
        });
    }

    private static void SelectDisplayMode(PlaylistLampViewerWindow window, bool count)
    {
        RadioButton radio = FindByAutomationId<RadioButton>(window,
            count ? "PlaylistLampViewerCountMode" : "PlaylistLampViewerPercentageMode");
        Assert.IsTrue(radio.IsEnabled);
        radio.IsChecked = true;
        TestUiDispatcherHost.ProcessQueuedPresentation();
        window.UpdateLayout();
    }

    private static Border FindRenderedFolder(Window window, string folderName)
        => FindDescendants<Border>(window).Single(border =>
            AutomationProperties.GetAutomationId(border) == "PlaylistLampViewerFolderRow"
            && border.DataContext is PlaylistLampViewerFolderRowViewModel row && row.FolderName == folderName);

    private static void AssertFolderOccupancy(Border row, double fraction)
    {
        foreach (string id in new[] { "PlaylistLampViewerClearFolderBarHost", "PlaylistLampViewerRankFolderBarHost" })
        {
            Border host = FindByAutomationId<Border>(row, id);
            Button[] buttons = FindDescendants<Button>(host).ToArray();
            double right = buttons.Max(button => button.TranslatePoint(new Point(button.ActualWidth, 0), host).X);
            Assert.AreEqual(host.ActualWidth * fraction, right, 1d, "folder occupied width");
        }
    }

    private static void AssertSegmentWidth(Border row, string category, double fraction)
    {
        Button button = FindDescendants<Button>(row).Single(candidate =>
            candidate.DataContext is PlaylistLampViewerSegmentViewModel segment && segment.CategoryKey == category);
        Border host = FindAncestor<Border>(button)
            ?? throw new AssertFailedException("The segment bar host is missing.");
        Assert.AreEqual(host.ActualWidth * fraction, button.ActualWidth, 1d, category + " common scale width");
    }

    private static void AssertOverallLabel(Window window, bool count, int npCount)
    {
        foreach (string id in new[] { "PlaylistLampViewerClearGraphHost", "PlaylistLampViewerRankGraphHost" })
        {
            Border host = FindByAutomationId<Border>(window, id);
            AssertBoundedHost(host);
            Button np = FindDescendants<Button>(host).Single(button =>
                button.DataContext is PlaylistLampViewerSegmentViewModel segment && segment.CategoryKey == "NP");
            Assert.AreEqual(count ? npCount.ToString("0", CultureInfo.InvariantCulture)
                : string.Format(CultureInfo.InvariantCulture, Resources.PlaylistLampViewer_percentage_format, npCount * 100d / 150d), np.Content);
        }
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
        Assert.IsTrue(barLeft >= labelRight - 1d, "ラベルと棒を重ねません。");
        Assert.IsTrue(countLeft >= barRight - 1d, "棒と件数を重ねません。");
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
            Grid clearHalf = FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerClearFolderHalf");
            Grid rankHalf = FindByAutomationId<Grid>(rowElement, "PlaylistLampViewerRankFolderHalf");
            yield return clearHalf.ColumnDefinitions[Grid.GetColumn(FindFolderCountBlock(clearHalf, row))].ActualWidth;
            yield return rankHalf.ColumnDefinitions[Grid.GetColumn(FindFolderCountBlock(rankHalf, row))].ActualWidth;
        }
    }

    private static void AssertContrastForeground(DependencyObject backgroundElement, Control foregroundElement)
    {
        Brush? backgroundBrush = GetBackgroundBrush(backgroundElement);
        Assert.IsInstanceOfType(backgroundBrush, typeof(SolidColorBrush));
        Assert.IsInstanceOfType<SolidColorBrush>(foregroundElement.Foreground);
        var background = (SolidColorBrush)backgroundBrush!;
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
        Brush? backgroundBrush = GetBackgroundBrush(backgroundElement);
        Assert.IsInstanceOfType(backgroundBrush, typeof(SolidColorBrush));
        Assert.IsInstanceOfType<SolidColorBrush>(foregroundElement.Foreground);
        var background = (SolidColorBrush)backgroundBrush!;
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
        TestUiDispatcherHost.ProcessQueuedPresentation();
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

    private static Brush? GetBackgroundBrush(DependencyObject element)
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

    private static void AssertRenderedStatisticMembership(Window window, string[] expectedLabels)
    {
        FrameworkElement[] items = FindDescendants<FrameworkElement>(window)
            .Where(element => AutomationProperties.GetAutomationId(element) is
                "PlaylistLampViewerStatistic" or "PlaylistLampViewerSupplementaryStatistic")
            .ToArray();
        Assert.AreEqual(expectedLabels.Length, items.Length);
        CollectionAssert.AreEqual(expectedLabels, items.Select(item => ((PlaylistLampViewerStatistic)item.DataContext).Label).ToArray());
        Assert.AreEqual(2, items.Count(item => AutomationProperties.GetAutomationId(item) == "PlaylistLampViewerSupplementaryStatistic"));
        foreach (FrameworkElement item in items)
        {
            var statistic = (PlaylistLampViewerStatistic)item.DataContext;
            string expectedDetail = string.Format(CultureInfo.CurrentCulture,
                Resources.PlaylistLampViewer_statistic_detail_format, statistic.Label, statistic.Value);
            Assert.AreEqual(expectedDetail, AutomationProperties.GetName(item));
            if (item is TextBlock auxiliary)
            {
                Assert.AreEqual(expectedDetail, auxiliary.Text);
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(auxiliary)
                    ?? throw new AssertFailedException("補助行のAutomation peerがありません。");
                Assert.AreEqual(expectedDetail, peer.GetName());
            }
            else
            {
                TextBlock[] blocks = FindDescendants<TextBlock>(item).ToArray();
                CollectionAssert.AreEqual(new[] { statistic.Label, statistic.Value }, blocks.Select(block => block.Text).ToArray());
                AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(blocks[1])
                    ?? throw new AssertFailedException("統計値のAutomation peerがありません。");
                Assert.AreEqual(expectedDetail, peer.GetName());
            }
        }
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

    private static void AssertWidthAwareLabels(Button[] buttons, Window coordinateRoot, bool countMode)
    {
        Button wideButton = buttons.Single(button =>
            ((PlaylistLampViewerSegmentViewModel)button.DataContext).Count == 203);
        Button narrowButton = buttons.Single(button =>
            ((PlaylistLampViewerSegmentViewModel)button.DataContext).Count == 1);
        Assert.IsTrue(wideButton.ActualWidth > narrowButton.ActualWidth,
            "the width-aware fixture must provide an actual wide and narrow segment");
        Assert.IsTrue(wideButton.ActualWidth > 0d);
        Assert.IsTrue(narrowButton.ActualWidth > 0d);

        string? wideContent = wideButton.Content as string;
        Assert.IsFalse(string.IsNullOrWhiteSpace(wideContent),
            "a segment with enough width must expose an in-bar percentage");
        Assert.AreEqual(
            countMode ? 203.ToString("0", CultureInfo.InvariantCulture)
                : string.Format(CultureInfo.InvariantCulture, Resources.PlaylistLampViewer_percentage_format, 203d * 100d / 204d),
            wideContent);
        Assert.IsTrue(wideContent != ((PlaylistLampViewerSegmentViewModel)wideButton.DataContext).DetailText,
            "the in-bar label must not expand to the full tooltip detail");
        TextBlock? renderedWideLabel = FindDescendants<TextBlock>(wideButton)
            .SingleOrDefault(textBlock => textBlock.Text == wideContent);
        Assert.IsNotNull(renderedWideLabel,
            "the wide percentage must be present in the rendered button content");
        Point buttonOrigin = wideButton.TranslatePoint(new Point(0, 0), coordinateRoot);
        Point buttonEnd = wideButton.TranslatePoint(
            new Point(wideButton.ActualWidth, wideButton.ActualHeight),
            coordinateRoot);
        Point labelOrigin = renderedWideLabel!.TranslatePoint(new Point(0, 0), coordinateRoot);
        Point labelEnd = renderedWideLabel!.TranslatePoint(
            new Point(renderedWideLabel!.ActualWidth, renderedWideLabel!.ActualHeight),
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

    private static T? FindAncestor<T>(DependencyObject element, string automationId)
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

    private static T? FindAncestor<T>(DependencyObject element)
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

        PlaylistLampEntrySnapshot[] entries = new[]
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
        DateTime playlistLastUpdated)
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

            PlaylistLampViewerStatistic card = viewModel.SupplementaryStatistics.Single(
                candidate => candidate.Label == Resources.PlaylistLampViewer_playlist_last_update);
            Assert.AreEqual(
                "2026-08-29 10:49",
                card.Value,
                "playlist.last_update is a legacy local wall-clock value and must not be timezone converted");
        }
        finally
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(viewModel.DisposeAsync().AsTask(), "lamp presentation session actual drain");
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
        Dictionary<string, PlaylistLampScore> bySha256 = scores.ToDictionary(score => score.Sha256, StringComparer.OrdinalIgnoreCase);
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
        PlaylistLampScore[] scores = (folderNames ?? [])
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
        Dictionary<string, PlaylistLampScore> bySha256 = scores.ToDictionary(score => score.Sha256, StringComparer.OrdinalIgnoreCase);
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

    private static PlaylistLampAggregationRequest CreateScaleRequest(int maximumCount)
    {
        PlaylistLampScore[] scores = new[]
        {
            new PlaylistLampScore("hard-a", "hard-a", ClearType.HARD, RankType.A, 50, 50, 100, 1),
            new PlaylistLampScore("hard-f", "hard-f", ClearType.HARD, RankType.F, 50, 50, 100, 1),
            new PlaylistLampScore("assist-f", "assist-f", ClearType.L_ASSIST, RankType.F, 50, 50, 100, 1)
        };
        PlaylistLampEntrySnapshot[] entries = Enumerable.Range(0, 50).Select(index =>
        {
            PlaylistLampScore score = scores[index < 10 ? 0 : index < 20 ? 1 : 2];
            return new PlaylistLampEntrySnapshot("small", "small-" + index, true,
                sha256: score.Sha256, resolvedSha256: score.Sha256);
        }).Concat(Enumerable.Range(0, maximumCount).Select(index =>
            new PlaylistLampEntrySnapshot("maximum", "maximum-" + index, false, sha256: "missing")))
            .Concat(Enumerable.Range(0, 250).Select(index =>
                new PlaylistLampEntrySnapshot("[NO SONG]", "special-" + index, false, sha256: "special"))).ToArray();
        string[] folders = new[] { "leading-empty", "small", "empty" }
            .Concat(Enumerable.Range(0, 60).Select(index => "padding-" + index))
            .Concat(new[] { "maximum", "[NO SONG]" }).ToArray();
        var snapshot = new PlaylistLampScoreSnapshot(ActiveScoreSource.Beatoraja, ScoreTableLoadStatus.Loaded,
            1, 1, null, scoresBySha256: scores.ToDictionary(score => score.Sha256, StringComparer.OrdinalIgnoreCase));
        return new PlaylistLampAggregationRequest("playlist", folders, entries, snapshot);
    }

    private sealed class LargeStatisticsBuildExecutor : IPlaylistLampViewerBuildExecutor
    {
        internal int BuildCount { get; private set; }

        public Task<PlaylistLampAggregationResult> BuildAsync(PlaylistLampAggregationRequest request, PlaylistLampAggregationService aggregation, CancellationToken token)
        {
            BuildCount++;
            PlaylistLampAggregationResult actual = aggregation.Aggregate(request, token);
            // 有効なint件数の大数値を、巨大なエントリ集合の生成なしで表示境界へ与える。
            var statistics = new PlaylistLampStatistics(2100000000, 1450000000, 650000000, 1230000000, 870000000,
                145d / 210, 123d / 210, 0.734, 0.41, true, null, new DateTime(2026, 8, 29, 10, 49, 7));
            // 表示用の不変入力で1/25001の小正値と空フォルダを用意し、巨大entryや実DBを不要にする。
            PlaylistLampSegment[] ClearSegments(string? folder, int denominator) => actual.ClearSegments.Select(segment =>
                PlaylistLampSegment.CreateClear(segment.ClearCategory ?? throw new AssertFailedException("clear category is required"),
                    denominator == 0 ? 0 : segment.ClearCategory == PlaylistLampClearCategory.MAX ? 1
                        : segment.ClearCategory == PlaylistLampClearCategory.NP ? 25000 : 0,
                    denominator, true, folder != null, folder)).ToArray();
            PlaylistLampSegment[] RankSegments(string? folder, int denominator) => actual.RankSegments.Select(segment =>
                PlaylistLampSegment.CreateRank(segment.RankCategory ?? throw new AssertFailedException("rank category is required"),
                    denominator == 0 ? 0 : segment.RankCategory == PlaylistLampRankCategory.AAA ? 1
                        : segment.RankCategory == PlaylistLampRankCategory.NP ? 25000 : 0,
                    denominator, true, folder != null, folder)).ToArray();
            PlaylistLampFolderRow[] rows =
            [
                new("folder", 25001, 1, 25000, ClearSegments("folder", 25001), RankSegments("folder", 25001)),
                new("empty", 0, 0, 0, ClearSegments("empty", 0), RankSegments("empty", 0))
            ];
            return Task.FromResult(new PlaylistLampAggregationResult(actual.PlaylistId, actual.State, rows,
                ClearSegments(null, 25001), RankSegments(null, 25001), statistics, actual.ScoreSnapshot, actual.FailureMessage, actual.Query));
        }
    }

    private sealed class CountingLampBuildExecutor : IPlaylistLampViewerBuildExecutor
    {
        private int buildCount;

        /// <summary>実際の集計呼出し回数。表示切替による再集計を検出します。</summary>
        internal int BuildCount => Volatile.Read(ref buildCount);

        /// <inheritdoc />
        public Task<PlaylistLampAggregationResult> BuildAsync(PlaylistLampAggregationRequest request,
            PlaylistLampAggregationService aggregationService, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref buildCount);
            return Task.Run(() => aggregationService.Aggregate(request, cancellationToken), cancellationToken);
        }
    }

    private sealed class HistoricalLampSource : IPlaylistLampViewerDataSource
    {
        private readonly object stateGate = new();


        private bool historyAvailable;

        private DateTime? expectedSelectedDate;

        private TaskCompletionSource<PlaylistLampViewerQuery>? expectedQuery;


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
                    throw new InvalidOperationException("履歴要求の待機は一度に一件に限ります。");
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
            TaskCompletionSource<PlaylistLampViewerQuery>? completion = null;
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
            var today = DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified);
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
        private int captureCount;

        /// <summary>データ捕捉回数。表示切替による読取り要求を検出します。</summary>
        internal int CaptureCount => Volatile.Read(ref captureCount);
        private PlaylistLampAggregationRequest request;


        internal FixedLampSource(PlaylistLampAggregationRequest request)
        {
            this.request = request;
        }


        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            PlaylistLampViewerQuery query,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref captureCount);
            return ValueTask.FromResult(new PlaylistLampAggregationRequest(
                query.PlaylistId, request.FolderOrder, request.Entries, request.ScoreSnapshot,
                request.PlaylistLastUpdated, request.InputState, request.FailureMessage, query: query,
                historicalDateRange: request.HistoricalDateRange, historicalStatus: request.HistoricalStatus,
                historicalFailureMessage: request.HistoricalFailureMessage));
        }

        internal void Replace(PlaylistLampAggregationRequest request)
        {
            this.request = request;
        }
    }
}
