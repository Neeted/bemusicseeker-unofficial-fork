using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ribbit.Net;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryIrClient : IBmsLibraryIrClient
{
    public string GetPlayerScoresXml(int lr2Id)
    {
        var uri = new Uri("http://www.dream-pro.info/~lavalse/LR2IR/2/getplayerxml.cgi?id=" + lr2Id);
        return AppHttpClient.Shared.GetString(uri, Encoding.GetEncoding("shift_jis"));
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

    internal static List<BMSLibrary.IRDataCacheInfo> ParseRankingInfoResponse(string responseJson)
    {
        JArray response = ParseJsonArray(responseJson);
        return [.. response
            .OfType<JObject>()
            .Select(item =>
            {
                try
                {
                    return new BMSLibrary.IRDataCacheInfo(item);
                }
                catch
                {
                    return null;
                }
            })
            .Where(item => item != null)];
    }

    internal static JArray ParseJsonArray(string json)
    {
        using var reader = new JsonTextReader(new System.IO.StringReader(json ?? string.Empty))
        {
            DateParseHandling = DateParseHandling.None
        };
        JToken token = JToken.ReadFrom(reader);
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
