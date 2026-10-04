using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>接続解放後の読取り状態と、モデルへ射影済みの変更不能な原観測です。</summary>
internal sealed record WalkureScoreInput(
    ScoreTableLoadStatus Status, ImmutableDictionary<string, WalkureLamp> Scores, string FailureMessage = "");

/// <summary>同梱モデルから内蔵推定表と、選択中スコアのリコメンド表を生成します。</summary>
internal sealed class PlaylistRecommendedTableOwner
{
    private readonly Func<CancellationToken, Task<WalkureScoreInput>> scoreReader;
    private readonly PlaylistOperationNotificationOwner notificationOwner;
    private readonly Func<CustomFolderOutputSettingsSnapshot> playlistSettingsProvider;

    /// <summary>ローカル原入力の読取り能力と、反映後の通知設定を接続します。</summary>
    internal PlaylistRecommendedTableOwner(
        Func<CancellationToken, Task<WalkureScoreInput>> scoreReader,
        PlaylistOperationNotificationOwner notificationOwner,
        Func<CustomFolderOutputSettingsSnapshot> playlistSettingsProvider)
    {
        this.scoreReader = scoreReader ?? throw new ArgumentNullException(nameof(scoreReader));
        this.notificationOwner = notificationOwner ?? throw new ArgumentNullException(nameof(notificationOwner));
        this.playlistSettingsProvider = playlistSettingsProvider ?? throw new ArgumentNullException(nameof(playlistSettingsProvider));
    }

    /// <summary>一操作で一度だけ読む原入力を遅延捕捉します。推定表のみなら読取りません。</summary>
    internal Lazy<Task<WalkureScoreInput>> CreateScoreCapture(CancellationToken cancellationToken)
        => new(() => scoreReader(cancellationToken));

    /// <summary>内蔵表を生成します。推薦の両名称は毎回自動生成し、推定表の手動名は保持します。取消・読取り失敗・推定不能は部分表へ置き換えません。</summary>
    /// <param name="pageUri">種類・方針を指定する保存用 URI。</param>
    /// <param name="baseTable">設定を引き継ぐ既存表。</param>
    /// <param name="cancellationToken">読取り、計算、表生成を取り消すトークン。</param>
    /// <param name="scoreCapture">複数表操作内だけで共有する原入力。</param>
    internal async Task<BMSTable> LoadWalkureTableAsync(
        Uri pageUri, BMSTable baseTable = null, CancellationToken cancellationToken = default,
        Lazy<Task<WalkureScoreInput>> scoreCapture = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageUri == null || !pageUri.IsAbsoluteUri || pageUri.Scheme != "bmseeker")
        {
            throw new ArgumentException(Resources.Error_SchemeMustBeBemusic, nameof(pageUri));
        }

        if (pageUri.AbsolutePath is not ("table.estimation" or "table.recommended"))
        {
            throw new ArgumentException(Resources.Error_UnsupportedURI, nameof(pageUri));
        }

