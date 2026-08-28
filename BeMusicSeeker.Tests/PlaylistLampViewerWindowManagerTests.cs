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

/// <summary>
/// Exercises the playlist lamp viewer manager's owner-scoped open, terminal, and shutdown
/// lifecycle against deterministic source and dialog boundaries.
/// </summary>
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
                    ?? throw new AssertFailedException("The ready viewer did not open.");
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
                    ?? throw new AssertFailedException("The primary viewer did not open.");

                Task<PlaylistLampViewerWindow> otherOpen = fixture.Manager.TryOpenAsync(otherContext);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(otherOpen, "manager.collection-removal.other-open");
                PlaylistLampViewerWindow otherWindow = otherOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("The other viewer did not open.");
                TestUiDispatcherHost.Drain();

                int firstClosedCount = 0;
                var firstClosed = Completion<bool>();
                firstWindow.Closed += (_, _) =>
                {
                    firstClosedCount++;
                    firstClosed.TrySetResult(true);
                };

                Assert.AreEqual(2, fixture.Manager.Count);
                Assert.AreEqual(PlaylistLampViewerState.Ready, firstWindow.ViewModel.State);

                // This is the real active playlist collection mutation watched by the
                // production BmsLibraryPlaylistLampDataSource subscription.
                Assert.IsTrue(fixture.Playlist.BMSTables.Remove(fixture.Table));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    firstClosed.Task,
                    "manager.collection-removal.primary-closed");
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(1, firstClosedCount);
                Assert.AreEqual(1, fixture.Manager.Count);
                Assert.IsTrue(otherWindow.IsVisible);
                Assert.AreEqual(0, dialogs.MessageCount);
                Assert.AreEqual(1, createdSources[0].DisposeCount);
            });
    }

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
                    ?? throw new AssertFailedException("The first same-playlist viewer did not open.");

                Task<PlaylistLampViewerWindow> secondOpen = fixture.Manager.TryOpenAsync(fixture.Context);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(secondOpen, "manager.same-playlist.second-open");
                PlaylistLampViewerWindow secondWindow = secondOpen.GetAwaiter().GetResult()
                    ?? throw new AssertFailedException("The second same-playlist viewer did not open.");

                Assert.AreNotSame(firstWindow, secondWindow);
                Assert.AreNotSame(firstWindow.ViewModel, secondWindow.ViewModel);
                Assert.AreEqual(2, fixture.Manager.Count);
                Assert.AreEqual(2, fixture.PreparedWindowCount);
                Assert.AreEqual(2, createdSources.Count);
                Assert.AreNotSame(createdSources[0], createdSources[1]);

                PlaylistLampViewerSegmentViewModel firstGlobalRank = firstWindow.ViewModel.PositiveRankSegments
                    .Single(segment => segment.CategoryKey == "A");
                PlaylistLampViewerSegmentViewModel secondGlobalRank = secondWindow.ViewModel.PositiveRankSegments
                    .Single(segment => segment.CategoryKey == "A");
                firstGlobalRank.Invoke();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(firstGlobalRank.IsSelected);
                Assert.IsFalse(secondGlobalRank.IsSelected);
                Assert.IsTrue(secondWindow.IsVisible);
                Assert.IsTrue(secondGlobalRank.IsInvokable);

                secondGlobalRank.Invoke();
                TestUiDispatcherHost.Drain();
                Assert.IsTrue(firstGlobalRank.IsSelected);
                Assert.IsTrue(secondGlobalRank.IsSelected);
                Assert.AreSame(firstGlobalRank, firstWindow.ViewModel.SelectedSegment);
                Assert.AreSame(secondGlobalRank, secondWindow.ViewModel.SelectedSegment);

                var firstClosed = Completion<bool>();
                int firstClosedCount = 0;
                firstWindow.Closed += (_, _) =>
                {
                    firstClosedCount++;
                    firstClosed.TrySetResult(true);
                };
                firstWindow.Close();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    firstClosed.Task,
                    "manager.same-playlist.first-closed");
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(1, firstClosedCount);
                Assert.AreEqual(1, fixture.Manager.Count);
                Assert.IsTrue(secondWindow.IsVisible);
                Assert.AreEqual(1, createdSources[0].DisposeCount);
                Assert.AreEqual(0, createdSources[1].DisposeCount);
            });
    }

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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    source.CaptureStarted.Task,
                    "manager.presentable-gate.capture-started");
                TestUiDispatcherHost.Drain();

                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.IsFalse(Application.Current.Windows
                    .OfType<PlaylistLampViewerWindow>()
                    .Any(window => window.IsVisible));
                Assert.AreEqual(0, dialogs.MessageCount);

                source.CaptureRelease.TrySetResult(CreateReadyRequest("1"));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    open,
                    "manager.presentable-gate.ready");
                TestUiDispatcherHost.Drain();

                Assert.IsNotNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(1, fixture.Manager.Count);
                Assert.AreEqual(1, fixture.PreparedWindowCount);
                Assert.AreEqual(1, Application.Current.Windows
                    .OfType<PlaylistLampViewerWindow>()
                    .Count(window => window.IsVisible));
                Assert.AreEqual(0, dialogs.MessageCount);
            });
    }

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
                    ?? throw new AssertFailedException("The live-failure viewer did not open.");

                int closedCount = 0;
                var closed = Completion<bool>();
                window.Closed += (_, _) =>
                {
                    closedCount++;
                    closed.TrySetResult(true);
                };
                dialogs.BlockedMessageResult = Completion<UiDialogResult>();

                source.Replace(PlaylistLampAggregationRequest.Failed("1", "live aggregation failure"));
                source.Raise("1", 1);
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    dialogs.FirstMessage.Task,
                    "manager.live-failed.dialog-presented");

                // Keep the first dialog pending while another terminal publication arrives.
                // FailureHandled must suppress the duplicate notification, but the original
                // viewer remains the one that is eventually closed.
                source.Raise("1", 2);
                TestUiDispatcherHost.Drain();
                Assert.AreEqual(1, dialogs.MessageCount);

                dialogs.BlockedMessageResult.TrySetResult(
                    UiDialogResult.FromMessageBoxResult(System.Windows.MessageBoxResult.OK));
                TestUiDispatcherHost.AwaitTaskOnDispatcher(closed.Task, "manager.live-failed.closed");

                Assert.AreEqual(1, closedCount);
                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.AreEqual(1, dialogs.MessageCount);
                Assert.AreSame(fixture.Owner, dialogs.Messages.Single().Owner);
                Assert.AreEqual(1, source.DisposeCount);
            });
    }

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
                TestUiDispatcherHost.AwaitTaskOnDispatcher(
                    source.CaptureStarted.Task,
                    "manager.pending-cancel.capture-started");

                fixture.Manager.Dispose();
                TestUiDispatcherHost.AwaitTaskOnDispatcher(open, "manager.pending-cancel.open-canceled");
                TestUiDispatcherHost.Drain();

                Assert.IsNull(open.GetAwaiter().GetResult());
                Assert.AreEqual(0, fixture.Manager.Count);
                Assert.AreEqual(0, dialogs.MessageCount);
                Assert.AreEqual(1, source.DisposeCount);
                Assert.IsFalse(Application.Current.Windows
                    .OfType<PlaylistLampViewerWindow>()
                    .Any(window => window.IsVisible));
            },
            prepareWindowPresentation: false);
    }

    private static void RunScenario(
        IUiDialogService dialogs,
        Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> sourceFactory,
        Action<ManagerTestFixture> test,
        bool prepareWindowPresentation = true,
        Action<PlaylistLampViewerWindow> activateWindowForInitialPresentation = null)
    {
        string settingsRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(PlaylistLampViewerWindowManagerTests),
            "settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(settingsRoot);
        try
        {
            MainWindowPackageMaintenanceTestHarness.RunConstructorOnly(
                new Settings
                {
                    OperationModeLR2DB = false,
                    BMSRootPath = settingsRoot,
                    StandaloneBmsRootPaths = settingsRoot,
                    BMSInstallDir = settingsRoot,
                    TableListURL = new Uri("http://127.0.0.1:1/table-list.json"),
                    EnablePlaylistUrlCompletion = false,
                    ScanBmsFilesOnStartup = false,
                    SkipInitPlaylistLoad = true,
                    UseBeatorajaScoreDb = false,
                    EnableBeatorajaBmtOutput = false,
                    UseExternalPanelImage = false,
                    UsePlayeruBMplay = false,
                    UsePlayerLR2body = false,
                    UsePlayerBMIIDXView = false,
                    IsLR2BackupEnabled = false
                },
                (_, owner) =>
                {
                    var presentationScope = new TestWindowPresentationScope(
                        Application.Current,
                        TestWindowPresentationScope.GetCurrentNativeThreadId());
                    ManagerTestFixture fixture = null;
                    ExceptionDispatchInfo bodyFailure = null;
                    Exception cleanupFailure = null;
                    try
                    {
                        presentationScope.PrepareForOwnedPresentation(owner);
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
                                    "the manager must prepare a fresh viewer before it is loaded");
                                Assert.IsFalse(
                                    window.IsVisible,
                                    "the manager must prepare a fresh viewer before it is visible");
                                Assert.AreEqual(
                                    nint.Zero,
                                    TestWindowPresentationScope.GetNativeHandle(window),
                                    "a fresh viewer must not have an HWND before manager Show");
                                if (prepareWindowPresentation)
                                {
                                    presentationScope.PrepareForOwnedPresentation(
                                        window,
                                        TestWindowActivation.NonActivating);
                                }
                                fixture?.RecordPreparedWindow(window);
                            },
                            activateWindowForInitialPresentation);
                        test(fixture);
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

                    try
                    {
                        presentationScope.Cleanup();
                    }
                    catch (Exception ex)
                    {
                        cleanupFailure = cleanupFailure == null
                            ? ex
                            : new AggregateException(
                                "Manager test presentation cleanup failed.",
                                cleanupFailure,
                                ex);
                    }

                    ExceptionDispatchInfo presentationFailure = presentationScope.GetPresentationFailure();
                    if (bodyFailure != null)
                    {
                        if (presentationFailure != null)
                        {
                            bodyFailure.SourceException.Data["TestWindowPresentationFailure"] =
                                presentationFailure.SourceException.ToString();
                        }
                        if (cleanupFailure != null)
                        {
                            bodyFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] =
                                cleanupFailure.ToString();
                        }
                        bodyFailure.Throw();
                    }
                    if (presentationFailure != null)
                    {
                        if (cleanupFailure != null)
                        {
                            presentationFailure.SourceException.Data["TestWindowPresentationCleanupFailure"] =
                                cleanupFailure.ToString();
                        }
                        presentationFailure.Throw();
                    }
                    if (cleanupFailure != null)
                    {
                        ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
                    }
                });
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

        internal ManagerTestFixture(
            MainWindow owner,
            IUiDialogService dialogs,
            Func<PlaylistLampViewerOpenContext, IPlaylistLampViewerDataSource> sourceFactory,
            Action<PlaylistLampViewerWindow> prepareWindowForShow,
            Action<PlaylistLampViewerWindow> activateWindowForInitialPresentation)
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
            Library.BMSFiles = [];
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

        private EventHandler<PlaylistLampViewerSourceChangedEventArgs> changed;

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
            string playlistId,
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
            EventHandler<PlaylistLampViewerSourceChangedEventArgs> handler;
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

        public event EventHandler<PlaylistLampViewerSourceChangedEventArgs> Changed;

        public async ValueTask<PlaylistLampAggregationRequest> CaptureAsync(
            string requestedPlaylistId,
            CancellationToken cancellationToken)
        {
            PlaylistLampAggregationRequest captured = await inner
                .CaptureAsync(requestedPlaylistId, cancellationToken)
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

        private void InnerChanged(object sender, PlaylistLampViewerSourceChangedEventArgs e)
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

        internal TaskCompletionSource<UiDialogResult> BlockedMessageResult { get; set; }

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
            TaskCompletionSource<UiDialogResult> blocked = BlockedMessageResult;
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
