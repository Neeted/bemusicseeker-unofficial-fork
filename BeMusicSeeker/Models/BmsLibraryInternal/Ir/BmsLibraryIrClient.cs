using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ribbit.Logging;
using Ribbit.Net;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryIrClient : IBmsLibraryIrClient
{
    /// <summary>既存のバックグラウンド呼出元へ、本文まで30秒の期限を持つ取得結果を返します。</summary>
    public string GetPlayerScoresXml(int lr2Id, CancellationToken cancellationToken = default)
    {
        var uri = new Uri("http://www.dream-pro.info/~lavalse/LR2IR/2/getplayerxml.cgi?id=" + lr2Id);
        return AppHttpClient.Shared.GetStringAsync(uri, Encoding.GetEncoding("shift_jis"), cancellationToken)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    public List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s)
    {
        string responseJson = AppHttpClient.Shared.PostString(rankingInfoUrl, BuildRankingInfoRequestJson(md5s), "application/json;charset=UTF-8", Encoding.UTF8, new Dictionary<string, string>
        {
            { "Accept", "application/json" }
        });
        return ParseRankingInfoResponse(responseJson);
    }

    internal static string BuildRankingInfoRequestJson(IEnumerable<string> md5s)
    {
        var body = new JArray();
        foreach (string md5 in md5s ?? [])
        {
            if (LR2SongDB.md5HashRegex.IsMatch(md5))
            {
                body.Add(md5);
            }
        }
        return body.ToString(Formatting.None);
    }

    /// <summary>
    /// ランキング応答の有効行を保持し、既知の入力不正行だけを省略します。
    /// 応答は部分一覧として扱い、省略件数と最大5件の理由を一度だけ診断します。
    /// 診断先の失敗は返す一覧を変更しません。
    /// </summary>
    internal static List<BMSLibrary.IRDataCacheInfo> ParseRankingInfoResponse(string responseJson, Action<string> reportSkippedRows = null)
    {
        JArray response = ParseJsonArray(responseJson);
        List<BMSLibrary.IRDataCacheInfo> result = [];
        List<string> reasons = [];
        int skippedCount = 0;
        for (int index = 0; index < response.Count; index++)
        {
            string reason;
            if (response[index] is not JObject item)
            {
                reason = "not_object";
            }
            else
            {
                try
                {
                    result.Add(new BMSLibrary.IRDataCacheInfo(item));
                    continue;
                }
                catch (ArgumentException ex) when (ex.ParamName is "json" or "json.md5")
                {
                    reason = ex.ParamName == "json.md5" ? "invalid_md5" : "missing_required_field";
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    reason = ex.GetType().Name;
                }
            }
            skippedCount++;
            if (reasons.Count < 5)
            {
                reasons.Add(index + ":" + reason);
            }
        }
        if (skippedCount > 0)
        {
            try
            {
                string message = "lr2_ranking skipped_rows=" + skippedCount
                    + " accepted_rows=" + result.Count
                    + " reasons=" + string.Join("|", reasons);
                if (reportSkippedRows == null)
                {
                    NLogWrapper.FileLogger.Warn(message);
                }
                else
                {
                    reportSkippedRows(message);
                }
            }
            catch (Exception)
            {
                // 行の入力判定は完了済みです。診断先の失敗で部分一覧を失敗へ変えません。
            }
        }
        return result;
    }

    internal static JArray ParseJsonArray(string json)
    {
        using var reader = new JsonTextReader(new System.IO.StringReader(json ?? string.Empty))
        {
            DateParseHandling = DateParseHandling.None
        };
        var token = JToken.ReadFrom(reader);
        if (reader.Read())
        {
            throw new JsonReaderException("JSON document contains trailing content.");
        }
        return token as JArray ?? throw new FormatException("Ranking info JSON must be an array.");
    }

    public void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath)
    {
        var address = new Uri(rankingDataUrl, "./" + md5 + ".xml");
        AppHttpClient.Shared.DownloadFile(address, destinationPath);
    }
}
