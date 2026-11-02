using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryInitializationFileScanTestSupport;
namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class BmsLibraryInitializationFileScanTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>本番既定の10,000行commitと2,048行inline幅で10,001実譜面を保存し、末尾を欠かさず全行を確定します。</summary>
    [TestMethod, TestCategory("Suite.scan-commit")]
    public void FileScan_ProductionDefaultsPersistGeneratedLibrary()
        => AssertFileScanChunkCommits(databaseScope, 10_001, 10_000, new BmsLibraryInitializationService(),
            elapsed => Console.WriteLine(FormattableString.Invariant($"route=scan_commit charts=10001 bodyElapsedMs={elapsed.TotalMilliseconds:F3}")));

}