        WalkureLamp estimationLamp = WalkureLamp.Easy;
        WalkureScorePolicy policy = WalkureScorePolicy.Standard;
        if (pageUri.AbsolutePath == "table.estimation")
        {
            estimationLamp = QueryValue(pageUri, "type") switch
            {
                "easy" => WalkureLamp.Easy,
                "normal" => WalkureLamp.Normal,
                "hard" => WalkureLamp.Hard,
                "fc" => WalkureLamp.FullCombo,
                _ => throw new ArgumentException(string.Format(Resources.Error_UnsupportedType, QueryValue(pageUri, "type")), nameof(pageUri))
            };
        }
        else
        {
            bool baseline = QueryValue(pageUri, "base") == "failed";
            bool omitFailed = QueryValue(pageUri, "failed") == "noplay";
            if (baseline && omitFailed)
            {
                throw new ArgumentException(Resources.Error_UnsupportedURI, nameof(pageUri));
            }

            policy = baseline ? WalkureScorePolicy.UnplayedAsFailed
                : omitFailed ? WalkureScorePolicy.FailedAsUnplayed : WalkureScorePolicy.Standard;
        }
        WalkureScoreInput input = null;
        if (pageUri.AbsolutePath == "table.recommended")
        {
            input = await (scoreCapture ?? CreateScoreCapture(cancellationToken)).Value.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (input.Status != ScoreTableLoadStatus.Loaded)
            {
                throw new InvalidOperationException(input.Status == ScoreTableLoadStatus.NotConfigured
                    ? Resources.Error_RecommendationScoreNotConfigured
                    : Resources.Error_RecommendationScoreReadFailed + " " + input.FailureMessage);
            }
        }
        // モデル読込み・数学・行構築を UI スレッドから離し、原入力を一操作中に固定する。
        BMSTable table = await Task.Run(() => BuildTable(pageUri, estimationLamp, policy, input, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (baseTable != null)
        {
            if (pageUri.AbsolutePath != "table.recommended"
                && !string.Equals(baseTable.name, baseTable.org_name, StringComparison.Ordinal))
            {
                table.name = baseTable.name;
            }
            table.compat_prefix = baseTable.compat_prefix;
            table.playlist_id = baseTable.playlist_id;
            table.symbol = baseTable.symbol;
            table.ignore_folder_output = baseTable.ignore_folder_output;
            table.is_external_sync = baseTable.is_external_sync;
            table.is_root_folder = baseTable.is_root_folder;
            table.Output_dir = baseTable.Output_dir;
            table.custom_folder_output_base_name = baseTable.custom_folder_output_base_name;
            table.bmt_sort = baseTable.bmt_sort;
            table.is_bmt_output = baseTable.is_bmt_output;
        }
        return table;
    }

    private static string QueryValue(Uri uri, string key)
    {
        string part = uri.Query.TrimStart('?').Split('&')
            .FirstOrDefault(value => value.StartsWith(key + "=", StringComparison.Ordinal));
        return part == null ? null : Uri.UnescapeDataString(part[(key.Length + 1)..]);
    }

    private static BMSTable BuildTable(Uri uri, WalkureLamp estimationLamp, WalkureScorePolicy policy,
        WalkureScoreInput input, CancellationToken cancellationToken)
    {
        WalkureRecommendationModel model = WalkureRecommendationModel.Bundled;
        var table = new BMSTable
        {
            Page_url = uri,
            is_external_sync = true,
            folder_sort_key = LR2SongDBExtended.playlist.CustomFolderSortType.LEVEL,
            folder_sort_ascending = input == null,
            ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder
                & ~LR2SongDBExtended.playlist.CustomFolderType.LevelFolder
        };
        if (input == null)
        {
            table.org_name = estimationLamp switch
            {
                WalkureLamp.Easy => Resources.Insane_estimation_table_easy,
                WalkureLamp.Normal => Resources.Insane_estimation_table_normal,
                WalkureLamp.Hard => Resources.Insane_estimation_table_hard,
                _ => Resources.Insane_estimation_table_fc
            };
            table.org_symbol = estimationLamp switch
            {
                WalkureLamp.Easy => "E★",
                WalkureLamp.Normal => "N★",
                WalkureLamp.Hard => "H★",
                _ => "F★"
            };
            table.entries = [];
            foreach (WalkureModelEntry entry in model.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsCourse)
                {
                    continue;
                }

                foreach ((string membership, string level) in entry.TableLevels.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    table.entries.Add(new BMSTableEntry
                    {
                        md5 = entry.Md5,
                        title = entry.Name,
                        level = entry.StarRatings.Get(estimationLamp),
                        folder = membership == "insane" ? "INSANE ★" + level : "Overjoy ★★" + level
                    });
                }
            }
        }
        else
        {
            ImmutableDictionary<string, WalkureLamp> observations = model.ApplyPolicy(input.Scores, policy);
            WalkurePlayerRating rating = model.Estimate(observations, cancellationToken);
            string policyName = policy switch
            {
                WalkureScorePolicy.UnplayedAsFailed => Resources.Recommended_unplayed_as_failed,
                WalkureScorePolicy.FailedAsUnplayed => Resources.Recommended_failed_as_unplayed,
                _ => Resources.Recommended_standard
            };
            table.org_name = string.Format(Resources.RecommendFormat, policyName, rating.StarRating.ToString("F2", CultureInfo.InvariantCulture));
            table.org_symbol = "R★";
            table.ignore_folder_output |= LR2SongDBExtended.playlist.CustomFolderType.LevelFolder;
            table.Folder_order = ["EASY", "NORMAL", "HARD", "FC"];
            table.entries = [.. model.Recommend(observations, rating.Theta, cancellationToken).Select(item => new BMSTableEntry
            {
                md5 = item.Entry.Md5, title = item.Entry.Name, level = item.Percent,
                folder = item.Lamp == WalkureLamp.FullCombo ? "FC" : item.Lamp.ToString().ToUpperInvariant()
            })];
        }
        table.name = table.org_name;
        table.symbol = table.org_symbol;
        // 外部表と同じ変更検知へ、時刻や利用者設定を含まない生成内容を渡す。
        table.header_sha256 = BMSTable.ComputeSha256Hex(new JObject
        {
            ["name"] = table.org_name,
            ["symbol"] = table.org_symbol,
            ["folder_sort_ascending"] = table.folder_sort_ascending,
            ["folder_order"] = new JArray(table.Folder_order)
        }.ToString(Formatting.None));
        table.data_sha256 = BMSTable.ComputeSha256Hex(new JArray(table.entries.Select(entry => new JObject
        {
            ["md5"] = entry.md5,
            ["title"] = entry.title,
            ["folder"] = entry.folder,
            ["level"] = entry.level
        })).ToString(Formatting.None));
        return table;
    }

    /// <summary>推薦表の反映成功後に、旧新の取得元名の★が解釈可能で異なり、設定が有効なら実力と増減だけを通知します。保存・日時の更新判定には依存しません。</summary>
    internal void NotifyAppliedSkillChange(string previousName, BMSTable appliedTable)
    {
        if (appliedTable?.Page_url?.Scheme != "bmseeker" || appliedTable.Page_url.AbsolutePath != "table.recommended"
            || !playlistSettingsProvider().ShowRecommUpdatedMsg)
        {
            return;
        }

        static double? Skill(string name)
        {
            Match match = Regex.Match(name ?? "", "★(-?\\d+(?:\\.\\d+)?)");
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                ? value : null;
        }
        double? oldSkill = Skill(previousName), newSkill = Skill(appliedTable.org_name);
        if (oldSkill.HasValue && newSkill.HasValue && oldSkill != newSkill)
        {
            notificationOwner.QueueInformation(string.Format(Resources.Recommend_SkillUpdatedMessage,
                newSkill.Value.ToString("F2"), (newSkill.Value - oldSkill.Value).ToString(" (+#0.00); (-#0.00);")),
                Resources.Recommend_SkillUpdatedTitle);
        }
    }
}
