using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ribbit.Net;
using SQLite;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Walkure recommended / estimation workflow のキャンセル可能な非同期 HTTP 境界です。
/// </summary>
internal interface IPlaylistRecommendedTableHttpClient
{
    /// <summary>
    /// 指定 URI のレスポンス本文を、本文完了までの期限と取消し付きで取得します。
    /// </summary>
    Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken);

    /// <summary>
    /// 指定 URI へフォームを POST し、本文完了までの期限と取消し付きで応答を取得します。
    /// </summary>
    Task<string> PostFormAsync(Uri uri, NameValueCollection formData, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="AppHttpClient"/> を recommended / estimation workflow の HTTP 境界へ接続します。
/// </summary>
internal sealed class AppPlaylistRecommendedTableHttpClient : IPlaylistRecommendedTableHttpClient
{
    private readonly AppHttpClient httpClient;

    /// <summary>
    /// 指定した共有 HTTP クライアントを使用します。
    /// </summary>
    internal AppPlaylistRecommendedTableHttpClient(AppHttpClient httpClient)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    /// <inheritdoc />
    public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken)
    {
        return httpClient.GetStringAsync(uri, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public Task<string> PostFormAsync(Uri uri, NameValueCollection formData, CancellationToken cancellationToken)
    {
        return httpClient.PostFormAsync(uri, formData, cancellationToken: cancellationToken);
    }
}

/// <summary>
/// <c>bmseeker:</c> の recommended / estimation table を取得・構築する owner です。
/// </summary>
internal sealed class PlaylistRecommendedTableOwner
{
    private enum EstimationTableType
    {
        Easy,
        Normal,
        Hard,
        FullCombo
    }

    private sealed class EstimationData
    {
        public string type { get; set; }

        public string bmsid { get; set; }

        public EstimationHoshi hoshi { get; set; }
    }

    private sealed class EstimationHoshi
    {
        public double? easy { get; set; }

        public double? normal { get; set; }

        public double? hard { get; set; }

        public double? fc { get; set; }
    }

    private sealed class RecommendedData
    {
        public string type { get; set; }

        public string bmsid { get; set; }

        public string new_lamp { get; set; }

        public double? percent { get; set; }
    }

    private sealed class EstimationSource
    {
        public BMSTableEntry Entry { get; init; }

        public IReadOnlyList<EstimationData> Estimations { get; init; }
    }

    private static readonly Uri estimationJsonUri = new("http://walkure.net/hakkyou/data/bms.json", UriKind.Absolute);

    private static readonly string recommendJsonUriStr = "http://walkure.net/hakkyou/recommended_json.cgi?id=";

    private static readonly Uri walkureUpdateUri = new("http://walkure.net/hakkyou/mle.cgi", UriKind.Absolute);

    private static readonly Uri insaneUri = new("https://darksabun.club/table/archive/insane1/");

    private static readonly Uri overjoyUri = new("https://darksabun.club/table/archive/old-overjoy/");

    private static readonly Regex workAroundRegex = new("\"(?<id>\\d+)\":{", RegexOptions.Compiled);

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

    private readonly string lr2ScoreDbPath;

    private readonly Func<List<BMSScore>> bmsScoresProvider;

    private readonly Func<Uri, CancellationToken, Task<BMSTable>> externalTableLoader;

    private readonly PlaylistOperationNotificationOwner notificationOwner;

    private readonly IPlaylistRecommendedTableHttpClient httpClient;

    private readonly object insaneTableLock = new();
    private readonly SemaphoreSlim insaneTableLoadGate = new(1, 1);

    private readonly Func<CustomFolderOutputSettingsSnapshot> playlistSettingsProvider;

    private readonly object overjoyTableLock = new();
    private readonly SemaphoreSlim overjoyTableLoadGate = new(1, 1);

    private readonly SemaphoreSlim estimationTableLoadGate = new(1, 1);

    private BMSTable insaneTableValue;

    private BMSTable overjoyTableValue;

    private List<BMSTableEntry> easyEntries;

    private List<BMSTableEntry> normalEntries;

    private List<BMSTableEntry> hardEntries;

    private List<BMSTableEntry> fullComboEntries;

    /// <summary>
    /// 外部表と Walkure HTTP の非同期取得、および結果通知に必要な依存を構成します。
    /// </summary>
    internal PlaylistRecommendedTableOwner(
        string lr2ScoreDbPath,
        Func<List<BMSScore>> bmsScoresProvider,
        Func<Uri, CancellationToken, Task<BMSTable>> externalTableLoader,
        IPlaylistRecommendedTableHttpClient httpClient,
        PlaylistOperationNotificationOwner notificationOwner,
        Func<CustomFolderOutputSettingsSnapshot> playlistSettingsProvider)
    {
        this.lr2ScoreDbPath = lr2ScoreDbPath;
        this.bmsScoresProvider = bmsScoresProvider;
        this.externalTableLoader = externalTableLoader ?? throw new ArgumentNullException(nameof(externalTableLoader));
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.notificationOwner = notificationOwner ?? throw new ArgumentNullException(nameof(notificationOwner));
        this.playlistSettingsProvider = playlistSettingsProvider ?? throw new ArgumentNullException(nameof(playlistSettingsProvider));
    }

    /// <summary>
    /// 推定難度表またはリコメンド表を取得します。取消しは HTTP 本文、参照表取得、取得待機へ伝播します。
    /// </summary>
    /// <param name="pageUri">取得内容を指定する bmseeker URI。</param>
    /// <param name="baseTable">ローカル設定を引き継ぐ既存表。</param>
    /// <param name="cancellationToken">取得と取得待機を取り消すトークン。</param>
    /// <returns>取得が完了した表。失敗や取消しで部分的な表を返しません。</returns>
    internal async Task<BMSTable> LoadWalkureTableAsync(Uri pageUri, BMSTable baseTable = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!pageUri.IsAbsoluteUri || pageUri.Scheme != "bmseeker")
        {
            throw new ArgumentException(Resources.Error_SchemeMustBeBemusic, "pageUri");
        }
        BMSTable table = new()
        {
            Page_url = pageUri
        };
        if (baseTable != null)
        {
            table.compat_prefix = baseTable.compat_prefix;
            table.playlist_id = baseTable.playlist_id;
        }
        if (pageUri.AbsolutePath == "table.recommended")
        {
            string input = Uri.UnescapeDataString(pageUri.Query);
            int lr2Id = ParseQueryInt(input, "id");
            await LoadRecommendedTableAsync(
                table,
                baseTable,
                ParseQueryValue(input, "mode"),
                ParseQueryValue(input, "filter"),
                ParseQueryValue(input, "name"),
                lr2Id,
                ParseQueryValue(input, "base"),
                cancellationToken).ConfigureAwait(false);
        }
        else if (pageUri.AbsolutePath == "table.estimation")
        {
            await LoadEstimationTableAsync(table, pageUri.Query.TrimStart('?'), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException(Resources.Error_UnsupportedURI, pageUri.ToString());
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (baseTable != null)
        {
            table.symbol = baseTable.symbol;
            table.ignore_folder_output = baseTable.ignore_folder_output;
            table.is_external_sync = baseTable.is_external_sync;
            table.is_root_folder = baseTable.is_root_folder;
            table.custom_folder_output_base_name = baseTable.custom_folder_output_base_name;
            table.bmt_sort = baseTable.bmt_sort;
            table.is_bmt_output = baseTable.is_bmt_output;
        }
        return table;
    }

    private static int ParseQueryInt(string input, string key)
    {
        Match match = Regex.Match(input ?? string.Empty, key == "id" ? "id=(\\d+)" : Regex.Escape(key) + "=([^&]+)");
        return match.Success ? int.Parse(match.Groups[1].Value) : 0;
    }

    private static string ParseQueryValue(string input, string key)
    {
        string pattern = key == "id"
            ? "id=(\\d+)"
            : Regex.Escape(key) + "=([^&]+)";
        Match match = Regex.Match(input ?? string.Empty, pattern);
        return match.Success ? match.Groups[1].Value : null;
    }

    private async Task<BMSTable> GetInsaneTableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (insaneTableLock)
        {
            if (insaneTableValue != null)
            {
                return insaneTableValue;
            }
        }
        await insaneTableLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (insaneTableLock)
            {
                if (insaneTableValue != null)
                {
                    return insaneTableValue;
                }
            }
            BMSTable loaded;
            try
            {
                loaded = await externalTableLoader(insaneUri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
            lock (insaneTableLock)
            {
                insaneTableValue ??= loaded;
                return insaneTableValue;
            }
        }
        finally
        {
            insaneTableLoadGate.Release();
        }
    }

    private async Task<BMSTable> GetOverjoyTableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (overjoyTableLock)
        {
            if (overjoyTableValue != null)
            {
                return overjoyTableValue;
            }
        }
        await overjoyTableLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (overjoyTableLock)
            {
                if (overjoyTableValue != null)
                {
                    return overjoyTableValue;
                }
            }
            BMSTable loaded;
            try
            {
                loaded = await externalTableLoader(overjoyUri, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
            lock (overjoyTableLock)
            {
                overjoyTableValue ??= loaded;
                return overjoyTableValue;
            }
        }
        finally
        {
            overjoyTableLoadGate.Release();
        }
    }

    private async Task<List<BMSTableEntry>> GetEstimationEntriesAsync(EstimationTableType type, CancellationToken cancellationToken)
    {
        // variant 間の single-flight は維持するが、HTTP を Monitor 内で待たない。
        await estimationTableLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<BMSTableEntry> entries = type switch
            {
                EstimationTableType.Easy => easyEntries,
                EstimationTableType.Normal => normalEntries,
                EstimationTableType.Hard => hardEntries,
                EstimationTableType.FullCombo => fullComboEntries,
                _ => null
            };
            if (entries == null)
            {
                await BuildEstimationEntriesAsync(cancellationToken).ConfigureAwait(false);
            }
            return type switch
            {
                EstimationTableType.Easy => easyEntries,
                EstimationTableType.Normal => normalEntries,
                EstimationTableType.Hard => hardEntries,
                EstimationTableType.FullCombo => fullComboEntries,
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }
        finally
        {
            estimationTableLoadGate.Release();
        }
    }

    private async Task BuildEstimationEntriesAsync(CancellationToken cancellationToken)
    {
        string input = await httpClient.GetStringAsync(estimationJsonUri, cancellationToken).ConfigureAwait(false);
        input = workAroundRegex.Replace(input, "\"key${id}\":{");
        JObject dataJson = ParseJsonObject(input);
        BMSTable insane = await GetInsaneTableAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Load insane table failed");
        BMSTable overjoy = await GetOverjoyTableAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Load overjoy table failed");
        var inner = dataJson.Properties()
            .Select(property =>
            {
                try
                {
                    JObject item = property.Value as JObject ?? throw new FormatException("estimation item must be an object.");
                    string bmsid = null;
                    EstimationHoshi hoshi = null;
                    if (item.TryGetValue("bmsid", out JToken bmsIdToken))
                    {
                        bmsid = ParseRequiredInt(bmsIdToken).ToString();
                        JObject hoshiObject = item["hoshi"] as JObject ?? throw new FormatException("estimation hoshi must be an object.");
                        hoshi = new EstimationHoshi
                        {
                            easy = ParseRequiredNullableDouble(hoshiObject, "easy"),
                            normal = ParseRequiredNullableDouble(hoshiObject, "normal"),
                            hard = ParseRequiredNullableDouble(hoshiObject, "hard"),
                            fc = ParseRequiredNullableDouble(hoshiObject, "fc")
                        };
                    }
                    return new EstimationData
                    {
                        type = ParseRequiredNullableString(item, "type"),
                        bmsid = bmsid,
                        hoshi = hoshi
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(entry => entry != null && entry.type != "course")
            .ToList();
        var source = insane.entries.Concat(overjoy.entries)
            .GroupJoin(
                inner,
                entry => entry.lr2_bmsid,
                estimation => estimation.bmsid,
                (entry, estimations) => new EstimationSource
                {
                    Entry = CreateEstimationEntry(entry, entry.parent == insane ? "INSANE " : "Overjoy "),
                    Estimations = estimations.DefaultIfEmpty().ToList()
                })
            .ToList();
        easyEntries = BuildEstimationVariant(source, estimation => estimation.hoshi.easy);
        normalEntries = BuildEstimationVariant(source, estimation => estimation.hoshi.normal);
        hardEntries = BuildEstimationVariant(source, estimation => estimation.hoshi.hard);
        fullComboEntries = BuildEstimationVariant(source, estimation => estimation.hoshi.fc);
    }

    private static BMSTableEntry CreateEstimationEntry(BMSTableEntry entry, string prefix)
    {
        BMSTableEntry copy = entry.Duplicate();
        copy.folder = prefix + entry.parent.symbol + entry.parent.ConvertBackFolderNameToCompatibleLevelName(entry.folder);
        return copy;
    }

    private static List<BMSTableEntry> BuildEstimationVariant(
        IEnumerable<EstimationSource> source,
        Func<EstimationData, double?> levelSelector)
    {
        return [.. source.SelectMany(
            group => group.Estimations,
            (group, estimation) =>
            {
                BMSTableEntry entry = group.Entry.Duplicate();
                entry.level = estimation == null ? null : levelSelector(estimation);
                return entry;
            })];
    }

    private async Task LoadEstimationTableAsync(BMSTable table, string query, CancellationToken cancellationToken)
    {
        EstimationTableType type = query switch
        {
            "type=easy" => EstimationTableType.Easy,
            "type=normal" => EstimationTableType.Normal,
            "type=hard" => EstimationTableType.Hard,
            "type=fc" => EstimationTableType.FullCombo,
            _ => throw new ArgumentException(Resources.Error_UnsupportedURI, table.Page_url?.ToString())
        };
        table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
        table.folder_sort_ascending = true;
        table.is_external_sync = true;
        table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder
            | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
            | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
            | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder
            | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder
            | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder
            | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder
            | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder
            | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder
            | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
        table.entries = await GetEstimationEntriesAsync(type, cancellationToken).ConfigureAwait(false);
        string name;
        switch (type)
        {
            case EstimationTableType.Easy:
                name = table.org_name = Resources.Insane_estimation_table_easy;
                table.org_symbol = "E★";
                break;
            case EstimationTableType.Normal:
                name = table.org_name = Resources.Insane_estimation_table_normal;
                table.org_symbol = "N★";
                break;
            case EstimationTableType.Hard:
                name = table.org_name = Resources.Insane_estimation_table_hard;
                table.org_symbol = "H★";
                break;
            default:
                name = table.org_name = Resources.Insane_estimation_table_fc;
                table.org_symbol = "F★";
                break;
        }
        table.name = name;
        table.symbol = table.org_symbol;
    }

    private async Task LoadRecommendedTableAsync(
        BMSTable table,
        BMSTable baseTable,
        string mode,
        string filter,
        string displayName,
        int lr2Id,
        string baseline,
        CancellationToken cancellationToken)
    {
        string name = string.Empty;
        if (lr2Id == 0)
        {
            if (lr2ScoreDbPath == null)
            {
                throw new InvalidOperationException(Resources.Error_ScoreDBConnectionFailed);
            }
            try
            {
                using LR2ScoreDB scoreDb = new LR2ScoreDBExtended(lr2ScoreDbPath);
                LR2ScoreDB.player player = scoreDb.Table<LR2ScoreDB.player>().ToList().FirstOrDefault();
                lr2Id = player.irid.Value;
                name = player.name;
            }
            catch
            {
                lr2Id = 0;
            }
        }
        else
        {
            mode = "readonly";
        }
        if (lr2Id == 0)
        {
            throw new InvalidOperationException(Resources.Error_LR2IDOrScoreDBFailed);
        }
        if (mode != "readonly")
        {
            try
            {
                // 通常の通信失敗は従来どおり初回 + 最大5回。呼出し元の取消しは再試行しない。
                for (int retry = 0; ; retry++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await UpdatedClearedSongsAsync(mode, lr2Id, name, filter, baseline, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception) when (retry < 5 && !cancellationToken.IsCancellationRequested)
                    {
                        await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                notificationOwner.QueueWarning(string.Format(Resources.Warn_RecommendUpdateFailed, exception.Message), null);
            }
        }
        Uri address = new(recommendJsonUriStr + lr2Id, UriKind.Absolute);
        string response = await httpClient.GetStringAsync(address, cancellationToken).ConfigureAwait(false);
        JObject value = ParseJsonObject(response);
        if (ParseRequiredNullableString(value, "status") != "success")
        {
            notificationOwner.QueueWarning(string.Format(Resources.Warn_RecommendFetchFailed, ParseRequiredNullableString(value, "message")), null);
            throw new InvalidOperationException(Resources.Error_RecommendFetchFailed);
        }
        double skill = ParseRequiredDouble(value, "hoshi");
        DateTime lastModified = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddSeconds(ParseRequiredInt(value, "last_modified"))
            .ToLocalTime();
        name = ParseRequiredNullableString(value, "name").Replace('〜', '～');
        BMSTable insane = await GetInsaneTableAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Load insane table failed");
        BMSTable overjoy = await GetOverjoyTableAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Load overjoy table failed");
        IEnumerable<BMSTableEntry> sourceEntries = insane.entries.Concat(overjoy.entries)
            .Where(entry => !string.IsNullOrWhiteSpace(entry.md5) && !string.IsNullOrWhiteSpace(entry.lr2_bmsid))
            .GroupBy(entry => entry.md5)
            .Select(group => group.FirstOrDefault(entry => entry.parent == insane) ?? group.First());
        List<BMSTableEntry> entries = [.. ParseRequiredArray(value, "recommended")
            .Select(item =>
            {
                try
                {
                    JObject itemObject = item as JObject ?? throw new FormatException("recommendation item must be an object.");
                    JObject bms = itemObject["bms"] as JObject ?? throw new FormatException("recommendation bms must be an object.");
                    return new RecommendedData
                    {
                        type = ParseRequiredNullableString(bms, "type"),
                        bmsid = bms.TryGetValue("bmsid", out JToken bmsIdToken) ? ParseRequiredInt(bmsIdToken).ToString() : null,
                        new_lamp = ParseRequiredNullableString(itemObject, "new_lamp"),
                        percent = ParseRequiredNullableDouble(itemObject, "p")
                    };
                }
                catch
                {
                    return null;
                }
            })
            .Where(entry => entry != null && entry.type != "course" && entry.new_lamp != null)
            .ToList()
            .Join(sourceEntries, recommendation => recommendation.bmsid, entry => entry.lr2_bmsid, (recommendation, entry) =>
            {
                BMSTableEntry copy = entry.Duplicate();
                copy.folder = recommendation.new_lamp.ToUpperInvariant();
                copy.level = recommendation.percent;
                return copy;
            })];
        table.folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL;
        table.folder_sort_ascending = false;
        table.is_external_sync = true;
        table.entries = entries;
        table.last_update = DateTime.Parse(lastModified.ToString());
        table.ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
            | LR2SongDBExtended.playlist.CustomFolderType.AlphabetFolder
            | LR2SongDBExtended.playlist.CustomFolderType.ClearFolder
            | LR2SongDBExtended.playlist.CustomFolderType.DJLevelFolder
            | LR2SongDBExtended.playlist.CustomFolderType.CategoryAllFolder
            | LR2SongDBExtended.playlist.CustomFolderType.OtherFolder
            | LR2SongDBExtended.playlist.CustomFolderType.RandomFolder
            | LR2SongDBExtended.playlist.CustomFolderType.BpmSortFolder
            | LR2SongDBExtended.playlist.CustomFolderType.BpSortFolder
            | LR2SongDBExtended.playlist.CustomFolderType.PlayCountSortFolder
            | LR2SongDBExtended.playlist.CustomFolderType.LastPlaySortFolder;
        table.Folder_order = ["EASY", "NORMAL", "HARD", "FC"];
        table.name = table.org_name = string.Format(Resources.RecommendFormat, displayName ?? name, skill.ToString("F2"));
        table.symbol = table.org_symbol = "R★";
        try
        {
            CustomFolderOutputSettingsSnapshot settings = playlistSettingsProvider()
                ?? throw new InvalidOperationException("Playlist settings provider returned null.");
            if (baseTable == null || !settings.ShowRecommUpdatedMsg)
            {
                return;
            }
            Match match = new Regex("★(\\d+(?:\\.\\d+)?)").Match(baseTable.org_name);
            if (match.Success)
            {
                double previousSkill = double.Parse(match.Groups[1].Value);
                if (previousSkill != skill)
                {
                    notificationOwner.QueueInformation(
                        string.Format(Resources.Recommend_SkillUpdatedMessage, skill.ToString("F2"), (skill - previousSkill).ToString(" (+#0.00); (-#0.00);"), lastModified.ToString()),
                        Resources.Recommend_SkillUpdatedTitle);
                }
            }
        }
        catch
        {
        }
    }

    private static string ParseNullableString(JObject source, string propertyName)
    {
        return source.TryGetValue(propertyName, out JToken token) ? ParseNullableString(token) : null;
    }

    private static string ParseRequiredNullableString(JObject source, string propertyName)
    {
        if (!source.TryGetValue(propertyName, out JToken token))
        {
            throw new FormatException("Required string property is missing: " + propertyName);
        }
        return ParseNullableString(token);
    }

    private static string ParseNullableString(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Null => null,
            JTokenType.String => token.Value<string>(),
            _ => throw new FormatException("JSON value must be a string or null.")
        };
    }

    private static int ParseRequiredInt(JObject source, string propertyName)
    {
        if (!source.TryGetValue(propertyName, out JToken token))
        {
            throw new FormatException("Required integer property is missing: " + propertyName);
        }
        return ParseRequiredInt(token);
    }

    private static int ParseRequiredInt(JToken token)
    {
        if (token.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            throw new FormatException("JSON value must be numeric.");
        }
        double value = token.ToObject<double>();
        double truncated = Math.Truncate(value);
        if (double.IsNaN(truncated) || double.IsInfinity(truncated) || truncated < int.MinValue || truncated > int.MaxValue)
        {
            throw new FormatException("JSON number is outside the supported integer range.");
        }
        return (int)truncated;
    }

    private static double ParseRequiredDouble(JObject source, string propertyName)
    {
        if (!source.TryGetValue(propertyName, out JToken token) || token.Type is not (JTokenType.Integer or JTokenType.Float))
        {
            throw new FormatException("Required numeric property is missing or invalid: " + propertyName);
        }
        return token.ToObject<double>();
    }

    private static double? ParseNullableDouble(JObject source, string propertyName)
    {
        return source.TryGetValue(propertyName, out JToken token) ? ParseNullableDouble(token) : null;
    }

    private static double? ParseRequiredNullableDouble(JObject source, string propertyName)
    {
        if (!source.TryGetValue(propertyName, out JToken token))
        {
            throw new FormatException("Required numeric property is missing: " + propertyName);
        }
        return ParseNullableDouble(token);
    }

    private static double? ParseNullableDouble(JToken token)
    {
        return token.Type switch
        {
            JTokenType.Null => null,
            JTokenType.Integer or JTokenType.Float => token.ToObject<double?>(),
            _ => throw new FormatException("JSON value must be numeric or null.")
        };
    }

    private static JArray ParseRequiredArray(JObject source, string propertyName)
    {
        return source.TryGetValue(propertyName, out JToken token) && token is JArray array
            ? array
            : throw new FormatException("Required array property is missing or invalid: " + propertyName);
    }

    private static JObject ParseJsonObject(string json)
    {
        using var reader = new JsonTextReader(new StringReader(json))
        {
            DateParseHandling = DateParseHandling.None
        };
        var token = JToken.ReadFrom(reader);
        if (reader.Read())
        {
            throw new JsonReaderException("JSON document contains trailing content.");
        }
        return token as JObject ?? throw new FormatException("JSON document must be an object.");
    }

    private async Task UpdatedClearedSongsAsync(string mode, int lr2Id, string name, string filter, string baseline, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mode) || mode == "readonly")
        {
            return;
        }
        BMSTable insane = await GetInsaneTableAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Load insane table failed");
        BMSTable overjoy = await GetOverjoyTableAsync(cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("Load overjoy table failed");
        var entries = from entry in insane.entries.Concat(overjoy.entries)
                      where !string.IsNullOrWhiteSpace(entry.md5) && !string.IsNullOrWhiteSpace(entry.lr2_bmsid)
                      group entry by entry.md5 into grouped
                      select new
                      {
                          md5 = grouped.Key,
                          bmsid = grouped.First().lr2_bmsid
                      };
        List<BMSScore> scores = bmsScoresProvider?.Invoke() ?? throw new InvalidOperationException(Resources.Error_LocalScoreDataNotFetched);
        int threshold = filter switch
        {
            "hard" => 4,
            "clear" => 3,
            "easy" => 2,
            _ => 1,
        };
        var lampsBms = (from entry in entries
                        join score in scores on entry.md5 equals score.hash
                        select new
                        {
                            entry.bmsid,
                            lamp = ClearTypeStorageConverter.ToLr2Value(score.clear),
                            rank = (int)score.rank
                        } into score
                        where score.lamp >= threshold && score.lamp <= 5 && score.rank != 0
                        select score).ToList();
        var lampsGrade = (from grade in insaneGrade
                          join score in scores on grade.Value equals score.hash
                          select new
                          {
                              bmsid = (grade.Key + 100000000).ToString(),
                              lamp = ClearTypeStorageConverter.ToLr2Value(score.clear),
                              rank = (int)score.rank
                          } into score
                          where score.lamp >= threshold && score.lamp <= 5 && score.rank != 0
                          select score).ToList();
        if (baseline == "failed")
        {
            lampsBms = [.. entries.Select(entry =>
            {
                var existing = lampsBms.FirstOrDefault(item => item.bmsid == entry.bmsid);
                return new
                {
                    entry.bmsid,
                    lamp = existing?.lamp ?? 1,
                    rank = existing?.rank ?? 0
                };
            })];
            lampsGrade = [.. insaneGrade.Select(grade =>
            {
                var existing = lampsGrade.FirstOrDefault(item => item.bmsid == (grade.Key + 100000000).ToString());
                return new
                {
                    bmsid = (grade.Key + 100000000).ToString(),
                    lamp = existing?.lamp ?? 1,
                    rank = existing?.rank ?? 0
                };
            })];
        }
        IEnumerable<string> values = lampsBms.Concat(lampsGrade).Select(score => score.bmsid + "-" + score.lamp);
        _ = await httpClient.PostFormAsync(walkureUpdateUri, new NameValueCollection
        {
            { "name", name },
            { "id", lr2Id.ToString() },
            { "data", string.Join(",", values) }
        }, cancellationToken).ConfigureAwait(false);
    }
}
