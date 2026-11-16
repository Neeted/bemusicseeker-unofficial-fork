using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistActionTestSupport
{

    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static BMSScore CreateScore(
        string hash,
        ClearType clear,
        RankType rank,
        int rate = 80)
    {
        return new BMSScore
        {
            hash = hash,
            clear = clear,
            rank = rank,
            perfect = 80,
            great = 20,
            totalnotes = 100,
            rate = rate
        };
    }

}
