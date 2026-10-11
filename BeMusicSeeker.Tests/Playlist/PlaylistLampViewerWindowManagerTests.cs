using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>ランプビューアの表示受付・対象別の終了・メイン終了時の取消を検証する。</summary>
/// <remarks>実Windowの表示とClosedを観測するため既存MainWindow fixtureを使う。共有Application資源・実Window集合・native後片付けを扱うため直列実行し、集計・履歴復元は下位テストへ分担する。</remarks>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistLampViewerWindowManagerTests
{
    [TestInitialize]
    public void MaterializeCanonicalApplicationResources()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            Application application = Application.Current
                ?? throw new AssertFailedException("共有WPF Applicationを利用できない。");
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

    /// <summary>初回入力が削除済みなら所有者付きの削除通知を一回表示し、ビューアを表示せず情報源を一回解放する。削除対象の誤表示と通知・解放漏れを検出する。</summary>
    [TestMethod]
    public void Manager_initialDeletedShowsOneLocalizedDialogAndNoWindow()
    {
        var source = new ControlledLampSource(PlaylistLampAggregationRequest.Deleted("1"));
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.initial-deleted.open");

                Assert.IsNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.AreEqual(1, fixture.PreparedWindowCount);
                Assert.AreEqual(1, dialogs.MessageCount);
                Assert.AreEqual(
                    Resources.PlaylistLampViewer_initial_deleted,
                    dialogs.Messages.Single().MessageBoxText);
                Assert.AreSame(fixture.Owner, dialogs.Messages.Single().Owner);
                Assert.IsFalse(Application.Current.Windows
                    .OfType<PlaylistLampViewerWindow>()
                    .Any(window => window.IsVisible));
                Assert.AreEqual(1, source.DisposeCount);
            },
            prepareWindowPresentation: false);
    }

    /// <summary>初回集計失敗は原因付きの通知を所有者へ一回渡し、ビューアを表示せず情報源を一回解放する。失敗の空表示化と通知・解放漏れを検出する。</summary>
    [TestMethod]
    public void Manager_initialFailedShowsOneLocalizedDialogAndNoWindow()
    {
        var source = new ControlledLampSource(
            PlaylistLampAggregationRequest.Failed("1", "initial aggregation failure"));
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.initial-failed.open");

                Assert.IsNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.AreEqual(1, fixture.PreparedWindowCount);
                Assert.AreEqual(1, dialogs.MessageCount);
                StringAssert.Contains(
                    dialogs.Messages.Single().MessageBoxText,
                    "initial aggregation failure");
                Assert.AreSame(fixture.Owner, dialogs.Messages.Single().Owner);
                Assert.IsFalse(Application.Current.Windows
                    .OfType<PlaylistLampViewerWindow>()
                    .Any(window => window.IsVisible));
                Assert.AreEqual(1, source.DisposeCount);
            },
            prepareWindowPresentation: false);
    }

    /// <summary>Readyの実Windowは表示後の最初のactivate時までOwnerを持ち、その後解除する。表示前activationと以後の重なり順をメインへ拘束する誤りを検出する。</summary>
    [TestMethod]
    public void Manager_readyPresentationReleasesOwnerAfterShow()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        var dialogs = new RecordingDialogService();
        int activationAttemptCount = 0;
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.owner-release.open");

                PlaylistLampViewerWindow window = open.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("Readyのビューアが開かなかった。");
                Assert.IsNull(window.Owner);
                Assert.IsTrue(window.IsVisible);
                Assert.AreEqual(1, fixture.Manager.Count);
            },
            activateWindowForInitialPresentation: window =>
            {
                activationAttemptCount++;
                Assert.IsTrue(window.IsVisible);
                Assert.IsNotNull(window.Owner);
                Assert.IsFalse(window.ShowActivated);
            });
        Assert.AreEqual(1, activationAttemptCount);
    }

    /// <summary>実プレイリスト集合から一対象を削除すると、その情報源の通知で対象ビューアだけを一回閉じ、他の窓は保持して通知ダイアログを出さない。対象外の巻込みと解除漏れを検出する。</summary>
    [TestMethod]
    public void Manager_collectionRemovalSilentlyClosesOnlyTargetViewer()
    {
        var createdSources = new List<ActualReadySource>();
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            context =>
            {
                var source = new ActualReadySource(context.Playlist, context.Library, context.PlaylistId);
                createdSources.Add(source);
                return source;
            },
            fixture =>
            {
                PlaylistLampViewerOpenContext otherContext = fixture.CreateOtherContext();
                Task<PlaylistLampViewerWindow> firstOpen = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(firstOpen, "manager.collection-removal.first-open");
                PlaylistLampViewerWindow firstWindow = firstOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("対象ビューアが開かなかった。");

                Task<PlaylistLampViewerWindow> otherOpen = fixture.Manager.TryOpenAsync(otherContext);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOpen, "manager.collection-removal.other-open");
                PlaylistLampViewerWindow otherWindow = otherOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("別対象のビューアが開かなかった。");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                int firstClosedCount = 0;
                TaskCompletionSource<bool> firstClosed = Completion<bool>();
                firstWindow.Closed += (_, _) =>
                {
                    firstClosedCount++;
                    firstClosed.TrySetResult(true);
                };

                Assert.AreEqual(2, fixture.Manager.Count);
                Assert.AreEqual(PlaylistLampViewerState.Ready, firstWindow.ViewModel.State);

                // 実情報源が購読する正本集合を変更し、手動イベント注入ではなく対象削除から終了へ接続する。
                Assert.IsTrue(fixture.Playlist.BMSTables.Remove(fixture.Table));
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(
                    firstClosed.Task,
                    "manager.collection-removal.primary-closed");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, firstClosedCount);
                Assert.AreEqual(1, fixture.Manager.Count);
                Assert.IsTrue(otherWindow.IsVisible);
                Assert.AreEqual(0, dialogs.MessageCount);
                Assert.AreEqual(1, createdSources[0].DisposeCount);
            });
    }

    /// <summary>同じプレイリストを2窓で開き、区間選択を独立に保持し、片方を閉じても他方の表示・操作・情報源を維持する。窓状態の共有と一括終了を検出する。</summary>
    [TestMethod]
    public void Manager_samePlaylistOpensFreshIndependentViewers()
    {
        var createdSources = new List<ActualReadySource>();
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            context =>
            {
                var source = new ActualReadySource(context.Playlist, context.Library, context.PlaylistId);
                createdSources.Add(source);
                return source;
            },
            fixture =>
            {
                Task<PlaylistLampViewerWindow> firstOpen = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(firstOpen, "manager.same-playlist.first-open");
                PlaylistLampViewerWindow firstWindow = firstOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("同じプレイリストの最初の窓が開かなかった。");

                Task<PlaylistLampViewerWindow> secondOpen = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(secondOpen, "manager.same-playlist.second-open");
                PlaylistLampViewerWindow secondWindow = secondOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("同じプレイリストの次の窓が開かなかった。");

                Assert.AreNotSame(firstWindow, secondWindow);
                Assert.AreEqual(2, fixture.Manager.Count);
                Assert.AreEqual(2, fixture.PreparedWindowCount);
                Assert.AreEqual(2, createdSources.Count);

                PlaylistLampViewerSegmentViewModel firstGlobalRank = firstWindow.ViewModel.PositiveRankSegments
                    .Single(segment => segment.CategoryKey == "A");
                PlaylistLampViewerSegmentViewModel secondGlobalRank = secondWindow.ViewModel.PositiveRankSegments
                    .Single(segment => segment.CategoryKey == "A");
                firstGlobalRank.Invoke();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(firstGlobalRank.IsSelected);
                Assert.IsFalse(secondGlobalRank.IsSelected);
                Assert.IsTrue(secondWindow.IsVisible);
                Assert.IsTrue(secondGlobalRank.IsInvokable);

                secondGlobalRank.Invoke();
                TestUiDispatcherHost.ProcessQueuedPresentation();
                Assert.IsTrue(firstGlobalRank.IsSelected);
                Assert.IsTrue(secondGlobalRank.IsSelected);
                Assert.AreSame(firstGlobalRank, firstWindow.ViewModel.SelectedSegment);
                Assert.AreSame(secondGlobalRank, secondWindow.ViewModel.SelectedSegment);

                TaskCompletionSource<bool> firstClosed = Completion<bool>();
                int firstClosedCount = 0;
                firstWindow.Closed += (_, _) =>
                {
                    firstClosedCount++;
                    firstClosed.TrySetResult(true);
                };
                firstWindow.Close();
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(
                    firstClosed.Task,
                    "manager.same-playlist.first-closed");
                TestUiDispatcherHost.ProcessQueuedPresentation();

                Assert.AreEqual(1, firstClosedCount);
                Assert.AreEqual(1, fixture.Manager.Count);
                Assert.IsTrue(secondWindow.IsVisible);
                Assert.AreEqual(1, createdSources[0].DisposeCount);
                Assert.AreEqual(0, createdSources[1].DisposeCount);
            });
    }

    /// <summary>初回捕捉gate中はビューアも通知も表示せず、Ready捕捉の完了後に一窓だけ表示する。初期Loadingの早期表示と完了後の接続漏れを検出する。</summary>
    [TestMethod]
    public void Manager_firstPresentableGateDoesNotShowWhileCaptureIsPending()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"), waitForCapture: true);
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        source.CaptureStarted.Task,
                        "manager.presentable-gate.capture-started");

                    Assert.AreEqual(0, fixture.Manager.Count);
                    Assert.IsFalse(Application.Current.Windows
                        .OfType<PlaylistLampViewerWindow>()
                        .Any(window => window.IsVisible));
                    Assert.AreEqual(0, dialogs.MessageCount);

                    source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        open,
                        "manager.presentable-gate.ready");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.IsNotNull(open.GetAwaiter().GetResult());
                    Assert.AreEqual(1, fixture.Manager.Count);
                    Assert.AreEqual(1, fixture.PreparedWindowCount);
                    Assert.AreEqual(1, Application.Current.Windows
                        .OfType<PlaylistLampViewerWindow>()
                        .Count(window => window.IsVisible));
                    Assert.AreEqual(0, dialogs.MessageCount);
                }
                finally
                {
                    source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.presentable-gate.finally-open");
                }
            });
    }

    /// <summary>表示中の失敗通知gateへ重複終端信号を送り、通知を一回に抑えて対象窓を一回閉じ、情報源を解放する。重複ダイアログと終了処理の重複・漏れを検出する。</summary>
    [TestMethod]
    public void Manager_liveFailedShowsOneDialogAndClosesTargetAfterRepeatedSignals()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.live-failed.open");
                PlaylistLampViewerWindow window = open.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("表示中の失敗を確認するビューアが開かなかった。");

                int closedCount = 0;
                TaskCompletionSource<bool> closed = Completion<bool>();
                window.Closed += (_, _) =>
                {
                    closedCount++;
                    closed.TrySetResult(true);
                };
                TaskCompletionSource<UiDialogResult> blockedMessage = Completion<UiDialogResult>();
                dialogs.BlockedMessageResult = blockedMessage;

                try
                {
                    source.Replace(PlaylistLampAggregationRequest.Failed("1", "live aggregation failure"));
                    source.Raise("1", 1);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        dialogs.FirstMessage.Task,
                        "manager.live-failed.dialog-presented");

                    // 通知が未完了のまま重複終端を受けても、新しい通知を作らず元の対象だけを閉じる。
                    source.Raise("1", 2);
                    Assert.AreEqual(1, dialogs.MessageCount);

                    blockedMessage.TrySetResult(
                        UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "manager.live-failed.closed");

                    Assert.AreEqual(1, closedCount);
                    Assert.AreEqual(0, fixture.Manager.Count);
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreSame(fixture.Owner, dialogs.Messages.Single().Owner);
                    Assert.AreEqual(1, source.DisposeCount);
                }
                finally
                {
                    blockedMessage.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    fixture.Manager.Dispose();
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "manager.live-failed.finally-closed");
                }
            });
    }

    /// <summary>初回捕捉中に管理主体を破棄すると開くTaskを取消で回収し、遅れて窓・通知を表示せず情報源を解放する。終了後の遅延表示と捕捉処理の残存を検出する。</summary>
    [TestMethod]
    public void Manager_pendingOpenCanceledByDisposeNeverShowsWindowOrDialog()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"), waitForCapture: true);
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        source.CaptureStarted.Task,
                        "manager.pending-cancel.capture-started");

                    fixture.Manager.Dispose();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.pending-cancel.open-canceled");
                    TestUiDispatcherHost.ProcessQueuedPresentation();

                    Assert.IsNull(open.GetAwaiter().GetResult());
                    Assert.AreEqual(0, fixture.Manager.Count);
                    Assert.AreEqual(0, dialogs.MessageCount);
                    Assert.AreEqual(1, source.DisposeCount);
                    Assert.IsFalse(Application.Current.Windows
                        .OfType<PlaylistLampViewerWindow>()
                        .Any(window => window.IsVisible));
                }
                finally
                {
                    fixture.Manager.Dispose();
                    source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.pending-cancel.finally-open");
                }
            },
            prepareWindowPresentation: false);
    }

    private static void RunScenario(
        IUiDialogService dialogs,
        Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> sourceFactory,
        Action<ManagerTestFixture> test,
        bool prepareWindowPresentation = true,
        Action<PlaylistLampViewerWindow>? activateWindowForInitialPresentation = null)
    {
        string settingsRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistLampViewerWindowManagerTests),
            "settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = settingsRoot;
                values.StandaloneBmsRootPaths = settingsRoot;
                values.BMSInstallDir = settingsRoot;
                values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
                values.EnablePlaylistUrlCompletion = false;
                values.ScanBmsFilesOnStartup = false;
                values.SkipInitPlaylistLoad = true;
                values.UseBeatorajaScoreDb = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseExternalPanelImage = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
                values.RightClickActionsJson = RightClickActionSettingsDefaults.SerializedJson;
            }),
                (presentationScope, _, owner) =>
                {
                    ManagerTestFixture? fixture = null;
                    ExceptionDispatchInfo? bodyFailure = null;
                    Exception? cleanupFailure = null;
                    try
                    {
                        owner.Show();
                        owner.UpdateLayout();
                        fixture = new ManagerTestFixture(
                            owner,
                            dialogs,
                            sourceFactory,
                            window =>
                            {
                                Assert.IsFalse(
                                    window.IsLoaded,
                                    "未読込みの新しいビューアを表示scopeへ登録する。");
                                Assert.IsFalse(
                                    window.IsVisible,
                                    "表示前に新しいビューアを表示scopeへ登録する。");
                                Assert.AreEqual(
                                    nint.Zero,
                                    TestWindowPresentationScope.GetNativeHandle(window),
                                    "表示scopeの準備前は新しいビューアにHWNDを作らない。");
                                if (prepareWindowPresentation)
                                {
                                    presentationScope.PrepareForOwnedPresentation(
                                        window);
                                }
                                fixture?.RecordPreparedWindow(window);
                            },
                            activateWindowForInitialPresentation);
                        test(fixture!);
                    }
                    catch (Exception ex)
                    {
                        bodyFailure = ExceptionDispatchInfo.Capture(ex);
                    }

                    try
                    {
                        fixture?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        cleanupFailure = ex;
                    }

                    if (bodyFailure != null)
                    {
                        if (cleanupFailure != null)
                        {
                            bodyFailure.SourceException.Data["ManagerFixtureCleanupFailure"] = cleanupFailure.ToString();
                        }
                        bodyFailure.Throw();
                    }
                    if (cleanupFailure != null) { ExceptionDispatchInfo.Capture(cleanupFailure).Throw(); }
                },
                prepareWindowForPresentation: (windowTest, owner) => windowTest.PrepareForOwnedPresentation(owner));
        }
        finally
        {
            if (Directory.Exists(settingsRoot))
            {
                Directory.Delete(settingsRoot, recursive: true);
            }
        }
    }

    private static PlaylistLampAggregationRequest CreateReadyRequest(string playlistId)
    {
        string hash = new('a', 32);
        string sha256 = new('b', 64);
        var score = new PlaylistLampScore(
            hash,
            sha256,
            ClearType.CLEAR,
            RankType.A,
            100,
            0,
            100,
            1);
        var scoreByHash = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
        {
            [score.Hash] = score
        };
        var scoreBySha256 = new Dictionary<string, PlaylistLampScore>(StringComparer.OrdinalIgnoreCase)
        {
            [score.Sha256] = score
        };
        return new PlaylistLampAggregationRequest(
            playlistId,
            ["folder"],
            [new PlaylistLampEntrySnapshot(
                "folder",
                "manager-entry",
                isOwned: true,
                md5: score.Hash,
                sha256: score.Sha256,
                resolvedPath: "C:/manager-fixture/chart.bms",
                resolvedMd5: score.Hash,
                resolvedSha256: score.Sha256)],
            new PlaylistLampScoreSnapshot(
                ActiveScoreSource.Beatoraja,
                ScoreTableLoadStatus.Loaded,
                1,
                1,
                new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
                scoreByHash,
                scoreBySha256),
            new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc));
    }

    private static TaskCompletionSource<T> Completion<T>()
    {
        return new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManagerTestFixture : IDisposable
    {
        private readonly string root;

        private readonly PlaylistWorkspaceViewModel workspace;

        /// <summary>設定とプレイリストを分離し、省略可能な初回表示処理を持つウィンドウ管理テストを構成します。</summary>
        internal ManagerTestFixture(
            MainWindow owner,
            IUiDialogService dialogs,
            Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> sourceFactory,
            Action<PlaylistLampViewerWindow> prepareWindowForShow,
            Action<PlaylistLampViewerWindow>? activateWindowForInitialPresentation)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            root = Path.Combine(
                Path.GetTempPath(),
                nameof(PlaylistLampViewerWindowManagerTests),
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string songDbPath = Path.Combine(root, "song.db");
            StartupLibraryConstructionTestSupport.CreateSongDatabase(songDbPath);

            Library = new TestBmsLibrary(
                songDbPath,
                null,
                null,
                new TestFileMutationService());
            Library.BmsCharts = [];
            Playlist = BmsPlaylistTestSupport.CreatePlaylist(songDbPath);
            Table = CreateTable(1, "Manager fixture");
            Playlist.BMSTables = new ObservableCollection<BMSTable> { Table };
            workspace = BmsPlaylistTestSupport.CreatePlaylistWorkspace(Playlist, Library);
            Context = CreateContext(Playlist, Table, Library);
            Manager = new PlaylistLampViewerWindowManager(
                Owner,
                workspace,
                dialogs,
                sourceFactory,
                prepareWindowForShow,
                activateWindowForInitialPresentation);
        }

        internal BMSLibrary Library { get; }

        internal MainWindow Owner { get; }

        internal BMSPlaylist Playlist { get; }

        internal BMSTable Table { get; }

        internal PlaylistLampViewerOpenContext Context { get; }

        internal PlaylistLampViewerWindowManager Manager { get; }

        private readonly List<PlaylistLampViewerWindow> preparedWindows = [];

        internal int PreparedWindowCount => preparedWindows.Count;

        internal void RecordPreparedWindow(PlaylistLampViewerWindow window)
        {
            preparedWindows.Add(window ?? throw new ArgumentNullException(nameof(window)));
        }

        internal PlaylistLampViewerOpenContext CreateOtherContext()
        {
            BMSPlaylist otherPlaylist = BmsPlaylistTestSupport.CreatePlaylist(
                Path.Combine(root, "song.db"));
            BMSTable otherTable = CreateTable(2, "Other manager fixture");
            otherPlaylist.BMSTables = new ObservableCollection<BMSTable> { otherTable };
            return CreateContext(otherPlaylist, otherTable, Library);
        }

        public void Dispose()
        {
            Manager.Dispose();
            workspace.Dispose();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }

        private static BMSTable CreateTable(int playlistId, string name)
        {
            return new BMSTable
            {
                playlist_id = playlistId,
                name = name,
                Folder_order = ["folder"],
                entries = [new BMSTableEntry
                {
                    folder = "folder",
                    md5 = new string('a', 32),
                    sha256 = new string('b', 64)
                }]
            };
        }

        private static PlaylistLampViewerOpenContext CreateContext(
            BMSPlaylist playlist,
            BMSTable table,
            BMSLibrary library)
        {
            return new PlaylistLampViewerOpenContext(
                table.playlist_id?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                table.name,
                table,
                playlist,
                library);
        }
    }

    private sealed class ControlledLampSource : IPlaylistLampViewerDataSource, IDisposable
    {
        private readonly object gate = new();

        private readonly bool waitForCapture;

        private PlaylistLampAggregationRequest request;

        private EventHandler<PlaylistLampViewerSourceChangedEventArgs>? changed;

        private int disposed;

        internal ControlledLampSource(PlaylistLampAggregationRequest request, bool waitForCapture = false)
        {
            this.request = request ?? throw new ArgumentNullException(nameof(request));
            this.waitForCapture = waitForCapture;
        }

        internal TaskCompletionSource<bool> CaptureStarted { get; } = Completion<bool>();

        internal TaskCompletionSource<PlaylistLampAggregationRequest> CaptureRelease { get; } =
            Completion<PlaylistLampAggregationRequest>();

        internal int DisposeCount { get; private set; }

        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed
        {
            add
            {
                lock (gate)
                {
                    changed += value;
                }
            }
            remove
            {
                lock (gate)
                {
                    changed -= value;
                }
            }
        }

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            PlaylistLampViewerQuery query,
            CancellationToken cancellationToken)
        {
            CaptureStarted.TrySetResult(true);
            if (waitForCapture)
            {
                return new ValueTask<PlaylistLampAggregationRequest>(
                    CaptureRelease.Task.WaitAsync(cancellationToken));
            }
            lock (gate)
            {
                return ValueTask.FromResult(request);
            }
        }

        internal void Replace(PlaylistLampAggregationRequest next)
        {
            lock (gate)
            {
                request = next ?? throw new ArgumentNullException(nameof(next));
            }
        }

        internal void Raise(string playlistId, long sequence)
        {
            EventHandler<PlaylistLampViewerSourceChangedEventArgs>? handler;
            lock (gate)
            {
                handler = changed;
            }
            handler?.Invoke(
                this,
                new PlaylistLampViewerSourceChangedEventArgs(playlistId, sequence));
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            DisposeCount++;
            lock (gate)
            {
                changed = null;
            }
        }
    }

    private sealed class ActualReadySource : IPlaylistLampViewerDataSource, IDisposable
    {
        private readonly BmsLibraryPlaylistLampDataSource inner;

        private readonly string playlistId;

        private int disposed;

        internal ActualReadySource(BMSPlaylist playlist, BMSLibrary library, string playlistId)
        {
            inner = new BmsLibraryPlaylistLampDataSource(playlist, library);
            this.playlistId = playlistId;
            inner.Changed += InnerChanged;
        }

        internal int DisposeCount { get; private set; }

        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs>? Changed;

        public async ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            PlaylistLampViewerQuery requestedQuery,
            CancellationToken cancellationToken)
        {
            PlaylistLampAggregationRequest captured = await inner
                .CaptureAsync(requestedQuery, cancellationToken)
                .ConfigureAwait(false);
            return captured.InputState == PlaylistLampInputState.Loaded
                ? CreateReadyRequest(playlistId)
                : captured;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }
            inner.Changed -= InnerChanged;
            inner.Dispose();
            DisposeCount++;
            Changed = null;
        }

        private void InnerChanged(object? sender, PlaylistLampViewerSourceChangedEventArgs e)
        {
            Changed?.Invoke(this, e);
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        private readonly object gate = new();

        internal List<UiMessageRequest> Messages { get; } = [];

        internal TaskCompletionSource<UiMessageRequest> FirstMessage { get; } =
            Completion<UiMessageRequest>();

        internal TaskCompletionSource<UiDialogResult>? BlockedMessageResult { get; set; }

        internal int MessageCount
        {
            get
            {
                lock (gate)
                {
                    return Messages.Count;
                }
            }
        }

        public Task<UiDialogResult> ShowMessageAsync(
            UiMessageRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                Messages.Add(request);
            }
            FirstMessage.TrySetResult(request);
            TaskCompletionSource<UiDialogResult>? blocked = BlockedMessageResult;
            if (blocked != null)
            {
                return blocked.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(
                UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
        }

        public Task<UiDialogResult> ConfirmAsync(
            UiConfirmationRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window
            => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(
            UiFilePickerRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(
            UiSaveFilePickerRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
