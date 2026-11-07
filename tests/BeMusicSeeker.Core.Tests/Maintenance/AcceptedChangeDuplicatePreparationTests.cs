using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.RegularChartListOwnerTestSupport;
using DuplicateSupport = BeMusicSeeker.TestSupport.Components.DuplicateMaintenanceWorkflowOwnerTestSupport;
using PendingSupport = BeMusicSeeker.TestSupport.Components.PendingPackageWorkflowOwnerTestSupport;
using SelectedSupport = BeMusicSeeker.TestSupport.Components.SelectedChartMutationWorkflowOwnerTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 受理済みのライブラリ変更が重複結果を失効させたとき、各操作が受理済みの重複準備を表示抑制の解除より前に登録し、
/// 解除で流れる画面反映からの重複選択を Busy にせず準備へ合流させることを、実ライブラリ・実一覧所有者・実受付Lで検証します。
/// </summary>
/// <remarks>
/// 代替するのは変更本体の store / port と再生・確認だけです。準備・受付判定・L・抑制通知・後片付けは本番実装を動かします。
/// 準備は完成通知 <see cref="RegularChartListOwner.DuplicateDataReady"/> の受信で保留し、解除通知の時点で必ず登録済み・未完了にします。
/// WPF の親自動選択そのものは対象外で、解除通知の中から本番入口を呼んで同じ受付判定を決定的に通します。
/// </remarks>
[TestClass]
public sealed class AcceptedChangeDuplicatePreparationTests
{
    private const string DuplicateMerge = "duplicate-merge";
    private const string ChartMove = "chart-move";
    private const string PendingPackage = "pending-package";
    private const string FolderAutoRename = "folder-auto-rename";
    private const string FolderRename = "folder-rename";

    /// <summary>
    /// 保証：現在選択が重複表示で、受理済み変更がその中で重複結果を失効させ、親の共通受付Lを保持している間に、
    /// 操作の抑制解除通知の中で本番入口から重複選択を要求すると、Busy 案内を出さず進行中の準備へ合流して受理されます。
    /// 操作は準備の完了までLを保持し、準備が終わってから権限・Lを解放します（活動期間は準備の待機より前に終了します）。抑制解除通知は一回です。
    /// 前提は「変更の中で失効させたこと」と「解除時点でLを保持していること」で、解除時点のグループの有無は契約に含めません。
    /// 検出する誤り：準備の登録を抑制解除の後に行う従来順序（Busy 案内と拒否）、変更より前の登録（失効後に未登録で Busy）、
    /// 準備を待たずにLを解放すること、抑制解除通知の欠落・重複。
    /// </summary>
    [TestMethod]
    [DataRow(DuplicateMerge, DisplayName = "統合：store代替の統合本体で失効させ、requireGroups付きで準備する")]
    [DataRow(ChartMove, DisplayName = "譜面移動：store代替の移動本体で失効させ、変更適用通知の後に準備する")]
    [DataRow(PendingPackage, DisplayName = "保留の導入位置修復：store代替の修復本体で失効させる")]
    [DataRow(FolderAutoRename, DisplayName = "選択フォルダ自動改名：port代替の改名本体で失効させる")]
    [DataRow(FolderRename, DisplayName = "フォルダ名変更：実ファイルの実改名で失効させる")]
    public void SuppressionRelease_DuplicateSelectionJoinsRegisteredPreparation(string operation)
    {
        WithTemporarySongDb(songDb =>
        {
            using var harness = new Harness(songDb, preparationFailure: null);
            harness.Run(operation);
        });
    }

