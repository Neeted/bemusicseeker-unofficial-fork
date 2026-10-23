using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class MainWindowPackageMaintenanceWpfTests
{
    /// <summary>保留rootと個別表示だけで検索下・一覧上へ現れ、他区分・概要では高さを残さず、履歴バーとも排他的に表示します。</summary>
    /// <remarks>実ApplyRowsからcompiled Bindingと実配置までを確認し、区分の分類判断は既存の一覧テストへ任せます。</remarks>
    [TestMethod]
    public void PendingInstallBar_TracksPresentedSectionAndCollapsesOutsidePending()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(s => s.StartupSelectInstallPending = false),
            (scope, vm, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, vm, window);
                var bar = (Border)window.FindName("pendingInstallOperationBar");
                var history = (Border)window.FindName("playHistorySummaryBar");
                var search = (Border)window.FindName("mainTableToolbar");
                var table = (CustomTableView)window.FindName("customTableView");
                var pendingRoot = (TreeViewItem)window.FindName("treeViewItemInstallPending");
                PackageChartEntry entry = CreatePendingBarEntry("A");
                var package = ChartPackage.FromChartEntries([entry]);
                package.path = @"C:\pending-bar";
                pendingRoot.ItemsSource = new[] { package };
                pendingRoot.IsSelected = true;
                PresentPendingBarRows(vm, [LibraryChartRow.FromPackageChartEntry(entry)]);
                AssertVisiblePendingBar();
                pendingRoot.UpdateLayout();
                var child = pendingRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(child);
                child.IsSelected = true;
                PresentPendingBarRows(vm, [LibraryChartRow.FromPackageChartEntry(entry)]);
                AssertVisiblePendingBar();

                // 通常・導入済み・保守・playlist詳細・履歴は同じAuto行に余白を残しません。
                foreach (MainViewUpdateMode mode in new[] { MainViewUpdateMode.FolderFilterSelected,
                    MainViewUpdateMode.NewlyInstalledFolderSelected, MainViewUpdateMode.FullScanAllChartsFilterSelected,
                    MainViewUpdateMode.PlaylistFilterSelected, MainViewUpdateMode.PlayHistorySelected })
                {
                    PresentPendingBarRows(vm, [], mode);
                    AssertCollapsedPendingBar();
                }
                PresentPendingBarRows(vm, []);
                vm.PlaylistWorkspace.IsPlaylistSummaryMode = true;
                AssertCollapsedPendingBar();
                vm.PlaylistWorkspace.IsPlaylistSummaryMode = false;
                vm.PlayHistory.BeginRequest(PlayHistoryPeriodRequest.All(), string.Empty,
                    vm.PlayHistory.SelectedDisplayTargetIdentity, vm.PlayHistory.DisplayTargetRevision);
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.AreEqual(Visibility.Collapsed, bar.Visibility);
                Assert.AreEqual(Visibility.Visible, history.Visibility);
                vm.PlayHistory.Deactivate();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                AssertVisiblePendingBar();

                void AssertVisiblePendingBar()
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    bar.UpdateLayout();
                    Assert.AreEqual(Visibility.Visible, bar.Visibility);
                    Assert.IsTrue(bar.ActualHeight > 0);
                    Assert.AreEqual(Visibility.Collapsed, history.Visibility);
                    var parent = (Grid)bar.Parent;
                    double top = bar.TranslatePoint(new Point(), parent).Y;
                    Assert.IsTrue(top >= search.TranslatePoint(new Point(), parent).Y + search.ActualHeight);
                    Assert.IsTrue(top + bar.ActualHeight <= table.TranslatePoint(new Point(), parent).Y);
                }
                void AssertCollapsedPendingBar()
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    ((Grid)bar.Parent).UpdateLayout();
                    Assert.AreEqual(Visibility.Collapsed, bar.Visibility);
                    Assert.AreEqual(0d, ((Grid)bar.Parent).RowDefinitions[Grid.GetRow(bar)].ActualHeight,
                        "同じ行の履歴バーが非表示のときは空白を残しません。");
                }
            }, installTreeTerminal: new MainWindowInstallTreeTerminal((_, _) => Task.FromResult(true)));
    }

    /// <summary>未選択・単一・複数・解除・差替え・空化・並替えで実Bindingの有効状態と現在の要求が一致し、フォーカス要求後も選択を維持し、未選択の兄弟を追加しません。</summary>
    /// <remarks>実Button.Clickから選択譜面用terminalへの要求を観測し、推定/導入/クリア本体とreceiptは既存の下位・代表フローテストが担当します。</remarks>
    [TestMethod]
    public void PendingInstallBar_UsesCurrentBoundSelectionAndPreservesMultipleRowsAfterFocusRequest()
    {
        var estimates = new List<PendingInstallDestinationSearchRequest>();
        var installs = new List<PendingInstallPackageOperationRequest>();
        var clears = new List<PendingInstallDestinationClearRequest>();
        int packageInstalls = 0;
        int repairs = 0;
        var installCompletion = new TaskCompletionSource<PendingPackageMutationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task installEvent = Task.CompletedTask;
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(s => s.StartupSelectInstallPending = false),
            (scope, vm, window) =>
            {
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, vm, window);
                var table = (CustomTableView)window.FindName("customTableView");
                Button[] buttons = PendingBarButtons(window);
                PackageChartEntry[] entries = [CreatePendingBarEntry("A"), CreatePendingBarEntry("B"), CreatePendingBarEntry("Sibling")];
                LibraryChartRow[] rows = entries.Select(entry => LibraryChartRow.FromPackageChartEntry(entry)).ToArray();
                var package = ChartPackage.FromChartEntries(entries);
                package.path = @"C:\pending-bar";
                var pendingRoot = (TreeViewItem)window.FindName("treeViewItemInstallPending");
                pendingRoot.ItemsSource = new[] { package };
                pendingRoot.UpdateLayout();
                var child = pendingRoot.ItemContainerGenerator.ContainerFromIndex(0) as TreeViewItem;
                Assert.IsNotNull(child);
                child.IsSelected = true;
                PresentPendingBarRows(vm, rows);
                try
                {
                    AssertBarEnabled(false);
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, rows[0]));
                    AssertBarEnabled(true);
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, rows[0]) || ReferenceEquals(row, rows[1]));
                    AssertBarEnabled(true);
                    foreach (Button button in buttons)
                    {
                        Assert.IsTrue(button.Focusable);
                        // 非活性表示ではOSの取得成功を条件にせず、実ボタンへのフォーカス要求後の選択を観測します。
                        Keyboard.Focus(button);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        CollectionAssert.AreEquivalent(rows[..2], table.GetSelectedRowsSnapshot().Cast<LibraryChartRow>().ToArray());
                    }
                    ClickPendingBar(buttons[0]);
                    Assert.AreEqual(1, estimates.Count);
                    Assert.AreEqual(InstallEstimationCandidatePolicy.RelatedOnly, estimates[0].CandidatePolicy);
                    CollectionAssert.AreEqual(entries[..2], estimates[0].PackageTargets.Select(t => t.PackageEntry).ToArray());
                    Assert.AreEqual(0, installs.Count, "推定後の自動導入はありません。");
                    ClickPendingBar(buttons[2]);
                    CollectionAssert.AreEqual(entries[..2], clears.Single().PackageTargets.Select(t => t.PackageEntry).ToArray());
                    Assert.AreEqual(0, repairs);
                    table.SelectRowsByPredicate(_ => false);
                    AssertBarEnabled(false);
                    // RaiseEventでも入口自身が空選択を拒否し、左の個別packageへ補完しません。
                    foreach (Button button in buttons) { ClickPendingBar(button); }
                    Assert.AreEqual(1, estimates.Count);
                    Assert.AreEqual(1, clears.Count);
                    Assert.AreEqual(0, installs.Count);

                    vm.MainChartList.SetRows(new List<LibraryChartRow> { rows[2] }, updateSummary: false);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertBarEnabled(false);
                    table.SelectRowsByPredicate(_ => true);
                    ClickPendingBar(buttons[0]);
                    Assert.AreSame(entries[2], estimates[^1].PackageTargets.Single().PackageEntry);
                    // 行の並替え後の選択保持/解除そのものは固定せず、現在の実選択を要求へ渡します。
                    vm.MainChartList.SetRows(new List<LibraryChartRow> { rows[2], rows[1], rows[0] }, updateSummary: false);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    AssertBarEnabled(table.GetSelectedRowsSnapshot().Count > 0);
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, rows[1]));
                    ClickPendingBar(buttons[0]);
                    Assert.AreSame(entries[1], estimates[^1].PackageTargets.Single().PackageEntry);
                    vm.MainChartList.SetRows(new List<LibraryChartRow>(), updateSummary: false);
                    AssertBarEnabled(false);
                    PresentPendingBarRows(vm, rows);
                    table.SelectRowsByPredicate(row => ReferenceEquals(row, rows[0]) || ReferenceEquals(row, rows[1]));
                    // 実変更活動中の3入口は終端へ到達せず、解放だけでは予約実行されません。
                    int estimateCount = estimates.Count;
                    using (vm.ChartMutationActivity.Enter())
                    {
                        foreach (Button button in buttons) { ClickPendingBar(button); }
                        Assert.AreEqual(estimateCount, estimates.Count);
                        Assert.AreEqual(1, clears.Count);
                        Assert.AreEqual(0, installs.Count);
                    }
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(estimateCount, estimates.Count);
                    Assert.AreEqual(0, installs.Count);
                    installEvent = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(
                        () => buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent, buttons[1])));
                    Assert.IsFalse(installEvent.IsCompleted);
                    Assert.AreEqual(PendingInstallPackageOperationKind.ManualInstall, installs.Single().Kind);
                    CollectionAssert.AreEqual(entries[..2], installs.Single().Targets.Select(t => t.PackageEntry).ToArray());
                    Assert.AreEqual(0, packageInstalls);
                    Assert.IsTrue(child.IsSelected, "導入完了前はツリー選択を移しません。");
                    installCompletion.SetResult(PendingPackageMutationResult.CompletedFor(PackageCatalogSection.Pending));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(installEvent, "pending-bar install consumer");
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    Assert.AreEqual(0, table.GetSelectedRowsSnapshot().Count);
                    AssertBarEnabled(false);
                    Assert.IsTrue(pendingRoot.IsSelected, "実view terminalが既存の最後のpackageからrootへのplanを適用します。");
                    PresentPendingBarRows(vm, []);
                    AssertBarEnabled(false);
                    Assert.IsTrue(BindingOperations.IsDataBound(table, CustomTableView.ItemsSourceProperty));

                    PresentPendingBarRows(vm, rows, MainViewUpdateMode.FolderFilterSelected);
                    table.SelectRowsByPredicate(_ => true);
                    foreach (Button button in buttons) { ClickPendingBar(button); }
                    Assert.AreEqual(estimateCount, estimates.Count);
                    Assert.AreEqual(1, installs.Count);
                    Assert.AreEqual(1, clears.Count);
                }
                finally
                {
                    installCompletion.TrySetResult(PendingPackageMutationResult.Rejected);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(installEvent, "pending-bar install cleanup");
                }
                void AssertBarEnabled(bool enabled)
                {
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                    foreach (Button button in buttons) { Assert.AreEqual(enabled, button.IsEnabled); }
                }
            },
            pendingInstallEstimationTerminal: new MainWindowPendingInstallEstimationTerminal(
                (_, _) => Task.CompletedTask, request => { estimates.Add(request); return Task.CompletedTask; },
                request => { clears.Add(request); return Task.CompletedTask; }, _ => Task.CompletedTask),
            pendingInstallationTerminal: new MainWindowPendingInstallationTerminal(
                _ => { packageInstalls++; return Task.FromResult(PendingPackageMutationResult.Rejected); },
                _ => { packageInstalls++; return Task.FromResult(PendingPackageMutationResult.Rejected); },
                request => { installs.Add(request); return installCompletion.Task; }),
            installedLocationRepairTerminal: new MainWindowInstalledLocationRepairTerminal(
                _ => { repairs++; return Task.CompletedTask; }, _ => { repairs++; return Task.CompletedTask; }, _ => { repairs++; return Task.CompletedTask; }),
            installTreeTerminal: new MainWindowInstallTreeTerminal((_, _) => Task.FromResult(true)));
    }

    /// <summary>6言語の実切替で短縮名と正式tooltip/Automation名が更新され、Light/Darkの意味brushが実controlへ反映されます。</summary>
    /// <remarks>辞書のキー/非空/整合はLocalizationResourceParityTestsへ分担し、ここではResourceServiceとthemeからcompiled Bindingへの接続だけを確認します。</remarks>
    [TestMethod]
    public void PendingInstallBar_UpdatesLocalizedNamesAndThemeBrushes()
    {
        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            MainWindowViewModelTestFactory.CreateIsolatedSettings(s => s.StartupSelectInstallPending = false),
            (scope, vm, window) =>
            {
                CultureInfo? previousCulture = Resources.Culture;
                string previousTheme = Settings.Default.AppearanceTheme;
                MainWindowPresentationTestHarness.ShowCompiledContent(scope, vm, window);
                var bar = (Border)window.FindName("pendingInstallOperationBar");
                var table = (CustomTableView)window.FindName("customTableView");
                Button[] buttons = PendingBarButtons(window);
                PresentPendingBarRows(vm, [LibraryChartRow.FromPackageChartEntry(CreatePendingBarEntry("A"))]);
                table.SelectRowsByPredicate(_ => true);
                try
                {
                    foreach (string culture in new[] { "ja-JP", "en-US", "fr-FR", "ko-KR", "zh-CN", "zh-TW" })
                    {
                        ResourceService.Current.ChangeCulture(culture);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        string[] labels = [Resources.Pending_install_estimate, Resources.Install, Resources.Pending_install_clear];
                        string[] formalNames = [Resources.Estimate_install_loc, Resources.Install_to_estimation, Resources.Clear_estimation];
                        for (int i = 0; i < buttons.Length; i++)
                        {
                            Assert.AreEqual(labels[i], buttons[i].Content);
                            Assert.AreEqual(formalNames[i], buttons[i].ToolTip);
                            Assert.AreEqual(formalNames[i], AutomationProperties.GetName(buttons[i]));
                        }
                    }
                    foreach (string theme in new[] { AppThemeService.Light, AppThemeService.Dark })
                    {
                        AppThemeService.ApplyTheme(theme);
                        TestUiDispatcherHost.ProcessQueuedPresentation();
                        Assert.AreSame(bar.FindResource("App.SurfaceBrush"), bar.Background);
                        foreach (Button button in buttons)
                        {
                            Assert.AreSame(button.FindResource("App.ControlBackgroundBrush"), button.Background);
                            Assert.AreSame(button.FindResource("App.TextBrush"), button.Foreground);
                            Assert.AreSame(button.FindResource("App.StrongBorderBrush"), button.BorderBrush);
                        }
                    }
                }
                finally
                {
                    ResourceService.Current.ChangeCulture(previousCulture?.Name ?? "en-US");
                    Resources.Culture = previousCulture;
                    AppThemeService.ApplyTheme(previousTheme);
                    TestUiDispatcherHost.ProcessQueuedPresentation();
                }
            });
    }

    private static PackageChartEntry CreatePendingBarEntry(string name)
        => PackageChartEntry.FromChart(ChartTestValues.Empty() with
        {
            Path = @"C:\pending-bar\" + name + ".bms",
            Md5 = name.PadRight(32, 'a'),
            RawTitle = name
        });

    private static Button[] PendingBarButtons(MainWindow window)
        => [(Button)window.FindName("pendingInstallEstimateButton"), (Button)window.FindName("pendingInstallExecuteButton"), (Button)window.FindName("pendingInstallClearButton")];

    private static void PresentPendingBarRows(MainWindowViewModel vm, LibraryChartRow[] rows,
        MainViewUpdateMode mode = MainViewUpdateMode.PendingInstallFolderSelected)
    {
        vm.MainChartList.ApplyRows(new MainChartListRowsApplyRequest
        {
            Rows = rows,
            ColumnsSettings = new CustomTableColumnSettings(CustomTableColumnSettings.ViewKind.INSTALL),
            SelectionPolicy = MainChartListSelectionPolicy.Reset,
            Summary = MainChartListSummaryUpdate.Preserve(),
            Stopwatch = Stopwatch.StartNew(),
            OperationContextMode = mode
        });
        TestUiDispatcherHost.ProcessQueuedPresentation();
    }

    private static void ClickPendingBar(Button button)
    {
        Task completion = MainWindowPresentationTestHarness.CaptureAsyncEventCompletion(
            () => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button)));
        TestUiDispatcherHost.AwaitTaskOnDispatcher(completion, "pending-bar click consumer");
    }
}
