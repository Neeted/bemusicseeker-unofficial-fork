using System;
using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IBmsLibraryIrClient
{
    string GetPlayerScoresXml(int lr2Id);

    List<BMSLibrary.IRDataCacheInfo> GetRankingInfo(Uri rankingInfoUrl, IEnumerable<string> md5s);

    void DownloadRankingData(Uri rankingDataUrl, string md5, string destinationPath);

    BMSLibrary.IRSongInfo GetSongInfo(Uri songInfoUrl, string md5OrLr2BmsId, bool searchAggressively);
}
