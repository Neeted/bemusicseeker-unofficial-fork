using System;
using System.Globalization;
using System.Text;
using System.Threading;
using Ribbit.Net;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class BmsLibraryIrClient : IBmsLibraryIrClient
{
    /// <summary>プレイヤーIDをInvariantでURLへ渡し、本文まで30秒の期限を持つ取得結果を返します。</summary>
    public string GetPlayerScoresXml(int lr2Id, CancellationToken cancellationToken = default)
    {
        var uri = new Uri("http://www.dream-pro.info/~lavalse/LR2IR/2/getplayerxml.cgi?id=" + lr2Id.ToString(CultureInfo.InvariantCulture));
        return AppHttpClient.Shared.GetStringAsync(uri, Encoding.GetEncoding("shift_jis"), cancellationToken)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }
}
