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

    /// <summary>初回Deletedは所有者付き通知一回を維持し、未表示Windowの実Closed・両所有集合からの除去と開始済み処理を回収します。不可視Windowを回収済みとする誤りを検出します。</summary>
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
            },
            prepareWindowPresentation: false);
    }

    /// <summary>初回取得失敗でも窓を表示し、依存操作を止めて正常な別窓を維持する。Failedをcloseへ流す誤りを検出する。</summary>
    [TestMethod]
    public void Manager_initialFailedKeepsWindowAndIndependentHealthyViewer()
    {
        var source = new ControlledLampSource(PlaylistLampAggregationRequest.Failed("1", "initial aggregation failure"));
        var healthySource = new ControlledLampSource(CreateReadyRequest("2"));
        int sourceCount = 0;
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs, _ => ++sourceCount == 1 ? healthySource : source, fixture =>
        {
            Task<PlaylistLampViewerWindow> healthyOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext());
            TestUiDispatcherHost.AwaitTaskOnDispatcher(healthyOpen, "manager.failed.healthy");
            PlaylistLampViewerWindow healthy = healthyOpen.GetAwaiter().GetResult();
            Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.failed.open");
            PlaylistLampViewerWindow failed = open.GetAwaiter().GetResult();
            Assert.IsNotNull(failed);
            Assert.IsTrue(failed.IsVisible);
            Assert.IsTrue(failed.ViewModel.IsFailure);
            Assert.IsFalse(failed.ViewModel.IsGraphVisible);
            Assert.AreEqual(0, failed.ViewModel.RankSegments.Count);
            Assert.AreEqual(2, fixture.Manager.Count);
            Assert.IsTrue(healthy.IsVisible);
            Assert.IsTrue(healthy.ViewModel.RankSegments.Any(item => item.IsInvokable));
            Assert.AreEqual(0, dialogs.MessageCount);
        });
    }

    /// <summary>factory失敗はWindowを生成せず、prepare失敗は未表示Windowを実Closed・両集合除去まで回収する。元の生成失敗を呼出元へ返し、回収後の通常終了は成功する。</summary>
    [DataTestMethod]
    [DataRow(0)] // factory自体がthrow。
    [DataRow(1)] // factory成功後のwindow準備がthrow。
    public void Manager_creationFailurePreservesRequiredFailureAndAttemptsCleanup(int failureStage)
    {
        var failure = new IOException("required lamp creation marker");
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs,
            _ => failureStage == 0 ? throw failure : source,
            fixture =>
            {
                Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
                Exception observed = CaptureTaskFailure(open, "lamp creation failure");
                Assert.IsTrue(GetFailureLeaves(observed).Any(cause => ReferenceEquals(cause, failure)));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.WaitForCompletionAsync(), "lamp ordinary creation failure clean drain");
                TestUiDispatcherHost.AwaitTaskOnDispatcher(fixture.Manager.DisposeAsync().AsTask(), "lamp ordinary creation failure clean shutdown");
                if (failureStage == 0) { Assert.AreEqual(0, fixture.PreparedWindowCount); }
                else
                {
                    Assert.AreEqual(1, fixture.PreparedWindowCount);
                    fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
                }
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

    /// <summary>実プレイリスト集合から一対象を削除すると、Workspaceのcatalog通知で対象ビューアだけを一回閉じ、他の窓は保持して通知ダイアログを出さない。対象外の巻込みと閉鎖漏れを検出する。</summary>
    [TestMethod]
    public void Manager_collectionRemovalSilentlyClosesOnlyTargetViewer()
    {
        var dialogs = new RecordingDialogService();
        RunScenario(
            dialogs,
            context => new ActualReadySource(context.Playlist, context.Library, context.PlaylistId),
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

                // Workspaceが購読する正本集合を変更し、手動イベント注入ではなく対象削除から終了へ接続する。
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
            });
    }

    /// <summary>表示後の明示日付取得が失敗しても対象窓と別窓を維持し、最新への明示変更で復帰する。FailedをDeletedの終了経路へ流す誤りを実Windowで検出する。</summary>
    [TestMethod]
    public void Manager_dateFailureKeepsWindowsAndLatestRecovers()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs, _ => source, fixture =>
        {
            Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "date failure initial open");
            PlaylistLampViewerWindow target = open.GetAwaiter().GetResult();
            Task<PlaylistLampViewerWindow> otherOpen = fixture.Manager.TryOpenAsync(fixture.Context);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOpen, "date failure independent open");
            PlaylistLampViewerWindow other = otherOpen.GetAwaiter().GetResult();
            source.CaptureOverride = (query, _) => ValueTask.FromResult(query.SelectedLocalDate.HasValue
                ? PlaylistLampAggregationRequest.Failed("1", "date unavailable", query: query)
                : CreateReadyRequest("1", query));
            TaskCompletionSource<bool> failed = Completion<bool>();
            System.ComponentModel.PropertyChangedEventHandler handler = (_, _) =>
            {
                if (target.ViewModel.IsFailure) { failed.TrySetResult(true); }
            };
            target.ViewModel.PropertyChanged += handler;
            try
            {
                target.ViewModel.SelectedAsOfDate = new DateTime(2026, 8, 27);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(failed.Task, "date failure projection");
                Assert.IsTrue(target.IsVisible);
                Assert.IsFalse(target.ViewModel.IsGraphVisible);
                Assert.AreEqual(0, target.ViewModel.RankSegments.Count);
                Assert.IsTrue(other.IsVisible);
                Assert.IsTrue(other.ViewModel.IsGraphVisible);
                Assert.AreEqual(2, fixture.Manager.Count);
                Assert.AreEqual(0, dialogs.MessageCount);
                TaskCompletionSource<bool> recovered = Completion<bool>();
                System.ComponentModel.PropertyChangedEventHandler recoveredHandler = (_, _) =>
                {
                    if (target.ViewModel.State == PlaylistLampViewerState.Ready) { recovered.TrySetResult(true); }
                };
                target.ViewModel.PropertyChanged += recoveredHandler;
                try
                {
                    target.ViewModel.LatestCommand.Execute();
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(recovered.Task, "date failure latest recovery");
                    Assert.IsTrue(target.ViewModel.IsGraphVisible);
                    Assert.IsNull(target.ViewModel.SelectedAsOfDate);
                }
                finally { target.ViewModel.PropertyChanged -= recoveredHandler; }
            }
            finally { target.ViewModel.PropertyChanged -= handler; }
        });
    }

    /// <summary>同じUI操作で正本集合をClearして同IDを戻した場合は窓を維持する。途中Resetを削除確定へ読み替える接続を検出する。</summary>
    [TestMethod]
    public void Manager_catalogReloadWithSameIdKeepsViewer()
    {
        RunScenario(new RecordingDialogService(), _ => new ControlledLampSource(CreateReadyRequest("1")), fixture =>
        {
            Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
            TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "reload viewer open");
            PlaylistLampViewerWindow window = open.GetAwaiter().GetResult();
            fixture.Playlist.BMSTables.Clear();
            fixture.Playlist.BMSTables.Add(fixture.Table);
            TestUiDispatcherHost.ProcessQueuedPresentation();
            Assert.IsTrue(window.IsVisible);
            Assert.AreEqual(1, fixture.Manager.Count);
            Assert.AreEqual(PlaylistLampViewerState.Ready, window.ViewModel.State);
        });
    }

    /// <summary>初回捕捉中の表削除も既存catalog通知でDeletedへ確定し、捕捉を取り消して一回だけ案内する。表示前lifetimeの削除見落としを検出する。</summary>
    [TestMethod]
    public void Manager_catalogRemovalDuringInitialCaptureDoesNotShowViewer()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"), waitForCapture: true);
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs, _ => source, fixture =>
        {
            Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(source.CaptureStarted.Task, "deleted initial capture started");
                fixture.Playlist.BMSTables.Remove(fixture.Table);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "deleted initial capture completed");
                Assert.IsNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.AreEqual(1, dialogs.MessageCount);
                Assert.AreEqual(Resources.PlaylistLampViewer_initial_deleted, dialogs.Messages.Single().MessageBoxText);
                fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
            }
            finally
            {
                source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "deleted initial capture drain");
            }
        }, prepareWindowPresentation: false);
    }

    /// <summary>初回Readyの公開後、open継続より先に実catalog削除を配送し、Deletedを一度案内して未表示のまま回収する。初回結果だけで表示可否を決める誤りを検出する。</summary>
    [TestMethod]
    public void Manager_catalogRemovalAfterFirstResultBeforeOpenContinuationDoesNotShowViewer()
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"), waitForCapture: true);
        var dialogs = new RecordingDialogService();
        RunScenario(dialogs, _ => source, fixture =>
        {
            // 実catalog通知をopenの通常継続より先に配送し、問題の順序を固定します。
            fixture.ConfigureCatalogNotificationQueue(action => fixture.Owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send, action));
            Task<PlaylistLampViewerWindow> open = fixture.Manager.TryOpenAsync(fixture.Context);
            PlaylistLampViewerViewModel viewModel = fixture.PreparedWindow.ViewModel;
            var publishedStates = new List<PlaylistLampViewerState>();
            bool deletedBeforePresentation = false;
            System.ComponentModel.PropertyChangedEventHandler handler = (_, _) =>
            {
                if (viewModel.State == PlaylistLampViewerState.Ready && publishedStates.Count == 0)
                {
                    // VMのReady通知はfirstPresentableの公開後です。実集合を削除して次のUI通知を作ります。
                    publishedStates.Add(viewModel.State);
                    fixture.Playlist.BMSTables.Remove(fixture.Table);
                }
                else if (viewModel.State == PlaylistLampViewerState.Deleted)
                {
                    publishedStates.Add(viewModel.State);
                    deletedBeforePresentation = !fixture.PreparedWindow.IsVisible && !open.IsCompleted;
                }
            };
            viewModel.PropertyChanged += handler;
            try
            {
                TestUiDispatcherHost.AwaitTaskOnDispatcher(source.CaptureStarted.Task, "deleted after first result capture started");
                source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "deleted after first result open completed");
                CollectionAssert.AreEqual(new[] { PlaylistLampViewerState.Ready, PlaylistLampViewerState.Deleted }, publishedStates);
                Assert.IsTrue(deletedBeforePresentation);
                Assert.IsNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.AreEqual(1, dialogs.MessageCount);
                Assert.AreEqual(Resources.PlaylistLampViewer_initial_deleted, dialogs.Messages.Single().MessageBoxText);
                fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
            }
            finally
            {
                viewModel.PropertyChanged -= handler;
                source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "deleted after first result drain");
            }
        }, prepareWindowPresentation: false);
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

    /// <summary>
    /// 初回capture中のowner終了またはcaller取消は、取消に応じないcaptureも実終端まで所有します。
    /// gate解放後にopen/drain・未表示Windowの実Closed/両集合除去・capture回収を確認し、遅い表示や通知を禁止します。
    /// </summary>
    [DataTestMethod]
    [DataRow("dispose", false)] // 同期owner終了、captureは取消を遵守。
    [DataRow("disposeAsync", false)] // 非同期owner終了、captureは取消を遵守。
    [DataRow("dispose", true)] // 同期owner終了でも取消を無視するcaptureの実終端を待つ。
    [DataRow("disposeAsync", true)] // 非同期owner終了でも取消を無視するcaptureの実終端を待つ。
    [DataRow("caller", false)] // caller tokenから初回openを取り消す。
    [DataRow("caller", true)] // caller取消後もcaptureが返るまで実captureを保持。
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
                        source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                    }
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.pending-cancel.open-canceled");
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(drain, "manager.pending-cancel.drain-terminal");

                    Assert.IsNull(open.GetAwaiter().GetResult());
                    Assert.AreEqual(0, fixture.Manager.Count);
                    Assert.AreEqual(0, dialogs.MessageCount);
                    fixture.AssertUnshownWindowReclaimed(fixture.PreparedWindow);
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
    /// VMの異なる二つの日付変更で取消を無視するcaptureを二つ受理し、native Close/owner終了を先行させます。
    /// 最新だけを返してもdrainを完了せず、旧captureまで回収し、遅いReady/Failedを再表示しません。
    /// </summary>
    /// <remarks>実WindowのClosed、VMの日付入口と別窓の表示/操作を観測するため、同じpresentation scopeを使います。</remarks>
    [DataTestMethod]
    [DataRow(false, false)] // 手動閉鎖後の遅い成功と別窓独立性。
    [DataRow(false, true)] // 手動閉鎖後の遅い失敗でも通知を再表示しません。
    [DataRow(true, false)] // owner終了後の遅い成功。
    [DataRow(true, true)] // owner終了後の遅い失敗。
    public void Manager_closeDrainsSupersededDateCaptures(bool shutdown, bool lateFailure)
    {
        var source = new ControlledLampSource(CreateReadyRequest("1"));
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "date drain target open");
                PlaylistLampViewerWindow target = open.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("date drain対象が開かなかった。");
                Task<PlaylistLampViewerWindow> otherOpen = fixture.Manager.TryOpenAsync(fixture.CreateOtherContext());
                TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOpen, "date drain independent open");
                PlaylistLampViewerWindow other = otherOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("date drain別窓が開かなかった。");
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
                target.ViewModel.SelectedAsOfDate = new DateTime(2026, 8, 26);
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
                TestUiDispatcherHost.AwaitPresentationOnDispatcher(closed.Task, "date drain actual native close");
                Assert.IsFalse(drain.IsCompleted);
                if (!shutdown)
                {
                    Assert.IsTrue(other.IsVisible);
                    PlaylistLampViewerSegmentViewModel segment = other.ViewModel.RankSegments.First(item => item.IsInvokable);
                    other.ViewModel.InvokeSegment(segment);
                    Assert.IsTrue(segment.IsSelected);
                }
                held[1].Completion.TrySetResult(lateFailure
                    ? PlaylistLampAggregationRequest.Failed("1", "late date failure", query: held[1].Query)
                    : CreateReadyRequest("1", held[1].Query));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(held[1].Completion.Task, "date drain latest actual capture");
                Assert.IsFalse(drain.IsCompleted, "取消を無視する旧日付captureも実終端まで所有します。");
                held[0].Completion.TrySetResult(lateFailure
                    ? PlaylistLampAggregationRequest.Failed("1", "late earlier date failure", query: held[0].Query)
                    : CreateReadyRequest("1", held[0].Query));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(held.Select(item => (Task)item.Completion.Task).Append(drain)), "date drain all actual captures and cleanup");
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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(Task.WhenAll(joins), "date drain finally actual tasks");
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
                    TestUiDispatcherHost.AwaitTaskOnDispatcher(changed, "date drain accepted capture");
                }
            }
        });
    }

    /// <summary>
    /// 初回open token由来の取消callbackをholdしても、実Close/DisposeAsyncはnative Closedと
    /// Dispatcher pulseを先に実行し、drainはcallback・captureの実終端まで待ちます。
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
            query: query,
            historicalDateRange: new PlaylistLampHistoricalDateRange(new DateTime(2026, 8, 26), DateTime.Today));
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
            workspace.ConfigureCatalogNotificationQueue(action => Owner.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.DataBind, action));
            Context = CreateContext(Playlist, Table, Library);
            Manager = new PlaylistLampViewerWindowManager(
                Owner,
                workspace,
                dialogs,
                sourceFactory,
                prepareWindowForShow,
                activateWindowForInitialPresentation);
        }

        /// <summary>通知の優先順位を制御し、実catalog通知とopen継続の境界を検証します。</summary>
        internal void ConfigureCatalogNotificationQueue(Action<Action> queueAction)
            => workspace.ConfigureCatalogNotificationQueue(queueAction);

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
            BMSTable otherTable = CreateTable(playlistId, "Other manager fixture");
            Playlist.BMSTables.Add(otherTable);
            return CreateContext(Playlist, otherTable, Library);
        }

        public void Dispose()
        {
            var failures = new List<Exception>();
            try { TestUiDispatcherHost.AwaitTaskOnDispatcher(Manager.DisposeAsync().AsTask(), "lamp fixture dispose"); }
            catch (Exception exception) { failures.Add(exception); }
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

    private sealed class ControlledLampSource : IPlaylistLampViewerDataSource
    {
        private readonly PlaylistLampAggregationRequest request;
        private readonly bool waitForCapture;
        private readonly bool ignoreCaptureCancellation;

        internal ControlledLampSource(PlaylistLampAggregationRequest request, bool waitForCapture = false, bool ignoreCaptureCancellation = false)
        {
            this.request = request;
            this.waitForCapture = waitForCapture;
            this.ignoreCaptureCancellation = ignoreCaptureCancellation;
        }
        internal TaskCompletionSource<bool> CaptureStarted { get; } = Completion<bool>();
        internal TaskCompletionSource<PlaylistLampAggregationRequest> CaptureRelease { get; } = Completion<PlaylistLampAggregationRequest>();
        internal Func<PlaylistLampViewerQuery, CancellationToken, ValueTask<PlaylistLampAggregationRequest>>? CaptureOverride { get; set; }
        public ValueTask<PlaylistLampAggregationRequest> CaptureAsync(PlaylistLampViewerQuery query, CancellationToken cancellationToken)
        {
            CaptureStarted.TrySetResult(true);
            if (CaptureOverride != null) { return CaptureOverride(query, cancellationToken); }
            if (waitForCapture)
            {
                return new ValueTask<PlaylistLampAggregationRequest>(ignoreCaptureCancellation ? CaptureRelease.Task : CaptureRelease.Task.WaitAsync(cancellationToken));
            }
            return ValueTask.FromResult(new PlaylistLampAggregationRequest(
                query.PlaylistId, request.FolderOrder, request.Entries, request.ScoreSnapshot,
                request.PlaylistLastUpdated, request.InputState, request.FailureMessage, query: query));
        }
    }

    private sealed class ActualReadySource(BMSPlaylist playlist, BMSLibrary library, string playlistId) : IPlaylistLampViewerDataSource
    {
        private readonly BmsLibraryPlaylistLampDataSource inner = new(playlist, library);
        public async ValueTask<PlaylistLampAggregationRequest> CaptureAsync(PlaylistLampViewerQuery query, CancellationToken cancellationToken)
        {
            PlaylistLampAggregationRequest captured = await inner.CaptureAsync(query, cancellationToken).ConfigureAwait(false);
            return captured.InputState == PlaylistLampInputState.Loaded ? CreateReadyRequest(playlistId, query) : captured;
        }
    }

    private sealed class RecordingDialogService : IUiDialogService
    {
        private readonly object gate = new();

        internal List<UiMessageRequest> Messages { get; } = [];

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
            return Task.FromResult(UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
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