    /// <summary>
    /// 保証：抑制解除より前に開始した準備が失敗した場合も、同じ例外インスタンスが操作の終端結果へ届き、
    /// 抑制解除は一回、活動期間は終了し、Lは解放されて次の取得が成功します。必須配送が成功する条件での確認です。
    /// 統合・ハッシュ整理の同じ保証は Windows の
    /// <c>DuplicateMaintenanceWorkflowOwnerTests.ExplicitMaintenance_RequiredPreparationFailurePreservesFactsAndReleasesAdmission</c> が担います。
    /// 検出する誤り：準備の失敗の喪失、失敗による抑制解除・活動・受付の後片付けの省略。
    /// </summary>
    [TestMethod]
    [DataRow(ChartMove, DisplayName = "譜面移動：結果のFailureへ届く")]
    [DataRow(PendingPackage, DisplayName = "保留の導入位置修復：操作Taskの失敗へ届く")]
    [DataRow(FolderAutoRename, DisplayName = "選択フォルダ自動改名：失敗通知へ届く")]
    [DataRow(FolderRename, DisplayName = "フォルダ名変更：改名Taskの失敗へ届く")]
    public void PreparationFailure_ComposesIntoResultAndReleasesAdmission(string operation)
    {
        WithTemporarySongDb(songDb =>
        {
            using var harness = new Harness(songDb, new InvalidOperationException("accepted duplicate preparation failed: " + operation));
            harness.Run(operation);
        });
    }

