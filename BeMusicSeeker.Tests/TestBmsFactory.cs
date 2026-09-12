#nullable disable

using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Tests;

/// <summary>
/// Provides process-local composition values shared by production-shaped test fixtures.
/// </summary>
internal static class TestBmsFactory
{
    /// <summary>
    /// Gets an application snapshot whose native bridge path is intentionally absent,
    /// so tests cannot depend on the local Everything service or index.
    /// </summary>
    internal static ApplicationPathSnapshot MissingEverythingBridge { get; } =
        ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(Path.GetTempPath(), "BeMusicSeeker.Tests", "MissingEverythingBridge", "BeMusicSeeker.exe"));

    /// <summary>
    /// Uses the repository's shipped bridge for production-shaped scan-route tests.
    /// The scan still falls back to the managed enumerator when the Everything
    /// service is unavailable.
    /// </summary>
    internal static ApplicationPathSnapshot AvailableEverythingBridge { get; } =
        ApplicationPathSnapshot.FromExecutablePath(
            Path.Combine(AppContext.BaseDirectory, "BeMusicSeeker.Tests.exe"));
}

internal sealed class TestBmsLibrary : BMSLibrary
{
    private static Func<BmsLibraryOptionsSnapshot> CurrentOptions =>
        () => BmsLibraryOptionsSnapshot.CreateCurrent(Settings.Default);

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config = null,
        string _lr2ScoreDB = null,
        string startupRequiredFileScanReason = null)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, startupRequiredFileScanReason, CurrentOptions, new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, fileMutationService, null, null, CurrentOptions, new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, fileMutationService, dialogService, null, CurrentOptions, new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// Creates a test library with the narrow install-estimation diagnostic boundary enabled.
    /// </summary>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        IInstallEstimationExecutionObserver installEstimationExecutionObserver)
        : base(
            songDbPath,
            getLR2Config,
            _lr2ScoreDB,
            fileMutationService,
            dialogService,
            null,
            CurrentOptions,
            new TestUiScheduler(() => Dispatcher.CurrentDispatcher),
            TestBmsFactory.MissingEverythingBridge,
            installEstimationExecutionObserver)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        IUiScheduler uiScheduler)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, fileMutationService, dialogService, null, CurrentOptions, uiScheduler, TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// Creates a package-lifecycle fixture with an explicit options snapshot
    /// and UI scheduler, so cleanup and publication tests do not mutate the
    /// process-wide settings singleton.
    /// </summary>
    /// <param name="chartFileScanner">Optional captured scanner for tests that exercise the production file-diff ingress.</param>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        IFileMutationService fileMutationService,
        IBmsLibraryDialogService dialogService,
        IUiScheduler uiScheduler,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        IChartFileScanner chartFileScanner = null)
        : base(
            songDbPath,
            getLR2Config,
            _lr2ScoreDB,
            fileMutationService,
            dialogService,
            null,
            optionsSnapshotProvider ?? CurrentOptions,
            uiScheduler,
            TestBmsFactory.MissingEverythingBridge,
            installEstimationExecutionObserver: null,
            chartFileScanner: chartFileScanner)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider)
        : base(songDbPath, getLR2Config, _lr2ScoreDB, startupRequiredFileScanReason, optionsSnapshotProvider, new TestUiScheduler(() => Dispatcher.CurrentDispatcher), TestBmsFactory.MissingEverythingBridge)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// Creates a production-shaped library with an explicitly selected native
    /// bridge path for canonical file-scan route tests.  A captured scanner may
    /// be supplied when the route must be independent of the local Everything
    /// service and index.
    /// </summary>
    /// <param name="chartFileScanner">Optional captured scanner for the test fixture; null keeps the selected bridge-backed scan.</param>
    /// <param name="uiScheduler">Optional scheduler for deterministic progress publication in route tests.</param>
    /// <param name="rootFileEnumerator">Optional captured grouped enumerator for deterministic LR2 file candidates.</param>
    /// <param name="irClient">startup から終了までの IR 通信を所有する明示的な境界。</param>
    /// <param name="dialogService">Optional dialog recorder for production-ingress warning tests.</param>
    internal TestBmsLibrary(
        string songDbPath,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        string startupRequiredFileScanReason,
        Func<BmsLibraryOptionsSnapshot> optionsSnapshotProvider,
        ApplicationPathSnapshot applicationPathSnapshot,
        IChartFileScanner chartFileScanner = null,
        IUiScheduler uiScheduler = null,
        IRootFileEnumerator rootFileEnumerator = null,
        IBmsLibraryIrClient irClient = null,
        IBmsLibraryDialogService dialogService = null)
        : base(
            _lr2SongDB: songDbPath,
            getLR2Config: getLR2Config,
            _lr2ScoreDB: _lr2ScoreDB,
            fileMutationService: null,
            dialogService: dialogService,
            startupRequiredFileScanReason: startupRequiredFileScanReason,
            optionsSnapshotProvider: optionsSnapshotProvider,
            uiScheduler: uiScheduler ?? new TestUiScheduler(() => Dispatcher.CurrentDispatcher),
            applicationPathSnapshot: applicationPathSnapshot,
            installEstimationExecutionObserver: null,
            chartFileScanner: chartFileScanner,
            rootFileEnumerator: rootFileEnumerator,
            irClient: irClient)
    {
        MarkCatalogPathConvergenceCompleted();
    }

    /// <summary>
    /// Creates a production-shaped library with the shipped Everything bridge.
    /// </summary>
    internal TestBmsLibrary(
        string songDbPath,
        ApplicationPathSnapshot applicationPathSnapshot)
        : base(songDbPath, null, null, null, CurrentOptions, new TestUiScheduler(() => Dispatcher.CurrentDispatcher), applicationPathSnapshot)
    {
        MarkCatalogPathConvergenceCompleted();
    }
}

