using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml.Linq;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Codeplex.Data;
using Livet;
using Livet.EventListeners;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json.Linq;
using NLog;
using Ribbit.Net;
using Ribbit.Util;
using Ribbit.Util.Extensions;
using SQLite;
using Sgml;

namespace BeMusicSeeker.Models;

/// <summary>
/// LR2 のプレイリスト定義、外部テーブル同期、カスタムフォルダ出力を一括管理します。
/// DB 永続化と外部取得の境界が同居しているため、この型がプレイリスト関連処理の集約点です。
/// </summary>
public partial class BMSPlaylist : NotificationObject
{
    private const string CustomFolderOutputLr2FolderEnumerationGroupName = "lr2folder";

    internal sealed class ComparablePlaylistEntryRow
    {
        public string Md5 { get; set; }

        public string Sha256 { get; set; }

        public string Level { get; set; }

        public string Title { get; set; }

        public string Artist { get; set; }

        public string Lr2BmsId { get; set; }

        public string Url { get; set; }

        public string UrlDiff { get; set; }

        public string NameDiff { get; set; }

        public string Comment { get; set; }

        public string Fingerprint { get; set; }
    }

    internal sealed class PlaylistContentDiffResult
    {
        public bool HasChanges { get; set; }

        public int PersistedOnlyCount { get; set; }

        public int ReloadedOnlyCount { get; set; }

        public IReadOnlyList<string> PersistedOnlySamples { get; set; }

        public IReadOnlyList<string> ReloadedOnlySamples { get; set; }
    }

    private sealed class PlaylistHashChangeResult
    {
        public bool HeaderKnownChanged { get; set; }

        public bool HeaderHashInitialized { get; set; }

        public bool DataKnownChanged { get; set; }

        public bool DataHashInitialized { get; set; }
    }

    internal sealed class PlaylistReloadPersistenceDecision
    {
        public bool EntryFingerprintChanged { get; internal set; }

        public bool HeaderKnownChanged { get; internal set; }

        public bool HeaderHashInitialized { get; internal set; }

        public bool DataKnownChanged { get; internal set; }

        public bool DataHashInitialized { get; internal set; }

        public bool UpdatesLastUpdate => HeaderKnownChanged || DataKnownChanged;

        public bool NeedsHeaderPersistence => HeaderKnownChanged || HeaderHashInitialized || DataKnownChanged || DataHashInitialized;

        public bool NeedsEntryPersistence => EntryFingerprintChanged || DataKnownChanged || DataHashInitialized;

        public bool NeedsStatePersistence => NeedsHeaderPersistence || NeedsEntryPersistence;

        public bool NeedsBmtExport => NeedsStatePersistence;
    }

    public sealed class PlaylistTableUpdateContext
    {
        public BMSTable NewTable { get; internal set; }

        public bool Updated { get; internal set; }

        public bool ReferenceEntriesChanged { get; internal set; }

        public BMSTable OldTable { get; internal set; }

        public IReadOnlyList<BMSTableEntry> OldEntriesSnapshot { get; internal set; }

        public IReadOnlyList<BMSTableEntry> NewEntriesSnapshot { get; internal set; }
    }

    internal sealed class PlaylistReloadTargetResult
    {
        public BMSTable SourceTable { get; internal set; }

        public BMSTable ResultTable { get; internal set; }

        public Uri Uri { get; internal set; }

        public bool Updated { get; internal set; }

        public Exception Exception { get; internal set; }

        public PlaylistTableUpdateContext UpdateContext { get; internal set; }

        public bool Succeeded => Exception == null;
    }

    internal sealed class PlaylistExternalTableLoadResult
    {
        public BMSTable SourceTable { get; internal set; }

        public BMSTable ExternalTable { get; internal set; }

        public Uri Uri { get; internal set; }

        public Exception Exception { get; internal set; }

        public bool Succeeded => Exception == null && ExternalTable != null;
    }

    /// <summary>
    /// 推定表の派生種類を識別します。
    /// </summary>
    private enum estimationTableType
    {
        easy,
        normal,
        hard,
        fc
    }

    /// <summary>
    /// プレイリスト更新処理の性能ログを出力するロガーです。
    /// </summary>
    private static readonly Logger installPerformanceLogger = LogManager.GetLogger("InstallPerformance.BMSPlaylist");

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
    /// 外部プレイリスト同期で同時に走らせる取得数の上限です。
    /// HTTP 待ち主体のため CPU 数ではなく接続数ベースで抑制します。
    /// </summary>
    private const int ExternalPlaylistSyncMaxConcurrency = 32;

    /// <summary>
    /// プレイリストを保存する LR2 Song DB のパスを保持します。
    /// </summary>
    private readonly string lr2SongDBPath;

    /// <summary>
    /// 推奨表更新時に参照する LR2 Score DB のパスを保持します。
    /// </summary>
    private readonly string lr2ScoreDBPath;

    /// <summary>
    /// LR2 設定を取得するための遅延評価デリゲートです。
    /// </summary>
    private readonly Func<LR2Config> lr2config;

    /// <summary>
    /// ローカルスコア一覧を取得するための遅延評価デリゲートです。
    /// </summary>
    private readonly Func<List<BMSScore>> bmsScores;

    /// <summary>
    /// beatoraja `.bmt` 出力時に playlist entry の欠けている hash を補完する resolver を取得します。
    /// </summary>
    private readonly Func<Func<BmtSongHashResolveRequest, Tuple<string, string>>> beatorajaBmtSongHashResolverFactory;

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

    private readonly SemaphoreSlim playlistEntriesHydrationSemaphore = new(1, 1);

    private int playlistEntriesHydrationQueued;

    private readonly object playlistEntriesHydrationRequestLock = new();

    private bool playlistEntriesHydrationPendingRunExternalSync;

    private readonly List<Action<PlaylistTableUpdateContext>> playlistEntriesHydrationPendingUpdateCallbacks = [];

    private readonly List<Action> playlistEntriesHydrationPendingCompletionActions = [];

    private readonly object beatorajaBmtExportQueueLock = new();

    private readonly object beatorajaBmtFileMutationLock = new();

    private readonly HashSet<int> pendingBeatorajaBmtExportPlaylistIds = [];

    private int beatorajaBmtExportQueued;

    private long beatorajaBmtFullExportGeneration;

    private long beatorajaBmtUrlSyncGeneration;

    private int beatorajaBmtFullExportActiveCount;

