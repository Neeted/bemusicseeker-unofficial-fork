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
}