internal sealed class TestBmsPlaylist : BMSPlaylist
{
    private static Func<PlaylistUrlCompletionOptionsSnapshot> CurrentPlaylistUrlOptions =>
        () => PlaylistUrlCompletionOptionsSnapshot.CreateCurrent(Settings.Default);

    private static Func<BeatorajaBmtOptionsSnapshot> CurrentBeatorajaOptions =>
        () => BeatorajaBmtOptionsSnapshot.CreateCurrent(Settings.Default);

    private static Func<CustomFolderOutputSettingsSnapshot> CurrentCustomFolderOptions =>
        () => CustomFolderOutputSettingsSnapshot.CreateCurrent(Settings.Default);

    private static LibraryFileMutationLease CreateTestMutationLease(string _)
        => new(new object(), static () => true, static () => { });

    private static LibraryFileMutationLease CreateTestMutationLease(string _, bool __)
        => CreateTestMutationLease(_);

    internal new ObservableCollection<BMSTable> BMSTables
    {
        get => base.BMSTables;
        set => base.BMSTables = value;
    }

    internal TestBmsPlaylist(
        string songDbPath,
        Func<LR2Config> getLr2Config = null,
        string scoreDbPath = null,
        Func<List<BMSScore>> getBmsScores = null,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver = null)
        : base(
            songDbPath,
            getLr2Config,
            scoreDbPath,
            getBmsScores,
            getBeatorajaBmtSongHashResolver,
            CurrentPlaylistUrlOptions,
            CurrentBeatorajaOptions,
            CurrentCustomFolderOptions,
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            new TestLr2PlaylistFolderSynchronizationPort(songDbPath),
            CreateTestMutationLease)
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization)
        : base(
            songDbPath,
            null,
            null,
            null,
            null,
            CurrentPlaylistUrlOptions,
            CurrentBeatorajaOptions,
            CurrentCustomFolderOptions,
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization,
            CreateTestMutationLease)
    {
    }

    /// <summary>
    /// Creates a playlist test double with an explicitly controlled UI-operation scheduler.
    /// </summary>
    /// <param name="songDbPath">The song database used by the playlist.</param>
    /// <param name="lr2PlaylistFolderSynchronization">The LR2 folder synchronization port.</param>
    /// <param name="uiScheduler">The scheduler whose accepted-operation lifecycle the test controls.</param>
    internal TestBmsPlaylist(
        string songDbPath,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization,
        IUiScheduler uiScheduler)
        : base(
            songDbPath,
            null,
            null,
            null,
            null,
            CurrentPlaylistUrlOptions,
            CurrentBeatorajaOptions,
            CurrentCustomFolderOptions,
            TestBmsFactory.MissingEverythingBridge,
            uiScheduler,
            lr2PlaylistFolderSynchronization,
            CreateTestMutationLease)
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        Func<LR2Config> getLr2Config,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization)
        : base(
            songDbPath,
            getLr2Config,
            null,
            null,
            null,
            CurrentPlaylistUrlOptions,
            CurrentBeatorajaOptions,
            CurrentCustomFolderOptions,
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization,
            CreateTestMutationLease)
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        string scoreDbPath,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization)
        : base(
            songDbPath,
            null,
            scoreDbPath,
            null,
            null,
            CurrentPlaylistUrlOptions,
            CurrentBeatorajaOptions,
            CurrentCustomFolderOptions,
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization,
            CreateTestMutationLease)
    {
    }

    internal TestBmsPlaylist(
        string songDbPath,
        Func<LR2Config> getLr2Config,
        string scoreDbPath,
        Func<List<BMSScore>> getBmsScores,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization = null,
        Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionTsvContentFetcher = null,
        Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionStellaContentFetcher = null,
        Func<string, LibraryFileMutationLease> mutationLeaseProvider = null,
        Func<string, bool, LibraryFileMutationLease> mutationLeaseProviderWithMessage = null)
        : base(
            songDbPath,
            getLr2Config,
            scoreDbPath,
            getBmsScores,
            getBeatorajaBmtSongHashResolver,
            playlistUrlCompletionOptionsProvider,
            beatorajaBmtOptionsProvider,
            customFolderOutputSettingsProvider,
            TestBmsFactory.MissingEverythingBridge,
            new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher),
            lr2PlaylistFolderSynchronization ?? new TestLr2PlaylistFolderSynchronizationPort(songDbPath),
            mutationLeaseProviderWithMessage
                ?? ((operation, _) => (mutationLeaseProvider ?? CreateTestMutationLease)(operation)),
            playlistUrlCompletionTsvContentFetcher,
            playlistUrlCompletionStellaContentFetcher)
    {
    }
}
