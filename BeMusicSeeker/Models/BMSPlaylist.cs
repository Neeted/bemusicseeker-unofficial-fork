using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Livet.EventListeners;
using Newtonsoft.Json.Linq;
using NLog;
using Ribbit.Logging;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;
using SQLite;

using CustomFolderBatchMaterializationResult = BeMusicSeeker.Models.BmsLibraryInternal.PlaylistCustomFolderOutputOwner.CustomFolderBatchMaterializationResult;
using CustomFolderDefinition = BeMusicSeeker.Models.BmsLibraryInternal.PlaylistCustomFolderOutputOwner.CustomFolderDefinition;
using CustomFolderOutputFileProjection = BeMusicSeeker.Models.BmsLibraryInternal.PlaylistCustomFolderOutputOwner.CustomFolderOutputFileProjection;
using CustomFolderOutputPhysicalMtimeSignatureIndex = BeMusicSeeker.Models.BmsLibraryInternal.PlaylistCustomFolderOutputOwner.CustomFolderOutputPhysicalMtimeSignatureIndex;
using CustomFolderOutputProjection = BeMusicSeeker.Models.BmsLibraryInternal.PlaylistCustomFolderOutputOwner.CustomFolderOutputProjection;
using CustomFolderBatchOutputResult = BeMusicSeeker.Models.BmsLibraryInternal.PlaylistCustomFolderOutputMaintenanceOwner.CustomFolderBatchOutputResult;

namespace BeMusicSeeker.Models;

/// <summary>
/// LR2 のプレイリスト定義、外部テーブル同期、カスタムフォルダ出力を一括管理します。
/// DB 永続化と外部取得の境界が同居しているため、この型がプレイリスト関連処理の集約点です。
/// </summary>
public partial class BMSPlaylist : NotificationObject
{
    private const string CustomFolderOutputLr2FolderEnumerationGroupName = "lr2folder";

    /// <summary>
    /// プレイリスト更新処理の性能ログを出力するロガーです。
    /// </summary>
    private static readonly Logger installPerformanceLogger = NLogWrapper.GetLogger("InstallPerformance.BMSPlaylist");

    /// <summary>
    /// 性能ログ出力を有効化するかどうかを保持します。
    /// </summary>
    private static readonly bool installPerformanceLoggingEnabled = CommandLineSwitches.IsInfoLoggingEnabled;

    /// <summary>
    /// 外部プレイリスト取得に使う既定のタイムアウト時間（ミリ秒）です。
    /// </summary>
    private const int PlaylistWebTimeoutMs = 300000;

    /// <summary>
    /// 差分ログへ出す fingerprint サンプル件数です。
    /// </summary>
    private const int PlaylistDiffSampleLogCount = 3;

    /// <summary>
    /// プレイリスト更新処理の性能ログを、INFO ログが有効な場合のみ出力します。
    /// </summary>
    /// <param name="message">出力する性能ログ本文。</param>
    private static void LogPlaylistPerformance(string message)
    {
        if (installPerformanceLoggingEnabled)
        {
            installPerformanceLogger.Info(message);
        }
    }

    /// <summary>
    /// ログ出力向けに URI を安全な文字列へ整形します。
    /// </summary>
    /// <param name="uri">整形対象の URI。</param>
    /// <returns><paramref name="uri"/> の文字列表現。null の場合は "(null)"。</returns>
    private static string FormatUriForLog(Uri uri)
    {
        return uri?.ToString() ?? "(null)";
    }

    /// <summary>
    /// ログ出力向けに文字列を安全な表現へ整形します。
    /// </summary>
    /// <param name="value">整形対象の文字列。</param>
    /// <returns>空白を含む文字列はそのまま、null または空文字列は "(empty)"。</returns>
    private static string FormatTextForLog(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? "(empty)" : value;
    }

    private static string QuoteLogValue(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "\"\"";
        }
        return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>
    /// <see cref="BMSPlaylist"/> 内の HTTP 通信に使う共有クライアントです。
    /// 外部テーブル同期は応答待ちが長くなりやすいため、既定より長いタイムアウトを設定します。
    /// </summary>
    private static readonly AppHttpClient playlistHttpClient = AppHttpClient.Create(PlaylistWebTimeoutMs);

    /// <summary>
    /// プレイリストを保存する LR2 Song DB のパスを保持します。
    /// </summary>
    private readonly string lr2SongDBPath;

    private readonly PlaylistPersistenceRepository playlistPersistenceRepository;

    private readonly PlaylistAggregatePersistenceOwner playlistAggregatePersistenceOwner;

    private readonly PlaylistEntriesHydrationOwner playlistEntriesHydrationOwner;

    private readonly PlaylistShutdownCoordinator shutdownCoordinator = new();

    private readonly PlaylistBmtOutputOwner bmtOutput;

    private readonly PlaylistOperationNotificationOwner operationNotificationOwner;

    private readonly PlaylistRecommendedTableOwner recommendedTableOwner;

    private readonly PlaylistCustomFolderOutputOwner customFolderOutputOwner;

    private readonly PlaylistCustomFolderOutputStatusOwner customFolderOutputStatusOwner;

    private readonly PlaylistCustomFolderOutputMaintenanceOwner customFolderOutputMaintenanceOwner;

    private readonly PlaylistExternalSyncOwner externalSyncOwner;

    private readonly ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization;

    /// <summary>
    /// LR2 設定を取得するための遅延評価デリゲートです。
    /// </summary>
    private readonly Func<LR2Config> lr2config;

    /// <summary>
    /// beatoraja `.bmt` 出力時に playlist entry の欠けている hash を補完する resolver を取得します。
    /// </summary>
    private readonly Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> beatorajaBmtSongHashResolverFactory;

    private readonly Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider;

    private readonly Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider;

    private readonly Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider;

    private readonly Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionTsvContentFetcher;

    private readonly Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionStellaContentFetcher;

    /// <summary>
    /// 初期化処理の連携用に一時保持するセマフォです。
    /// </summary>
    private SemaphoreSlim initSemaphore;

    /// <summary>
    /// 全件初期化工程全体を直列化するための書き込みロックです。
    /// </summary>
    private readonly ReaderWriterLockSlimWrapper rwlockBMSTablesInitializeAll = new();

    /// <summary>
    /// 最小限のプレイリスト初期化工程を保護する書き込みロックです。
    /// </summary>
    private readonly ReaderWriterLockSlimWrapper rwlockBMSTablesInitializeMin = new();