    private long beatorajaBmtExportProgressOperationSeed;

    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; set; }

    internal Func<CustomFolderOutputPhysicalSurface> CustomFolderOutputPhysicalSurfaceProvider { get; set; }

    internal Action<PlaylistSyncProgressSnapshot> BeatorajaBmtExportProgressReporter { get; set; }

    internal Action<string> Lr2FolderSyncMutationGuard { get; set; }

    internal Action<string, Exception> Lr2FolderSyncFailureReporter { get; set; }

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
    /// 発狂難易度推定 JSON の取得先 URI です。
    /// </summary>
    private static readonly Uri estimationJsonUri = new("http://walkure.net/hakkyou/data/bms.json", UriKind.Absolute);

    /// <summary>
    /// おすすめ表 JSON API のベース URL です。
    /// </summary>
    private static readonly string recommendJsonUriStr = "http://walkure.net/hakkyou/recommended_json.cgi?id=";

    /// <summary>
    /// おすすめ表へクリア状況を送信する更新 API の URI です。
    /// </summary>
    private static readonly Uri walkureUpdateUri = new("http://walkure.net/hakkyou/mle.cgi", UriKind.Absolute);

    /// <summary>
    /// 発狂表アーカイブの取得先 URI です。
    /// </summary>
    private static readonly Uri insaneUri = new("https://darksabun.club/table/archive/insane1/");

    /// <summary>
    /// Overjoy 表アーカイブの取得先 URI です。
    /// </summary>
    private static readonly Uri overjoyUri = new("https://darksabun.club/table/archive/old-overjoy/");

    /// <summary>
    /// 発狂表キャッシュの遅延初期化を直列化するためのロックです。
    /// </summary>
    private readonly object insaneTableLock = new();

    /// <summary>
    /// 読み込み済みの発狂表キャッシュです。
    /// </summary>
    private BMSTable _insaneTable;

    /// <summary>
    /// Overjoy 表キャッシュの遅延初期化を直列化するためのロックです。
    /// </summary>
    private readonly object overjoyTableLock = new();

    /// <summary>
    /// 読み込み済みの Overjoy 表キャッシュです。
    /// </summary>
    private BMSTable _overjoyTable;

    /// <summary>
    /// 段位課題曲に対応する疑似 BMS ID 変換表です。
    /// </summary>
    private static readonly Dictionary<int, string> insaneGrade = new()
    {
        { 4934, "0000000000200000000000000000519096a1536917e1f7f12a85d3dd7eb64932c65d0badebb7738e350022a59a1f0637c5605fc262eb9023b82c14d14cc837f8c46a81cb184f5a804c119930d6eba748" },
        { 4935, "00000000002000000000000000005190e6af04686aeaf0a0fa2698cb0b0111ad5dc1e3e22e4fc735f010e35f2ae77480093a8d66b89944db9e42aa2321b4f63739d0732ef7fee9ad0c8b044ccbe8a396" },
        { 4936, "00000000002000000000000000005190323e391cb09c023da7fe439d12bc6defc67ba013af164f2cdec7c8c98c90d8f53c4e5a90478a6a60b430e9816c942ffda8d67366d1e603ff3000af761aef0e53" },
        { 4937, "0000000000200000000000000000519096641e3e89ca6c61b1882ebf04ab4ad179dc444b48299814462ff23a2bf3ab79732abafcb87ac588b64a9be39c6deb5c2e1cc5e6bb96bff8a92f2defce49e70a" },
        { 4938, "00000000002000000000000000005190556ed0c159434dd1e7a252086482cfd2cd24fd54b865c744428da2513643dd8d17ee7a35750c8e1b1bdfe3a7ca3a311963947bce9af12ca7f84492b20ca21928" },
        { 4939, "000000000020000000000000000051906de6909c19156221d729b5e965c5cc2adab5146a185e036cb1514b71edf1032a755c4087937b7223c31cd17cedfce7888ae06bf384dbe6956b1fc76c071ff86f" },
        { 4940, "00000000002000000000000000005190bcf7607db2955c8979b54a0981b5eeb6fd493e9ff008dc10636a55dc4f2a081439f78361dc62f7ac355fb73e628bb336bcb640aa649d12c1ba16cd70f94ff0f8" },
        { 4941, "00000000002000000000000000005190683340fac1d376bea11c0848531ed6dd1f5cf9788ce30603c5ffe261f55d6c3d462c47942b654985da2e94c6e31a238b18a3d4f7f8b1d35277e759e5643a4757" },
        { 4942, "00000000002000000000000000005190cd78da88201c01afb934bcba55f955cbadec1bcc37806f155cdae08d873c9f770ab22f8cc36b18e6cce19f5f0906d71eee21ce9f5b84794c69e64e6d6d27caed" },
        { 4943, "000000000020000000000000000051901a1ca14aededce99bd136da649fbec7ac0d7baaaeb1b0ba9b17b94b587d2fb7067bc996815b98296a44fe0f802a6115da6738157b2dc1689bea1f6123660d723" },
        { 4944, "00000000002000000000000000005190032b0582871a07c604d8cba171f9579715e44dfea14cb6812f7fcb56a3a0c409151c0d2bb94ab56dfb753410ae70598e7300591ce85f888fbefe6984dc4938c3" },
        { 4945, "00000000002000000000000000005190c07125de4ed7fbe7cb066cc41e50e51efc7d46e7bbc9f6afd26d05e3bf2ef555b3887714270e28988ce900e4b9300994d1877ad5dc0134b27eb0238da5721eed" },
        { 11099, "00000000002000000000000000005190cfad3baadce9e02c45021963453d7c9477d23be22b2370925c573d922276bce0188a99f74ab71804f2e360dcf484545cc46a81cb184f5a804c119930d6eba748" },
        { 11100, "0000000000200000000000000000519010420967a85371e65db57967e6c696cdebc5946c3de24a01048d1c90cbd5c9d645446f6c96bdb081a2054b80cd8f720839d0732ef7fee9ad0c8b044ccbe8a396" },
        { 11101, "00000000002000000000000000005190d6fea1389a86fc14eb354fcc5e60e03ee58e3f718d089ec37caa0f2c7149a54b1e049d189ef9bef79824f55764517a58fb975909eb0f60a9190c7a1007515d4b" },
        { 11102, "00000000002000000000000000005190603aae2c3681cc2562b9b53b50408c6979dc444b48299814462ff23a2bf3ab79ff5d7235ba643bc85b804a3df2778590451ed38cdba0323388027129f5929645" },
        { 11103, "00000000002000000000000000005190f0feb9654ae044c0e95ebf68e2497c2e51e0a29d5c7fc9cdf5e5e8eee1ad11241cf2e138abd7199d6e4656447cfaed6c8e0df85dbf59c4753346f1582e2d4422" },
        { 11104, "00000000002000000000000000005190a4b59ca055a856b9a200ccfd1bf5c7bca0f48e96286e0ce275a33fd8cb851ff0c4134c29746e1ece168159ba4ae88d4567a76e7d26f3c3ea99fdc6f912f83021" },
        { 11105, "00000000002000000000000000005190f96cf3af3d9356407bff483d27f4aecca29f9500cebd16cbb8d14b69ace79db1871c6aed53c5dd3ac35686d9a92af4f440673cc8ea628a9452f6ae9ac74d5817" },
        { 11106, "00000000002000000000000000005190e9fc0fbbde68bfd10f2d289751a1fc6a3a500560e6ace69e08b51736ff1be0ca8a8019d432ab4dd9bde4e65faf1b663f7fd4d1b5a767fd97343129157782a95f" },
        { 11107, "0000000000200000000000000000519047de06762f16d164426e3197c5de967ca718de9a2cd2f88deafbd40f5929abb45077cd14b4759d0e8d61e854eac4e88ac87682922d1b87622b34fe7a7ffba52c" },
        { 11108, "0000000000200000000000000000519019fbf62d711282d81208f97f7135e2a23791d42c00cdfa135df3779eb0f78505b46b23fa8e2ad96e02763f687bb5a494efae90d527a71d753ea7ad2847ec2006" },
        { 11109, "00000000002000000000000000005190d70e2549e841819708d279d2478bb1e25e3711f2f576f3929156c9594b87efaca56ddb2169246817fb7527363fb9c687cefb4735c26f9488438021b4d7fdca31" },
        { 11110, "00000000002000000000000000005190f872dd65dd08638b06d80470a3233fb91b72e8f6439a698e78f94be16470b7892371263af3b0d644fba62526c9f494818a8a6c2f3511eb0876a6c9a2027d7bbe" }
    };

    /// <summary>
    /// 推定表キャッシュの遅延初期化を直列化するためのロックです。
    /// </summary>
    private readonly object estimationTableLock = new();

    /// <summary>
    /// EASY 推定表エントリのキャッシュです。
    /// </summary>
    private List<BMSTableEntry> _easyEntries;

    /// <summary>
    /// NORMAL 推定表エントリのキャッシュです。
    /// </summary>
    private List<BMSTableEntry> _normalEntries;

    /// <summary>
    /// HARD 推定表エントリのキャッシュです。
    /// </summary>
    private List<BMSTableEntry> _hardEntries;

    /// <summary>
    /// プレイリスト同期処理の実行中状態を保持します。
    /// </summary>
    private bool _IsPlaylistUpdating;

    private bool _PlaylistEntriesHydrationRunning;

    private int _PlaylistEntriesHydrationRequestedVersion;

    private int _PlaylistEntriesHydrationCompletedVersion;

    /// <summary>
    /// FC 推定表エントリのキャッシュです。
    /// </summary>
    private List<BMSTableEntry> _fcEntries;

    /// <summary>
    /// 推定表 JSON の数値キーを DynamicJson で扱いやすい形式へ変換するための正規表現です。
    /// </summary>
    private readonly Regex workAroundRegex = new("\"(?<id>\\d+)\":{", RegexOptions.Compiled);

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

    public bool PlaylistEntriesHydrationRunning
    {
        get
        {
            return _PlaylistEntriesHydrationRunning;
        }
        private set
        {
            if (_PlaylistEntriesHydrationRunning != value)
            {
                _PlaylistEntriesHydrationRunning = value;
                RaisePropertyChanged("PlaylistEntriesHydrationRunning");
            }
        }
    }

    public int PlaylistEntriesHydrationRequestedVersion
    {
        get
        {
            return _PlaylistEntriesHydrationRequestedVersion;
        }
        private set
        {
            if (_PlaylistEntriesHydrationRequestedVersion != value)
            {
                _PlaylistEntriesHydrationRequestedVersion = value;
                RaisePropertyChanged("PlaylistEntriesHydrationRequestedVersion");
            }
        }
    }

    public int PlaylistEntriesHydrationCompletedVersion
    {
        get
        {
            return _PlaylistEntriesHydrationCompletedVersion;
        }
        private set
        {
            if (_PlaylistEntriesHydrationCompletedVersion != value)
            {
                _PlaylistEntriesHydrationCompletedVersion = value;
                RaisePropertyChanged("PlaylistEntriesHydrationCompletedVersion");
            }
        }
    }

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
                _BMSTables = value;
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
            using (rwlockBMSTables.GetReaderGuard())
            {
                if (BMSTables != null)
                {
                    return BMSTables.Any(t => t.ReaderWriterLock.LockingWriteCount != 0 || t.ReaderWriterLock.WaitingWriteCount > 0);
                }
            }
            return true;
        }
    }

    /// <summary>
    /// 発狂表を必要時に読み込み、以後はキャッシュを返します。
    /// </summary>
    /// <returns>読み込み済みの発狂表。取得失敗時は <see langword="null"/>。</returns>
    private BMSTable insaneTable
    {
        get
        {
            lock (insaneTableLock)
            {
                if (_insaneTable == null)
                {
                    try
                    {
                        _insaneTable = LoadExternalTable(insaneUri);
                    }
                    catch
                    {
                        _insaneTable = null;
                    }
                }
            }
            return _insaneTable;
        }
    }

    /// <summary>
    /// Overjoy 表を必要時に読み込み、以後はキャッシュを返します。
    /// </summary>
    /// <returns>読み込み済みの Overjoy 表。取得失敗時は <see langword="null"/>。</returns>
    private BMSTable overjoyTable
    {
        get
        {
            lock (overjoyTableLock)
            {
                if (_overjoyTable == null)
                {
                    try
                    {
                        _overjoyTable = LoadExternalTable(overjoyUri);
                    }
                    catch
                    {
                        _overjoyTable = null;
                    }
                }
            }
            return _overjoyTable;
        }
    }

    /// <summary>
    /// EASY 推定表エントリを必要時に生成して返します。
    /// </summary>
    /// <returns>EASY 推定表のエントリ一覧。</returns>
    private List<BMSTableEntry> easyEntries
    {
        get
        {
            lock (estimationTableLock)
            {
                if (_easyEntries == null)
                {
                    setEstimationTable();
                }
            }
            return _easyEntries;
        }
    }

    /// <summary>
    /// NORMAL 推定表エントリを必要時に生成して返します。
    /// </summary>
    /// <returns>NORMAL 推定表のエントリ一覧。</returns>
    private List<BMSTableEntry> normalEntries
    {
        get
        {
            lock (estimationTableLock)
            {
                if (_normalEntries == null)
                {
                    setEstimationTable();
                }
            }
            return _normalEntries;
        }
    }

    /// <summary>
    /// HARD 推定表エントリを必要時に生成して返します。
    /// </summary>
    /// <returns>HARD 推定表のエントリ一覧。</returns>
    private List<BMSTableEntry> hardEntries
    {
        get
        {
            lock (estimationTableLock)
            {
                if (_hardEntries == null)
                {
                    setEstimationTable();
                }
            }
            return _hardEntries;
        }
    }

    /// <summary>
    /// FC 推定表エントリを必要時に生成して返します。
    /// </summary>
    /// <returns>FC 推定表のエントリ一覧。</returns>
    private List<BMSTableEntry> fcEntries
    {
        get
        {
            lock (estimationTableLock)
            {
                if (_fcEntries == null)
                {
                    setEstimationTable();
                }
            }
            return _fcEntries;
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
        lr2ScoreDBPath = _lr2ScoreDB;
        lr2config = (getLR2Config ?? (Func<LR2Config>)(() => (LR2Config)null));
        bmsScores = (getBMSScores ?? (Func<List<BMSScore>>)(() => (List<BMSScore>)null));
        beatorajaBmtSongHashResolverFactory = getBeatorajaBmtSongHashResolver;
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
    /// プレイリスト関連テーブルと index を現在のアプリ所有スキーマへ揃えます。
    /// </summary>
    /// <param name="songDbPath">対象の song.db パス。</param>
    public static void EnsureSchema(string songDbPath)
    {
        if (songDbPath == null)
        {
            throw new ArgumentNullException(nameof(songDbPath));
        }
        if (!File.Exists(songDbPath))
        {
            throw new ArgumentException(string.Format(Resources.Error_LR2SongDBNotFound, songDbPath), nameof(songDbPath));
        }
        using var lR2SongDBExtended = new LR2SongDBExtended(songDbPath);
        EnsurePlaylistTablesAndIndexes(lR2SongDBExtended);
    }

    internal static void EnsureSchema(LR2SongDBExtended db)
    {
        if (db == null)
        {
            throw new ArgumentNullException(nameof(db));
        }
        EnsurePlaylistTablesAndIndexes(db);
    }

    /// <summary>
    /// DB からプレイリストを読み込み、必要に応じて外部同期とカスタムフォルダ出力まで実行します。
    /// 初期化済み一覧が空でない場合は、既存一覧を土台に同期処理のみ進めます。
    /// </summary>
    /// <param name="reloadExtPlaylist">外部同期対象プレイリストを再取得するかどうか。</param>
    /// <param name="updateCallbackAction">各プレイリスト更新後に呼ぶ追加コールバック。</param>
    /// <param name="semaphore">他初期化処理と連携するためのセマフォ。</param>
    /// <param name="queueBeatorajaBmtExportAfterHydration">playlist entries hydration 後に beatoraja `.bmt` 全体投影出力を予約するかどうか。</param>
    public void Initialize(bool reloadExtPlaylist = true, Action<PlaylistTableUpdateContext> updateCallbackAction = null, SemaphoreSlim semaphore = null, bool queueBeatorajaBmtExportAfterHydration = true)
    {
        var stopwatchInitialize = Stopwatch.StartNew();
        long updateTablesMs = 0L;
        long lr2configSyncMs = 0L;
        if (semaphore != null)
        {
            initSemaphore = semaphore;
        }
        using (rwlockBMSTablesInitializeAll.GetWriterGuard())
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                using (rwlockBMSTablesInitializeMin.GetWriterGuard())
                {
                    if (BMSTables.Count == 0)
                    {
                        List<BMSTable> list = LoadPlaylistHeadersFromDatabase(out long loadTablesMs);
                        LogPlaylistPerformance("playlist_init_header loadTablesMs=" + loadTablesMs
                            + " tableCount=" + list.Count
                            + " readOnly=true"
                            + " dbLockWaitMs=0"
                            + " entriesDeferred=true");
                        BMSTables.AddRange(list);
                    }
                }
            }
            initSemaphore?.Release();
            updateTablesMs = 0L;
            var stopwatchLr2configSync = Stopwatch.StartNew();
            SyncRootFolderOutputDirectoriesToLr2Config();
            stopwatchLr2configSync.Stop();
            lr2configSyncMs = stopwatchLr2configSync.ElapsedMilliseconds;
            QueueDeferredPlaylistEntriesHydration(
                "Initialize",
                reloadExtPlaylist,
                updateCallbackAction == null ? null : [updateCallbackAction],
                CreatePlaylistHydrationCompletionActions(queueBeatorajaBmtExportAfterHydration, "Initialize"));
        }
        stopwatchInitialize.Stop();
        LogPlaylistPerformance("playlist_init update_tables_ms=" + updateTablesMs + " lr2config_sync_ms=" + lr2configSyncMs + " total_ms=" + stopwatchInitialize.ElapsedMilliseconds);
        SchedulePlaylistUrlCompletionRefresh("Initialize");
        initSemaphore = null;
    }

    /// <summary>
    /// プレイリスト一覧とエントリを DB から再読み込みします。
    /// score DB は触らず、外部同期は呼び出し側で別途 schedule します。
    /// </summary>
    /// <param name="queueBeatorajaBmtExportAfterHydration">playlist entries hydration 後に beatoraja `.bmt` 全体投影出力を予約するかどうか。</param>
    public void ReloadTables(Action<PlaylistTableUpdateContext> updateCallbackAction = null, bool queueBeatorajaBmtExportAfterHydration = true)
    {
        var stopwatchReloadTables = Stopwatch.StartNew();
        long lr2configSyncMs = 0L;
        using (rwlockBMSTablesInitializeAll.GetWriterGuard())
        {
            List<BMSTable> list;
            long loadTablesMs;
            using (rwlockBMSTables.GetWriterGuard())
            {
                using (rwlockBMSTablesInitializeMin.GetWriterGuard())
                {
                    list = LoadPlaylistHeadersFromDatabase(out loadTablesMs);
                    BMSTables.Clear();
                    BMSTables.AddRange(list);
                }
            }
            LogPlaylistPerformance("playlist_reload_tables_header loadTablesMs=" + loadTablesMs
                + " tableCount=" + list.Count
                + " readOnly=true"
                + " dbLockWaitMs=0"
                + " entriesDeferred=true");
            var stopwatchLr2configSync = Stopwatch.StartNew();
            SyncRootFolderOutputDirectoriesToLr2Config();
            stopwatchLr2configSync.Stop();
            lr2configSyncMs = stopwatchLr2configSync.ElapsedMilliseconds;
            QueueDeferredPlaylistEntriesHydration(
                "ReloadTables",
                runExternalSyncAfterHydration: false,
                updateCallbackAction == null ? null : [updateCallbackAction],
                CreatePlaylistHydrationCompletionActions(queueBeatorajaBmtExportAfterHydration, "ReloadTables"));
        }
        stopwatchReloadTables.Stop();
        LogPlaylistPerformance("playlist_reload_tables lr2config_sync_ms=" + lr2configSyncMs + " total_ms=" + stopwatchReloadTables.ElapsedMilliseconds);
        SchedulePlaylistUrlCompletionRefresh("ReloadTables");
    }

    private List<BMSTable> LoadPlaylistHeadersFromDatabase(out long loadTablesMs)
    {
        var stopwatchLoadTables = Stopwatch.StartNew();
        List<BMSTable> list;
        using (LR2SongDBExtended lR2SongDBExtended = new BmsLibraryDbGateway(lr2SongDBPath).OpenSongDbReadOnly())
        {
            list = [.. (from t in lR2SongDBExtended.Table<BMSTable>()
                    orderby t.name
                    select t)];
            AttachPersistedCourses(lR2SongDBExtended, list);
        }
        stopwatchLoadTables.Stop();
        foreach (BMSTable table in list)
        {
            table.MarkEntriesNotLoaded();
        }
        loadTablesMs = stopwatchLoadTables.ElapsedMilliseconds;
        return list;
    }

    private static void AttachPersistedCourses(LR2SongDBExtended db, IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = tables?.Where(table => table != null && table.playlist_id.HasValue).ToList() ?? [];
        if (tableList.Count == 0)
        {
            return;
        }
        var coursesByPlaylistId = db.Table<LR2SongDBExtended.playlist_course>()
            .ToList()
            .Where(course => course.playlist_id.HasValue)
            .GroupBy(course => course.playlist_id.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(course => course.course_order).ToList());
        foreach (BMSTable table in tableList)
        {
            if (coursesByPlaylistId.TryGetValue(table.playlist_id.Value, out List<LR2SongDBExtended.playlist_course> courses))
            {
                table.SetPersistedCourses(courses);
            }
        }
    }

    private List<Action> CreateBeatorajaBmtProjectionCompletionActions(bool enabled, string reason)
    {
        if (!enabled)
        {
            return null;
        }
        return [() => QueueBeatorajaBmtExportAll(reason)];
    }

    private List<Action> CreatePlaylistHydrationCompletionActions(bool queueBeatorajaBmtExportAfterHydration, string reason)
    {
        List<Action> actions =
        [
            () => QueueCustomFolderOutputRepairAfterHydration(reason)
        ];
        List<Action> beatorajaBmtActions = CreateBeatorajaBmtProjectionCompletionActions(queueBeatorajaBmtExportAfterHydration, reason);
        if (beatorajaBmtActions != null)
        {
            actions.AddRange(beatorajaBmtActions);
        }
        return actions;
    }

    private void QueueCustomFolderOutputRepairAfterHydration(string reason)
    {
        if (!Settings.Default.OperationModeLR2DB)
        {
            return;
        }

        Task work()
        {
            RepairMissingCustomFolderOutputsAfterHydration(reason);
            return Task.CompletedTask;
        }

        if (StartupBackgroundTaskScheduler != null && StartupBackgroundTaskScheduler("playlist_custom_folder_output_repair", reason ?? "queue", "playlist_entries_hydration", work))
        {
            return;
        }
        Task.Run(work).Logging("QueueCustomFolderOutputRepairAfterHydration");
    }

    private bool IsBeatorajaBmtOutputEnabled()
    {
        return Settings.Default.EnableBeatorajaBmtOutput && !string.IsNullOrWhiteSpace(GetBeatorajaBmtTablePath());
    }

    private void ReportBeatorajaBmtExportProgress(long operationId, bool isActive, int totalCount, int completedCount, string currentTableName)
    {
        BeatorajaBmtExportProgressReporter?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = isActive,
            OperationId = operationId,
            TotalTableCount = totalCount,
            CompletedTableCount = completedCount,
            CurrentTableName = currentTableName ?? string.Empty,
            LabelFormat = Resources.Beatoraja_bmt_export_progress_label_format,
            SingleLabel = Resources.Beatoraja_bmt_export_progress_single_label
        });
    }

    internal void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath = null)
    {
        string outputPath = GetBeatorajaBmtTablePath();
        bool enabled = IsBeatorajaBmtOutputEnabled();
        bool keepFilesWhenDisabled = Settings.Default.KeepBeatorajaBmtFilesWhenOutputDisabled;
        long fullExportGeneration = Interlocked.Increment(ref beatorajaBmtFullExportGeneration);
        Interlocked.Increment(ref beatorajaBmtUrlSyncGeneration);
        async Task work()
        {
            Interlocked.Increment(ref beatorajaBmtFullExportActiveCount);
            try
            {
                await Task.Yield();
                if (!IsCurrentBeatorajaBmtFullExportGeneration(fullExportGeneration))
                {
                    return;
                }
                var totalStopwatch = Stopwatch.StartNew();
                long progressOperationId = Interlocked.Increment(ref beatorajaBmtExportProgressOperationSeed);
                if (!string.IsNullOrWhiteSpace(cleanupTablePath)
                    && (!enabled || !string.Equals(cleanupTablePath, outputPath, StringComparison.OrdinalIgnoreCase))
                    && (enabled || !keepFilesWhenDisabled))
                {
                    lock (beatorajaBmtFileMutationLock)
                    {
                        if (!IsCurrentBeatorajaBmtFullExportGeneration(fullExportGeneration))
                        {
                            return;
                        }
                        SyncBeatorajaManagedTableUrls(cleanupTablePath, BmtTableExportService.ReadManagedTableUrls(cleanupTablePath), []);
                        BmtTableExportService.CleanupManagedFiles(cleanupTablePath);
                    }
                }
                if (!enabled)
                {
                    if (!keepFilesWhenDisabled)
                    {
                        lock (beatorajaBmtFileMutationLock)
                        {
                            if (!IsCurrentBeatorajaBmtFullExportGeneration(fullExportGeneration))
                            {
                                return;
                            }
                            SyncBeatorajaManagedTableUrls(outputPath, BmtTableExportService.ReadManagedTableUrls(outputPath), []);
                        }
                    }
                    totalStopwatch.Stop();
                    LogPlaylistPerformance("beatoraja_bmt_export_all skipped reason=" + FormatTextForLog(reason)
                        + " enabled=false"
                        + " keepFiles=" + keepFilesWhenDisabled
                        + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
                    return;
                }
                List<BMSTable> tablesSnapshot;
                var snapshotStopwatch = Stopwatch.StartNew();
                using (rwlockBMSTables.GetReaderGuard())
                {
                    tablesSnapshot = BMSTables?.Where(table => table != null).ToList() ?? [];
                }
                snapshotStopwatch.Stop();
                bool progressStarted = false;
                try
                {
                    List<BMSTable> outputTablesSnapshot = [.. tablesSnapshot.Where(IsBeatorajaBmtOutputTarget).OrderBy(table => GetBeatorajaBmtSortOrTail(table.bmt_sort)).ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase).ThenBy(table => table.playlist_id ?? int.MaxValue)];
                    List<Tuple<BMSTable, BmtTableExportService.PlaylistExportMetadata>> outputTargets = [.. outputTablesSnapshot
                        .Select(table => Tuple.Create(table, BmtTableExportService.CreatePlaylistExportMetadata(table)))];
                    BmtTableExportService.ExportPlan exportPlan = BmtTableExportService.CreateExportPlan(outputPath, outputTargets.Select(target => target.Item2), cleanupStaleManagedFiles: true);
                    List<BMSTable> projectionTablesSnapshot = [.. outputTargets
                        .Where(target => exportPlan.RequiresProjection(target.Item2))
                        .Select(target => target.Item1)];
                    bool shouldReportProgress = projectionTablesSnapshot.Count > 0;
                    if (shouldReportProgress)
                    {
                        ReportBeatorajaBmtExportProgress(progressOperationId, true, projectionTablesSnapshot.Count, 0, string.Empty);
                        progressStarted = true;
                    }
                    var resolverStopwatch = Stopwatch.StartNew();
                    BeatorajaBmtHashOutputMode hashOutputMode = GetBeatorajaBmtHashOutputMode();
                    Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc = projectionTablesSnapshot.Count == 0 || hashOutputMode == BeatorajaBmtHashOutputMode.Original
                        ? null
                        : beatorajaBmtSongHashResolverFactory?.Invoke();
                    resolverStopwatch.Stop();
                    var projectionStopwatch = Stopwatch.StartNew();
                    List<Tuple<string, JObject>> tableDataSet = BuildBeatorajaBmtTableDataSetSnapshot(
                        projectionTablesSnapshot,
                        reason,
                        hashOutputMode,
                        hashResolverFunc,
                        shouldReportProgress
                            ? delegate (int completed, int total, string tableName)
                            {
                                ReportBeatorajaBmtExportProgress(progressOperationId, true, Math.Max(total, 1), completed, tableName);
                            }
                            : null);
                    projectionStopwatch.Stop();
                    var exportStopwatch = Stopwatch.StartNew();
                    int exportProgressTotal = projectionTablesSnapshot.Count + tableDataSet.Count;
                    BmtTableExportService.ExportResult exportResult;
                    lock (beatorajaBmtFileMutationLock)
                    {
                        if (!IsCurrentBeatorajaBmtFullExportGeneration(fullExportGeneration))
                        {
                            return;
                        }
                        exportResult = BmtTableExportService.ExportTableDataSet(
                            outputPath,
                            tableDataSet,
                            exportPlan,
                            shouldReportProgress
                                ? delegate (int completed, int total, string tableName)
                                {
                                    ReportBeatorajaBmtExportProgress(progressOperationId, true, Math.Max(exportProgressTotal, 1), projectionTablesSnapshot.Count + completed, tableName);
                                }
                                : null);
                        var urlSyncStopwatch = Stopwatch.StartNew();
                        SyncBeatorajaManagedTableUrls(outputPath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                        urlSyncStopwatch.Stop();
                        exportStopwatch.Stop();
                        totalStopwatch.Stop();
                        LogPlaylistPerformance("beatoraja_bmt_export_all completed reason=" + FormatTextForLog(reason)
                            + " tableCount=" + tablesSnapshot.Count
                            + " enabledOutputCount=" + outputTablesSnapshot.Count
                            + " outputCount=" + tableDataSet.Count
                            + " written=" + exportResult.WrittenCount
                            + " skipped=" + exportResult.SkippedWriteCount
                            + " removed=" + exportResult.RemovedCount
                            + " snapshotMs=" + snapshotStopwatch.ElapsedMilliseconds
                            + " resolverMs=" + resolverStopwatch.ElapsedMilliseconds
                            + " projectionMs=" + projectionStopwatch.ElapsedMilliseconds
                            + " exportMs=" + exportStopwatch.ElapsedMilliseconds
                            + " urlSyncMs=" + urlSyncStopwatch.ElapsedMilliseconds
                            + " elapsedMs=" + totalStopwatch.ElapsedMilliseconds);
                    }
                }
                finally
                {
                    if (progressStarted)
                    {
                        ReportBeatorajaBmtExportProgress(progressOperationId, false, 0, 0, string.Empty);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref beatorajaBmtFullExportActiveCount);
            }
        }
        if (StartupBackgroundTaskScheduler != null && StartupBackgroundTaskScheduler("beatoraja_bmt_export_all", reason ?? "queue", null, work))
        {
            return;
        }
        Task.Run(work).Logging("QueueBeatorajaBmtExportAll");
    }

    private bool IsCurrentBeatorajaBmtFullExportGeneration(long generation)
    {
        return generation == Interlocked.Read(ref beatorajaBmtFullExportGeneration);
    }

    private bool IsBeatorajaBmtFullExportActive()
    {
        return Volatile.Read(ref beatorajaBmtFullExportActiveCount) > 0;
    }

    internal void QueueBeatorajaBmtExportForTable(BMSTable table, string reason)
    {
        QueueBeatorajaBmtExport(table, reason);
    }

    internal void QueueBeatorajaBmtExportForTables(IEnumerable<BMSTable> tables, string reason)
    {
        if (!IsBeatorajaBmtOutputEnabled())
        {
            return;
        }
        List<int> playlistIds = [.. (tables ?? [])
            .Where(table => table?.playlist_id.HasValue == true)
            .Select(table => table.playlist_id.Value)
            .Distinct()];
        if (playlistIds.Count == 0)
        {
            return;
        }
        Interlocked.Increment(ref beatorajaBmtUrlSyncGeneration);
        lock (beatorajaBmtExportQueueLock)
        {
            foreach (int playlistId in playlistIds)
            {
                pendingBeatorajaBmtExportPlaylistIds.Add(playlistId);
            }
        }
        ScheduleBeatorajaBmtExportQueue(reason);
    }

    internal void QueueBeatorajaBmtRemoveForTable(BMSTable table, string reason)
    {
        if (!IsBeatorajaBmtOutputEnabled() || table?.playlist_id.HasValue != true)
        {
            return;
        }
        string tablePath = GetBeatorajaBmtTablePath();
        int playlistId = table.playlist_id.Value;
        Interlocked.Increment(ref beatorajaBmtUrlSyncGeneration);
        string playlistIdentity = playlistId.ToString(CultureInfo.InvariantCulture);
        async Task work()
        {
            await Task.Yield();
            while (IsBeatorajaBmtFullExportActive())
            {
                await Task.Delay(250);
            }
            lock (beatorajaBmtFileMutationLock)
            {
                if (!IsBeatorajaBmtOutputEnabled()
                    || !string.Equals(tablePath, GetBeatorajaBmtTablePath(), StringComparison.OrdinalIgnoreCase)
                    || FindBMSTableByPlaylistId(playlistId) != null)
                {
                    return;
                }
                BmtTableExportService.ExportResult exportResult = BmtTableExportService.RemoveManagedPlaylist(tablePath, playlistIdentity);
                SyncBeatorajaManagedTableUrls(tablePath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                LogPlaylistPerformance("beatoraja_bmt_remove completed reason=" + FormatTextForLog(reason)
                    + " playlistId=" + playlistIdentity
                    + " removed=" + exportResult.RemovedCount);
            }
        }
        Task.Run(work).Logging("QueueBeatorajaBmtRemove");
    }

    /// <summary>
    /// beatoraja の config_sys.json に登録する .bmt URL だけを、現在の playlist 設定で同期します。
    /// </summary>
    /// <param name="reason">同期理由。</param>
    internal void QueueBeatorajaBmtUrlSync(string reason)
    {
        if (!IsBeatorajaBmtOutputEnabled())
        {
            return;
        }
        long syncGeneration = Interlocked.Increment(ref beatorajaBmtUrlSyncGeneration);
        async Task work()
        {
            await Task.Yield();
            lock (beatorajaBmtFileMutationLock)
            {
                if (syncGeneration != Interlocked.Read(ref beatorajaBmtUrlSyncGeneration))
                {
                    return;
                }
                string tablePath = GetBeatorajaBmtTablePath();
                List<BmtTableExportService.ManagedTableUrlEntry> managedTables = BmtTableExportService.ReadManagedTableUrls(tablePath);
                SyncBeatorajaManagedTableUrls(tablePath, managedTables, managedTables, syncGeneration);
                LogPlaylistPerformance("beatoraja_bmt_url_sync completed reason=" + FormatTextForLog(reason)
                    + " managedCount=" + managedTables.Count);
            }
        }
        Task.Run(work).Logging("QueueBeatorajaBmtUrlSync");
    }

    private void QueueBeatorajaBmtExport(BMSTable table, string reason)
    {
        if (!IsBeatorajaBmtOutputEnabled() || table == null || !table.playlist_id.HasValue)
        {
            return;
        }
        Interlocked.Increment(ref beatorajaBmtUrlSyncGeneration);
        lock (beatorajaBmtExportQueueLock)
        {
            pendingBeatorajaBmtExportPlaylistIds.Add(table.playlist_id.Value);
        }
        ScheduleBeatorajaBmtExportQueue(reason);
    }

    private void ScheduleBeatorajaBmtExportQueue(string reason)
    {
        if (Interlocked.Exchange(ref beatorajaBmtExportQueued, 1) != 0)
        {
            return;
        }
        async Task work()
        {
            await Task.Yield();
            try
            {
                if (IsBeatorajaBmtFullExportActive())
                {
                    await Task.Delay(250);
                    return;
                }
                ProcessBeatorajaBmtExportQueue(reason);
            }
            finally
            {
                Interlocked.Exchange(ref beatorajaBmtExportQueued, 0);
                bool hasPending;
                lock (beatorajaBmtExportQueueLock)
                {
                    hasPending = pendingBeatorajaBmtExportPlaylistIds.Count > 0;
                }
                if (hasPending)
                {
                    ScheduleBeatorajaBmtExportQueue(reason ?? "reschedule");
                }
            }
        }
        if (StartupBackgroundTaskScheduler != null && StartupBackgroundTaskScheduler("beatoraja_bmt_export", reason ?? "queue", null, work))
        {
            return;
        }
        Task.Run(work).Logging("QueueBeatorajaBmtExport");
    }

    private void ProcessBeatorajaBmtExportQueue(string reason)
    {
        while (IsBeatorajaBmtOutputEnabled())
        {
            List<int> playlistIds;
            lock (beatorajaBmtExportQueueLock)
            {
                if (pendingBeatorajaBmtExportPlaylistIds.Count == 0)
                {
                    return;
                }
                playlistIds = [.. pendingBeatorajaBmtExportPlaylistIds];
                pendingBeatorajaBmtExportPlaylistIds.Clear();
            }
            foreach (int playlistId in playlistIds)
            {
                BMSTable table = FindBMSTableByPlaylistId(playlistId);
                string tablePath = GetBeatorajaBmtTablePath();
                BmtTableExportService.PlaylistExportMetadata exportMetadata = BmtTableExportService.CreatePlaylistExportMetadata(table);
                JObject tableData = BuildBeatorajaBmtTableDataSnapshot(table, reason);
                lock (beatorajaBmtFileMutationLock)
                {
                    if (!IsBeatorajaBmtOutputEnabled()
                        || !string.Equals(tablePath, GetBeatorajaBmtTablePath(), StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    BMSTable currentTable = FindBMSTableByPlaylistId(playlistId);
                    BmtTableExportService.PlaylistExportMetadata currentMetadata = BmtTableExportService.CreatePlaylistExportMetadata(currentTable);
                    bool preparedSnapshotIsCurrent = AreSameBeatorajaBmtExportMetadata(exportMetadata, currentMetadata);
                    List<BmtTableExportService.ManagedTableUrlEntry> previousManagedTables = BmtTableExportService.ReadManagedTableUrls(tablePath);
                    if (tableData != null && IsBeatorajaBmtOutputTarget(currentTable) && preparedSnapshotIsCurrent)
                    {
                        BmtTableExportService.ExportTableData(tablePath, tableData, currentMetadata);
                        SyncBeatorajaManagedTableUrls(tablePath, previousManagedTables, BmtTableExportService.ReadManagedTableUrls(tablePath));
                    }
                    else if (currentTable == null || !IsBeatorajaBmtOutputTarget(currentTable) || (tableData == null && preparedSnapshotIsCurrent))
                    {
                        BmtTableExportService.ExportResult exportResult = BmtTableExportService.RemoveManagedPlaylist(tablePath, playlistId.ToString(CultureInfo.InvariantCulture));
                        SyncBeatorajaManagedTableUrls(tablePath, exportResult.PreviousManagedTables, exportResult.CurrentManagedTables);
                    }
                    else
                    {
                        QueueBeatorajaBmtExport(currentTable, reason ?? "stale_snapshot_retry");
                    }
                }
            }
        }
    }

    private BMSTable FindBMSTableByPlaylistId(int playlistId)
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            return BMSTables?.FirstOrDefault(candidate => candidate != null && candidate.playlist_id == playlistId);
        }
    }

    private static bool AreSameBeatorajaBmtExportMetadata(BmtTableExportService.PlaylistExportMetadata left, BmtTableExportService.PlaylistExportMetadata right)
    {
        return left != null
            && right != null
            && string.Equals(left.PlaylistIdentity, right.PlaylistIdentity, StringComparison.Ordinal)
            && string.Equals(left.Url, right.Url, StringComparison.Ordinal)
            && string.Equals(left.FileName, right.FileName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && string.Equals(left.HeaderSha256, right.HeaderSha256, StringComparison.Ordinal)
            && string.Equals(left.DataSha256, right.DataSha256, StringComparison.Ordinal)
            && left.LastUpdateTicks == right.LastUpdateTicks
            && string.Equals(left.ProjectionInputSha256, right.ProjectionInputSha256, StringComparison.Ordinal);
    }

    private string GetBeatorajaBmtTablePath()
    {
        if (!string.IsNullOrWhiteSpace(Settings.Default.BeatorajaRootPath) && BeatorajaConfigService.IsBeatorajaRootPathValid(Settings.Default.BeatorajaRootPath))
        {
            return BeatorajaConfigService.GetTablePath(Settings.Default.BeatorajaRootPath);
        }
        return Settings.Default.BeatorajaBmtTablePath;
    }

    private void SyncBeatorajaManagedTableUrls(string tablePath, IEnumerable<BmtTableExportService.ManagedTableUrlEntry> previousManagedTables, IEnumerable<BmtTableExportService.ManagedTableUrlEntry> currentManagedTables, long? urlSyncGeneration = null)
    {
        if (string.IsNullOrWhiteSpace(Settings.Default.BeatorajaRootPath) || !BeatorajaConfigService.IsBeatorajaRootPathValid(Settings.Default.BeatorajaRootPath))
        {
            return;
        }
        string configuredTablePath = BeatorajaConfigService.GetTablePath(Settings.Default.BeatorajaRootPath);
        if (!string.IsNullOrWhiteSpace(tablePath) && !string.Equals(Path.GetFullPath(tablePath), Path.GetFullPath(configuredTablePath), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        List<string> previousUrls = [.. (previousManagedTables ?? [])
            .Select(entry => entry?.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))];
        IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys = CreateBeatorajaBmtTableUrlSortKeysSnapshot();
        List<string> currentUrls = Settings.Default.RegisterBeatorajaBmtUrls
            ? BuildBeatorajaManagedTableUrlsForConfigSync(currentManagedTables, sortKeys)
            : [];
        try
        {
            BeatorajaConfigService.SyncTableUrls(
                Settings.Default.BeatorajaRootPath,
                currentUrls,
                previousUrls,
                urlSyncGeneration.HasValue
                    ? () => urlSyncGeneration.Value == Interlocked.Read(ref beatorajaBmtUrlSyncGeneration)
                    : null);
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "beatoraja_table_url_sync_failed tablePath=" + FormatTextForLog(tablePath));
        }
    }

    private IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> CreateBeatorajaBmtTableUrlSortKeysSnapshot()
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            return (BMSTables ?? Enumerable.Empty<BMSTable>())
                .Where(table => table?.playlist_id.HasValue == true)
                .GroupBy(table => GetBeatorajaBmtPlaylistIdentity(table), StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        BMSTable table = group.First();
                        return new BeatorajaBmtTableUrlSortKey
                        {
                            Sort = GetBeatorajaBmtSortOrTail(table.bmt_sort),
                            Name = table.name ?? string.Empty,
                            PlaylistId = table.playlist_id ?? int.MaxValue
                        };
                    },
                    StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// beatoraja の config_sys.json に登録する管理対象 .bmt URL を、playlist の BMT SORT 順に並べます。
    /// </summary>
    /// <param name="currentManagedTables">現在 manifest に登録されている管理対象 .bmt URL。</param>
    /// <param name="sortKeys">playlist identity ごとの BMT SORT 情報。</param>
    /// <returns>config_sys.json に登録する URL。</returns>
    internal static List<string> BuildBeatorajaManagedTableUrlsForConfigSync(IEnumerable<BmtTableExportService.ManagedTableUrlEntry> currentManagedTables, IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys)
    {
        return [.. (currentManagedTables ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Url))
            .OrderBy(entry => ResolveBeatorajaBmtTableUrlSortKey(entry, sortKeys).IsKnown ? 0 : 1)
            .ThenBy(entry => ResolveBeatorajaBmtTableUrlSortKey(entry, sortKeys).Sort)
            .ThenBy(entry => ResolveBeatorajaBmtTableUrlSortKey(entry, sortKeys).Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(entry => ResolveBeatorajaBmtTableUrlSortKey(entry, sortKeys).PlaylistId)
            .ThenBy(entry => entry.PlaylistIdentity ?? string.Empty, StringComparer.Ordinal)
            .Select(entry => entry.Url)];
    }

    private static BeatorajaBmtTableUrlSortKey ResolveBeatorajaBmtTableUrlSortKey(BmtTableExportService.ManagedTableUrlEntry entry, IReadOnlyDictionary<string, BeatorajaBmtTableUrlSortKey> sortKeys)
    {
        if (entry != null
            && !string.IsNullOrEmpty(entry.PlaylistIdentity)
            && sortKeys != null
            && sortKeys.TryGetValue(entry.PlaylistIdentity, out BeatorajaBmtTableUrlSortKey key))
        {
            return key;
        }
        return new BeatorajaBmtTableUrlSortKey
        {
            IsKnown = false,
            Sort = int.MaxValue,
            Name = entry?.Name ?? string.Empty,
            PlaylistId = int.MaxValue
        };
    }

    internal sealed class BeatorajaBmtTableUrlSortKey
    {
        public bool IsKnown { get; set; } = true;

        public int Sort { get; set; }

        public string Name { get; set; }

        public int PlaylistId { get; set; }
    }

    private static string GetBeatorajaBmtPlaylistIdentity(BMSTable table)
    {
        return table?.playlist_id?.ToString(CultureInfo.InvariantCulture);
    }

    private JObject BuildBeatorajaBmtTableDataSnapshot(BMSTable table, string reason)
    {
        BeatorajaBmtHashOutputMode hashOutputMode = GetBeatorajaBmtHashOutputMode();
        Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc = hashOutputMode == BeatorajaBmtHashOutputMode.Original
            ? null
            : beatorajaBmtSongHashResolverFactory?.Invoke();
        return BuildBeatorajaBmtTableDataSnapshot(table, reason, hashOutputMode, hashResolverFunc);
    }

    private List<Tuple<string, JObject>> BuildBeatorajaBmtTableDataSetSnapshot(
        List<BMSTable> tablesSnapshot,
        string reason,
        BeatorajaBmtHashOutputMode hashOutputMode,
        Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc,
        Action<int, int, string> progressReporter)
    {
        if (tablesSnapshot == null || tablesSnapshot.Count == 0)
        {
            return [];
        }
        var projectionInputs = new BeatorajaBmtTableProjectionInput[tablesSnapshot.Count];
        for (int index = 0; index < tablesSnapshot.Count; index++)
        {
            BMSTable table = tablesSnapshot[index];
            projectionInputs[index] = CreateBeatorajaBmtTableProjectionInput(table, reason, index);
        }
        var projectionResults = new Tuple<string, JObject>[projectionInputs.Length];
        int projectedCount = 0;
        object progressLock = new();
        Parallel.ForEach(
            projectionInputs,
            new ParallelOptions { MaxDegreeOfParallelism = ResolveBeatorajaBmtProjectionDegree(projectionInputs.Length) },
            input =>
            {
                if (input?.Snapshot != null)
                {
                    BmtTableExportService.ISongHashResolver hashResolver = hashResolverFunc == null
                        ? null
                        : new BeatorajaBmtSongHashResolver(hashResolverFunc);
                    JObject tableData = BmtTableExportService.BuildTableData(input.Snapshot, hashResolver, hashOutputMode);
                    if (tableData != null)
                    {
                        projectionResults[input.Index] = Tuple.Create(input.PlaylistIdentity, tableData);
                    }
                }
                lock (progressLock)
                {
                    projectedCount++;
                    progressReporter?.Invoke(projectedCount, projectionInputs.Length, input?.TableName);
                }
            });
        return [.. projectionResults.Where(result => result != null)];
    }

    private BeatorajaBmtTableProjectionInput CreateBeatorajaBmtTableProjectionInput(BMSTable table, string reason, int index)
    {
        if (table == null)
        {
            return new BeatorajaBmtTableProjectionInput
            {
                Index = index
            };
        }
        EnsurePlaylistEntriesLoaded(table, reason ?? "BeatorajaBmtExport");
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return new BeatorajaBmtTableProjectionInput
            {
                Index = index,
                PlaylistIdentity = GetBeatorajaBmtPlaylistIdentity(table),
                TableName = table.name,
                Snapshot = BmtTableExportService.CreateProjectionSnapshot(table)
            };
        }
    }

    private static int ResolveBeatorajaBmtProjectionDegree(int count)
    {
        return Math.Max(1, Math.Min(Math.Min(Environment.ProcessorCount, 4), Math.Max(count, 1)));
    }

    private JObject BuildBeatorajaBmtTableDataSnapshot(BMSTable table, string reason, BeatorajaBmtHashOutputMode hashOutputMode, Func<BmtSongHashResolveRequest, Tuple<string, string>> hashResolverFunc)
    {
        if (!IsBeatorajaBmtOutputTarget(table))
        {
            return null;
        }
        EnsurePlaylistEntriesLoaded(table, reason ?? "BeatorajaBmtExport");
        BmtTableExportService.ISongHashResolver hashResolver = hashResolverFunc == null
            ? null
            : new BeatorajaBmtSongHashResolver(hashResolverFunc);
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return BmtTableExportService.BuildTableData(table, hashResolver, hashOutputMode);
        }
    }

    private static BeatorajaBmtHashOutputMode GetBeatorajaBmtHashOutputMode()
    {
        return BmtTableExportService.NormalizeHashOutputMode(Settings.Default.BeatorajaBmtHashOutputMode);
    }

    private sealed class BeatorajaBmtSongHashResolver(Func<BmtSongHashResolveRequest, Tuple<string, string>> resolve)
        : BmtTableExportService.ISongHashResolver
    {
        public BmtTableExportService.SongHashResolution Resolve(BmtSongHashResolveRequest request)
        {
            Tuple<string, string> resolved = resolve?.Invoke(request);
            return resolved == null
                ? null
                : new BmtTableExportService.SongHashResolution(resolved.Item1, resolved.Item2);
        }
    }

    private void SyncRootFolderOutputDirectoriesToLr2Config()
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            if (!Settings.Default.OperationModeLR2DB)
            {
                return;
            }
            IEnumerable<string> second = from t in BMSTables
                                         where t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)
                                         select Path.Combine(Settings.Default.LR2CustomFolderOutputBaseDirRootType, t.Output_dir);
            List<string> bMSSearchDirectories = lr2config().GetBMSSearchDirectoriesForChangeTracking();
            lr2config().SetBMSSearchDirectories(bMSSearchDirectories.Union(second).Distinct(StringComparer.OrdinalIgnoreCase));
            lr2config().Save();
        }
    }

    public void QueueDeferredPlaylistEntriesHydration(string reason, bool runExternalSyncAfterHydration = false, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null, List<Action> completionActions = null)
    {
        int version = PlaylistEntriesHydrationRequestedVersion + 1;
        PlaylistEntriesHydrationRequestedVersion = version;
        LogPlaylistPerformance("playlist_entries_hydration queue reason=" + (reason ?? string.Empty) + " version=" + version + " runExternalSyncAfterHydration=" + runExternalSyncAfterHydration.ToString().ToLowerInvariant());
        lock (playlistEntriesHydrationRequestLock)
        {
            playlistEntriesHydrationPendingRunExternalSync = playlistEntriesHydrationPendingRunExternalSync || runExternalSyncAfterHydration;
            if (updateCallbackActions != null)
            {
                playlistEntriesHydrationPendingUpdateCallbacks.AddRange(updateCallbackActions.Where(action => action != null));
            }
            if (completionActions != null)
            {
                playlistEntriesHydrationPendingCompletionActions.AddRange(completionActions.Where(action => action != null));
            }
        }
        if (Interlocked.Exchange(ref playlistEntriesHydrationQueued, 1) != 0)
        {
            LogPlaylistPerformance("playlist_entries_hydration coalesced reason=" + (reason ?? string.Empty) + " version=" + version);
            return;
        }
        async Task work()
        {
            try
            {
                await EnsureAllPlaylistEntriesLoadedAsync(reason ?? "queue", publishCompletedVersion: false).ConfigureAwait(false);
                bool mergedRunExternalSync;
                List<Action<PlaylistTableUpdateContext>> mergedUpdateCallbacks;
                List<Action> mergedCompletionActions;
                lock (playlistEntriesHydrationRequestLock)
                {
                    mergedRunExternalSync = playlistEntriesHydrationPendingRunExternalSync;
                    mergedUpdateCallbacks = [.. playlistEntriesHydrationPendingUpdateCallbacks];
                    mergedCompletionActions = [.. playlistEntriesHydrationPendingCompletionActions];
                    playlistEntriesHydrationPendingRunExternalSync = false;
                    playlistEntriesHydrationPendingUpdateCallbacks.Clear();
                    playlistEntriesHydrationPendingCompletionActions.Clear();
                }
                if (mergedRunExternalSync || mergedUpdateCallbacks.Count > 0)
                {
                    var stopwatchUpdateTables = Stopwatch.StartNew();
                    UpdateBMSTables(mergedRunExternalSync, mergedUpdateCallbacks);
                    stopwatchUpdateTables.Stop();
                    LogPlaylistPerformance("playlist_entries_hydration post_update_tables reason=" + (reason ?? string.Empty) + " reloadExtPlaylist=" + mergedRunExternalSync.ToString().ToLowerInvariant() + " callbackCount=" + mergedUpdateCallbacks.Count + " elapsedMs=" + stopwatchUpdateTables.ElapsedMilliseconds);
                }
                foreach (Action completionAction in mergedCompletionActions)
                {
                    try
                    {
                        completionAction();
                    }
                    catch (Exception ex)
                    {
                        Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_entries_hydration_completion_failed reason=" + FormatTextForLog(reason));
                    }
                }
                PlaylistEntriesHydrationCompletedVersion = PlaylistEntriesHydrationRequestedVersion;
            }
            finally
            {
                Interlocked.Exchange(ref playlistEntriesHydrationQueued, 0);
                bool hasPendingRequest;
                lock (playlistEntriesHydrationRequestLock)
                {
                    hasPendingRequest = playlistEntriesHydrationPendingRunExternalSync || playlistEntriesHydrationPendingUpdateCallbacks.Count > 0 || playlistEntriesHydrationPendingCompletionActions.Count > 0;
                }
                if (hasPendingRequest)
                {
                    LogPlaylistPerformance("playlist_entries_hydration reschedule reason=" + (reason ?? string.Empty));
                    QueueDeferredPlaylistEntriesHydration(reason ?? "reschedule");
                }
            }
        }
        if (StartupBackgroundTaskScheduler != null && StartupBackgroundTaskScheduler("playlist_entries_hydration", reason ?? "queue", null, work))
        {
            return;
        }
        Task.Run(work).Logging("QueueDeferredPlaylistEntriesHydration");
    }

    internal async Task EnsureAllPlaylistEntriesLoadedAsync(string reason, bool publishCompletedVersion = true)
    {
        List<BMSTable> tablesSnapshot;
        using (rwlockBMSTables.GetReaderGuard())
        {
            tablesSnapshot = [.. BMSTables.Where(table => table != null)];
        }
        if (tablesSnapshot.Count > 0 && tablesSnapshot.All(table => table.ArePlaylistEntriesLoaded))
        {
            return;
        }
        await playlistEntriesHydrationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using (rwlockBMSTables.GetReaderGuard())
            {
                tablesSnapshot = [.. BMSTables.Where(table => table != null)];
            }
            if (tablesSnapshot.Count > 0 && tablesSnapshot.All(table => table.ArePlaylistEntriesLoaded))
            {
                return;
            }
            PlaylistEntriesHydrationRunning = true;
            var stopwatchTotal = Stopwatch.StartNew();
            var dbGateway = new BmsLibraryDbGateway(lr2SongDBPath);
            PlaylistEntriesHydrationLoadResult loadResult = dbGateway.LoadStartupPlaylistEntries();
            List<BMSTableEntry> source = loadResult.Entries;
            var stopwatchGroup = Stopwatch.StartNew();
            Dictionary<int, List<BMSTableEntry>> entriesByPlaylistId = [];
            foreach (BMSTableEntry entryItem in source)
            {
                if (!entryItem.playlist_id.HasValue)
                {
                    continue;
                }
                int key = entryItem.playlist_id.Value;
                if (!entriesByPlaylistId.TryGetValue(key, out List<BMSTableEntry> value))
                {
                    value = [];
                    entriesByPlaylistId[key] = value;
                }
                value.Add(entryItem);
            }
            stopwatchGroup.Stop();
            int removedEntryCount = source.Count(entry => entry != null && entry.is_removed);
            int activeEntryCount = source.Count - removedEntryCount;
            var stopwatchAssign = Stopwatch.StartNew();
            int assignedTableCount = 0;
            using (rwlockBMSTables.GetWriterGuard())
            {
                foreach (BMSTable table in BMSTables.Where(table => table != null).ToList())
                {
                    if (table.ArePlaylistEntriesLoaded)
                    {
                        continue;
                    }
                    if (table.playlist_id.HasValue && entriesByPlaylistId.TryGetValue(table.playlist_id.Value, out List<BMSTableEntry> value))
                    {
                        using (table.ReaderWriterLock.GetWriterGuard())
                        {
                            table.entries = value;
                        }
                    }
                    else
                    {
                        using (table.ReaderWriterLock.GetWriterGuard())
                        {
                            table.entries = [];
                        }
                    }
                    assignedTableCount++;
                }
            }
            stopwatchAssign.Stop();
            stopwatchTotal.Stop();
            long entryLoadRowsPerMs = loadResult.DbReadMs <= 0
                ? source.Count
                : source.Count / Math.Max(1L, loadResult.DbReadMs);
            LogPlaylistPerformance("playlist_entries_hydration done reason=" + (reason ?? string.Empty)
                + " projection=" + loadResult.Projection
                + " tableCount=" + tablesSnapshot.Count
                + " assignedTableCount=" + assignedTableCount
                + " rows=" + source.Count
                + " entryCount=" + source.Count
                + " activeEntryCount=" + activeEntryCount
                + " removedEntryCount=" + removedEntryCount
                + " readOnly=" + loadResult.ReadOnly.ToString().ToLowerInvariant()
                + " dbLockWaitMs=" + loadResult.DbLockWaitMs
                + " dbReadMs=" + loadResult.DbReadMs
                + " materializeMs=" + loadResult.MaterializeMs
                + " groupMs=" + stopwatchGroup.ElapsedMilliseconds
                + " assignMs=" + stopwatchAssign.ElapsedMilliseconds
                + " totalMs=" + stopwatchTotal.ElapsedMilliseconds
                + " entryLoadRowsPerMs=" + entryLoadRowsPerMs);
            bool allTablesLoaded;
            using (rwlockBMSTables.GetReaderGuard())
            {
                allTablesLoaded = BMSTables.Where(table => table != null).All(table => table.ArePlaylistEntriesLoaded);
            }
            if (allTablesLoaded)
            {
                if (publishCompletedVersion)
                {
                    PlaylistEntriesHydrationCompletedVersion = PlaylistEntriesHydrationRequestedVersion;
                }
            }
        }
        catch (Exception ex)
        {
            using (rwlockBMSTables.GetReaderGuard())
            {
                foreach (BMSTable table in BMSTables.Where(table => table != null && !table.ArePlaylistEntriesLoaded))
                {
                    table.MarkEntriesLoadFailed(ex.Message);
                }
            }
            LogPlaylistPerformance("playlist_entries_hydration failed reason=" + (reason ?? string.Empty) + " message=" + ex.Message);
            throw;
        }
        finally
        {
            PlaylistEntriesHydrationRunning = false;
            playlistEntriesHydrationSemaphore.Release();
        }
    }

    internal void EnsurePlaylistEntriesLoaded(BMSTable table, string reason)
    {
        if (table == null || table.ArePlaylistEntriesLoaded)
        {
            return;
        }
        using (table.ReaderWriterLock.GetWriterGuard())
        {
            if (table.ArePlaylistEntriesLoaded)
            {
                return;
            }
            table.MarkEntriesLoading();
        }
        var stopwatch = Stopwatch.StartNew();
        try
        {
            List<BMSTableEntry> entries = [.. LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: false)];
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                table.entries = entries;
            }
            stopwatch.Stop();
            int removedEntryCount = entries.Count(entry => entry != null && entry.is_removed);
            LogPlaylistPerformance("playlist_entries_load_table done reason=" + (reason ?? string.Empty)
                + " playlistId=" + (table.playlist_id.HasValue ? table.playlist_id.Value.ToString(CultureInfo.InvariantCulture) : "(null)")
                + " name=\"" + (table.name ?? string.Empty).Replace("\"", "\"\"") + "\""
                + " entryCount=" + entries.Count
                + " activeEntryCount=" + (entries.Count - removedEntryCount)
                + " removedEntryCount=" + removedEntryCount
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                table.MarkEntriesLoadFailed(ex.Message);
            }
            LogPlaylistPerformance("playlist_entries_load_table failed reason=" + (reason ?? string.Empty) + " message=" + ex.Message);
            throw;
        }
    }

    private static void EnsurePlaylistTablesAndIndexes(LR2SongDBExtended db)
    {
        db.CreateTable<LR2SongDBExtended.playlist>();
        db.CreateTable<LR2SongDBExtended.playlist_course>();
        db.CreateTable<LR2SongDBExtended.playlist_entry>();
        EnsurePlaylistMetadataColumns(db);
        EnsurePlaylistEntrySha256Column(db);
        EnsureCustomFolderOutputStatusTable(db);
        EnsurePlaylistCourseIndexes(db);
        RebuildPlaylistEntryIndexes(db);
    }

    private static void EnsureCustomFolderOutputStatusTable(LR2SongDBExtended db)
    {
        const string createSql =
            "CREATE TABLE IF NOT EXISTS playlist_custom_folder_output_status ("
            + "playlist_id INTEGER PRIMARY KEY,"
            + "output_directory TEXT NOT NULL,"
            + "is_root_folder INTEGER NOT NULL,"
            + "ignore_folder_output INTEGER NOT NULL,"
            + "entry_type INTEGER NOT NULL,"
            + "folder_sort_key INTEGER NOT NULL,"
            + "folder_sort_ascending INTEGER NOT NULL,"
            + "enable_unsent INTEGER NOT NULL,"
            + "header_sha256 TEXT NULL,"
            + "data_sha256 TEXT NULL,"
            + "last_update_ticks INTEGER NOT NULL,"
            + "physical_mtime_signature TEXT NOT NULL"
            + ");";
        db.Execute(createSql);
        if (!IsCustomFolderOutputStatusSchemaCurrent(db))
        {
            db.Execute("DROP TABLE IF EXISTS playlist_custom_folder_output_status;");
            db.Execute(createSql);
        }
    }

    private static bool IsCustomFolderOutputStatusSchemaCurrent(LR2SongDBExtended db)
    {
        string[] expectedColumns =
        [
            "playlist_id",
            "output_directory",
            "is_root_folder",
            "ignore_folder_output",
            "entry_type",
            "folder_sort_key",
            "folder_sort_ascending",
            "enable_unsent",
            "header_sha256",
            "data_sha256",
            "last_update_ticks",
            "physical_mtime_signature"
        ];
        try
        {
            string[] actualColumns = [.. db.Query<CustomFolderOutputStatusColumnRow>(
                "PRAGMA table_info(playlist_custom_folder_output_status);")
                .Select(row => row?.name)
                .Where(name => !string.IsNullOrWhiteSpace(name))];
            return actualColumns.SequenceEqual(expectedColumns, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is SQLiteException || ex is InvalidOperationException)
        {
            return false;
        }
    }

    private static void EnsurePlaylistMetadataColumns(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
        EnsureColumn(db, tableName, "tag", "TEXT NULL");
        EnsureColumn(db, tableName, "header_sha256", "TEXT NULL");
        EnsureColumn(db, tableName, "data_sha256", "TEXT NULL");
        EnsureColumn(db, tableName, "custom_folder_output_base_name", "TEXT NULL");
        EnsureColumn(db, tableName, "bmt_sort", "INTEGER NULL");
        EnsureColumn(db, tableName, "is_bmt_output", "INTEGER NULL");
        NormalizePersistedBeatorajaBmtPlaylistSettings(db);
    }

    /// <summary>
    /// 既存 DB に BMT 出力順の欠損や重複がある場合、現在の意味を保ったまま連番へ正規化します。
    /// </summary>
    /// <param name="db">playlist table を含む DB 接続。</param>
    private static void NormalizePersistedBeatorajaBmtPlaylistSettings(LR2SongDBExtended db)
    {
        List<BMSTable> tables = [.. db.Table<BMSTable>()];
        if (NormalizeBeatorajaBmtPlaylistSettings(tables) == 0)
        {
            return;
        }
        string savepoint = db.SaveTransactionPoint();
        try
        {
            foreach (BMSTable table in tables)
            {
                db.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
            }
            db.Commit();
        }
        catch
        {
            db.RollbackTo(savepoint);
            throw;
        }
    }

    /// <summary>
    /// playlist の BMT 出力順を、現在の並び意味を保ったまま 1 始まりの連番へ正規化します。
    /// 欠損値は既存の name 順互換を優先して末尾側へ配置します。
    /// </summary>
    /// <param name="tables">正規化対象の playlist 群。</param>
    /// <returns>値が変更された playlist 数。</returns>
    internal static int NormalizeBeatorajaBmtSortOrder(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> orderedTables = [.. (tables ?? [])
            .Where(table => table != null)
            .OrderBy(table => IsValidBeatorajaBmtSort(table.bmt_sort) ? 0 : 1)
            .ThenBy(table => GetBeatorajaBmtSortOrTail(table.bmt_sort))
            .ThenBy(table => table.name ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(table => table.playlist_id ?? int.MaxValue)];
        int changedCount = 0;
        for (int index = 0; index < orderedTables.Count; index++)
        {
            int normalizedSort = index + 1;
            if (orderedTables[index].bmt_sort != normalizedSort)
            {
                orderedTables[index].bmt_sort = normalizedSort;
                changedCount++;
            }
        }
        return changedCount;
    }

    /// <summary>
    /// 新規 playlist 用に、現在の BMT 出力順の最後尾番号を返します。
    /// </summary>
    /// <param name="tables">既存 playlist 群。</param>
    /// <returns>現在の最大 BMT 出力順 + 1。</returns>
    internal static int ResolveNextBeatorajaBmtSort(IEnumerable<BMSTable> tables)
    {
        return ((tables ?? [])
            .Where(table => table != null && IsValidBeatorajaBmtSort(table.bmt_sort))
            .Select(table => table.bmt_sort.Value)
            .DefaultIfEmpty(0)
            .Max()) + 1;
    }

    private static int NormalizeBeatorajaBmtPlaylistSettings(IEnumerable<BMSTable> tables)
    {
        List<BMSTable> tableList = [.. (tables ?? []).Where(table => table != null)];
        return NormalizeBeatorajaBmtSortOrder(tableList)
            + NormalizeBeatorajaBmtOutputTargets(tableList);
    }

    private static bool IsValidBeatorajaBmtSort(int? sort)
    {
        return sort.HasValue && sort.Value > 0;
    }

    private static int NormalizeBeatorajaBmtOutputTargets(IEnumerable<BMSTable> tables)
    {
        int changedCount = 0;
        foreach (BMSTable table in (tables ?? []).Where(table => table != null && !table.is_bmt_output.HasValue))
        {
            table.is_bmt_output = true;
            changedCount++;
        }
        return changedCount;
    }

    private static bool IsBeatorajaBmtOutputTarget(BMSTable table)
    {
        return table?.is_bmt_output != false;
    }

    private static int GetBeatorajaBmtSortOrTail(int? sort)
    {
        return IsValidBeatorajaBmtSort(sort) ? sort.Value : int.MaxValue;
    }

    private static void EnsureColumn(LR2SongDBExtended db, string tableName, string columnName, string columnType)
    {
        string sql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + sqlQuote(tableName) + ";");
        if (string.IsNullOrWhiteSpace(sql) || sql.IndexOf("\"" + columnName + "\"", StringComparison.OrdinalIgnoreCase) >= 0 || Regex.IsMatch(sql, "(^|[^A-Za-z0-9_])" + Regex.Escape(columnName) + "([^A-Za-z0-9_]|$)", RegexOptions.IgnoreCase))
        {
            return;
        }
        db.Execute("ALTER TABLE \"" + tableName + "\" ADD COLUMN \"" + columnName + "\" " + columnType + ";");
    }

    private static void EnsurePlaylistEntrySha256Column(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string sql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'table' AND name = " + sqlQuote(tableName) + ";");
        if (string.IsNullOrWhiteSpace(sql) || sql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            db.Execute("ALTER TABLE \"" + tableName + "\" ADD COLUMN \"sha256\" TEXT NULL;");
        }
    }

    private static void EnsurePlaylistCourseIndexes(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName();
        EnsurePlaylistEntryIndex(db, tableName, "playlist_course_idx_id",
        [
            SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id)
        ]);
        long count = db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + sqlQuote("playlist_course_idx_uniq") + ";");
        if (count == 0)
        {
            db.CreateIndex("playlist_course_idx_uniq", tableName,
            [
                SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id),
                SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.course_order)
            ], unique: true);
        }
    }

    private static void RebuildPlaylistEntryIndexes(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string uniqueIndexSql = db.ExecuteScalar<string>("SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'playlist_entry_idx_uniq';");
        if (string.IsNullOrWhiteSpace(uniqueIndexSql) || uniqueIndexSql.IndexOf("sha256", StringComparison.OrdinalIgnoreCase) < 0)
        {
            if (!string.IsNullOrWhiteSpace(uniqueIndexSql))
            {
                db.Execute("DROP INDEX IF EXISTS 'playlist_entry_idx_uniq';");
            }
            db.CreateIndex("playlist_entry_idx_uniq", tableName,
            [
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.sha256),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.lr2_bmsid),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
            ], unique: true);
        }
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_id",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_folder",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_title",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_md5",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_sha256",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.sha256),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_level",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.level),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_adddate",
        [
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.adddate),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.is_removed)
        ]);
    }

    private static void EnsurePlaylistEntryIndex(LR2SongDBExtended db, string tableName, string indexName, string[] columnNames)
    {
        long count = db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + sqlQuote(indexName) + ";");
        if (count == 0)
        {
            db.CreateIndex(indexName, tableName, columnNames);
        }
    }

    internal static string SqlQuoteForTest(string value)
    {
        return sqlQuote(value);
    }

    /// <summary>
    /// <c>bmseeker:</c> スキームの Walkure 系テーブル URI を解釈し、対応するプレイリストを構築します。
    /// </summary>
    /// <param name="pageUri">Walkure 系テーブルを指す絶対 URI。</param>
    /// <param name="baseTable">既存プレイリストから引き継ぐ設定値。</param>
    /// <returns>URI に対応するプレイリスト。</returns>
    /// <exception cref="ArgumentException">対応していない URI が指定された場合。</exception>
    public BMSTable LoadWalkureTable(Uri pageUri, BMSTable baseTable = null)
    {
        if (!pageUri.IsAbsoluteUri || pageUri.Scheme != "bmseeker")
        {
            throw new ArgumentException(Resources.Error_SchemeMustBeBemusic, "pageUri");
        }
        var bMSTable = new BMSTable
        {
            Page_url = pageUri
        };
        if (baseTable != null)
        {
            bMSTable.compat_prefix = baseTable.compat_prefix;
            bMSTable.playlist_id = baseTable.playlist_id;
        }
        string absolutePath = pageUri.AbsolutePath;
        if (!(absolutePath == "table.estimation"))
        {
            if (!(absolutePath == "table.recommended"))
            {
                throw new ArgumentException(Resources.Error_UnsupportedURI, pageUri.ToString());
            }
            string input = Uri.UnescapeDataString(pageUri.Query);
            var regex = new Regex("id=(\\d+)");
            var regex2 = new Regex("mode=([^&]+)");
            var regex3 = new Regex("filter=([^&]+)");
            var regex4 = new Regex("name=([^&]+)");
            var regex5 = new Regex("base=([^&]+)");
            Match match = regex.Match(input);
            Match match2 = regex2.Match(input);
            Match match3 = regex3.Match(input);
            Match match4 = regex4.Match(input);
            Match match5 = regex5.Match(input);
            int lr2id = 0;
            string mode = null;
            string filter = null;
            string displayName = null;
            string baseline = null;
            if (match.Success)
            {
                lr2id = int.Parse(match.Groups[1].ToString());
            }
            if (match2.Success)
            {
                mode = match2.Groups[1].ToString();
            }
            if (match3.Success)
            {
                filter = match3.Groups[1].ToString();
            }
            if (match4.Success)
            {
                displayName = match4.Groups[1].ToString();
            }
            if (match5.Success)
            {
                baseline = match5.Groups[1].ToString();
            }
            loadRecommendedTable(bMSTable, baseTable, mode, filter, displayName, lr2id, baseline);
        }
        else
        {
            switch (pageUri.Query.TrimStart('?'))
            {
                case "type=easy":
                    loadEstimationTable(bMSTable, estimationTableType.easy);
                    break;
                case "type=normal":
                    loadEstimationTable(bMSTable, estimationTableType.normal);
                    break;
                case "type=hard":
                    loadEstimationTable(bMSTable, estimationTableType.hard);
                    break;
                case "type=fc":
                    loadEstimationTable(bMSTable, estimationTableType.fc);
                    break;
                default:
                    throw new ArgumentException(Resources.Error_UnsupportedURI, pageUri.ToString());
            }
        }
        if (baseTable != null)
        {
            bMSTable.symbol = baseTable.symbol;
            bMSTable.ignore_folder_output = baseTable.ignore_folder_output;
            bMSTable.is_external_sync = baseTable.is_external_sync;
            bMSTable.is_root_folder = baseTable.is_root_folder;
            bMSTable.custom_folder_output_base_name = baseTable.custom_folder_output_base_name;
            bMSTable.bmt_sort = baseTable.bmt_sort;
            bMSTable.is_bmt_output = baseTable.is_bmt_output;
        }
        return bMSTable;
    }

    /// <summary>
    /// 発狂難易度推定 JSON と基準表を読み込み、推定表キャッシュを生成します。
    /// </summary>
    /// <exception cref="InvalidOperationException">基準となる外部テーブルの読み込みに失敗した場合。</exception>
    private void setEstimationTable()
    {
        string input = playlistHttpClient.GetString(estimationJsonUri);
        input = workAroundRegex.Replace(input, "\"key${id}\":{");
        dynamic data_json = DynamicJson.Parse(input);
        BMSTable insane = insaneTable ?? throw new InvalidOperationException("Load insane table failed");
        BMSTable bMSTable = overjoyTable ?? throw new InvalidOperationException("Load overjoy table failed");
        var inner = (from e in ((IEnumerable<string>)data_json.GetDynamicMemberNames()).Select(delegate (string item)
            {
                try
                {
                    return new EstimationData
                    {
                        type = (string)data_json[item].type,
                        bmsid = ((data_json[item].IsDefined("bmsid")) ? ((int)data_json[item].bmsid).ToString() : null),
                        hoshi = ((data_json[item].IsDefined("bmsid")) ? new EstimationHoshi
                        {
                            easy = (double?)data_json[item].hoshi.easy,
                            normal = (double?)data_json[item].hoshi.normal,
                            hard = (double?)data_json[item].hoshi.hard,
                            fc = (double?)data_json[item].hoshi.fc
                        } : null)
                    };
                }
                catch
                {
                    return (EstimationData)null;
                }
            })
                     where e != null && e.type != "course"
                     select e).ToList();
        var source = insane.entries.Concat(bMSTable.entries).GroupJoin(inner, e => e.lr2_bmsid, w => w.bmsid, (e, w) =>
        {
            BMSTableEntry bMSTableEntry = e.Duplicate();
            bMSTableEntry.folder = ((e.parent == insane) ? "INSANE " : "Overjoy ") + e.parent.symbol + e.parent.ConvertBackFolderNameToCompatibleLevelName(e.folder);
            return new
            {
                entry = bMSTableEntry,
                estimation = w.DefaultIfEmpty()
            };
        }).ToList();
        _easyEntries = [.. source.SelectMany(grp => grp.estimation, (grp, a) =>
        {
            BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
            if (a != null)
            {
                bMSTableEntry.level = a.hoshi.easy;
            }
            else
            {
                bMSTableEntry.level = null;
            }
            return bMSTableEntry;
        })];
        _normalEntries = [.. source.SelectMany(grp => grp.estimation, (grp, a) =>
        {
            BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
            if (a != null)
            {
                bMSTableEntry.level = a.hoshi.normal;
            }
            else
            {
                bMSTableEntry.level = null;
            }
            return bMSTableEntry;
        })];
        _hardEntries = [.. source.SelectMany(grp => grp.estimation, (grp, a) =>
        {
            BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
            if (a != null)
            {
                bMSTableEntry.level = a.hoshi.hard;
            }
            else
            {
                bMSTableEntry.level = null;
            }
            return bMSTableEntry;
        })];
        _fcEntries = [.. source.SelectMany(grp => grp.estimation, (grp, a) =>
        {
            BMSTableEntry bMSTableEntry = grp.entry.Duplicate();
            if (a != null)
            {
                bMSTableEntry.level = a.hoshi.fc;
            }
            else
            {
                bMSTableEntry.level = null;
            }
            return bMSTableEntry;
        })];
    }

    /// <summary>
    /// 生成済みの推定表キャッシュから指定種類のプレイリストを組み立てます。
    /// </summary>
    /// <param name="table">結果を書き込むプレイリスト。</param>
    /// <param name="type">生成する推定表の種類。</param>
    /// <exception cref="ArgumentException">未対応の種類が指定された場合。</exception>
    private void loadEstimationTable(BMSTable table, estimationTableType type)
    {
        table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
        table.folder_sort_ascending = true;
        table.is_external_sync = true;
        table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
        switch (type)
        {
            case estimationTableType.easy:
                {
                    table.entries = easyEntries;
                    string name = (table.org_name = Resources.InsaneBMSDiffTable_Easy);
                    table.name = name;
                    name = (table.org_symbol = "E★");
                    table.symbol = name;
                    break;
                }
            case estimationTableType.normal:
                {
                    table.entries = normalEntries;
                    string name = (table.org_name = Resources.InsaneBMSDiffTable_Normal);
                    table.name = name;
                    name = (table.org_symbol = "N★");
                    table.symbol = name;
                    break;
                }
            case estimationTableType.hard:
                {
                    table.entries = hardEntries;
                    string name = (table.org_name = Resources.InsaneBMSDiffTable_Hard);
                    table.name = name;
                    name = (table.org_symbol = "H★");
                    table.symbol = name;
                    break;
                }
            case estimationTableType.fc:
                {
                    table.entries = fcEntries;
                    string name = (table.org_name = Resources.InsaneBMSDiffTable_FC);
                    table.name = name;
                    name = (table.org_symbol = "F★");
                    table.symbol = name;
                    break;
                }
            default:
                throw new ArgumentException(string.Format(Resources.Error_UnsupportedType, type), "type");
        }
    }

    /// <summary>
    /// おすすめ表 API を参照し、クリア状況の送信とプレイリスト構築を行います。
    /// </summary>
    /// <param name="table">結果を書き込むプレイリスト。</param>
    /// <param name="baseTable">比較表示や設定引き継ぎに使う既存プレイリスト。</param>
    /// <param name="mode">API の更新モード。</param>
    /// <param name="filter">クリア状況送信時のフィルタ。</param>
    /// <param name="displayName">表示名上書き用の文字列。</param>
    /// <param name="lr2id">対象プレイヤーの LR2 ID。</param>
    /// <param name="baseline">送信時の基準モード。</param>
    /// <exception cref="InvalidOperationException">必要なローカル情報または外部データが取得できない場合。</exception>
    private void loadRecommendedTable(BMSTable table, BMSTable baseTable = null, string mode = null, string filter = null, string displayName = null, int lr2id = 0, string baseline = null)
    {
        string name = string.Empty;
        if (lr2id == 0)
        {
            if (lr2ScoreDBPath == null)
            {
                throw new InvalidOperationException(Resources.Error_ScoreDBConnectionFailed);
            }
            try
            {
                using LR2ScoreDB lR2ScoreDB = new LR2ScoreDBExtended(lr2ScoreDBPath);
                LR2ScoreDB.player player = lR2ScoreDB.Table<LR2ScoreDB.player>().ToList().FirstOrDefault();
                lr2id = player.irid.Value;
                name = player.name;
            }
            catch (Exception)
            {
                lr2id = 0;
            }
        }
        else
        {
            mode = "readonly";
        }
        if (lr2id == 0)
        {
            throw new InvalidOperationException(Resources.Error_LR2IDOrScoreDBFailed);
        }
        if (mode != "readonly")
        {
            try
            {
                RetryHelper.RetryIfError(delegate
                {
                    updatedClearedSongs(mode, lr2id, name, filter, baseline);
                }, delegate (Exception source)
                {
                    ExceptionDispatchInfo.Capture(source).Throw();
                }, delegate
                {
                    Thread.Sleep(100);
                }, 5u);
            }
            catch (Exception ex2)
            {
                DispatcherMessageBox.Show(string.Format(Resources.Warn_RecommendUpdateFailed, ex2.Message), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            }
        }
        var address = new Uri(recommendJsonUriStr + lr2id, UriKind.Absolute);
        dynamic val = DynamicJson.Parse(playlistHttpClient.GetString(address));
        if ((string)val.status != "success")
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_RecommendFetchFailed, (string)val.message), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            throw new InvalidOperationException(Resources.Error_RecommendFetchFailed);
        }
        double num = (double)val.hoshi;
        DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((int)val.last_modified).ToLocalTime();
        name = ((string)val.name).Replace('〜', '～');
        BMSTable insane = insaneTable ?? throw new InvalidOperationException("Load insane table failed");
        BMSTable bMSTable = overjoyTable ?? throw new InvalidOperationException("Load overjoy table failed");
        IEnumerable<BMSTableEntry> inner = from e in insane.entries.Concat(bMSTable.entries)
                                           where !string.IsNullOrWhiteSpace(e.md5) && !string.IsNullOrWhiteSpace(e.lr2_bmsid)
                                           group e by e.md5 into e
                                           select e.FirstOrDefault(f => f.parent == insane) ?? e.First();
        List<BMSTableEntry> entries = [.. (from e in ((object[])val.recommended).Select(delegate (dynamic item)
            {
                try
                {
                    return new RecommendedData
                    {
                        type = (string)item.bms.type,
                        bmsid = ((item.bms.IsDefined("bmsid")) ? ((int)item.bms.bmsid).ToString() : null),
                        new_lamp = (string)item.new_lamp,
                        percent = (double?)item.p
                    };
                }
                catch
                {
                    return (RecommendedData)null;
                }
            })
                                       where e != null && e.type != "course"
                                       select e).ToList().Join(inner, r => r.bmsid, e => e.lr2_bmsid, (r, e) =>
                                   {
                                       BMSTableEntry bMSTableEntry = e.Duplicate();
                                       bMSTableEntry.folder = r.new_lamp.ToUpperInvariant();
                                       bMSTableEntry.level = r.percent;
                                       return bMSTableEntry;
                                   })];
        table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
        table.folder_sort_ascending = false;
        table.is_external_sync = true;
        table.entries = entries;
        table.last_update = DateTime.Parse(dateTime.ToString());
        table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.LevelFolder | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
        table.Folder_order = ["EASY", "NORMAL", "HARD", "FC"];
        string name2 = (table.org_name = string.Format(Resources.RecommendFormat, displayName ?? name, num.ToString("F2")));
        table.name = name2;
        name2 = (table.org_symbol = "R★");
        table.symbol = name2;
        try
        {
            if (baseTable == null || !Settings.Default.ShowRecommUpdatedMsg)
            {
                return;
            }
            Match match = new Regex("★(\\d+(?:\\.\\d+)?)").Match(baseTable.org_name);
            if (match.Success)
            {
                double num2 = double.Parse(match.Groups[1].ToString());
                if (num2 != num)
                {
                    DispatcherMessageBox.Show(string.Format(Resources.Recommend_SkillUpdatedMessage, num.ToString("F2"), (num - num2).ToString(" (+#0.00); (-#0.00);"), dateTime.ToString()), Resources.Recommend_SkillUpdatedTitle, MessageBoxButton.OK, MessageBoxImage.Asterisk, MessageBoxResult.OK);
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// おすすめ表更新 API に送るクリア状況データを組み立てて送信します。
    /// </summary>
    /// <param name="mode">送信モード。</param>
    /// <param name="lr2Id">送信対象プレイヤーの LR2 ID。</param>
    /// <param name="name">送信名義。</param>
    /// <param name="filter">クリア状態フィルタ。</param>
    /// <param name="baseline">未クリア扱いを含めるかを表す基準モード。</param>
    /// <exception cref="InvalidOperationException">必要な基準表やローカルスコアが取得できない場合。</exception>
    private void updatedClearedSongs(string mode, int lr2Id, string name, string filter, string baseline)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == "readonly")
        {
            return;
        }
        BMSTable obj = insaneTable ?? throw new InvalidOperationException("Load insane table failed");
        BMSTable bMSTable = overjoyTable ?? throw new InvalidOperationException("Load overjoy table failed");
        var enumerable = from e in obj.entries.Concat(bMSTable.entries)
                         where !string.IsNullOrWhiteSpace(e.md5) && !string.IsNullOrWhiteSpace(e.lr2_bmsid)
                         group e by e.md5 into g
                         select new
                         {
                             md5 = g.Key,
                             bmsid = g.First().lr2_bmsid
                         };
        initSemaphore?.Wait();
        initSemaphore?.Release();
        List<BMSScore> list = bmsScores() ?? throw new InvalidOperationException(Resources.Error_LocalScoreDataNotFetched);
        int thresh = filter switch
        {
            "hard" => 4,
            "clear" => 3,
            "easy" => 2,
            _ => 1,
        };
        var lampsBMS = (from t in enumerable
                        join s in list on t.md5 equals s.hash
                        select new
                        {
                            t.bmsid,
                            lamp = ClearTypeStorageConverter.ToLr2Value(s.clear),
                            rank = (int)s.rank
                        } into s
                        where s.lamp >= thresh && s.lamp <= 5 && s.rank != 0
                        select s).ToList();
        var lampsGrade = (from t in insaneGrade
                          join s in list on t.Value equals s.hash
                          select new
                          {
                              bmsid = (t.Key + 100000000).ToString(),
                              lamp = ClearTypeStorageConverter.ToLr2Value(s.clear),
                              rank = (int)s.rank
                          } into s
                          where s.lamp >= thresh && s.lamp <= 5 && s.rank != 0
                          select s).ToList();
        if (baseline == "failed")
        {
            lampsBMS = [.. enumerable.Select(t =>
            {
                var anon = lampsBMS.FirstOrDefault(m => m.bmsid == t.bmsid);
                return new
                {
                    t.bmsid,
                    lamp = (anon?.lamp ?? 1),
                    rank = (anon?.rank ?? 0)
                };
            })];
            lampsGrade = [.. insaneGrade.Select(delegate (KeyValuePair<int, string> t)
            {
                var anon = lampsGrade.FirstOrDefault(h => h.bmsid == (t.Key + 100000000).ToString());
                return new
                {
                    bmsid = (t.Key + 100000000).ToString(),
                    lamp = (anon?.lamp ?? 1),
                    rank = (anon?.rank ?? 0)
                };
            })];
        }
        IEnumerable<string> values = from e in lampsBMS.Concat(lampsGrade)
                                     select e.bmsid + "-" + e.lamp;
        _ = playlistHttpClient.PostForm(walkureUpdateUri, new NameValueCollection
        {
            { "name", name },
            {
                "id",
                lr2Id.ToString()
            },
            {
                "data",
                string.Join(",", values)
            }
        });
    }

    /// <summary>
    /// 「その他」系カスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>その他系フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsOtherFolder(BMSTable bmsTable)
    {
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
        if (Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent)
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
        var scopes = new List<CustomFolderEntryScope>
        {
            new()
            {
                IsAll = true,
                Title = (bmsTable?.name ?? string.Empty) + " ALL"
            }
        };
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
        additionalOutputBaseDirsBefore ??= Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
        additionalOutputBaseDirsAfter ??= Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs;
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
            outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable);
    }

    private sealed class BeatorajaBmtTableProjectionInput
    {
        public int Index { get; set; }

        public string PlaylistIdentity { get; set; }

        public string TableName { get; set; }

        public BmtTableExportService.TableDataProjectionSnapshot Snapshot { get; set; }
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
            outputBaseDirPathBeforeByTable: outputBaseDirPathBeforeByTable);
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
        if (!Settings.Default.OperationModeLR2DB)
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
            outputDirPathAfter = GetCustomFolderOutputDirectory(bmsTable);
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "MigrateCustomFolderOutputDirectory");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                migrateCustomFolderOutputDirectoryFiles(
                    bmsTable,
                    outputDirPathBefore,
                    outputDirPathAfter,
                    wasRootFolderBefore ?? bmsTable.is_root_folder,
                    rootOutputBaseDirBefore,
                    outputBaseDirBefore,
                    inferOutputBaseDirBeforeWhenMissing);
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
        if (!Settings.Default.OperationModeLR2DB)
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
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                reOutputCustomFolderFiles(bmsTable);
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
        if (!Settings.Default.OperationModeLR2DB)
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
        CustomFolderBatchOutputResult result = await ReOutputCustomFoldersForTablesCoreAsync(
            tablesSnapshot,
            reason,
            "playlist_lr2_song_db_sync_data_resync",
            forceWriteAllFiles: false,
            throwOnProjectionFailure: false,
            buildPreparedDataSurface: true,
            yieldBetweenTables,
            progressCallback);
        return result.PreparedDataSurface ?? Lr2SongDbSyncPreparedDataSurface.Empty;
    }

    private int RepairMissingCustomFolderOutputsAfterHydration(string reason)
    {
        if (!Settings.Default.OperationModeLR2DB)
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
            + " tableCount=" + tableSnapshot.Count);
        List<CustomFolderOutputPlan> targets = CreateCustomFolderOutputRepairPlans(tableSnapshot, reason);
        targetStopwatch.Stop();
        if (targets.Count == 0)
        {
            stopwatch.Stop();
            LogPlaylistPerformance("playlist_custom_folder_output_repair skipped"
                + " reason=" + (reason ?? "unknown")
                + " tableCount=" + tableSnapshot.Count
                + " targetCount=0"
                + " targetMs=" + targetStopwatch.ElapsedMilliseconds
                + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
            return 0;
        }

        CustomFolderBatchOutputResult result = ReOutputCustomFoldersForTablesCoreAsync(
            targets,
            reason,
            "playlist_custom_folder_output_repair",
            buildPreparedDataSurface: false,
            yieldBetweenTables: false)
            .GetAwaiter()
            .GetResult();
        stopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair summary"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableSnapshot.Count
            + " targetCount=" + targets.Count
            + " reOutputCount=" + result.ReOutputCount
            + " targetMs=" + targetStopwatch.ElapsedMilliseconds
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return result.ReOutputCount;
    }

    private List<CustomFolderOutputPlan> CreateCustomFolderOutputRepairPlans(IReadOnlyList<BMSTable> tablesSnapshot, string reason)
    {
        var statusStopwatch = Stopwatch.StartNew();
        Dictionary<int, CustomFolderOutputStatusRow> statusRows = ReadCustomFolderOutputStatusRows();
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
                string outputDirectory = GetCustomFolderOutputDirectory(table);
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
                && IsCustomFolderOutputStatusConfigCurrent(candidate.Table, candidate.OutputDirectory, status))
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
                CreateCustomFolderPhysicalMtimeSignatureIndex(
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
        foreach (CustomFolderOutputRepairCandidate candidate in physicalCheckCandidates)
        {
            int? playlistId = candidate?.Table?.playlist_id;
            if (playlistId.HasValue
                && physicalCheckStatuses.TryGetValue(playlistId.Value, out CustomFolderOutputStatusRow status)
                && IsCustomFolderOutputStatusPhysicalCurrent(candidate.OutputDirectory, status, physicalSignatureIndex))
            {
                currentStatusCount++;
                continue;
            }

            pendingCandidates.Add(candidate);
        }
        filterStopwatch.Stop();
        LogPlaylistPerformance("playlist_custom_folder_output_repair status_filter_done"
            + " reason=" + (reason ?? "unknown")
            + " candidateCount=" + candidates.Count
            + " statusRowCount=" + statusRows.Count
            + " currentStatusCount=" + currentStatusCount
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
                    pendingProjections.Add(CreateCustomFolderOutputLayoutProjection(candidate.Table, candidate.OutputDirectory));
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
        AssignCustomFolderProtectedOutputDirectories(pendingProjections);
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
        var plans = new List<CustomFolderOutputPlan>();
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
                    fullProjection = CreateCustomFolderOutputProjection(projection.Table, includeText: true);
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
            plans.Add(new CustomFolderOutputPlan
            {
                Table = fullProjection.Table,
                Projection = fullProjection
            });
        }
        if (verifiedCurrentProjections.Count > 0)
        {
            PersistCustomFolderOutputStatuses(
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
            using var lr2Song = new LR2SongDBExtended(lr2SongDBPath);
            IEnumerable<LR2SongDB.folder> exactRows = layoutOnly
                ? Lr2FolderExistingRowLookup.QueryExactPathsForCustomFolderLayout(lr2Song, exactPaths)
                : Lr2FolderExistingRowLookup.QueryExactPaths(lr2Song, exactPaths);
            IEnumerable<LR2SongDB.folder> scopeRows = layoutOnly
                ? Lr2FolderExistingRowLookup.QueryCustomFolderLayoutPathPrefixScopes(lr2Song, scopePaths)
                : Lr2FolderExistingRowLookup.QueryPathPrefixScopes(lr2Song, scopePaths);
            return exactRows
                .Concat(scopeRows)
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
                string databasePath = ResolveCustomFolderDatabasePath(projection.Table, rowPath);
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

    private static IReadOnlyCollection<string> CreateCustomFolderOutputRowScopePaths(BMSTable table, string outputDirectory)
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
        string databasePath = ResolveCustomFolderDatabasePath(table, physicalPath ?? outputDirectory);
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
            foreach (string scopePath in CreateCustomFolderOutputRowScopePaths(projection.Table, protectedDirectory))
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
            providedSurface = CustomFolderOutputPhysicalSurfaceProvider?.Invoke();
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

    private static CustomFolderOutputPhysicalMtimeSignatureIndex CreateCustomFolderPhysicalMtimeSignatureIndex(
        IEnumerable<string> outputDirectories,
        CustomFolderOutputPhysicalSurface physicalSurface,
        IEnumerable<string> ownerBoundaryDirectories = null)
    {
        if (physicalSurface?.DiscoveryComplete != true)
        {
            return CustomFolderOutputPhysicalMtimeSignatureIndex.Incomplete;
        }

        var builders = new Dictionary<string, CustomFolderOutputPhysicalMtimeSignatureBuilder>(StringComparer.OrdinalIgnoreCase);
        foreach (string outputDirectory in outputDirectories ?? [])
        {
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(outputDirectory);
            if (!string.IsNullOrWhiteSpace(normalizedDirectory) && !builders.ContainsKey(normalizedDirectory))
            {
                builders[normalizedDirectory] = new CustomFolderOutputPhysicalMtimeSignatureBuilder(normalizedDirectory);
            }
        }
        if (builders.Count == 0)
        {
            return new CustomFolderOutputPhysicalMtimeSignatureIndex(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                discoveryComplete: true);
        }

        IEnumerable<string> ownerDirectories = ownerBoundaryDirectories ?? builders.Keys;
        var ownerResolver = new CustomFolderOutputOwnerResolver(ownerDirectories);
        foreach (RootFileEnumerationEntry entry in (physicalSurface.FileEntries?.Values ?? [])
            .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Path))
            .OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase))
        {
            string normalizedFile = CustomFolderOutputPhysicalSurface.NormalizeFilePath(entry.Path);
            if (string.IsNullOrWhiteSpace(normalizedFile)
                || !ownerResolver.TryFindOwner(normalizedFile, out string ownerDirectory)
                || !builders.TryGetValue(ownerDirectory, out CustomFolderOutputPhysicalMtimeSignatureBuilder builder))
            {
                continue;
            }

            builder.AddFile(normalizedFile, entry.LastWriteTimeUtc);
        }

        var signatures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderOutputPhysicalMtimeSignatureBuilder builder in builders.Values)
        {
            if (builder.TryBuild(out string signature))
            {
                signatures[builder.OutputDirectory] = signature;
            }
        }

        return new CustomFolderOutputPhysicalMtimeSignatureIndex(signatures, discoveryComplete: true);
    }

    private static bool TryCreateCustomFolderPhysicalMtimeSignature(
        string outputDirectory,
        CustomFolderOutputPhysicalSurface physicalSurface,
        out string signature)
    {
        CustomFolderOutputPhysicalMtimeSignatureIndex index = CreateCustomFolderPhysicalMtimeSignatureIndex(
            [outputDirectory],
            physicalSurface);
        return index.TryGetSignature(outputDirectory, out signature);
    }

    private static IReadOnlyCollection<string> CreateCustomFolderExpectedDirectoryMetadataTargets(
        CustomFolderOutputProjection projection)
    {
        if (projection == null || string.IsNullOrWhiteSpace(projection.OutputDirectory))
        {
            return [];
        }

        IReadOnlyCollection<string> directoryRowGenerationScopes =
            CreateCustomFolderDirectoryRowGenerationScopes(projection.OutputDirectory, projection.Table);
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
                ApplyCustomFolderSourceClassification(item, projection.Table);
                return item;
            })];
    }

    private static CustomFolderOutputPhysicalSurface CreateCustomFolderOutputPhysicalSurfaceFromSyncItems(
        IEnumerable<Lr2FolderFileSyncItem> syncItems)
    {
        return CustomFolderOutputPhysicalSurface.FromEntries(
            (syncItems ?? [])
                .Where(item => !string.IsNullOrWhiteSpace(item?.FilePath))
                .Select(item => new RootFileEnumerationEntry(item.FilePath, item.LastWriteTimeUtc)),
            discoveryComplete: true);
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
                rowPath = ResolveCustomFolderDatabasePath(projection.Table, rowPath ?? directory);
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
            MarkAllCustomFolderOutputFilesForWrite(projection);
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
                projection.ForceWriteFilePaths.Add(file.FilePath);
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
        ApplyCustomFolderSourceClassification(item, projection.Table);
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

    private static string ResolveCustomFolderDatabasePath(BMSTable table, string filePath)
    {
        var item = new Lr2FolderFileSyncItem
        {
            FilePath = filePath
        };
        ApplyCustomFolderSourceClassification(item, table);
        return NormalizeCustomFolderRowPath(item.DatabasePath ?? item.FilePath);
    }

    private sealed class CustomFolderOutputPlan
    {
        public BMSTable Table { get; set; }

        public CustomFolderOutputProjection Projection { get; set; }

        public bool ProjectionFailed { get; set; }
    }

    private async Task<CustomFolderBatchOutputResult> ReOutputCustomFoldersForTablesCoreAsync(
        IReadOnlyList<BMSTable> tablesSnapshot,
        string reason,
        string operation,
        bool forceWriteAllFiles,
        bool throwOnProjectionFailure,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Action<int, int, string> progressCallback = null)
    {
        tablesSnapshot ??= [];
        int progressTotalCount = GetCustomFolderBatchProgressTotalCount(tablesSnapshot.Count, progressCallback);
        int reOutputCount = 0;
        int projectionFailedCount = 0;
        var stopwatch = Stopwatch.StartNew();
        var projectionStopwatch = Stopwatch.StartNew();
        var projections = new List<CustomFolderOutputProjection>();
        LogPlaylistPerformance(operation + " start"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tablesSnapshot.Count);
        for (int index = 0; index < tablesSnapshot.Count; index++)
        {
            if (yieldBetweenTables)
            {
                await Task.Yield();
            }

            BMSTable table = tablesSnapshot[index];
            var tableStopwatch = Stopwatch.StartNew();
            LogPlaylistPerformance(operation + " table_start"
                + " reason=" + (reason ?? "unknown")
                + " index=" + (index + 1)
                + " total=" + tablesSnapshot.Count
                + " name=" + QuoteLogValue(table?.name));
            if (table == null || string.IsNullOrWhiteSpace(table.Output_dir))
            {
                tableStopwatch.Stop();
                progressCallback?.Invoke(index + 1, progressTotalCount, table?.name ?? string.Empty);
                LogPlaylistPerformance(operation + " table_skipped"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (index + 1)
                    + " total=" + tablesSnapshot.Count
                    + " name=" + QuoteLogValue(table?.name)
                    + " detail=no_output_dir"
                    + " elapsedMs=" + tableStopwatch.ElapsedMilliseconds);
                continue;
            }

            try
            {
                EnsurePlaylistEntriesLoaded(table, "ReOutputCustomFoldersForTablesCoreAsync");
                using (table.ReaderWriterLock.GetReaderGuard())
                {
                    CustomFolderOutputProjection projection = CreateCustomFolderOutputProjection(table);
                    if (forceWriteAllFiles)
                    {
                        MarkAllCustomFolderOutputFilesForWrite(projection);
                    }
                    projections.Add(projection);
                }
                reOutputCount++;
                tableStopwatch.Stop();
                progressCallback?.Invoke(index + 1, progressTotalCount, table.name ?? string.Empty);
                LogPlaylistPerformance(operation + " table_done"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (index + 1)
                    + " total=" + tablesSnapshot.Count
                    + " name=" + QuoteLogValue(table.name)
                    + " elapsedMs=" + tableStopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
            {
                projectionFailedCount++;
                tableStopwatch.Stop();
                progressCallback?.Invoke(index + 1, progressTotalCount, table.name ?? string.Empty);
                LogPlaylistPerformance(operation + " table_skipped"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (index + 1)
                    + " total=" + tablesSnapshot.Count
                    + " name=" + QuoteLogValue(table.name)
                    + " detail=projection_failed"
                    + " exception=" + QuoteLogValue(ex.GetType().Name)
                    + " elapsedMs=" + tableStopwatch.ElapsedMilliseconds);
                if (throwOnProjectionFailure)
                {
                    ExceptionDispatchInfo.Capture(ex).Throw();
                }
            }
        }
        projectionStopwatch.Stop();
        return CompleteCustomFolderBatchOutput(
            projections,
            tablesSnapshot.Count,
            projectionFailedCount,
            reOutputCount,
            reason,
            operation,
            stopwatch,
            projectionStopwatch.ElapsedMilliseconds,
            buildPreparedDataSurface,
            progressCallback);
    }

    private async Task<CustomFolderBatchOutputResult> ReOutputCustomFoldersForTablesCoreAsync(
        IReadOnlyList<CustomFolderOutputPlan> tablePlansSnapshot,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Action<int, int, string> progressCallback = null)
    {
        return await ReOutputCustomFoldersForPlansCoreAsync(
            tablePlansSnapshot,
            reason,
            operation,
            buildPreparedDataSurface,
            yieldBetweenTables,
            progressCallback);
    }

    private async Task<CustomFolderBatchOutputResult> ReOutputCustomFoldersForPlansCoreAsync(
        IReadOnlyList<CustomFolderOutputPlan> tablePlansSnapshot,
        string reason,
        string operation,
        bool buildPreparedDataSurface,
        bool yieldBetweenTables,
        Action<int, int, string> progressCallback = null)
    {
        tablePlansSnapshot ??= [];
        int progressTotalCount = GetCustomFolderBatchProgressTotalCount(tablePlansSnapshot.Count, progressCallback);
        int reOutputCount = 0;
        var stopwatch = Stopwatch.StartNew();
        var projectionStopwatch = Stopwatch.StartNew();
        var projections = new List<CustomFolderOutputProjection>();
        int projectionFailedCount = tablePlansSnapshot.Count(plan => plan?.ProjectionFailed == true);
        LogPlaylistPerformance(operation + " start"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tablePlansSnapshot.Count);
        for (int index = 0; index < tablePlansSnapshot.Count; index++)
        {
            if (yieldBetweenTables)
            {
                await Task.Yield();
            }

            CustomFolderOutputPlan plan = tablePlansSnapshot[index];
            BMSTable table = plan?.Table;
            if (plan?.ProjectionFailed == true)
            {
                progressCallback?.Invoke(index + 1, progressTotalCount, table?.name ?? string.Empty);
                LogPlaylistPerformance(operation + " table_skipped"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (index + 1)
                    + " total=" + tablePlansSnapshot.Count
                    + " name=" + QuoteLogValue(table?.name)
                    + " detail=projection_failed");
                continue;
            }
            var tableStopwatch = Stopwatch.StartNew();
            LogPlaylistPerformance(operation + " table_start"
                + " reason=" + (reason ?? "unknown")
                + " index=" + (index + 1)
                + " total=" + tablePlansSnapshot.Count
                + " name=" + QuoteLogValue(table?.name));
            CustomFolderOutputProjection projection = plan?.Projection;
            if (projection == null)
            {
                try
                {
                    EnsurePlaylistEntriesLoaded(table, "ReOutputAllCustomFoldersForLr2SongDbSync");
                    using (table.ReaderWriterLock.GetReaderGuard())
                    {
                        projection = CreateCustomFolderOutputProjection(table);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
                {
                    projectionFailedCount++;
                    tableStopwatch.Stop();
                    progressCallback?.Invoke(index + 1, progressTotalCount, table?.name ?? string.Empty);
                    LogPlaylistPerformance(operation + " table_skipped"
                        + " reason=" + (reason ?? "unknown")
                        + " index=" + (index + 1)
                        + " total=" + tablePlansSnapshot.Count
                        + " name=" + QuoteLogValue(table?.name)
                        + " detail=projection_failed"
                        + " exception=" + QuoteLogValue(ex.GetType().Name)
                        + " elapsedMs=" + tableStopwatch.ElapsedMilliseconds);
                    continue;
                }
            }
            projections.Add(projection);
            reOutputCount++;
            tableStopwatch.Stop();
            progressCallback?.Invoke(index + 1, progressTotalCount, table?.name ?? string.Empty);
            LogPlaylistPerformance(operation + " table_done"
                + " reason=" + (reason ?? "unknown")
                + " index=" + (index + 1)
                + " total=" + tablePlansSnapshot.Count
                + " name=" + QuoteLogValue(table?.name)
                + " elapsedMs=" + tableStopwatch.ElapsedMilliseconds);
        }
        projectionStopwatch.Stop();
        return CompleteCustomFolderBatchOutput(
            projections,
            tablePlansSnapshot.Count,
            projectionFailedCount,
            reOutputCount,
            reason,
            operation,
            stopwatch,
            projectionStopwatch.ElapsedMilliseconds,
            buildPreparedDataSurface,
            progressCallback);
    }

    private CustomFolderBatchOutputResult CompleteCustomFolderBatchOutput(
        IReadOnlyList<CustomFolderOutputProjection> projections,
        int tableCount,
        int projectionFailedCount,
        int reOutputCount,
        string reason,
        string operation,
        Stopwatch stopwatch,
        long projectionMs,
        bool buildPreparedDataSurface,
        Action<int, int, string> progressCallback = null)
    {
        projections ??= [];
        int progressTotalCount = GetCustomFolderBatchProgressTotalCount(tableCount, progressCallback);
        progressCallback?.Invoke(Math.Min(tableCount, progressTotalCount), progressTotalCount, Resources.Custom_folder_output_progress_single_label);
        LogPlaylistPerformance(operation + " materialize_start"
            + " reason=" + (reason ?? "unknown")
            + " projectionCount=" + projections.Count);
        var materializeStopwatch = Stopwatch.StartNew();
        CustomFolderBatchMaterializationResult materialization = MaterializeCustomFolderOutputBatch(
            projections,
            delegate (int completedProjectionCount, int projectionCount, string tableName)
            {
                int completed = Math.Min(tableCount + completedProjectionCount, progressTotalCount);
                progressCallback?.Invoke(completed, progressTotalCount, tableName ?? Resources.Custom_folder_output_progress_single_label);
            },
            operation,
            reason);
        materializeStopwatch.Stop();
        LogPlaylistPerformance(operation + " materialize_done"
            + " reason=" + (reason ?? "unknown")
            + " projectionCount=" + projections.Count
            + " outputDirCount=" + materialization.OutputDirectories.Count
            + " syncItemCount=" + materialization.SyncItems.Count
            + " writtenFiles=" + materialization.WrittenFileCount
            + " unchangedFiles=" + materialization.UnchangedFileCount
            + " deletedFiles=" + materialization.DeletedFileCount
            + " elapsedMs=" + materializeStopwatch.ElapsedMilliseconds);

        progressCallback?.Invoke(Math.Min(tableCount + projections.Count, progressTotalCount), progressTotalCount, Resources.Custom_folder_db_sync_progress_single_label);
        LogPlaylistPerformance(operation + " sync_start"
            + " reason=" + (reason ?? "unknown")
            + " outputDirCount=" + materialization.OutputDirectories.Count
            + " syncItemCount=" + materialization.SyncItems.Count);
        var syncStopwatch = Stopwatch.StartNew();
        Lr2FolderFileDbSyncResult syncResult = SyncCustomFolderRowsBatch(
            materialization.OutputDirectories,
            materialization.OutputRowScopeDirectories,
            materialization.SyncItems,
            materialization.DirectoryRowGenerationScopeDirectories,
            materialization.DirectoryEntries,
            materialization.PruneScopePaths,
            materialization.PruneExcludedDirectories,
            materialization.EmptyOutputDirectories);
        syncStopwatch.Stop();
        LogPlaylistPerformance(operation + " sync_done"
            + " reason=" + (reason ?? "unknown")
            + " syncUpserted=" + (syncResult?.UpsertedCount ?? 0)
            + " syncDeleted=" + (syncResult?.DeletedCount ?? 0)
            + " elapsedMs=" + syncStopwatch.ElapsedMilliseconds);
        progressCallback?.Invoke(progressTotalCount, progressTotalCount, Resources.Custom_folder_db_sync_progress_single_label);

        var statusStopwatch = Stopwatch.StartNew();
        PersistCustomFolderOutputStatuses(
            projections,
            CreateCustomFolderOutputPhysicalSurfaceFromSyncItems(materialization.SyncItems));
        statusStopwatch.Stop();
        LogPlaylistPerformance(operation + " status_persist_done"
            + " reason=" + (reason ?? "unknown")
            + " projectionCount=" + projections.Count
            + " elapsedMs=" + statusStopwatch.ElapsedMilliseconds);

        var preparedSurfaceStopwatch = Stopwatch.StartNew();
        Lr2SongDbSyncPreparedDataSurface preparedDataSurface = buildPreparedDataSurface
            ? Lr2SongDbSyncPreparedDataSurface.FromSyncItems(
                materialization.Lr2FolderSurfaceScopeDirectories,
                materialization.SyncItems,
                directoryEntries: materialization.DirectoryEntries,
                discoveryComplete: projectionFailedCount == 0)
            : Lr2SongDbSyncPreparedDataSurface.Empty;
        preparedSurfaceStopwatch.Stop();
        stopwatch.Stop();
        LogPlaylistPerformance(operation + " done"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableCount
            + " projectionFailedCount=" + projectionFailedCount
            + " reOutputCount=" + reOutputCount
            + " outputDirCount=" + materialization.OutputDirectories.Count
            + " syncItemCount=" + materialization.SyncItems.Count
            + " writtenFiles=" + materialization.WrittenFileCount
            + " unchangedFiles=" + materialization.UnchangedFileCount
            + " deletedFiles=" + materialization.DeletedFileCount
            + " syncUpserted=" + (syncResult?.UpsertedCount ?? 0)
            + " syncDeleted=" + (syncResult?.DeletedCount ?? 0)
            + " projectionMs=" + projectionMs
            + " materializeMs=" + materializeStopwatch.ElapsedMilliseconds
            + " syncMs=" + syncStopwatch.ElapsedMilliseconds
            + " statusPersistMs=" + statusStopwatch.ElapsedMilliseconds
            + " preparedSurfaceMs=" + preparedSurfaceStopwatch.ElapsedMilliseconds
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
        return new CustomFolderBatchOutputResult
        {
            ReOutputCount = reOutputCount,
            Materialization = materialization,
            SyncResult = syncResult,
            PreparedDataSurface = preparedDataSurface,
            ElapsedMs = stopwatch.ElapsedMilliseconds
        };
    }

    private static int GetCustomFolderBatchProgressTotalCount(int tableCount, Action<int, int, string> progressCallback)
    {
        return progressCallback == null ? tableCount : (Math.Max(tableCount, 0) * 2) + 1;
    }

    private static void MarkAllCustomFolderOutputFilesForWrite(CustomFolderOutputProjection projection)
    {
        if (projection == null)
        {
            return;
        }

        foreach (CustomFolderOutputFileProjection file in projection.Files ?? [])
        {
            if (!string.IsNullOrWhiteSpace(file?.FilePath))
            {
                projection.ForceWriteFilePaths.Add(file.FilePath);
            }
        }
    }

    private sealed class CustomFolderBatchOutputResult
    {
        public int ReOutputCount { get; set; }

        public CustomFolderBatchMaterializationResult Materialization { get; set; }

        public Lr2FolderFileDbSyncResult SyncResult { get; set; }

        public Lr2SongDbSyncPreparedDataSurface PreparedDataSurface { get; set; }

        public long ElapsedMs { get; set; }
    }

    private sealed class CustomFolderOutputStatusRow
    {
        public int PlaylistId { get; set; }

        public string OutputDirectory { get; set; }

        public int IsRootFolder { get; set; }

        public int IgnoreFolderOutput { get; set; }

        public int EntryType { get; set; }

        public int FolderSortKey { get; set; }

        public int FolderSortAscending { get; set; }

        public int EnableUnsent { get; set; }

        public string HeaderSha256 { get; set; }

        public string DataSha256 { get; set; }

        public long LastUpdateTicks { get; set; }

        public string PhysicalMtimeSignature { get; set; }
    }

    private sealed class CustomFolderOutputStatusColumnRow
    {
        public string name { get; set; }
    }

    private sealed class CustomFolderOutputProjection
    {
        public BMSTable Table { get; set; }

        public string OutputDirectory { get; set; }

        public IReadOnlyCollection<string> OutputRowScopePaths { get; set; } = [];

        public IReadOnlyCollection<string> ProtectedOutputDirectories { get; set; } = [];

        public IReadOnlyList<CustomFolderOutputFileProjection> Files { get; set; } = [];

        public HashSet<string> ForceWriteFilePaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        public CustomFolderOutputPhysicalSurface PhysicalSurface { get; set; }
    }

    private sealed class CustomFolderOutputRepairCandidate
    {
        public BMSTable Table { get; set; }

        public string OutputDirectory { get; set; }
    }

    private sealed class CustomFolderOutputPhysicalMtimeSignatureIndex(
        IReadOnlyDictionary<string, string> signatures,
        bool discoveryComplete)
    {
        public static CustomFolderOutputPhysicalMtimeSignatureIndex Incomplete { get; } = new(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            discoveryComplete: false);

        private IReadOnlyDictionary<string, string> Signatures { get; } =
            signatures ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public bool DiscoveryComplete { get; } = discoveryComplete;

        public int SignatureCount => Signatures.Count;

        public bool TryGetSignature(string outputDirectory, out string signature)
        {
            signature = null;
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(outputDirectory);
            return DiscoveryComplete
                && !string.IsNullOrWhiteSpace(normalizedDirectory)
                && Signatures.TryGetValue(normalizedDirectory, out signature);
        }
    }

    private sealed class CustomFolderOutputOwnerResolver
    {
        private const string NoOwner = "";

        private readonly HashSet<string> outputDirectories;

        private readonly Dictionary<string, string> ownerByDirectory = new(StringComparer.OrdinalIgnoreCase);

        public CustomFolderOutputOwnerResolver(IEnumerable<string> outputDirectories)
        {
            this.outputDirectories = new HashSet<string>(
                (outputDirectories ?? [])
                    .Select(Lr2FolderPath.NormalizeDirectoryPath)
                    .Where(directory => !string.IsNullOrWhiteSpace(directory)),
                StringComparer.OrdinalIgnoreCase);
        }

        public bool TryFindOwner(string filePath, out string ownerDirectory)
        {
            ownerDirectory = null;
            string directory = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(filePath));
            if (string.IsNullOrWhiteSpace(directory) || outputDirectories.Count == 0)
            {
                return false;
            }

            if (ownerByDirectory.TryGetValue(directory, out string cachedOwner))
            {
                ownerDirectory = string.Equals(cachedOwner, NoOwner, StringComparison.Ordinal)
                    ? null
                    : cachedOwner;
                return ownerDirectory != null;
            }

            var visitedDirectories = new List<string>();
            string current = directory;
            string resolvedOwner = null;
            while (!string.IsNullOrWhiteSpace(current))
            {
                if (ownerByDirectory.TryGetValue(current, out cachedOwner))
                {
                    resolvedOwner = string.Equals(cachedOwner, NoOwner, StringComparison.Ordinal)
                        ? null
                        : cachedOwner;
                    break;
                }

                visitedDirectories.Add(current);
                if (outputDirectories.Contains(current))
                {
                    resolvedOwner = current;
                    break;
                }

                string parent = Lr2FolderPath.SafeGetParentNormalizedDirectory(current);
                if (string.IsNullOrWhiteSpace(parent)
                    || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }

            string cacheValue = resolvedOwner ?? NoOwner;
            foreach (string visitedDirectory in visitedDirectories)
            {
                ownerByDirectory[visitedDirectory] = cacheValue;
            }

            ownerDirectory = resolvedOwner;
            return ownerDirectory != null;
        }
    }

    private sealed class CustomFolderOutputPhysicalMtimeSignatureBuilder(string outputDirectory)
    {
        private readonly StringBuilder builder = new();

        private readonly HashSet<string> parentDirectories = new(StringComparer.OrdinalIgnoreCase);

        public string OutputDirectory { get; } = outputDirectory;

        public void AddFile(string filePath, DateTime? lastWriteTimeUtc)
        {
            string normalizedFile = CustomFolderOutputPhysicalSurface.NormalizeFilePath(filePath);
            if (string.IsNullOrWhiteSpace(normalizedFile))
            {
                return;
            }

            builder
                .Append("F\t")
                .Append(normalizedFile)
                .Append('\t')
                .Append(lastWriteTimeUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "missing")
                .Append('\n');
            string parent = Lr2FolderPath.NormalizeDirectoryPath(Lr2FolderPath.SafeGetDirectoryName(normalizedFile));
            while (!string.IsNullOrWhiteSpace(parent))
            {
                parentDirectories.Add(parent);
                if (string.Equals(parent, OutputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                string next = Lr2FolderPath.SafeGetParentNormalizedDirectory(parent);
                if (string.IsNullOrWhiteSpace(next)
                    || string.Equals(next, parent, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
                parent = next;
            }
        }

        public bool TryBuild(out string signature)
        {
            signature = null;
            string normalizedOutputDirectory = Lr2FolderPath.NormalizeDirectoryPath(OutputDirectory);
            if (string.IsNullOrWhiteSpace(normalizedOutputDirectory))
            {
                return false;
            }

            AppendDirectoryLine(builder, "O", normalizedOutputDirectory);
            foreach (string directory in parentDirectories.OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(directory, normalizedOutputDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    AppendDirectoryLine(builder, "D", directory);
                }
            }

            signature = BMSTable.ComputeSha256Hex(builder.ToString());
            return true;
        }

        private static void AppendDirectoryLine(StringBuilder builder, string kind, string directory)
        {
            RootFileEnumerationEntry entry = RootFileEnumerationEntry.FromDirectoryInfo(directory);
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(entry?.Path ?? directory);
            if (string.IsNullOrWhiteSpace(normalizedDirectory))
            {
                return;
            }

            builder
                .Append(kind)
                .Append('\t')
                .Append(normalizedDirectory)
                .Append('\t')
                .Append(entry?.LastWriteTimeUtc?.Ticks.ToString(CultureInfo.InvariantCulture) ?? "missing")
                .Append('\n');
        }
    }

    private sealed class CustomFolderOutputFileProjection
    {
        public int Index { get; set; }

        public string RelativeDirectory { get; set; }

        public string Text { get; set; }

        public Lr2FolderFileDefinition Definition { get; set; }

        public string FilePath { get; set; }

        public string DatabasePath { get; set; }
    }

    private Dictionary<int, CustomFolderOutputStatusRow> ReadCustomFolderOutputStatusRows()
    {
        if (string.IsNullOrWhiteSpace(lr2SongDBPath))
        {
            return [];
        }

        try
        {
            using var db = new LR2SongDBExtended(lr2SongDBPath);
            EnsureCustomFolderOutputStatusTable(db);
            return db.Query<CustomFolderOutputStatusRow>(
                "SELECT "
                + "playlist_id AS PlaylistId,"
                + "output_directory AS OutputDirectory,"
                + "is_root_folder AS IsRootFolder,"
                + "ignore_folder_output AS IgnoreFolderOutput,"
                + "entry_type AS EntryType,"
                + "folder_sort_key AS FolderSortKey,"
                + "folder_sort_ascending AS FolderSortAscending,"
                + "enable_unsent AS EnableUnsent,"
                + "header_sha256 AS HeaderSha256,"
                + "data_sha256 AS DataSha256,"
                + "last_update_ticks AS LastUpdateTicks,"
                + "physical_mtime_signature AS PhysicalMtimeSignature "
                + "FROM playlist_custom_folder_output_status;")
                .Where(row => row != null)
                .GroupBy(row => row.PlaylistId)
                .ToDictionary(group => group.Key, group => group.First());
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
        {
            LogPlaylistPerformance("playlist_custom_folder_output_status read_failed"
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
            return [];
        }
    }

    private void PersistCustomFolderOutputStatuses(
        IEnumerable<CustomFolderOutputProjection> projections,
        CustomFolderOutputPhysicalSurface physicalSurface = null,
        IEnumerable<string> ownerBoundaryDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(lr2SongDBPath))
        {
            return;
        }

        List<CustomFolderOutputProjection> projectionList = [.. (projections ?? [])
            .Where(projection => projection?.Table?.playlist_id != null)];
        if (projectionList.Count == 0)
        {
            return;
        }

        try
        {
            using var db = new LR2SongDBExtended(lr2SongDBPath);
            EnsureCustomFolderOutputStatusTable(db);
            string savepoint = db.SaveTransactionPoint();
            try
            {
                CustomFolderOutputPhysicalMtimeSignatureIndex physicalSignatureIndex =
                    CreateCustomFolderPhysicalMtimeSignatureIndex(
                        projectionList.Select(projection => projection.OutputDirectory),
                        physicalSurface ?? projectionList.FirstOrDefault(projection => projection.PhysicalSurface != null)?.PhysicalSurface,
                        ownerBoundaryDirectories);
                foreach (CustomFolderOutputProjection projection in projectionList)
                {
                    BMSTable table = projection.Table;
                    if (physicalSignatureIndex.TryGetSignature(projection.OutputDirectory, out string physicalMtimeSignature) != true)
                    {
                        continue;
                    }

                    db.Execute(
                        "INSERT OR REPLACE INTO playlist_custom_folder_output_status ("
                        + "playlist_id, output_directory, is_root_folder, ignore_folder_output, entry_type, folder_sort_key, folder_sort_ascending, enable_unsent, header_sha256, data_sha256, last_update_ticks, physical_mtime_signature"
                        + ") VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?);",
                        table.playlist_id.Value,
                        NormalizeCustomFolderStatusPath(projection.OutputDirectory),
                        table.is_root_folder ? 1 : 0,
                        (int)table.ignore_folder_output,
                        (int)table.entry_type,
                        (int)table.folder_sort_key,
                        table.folder_sort_ascending ? 1 : 0,
                        Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent ? 1 : 0,
                        table.header_sha256 ?? string.Empty,
                        table.data_sha256 ?? string.Empty,
                        table.last_update.Ticks,
                        physicalMtimeSignature);
                }
                db.Commit();
            }
            catch
            {
                db.RollbackTo(savepoint);
                throw;
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
        {
            LogPlaylistPerformance("playlist_custom_folder_output_status write_failed"
                + " projectionCount=" + projectionList.Count
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
        }
    }

    private void DeleteCustomFolderOutputStatus(BMSTable table)
    {
        if (string.IsNullOrWhiteSpace(lr2SongDBPath) || table?.playlist_id == null)
        {
            return;
        }

        try
        {
            using var db = new LR2SongDBExtended(lr2SongDBPath);
            EnsureCustomFolderOutputStatusTable(db);
            db.Execute(
                "DELETE FROM playlist_custom_folder_output_status WHERE playlist_id = ?;",
                table.playlist_id.Value);
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException || ex is SQLiteException)
        {
            LogPlaylistPerformance("playlist_custom_folder_output_status delete_failed"
                + " playlistId=" + (table.playlist_id?.ToString(CultureInfo.InvariantCulture) ?? "null")
                + " exception=" + QuoteLogValue(ex.GetType().Name)
                + " message=" + QuoteLogValue(ex.Message));
        }
    }

    private static bool IsCustomFolderOutputStatusConfigCurrent(
        BMSTable table,
        string outputDirectory,
        CustomFolderOutputStatusRow status)
    {
        if (table?.playlist_id == null || status == null)
        {
            return false;
        }

        return status.PlaylistId == table.playlist_id.Value
            && string.Equals(status.OutputDirectory, NormalizeCustomFolderStatusPath(outputDirectory), StringComparison.OrdinalIgnoreCase)
            && status.IsRootFolder == (table.is_root_folder ? 1 : 0)
            && status.IgnoreFolderOutput == (int)table.ignore_folder_output
            && status.EntryType == (int)table.entry_type
            && status.FolderSortKey == (int)table.folder_sort_key
            && status.FolderSortAscending == (table.folder_sort_ascending ? 1 : 0)
            && status.EnableUnsent == (Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent ? 1 : 0)
            && string.Equals(status.HeaderSha256 ?? string.Empty, table.header_sha256 ?? string.Empty, StringComparison.Ordinal)
            && string.Equals(status.DataSha256 ?? string.Empty, table.data_sha256 ?? string.Empty, StringComparison.Ordinal)
            && status.LastUpdateTicks == table.last_update.Ticks;
    }

    private static bool IsCustomFolderOutputStatusPhysicalCurrent(
        string outputDirectory,
        CustomFolderOutputStatusRow status,
        CustomFolderOutputPhysicalMtimeSignatureIndex physicalSignatureIndex)
    {
        if (status == null
            || physicalSignatureIndex?.TryGetSignature(outputDirectory, out string physicalMtimeSignature) != true)
        {
            return false;
        }

        return string.Equals(status.PhysicalMtimeSignature ?? string.Empty, physicalMtimeSignature, StringComparison.Ordinal);
    }

    private static string NormalizeCustomFolderStatusPath(string path)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(path);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }

    private sealed class CustomFolderDefinition
    {
        public string RelativeDirectory { get; set; }

        public string Text { get; set; }

        public Lr2FolderFileDefinition ParsedDefinition { get; set; }

        public bool IsRandomVariant { get; set; }
    }

    private sealed class CustomFolderEntryScope
    {
        public string FolderName { get; set; }

        public string Title { get; set; }

        public bool IsAll { get; set; }
    }

    private sealed class CustomFolderBatchMaterializationResult
    {
        public List<string> OutputDirectories { get; } = [];

        public List<string> OutputRowScopeDirectories { get; } = [];

        public List<string> DirectoryRowGenerationScopeDirectories { get; } = [];

        public List<string> Lr2FolderSurfaceScopeDirectories { get; } = [];

        public List<Lr2FolderFileSyncItem> SyncItems { get; } = [];

        public Dictionary<string, RootFileEnumerationEntry> DirectoryEntries { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public int WrittenFileCount { get; set; }

        public int UnchangedFileCount { get; set; }

        public int DeletedFileCount { get; set; }

        public List<string> PruneScopePaths { get; } = [];

        public List<string> PruneExcludedDirectories { get; } = [];

        public List<string> EmptyOutputDirectories { get; } = [];
    }

    private CustomFolderOutputProjection CreateCustomFolderOutputLayoutProjection(BMSTable table, string outputDirectoryOverride = null)
    {
        string outputDirectory = string.IsNullOrWhiteSpace(outputDirectoryOverride)
            ? GetCustomFolderOutputDirectory(table)
            : outputDirectoryOverride;
        IReadOnlyList<string> relativeFilePaths = Lr2ManagedCustomFolderOutputLayout.CreateRelativeFilePaths(
            table,
            Lr2ManagedCustomFolderOutputLayout.CreateCountsFromLoadedTable(table),
            Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent);
        var files = new List<CustomFolderOutputFileProjection>();
        foreach (string relativeFilePath in relativeFilePaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(relativeFilePath))
            {
                continue;
            }

            string relativeDirectory = NormalizeCustomFolderRelativeDirectory(Path.GetDirectoryName(relativeFilePath));
            string filePath = string.IsNullOrWhiteSpace(relativeDirectory)
                ? Path.Combine(outputDirectory, Path.GetFileName(relativeFilePath))
                : Path.Combine(outputDirectory, relativeDirectory, Path.GetFileName(relativeFilePath));
            files.Add(new CustomFolderOutputFileProjection
            {
                RelativeDirectory = relativeDirectory,
                FilePath = filePath,
                DatabasePath = ResolveCustomFolderDatabasePath(table, filePath)
            });
        }

        return new CustomFolderOutputProjection
        {
            Table = table,
            OutputDirectory = outputDirectory,
            OutputRowScopePaths = CreateCustomFolderOutputRowScopePaths(table, outputDirectory),
            Files = files
        };
    }

    private CustomFolderOutputProjection CreateCustomFolderOutputProjection(BMSTable table, bool includeText = true, string outputDirectoryOverride = null)
    {
        string outputDirectory = string.IsNullOrWhiteSpace(outputDirectoryOverride)
            ? GetCustomFolderOutputDirectory(table)
            : outputDirectoryOverride;
        IReadOnlyList<CustomFolderDefinition> definitions = OrderCustomFolderDefinitionsForOutput(BuildCustomFolderDefinitions(table));
        var files = new List<CustomFolderOutputFileProjection>();
        var nextFileIndexByDirectory = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomFolderDefinition definition in definitions)
        {
            string relativeDirectory = NormalizeCustomFolderRelativeDirectory(definition.RelativeDirectory);
            nextFileIndexByDirectory.TryGetValue(relativeDirectory, out int index);
            nextFileIndexByDirectory[relativeDirectory] = index + 1;
            string filePath = Path.Combine(outputDirectory, relativeDirectory, $"{index:D4}.lr2folder");
            files.Add(new CustomFolderOutputFileProjection
            {
                Index = index,
                RelativeDirectory = relativeDirectory,
                Text = includeText ? definition.Text ?? string.Empty : null,
                Definition = definition.ParsedDefinition,
                FilePath = filePath,
                DatabasePath = ResolveCustomFolderDatabasePath(table, filePath)
            });
        }

        return new CustomFolderOutputProjection
        {
            Table = table,
            OutputDirectory = outputDirectory,
            OutputRowScopePaths = CreateCustomFolderOutputRowScopePaths(table, outputDirectory),
            Files = files
        };
    }

    private void AssignCustomFolderProtectedOutputDirectories(IReadOnlyCollection<CustomFolderOutputProjection> projections)
    {
        IReadOnlyCollection<string> knownOutputDirectories = CreateKnownCustomFolderOutputDirectories(projections);
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

    private IReadOnlyCollection<string> CreateKnownCustomFolderOutputDirectories(IEnumerable<CustomFolderOutputProjection> projections)
    {
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
                    AddCustomFolderOutputDirectory(directories, GetCustomFolderOutputDirectory(table));
                }
                catch (Exception ex) when (ex is ArgumentNullException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
                {
                    continue;
                }
            }
        }
        return [.. directories];
    }

    private static void AddCustomFolderOutputDirectory(ISet<string> directories, string directory)
    {
        string normalized = Lr2FolderPath.NormalizeDirectoryPath(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            directories?.Add(normalized);
        }
    }

    private static IReadOnlyList<CustomFolderDefinition> OrderCustomFolderDefinitionsForOutput(IEnumerable<CustomFolderDefinition> definitions)
    {
        var indexedDefinitions = (definitions ?? [])
            .Select((definition, index) => new
            {
                Definition = definition,
                Index = index,
                RelativeDirectory = NormalizeCustomFolderRelativeDirectory(definition?.RelativeDirectory)
            })
            .Where(item => item.Definition != null)
            .ToList();
        var directoryOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in indexedDefinitions)
        {
            if (!directoryOrder.ContainsKey(item.RelativeDirectory))
            {
                directoryOrder[item.RelativeDirectory] = item.Index;
            }
        }

        return [.. indexedDefinitions
            .OrderBy(item => directoryOrder[item.RelativeDirectory])
            .ThenBy(item => item.Definition.IsRandomVariant ? 1 : 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Definition)];
    }

    private CustomFolderBatchMaterializationResult MaterializeCustomFolderOutputBatch(
        IReadOnlyList<CustomFolderOutputProjection> projections,
        Action<int, int, string> progressCallback = null,
        string operation = null,
        string reason = null)
    {
        var result = new CustomFolderBatchMaterializationResult();
        var shiftJis = Encoding.GetEncoding("shift_jis");
        IReadOnlyList<CustomFolderOutputProjection> projectionList = [.. (projections ?? [])
            .Where(projection => projection != null && !string.IsNullOrWhiteSpace(projection.OutputDirectory))];
        AssignCustomFolderProtectedOutputDirectories(projectionList);
        if (projectionList.Any(projection => projection.PhysicalSurface == null))
        {
            CustomFolderOutputPhysicalSurface physicalSurface =
                CreateCustomFolderOutputPhysicalSurfaceFromGroupedEnumeration(projectionList, reason);
            foreach (CustomFolderOutputProjection projection in projectionList.Where(projection => projection.PhysicalSurface == null))
            {
                projection.PhysicalSurface = physicalSurface;
                if (physicalSurface?.DiscoveryComplete != true)
                {
                    MarkAllCustomFolderOutputFilesForWrite(projection);
                }
            }
        }
        var batchExpectedFilePaths = new HashSet<string>(
            projectionList
                .SelectMany(projection => projection.Files ?? [])
                .Select(file => Lr2FolderPath.NormalizeDirectoryPath(file?.FilePath))
                .Where(path => !string.IsNullOrWhiteSpace(path)),
            StringComparer.OrdinalIgnoreCase);
        var batchOutputDirectories = new HashSet<string>(
            projectionList
                .Select(projection => Lr2FolderPath.NormalizeDirectoryPath(projection.OutputDirectory))
                .Where(directory => !string.IsNullOrWhiteSpace(directory)),
            StringComparer.OrdinalIgnoreCase);
        var batchOutputRowScopeDirectories = new HashSet<string>(
            projectionList
                .SelectMany(projection => projection.OutputRowScopePaths ?? [])
                .Where(directory => !string.IsNullOrWhiteSpace(directory)),
            StringComparer.OrdinalIgnoreCase);
        result.PruneExcludedDirectories.AddRange(projectionList
            .SelectMany(CreateCustomFolderProtectedOutputRowScopePaths)
            .Where(directory => !string.IsNullOrWhiteSpace(directory)
                && !batchOutputDirectories.Contains(directory)
                && !batchOutputRowScopeDirectories.Contains(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase));
        for (int projectionIndex = 0; projectionIndex < projectionList.Count; projectionIndex++)
        {
            CustomFolderOutputProjection projection = projectionList[projectionIndex];
            int writtenBefore = result.WrittenFileCount;
            int unchangedBefore = result.UnchangedFileCount;
            int deletedBefore = result.DeletedFileCount;
            var projectionStopwatch = Stopwatch.StartNew();
            LogPlaylistPerformance((operation ?? "playlist_custom_folder_output") + " materialize_projection_start"
                + " reason=" + (reason ?? "unknown")
                + " index=" + (projectionIndex + 1)
                + " total=" + projectionList.Count
                + " name=" + QuoteLogValue(projection.Table?.name)
                + " outputDir=" + QuoteLogValue(projection.OutputDirectory)
                + " fileCount=" + (projection.Files?.Count ?? 0));
            string outputDir = projection.OutputDirectory;
            try
            {
                result.OutputDirectories.Add(outputDir);
                result.OutputRowScopeDirectories.AddRange(projection.OutputRowScopePaths ?? []);
                result.DirectoryRowGenerationScopeDirectories.AddRange(CreateCustomFolderDirectoryRowGenerationScopes(outputDir, projection.Table));
                IReadOnlyList<CustomFolderOutputFileProjection> files = projection.Files ?? [];
                if (files.Count > 0)
                {
                    Directory.CreateDirectory(outputDir);
                }

                foreach (CustomFolderOutputFileProjection file in files)
                {
                    if (file == null || string.IsNullOrWhiteSpace(file.FilePath))
                    {
                        continue;
                    }

                    string text = file.Text ?? string.Empty;
                    string filePath = file.FilePath;
                    RootFileEnumerationEntry physicalEntry = projection.PhysicalSurface?.DiscoveryComplete == true
                        ? projection.PhysicalSurface.Resolve(filePath)
                        : null;
                    string fileDirectory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrWhiteSpace(fileDirectory))
                    {
                        Directory.CreateDirectory(fileDirectory);
                    }
                    bool writeRequired = projection.ForceWriteFilePaths.Contains(filePath)
                        || physicalEntry?.LastWriteTimeUtc == null;
                    if (writeRequired)
                    {
                        File.WriteAllText(filePath, text, shiftJis);
                        physicalEntry = null;
                        result.WrittenFileCount++;
                    }
                    else
                    {
                        result.UnchangedFileCount++;
                    }

                    var syncItem = new Lr2FolderFileSyncItem
                    {
                        FilePath = filePath,
                        DatabasePath = file.DatabasePath,
                        Definition = file.Definition ?? Lr2FolderFileProjection.ParseDefinition(ReadLinesFromText(text)),
                        LastWriteTimeUtc = physicalEntry?.LastWriteTimeUtc ?? File.GetLastWriteTimeUtc(filePath)
                    };
                    ApplyCustomFolderSourceClassification(syncItem, projection.Table);
                    result.SyncItems.Add(syncItem);
                }
                RemoveStaleManagedCustomFolderFiles(projection, batchExpectedFilePaths, result);
                if (files.Count == 0)
                {
                    result.EmptyOutputDirectories.Add(outputDir);
                    if (TryDeleteEmptyCustomFolderDirectory(outputDir, projection.ProtectedOutputDirectories, out string deletedOutputDirectory))
                    {
                        AddCustomFolderPruneScopePath(result, projection.Table, deletedOutputDirectory, directoryPath: true);
                    }
                }
                projectionStopwatch.Stop();
                LogPlaylistPerformance((operation ?? "playlist_custom_folder_output") + " materialize_projection_done"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (projectionIndex + 1)
                    + " total=" + projectionList.Count
                    + " name=" + QuoteLogValue(projection.Table?.name)
                    + " writtenFiles=" + (result.WrittenFileCount - writtenBefore)
                    + " unchangedFiles=" + (result.UnchangedFileCount - unchangedBefore)
                    + " deletedFiles=" + (result.DeletedFileCount - deletedBefore)
                    + " elapsedMs=" + projectionStopwatch.ElapsedMilliseconds);
                progressCallback?.Invoke(projectionIndex + 1, projectionList.Count, projection.Table?.name ?? string.Empty);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                projectionStopwatch.Stop();
                LogPlaylistPerformance((operation ?? "playlist_custom_folder_output") + " materialize_projection_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + (projectionIndex + 1)
                    + " total=" + projectionList.Count
                    + " name=" + QuoteLogValue(projection.Table?.name)
                    + " outputDir=" + QuoteLogValue(projection.OutputDirectory)
                    + " exception=" + QuoteLogValue(ex.GetType().Name)
                    + " message=" + QuoteLogValue(ex.Message)
                    + " elapsedMs=" + projectionStopwatch.ElapsedMilliseconds);
                throw;
            }
        }
        foreach (CustomFolderOutputProjection projection in projectionList
            .Where(projection => !string.IsNullOrWhiteSpace(projection.OutputDirectory))
            .OrderByDescending(projection => projection.OutputDirectory.Length))
        {
            if (TryDeleteEmptyCustomFolderDirectory(projection.OutputDirectory, projection.ProtectedOutputDirectories, out string deletedOutputDirectory))
            {
                result.EmptyOutputDirectories.Add(deletedOutputDirectory);
                AddCustomFolderPruneScopePath(result, projection.Table, deletedOutputDirectory, directoryPath: true);
            }
        }

        result.OutputDirectories.RemoveAll(string.IsNullOrWhiteSpace);
        AddOwnedCustomFolderDirectoryEntries(
            result.DirectoryEntries,
            result.SyncItems,
            result.DirectoryRowGenerationScopeDirectories);
        return result;
    }

    private List<CustomFolderDefinition> BuildCustomFolderDefinitions(BMSTable bmsTable)
    {
        var definitions = new List<CustomFolderDefinition>();
        if (bmsTable == null)
        {
            return definitions;
        }
        foreach (Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<CustomFolderDefinition>>> item in new List<Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<CustomFolderDefinition>>>>
        {
            new(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, makeCustomFolderDefinitionsUserFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, makeCustomFolderDefinitionsLevelFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, table => WrapFlatCustomFolderDefinitions(makeCustomFolderTextsAlphabetFolder(table))),
            new(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, makeCustomFolderDefinitionsClearFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, makeCustomFolderDefinitionsDJLevelFolder),
            new(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, table => WrapFlatCustomFolderDefinitions(makeCustomFolderTextsCategoryAllFolder(table))),
            new(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, table => WrapFlatCustomFolderDefinitions(makeCustomFolderTextsOtherFolder(table))),
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

    private static void RemoveStaleManagedCustomFolderFiles(
        CustomFolderOutputProjection projection,
        ISet<string> expectedFilePaths,
        CustomFolderBatchMaterializationResult result)
    {
        if (projection == null || string.IsNullOrWhiteSpace(projection.OutputDirectory) || result == null)
        {
            return;
        }
        if (!Directory.Exists(projection.OutputDirectory))
        {
            return;
        }

        foreach (string filePath in EnumerateManagedCustomFolderFiles(projection.OutputDirectory))
        {
            string normalizedFilePath = Lr2FolderPath.NormalizeDirectoryPath(filePath);
            if (!string.IsNullOrWhiteSpace(normalizedFilePath)
                && expectedFilePaths != null
                && expectedFilePaths.Contains(normalizedFilePath))
            {
                continue;
            }
            if (IsPathUnderAnyCustomFolderDirectory(normalizedFilePath, projection.ProtectedOutputDirectories))
            {
                continue;
            }

            if (TryDeleteManagedCustomFolderFile(filePath, out bool deleted))
            {
                if (deleted)
                {
                    result.DeletedFileCount++;
                }
                AddCustomFolderPruneScopePath(result, projection.Table, filePath, directoryPath: false);
            }
        }
        foreach (string deletedDirectory in RemoveEmptyCustomFolderDirectories(projection.OutputDirectory, projection.ProtectedOutputDirectories))
        {
            result.EmptyOutputDirectories.Add(deletedDirectory);
            AddCustomFolderPruneScopePath(result, projection.Table, deletedDirectory, directoryPath: true);
        }
    }

    private static IReadOnlyList<string> EnumerateManagedCustomFolderFiles(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(outputDirectory, "*.lr2folder", System.IO.SearchOption.AllDirectories)];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return [];
        }
    }

    private static bool TryDeleteManagedCustomFolderFile(string filePath, out bool deleted)
    {
        deleted = false;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            bool existed = File.Exists(filePath);
            File.Delete(filePath);
            deleted = existed;
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static void AddCustomFolderPruneScopePath(
        CustomFolderBatchMaterializationResult result,
        BMSTable table,
        string path,
        bool directoryPath)
    {
        if (result == null || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        result.PruneScopePaths.Add(path);
        string databasePath = ResolveCustomFolderDatabasePath(
            table,
            directoryPath ? Lr2FolderPath.ToFolderPath(path) : path);
        if (!string.IsNullOrWhiteSpace(databasePath))
        {
            result.PruneScopePaths.Add(databasePath);
        }
    }

    private static List<string> RemoveEmptyCustomFolderDirectories(
        string outputDirectory,
        IReadOnlyCollection<string> protectedDirectories = null)
    {
        var deletedDirectories = new List<string>();
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            return deletedDirectories;
        }
        IReadOnlyList<string> directories;
        try
        {
            directories = [.. Directory.EnumerateDirectories(outputDirectory, "*", System.IO.SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length)];
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return deletedDirectories;
        }

        foreach (string directory in directories)
        {
            if (IsPathUnderAnyCustomFolderDirectory(directory, protectedDirectories))
            {
                continue;
            }
            if (TryDeleteEmptyCustomFolderDirectory(directory, protectedDirectories, out string deletedDirectory))
            {
                deletedDirectories.Add(deletedDirectory);
            }
        }
        return deletedDirectories;
    }

    private static bool TryDeleteEmptyCustomFolderDirectory(
        string directory,
        IReadOnlyCollection<string> protectedDirectories,
        out string deletedDirectory)
    {
        deletedDirectory = null;
        if (string.IsNullOrWhiteSpace(directory)
            || IsPathUnderAnyCustomFolderDirectory(directory, protectedDirectories))
        {
            return false;
        }

        try
        {
            if (!Directory.Exists(directory) || Directory.EnumerateFileSystemEntries(directory).Any())
            {
                return false;
            }

            Directory.Delete(directory);
            deletedDirectory = directory;
            return true;
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException || ex is FileNotFoundException)
        {
            return false;
        }
    }

    private static bool IsPathUnderAnyCustomFolderDirectory(string path, IEnumerable<string> directories)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string normalizedPath = Lr2FolderPath.NormalizeDirectoryPath(path);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return false;
        }

        foreach (string directory in directories ?? [])
        {
            string normalizedDirectory = Lr2FolderPath.NormalizeDirectoryPath(directory);
            if (!string.IsNullOrWhiteSpace(normalizedDirectory)
                && Lr2FolderPath.IsSameOrDescendant(normalizedPath, normalizedDirectory))
            {
                return true;
            }
        }
        return false;
    }

    private void reOutputCustomFolderFiles(BMSTable bmsTable)
    {
        string customFolderOutputDirectory = GetCustomFolderOutputDirectory(bmsTable);
        migrateCustomFolderOutputDirectoryFiles(bmsTable, customFolderOutputDirectory, customFolderOutputDirectory, bmsTable.is_root_folder);
    }

    private void migrateCustomFolderOutputDirectoryFiles(
        BMSTable bmsTable,
        string outputDirPathBefore,
        string outputDirPathAfter,
        bool wasRootFolderBefore,
        string rootOutputBaseDirBefore = null,
        string outputBaseDirBefore = null,
        bool inferOutputBaseDirBeforeWhenMissing = true)
    {
        bool sameDirectory = IsSameCustomFolderDirectory(outputDirPathBefore, outputDirPathAfter);
        if (sameDirectory)
        {
            createCustomFolder(bmsTable, outputDirPathAfter, forceWriteFiles: true);
            return;
        }

        bool created = createCustomFolder(bmsTable, outputDirPathAfter);
        if (!created)
        {
            return;
        }

        IReadOnlyCollection<string> deleteBaseDirectories = CreateCustomFolderManagedOutputBaseScopes(
            bmsTable,
            wasRootFolderBefore,
            rootOutputBaseDirBefore,
            outputBaseDirBefore,
            inferOutputBaseDirBeforeWhenMissing);
        if (!DeleteCustomFolderOutputDirectoryTree(outputDirPathBefore, deleteBaseDirectories))
        {
            return;
        }

        if (!sameDirectory)
        {
            try
            {
                SyncCustomFolderRowsByPaths([], CreateCustomFolderDirectoryPruneScopes(outputDirPathBefore, wasRootFolderBefore, rootOutputBaseDirBefore));
            }
            catch
            {
                if (created)
                {
                    DispatcherMessageBox.Show(string.Format(Resources.Warn_FileOrDirDeleteFailed, outputDirPathBefore), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                }
            }
        }
    }

    private static bool IsSameCustomFolderDirectory(string left, string right)
    {
        string normalizedLeft = Lr2FolderPath.NormalizeDirectoryPath(left);
        string normalizedRight = Lr2FolderPath.NormalizeDirectoryPath(right);
        return !string.IsNullOrWhiteSpace(normalizedLeft)
            && !string.IsNullOrWhiteSpace(normalizedRight)
            && string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSameOrDescendantCustomFolderDirectory(string candidate, string ancestor)
    {
        string normalizedCandidate = Lr2FolderPath.NormalizeDirectoryPath(candidate);
        string normalizedAncestor = Lr2FolderPath.NormalizeDirectoryPath(ancestor);
        return !string.IsNullOrWhiteSpace(normalizedCandidate)
            && !string.IsNullOrWhiteSpace(normalizedAncestor)
            && Lr2FolderPath.IsSameOrDescendant(normalizedCandidate, normalizedAncestor);
    }

    /// <summary>
    /// プレイリスト設定に基づき、出力先ディレクトリへ <c>.lr2folder</c> 群を生成します。
    /// </summary>
    /// <param name="bmsTable">出力元のプレイリスト。</param>
    /// <param name="outputDir">出力先ディレクトリ。</param>
    private bool createCustomFolder(BMSTable bmsTable, string outputDir, bool forceWriteFiles = false)
    {
        try
        {
            CustomFolderOutputProjection projection = CreateCustomFolderOutputProjection(bmsTable, outputDirectoryOverride: outputDir);
            if (forceWriteFiles)
            {
                foreach (CustomFolderOutputFileProjection file in projection.Files ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(file?.FilePath))
                    {
                        projection.ForceWriteFilePaths.Add(file.FilePath);
                    }
                }
            }
            CustomFolderBatchMaterializationResult materialization = MaterializeCustomFolderOutputBatch([projection]);
            SyncCustomFolderRowsBatch(
                materialization.OutputDirectories,
                materialization.OutputRowScopeDirectories,
                materialization.SyncItems,
                materialization.DirectoryRowGenerationScopeDirectories,
                materialization.DirectoryEntries,
                materialization.PruneScopePaths,
                materialization.PruneExcludedDirectories,
                materialization.EmptyOutputDirectories);
            PersistCustomFolderOutputStatuses(
                [projection],
                CreateCustomFolderOutputPhysicalSurfaceFromSyncItems(materialization.SyncItems));
            if (Directory.Exists(outputDir)
                && !Directory.EnumerateFileSystemEntries(outputDir).Any())
            {
                FileSystem.DeleteDirectory(outputDir, DeleteDirectoryOption.ThrowIfDirectoryNonEmpty);
            }
            return true;
        }
        catch
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_CustomFolderOutputFailed, bmsTable.name, outputDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return false;
        }
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
        if (!Settings.Default.OperationModeLR2DB)
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
        EnsurePlaylistEntriesLoaded(bmsTable, "ReOutputCustomFolder");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                if (removeCustomFolder(
                    GetCustomFolderOutputDirectory(bmsTable),
                    pruneRows: true,
                    wasRootFolder: bmsTable.is_root_folder,
                    rootCustomFolderOutputBaseDir: Settings.Default.LR2CustomFolderOutputBaseDirRootType,
                    outputBaseDir: CreateCustomFolderManagedOutputBase(bmsTable, bmsTable.is_root_folder)))
                {
                    DeleteCustomFolderOutputStatus(bmsTable);
                }
            }
        }
    }

    private static void ApplyCustomFolderSourceClassification(Lr2FolderFileSyncItem item, BMSTable bmsTable)
    {
        if (item == null || bmsTable?.is_root_folder != true)
        {
            return;
        }
        Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
        {
            FilePath = item.FilePath,
            Lr2RootPath = Settings.Default.LR2RootPath,
            RootCustomFolderOutputBaseDir = Settings.Default.LR2CustomFolderOutputBaseDirRootType
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
        if (string.IsNullOrWhiteSpace(outputDir) || string.IsNullOrWhiteSpace(lr2SongDBPath))
        {
            return;
        }

        directoryRowGenerationScopes ??= CreateCustomFolderDirectoryRowGenerationScopes(outputDir, bmsTable);
        Lr2FolderDirectoryMetadataSnapshot directoryMetadata = CreateCustomFolderParentDirectoryMetadataSnapshot(
            items,
            directoryRowGenerationScopes,
            [outputDir],
            ownedDirectoryEntries);
        Lr2FolderFileDbSyncResult result = null;
        ExecuteLr2FolderSync("playlist_lr2folder_sync", delegate
        {
            using var lr2Song = new LR2SongDBExtended(lr2SongDBPath);
            result = Lr2FolderFileDbSyncService.Sync(lr2Song, new Lr2FolderFileDbSyncRequest
            {
                Items = items ?? [],
                ScopeDirectories = [outputDir],
                DirectoryRowScopeDirectories = [outputDir],
                DirectoryRowGenerationScopeDirectories = directoryRowGenerationScopes,
                DirectoryMetadataResolver = directoryMetadata.Resolve,
                AllowPrune = true
            });
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
        IReadOnlyCollection<string> emptyOutputDirectories = null)
    {
        outputDirs = [.. (outputDirs ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        if (outputDirs.Count == 0 || string.IsNullOrWhiteSpace(lr2SongDBPath))
        {
            return null;
        }

        IReadOnlyCollection<string> rowScopeDirectories = [.. (outputRowScopeDirectories ?? [])
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
        Lr2FolderFileDbSyncResult result = null;
        ExecuteLr2FolderSync("playlist_lr2folder_batch_sync", delegate
        {
            using var lr2Song = new LR2SongDBExtended(lr2SongDBPath);
            IReadOnlyCollection<string> exactScopePaths = CreateCustomFolderExactScopePaths(items, pruneScopePaths);
            IReadOnlyCollection<string> emptyDirectoryRowScopeDirectories = [.. (emptyOutputDirectories ?? [])
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            IReadOnlyCollection<string> directoryRowScopeDirectories = [.. pruneScopeDirectories
                .Concat(emptyDirectoryRowScopeDirectories)
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            result = Lr2FolderFileDbSyncService.Sync(lr2Song, new Lr2FolderFileDbSyncRequest
            {
                Items = items ?? [],
                ScopeDirectories = pruneScopeDirectories,
                ScopePaths = exactScopePaths,
                DirectoryRowScopeDirectories = directoryRowScopeDirectories,
                DirectoryRowGenerationScopeDirectories = directoryRowGenerationScopes,
                PruneExcludedDirectories = pruneExcludedDirectories ?? [],
                DirectoryMetadataResolver = directoryMetadata.Resolve,
                AllowPrune = true
            });
        });
        LogLr2FolderSyncResult("playlist_lr2folder_batch_sync", result, outputDirs.Count, items?.Count ?? 0);
        return result;
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
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            return [];
        }
        try
        {
            string normalizedOutputDir = Lr2FolderPath.NormalizeDirectoryPath(outputDir);
            if (bmsTable?.is_root_folder != true)
            {
                IEnumerable<string> normalOutputBases = new[]
                    {
                        Settings.Default.LR2CustomFolderOutputBaseDir
                    }
                    .Concat(CustomFolderOutputBaseRegistry.ReadAdditionalBaseDirectories());
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

    private static IReadOnlyCollection<string> CreateCustomFolderDirectoryPruneScopes(
        string physicalDirectory,
        bool wasRootFolder,
        string rootCustomFolderOutputBaseDir = null)
    {
        var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddCustomFolderDirectoryPruneScope(scopes, physicalDirectory);
        if (wasRootFolder)
        {
            Lr2FolderFileSourceClassification classification = Lr2FolderFileSourceClassifier.Classify(new Lr2FolderFileSourceClassificationRequest
            {
                FilePath = physicalDirectory,
                Lr2RootPath = Settings.Default.LR2RootPath,
                RootCustomFolderOutputBaseDir = rootCustomFolderOutputBaseDir ?? Settings.Default.LR2CustomFolderOutputBaseDirRootType
            });
            AddCustomFolderDirectoryPruneScope(scopes, classification.DatabasePath);
        }
        return [.. scopes];
    }

    private static void AddCustomFolderDirectoryPruneScope(ISet<string> scopes, string directory)
    {
        string normalized = NormalizeCustomFolderRowPath(directory);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            scopes?.Add(normalized);
        }
    }

    private static IReadOnlyCollection<string> CreateCustomFolderManagedOutputBaseScopes(
        BMSTable bmsTable,
        bool wasRootFolder,
        string rootOutputBaseDirBefore = null,
        string outputBaseDirBefore = null,
        bool inferOutputBaseDirBeforeWhenMissing = true)
    {
        string outputBaseDir = !string.IsNullOrWhiteSpace(outputBaseDirBefore)
            ? outputBaseDirBefore
            : inferOutputBaseDirBeforeWhenMissing
                ? CreateCustomFolderManagedOutputBase(bmsTable, wasRootFolder, rootOutputBaseDirBefore)
                : null;
        return string.IsNullOrWhiteSpace(outputBaseDir)
            ? []
            : [outputBaseDir];
    }

    private static string CreateCustomFolderManagedOutputBase(
        BMSTable bmsTable,
        bool isRootFolder,
        string rootOutputBaseDir = null)
    {
        if (isRootFolder)
        {
            return rootOutputBaseDir ?? Settings.Default.LR2CustomFolderOutputBaseDirRootType;
        }
        return CustomFolderOutputBaseRegistry.TryResolveNormalOutputBaseDirectory(
            bmsTable?.custom_folder_output_base_name,
            Settings.Default.LR2CustomFolderOutputBaseDir,
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs,
            out string outputBaseDir)
                ? outputBaseDir
                : null;
    }

    private void SyncCustomFolderRowsByPaths(
        IReadOnlyCollection<string> filePaths,
        IReadOnlyCollection<string> directoryRowScopeDirectories = null)
    {
        if ((filePaths == null || filePaths.Count == 0)
            && (directoryRowScopeDirectories == null || directoryRowScopeDirectories.Count == 0))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(lr2SongDBPath))
        {
            return;
        }

        Lr2FolderFileDbSyncResult result = null;
        ExecuteLr2FolderSync("playlist_lr2folder_prune", delegate
        {
            using var lr2Song = new LR2SongDBExtended(lr2SongDBPath);
            result = Lr2FolderFileDbSyncService.Sync(lr2Song, new Lr2FolderFileDbSyncRequest
            {
                ScopePaths = filePaths,
                DirectoryRowScopeDirectories = directoryRowScopeDirectories ?? [],
                AllowPrune = true
            });
        });
        LogLr2FolderSyncResult("playlist_lr2folder_prune", result, directoryRowScopeDirectories?.Count ?? 0, filePaths?.Count ?? 0);
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

    private void ExecuteLr2FolderSync(string operation, Action action)
    {
        Lr2FolderSyncMutationGuard?.Invoke(operation);
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Lr2FolderSyncFailureReporter?.Invoke(operation, ex);
            throw;
        }
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

    /// <summary>
    /// 指定ディレクトリ配下の <c>.lr2folder</c> と LR2 DB 上の対応フォルダ情報を削除します。
    /// </summary>
    /// <param name="targetDir">削除対象ディレクトリ。</param>
    /// <param name="pruneRows">対応する LR2 folder row も削除する場合は <see langword="true"/>。</param>
    private bool removeCustomFolder(
        string targetDir,
        bool pruneRows,
        bool wasRootFolder = false,
        string rootCustomFolderOutputBaseDir = null,
        string outputBaseDir = null)
    {
        if (!DeleteCustomFolderOutputDirectoryTree(targetDir, [outputBaseDir]))
        {
            return false;
        }

        if (pruneRows)
        {
            try
            {
                SyncCustomFolderRowsByPaths(
                    [],
                    CreateCustomFolderDirectoryPruneScopes(targetDir, wasRootFolder, rootCustomFolderOutputBaseDir));
            }
            catch
            {
                DispatcherMessageBox.Show(string.Format(Resources.Warn_FileOrDirDeleteFailed, targetDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
                return false;
            }
        }

        return true;
    }

    private static bool DeleteCustomFolderOutputDirectoryTree(
        string targetDir,
        IReadOnlyCollection<string> managedOutputBaseDirectories)
    {
        if (!IsSafeManagedCustomFolderOutputDirectory(targetDir, managedOutputBaseDirectories))
        {
            return false;
        }
        if (!Directory.Exists(targetDir))
        {
            return true;
        }

        try
        {
            Directory.Delete(targetDir, recursive: true);
            return true;
        }
        catch
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_FileOrDirDeleteFailed, targetDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            return false;
        }
    }

    private static bool IsSafeManagedCustomFolderOutputDirectory(
        string targetDir,
        IEnumerable<string> managedOutputBaseDirectories)
    {
        string normalizedTarget = NormalizeDirectoryPathOrNull(targetDir);
        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            return false;
        }

        foreach (string baseDirectory in managedOutputBaseDirectories ?? [])
        {
            string normalizedBase = NormalizeDirectoryPathOrNull(baseDirectory);
            if (string.IsNullOrWhiteSpace(normalizedBase)
                || string.Equals(normalizedTarget, normalizedBase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (Lr2FolderPath.IsSameOrDescendantNormalized(normalizedTarget, normalizedBase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsFileUnderDirectory(string filePath, string normalizedDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(normalizedDirectory))
        {
            return false;
        }
        string parentDirectory = NormalizeDirectoryPathOrNull(Path.GetDirectoryName(filePath));
        return !string.IsNullOrWhiteSpace(parentDirectory)
            && Lr2FolderPath.IsSameOrDescendant(parentDirectory, normalizedDirectory);
    }

    /// <summary>
    /// 外部プレイリストを読み込み、一覧と DB へ新規登録します。
    /// </summary>
    /// <param name="pageUri">登録対象の絶対 URI。</param>
    /// <returns>登録されたプレイリスト。</returns>
    /// <exception cref="InvalidOperationException">URI 不正、重複、または登録要件を満たさない場合。</exception>
    public BMSTable RegistrateExternalTable(Uri pageUri)
    {
        return RegistrateExternalTableAsync(pageUri).GetAwaiter().GetResult();
    }

    internal async Task<BMSTable> RegistrateExternalTableAsync(Uri pageUri, CancellationToken cancellationToken = default)
    {
        if (!pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("pageUri.IsAbsoluteUri is not true");
        }
        BMSTable bMSTable = await LoadExternalTableAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        bool migrateCustomFolderOutput = false;
        string customFolderOutputDirectory = null;
        using (rwlockBMSTablesInitializeMin.GetReaderGuard())
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                if (bMSTable.last_update == default)
                {
                    bMSTable.last_update = DateTime.Now;
                }
                if (BMSTables.Select(t => t.name).Contains(bMSTable.name))
                {
                    throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, bMSTable.name);
                }
                if (string.IsNullOrWhiteSpace(bMSTable.Output_dir))
                {
                    throw new InvalidOperationException(Resources.Error_OutputDirNameEmpty);
                }
                bMSTable.bmt_sort = ResolveNextBeatorajaBmtSort(BMSTables);
                bMSTable.is_bmt_output = true;
                if (Settings.Default.OperationModeLR2DB)
                {
                    migrateCustomFolderOutput = true;
                    customFolderOutputDirectory = GetCustomFolderOutputDirectory(bMSTable);
                }
            }
        }
        CommitBMSTable(bMSTable);
        await AddCommittedBMSTableToVisibleCollectionAsync(bMSTable).ConfigureAwait(false);
        if (migrateCustomFolderOutput)
        {
            migrateCustomFolderOutputDirectoryFiles(bMSTable, customFolderOutputDirectory, customFolderOutputDirectory, bMSTable.is_root_folder);
        }
        QueueBeatorajaBmtExport(bMSTable, "RegistrateExternalTableAsync");
        ApplyCachedPlaylistUrlCompletionToTable(bMSTable, "RegistrateExternalTableAsync");
        SchedulePlaylistUrlCompletionRefresh("RegistrateExternalTableAsync");
        return bMSTable;
    }

    /// <summary>
    /// プレイリスト一覧を走査し、必要な外部同期と後処理コールバックを実行します。
    /// </summary>
    /// <param name="reloadExtPlaylist">外部同期対象プレイリストを再取得するかどうか。</param>
    /// <param name="updateCallbackActions">各プレイリスト処理後に呼ぶコールバック群。</param>
    /// <returns><c>last_update</c> が変化したプレイリスト一覧。</returns>
    public List<BMSTable> UpdateBMSTables(bool reloadExtPlaylist = true, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null)
    {
        return UpdateBMSTablesInternalAsync(reloadExtPlaylist, updateCallbackActions, null).GetAwaiter().GetResult();
    }

    internal List<BMSTable> UpdateBMSTablesInternal(bool reloadExtPlaylist = true, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null, Action<PlaylistSyncAttemptResult> syncResultCallback = null)
    {
        return UpdateBMSTablesInternalAsync(reloadExtPlaylist, updateCallbackActions, syncResultCallback, null).GetAwaiter().GetResult();
    }

    internal async Task<List<BMSTable>> UpdateBMSTablesInternalAsync(bool reloadExtPlaylist = true, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null, Action<PlaylistSyncAttemptResult> syncResultCallback = null, Action<PlaylistSyncProgressSnapshot> progressCallback = null, CancellationToken cancellationToken = default)
    {
        IsPlaylistUpdating = true;
        try
        {
            var stopwatchUpdateTablesTotal = Stopwatch.StartNew();
            List<BMSTable> tableSnapshot;
            using (rwlockBMSTables.GetReaderGuard())
            {
                tableSnapshot = [.. BMSTables];
            }
            List<BMSTable> reloadTargets = [.. tableSnapshot.Where(delegate (BMSTable table)
            {
                Uri uri2 = table?.Page_url ?? table?.Header_url;
                return reloadExtPlaylist && table != null && table.is_external_sync && uri2 != null && uri2.IsAbsoluteUri;
            })];
            List<PlaylistReloadTargetResult> results = await ReloadPlaylistTargetsAsync(
                reloadTargets,
                updateCallbackActions,
                syncResultCallback,
                progressCallback,
                "UpdateBMSTablesInternalAsync",
                cancellationToken).ConfigureAwait(false);
            if (updateCallbackActions != null)
            {
                var reloadedTables = new HashSet<BMSTable>(results.Select(result => result.SourceTable).Where(table => table != null));
                foreach (BMSTable table in tableSnapshot.Where(table => table != null && !reloadedTables.Contains(table)))
                {
                    InvokePlaylistUpdateCallbacks(new PlaylistTableUpdateContext
                    {
                        NewTable = table,
                        Updated = false,
                        ReferenceEntriesChanged = false,
                        OldTable = table,
                        OldEntriesSnapshot = null,
                        NewEntriesSnapshot = null
                    }, updateCallbackActions, table.Page_url ?? table.Header_url);
                }
            }
            stopwatchUpdateTablesTotal.Stop();
            List<BMSTable> updatedTables = [.. results.Where(result => result.Succeeded && result.Updated && result.ResultTable != null).Select(result => result.ResultTable)];
            LogPlaylistPerformance("playlist_update table_count=" + tableSnapshot.Count + " target_count=" + reloadTargets.Count + " updated_count=" + updatedTables.Count + " total_ms=" + stopwatchUpdateTablesTotal.ElapsedMilliseconds);
            return updatedTables;
        }
        finally
        {
            IsPlaylistUpdating = false;
        }
    }

    internal async Task<List<PlaylistReloadTargetResult>> ReloadPlaylistTargetsAsync(IEnumerable<BMSTable> targets, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null, Action<PlaylistSyncAttemptResult> syncResultCallback = null, Action<PlaylistSyncProgressSnapshot> progressCallback = null, string reason = "ReloadPlaylistTargetsAsync", CancellationToken cancellationToken = default)
    {
        List<BMSTable> targetSnapshot = [.. (targets ?? [])
            .Where(table => table != null)
            .Distinct()
            .Where(delegate (BMSTable table)
            {
                Uri uri = table.Page_url ?? table.Header_url;
                return uri != null && uri.IsAbsoluteUri;
            })];
        int completedTableCount = 0;
        List<PlaylistReloadTargetResult> results = [];
        object resultLock = new();
        progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = targetSnapshot.Count > 0,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = 0,
            CurrentTableName = string.Empty,
            CurrentUri = null
        });
        using var semaphoreSlim = new SemaphoreSlim(ExternalPlaylistSyncMaxConcurrency, ExternalPlaylistSyncMaxConcurrency);
        await Task.WhenAll([.. targetSnapshot.Select(async delegate (BMSTable table)
        {
            await semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            Uri uri = table.Page_url ?? table.Header_url;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = targetSnapshot.Count,
                    CompletedTableCount = Volatile.Read(ref completedTableCount),
                    CurrentTableName = table.name,
                    CurrentUri = uri
                });
                PlaylistReloadTargetResult result = await ReloadPlaylistTargetCoreAsync(table, uri, updateCallbackActions, syncResultCallback, reason, cancellationToken).ConfigureAwait(false);
                lock (resultLock)
                {
                    results.Add(result);
                }
            }
            finally
            {
                int num = Interlocked.Increment(ref completedTableCount);
                progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = targetSnapshot.Count,
                    CompletedTableCount = num,
                    CurrentTableName = table.name,
                    CurrentUri = uri
                });
                semaphoreSlim.Release();
            }
        })]).ConfigureAwait(false);
        progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = completedTableCount,
            CurrentTableName = string.Empty,
            CurrentUri = null
        });
        if (targetSnapshot.Count > 0 && Settings.Default.EnablePlaylistUrlCompletion)
        {
            SchedulePlaylistUrlCompletionRefresh(reason);
        }
        return results;
    }

    internal async Task<List<PlaylistExternalTableLoadResult>> LoadExternalTableSnapshotsAsync(IEnumerable<BMSTable> targets, bool inheritLocalTableProperties, Action<PlaylistSyncProgressSnapshot> progressCallback = null, string reason = "LoadExternalTableSnapshotsAsync", CancellationToken cancellationToken = default)
    {
        List<BMSTable> targetSnapshot = [.. (targets ?? [])
            .Where(table => table != null)
            .Distinct()
            .Where(delegate (BMSTable table)
            {
                Uri uri = table.Page_url ?? table.Header_url;
                return uri != null && uri.IsAbsoluteUri;
            })];
        int completedTableCount = 0;
        PlaylistExternalTableLoadResult[] results = new PlaylistExternalTableLoadResult[targetSnapshot.Count];
        progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = targetSnapshot.Count > 0,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = 0,
            CurrentTableName = string.Empty,
            CurrentUri = null
        });
        using var semaphoreSlim = new SemaphoreSlim(ExternalPlaylistSyncMaxConcurrency, ExternalPlaylistSyncMaxConcurrency);
        await Task.WhenAll([.. targetSnapshot.Select(async delegate (BMSTable table, int index)
        {
            await semaphoreSlim.WaitAsync(cancellationToken).ConfigureAwait(false);
            Uri uri = table.Page_url ?? table.Header_url;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = targetSnapshot.Count,
                    CompletedTableCount = Volatile.Read(ref completedTableCount),
                    CurrentTableName = table.name,
                    CurrentUri = uri
                });
                BMSTable externalTable = await LoadExternalTableAsync(uri, inheritLocalTableProperties ? table : null, cancellationToken).ConfigureAwait(false);
                results[index] = new PlaylistExternalTableLoadResult
                {
                    SourceTable = table,
                    ExternalTable = externalTable,
                    Uri = uri
                };
            }
            catch (Exception ex)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_external_snapshot_load_failed reason=" + FormatTextForLog(reason) + " table=" + FormatTextForLog(table?.name) + " uri=" + FormatUriForLog(uri));
                results[index] = new PlaylistExternalTableLoadResult
                {
                    SourceTable = table,
                    Uri = uri,
                    Exception = ex
                };
            }
            finally
            {
                int num = Interlocked.Increment(ref completedTableCount);
                progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
                {
                    IsActive = true,
                    TotalTableCount = targetSnapshot.Count,
                    CompletedTableCount = num,
                    CurrentTableName = table.name,
                    CurrentUri = uri
                });
                semaphoreSlim.Release();
            }
        })]).ConfigureAwait(false);
        progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = false,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = completedTableCount,
            CurrentTableName = string.Empty,
            CurrentUri = null
        });
        if (targetSnapshot.Count > 0 && Settings.Default.EnablePlaylistUrlCompletion)
        {
            SchedulePlaylistUrlCompletionRefresh(reason);
        }
        return [.. results.Where(result => result != null)];
    }

    private async Task<PlaylistReloadTargetResult> ReloadPlaylistTargetCoreAsync(BMSTable table, Uri uri, List<Action<PlaylistTableUpdateContext>> updateCallbackActions, Action<PlaylistSyncAttemptResult> syncResultCallback, string reason, CancellationToken cancellationToken)
    {
        BMSTable newTable = table;
        List<BMSTableEntry> oldEntriesSnapshot = null;
        List<BMSTableEntry> newEntriesSnapshot = null;
        PlaylistReloadPersistenceDecision persistenceDecision = null;
        bool updated = false;
        Exception failure = null;
        try
        {
            BMSTable reloadedTable = await reloadBMSTableAsync(table, uri, cancellationToken).ConfigureAwait(false);
            EnsurePlaylistEntriesLoaded(table, reason);
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                oldEntriesSnapshot = table.entries?.ToList() ?? [];
                IReadOnlyList<BMSTableEntry> persistedActiveEntries = LoadPersistedActivePlaylistEntries(table.playlist_id);
                newTable = MergeReloadedBMSTableState(table, reloadedTable, BuildComparablePlaylistEntryRows(persistedActiveEntries), out persistenceDecision, logLastUpdateDecision: true);
                updated = persistenceDecision.UpdatesLastUpdate;
                if (persistenceDecision.NeedsEntryPersistence)
                {
                    newEntriesSnapshot = newTable.entries?.ToList() ?? [];
                }
                else
                {
                    newEntriesSnapshot = oldEntriesSnapshot;
                    if (persistenceDecision.NeedsHeaderPersistence)
                    {
                        newTable.entries = [.. oldEntriesSnapshot];
                    }
                    if (!persistenceDecision.NeedsStatePersistence)
                    {
                        newTable = table;
                    }
                }
            }
            if (persistenceDecision?.NeedsEntryPersistence == true)
            {
                CommitBMSTable(newTable);
                ReplaceBMSTableInCollection(table, newTable);
            }
            else if (persistenceDecision?.NeedsHeaderPersistence == true)
            {
                commitBMSTableHeaderOnly(newTable);
                ReplaceBMSTableInCollection(table, newTable);
            }
            if (persistenceDecision?.NeedsBmtExport == true)
            {
                QueueBeatorajaBmtExport(newTable, reason);
            }
            ApplyCachedPlaylistUrlCompletionToTable(newTable, reason);
            syncResultCallback?.Invoke(PlaylistSyncAttemptResult.CreateSuccess(table, newTable, uri, updated));
        }
        catch (Exception ex)
        {
            failure = ex;
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_reload_target_failed reason=" + FormatTextForLog(reason) + " table=" + FormatTextForLog(table?.name) + " uri=" + FormatUriForLog(uri));
            syncResultCallback?.Invoke(PlaylistSyncAttemptResult.CreateFailure(table, uri, ex));
        }
        var updateContext = new PlaylistTableUpdateContext
        {
            NewTable = newTable,
            Updated = updated,
            ReferenceEntriesChanged = persistenceDecision?.NeedsEntryPersistence == true,
            OldTable = table,
            OldEntriesSnapshot = oldEntriesSnapshot,
            NewEntriesSnapshot = newEntriesSnapshot
        };
        InvokePlaylistUpdateCallbacks(updateContext, updateCallbackActions, uri);
        return new PlaylistReloadTargetResult
        {
            SourceTable = table,
            ResultTable = newTable,
            Uri = uri,
            Updated = updated,
            Exception = failure,
            UpdateContext = updateContext
        };
    }

    private static void InvokePlaylistUpdateCallbacks(PlaylistTableUpdateContext updateContext, List<Action<PlaylistTableUpdateContext>> updateCallbackActions, Uri uri)
    {
        if (updateCallbackActions == null)
        {
            return;
        }
        try
        {
            foreach (Action<PlaylistTableUpdateContext> item in updateCallbackActions.Where(action => action != null))
            {
                item(updateContext);
            }
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_update_callback_failed table=" + FormatTextForLog(updateContext?.NewTable?.name) + " uri=" + FormatUriForLog(uri));
        }
    }

    private static System.Windows.Threading.Dispatcher GetBMSTablesDispatcher(DispatcherCollection<BMSTable> tables)
    {
        return tables?.Dispatcher ?? DispatcherHelper.UIDispatcher ?? Application.Current?.Dispatcher;
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

    private async Task<T> InvokeBMSTablesCollectionMutationAsync<T>(Func<T> mutation)
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
        return await dispatcher.InvokeAsync(mutation, System.Windows.Threading.DispatcherPriority.Normal).Task.ConfigureAwait(false);
    }

    private void InvokeBMSTablesCollectionMutation(Action mutation)
    {
        InvokeBMSTablesCollectionMutation(delegate
        {
            mutation();
            return true;
        });
    }

    private async Task AddCommittedBMSTableToVisibleCollectionAsync(BMSTable table)
    {
        bool added = await InvokeBMSTablesCollectionMutationAsync(delegate
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
                BMSTables.Add(table);
                return true;
            }
        }).ConfigureAwait(false);
        if (!added)
        {
            deleteBMSTable(table);
            throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, table?.name);
        }
    }

    private void ReplaceBMSTableInCollection(BMSTable oldTable, BMSTable newTable)
    {
        DispatcherCollection<BMSTable> tables = BMSTables;
        if (tables == null)
        {
            return;
        }

        void ReplaceCore()
        {
            int index = tables.IndexOf(oldTable);
            if (index >= 0)
            {
                tables[index] = newTable;
            }
        }

        if (tables.Dispatcher == null || tables.Dispatcher.CheckAccess())
        {
            ReplaceCore();
            return;
        }

        System.Windows.Threading.DispatcherOperation operation = tables.Dispatcher.BeginInvoke((Action)delegate
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                ReplaceCore();
            }
        });
        if (Application.Current == null)
        {
            return;
        }
        try
        {
            operation.Wait(TimeSpan.FromSeconds(5));
            if (operation.Status != System.Windows.Threading.DispatcherOperationStatus.Completed)
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn("playlist_table_replace_dispatch_wait_incomplete status=" + operation.Status);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException || ex is ThreadInterruptedException)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_table_replace_dispatch_wait_failed");
        }
    }

    /// <summary>
    /// 指定した外部プレイリストを再取得し、既存のローカル状態を維持しながら差分同期結果へ置き換えます。
    /// <c>is_external_sync</c> の有無に関わらず明示指定されたプレイリストを対象にし、<c>last_update</c> は header/data hash の既知値変化に応じて維持または更新されます。
    /// </summary>
    /// <param name="bmsTable">再同期対象のプレイリスト。</param>
    /// <param name="pageUri">再取得に使用する URI。省略時は対象プレイリストに保持された URL を使用します。</param>
    /// <returns>差分統合後のプレイリスト。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    public BMSTable ResetBMSTable(BMSTable bmsTable, Uri pageUri = null)
    {
        return ResetBMSTableAsync(bmsTable, pageUri).GetAwaiter().GetResult();
    }

    internal async Task<BMSTable> ResetBMSTableAsync(BMSTable bmsTable, Uri pageUri = null, CancellationToken cancellationToken = default)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        if (pageUri == null)
        {
            pageUri = bmsTable.Page_url ?? bmsTable.Header_url;
        }
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Playlist reload URI is not absolute.");
        }
        PlaylistReloadTargetResult result = await ReloadPlaylistTargetCoreAsync(bmsTable, pageUri, null, null, "ResetBMSTableAsync", cancellationToken).ConfigureAwait(false);
        if (result.Exception != null)
        {
            throw result.Exception;
        }
        if (Settings.Default.EnablePlaylistUrlCompletion)
        {
            SchedulePlaylistUrlCompletionRefresh("ResetBMSTableAsync");
        }
        return result.ResultTable;
    }

    /// <summary>
    /// 指定プレイリストを一覧と DB から削除します。
    /// </summary>
    /// <param name="bmsTable">削除対象のプレイリスト。</param>
    public void RemoveBMSTable(BMSTable bmsTable)
    {
        bool removed = InvokeBMSTablesCollectionMutation(delegate
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                return BMSTables.Contains(bmsTable) && BMSTables.RemoveExt(bmsTable);
            }
        });
        if (removed)
        {
            deleteBMSTable(bmsTable);
            QueueBeatorajaBmtRemoveForTable(bmsTable, "RemoveBMSTable");
        }
    }

    /// <summary>
    /// 新規の空プレイリストを生成し、一覧へ追加します。
    /// </summary>
    /// <returns>追加された新規プレイリスト。</returns>
    public BMSTable CreateBMSTable()
    {
        var bMSTable = new BMSTable
        {
            last_update = DateTime.Now
        };
        InvokeBMSTablesCollectionMutation(delegate
        {
            using (rwlockBMSTablesInitializeMin.GetReaderGuard())
            {
                using (rwlockBMSTables.GetWriterGuard())
                {
                    bMSTable.bmt_sort = ResolveNextBeatorajaBmtSort(BMSTables);
                    bMSTable.is_bmt_output = true;
                    BMSTables.Add(bMSTable);
                }
            }
        });
        return bMSTable;
    }

    /// <summary>
    /// プレイリスト内フォルダ名を変更し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="foldeNameBefore">変更前フォルダ名。</param>
    /// <param name="folderNameAfter">変更後フォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void RenameFolderBMSTable(BMSTable bmsTable, string foldeNameBefore, string folderNameAfter, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "RenameFolderBMSTable");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.RenameFolder(foldeNameBefore, folderNameAfter);
                if (commitFlag)
                {
                    ReOutputCustomFolderAndCommitToDB(bmsTable);
                }
            }
        }
    }

    /// <summary>
    /// プレイリスト内フォルダを削除し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="folderNameDelete">削除するフォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void RemoveFolderBMSTable(BMSTable bmsTable, string folderNameDelete, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "RemoveFolderBMSTable");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.RemoveFolder(folderNameDelete);
                if (commitFlag)
                {
                    ReOutputCustomFolderAndCommitToDB(bmsTable);
                }
            }
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
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void AddPlaylistEntriesToFolderBMSTable(IEnumerable<BMSTableEntry> entries, BMSTable bmsTable, string folderName, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "AddPlaylistEntriesToFolderBMSTable");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.AddBMSTableEntriesToFolder(entries, folderName);
                if (commitFlag)
                {
                    ReOutputCustomFolderAndCommitToDB(bmsTable);
                }
            }
        }
    }

    /// <summary>
    /// 指定エントリ群をプレイリストから除去し、必要なら永続化まで行います。
    /// </summary>
    /// <param name="bmsEntries">除去するエントリ群。</param>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void RemoveEntriesBMSTable(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "RemoveEntriesBMSTable");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.RemoveBMSTableEntries(bmsEntries);
                if (commitFlag)
                {
                    ReOutputCustomFolderAndCommitToDB(bmsTable);
                }
            }
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
        EnsurePlaylistEntriesLoaded(bmsTable, "ReOutputCustomFolderAndCommitToDB");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                CommitBMSTable(bmsTable);
                if (Settings.Default.OperationModeLR2DB)
                {
                    reOutputCustomFolderFiles(bmsTable);
                }
            }
        }
        QueueBeatorajaBmtExport(bmsTable, "ReOutputCustomFolderAndCommitToDB");
    }

    /// <summary>
    /// プレイリスト本体のヘッダ情報を永続化し、LR2DB モード時はカスタムフォルダだけを再出力します。
    /// </summary>
    /// <param name="bmsTables">反映対象のプレイリスト群。</param>
    /// <param name="reason">性能ログに残す理由。</param>
    /// <param name="progressCallback">処理済み件数、全件数、処理中プレイリスト名を通知する callback。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTables"/> が <see langword="null"/> の場合。</exception>
    internal void ReOutputCustomFoldersAndCommitHeadersToDB(IEnumerable<BMSTable> bmsTables, string reason, Action<int, int, string> progressCallback = null)
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

        if (!Settings.Default.OperationModeLR2DB)
        {
            CommitBMSTableHeadersToDB(tableList);
            return;
        }

        ReOutputCustomFoldersForTablesCoreAsync(
            tableList,
            reason,
            "playlist_custom_folder_output_bulk",
            forceWriteAllFiles: true,
            throwOnProjectionFailure: true,
            buildPreparedDataSurface: false,
            yieldBetweenTables: false,
            progressCallback)
            .GetAwaiter()
            .GetResult();
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
        IReadOnlyDictionary<BMSTable, string> outputBaseDirPathBeforeByTable = null)
    {
        if (bmsTables == null)
        {
            throw new ArgumentNullException(nameof(bmsTables));
        }

        const string operation = "playlist_custom_folder_output_migrate_bulk";
        var prepareStopwatch = Stopwatch.StartNew();
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

        LogPlaylistPerformance(operation + " prepare_start"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableList.Count);
        var headerCommitStopwatch = Stopwatch.StartNew();
        CommitBMSTableHeadersToDB(tableList);
        headerCommitStopwatch.Stop();
        LogPlaylistPerformance(operation + " header_commit_done"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableList.Count
            + " elapsedMs=" + headerCommitStopwatch.ElapsedMilliseconds);
        if (!Settings.Default.OperationModeLR2DB)
        {
            prepareStopwatch.Stop();
            LogPlaylistPerformance(operation + " skipped"
                + " reason=" + (reason ?? "unknown")
                + " detail=operation_mode_not_lr2db"
                + " tableCount=" + tableList.Count
                + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds);
            return;
        }

        List<CustomFolderOutputDirectoryMigrationPlan> migrationPlans = [];
        foreach (BMSTable table in tableList)
        {
            if (table == null
                || string.IsNullOrWhiteSpace(table.Output_dir)
                || outputDirPathBeforeByTable?.TryGetValue(table, out string beforeDirectory) != true
                || string.IsNullOrWhiteSpace(beforeDirectory))
            {
                continue;
            }

            string afterDirectory = GetCustomFolderOutputDirectory(table);
            if (string.IsNullOrWhiteSpace(afterDirectory)
                || IsSameCustomFolderDirectory(beforeDirectory, afterDirectory))
            {
                continue;
            }

            migrationPlans.Add(new CustomFolderOutputDirectoryMigrationPlan
            {
                Table = table,
                OutputDirectoryBefore = beforeDirectory,
                OutputDirectoryAfter = afterDirectory,
                WasRootFolderBefore = wasRootFolderBeforeByTable?.TryGetValue(table, out bool wasRootFolderBefore) == true
                    ? wasRootFolderBefore
                    : table.is_root_folder,
                RootOutputBaseDirBefore = rootOutputBaseDirBefore,
                OutputBaseDirectoryBefore = outputBaseDirPathBeforeByTable?.TryGetValue(table, out string outputBaseDirectoryBefore) == true
                    ? outputBaseDirectoryBefore
                    : null,
                InferOutputBaseDirectoryBeforeWhenMissing = outputBaseDirPathBeforeByTable == null
            });
        }
        if (migrationPlans.Count == 0)
        {
            prepareStopwatch.Stop();
            LogPlaylistPerformance(operation + " skipped"
                + " reason=" + (reason ?? "unknown")
                + " detail=no_directory_migration"
                + " tableCount=" + tableList.Count
                + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds);
            return;
        }

        prepareStopwatch.Stop();
        LogPlaylistPerformance(operation + " prepare_done"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + tableList.Count
            + " migrationCount=" + migrationPlans.Count
            + " elapsedMs=" + prepareStopwatch.ElapsedMilliseconds);
        ReOutputCustomFoldersForTablesCoreAsync(
            [.. migrationPlans.Select(plan => plan.Table)],
            reason,
            operation,
            forceWriteAllFiles: true,
            throwOnProjectionFailure: true,
            buildPreparedDataSurface: false,
            yieldBetweenTables: false,
            progressCallback)
            .GetAwaiter()
            .GetResult();
        CleanupMigratedCustomFolderOutputDirectories(migrationPlans, reason, progressCallback);
    }

    private sealed class CustomFolderOutputDirectoryMigrationPlan
    {
        public BMSTable Table { get; set; }

        public string OutputDirectoryBefore { get; set; }

        public string OutputDirectoryAfter { get; set; }

        public bool WasRootFolderBefore { get; set; }

        public string RootOutputBaseDirBefore { get; set; }

        public string OutputBaseDirectoryBefore { get; set; }

        public bool InferOutputBaseDirectoryBeforeWhenMissing { get; set; } = true;
    }

    private void CleanupMigratedCustomFolderOutputDirectories(
        IReadOnlyCollection<CustomFolderOutputDirectoryMigrationPlan> migrationPlans,
        string reason,
        Action<int, int, string> progressCallback = null)
    {
        if (migrationPlans == null || migrationPlans.Count == 0)
        {
            return;
        }

        const string operation = "playlist_custom_folder_output_migrate_bulk";
        var stopwatch = Stopwatch.StartNew();
        var directoryRowScopeDirectories = new List<string>();
        int removedDirectoryCount = 0;
        int failedCount = 0;
        LogPlaylistPerformance(operation + " old_output_cleanup_start"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + migrationPlans.Count);
        int index = 0;
        foreach (CustomFolderOutputDirectoryMigrationPlan plan in migrationPlans)
        {
            index++;
            if (plan == null
                || string.IsNullOrWhiteSpace(plan.OutputDirectoryBefore)
                || string.IsNullOrWhiteSpace(plan.OutputDirectoryAfter)
                || IsSameCustomFolderDirectory(plan.OutputDirectoryBefore, plan.OutputDirectoryAfter))
            {
                continue;
            }

            progressCallback?.Invoke(index - 1, migrationPlans.Count, plan.Table?.name ?? string.Empty);
            LogPlaylistPerformance(operation + " old_output_cleanup_delete_start"
                + " reason=" + (reason ?? "unknown")
                + " index=" + index
                + " total=" + migrationPlans.Count
                + " name=" + QuoteLogValue(plan.Table?.name)
                + " outputDir=" + QuoteLogValue(plan.OutputDirectoryBefore));
            var deleteStopwatch = Stopwatch.StartNew();
            if (!DeleteCustomFolderOutputDirectoryTree(
                plan.OutputDirectoryBefore,
                CreateCustomFolderManagedOutputBaseScopes(
                    plan.Table,
                    plan.WasRootFolderBefore,
                    plan.RootOutputBaseDirBefore,
                    plan.OutputBaseDirectoryBefore,
                    plan.InferOutputBaseDirectoryBeforeWhenMissing)))
            {
                failedCount++;
                deleteStopwatch.Stop();
                LogPlaylistPerformance(operation + " old_output_cleanup_delete_failed"
                    + " reason=" + (reason ?? "unknown")
                    + " index=" + index
                    + " total=" + migrationPlans.Count
                    + " name=" + QuoteLogValue(plan.Table?.name)
                    + " elapsedMs=" + deleteStopwatch.ElapsedMilliseconds);
                continue;
            }

            deleteStopwatch.Stop();
            removedDirectoryCount++;
            directoryRowScopeDirectories.AddRange(CreateCustomFolderDirectoryPruneScopes(
                plan.OutputDirectoryBefore,
                plan.WasRootFolderBefore,
                plan.RootOutputBaseDirBefore));
            progressCallback?.Invoke(index, migrationPlans.Count, plan.Table?.name ?? string.Empty);
            LogPlaylistPerformance(operation + " old_output_cleanup_delete_done"
                + " reason=" + (reason ?? "unknown")
                + " index=" + index
                + " total=" + migrationPlans.Count
                + " name=" + QuoteLogValue(plan.Table?.name)
                + " elapsedMs=" + deleteStopwatch.ElapsedMilliseconds);
        }

        directoryRowScopeDirectories = [.. directoryRowScopeDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        LogPlaylistPerformance(operation + " old_output_cleanup_prune_start"
            + " reason=" + (reason ?? "unknown")
            + " directoryRowScopeCount=" + directoryRowScopeDirectories.Count);
        SyncCustomFolderRowsByPaths([], directoryRowScopeDirectories);
        stopwatch.Stop();
        LogPlaylistPerformance(operation + " old_output_cleanup_done"
            + " reason=" + (reason ?? "unknown")
            + " tableCount=" + migrationPlans.Count
            + " removedDirectoryCount=" + removedDirectoryCount
            + " prunePathCount=0"
            + " directoryRowScopeCount=" + directoryRowScopeDirectories.Count
            + " failedCount=" + failedCount
            + " elapsedMs=" + stopwatch.ElapsedMilliseconds);
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
        EnsurePlaylistEntriesLoaded(bmsTable, "MigrateCustomFolderOutputDirectoryAndCommitToDB");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                CommitBMSTable(bmsTable);
                if (Settings.Default.OperationModeLR2DB)
                {
                    if (string.IsNullOrWhiteSpace(outputDirPathAfter))
                    {
                        outputDirPathAfter = GetCustomFolderOutputDirectory(bmsTable);
                    }
                    migrateCustomFolderOutputDirectoryFiles(
                        bmsTable,
                        outputDirPathBefore,
                        outputDirPathAfter,
                        wasRootFolderBefore ?? bmsTable.is_root_folder,
                        rootOutputBaseDirBefore,
                        outputBaseDirBefore,
                        inferOutputBaseDirBeforeWhenMissing);
                }
            }
        }
        if (queueBeatorajaBmtExport)
        {
            QueueBeatorajaBmtExport(bmsTable, "MigrateCustomFolderOutputDirectoryAndCommitToDB");
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
        EnsurePlaylistEntriesLoaded(bmsTable, "CommitBMSTableWithEntriesToDB");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                CommitBMSTable(bmsTable);
            }
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
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                commitBMSTableHeaderOnly(bmsTable);
            }
        }
    }

    /// <summary>
    /// 複数プレイリスト本体のヘッダ情報を 1 transaction で DB へ保存します。
    /// </summary>
    /// <param name="bmsTables">保存対象のプレイリスト群。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTables"/> が <see langword="null"/> の場合。</exception>
    internal void CommitBMSTableHeadersToDB(IEnumerable<BMSTable> bmsTables)
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
        using var lr2Song = new LR2SongDBExtended(lr2SongDBPath);
        string savepoint = lr2Song.SaveTransactionPoint();
        try
        {
            foreach (BMSTable bmsTable in tableList)
            {
                lr2Song.InsertOrReplace(bmsTable, typeof(LR2SongDBExtended.playlist));
                ReplacePersistedCourses(lr2Song, bmsTable);
            }
            lr2Song.Commit();
        }
        catch
        {
            lr2Song.RollbackTo(savepoint);
            throw;
        }
    }

    /// <summary>
    /// 外部プレイリスト URI を解決し、ヘッダとデータ本体を取得してプレイリストを構築します。
    /// </summary>
    /// <param name="pageUri">取得元の絶対 URI。</param>
    /// <param name="baseTable">設定引き継ぎに使う既存プレイリスト。</param>
    /// <returns>取得したプレイリスト。</returns>
    /// <exception cref="ArgumentException">URI が無効な場合。</exception>
    public BMSTable LoadExternalTable(Uri pageUri, BMSTable baseTable = null)
    {
        return LoadExternalTableAsync(pageUri, baseTable).GetAwaiter().GetResult();
    }

    internal async Task<BMSTable> LoadExternalTableAsync(Uri pageUri, BMSTable baseTable = null, CancellationToken cancellationToken = default)
    {
        if (pageUri == null || !pageUri.IsAbsoluteUri)
        {
            throw new ArgumentException(Resources.Error_URIMustBeAbsolute, "pageUri");
        }
        if (pageUri.Scheme == "bmseeker")
        {
            return LoadWalkureTable(pageUri, baseTable);
        }
        Uri originalPageUri = pageUri;
        Uri headerUri = null;
        Uri resolvedHeaderUri = null;
        Uri resolvedDataUri = null;
        BMSTable bMSTable = null;
        try
        {
            string input = await playlistHttpClient.GetStringAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
            string header_json;
            if (TryResolveHeaderUri(input, pageUri, out headerUri))
            {
                resolvedHeaderUri = (!headerUri.IsAbsoluteUri) ? new Uri(pageUri, headerUri) : headerUri;
                header_json = await playlistHttpClient.GetStringAsync(resolvedHeaderUri, null, cancellationToken).ConfigureAwait(false);
            }
            else if (LooksLikeJsonContent(input))
            {
                headerUri = pageUri;
                pageUri = null;
                resolvedHeaderUri = headerUri;
                header_json = input;
            }
            else
            {
                throw new PlaylistHeaderUriNotFoundException(originalPageUri);
            }
            bMSTable = new BMSTable();
            if (baseTable != null)
            {
                bMSTable.compat_prefix = baseTable.compat_prefix;
            }
            bMSTable.LoadHeaderJSON(header_json, pageUri, headerUri, preserveLoadedCompatPrefix: baseTable != null);
            if (baseTable != null)
            {
                bMSTable.playlist_id = baseTable.playlist_id;
                bMSTable.name = baseTable.name;
                bMSTable.symbol = baseTable.symbol;
                bMSTable.ignore_folder_output = baseTable.ignore_folder_output;
                bMSTable.is_external_sync = baseTable.is_external_sync;
                bMSTable.Output_dir = baseTable.Output_dir;
                bMSTable.is_root_folder = baseTable.is_root_folder;
                bMSTable.custom_folder_output_base_name = baseTable.custom_folder_output_base_name;
                bMSTable.bmt_sort = baseTable.bmt_sort;
                bMSTable.is_bmt_output = baseTable.is_bmt_output;
            }
            resolvedDataUri = bMSTable.GetAbsoluteDataUrl();
            if (resolvedDataUri == null)
            {
                throw new InvalidOperationException("Failed to resolve playlist data_url. rawDataUrl=" + FormatTextForLog(bMSTable.data_url) + " pageUrl=" + FormatUriForLog(bMSTable.Page_url) + " headerUrl=" + FormatUriForLog(bMSTable.Header_url) + " sourcePage=" + FormatUriForLog(originalPageUri));
            }
            string data_json = await playlistHttpClient.GetStringAsync(resolvedDataUri, null, cancellationToken).ConfigureAwait(false);
            bMSTable.LoadDataJSON(data_json);
            return bMSTable;
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_external_load_failed pageUri=" + FormatUriForLog(originalPageUri) + " resolvedPageUri=" + FormatUriForLog(pageUri) + " rawHeaderUri=" + FormatUriForLog(headerUri) + " resolvedHeaderUri=" + FormatUriForLog(resolvedHeaderUri) + " rawDataUrl=" + FormatTextForLog(bMSTable?.data_url) + " storedPageUrl=" + FormatUriForLog(bMSTable?.Page_url) + " storedHeaderUrl=" + FormatUriForLog(bMSTable?.Header_url) + " resolvedDataUri=" + FormatUriForLog(resolvedDataUri) + " baseTable=" + FormatTextForLog(baseTable?.name));
            throw;
        }
    }

    private static bool TryResolveHeaderUri(string input, Uri pageUri, out Uri headerUri)
    {
        headerUri = null;
        if (string.IsNullOrWhiteSpace(input) || pageUri == null)
        {
            return false;
        }
        var stringBuilder = new StringBuilder();
        try
        {
            XDocument xDocument;
            using (var reader = new SgmlReader
            {
                Href = pageUri.AbsoluteUri,
                InputStream = new StringReader(input),
                IgnoreDtd = true,
                ErrorLog = new StringWriter(stringBuilder)
            })
            {
                xDocument = XDocument.Load(reader);
            }
            XNamespace xNamespace = xDocument.Root.Name.Namespace;
            string text = (from item in xDocument.Descendants(xNamespace + "meta")
                           let attrName = item.Attribute("name")
                           let attrCont = item.Attribute("content")
                           where attrName != null && attrCont != null && attrName.Value == "bmstable" && !string.IsNullOrWhiteSpace(attrCont.Value)
                           select attrCont.Value).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(text))
            {
                headerUri = new Uri(text, UriKind.RelativeOrAbsolute);
                return true;
            }
        }
        catch
        {
        }
        Match match = new Regex("name\\s*=\\s*\"bmstable\"[^<>]*content\\s*=\\s*\"([^?\"<>]+)[\"?<>]", RegexOptions.IgnoreCase).Match(input);
        if (!match.Success || string.IsNullOrWhiteSpace(match.Groups[1].Value))
        {
            return false;
        }
        headerUri = new Uri(match.Groups[1].Value, UriKind.RelativeOrAbsolute);
        return true;
    }

    private static bool LooksLikeJsonContent(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }
        string text = input.TrimStart('\ufeff', ' ', '\t', '\r', '\n');
        return text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal);
    }

    /// <summary>
    /// 外部テーブルを再取得し、既存プレイリストが持つローカル状態を維持した差分結果を生成します。
    /// </summary>
    /// <param name="oldTable">再取得前のプレイリスト。</param>
    /// <param name="pageUri">再取得に使用する URI。省略時は既存プレイリストに保持された URL を使用します。</param>
    /// <param name="logLastUpdateDecision"><c>last_update</c> の補正判断を INFO ログへ出力するか。</param>
    /// <returns>差分統合後のプレイリスト。</returns>
    private BMSTable MergeReloadedBMSTableWithExistingState(BMSTable oldTable, Uri pageUri = null, bool logLastUpdateDecision = false)
    {
        if (pageUri == null)
        {
            pageUri = oldTable.Page_url ?? oldTable.Header_url;
        }
        BMSTable reloadedTable = reloadBMSTable(oldTable, pageUri);
        EnsurePlaylistEntriesLoaded(oldTable, "MergeReloadedBMSTableWithExistingState");
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)), out bool _, logLastUpdateDecision);
    }

    private async Task<BMSTable> MergeReloadedBMSTableWithExistingStateAsync(BMSTable oldTable, Uri pageUri = null, bool logLastUpdateDecision = false, CancellationToken cancellationToken = default)
    {
        if (pageUri == null)
        {
            pageUri = oldTable.Page_url ?? oldTable.Header_url;
        }
        BMSTable reloadedTable = await reloadBMSTableAsync(oldTable, pageUri, cancellationToken).ConfigureAwait(false);
        EnsurePlaylistEntriesLoaded(oldTable, "MergeReloadedBMSTableWithExistingStateAsync");
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)), out bool _, logLastUpdateDecision);
    }

    /// <summary>
    /// 再取得済みプレイリストへ既存ローカル状態をマージします。
    /// </summary>
    /// <param name="oldTable">既存プレイリスト。</param>
    /// <param name="reloadedTable">外部から再取得したプレイリスト。</param>
    /// <param name="logLastUpdateDecision"><c>last_update</c> の補正判断を INFO ログへ出力するか。</param>
    /// <returns>マージ後のプレイリスト。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="oldTable"/> または <paramref name="reloadedTable"/> が <see langword="null"/> の場合。</exception>
    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where(entry => !entry.is_removed)), out bool _, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out hasContentChanges, out _, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, out bool hasStateToPersist, bool logLastUpdateDecision = false)
    {
        BMSTable mergedTable = MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out PlaylistReloadPersistenceDecision persistenceDecision, logLastUpdateDecision);
        hasContentChanges = persistenceDecision.UpdatesLastUpdate;
        hasStateToPersist = persistenceDecision.NeedsStatePersistence;
        return mergedTable;
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out PlaylistReloadPersistenceDecision persistenceDecision, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out _, out _, out persistenceDecision, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, out bool hasStateToPersist, out PlaylistReloadPersistenceDecision persistenceDecision, bool logLastUpdateDecision = false)
    {
        if (oldTable == null)
        {
            throw new ArgumentNullException("oldTable");
        }
        if (reloadedTable == null)
        {
            throw new ArgumentNullException("reloadedTable");
        }

        BMSTable newTable = reloadedTable;
        var list = persistedActiveRows?.Where(row => row != null).ToList();
        IReadOnlyList<ComparablePlaylistEntryRow> normalizedPersistedRows = list ?? (IReadOnlyList<ComparablePlaylistEntryRow>)[];
        IReadOnlyList<ComparablePlaylistEntryRow> normalizedReloadedRows = BuildComparablePlaylistEntryRows(newTable.entries);
        PlaylistContentDiffResult playlistContentDiffResult = AnalyzePlaylistContentDiff(normalizedPersistedRows, normalizedReloadedRows);
        PlaylistHashChangeResult hashChangeResult = AnalyzePlaylistHashChanges(oldTable, newTable);
        persistenceDecision = new PlaylistReloadPersistenceDecision
        {
            EntryFingerprintChanged = playlistContentDiffResult.HasChanges,
            HeaderKnownChanged = hashChangeResult.HeaderKnownChanged,
            HeaderHashInitialized = hashChangeResult.HeaderHashInitialized,
            DataKnownChanged = hashChangeResult.DataKnownChanged,
            DataHashInitialized = hashChangeResult.DataHashInitialized
        };
        hasContentChanges = persistenceDecision.UpdatesLastUpdate;
        hasStateToPersist = persistenceDecision.NeedsStatePersistence;

        Dictionary<string, List<BMSTableEntry>> newEntriesByMd5 = BuildEntryLookup(newTable.entries, entry => entry.md5, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<BMSTableEntry>> newEntriesBySha256 = BuildEntryLookup(newTable.entries, entry => entry.sha256, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<BMSTableEntry>> newEntriesByComparableRow = BuildComparableEntryLookup(newTable.entries);

        List<BMSTableEntry> matchedOldEntries = [.. oldTable.entries.Where(delegate (BMSTableEntry oe)
        {
            BMSTableEntry matchedNew = ResolveReloadedEntryMatch(oe, newEntriesByMd5, newEntriesBySha256, newEntriesByComparableRow);
            if (matchedNew == null)
            {
                return false;
            }
            matchedNew.memo = oe.memo;
            matchedNew.adddate = oe.adddate;
            matchedNew.is_removed = false;
            return true;
        })];

        DateTime oldLastUpdate = oldTable.last_update;
        DateTime reloadedLastUpdate = newTable.last_update;
        if (oldTable.playlist_id.HasValue)
        {
            newTable.last_update = persistenceDecision.UpdatesLastUpdate ? DateTime.Now : oldTable.last_update;
        }
        else if (persistenceDecision.UpdatesLastUpdate)
        {
            newTable.last_update = ((reloadedLastUpdate != default) ? reloadedLastUpdate : DateTime.Now);
        }
        else
        {
            newTable.last_update = ((reloadedLastUpdate != default) ? reloadedLastUpdate : oldTable.last_update);
        }

        List<BMSTableEntry> removedEntries = [.. oldTable.entries.Except(matchedOldEntries)];
        foreach (BMSTableEntry item in removedEntries)
        {
            item.is_removed = true;
        }
        newTable.entries = [.. newTable.entries, .. removedEntries];
        bool lastUpdateChanged = newTable.last_update != oldLastUpdate;
        if (logLastUpdateDecision)
        {
            if (playlistContentDiffResult.HasChanges)
            {
                LogPlaylistContentDiff(newTable.name, playlistContentDiffResult);
            }
            Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync last_update_decision table=" + (newTable.name ?? string.Empty) + " changed=" + persistenceDecision.UpdatesLastUpdate.ToString().ToLowerInvariant() + " entryFingerprintChanged=" + playlistContentDiffResult.HasChanges.ToString().ToLowerInvariant() + " headerChanged=" + hashChangeResult.HeaderKnownChanged.ToString().ToLowerInvariant() + " dataChanged=" + hashChangeResult.DataKnownChanged.ToString().ToLowerInvariant() + " headerInitialized=" + hashChangeResult.HeaderHashInitialized.ToString().ToLowerInvariant() + " dataInitialized=" + hashChangeResult.DataHashInitialized.ToString().ToLowerInvariant() + " persistHeader=" + persistenceDecision.NeedsHeaderPersistence.ToString().ToLowerInvariant() + " persistEntry=" + persistenceDecision.NeedsEntryPersistence.ToString().ToLowerInvariant() + " old=" + oldLastUpdate.ToString("O") + " reloaded=" + reloadedLastUpdate.ToString("O") + " final=" + newTable.last_update.ToString("O"));
        }
        return newTable;
    }

    private static PlaylistHashChangeResult AnalyzePlaylistHashChanges(BMSTable oldTable, BMSTable newTable)
    {
        var result = new PlaylistHashChangeResult();
        if (oldTable == null || newTable == null)
        {
            return result;
        }
        ApplyHashChange(
            oldTable.header_sha256,
            newTable.header_sha256,
            () => result.HeaderHashInitialized = true,
            () => result.HeaderKnownChanged = true);
        ApplyHashChange(
            oldTable.data_sha256,
            newTable.data_sha256,
            () => result.DataHashInitialized = true,
            () => result.DataKnownChanged = true);
        return result;
    }

    private static void ApplyHashChange(string oldHash, string newHash, Action markInitialized, Action markKnownChanged)
    {
        string normalizedOldHash = NormalizeHash(oldHash);
        string normalizedNewHash = NormalizeHash(newHash);
        if (string.Equals(normalizedOldHash, normalizedNewHash, StringComparison.Ordinal))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(normalizedOldHash) && !string.IsNullOrWhiteSpace(normalizedNewHash))
        {
            markInitialized?.Invoke();
            return;
        }
        markKnownChanged?.Invoke();
    }

    private static Dictionary<string, List<BMSTableEntry>> BuildEntryLookup(IEnumerable<BMSTableEntry> entries, Func<BMSTableEntry, string> keySelector, IEqualityComparer<string> comparer)
    {
        return entries.Where(delegate (BMSTableEntry entry)
        {
            string text = keySelector(entry);
            return !string.IsNullOrWhiteSpace(text);
        }).GroupBy(entry => keySelector(entry), comparer).ToDictionary(group => group.Key, group => group.ToList(), comparer);
    }

    private static BMSTableEntry ResolveReloadedEntryMatch(BMSTableEntry oldEntry, Dictionary<string, List<BMSTableEntry>> entriesByMd5, Dictionary<string, List<BMSTableEntry>> entriesBySha256, Dictionary<string, List<BMSTableEntry>> entriesByComparableRow)
    {
        List<BMSTableEntry> list = GetEntryMatchCandidates(oldEntry.md5, entriesByMd5) ?? GetEntryMatchCandidates(oldEntry.sha256, entriesBySha256);
        if (list == null)
        {
            ComparablePlaylistEntryRow comparableRow = CreateComparablePlaylistEntryRow(oldEntry);
            if (comparableRow != null)
            {
                list = GetEntryMatchCandidates(comparableRow.Fingerprint, entriesByComparableRow);
            }
        }
        return SelectBestMatchedEntry(oldEntry, list);
    }

    private IReadOnlyList<BMSTableEntry> LoadPersistedActivePlaylistEntries(int? playlistId)
    {
        return LoadPersistedPlaylistEntries(playlistId, activeOnly: true);
    }

    private IReadOnlyList<BMSTableEntry> LoadPersistedPlaylistEntries(int? playlistId, bool activeOnly)
    {
        if (!playlistId.HasValue)
        {
            return [];
        }
        using var lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        using (BMSTableEntry.BeginBulkLoadParseSuppression())
        {
            string sql = "SELECT * FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.playlist_id) + " = ?";
            if (activeOnly)
            {
                sql += " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(entry => entry.is_removed) + " = 0";
            }
            sql += ";";
            return lR2SongDBExtended.Query<BMSTableEntry>(sql, playlistId.Value);
        }
    }

    private static Dictionary<string, List<BMSTableEntry>> BuildComparableEntryLookup(IEnumerable<BMSTableEntry> entries)
    {
        var dictionary = new Dictionary<string, List<BMSTableEntry>>(StringComparer.Ordinal);
        foreach (BMSTableEntry entry in entries ?? [])
        {
            ComparablePlaylistEntryRow comparableRow = CreateComparablePlaylistEntryRow(entry);
            if (comparableRow == null)
            {
                continue;
            }
            if (!dictionary.TryGetValue(comparableRow.Fingerprint, out List<BMSTableEntry> value))
            {
                value = [];
                dictionary[comparableRow.Fingerprint] = value;
            }
            value.Add(entry);
        }
        return dictionary;
    }

    internal static IReadOnlyList<ComparablePlaylistEntryRow> BuildComparablePlaylistEntryRows(IEnumerable<BMSTableEntry> entries)
    {
        if (entries == null)
        {
            return [];
        }
        return [.. entries.Select(CreateComparablePlaylistEntryRow).Where(row => row != null)];
    }

    internal static ComparablePlaylistEntryRow CreateComparablePlaylistEntryRow(BMSTableEntry entry)
    {
        if (entry == null)
        {
            return null;
        }
        var comparableRow = new ComparablePlaylistEntryRow
        {
            Md5 = NormalizeHash(entry.md5),
            Sha256 = NormalizeHash(entry.sha256),
            Level = NormalizeLevel(entry.level),
            Title = NormalizeText(entry.title),
            Artist = NormalizeText(entry.artist),
            Lr2BmsId = NormalizeText(entry.lr2_bmsid),
            Url = NormalizeText(entry.url),
            UrlDiff = NormalizeText(entry.url_diff),
            NameDiff = NormalizeText(entry.name_diff),
            Comment = NormalizeText(entry.comment)
        };
        if (!HasMeaningfulComparableContent(comparableRow))
        {
            return null;
        }
        return new ComparablePlaylistEntryRow
        {
            Md5 = comparableRow.Md5,
            Sha256 = comparableRow.Sha256,
            Level = comparableRow.Level,
            Title = comparableRow.Title,
            Artist = comparableRow.Artist,
            Lr2BmsId = comparableRow.Lr2BmsId,
            Url = comparableRow.Url,
            UrlDiff = comparableRow.UrlDiff,
            NameDiff = comparableRow.NameDiff,
            Comment = comparableRow.Comment,
            Fingerprint = BuildComparablePlaylistEntryFingerprint(comparableRow)
        };
    }

    internal static PlaylistContentDiffResult AnalyzePlaylistContentDiff(IEnumerable<ComparablePlaylistEntryRow> persistedRows, IEnumerable<ComparablePlaylistEntryRow> reloadedRows)
    {
        Dictionary<string, int> dictionary = BuildComparableRowFingerprintCounts(persistedRows);
        Dictionary<string, int> dictionary2 = BuildComparableRowFingerprintCounts(reloadedRows);
        var playlistContentDiffResult = new PlaylistContentDiffResult
        {
            PersistedOnlySamples = [],
            ReloadedOnlySamples = []
        };
        List<string> list = null;
        List<string> list2 = null;
        foreach (string item in dictionary.Keys.Union(dictionary2.Keys, StringComparer.Ordinal).OrderBy(key => key, StringComparer.Ordinal))
        {
            dictionary.TryGetValue(item, out int value);
            dictionary2.TryGetValue(item, out int value2);
            if (value == value2)
            {
                continue;
            }
            if (value > value2)
            {
                int num = value - value2;
                playlistContentDiffResult.PersistedOnlyCount += num;
                list ??= new List<string>(PlaylistDiffSampleLogCount);
                AppendDiffSamples(list, item, num);
            }
            else
            {
                int num2 = value2 - value;
                playlistContentDiffResult.ReloadedOnlyCount += num2;
                list2 ??= new List<string>(PlaylistDiffSampleLogCount);
                AppendDiffSamples(list2, item, num2);
            }
        }
        playlistContentDiffResult.HasChanges = playlistContentDiffResult.PersistedOnlyCount > 0 || playlistContentDiffResult.ReloadedOnlyCount > 0;
        playlistContentDiffResult.PersistedOnlySamples = (IReadOnlyList<string>)(list ?? (IReadOnlyList<string>)[]);
        playlistContentDiffResult.ReloadedOnlySamples = (IReadOnlyList<string>)(list2 ?? (IReadOnlyList<string>)[]);
        return playlistContentDiffResult;
    }

    internal static bool HasPlaylistContentChanges(IEnumerable<ComparablePlaylistEntryRow> persistedRows, IEnumerable<ComparablePlaylistEntryRow> reloadedRows)
    {
        return AnalyzePlaylistContentDiff(persistedRows, reloadedRows).HasChanges;
    }

    private static Dictionary<string, int> BuildComparableRowFingerprintCounts(IEnumerable<ComparablePlaylistEntryRow> rows)
    {
        var dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rows == null)
        {
            return dictionary;
        }
        foreach (ComparablePlaylistEntryRow row in rows.Where(row => row != null))
        {
            if (dictionary.TryGetValue(row.Fingerprint, out int value))
            {
                dictionary[row.Fingerprint] = value + 1;
            }
            else
            {
                dictionary[row.Fingerprint] = 1;
            }
        }
        return dictionary;
    }

    private static void AppendDiffSamples(List<string> samples, string fingerprint, int count)
    {
        if (samples == null || count <= 0)
        {
            return;
        }
        int num = PlaylistDiffSampleLogCount - samples.Count;
        if (num <= 0)
        {
            return;
        }
        for (int i = 0; i < count && i < num; i++)
        {
            samples.Add(fingerprint);
        }
    }

    private static void LogPlaylistContentDiff(string tableName, PlaylistContentDiffResult diffResult)
    {
        if (diffResult == null || !diffResult.HasChanges)
        {
            return;
        }
        Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync diff_summary table=" + (tableName ?? string.Empty) + " persistedOnlyCount=" + diffResult.PersistedOnlyCount + " reloadedOnlyCount=" + diffResult.ReloadedOnlyCount + " sampleCount=" + PlaylistDiffSampleLogCount);
        Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync diff_samples table=" + (tableName ?? string.Empty) + " persistedOnly=[" + JoinDiffSamplesForLog(diffResult.PersistedOnlySamples) + "] reloadedOnly=[" + JoinDiffSamplesForLog(diffResult.ReloadedOnlySamples) + "]");
    }

    private static string JoinDiffSamplesForLog(IEnumerable<string> samples)
    {
        IEnumerable<string> enumerable = samples ?? (IEnumerable<string>)[];
        return string.Join(", ", enumerable.Select(EscapeFingerprintForLog));
    }

    private static string EscapeFingerprintForLog(string fingerprint)
    {
        if (fingerprint == null)
        {
            return string.Empty;
        }
        return fingerprint.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static bool HasMeaningfulComparableContent(ComparablePlaylistEntryRow row)
    {
        return row != null && (!string.IsNullOrWhiteSpace(row.Md5) || !string.IsNullOrWhiteSpace(row.Sha256) || !string.IsNullOrWhiteSpace(row.Level) || !string.IsNullOrWhiteSpace(row.Title) || !string.IsNullOrWhiteSpace(row.Artist) || !string.IsNullOrWhiteSpace(row.Lr2BmsId) || !string.IsNullOrWhiteSpace(row.Url) || !string.IsNullOrWhiteSpace(row.UrlDiff) || !string.IsNullOrWhiteSpace(row.NameDiff) || !string.IsNullOrWhiteSpace(row.Comment));
    }

    private static string NormalizeHash(string value)
    {
        string text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        return text.ToLowerInvariant();
    }

    private static string NormalizeText(string value)
    {
        if (value == null)
        {
            return string.Empty;
        }
        int num = value.IndexOf('\0');
        if (num >= 0)
        {
            value = value.Substring(0, num);
        }
        var stringBuilder = new StringBuilder(value.Length);
        foreach (char c in value)
        {
            if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')
            {
                continue;
            }
            stringBuilder.Append(c);
        }
        return stringBuilder.ToString().Trim();
    }

    private static string NormalizeLevel(double? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }
        return value.Value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string BuildComparablePlaylistEntryFingerprint(ComparablePlaylistEntryRow row)
    {
        var stringBuilder = new StringBuilder();
        AppendComparableFingerprintPart(stringBuilder, row.Md5);
        AppendComparableFingerprintPart(stringBuilder, row.Sha256);
        AppendComparableFingerprintPart(stringBuilder, row.Level);
        AppendComparableFingerprintPart(stringBuilder, row.Title);
        AppendComparableFingerprintPart(stringBuilder, row.Artist);
        AppendComparableFingerprintPart(stringBuilder, row.Lr2BmsId);
        AppendComparableFingerprintPart(stringBuilder, row.Url);
        AppendComparableFingerprintPart(stringBuilder, row.UrlDiff);
        AppendComparableFingerprintPart(stringBuilder, row.NameDiff);
        AppendComparableFingerprintPart(stringBuilder, row.Comment);
        return stringBuilder.ToString();
    }

    private static void AppendComparableFingerprintPart(StringBuilder builder, string value)
    {
        string text = value ?? string.Empty;
        builder.Append(text.Length).Append(':').Append(text).Append('|');
    }

    private static List<BMSTableEntry> GetEntryMatchCandidates(string key, Dictionary<string, List<BMSTableEntry>> lookup)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }
        lookup.TryGetValue(key, out List<BMSTableEntry> value);
        if (value == null || value.Count == 0)
        {
            return null;
        }
        return value;
    }

    private static BMSTableEntry SelectBestMatchedEntry(BMSTableEntry oldEntry, List<BMSTableEntry> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return null;
        }
        if (candidates.Count == 1)
        {
            return candidates[0];
        }
        return candidates.OrderBy(ne => Math.Abs((ne.level ?? 0.0) - (oldEntry.level ?? 0.0))).First();
    }

    /// <summary>
    /// プレイリストの構成差分有無を判定します。
    /// </summary>
    /// <param name="oldTable">既存プレイリスト。</param>
    /// <param name="newTable">再取得プレイリスト。</param>
    /// <param name="matchedOldEntryCount">新旧で対応付けられた既存エントリ数。</param>
    /// <returns>構成差分があれば <see langword="true"/>。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="oldTable"/> または <paramref name="newTable"/> が <see langword="null"/> の場合。</exception>
    internal static bool HasPlaylistStructuralChanges(BMSTable oldTable, BMSTable newTable, int matchedOldEntryCount)
    {
        if (oldTable == null)
        {
            throw new ArgumentNullException("oldTable");
        }
        if (newTable == null)
        {
            throw new ArgumentNullException("newTable");
        }
        List<string> oldFolderList = oldTable.folder_list;
        List<string> newFolderList = newTable.folder_list;
        return newTable.entries.Count != matchedOldEntryCount || matchedOldEntryCount != oldTable.entries.Where(entry => !entry.is_removed).Count() || oldFolderList.Except(newFolderList).Any() || newFolderList.Except(oldFolderList).Any();
    }

    /// <summary>
    /// 既存プレイリストの保持設定を引き継いだまま、外部ソースから生の再取得結果を作成します。
    /// </summary>
    /// <param name="bmsTable">再取得対象の既存プレイリスト。</param>
    /// <param name="pageUri">再取得に使う URI。省略時はプレイリスト保持値を使用します。</param>
    /// <returns>未マージの再取得プレイリスト。</returns>
    private BMSTable reloadBMSTable(BMSTable bmsTable, Uri pageUri = null)
    {
        if (pageUri == null)
        {
            pageUri = bmsTable.Page_url ?? bmsTable.Header_url;
        }
        return LoadExternalTable(pageUri, bmsTable);
    }

    private async Task<BMSTable> reloadBMSTableAsync(BMSTable bmsTable, Uri pageUri = null, CancellationToken cancellationToken = default)
    {
        if (pageUri == null)
        {
            pageUri = bmsTable.Page_url ?? bmsTable.Header_url;
        }
        return await LoadExternalTableAsync(pageUri, bmsTable, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 単一プレイリストを DB へ保存します。
    /// </summary>
    /// <param name="bmsTable">保存対象のプレイリスト。</param>
    private void CommitBMSTable(BMSTable bmsTable)
    {
        CommitBMSTable([bmsTable]);
    }

    /// <summary>
    /// 複数プレイリストを DB へ保存し、対応するエントリも全置換します。
    /// </summary>
    /// <param name="bmsTables">保存対象のプレイリスト群。</param>
    private void CommitBMSTable(IEnumerable<BMSTable> bmsTables)
    {
        try
        {
            List<BMSTable> tableList = bmsTables?.Where(table => table != null).ToList() ?? [];
            foreach (BMSTable table in tableList)
            {
                EnsurePlaylistEntriesLoaded(table, "CommitBMSTable");
            }
            var lr2Song = new LR2SongDBExtended(lr2SongDBPath);
            try
            {
                lr2Song.BeginTransaction();
                foreach (BMSTable bmsTable in tableList)
                {
                    lr2Song.InsertOrReplace(bmsTable, typeof(LR2SongDBExtended.playlist));
                    ReplacePersistedCourses(lr2Song, bmsTable);
                    lr2Song.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
                    bmsTable.entries.ForEach(delegate (BMSTableEntry e)
                    {
                        e?.NormalizeForPlaylistPersistence();
                        lr2Song.InsertOrReplace(e, typeof(LR2SongDBExtended.playlist_entry));
                    });
                }
                lr2Song.Commit();
            }
            finally
            {
                if (lr2Song != null)
                {
                    ((IDisposable)lr2Song).Dispose();
                }
            }
        }
        catch
        {
            throw;
        }
    }

    /// <summary>
    /// 単一プレイリストエントリを一意条件で置き換えて保存します。
    /// </summary>
    /// <param name="entry">保存対象のエントリ。</param>
    public void CommitBMSTableEntry(BMSTableEntry entry)
    {
        if (!entry.playlist_id.HasValue)
        {
            return;
        }
        BMSTable owningTable = ResolveOwningTableForEntry(entry);
        if (owningTable == null || !owningTable.is_external_sync)
        {
            entry.MaterializeEffectiveUrlsIntoPersistedValues();
        }
        entry.NormalizeForPlaylistPersistence();
        if (owningTable != null)
        {
            using (owningTable.ReaderWriterLock.GetWriterGuard())
            {
                owningTable.last_update = GetNextPlaylistLastUpdate(owningTable.last_update);
            }
        }
        try
        {
            using var lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + BuildPlaylistEntryReplacementPredicate(entry) + ";");
            lR2SongDBExtended.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            if (owningTable != null)
            {
                lR2SongDBExtended.InsertOrReplace(owningTable, typeof(LR2SongDBExtended.playlist));
                ReplacePersistedCourses(lR2SongDBExtended, owningTable);
            }
            lR2SongDBExtended.Commit();
        }
        catch
        {
            throw;
        }
        if (owningTable != null)
        {
            if (Settings.Default.OperationModeLR2DB && !string.IsNullOrWhiteSpace(owningTable.Output_dir))
            {
                EnsurePlaylistEntriesLoaded(owningTable, "CommitBMSTableEntry");
                using (owningTable.ReaderWriterLock.GetWriterGuard())
                {
                    if (BMSTables.Contains(owningTable))
                    {
                        reOutputCustomFolderFiles(owningTable);
                    }
                }
            }
            QueueBeatorajaBmtExport(owningTable, "CommitBMSTableEntry");
        }
    }

    private static DateTime GetNextPlaylistLastUpdate(DateTime currentLastUpdate)
    {
        return BMSTable.GetNextLastUpdate(currentLastUpdate);
    }

    /// <summary>
    /// 単一プレイリストを削除するためのラッパーです。
    /// </summary>
    /// <param name="bmsTable">削除対象のプレイリスト。</param>
    private void deleteBMSTable(BMSTable bmsTable)
    {
        deleteBMSTable([bmsTable]);
    }

    /// <summary>
    /// 複数プレイリストと対応エントリを DB から削除します。
    /// </summary>
    /// <param name="bmsTables">削除対象のプレイリスト群。</param>
    private void deleteBMSTable(IEnumerable<BMSTable> bmsTables)
    {
        try
        {
            using var lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSTable bmsTable in bmsTables)
            {
                if (bmsTable.playlist_id.HasValue)
                {
                    lR2SongDBExtended.Delete<LR2SongDBExtended.playlist>(bmsTable.playlist_id);
                    lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
                    lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
                }
            }
            lR2SongDBExtended.Commit();
        }
        catch
        {
            throw;
        }
    }

    /// <summary>
    /// プレイリスト本体のヘッダ情報のみを DB へ保存します。
    /// </summary>
    /// <param name="bmsTable">保存対象のプレイリスト。</param>
    private void commitBMSTableHeaderOnly(BMSTable bmsTable)
    {
        try
        {
            using var lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            lR2SongDBExtended.InsertOrReplace(bmsTable, typeof(LR2SongDBExtended.playlist));
            ReplacePersistedCourses(lR2SongDBExtended, bmsTable);
            lR2SongDBExtended.Commit();
        }
        catch
        {
            throw;
        }
    }

    private static void ReplacePersistedCourses(LR2SongDBExtended db, BMSTable bmsTable)
    {
        if (db == null || bmsTable == null || !bmsTable.playlist_id.HasValue)
        {
            return;
        }
        string tableName = SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName();
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName(e => e.playlist_id);
        db.Execute("DELETE FROM " + tableName + " WHERE " + playlistIdColumn + " = " + bmsTable.playlist_id + ";");
        int order = 0;
        foreach (LR2SongDBExtended.playlist_course course in bmsTable.Courses ?? [])
        {
            if (string.IsNullOrWhiteSpace(course?.course_json))
            {
                continue;
            }
            var row = new LR2SongDBExtended.playlist_course
            {
                playlist_id = bmsTable.playlist_id,
                course_order = order++,
                course_json = course.course_json
            };
            db.InsertOrReplace(row, typeof(LR2SongDBExtended.playlist_course));
        }
    }

    /// <summary>
    /// プレイリスト関連テーブルの SQL ダンプ文字列を生成します。
    /// </summary>
    /// <returns>バックアップ用の SQL ダンプ文字列。</returns>
    public string GetPlaylistDump()
    {
        using var lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        string separator = "\v" + Environment.NewLine;
        string playlistDump = string.Join(separator, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist>()
                                                     select c.Replace(separator, Environment.NewLine));
        string courseDump = string.Join(separator, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist_course>()
                                                   select c.Replace(separator, Environment.NewLine));
        string entryDump = string.Join(separator, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist_entry>()
                                                  select c.Replace(separator, Environment.NewLine));
        return string.Join(separator, [playlistDump, courseDump, entryDump]);
    }

    /// <summary>
    /// プレイリスト関連テーブルを SQL ダンプから復元します。
    /// </summary>
    /// <param name="sql">復元する SQL ダンプ文字列。</param>
    public void LoadPlaylistDump(string sql)
    {
        using var lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        using (new StringReader(sql))
        {
            string savepoint = lR2SongDBExtended.SaveTransactionPoint();
            try
            {
                lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist>();
                lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist_course>();
                lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist_entry>();
                EnsurePlaylistTablesAndIndexes(lR2SongDBExtended);
                string[] source = sql.Split(["\v" + Environment.NewLine], StringSplitOptions.None);
                if (source.Count() <= 1)
                {
                    throw new InvalidDataException(Resources.Error_InvalidBackupData);
                }
                foreach (string item in source.Where(s => !string.IsNullOrWhiteSpace(s)))
                {
                    lR2SongDBExtended.Execute(item);
                }
                List<BMSTable> restoredTables = [.. lR2SongDBExtended.Table<BMSTable>()];
                if (NormalizeBeatorajaBmtPlaylistSettings(restoredTables) > 0)
                {
                    foreach (BMSTable table in restoredTables)
                    {
                        lR2SongDBExtended.InsertOrReplace(table, typeof(LR2SongDBExtended.playlist));
                    }
                }
                lR2SongDBExtended.Commit();
            }
            catch (Exception)
            {
                lR2SongDBExtended.RollbackTo(savepoint);
                throw;
            }
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

    private static string BuildNullableSqlEquality(string value, bool blankAsNull = false)
    {
        if (value == null || (blankAsNull && string.IsNullOrWhiteSpace(value)))
        {
            return " IS NULL ";
        }
        return " = " + sqlQuote(value);
    }

    private static string BuildPlaylistEntryReplacementPredicate(BMSTableEntry entry)
    {
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.playlist_id);
        string md5Column = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.md5);
        string sha256Column = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.sha256);
        string folderColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.folder);
        string lr2BmsIdColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.lr2_bmsid);
        string titleColumn = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName(e => e.title);

        string hashPredicate;
        if (!string.IsNullOrWhiteSpace(entry.md5))
        {
            hashPredicate = md5Column + " = " + sqlQuote(entry.md5);
            if (!string.IsNullOrWhiteSpace(entry.sha256))
            {
                hashPredicate = "(" + hashPredicate + " OR (" + md5Column + " IS NULL AND " + sha256Column + " = " + sqlQuote(entry.sha256) + "))";
            }
        }
        else if (!string.IsNullOrWhiteSpace(entry.sha256))
        {
            hashPredicate = md5Column + " IS NULL AND " + sha256Column + " = " + sqlQuote(entry.sha256);
        }
        else
        {
            hashPredicate = md5Column + " IS NULL AND " + sha256Column + " IS NULL";
        }

        return playlistIdColumn + " = " + entry.playlist_id
            + " AND " + hashPredicate
            + " AND " + folderColumn + " = " + sqlQuote(entry.folder)
            + " AND " + lr2BmsIdColumn + BuildNullableSqlEquality(entry.lr2_bmsid, blankAsNull: true)
            + " AND " + titleColumn + BuildNullableSqlEquality(entry.title);
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

    /// <summary>
    /// プレイリスト設定からカスタムフォルダ出力先ディレクトリを算出します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>算出された出力先ディレクトリ。</returns>
    public static string GetCustomFolderOutputDirectory(BMSTable bmsTable)
    {
        return GetCustomFolderOutputDirectory(
            bmsTable,
            Settings.Default.LR2CustomFolderOutputBaseDir,
            Settings.Default.LR2CustomFolderOutputBaseDirRootType,
            Settings.Default.LR2CustomFolderAdditionalOutputBaseDirs);
    }

    public static string GetCustomFolderOutputDirectory(
        BMSTable bmsTable,
        string normalOutputBaseDirectory,
        string rootOutputBaseDirectory,
        string serializedAdditionalOutputBaseDirectories)
    {
        try
        {
            string outputBaseDirectory = bmsTable.is_root_folder
                ? rootOutputBaseDirectory
                : CustomFolderOutputBaseRegistry.ResolveNormalOutputBaseDirectory(
                    bmsTable.custom_folder_output_base_name,
                    normalOutputBaseDirectory,
                    serializedAdditionalOutputBaseDirectories);
            return Path.Combine(outputBaseDirectory, bmsTable.Output_dir);
        }
        catch (ArgumentNullException)
        {
            DispatcherMessageBox.Show(Resources.Warn_CustomFolderOutputDirInvalid, Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            throw;
        }
    }
    /// <summary>
    /// 推定表 JSON の 1 レコードを表します。
    /// </summary>
    internal class EstimationData
    {
        /// <summary>
        /// レコード種別です。
        /// </summary>
        public string type { get; set; }

        /// <summary>
        /// 楽曲の BMS ID です。
        /// </summary>
        public string bmsid { get; set; }

        /// <summary>
        /// 推定難易度セットです。
        /// </summary>
        public EstimationHoshi hoshi { get; set; }
    }

    /// <summary>
    /// 推定表が返す難易度セットを表します。
    /// </summary>
    internal class EstimationHoshi
    {
        /// <summary>
        /// EASY 推定値です。
        /// </summary>
        public double? easy { get; set; }

        /// <summary>
        /// NORMAL 推定値です。
        /// </summary>
        public double? normal { get; set; }

        /// <summary>
        /// HARD 推定値です。
        /// </summary>
        public double? hard { get; set; }

        /// <summary>
        /// FC 推定値です。
        /// </summary>
        public double? fc { get; set; }
    }

    /// <summary>
    /// おすすめ表 API の 1 レコードを表します。
    /// </summary>
    internal class RecommendedData
    {
        /// <summary>
        /// レコード種別です。
        /// </summary>
        public string type { get; set; }

        /// <summary>
        /// 楽曲の BMS ID です。
        /// </summary>
        public string bmsid { get; set; }

        /// <summary>
        /// 新しいクリアランプ名です。
        /// </summary>
        public string new_lamp { get; set; }

        /// <summary>
        /// おすすめ度の割合です。
        /// </summary>
        public double? percent { get; set; }
    }
}
