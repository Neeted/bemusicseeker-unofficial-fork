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

    /// <summary>初回Deletedは所有者付き通知一回を維持し、未表示Windowの実Closed・両所有集合からの除去と情報源解放を一回行います。不可視Windowを回収済みとする誤りを検出します。</summary>
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
                fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
                Assert.AreEqual(1, source.ChangedRemoveAttemptCount);
                Assert.AreEqual(1, source.DisposeAttemptCount);
                Assert.AreEqual(1, source.SyncDisposeCallCount);
                Assert.AreEqual(1, source.DisposeCount);
            },
            prepareWindowPresentation: false);
    }

    /// <summary>
    /// 初回Failedは原因付き通知を一回表示して未表示Windowを実回収します。先に表示した正常viewerの
    /// 表示・segment操作・sourceを維持し、不可視Windowの残留と別viewerの巻込みを検出します。
    /// </summary>
    [TestMethod]
    public void Manager_initialFailedShowsOneLocalizedDialogAndNoWindow()
    {
        var source = new ControlledLampSource(
            PlaylistLampAggregationRequest.Failed("1", "initial aggregation failure"));
        var healthySource = new ControlledLampSource(CreateReadyRequest("2"));
        int sourceCount = 0;
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => ++sourceCount == 1 ? healthySource : source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> healthyOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(healthyOpen, "manager.initial-failed.healthy-open");
                PlaylistLampViewerWindow healthy = healthyOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("初回Failedと独立した正常viewerが開かなかった。");
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.initial-failed.open");

                Assert.IsNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(1, fixture.Manager.Count);
                Assert.AreEqual(2, fixture.PreparedWindowCount);
                Assert.AreEqual(1, dialogs.MessageCount);
                StringAssert.Contains(
                    dialogs.Messages.Single().MessageBoxText,
                    "initial aggregation failure");
                Assert.AreSame(fixture.Owner, dialogs.Messages.Single().Owner);
                fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
                Assert.AreEqual(1, source.ChangedRemoveAttemptCount);
                Assert.AreEqual(1, source.DisposeAttemptCount);
                Assert.AreEqual(1, source.SyncDisposeCallCount);
                Assert.AreEqual(1, source.DisposeCount);
                Assert.IsTrue(healthy.IsVisible);
                Assert.IsTrue(Application.Current.Windows.OfType<Window>().Contains(healthy));
                PlaylistLampViewerSegmentViewModel segment = healthy.ViewModel.RankSegments.First(item => item.IsInvokable);
                healthy.ViewModel.InvokeSegment(segment);
                Assert.IsTrue(segment.IsSelected);
                Assert.AreEqual(0, healthySource.ChangedRemoveAttemptCount);
                Assert.AreEqual(0, healthySource.DisposeAttemptCount);
            },
            shouldPrepareWindowPresentation: window => window.ViewModel.PlaylistId == "2");
    }

    /// <summary>初回Failed/Deletedの通知がthrowまたはFailedでも、未表示Windowの実Closed・両集合からの除去とsource解放を一回確認し、通知故障が後続drainを汚染しません。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Manager_initialTerminalNotificationFailureStillCleansSource(bool deleted, bool notificationThrows)
    {
        var source = new ControlledLampSource(deleted
            ? PlaylistLampAggregationRequest.Deleted("1")
            : PlaylistLampAggregationRequest.Failed("1", "initial failure"));
        var dialogs = new RecordingDialogService
        {
            MessageFailure = notificationThrows ? new IOException("optional initial notice") : null,
            MessageResult = UiDialogResult.NotShown(UiDialogStatus.Failed)
        };
        RunScenario(dialogs, _ => source, fixture =>
        {
            Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "initial optional notice terminal");
            Assert.IsNull(open.GetAwaiter().GetResult());
            Assert.AreEqual(1, dialogs.MessageCount);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "initial optional notice clean drain");
            fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
            Assert.AreEqual(1, source.ChangedRemoveAttemptCount);
            Assert.AreEqual(1, source.DisposeAttemptCount);
            Assert.AreEqual(1, source.SyncDisposeCallCount);
            Assert.AreEqual(1, source.DisposeCount);
            Assert.AreEqual(0, fixture.Manager.Count);
        }, prepareWindowPresentation: false);
    }

    /// <summary>factory失敗はWindowを生成せず、prepare失敗は未表示Windowを実Closed・両集合除去まで回収します。元故障と必須解放故障を保持し、成功した回収へ通常open故障を持ち越しません。</summary>
    [DataTestMethod]
    [DataRow(0)] // factory自体がthrow。
    [DataRow(1)] // factory成功後のwindow準備がthrow。
    [DataRow(2)] // 準備とsource解放の両方がthrow。
    public void Manager_creationFailurePreservesRequiredFailureAndAttemptsCleanup(int failureStage)
    {
        var failure = new IOException("required lamp creation marker");
        var cleanupFailure = new IOException("required lamp source cleanup marker");
        var source = new ControlledLampSource(CreateReadyRequest("1"))
        {
            DisposeFailure = failureStage == 2 ? cleanupFailure : null
        };
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs,
            _ => failureStage == 0 ? throw failure : source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                Exception observed = CaptureTaskFailure(open, "lamp creation failure");
                Assert.IsTrue(GetFailureLeaves(observed).Any(cause => ReferenceEquals(cause, failure)));
                if (failureStage == 2)
                {
                    Assert.IsTrue(GetFailureLeaves(observed).Any(cause => ReferenceEquals(cause, cleanupFailure)));
                    Exception drainFailure = CaptureTaskFailure(fixture.Manager.WaitForCompletionAsync(), "lamp failed creation cleanup drain");
                    AssertCleanupCauses(drainFailure, [cleanupFailure]);
                    fixture.RecordObservedManagerCleanupFailure(drainFailure);
                }
                else
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "lamp ordinary creation failure clean drain");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.DisposeAsync().AsTask(), "lamp ordinary creation failure clean shutdown");
                }
                if (failureStage == 0) { Assert.AreEqual(0, fixture.PreparedWindowCount); }
                else
                {
                    Assert.AreEqual(1, fixture.PreparedWindowCount);
                    fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
                }
                Assert.AreEqual(failureStage == 0 ? 0 : 1, source.ChangedRemoveAttemptCount);
                Assert.AreEqual(failureStage == 0 ? 0 : 1, source.DisposeAttemptCount);
                Assert.AreEqual(failureStage == 0 ? 0 : 1, source.SyncDisposeCallCount);
                Assert.AreEqual(failureStage == 1 ? 1 : 0, source.DisposeCount);
                Assert.AreEqual(0, dialogs.MessageCount, "実生成・解放故障は呼出元のowning境界へ伝えます。");
                Assert.AreEqual(0, fixture.Manager.Count);
            },
            prepareWindowPresentation: false,
            prepareAdditional: failureStage == 0 ? null : _ => throw failure);
    }

    /// <summary>Readyの実Windowは表示後の最初のactivate時までOwnerを持ち、その後解除する。表示前activationと以後の重なり順をメインへ拘束する誤りを検出する。</summary>
    [TestMethod]
    public void Manager_readyPresentationReleasesOwnerAfterShow()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        var dialogs = new RecordingDialogService();
        int activationAttemptCount = 0;
        bool wasVisible = false;
        bool hadOwner = false;
        bool activatedForShow = true;
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
                wasVisible = window.IsVisible;
                hadOwner = window.Owner != null;
                activatedForShow = window.ShowActivated;
            });
        Assert.AreEqual(1, activationAttemptCount);
        Assert.IsTrue(wasVisible);
        Assert.IsTrue(hadOwner);
        Assert.IsFalse(activatedForShow);
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "lamp closed source actual drain");
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "lamp closed source actual drain");
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
    [DataTestMethod]
    [DataRow(0)] // 成功通知。
    [DataRow(1)] // gate後にFailed。
    [DataRow(2)] // gate後にfaulted Task。
    [DataRow(3)] // 呼出し自体が同期throw。
    public void Manager_liveFailedShowsOneDialogAndClosesTargetAfterRepeatedSignals(int notificationFailureKind)
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        var otherSource = new ControlledLampSource(CreateReadyRequest("2"));
        int sourceCount = 0;
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => ++sourceCount == 1 ? source : otherSource,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.live-failed.open");
                PlaylistLampViewerWindow window = open.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("表示中の失敗を確認するビューアが開かなかった。");

                Task<PlaylistLampViewerWindow> otherOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOpen, "manager.live-failed.other-open");
                PlaylistLampViewerWindow otherWindow = otherOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("別対象の窓が開かなかった。");
                int closedCount = 0;
                TaskCompletionSource<bool> closed = Completion<bool>();
                window.Closed += (_, _) =>
                {
                    closedCount++;
                    closed.TrySetResult(true);
                };
                TaskCompletionSource<UiDialogResult> blockedMessage = Completion<UiDialogResult>();
                dialogs.BlockedMessageResult = notificationFailureKind == 3 ? null : blockedMessage;
                dialogs.MessageFailure = notificationFailureKind == 3 ? new IOException("optional live notice") : null;

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

                    if (notificationFailureKind == 2)
                    {
                        blockedMessage.TrySetException(new IOException("optional live notice task"));
                    }
                    else
                    {
                        blockedMessage.TrySetResult(notificationFailureKind == 1
                            ? UiDialogResult.NotShown(UiDialogStatus.Failed)
                            : UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    }
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "manager.live-failed.closed");

                    Assert.AreEqual(1, closedCount);
                    Assert.AreEqual(1, fixture.Manager.Count);
                    Assert.IsTrue(otherWindow.IsVisible);
                    Assert.AreEqual(0, otherSource.DisposeCount);
                    Assert.AreEqual(1, dialogs.MessageCount);
                    Assert.AreSame(fixture.Owner, dialogs.Messages.Single().Owner);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "lamp terminal source actual drain");
                    Assert.AreEqual(1, source.DisposeCount);
                }
                finally
                {
                    blockedMessage.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    dialogs.MessageFailure = null;
                    fixture.Manager.Dispose();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "manager.live-failed.finally-tasks");
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "manager.live-failed.finally-closed");
                }
            });
    }

    /// <summary>live Failedと実native Closeの実解放故障をownerに残し、取消を無視する捕捉をdrainした後も重複・後続shutdownへ返して再試行しません。</summary>
    [DataTestMethod]
    [DataRow(true)] // live通知のgate後故障と実VM/source解放故障を区別します。
    [DataRow(false)] // 実Closeの同期漏れを防ぎ、同じ解放結果をshutdownへ残します。
    public void Manager_requiredCleanupFailureSurvivesCloseAndOverlappingShutdown(bool liveFailure)
    {
        var viewModelCleanupFailure = new IOException("required lamp subscription release marker");
        var sourceCleanupFailure = new IOException("required lamp source release marker");
        var optionalLiveNoticeFailure = new IOException("optional live notice gate failure");
        var optionalOperationNoticeFailure = new IOException("optional operation notice failure");
        ControlledLampSource targetSource = liveFailure
            ? new AsyncControlledLampSource(CreateReadyRequest("1"))
            : new ControlledLampSource(CreateReadyRequest("1"));
        targetSource.ChangedRemoveFailure = viewModelCleanupFailure;
        targetSource.DisposeFailure = sourceCleanupFailure;
        var healthySource = new ControlledLampSource(CreateReadyRequest("2"));
        var pendingSource = new ControlledLampSource(CreateReadyRequest("3"), waitForCapture: true,
            ignoreCaptureCancellation: true);
        int sourceCount = 0;
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs,
            _ => (++sourceCount) switch
            {
                1 => targetSource,
                2 => healthySource,
                _ => pendingSource
            },
            fixture =>
            {
                Task<PlaylistLampViewerWindow>? targetOpen = null;
                Task<PlaylistLampViewerWindow>? healthyOpen = null;
                Task<PlaylistLampViewerWindow>? pendingOpen = null;
                Task? firstShutdown = null;
                Task? secondShutdown = null;
                TaskCompletionSource<UiDialogResult> releaseLiveNotice = Completion<UiDialogResult>();
                try
                {
                    targetOpen = fixture.Manager.TryOpenAsync(fixture.Context);
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(targetOpen, "cleanup regression target open");
                    PlaylistLampViewerWindow target = targetOpen.GetAwaiter().GetResult()
                        ?? throw new AssertFailedException("Target viewer was not shown.");
                    healthyOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext());
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(healthyOpen, "cleanup regression healthy open");
                    PlaylistLampViewerWindow healthy = healthyOpen.GetAwaiter().GetResult()
                        ?? throw new AssertFailedException("Healthy viewer was not shown.");
                    int closedCount = 0;
                    int detachedCount = 0;
                    TaskCompletionSource<bool> targetClosed = Completion<bool>();
                    target.Closed += (_, _) => { closedCount++; targetClosed.TrySetResult(true); };
                    target.DataContextChanged += (_, args) => { if (args.NewValue == null) { detachedCount++; } };
                    if (liveFailure)
                    {
                        dialogs.BlockedMessageResult = releaseLiveNotice;
                        targetSource.Replace(PlaylistLampAggregationRequest.Failed("1", "live aggregation failure"));
                        targetSource.Raise("1", 1);
                        TestUiDispatcherHost.AwaitTaskOnDispatcher(dialogs.FirstMessage.Task, "cleanup regression live notice started");
                        Assert.IsFalse(targetClosed.Task.IsCompleted);
                        targetSource.Raise("1", 2);
                        Assert.AreEqual(1, dialogs.MessageCount);
                        dialogs.BlockedMessageResult = null;
                        dialogs.MessageResult = UiDialogResult.NotShown(UiDialogStatus.Failed);
                        releaseLiveNotice.TrySetException(optionalLiveNoticeFailure);
                    }
                    else
                    {
                        dialogs.MessageFailure = optionalOperationNoticeFailure;
                        Exception? closeFailure = null;
                        try { target.Close(); }
                        catch (Exception exception) { closeFailure = exception; }
                        Assert.IsNull(closeFailure, "実Closedの回収故障をDispatcherへ漏らしません。");
                    }
                    TestUiDispatcherHost.AwaitPresentationOnDispatcher(targetClosed.Task, "cleanup regression actual target closed");
                    Assert.AreEqual(1, closedCount);
                    Assert.AreEqual(1, detachedCount);
                    Assert.IsNull(target.DataContext);
                    Assert.IsFalse(target.IsVisible);
                    Exception targetDrainFailure = CaptureTaskFailure(fixture.Manager.WaitForCompletionAsync(), "cleanup regression target actual drain");
                    AssertCleanupCauses(targetDrainFailure, [viewModelCleanupFailure, sourceCleanupFailure]);
                    Assert.AreEqual(1, targetSource.ChangedRemoveAttemptCount);
                    Assert.AreEqual(1, targetSource.DisposeAttemptCount);
                    Assert.AreEqual(0, targetSource.DisposeCount, "実解放前に失敗した情報源を解放済みにしません。");
                    Assert.IsTrue(healthy.IsVisible);
                    Assert.AreEqual(0, healthySource.DisposeAttemptCount);
                    Assert.AreEqual(1, fixture.Manager.Count);

                    pendingOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext(3));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(pendingSource.CaptureStarted.Task, "cleanup regression pending capture started");
                    firstShutdown = fixture.Manager.DisposeAsync().AsTask();
                    secondShutdown = fixture.Manager.DisposeAsync().AsTask();
                    Assert.IsFalse(firstShutdown.IsCompleted);
                    Assert.IsFalse(secondShutdown.IsCompleted);
                    Assert.IsFalse(pendingOpen.IsCompleted);
                    Assert.IsFalse(healthy.IsVisible);
                    Assert.AreEqual(0, pendingSource.DisposeAttemptCount);
                    pendingSource.CaptureRelease.TrySetResult(CreateReadyRequest("3"));
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(pendingOpen, "cleanup regression pending open terminal");
                    Assert.IsNull(pendingOpen.GetAwaiter().GetResult());
                    Exception firstFailure = CaptureTaskFailure(firstShutdown, "cleanup regression first shutdown");
                    Exception secondFailure = CaptureTaskFailure(secondShutdown, "cleanup regression overlapping shutdown");
                    Exception laterFailure = CaptureTaskFailure(fixture.Manager.WaitForCompletionAsync(), "cleanup regression later drain");
                    foreach (Exception observed in new[] { firstFailure, secondFailure, laterFailure })
                    {
                        AssertCleanupCauses(observed, [viewModelCleanupFailure, sourceCleanupFailure]);
                        Assert.IsFalse(GetFailureLeaves(observed).Any(cause => ReferenceEquals(cause, optionalLiveNoticeFailure)
                            || ReferenceEquals(cause, optionalOperationNoticeFailure)));
                    }
                    fixture.RecordObservedManagerCleanupFailure(laterFailure);
                    Assert.AreEqual(1, pendingSource.DisposeAttemptCount);
                    Assert.AreEqual(1, pendingSource.DisposeCount);
                    Assert.AreEqual(1, targetSource.ChangedRemoveAttemptCount);
                    Assert.AreEqual(1, targetSource.DisposeAttemptCount, "後続drainで実解放を再試行しません。");
                    if (targetSource is AsyncControlledLampSource asyncSource)
                    {
                        Assert.AreEqual(1, asyncSource.AsyncDisposeCallCount);
                        Assert.AreEqual(0, asyncSource.SyncDisposeCallCount);
                    }
                    else { Assert.AreEqual(1, targetSource.SyncDisposeCallCount); }
                    Assert.AreEqual(1, healthySource.DisposeAttemptCount);
                    Assert.AreEqual(1, closedCount);
                    Assert.AreEqual(1, detachedCount);
                    Assert.AreEqual(0, fixture.Manager.Count);
                }
                finally
                {
                    dialogs.BlockedMessageResult = null;
                    dialogs.MessageFailure = null;
                    releaseLiveNotice.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
                    pendingSource.CaptureRelease.TrySetResult(CreateReadyRequest("3"));
                    var joins = new List<Task> { fixture.Manager.DisposeAsync().AsTask() };
                    if (targetOpen != null) { joins.Add(targetOpen); }
                    if (healthyOpen != null) { joins.Add(healthyOpen); }
                    if (pendingOpen != null) { joins.Add(pendingOpen); }
                    if (firstShutdown != null) { joins.Add(firstShutdown); }
                    if (secondShutdown != null) { joins.Add(secondShutdown); }
                    try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(joins), "cleanup regression finally actual tasks"); }
                    catch (Exception exception) when (GetFailureLeaves(exception).Any()
                        && GetFailureLeaves(exception).All(cause => ReferenceEquals(cause, viewModelCleanupFailure)
                        || ReferenceEquals(cause, sourceCleanupFailure)))
                    {
                        // このケースが注入した実解放故障だけを区別し、全Taskのjoinを終えています。
                    }
                }
            },
            shouldPrepareWindowPresentation: window => window.ViewModel.PlaylistId is "1" or "2");
    }

    /// <summary>
    /// 初回capture中のowner終了またはcaller取消は、取消に応じないcaptureも実終端まで所有します。
    /// gate解放後にopen/drain・未表示Windowの実Closed/両集合除去・source解放を確認し、遅い表示や通知を禁止します。
    /// </summary>
    [DataTestMethod]
    [DataRow("dispose", false)] // 同期owner終了、captureは取消を遵守。
    [DataRow("disposeAsync", false)] // 非同期owner終了、captureは取消を遵守。
    [DataRow("dispose", true)] // 同期owner終了でも取消を無視するcaptureの実終端を待つ。
    [DataRow("disposeAsync", true)] // 非同期owner終了でも取消を無視するcaptureの実終端を待つ。
    [DataRow("caller", false)] // caller tokenから初回openを取り消す。
    [DataRow("caller", true)] // caller取消後もcaptureが返るまでsourceを保持。
    public void Manager_pendingOpenCancellationReclaimsUnshownWindowWithoutDialog(string cancellationMethod, bool ignoreCaptureCancellation)
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"), waitForCapture: true,
            ignoreCaptureCancellation: ignoreCaptureCancellation);
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            _ => source,
            fixture =>
            {
                using var callerCancellation = new CancellationTokenSource();
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context, callerCancellation.Token);
                Task? drain = null;
                try
                {
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        source.CaptureStarted.Task,
                        "manager.pending-cancel.capture-started");

                    if (cancellationMethod == "disposeAsync") { drain = fixture.Manager.DisposeAsync().AsTask(); }
                    else if (cancellationMethod == "caller")
                    {
                        callerCancellation.Cancel();
                        drain = fixture.Manager.WaitForCompletionAsync();
                    }
                    else
                    {
                        fixture.Manager.Dispose();
                        drain = fixture.Manager.WaitForCompletionAsync();
                    }
                    if (ignoreCaptureCancellation)
                    {
                        Assert.IsFalse(drain.IsCompleted);
                        Assert.IsFalse(open.IsCompleted);
                        Assert.AreEqual(0, source.DisposeAttemptCount, "実captureの終端前にsource解放を開始しません。");
                        source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.pending-cancel.open-canceled");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(drain, "manager.pending-cancel.drain-terminal");

                    Assert.IsNull(open.GetAwaiter().GetResult());
                    Assert.AreEqual(0, fixture.Manager.Count);
                    Assert.AreEqual(0, dialogs.MessageCount);
                    fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
                    Assert.AreEqual(1, source.ChangedRemoveAttemptCount);
                    Assert.AreEqual(1, source.DisposeAttemptCount);
                    Assert.AreEqual(1, source.SyncDisposeCallCount);
                    Assert.AreEqual(1, source.DisposeCount);
                }
                finally
                {
                    source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                    fixture.Manager.Dispose();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(
                        Task.WhenAll(open, drain ?? fixture.Manager.WaitForCompletionAsync(), fixture.Manager.DisposeAsync().AsTask()),
                        "manager.pending-cancel.finally-actual-tasks");
                }
            },
            prepareWindowPresentation: false);
    }

    /// <summary>
    /// 実live ChangedとVMの日付変更で取消を無視するcaptureを二つ受理し、native Close/owner終了を先行させます。
    /// 最新だけを返してもsourceを解放せず、旧captureまで回収後に一回解放し、遅いReady/Failedを再表示しません。
    /// </summary>
    /// <remarks>実WindowのClosed、VMの日付入口と別窓の表示/操作を観測するため、同じpresentation scopeを使います。</remarks>
    [DataTestMethod]
    [DataRow(false, false)] // 手動閉鎖後の遅い成功と別窓独立性。
    [DataRow(false, true)] // 手動閉鎖後の遅い失敗でも通知を再表示しません。
    [DataRow(true, false)] // owner終了後の遅い成功。
    [DataRow(true, true)] // owner終了後の遅い失敗。
    public void Manager_closeDrainsLiveAndSupersededDateCapturesBeforeSourceRelease(bool shutdown, bool lateFailure)
    {
        var source = new AsyncControlledLampSource(CreateReadyRequest("1"));
        var otherSource = new ControlledLampSource(CreateReadyRequest("2"));
        int sourceCount = 0;
        var held = new List<(PlaylistLampViewerQuery Query, TaskCompletionSource<PlaylistLampAggregationRequest> Completion)>();
        TaskCompletionSource<bool> captureChanged = Completion<bool>();
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs, _ => ++sourceCount == 1 ? source : otherSource, fixture =>
        {
            Task? drain = null;
            try
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "live drain target open");
                PlaylistLampViewerWindow target = open.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("live drain対象が開かなかった。");
                Task<PlaylistLampViewerWindow> otherOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOpen, "live drain independent open");
                PlaylistLampViewerWindow other = otherOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("live drain別窓が開かなかった。");
                source.CaptureOverride = (query, _) =>
                {
                    TaskCompletionSource<PlaylistLampAggregationRequest> completion = Completion<PlaylistLampAggregationRequest>();
                    lock (held)
                    {
                        held.Add((query, completion));
                        captureChanged.TrySetResult(true);
                        captureChanged = Completion<bool>();
                    }
                    return new ValueTask<PlaylistLampAggregationRequest>(completion.Task);
                };
                source.Raise("1", 1);
                waitForCaptures(1);
                target.ViewModel.SelectedAsOfDate = new DateTime(2026, 8, 27);
                waitForCaptures(2);
                TaskCompletionSource<bool> closed = Completion<bool>();
                target.Closed += (_, _) => closed.TrySetResult(true);
                if (shutdown) { drain = fixture.Manager.DisposeAsync().AsTask(); }
                else
                {
                    target.Close();
                    drain = fixture.Manager.WaitForCompletionAsync();
                }
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "live drain actual native close");
                Assert.IsFalse(drain.IsCompleted);
                Assert.AreEqual(0, source.DisposeAttemptCount);
                if (!shutdown)
                {
                    Assert.IsTrue(other.IsVisible);
                    PlaylistLampViewerSegmentViewModel segment = other.ViewModel.RankSegments.First(item => item.IsInvokable);
                    other.ViewModel.InvokeSegment(segment);
                    Assert.IsTrue(segment.IsSelected);
                    Assert.AreEqual(0, otherSource.DisposeAttemptCount);
                }
                held[1].Completion.TrySetResult(lateFailure
                    ? PlaylistLampAggregationRequest.Failed("1", "late date failure", query: held[1].Query)
                    : CreateReadyRequest("1", held[1].Query));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(held[1].Completion.Task, "live drain latest actual capture");
                Assert.IsFalse(drain.IsCompleted, "取消を無視する旧live captureも実終端まで所有します。");
                Assert.AreEqual(0, source.DisposeAttemptCount);
                held[0].Completion.TrySetResult(lateFailure
                    ? PlaylistLampAggregationRequest.Failed("1", "late live failure", query: held[0].Query)
                    : CreateReadyRequest("1", held[0].Query));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(held.Select(item => (Task)item.Completion.Task).Append(drain)), "live drain all actual captures and cleanup");
                Assert.AreEqual(1, source.AsyncDisposeCallCount);
                Assert.AreEqual(0, source.SyncDisposeCallCount);
                Assert.AreEqual(1, source.DisposeCount);
                Assert.IsNull(target.DataContext);
                Assert.IsFalse(target.IsVisible);
                Assert.AreEqual(0, dialogs.MessageCount);
                if (!shutdown) { Assert.IsTrue(other.IsVisible); }
            }
            finally
            {
                fixture.Manager.Dispose();
                lock (held)
                {
                    foreach ((PlaylistLampViewerQuery Query, TaskCompletionSource<PlaylistLampAggregationRequest> Completion) item in held) { item.Completion.TrySetResult(CreateReadyRequest("1", item.Query)); }
                }
                IEnumerable<Task> joins = held.Select(item => (Task)item.Completion.Task).Append(fixture.Manager.DisposeAsync().AsTask());
                if (drain != null) { joins = joins.Append(drain); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(joins), "live drain finally actual tasks");
            }

            void waitForCaptures(int count)
            {
                while (true)
                {
                    Task changed;
                    lock (held)
                    {
                        if (held.Count >= count) { return; }
                        changed = captureChanged.Task;
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(changed, "live drain accepted capture");
                }
            }
        });
    }

    /// <summary>
    /// 初回open token由来の取消callbackをholdしても、実Close/DisposeAsyncはnative Closedと
    /// Dispatcher pulseを先に実行し、source解放とdrainはcallback・captureの実終端まで待ちます。
    /// </summary>
    /// <remarks>UI外driverが実DispatcherOperationを期限付き観測し、旧同期Cancelでもfinallyからgateを解放できる構成です。</remarks>
    [DataTestMethod]
    [DataRow(false)] // 未表示初回窓への実Close。
    [DataRow(true)] // owner shutdown由来のlinked取消。
    public void Manager_initialCancellationCallbackDoesNotBlockNativeCloseOrDispatcher(bool shutdown)
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        using var callbackRelease = new ManualResetEventSlim();
        TaskCompletionSource<bool> callbackEntered = Completion<bool>();
        TaskCompletionSource<PlaylistLampAggregationRequest> captureRelease = Completion<PlaylistLampAggregationRequest>();
        source.CaptureOverride = (_, token) =>
        {
            token.Register(() =>
            {
                callbackEntered.TrySetResult(true);
                callbackRelease.Wait();
            });
            return new ValueTask<PlaylistLampAggregationRequest>(captureRelease.Task);
        };
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs, _ => source, fixture =>
        {
            Task<PlaylistLampViewerWindow>? open = null;
            Task? drain = null;
            Task? driver = null;
            try
            {
                open = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(source.CaptureStarted.Task, "callback drain initial capture started");
                PlaylistLampViewerWindow pendingWindow = fixture.PreparedWindow;
                TaskCompletionSource<bool> closed = Completion<bool>();
                pendingWindow.Closed += (_, _) => closed.TrySetResult(true);
                driver = Task.Run(async () =>
                {
                    try
                    {
                        System.Windows.Threading.DispatcherOperation closeOperation = fixture.Owner.Dispatcher.InvokeAsync(() =>
                        {
                            if (shutdown) { drain = fixture.Manager.DisposeAsync().AsTask(); }
                            else
                            {
                                pendingWindow.Close();
                                drain = fixture.Manager.WaitForCompletionAsync();
                            }
                        });
                        await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await closeOperation.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        bool pulseExecuted = false;
                        System.Windows.Threading.DispatcherOperation<bool> pulse = fixture.Owner.Dispatcher.InvokeAsync(() => pulseExecuted = true);
                        await pulse.Task.WaitAsync(TimeSpan.FromSeconds(5));
                        Assert.IsTrue(pulseExecuted, "経過時間ではなく実DispatcherOperationの実行を確認します。");
                        Assert.IsNotNull(drain);
                        Assert.IsFalse(drain.IsCompleted);
                        Assert.AreEqual(0, source.DisposeAttemptCount);
                    }
                    finally
                    {
                        callbackRelease.Set();
                        captureRelease.TrySetResult(CreateReadyRequest("1"));
                    }
                });
                TestUiDispatcherHost.AwaitTaskOnDispatcher(driver, "callback drain UI-independent driver");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(open, drain ?? fixture.Manager.WaitForCompletionAsync()), "callback drain all actual tasks");
                Assert.IsNull(open.GetAwaiter().GetResult());
                fixture.AssertUnshownWindowReclaimed(pendingWindow);
                Assert.AreEqual(1, source.ChangedRemoveAttemptCount);
                Assert.AreEqual(1, source.DisposeAttemptCount);
                Assert.AreEqual(1, source.SyncDisposeCallCount);
                Assert.AreEqual(1, source.DisposeCount);
                Assert.AreEqual(0, dialogs.MessageCount);
            }
            finally
            {
                callbackRelease.Set();
                captureRelease.TrySetResult(CreateReadyRequest("1"));
                fixture.Manager.Dispose();
                var joins = new List<Task> { fixture.Manager.DisposeAsync().AsTask() };
                if (open != null) { joins.Add(open); }
                if (drain != null) { joins.Add(drain); }
                if (driver != null) { joins.Add(driver); }
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(joins), "callback drain finally actual tasks");
            }
        }, prepareWindowPresentation: false);
    }

    /// <summary>
    /// 全生成Windowを受動観測し、実表示する対象だけをnative表示policyの検証へ登録します。
    /// 未表示のまま閉じる対象を登録すると、Closing時のHWND検証と未表示契約が衝突するため選別します。
    /// </summary>
    private static void RunScenario(
        IUiDialogService dialogs,
        Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> sourceFactory,
        Action<ManagerTestFixture> test,
        bool prepareWindowPresentation = true,
        Action<PlaylistLampViewerWindow>? activateWindowForInitialPresentation = null,
        Action<PlaylistLampViewerWindow>? prepareAdditional = null,
        Func<PlaylistLampViewerWindow, bool>? shouldPrepareWindowPresentation = null)
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
                                fixture?.RecordPreparedWindow(window);
                                if (prepareWindowPresentation && (shouldPrepareWindowPresentation?.Invoke(window) ?? true))
                                {
                                    presentationScope.PrepareForOwnedPresentation(
                                        window);
                                }
                                prepareAdditional?.Invoke(window);
                            },
                            activateWindowForInitialPresentation);
                        test(fixture!);
                        foreach ((bool loaded, bool visible, nint handle) in fixture!.PreparationObservations)
                        {
                            Assert.IsFalse(loaded);
                            Assert.IsFalse(visible);
                            Assert.AreEqual(nint.Zero, handle);
                        }
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

    private static PlaylistLampAggregationRequest CreateReadyRequest(string playlistId, PlaylistLampViewerQuery? query = null)
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
            new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
            query: query);
    }

    private static Exception CaptureTaskFailure(Task task, string context)
    {
        Exception? failure = null;
        try { TestUiDispatcherHost.AwaitTaskOnDispatcher(task, context); }
        catch (Exception exception) { failure = exception; }
        return failure ?? throw new AssertFailedException("実解放の未確認結果を成功へ変えません。");
    }

    private static IEnumerable<Exception> GetFailureLeaves(Exception failure)
    {
        if (failure is AggregateException aggregate)
        {
            foreach (Exception inner in aggregate.InnerExceptions)
            {
                foreach (Exception cause in GetFailureLeaves(inner)) { yield return cause; }
            }
        }
        else if (failure.InnerException != null)
        {
            foreach (Exception cause in GetFailureLeaves(failure.InnerException)) { yield return cause; }
        }
        else { yield return failure; }
    }

    private static void AssertCleanupCauses(Exception failure, IReadOnlyCollection<Exception> expected)
    {
        Exception[] causes = GetFailureLeaves(failure).ToArray();
        foreach (Exception cause in expected) { Assert.IsTrue(causes.Any(actual => ReferenceEquals(actual, cause))); }
        Assert.IsTrue(causes.All(actual => expected.Any(cause => ReferenceEquals(actual, cause))),
            "任意通知や通常openの元故障を実解放結果へ混入しません。");
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

        private readonly Dictionary<PlaylistLampViewerWindow, PreparedWindowObservation> windowObservations = [];

        /// <summary>初期所属と実イベントだけを保存し、callback内では合否を表明しません。</summary>
        private sealed class PreparedWindowObservation(bool inApplication, bool inOwner)
        {
            internal bool InitiallyInApplication { get; } = inApplication;
            internal bool InitiallyInOwner { get; } = inOwner;
            internal int ClosedCount;
            internal int DataContextReleasedCount;
            internal int LoadedCount;
            internal bool BecameVisible;
            internal TaskCompletionSource<bool> Closed { get; } = Completion<bool>();
        }

        internal List<(bool Loaded, bool Visible, nint Handle)> PreparationObservations { get; } = [];

        internal int PreparedWindowCount => preparedWindows.Count;

        internal PlaylistLampViewerWindow PreparedWindow => preparedWindows.Last();

        internal void RecordPreparedWindow(PlaylistLampViewerWindow window)
        {
            ArgumentNullException.ThrowIfNull(window);
            PreparationObservations.Add((window.IsLoaded, window.IsVisible, TestWindowPresentationScope.GetNativeHandle(window)));
            preparedWindows.Add(window);
            var observation = new PreparedWindowObservation(
                Application.Current.Windows.OfType<Window>().Contains(window),
                Owner.OwnedWindows.OfType<Window>().Contains(window));
            windowObservations.Add(window, observation);
            window.Closed += (_, _) =>
            {
                observation.ClosedCount++;
                observation.Closed.TrySetResult(true);
            };
            window.DataContextChanged += (_, args) =>
            {
                if (args.NewValue == null) { observation.DataContextReleasedCount++; }
            };
            window.Loaded += (_, _) => observation.LoadedCount++;
            window.IsVisibleChanged += (_, args) =>
            {
                if (args.NewValue is true) { observation.BecameVisible = true; }
            };
        }

        /// <summary>fixture/presentation scopeの救済前に、対象の未表示Windowの実閉鎖と両集合からの除去を確認します。</summary>
        internal void AssertUnshownWindowReclaimed(PlaylistLampViewerWindow window)
        {
            PreparedWindowObservation observation = windowObservations[window];
            TestUiDispatcherHost.AwaitPresentationOnDispatcher(observation.Closed.Task, "lamp unshown window actual Closed");
            Assert.IsTrue(observation.InitiallyInApplication);
            Assert.IsTrue(observation.InitiallyInOwner);
            Assert.AreEqual(1, observation.ClosedCount);
            Assert.AreEqual(1, observation.DataContextReleasedCount);
            Assert.AreEqual(0, observation.LoadedCount);
            Assert.IsFalse(observation.BecameVisible);
            Assert.IsNull(window.DataContext);
            Assert.IsFalse(window.IsVisible);
            Assert.IsFalse(Application.Current.Windows.OfType<Window>().Contains(window));
            Assert.IsFalse(Owner.OwnedWindows.OfType<Window>().Contains(window));
        }

        /// <summary>別窓または保留初回捕捉の独立した対象を作ります。</summary>
        internal PlaylistLampViewerOpenContext CreateOtherContext(int playlistId = 2)
        {
            BMSPlaylist otherPlaylist = BmsPlaylistTestSupport.CreatePlaylist(
                Path.Combine(root, "song.db"));
            BMSTable otherTable = CreateTable(playlistId, "Other manager fixture");
            otherPlaylist.BMSTables = new ObservableCollection<BMSTable> { otherTable };
            return CreateContext(otherPlaylist, otherTable, Library);
        }

        private readonly HashSet<Exception> observedManagerCleanupCauses = new(ReferenceEqualityComparer.Instance);

        /// <summary>テスト本体が表明済みの実解放結果だけを、fixture終了時の期待故障として記録します。</summary>
        internal void RecordObservedManagerCleanupFailure(Exception failure)
        {
            foreach (Exception cause in GetFailureLeaves(failure)) { observedManagerCleanupCauses.Add(cause); }
        }

        public void Dispose()
        {
            var failures = new List<Exception>();
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Manager.DisposeAsync().AsTask(), "lamp fixture dispose"); }
            catch (Exception exception)
            {
                Exception[] causes = GetFailureLeaves(exception).ToArray();
                if (observedManagerCleanupCauses.Count == 0 || causes.Length == 0
                    || !causes.All(observedManagerCleanupCauses.Contains)
                    || !observedManagerCleanupCauses.All(expected => causes.Any(cause => ReferenceEquals(cause, expected))))
                {
                    failures.Add(exception);
                }
            }
            try { workspace.Dispose(); }
            catch (Exception exception) { failures.Add(exception); }
            try { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
            catch (Exception exception) { failures.Add(exception); }
            if (failures.Count == 1) { ExceptionDispatchInfo.Capture(failures[0]).Throw(); }
            if (failures.Count > 1) { throw new AggregateException(failures); }
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

    private class ControlledLampSource : IPlaylistLampViewerDataSource, IDisposable
    {
        private readonly object gate = new();

        private readonly bool waitForCapture;

        private readonly bool ignoreCaptureCancellation;

        private PlaylistLampAggregationRequest request;

        private EventHandler<PlaylistLampViewerSourceChangedEventArgs>? changed;

        private int disposed;

        private int changedRemoveAttemptCount;

        private int disposeAttemptCount;

        /// <summary>初回捕捉gateを所有し、終了取消を即時に観測する場合と実Taskを待つ場合を分ける情報源を作ります。</summary>
        internal ControlledLampSource(PlaylistLampAggregationRequest request, bool waitForCapture = false,
            bool ignoreCaptureCancellation = false)
        {
            this.request = request ?? throw new ArgumentNullException(nameof(request));
            this.waitForCapture = waitForCapture;
            this.ignoreCaptureCancellation = ignoreCaptureCancellation;
        }

        internal TaskCompletionSource<bool> CaptureStarted { get; } = Completion<bool>();

        internal TaskCompletionSource<PlaylistLampAggregationRequest> CaptureRelease { get; } =
            Completion<PlaylistLampAggregationRequest>();

        internal int DisposeCount { get; private set; }

        internal Exception? DisposeFailure { get; set; }

        internal Func<PlaylistLampViewerQuery, CancellationToken, ValueTask<PlaylistLampAggregationRequest>>? CaptureOverride { get; set; }

        internal int SyncDisposeCallCount { get; private set; }

        internal Exception? ChangedRemoveFailure { get; set; }

        internal int ChangedRemoveAttemptCount => Volatile.Read(ref changedRemoveAttemptCount);

        internal int DisposeAttemptCount => Volatile.Read(ref disposeAttemptCount);

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
                Interlocked.Increment(ref changedRemoveAttemptCount);
                lock (gate)
                {
                    if (ChangedRemoveFailure != null) { throw ChangedRemoveFailure; }
                    changed -= value;
                }
            }
        }

        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            PlaylistLampViewerQuery query,
            CancellationToken cancellationToken)
        {
            CaptureStarted.TrySetResult(true);
            if (CaptureOverride != null) { return CaptureOverride(query, cancellationToken); }
            if (waitForCapture)
            {
                return new ValueTask<PlaylistLampAggregationRequest>(
                    ignoreCaptureCancellation ? CaptureRelease.Task : CaptureRelease.Task.WaitAsync(cancellationToken));
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
            SyncDisposeCallCount++;
            ReleaseSource();
        }

        protected void ReleaseSource()
        {
            Interlocked.Increment(ref disposeAttemptCount);
            if (DisposeFailure != null) { throw DisposeFailure; }
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

    private sealed class AsyncControlledLampSource : ControlledLampSource, IAsyncDisposable
    {
        internal AsyncControlledLampSource(PlaylistLampAggregationRequest request) : base(request) { }

        internal int AsyncDisposeCallCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            AsyncDisposeCallCount++;
            ReleaseSource();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ActualReadySource : IPlaylistLampViewerDataSource, IDisposable, IAsyncDisposable
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
                ? CreateReadyRequest(playlistId, requestedQuery)
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

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                inner.Changed -= InnerChanged;
                Changed = null;
                DisposeCount++;
            }
            await inner.DisposeAsync().ConfigureAwait(false);
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

        /// <summary>要求を記録した後、通知呼出しそのものへ注入する元故障を取得・設定します。</summary>
        internal Exception? MessageFailure { get; set; }

        /// <summary>gateを使わない通知の実表示結果を取得・設定します。</summary>
        internal UiDialogResult MessageResult { get; set; } =
            UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK);

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
            if (MessageFailure != null) { throw MessageFailure; }
            TaskCompletionSource<UiDialogResult>? blocked = BlockedMessageResult;
            if (blocked != null)
            {
                return blocked.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(MessageResult);
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
