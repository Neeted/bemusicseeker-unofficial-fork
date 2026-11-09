using System.IO;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;
using static BeMusicSeeker.TestSupport.Components.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DuplicateMaintenanceWorkflowOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>実在親folderのHash整理で登録子rootだけを削除し、cleanup中の親L保持と解放後の実保留通知を確認します。Pは取得しません。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void RunHashCleanupAsync_RootPendingPublicationWaitsForActualLibraryRelease()
    {
        WithTemporarySongDb(songDbPath => TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string parent = Directory.CreateDirectory(Path.Combine(root, "Parent")).FullName;
            string child = Directory.CreateDirectory(Path.Combine(parent, "Child")).FullName;
            Settings settings = TestBmsFactory.CreateRootSettings([parent, child]);
            settings.BMSInstallDir = parent;
            ChartFile keeper = CreateFile(new string('a', 32), Path.Combine(parent, "a.bms"));
            ChartFile duplicate = CreateFile(new string('a', 32), Path.Combine(child, "long-duplicate.bms"));
            foreach (ChartFile chart in new[] { keeper, duplicate }) { File.WriteAllText(chart.Path, "#TITLE Duplicate\n#BPM 120\n"); }
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher), () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), settings: settings)
            { BmsCharts = [keeper, duplicate], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs([keeper, duplicate]);
            using var observation = RootPendingPublicationObservation.Seed(library, songDbPath, parent, Path.Combine(root, "Unrelated"));
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = request => Task.FromResult(UiDialogResult.FromMessageBoxResult(
                    request.Button == MessageBoxButton.YesNoCancel ? MessageBoxResult.Yes : MessageBoxResult.OK))
            };
            var activity = new ChartMutationActivityOwner();
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, library.OperationAdmission, activity,
                new RecordingPresentation([]), dialogs, () => true, Directory.Exists, _ => new ExplorerOpenResult(),
                _ => null, new BmsLibraryDuplicateMaintenanceStore());
            var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCleanup = new ManualResetEventSlim();
            void HoldActualCleanup(object? sender, EventArgs args)
            {
                if (!activity.IsActive) { cleanupReached.TrySetResult(); releaseCleanup.Wait(); }
            }
            activity.ActivityChanged += HoldActualCleanup;
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                operation = owner.RunHashCleanupAsync(new DuplicateGroup([keeper, duplicate], [parent, child]), parent);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(cleanupReached.Task, operation, "root-hash-cleanup"), "root-hash-cleanup");
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.AreEqual(0, observation.Count);
                releaseCleanup.Set();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "root-hash-terminal");
                DuplicateMaintenanceMutationResult result = operation.GetAwaiter().GetResult();
                Assert.IsTrue(result.Succeeded, result.Failure?.ToString());
                Assert.AreEqual(1, result.RemovedChartCount);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(TestUiDispatcherHost.AwaitNotificationAsync(observation.Received, operation, "root-hash-publication"), "root-hash-publication");
                observation.AssertPublishedAfterParentRelease();
                Assert.IsFalse(Directory.Exists(child));
                Assert.IsTrue(File.Exists(keeper.Path));
                CollectionAssert.AreEqual(new[] { parent }, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                Assert.AreEqual(keeper.Path, library.BmsCharts.Single().Path);
                using LR2SongDBExtended db = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(keeper.Path, db.Table<LR2SongDB.song>().Single().path);
            }
            finally
            {
                releaseCleanup.Set();
                try { if (operation != null) { TestUiDispatcherHost.AwaitTaskOnDispatcher(operation, "root-hash-recovery"); } }
                finally
                {
                    activity.ActivityChanged -= HoldActualCleanup;
                    library.RequestStop("root-hash-publication-test");
                }
            }
        }));
    }

    /// <summary>空の登録子を含む統合元の拒否を本番storeへ通し、停止後も実体とDBを保ち、終端後の通常統合を新規に受け付けます。</summary>
    /// <remarks>判断・物理処理・SQLiteは実装を使い、再生・確認・表示だけを既存portで記録します。</remarks>
    [TestMethod]
    public async Task RunFolderMergeAsync_RegisteredScopeRefusalReleasesAdmissionForNextMerge()
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string source = Path.Combine(root, "Source");
            string empty = Path.Combine(source, "EmptyRegistered");
            string destination = Path.Combine(root, "Destination");
            string normal = Path.Combine(root, "Normal");
            ChartFile[] charts = [LibraryResourceIndexTestSupport.CreateChart(source, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(destination, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(normal, "same.bms"), LibraryResourceIndexTestSupport.CreateChart(normal, "unique.bms")];
            Directory.CreateDirectory(empty);
            File.WriteAllText(Path.Combine(source, "kept.wav"), "protected audio");
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs(),
                settings: TestBmsFactory.CreateRootSettings([root, empty]))
            { BmsCharts = charts };
            var gateway = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath);
            gateway.UpsertSongs(charts);
            var events = new List<string>();
            var presentation = new RecordingPresentation(events);
            var activity = new ChartMutationActivityOwner();
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, library.OperationAdmission, activity, presentation,
                AcceptedDialogs(), () => true, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore());
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            try
            {
                DuplicateMaintenanceMutationResult refused = await owner.RunFolderMergeAsync(source, destination, new DuplicateGroup(charts.ToList(), [source, destination]));
                Assert.IsFalse(refused.Succeeded);
                Assert.IsNotNull(refused.MutationReceipt);
                Assert.IsFalse(refused.MutationReceipt.MergeApplied);
                Assert.IsTrue(events.Contains("playback-stop"));
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(Directory.Exists(empty));
                Assert.AreEqual("protected audio", File.ReadAllText(Path.Combine(source, "kept.wav")));
                foreach (ChartFile chart in charts) { Assert.IsTrue(File.Exists(chart.Path)); }
                using (LR2SongDBExtended db = gateway.OpenSongDbReadOnly()) { Assert.AreEqual(charts.Length, db.Table<LR2SongDB.song>().Count()); }
                DuplicateMaintenanceMutationResult accepted = await owner.RunFolderMergeAsync(normal, destination, new DuplicateGroup(charts.ToList(), [normal, destination]));
                Assert.IsTrue(accepted.Succeeded, accepted.Failure?.ToString());
                Assert.IsFalse(Directory.Exists(normal));
                Assert.IsTrue(File.Exists(Path.Combine(destination, "unique.bms")));
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsFalse(activity.IsActive);
            }
            finally
            {
                owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                activity.ActivityChanged -= presentation.OnActivityChanged;
                library.RequestShutdown("registered-merge-workflow");
            }
        });
    }

    /// <summary>明示保守の必要な準備が失敗しても、統合とHash削除それぞれの先行確定・元失敗を結果へ残します。実ライブラリの接続で同じ存続権限と必要指定を確認し、準備終端まで親Lを保持して最後に回収します。</summary>
    [TestMethod]
    [DataRow("merge")]
    [DataRow("hash")]
    public async Task ExplicitMaintenance_RequiredPreparationFailurePreservesFactsAndReleasesAdmission(string operationKind)
    {
        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new AssertFailedException();
            ChartFile keeper = CreateChart(Path.Combine(root, "a.bms"), "same-hash");
            ChartFile duplicate = CreateChart(Path.Combine(root, "duplicate.bms"), "same-hash");
            File.WriteAllText(keeper.Path, "same content");
            File.WriteAllText(duplicate.Path, "same content");
            FakeUiDialogService dialogs = AcceptedDialogs();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new FileDbReportRecordingDialogs(),
                settings: TestBmsFactory.CreateRootSettings([root]))
            { BmsCharts = [keeper, duplicate] };
            var originalFailure = new IOException("required original finalization failure");
            var preparationFailure = new IOException("required duplicate preparation failure");
            var session = new LibraryMutationSessionReceipt([new LibraryMutationSessionTarget(duplicate.Path, keeper.Path)],
                durableCommit: true, finalizationFailure: originalFailure);
            var mergeReceipt = new DuplicateMergeMaintenanceReceipt(true, ResourceHealthIndexUpdateMode.DeferOnUpdates,
                MaintenanceWorkflowResultFacts.From(null), false, false, false, false, false, session);
            var removal = new LibraryChartRemovalOutcome([new(duplicate.Path, LibraryChartRemovalState.Confirmed)], true, true, originalFailure);
            var events = new List<string>();
            var store = new TerminalRecordingStore(events, mergeReceipt) { RemovalOutcome = removal };
            var activity = new ChartMutationActivityOwner();
            var presentation = new RecordingPresentation(events);
            var selection = DuplicateViewContext.ForGroup(Path.Combine(root, "next"));
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, library.OperationAdmission, activity, presentation,
                dialogs, () => false, _ => true, _ => new ExplorerOpenResult(), _ => selection, store);
            var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Func<BMSLibrary, LibraryFileMutationCapability, bool, Task> prepare = (source, capability, requireGroups) =>
            {
                Assert.AreSame(library, source);
                Assert.IsTrue(requireGroups, "明示保守は現在画面によらず成功後選択の準備を指定します。");
                using (source.OperationAdmission.Borrow(capability)) { }
                Assert.IsTrue(source.OperationAdmission.IsActive);
                reached.TrySetResult();
                return CompletePreparationAsync();
            };
            async Task CompletePreparationAsync()
            {
                await release.Task;
                throw preparationFailure;
            }
            library.ConnectDuplicateDataPreparation(prepare);
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                var group = new DuplicateGroup([keeper, duplicate], [root, Path.Combine(root, "destination")]);
                operation = operationKind == "merge"
                    ? owner.RunFolderMergeAsync(root, Path.Combine(root, "destination"), group)
                    : owner.RunHashCleanupAsync(group, root);
                Assert.AreSame(reached.Task, await Task.WhenAny(reached.Task, operation));
                await reached.Task;
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(library.OperationAdmission.IsActive);
                Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected));
                Assert.IsNull(rejected);
                release.TrySetResult();
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.IsFalse(result.Succeeded);
                Assert.IsTrue(result.HasDurableCommit);
                Assert.IsTrue(result.HasDurableFinalizationFailure);
                Assert.AreSame(selection, result.SelectionContext);
                Assert.IsInstanceOfType(result.Failure, typeof(AggregateException));
                CollectionAssert.AreEquivalent(new Exception[] { originalFailure, preparationFailure },
                    ((AggregateException)result.Failure).Flatten().InnerExceptions.ToArray());
                if (operationKind == "merge")
                {
                    Assert.AreSame(mergeReceipt, result.MutationReceipt);
                    Assert.AreEqual(1, store.MergeWithReceiptCallCount);
                }
                else
                {
                    Assert.AreSame(removal, result.RemovalOutcome);
                    Assert.AreEqual(1, result.RemovedChartCount);
                    CollectionAssert.AreEqual(new[] { duplicate.Path }, store.Charts.Select(chart => chart.Path).ToArray());
                }
                Assert.IsFalse(activity.IsActive);
                Assert.IsFalse(library.OperationAdmission.IsActive);
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable next));
                next.Dispose();
            }
            finally
            {
                release.TrySetResult();
                try { if (operation != null) { await operation; } }
                finally
                {
                    library.DisconnectDuplicateDataPreparation(prepare);
                    library.RequestShutdown("explicit-maintenance-required-preparation");
                }
            }
        });
    }

    /// <summary>
    /// 実モデルで確認前に固定した重複対象を使い、確認中のBusy拒否から実削除・片付け終端まで共通権限を保持して対象再計算や早期解放を検出する。
    /// </summary>
    /// <remarks>合成した祖先groupのprepared応答プロトコルを実store・小SQLiteへ接続する。通常UIのwhole-folder到達は保証せず、実解析のkeeper保持は別代表が担当する。</remarks>
    [TestMethod]
    public async Task RunHashCleanupAsync_RealPreparedTargetsKeepGateAndModelAdmission()
    {

        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string folder = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException(), "Pack");
            Directory.CreateDirectory(folder);
            string keeperPath = Path.Combine(folder, "a.bms");
            string duplicatePath = Path.Combine(folder, "long-name.bms");
            File.WriteAllText(keeperPath, "#PLAYER 1");
            File.WriteAllText(duplicatePath, "#PLAYER 1");
            string configPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "user.config");
            Settings settings = TestSettingsFactory.OpenSettings(configPath);
            settings.OperationModeLR2DB = false; settings.StandaloneBmsRootPaths = StandaloneBmsRootPathSettings.Serialize([folder]);
            settings.BMSRootPath = folder; settings.BMSInstallDir = folder; settings.Save();
            byte[] registeredConfig = File.ReadAllBytes(configPath);
            string resource = Path.Combine(folder, "user.wav"); File.WriteAllText(resource, "keep resource");
            var filesystem = new TestFileMutationService();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, filesystem,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => BmsLibraryOptionsSnapshot.CreateCurrent(settings), settings: settings)
            { BmsCharts = [CreateFile(new string('a', 32), keeperPath), CreateFile(new string('a', 32), duplicatePath)], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(library.BmsCharts);
            ChartFile[] selected = library.BmsCharts.ToArray();
            var group = new DuplicateGroup([.. selected], [folder]);
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int confirmationCount = 0;
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = _ =>
                {
                    confirmationCount++;
                    reached.TrySetResult(true);
                    return confirmation.Task;
                }
            };
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var events = new List<string>();
            var presentation = new RecordingPresentation(events);
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity, presentation, dialogs,
                () => true, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), new BmsLibraryDuplicateMaintenanceStore());
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseCleanup = new ManualResetEventSlim();
            activity.ActivityChanged += HoldActualCleanup;
            void HoldActualCleanup(object? sender, EventArgs args)
            {
                if (!activity.IsActive)
                {
                    cleanupReached.TrySetResult();
                    releaseCleanup.Wait();
                }
            }
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                operation = owner.RunHashCleanupAsync(group, folder);
                Task arrived = await Task.WhenAny(reached.Task, operation);
                Assert.AreSame(reached.Task, arrived, "確認到達前にTaskが終端しました。");
                await reached.Task;
                Assert.IsTrue(gate.IsActive);
                Assert.IsFalse(gate.TryEnter(out IDisposable concurrent));
                concurrent?.Dispose();
                DuplicateMaintenanceMutationResult rejected = await owner.RunHashCleanupAsync(group, folder);
                Assert.IsFalse(rejected.Succeeded);
                Assert.AreEqual(0, rejected.RemovedChartCount);
                Assert.AreEqual(0, filesystem.FileDeleteCalls);
                using (LibraryFileMutationLease rejectedModel = library.Lr2Synchronization.TryBeginMutation("prepared_cleanup_model_guard"))
                { Assert.IsNull(rejectedModel, "確認中の別モデル変更も副作用前Busyです。"); }
                Assert.AreEqual(1, confirmationCount);
                Assert.AreEqual(0, events.Count, "拒否要求は追加確認や再生停止を始めません。");
                confirmation.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                await Task.WhenAny(cleanupReached.Task, operation);
                if (!cleanupReached.Task.IsCompleted) { await operation; Assert.Fail("実削除後のcleanupへ到達しませんでした。"); }
                Assert.IsFalse(operation.IsCompleted);
                Assert.IsTrue(gate.IsActive, "実store返却後もscope回収の終端まで受付を保持します。");
                Assert.IsFalse(gate.TryEnter(out IDisposable duringCleanup));
                duringCleanup?.Dispose();
                using (LibraryFileMutationLease cleanupProbe = library.Lr2Synchronization.TryBeginMutation("prepared_cleanup_terminal_guard"))
                { Assert.IsNull(cleanupProbe); }
                Assert.AreEqual(1, filesystem.FileDeleteCalls);
                Assert.IsTrue(Directory.Exists(folder));
                Assert.AreEqual("keep resource", File.ReadAllText(resource));
                CollectionAssert.AreEqual(registeredConfig, File.ReadAllBytes(configPath));
                CollectionAssert.AreEqual(new[] { folder }, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                Assert.AreEqual(0, filesystem.DirectoryDeleteCalls);
                Assert.AreEqual(0, filesystem.FileMoveCalls);
                Assert.IsTrue(File.Exists(keeperPath));
                Assert.IsFalse(File.Exists(duplicatePath));
                Assert.AreEqual(keeperPath, library.BmsCharts.Single().Path);
                using (LR2SongDBExtended readback = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly())
                { Assert.AreEqual(keeperPath, readback.Table<LR2SongDB.song>().Single().path); }
                releaseCleanup.Set();
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.IsTrue(result.Succeeded);
                Assert.IsNull(result.Failure);
                Assert.AreEqual(1, result.RemovedChartCount);
                Assert.IsNotNull(result.RemovalOutcome);
                Assert.AreEqual(1, result.RemovalOutcome.ConfirmedChartCount);
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.AreEqual(1, confirmationCount, "拒否した要求は終端後にも再実行しません。");
                Assert.AreEqual(1, filesystem.FileDeleteCalls);
                Assert.IsTrue(Directory.Exists(folder));
                Assert.AreEqual("keep resource", File.ReadAllText(resource));
                CollectionAssert.AreEqual(registeredConfig, File.ReadAllBytes(configPath));
                CollectionAssert.AreEqual(new[] { folder }, new BmsRootRegistrationStore(settings, () => null).CaptureUserRoots().ToArray());
                Assert.IsTrue(gate.TryEnter(out IDisposable probe));
                probe.Dispose();
                using LibraryFileMutationLease next = library.Lr2Synchronization.TryBeginMutation("prepared_cleanup_released");
                Assert.IsNotNull(next);
            }
            finally
            {
                releaseCleanup.Set();
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    activity.ActivityChanged -= HoldActualCleanup;
                    library.RequestShutdown("prepared-duplicate-test");
                }
            }
        });
    }

    /// <summary>
    /// 固定した全フォルダ候補のYes・No・未選択閉鎖を変更前に判断し、承認範囲だけ実削除して所持行を保存し、判断中の受付解放や未承認資源の削除を検出する。
    /// </summary>
    /// <remarks>合成した祖先groupのprepared応答プロトコルを実store・小SQLiteへ接続する。通常UIのwhole-folder到達は保証せず、実解析のkeeper保持は別代表が担当する。</remarks>
    // 0は件数確認なし・フォルダYes、1は件数確認あり・フォルダNo、2は件数確認あり・未選択閉鎖。
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task RunHashCleanupAsync_WholeFolderDecisionUsesPreparedRealStore(int decision)
    {

        await WithTemporarySongDbAsync(async songDbPath =>
        {
            string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException();
            string keeperFolder = Path.Combine(root, "Keep");
            string deletionFolder = Path.Combine(root, "Delete");
            Directory.CreateDirectory(keeperFolder);
            Directory.CreateDirectory(deletionFolder);
            string keeperPath = Path.Combine(keeperFolder, "a.bms");
            string deletionPath = Path.Combine(deletionFolder, "long-name.bms");
            string resourcePath = Path.Combine(deletionFolder, "user.wav");
            File.WriteAllText(keeperPath, "#PLAYER 1");
            File.WriteAllText(deletionPath, "#PLAYER 1");
            File.WriteAllText(resourcePath, "user resource");
            DateTime fixedDate = new(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(keeperPath, fixedDate);
            File.SetLastWriteTimeUtc(deletionPath, fixedDate);
            var filesystem = new TestFileMutationService();
            var modelDialogs = new FileDbReportRecordingDialogs();
            var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, filesystem, modelDialogs,
                new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
                () => new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false })
            { BmsCharts = [CreateFile(new string('a', 32), keeperPath), CreateFile(new string('a', 32), deletionPath)], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs(library.BmsCharts);
            var group = new DuplicateGroup([.. library.BmsCharts], [root]);
            var store = new DelegatingPreparedStore();
            ChartFileOperationSynchronizer gate = library.OperationAdmission;
            var activity = new ChartMutationActivityOwner();
            var events = new List<string>();
            var presentation = new RecordingPresentation(events);
            var reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var confirmation = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            int countPrompts = 0;
            int folderPrompts = 0;
            var dialogs = new FakeUiDialogService
            {
                ConfirmationHandler = request =>
                {
                    if (request.Button == MessageBoxButton.OKCancel)
                    {
                        countPrompts++;
                        return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    }
                    folderPrompts++;
                    Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Confirm_DeleteFolderWithNoBms, deletionFolder), request.MessageBoxText);
                    Assert.AreEqual(BeMusicSeeker.Properties.Resources.MessageBoxTitle_Confirm, request.Caption);
                    Assert.AreEqual(MessageBoxButton.YesNoCancel, request.Button);
                    Assert.AreEqual(MessageBoxImage.Question, request.Icon);
                    Assert.AreEqual(MessageBoxResult.Yes, request.DefaultResult);
                    Assert.IsFalse(gate.TryEnter(out IDisposable concurrent));
                    concurrent?.Dispose();
                    Assert.IsFalse(activity.IsActive);
                    Assert.AreEqual(0, events.Count);
                    Assert.AreEqual(0, store.ExecuteCount);
                    using LibraryFileMutationLease modelProbe = library.Lr2Synchronization.TryBeginMutation("folder_confirmation_probe");
                    Assert.IsNull(modelProbe, "フォルダ確認中も未受理のモデル変更はBusyです。");
                    reached.TrySetResult(true);
                    return confirmation.Task;
                }
            };
            var owner = new DuplicateMaintenanceWorkflowOwner(() => library, gate, activity, presentation, dialogs,
                () => decision != 0, Directory.Exists, _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(@"C:\Songs\next"), store);
            owner.WorkflowChanged += presentation.OnWorkflowChanged;
            activity.ActivityChanged += presentation.OnActivityChanged;
            Task<DuplicateMaintenanceMutationResult>? operation = null;
            try
            {
                operation = owner.RunHashCleanupAsync(group, root);
                Task arrived = await Task.WhenAny(reached.Task, operation);
                Assert.AreSame(reached.Task, arrived, "フォルダ確認前にTaskが終端しました。");
                await reached.Task;
                Assert.IsNotNull(store.Prepared);
                CollectionAssert.AreEqual(new[] { deletionFolder }, store.Prepared.WholeFolderCandidatePaths.ToArray());
                UiDialogResult answer = decision switch
                {
                    1 => UiDialogResult.FromMessageBoxResult(MessageBoxResult.No),
                    2 => UiDialogResult.ClosedByUser(MessageBoxResult.Yes),
                    _ => UiDialogResult.FromMessageBoxResult(MessageBoxResult.Yes)
                };
                confirmation.SetResult(answer);
                DuplicateMaintenanceMutationResult result = await operation;
                Assert.AreEqual(decision == 0 ? 0 : 1, countPrompts);
                Assert.AreEqual(1, folderPrompts);
                Assert.AreEqual(1, store.PrepareCount);
                Assert.AreEqual(decision == 2 ? 0 : 1, store.ExecuteCount);
                if (decision != 2)
                {
                    Assert.IsNotNull(store.ApprovedPaths);
                    CollectionAssert.AreEqual(decision == 0 ? new[] { deletionFolder } : [], store.ApprovedPaths.ToArray());
                }
                Assert.AreEqual(0, modelDialogs.ModelMessages);
                Assert.IsFalse(gate.IsActive);
                Assert.IsFalse(activity.IsActive);
                Assert.IsTrue(File.Exists(keeperPath));
                Assert.IsTrue(Directory.Exists(keeperFolder));
                using LR2SongDBExtended readback = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).OpenSongDbReadOnly();
                Assert.AreEqual(decision != 2, result.Succeeded);
                Assert.IsNull(result.Failure);
                Assert.AreEqual(decision == 2 ? 0 : 1, result.RemovedChartCount);
                Assert.AreEqual(decision == 2, File.Exists(deletionPath));
                Assert.AreEqual(decision != 0, File.Exists(resourcePath));
                Assert.AreEqual(decision != 0, Directory.Exists(deletionFolder));
                CollectionAssert.AreEquivalent(decision == 2 ? new[] { keeperPath, deletionPath } : [keeperPath],
                    readback.Table<LR2SongDB.song>().Select(row => row.path).ToArray());
                if (decision == 2) { Assert.AreEqual(0, events.Count); }
            }
            finally
            {
                confirmation.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.No));
                try { if (operation != null) { await operation; } }
                finally
                {
                    owner.WorkflowChanged -= presentation.OnWorkflowChanged;
                    activity.ActivityChanged -= presentation.OnActivityChanged;
                    library.RequestShutdown("duplicate-folder-confirmation-test");
                }
            }
        });
    }

    /// <summary>preparedと承認列を観測しつつ、処理結果は本番storeへ委譲します。</summary>
    private sealed class DelegatingPreparedStore : IDuplicateMaintenanceStore
    {
        private readonly IDuplicateMaintenanceStore inner;
        private readonly DelegatingPreparedStore observations;

        internal DelegatingPreparedStore()
        { inner = new BmsLibraryDuplicateMaintenanceStore(); observations = this; }

        private DelegatingPreparedStore(DelegatingPreparedStore observations, IDuplicateMaintenanceStore inner)
        { this.observations = observations; this.inner = inner; }

        internal LibraryChartRemovalPreflight? Prepared { get; private set; }
        internal IReadOnlyList<string>? ApprovedPaths { get; private set; }
        internal int PrepareCount { get; private set; }
        internal int ExecuteCount { get; private set; }

        /// <summary>生存権限を内側の実storeへ転送し、観測だけを元wrapperへ集約します。</summary>
        public IDuplicateMaintenanceStore ForAcceptedOperation(LibraryFileMutationCapability capability)
            => new DelegatingPreparedStore(observations, inner.ForAcceptedOperation(capability));

        public LibraryChartRemovalPreflight PrepareChartRemoval(BMSLibrary library, IReadOnlyList<ChartFile> charts)
        {
            observations.PrepareCount++;
            return observations.Prepared = inner.PrepareChartRemoval(library, charts);
        }
        public LibraryChartRemovalOutcome RemoveCharts(BMSLibrary library, LibraryChartRemovalPreflight prepared, IReadOnlyList<string> approvedWholeFolderPaths, out Action? publication)
        {
            publication = null;
            observations.ExecuteCount++;
            Assert.AreSame(observations.Prepared, prepared);
            observations.ApprovedPaths = approvedWholeFolderPaths;
            return inner.RemoveCharts(library, prepared, approvedWholeFolderPaths, out publication);
        }
        public DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId)
            => inner.MergeFolderWithReceipt(library, sourceDirectory, destinationDirectory, operationId);
    }

}