    /// <summary>
    /// プレイリスト一覧そのものへの更新を保護する書き込みロックです。
    /// </summary>
    private readonly ReaderWriterLockSlimWrapper rwlockBMSTables = new();

    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; set; }

    internal PlaylistBmtOutputOwner BmtOutput => bmtOutput;

    internal PlaylistExternalSyncOwner ExternalSyncOwner => externalSyncOwner;

    internal PlaylistOperationNotificationOwner OperationNotificationOwner => operationNotificationOwner;

    internal bool IsShutdownRequested => shutdownCoordinator.IsRequested;

    internal void RequestShutdown(string reason)
    {
        shutdownCoordinator.Request(
            reason,
            () => BmtOutput.RequestShutdown(reason),
            () => playlistEntriesHydrationOwner.ClearPendingForShutdown(reason),
            LogPlaylistPerformance);
    }

    internal bool HasShutdownBlockingWork =>
        IsPlaylistUpdating
        || playlistEntriesHydrationOwner.HasBlockingWork
        || BmtOutput.HasBlockingWork;

    internal string GetShutdownBlockingWorkLogFields()
    {
        return "playlistUpdating=" + IsPlaylistUpdating.ToString().ToLowerInvariant()
            + " playlistEntriesHydrationRunning=" + playlistEntriesHydrationOwner.PlaylistEntriesHydrationRunning.ToString().ToLowerInvariant()
            + " " + BmtOutput.GetShutdownBlockingWorkLogFields();
    }

    private bool TrySkipForShutdown(string operation, string reason)
    {
        if (!IsShutdownRequested)
        {
            return false;
        }
        LogPlaylistPerformance((operation ?? "background_work") + " skipped reason=shutdown_requested requestReason=" + FormatTextForLog(reason));
        return true;
    }

    /// <summary>
    /// 全件初期化ロックの状態変化を監視するリスナーです。
    /// </summary>
    private readonly PropertyChangedEventListener listenerForRwlockBMSTablesInitializedAll;

    /// <summary>
    /// 最小初期化ロックの状態変化を監視するリスナーです。
    /// </summary>
    private readonly PropertyChangedEventListener listenerForRwlockBMSTablesInitializedMin;

    /// <summary>
    /// プレイリスト一覧ロックの状態変化を監視するリスナーです。
    /// </summary>
    private readonly PropertyChangedEventListener listenerForRwlockBMSTables;

    /// <summary>
    /// UI バインディングに公開するプレイリスト一覧を保持します。
    /// </summary>
    private DispatcherCollection<BMSTable> _BMSTables = new(DispatcherHelper.UIDispatcher);

    /// <summary>
    /// プレイリスト同期処理の実行中状態を保持します。
    /// </summary>
    private bool _IsPlaylistUpdating;

    private int playlistUpdatingActiveCount;

    /// <summary>
    /// プレイリスト同期処理が実行中かどうかを表します。
    /// </summary>
    /// <returns>同期処理中であれば <see langword="true"/>。</returns>
    public bool IsPlaylistUpdating
    {
        get
        {
            return _IsPlaylistUpdating;
        }
        set
        {
            if (_IsPlaylistUpdating != value)
            {
                _IsPlaylistUpdating = value;
                RaisePropertyChanged("IsPlaylistUpdating");
            }
        }
    }

    private void EnterPlaylistUpdating()
    {
        if (Interlocked.Increment(ref playlistUpdatingActiveCount) == 1)
        {
            IsPlaylistUpdating = true;
        }
    }

    private void ExitPlaylistUpdating()
    {
        int count = Interlocked.Decrement(ref playlistUpdatingActiveCount);
        if (count <= 0)
        {
            if (count < 0)
            {
                Interlocked.Exchange(ref playlistUpdatingActiveCount, 0);
            }
            IsPlaylistUpdating = false;
        }
    }

    public bool PlaylistEntriesHydrationRunning => playlistEntriesHydrationOwner.PlaylistEntriesHydrationRunning;

    public int PlaylistEntriesHydrationRequestedVersion => playlistEntriesHydrationOwner.PlaylistEntriesHydrationRequestedVersion;

    public int PlaylistEntriesHydrationCompletedVersion => playlistEntriesHydrationOwner.PlaylistEntriesHydrationCompletedVersion;

    internal event EventHandler<PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceiptEventArgs> PlaylistEntriesHydrationReceiptPublished;

    /// <summary>
    /// UI に公開するプレイリスト一覧です。
    /// </summary>
    /// <returns>現在のプレイリスト一覧コレクション。</returns>
    public DispatcherCollection<BMSTable> BMSTables
    {
        get
        {
            return _BMSTables;
        }
        set
        {
            if (_BMSTables != value)
            {
                List<BMSTable> previousTables = [.. (_BMSTables ?? Enumerable.Empty<BMSTable>()).Where(table => table != null)];
                _BMSTables = value;
                playlistAggregatePersistenceOwner.SetActiveCollection(previousTables, value);
                RaisePropertyChanged("BMSTables");
            }
        }
    }

    /// <summary>
    /// 全件初期化ロックに書き込み待ち、または書き込み保持があるかを返します。
    /// </summary>
    /// <returns>初期化全体が書き込み待ちまたは実行中なら <see langword="true"/>。</returns>
    public bool IsWriteLockHeldBMSTablesInitializeAll
    {
        get
        {
            if (BMSTables != null && rwlockBMSTablesInitializeAll.LockingWriteCount == 0)
            {
                return rwlockBMSTablesInitializeAll.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    /// <summary>
    /// 最小初期化ロックに書き込み待ち、または書き込み保持があるかを返します。
    /// </summary>
    /// <returns>最小初期化が書き込み待ちまたは実行中なら <see langword="true"/>。</returns>
    public bool IsWriteLockHeldBMSTablesInitializeMin
    {
        get
        {
            if (rwlockBMSTablesInitializeMin.LockingWriteCount == 0)
            {
                return rwlockBMSTablesInitializeMin.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    /// <summary>
    /// プレイリスト一覧ロックに書き込み待ち、または書き込み保持があるかを返します。
    /// </summary>
    /// <returns>一覧更新が書き込み待ちまたは実行中なら <see langword="true"/>。</returns>
    public bool IsWriteLockHeldBMSTables
    {
        get
        {
            if (rwlockBMSTables.LockingWriteCount == 0)
            {
                return rwlockBMSTables.WaitingWriteCount > 0;
            }
            return true;
        }
    }

    /// <summary>
    /// プレイリスト一覧または各プレイリスト本体のいずれかに書き込み待ちがあるかを返します。
    /// </summary>
    /// <returns>いずれかのプレイリスト更新が進行中なら <see langword="true"/>。</returns>
    public bool IsWriteLockHeldAnyBMSTable
    {
        get
        {
            if (!rwlockBMSTables.TryEnterReadLock(0))
            {
                return true;
            }
            try
            {
                return BMSTables == null
                    || BMSTables.Any(t =>
                        t.ReaderWriterLock.LockingWriteCount != 0
                        || t.ReaderWriterLock.WaitingWriteCount > 0);
            }
            finally
            {
                rwlockBMSTables.ExitReadLock();
            }
        }
    }

    /// <summary>
    /// プレイリスト一覧ロックの書き込みロックを取得します。
    /// </summary>
    public void AcquireWriterLockBMSTables()
    {
        rwlockBMSTables.EnterWriteLock();
    }

    /// <summary>
    /// プレイリスト一覧ロックの書き込みロックを解放します。
    /// </summary>
    public void FreeWriterLockBMSTables()
    {
        rwlockBMSTables.ExitWriteLock();
    }

    /// <summary>
    /// プレイリスト一覧ロックの読み取りロックを取得します。
    /// </summary>
    public void AcquireReaderLockBMSTables()
    {
        rwlockBMSTables.EnterReadLock();
    }

    /// <summary>
    /// プレイリスト一覧ロックの読み取りロックを解放します。
    /// </summary>
    public void FreeReaderLockBMSTables()
    {
        rwlockBMSTables.ExitReadLock();
    }

    public bool ContainsBMSTable(BMSTable table)
    {
        if (table == null)
        {
            return false;
        }
        if (rwlockBMSTables.IsWriteLockHeld)
        {
            return BMSTables.Contains(table);
        }
        using (rwlockBMSTables.GetReaderGuard())
        {
            return BMSTables.Contains(table);
        }
    }

    /// <summary>
    /// プレイリスト DB への接続情報と関連取得デリゲートを初期化します。
    /// 必要なテーブルとインデックスもここで整備します。
    /// </summary>
    /// <param name="_lr2SongDB">プレイリスト保存先の LR2 Song DB パス。</param>
    /// <param name="getLR2Config">LR2 設定を返すデリゲート。</param>
    /// <param name="_lr2ScoreDB">推奨表更新に使う LR2 Score DB パス。</param>
    /// <param name="getBMSScores">ローカルスコア一覧を返すデリゲート。</param>
    /// <param name="getBeatorajaBmtSongHashResolver">beatoraja `.bmt` 出力用 hash 補完 resolver を返すデリゲート。</param>
    /// <exception cref="ArgumentNullException"><paramref name="_lr2SongDB"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentException">必要な DB ファイルが存在しない場合。</exception>
    public BMSPlaylist(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config = null,
        string _lr2ScoreDB = null,
        Func<List<BMSScore>> getBMSScores = null,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver = null)
        : this(
            _lr2SongDB,
            getLR2Config,
            _lr2ScoreDB,
            getBMSScores,
            getBeatorajaBmtSongHashResolver,
            PlaylistUrlCompletionOptionsSnapshot.CreateCurrent,
            BeatorajaBmtOptionsSnapshot.CreateCurrent,
            CustomFolderOutputSettingsSnapshot.CreateCurrent)
    {
    }

    internal BMSPlaylist(
        string _lr2SongDB,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization)
        : this(
            _lr2SongDB,
            null,
            null,
            null,
            null,
            PlaylistUrlCompletionOptionsSnapshot.CreateCurrent,
            BeatorajaBmtOptionsSnapshot.CreateCurrent,
            CustomFolderOutputSettingsSnapshot.CreateCurrent,
            lr2PlaylistFolderSynchronization)
    {
    }

    internal BMSPlaylist(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization)
        : this(
            _lr2SongDB,
            getLR2Config,
            null,
            null,
            null,
            PlaylistUrlCompletionOptionsSnapshot.CreateCurrent,
            BeatorajaBmtOptionsSnapshot.CreateCurrent,
            CustomFolderOutputSettingsSnapshot.CreateCurrent,
            lr2PlaylistFolderSynchronization)
    {
    }

    internal BMSPlaylist(
        string _lr2SongDB,
        string _lr2ScoreDB,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization)
        : this(
            _lr2SongDB,
            null,
            _lr2ScoreDB,
            null,
            null,
            PlaylistUrlCompletionOptionsSnapshot.CreateCurrent,
            BeatorajaBmtOptionsSnapshot.CreateCurrent,
            CustomFolderOutputSettingsSnapshot.CreateCurrent,
            lr2PlaylistFolderSynchronization)
    {
    }

    internal BMSPlaylist(
        string _lr2SongDB,
        Func<LR2Config> getLR2Config,
        string _lr2ScoreDB,
        Func<List<BMSScore>> getBMSScores,
        Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> getBeatorajaBmtSongHashResolver,
        Func<PlaylistUrlCompletionOptionsSnapshot> playlistUrlCompletionOptionsProvider,
        Func<BeatorajaBmtOptionsSnapshot> beatorajaBmtOptionsProvider,
        Func<CustomFolderOutputSettingsSnapshot> customFolderOutputSettingsProvider,
        ILr2PlaylistFolderSynchronizationPort lr2PlaylistFolderSynchronization = null,
        Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionTsvContentFetcher = null,
        Func<Uri, CancellationToken, Task<string>> playlistUrlCompletionStellaContentFetcher = null)
    {
        if (_lr2SongDB == null)
        {
            throw new ArgumentNullException("_LR2SongDB");
        }
        if (!File.Exists(_lr2SongDB))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2SongDBNotFound, _lr2SongDB), "_LR2SongDB");
        }
        if (_lr2ScoreDB != null && !File.Exists(_lr2ScoreDB))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2ScoreDBNotFound, _lr2ScoreDB), "_lr2ScoreDB");
        }
        lr2SongDBPath = _lr2SongDB;
        playlistPersistenceRepository = new PlaylistPersistenceRepository(_lr2SongDB);
        playlistAggregatePersistenceOwner = new PlaylistAggregatePersistenceOwner(playlistPersistenceRepository, rwlockBMSTables);
        playlistAggregatePersistenceOwner.SetActiveCollection([], _BMSTables);
        lr2config = (getLR2Config ?? (Func<LR2Config>)(() => (LR2Config)null));
        beatorajaBmtSongHashResolverFactory = getBeatorajaBmtSongHashResolver;
        this.playlistUrlCompletionOptionsProvider = playlistUrlCompletionOptionsProvider ?? PlaylistUrlCompletionOptionsSnapshot.CreateCurrent;
        this.beatorajaBmtOptionsProvider = beatorajaBmtOptionsProvider ?? BeatorajaBmtOptionsSnapshot.CreateCurrent;
        this.customFolderOutputSettingsProvider = customFolderOutputSettingsProvider ?? CustomFolderOutputSettingsSnapshot.CreateCurrent;
        this.playlistUrlCompletionTsvContentFetcher = playlistUrlCompletionTsvContentFetcher;
        this.playlistUrlCompletionStellaContentFetcher = playlistUrlCompletionStellaContentFetcher;
        this.lr2PlaylistFolderSynchronization = lr2PlaylistFolderSynchronization;
        operationNotificationOwner = new PlaylistOperationNotificationOwner();
        playlistEntriesHydrationOwner = new PlaylistEntriesHydrationOwner(
            playlistPersistenceRepository,
            () => playlistAggregatePersistenceOwner.GetActiveCollectionSnapshot(),
            generation => playlistAggregatePersistenceOwner.TryBeginHydrationPublish(generation),
            () => IsShutdownRequested,
            () => StartupBackgroundTaskScheduler,
            LogPlaylistPerformance,
            (exception, reason) => Ribbit.Logging.NLogWrapper.FileLogger?.Warn(
                exception,
                "playlist_entries_hydration_completion_failed reason=" + FormatTextForLog(reason)));
        bmtOutput = new PlaylistBmtOutputOwner(
            playlistAggregatePersistenceOwner,
            playlistEntriesHydrationOwner,
            this.beatorajaBmtOptionsProvider,
            this.beatorajaBmtSongHashResolverFactory,
            () => StartupBackgroundTaskScheduler,
            LogPlaylistPerformance,
            (exception, message) => Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception, message),
            () => shutdownCoordinator.IsRequested);
        PlaylistExternalSyncOwner externalSyncOwnerLocal = null;
        recommendedTableOwner = new PlaylistRecommendedTableOwner(
            _lr2ScoreDB,
            getBMSScores ?? (() => null),
            () => initSemaphore,
            uri => externalSyncOwnerLocal.LoadExternalTable(uri),
            new AppPlaylistRecommendedTableHttpClient(playlistHttpClient),
            operationNotificationOwner);
        externalSyncOwnerLocal = new PlaylistExternalSyncOwner(
            playlistHttpClient,
            recommendedTableOwner,
            (exception, message) => NLogWrapper.FileLogger?.Warn(exception, message),
            IsPlaylistUrlCompletionEnabled,
            SchedulePlaylistUrlCompletionRefresh,
            playlistAggregatePersistenceOwner,
            EnsurePlaylistEntriesLoaded,
            table => playlistAggregatePersistenceOwner.IsActive(table),
            (table, reason) => BmtOutput.QueueBeatorajaBmtExportForTable(table, reason),
            ApplyCachedPlaylistUrlCompletionToTable,
            EnterPlaylistUpdating,
            ExitPlaylistUpdating,
            LogPlaylistPerformance,
            table =>
            {
                return InvokeBMSTablesCollectionMutation(delegate
                {
                    using (rwlockBMSTables.GetWriterGuard())
                    {
                        if (BMSTables.Contains(table))
                        {
                            return true;
                        }
                        if (BMSTables.Any(existing => existing != null
                            && !ReferenceEquals(existing, table)
                            && string.Equals(existing.name, table.name, StringComparison.Ordinal)))
                        {
                            return false;
                        }
                        playlistAggregatePersistenceOwner.MarkActiveTables([table]);
                        BMSTables.Add(table);
                        return true;
                    }
                });
            },
            tables =>
            {
                return InvokeBMSTablesCollectionMutation(delegate
                {
                    using (rwlockBMSTables.GetWriterGuard())
                    {
                        var visibleNames = new HashSet<string>(
                            BMSTables.Select(table => table?.name).Where(name => name != null),
                            StringComparer.Ordinal);
                        BMSTable duplicate = (tables ?? [])
                            .Where(table => table != null)
                            .FirstOrDefault(table => BMSTables.Contains(table));
                        if (duplicate == null)
                        {
                            foreach (BMSTable table in tables ?? [])
                            {
                                if (!visibleNames.Add(table?.name))
                                {
                                    duplicate = table;
                                    break;
                                }
                            }
                        }
                        if (duplicate != null)
                        {
                            return duplicate;
                        }
                        foreach (BMSTable table in tables ?? [])
                        {
                            playlistAggregatePersistenceOwner.MarkActiveTables([table]);
                            BMSTables.Add(table);
                        }
                        return null;
                    }
                });
            },
            RemoveTablesFromVisibleCollection,
            this.customFolderOutputSettingsProvider,
            ResolveCustomFolderOutputDirectory,
            (table, outputDirectoryBefore, outputDirectoryAfter, isRootFolder, rootOutputBaseDirectoryBefore, outputBaseDirectoryBefore, inferOutputBaseDirectoryBeforeWhenMissing, settings) =>
                customFolderOutputMaintenanceOwner.TryMigrateCustomFolderOutputDirectory(
                    table,
                    outputDirectoryBefore,
                    outputDirectoryAfter,
                    isRootFolder,
                    rootOutputBaseDirectoryBefore,
                    outputBaseDirectoryBefore,
                    inferOutputBaseDirectoryBeforeWhenMissing,
                    settings),
            (tables, reason) => BmtOutput.QueueBeatorajaBmtExportForTables(tables, reason),
            ApplyCachedPlaylistUrlCompletionToTables);
        externalSyncOwner = externalSyncOwnerLocal;
        customFolderOutputOwner = new PlaylistCustomFolderOutputOwner(
            this.customFolderOutputSettingsProvider,
            (table, settings) => BuildCustomFolderDefinitions(table, settings),
            ResolveCustomFolderOutputDirectory,
            CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration,
            (projections, settings) => CreateKnownCustomFolderOutputDirectories(projections, settings),
            CreateCustomFolderDirectoryRowGenerationScopes,
            LogPlaylistPerformance);
        customFolderOutputStatusOwner = new PlaylistCustomFolderOutputStatusOwner(
            playlistPersistenceRepository,
            customFolderOutputOwner,
            this.customFolderOutputSettingsProvider,
            LogPlaylistPerformance);
        customFolderOutputMaintenanceOwner = new PlaylistCustomFolderOutputMaintenanceOwner(
            customFolderOutputOwner,
            this.customFolderOutputSettingsProvider,
            () => GetCustomFolderTablesSnapshot(),
            EnsurePlaylistEntriesLoaded,
            () => lr2config(),
            (request) => SyncCustomFolderRowsBatch(
                request.OutputDirectories,
                request.OutputRowScopeDirectories,
                request.Items,
                request.DirectoryRowGenerationScopeDirectories,
                request.DirectoryEntries,
                request.PruneScopePaths,
                request.PruneExcludedDirectories,
                request.PruneExcludedPaths,
                request.EmptyOutputDirectories),
            customFolderOutputStatusOwner,
            ResolveCustomFolderOutputDirectory,
            LogPlaylistPerformance,
            operationNotificationOwner);
        playlistAggregatePersistenceOwner.AttachEntriesHydrationOwner(playlistEntriesHydrationOwner);
        playlistEntriesHydrationOwner.PropertyChanged += (_, eventArgs) =>
            RaisePropertyChanged(eventArgs.PropertyName);
        playlistEntriesHydrationOwner.HydrationReceiptPublished += PlaylistEntriesHydrationReceiptPublishedHandler;
        listenerForRwlockBMSTablesInitializedAll = new PropertyChangedEventListener(rwlockBMSTablesInitializeAll);
        listenerForRwlockBMSTablesInitializedMin = new PropertyChangedEventListener(rwlockBMSTablesInitializeMin);
        listenerForRwlockBMSTables = new PropertyChangedEventListener(rwlockBMSTables);
        listenerForRwlockBMSTablesInitializedAll.RegisterHandler(() => rwlockBMSTablesInitializeAll.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSTablesInitializeAll);
        });
        listenerForRwlockBMSTablesInitializedMin.RegisterHandler(() => rwlockBMSTablesInitializeMin.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSTablesInitializeMin);
        });
        listenerForRwlockBMSTables.RegisterHandler(() => rwlockBMSTables.LockingWriteCount, delegate
        {
            RaisePropertyChanged(() => IsWriteLockHeldBMSTables);
        });
    }

    /// <summary>
    /// DB からプレイリストを読み込み、必要に応じて外部同期とカスタムフォルダ出力まで実行します。
    /// 初期化済み一覧が空でない場合は、既存一覧を土台に同期処理のみ進めます。
    /// </summary>
    /// <param name="reloadExtPlaylist">外部同期対象プレイリストを再取得するかどうか。</param>
    /// <param name="semaphore">他初期化処理と連携するためのセマフォ。</param>
    /// <param name="queueBeatorajaBmtExportAfterHydration">playlist entries hydration 後に beatoraja `.bmt` 全体投影出力を予約するかどうか。</param>
    public void Initialize(bool reloadExtPlaylist = true, SemaphoreSlim semaphore = null, bool queueBeatorajaBmtExportAfterHydration = true)
    {
        var stopwatchInitialize = Stopwatch.StartNew();
        long updateTablesMs = 0L;
        long lr2configSyncMs = 0L;
        if (!playlistAggregatePersistenceOwner.TryBeginReload())
        {
            throw new InvalidOperationException("Playlist initialization cannot run while another playlist persistence transition is active.");
        }
        try
        {
            if (semaphore != null)
            {
                initSemaphore = semaphore;
            }
            using (rwlockBMSTablesInitializeAll.GetWriterGuard())
            {
                using (rwlockBMSTablesInitializeMin.GetWriterGuard())
                {
                    using (rwlockBMSTables.GetWriterGuard())
                    {
                        if (BMSTables.Count == 0)
                        {
                            List<BMSTable> list = playlistEntriesHydrationOwner.LoadPlaylistHeaders(out long loadTablesMs);
                            LogPlaylistPerformance("playlist_init_header loadTablesMs=" + loadTablesMs
                                + " tableCount=" + list.Count
                                + " readOnly=true"
                                + " dbLockWaitMs=0"
                                + " entriesDeferred=true");
                            BMSTables.AddRange(list);
                            playlistAggregatePersistenceOwner.MarkActiveTables(list);
                        }
                    }
                }
                initSemaphore?.Release();
                updateTablesMs = 0L;
                var stopwatchLr2configSync = Stopwatch.StartNew();
                bool rootOutputSearchRootsChanged = SyncRootFolderOutputDirectoriesToLr2Config();
                stopwatchLr2configSync.Stop();
                lr2configSyncMs = stopwatchLr2configSync.ElapsedMilliseconds;
                QueueDeferredPlaylistEntriesHydration(
                    "Initialize",
                    runExternalSyncAfterHydration: reloadExtPlaylist,
                    queueBeatorajaBmtExportAfterHydration: queueBeatorajaBmtExportAfterHydration,
                    runCustomFolderOutputRepairAfterHydration: true,
                    verifyRootOutputDirectoryRows: rootOutputSearchRootsChanged);
            }
            stopwatchInitialize.Stop();
            LogPlaylistPerformance("playlist_init update_tables_ms=" + updateTablesMs + " lr2config_sync_ms=" + lr2configSyncMs + " total_ms=" + stopwatchInitialize.ElapsedMilliseconds);
            SchedulePlaylistUrlCompletionRefresh("Initialize");
            initSemaphore = null;
        }
        finally
        {
            playlistAggregatePersistenceOwner.EndReload();
        }
    }

    /// <summary>
    /// プレイリスト一覧とエントリを DB から再読み込みします。
    /// score DB は触らず、外部同期は呼び出し側で別途 schedule します。
    /// </summary>
    /// <param name="queueBeatorajaBmtExportAfterHydration">playlist entries hydration 後に beatoraja `.bmt` 全体投影出力を予約するかどうか。</param>
    public void ReloadTables(bool queueBeatorajaBmtExportAfterHydration = true)
    {
        var stopwatchReloadTables = Stopwatch.StartNew();
        long lr2configSyncMs = 0L;
        if (!playlistAggregatePersistenceOwner.TryBeginReload())
        {
            throw new InvalidOperationException("Playlist reload cannot run while another playlist persistence transition is active.");
        }
        try
        {
            using (rwlockBMSTablesInitializeAll.GetWriterGuard())
            {
                List<BMSTable> list;
                List<BMSTable> previousTables;
                long loadTablesMs;
                using (rwlockBMSTablesInitializeMin.GetWriterGuard())
                {
                    using (rwlockBMSTables.GetWriterGuard())
                    {
                        previousTables = [.. BMSTables.Where(table => table != null)];
                        list = playlistEntriesHydrationOwner.LoadPlaylistHeaders(out loadTablesMs);
                        BMSTables.Clear();
                        BMSTables.AddRange(list);
                        playlistAggregatePersistenceOwner.SetActiveCollection(previousTables, BMSTables);
                    }
                }
                LogPlaylistPerformance("playlist_reload_tables_header loadTablesMs=" + loadTablesMs
                    + " tableCount=" + list.Count
                    + " readOnly=true"
                    + " dbLockWaitMs=0"
                    + " entriesDeferred=true");
                var stopwatchLr2configSync = Stopwatch.StartNew();
                bool rootOutputSearchRootsChanged = SyncRootFolderOutputDirectoriesToLr2Config();
                stopwatchLr2configSync.Stop();
                lr2configSyncMs = stopwatchLr2configSync.ElapsedMilliseconds;
                QueueDeferredPlaylistEntriesHydration(
                    "ReloadTables",
                    runExternalSyncAfterHydration: false,
                    queueBeatorajaBmtExportAfterHydration: queueBeatorajaBmtExportAfterHydration,
                    runCustomFolderOutputRepairAfterHydration: true,
                    verifyRootOutputDirectoryRows: rootOutputSearchRootsChanged);
            }
            stopwatchReloadTables.Stop();
            LogPlaylistPerformance("playlist_reload_tables lr2config_sync_ms=" + lr2configSyncMs + " total_ms=" + stopwatchReloadTables.ElapsedMilliseconds);
            SchedulePlaylistUrlCompletionRefresh("ReloadTables");
        }
        finally
        {
            playlistAggregatePersistenceOwner.EndReload();
        }
    }

    private void QueueCustomFolderOutputRepairAfterHydration(string reason, bool verifyRootOutputDirectoryRows)
    {
        if (TrySkipForShutdown("custom_folder_repair_after_hydration", reason))
        {
            return;
        }
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        if (!settings.OperationModeLR2DB)
        {
            return;
        }

        Task work()
        {
            if (IsShutdownRequested)
            {
                LogPlaylistPerformance("custom_folder_repair_after_hydration skipped reason=shutdown_requested requestReason=" + FormatTextForLog(reason));
                return Task.CompletedTask;
            }
            RepairMissingCustomFolderOutputsAfterHydrationCore(reason, verifyRootOutputDirectoryRows, settings);
            return Task.CompletedTask;
        }

        if (StartupBackgroundTaskScheduler != null)
        {
            if (StartupBackgroundTaskScheduler("playlist_custom_folder_output_repair", reason ?? "queue", "playlist_entries_hydration", work))
            {
                return;
            }
            LogPlaylistPerformance("custom_folder_repair_after_hydration skipped reason=startup_scheduler_rejected requestReason=" + FormatTextForLog(reason));
            return;
        }
        if (IsShutdownRequested)
        {
            LogPlaylistPerformance("custom_folder_repair_after_hydration skipped reason=shutdown_requested requestReason=" + FormatTextForLog(reason));
            return;
        }
        Task.Run(work).Logging("QueueCustomFolderOutputRepairAfterHydration");
    }

    private void PlaylistEntriesHydrationReceiptPublishedHandler(
        object sender,
        PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceiptEventArgs eventArgs)
    {
        PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt receipt = eventArgs?.Receipt;
        if (receipt == null)
        {
            return;
        }

        PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent continuation = receipt.Continuation;
        if (IsShutdownRequested)
        {
            return;
        }
        if (!playlistEntriesHydrationOwner.IsReceiptCurrent(receipt))
        {
            eventArgs.CompositionFailure = new InvalidOperationException(
                "Playlist hydration receipt is no longer current before consumer composition.");
            eventArgs.RetryContinuation = continuation;
            eventArgs.RetryRequested = true;
            return;
        }
        PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceipt publishedReceipt = receipt;
        bool externalSyncLeaseRequired = continuation?.RunExternalSyncAfterHydration == true;
        if (continuation?.RunExternalSyncAfterHydration == true)
        {
            bool externalSyncCompleted = false;
            try
            {
                if (IsShutdownRequested)
                {
                    return;
                }
                externalSyncOwner.UpdateBMSTablesInternalAsync(reloadExtPlaylist: true).GetAwaiter().GetResult();
                externalSyncCompleted = true;
                if (IsShutdownRequested)
                {
                    return;
                }
                publishedReceipt = playlistEntriesHydrationOwner.CreateReceiptForCurrentTables(
                    receipt,
                    continuation.WithoutExternalSync());
            }
            catch (Exception ex)
            {
                if (IsShutdownRequested)
                {
                    return;
                }
                eventArgs.CompositionFailure = ex;
                eventArgs.RetryContinuation = externalSyncCompleted
                    ? continuation.WithoutExternalSync()
                    : continuation;
                eventArgs.RetryRequested = externalSyncCompleted && !IsShutdownRequested;
                NLogWrapper.FileLogger?.Warn(
                    ex,
                    "playlist_entries_hydration_external_sync_failed reason=" + FormatTextForLog(receipt.Reason));
                return;
            }
        }

        if (!playlistEntriesHydrationOwner.IsReceiptCurrent(publishedReceipt))
        {
            if (!IsShutdownRequested)
            {
                eventArgs.CompositionFailure = new InvalidOperationException(
                    "Playlist hydration receipt became stale during consumer composition.");
                eventArgs.RetryContinuation = publishedReceipt.Continuation;
                eventArgs.RetryRequested = true;
            }
            return;
        }

        IDisposable receiptPublicationLease = null;
        if (externalSyncLeaseRequired)
        {
            receiptPublicationLease = playlistAggregatePersistenceOwner.TryBeginHydrationPublish(
                publishedReceipt.Generation);
        }
        if (externalSyncLeaseRequired
            && receiptPublicationLease == null)
        {
            if (!IsShutdownRequested)
            {
                eventArgs.CompositionFailure = new InvalidOperationException(
                    "Playlist hydration receipt publication could not reserve the current collection.");
                eventArgs.RetryContinuation = publishedReceipt.Continuation;
                eventArgs.RetryRequested = true;
            }
            return;
        }

        using (receiptPublicationLease)
        {
            if (!playlistEntriesHydrationOwner.IsReceiptCurrent(publishedReceipt))
            {
                if (!IsShutdownRequested)
                {
                    eventArgs.CompositionFailure = new InvalidOperationException(
                        "Playlist hydration receipt became stale while acquiring the publication reservation.");
                    eventArgs.RetryContinuation = publishedReceipt.Continuation;
                    eventArgs.RetryRequested = true;
                }
                return;
            }

            PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent effectiveContinuation = publishedReceipt.Continuation;
            if (effectiveContinuation?.RunCustomFolderOutputRepairAfterHydration == true)
            {
                try
                {
                    QueueCustomFolderOutputRepairAfterHydration(
                        receipt.Reason,
                        effectiveContinuation.VerifyRootOutputDirectoryRows);
                }
                catch (Exception ex)
                {
                    NLogWrapper.FileLogger?.Warn(
                        ex,
                        "playlist_entries_hydration_custom_folder_repair_failed reason=" + FormatTextForLog(receipt.Reason));
                }
            }
            if (effectiveContinuation?.QueueBeatorajaBmtExportAfterHydration == true)
            {
                try
                {
                    BmtOutput.QueueBeatorajaBmtExportAll(receipt.Reason);
                }
                catch (Exception ex)
                {
                    NLogWrapper.FileLogger?.Warn(
                        ex,
                        "playlist_entries_hydration_bmt_export_failed reason=" + FormatTextForLog(receipt.Reason));
                }
            }

            if (playlistEntriesHydrationOwner.IsReceiptCurrent(publishedReceipt))
            {
                try
                {
                    PlaylistEntriesHydrationReceiptPublished?.Invoke(
                        this,
                        new PlaylistEntriesHydrationOwner.PlaylistEntriesHydrationReceiptEventArgs(publishedReceipt));
                }
                catch (Exception ex)
                {
                    if (IsShutdownRequested)
                    {
                        return;
                    }
                    eventArgs.CompositionFailure = ex;
                    eventArgs.RetryContinuation = publishedReceipt.Continuation;
                    // A presentation consumer failure is deterministic for this receipt. Let the
                    // hydration owner fault the operation instead of retrying an unchanged consumer.
                    eventArgs.RetryRequested = false;
                }
            }
            else if (!IsShutdownRequested)
            {
                eventArgs.CompositionFailure = new InvalidOperationException(
                    "Playlist hydration receipt became stale before presentation publication.");
                eventArgs.RetryContinuation = publishedReceipt.Continuation;
                eventArgs.RetryRequested = true;
            }
        }
    }

    private bool SyncRootFolderOutputDirectoriesToLr2Config()
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            return customFolderOutputMaintenanceOwner.SyncRootFolderOutputDirectoriesToLr2Config(
                GetCustomFolderOutputSettings());
        }
    }

    /// <summary>
    /// 現在の custom-folder output base と root playlist output を LR2Config の search root へ同期します。
    /// 設定保存後の旧 root の除去と、起動後に残った親 root の adoption 整理を同じ playlist owner で行います。
    /// </summary>
    /// <param name="previousRootOutputBaseDirectory">保存前に設定されていた root output base。起動後の修復では現在値を渡します。</param>
    /// <param name="configOverride">呼び出し側が保持している LR2Config。未指定時は playlist の provider を使います。</param>
    /// <returns>LR2Config が変更された場合は <see langword="true"/>。</returns>
    internal bool SyncCustomFolderOutputSearchRootsAfterSettingsChange(
        string previousRootOutputBaseDirectory,
        LR2Config configOverride = null)
    {
        return SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
            previousRootOutputBaseDirectory,
            configOverride,
            settings: null);
    }

    internal bool SyncCustomFolderOutputSearchRootsAfterSettingsChangeWithSettings(
        string previousRootOutputBaseDirectory,
        LR2Config configOverride,
        CustomFolderOutputSettingsSnapshot settings)
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            return customFolderOutputMaintenanceOwner.SyncCustomFolderOutputSearchRootsAfterSettingsChange(
                previousRootOutputBaseDirectory,
                configOverride,
                settings ?? GetCustomFolderOutputSettings());
        }
    }

    public void QueueDeferredPlaylistEntriesHydration(
        string reason,
        bool runExternalSyncAfterHydration = false,
        bool queueBeatorajaBmtExportAfterHydration = false,
        bool runCustomFolderOutputRepairAfterHydration = false,
        bool verifyRootOutputDirectoryRows = false)
    {
        PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent continuation =
            runExternalSyncAfterHydration
                || queueBeatorajaBmtExportAfterHydration
                || runCustomFolderOutputRepairAfterHydration
                || verifyRootOutputDirectoryRows
                ? new PlaylistEntriesHydrationOwner.PlaylistHydrationContinuationIntent(
                    runExternalSyncAfterHydration,
                    queueBeatorajaBmtExportAfterHydration,
                    runCustomFolderOutputRepairAfterHydration,
                    verifyRootOutputDirectoryRows)
                : null;
        playlistEntriesHydrationOwner.QueueDeferredPlaylistEntriesHydration(
            reason,
            continuation);
    }

    internal Task EnsureAllPlaylistEntriesLoadedAsync(string reason, bool publishCompletedVersion = true)
    {
        return playlistEntriesHydrationOwner.EnsureAllPlaylistEntriesLoadedAsync(reason, publishCompletedVersion);
    }

    internal void EnsurePlaylistEntriesLoaded(BMSTable table, string reason)
    {
        playlistEntriesHydrationOwner.EnsurePlaylistEntriesLoaded(table, reason);
    }

    private List<string> makeCustomFolderTextsOtherFolder(
        BMSTable bmsTable,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetCustomFolderOutputSettings();
        SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.playcount);
        SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.playcount);
        var source = new Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType>
        {
            {
                "MY BEST",
                LR2SongDBExtended.playlist.CustomFolderSortType.PLAYCOUNT
            },
            {
                "NEW SONGS",
                LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE
            }
        };
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed);
        List<string> list = [.. source.Select(f => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "ORDER BY " + makeCustomFolderCmdSort(f.Value, asc: false, bmsTable.playlist_id), bmsTable.name, f.Key, 40))];
        if (settings.EnableDownloadLr2IrScoreAndDetectUnsent)
        {
            string tableName = SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName();
            string columnName = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.hash);
            string columnName2 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.clear);
            string columnName3 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.combo);
            string columnName4 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.pg);
            string columnName5 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.gr);
            string columnName6 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.gd);
            string columnName7 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.bd);
            string columnName8 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.pr);
            string columnName9 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName(e => e.minbp);
            string customFolderText = getCustomFolderText("song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0) AND NOT EXISTS (SELECT " + columnName + " FROM " + tableName + " WHERE song.hash = " + tableName + "." + columnName + " AND score.clear = " + tableName + "." + columnName2 + " AND maxcombo = " + columnName3 + " AND perfect = " + columnName4 + " AND great = " + columnName5 + " AND good = " + columnName6 + " AND bad = " + columnName7 + " AND poor = " + columnName8 + " AND score.minbp = " + tableName + "." + columnName9 + ") AND score.clear IS NOT NULL ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, "UNSENT SONGS");
            list.Add(customFolderText);
        }
        string customFolderText2 = getCustomFolderText("song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 1) ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, "REMOVED SONGS");
        list.Add(customFolderText2);
        return list;
    }

    /// <summary>
    /// 「ALL LONG NOTES」などカテゴリ全体フォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>カテゴリ全体フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsCategoryAllFolder(BMSTable bmsTable)
    {
        string columnName = SQLiteTable<LR2SongDB.song>.GetColumnName(e => e.longnote);
        string columnName2 = SQLiteTable<LR2SongDB.song>.GetColumnName(e => e.judge);
        List<string[]> list =
        [
            [
                "ALL LONG NOTES",
                columnName + " = 1"
            ],
            [
                "ALL VERY HARD JUDGES",
                columnName2 + " = " + 0
            ],
            [
                "ALL HARD JUDGES",
                columnName2 + " = " + 1
            ],
            [
                "ALL NORMAL JUDGES",
                columnName2 + " = " + 2
            ],
            [
                "ALL EASY JUDGES",
                columnName2 + " >= " + 3
            ],
        ];
        List<string[]> source = list;
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed);
        return [.. source.Select(a => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + a[1] + ")", bmsTable.name, a[0]))];
    }

    /// <summary>
    /// タイトル頭文字別カスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>アルファベット別フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsAlphabetFolder(BMSTable bmsTable)
    {
        var dict = new Dictionary<string, char[]>
        {
            {
                "A.B.C.D.",
                new char[2] { 'A', 'E' }
            },
            {
                "E.F.G.H.",
                new char[2] { 'E', 'I' }
            },
            {
                "I.J.K.L.",
                new char[2] { 'I', 'M' }
            },
            {
                "M.N.O.P.",
                new char[2] { 'M', 'Q' }
            },
            {
                "Q.R.S.T.",
                new char[2] { 'Q', 'U' }
            },
            {
                "U.V.W.X.Y.Z.",
                new char[2] { 'U', '[' }
            }
        };
        string title = "OTHERS";
        char[] array = ['A', '['];
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
        string columnNameTitle = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed);
        List<string> list = [.. (from t in dict.Keys
                             orderby t
                             select getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")) " : " ") + "AND UPPER(" + columnNameTitle + ") BETWEEN " + sqlQuote(dict[t][0].ToString()) + " AND " + sqlQuote(dict[t][1].ToString()) + " AND UPPER(" + columnNameTitle + ") != " + sqlQuote(dict[t][1].ToString()) + ((bmsTable.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ") " : " ") + "ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, t))];
        string command = ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")) " : " ") + "AND (UPPER(" + columnNameTitle + ") NOT BETWEEN " + sqlQuote(array[0].ToString()) + " AND " + sqlQuote(array[1].ToString()) + " OR UPPER(" + columnNameTitle + ") = " + sqlQuote(array[1].ToString()) + ")" + ((bmsTable.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ") " : " ") + "ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true);
        list.Add(getCustomFolderText(command, bmsTable.name, title));
        return list;
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsUserFolder(BMSTable bmsTable)
    {
        var definitions = new List<CustomFolderDefinition>();
        LR2SongDBExtended.playlist.CustomFolderSortType sortType = bmsTable.folder_sort_key;
        bool sortDirAsc = bmsTable.folder_sort_ascending;
        bool outputRandom = IsCustomFolderTypeEnabled(bmsTable, LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
        IReadOnlyList<CustomFolderEntryScope> scopes = CreateCustomFolderEntryScopes(bmsTable);
        foreach (CustomFolderEntryScope scope in scopes)
        {
            string orderBy = sortType == LR2SongDBExtended.playlist.CustomFolderSortType.NONE
                ? string.Empty
                : makeCustomFolderCmdSort(sortType, sortDirAsc, bmsTable.playlist_id, scope.IsAll ? null : scope.FolderName);
            string command = BuildCustomFolderCommand(CreatePlaylistEntryScopePredicate(bmsTable, scope), orderBy);
            AddCustomFolderDefinition(definitions, string.Empty, command, bmsTable.name, scope.Title, 0, bmsTable.name, DescribeCustomFolderScope(scope));
        }
        if (outputRandom)
        {
            foreach (CustomFolderEntryScope scope in scopes)
            {
                AddCustomFolderDefinition(definitions, string.Empty, BuildCustomFolderCommand(CreatePlaylistEntryScopePredicate(bmsTable, scope), "random()"), bmsTable.name, scope.Title + " RANDOM", 1, bmsTable.name, "Random: " + DescribeCustomFolderScope(scope), isRandomVariant: true);
            }
        }
        return definitions;
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsLevelFolder(BMSTable bmsTable)
    {
        var definitions = new List<CustomFolderDefinition>();
        bool outputRandom = IsCustomFolderTypeEnabled(bmsTable, LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
        string columnNameLevel = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.level);
        string levelBucketExpression = "CASE WHEN " + columnNameLevel + " >= 0 OR CAST(" + columnNameLevel + " AS INTEGER) = " + columnNameLevel + " THEN CAST(" + columnNameLevel + " AS INTEGER) ELSE CAST(" + columnNameLevel + " - 1.0 AS INTEGER) END";
        List<Tuple<string, string>> levelScopes = [.. (from e in bmsTable.entries
                                                       where !e.is_removed && e.level.HasValue
                                                       select (int)Math.Floor(e.level.Value)).Distinct().OrderBy(e => e)
            .Select(level => Tuple.Create("LEVEL " + level, levelBucketExpression + " = " + level))];
        if (bmsTable.entries.Any(e => !e.is_removed && !e.level.HasValue))
        {
            levelScopes.Add(Tuple.Create("LEVEL ???", columnNameLevel + " IS NULL"));
        }

        string orderBy = makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, asc: true, bmsTable.playlist_id)
            + ","
            + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true);
        foreach (Tuple<string, string> levelScope in levelScopes)
        {
            string command = BuildCustomFolderCommand(CombineCustomFolderPredicates(
                CreatePlaylistEntryScopePredicate(bmsTable, (string)null),
                "song.hash in (" + CreatePlaylistEntryMd5Subquery(bmsTable, null, levelScope.Item2, includeRemoved: false) + ")"),
                orderBy);
            AddCustomFolderDefinition(definitions, string.Empty, command, bmsTable.name, levelScope.Item1, 0, bmsTable.name, "Level: " + levelScope.Item1);
        }
        if (outputRandom)
        {
            foreach (Tuple<string, string> levelScope in levelScopes)
            {
                AddCustomFolderDefinition(definitions, string.Empty, BuildCustomFolderCommand(CombineCustomFolderPredicates(
                    CreatePlaylistEntryScopePredicate(bmsTable, (string)null),
                    "song.hash in (" + CreatePlaylistEntryMd5Subquery(bmsTable, null, levelScope.Item2, includeRemoved: false) + ")"),
                    "random()"), bmsTable.name, levelScope.Item1 + " RANDOM", 1, bmsTable.name, "Random level: " + levelScope.Item1, isRandomVariant: true);
            }
        }
        return definitions;
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsClearFolder(BMSTable bmsTable)
    {
        var definitions = new List<CustomFolderDefinition>();
        bool outputRandom = IsCustomFolderTypeEnabled(bmsTable, LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
        string scoreTable = SQLiteTable<LR2ScoreDB.score>.GetTableName();
        string clearColumn = scoreTable + "." + SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.clear);
        string opHistoryColumn = scoreTable + "." + SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.op_history);
        string opHistoryValue = "IFNULL(" + opHistoryColumn + ", 0)";
        string easyPredicate = clearColumn + " = 2 AND (" + opHistoryValue + " & " + ClearTypeStorageConverter.OptionHistoryEasy + ") != 0";
        var clearScopes = new List<Tuple<string, string, string>>
        {
            Tuple.Create("0 NO PLAY", "NO PLAY", clearColumn + " IS NULL"),
            Tuple.Create("1 FAILED", "FAILED", clearColumn + " = 1"),
            Tuple.Create("2 ASSIST", "ASSIST", clearColumn + " = 2 AND NOT (" + easyPredicate + ")"),
            Tuple.Create("3 EASY", "EASY", easyPredicate),
            Tuple.Create("4 CLEAR", "CLEAR", clearColumn + " = 3"),
            Tuple.Create("5 HARD", "HARD", clearColumn + " = 4"),
            Tuple.Create("6 FC", "FC", clearColumn + " = 5 AND (" + opHistoryValue + " & " + ClearTypeStorageConverter.OptionHistoryPerfect + ") = 0"),
            Tuple.Create("7 P.A", "P.A", clearColumn + " = 5 AND (" + opHistoryValue + " & " + ClearTypeStorageConverter.OptionHistoryPerfect + ") != 0")
        };
        IReadOnlyList<CustomFolderEntryScope> entryScopes = CreateCustomFolderEntryScopes(bmsTable);
        string orderBy = MakeScoreColumnNullLastOrder(SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.minbp), asc: true);
        foreach (Tuple<string, string, string> clearScope in clearScopes)
        {
            foreach (CustomFolderEntryScope entryScope in entryScopes)
            {
                string title = entryScope.Title + " " + clearScope.Item2;
                string predicate = CombineCustomFolderPredicates(CreatePlaylistEntryScopePredicate(bmsTable, entryScope), clearScope.Item3);
                AddCustomFolderDefinition(definitions, Path.Combine("CLEAR FOLDER", clearScope.Item1), BuildCustomFolderCommand(predicate, orderBy), bmsTable.name, title, 0, clearScope.Item2, DescribeCustomFolderScope(entryScope));
            }
            if (outputRandom)
            {
                foreach (CustomFolderEntryScope entryScope in entryScopes)
                {
                    string title = entryScope.Title + " " + clearScope.Item2;
                    string predicate = CombineCustomFolderPredicates(CreatePlaylistEntryScopePredicate(bmsTable, entryScope), clearScope.Item3);
                    AddCustomFolderDefinition(definitions, Path.Combine("CLEAR FOLDER", clearScope.Item1), BuildCustomFolderCommand(predicate, "random()"), bmsTable.name, title + " RANDOM", 1, clearScope.Item2, "Random: " + DescribeCustomFolderScope(entryScope), isRandomVariant: true);
                }
            }
        }
        return definitions;
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsDJLevelFolder(BMSTable bmsTable)
    {
        var definitions = new List<CustomFolderDefinition>();
        bool outputRandom = IsCustomFolderTypeEnabled(bmsTable, LR2SongDBExtended.playlist.CustomFolderType.RandomFolder);
        string scoreTable = SQLiteTable<LR2ScoreDB.score>.GetTableName();
        string rankColumn = scoreTable + "." + SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.rank);
        var rankScopes = new List<Tuple<string, string>>
        {
            Tuple.Create("AAA", rankColumn + " = 8"),
            Tuple.Create("AA", rankColumn + " = 7"),
            Tuple.Create("A", rankColumn + " = 6"),
            Tuple.Create("UNDER A", rankColumn + " < 6 OR " + rankColumn + " IS NULL")
        };
        string orderBy = MakeScoreColumnNullLastOrder(SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.rate), asc: false);
        IReadOnlyList<CustomFolderEntryScope> entryScopes = CreateCustomFolderEntryScopes(bmsTable);
        foreach (Tuple<string, string> rankScope in rankScopes)
        {
            foreach (CustomFolderEntryScope entryScope in entryScopes)
            {
                string title = entryScope.Title + " " + rankScope.Item1;
                string predicate = CombineCustomFolderPredicates(CreatePlaylistEntryScopePredicate(bmsTable, entryScope), rankScope.Item2);
                AddCustomFolderDefinition(definitions, Path.Combine("DJ LEVEL", rankScope.Item1), BuildCustomFolderCommand(predicate, orderBy), bmsTable.name, title, 0, rankScope.Item1, DescribeCustomFolderScope(entryScope));
            }
            if (outputRandom)
            {
                foreach (CustomFolderEntryScope entryScope in entryScopes)
                {
                    string title = entryScope.Title + " " + rankScope.Item1;
                    string predicate = CombineCustomFolderPredicates(CreatePlaylistEntryScopePredicate(bmsTable, entryScope), rankScope.Item2);
                    AddCustomFolderDefinition(definitions, Path.Combine("DJ LEVEL", rankScope.Item1), BuildCustomFolderCommand(predicate, "random()"), bmsTable.name, title + " RANDOM", 1, rankScope.Item1, "Random: " + DescribeCustomFolderScope(entryScope), isRandomVariant: true);
                }
            }
        }
        return definitions;
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsBpmSortFolder(BMSTable bmsTable)
    {
        string mainBpmExpression = MakeChartInfoMainBpmExpression();
        return makeCustomFolderDefinitionsScopedSortFolder(
            bmsTable,
            "BPM SORT",
            mainBpmExpression + " IS NULL ASC, " + mainBpmExpression + " ASC",
            "Sort: BPM ASC");
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsBpSortFolder(BMSTable bmsTable)
    {
        return makeCustomFolderDefinitionsScopedSortFolder(
            bmsTable,
            "BP SORT",
            MakeScoreColumnNullLastOrder(SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.minbp), asc: true),
            "Sort: BP ASC");
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsPlayCountSortFolder(BMSTable bmsTable)
    {
        return makeCustomFolderDefinitionsScopedSortFolder(
            bmsTable,
            "PLAY COUNT SORT",
            MakeScoreColumnNullLastOrder(SQLiteTable<LR2ScoreDB.score>.GetColumnName(e => e.playcount), asc: false),
            "Sort: PLAY COUNT DESC");
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsLastPlaySortFolder(BMSTable bmsTable)
    {
        string lastPlayAtExpression = MakeLastPlayAtExpression();
        return makeCustomFolderDefinitionsScopedSortFolder(
            bmsTable,
            "LAST PLAY SORT",
            lastPlayAtExpression + " IS NULL ASC, " + lastPlayAtExpression + " DESC",
            "Sort: LAST PLAY DESC");
    }

    private List<CustomFolderDefinition> makeCustomFolderDefinitionsScopedSortFolder(BMSTable bmsTable, string relativeDirectory, string orderBy, string infoA)
    {
        var definitions = new List<CustomFolderDefinition>();
        foreach (CustomFolderEntryScope scope in CreateCustomFolderEntryScopes(bmsTable))
        {
            string command = BuildCustomFolderCommand(CreatePlaylistEntryScopePredicate(bmsTable, scope), orderBy);
            AddCustomFolderDefinition(definitions, relativeDirectory, command, bmsTable.name, scope.Title, 0, infoA, DescribeCustomFolderScope(scope));
        }
        return definitions;
    }

    private static bool IsCustomFolderTypeEnabled(BMSTable bmsTable, LR2SongDBExtended.playlist.CustomFolderType type)
    {
        return bmsTable != null && LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(bmsTable.ignore_folder_output, type);
    }

    private static IReadOnlyList<CustomFolderEntryScope> CreateCustomFolderEntryScopes(BMSTable bmsTable)
    {
        var scopes = new List<CustomFolderEntryScope>();
        if (IsCustomFolderTypeEnabled(bmsTable, LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder))
        {
            scopes.Add(new CustomFolderEntryScope
            {
                IsAll = true,
                Title = (bmsTable?.name ?? string.Empty) + " ALL"
            });
        }
        if (!IsCustomFolderTypeEnabled(bmsTable, LR2SongDBExtended.playlist.CustomFolderType.UserFolder))
        {
            return scopes;
        }
        foreach (string folder in bmsTable?.folder_list ?? [])
        {
            scopes.Add(new CustomFolderEntryScope
            {
                FolderName = folder,
                Title = string.IsNullOrWhiteSpace(folder) ? bmsTable.name : folder
            });
        }
        return scopes;
    }

    private static string DescribeCustomFolderScope(CustomFolderEntryScope scope)
    {
        if (scope == null)
        {
            return string.Empty;
        }
        return scope.IsAll ? "ALL" : "Folder: " + (scope.Title ?? string.Empty);
    }

    private static string CreatePlaylistEntryScopePredicate(BMSTable bmsTable, CustomFolderEntryScope scope)
    {
        return CreatePlaylistEntryScopePredicate(bmsTable, scope?.IsAll == true ? null : scope?.FolderName);
    }

    private static string CreatePlaylistEntryScopePredicate(BMSTable bmsTable, string folder)
    {
        string md5Subquery = CreatePlaylistEntryMd5Subquery(bmsTable, folder, null, includeRemoved: false);
        if (bmsTable?.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder)
        {
            return "song.folder in (SELECT folder FROM song WHERE hash in (" + md5Subquery + "))";
        }
        return "song.hash in (" + md5Subquery + ")";
    }

    private static string CreatePlaylistEntryMd5Subquery(BMSTable bmsTable, string folder, string extraPredicate, bool includeRemoved)
    {
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
        string columnNameFolder = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed);
        var predicates = new List<string>
        {
            columnNamePlaylistId + " = " + bmsTable.playlist_id,
            columnNameIsRemoved + " = " + (includeRemoved ? "1" : "0")
        };
        if (folder != null)
        {
            predicates.Add(columnNameFolder + " = " + sqlQuote(folder));
        }
        if (!string.IsNullOrWhiteSpace(extraPredicate))
        {
            predicates.Add(extraPredicate);
        }
        return "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + string.Join(" AND ", predicates);
    }

    private static string CombineCustomFolderPredicates(params string[] predicates)
    {
        return string.Join(" AND ", (predicates ?? [])
            .Where(predicate => !string.IsNullOrWhiteSpace(predicate))
            .Select(predicate => "(" + predicate.Trim() + ")"));
    }

    private static string BuildCustomFolderCommand(string predicate, string orderBy)
    {
        string command = predicate ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            command += " ORDER BY " + orderBy;
        }
        return command;
    }

    private static void AddCustomFolderDefinition(
        IList<CustomFolderDefinition> definitions,
        string relativeDirectory,
        string command,
        string category,
        string title,
        int maxTracks,
        string informationA,
        string informationB,
        bool isRandomVariant = false)
    {
        if (definitions == null)
        {
            return;
        }
        string text = getCustomFolderText(command, category, title, maxTracks, informationA, informationB);
        definitions.Add(new CustomFolderDefinition
        {
            RelativeDirectory = NormalizeCustomFolderRelativeDirectory(relativeDirectory),
            Text = text,
            ParsedDefinition = Lr2FolderFileProjection.ParseDefinition(ReadLinesFromText(text)),
            IsRandomVariant = isRandomVariant
        });
    }

    private static string NormalizeCustomFolderRelativeDirectory(string relativeDirectory)
    {
        if (string.IsNullOrWhiteSpace(relativeDirectory))
        {
            return string.Empty;
        }
        return relativeDirectory
            .Trim()
            .Trim(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static string MakeScoreColumnNullLastOrder(string columnName, bool asc)
    {
        string column = SQLiteTable<LR2ScoreDB.score>.GetTableName() + "." + columnName;
        return column + " IS NULL ASC, " + column + " " + (asc ? "ASC" : "DESC");
    }

    private static string MakeChartInfoMainBpmExpression()
    {
        string tableName = SQLiteTable<LR2SongDBExtended.chart_info>.GetTableName();
        string md5Column = SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(e => e.md5);
        string mainBpmColumn = SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(e => e.mainbpm);
        string parserVersionColumn = SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(e => e.parser_version);
        string updatedAtColumn = SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(e => e.updated_at);
        string sha256Column = SQLiteTable<LR2SongDBExtended.chart_info>.GetColumnName(e => e.sha256);
        return "(SELECT " + mainBpmColumn
            + " FROM " + tableName
            + " WHERE " + tableName + "." + md5Column + " = song.hash"
            + " AND " + mainBpmColumn + " IS NOT NULL"
            + " ORDER BY " + parserVersionColumn + " DESC, " + updatedAtColumn + " DESC, " + sha256Column + " ASC"
            + " LIMIT 1)";
    }

    private static string MakeLastPlayAtExpression()
    {
        return "(SELECT last_play_at FROM " + Lr2PlayHistorySchemaService.LastPlayTableName + " WHERE hash = song.hash)";
    }

    /// <summary>
    /// LR2 カスタムフォルダ定義に埋め込む ORDER BY 句を生成します。
    /// </summary>
    /// <param name="ftype">ソート種別。</param>
    /// <param name="asc">昇順であれば <see langword="true"/>。</param>
    /// <param name="playlist_id">プレイリスト内参照が必要な場合のプレイリスト ID。</param>
    /// <param name="folder">フォルダ限定参照が必要な場合のフォルダ名。</param>
    /// <returns>ORDER BY 句に相当する文字列。不要なら空文字列。</returns>
    /// <exception cref="ArgumentNullException">必要な <paramref name="playlist_id"/> が未指定の場合。</exception>
    private string makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType ftype, bool asc, int? playlist_id = null, string folder = null)
    {
        if (ftype == LR2SongDBExtended.playlist.CustomFolderSortType.NONE)
        {
            return string.Empty;
        }
        string text = (asc ? "ASC" : "DESC");
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed);
        string columnName2 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        switch (ftype)
        {
            case LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL:
            case LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE:
                {
                    if (!playlist_id.HasValue)
                    {
                        throw new ArgumentNullException("playlist_id");
                    }
                    string columnName3 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        return "(SELECT " + ftype.ToColumnName() + " FROM " + tableName + " WHERE " + columnName2 + " = song.hash AND " + columnName3 + " = " + playlist_id + " AND " + columnName + " = 0) " + text;
                    }
                    string columnName4 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder);
                    return "(SELECT " + ftype.ToColumnName() + " FROM " + tableName + " WHERE " + columnName2 + " = song.hash AND " + columnName3 + " = " + playlist_id + " AND " + columnName4 + " = " + sqlQuote(folder) + " AND " + columnName + " = 0) " + text;
                }
            case LR2SongDBExtended.playlist.CustomFolderSortType.SCORE:
            case LR2SongDBExtended.playlist.CustomFolderSortType.MISS:
            case LR2SongDBExtended.playlist.CustomFolderSortType.PLAYCOUNT:
                return ftype.ToColumnName() + " " + text;
            case LR2SongDBExtended.playlist.CustomFolderSortType.TITLE:
            case LR2SongDBExtended.playlist.CustomFolderSortType.ARTIST:
                return "UPPER(" + ftype.ToColumnName() + ") " + text;
            default:
                return string.Empty;
        }
    }

    /// <summary>
    /// 通常プレイリストのカスタムフォルダ出力先ベースディレクトリ変更を反映します。
    /// </summary>
    /// <param name="outputDirBaseBefore">変更前のベースディレクトリ。</param>
    /// <param name="outputDirBaseAfter">変更後のベースディレクトリ。</param>
    public void ChangeCustomFolderBaseDirectory(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        string additionalOutputBaseDirsBefore = null,
        string additionalOutputBaseDirsAfter = null)
    {
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        ChangeCustomFolderBaseDirectoryWithSettings(
            outputDirBaseBefore,
            outputDirBaseAfter,
            additionalOutputBaseDirsBefore,
            additionalOutputBaseDirsAfter,
            settings);
    }

    internal void ChangeCustomFolderBaseDirectoryWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        string additionalOutputBaseDirsBefore,
        string additionalOutputBaseDirsAfter,
        CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetCustomFolderOutputSettings();
        additionalOutputBaseDirsBefore ??= settings.LR2CustomFolderAdditionalOutputBaseDirs;
        additionalOutputBaseDirsAfter ??= settings.LR2CustomFolderAdditionalOutputBaseDirs;
        var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        using (rwlockBMSTables.GetReaderGuard())
        {
            foreach (BMSTable item in BMSTables.Where(t =>
                !t.is_root_folder
                && !string.IsNullOrWhiteSpace(t.Output_dir)))
            {
                bool oldBaseResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                    item.custom_folder_output_base_name,
                    outputDirBaseBefore,
                    additionalOutputBaseDirsBefore,
                    out string oldBaseDirectory);
                bool newBaseResolved = CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
                    item.custom_folder_output_base_name,
                    outputDirBaseAfter,
                    additionalOutputBaseDirsAfter,
                    out string newBaseDirectory);
                if (!oldBaseResolved
                    || !newBaseResolved
                    || !IsSameCustomFolderDirectory(oldBaseDirectory, outputDirBaseBefore)
                    || !IsSameCustomFolderDirectory(newBaseDirectory, outputDirBaseAfter)
                    || IsSameCustomFolderDirectory(oldBaseDirectory, newBaseDirectory))
                {
                    continue;
                }
                outputDirPathBeforeByTable[item] = Path.Combine(oldBaseDirectory, item.Output_dir);
                outputBaseDirPathBeforeByTable[item] = oldBaseDirectory;
            }
        }
        MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
            outputDirPathBeforeByTable.Keys,
            outputDirPathBeforeByTable,
            "setting_custom_folder_output_base_dir_changed",
            wasRootFolderBeforeByTable: outputDirPathBeforeByTable.Keys.ToDictionary(table => table, _ => false),
            outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
            settings: settings);
    }

    /// <summary>
    /// ルートプレイリストのカスタムフォルダ出力先ベースディレクトリ変更を反映します。
    /// </summary>
    /// <param name="outputDirBaseBefore">変更前のベースディレクトリ。</param>
    /// <param name="outputDirBaseAfter">変更後のベースディレクトリ。</param>
    public void ChangeCustomFolderBaseDirectoryRoot(
        string outputDirBaseBefore,
        string outputDirBaseAfter)
    {
        ChangeCustomFolderBaseDirectoryRootWithSettings(
            outputDirBaseBefore,
            outputDirBaseAfter,
            settings: null);
    }

    internal void ChangeCustomFolderBaseDirectoryRootWithSettings(
        string outputDirBaseBefore,
        string outputDirBaseAfter,
        CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetCustomFolderOutputSettings();
        var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        using (rwlockBMSTables.GetReaderGuard())
        {
            foreach (BMSTable item in BMSTables.Where(t => t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)))
            {
                outputDirPathBeforeByTable[item] = Path.Combine(outputDirBaseBefore, item.Output_dir);
                outputBaseDirPathBeforeByTable[item] = outputDirBaseBefore;
            }
        }
        MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
            outputDirPathBeforeByTable.Keys,
            outputDirPathBeforeByTable,
            "setting_custom_folder_root_output_base_dir_changed",
            wasRootFolderBeforeByTable: outputDirPathBeforeByTable.Keys.ToDictionary(table => table, _ => true),
            rootOutputBaseDirBefore: outputDirBaseBefore,
            outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
            settings: settings);
    }

    /// <summary>
    /// 追加カスタムフォルダ出力先の登録変更をプレイリスト本体へ反映し、必要な移行を実行します。
    /// </summary>
    /// <param name="previousAdditionalOutputBaseDirectories">変更前の追加出力先定義。</param>
    /// <param name="pendingRenames">編集中に確定した登録名の変更。</param>
    /// <param name="settings">変更後のカスタムフォルダ出力設定。</param>
    /// <returns>変更したプレイリスト数。</returns>
    internal int ApplyCustomFolderAdditionalOutputBaseRegistrationChangesWithSettings(
        string previousAdditionalOutputBaseDirectories,
        IReadOnlyDictionary<string, string> pendingRenames,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null || BMSTables == null)
        {
            return 0;
        }

        IReadOnlyList<CustomFolderOutputBaseEntry> oldEntries = CustomFolderOutputBaseRegistry.CreateAdditionalEntries(
            CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(previousAdditionalOutputBaseDirectories));
        IReadOnlyList<CustomFolderOutputBaseEntry> newEntries = CustomFolderOutputBaseRegistry.CreateAdditionalEntries(
            CustomFolderOutputBaseRegistry.DeserializeBaseDirectoriesStrict(settings.LR2CustomFolderAdditionalOutputBaseDirs));
        Dictionary<string, CustomFolderOutputBaseEntry> newByName = newEntries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, CustomFolderOutputBaseEntry> oldByName = oldEntries
            .GroupBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var processedTables = new HashSet<BMSTable>();
        var changedTables = new List<BMSTable>();
        var outputDirPathBeforeByTable = new Dictionary<BMSTable, string>();
        var outputBaseDirPathBeforeByTable = new Dictionary<BMSTable, string>();

        using (rwlockBMSTables.GetWriterGuard())
        {
            foreach (CustomFolderOutputBaseEntry oldEntry in oldEntries)
            {
                string newName = null;
                if (pendingRenames != null
                    && pendingRenames.TryGetValue(oldEntry.Name, out string renamedName)
                    && newByName.ContainsKey(renamedName))
                {
                    newName = renamedName;
                }
                else if (newByName.ContainsKey(oldEntry.Name))
                {
                    newName = oldEntry.Name;
                }

                string newBasePath = newName == null
                    ? settings.LR2CustomFolderOutputBaseDir
                    : newByName[newName].Path;
                if (newName != null
                    && string.Equals(oldEntry.Name, newName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(
                        CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(oldEntry.Path),
                        CustomFolderOutputBaseRegistry.NormalizeDirectoryPath(newBasePath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                ApplyCustomFolderOutputBaseNameChange(
                    oldEntry.Name,
                    oldEntry.Path,
                    newName,
                    processedTables,
                    changedTables,
                    outputDirPathBeforeByTable,
                    outputBaseDirPathBeforeByTable,
                    settings);
            }

            foreach (CustomFolderOutputBaseEntry newEntry in newEntries)
            {
                if (oldByName.ContainsKey(newEntry.Name))
                {
                    continue;
                }

                ApplyCustomFolderOutputBaseNameChange(
                    newEntry.Name,
                    null,
                    newEntry.Name,
                    processedTables,
                    changedTables,
                    outputDirPathBeforeByTable,
                    outputBaseDirPathBeforeByTable,
                    settings);
            }
        }

        if (changedTables.Count > 0)
        {
            MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
                changedTables,
                outputDirPathBeforeByTable,
                "setting_custom_folder_output_base_registration_changed",
                outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable,
                settings: settings);
        }
        return changedTables.Count;
    }

    private void ApplyCustomFolderOutputBaseNameChange(
        string oldBaseName,
        string oldBasePath,
        string newBaseName,
        ISet<BMSTable> processedTables,
        ICollection<BMSTable> changedTables,
        IDictionary<BMSTable, string> outputDirPathBeforeByTable,
        IDictionary<BMSTable, string> outputBaseDirPathBeforeByTable,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (string.IsNullOrWhiteSpace(oldBaseName))
        {
            return;
        }

        foreach (BMSTable table in BMSTables.Where(table =>
            table != null
            && !processedTables.Contains(table)
            && string.Equals(table.custom_folder_output_base_name, oldBaseName, StringComparison.OrdinalIgnoreCase)))
        {
            processedTables.Add(table);
            string outputDirName = table.Output_dir;
            string beforeDirectory = !string.IsNullOrWhiteSpace(oldBasePath)
                && !string.IsNullOrWhiteSpace(outputDirName)
                ? Path.Combine(oldBasePath, outputDirName)
                : null;
            if (settings.OperationModeLR2DB
                && !table.is_root_folder
                && !string.IsNullOrWhiteSpace(beforeDirectory))
            {
                outputDirPathBeforeByTable.Add(table, beforeDirectory);
                outputBaseDirPathBeforeByTable.Add(table, oldBasePath);
            }
            table.custom_folder_output_base_name = CustomFolderOutputBaseRegistry.NormalizeBaseName(newBaseName);
            changedTables.Add(table);
        }
    }

    /// <summary>
    /// プレイリストのカスタムフォルダ出力先変更をファイルシステムへ反映します。
    /// </summary>
    /// <param name="bmsTable">移行対象のプレイリスト。</param>
    /// <param name="outputDirPathBefore">変更前の出力先パス。</param>
    /// <param name="outputDirPathAfter">変更後の出力先パス。省略時は現設定から算出します。</param>
    /// <exception cref="InvalidOperationException">LR2DB モードでない場合。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentException">出力先に必要な情報が不足している場合。</exception>
    public void MigrateCustomFolderOutputDirectory(
        BMSTable bmsTable,
        string outputDirPathBefore,
        string outputDirPathAfter = null,
        bool? wasRootFolderBefore = null,
        string rootOutputBaseDirBefore = null,
        string outputBaseDirBefore = null,
        bool inferOutputBaseDirBeforeWhenMissing = true)
    {
        MigrateCustomFolderOutputDirectoryWithSettings(
            bmsTable,
            outputDirPathBefore,
            outputDirPathAfter,
            wasRootFolderBefore,
            rootOutputBaseDirBefore,
            outputBaseDirBefore,
            inferOutputBaseDirBeforeWhenMissing,
            settings: null);
    }

    internal void MigrateCustomFolderOutputDirectoryWithSettings(
        BMSTable bmsTable,
        string outputDirPathBefore,
        string outputDirPathAfter,
        bool? wasRootFolderBefore,
        string rootOutputBaseDirBefore,
        string outputBaseDirBefore,
        bool inferOutputBaseDirBeforeWhenMissing,
        CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetCustomFolderOutputSettings();
        if (!settings.OperationModeLR2DB)
        {
            throw new InvalidOperationException("Properties.Settings.Default.OperationModeLR2DB is not true");
        }
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
        {
            throw new ArgumentException("bmsTable.Output_dir");
        }
        if (string.IsNullOrWhiteSpace(outputDirPathAfter))
        {
            outputDirPathAfter = ResolveCustomFolderOutputDirectory(bmsTable, settings);
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "MigrateCustomFolderOutputDirectory");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                customFolderOutputMaintenanceOwner.TryMigrateCustomFolderOutputDirectory(
                    bmsTable,
                    outputDirPathBefore,
                    outputDirPathAfter,
                    wasRootFolderBefore ?? bmsTable.is_root_folder,
                    rootOutputBaseDirBefore,
                    outputBaseDirBefore,
                    inferOutputBaseDirBeforeWhenMissing,
                    settings);
            }
        }
    }

    /// <summary>
    /// 指定プレイリストのカスタムフォルダ出力を再生成します。
    /// </summary>
    /// <param name="bmsTable">再出力対象のプレイリスト。</param>
    /// <exception cref="InvalidOperationException">LR2DB モードでない場合。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentException">出力先に必要な情報が不足している場合。</exception>
    public void ReOutputCustomFolder(BMSTable bmsTable)
    {
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        if (!settings.OperationModeLR2DB)
        {
            throw new InvalidOperationException("Properties.Settings.Default.OperationModeLR2DB is not true");
        }
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
        {
            throw new ArgumentException("bmsTable.Output_dir");
        }
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                customFolderOutputMaintenanceOwner.TryReOutputCustomFolder(bmsTable, settings);
            }
        }
    }

    internal Lr2SongDbSyncPreparedDataSurface ReOutputAllCustomFoldersForLr2SongDbSync(string reason)
    {
        return ReOutputAllCustomFoldersForLr2SongDbSync(reason, null);
    }

    internal Lr2SongDbSyncPreparedDataSurface ReOutputAllCustomFoldersForLr2SongDbSync(string reason, Action<int, int, string> progressCallback)
    {
        return ReOutputAllCustomFoldersForLr2SongDbSyncCoreAsync(reason, yieldBetweenTables: false, progressCallback)
            .GetAwaiter()
            .GetResult();
    }

    internal Task<Lr2SongDbSyncPreparedDataSurface> ReOutputAllCustomFoldersForLr2SongDbSyncAsync(string reason, Action<int, int, string> progressCallback = null)
    {
        return ReOutputAllCustomFoldersForLr2SongDbSyncCoreAsync(reason, yieldBetweenTables: true, progressCallback);
    }

    private async Task<Lr2SongDbSyncPreparedDataSurface> ReOutputAllCustomFoldersForLr2SongDbSyncCoreAsync(string reason, bool yieldBetweenTables, Action<int, int, string> progressCallback = null)
    {
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        if (!settings.OperationModeLR2DB)
        {
            return Lr2SongDbSyncPreparedDataSurface.Empty;
        }
        List<BMSTable> tablesSnapshot;
        using (rwlockBMSTables.GetReaderGuard())
        {
            tablesSnapshot = BMSTables == null
                ? []
                : [.. BMSTables.Where(table => table != null && !string.IsNullOrWhiteSpace(table.Output_dir))];
        }
        CustomFolderBatchOutputResult result = await customFolderOutputMaintenanceOwner.ReOutputTablesAsync(
            tablesSnapshot,
            reason,
            "playlist_lr2_song_db_sync_data_resync",
            forceWriteAllFiles: false,
            throwOnProjectionFailure: false,
            buildPreparedDataSurface: true,
            yieldBetweenTables,
            progressCallback,
            settings: settings);
        if (result.HasUnverifiedFiles)
        {
            throw new InvalidOperationException(
                "Custom-folder output could not verify one or more existing files before LR2 synchronization preparation.");
        }
        return result.PreparedDataSurface ?? Lr2SongDbSyncPreparedDataSurface.Empty;
    }

    private int RepairMissingCustomFolderOutputsAfterHydration(string reason, bool verifyRootOutputDirectoryRows = false)
    {
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        return RepairMissingCustomFolderOutputsAfterHydrationCore(reason, verifyRootOutputDirectoryRows, settings);
    }

    private int RepairMissingCustomFolderOutputsAfterHydrationCore(
        string reason,
        bool verifyRootOutputDirectoryRows,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null)
        {
            throw new InvalidOperationException("Custom-folder output settings snapshot was not provided.");
        }
        if (!settings.OperationModeLR2DB)
        {
            return 0;
        }

        var stopwatch = Stopwatch.StartNew();
        var targetStopwatch = Stopwatch.StartNew();
        List<BMSTable> tableSnapshot;
        using (rwlockBMSTables.GetReaderGuard())
        {
            tableSnapshot = BMSTables == null
                ? []
                : [.. BMSTables.Where(table => table != null && !string.IsNullOrWhiteSpace(table.Output_dir))];
        }
        LogPlaylistPerformance("playlist_custom_folder_output_repair start"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableSnapshot.Count
            + " verifyRootOutputDirectoryRows=" + verifyRootOutputDirectoryRows.ToString().ToLowerInvariant());
        List<CustomFolderOutputProjection> targets = CreateCustomFolderOutputRepairPlans(tableSnapshot, reason, verifyRootOutputDirectoryRows, settings);
        targetStopwatch.Stop();
        if (targets.Count == 0)
        {
            stopwatch.Stop();
            LogPlaylistPerformance("playlist_custom_folder_output_repair skipped"
                + " reason=" + (reason ?? "unknown")
                + " tableCount=" + tableSnapshot.Count
                + " targetCount=0"
                + " verifyRootOutputDirectoryRows=" + verifyRootOutputDirectoryRows.ToString().ToLowerInvariant()
                + " targetMs=" + targetStopwatch.ElapsedMilliseconds
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return 0;
        }

        CustomFolderBatchOutputResult result = customFolderOutputMaintenanceOwner.ReOutputProjectionsAsync(
            targets,
            targets.Count,
            reason,
            "playlist_custom_folder_output_repair",
            buildPreparedDataSurface: false,
            yieldBetweenTables: false,
            settings: settings)
            .GetAwaiter()
            .GetResult();
        stopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair summary"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableSnapshot.Count
            + " targetCount=" + targets.Count
            + " reOutputCount=" + result.ReOutputCount
            + " verifyRootOutputDirectoryRows=" + verifyRootOutputDirectoryRows.ToString().ToLowerInvariant()
            + " targetMs=" + targetStopwatch.ElapsedMilliseconds
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        if (result.HasUnverifiedFiles)
        {
            throw new InvalidOperationException(
                "Custom-folder output could not verify one or more existing files during hydration repair.");
        }
        return result.ReOutputCount;
    }

    private List<CustomFolderOutputProjection> CreateCustomFolderOutputRepairPlans(
        IReadOnlyList<BMSTable> tablesSnapshot,
        string reason,
        bool verifyRootOutputDirectoryRows,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetCustomFolderOutputSettings();
        var statusStopwatch = Stopwatch.StartNew();
        Dictionary<int, CustomFolderOutputStatusRow> statusRows = customFolderOutputStatusOwner.ReadStatusRows();
        statusStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair status_read_done"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + (tablesSnapshot?.Count ?? 0)
            + " statusRowCount=" + statusRows.Count
            + " elapsedMs=" + statusStopwatch.ElapsedMilliseconds);

        var candidateStopwatch = Stopwatch.StartNew();
        var candidates = new List<CustomFolderOutputRepairCandidate>();
        foreach (BMSTable table in tablesSnapshot ?? [])
        {
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
            {
                continue;
            }

            try
            {
                string outputDirectory = ResolveCustomFolderOutputDirectory(table, settings);
                if (string.IsNullOrWhiteSpace(outputDirectory))
                {
                    continue;
                }

                candidates.Add(new CustomFolderOutputRepairCandidate
                {
                    Table = table,
                    OutputDirectory = outputDirectory
                });
            }
            catch (Exception ex) when (ex is ArgumentNullException || ex is ArgumentException || ex is InvalidOperationException || ex is NotSupportedException || ex is PathTooLongException)
            {
                continue;
            }
        }
        candidateStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair candidate_done"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + (tablesSnapshot?.Count ?? 0)
            + " candidateCount=" + candidates.Count
            + " elapsedMs=" + candidateStopwatch.ElapsedMilliseconds);

        var filterStopwatch = Stopwatch.StartNew();
        var pendingCandidates = new List<CustomFolderOutputRepairCandidate>();
        var physicalCheckCandidates = new List<CustomFolderOutputRepairCandidate>();
        var physicalCheckStatuses = new Dictionary<int, CustomFolderOutputStatusRow>();
        int configCurrentCount = 0;
        foreach (CustomFolderOutputRepairCandidate candidate in candidates)
        {
            int? playlistId = candidate?.Table?.playlist_id;
            if (playlistId.HasValue
                && statusRows.TryGetValue(playlistId.Value, out CustomFolderOutputStatusRow status)
                && customFolderOutputStatusOwner.IsConfigCurrent(candidate.Table, candidate.OutputDirectory, status, settings))
            {
                configCurrentCount++;
                physicalCheckCandidates.Add(candidate);
                physicalCheckStatuses[playlistId.Value] = status;
                continue;
            }

            pendingCandidates.Add(candidate);
        }
        filterStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair status_config_filter_done"
            + " reason=" + (reason ?? "unknown")
            + " candidateCount=" + candidates.Count
            + " statusRowCount=" + statusRows.Count
            + " configCurrentCount=" + configCurrentCount
            + " physicalCheckCount=" + physicalCheckCandidates.Count
            + " pendingCount=" + pendingCandidates.Count
            + " elapsedMs=" + filterStopwatch.ElapsedMilliseconds);

        CustomFolderOutputPhysicalSurface physicalSurface = new(
            new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);
        if (pendingCandidates.Count > 0 || physicalCheckCandidates.Count > 0)
        {
            IEnumerable<string> physicalSurfaceDirectories = pendingCandidates.Count > 0
                ? candidates.Select(candidate => candidate.OutputDirectory)
                : physicalCheckCandidates.Select(candidate => candidate.OutputDirectory);
            physicalSurface = ResolveCustomFolderOutputPhysicalSurface(
                physicalSurfaceDirectories,
                reason);
        }

        CustomFolderOutputPhysicalMtimeSignatureIndex physicalSignatureIndex = CustomFolderOutputPhysicalMtimeSignatureIndex.Incomplete;
        int currentStatusCount = 0;
        if (physicalCheckCandidates.Count > 0)
        {
            var signatureStopwatch = Stopwatch.StartNew();
            physicalSignatureIndex =
                customFolderOutputOwner.CreatePhysicalMtimeSignatureIndex(
                    physicalCheckCandidates.Select(candidate => candidate.OutputDirectory),
                    physicalSurface,
                    candidates.Select(candidate => candidate.OutputDirectory));
            signatureStopwatch.Stop();
            LogPlaylistPerformance("playlist_custom_folder_output_repair physical_signature_done"
                + " reason=" + (reason ?? "unknown")
                + " candidateCount=" + physicalCheckCandidates.Count
                + " signatureCount=" + physicalSignatureIndex.SignatureCount
                + " physicalEntryCount=" + (physicalSurface?.FileEntries.Count ?? 0)
                + " discoveryComplete=" + physicalSignatureIndex.DiscoveryComplete.ToString().ToLowerInvariant()
                + " elapsedMs=" + signatureStopwatch.ElapsedMilliseconds);
        }
        else
        {
            LogPlaylistPerformance("playlist_custom_folder_output_repair physical_signature_skipped"
                + " reason=" + (reason ?? "unknown")
                + " candidateCount=0");
        }

        filterStopwatch.Restart();
        var rootDirectoryRowCheckCandidates = new List<CustomFolderOutputRepairCandidate>();
        foreach (CustomFolderOutputRepairCandidate candidate in physicalCheckCandidates)
        {
            int? playlistId = candidate?.Table?.playlist_id;
            if (playlistId.HasValue
                && physicalCheckStatuses.TryGetValue(playlistId.Value, out CustomFolderOutputStatusRow status)
                && PlaylistCustomFolderOutputStatusOwner.IsPhysicalCurrent(candidate.OutputDirectory, status, physicalSignatureIndex))
            {
                currentStatusCount++;
                if (verifyRootOutputDirectoryRows && candidate?.Table?.is_root_folder == true)
                {
                    rootDirectoryRowCheckCandidates.Add(candidate);
                }
                continue;
            }

            pendingCandidates.Add(candidate);
        }
        int rootDirectoryRowRepairCount = 0;
        if (rootDirectoryRowCheckCandidates.Count > 0)
        {
            IReadOnlyCollection<CustomFolderOutputRepairCandidate> rootDirectoryRowRepairCandidates =
                FindRootOutputDirectoryRowRepairCandidates(rootDirectoryRowCheckCandidates, reason, settings);
            rootDirectoryRowRepairCount = rootDirectoryRowRepairCandidates.Count;
            foreach (CustomFolderOutputRepairCandidate candidate in rootDirectoryRowRepairCandidates)
            {
                pendingCandidates.Add(candidate);
            }
        }
        filterStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair status_filter_done"
            + " reason=" + (reason ?? "unknown")
            + " candidateCount=" + candidates.Count
            + " statusRowCount=" + statusRows.Count
            + " currentStatusCount=" + currentStatusCount
            + " rootDirectoryRowCheckCount=" + rootDirectoryRowCheckCandidates.Count
            + " rootDirectoryRowRepairCount=" + rootDirectoryRowRepairCount
            + " pendingCount=" + pendingCandidates.Count
            + " elapsedMs=" + filterStopwatch.ElapsedMilliseconds);
        if (pendingCandidates.Count == 0)
        {
            return [];
        }

        var projectionStopwatch = Stopwatch.StartNew();
        var pendingProjections = new List<CustomFolderOutputProjection>();
        foreach (CustomFolderOutputRepairCandidate candidate in pendingCandidates)
        {
            try
            {
                EnsurePlaylistEntriesLoaded(candidate.Table, "CustomFolderOutputRepairPlan");
                using (candidate.Table.ReaderWriterLock.GetReaderGuard())
                {
                    pendingProjections.Add(customFolderOutputOwner.CreateLayoutProjection(candidate.Table, candidate.OutputDirectory, settings));
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
            {
                continue;
            }
        }
        projectionStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair projection_done"
            + " reason=" + (reason ?? "unknown")
            + " pendingCandidateCount=" + pendingCandidates.Count
            + " projectionCount=" + pendingProjections.Count
            + " skippedBeforeProjectionCount=" + (candidates.Count - pendingCandidates.Count)
            + " elapsedMs=" + projectionStopwatch.ElapsedMilliseconds);
        if (pendingProjections.Count == 0)
        {
            return [];
        }

        var rowLookupStopwatch = Stopwatch.StartNew();
        AssignCustomFolderProtectedOutputDirectories(pendingProjections, settings);
        IReadOnlyDictionary<string, LR2SongDB.folder> rowsByPath = ReadCustomFolderOutputRows(pendingProjections, out bool rowLookupSucceeded, layoutOnly: true);
        rowLookupStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair row_lookup_done"
            + " reason=" + (reason ?? "unknown")
            + " projectionCount=" + pendingProjections.Count
            + " rowCount=" + (rowsByPath?.Count ?? 0)
            + " succeeded=" + rowLookupSucceeded
            + " elapsedMs=" + rowLookupStopwatch.ElapsedMilliseconds);

        var staleTargetStopwatch = Stopwatch.StartNew();
        var staleRowRepairTargets = new HashSet<CustomFolderOutputProjection>();
        if (rowLookupSucceeded)
        {
            MarkCustomFolderOutputStaleRowRepairTargets(pendingProjections, rowsByPath?.Values, staleRowRepairTargets);
        }
        staleTargetStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair stale_target_done"
            + " reason=" + (reason ?? "unknown")
            + " projectionCount=" + pendingProjections.Count
            + " staleTargetCount=" + staleRowRepairTargets.Count
            + " elapsedMs=" + staleTargetStopwatch.ElapsedMilliseconds);

        var targetSelectionStopwatch = Stopwatch.StartNew();
        var plans = new List<CustomFolderOutputProjection>();
        var verifiedCurrentProjections = new List<CustomFolderOutputProjection>();
        foreach (CustomFolderOutputProjection projection in pendingProjections)
        {
            bool needsRepair = staleRowRepairTargets.Contains(projection)
                || MarkCustomFolderOutputLayoutRepairTargets(projection, rowsByPath, rowLookupSucceeded, physicalSurface);
            if (!needsRepair)
            {
                if (rowLookupSucceeded && physicalSurface?.DiscoveryComplete == true)
                {
                    verifiedCurrentProjections.Add(projection);
                }
                continue;
            }

            CustomFolderOutputProjection fullProjection;
            try
            {
                using (projection.Table.ReaderWriterLock.GetReaderGuard())
                {
                    fullProjection = customFolderOutputOwner.CreateProjection(projection.Table, includeText: true, settings: settings);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
            {
                continue;
            }

            foreach (string forceWritePath in projection.ForceWriteFilePaths)
            {
                fullProjection.ForceWriteFilePaths.Add(forceWritePath);
            }
            fullProjection.ProtectedOutputDirectories = projection.ProtectedOutputDirectories;
            fullProjection.PhysicalSurface = physicalSurface;
            plans.Add(fullProjection);
        }
        if (verifiedCurrentProjections.Count > 0)
        {
            customFolderOutputStatusOwner.PersistStatuses(
                verifiedCurrentProjections,
                physicalSurface,
                candidates.Select(candidate => candidate.OutputDirectory));
        }
        targetSelectionStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair target_selection_done"
            + " reason=" + (reason ?? "unknown")
            + " projectionCount=" + pendingProjections.Count
            + " targetCount=" + plans.Count
            + " verifiedCurrentCount=" + verifiedCurrentProjections.Count
            + " elapsedMs=" + targetSelectionStopwatch.ElapsedMilliseconds);
        return plans;
    }

    private IReadOnlyCollection<CustomFolderOutputRepairCandidate> FindRootOutputDirectoryRowRepairCandidates(
        IReadOnlyCollection<CustomFolderOutputRepairCandidate> candidates,
        string reason,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetCustomFolderOutputSettings();
        List<CustomFolderOutputRepairCandidate> checkCandidates = [.. (candidates ?? [])
            .Where(candidate => candidate?.Table?.is_root_folder == true
                && !string.IsNullOrWhiteSpace(candidate.OutputDirectory))];
        if (checkCandidates.Count == 0)
        {
            return [];
        }

        var expectedPathByCandidate = new Dictionary<CustomFolderOutputRepairCandidate, string>();
        var expectedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderOutputRepairCandidate candidate in checkCandidates)
        {
            string rowPath = Lr2FolderPath.ToFolderPath(candidate.OutputDirectory);
            rowPath = ResolveCustomFolderDatabasePath(candidate.Table, rowPath ?? candidate.OutputDirectory, settings);
            string normalizedRowPath = NormalizeCustomFolderRowPath(rowPath);
            if (string.IsNullOrWhiteSpace(normalizedRowPath))
            {
                continue;
            }

            expectedPathByCandidate[candidate] = normalizedRowPath;
            expectedPaths.Add(normalizedRowPath);
        }
        if (expectedPaths.Count == 0)
        {
            return [];
        }

        var lookupStopwatch = Stopwatch.StartNew();
        IReadOnlyDictionary<string, LR2SongDB.folder> rowsByPath;
        try
        {
            rowsByPath = playlistPersistenceRepository
                .QueryCustomFolderLayoutRowsByExactPath(expectedPaths)
                .Where(row => !string.IsNullOrWhiteSpace(row?.path))
                .GroupBy(row => NormalizeCustomFolderRowPath(row.path), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
        {
            lookupStopwatch.Stop();
            LogPlaylistPerformance("playlist_custom_folder_output_repair root_directory_row_lookup_failed"
                + " reason=" + (reason ?? "unknown")
                + " candidateCount=" + checkCandidates.Count
                + " expectedPathCount=" + expectedPaths.Count
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message)
                + " elapsedMs=" + lookupStopwatch.ElapsedMilliseconds);
            return checkCandidates;
        }

        var repairCandidates = new List<CustomFolderOutputRepairCandidate>();
        foreach (CustomFolderOutputRepairCandidate candidate in checkCandidates)
        {
            if (!expectedPathByCandidate.TryGetValue(candidate, out string expectedPath)
                || rowsByPath.TryGetValue(expectedPath, out LR2SongDB.folder row) != true
                || !IsRootOutputDirectoryRowCurrent(row))
            {
                repairCandidates.Add(candidate);
            }
        }
        lookupStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair root_directory_row_lookup_done"
            + " reason=" + (reason ?? "unknown")
            + " candidateCount=" + checkCandidates.Count
            + " expectedPathCount=" + expectedPaths.Count
            + " rowCount=" + rowsByPath.Count
            + " repairCount=" + repairCandidates.Count
            + " elapsedMs=" + lookupStopwatch.ElapsedMilliseconds);
        return repairCandidates;
    }

    private static bool IsRootOutputDirectoryRowCurrent(LR2SongDB.folder row)
    {
        return row != null
            && row.type == 1
            && string.Equals(row.parent, Lr2SongFolderParentNormalizer.RootParentHash, StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyDictionary<string, LR2SongDB.folder> ReadCustomFolderOutputRows(
        IEnumerable<CustomFolderOutputProjection> projections,
        out bool succeeded,
        bool layoutOnly = false)
    {
        succeeded = true;
        if (string.IsNullOrWhiteSpace(lr2SongDBPath))
        {
            return new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        }

        List<string> exactPaths = [.. (projections ?? [])
            .SelectMany(CreateCustomFolderExpectedRowLookupPaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> scopePaths = [.. (projections ?? [])
            .SelectMany(CreateCustomFolderOutputRowScopePaths)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (exactPaths.Count == 0 && scopePaths.Count == 0)
        {
            return new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            return playlistPersistenceRepository
                .QueryCustomFolderOutputRows(exactPaths, scopePaths, layoutOnly)
                .Where(row => !string.IsNullOrWhiteSpace(row?.path))
                .GroupBy(row => NormalizeCustomFolderRowPath(row.path), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key))
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
        {
            succeeded = false;
            return new Dictionary<string, LR2SongDB.folder>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IReadOnlyCollection<string> CreateCustomFolderExpectedRowLookupPaths(CustomFolderOutputProjection projection)
    {
        if (projection == null)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderOutputFileProjection file in projection.Files ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file?.DatabasePath))
            {
                result.Add(file.DatabasePath);
            }
            if (!string.IsNullOrWhiteSpace(file?.FilePath))
            {
                result.Add(file.FilePath);
            }
        }
        foreach (string rowPath in CreateCustomFolderExpectedDirectoryRowLookupPaths(projection))
        {
            result.Add(rowPath);
        }
        return [.. result];
    }

    private static IReadOnlyCollection<string> CreateCustomFolderExpectedDirectoryRowLookupPaths(CustomFolderOutputProjection projection)
    {
        if (projection == null || string.IsNullOrWhiteSpace(projection.OutputDirectory))
        {
            return [];
        }

        IReadOnlyCollection<string> directoryTargets =
            CreateCustomFolderExpectedDirectoryMetadataTargets(projection);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string directory in directoryTargets)
        {
            string rowPath = Lr2FolderPath.ToFolderPath(directory);
            if (!string.IsNullOrWhiteSpace(rowPath))
            {
                result.Add(rowPath);
                string databasePath = ResolveCustomFolderDatabasePath(projection.Table, rowPath, projection.Settings);
                if (!string.IsNullOrWhiteSpace(databasePath))
                {
                    result.Add(databasePath);
                }
            }
        }
        return [.. result];
    }

    private static IReadOnlyCollection<string> CreateCustomFolderOutputRowScopePaths(CustomFolderOutputProjection projection)
    {
        return projection?.OutputRowScopePaths ?? [];
    }

    private static IReadOnlyCollection<string> CreateCustomFolderOutputRowScopePaths(
        BMSTable table,
        string outputDirectory,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string physicalPath = Lr2FolderPath.ToFolderPath(outputDirectory);
        if (!string.IsNullOrWhiteSpace(physicalPath))
        {
            result.Add(physicalPath);
        }
        string databasePath = ResolveCustomFolderDatabasePath(table, physicalPath ?? outputDirectory, settings);
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            result.Add(databasePath);
        }
        return [.. result];
    }

    private static IReadOnlyCollection<string> CreateCustomFolderProtectedOutputRowScopePaths(CustomFolderOutputProjection projection)
    {
        if (projection == null || projection.ProtectedOutputDirectories == null)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string protectedDirectory in projection.ProtectedOutputDirectories)
        {
            foreach (string scopePath in CreateCustomFolderOutputRowScopePaths(projection.Table, protectedDirectory, projection.Settings))
            {
                if (!string.IsNullOrWhiteSpace(scopePath))
                {
                    result.Add(scopePath);
                }
            }
        }
        return [.. result];
    }

    private static void MarkCustomFolderOutputStaleRowRepairTargets(
        IReadOnlyList<CustomFolderOutputProjection> projections,
        IEnumerable<LR2SongDB.folder> rows,
        ISet<CustomFolderOutputProjection> repairTargets)
    {
        if (projections == null || projections.Count == 0 || rows == null || repairTargets == null)
        {
            return;
        }

        var expectedRowPaths = new HashSet<string>(
            projections
                .SelectMany(CreateCustomFolderExpectedRowLookupPaths)
                .Select(NormalizeCustomFolderRowPath)
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<CustomFolderOutputProjection>> scopeLookup =
            CreateCustomFolderProjectionScopeLookup(projections);
        foreach (LR2SongDB.folder row in rows)
        {
            string normalizedRowPath = NormalizeCustomFolderRowPath(row?.path);
            if (string.IsNullOrWhiteSpace(normalizedRowPath) || expectedRowPaths.Contains(normalizedRowPath))
            {
                continue;
            }

            foreach (CustomFolderOutputProjection projection in FindCustomFolderProjectionsForRowPath(normalizedRowPath, scopeLookup))
            {
                if (IsCustomFolderRowPathInProtectedOutputScope(normalizedRowPath, projection))
                {
                    continue;
                }

                repairTargets.Add(projection);
                break;
            }
        }
    }

    private static Dictionary<string, List<CustomFolderOutputProjection>> CreateCustomFolderProjectionScopeLookup(
        IEnumerable<CustomFolderOutputProjection> projections)
    {
        var lookup = new Dictionary<string, List<CustomFolderOutputProjection>>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderOutputProjection projection in projections ?? [])
        {
            foreach (string scopePath in CreateCustomFolderOutputRowScopePaths(projection))
            {
                AddCustomFolderProjectionScopeLookupKey(lookup, scopePath, projection);
            }
        }
        return lookup;
    }

    private static void AddCustomFolderProjectionScopeLookupKey(
        IDictionary<string, List<CustomFolderOutputProjection>> lookup,
        string scopePath,
        CustomFolderOutputProjection projection)
    {
        if (lookup == null || projection == null)
        {
            return;
        }

        string normalizedScope = NormalizeCustomFolderRowPath(scopePath);
        if (string.IsNullOrWhiteSpace(normalizedScope))
        {
            return;
        }

        AddCustomFolderProjectionScopeLookupKeyCore(lookup, normalizedScope, projection);
        string trimmedScope = TrimCustomFolderRowDirectorySeparators(normalizedScope);
        if (!string.Equals(trimmedScope, normalizedScope, StringComparison.OrdinalIgnoreCase))
        {
            AddCustomFolderProjectionScopeLookupKeyCore(lookup, trimmedScope, projection);
        }
    }

    private static void AddCustomFolderProjectionScopeLookupKeyCore(
        IDictionary<string, List<CustomFolderOutputProjection>> lookup,
        string normalizedScope,
        CustomFolderOutputProjection projection)
    {
        if (string.IsNullOrWhiteSpace(normalizedScope))
        {
            return;
        }

        if (!lookup.TryGetValue(normalizedScope, out List<CustomFolderOutputProjection> projections))
        {
            projections = [];
            lookup[normalizedScope] = projections;
        }
        if (!projections.Contains(projection))
        {
            projections.Add(projection);
        }
    }

    private static IEnumerable<CustomFolderOutputProjection> FindCustomFolderProjectionsForRowPath(
        string normalizedRowPath,
        IReadOnlyDictionary<string, List<CustomFolderOutputProjection>> scopeLookup)
    {
        if (string.IsNullOrWhiteSpace(normalizedRowPath) || scopeLookup == null || scopeLookup.Count == 0)
        {
            yield break;
        }

        var yielded = new HashSet<CustomFolderOutputProjection>();
        foreach (string ancestor in EnumerateCustomFolderRowPathAncestors(normalizedRowPath))
        {
            if (!scopeLookup.TryGetValue(ancestor, out List<CustomFolderOutputProjection> projections))
            {
                continue;
            }

            foreach (CustomFolderOutputProjection projection in projections)
            {
                if (projection != null
                    && yielded.Add(projection)
                    && IsCustomFolderRowPathInProjectionScope(normalizedRowPath, projection))
                {
                    yield return projection;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateCustomFolderRowPathAncestors(string normalizedRowPath)
    {
        string current = TrimCustomFolderRowDirectorySeparators(NormalizeCustomFolderRowPath(normalizedRowPath));
        while (!string.IsNullOrWhiteSpace(current))
        {
            yield return current;

            string folderPath = NormalizeCustomFolderRowPath(Lr2FolderPath.ToFolderPath(current));
            folderPath = TrimCustomFolderRowDirectorySeparators(folderPath);
            if (!string.IsNullOrWhiteSpace(folderPath)
                && !string.Equals(folderPath, current, StringComparison.OrdinalIgnoreCase))
            {
                yield return folderPath;
            }

            string parent = Path.GetDirectoryName(current);
            parent = TrimCustomFolderRowDirectorySeparators(NormalizeCustomFolderRowPath(parent));
            if (string.IsNullOrWhiteSpace(parent)
                || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent;
        }
    }

    private static string TrimCustomFolderRowDirectorySeparators(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private CustomFolderOutputPhysicalSurface ResolveCustomFolderOutputPhysicalSurface(
        IReadOnlyCollection<CustomFolderOutputProjection> projections,
        string reason)
    {
        return ResolveCustomFolderOutputPhysicalSurface(
            (projections ?? [])
                .Select(projection => projection?.OutputDirectory)
                .Where(directory => !string.IsNullOrWhiteSpace(directory)),
            reason);
    }

    private CustomFolderOutputPhysicalSurface ResolveCustomFolderOutputPhysicalSurface(
        IEnumerable<string> outputDirectories,
        string reason)
    {
        var stopwatch = Stopwatch.StartNew();
        CustomFolderOutputPhysicalSurface providedSurface = null;
        try
        {
            providedSurface = lr2PlaylistFolderSynchronization?.GetCurrentAppManagedCustomFolderOutputPhysicalSurface();
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ObjectDisposedException)
        {
            LogPlaylistPerformance("playlist_custom_folder_output_repair physical_surface_provider_failed"
                + " reason=" + (reason ?? "unknown")
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
        }

        if (providedSurface?.DiscoveryComplete == true)
        {
            stopwatch.Stop();
            LogPlaylistPerformance("playlist_custom_folder_output_repair physical_surface_resolved"
                + " reason=" + (reason ?? "unknown")
                + " source=provider"
                + " entryCount=" + providedSurface.FileEntries.Count
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return providedSurface;
        }

        CustomFolderOutputPhysicalSurface enumeratedSurface =
            CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration(outputDirectories, reason);
        stopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair physical_surface_resolved"
            + " reason=" + (reason ?? "unknown")
            + " source=grouped_enumeration"
            + " entryCount=" + (enumeratedSurface?.FileEntries.Count ?? 0)
            + " discoveryComplete=" + (enumeratedSurface?.DiscoveryComplete == true).ToString().ToLowerInvariant()
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return enumeratedSurface ?? CustomFolderOutputPhysicalSurface.Empty;
    }

    private static CustomFolderOutputPhysicalSurface CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration(
        IEnumerable<CustomFolderOutputProjection> projections,
        string reason)
    {
        return CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration(
            (projections ?? []).Select(projection => projection?.OutputDirectory),
            reason);
    }

    private static CustomFolderOutputPhysicalSurface CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration(
        IEnumerable<string> outputDirectories,
        string reason)
    {
        List<string> roots = [.. (outputDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (roots.Count == 0)
        {
            return CustomFolderOutputPhysicalSurface.Empty;
        }

        var stopwatch = Stopwatch.StartNew();
        RootFileEnumerationResult result = RootFileEnumerationService.EnumerateFilesWithFallback(
            roots,
            [new RootFileEnumerationGroup(CustomFolderOutputLr2FolderEnumerationGroupName, [".lr2folder"])]);
        stopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair physical_surface_grouped_enumeration"
            + " reason=" + (reason ?? "unknown")
            + " roots=" + roots.Count
            + " success=" + result.Success.ToString().ToLowerInvariant()
            + " backend=" + QuoteLogValue(result.BackendName)
            + " entries=" + result.GetEntries(CustomFolderOutputLr2FolderEnumerationGroupName).Count
            + " queryHits=" + result.GetQueryHitCount(CustomFolderOutputLr2FolderEnumerationGroupName)
            + " queryMs=" + result.GetQueryMs(CustomFolderOutputLr2FolderEnumerationGroupName)
            + " enumerationMs=" + result.EnumerationMs
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds
            + " reasonDetail=" + QuoteLogValue(result.ErrorReason));
        return result.Success
            ? CustomFolderOutputPhysicalSurface.FromEntries(
                result.GetEntries(CustomFolderOutputLr2FolderEnumerationGroupName),
                discoveryComplete: true)
            : new CustomFolderOutputPhysicalSurface(
                new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: false);
    }

    private static IReadOnlyCollection<string> CreateCustomFolderExpectedDirectoryMetadataTargets(
        CustomFolderOutputProjection projection)
    {
        if (projection == null || string.IsNullOrWhiteSpace(projection.OutputDirectory))
        {
            return [];
        }

        IReadOnlyCollection<string> directoryRowGenerationScopes =
            CreateCustomFolderDirectoryRowGenerationScopes(projection.OutputDirectory, projection.Table, projection.Settings);
        return Lr2FolderFileDbSyncService.CreateParentDirectoryMetadataTargets(
            CreateCustomFolderFileSyncItemsForMetadata(projection),
            directoryRowGenerationScopes);
    }

    private static List<Lr2FolderFileSyncItem> CreateCustomFolderFileSyncItemsForMetadata(
        CustomFolderOutputProjection projection)
    {
        return [.. (projection?.Files ?? [])
            .Where(file => !string.IsNullOrWhiteSpace(file?.FilePath))
            .Select(file =>
            {
                var item = new Lr2FolderFileSyncItem
                {
                    FilePath = file.FilePath,
                    DatabasePath = file.DatabasePath
                };
                ApplyCustomFolderSourceClassification(item, projection.Table, projection.Settings);
                return item;
            })];
    }

    private static bool MarkCustomFolderOutputLayoutRepairTargets(
        CustomFolderOutputProjection projection,
        IReadOnlyDictionary<string, LR2SongDB.folder> rowsByPath,
        bool rowLookupSucceeded,
        CustomFolderOutputPhysicalSurface physicalSurface)
    {
        if (projection == null)
        {
            return false;
        }

        IReadOnlyList<CustomFolderOutputFileProjection> files = projection.Files ?? [];
        bool needsRepair = false;
        if (rowLookupSucceeded)
        {
            foreach (string directory in CreateCustomFolderExpectedDirectoryMetadataTargets(projection))
            {
                string rowPath = Lr2FolderPath.ToFolderPath(directory);
                rowPath = ResolveCustomFolderDatabasePath(projection.Table, rowPath ?? directory, projection.Settings);
                string normalizedRowPath = NormalizeCustomFolderRowPath(rowPath);
                if (string.IsNullOrWhiteSpace(normalizedRowPath)
                    || rowsByPath?.TryGetValue(normalizedRowPath, out LR2SongDB.folder directoryRow) != true)
                {
                    needsRepair = true;
                    break;
                }

                RootFileEnumerationEntry directoryEntry = RootFileEnumerationEntry.FromDirectoryInfo(directory);
                if (directoryEntry?.LastWriteTimeUtc == null
                    || directoryRow.date != directoryEntry.LastWriteTimeUtc.Value.ToUnixtime())
                {
                    needsRepair = true;
                    break;
                }
            }
        }

        if (physicalSurface?.DiscoveryComplete != true)
        {
            PlaylistCustomFolderOutputOwner.MarkAllFilesForWrite(projection);
            return needsRepair || files.Count > 0;
        }

        foreach (CustomFolderOutputFileProjection file in files)
        {
            if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
            {
                continue;
            }

            RootFileEnumerationEntry fileEntry = physicalSurface.Resolve(file.FilePath);
            if (fileEntry?.LastWriteTimeUtc == null)
            {
                needsRepair = true;
                continue;
            }

            if (!rowLookupSucceeded)
            {
                continue;
            }

            LR2SongDB.folder row = ResolveCustomFolderOutputRow(file, rowsByPath);
            DateTime lastWriteTimeUtc = fileEntry.LastWriteTimeUtc.Value;
            if (row == null
                || row.type == 1
                || row.date != lastWriteTimeUtc.ToUnixtime()
                || !IsCustomFolderFileRowParentEquivalent(projection, file, row, lastWriteTimeUtc))
            {
                projection.ForceWriteFilePaths.Add(file.FilePath);
                needsRepair = true;
            }
        }

        return needsRepair;
    }

    private static bool IsCustomFolderRowPathInProjectionScope(string normalizedRowPath, CustomFolderOutputProjection projection)
    {
        return IsCustomFolderRowPathInAnyScope(normalizedRowPath, CreateCustomFolderOutputRowScopePaths(projection));
    }

    private static bool IsCustomFolderRowPathInProtectedOutputScope(string normalizedRowPath, CustomFolderOutputProjection projection)
    {
        return IsCustomFolderRowPathInAnyScope(normalizedRowPath, CreateCustomFolderProtectedOutputRowScopePaths(projection));
    }

    private static bool IsCustomFolderRowPathInAnyScope(string normalizedRowPath, IEnumerable<string> scopePaths)
    {
        if (string.IsNullOrWhiteSpace(normalizedRowPath))
        {
            return false;
        }

        foreach (string scopePath in scopePaths ?? [])
        {
            if (IsSameOrDescendantCustomFolderRowPath(normalizedRowPath, scopePath))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSameOrDescendantCustomFolderRowPath(string path, string ancestor)
    {
        string normalizedPath = NormalizeCustomFolderRowPath(path);
        string normalizedAncestor = NormalizeCustomFolderRowPath(ancestor);
        if (string.IsNullOrWhiteSpace(normalizedPath) || string.IsNullOrWhiteSpace(normalizedAncestor))
        {
            return false;
        }
        if (string.Equals(normalizedPath, normalizedAncestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedPath.Length > normalizedAncestor.Length
            && normalizedPath.StartsWith(normalizedAncestor, StringComparison.OrdinalIgnoreCase)
            && (Lr2FolderPath.IsDirectorySeparator(normalizedAncestor[normalizedAncestor.Length - 1])
                || Lr2FolderPath.IsDirectorySeparator(normalizedPath[normalizedAncestor.Length]));
    }

    private static bool IsCustomFolderFileRowParentEquivalent(
        CustomFolderOutputProjection projection,
        CustomFolderOutputFileProjection file,
        LR2SongDB.folder row,
        DateTime lastWriteTimeUtc)
    {
        if (projection == null || file == null || row == null)
        {
            return false;
        }

        var item = new Lr2FolderFileSyncItem
        {
            FilePath = file.FilePath,
            DatabasePath = file.DatabasePath
        };
        ApplyCustomFolderSourceClassification(item, projection.Table, projection.Settings);
        bool created = Lr2FolderFileProjection.TryCreateFolderRow(new Lr2FolderFileRowRequest
        {
            FilePath = item.FilePath,
            DatabasePath = item.DatabasePath ?? file.DatabasePath ?? file.FilePath,
            Definition = file.Definition,
            ExistingRow = row,
            LastWriteTimeUtc = lastWriteTimeUtc,
            FolderType = item.FolderType,
            ParentHash = item.ParentHash
        }, out LR2SongDB.folder expectedRow);
        return created
            && row.type == expectedRow.type
            && string.Equals(row.parent, expectedRow.parent, StringComparison.Ordinal);
    }

    private static LR2SongDB.folder ResolveCustomFolderOutputRow(
        CustomFolderOutputFileProjection file,
        IReadOnlyDictionary<string, LR2SongDB.folder> rowsByPath)
    {
        if (file == null || rowsByPath == null)
        {
            return null;
        }

        string databasePath = NormalizeCustomFolderRowPath(file.DatabasePath);
        if (!string.IsNullOrWhiteSpace(databasePath)
            && rowsByPath.TryGetValue(databasePath, out LR2SongDB.folder row))
        {
            return row;
        }

        string filePath = NormalizeCustomFolderRowPath(file.FilePath);
        return !string.IsNullOrWhiteSpace(filePath)
            && rowsByPath.TryGetValue(filePath, out row)
                ? row
                : null;
    }

    private static string NormalizeCustomFolderRowPath(string path)
    {
        return Lr2FolderFileProjection.NormalizeDatabasePath(path);
    }

    private static string ResolveCustomFolderDatabasePath(
        BMSTable table,
        string filePath,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        var item = new Lr2FolderFileSyncItem
        {
            FilePath = filePath
        };
        ApplyCustomFolderSourceClassification(item, table, settings);
        return NormalizeCustomFolderRowPath(item.DatabasePath ?? item.FilePath);
    }

    private sealed class CustomFolderOutputRepairCandidate
    {
        public BMSTable Table { get; set; }

        public string OutputDirectory { get; set; }
    }

    private sealed class CustomFolderEntryScope
    {
        public string FolderName { get; set; }

        public string Title { get; set; }

        public bool IsAll { get; set; }
    }

    private void AssignCustomFolderProtectedOutputDirectories(
        IReadOnlyCollection<CustomFolderOutputProjection> projections,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= projections?
            .Select(projection => projection?.Settings)
            .FirstOrDefault(snapshot => snapshot != null)
            ?? GetCustomFolderOutputSettings();
        IReadOnlyCollection<string> knownOutputDirectories = CreateKnownCustomFolderOutputDirectories(projections, settings);
        foreach (CustomFolderOutputProjection projection in projections ?? [])
        {
            string outputDirectory = Lr2FolderPath.NormalizeDirectoryPath(projection?.OutputDirectory);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                continue;
            }

            projection.ProtectedOutputDirectories = [.. knownOutputDirectories
                .Where(directory => !string.Equals(directory, outputDirectory, StringComparison.OrdinalIgnoreCase)
                    && Lr2FolderPath.IsSameOrDescendant(directory, outputDirectory))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
    }

    private IReadOnlyCollection<string> CreateKnownCustomFolderOutputDirectories(
        IEnumerable<CustomFolderOutputProjection> projections,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetCustomFolderOutputSettings();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderOutputProjection projection in projections ?? [])
        {
            AddCustomFolderOutputDirectory(directories, projection?.OutputDirectory);
        }

        using (rwlockBMSTables.GetReaderGuard())
        {
            if (BMSTables == null)
            {
                return [.. directories];
            }

            foreach (BMSTable table in BMSTables)
            {
                if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
                {
                    continue;
                }

                try
                {
                    AddCustomFolderOutputDirectory(directories, ResolveCustomFolderOutputDirectory(table, settings));
                }
                catch (Exception ex) when (ex is ArgumentNullException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    continue;
                }
            }
        }
        return [.. directories];
    }

    private IReadOnlyList<BMSTable> GetCustomFolderTablesSnapshot()
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            return BMSTables == null
                ? []
                : [.. BMSTables.Where(table => table != null)];
        }
    }

    private static void AddCustomFolderOutputDirectory(ISet<string> directories, string directory)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            directories?.Add(normalized);
        }
    }

    private List<CustomFolderDefinition> BuildCustomFolderDefinitions(
        BMSTable bmsTable,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= GetCustomFolderOutputSettings();
        var definitions = new List<CustomFolderDefinition>();
        if (bmsTable == null)
        {
            return definitions;
        }
        if (LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(bmsTable.ignore_folder_output, LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder)
            || LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(bmsTable.ignore_folder_output, LR2SongDBExtended.playlist.CustomFolderType.UserFolder))
        {
            definitions.AddRange(makeCustomFolderDefinitionsUserFolder(bmsTable));
        }
        foreach (Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<CustomFolderDefinition>>> item in new List<Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<CustomFolderDefinition>>>>
        {
            new(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, makeCustomFolderDefinitionsLevelFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, table => WrapFlatCustomFolderDefinitions(makeCustomFolderTextsAlphabetFolder(table))),
            new(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, makeCustomFolderDefinitionsClearFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, makeCustomFolderDefinitionsDJLevelFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, table => WrapFlatCustomFolderDefinitions(makeCustomFolderTextsCategoryAllFolder(table))),
            new(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, table => WrapFlatCustomFolderDefinitions(makeCustomFolderTextsOtherFolder(table, settings))),
            new(LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder, makeCustomFolderDefinitionsBpmSortFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder, makeCustomFolderDefinitionsBpSortFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder, makeCustomFolderDefinitionsPlayCountSortFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder, makeCustomFolderDefinitionsLastPlaySortFolder)
        })
        {
            if (LR2SongDBExtended.playlist.IsCustomFolderTypeEnabled(bmsTable.ignore_folder_output, item.Item1))
            {
                definitions.AddRange(item.Item2(bmsTable));
            }
        }
        return definitions;
    }

    private static List<CustomFolderDefinition> WrapFlatCustomFolderDefinitions(IEnumerable<string> texts)
    {
        return [.. (texts ?? []).Select(text => new CustomFolderDefinition
        {
            RelativeDirectory = string.Empty,
            Text = text ?? string.Empty,
            ParsedDefinition = Lr2FolderFileProjection.ParseDefinition(ReadLinesFromText(text))
        })];
    }

    private static bool IsSameCustomFolderDirectory(string left, string right)
    {
        string normalizedLeft = Lr2FolderPath.NormalizeDirectoryPath(left);
        string normalizedRight = Lr2FolderPath.NormalizeDirectoryPath(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateCustomFolderManagedOutputBase(
        BMSTable bmsTable,
        bool isRootFolder,
        string rootOutputBaseDir = null,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        settings ??= CustomFolderOutputSettingsSnapshot.CreateCurrent();
        if (isRootFolder)
        {
            return rootOutputBaseDir ?? settings.LR2CustomFolderOutputBaseDirRootType;
        }
        return CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
            bmsTable?.custom_folder_output_base_name,
            settings.LR2CustomFolderOutputBaseDir,
            settings.LR2CustomFolderAdditionalOutputBaseDirs,
            out string outputBaseDir)
                ? outputBaseDir
                : null;
    }

    /// <summary>
    /// 指定プレイリストに対応するカスタムフォルダ出力を削除します。
    /// </summary>
    /// <param name="bmsTable">削除対象のプレイリスト。</param>
    /// <exception cref="InvalidOperationException">LR2DB モードでない場合。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentException">出力先に必要な情報が不足している場合。</exception>
    public void RemoveCustomFolder(BMSTable bmsTable)
    {
        RemoveCustomFolder(bmsTable, null);
    }

    internal void RemoveCustomFolder(BMSTable bmsTable, CustomFolderOutputSettingsSnapshot settings)
    {
        settings ??= GetCustomFolderOutputSettings();
        if (!settings.OperationModeLR2DB)
        {
            throw new InvalidOperationException("Custom-folder output operation mode is not enabled.");
        }
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        if (string.IsNullOrWhiteSpace(bmsTable.Output_dir))
        {
            throw new ArgumentException("bmsTable.Output_dir");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "ReOutputCustomFolder");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                customFolderOutputMaintenanceOwner.TryRemoveCustomFolder(
                    bmsTable,
                    ResolveCustomFolderOutputDirectory(bmsTable, settings),
                    bmsTable.is_root_folder,
                    settings.LR2CustomFolderOutputBaseDirRootType,
                    CreateCustomFolderManagedOutputBase(bmsTable, bmsTable.is_root_folder, settings: settings),
                    settings);
            }
        }
    }

    private static void ApplyCustomFolderSourceClassification(
        Lr2FolderFileSyncItem item,
        BMSTable bmsTable,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        if (item == null || bmsTable?.is_root_folder != true)
        {
            return;
        }
        settings ??= CustomFolderOutputSettingsSnapshot.CreateCurrent();
        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = item.FilePath,
            Lr2RootPath = settings.LR2RootPath,
            RootCustomFolderOutputBaseDir = settings.LR2CustomFolderOutputBaseDirRootType
        });
        item.DatabasePath = classification.DatabasePath;
        item.FolderType = classification.FolderType;
        item.ParentHash = classification.ParentHash;
    }

    private void SyncCustomFolderRows(
        string outputDir,
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        BMSTable bmsTable = null,
        IReadOnlyCollection<string> directoryRowGenerationScopes = null,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> ownedDirectoryEntries = null)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return;
        }

        directoryRowGenerationScopes ??= CreateCustomFolderDirectoryRowGenerationScopes(outputDir, bmsTable);
        Lr2FolderDirectoryMetadataSnapshot directoryMetadata = CreateCustomFolderParentDirectoryMetadataSnapshot(
            items,
            directoryRowGenerationScopes,
            [outputDir],
            ownedDirectoryEntries);
        Lr2FolderFileDbSyncResult result = GetLr2PlaylistFolderSynchronization().SyncPlaylistLr2FolderFileRows(
            "playlist_lr2folder_sync",
            new Lr2FolderFileDbSyncRequest
            {
                Items = items ?? [],
                ScopeDirectories = [outputDir],
                DirectoryRowScopeDirectories = [outputDir],
                DirectoryRowGenerationScopeDirectories = directoryRowGenerationScopes,
                DirectoryMetadataResolver = directoryMetadata.Resolve,
                AllowPrune = true
            });
        LogLr2FolderSyncResult("playlist_lr2folder_sync", result, 1, items?.Count ?? 0);
    }

    private Lr2FolderFileDbSyncResult SyncCustomFolderRowsBatch(
        IReadOnlyCollection<string> outputDirs,
        IReadOnlyCollection<string> outputRowScopeDirectories,
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories = null,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> ownedDirectoryEntries = null,
        IReadOnlyCollection<string> pruneScopePaths = null,
        IReadOnlyCollection<string> pruneExcludedDirectories = null,
        IReadOnlyCollection<string> pruneExcludedPaths = null,
        IReadOnlyCollection<string> emptyOutputDirectories = null)
    {
        outputDirs = [.. (outputDirs ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        IReadOnlyCollection<string> rowScopeDirectories = [.. (outputRowScopeDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (outputDirs.Count == 0 && rowScopeDirectories.Count == 0)
        {
            return null;
        }
        IReadOnlyCollection<string> pruneScopeDirectories = [.. outputDirs
            .Concat(rowScopeDirectories)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        IReadOnlyCollection<string> directoryRowGenerationScopes = [.. (directoryRowGenerationScopeDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        Lr2FolderDirectoryMetadataSnapshot directoryMetadata = CreateCustomFolderParentDirectoryMetadataSnapshot(
            items,
            directoryRowGenerationScopes,
            outputDirs,
            ownedDirectoryEntries);
        IReadOnlyCollection<string> exactScopePaths = CreateCustomFolderExactScopePaths(items, pruneScopePaths);
        IReadOnlyCollection<string> emptyDirectoryRowScopeDirectories = [.. (emptyOutputDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        IReadOnlyCollection<string> directoryRowScopeDirectories = [.. pruneScopeDirectories
            .Concat(emptyDirectoryRowScopeDirectories)
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        Lr2FolderFileDbSyncResult result = GetLr2PlaylistFolderSynchronization().SyncPlaylistLr2FolderFileRows(
            "playlist_lr2folder_batch_sync",
            new Lr2FolderFileDbSyncRequest
            {
                Items = items ?? [],
                ScopeDirectories = pruneScopeDirectories,
                ScopePaths = exactScopePaths,
                DirectoryRowScopeDirectories = directoryRowScopeDirectories,
                DirectoryRowGenerationScopeDirectories = directoryRowGenerationScopes,
                PruneExcludedDirectories = pruneExcludedDirectories ?? [],
                PruneExcludedPaths = pruneExcludedPaths ?? [],
                DirectoryMetadataResolver = directoryMetadata.Resolve,
                AllowPrune = true
            });
        LogLr2FolderSyncResult("playlist_lr2folder_batch_sync", result, outputDirs.Count, items?.Count ?? 0);
        return result;
    }

    private ILr2PlaylistFolderSynchronizationPort GetLr2PlaylistFolderSynchronization()
    {
        if (lr2PlaylistFolderSynchronization == null)
        {
            throw new InvalidOperationException("LR2 playlist folder synchronization is not configured.");
        }
        return lr2PlaylistFolderSynchronization;
    }

    private static void LogLr2FolderSyncResult(string operation, Lr2FolderFileDbSyncResult result, int scopeCount, int itemCount)
    {
        if (result == null)
        {
            return;
        }

        LogPlaylistPerformance(operation + " done"
            + " scopeCount=" + scopeCount
            + " itemCount=" + itemCount
            + " existingRows=" + result.ExistingReadCount
            + " existingExactRows=" + result.ExistingExactReadCount
            + " existingScopeRows=" + result.ExistingScopeReadCount
            + " generated=" + result.GeneratedCount
            + " upserted=" + result.UpsertedCount
            + " deleted=" + result.DeletedCount
            + " preserved=" + result.PreservedCount
            + " skippedUnsupported=" + result.SkippedUnsupportedPathCount
            + " skippedMissingMetadata=" + result.SkippedMissingMetadataCount
            + " elapsedMs=" + result.ElapsedMs);
    }

    private static IReadOnlyCollection<string> CreateCustomFolderExactScopePaths(IEnumerable<Lr2FolderFileSyncItem> items, IEnumerable<string> additionalPaths = null)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Lr2FolderFileSyncItem item in items ?? [])
        {
            if (item == null)
            {
                continue;
            }
            if (!string.IsNullOrWhiteSpace(item.DatabasePath))
            {
                result.Add(item.DatabasePath);
            }
            if (!string.IsNullOrWhiteSpace(item.FilePath))
            {
                result.Add(item.FilePath);
            }
        }
        foreach (string path in additionalPaths ?? [])
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                result.Add(path);
            }
        }
        return [.. result];
    }

    private static IEnumerable<string> ReadLinesFromText(string text)
    {
        using var reader = new StringReader(text ?? string.Empty);
        string line;
        while ((line = reader.ReadLine()) != null)
        {
            yield return line;
        }
    }

    private static Lr2FolderDirectoryMetadataSnapshot CreateCustomFolderParentDirectoryMetadataSnapshot(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories,
        IReadOnlyCollection<string> metadataSourceDirectories,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> ownedDirectoryEntries)
    {
        IReadOnlyCollection<string> metadataTargets = Lr2FolderFileDbSyncService.CreateParentDirectoryMetadataTargets(
            items,
            directoryRowGenerationScopeDirectories);
        HashSet<string> targetSet = CreateDirectoryTargetSet(metadataTargets);
        var directoryEntries = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddDirectoryEntries(directoryEntries, ownedDirectoryEntries, targetSet);
        List<string> missingTargets = [.. targetSet
            .Where(target => !directoryEntries.TryGetValue(target, out RootFileEnumerationEntry entry)
                || entry.LastWriteTimeUtc == null)];
        if (missingTargets.Count > 0)
        {
            IReadOnlyCollection<string> groupedSourceDirectories = [.. (metadataSourceDirectories ?? [])
                .Concat(missingTargets)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            IReadOnlyDictionary<string, RootFileEnumerationEntry> groupedDirectoryEntries =
                Lr2FolderDirectoryEnumerationService.CreateEntriesFromGroupedEnumeration(groupedSourceDirectories, missingTargets);
            AddDirectoryEntries(directoryEntries, groupedDirectoryEntries, targetSet);
        }

        return Lr2FolderDirectoryMetadataBuilder.Build(new Lr2FolderDirectoryMetadataBuildRequest
        {
            DirectoryPaths = metadataTargets,
            DirectoryLastWriteTimeUtcResolver = CreateDirectoryLastWriteTimeResolver(directoryEntries)
        });
    }

    private static IReadOnlyDictionary<string, RootFileEnumerationEntry> CreateOwnedCustomFolderDirectoryEntries(
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories)
    {
        IReadOnlyCollection<string> metadataTargets = Lr2FolderFileDbSyncService.CreateParentDirectoryMetadataTargets(
            items,
            directoryRowGenerationScopeDirectories);
        var result = new Dictionary<string, RootFileEnumerationEntry>(StringComparer.OrdinalIgnoreCase);
        AddOwnedCustomFolderDirectoryEntries(result, metadataTargets);
        return result;
    }

    private static void AddOwnedCustomFolderDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyCollection<Lr2FolderFileSyncItem> items,
        IReadOnlyCollection<string> directoryRowGenerationScopeDirectories)
    {
        IReadOnlyCollection<string> metadataTargets = Lr2FolderFileDbSyncService.CreateParentDirectoryMetadataTargets(
            items,
            directoryRowGenerationScopeDirectories);
        AddOwnedCustomFolderDirectoryEntries(result, metadataTargets);
    }

    private static void AddOwnedCustomFolderDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IEnumerable<string> metadataTargets)
    {
        if (result == null)
        {
            return;
        }

        foreach (string target in metadataTargets ?? [])
        {
            RootFileEnumerationEntry entry = RootFileEnumerationEntry.FromDirectoryInfo(target);
            string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
            if (!string.IsNullOrWhiteSpace(key))
            {
                result[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
            }
        }
    }

    private static HashSet<string> CreateDirectoryTargetSet(IEnumerable<string> metadataTargets)
    {
        return new HashSet<string>((metadataTargets ?? [])
            .Select(Lr2FolderPath.NormalizeDirectoryPath)
            .Where(path => !string.IsNullOrWhiteSpace(path)), StringComparer.OrdinalIgnoreCase);
    }

    private static void AddDirectoryEntries(
        IDictionary<string, RootFileEnumerationEntry> result,
        IReadOnlyDictionary<string, RootFileEnumerationEntry> source,
        ISet<string> targetSet)
    {
        if (result == null || source == null || targetSet == null)
        {
            return;
        }

        foreach (RootFileEnumerationEntry entry in source.Values)
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path);
            if (string.IsNullOrWhiteSpace(key) || !targetSet.Contains(key))
            {
                continue;
            }
            result[key] = new RootFileEnumerationEntry(key, entry.LastWriteTimeUtc, entry.FileSize);
        }
    }

    private static Func<string, DateTime?> CreateDirectoryLastWriteTimeResolver(
        IReadOnlyDictionary<string, RootFileEnumerationEntry> entriesByPath)
    {
        return path =>
        {
            string key = Lr2FolderPath.NormalizeDirectoryPath(path);
            return !string.IsNullOrWhiteSpace(key)
                && entriesByPath != null
                && entriesByPath.TryGetValue(key, out RootFileEnumerationEntry entry)
                    ? entry.LastWriteTimeUtc
                    : null;
        };
    }

    private static IReadOnlyCollection<string> CreateCustomFolderDirectoryRowGenerationScopes(string outputDir, BMSTable bmsTable = null)
    {
        return CreateCustomFolderDirectoryRowGenerationScopes(outputDir, bmsTable, null);
    }

    private static IReadOnlyCollection<string> CreateCustomFolderDirectoryRowGenerationScopes(
        string outputDir,
        BMSTable bmsTable,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return [];
        }
        try
        {
            string normalizedOutputDir = Lr2FolderPath.NormalizeDirectoryPath(outputDir);
            settings ??= CustomFolderOutputSettingsSnapshot.CreateCurrent();
            if (bmsTable?.is_root_folder != true)
            {
                IEnumerable<string> normalOutputBases = new[]
                    {
                        settings.LR2CustomFolderOutputBaseDir
                    }
                    .Concat(CustomFolderOutputBaseRegistry.DeserializeBaseDirectories(settings.LR2CustomFolderAdditionalOutputBaseDirs));
                string normalOutputBase = normalOutputBases
                    .Select(NormalizeDirectoryPathOrNull)
                    .Where(baseDir => !string.IsNullOrWhiteSpace(baseDir))
                    .FirstOrDefault(baseDir => Lr2FolderPath.IsSameOrDescendant(normalizedOutputDir, baseDir));
                if (!string.IsNullOrWhiteSpace(normalOutputBase))
                {
                    return [CreateDirectoryRowGenerationBoundary(normalOutputBase)];
                }
            }

            return [CreateDirectoryRowGenerationBoundary(normalizedOutputDir)];
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return [outputDir];
        }
    }

    private static string CreateDirectoryRowGenerationBoundary(string rootEquivalentDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootEquivalentDirectory))
        {
            return null;
        }
        try
        {
            string normalizedRoot = Lr2FolderPath.NormalizeDirectoryPath(rootEquivalentDirectory);
            string parentDirectory = Path.GetDirectoryName(normalizedRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.IsNullOrWhiteSpace(parentDirectory)
                ? normalizedRoot
                : Lr2FolderPath.NormalizeDirectoryPath(parentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return rootEquivalentDirectory;
        }
    }

    private static string NormalizeDirectoryPathOrNull(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Lr2FolderPath.NormalizeDirectoryPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
        {
            return null;
        }
    }

    private static System.Windows.Threading.Dispatcher GetBMSTablesDispatcher(DispatcherCollection<BMSTable> tables)
    {
        if (tables == null)
        {
            return null;
        }
        if (Application.Current == null)
        {
            // Headless verification has no WPF application dispatcher.  Keep the collection
            // bound to the current thread so a later continuation cannot enqueue work onto a
            // dispatcher whose message pump is not running.
            System.Windows.Threading.Dispatcher currentDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
            if (tables.Dispatcher == null || !tables.Dispatcher.CheckAccess())
            {
                tables.Dispatcher = currentDispatcher;
            }
            return currentDispatcher;
        }
        return tables.Dispatcher ?? DispatcherHelper.UIDispatcher ?? Application.Current?.Dispatcher;
    }

    private T InvokeBMSTablesCollectionMutation<T>(Func<T> mutation)
    {
        if (mutation == null)
        {
            throw new ArgumentNullException(nameof(mutation));
        }
        System.Windows.Threading.Dispatcher dispatcher = GetBMSTablesDispatcher(BMSTables);
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            return mutation();
        }
        return (T)dispatcher.Invoke(mutation, System.Windows.Threading.DispatcherPriority.Normal);
    }

    private void InvokeBMSTablesCollectionMutation(Action mutation)
    {
        InvokeBMSTablesCollectionMutation(delegate
        {
            mutation();
            return true;
        });
    }

    private void RemoveTablesFromVisibleCollection(IEnumerable<BMSTable> tables)
    {
        InvokeBMSTablesCollectionMutation(delegate
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                List<Exception> failures = [];
                foreach (BMSTable table in tables ?? [])
                {
                    try
                    {
                        BMSTables.RemoveExt(table);
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                }
                try
                {
                    playlistAggregatePersistenceOwner.SetActiveCollection([], BMSTables);
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
                if (failures.Count == 1)
                {
                    throw failures[0];
                }
                if (failures.Count > 1)
                {
                    throw new AggregateException(failures).Flatten();
                }
            }
        });
    }

    /// <summary>
    /// 指定プレイリストを一覧と DB から削除します。
    /// </summary>
    /// <param name="bmsTable">削除対象のプレイリスト。</param>
    /// <returns>実際に削除されたプレイリスト。対象が存在しない場合は <see langword="null"/>。</returns>
    public BMSTable RemoveBMSTable(BMSTable bmsTable)
    {
        Exception collectionNotificationFailure = null;
        BMSTable tableToRemove = InvokeBMSTablesCollectionMutation(delegate
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                BMSTable activeTable = BMSTables.FirstOrDefault(candidate =>
                    ReferenceEquals(candidate, bmsTable)
                    || (bmsTable?.playlist_id.HasValue == true
                        && candidate?.playlist_id == bmsTable.playlist_id));
                if (activeTable == null)
                {
                    return null;
                }
                EnsurePlaylistEntriesLoaded(activeTable, "BMSPlaylist.RemoveBMSTable");
                try
                {
                    playlistAggregatePersistenceOwner.DeleteActiveTables([activeTable]);
                }
                catch (Exception deletionFailure)
                {
                    RestoreActivePlaylistAfterFailedRemoval(activeTable, deletionFailure);
                    throw;
                }

                bool removed;
                try
                {
                    removed = BMSTables.RemoveExt(activeTable);
                }
                catch (Exception removalFailure)
                {
                    removed = !BMSTables.Contains(activeTable);
                    if (removed)
                    {
                        collectionNotificationFailure = removalFailure;
                        NLogWrapper.FileLogger?.Warn(
                            removalFailure,
                            "playlist_collection_remove_notification_failed_after_commit table="
                            + (activeTable.name ?? string.Empty));
                    }
                }
                if (!removed)
                {
                    RestoreActivePlaylistAfterFailedRemoval(
                        activeTable,
                        new InvalidOperationException("Playlist collection removal failed after durable deletion."));
                    throw new InvalidOperationException("Playlist removal rollback unexpectedly returned.");
                }
                return activeTable;
            }
        });
        if (tableToRemove == null)
        {
            return null;
        }
        Exception bmtRemovalFailure = null;
        try
        {
            BmtOutput.QueueBeatorajaBmtRemoveForTable(tableToRemove, "RemoveBMSTable");
        }
        catch (Exception ex)
        {
            bmtRemovalFailure = ex;
        }
        if (collectionNotificationFailure != null && bmtRemovalFailure != null)
        {
            throw new AggregateException(collectionNotificationFailure, bmtRemovalFailure).Flatten();
        }
        if (collectionNotificationFailure != null)
        {
            ExceptionDispatchInfo.Capture(collectionNotificationFailure).Throw();
        }
        if (bmtRemovalFailure != null)
        {
            ExceptionDispatchInfo.Capture(bmtRemovalFailure).Throw();
        }
        return tableToRemove;
    }

    private void RestoreActivePlaylistAfterFailedRemoval(
        BMSTable table,
        Exception removalFailure)
    {
        try
        {
            playlistAggregatePersistenceOwner.MarkActiveTables([table]);
            playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                [table],
                requireCurrentTarget: true,
                hydrationReason: "BMSPlaylist.RemoveBMSTableRollback");
        }
        catch (Exception rollbackFailure)
        {
            throw new AggregateException(removalFailure, rollbackFailure).Flatten();
        }
        ExceptionDispatchInfo.Capture(removalFailure).Throw();
    }

    /// <summary>
    /// 新規の空プレイリストを生成し、一覧へ追加します。
    /// </summary>
    /// <returns>追加された新規プレイリスト。</returns>
    public BMSTable CreateBMSTable()
    {
        var bMSTable = new BMSTable
        {
            last_update = DateTime.Now,
            ignore_folder_output = ReadNewPlaylistIgnoreFolderOutputDefault()
        };
        return InvokeBMSTablesCollectionMutation(delegate
        {
            using (rwlockBMSTablesInitializeMin.GetReaderGuard())
            using (rwlockBMSTables.GetWriterGuard())
            {
                try
                {
                    bMSTable.bmt_sort = PlaylistBmtOutputOwner.ResolveNextBeatorajaBmtSort(BMSTables);
                    bMSTable.is_bmt_output = true;
                    BMSTables.Add(bMSTable);
                    playlistAggregatePersistenceOwner.MarkActiveTables([bMSTable]);
                }
                catch (Exception creationFailure)
                {
                    var rollbackFailures = new List<Exception>();
                    try
                    {
                        bool isVisible = BMSTables.Contains(bMSTable);
                        if (isVisible)
                        {
                            BMSTables.RemoveExt(bMSTable);
                        }
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures.Add(rollbackFailure);
                    }
                    try
                    {
                        bool remainsVisible = BMSTables.Contains(bMSTable);
                        if (remainsVisible)
                        {
                            rollbackFailures.Add(
                                new InvalidOperationException("New playlist remained visible after creation rollback."));
                        }
                    }
                    catch (Exception rollbackFailure)
                    {
                        rollbackFailures.Add(rollbackFailure);
                    }
                    if (rollbackFailures.Count > 0)
                    {
                        rollbackFailures.Insert(0, creationFailure);
                        throw new AggregateException(rollbackFailures).Flatten();
                    }
                    throw;
                }
                return bMSTable;
            }
        });
    }

    internal static LR2SongDBExtended.playlist.CustomFolderType ReadNewPlaylistIgnoreFolderOutputDefault()
    {
        return NormalizeNewPlaylistIgnoreFolderOutputDefault(Settings.Default.PlaylistDefaultIgnoreFolderOutput);
    }

    internal static LR2SongDBExtended.playlist.CustomFolderType NormalizeNewPlaylistIgnoreFolderOutputDefault(int value)
    {
        return (LR2SongDBExtended.playlist.CustomFolderType)(value & (int)LR2SongDBExtended.playlist.CustomFolderType.AllFolders);
    }

    /// <summary>
    /// プレイリスト内フォルダ名を変更し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="foldeNameBefore">変更前フォルダ名。</param>
    /// <param name="folderNameAfter">変更後フォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <returns>対象テーブルがアクティブで変更を適用した場合は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal bool RenameFolderBMSTable(BMSTable bmsTable, string foldeNameBefore, string folderNameAfter, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "RenameFolderBMSTable");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (!BMSTables.Contains(bmsTable))
            {
                return false;
            }
            bmsTable.RenameFolder(foldeNameBefore, folderNameAfter);
            if (commitFlag)
            {
                ReOutputCustomFolderAndCommitToDB(bmsTable);
            }
            return true;
        }
    }

    /// <summary>
    /// プレイリスト内フォルダを削除し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="folderNameDelete">削除するフォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <returns>対象テーブルがアクティブで変更を適用した場合は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal bool RemoveFolderBMSTable(BMSTable bmsTable, string folderNameDelete, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "RemoveFolderBMSTable");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (!BMSTables.Contains(bmsTable))
            {
                return false;
            }
            bmsTable.RemoveFolder(folderNameDelete);
            if (commitFlag)
            {
                ReOutputCustomFolderAndCommitToDB(bmsTable);
            }
            return true;
        }
    }

    /// <summary>
    /// プレイリスト内へ新規フォルダを追加し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="newfolder">希望する新規フォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <returns>実際に追加されたフォルダ名。対象が一覧に無い場合は <see langword="null"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal string CreateNewFolderBMSTable(BMSTable bmsTable, string newfolder = null, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "CreateNewFolderBMSTable");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (!BMSTables.Contains(bmsTable))
            {
                return null;
            }
            string result = bmsTable.CreateNewFolder(newfolder);
            if (commitFlag)
            {
                ReOutputCustomFolderAndCommitToDB(bmsTable);
            }
            return result;
        }
    }

    /// <summary>
    /// 指定エントリ群をプレイリスト内フォルダへ追加し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="entries">追加するエントリ群。</param>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="folderName">追加先フォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <returns>対象テーブルがアクティブで変更を適用した場合は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal bool AddPlaylistEntriesToFolderBMSTable(IEnumerable<BMSTableEntry> entries, BMSTable bmsTable, string folderName, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "AddPlaylistEntriesToFolderBMSTable");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.AddBMSTableEntriesToFolder(entries, folderName);
                if (commitFlag)
                {
                    ReOutputCustomFolderAndCommitToDB(bmsTable);
                }
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// 指定エントリ群をプレイリストから除去し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsEntries">除去するエントリ群。</param>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <returns>対象テーブルがアクティブで変更を適用した場合は <see langword="true"/>、それ以外は <see langword="false"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal bool RemoveEntriesBMSTable(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "RemoveEntriesBMSTable");
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.RemoveBMSTableEntries(bmsEntries);
                if (commitFlag)
                {
                    ReOutputCustomFolderAndCommitToDB(bmsTable);
                }
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// プレイリストを永続化し、LR2DB モード時はカスタムフォルダも再出力します。
    /// </summary>
    /// <param name="bmsTable">反映対象のプレイリスト。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void ReOutputCustomFolderAndCommitToDB(BMSTable bmsTable)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        using (rwlockBMSTables.GetReaderGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                EnsurePlaylistEntriesLoaded(bmsTable, "ReOutputCustomFolderAndCommitToDB");
                using (bmsTable.ReaderWriterLock.GetWriterGuard())
                {
                    playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                        [bmsTable],
                        hydrationReason: "ReOutputCustomFolderAndCommitToDB");
                    if (settings.OperationModeLR2DB)
                    {
                        customFolderOutputMaintenanceOwner.TryReOutputCustomFolder(bmsTable, settings);
                    }
                }
            }
        }
        BmtOutput.QueueBeatorajaBmtExportForTable(bmsTable, "ReOutputCustomFolderAndCommitToDB");
    }

    /// <summary>
    /// プレイリスト本体のヘッダ情報を永続化し、LR2DB モード時はカスタムフォルダだけを再出力します。
    /// </summary>
    /// <param name="bmsTables">反映対象のプレイリスト群。</param>
    /// <param name="reason">性能ログに残す理由。</param>
    /// <param name="progressCallback">処理済み件数、全件数、処理中プレイリスト名を通知する callback。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTables"/> が <see langword="null"/> の場合。</exception>
    internal void ReOutputCustomFoldersAndCommitHeadersToDB(
        IEnumerable<BMSTable> bmsTables,
        string reason,
        Action<int, int, string> progressCallback = null,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        if (bmsTables == null)
        {
            throw new ArgumentNullException(nameof(bmsTables));
        }
        List<BMSTable> tableList = [.. bmsTables.Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        using (rwlockBMSTables.GetReaderGuard())
        {
            tableList = [.. tableList.Where(table => BMSTables.Contains(table))];
        }
        if (tableList.Count == 0)
        {
            return;
        }

        settings ??= GetCustomFolderOutputSettings();
        if (!settings.OperationModeLR2DB)
        {
            CommitBMSTableHeadersToDB(tableList);
            return;
        }

        CustomFolderBatchOutputResult result = customFolderOutputMaintenanceOwner.ReOutputTablesAsync(
            tableList,
            reason,
            "playlist_custom_folder_output_bulk",
            forceWriteAllFiles: true,
            throwOnProjectionFailure: true,
            buildPreparedDataSurface: false,
            yieldBetweenTables: false,
            progressCallback,
            settings)
            .GetAwaiter()
            .GetResult();
        if (result.HasUnverifiedFiles)
        {
            throw new InvalidOperationException(
                "Custom-folder output could not verify one or more existing files before committing playlist headers.");
        }
        CommitBMSTableHeadersToDB(tableList);
    }

    /// <summary>
    /// カスタムフォルダ出力先を複数プレイリスト分まとめて移行し、プレイリスト本体のヘッダ情報だけを永続化します。
    /// </summary>
    /// <param name="bmsTables">反映対象のプレイリスト群。</param>
    /// <param name="outputDirPathBeforeByTable">変更前の出力先パス。</param>
    /// <param name="reason">性能ログに残す理由。</param>
    /// <param name="progressCallback">処理済み件数、全件数、処理中プレイリスト名を通知する callback。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTables"/> が <see langword="null"/> の場合。</exception>
    internal void MigrateCustomFolderOutputDirectoriesAndCommitHeadersToDB(
        IEnumerable<BMSTable> bmsTables,
        IReadOnlyDictionary<BMSTable, string> outputDirPathBeforeByTable,
        string reason,
        Action<int, int, string> progressCallback = null,
        IReadOnlyDictionary<BMSTable, bool> wasRootFolderBeforeByTable = null,
        string rootOutputBaseDirBefore = null,
        IReadOnlyDictionary<BMSTable, string> outputBaseDirPathBeforeByTable = null,
        CustomFolderOutputSettingsSnapshot settings = null)
    {
        if (bmsTables == null)
        {
            throw new ArgumentNullException(nameof(bmsTables));
        }

        List<BMSTable> tableList = [.. bmsTables.Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }

        using (rwlockBMSTables.GetReaderGuard())
        {
            tableList = [.. tableList.Where(table => BMSTables.Contains(table))];
        }
        if (tableList.Count == 0)
        {
            return;
        }

        settings ??= GetCustomFolderOutputSettings();
        CommitBMSTableHeadersToDB(tableList);
        if (!settings.OperationModeLR2DB)
        {
            return;
        }

        customFolderOutputMaintenanceOwner.MigrateCustomFolderOutputDirectories(
            tableList,
            outputDirPathBeforeByTable,
            reason,
            progressCallback,
            wasRootFolderBeforeByTable,
            rootOutputBaseDirBefore,
            outputBaseDirPathBeforeByTable,
            settings);
    }

    /// <summary>
    /// プレイリストを永続化し、LR2DB モード時は旧出力先から新出力先へカスタムフォルダも移行します。
    /// </summary>
    /// <param name="bmsTable">反映対象のプレイリスト。</param>
    /// <param name="outputDirPathBefore">変更前の出力先パス。</param>
    /// <param name="outputDirPathAfter">変更後の出力先パス。省略時は現設定から算出します。</param>
    /// <param name="queueBeatorajaBmtExport">変更後に beatoraja `.bmt` 出力を予約するかどうか。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void MigrateCustomFolderOutputDirectoryAndCommitToDB(
        BMSTable bmsTable,
        string outputDirPathBefore,
        string outputDirPathAfter = null,
        bool queueBeatorajaBmtExport = true,
        bool? wasRootFolderBefore = null,
        string rootOutputBaseDirBefore = null,
        string outputBaseDirBefore = null,
        bool inferOutputBaseDirBeforeWhenMissing = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException(nameof(bmsTable));
        }
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        if (settings.OperationModeLR2DB && string.IsNullOrWhiteSpace(outputDirPathAfter))
        {
            outputDirPathAfter = ResolveCustomFolderOutputDirectory(bmsTable, settings);
        }
        using (rwlockBMSTables.GetReaderGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                EnsurePlaylistEntriesLoaded(bmsTable, "MigrateCustomFolderOutputDirectoryAndCommitToDB");
                using (bmsTable.ReaderWriterLock.GetWriterGuard())
                {
                    playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                        [bmsTable],
                        hydrationReason: "MigrateCustomFolderOutputDirectoryAndCommitToDB");
                    if (settings.OperationModeLR2DB)
                    {
                        customFolderOutputMaintenanceOwner.TryMigrateCustomFolderOutputDirectory(
                            bmsTable,
                            outputDirPathBefore,
                            outputDirPathAfter,
                            wasRootFolderBefore ?? bmsTable.is_root_folder,
                            rootOutputBaseDirBefore,
                            outputBaseDirBefore,
                            inferOutputBaseDirBeforeWhenMissing,
                            settings);
                    }
                }
            }
        }
        if (queueBeatorajaBmtExport)
        {
            BmtOutput.QueueBeatorajaBmtExportForTable(bmsTable, "MigrateCustomFolderOutputDirectoryAndCommitToDB");
        }
    }

    /// <summary>
    /// プレイリスト本体とエントリを DB へ保存します。LR2 カスタムフォルダ出力は行いません。
    /// </summary>
    /// <param name="bmsTable">保存対象のプレイリスト。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void CommitBMSTableWithEntriesToDB(BMSTable bmsTable)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException(nameof(bmsTable));
        }
        using (rwlockBMSTables.GetReaderGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                    [bmsTable],
                    hydrationReason: "CommitBMSTableWithEntriesToDB");
            }
        }
    }

    /// <summary>
    /// 複数プレイリスト本体とエントリを 1 transaction で DB へ保存します。LR2 カスタムフォルダ出力は行いません。
    /// </summary>
    /// <param name="bmsTables">保存対象のプレイリスト群。</param>
    /// <param name="progressCallback">処理済み件数、全件数、処理中プレイリスト名を通知する callback。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTables"/> が <see langword="null"/> の場合。</exception>
    internal void CommitBMSTablesWithEntriesToDB(IEnumerable<BMSTable> bmsTables, Action<int, int, string> progressCallback = null)
    {
        if (bmsTables == null)
        {
            throw new ArgumentNullException(nameof(bmsTables));
        }
        List<BMSTable> tableList = [.. bmsTables.Where(table => table != null).Distinct()];
        if (tableList.Count == 0)
        {
            return;
        }
        using (rwlockBMSTables.GetReaderGuard())
        {
            tableList = [.. tableList.Where(table => BMSTables.Contains(table))];
            if (tableList.Count == 0)
            {
                return;
            }
            playlistAggregatePersistenceOwner.CommitTablesWithEntries(
                tableList,
                progressCallback,
                allowReloadReservation: false,
                requireCurrentTarget: true,
                hydrationReason: "CommitBMSTablesWithEntriesToDB");
        }
    }

    /// <summary>
    /// プレイリスト本体のヘッダ情報を DB へ保存します。
    /// </summary>
    /// <param name="bmsTable">保存対象のプレイリスト。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void CommitBMSTableHeaderToDB(BMSTable bmsTable)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        if (!ContainsBMSTable(bmsTable))
        {
            return;
        }
        using (rwlockBMSTables.GetReaderGuard())
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            playlistAggregatePersistenceOwner.ReplaceHeader(bmsTable);
        }
    }

    /// <summary>
    /// 複数プレイリスト本体のヘッダ情報を 1 transaction で DB へ保存します。
    /// </summary>
    /// <param name="bmsTables">保存対象のプレイリスト群。</param>
    /// <param name="requireCurrentTarget">対象が現在のコレクションから外れていた場合に失敗させるかどうか。</param>
    /// <param name="collectionReadLockHeld">呼び出し元がコレクション読み取りロックを保持している、直列化された経路かどうか。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTables"/> が <see langword="null"/> の場合。</exception>
    internal void CommitBMSTableHeadersToDB(
        IEnumerable<BMSTable> bmsTables,
        bool requireCurrentTarget = true,
        bool collectionReadLockHeld = false)
    {
        if (bmsTables == null)
        {
            throw new ArgumentNullException(nameof(bmsTables));
        }
        if (collectionReadLockHeld && !rwlockBMSTables.IsReadLockHeld)
        {
            throw new InvalidOperationException("The playlist collection read lock is required for guarded header persistence.");
        }
        List<BMSTable> requestedTableList = [.. bmsTables.Where(table => table != null).Distinct()];
        List<BMSTable> tableList = requestedTableList;
        if (tableList.Count == 0)
        {
            return;
        }
        IDisposable collectionReadGuard = null;
        bool hasCollectionReadLock = collectionReadLockHeld || rwlockBMSTables.IsReadLockHeld;
        if (!hasCollectionReadLock)
        {
            collectionReadGuard = rwlockBMSTables.GetReaderGuard();
            hasCollectionReadLock = true;
        }
        try
        {
            tableList = [.. tableList.Where(table => BMSTables.Contains(table))];
            if (tableList.Count == 0)
            {
                return;
            }
            if (requireCurrentTarget && tableList.Count != requestedTableList.Count)
            {
                throw new InvalidOperationException("Playlist header persistence target changed while the playlist was being updated.");
            }
            playlistAggregatePersistenceOwner.ReplaceHeaders(
                tableList,
                allowReloadReservation: false,
                requireCurrentTarget: requireCurrentTarget);
        }
        finally
        {
            collectionReadGuard?.Dispose();
        }
    }

    /// <summary>
    /// 単一プレイリストエントリを一意条件で置き換えて保存します。
    /// </summary>
    /// <param name="entry">保存対象のエントリ。</param>
    public void CommitBMSTableEntry(BMSTableEntry entry)
    {
        CommitBMSTableEntry(entry, null);
    }

    internal void CommitBMSTableEntry(BMSTableEntry entry, string editedPropertyName)
    {
        BMSTable owningTable = playlistAggregatePersistenceOwner.CommitEntry(
            entry,
            editedPropertyName,
            () => rwlockBMSTables.GetReaderGuard());
        if (owningTable != null)
        {
            bool owningTableWasActive = playlistAggregatePersistenceOwner.IsActive(owningTable);
            CustomFolderOutputSettingsSnapshot outputSettings = !string.IsNullOrWhiteSpace(owningTable.Output_dir)
                ? GetCustomFolderOutputSettings()
                : null;
            if (outputSettings?.OperationModeLR2DB == true)
            {
                EnsurePlaylistEntriesLoaded(owningTable, "CommitBMSTableEntry");
                using (owningTable.ReaderWriterLock.GetWriterGuard())
                {
                    if (owningTableWasActive)
                    {
                        customFolderOutputMaintenanceOwner.TryReOutputCustomFolder(owningTable, outputSettings);
                    }
                }
            }
            BmtOutput.QueueBeatorajaBmtExportForTable(owningTable, "CommitBMSTableEntry");
        }
    }

    /// <summary>
    /// プレイリスト関連テーブルの SQL ダンプ文字列を生成します。
    /// </summary>
    /// <returns>バックアップ用の SQL ダンプ文字列。</returns>
    public string GetPlaylistDump()
    {
        return playlistPersistenceRepository.GetPlaylistDump();
    }

    /// <summary>
    /// プレイリスト関連テーブルを SQL ダンプから復元します。
    /// </summary>
    /// <param name="sql">復元する SQL ダンプ文字列。</param>
    public void LoadPlaylistDump(string sql)
    {
        using (rwlockBMSTables.GetWriterGuard())
        {
            playlistAggregatePersistenceOwner.LoadPlaylistDump(sql);
        }
    }

    /// <summary>
    /// テーブル一覧 API から簡易プレイリスト情報を取得します。
    /// </summary>
    /// <param name="tableinfoUri">一覧 API の絶対 URI。</param>
    /// <returns>簡易プレイリスト情報の一覧。</returns>
    /// <exception cref="InvalidOperationException">URI が絶対 URI でない場合。</exception>
    /// <exception cref="ArgumentException">JSON の解釈に失敗した場合。</exception>
    public static List<BMSTableSimple> GetBMSTableInfo(Uri tableinfoUri)
    {
        if (!tableinfoUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("tableinfoUri.IsAbsoluteUri is not true");
        }
        string json;
        try
        {
            json = playlistHttpClient.GetString(tableinfoUri);
        }
        catch
        {
            throw;
        }
        object[] source;
        try
        {
            source = (object[])DynamicJson.Parse(json);
        }
        catch
        {
            throw new ArgumentException(Resources.Error_ParseFailed, "tableinfoUri");
        }
        return [.. source.Select((dynamic e) => new BMSTableSimple(e))];
    }

    /// <summary>
    /// SQL リテラルとして安全に埋め込める単引用符付き文字列へ変換します。
    /// </summary>
    /// <param name="str">引用する文字列。</param>
    /// <returns>単引用符で囲み、必要なエスケープを行った文字列。</returns>
    private static string sqlQuote(string str = null)
    {
        if (!string.IsNullOrWhiteSpace(str))
        {
            return "'" + str.Replace("'", "''") + "'";
        }
        return "''";
    }

    /// <summary>
    /// LR2 の <c>.lr2folder</c> 1 件分のテキストを組み立てます。
    /// </summary>
    /// <param name="command">抽出条件コマンド。</param>
    /// <param name="category">カテゴリ名。</param>
    /// <param name="title">表示タイトル。</param>
    /// <param name="maxtracks">最大曲数制限。</param>
    /// <returns>LR2 が解釈するフォルダ定義テキスト。</returns>
    private static string getCustomFolderText(string command, string category, string title, int maxtracks = 0, string informationA = "", string informationB = "")
    {
        return "#COMMAND " + command + Environment.NewLine + "#MAXTRACKS " + maxtracks + Environment.NewLine + "#CATEGORY " + category + Environment.NewLine + "#TITLE " + title + Environment.NewLine + "#INFORMATION_A " + (informationA ?? string.Empty) + Environment.NewLine + "#INFORMATION_B " + (informationB ?? string.Empty) + Environment.NewLine + Environment.NewLine;
    }

    private CustomFolderOutputSettingsSnapshot GetCustomFolderOutputSettings()
    {
        return customFolderOutputSettingsProvider()
            ?? throw new InvalidOperationException("Custom-folder output settings provider returned null.");
    }

    private string ResolveCustomFolderOutputDirectory(BMSTable bmsTable)
    {
        CustomFolderOutputSettingsSnapshot settings = GetCustomFolderOutputSettings();
        return ResolveCustomFolderOutputDirectory(bmsTable, settings);
    }

    private static string ResolveCustomFolderOutputDirectory(
        BMSTable bmsTable,
        CustomFolderOutputSettingsSnapshot settings)
    {
        if (settings == null)
        {
            throw new InvalidOperationException("Custom-folder output settings snapshot was not provided.");
        }
        return GetCustomFolderOutputDirectory(
            bmsTable,
            settings.LR2CustomFolderOutputBaseDir,
            settings.LR2CustomFolderOutputBaseDirRootType,
            settings.LR2CustomFolderAdditionalOutputBaseDirs);
    }

    public static string GetCustomFolderOutputDirectory(
        BMSTable bmsTable,
        string normalOutputBaseDirectory,
        string rootOutputBaseDirectory,
        string serializedAdditionalOutputBaseDirectories)
    {
        string outputBaseDirectory = bmsTable.is_root_folder
            ? rootOutputBaseDirectory
            : CustomFolderOutputBaseRegistry.ResolveNormalOutputBaseDirectory(
                bmsTable.custom_folder_output_base_name,
                normalOutputBaseDirectory,
                serializedAdditionalOutputBaseDirectories);
        return Path.Combine(outputBaseDirectory, bmsTable.Output_dir);
    }
}
