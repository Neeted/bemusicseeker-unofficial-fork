using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class MainWindowSelectedChartContextMenuWpfTests
{
    [TestMethod]
    public void CompiledTableContextMenuState_UsesRowFallbackAndPreservesSelectedBmson()
    {
        MainWindowScoreViewerTerminal scoreViewer = new(
            _ => true,
            _ => true,
            (_, _) => Task.FromResult<ScoreViewerRegistrationResult>(null),
            (_, _, _) => Task.FromResult<ScoreViewerRegistrationResult>(null));
        MainWindowSelectedChartContextMenuTerminals terminals = CreateTerminals(scoreViewer: scoreViewer);
        LibraryChartRow bmsRow = CreateChartRow(ChartFileKind.Bms, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", @"C:\wave6e-state\row.bms");
        LibraryChartRow bmsonRow = CreateChartRow(ChartFileKind.Bmson, "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB", @"C:\wave6e-state\row.bmson");

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { bmsRow, bmsonRow };
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                FrameworkElement placementTarget = new() { DataContext = bmsRow };
                menu.PlacementTarget = placementTarget;

                table.SelectRowsByPredicate(_ => false);
                OpenContextMenu(menu);

                MenuItem fixEncoding = FindMenuItem(menu, "tableContextMenuItemFixEncoding");
                Assert.AreEqual(Visibility.Visible, fixEncoding.Visibility);
                Assert.AreEqual(Visibility.Visible, FindMenuItem(menu, "tableContextMenuItemFullScanCheck").Visibility);

                table.SelectRowsByPredicate(row => ReferenceEquals(row, bmsonRow));
                OpenContextMenu(menu);

                Assert.AreEqual(Visibility.Collapsed, fixEncoding.Visibility);
                Assert.AreEqual(Visibility.Collapsed, FindMenuItem(menu, "tableContextMenuItemRegisterScore").Visibility);
                Assert.AreEqual(Resources.Open_chart_viewer, FindMenuItem(menu, "tableContextMenuItemRegisterScore").Header);
            },
            selectedChartContextMenuTerminals: terminals);
    }

    [TestMethod]
    public void CompiledChartContextMenuRoutesMutationAndResourceTerminalsWithExactTargets()
    {
        var renameCompletion = NewCompletion<SelectedChartMutationResult>();
        var deleteCompletion = NewCompletion<SelectedChartMutationResult>();
        var rescanCompletion = NewCompletion<SelectedChartResourceHealthWorkflowResult>();
        var audioCompletion = NewCompletion<SelectedChartAudioConversionResult>();
        SelectedInvalidExtensionRenameRequest renameRequest = null;
        SelectedChartDeleteRequest deleteRequest = null;
        SelectedChartEncodingRequest encodingRequest = null;
        ChartResourceHealthRequest rescanRequest = null;
        ChartResourceHealthRequest ignoredRequest = null;
        ChartResourceHealthRequest unignoredRequest = null;
        SelectedChartAudioConversionRequest audioRequest = null;
        LibraryChartRow row = CreateChartRow(ChartFileKind.Bms, "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC", typeof(MainWindow).Assembly.Location);

        MainWindowSelectedChartMutationTerminal mutation = new(
            request =>
            {
                deleteRequest = request;
                return deleteCompletion.Task;
            },
            request =>
            {
                renameRequest = request;
                return renameCompletion.Task;
            },
            _ => Task.FromResult(SelectedChartMutationResult.Completed),
            request =>
            {
                encodingRequest = request;
                return SelectedChartMutationResult.Completed;
            });
        MainWindowSelectedChartResourceHealthTerminal resourceHealth = new(
            request =>
            {
                rescanRequest = request;
                return rescanCompletion.Task;
            },
            (request, unset) =>
            {
                if (unset)
                {
                    unignoredRequest = request;
                }
                else
                {
                    ignoredRequest = request;
                }
                return SelectedChartResourceHealthWorkflowResult.Completed;
            });
        MainWindowSelectedChartAudioConversionTerminal audio = new(
            (request, _) =>
            {
                audioRequest = request;
                return audioCompletion.Task;
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { row };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                menu.PlacementTarget = new FrameworkElement { DataContext = row };

                MenuItem rename = FindMenuItem(menu, "tableContextMenuItemRenameInvalidExt");
                RoutedEventArgs renameArgs = RaiseMenuClick(rename);
                Assert.IsTrue(renameArgs.Handled);
                Assert.IsFalse(renameCompletion.Task.IsCompleted);
                Assert.AreEqual(1, renameRequest.Targets.Count);
                Assert.AreSame(row.Chart, renameRequest.Targets[0].Chart);
                Assert.IsFalse(renameRequest.IsPendingSelected);
                renameCompletion.SetResult(SelectedChartMutationResult.Completed);
                TestUiDispatcherHost.Drain();

                MenuItem deleteGroup = FindMenuItem(menu, "tableContextMenuItemDeleteFile");
                RoutedEventArgs deleteArgs = RaiseMenuClick((MenuItem)deleteGroup.Items[1]);
                Assert.IsTrue(deleteArgs.Handled);
                Assert.IsFalse(deleteCompletion.Task.IsCompleted);
                Assert.AreEqual(MainViewOperationSection.Library, deleteRequest.Section);
                Assert.AreEqual(1, deleteRequest.SelectedTargets.Count);
                Assert.AreSame(row.Chart, deleteRequest.SelectedTargets[0].Chart);
                Assert.AreSame(row.Chart, deleteRequest.ContextTarget.Chart);
                deleteCompletion.SetResult(SelectedChartMutationResult.Completed);
                TestUiDispatcherHost.Drain();

                MenuItem encoding = FindMenuItem(menu, "tableContextMenuItemFixEncoding").Items.OfType<MenuItem>().First();
                RoutedEventArgs encodingArgs = RaiseMenuClick(encoding);
                Assert.IsTrue(encodingArgs.Handled);
                Assert.AreEqual("shift_jis", encodingRequest.Encoding);
                Assert.AreEqual(1, encodingRequest.BmsFiles.Count);
                Assert.AreSame(row.Chart.GetBmsStorageOwner(), encodingRequest.BmsFiles[0]);

                MenuItem rescan = FindMenuItem(menu, "tableContextMenuItemFullScanCheck").Items
                    .OfType<MenuItem>()
                    .First(item => item.Name.Length == 0);
                RoutedEventArgs rescanArgs = RaiseMenuClick(rescan);
                Assert.IsTrue(rescanArgs.Handled);
                Assert.IsFalse(rescanCompletion.Task.IsCompleted);
                Assert.AreEqual(1, rescanRequest.Charts.Count);
                Assert.AreSame(row.Chart, rescanRequest.Charts[0]);
                rescanCompletion.SetResult(SelectedChartResourceHealthWorkflowResult.Completed);
                TestUiDispatcherHost.Drain();

                RoutedEventArgs ignoreArgs = RaiseMenuClick(FindMenuItem(menu, "tableContextMenuItemIgnoreFileScanCheck"));
                Assert.IsTrue(ignoreArgs.Handled);
                Assert.AreEqual(1, ignoredRequest.Charts.Count);
                Assert.AreSame(row.Chart, ignoredRequest.Charts[0]);

                RoutedEventArgs unignoreArgs = RaiseMenuClick(FindMenuItem(menu, "tableContextMenuItemNotIgnoreFileScanCheck"));
                Assert.IsTrue(unignoreArgs.Handled);
                Assert.AreEqual(1, unignoredRequest.Charts.Count);
                Assert.AreSame(row.Chart, unignoredRequest.Charts[0]);

                RoutedEventArgs audioArgs = RaiseMenuClick(FindMenuItem(menu, "tableContextMenuItemConvertToAudioFile"));
                Assert.IsTrue(audioArgs.Handled);
                Assert.IsFalse(audioCompletion.Task.IsCompleted);
                Assert.AreEqual(1, audioRequest.Targets.Count);
                Assert.AreSame(row.Chart, audioRequest.Targets[0].Chart);
                audioCompletion.SetResult(SelectedChartAudioConversionResult.Empty);
                TestUiDispatcherHost.Drain();
            },
            selectedChartContextMenuTerminals: CreateTerminals(
                mutation: mutation,
                resourceHealth: resourceHealth,
                audio: audio));
    }

    [TestMethod]
    public void CompiledParseFailureContextMenuNormalizesRequestAndHandlesOnlyAfterAcceptance()
    {
        var acceptance = NewCompletion<ChartInfoParseFailureRemovalAcceptance>();
        var completion = NewCompletion<ChartInfoParseFailureRemovalResult>();
        ChartInfoParseFailureRemovalRequest request = null;
        LibraryChartRow row = CreateChartRow(ChartFileKind.Bms, "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD", @"C:\wave6e-parse\chart.bms");
        MainWindowChartInfoParseFailureRemovalTerminal parseTerminal = new(
            capturedRequest =>
            {
                request = capturedRequest;
                return new ChartInfoParseFailureRemovalOperation(acceptance.Task, completion.Task);
            });

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { row };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.ChartInfoParseErrorFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                menu.PlacementTarget = new FrameworkElement { DataContext = row };
                RoutedEventArgs args = RaiseMenuClick(FindMenuItem(menu, "tableContextMenuItemRemoveChartInfoParseFailure"));

                Assert.IsFalse(args.Handled);
                CollectionAssert.AreEqual(
                    new[] { "dddddddddddddddddddddddddddddddd" },
                    request.Md5s.ToArray());
                Assert.IsFalse(completion.Task.IsCompleted);

                acceptance.SetResult(new ChartInfoParseFailureRemovalAcceptance(accepted: true));
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(args.Handled);
                Assert.IsFalse(completion.Task.IsCompleted);

                completion.SetResult(ChartInfoParseFailureRemovalResult.Failed(
                    new InvalidOperationException("parse failure"),
                    accepted: true));
                TestUiDispatcherHost.Drain();
            },
            selectedChartContextMenuTerminals: CreateTerminals(chartInfoParseFailureRemoval: parseTerminal));
    }

    [TestMethod]
    public void CompiledParseFailureContextMenuRejectsWithoutHandling()
    {
        var acceptance = NewCompletion<ChartInfoParseFailureRemovalAcceptance>();
        var completion = NewCompletion<ChartInfoParseFailureRemovalResult>();
        LibraryChartRow row = CreateChartRow(ChartFileKind.Bms, "EEEEEEEEEEEEEEEEEEEEEEEEEEEEEEEE", @"C:\wave6e-parse-reject\chart.bms");
        MainWindowChartInfoParseFailureRemovalTerminal parseTerminal = new(
            _ => new ChartInfoParseFailureRemovalOperation(acceptance.Task, completion.Task));

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { row };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.ChartInfoParseErrorFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                menu.PlacementTarget = new FrameworkElement { DataContext = row };
                RoutedEventArgs args = RaiseMenuClick(FindMenuItem(menu, "tableContextMenuItemRemoveChartInfoParseFailure"));

                acceptance.SetResult(new ChartInfoParseFailureRemovalAcceptance(accepted: false));
                TestUiDispatcherHost.Drain();
                Assert.IsFalse(args.Handled);
                Assert.IsFalse(completion.Task.IsCompleted);

                completion.SetResult(ChartInfoParseFailureRemovalResult.Rejected);
                TestUiDispatcherHost.Drain();
            },
            selectedChartContextMenuTerminals: CreateTerminals(chartInfoParseFailureRemoval: parseTerminal));
    }

    [TestMethod]
    public void CompiledRankingContextMenuUsesExactSelectionSnapshotAndOneRequest()
    {
        var availabilitySnapshots = new List<List<ChartOperationTarget>>();
        var requestSnapshots = new List<List<ChartOperationTarget>>();
        MainWindowRankingCacheTerminal ranking = new(
            targets =>
            {
                availabilitySnapshots.Add(targets.ToList());
                return true;
            },
            targets =>
            {
                availabilitySnapshots.Add(targets.ToList());
                return true;
            },
            targets =>
            {
                requestSnapshots.Add(targets.ToList());
                return true;
            });
        LibraryChartRow row = CreateChartRow(ChartFileKind.Bms, "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", @"C:\wave6e-ranking\chart.bms");

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { row };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                menu.PlacementTarget = new FrameworkElement { DataContext = row };
                OpenContextMenu(menu);

                MenuItem update = FindMenuItem(menu, "tableContextMenuItemUpdateRankingData");
                Assert.AreEqual(Visibility.Visible, update.Visibility);
                Assert.IsTrue(update.IsEnabled);
                RoutedEventArgs args = RaiseMenuClick(update);
                Assert.IsTrue(args.Handled);
                Assert.AreEqual(1, requestSnapshots.Count);
                Assert.AreEqual(1, requestSnapshots[0].Count);
                Assert.AreSame(row.Chart, requestSnapshots[0][0].Chart);
                Assert.IsTrue(availabilitySnapshots.All(snapshot => snapshot.Count == 1));
            },
            selectedChartContextMenuTerminals: CreateTerminals(rankingCache: ranking));
    }

    [TestMethod]
    public void CompiledScoreViewerContextMenusDelegateNormalAndMissingSelectionWithHandledTiming()
    {
        var completion = NewCompletion<ScoreViewerRegistrationResult>();
        var calls = new List<IReadOnlyList<ChartOperationTarget>>();
        MainWindowScoreViewerTerminal scoreViewer = new(
            _ => true,
            _ => true,
            (targets, _) =>
            {
                calls.Add(targets);
                return completion.Task;
            },
            (_, _, _) => Task.FromResult<ScoreViewerRegistrationResult>(null));
        LibraryChartRow normalRow = CreateChartRow(ChartFileKind.Bms, "11111111111111111111111111111111", @"C:\wave6e-score\chart.bms");
        PlaylistDetailRow missingRow = CreateMissingPlaylistRow("22222222222222222222222222222222");

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                ContextMenu normalMenu = (ContextMenu)window.FindResource("tableContextMenu");
                table.ItemsSource = new List<object> { normalRow };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                normalMenu.PlacementTarget = new FrameworkElement { DataContext = normalRow };
                OpenContextMenu(normalMenu);

                MenuItem normalRegister = FindMenuItem(normalMenu, "tableContextMenuItemRegisterScore");
                Assert.AreEqual(Resources.Open_chart_viewer, normalRegister.Header);
                Assert.AreEqual(Visibility.Visible, normalRegister.Visibility);
                Assert.IsTrue(normalRegister.IsEnabled);
                RoutedEventArgs normalArgs = RaiseMenuClick(normalRegister);
                Assert.IsTrue(normalArgs.Handled);
                Assert.IsFalse(completion.Task.IsCompleted);
                Assert.AreEqual(1, calls.Count);
                Assert.AreEqual(1, calls[0].Count);
                Assert.AreSame(normalRow.Chart, calls[0][0].Chart);
                completion.SetResult(null);
                TestUiDispatcherHost.Drain();

                table.ItemsSource = new List<object> { missingRow };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.PlaylistNotOwnedFilterSelected);
                ContextMenu missingMenu = (ContextMenu)window.FindResource("tableContextMenuPlaylistMissing");
                missingMenu.PlacementTarget = new FrameworkElement { DataContext = missingRow };
                OpenContextMenu(missingMenu);

                MenuItem missingRegister = FindMenuItem(missingMenu, "tableContextMenuItemRegisterScore");
                Assert.AreEqual(Visibility.Visible, missingRegister.Visibility);
                Assert.IsTrue(missingRegister.IsEnabled);
                RoutedEventArgs missingArgs = RaiseMenuClick(missingRegister);
                Assert.IsTrue(missingArgs.Handled);
                Assert.AreEqual(2, calls.Count);
                Assert.AreEqual(1, calls[1].Count);
                Assert.AreSame(missingRow.Chart, calls[1][0].Chart);
            },
            selectedChartContextMenuTerminals: CreateTerminals(scoreViewer: scoreViewer));
    }

    [TestMethod]
    public void CompiledSelectedChartExternalActionsUseEligibilityAndExactRowTargets()
    {
        var legacyCalls = new List<(ChartOperationTarget Target, SelectedChartExternalActionKind Action)>();
        var configuredCalls = new List<(string Md5, ConfiguredExternalActionKind Kind, string Id)>();
        var createdTargets = new List<ChartOperationTarget>();
        bool eligible = true;
        string md5 = "33333333333333333333333333333333";
        string chartPath = @"C:\wave6e-external\chart.bms";
        string[] configuredIds = ["first-web", "second-web", "third-web"];
        RightClickActionResolution configuredResolution = new(
            configuredIds.Select(id => new ResolvedRightClickWebAction(
                new RightClickWebActionDefinition(
                    id,
                    id,
                    "https://example.test/" + id + "/{md5}",
                    enabled: true,
                    ExternalChartKind.BmsOnly),
                "https://example.test/" + id + "/" + md5)),
            [new ResolvedRightClickProgramAction(
                new RightClickProgramActionDefinition(
                    "program",
                    "Player",
                    @"C:\Tools\player.exe",
                    "{filePath}",
                    enabled: true),
                [chartPath])]);
        MainWindowSelectedChartExternalActionsTerminal external = new(
            (_, action) => eligible
                && (action is SelectedChartExternalActionKind.OpenExplorer or SelectedChartExternalActionKind.OpenFile),
            (target, action) => legacyCalls.Add((target, action)),
            _ => false,
            (_, _) => Task.FromResult(RelatedDocumentQueryReceipt.Unavailable),
            _ => { },
            createResolutionInput: target =>
            {
                createdTargets.Add(target);
                return new RightClickActionResolutionInput(
                    target.Chart.Md5,
                    null,
                    target.Chart.Path,
                    ExternalChartKind.BmsOnly);
            },
            resolveConfiguredActions: _ => configuredResolution,
            executeConfiguredAction: (input, actionKind, actionId) =>
            {
                if (!eligible)
                {
                    return ExternalConfiguredActionResult.Failure(
                        ExternalConfiguredActionFailureKind.ActionUnavailable,
                        Resources.RightClick_external_action_unavailable);
                }
                configuredCalls.Add((input.Md5, actionKind, actionId));
                return ExternalConfiguredActionResult.Success;
            },
            getDisplayName: action => action.Name);
        LibraryChartRow row = CreateChartRow(ChartFileKind.Bms, md5, chartPath);

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { row };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                menu.PlacementTarget = new FrameworkElement { DataContext = row };
                OpenContextMenu(menu);

                var expected = new[]
                {
                    ("tableContextMenuItemOpenExplorer", SelectedChartExternalActionKind.OpenExplorer),
                    ("tableContextMenuItemOpenBMSFile", SelectedChartExternalActionKind.OpenFile)
                };
                foreach ((string name, SelectedChartExternalActionKind action) in expected)
                {
                    RoutedEventArgs args = RaiseMenuClick(FindMenuItem(menu, name));
                    Assert.IsTrue(args.Handled);
                }

                Assert.AreEqual(expected.Length, legacyCalls.Count);
                for (int index = 0; index < expected.Length; index++)
                {
                    Assert.AreSame(row.Chart, legacyCalls[index].Target.Chart);
                    Assert.AreEqual(expected[index].Item2, legacyCalls[index].Action);
                }

                foreach (string id in configuredIds)
                {
                    RoutedEventArgs args = RaiseMenuClick(FindMenuItem(menu, "configuredWebAction_" + id.Replace("-", "_", StringComparison.Ordinal)));
                    Assert.IsTrue(args.Handled);
                }
                MenuItem programParent = FindMenuItem(menu, "tableContextMenuItemOpenProgramActions");
                Assert.AreEqual(
                    menu.Items.IndexOf(FindMenuItem(menu, "tableContextMenuItemOpenBMSFile")) + 1,
                    menu.Items.IndexOf(programParent));
                Assert.AreEqual(Resources.RightClick_open_with_program, programParent.Header);
                RoutedEventArgs programArgs = RaiseMenuClick(FindMenuItem(programParent, "configuredProgramAction_program"));
                Assert.IsTrue(programArgs.Handled);

                Assert.AreEqual(configuredIds.Length + 1, configuredCalls.Count);
                CollectionAssert.AreEqual(
                    configuredIds,
                    configuredCalls.Where(call => call.Kind == ConfiguredExternalActionKind.Web).Select(call => call.Id).ToArray());
                Assert.IsTrue(configuredCalls.All(call => call.Md5 == md5));
                Assert.AreEqual(ConfiguredExternalActionKind.Program, configuredCalls[^1].Kind);
                Assert.AreEqual("program", configuredCalls[^1].Id);
                Assert.IsTrue(createdTargets.All(target => ReferenceEquals(target.Chart, row.Chart)));

                eligible = false;
                int callCountBeforeRejected = configuredCalls.Count;
                RoutedEventArgs rejectedArgs = RaiseMenuClick(FindMenuItem(menu, "configuredWebAction_first_web"));
                Assert.IsTrue(rejectedArgs.Handled);
                Assert.AreEqual(callCountBeforeRejected, configuredCalls.Count);
            },
            selectedChartContextMenuTerminals: CreateTerminals(selectedChartExternalActions: external));
    }

    [TestMethod]
    public void CompiledRelatedDocumentMenuSuppressesStaleResultsAndOpensGeneratedPath()
    {
        var queryCompletions = new List<TaskCompletionSource<RelatedDocumentQueryReceipt>>();
        var queryTokens = new List<CancellationToken>();
        var openedPaths = new List<string>();
        MainWindowSelectedChartExternalActionsTerminal external = new(
            (_, _) => false,
            (_, _) => { },
            _ => true,
            (target, token) =>
            {
                queryTokens.Add(token);
                var completion = NewCompletion<RelatedDocumentQueryReceipt>();
                queryCompletions.Add(completion);
                return completion.Task;
            },
            path => openedPaths.Add(path));
        LibraryChartRow row = CreateChartRow(ChartFileKind.Bms, "44444444444444444444444444444444", @"C:\wave6e-related\chart.bms");

        MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
            new Settings(),
            (viewModel, window) =>
            {
                CustomTableView table = (CustomTableView)window.FindName("customTableView");
                table.ItemsSource = new List<object> { row };
                table.SelectRowsByPredicate(_ => true);
                viewModel.MainChartList.SetOperationContext(MainViewUpdateMode.FolderFilterSelected);
                ContextMenu menu = (ContextMenu)window.FindResource("tableContextMenu");
                menu.PlacementTarget = new FrameworkElement { DataContext = row };

                OpenContextMenu(menu);
                Assert.AreEqual(1, queryCompletions.Count);
                OpenContextMenu(menu);
                Assert.AreEqual(2, queryCompletions.Count);
                Assert.IsTrue(queryTokens[0].IsCancellationRequested);

                queryCompletions[0].SetResult(RelatedDocumentQueryReceipt.Available(["stale.txt"]));
                TestUiDispatcherHost.Drain();
                MenuItem document = FindMenuItem(menu, "tableContextMenuItemOpenDocument");
                Assert.IsNull(document.ItemsSource);
                Assert.IsFalse(document.IsEnabled);

                queryCompletions[1].SetResult(RelatedDocumentQueryReceipt.Available(["current.txt"]));
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Visible, document.Visibility);
                Assert.IsTrue(document.IsEnabled);
                Assert.AreEqual("current.txt", document.Items.Cast<object>().Single());

                document.ApplyTemplate();
                document.Measure(new Size(400d, 200d));
                document.Arrange(new Rect(0d, 0d, 400d, 200d));
                document.UpdateLayout();
                MenuItem generated = document.ItemContainerGenerator.ContainerFromIndex(0) as MenuItem;
                if (generated == null)
                {
                    IItemContainerGenerator generator = document.ItemContainerGenerator;
                    using (generator.StartAt(
                        generator.GeneratorPositionFromIndex(0),
                        GeneratorDirection.Forward,
                        allowStartAtRealizedItem: true))
                    {
                        DependencyObject candidate = generator.GenerateNext(out bool newlyRealized);
                        if (newlyRealized)
                        {
                            generator.PrepareItemContainer(candidate);
                        }
                    }
                    generated = document.ItemContainerGenerator.ContainerFromIndex(0) as MenuItem;
                }
                Assert.IsNotNull(
                    generated,
                    $"The related-document item must be generated by the compiled ItemsSource route (items={document.Items.Count}, status={document.ItemContainerGenerator.Status}, style={document.ItemContainerStyle != null}, parent={document.Parent?.GetType().Name ?? "none"}).");
                RoutedEventArgs generatedArgs = RaiseMenuClick(generated);
                Assert.IsTrue(generatedArgs.Handled);
                CollectionAssert.AreEqual(new[] { "current.txt" }, openedPaths);

                OpenContextMenu(menu);
                queryCompletions[2].SetResult(RelatedDocumentQueryReceipt.Unavailable);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Collapsed, document.Visibility);

                OpenContextMenu(menu);
                queryCompletions[3].SetResult(RelatedDocumentQueryReceipt.Failed);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(Visibility.Collapsed, document.Visibility);

                OpenContextMenu(menu);
                queryCompletions[4].SetResult(RelatedDocumentQueryReceipt.Canceled);
                TestUiDispatcherHost.Drain();
                Assert.IsFalse(document.IsEnabled);
            },
            selectedChartContextMenuTerminals: CreateTerminals(selectedChartExternalActions: external));
    }

    private static MainWindowSelectedChartContextMenuTerminals CreateTerminals(
        MainWindowSelectedChartMutationTerminal? mutation = null,
        MainWindowSelectedChartResourceHealthTerminal? resourceHealth = null,
        MainWindowSelectedChartAudioConversionTerminal? audio = null,
        MainWindowChartInfoParseFailureRemovalTerminal? chartInfoParseFailureRemoval = null,
        MainWindowRankingCacheTerminal? rankingCache = null,
        MainWindowScoreViewerTerminal? scoreViewer = null,
        MainWindowSelectedChartExternalActionsTerminal? selectedChartExternalActions = null)
    {
        return new MainWindowSelectedChartContextMenuTerminals(
            mutation ?? new MainWindowSelectedChartMutationTerminal(
                _ => Task.FromResult(SelectedChartMutationResult.Completed),
                _ => Task.FromResult(SelectedChartMutationResult.Completed),
                _ => Task.FromResult(SelectedChartMutationResult.Completed),
                _ => SelectedChartMutationResult.Completed),
            resourceHealth ?? new MainWindowSelectedChartResourceHealthTerminal(
                _ => Task.FromResult(SelectedChartResourceHealthWorkflowResult.Completed),
                (_, _) => SelectedChartResourceHealthWorkflowResult.Completed),
            audio ?? new MainWindowSelectedChartAudioConversionTerminal(
                (_, _) => Task.FromResult(SelectedChartAudioConversionResult.Empty)),
            chartInfoParseFailureRemoval ?? new MainWindowChartInfoParseFailureRemovalTerminal(
                _ => new ChartInfoParseFailureRemovalOperation(
                    Task.FromResult(new ChartInfoParseFailureRemovalAcceptance(accepted: false)),
                    Task.FromResult(ChartInfoParseFailureRemovalResult.NotStarted))),
            rankingCache ?? new MainWindowRankingCacheTerminal(
                _ => false,
                _ => false,
                _ => false),
            scoreViewer ?? new MainWindowScoreViewerTerminal(
                _ => false,
                _ => false,
                (_, _) => Task.FromResult<ScoreViewerRegistrationResult>(null),
                (_, _, _) => Task.FromResult<ScoreViewerRegistrationResult>(null)),
            selectedChartExternalActions ?? new MainWindowSelectedChartExternalActionsTerminal(
                (_, _) => false,
                (_, _) => { },
                _ => false,
                (_, _) => Task.FromResult(RelatedDocumentQueryReceipt.Unavailable),
                _ => { }));
    }

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void OpenContextMenu(ContextMenu menu)
    {
        menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent, menu));
        TestUiDispatcherHost.Drain();
    }

    private static RoutedEventArgs RaiseMenuClick(MenuItem item)
    {
        var args = new RoutedEventArgs(MenuItem.ClickEvent, item);
        item.RaiseEvent(args);
        return args;
    }

    private static MenuItem FindMenuItem(ItemsControl root, string name)
    {
        MenuItem item = EnumerateMenuItems(root).FirstOrDefault(candidate => candidate.Name == name);
        if (item != null)
        {
            return item;
        }
        string availableNames = string.Join(", ", EnumerateMenuItems(root).Select(candidate => candidate.Name ?? "<null>"));
        throw new AssertFailedException($"Menu item '{name}' was not found. Menu items: {availableNames}");
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(ItemsControl root)
    {
        foreach (MenuItem item in root.Items.OfType<MenuItem>())
        {
            yield return item;
            foreach (MenuItem nested in EnumerateMenuItems(item))
            {
                yield return nested;
            }
        }
    }

    private static LibraryChartRow CreateChartRow(ChartFileKind kind, string md5, string path)
    {
        BMSFile bmsFile = kind == ChartFileKind.Bms
            ? new BMSFile
            {
                path = path,
                hash = md5,
                title = "Context chart"
            }
            : null;
        LR2SongDBExtended.bmson_song bmsonSong = kind == ChartFileKind.Bmson
            ? new LR2SongDBExtended.bmson_song { path = path }
            : null;
        ChartFile chart = new(
            kind,
            path,
            md5,
            new string('a', 64),
            "Context chart",
            "Context chart",
            "Artist",
            string.Empty,
            "Charts",
            string.Empty,
            "7",
            7,
            1,
            null,
            bmsFile,
            bmsonSong);
        return LibraryChartRow.FromChartFile(chart);
    }

    private static PlaylistDetailRow CreateMissingPlaylistRow(string md5)
    {
        BMSTableEntry entry = BMSTableEntry.CreateHydratedPlaylistEntry(
            playlistId: 1,
            md5Value: md5,
            sha256Value: new string('b', 64),
            levelValue: null,
            titleValue: "Missing chart",
            artistValue: "Artist",
            folderValue: string.Empty,
            lr2BmsIdValue: string.Empty,
            urlValue: string.Empty,
            urlDiffValue: string.Empty,
            nameDiffValue: string.Empty,
            orgMd5Value: string.Empty,
            addDateValue: null,
            commentValue: string.Empty,
            memoValue: string.Empty,
            isRemovedValue: false);
        ChartFile chart = new(
            ChartFileKind.Bms,
            path: null,
            md5,
            new string('b', 64),
            "Missing chart",
            "Missing chart",
            "Artist",
            string.Empty,
            "Charts",
            string.Empty,
            "7",
            7,
            1,
            null,
            null,
            null);
        return new PlaylistDetailSourceRow(entry, chart).CreateViewRow();
    }
}
