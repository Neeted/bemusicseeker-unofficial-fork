using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>同じ機能の複数moduleが必要とする入力・観測だけを共用します。</summary>
internal static class PlaylistRecommendedTableTestSupport
{
    /// <remarks>分割先のケースが同じ入力・観測を共用します。モデル・Task・媒体の所有と終端は元の呼出元・fixtureに従います。</remarks>
    internal static PlaylistRecommendedTableOwner CreateOwner(Func<CancellationToken, Task<WalkureScoreInput>>? read = null,
        PlaylistOperationNotificationOwner? notifications = null, bool showSkillUpdates = false)
        => new(read ?? (_ => Task.FromResult(ReadWalkureInput())), notifications ?? new PlaylistOperationNotificationOwner(),
            () => new CustomFolderOutputSettingsSnapshot { ShowRecommUpdatedMsg = showSkillUpdates });

}