    /// <summary>
    /// 保証：<see cref="BMSLibrary.CompleteAcceptedDuplicateDataAsync"/> は、接続先の同期例外も権限借用の拒否も呼出し元へ投げず、
    /// 失敗した Task として同じ例外を返します。呼出し側が抑制解除・後片付けを飛ばさないための契約です。
    /// 検出する誤り：同期例外の伝播、解放済み権限での接続先の呼出し、呼出しによる受付状態の変化。
    /// </summary>
    [TestMethod]
    [DataRow("preparation-throws", DisplayName = "接続先が同期例外を投げる")]
    [DataRow("released-capability", DisplayName = "解放済みの権限で呼ぶ")]
    public void CompleteAcceptedDuplicateDataAsync_SynchronousFailureReturnsFaultedTask(string source)
    {
        WithTemporarySongDb(songDb =>
        {
            var library = new TestBmsLibrary(new ApplicationDatabaseScope(), songDb, null, null, null,
                new FileDbReportRecordingDialogs(), new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot());
            var failure = new InvalidOperationException("synchronous duplicate preparation failure");
            int calls = 0;
            Func<BMSLibrary, LibraryFileMutationCapability, bool, Task> prepare = (_, _, _) =>
            {
                calls++;
                throw failure;
            };
            library.ConnectDuplicateDataPreparation(prepare);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable lease));
            try
            {
                LibraryFileMutationCapability capability = library.OperationAdmission.CreateMutationCapability(lease);
                if (source == "released-capability") { capability.Dispose(); }
                Task completion;
                try { completion = library.CompleteAcceptedDuplicateDataAsync(capability); }
                catch (Exception thrown) { throw new AssertFailedException("同期例外を呼出し元へ投げました: " + thrown); }
                Assert.IsTrue(completion.IsFaulted, "失敗した Task を返していません。");
                Assert.IsNotNull(completion.Exception, "失敗した Task が例外を保持していません。");
                Assert.IsTrue(library.OperationAdmission.IsActive, "呼出しが受付Lを変更しました。");
                if (source == "preparation-throws")
                {
                    Assert.AreSame(failure, completion.Exception.InnerException);
                    Assert.AreEqual(1, calls);
                    capability.Dispose();
                }
                else
                {
                    Assert.IsNotNull(completion.Exception.InnerException);
                    Assert.AreEqual(0, calls, "解放済み権限で接続先を呼びました。");
                }
            }
            finally
            {
                library.DisconnectDuplicateDataPreparation(prepare);
                lease.Dispose();
                library.RequestShutdown("accepted-duplicate-preparation-sync-failure");
            }
        });
    }

    /// <summary>実ライブラリ・実一覧所有者・各操作の実管理主体を同じLで構成し、準備の完成通知で保留して順序を観測します。</summary>
    private sealed class Harness : IDisposable
    {
        private readonly ApplicationDatabaseScope databaseScope = new();
        private readonly Exception? preparationFailure;
        private readonly ManualResetEventSlim gateRelease = new();
        private readonly TaskCompletionSource gateReached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource releaseObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly FileDbReportRecordingDialogs busyDialogs = new();
        private readonly ChartMutationActivityOwner activity = new();
        private readonly string root;
        private readonly string firstDirectory;
        private readonly string secondDirectory;
        private readonly ChartFile first;
        private readonly ChartFile second;
        private readonly TestBmsLibrary library;
        private readonly RegularChartListOwner regular;
        private int gateEnabled;
        private int gateHits;
        private int releaseCount;
        private int invalidationVersionBeforeChange;
        private bool invalidatedInChange;
        private bool admissionHeldAtInvalidation;
        private bool admissionHeldAtRelease;
        private bool invalidationVersionAdvancedAtRelease;
        private bool? groupsNullAtRelease;
        private Task? registeredPreparation;
        private Task<bool>? joinedSelection;
        private Exception? releaseFailure;
        private bool? preparationTerminalAtAdmissionRelease;
        private FolderAutoRenameWorkflowOwner? autoRename;

        internal Harness(string songDb, Exception? preparationFailure)
        {
            this.preparationFailure = preparationFailure;
            root = Path.GetDirectoryName(songDb) ?? throw new InvalidOperationException();
            firstDirectory = Directory.CreateDirectory(Path.Combine(root, "first")).FullName;
            secondDirectory = Directory.CreateDirectory(Path.Combine(root, "second")).FullName;
            first = CreateChart(Path.Combine(firstDirectory, "chart.bms"));
            second = CreateChart(Path.Combine(secondDirectory, "chart.bms"));
            library = new TestBmsLibrary(databaseScope, songDb, null, null, null, new FileDbReportRecordingDialogs(),
                new TestUiScheduler(() => null), () => new BmsLibraryOptionsSnapshot())
            { BmsCharts = [first, second], BmsonCharts = [] };
            new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).UpsertSongs([first, second]);
            regular = CreateOwner(new MainChartListViewModel(), CreateWorkspaceForOwner(), action => action(),
                chartFileOperations: library.OperationAdmission, mutationDialogs: new FileDbReportRecordingDialogs(),
                chartMutationActivity: activity);
            regular.AttachNormalLibraryRefreshSource(library);
            regular.MaintenanceNavigationPresentationRequested += (_, _) => { };
            regular.ConfigureDuplicateNavigationDialogs(busyDialogs);
            regular.DuplicateDataReady += OnDuplicateDataReady;
            library.OperationAdmission.AvailabilityChanged += OnAvailabilityChanged;
        }

        private static ChartFile CreateChart(string path)
        {
            File.WriteAllText(path, "#PLAYER 1\r\n#TITLE accepted duplicate preparation\r\n");
            return CreateTestableBmsFile(path);
        }

        internal void Run(string operation)
        {
            Assert.IsTrue(regular.NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected).GetAwaiter().GetResult(),
                "前提：変更前の重複選択を受理できません。");
            Assert.IsNotNull(library.DuplicateChartGroups, "前提：変更前の重複結果が準備されていません。");
            Assert.AreEqual(0, busyDialogs.Messages.Count);
            invalidationVersionBeforeChange = library.DuplicateChartGroupsInvalidationVersion;
            Volatile.Write(ref gateEnabled, 1);
            Task? op = null;
            Func<Exception?>? terminalFailure = null;
            try
            {
                (op, terminalFailure) = Start(operation);
                TestUiDispatcherHost.AwaitNotificationAsync(releaseObserved.Task, op, operation + " suppression release").GetAwaiter().GetResult();
                TestUiDispatcherHost.AwaitNotificationAsync(gateReached.Task, op, operation + " duplicate preparation gate").GetAwaiter().GetResult();

                Assert.IsNull(releaseFailure, "解除通知の中の受付要求が同期例外を投げました: " + releaseFailure);
                if (operation == FolderRename)
                {
                    Assert.IsTrue(invalidationVersionAdvancedAtRelease, "前提：実改名が重複結果を失効させていません。");
                }
                else
                {
                    Assert.IsTrue(invalidatedInChange, "前提：変更本体で重複結果を失効させていません。");
                    Assert.IsTrue(admissionHeldAtInvalidation, "前提：失効の時点でLを保持していません。");
                }
                Assert.IsTrue(admissionHeldAtRelease, "前提：抑制解除の時点でLを保持していません。");
                Assert.IsNotNull(registeredPreparation);
                // 解除時点のグループの有無は契約に含めず、失敗時の診断にだけ使います。
                Assert.IsFalse(registeredPreparation.IsCompleted,
                    "抑制解除の時点で受理済みの準備が登録されていません（解除時点のグループ未準備=" + groupsNullAtRelease + "）。");
                Assert.IsNotNull(joinedSelection);
                Assert.IsFalse(joinedSelection.IsCompleted && !joinedSelection.IsFaulted && !joinedSelection.Result,
                    "抑制解除の時点の重複選択を Busy として拒否しました。");
                Assert.AreEqual(0, busyDialogs.Messages.Count, "抑制解除の時点の重複選択で Busy 案内を出しました。");
                Assert.IsFalse(op.IsCompleted, "準備の完了前に操作が終わりました。");
                Assert.IsFalse(library.OperationAdmission.TryEnter(out IDisposable rejected), "準備の完了前にLを解放しました。");
                rejected?.Dispose();

                gateRelease.Set();
                Exception? opFailure = Capture(op);
                Exception? failure = opFailure ?? terminalFailure();
                if (preparationFailure == null)
                {
                    Assert.IsNull(failure, operation + " が失敗しました: " + failure);
                    Assert.IsNotNull(library.DuplicateChartGroups, "準備の完了後に重複結果がありません。");
                    Assert.IsTrue(joinedSelection.GetAwaiter().GetResult(), "合流した重複選択が受理として終わっていません。");
                }
                else
                {
                    Assert.IsNotNull(failure, "準備の失敗が操作の終端へ届いていません。");
                    Assert.IsTrue(Carries(failure, preparationFailure), "操作の終端が準備の失敗と別の例外です: " + failure);
                    _ = Capture(joinedSelection);
                }
                Assert.AreEqual(1, Volatile.Read(ref releaseCount), "抑制解除通知は一回です。");
                Assert.IsFalse(activity.IsActive, "活動期間が終了していません。");
                Assert.IsFalse(library.OperationAdmission.IsActive, "Lを解放していません。");
                Assert.IsTrue(preparationTerminalAtAdmissionRelease, "準備の終端前にLを解放しました。");
                Assert.AreEqual(0, busyDialogs.Messages.Count, "Busy 案内を出しました。");
                Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable next), "解放後の次の取得に失敗しました。");
                next.Dispose();
            }
            finally
            {
                gateRelease.Set();
                if (op != null) { _ = Capture(op); }
                if (joinedSelection != null) { _ = Capture(joinedSelection); }
                if (autoRename != null) { autoRename.WaitForIdleAsync().GetAwaiter().GetResult(); }
            }
        }

        private (Task Operation, Func<Exception?> TerminalFailure) Start(string operation)
        {
            switch (operation)
            {
                case DuplicateMerge:
                    {
                        var events = new List<string>();
                        var owner = new DuplicateMaintenanceWorkflowOwner(() => library, library.OperationAdmission, activity,
                            new DuplicateSupport.RecordingPresentation(events), DuplicateSupport.AcceptedDialogs(), () => false, _ => true,
                            _ => new ExplorerOpenResult(), _ => DuplicateViewContext.ForGroup(secondDirectory),
                            new InvalidatingDuplicateStore(events, InvalidateInChange));
                        owner.WorkflowChanged += (_, change) =>
                        {
                            if (change is DuplicateMaintenanceRefreshSuppressionChangedEventArgs { IsSuppressed: false }) { OnSuppressionReleased(); }
                        };
                        Task<DuplicateMaintenanceMutationResult> merge = owner.RunFolderMergeAsync(firstDirectory, secondDirectory,
                            new DuplicateGroup([first, second], [firstDirectory, secondDirectory]));
                        return (merge, () => merge.Result.Succeeded ? null : merge.Result.Failure ?? new AssertFailedException("統合が成功しませんでした。"));
                    }
                case ChartMove:
                    {
                        SelectedSupport.FakeUiDialogService dialogs = SelectedSupport.AcceptedMessageDialogs();
                        var owner = new SelectedChartMutationWorkflowOwner(() => library, library.OperationAdmission, activity,
                            new SelectedSupport.RecordingPresentation(), dialogs, dialogs, new InvalidatingSelectedStore(InvalidateInChange));
                        owner.WorkflowChanged += (_, change) =>
                        {
                            if (change is SelectedChartMutationRefreshSuppressionChangedEventArgs { IsSuppressed: false }) { OnSuppressionReleased(); }
                        };
                        Task<SelectedChartMutationResult> move = owner.MoveAsync(new SelectedChartMoveRequest(
                            [SelectedSupport.CreateTarget("alpha.bms", ChartOperationSourceScope.Library, false, ChartOperationCapabilities.MoveInLibrary)],
                            Path.Combine(root, "moved")));
                        return (move, () => move.Result.Succeeded ? null : move.Result.Failure ?? new AssertFailedException("移動が成功しませんでした。"));
                    }
                case PendingPackage:
                    {
                        var events = new List<string>();
                        var store = new PendingPackageStoreTestFixture.RecordingStore(events)
                        {
                            FixInstalledLocationsAction = (_, _, _, _) =>
                            {
                                InvalidateInChange();
                                return new LibraryFixInstallationResult();
                            }
                        };
                        var owner = new PendingPackageWorkflowOwner(() => library, library.OperationAdmission, activity,
                            new PendingSupport.RecordingPlayback(events), new FileDbReportRecordingDialogs(), PendingSupport.DefaultSettings,
                            new PendingSupport.TestExternalShellGateway(_ => new ExplorerOpenResult(), _ => new ExplorerOpenResult()), store);
                        owner.WorkflowChanged += (_, change) =>
                        {
                            if (change is PendingPackageRefreshSuppressionChangedEventArgs { IsSuppressed: false }) { OnSuppressionReleased(); }
                        };
                        ChartFile repairChart = ChartFileProjection.WithPackageState(first, Path.Combine(root, "installed"), string.Empty, string.Empty, []);
                        Assert.IsTrue(RepairInstalledLocationRequest.TryCreate(
                            [PendingSupport.CreateTarget(repairChart, ChartOperationCapabilities.RepairInstalledLocation)],
                            out RepairInstalledLocationRequest request));
                        Task repair = owner.FixInstalledLocationsAsync(request);
                        return (repair, () => null);
                    }
                case FolderAutoRename:
                    {
                        var port = new InvalidatingAutoRenamePort(InvalidateInChange);
                        autoRename = new FolderAutoRenameWorkflowOwner(library.OperationAdmission, activity, port,
                            new NoOpChartMutationPlaybackPort(), work => Task.Run(work), action => action(), new FileDbReportRecordingDialogs());
                        autoRename.AttachLibrary(library);
                        FolderAutoRenameFailure? failure = null;
                        FolderAutoRenameCompletionReceipt? completion = null;
                        autoRename.FailurePublished += value => failure = value;
                        autoRename.CompletionPublished += value => completion = value;
                        autoRename.RefreshSuppressionChanged += (_, change) =>
                        {
                            if (!change.IsSuppressed) { OnSuppressionReleased(); }
                        };
                        var target = new ChartOperationTarget(first, playlistEntry: null, ChartOperationSourceScope.Library,
                            isOwned: true, isPending: false, isPlaylistMissing: false, ChartOperationCapabilities.MoveInLibrary);
                        Assert.IsTrue(autoRename.RequestStartSelected([target]));
                        Task idle = autoRename.WaitForIdleAsync();
                        return (idle, () => failure?.Exception
                            ?? (completion == null ? new AssertFailedException("自動改名の完了通知がありません。") : null));
                    }
                case FolderRename:
                    {
                        regular.RefreshSuppressionChanged += (_, change) =>
                        {
                            if (!change.IsSuppressed) { OnSuppressionReleased(); }
                        };
                        Task rename = regular.RenameChartFolderAsync(CreateRenameRequest(first), "renamed");
                        return (rename, () => null);
                    }
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation), operation, null);
            }
        }

        private void InvalidateInChange()
        {
            library.DuplicateChartGroups = null;
            invalidatedInChange = library.DuplicateChartGroups == null;
            admissionHeldAtInvalidation = library.OperationAdmission.IsActive;
        }

        /// <summary>抑制解除通知の中では表明せず記録だけを行います。通知先の例外は操作側で握られるためです。</summary>
        private void OnSuppressionReleased()
        {
            if (Interlocked.Increment(ref releaseCount) != 1) { return; }
            try
            {
                groupsNullAtRelease = library.DuplicateChartGroups == null;
                admissionHeldAtRelease = library.OperationAdmission.IsActive;
                invalidationVersionAdvancedAtRelease = library.DuplicateChartGroupsInvalidationVersion > invalidationVersionBeforeChange;
                registeredPreparation = regular.PrepareDuplicateDataAsync();
                joinedSelection = regular.NavigateMaintenanceAsync(MainViewUpdateMode.DuplicateFilterSelected);
            }
            catch (Exception exception) { releaseFailure = exception; }
            finally { releaseObserved.TrySetResult(); }
        }

        private void OnDuplicateDataReady(object? sender, RegularDuplicateDataReadyEventArgs e)
        {
            if (Volatile.Read(ref gateEnabled) == 0 || Interlocked.Increment(ref gateHits) != 1) { return; }
            gateReached.TrySetResult();
            gateRelease.Wait();
            if (preparationFailure != null) { throw preparationFailure; }
        }

        private void OnAvailabilityChanged(object? sender, EventArgs e)
        {
            if (Volatile.Read(ref gateEnabled) == 0 || library.OperationAdmission.IsActive || preparationTerminalAtAdmissionRelease.HasValue)
            { return; }
            Task? preparation = registeredPreparation;
            if (preparation != null) { preparationTerminalAtAdmissionRelease = preparation.IsCompleted; }
        }

        private static Exception? Capture(Task task)
        {
            try { task.GetAwaiter().GetResult(); return null; }
            catch (Exception exception) { return exception; }
        }

        private static bool Carries(Exception actual, Exception expected)
        {
            if (ReferenceEquals(actual, expected)) { return true; }
            if (actual is AggregateException aggregate && aggregate.InnerExceptions.Any(inner => Carries(inner, expected))) { return true; }
            return actual.InnerException != null && Carries(actual.InnerException, expected);
        }

        public void Dispose()
        {
            gateRelease.Set();
            try { regular.StopAsync().GetAwaiter().GetResult(); }
            finally
            {
                autoRename?.RequestShutdown();
                regular.Dispose();
                library.RequestShutdown("accepted-duplicate-preparation");
                gateRelease.Dispose();
            }
        }
    }

    /// <summary>統合本体の代替です。重複結果の失効を変更の中で行う store の契約を再現します。</summary>
    private sealed class InvalidatingDuplicateStore(List<string> events, Action invalidate) : DuplicateSupport.RecordingStore(events)
    {
        public override DuplicateMergeMaintenanceReceipt MergeFolderWithReceipt(
            BMSLibrary library, string sourceDirectory, string destinationDirectory, long operationId)
        {
            DuplicateMergeMaintenanceReceipt receipt = base.MergeFolderWithReceipt(library, sourceDirectory, destinationDirectory, operationId);
            invalidate();
            return receipt;
        }
    }

    /// <summary>譜面移動本体の代替です。重複結果の失効を変更の中で行う store の契約を再現します。</summary>
    private sealed class InvalidatingSelectedStore(Action invalidate) : SelectedSupport.RecordingStore
    {
        public override LibraryMutationSessionReceipt MoveLibraryChartsWithReceipt(BMSLibrary library, ChartLibraryMoveRequest request)
        {
            LibraryMutationSessionReceipt receipt = base.MoveLibraryChartsWithReceipt(library, request);
            invalidate();
            return receipt;
        }
    }

    /// <summary>選択フォルダ自動改名本体の最小代替です。重複結果の失効を変更の中で行います。</summary>
    private sealed class InvalidatingAutoRenamePort(Action invalidate) : IFolderAutoRenameMutationPort
    {
        public bool HasTargets(BMSLibrary library, string parentDirectory) => true;

        public FolderAutoRenameExecutionResult RenameSelectedWithProgress(
            BMSLibrary library, ChartFolderAutoRenameRequest request, IFolderAutoRenameProgressWriter progressWriter)
        {
            invalidate();
            return new FolderAutoRenameExecutionResult();
        }

        public AutoRenameBatchResult RenameAllWithReceiptWithProgress(
            BMSLibrary library, string parentDirectory, IFolderAutoRenameProgressWriter progressWriter)
            => throw new NotSupportedException();
    }
}
