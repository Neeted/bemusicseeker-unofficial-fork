using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Globalization;
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
using Sgml;

namespace BeMusicSeeker.Models;

/// <summary>
/// LR2 のプレイリスト定義、外部テーブル同期、カスタムフォルダ出力を一括管理します。
/// DB 永続化と外部取得の境界が同居しているため、この型がプレイリスト関連処理の集約点です。
/// </summary>
public partial class BMSPlaylist : NotificationObject
{
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
        public bool HasKnownChanges { get; set; }

        public bool HasHashInitialization { get; set; }

        public bool HasStateToPersist => HasKnownChanges || HasHashInitialization;
    }

    public sealed class PlaylistTableUpdateContext
    {
        public BMSTable NewTable { get; internal set; }

        public bool Updated { get; internal set; }

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
    private string lr2SongDBPath;

    /// <summary>
    /// 推奨表更新時に参照する LR2 Score DB のパスを保持します。
    /// </summary>
    private string lr2ScoreDBPath;

    /// <summary>
    /// LR2 設定を取得するための遅延評価デリゲートです。
    /// </summary>
    private Func<LR2Config> lr2config;

    /// <summary>
    /// ローカルスコア一覧を取得するための遅延評価デリゲートです。
    /// </summary>
    private Func<List<BMSScore>> bmsScores;

    /// <summary>
    /// 初期化処理の連携用に一時保持するセマフォです。
    /// </summary>
    private SemaphoreSlim initSemaphore;

    /// <summary>
    /// 全件初期化工程全体を直列化するための書き込みロックです。
    /// </summary>
    private ReaderWriterLockSlimWrapper rwlockBMSTablesInitializeAll = new ReaderWriterLockSlimWrapper();

    /// <summary>
    /// 最小限のプレイリスト初期化工程を保護する書き込みロックです。
    /// </summary>
    private ReaderWriterLockSlimWrapper rwlockBMSTablesInitializeMin = new ReaderWriterLockSlimWrapper();

    /// <summary>
    /// プレイリスト一覧そのものへの更新を保護する書き込みロックです。
    /// </summary>
    private ReaderWriterLockSlimWrapper rwlockBMSTables = new ReaderWriterLockSlimWrapper();

    private readonly SemaphoreSlim playlistEntriesHydrationSemaphore = new SemaphoreSlim(1, 1);

    private int playlistEntriesHydrationQueued;

    private readonly object playlistEntriesHydrationRequestLock = new object();

    private bool playlistEntriesHydrationPendingRunExternalSync;

    private readonly List<Action<PlaylistTableUpdateContext>> playlistEntriesHydrationPendingUpdateCallbacks = new List<Action<PlaylistTableUpdateContext>>();

    private readonly object beatorajaBmtExportQueueLock = new object();

    private readonly HashSet<int> pendingBeatorajaBmtExportPlaylistIds = new HashSet<int>();

    private int beatorajaBmtExportQueued;

    internal Func<string, string, string, Func<Task>, bool> StartupBackgroundTaskScheduler { get; set; }

    /// <summary>
    /// 全件初期化ロックの状態変化を監視するリスナーです。
    /// </summary>
    private PropertyChangedEventListener listenerForRwlockBMSTablesInitializedAll;

    /// <summary>
    /// 最小初期化ロックの状態変化を監視するリスナーです。
    /// </summary>
    private PropertyChangedEventListener listenerForRwlockBMSTablesInitializedMin;

    /// <summary>
    /// プレイリスト一覧ロックの状態変化を監視するリスナーです。
    /// </summary>
    private PropertyChangedEventListener listenerForRwlockBMSTables;

    /// <summary>
    /// UI バインディングに公開するプレイリスト一覧を保持します。
    /// </summary>
    private DispatcherCollection<BMSTable> _BMSTables = new DispatcherCollection<BMSTable>(DispatcherHelper.UIDispatcher);

    /// <summary>
    /// 発狂難易度推定 JSON の取得先 URI です。
    /// </summary>
    private static Uri estimationJsonUri = new Uri("http://walkure.net/hakkyou/data/bms.json", UriKind.Absolute);

    /// <summary>
    /// おすすめ表 JSON API のベース URL です。
    /// </summary>
    private static string recommendJsonUriStr = "http://walkure.net/hakkyou/recommended_json.cgi?id=";

    /// <summary>
    /// おすすめ表へクリア状況を送信する更新 API の URI です。
    /// </summary>
    private static Uri walkureUpdateUri = new Uri("http://walkure.net/hakkyou/mle.cgi", UriKind.Absolute);

    /// <summary>
    /// 発狂表アーカイブの取得先 URI です。
    /// </summary>
    private static Uri insaneUri = new Uri("https://darksabun.club/table/archive/insane1/");

    /// <summary>
    /// Overjoy 表アーカイブの取得先 URI です。
    /// </summary>
    private static Uri overjoyUri = new Uri("https://darksabun.club/table/archive/old-overjoy/");

    /// <summary>
    /// 発狂表キャッシュの遅延初期化を直列化するためのロックです。
    /// </summary>
    private object insaneTableLock = new object();

    /// <summary>
    /// 読み込み済みの発狂表キャッシュです。
    /// </summary>
    private BMSTable _insaneTable;

    /// <summary>
    /// Overjoy 表キャッシュの遅延初期化を直列化するためのロックです。
    /// </summary>
    private object overjoyTableLock = new object();

    /// <summary>
    /// 読み込み済みの Overjoy 表キャッシュです。
    /// </summary>
    private BMSTable _overjoyTable;

    /// <summary>
    /// 段位課題曲に対応する疑似 BMS ID 変換表です。
    /// </summary>
    private static readonly Dictionary<int, string> insaneGrade = new Dictionary<int, string>
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
    private object estimationTableLock = new object();

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
    private Regex workAroundRegex = new Regex("\"(?<id>\\d+)\":{", RegexOptions.Compiled);

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
                    return BMSTables.Any((BMSTable t) => t.ReaderWriterLock.LockingWriteCount != 0 || t.ReaderWriterLock.WaitingWriteCount > 0);
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

    /// <summary>
    /// プレイリスト DB への接続情報と関連取得デリゲートを初期化します。
    /// 必要なテーブルとインデックスもここで整備します。
    /// </summary>
    /// <param name="_lr2SongDB">プレイリスト保存先の LR2 Song DB パス。</param>
    /// <param name="getLR2Config">LR2 設定を返すデリゲート。</param>
    /// <param name="_lr2ScoreDB">推奨表更新に使う LR2 Score DB パス。</param>
    /// <param name="getBMSScores">ローカルスコア一覧を返すデリゲート。</param>
    /// <exception cref="ArgumentNullException"><paramref name="_lr2SongDB"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentException">必要な DB ファイルが存在しない場合。</exception>
    public BMSPlaylist(string _lr2SongDB, Func<LR2Config> getLR2Config = null, string _lr2ScoreDB = null, Func<List<BMSScore>> getBMSScores = null)
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
        lr2config = ((getLR2Config != null) ? getLR2Config : ((Func<LR2Config>)(() => (LR2Config)null)));
        bmsScores = ((getBMSScores != null) ? getBMSScores : ((Func<List<BMSScore>>)(() => (List<BMSScore>)null)));
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
        using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(songDbPath);
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
    public void Initialize(bool reloadExtPlaylist = true, Action<PlaylistTableUpdateContext> updateCallbackAction = null, SemaphoreSlim semaphore = null)
    {
        Stopwatch stopwatchInitialize = Stopwatch.StartNew();
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
            if (initSemaphore != null)
            {
                initSemaphore.Release();
            }
            updateTablesMs = 0L;
            Stopwatch stopwatchLr2configSync = Stopwatch.StartNew();
            SyncRootFolderOutputDirectoriesToLr2Config();
            stopwatchLr2configSync.Stop();
            lr2configSyncMs = stopwatchLr2configSync.ElapsedMilliseconds;
            QueueDeferredPlaylistEntriesHydration("Initialize", reloadExtPlaylist, new List<Action<PlaylistTableUpdateContext>> { CreateCustomFolderOutputUpdateCallback(), CreateBeatorajaBmtOutputUpdateCallback(), updateCallbackAction });
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
    public void ReloadTables(Action<PlaylistTableUpdateContext> updateCallbackAction = null)
    {
        Stopwatch stopwatchReloadTables = Stopwatch.StartNew();
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
            Stopwatch stopwatchLr2configSync = Stopwatch.StartNew();
            SyncRootFolderOutputDirectoriesToLr2Config();
            stopwatchLr2configSync.Stop();
            lr2configSyncMs = stopwatchLr2configSync.ElapsedMilliseconds;
            QueueDeferredPlaylistEntriesHydration("ReloadTables", runExternalSyncAfterHydration: false, new List<Action<PlaylistTableUpdateContext>> { CreateCustomFolderOutputUpdateCallback(), CreateBeatorajaBmtOutputUpdateCallback(), updateCallbackAction });
        }
        stopwatchReloadTables.Stop();
        LogPlaylistPerformance("playlist_reload_tables lr2config_sync_ms=" + lr2configSyncMs + " total_ms=" + stopwatchReloadTables.ElapsedMilliseconds);
        SchedulePlaylistUrlCompletionRefresh("ReloadTables");
    }

    private List<BMSTable> LoadPlaylistHeadersFromDatabase(out long loadTablesMs)
    {
        Stopwatch stopwatchLoadTables = Stopwatch.StartNew();
        List<BMSTable> list;
        using (LR2SongDBExtended lR2SongDBExtended = new BmsLibraryDbGateway(lr2SongDBPath).OpenSongDbReadOnly())
        {
            list = (from t in lR2SongDBExtended.Table<BMSTable>()
                    orderby t.name
                    select t).ToList();
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
        List<BMSTable> tableList = tables?.Where((BMSTable table) => table != null && table.playlist_id.HasValue).ToList() ?? new List<BMSTable>();
        if (tableList.Count == 0)
        {
            return;
        }
        Dictionary<int, List<LR2SongDBExtended.playlist_course>> coursesByPlaylistId = db.Table<LR2SongDBExtended.playlist_course>()
            .ToList()
            .Where((LR2SongDBExtended.playlist_course course) => course.playlist_id.HasValue)
            .GroupBy((LR2SongDBExtended.playlist_course course) => course.playlist_id.Value)
            .ToDictionary((IGrouping<int, LR2SongDBExtended.playlist_course> group) => group.Key, (IGrouping<int, LR2SongDBExtended.playlist_course> group) => group.OrderBy((LR2SongDBExtended.playlist_course course) => course.course_order).ToList());
        foreach (BMSTable table in tableList)
        {
            if (coursesByPlaylistId.TryGetValue(table.playlist_id.Value, out List<LR2SongDBExtended.playlist_course> courses))
            {
                table.SetPersistedCourses(courses);
            }
        }
    }

    private Action<PlaylistTableUpdateContext> CreateCustomFolderOutputUpdateCallback()
    {
        object folderoutLock = new object();
        return delegate (PlaylistTableUpdateContext updateContext)
        {
            BMSTable bMSTable = updateContext?.NewTable;
            BMSTable oldtable = updateContext?.OldTable;
            bool updated = updateContext?.Updated ?? false;
            if (!Settings.Default.OperationModeLR2DB || bMSTable == null)
            {
                return;
            }
            using (bMSTable.ReaderWriterLock.GetWriterGuard())
            {
                if (string.IsNullOrWhiteSpace(bMSTable.Output_dir))
                {
                    return;
                }
                string customFolderOutputDirectory = GetCustomFolderOutputDirectory(bMSTable);
                if (updated || !Directory.Exists(customFolderOutputDirectory) || Directory.EnumerateFiles(customFolderOutputDirectory, "*.lr2folder", System.IO.SearchOption.TopDirectoryOnly).Count() == 0)
                {
                    lock (folderoutLock)
                    {
                        string customFolderOutputDirectory2 = GetCustomFolderOutputDirectory(oldtable);
                        if (customFolderOutputDirectory != customFolderOutputDirectory2)
                        {
                            removeCustomFolder(customFolderOutputDirectory2);
                        }
                        removeCustomFolder(customFolderOutputDirectory);
                        createCustomFolder(bMSTable, customFolderOutputDirectory);
                    }
                }
            }
        };
    }

    private Action<PlaylistTableUpdateContext> CreateBeatorajaBmtOutputUpdateCallback()
    {
        return delegate (PlaylistTableUpdateContext updateContext)
        {
            BMSTable bMSTable = updateContext?.NewTable;
            if (bMSTable != null)
            {
                QueueBeatorajaBmtExport(bMSTable, "playlist_update_callback");
            }
        };
    }

    private bool IsBeatorajaBmtOutputEnabled()
    {
        return Settings.Default.EnableBeatorajaBmtOutput && !string.IsNullOrWhiteSpace(Settings.Default.BeatorajaBmtTablePath);
    }

    internal void QueueBeatorajaBmtExportAll(string reason, string cleanupTablePath = null)
    {
        string outputPath = Settings.Default.BeatorajaBmtTablePath;
        bool enabled = IsBeatorajaBmtOutputEnabled();
        Func<Task> work = async delegate
        {
            await Task.Yield();
            if (!string.IsNullOrWhiteSpace(cleanupTablePath) && (!enabled || !string.Equals(cleanupTablePath, outputPath, StringComparison.OrdinalIgnoreCase)))
            {
                BmtTableExportService.CleanupManagedFiles(cleanupTablePath);
            }
            if (!enabled)
            {
                return;
            }
            List<BMSTable> tablesSnapshot;
            using (rwlockBMSTables.GetReaderGuard())
            {
                tablesSnapshot = BMSTables?.Where((BMSTable table) => table != null).ToList() ?? new List<BMSTable>();
            }
            List<Tuple<string, JObject>> tableDataSet = new List<Tuple<string, JObject>>();
            foreach (BMSTable table in tablesSnapshot)
            {
                JObject tableData = BuildBeatorajaBmtTableDataSnapshot(table, reason);
                if (tableData != null)
                {
                    tableDataSet.Add(Tuple.Create(GetBeatorajaBmtPlaylistIdentity(table), tableData));
                }
            }
            BmtTableExportService.ExportTableDataSet(outputPath, tableDataSet, cleanupStaleManagedFiles: true);
        };
        if (StartupBackgroundTaskScheduler != null && StartupBackgroundTaskScheduler("beatoraja_bmt_export_all", reason ?? "queue", null, work))
        {
            return;
        }
        Task.Run(work).Logging("QueueBeatorajaBmtExportAll");
    }

    internal void QueueBeatorajaBmtExportForTable(BMSTable table, string reason)
    {
        QueueBeatorajaBmtExport(table, reason);
    }

    private void QueueBeatorajaBmtExport(BMSTable table, string reason)
    {
        if (!IsBeatorajaBmtOutputEnabled() || table == null || !table.playlist_id.HasValue)
        {
            return;
        }
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
        Func<Task> work = async delegate
        {
            await Task.Yield();
            try
            {
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
        };
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
                playlistIds = pendingBeatorajaBmtExportPlaylistIds.ToList();
                pendingBeatorajaBmtExportPlaylistIds.Clear();
            }
            foreach (int playlistId in playlistIds)
            {
                BMSTable table;
                using (rwlockBMSTables.GetReaderGuard())
                {
                    table = BMSTables?.FirstOrDefault((BMSTable candidate) => candidate != null && candidate.playlist_id == playlistId);
                }
                JObject tableData = BuildBeatorajaBmtTableDataSnapshot(table, reason);
                if (tableData != null)
                {
                    BmtTableExportService.ExportTableData(Settings.Default.BeatorajaBmtTablePath, tableData, GetBeatorajaBmtPlaylistIdentity(table));
                }
            }
        }
    }

    private static string GetBeatorajaBmtPlaylistIdentity(BMSTable table)
    {
        return table?.playlist_id?.ToString(CultureInfo.InvariantCulture);
    }

    private JObject BuildBeatorajaBmtTableDataSnapshot(BMSTable table, string reason)
    {
        if (table == null)
        {
            return null;
        }
        EnsurePlaylistEntriesLoaded(table, reason ?? "BeatorajaBmtExport");
        using (table.ReaderWriterLock.GetReaderGuard())
        {
            return BmtTableExportService.BuildTableData(table);
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
            List<string> bMSSearchDirectories = lr2config().GetBMSSearchDirectories();
            lr2config().SetBMSSearchDirectories(bMSSearchDirectories.Union(second).Distinct(StringComparer.OrdinalIgnoreCase));
            lr2config().Save();
        }
    }

    public void QueueDeferredPlaylistEntriesHydration(string reason, bool runExternalSyncAfterHydration = false, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null)
    {
        int version = PlaylistEntriesHydrationRequestedVersion + 1;
        PlaylistEntriesHydrationRequestedVersion = version;
        LogPlaylistPerformance("playlist_entries_hydration queue reason=" + (reason ?? string.Empty) + " version=" + version + " runExternalSyncAfterHydration=" + runExternalSyncAfterHydration.ToString().ToLowerInvariant());
        lock (playlistEntriesHydrationRequestLock)
        {
            playlistEntriesHydrationPendingRunExternalSync = playlistEntriesHydrationPendingRunExternalSync || runExternalSyncAfterHydration;
            if (updateCallbackActions != null)
            {
                playlistEntriesHydrationPendingUpdateCallbacks.AddRange(updateCallbackActions.Where((Action<PlaylistTableUpdateContext> action) => action != null));
            }
        }
        if (Interlocked.Exchange(ref playlistEntriesHydrationQueued, 1) != 0)
        {
            LogPlaylistPerformance("playlist_entries_hydration coalesced reason=" + (reason ?? string.Empty) + " version=" + version);
            return;
        }
        Func<Task> work = async delegate
        {
            try
            {
                await EnsureAllPlaylistEntriesLoadedAsync(reason ?? "queue", publishCompletedVersion: false).ConfigureAwait(false);
                bool mergedRunExternalSync;
                List<Action<PlaylistTableUpdateContext>> mergedUpdateCallbacks;
                lock (playlistEntriesHydrationRequestLock)
                {
                    mergedRunExternalSync = playlistEntriesHydrationPendingRunExternalSync;
                    mergedUpdateCallbacks = playlistEntriesHydrationPendingUpdateCallbacks.ToList();
                    playlistEntriesHydrationPendingRunExternalSync = false;
                    playlistEntriesHydrationPendingUpdateCallbacks.Clear();
                }
                if (mergedRunExternalSync || mergedUpdateCallbacks.Count > 0)
                {
                    Stopwatch stopwatchUpdateTables = Stopwatch.StartNew();
                    UpdateBMSTables(mergedRunExternalSync, mergedUpdateCallbacks);
                    stopwatchUpdateTables.Stop();
                    LogPlaylistPerformance("playlist_entries_hydration post_update_tables reason=" + (reason ?? string.Empty) + " reloadExtPlaylist=" + mergedRunExternalSync.ToString().ToLowerInvariant() + " callbackCount=" + mergedUpdateCallbacks.Count + " elapsedMs=" + stopwatchUpdateTables.ElapsedMilliseconds);
                }
                PlaylistEntriesHydrationCompletedVersion = PlaylistEntriesHydrationRequestedVersion;
            }
            finally
            {
                Interlocked.Exchange(ref playlistEntriesHydrationQueued, 0);
                bool hasPendingRequest;
                lock (playlistEntriesHydrationRequestLock)
                {
                    hasPendingRequest = playlistEntriesHydrationPendingRunExternalSync || playlistEntriesHydrationPendingUpdateCallbacks.Count > 0;
                }
                if (hasPendingRequest)
                {
                    LogPlaylistPerformance("playlist_entries_hydration reschedule reason=" + (reason ?? string.Empty));
                    QueueDeferredPlaylistEntriesHydration(reason ?? "reschedule");
                }
            }
        };
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
            tablesSnapshot = BMSTables.Where((BMSTable table) => table != null).ToList();
        }
        if (tablesSnapshot.Count > 0 && tablesSnapshot.All((BMSTable table) => table.ArePlaylistEntriesLoaded))
        {
            return;
        }
        await playlistEntriesHydrationSemaphore.WaitAsync().ConfigureAwait(false);
        try
        {
            using (rwlockBMSTables.GetReaderGuard())
            {
                tablesSnapshot = BMSTables.Where((BMSTable table) => table != null).ToList();
            }
            if (tablesSnapshot.Count > 0 && tablesSnapshot.All((BMSTable table) => table.ArePlaylistEntriesLoaded))
            {
                return;
            }
            PlaylistEntriesHydrationRunning = true;
            Stopwatch stopwatchTotal = Stopwatch.StartNew();
            BmsLibraryDbGateway dbGateway = new BmsLibraryDbGateway(lr2SongDBPath);
            PlaylistEntriesHydrationLoadResult loadResult = dbGateway.LoadStartupPlaylistEntries();
            List<BMSTableEntry> source = loadResult.Entries;
            Stopwatch stopwatchGroup = Stopwatch.StartNew();
            Dictionary<int, List<BMSTableEntry>> entriesByPlaylistId = new Dictionary<int, List<BMSTableEntry>>();
            foreach (BMSTableEntry entryItem in source)
            {
                if (!entryItem.playlist_id.HasValue)
                {
                    continue;
                }
                int key = entryItem.playlist_id.Value;
                if (!entriesByPlaylistId.TryGetValue(key, out List<BMSTableEntry> value))
                {
                    value = new List<BMSTableEntry>();
                    entriesByPlaylistId[key] = value;
                }
                value.Add(entryItem);
            }
            stopwatchGroup.Stop();
            int removedEntryCount = source.Count((BMSTableEntry entry) => entry != null && entry.is_removed);
            int activeEntryCount = source.Count - removedEntryCount;
            Stopwatch stopwatchAssign = Stopwatch.StartNew();
            int assignedTableCount = 0;
            using (rwlockBMSTables.GetWriterGuard())
            {
                foreach (BMSTable table in BMSTables.Where((BMSTable table) => table != null).ToList())
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
                            table.entries = new List<BMSTableEntry>();
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
                allTablesLoaded = BMSTables.Where((BMSTable table) => table != null).All((BMSTable table) => table.ArePlaylistEntriesLoaded);
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
                foreach (BMSTable table in BMSTables.Where((BMSTable table) => table != null && !table.ArePlaylistEntriesLoaded))
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
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            List<BMSTableEntry> entries = LoadPersistedPlaylistEntries(table.playlist_id, activeOnly: false).ToList();
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                table.entries = entries;
            }
            stopwatch.Stop();
            int removedEntryCount = entries.Count((BMSTableEntry entry) => entry != null && entry.is_removed);
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
        EnsurePlaylistCourseIndexes(db);
        RebuildPlaylistEntryIndexes(db);
    }

    private static void EnsurePlaylistMetadataColumns(LR2SongDBExtended db)
    {
        string tableName = SQLiteTable<LR2SongDBExtended.playlist>.GetTableName();
        EnsureColumn(db, tableName, "tag", "TEXT NULL");
        EnsureColumn(db, tableName, "header_sha256", "TEXT NULL");
        EnsureColumn(db, tableName, "data_sha256", "TEXT NULL");
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
        EnsurePlaylistEntryIndex(db, tableName, "playlist_course_idx_id", new string[1]
        {
            SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName((LR2SongDBExtended.playlist_course e) => e.playlist_id)
        });
        long count = db.ExecuteScalar<long>("SELECT COUNT(1) FROM sqlite_master WHERE type = 'index' AND name = " + sqlQuote("playlist_course_idx_uniq") + ";");
        if (count == 0)
        {
            db.CreateIndex("playlist_course_idx_uniq", tableName, new string[2]
            {
                SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName((LR2SongDBExtended.playlist_course e) => e.playlist_id),
                SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName((LR2SongDBExtended.playlist_course e) => e.course_order)
            }, unique: true);
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
            db.CreateIndex("playlist_entry_idx_uniq", tableName, new string[7]
            {
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.sha256),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.lr2_bmsid),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title),
                SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
            }, unique: true);
        }
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_id", new string[2]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_folder", new string[3]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_title", new string[3]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_md5", new string[3]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_sha256", new string[3]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.sha256),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_level", new string[3]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.level),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
        EnsurePlaylistEntryIndex(db, tableName, "playlist_entry_idx_adddate", new string[3]
        {
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.adddate),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id),
            SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed)
        });
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
        BMSTable bMSTable = new BMSTable
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
            Regex regex = new Regex("id=(\\d+)");
            Regex regex2 = new Regex("mode=([^&]+)");
            Regex regex3 = new Regex("filter=([^&]+)");
            Regex regex4 = new Regex("name=([^&]+)");
            Regex regex5 = new Regex("base=([^&]+)");
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
        BMSTable insane = insaneTable;
        if (insane == null)
        {
            throw new InvalidOperationException("Load insane table failed");
        }
        BMSTable bMSTable = overjoyTable;
        if (bMSTable == null)
        {
            throw new InvalidOperationException("Load overjoy table failed");
        }
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
        _easyEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
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
        }).ToList();
        _normalEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
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
        }).ToList();
        _hardEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
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
        }).ToList();
        _fcEntries = source.SelectMany(grp => grp.estimation, (grp, a) =>
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
        }).ToList();
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
        table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder;
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
        Uri address = new Uri(recommendJsonUriStr + lr2id, UriKind.Absolute);
        dynamic val = DynamicJson.Parse(playlistHttpClient.GetString(address));
        if ((string)val.status != "success")
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_RecommendFetchFailed, (string)val.message), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
            throw new InvalidOperationException(Resources.Error_RecommendFetchFailed);
        }
        double num = (double)val.hoshi;
        DateTime dateTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds((int)val.last_modified).ToLocalTime();
        name = ((string)val.name).Replace('〜', '～');
        BMSTable insane = insaneTable;
        if (insane == null)
        {
            throw new InvalidOperationException("Load insane table failed");
        }
        BMSTable bMSTable = overjoyTable;
        if (bMSTable == null)
        {
            throw new InvalidOperationException("Load overjoy table failed");
        }
        IEnumerable<BMSTableEntry> inner = from e in insane.entries.Concat(bMSTable.entries)
                                           where !string.IsNullOrWhiteSpace(e.md5) && !string.IsNullOrWhiteSpace(e.lr2_bmsid)
                                           group e by e.md5 into e
                                           select e.FirstOrDefault((BMSTableEntry f) => f.parent == insane) ?? e.First();
        List<BMSTableEntry> entries = (from e in ((object[])val.recommended).Select(delegate (dynamic item)
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
                                   }).ToList();
        table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
        table.folder_sort_ascending = false;
        table.is_external_sync = true;
        table.entries = entries;
        table.last_update = DateTime.Parse(dateTime.ToString());
        table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.LevelFolder | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder;
        table.Folder_order = new List<string> { "EASY", "NORMAL", "HARD", "FC" };
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
        BMSTable bMSTable = overjoyTable;
        if (bMSTable == null)
        {
            throw new InvalidOperationException("Load overjoy table failed");
        }
        var enumerable = from e in obj.entries.Concat(bMSTable.entries)
                         where !string.IsNullOrWhiteSpace(e.md5) && !string.IsNullOrWhiteSpace(e.lr2_bmsid)
                         group e by e.md5 into g
                         select new
                         {
                             md5 = g.Key,
                             bmsid = g.First().lr2_bmsid
                         };
        if (initSemaphore != null)
        {
            initSemaphore.Wait();
        }
        if (initSemaphore != null)
        {
            initSemaphore.Release();
        }
        List<BMSScore> list = bmsScores();
        if (list == null)
        {
            throw new InvalidOperationException(Resources.Error_LocalScoreDataNotFetched);
        }
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
                            bmsid = t.bmsid,
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
            lampsBMS = enumerable.Select(t =>
            {
                var anon = lampsBMS.FirstOrDefault(m => m.bmsid == t.bmsid);
                return new
                {
                    bmsid = t.bmsid,
                    lamp = (anon?.lamp ?? 1),
                    rank = (anon?.rank ?? 0)
                };
            }).ToList();
            lampsGrade = insaneGrade.Select(delegate (KeyValuePair<int, string> t)
            {
                var anon = lampsGrade.FirstOrDefault(h => h.bmsid == (t.Key + 100000000).ToString());
                return new
                {
                    bmsid = (t.Key + 100000000).ToString(),
                    lamp = (anon?.lamp ?? 1),
                    rank = (anon?.rank ?? 0)
                };
            }).ToList();
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
        SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.playcount);
        SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.playcount);
        Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType> source = new Dictionary<string, LR2SongDBExtended.playlist.CustomFolderSortType>
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
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        List<string> list = source.Select((KeyValuePair<string, LR2SongDBExtended.playlist.CustomFolderSortType> f) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "ORDER BY " + makeCustomFolderCmdSort(f.Value, asc: false, bmsTable.playlist_id), bmsTable.name, f.Key, 40)).ToList();
        if (Settings.Default.EnableDownloadLr2IrScoreAndDetectUnsent)
        {
            string tableName = SQLiteTable<LR2SongDBExtended.ir_score>.GetTableName();
            string columnName = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.hash);
            string columnName2 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.clear);
            string columnName3 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.combo);
            string columnName4 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pg);
            string columnName5 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gr);
            string columnName6 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.gd);
            string columnName7 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.bd);
            string columnName8 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.pr);
            string columnName9 = SQLiteTable<LR2SongDBExtended.ir_score>.GetColumnName((LR2SongDBExtended.ir_score e) => e.minbp);
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
        string columnName = SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.longnote);
        string columnName2 = SQLiteTable<LR2SongDB.song>.GetColumnName((LR2SongDB.song e) => e.judge);
        List<string[]> list = new List<string[]>();
        list.Add(new string[2]
        {
            "ALL LONG NOTES",
            columnName + " = 1"
        });
        list.Add(new string[2]
        {
            "ALL VERY HARD JUDGES",
            columnName2 + " = " + 0
        });
        list.Add(new string[2]
        {
            "ALL HARD JUDGES",
            columnName2 + " = " + 1
        });
        list.Add(new string[2]
        {
            "ALL NORMAL JUDGES",
            columnName2 + " = " + 2
        });
        list.Add(new string[2]
        {
            "ALL EASY JUDGES",
            columnName2 + " >= " + 3
        });
        List<string[]> source = list;
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        return source.Select((string[] a) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + a[1] + ")", bmsTable.name, a[0])).ToList();
    }

    /// <summary>
    /// DJ LEVEL 別カスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>DJ LEVEL 別フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsDJLevelFolder(BMSTable bmsTable)
    {
        string columnName = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.rank);
        List<string[]> list = new List<string[]>();
        list.Add(new string[2]
        {
            "DJ LEVEL AAA",
            columnName + " = " + 8
        });
        list.Add(new string[2]
        {
            "DJ LEVEL AA",
            columnName + " = " + 7
        });
        list.Add(new string[2]
        {
            "DJ LEVEL A",
            columnName + " = " + 6
        });
        list.Add(new string[2]
        {
            "DJ LEVEL B",
            columnName + " = " + 5
        });
        list.Add(new string[2]
        {
            "DJ LEVEL C",
            columnName + " = " + 4
        });
        list.Add(new string[2]
        {
            "DJ LEVEL D",
            columnName + " = " + 3
        });
        list.Add(new string[2]
        {
            "DJ LEVEL E",
            columnName + " = " + 2
        });
        list.Add(new string[2]
        {
            "DJ LEVEL F",
            columnName + " = " + 1
        });
        list.Add(new string[2]
        {
            "NO SCORE",
            columnName + " = " + 0 + " OR " + columnName + " IS NULL "
        });
        List<string[]> source = list;
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        return source.Select((string[] r) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + r[1] + ") ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.SCORE, asc: false), bmsTable.name, r[0])).ToList();
    }

    /// <summary>
    /// クリアランプ別カスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>クリアランプ別フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsClearFolder(BMSTable bmsTable)
    {
        string columnName = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.clear);
        string columnName2 = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.rank);
        string columnName3 = SQLiteTable<LR2ScoreDB.score>.GetColumnName((LR2ScoreDB.score e) => e.op_history);
        List<string[]> list = new List<string[]>();
        list.Add(new string[2]
        {
            "PERFECT ATTACK CLEAR",
            columnName + " = " + 5 + " AND " + columnName3 + " & 16 != 0"
        });
        list.Add(new string[2]
        {
            "FULL COMBO CLEAR",
            columnName + " = " + 5
        });
        list.Add(new string[2]
        {
            "HARD CLEAR",
            columnName + " = " + 4
        });
        list.Add(new string[2]
        {
            "CLEAR",
            columnName + " = " + 3
        });
        list.Add(new string[2]
        {
            "EASY CLEAR",
            columnName + " = " + 2 + " AND " + columnName2 + " != " + 0
        });
        list.Add(new string[2]
        {
            "ASSIST CLEAR",
            columnName + " >= " + 2 + " AND " + columnName2 + " = " + 0
        });
        list.Add(new string[2]
        {
            "FAILED",
            columnName + " = " + 1
        });
        list.Add(new string[2]
        {
            "NO PLAY",
            columnName + " IS NULL"
        });
        List<string[]> source = list;
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        return source.Select((string[] c) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + "AND (" + c[1] + ") ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.MISS, asc: true), bmsTable.name, c[0])).ToList();
    }

    /// <summary>
    /// タイトル頭文字別カスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>アルファベット別フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsAlphabetFolder(BMSTable bmsTable)
    {
        Dictionary<string, char[]> dict = new Dictionary<string, char[]>
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
        char[] array = new char[2] { 'A', '[' };
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameTitle = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        List<string> list = (from t in dict.Keys
                             orderby t
                             select getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")) " : " ") + "AND UPPER(" + columnNameTitle + ") BETWEEN " + sqlQuote(dict[t][0].ToString()) + " AND " + sqlQuote(dict[t][1].ToString()) + " AND UPPER(" + columnNameTitle + ") != " + sqlQuote(dict[t][1].ToString()) + ((bmsTable.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ") " : " ") + "ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, t)).ToList();
        string command = ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameIsRemoved + " = 0" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")) " : " ") + "AND (UPPER(" + columnNameTitle + ") NOT BETWEEN " + sqlQuote(array[0].ToString()) + " AND " + sqlQuote(array[1].ToString()) + " OR UPPER(" + columnNameTitle + ") = " + sqlQuote(array[1].ToString()) + ")" + ((bmsTable.entry_type != LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ") " : " ") + "ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true);
        list.Add(getCustomFolderText(command, bmsTable.name, title));
        return list;
    }

    /// <summary>
    /// 譜面レベル別カスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>レベル別フォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsLevelFolder(BMSTable bmsTable)
    {
        List<int> source = (from e in (from e in bmsTable.entries
                                       where e.level.HasValue
                                       select (int)Math.Floor(e.level.Value)).Distinct()
                            orderby e
                            select e).ToList();
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameLevel = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.level);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        List<string> list = source.Select(delegate (int l)
        {
            string text = "CASE WHEN " + columnNameLevel + " >= 0 OR CAST(" + columnNameLevel + " AS INTEGER) = " + columnNameLevel + " THEN CAST(" + columnNameLevel + " AS INTEGER) ELSE CAST(" + columnNameLevel + " - 1.0 AS INTEGER) END";
            return getCustomFolderText("song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + text + " = " + l + " AND " + columnNameIsRemoved + " = 0)ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, asc: true, bmsTable.playlist_id) + "," + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true), bmsTable.name, "LEVEL " + l);
        }).ToList();
        if (bmsTable.entries.Any((BMSTableEntry e) => !e.level.HasValue))
        {
            string command = "song.hash in (SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameLevel + " IS NULL AND " + columnNameIsRemoved + " = 0)ORDER BY " + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL, asc: true, bmsTable.playlist_id) + "," + makeCustomFolderCmdSort(LR2SongDBExtended.playlist.CustomFolderSortType.TITLE, asc: true);
            list.Add(getCustomFolderText(command, bmsTable.name, "LEVEL ???"));
        }
        return list;
    }

    /// <summary>
    /// ユーザー定義フォルダ順に基づくカスタムフォルダの LR2 定義文字列を生成します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>ユーザーフォルダ定義の一覧。</returns>
    private List<string> makeCustomFolderTextsUserFolder(BMSTable bmsTable)
    {
        List<string> folder_list = bmsTable.folder_list;
        LR2SongDBExtended.playlist.CustomFolderSortType sortType = bmsTable.folder_sort_key;
        bool sortDirAsc = bmsTable.folder_sort_ascending;
        string tblNameEntry = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName();
        string columnNamePlaylistId = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
        string columnNameFolder = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder);
        string columnNameIsRemoved = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        string columnNameMD5 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        return folder_list.Select((string f) => getCustomFolderText(((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? "song.folder in (SELECT folder FROM song WHERE hash in (" : "song.hash   in (") + "SELECT " + columnNameMD5 + " FROM " + tblNameEntry + " WHERE " + columnNamePlaylistId + " = " + bmsTable.playlist_id + " AND " + columnNameFolder + " = " + sqlQuote(f) + " AND " + columnNameIsRemoved + " = 0)" + ((bmsTable.entry_type == LR2SongDBExtended.playlist.EntryUnitType.Folder) ? ")" : " ") + ((sortType == LR2SongDBExtended.playlist.CustomFolderSortType.NONE) ? string.Empty : (" ORDER BY " + makeCustomFolderCmdSort(sortType, sortDirAsc, bmsTable.playlist_id, f))), bmsTable.name, string.IsNullOrWhiteSpace(f) ? bmsTable.name : f)).ToList();
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
        string columnName = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.is_removed);
        string columnName2 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5);
        switch (ftype)
        {
            case LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL:
            case LR2SongDBExtended.playlist.CustomFolderSortType.ADDDATE:
                {
                    if (!playlist_id.HasValue)
                    {
                        throw new ArgumentNullException("playlist_id");
                    }
                    string columnName3 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        return "(SELECT " + ftype.ToColumnName() + " FROM " + tableName + " WHERE " + columnName2 + " = song.hash AND " + columnName3 + " = " + playlist_id + " AND " + columnName + " = 0) " + text;
                    }
                    string columnName4 = SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder);
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
    public void ChangeCustomFolderBaseDirectory(string outputDirBaseBefore, string outputDirBaseAfter)
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            foreach (BMSTable item in BMSTables.Where((BMSTable t) => !t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)))
            {
                EnsurePlaylistEntriesLoaded(item, "ChangeCustomFolderBaseDirectory");
                using (item.ReaderWriterLock.GetWriterGuard())
                {
                    removeCustomFolder(Path.Combine(outputDirBaseBefore, item.Output_dir), Path.Combine(outputDirBaseAfter, item.Output_dir));
                    createCustomFolder(item, Path.Combine(outputDirBaseAfter, item.Output_dir));
                }
            }
        }
    }

    /// <summary>
    /// ルートプレイリストのカスタムフォルダ出力先ベースディレクトリ変更を反映します。
    /// </summary>
    /// <param name="outputDirBaseBefore">変更前のベースディレクトリ。</param>
    /// <param name="outputDirBaseAfter">変更後のベースディレクトリ。</param>
    public void ChangeCustomFolderBaseDirectoryRoot(string outputDirBaseBefore, string outputDirBaseAfter)
    {
        using (rwlockBMSTables.GetReaderGuard())
        {
            foreach (BMSTable item in BMSTables.Where((BMSTable t) => t.is_root_folder && !string.IsNullOrWhiteSpace(t.Output_dir)))
            {
                EnsurePlaylistEntriesLoaded(item, "ChangeCustomFolderBaseDirectoryRoot");
                using (item.ReaderWriterLock.GetWriterGuard())
                {
                    removeCustomFolder(Path.Combine(outputDirBaseBefore, item.Output_dir), Path.Combine(outputDirBaseAfter, item.Output_dir));
                    createCustomFolder(item, Path.Combine(outputDirBaseAfter, item.Output_dir));
                }
            }
        }
    }

    /// <summary>
    /// プレイリストのカスタムフォルダ出力先変更を DB とファイルシステムの両方へ反映します。
    /// </summary>
    /// <param name="bmsTable">移行対象のプレイリスト。</param>
    /// <param name="outputDirPathBefore">変更前の出力先パス。</param>
    /// <param name="outputDirPathAfter">変更後の出力先パス。省略時は現設定から算出します。</param>
    /// <exception cref="InvalidOperationException">LR2DB モードでない場合。</exception>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="ArgumentException">出力先に必要な情報が不足している場合。</exception>
    public void MigrateCustomFolderOutputDirectory(BMSTable bmsTable, string outputDirPathBefore, string outputDirPathAfter = null)
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
                commitBMSTableHeaderOnly(bmsTable);
                removeCustomFolder(outputDirPathBefore, outputDirPathAfter);
                createCustomFolder(bmsTable, outputDirPathAfter);
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
                CommitBMSTable(bmsTable);
                removeCustomFolder(GetCustomFolderOutputDirectory(bmsTable), GetCustomFolderOutputDirectory(bmsTable));
                createCustomFolder(bmsTable, GetCustomFolderOutputDirectory(bmsTable));
            }
        }
    }

    /// <summary>
    /// プレイリスト設定に基づき、出力先ディレクトリへ <c>.lr2folder</c> 群を生成します。
    /// </summary>
    /// <param name="bmsTable">出力元のプレイリスト。</param>
    /// <param name="outputDir">出力先ディレクトリ。</param>
    private void createCustomFolder(BMSTable bmsTable, string outputDir)
    {
        List<string> list = new List<string>();
        foreach (Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>> item in new List<Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>>
        {
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.UserFolder, makeCustomFolderTextsUserFolder),
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.LevelFolder, makeCustomFolderTextsLevelFolder),
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder, makeCustomFolderTextsAlphabetFolder),
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.ClearFolder, makeCustomFolderTextsClearFolder),
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder, makeCustomFolderTextsDJLevelFolder),
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder, makeCustomFolderTextsCategoryAllFolder),
            new Tuple<LR2SongDBExtended.playlist.CustomFolderType, Func<BMSTable, List<string>>>(LR2SongDBExtended.playlist.CustomFolderType.OtherFolder, makeCustomFolderTextsOtherFolder)
        })
        {
            if ((item.Item1 & bmsTable.ignore_folder_output) == 0)
            {
                list.AddRange(item.Item2(bmsTable));
            }
        }
        list = list.ToList();
        if (list.Count() == 0)
        {
            try
            {
                if (Directory.Exists(outputDir))
                {
                    FileSystem.DeleteDirectory(outputDir, DeleteDirectoryOption.ThrowIfDirectoryNonEmpty);
                }
                return;
            }
            catch
            {
                return;
            }
        }
        int num = 0;
        try
        {
            Directory.CreateDirectory(outputDir);
            foreach (string item2 in list)
            {
                File.WriteAllText(Path.Combine(outputDir, $"{num:D4}" + ".lr2folder"), item2, Encoding.GetEncoding("shift_jis"));
                num++;
            }
        }
        catch
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_CustomFolderOutputFailed, bmsTable.name, outputDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
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
                removeCustomFolder(GetCustomFolderOutputDirectory(bmsTable));
            }
        }
    }

    /// <summary>
    /// 指定ディレクトリ配下の <c>.lr2folder</c> と LR2 DB 上の対応フォルダ情報を削除します。
    /// 同階層への移動時は DB のパスだけ新ディレクトリへ付け替えます。
    /// </summary>
    /// <param name="targetDir">削除対象ディレクトリ。</param>
    /// <param name="newDir">同階層移動時の移動先ディレクトリ。</param>
    private void removeCustomFolder(string targetDir, string newDir = null)
    {
        if (!Directory.Exists(targetDir))
        {
            return;
        }
        try
        {
            foreach (string item in Directory.EnumerateFiles(targetDir, "*.lr2folder", System.IO.SearchOption.TopDirectoryOnly))
            {
                FileSystem.DeleteFile(item, UIOption.OnlyErrorDialogs, RecycleOption.DeletePermanently);
            }
            if ((newDir == null || !targetDir.Equals(newDir, StringComparison.OrdinalIgnoreCase)) && Directory.GetFileSystemEntries(targetDir).Count() == 0)
            {
                FileSystem.DeleteDirectory(targetDir, DeleteDirectoryOption.ThrowIfDirectoryNonEmpty);
            }
        }
        catch
        {
            DispatcherMessageBox.Show(string.Format(Resources.Warn_FileOrDirDeleteFailed, targetDir), Resources.MessageBoxTitle_Warning, MessageBoxButton.OK, MessageBoxImage.Exclamation, MessageBoxResult.OK);
        }
        LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
        try
        {
            lr2Song.BeginTransaction();
            (from f in lr2Song.Table<LR2SongDB.folder>().ToList()
             where !string.Equals(f.path, targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && f.path.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase)
             select f.path).ToList().ForEach(delegate (string p)
         {
             lr2Song.Delete<LR2SongDB.folder>(p);
         });
            List<LR2SongDB.folder> source = (from f in lr2Song.Table<LR2SongDB.folder>().ToList()
                                             where string.Equals(f.path, targetDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                             select f).ToList();
            if (source.Count() > 0)
            {
                LR2SongDB.folder folder = source.First();
                lr2Song.Delete<LR2SongDB.folder>(folder.path);
                if (!string.IsNullOrWhiteSpace(newDir) && string.Equals(Path.GetDirectoryName(targetDir), Path.GetDirectoryName(newDir), StringComparison.OrdinalIgnoreCase))
                {
                    folder.path = folder.path.ReplaceFromStart(targetDir, newDir, isIgnoreCase: true);
                    folder.date = null;
                    folder.adddate = null;
                    lr2Song.InsertOrReplace(folder, typeof(LR2SongDB.folder));
                }
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

    internal async Task<BMSTable> RegistrateExternalTableAsync(Uri pageUri, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (!pageUri.IsAbsoluteUri)
        {
            throw new InvalidOperationException("pageUri.IsAbsoluteUri is not true");
        }
        BMSTable bMSTable = await LoadExternalTableAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
        using (rwlockBMSTablesInitializeMin.GetReaderGuard())
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                if (bMSTable.last_update == default(DateTime))
                {
                    bMSTable.last_update = DateTime.Now;
                }
                if (BMSTables.Select((BMSTable t) => t.name).Contains(bMSTable.name))
                {
                    throw new PlaylistAlreadyExistsException(Resources.Error_PlaylistAlreadyExists, bMSTable.name);
                }
                if (string.IsNullOrWhiteSpace(bMSTable.Output_dir))
                {
                    throw new InvalidOperationException(Resources.Error_OutputDirNameEmpty);
                }
                CommitBMSTable(bMSTable);
                BMSTables.Add(bMSTable);
                if (Settings.Default.OperationModeLR2DB)
                {
                    string customFolderOutputDirectory = GetCustomFolderOutputDirectory(bMSTable);
                    removeCustomFolder(customFolderOutputDirectory);
                    createCustomFolder(bMSTable, customFolderOutputDirectory);
                }
            }
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

    internal async Task<List<BMSTable>> UpdateBMSTablesInternalAsync(bool reloadExtPlaylist = true, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null, Action<PlaylistSyncAttemptResult> syncResultCallback = null, Action<PlaylistSyncProgressSnapshot> progressCallback = null, CancellationToken cancellationToken = default(CancellationToken))
    {
        IsPlaylistUpdating = true;
        try
        {
            Stopwatch stopwatchUpdateTablesTotal = Stopwatch.StartNew();
            List<BMSTable> tableSnapshot;
            using (rwlockBMSTables.GetReaderGuard())
            {
                tableSnapshot = BMSTables.ToList();
            }
            List<BMSTable> reloadTargets = tableSnapshot.Where(delegate(BMSTable table)
            {
                Uri uri2 = table?.Page_url ?? table?.Header_url;
                return reloadExtPlaylist && table != null && table.is_external_sync && uri2 != null && uri2.IsAbsoluteUri;
            }).ToList();
            List<PlaylistReloadTargetResult> results = await ReloadPlaylistTargetsAsync(
                reloadTargets,
                updateCallbackActions,
                syncResultCallback,
                progressCallback,
                "UpdateBMSTablesInternalAsync",
                cancellationToken).ConfigureAwait(false);
            if (updateCallbackActions != null)
            {
                HashSet<BMSTable> reloadedTables = new HashSet<BMSTable>(results.Select((PlaylistReloadTargetResult result) => result.SourceTable).Where((BMSTable table) => table != null));
                foreach (BMSTable table in tableSnapshot.Where((BMSTable table) => table != null && !reloadedTables.Contains(table)))
                {
                    InvokePlaylistUpdateCallbacks(new PlaylistTableUpdateContext
                    {
                        NewTable = table,
                        Updated = false,
                        OldTable = table,
                        OldEntriesSnapshot = null,
                        NewEntriesSnapshot = null
                    }, updateCallbackActions, table.Page_url ?? table.Header_url);
                }
            }
            stopwatchUpdateTablesTotal.Stop();
            List<BMSTable> updatedTables = results.Where((PlaylistReloadTargetResult result) => result.Succeeded && result.Updated && result.ResultTable != null).Select((PlaylistReloadTargetResult result) => result.ResultTable).ToList();
            LogPlaylistPerformance("playlist_update table_count=" + tableSnapshot.Count + " target_count=" + reloadTargets.Count + " updated_count=" + updatedTables.Count + " total_ms=" + stopwatchUpdateTablesTotal.ElapsedMilliseconds);
            return updatedTables;
        }
        finally
        {
            IsPlaylistUpdating = false;
        }
    }

    internal async Task<List<PlaylistReloadTargetResult>> ReloadPlaylistTargetsAsync(IEnumerable<BMSTable> targets, List<Action<PlaylistTableUpdateContext>> updateCallbackActions = null, Action<PlaylistSyncAttemptResult> syncResultCallback = null, Action<PlaylistSyncProgressSnapshot> progressCallback = null, string reason = "ReloadPlaylistTargetsAsync", CancellationToken cancellationToken = default(CancellationToken))
    {
        List<BMSTable> targetSnapshot = (targets ?? Enumerable.Empty<BMSTable>())
            .Where((BMSTable table) => table != null)
            .Distinct()
            .Where(delegate(BMSTable table)
            {
                Uri uri = table.Page_url ?? table.Header_url;
                return uri != null && uri.IsAbsoluteUri;
            })
            .ToList();
        int completedTableCount = 0;
        List<PlaylistReloadTargetResult> results = new List<PlaylistReloadTargetResult>();
        object resultLock = new object();
        progressCallback?.Invoke(new PlaylistSyncProgressSnapshot
        {
            IsActive = targetSnapshot.Count > 0,
            TotalTableCount = targetSnapshot.Count,
            CompletedTableCount = 0,
            CurrentTableName = string.Empty,
            CurrentUri = null
        });
        using SemaphoreSlim semaphoreSlim = new SemaphoreSlim(ExternalPlaylistSyncMaxConcurrency, ExternalPlaylistSyncMaxConcurrency);
        await Task.WhenAll(targetSnapshot.Select(async delegate(BMSTable table)
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
        }).ToArray()).ConfigureAwait(false);
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

    private async Task<PlaylistReloadTargetResult> ReloadPlaylistTargetCoreAsync(BMSTable table, Uri uri, List<Action<PlaylistTableUpdateContext>> updateCallbackActions, Action<PlaylistSyncAttemptResult> syncResultCallback, string reason, CancellationToken cancellationToken)
    {
        BMSTable newTable = table;
        List<BMSTableEntry> oldEntriesSnapshot = null;
        List<BMSTableEntry> newEntriesSnapshot = null;
        bool updated = false;
        bool hasStateToPersist = false;
        Exception failure = null;
        try
        {
            BMSTable reloadedTable = await reloadBMSTableAsync(table, uri, cancellationToken).ConfigureAwait(false);
            EnsurePlaylistEntriesLoaded(table, reason);
            using (table.ReaderWriterLock.GetWriterGuard())
            {
                oldEntriesSnapshot = table.entries?.ToList() ?? new List<BMSTableEntry>();
                IReadOnlyList<BMSTableEntry> persistedActiveEntries = LoadPersistedActivePlaylistEntries(table.playlist_id);
                newTable = MergeReloadedBMSTableState(table, reloadedTable, BuildComparablePlaylistEntryRows(persistedActiveEntries), out updated, out hasStateToPersist, logLastUpdateDecision: true);
                if (updated)
                {
                    newEntriesSnapshot = newTable.entries?.ToList() ?? new List<BMSTableEntry>();
                }
                else
                {
                    newEntriesSnapshot = oldEntriesSnapshot;
                    if (!hasStateToPersist)
                    {
                        newTable = table;
                    }
                }
            }
            if (hasStateToPersist)
            {
                CommitBMSTable(newTable);
                ReplaceBMSTableInCollection(table, newTable);
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
        PlaylistTableUpdateContext updateContext = new PlaylistTableUpdateContext
        {
            NewTable = newTable,
            Updated = updated,
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
            foreach (Action<PlaylistTableUpdateContext> item in updateCallbackActions.Where((Action<PlaylistTableUpdateContext> action) => action != null))
            {
                item(updateContext);
            }
        }
        catch (Exception ex)
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Warn(ex, "playlist_update_callback_failed table=" + FormatTextForLog(updateContext?.NewTable?.name) + " uri=" + FormatUriForLog(uri));
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
    /// <c>is_external_sync</c> の有無に関わらず明示指定されたプレイリストを対象にし、<c>last_update</c> は実際の構成差分に応じて維持または更新されます。
    /// </summary>
    /// <param name="bmsTable">再同期対象のプレイリスト。</param>
    /// <param name="pageUri">再取得に使用する URI。省略時は対象プレイリストに保持された URL を使用します。</param>
    /// <returns>差分統合後のプレイリスト。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    public BMSTable ResetBMSTable(BMSTable bmsTable, Uri pageUri = null)
    {
        return ResetBMSTableAsync(bmsTable, pageUri).GetAwaiter().GetResult();
    }

    internal async Task<BMSTable> ResetBMSTableAsync(BMSTable bmsTable, Uri pageUri = null, CancellationToken cancellationToken = default(CancellationToken))
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
        using (rwlockBMSTables.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                BMSTables.RemoveExt(bmsTable);
                deleteBMSTable(bmsTable);
            }
        }
        QueueBeatorajaBmtExportAll("RemoveBMSTable");
    }

    /// <summary>
    /// 新規の空プレイリストを生成し、一覧へ追加します。
    /// </summary>
    /// <returns>追加された新規プレイリスト。</returns>
    public BMSTable CreateBMSTable()
    {
        BMSTable bMSTable = new BMSTable
        {
            last_update = DateTime.Now
        };
        using (rwlockBMSTablesInitializeMin.GetReaderGuard())
        {
            using (rwlockBMSTables.GetWriterGuard())
            {
                BMSTables.Add(bMSTable);
                return bMSTable;
            }
        }
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
    /// <param name="bmsEntries">追加するエントリ群。</param>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <param name="folderName">追加先フォルダ名。</param>
    /// <param name="commitFlag">変更後に DB 反映するかどうか。</param>
    /// <exception cref="ArgumentNullException"><paramref name="bmsTable"/> が <see langword="null"/> の場合。</exception>
    internal void AddEntriesToFolderBMSTable(IEnumerable<BMSTableEntry> bmsEntries, BMSTable bmsTable, string folderName, bool commitFlag = true)
    {
        if (bmsTable == null)
        {
            throw new ArgumentNullException("bmsTable");
        }
        EnsurePlaylistEntriesLoaded(bmsTable, "AddEntriesToFolderBMSTable");
        using (bmsTable.ReaderWriterLock.GetWriterGuard())
        {
            if (BMSTables.Contains(bmsTable))
            {
                bmsTable.AddBMSTableEntriesToFolder(bmsEntries, folderName);
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
                if (Settings.Default.OperationModeLR2DB)
                {
                    ReOutputCustomFolder(bmsTable);
                }
                else
                {
                    CommitBMSTable(bmsTable);
                }
            }
        }
        QueueBeatorajaBmtExport(bmsTable, "ReOutputCustomFolderAndCommitToDB");
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

    internal async Task<BMSTable> LoadExternalTableAsync(Uri pageUri, BMSTable baseTable = null, CancellationToken cancellationToken = default(CancellationToken))
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
            string header_json = null;
        try
        {
            string input = await playlistHttpClient.GetStringAsync(pageUri, null, cancellationToken).ConfigureAwait(false);
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
            bMSTable.LoadHeaderJSON(header_json, pageUri, headerUri);
            if (baseTable != null)
            {
                bMSTable.playlist_id = baseTable.playlist_id;
                bMSTable.name = baseTable.name;
                bMSTable.symbol = baseTable.symbol;
                bMSTable.ignore_folder_output = baseTable.ignore_folder_output;
                bMSTable.is_external_sync = baseTable.is_external_sync;
                bMSTable.Output_dir = baseTable.Output_dir;
                bMSTable.is_root_folder = baseTable.is_root_folder;
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
        StringBuilder stringBuilder = new StringBuilder();
        try
        {
            XDocument xDocument;
            using (SgmlReader reader = new SgmlReader
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
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)), out _, logLastUpdateDecision);
    }

    private async Task<BMSTable> MergeReloadedBMSTableWithExistingStateAsync(BMSTable oldTable, Uri pageUri = null, bool logLastUpdateDecision = false, CancellationToken cancellationToken = default(CancellationToken))
    {
        if (pageUri == null)
        {
            pageUri = oldTable.Page_url ?? oldTable.Header_url;
        }
        BMSTable reloadedTable = await reloadBMSTableAsync(oldTable, pageUri, cancellationToken).ConfigureAwait(false);
        EnsurePlaylistEntriesLoaded(oldTable, "MergeReloadedBMSTableWithExistingStateAsync");
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)), out _, logLastUpdateDecision);
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
        return MergeReloadedBMSTableState(oldTable, reloadedTable, BuildComparablePlaylistEntryRows(oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed)), out _, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, bool logLastUpdateDecision = false)
    {
        return MergeReloadedBMSTableState(oldTable, reloadedTable, persistedActiveRows, out hasContentChanges, out _, logLastUpdateDecision);
    }

    internal static BMSTable MergeReloadedBMSTableState(BMSTable oldTable, BMSTable reloadedTable, IReadOnlyCollection<ComparablePlaylistEntryRow> persistedActiveRows, out bool hasContentChanges, out bool hasStateToPersist, bool logLastUpdateDecision = false)
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
        List<ComparablePlaylistEntryRow> list = persistedActiveRows?.Where((ComparablePlaylistEntryRow row) => row != null).ToList();
        IReadOnlyList<ComparablePlaylistEntryRow> normalizedPersistedRows = (list != null) ? list : (IReadOnlyList<ComparablePlaylistEntryRow>)Array.Empty<ComparablePlaylistEntryRow>();
        IReadOnlyList<ComparablePlaylistEntryRow> normalizedReloadedRows = BuildComparablePlaylistEntryRows(newTable.entries);
        PlaylistContentDiffResult playlistContentDiffResult = AnalyzePlaylistContentDiff(normalizedPersistedRows, normalizedReloadedRows);
        PlaylistHashChangeResult hashChangeResult = AnalyzePlaylistHashChanges(oldTable, newTable);
        hasContentChanges = playlistContentDiffResult.HasChanges || hashChangeResult.HasKnownChanges;
        hasStateToPersist = hasContentChanges || hashChangeResult.HasHashInitialization;

        Dictionary<string, List<BMSTableEntry>> newEntriesByMd5 = BuildEntryLookup(newTable.entries, (BMSTableEntry entry) => entry.md5, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<BMSTableEntry>> newEntriesBySha256 = BuildEntryLookup(newTable.entries, (BMSTableEntry entry) => entry.sha256, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<BMSTableEntry>> newEntriesByComparableRow = BuildComparableEntryLookup(newTable.entries);

        List<BMSTableEntry> matchedOldEntries = oldTable.entries.Where(delegate (BMSTableEntry oe)
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
        }).ToList();

        DateTime oldLastUpdate = oldTable.last_update;
        DateTime reloadedLastUpdate = newTable.last_update;
        if (oldTable.playlist_id.HasValue)
        {
            newTable.last_update = hasContentChanges ? DateTime.Now : oldTable.last_update;
        }
        else if (hasContentChanges)
        {
            newTable.last_update = ((reloadedLastUpdate != default(DateTime)) ? reloadedLastUpdate : DateTime.Now);
        }
        else
        {
            newTable.last_update = ((reloadedLastUpdate != default(DateTime)) ? reloadedLastUpdate : oldTable.last_update);
        }

        List<BMSTableEntry> removedEntries = oldTable.entries.Except(matchedOldEntries).ToList();
        foreach (BMSTableEntry item in removedEntries)
        {
            item.is_removed = true;
        }
        newTable.entries = newTable.entries.Concat(removedEntries).ToList();
        bool lastUpdateChanged = newTable.last_update != oldLastUpdate;
        if (logLastUpdateDecision)
        {
            if (hasContentChanges)
            {
                LogPlaylistContentDiff(newTable.name, playlistContentDiffResult);
            }
            Ribbit.Logging.NLogWrapper.FileLogger?.Info("playlist_resync last_update_decision table=" + (newTable.name ?? string.Empty) + " changed=" + hasContentChanges.ToString().ToLowerInvariant() + " structuralChanges=" + playlistContentDiffResult.HasChanges.ToString().ToLowerInvariant() + " hashChanges=" + hashChangeResult.HasKnownChanges.ToString().ToLowerInvariant() + " hashInitialized=" + hashChangeResult.HasHashInitialization.ToString().ToLowerInvariant() + " persist=" + hasStateToPersist.ToString().ToLowerInvariant() + " old=" + oldLastUpdate.ToString("O") + " reloaded=" + reloadedLastUpdate.ToString("O") + " final=" + newTable.last_update.ToString("O"));
        }
        return newTable;
    }

    private static PlaylistHashChangeResult AnalyzePlaylistHashChanges(BMSTable oldTable, BMSTable newTable)
    {
        PlaylistHashChangeResult result = new PlaylistHashChangeResult();
        if (oldTable == null || newTable == null)
        {
            return result;
        }
        ApplyHashChange(result, oldTable.header_sha256, newTable.header_sha256);
        ApplyHashChange(result, oldTable.data_sha256, newTable.data_sha256);
        return result;
    }

    private static void ApplyHashChange(PlaylistHashChangeResult result, string oldHash, string newHash)
    {
        string normalizedOldHash = NormalizeHash(oldHash);
        string normalizedNewHash = NormalizeHash(newHash);
        if (string.Equals(normalizedOldHash, normalizedNewHash, StringComparison.Ordinal))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(normalizedOldHash) && !string.IsNullOrWhiteSpace(normalizedNewHash))
        {
            result.HasHashInitialization = true;
            return;
        }
        result.HasKnownChanges = true;
    }

    private static Dictionary<string, List<BMSTableEntry>> BuildEntryLookup(IEnumerable<BMSTableEntry> entries, Func<BMSTableEntry, string> keySelector, IEqualityComparer<string> comparer)
    {
        return entries.Where(delegate (BMSTableEntry entry)
        {
            string text = keySelector(entry);
            return !string.IsNullOrWhiteSpace(text);
        }).GroupBy((BMSTableEntry entry) => keySelector(entry), comparer).ToDictionary((IGrouping<string, BMSTableEntry> group) => group.Key, (IGrouping<string, BMSTableEntry> group) => group.ToList(), comparer);
    }

    private static BMSTableEntry ResolveReloadedEntryMatch(BMSTableEntry oldEntry, Dictionary<string, List<BMSTableEntry>> entriesByMd5, Dictionary<string, List<BMSTableEntry>> entriesBySha256, Dictionary<string, List<BMSTableEntry>> entriesByComparableRow)
    {
        List<BMSTableEntry> list = GetEntryMatchCandidates(oldEntry.md5, entriesByMd5);
        if (list == null)
        {
            list = GetEntryMatchCandidates(oldEntry.sha256, entriesBySha256);
        }
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
            return Array.Empty<BMSTableEntry>();
        }
        using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        using (BMSTableEntry.BeginBulkLoadParseSuppression())
        {
            string sql = "SELECT * FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.playlist_id) + " = ?";
            if (activeOnly)
            {
                sql += " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry entry) => entry.is_removed) + " = 0";
            }
            sql += ";";
            return lR2SongDBExtended.Query<BMSTableEntry>(sql, playlistId.Value);
        }
    }

    private static Dictionary<string, List<BMSTableEntry>> BuildComparableEntryLookup(IEnumerable<BMSTableEntry> entries)
    {
        Dictionary<string, List<BMSTableEntry>> dictionary = new Dictionary<string, List<BMSTableEntry>>(StringComparer.Ordinal);
        foreach (BMSTableEntry entry in entries ?? Enumerable.Empty<BMSTableEntry>())
        {
            ComparablePlaylistEntryRow comparableRow = CreateComparablePlaylistEntryRow(entry);
            if (comparableRow == null)
            {
                continue;
            }
            if (!dictionary.TryGetValue(comparableRow.Fingerprint, out List<BMSTableEntry> value))
            {
                value = new List<BMSTableEntry>();
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
            return Array.Empty<ComparablePlaylistEntryRow>();
        }
        return entries.Select(CreateComparablePlaylistEntryRow).Where((ComparablePlaylistEntryRow row) => row != null).ToList();
    }

    internal static ComparablePlaylistEntryRow CreateComparablePlaylistEntryRow(BMSTableEntry entry)
    {
        if (entry == null)
        {
            return null;
        }
        ComparablePlaylistEntryRow comparableRow = new ComparablePlaylistEntryRow
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
        PlaylistContentDiffResult playlistContentDiffResult = new PlaylistContentDiffResult
        {
            PersistedOnlySamples = Array.Empty<string>(),
            ReloadedOnlySamples = Array.Empty<string>()
        };
        List<string> list = null;
        List<string> list2 = null;
        foreach (string item in dictionary.Keys.Union(dictionary2.Keys, StringComparer.Ordinal).OrderBy((string key) => key, StringComparer.Ordinal))
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
                list = list ?? new List<string>(PlaylistDiffSampleLogCount);
                AppendDiffSamples(list, item, num);
            }
            else
            {
                int num2 = value2 - value;
                playlistContentDiffResult.ReloadedOnlyCount += num2;
                list2 = list2 ?? new List<string>(PlaylistDiffSampleLogCount);
                AppendDiffSamples(list2, item, num2);
            }
        }
        playlistContentDiffResult.HasChanges = playlistContentDiffResult.PersistedOnlyCount > 0 || playlistContentDiffResult.ReloadedOnlyCount > 0;
        playlistContentDiffResult.PersistedOnlySamples = (IReadOnlyList<string>)(list != null ? list : (IReadOnlyList<string>)Array.Empty<string>());
        playlistContentDiffResult.ReloadedOnlySamples = (IReadOnlyList<string>)(list2 != null ? list2 : (IReadOnlyList<string>)Array.Empty<string>());
        return playlistContentDiffResult;
    }

    internal static bool HasPlaylistContentChanges(IEnumerable<ComparablePlaylistEntryRow> persistedRows, IEnumerable<ComparablePlaylistEntryRow> reloadedRows)
    {
        return AnalyzePlaylistContentDiff(persistedRows, reloadedRows).HasChanges;
    }

    private static Dictionary<string, int> BuildComparableRowFingerprintCounts(IEnumerable<ComparablePlaylistEntryRow> rows)
    {
        Dictionary<string, int> dictionary = new Dictionary<string, int>(StringComparer.Ordinal);
        if (rows == null)
        {
            return dictionary;
        }
        foreach (ComparablePlaylistEntryRow row in rows.Where((ComparablePlaylistEntryRow row) => row != null))
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
        IEnumerable<string> enumerable = samples ?? (IEnumerable<string>)Array.Empty<string>();
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
        StringBuilder stringBuilder = new StringBuilder(value.Length);
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
        StringBuilder stringBuilder = new StringBuilder();
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
        lookup.TryGetValue(key, out var value);
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
        return candidates.OrderBy((BMSTableEntry ne) => Math.Abs((ne.level ?? 0.0) - (oldEntry.level ?? 0.0))).First();
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
        return newTable.entries.Count != matchedOldEntryCount || matchedOldEntryCount != oldTable.entries.Where((BMSTableEntry entry) => !entry.is_removed).Count() || oldFolderList.Except(newFolderList).Any() || newFolderList.Except(oldFolderList).Any();
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

    private async Task<BMSTable> reloadBMSTableAsync(BMSTable bmsTable, Uri pageUri = null, CancellationToken cancellationToken = default(CancellationToken))
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
        CommitBMSTable(new BMSTable[1] { bmsTable });
    }

    /// <summary>
    /// 複数プレイリストを DB へ保存し、対応するエントリも全置換します。
    /// </summary>
    /// <param name="bmsTables">保存対象のプレイリスト群。</param>
    private void CommitBMSTable(IEnumerable<BMSTable> bmsTables)
    {
        try
        {
            List<BMSTable> tableList = bmsTables?.Where((BMSTable table) => table != null).ToList() ?? new List<BMSTable>();
            foreach (BMSTable table in tableList)
            {
                EnsurePlaylistEntriesLoaded(table, "CommitBMSTable");
            }
            LR2SongDBExtended lr2Song = new LR2SongDBExtended(lr2SongDBPath);
            try
            {
                lr2Song.BeginTransaction();
                foreach (BMSTable bmsTable in tableList)
                {
                    lr2Song.InsertOrReplace(bmsTable, typeof(LR2SongDBExtended.playlist));
                    ReplacePersistedCourses(lr2Song, bmsTable);
                    lr2Song.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
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
        try
        {
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id) + " = " + entry.playlist_id + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.md5) + BuildNullableSqlEquality(entry.md5, blankAsNull: true) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.sha256) + BuildNullableSqlEquality(entry.sha256, blankAsNull: true) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.folder) + " = " + sqlQuote(entry.folder) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.lr2_bmsid) + BuildNullableSqlEquality(entry.lr2_bmsid, blankAsNull: true) + " AND " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.title) + BuildNullableSqlEquality(entry.title) + ";");
            lR2SongDBExtended.InsertOrReplace(entry, typeof(LR2SongDBExtended.playlist_entry));
            lR2SongDBExtended.Commit();
        }
        catch
        {
            throw;
        }
        if (owningTable != null)
        {
            QueueBeatorajaBmtExport(owningTable, "CommitBMSTableEntry");
        }
    }

    /// <summary>
    /// 単一プレイリストを削除するためのラッパーです。
    /// </summary>
    /// <param name="bmsTable">削除対象のプレイリスト。</param>
    private void deleteBMSTable(BMSTable bmsTable)
    {
        deleteBMSTable(new BMSTable[1] { bmsTable });
    }

    /// <summary>
    /// 複数プレイリストと対応エントリを DB から削除します。
    /// </summary>
    /// <param name="bmsTables">削除対象のプレイリスト群。</param>
    private void deleteBMSTable(IEnumerable<BMSTable> bmsTables)
    {
        try
        {
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
            lR2SongDBExtended.BeginTransaction();
            foreach (BMSTable bmsTable in bmsTables)
            {
                if (bmsTable.playlist_id.HasValue)
                {
                    lR2SongDBExtended.Delete<LR2SongDBExtended.playlist>(bmsTable.playlist_id);
                    lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_entry>.GetColumnName((LR2SongDBExtended.playlist_entry e) => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
                    lR2SongDBExtended.Execute("DELETE FROM " + SQLiteTable<LR2SongDBExtended.playlist_course>.GetTableName() + " WHERE " + SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName((LR2SongDBExtended.playlist_course e) => e.playlist_id) + " = " + bmsTable.playlist_id + ";");
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
            using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
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
        string playlistIdColumn = SQLiteTable<LR2SongDBExtended.playlist_course>.GetColumnName((LR2SongDBExtended.playlist_course e) => e.playlist_id);
        db.Execute("DELETE FROM " + tableName + " WHERE " + playlistIdColumn + " = " + bmsTable.playlist_id + ";");
        int order = 0;
        foreach (LR2SongDBExtended.playlist_course course in bmsTable.Courses ?? Array.Empty<LR2SongDBExtended.playlist_course>())
        {
            if (string.IsNullOrWhiteSpace(course?.course_json))
            {
                continue;
            }
            LR2SongDBExtended.playlist_course row = new LR2SongDBExtended.playlist_course
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
        using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        string separator = "\v" + Environment.NewLine;
        string playlistDump = string.Join(separator, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist>()
                                                    select c.Replace(separator, Environment.NewLine));
        string courseDump = string.Join(separator, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist_course>()
                                                  select c.Replace(separator, Environment.NewLine));
        string entryDump = string.Join(separator, from c in lR2SongDBExtended.Dump<LR2SongDBExtended.playlist_entry>()
                                                 select c.Replace(separator, Environment.NewLine));
        return string.Join(separator, new string[3] { playlistDump, courseDump, entryDump });
    }

    /// <summary>
    /// プレイリスト関連テーブルを SQL ダンプから復元します。
    /// </summary>
    /// <param name="sql">復元する SQL ダンプ文字列。</param>
    public void LoadPlaylistDump(string sql)
    {
        using LR2SongDBExtended lR2SongDBExtended = new LR2SongDBExtended(lr2SongDBPath);
        using (new StringReader(sql))
        {
            string savepoint = lR2SongDBExtended.SaveTransactionPoint();
            try
            {
                lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist>();
                lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist_course>();
                lR2SongDBExtended.DropTable<LR2SongDBExtended.playlist_entry>();
                EnsurePlaylistTablesAndIndexes(lR2SongDBExtended);
                string[] source = sql.Split(new string[1] { "\v" + Environment.NewLine }, StringSplitOptions.None);
                if (source.Count() <= 1)
                {
                    throw new InvalidDataException(Resources.Error_InvalidBackupData);
                }
                foreach (string item in source.Where((string s) => !string.IsNullOrWhiteSpace(s)))
                {
                    lR2SongDBExtended.Execute(item);
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
        return source.Select((dynamic e) => new BMSTableSimple(e)).ToList();
    }

    private static string BuildNullableSqlEquality(string value, bool blankAsNull = false)
    {
        if (value == null || (blankAsNull && string.IsNullOrWhiteSpace(value)))
        {
            return " IS NULL ";
        }
        return " = " + sqlQuote(value);
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
    private static string getCustomFolderText(string command, string category, string title, int maxtracks = 0)
    {
        return "#COMMAND " + command + Environment.NewLine + "#MAXTRACKS " + maxtracks + Environment.NewLine + "#CATEGORY " + category + Environment.NewLine + "#TITLE " + title + Environment.NewLine + "#INFORMATION_A " + Environment.NewLine + "#INFORMATION_B " + Environment.NewLine + Environment.NewLine;
    }

    /// <summary>
    /// プレイリスト設定からカスタムフォルダ出力先ディレクトリを算出します。
    /// </summary>
    /// <param name="bmsTable">対象プレイリスト。</param>
    /// <returns>算出された出力先ディレクトリ。</returns>
    public static string GetCustomFolderOutputDirectory(BMSTable bmsTable)
    {
        try
        {
            return Path.Combine(bmsTable.is_root_folder ? Settings.Default.LR2CustomFolderOutputBaseDirRootType : Settings.Default.LR2CustomFolderOutputBaseDir, bmsTable.Output_dir);
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
