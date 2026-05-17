using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models.LR2;
using Codeplex.Data;
using Ribbit.Net;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryIrClient : IBmsLibraryIrClient
{
    private const string AggressiveSearchUrlPrefix = "http://www.ribbit.xyz/bms/search/run?search[value]=";

    public string GetPlayerScoresXml(int lr2Id)
    {
        var uri = new Uri("http://www.dream-pro.info/~lavalse/LR2IR/2/getplayerxml.cgi?id=" + lr2Id);
        return AppHttpClient.Shared.GetString(uri, Encoding.GetEncoding("shift_jis"));
    }

    public List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s)
    {
        List<string> hashes = [.. (md5s ?? []).Where(md5 => LR2SongDB.md5HashRegex.IsMatch(md5))];
        dynamic body = new DynamicJson(DynamicJson.JsonType.array);
        for (int i = 0; i < hashes.Count; i++)
        {
            body[i] = hashes[i];
        }
        dynamic response = DynamicJson.Parse(AppHttpClient.Shared.PostString(rankingInfoUrl, body.ToString(), "application/json;charset=UTF-8", Encoding.UTF8, new Dictionary<string, string>
        {
            { "Accept", "application/json" }
        }));
        return [.. (from ri in ((object[])response).Select(delegate (dynamic item)
                {
                    try
                    {
                        return new BMSLibrary.IRDataCacheInfo(item);
                    }
                    catch
                    {
                        return (BMSLibrary.IRDataCacheInfo)null;
                    }
                })
                where ri != null
                select ri)];
    }

    public void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath)
    {
        var address = new Uri(rankingDataUrl, "./" + md5 + ".xml");
        AppHttpClient.Shared.DownloadFile(address, destinationPath);
    }

    public BMSLibrary.IRSongInfo GetSongInfo(Uri songInfoUrl, string md5OrLr2BmsId, bool searchAggressively)
    {
        string searchUrl = string.Empty;
        string searchUrlSabun = string.Empty;
        BMSLibrary.IRSongInfo info = null;
        var task = System.Threading.Tasks.Task.Run(delegate
        {
            dynamic json = DynamicJson.Parse(AppHttpClient.Shared.GetString(new Uri(songInfoUrl, "./" + md5OrLr2BmsId), Encoding.UTF8));
            info = new BMSLibrary.IRSongInfo(json);
        });
        if (searchAggressively)
        {
            System.Threading.Tasks.Task.Run(delegate
            {
                dynamic json = DynamicJson.Parse(AppHttpClient.Shared.GetString(new Uri(AggressiveSearchUrlPrefix + md5OrLr2BmsId), Encoding.UTF8));
                searchUrl = json.data[0][10].ToString().Trim();
                searchUrlSabun = json.data[0][11].ToString().Trim();
            }).Wait();
        }
        task.Wait();
        if (searchAggressively)
        {
            if (!string.IsNullOrWhiteSpace(searchUrl))
            {
                info.url = (info.url + " " + searchUrl).Trim();
            }
            if (!string.IsNullOrWhiteSpace(searchUrlSabun))
            {
                info.url_diff = (info.url_diff + " " + searchUrlSabun).Trim();
            }
        }
        return info;
    }
}
