using BeMusicSeeker.Models;
using static BeMusicSeeker.TestSupport.Components.Lr2SongDbSyncServiceTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncServiceTests
{
    /// <summary>このケースのモデルと接続で共有するDBの所有者です。</summary>
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>本番既定の1,000行chunkを跨ぐ10,001行を実更新し、11commit・末尾・利用者列と順序回収を保ちます。</summary>
    [TestMethod, TestCategory("Suite.lr2-sync")]
    public void ProductionDefaults_1000RowChunksPersist10001Rows()
        => AssertSongChunkCommits(databaseScope, 10_001, 1000, null,
            elapsed => Console.WriteLine(FormattableString.Invariant($"route=lr2_sync rows=10001 bodyElapsedMs={elapsed.TotalMilliseconds:F3}")));

}
