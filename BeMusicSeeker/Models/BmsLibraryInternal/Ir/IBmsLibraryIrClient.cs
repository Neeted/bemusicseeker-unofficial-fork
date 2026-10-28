using System.Threading;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal interface IBmsLibraryIrClient
{
    /// <summary>本文完了まで期限と終了キャンセルを適用してプレイヤースコアを取得します。</summary>
    string GetPlayerScoresXml(int lr2Id, CancellationToken cancellationToken = default);
}
