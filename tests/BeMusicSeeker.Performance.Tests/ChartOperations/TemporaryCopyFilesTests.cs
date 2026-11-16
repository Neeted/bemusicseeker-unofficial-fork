using static BeMusicSeeker.TestSupport.Components.TemporaryCopyFilesTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TemporaryCopyFilesTests
{
    /// <summary>既定並列度で256件を転送し、量を増やしても所有fileの追跡と回収、入力保全を維持します。</summary>
    [TestMethod, TestCategory("Suite.copy-tracking")]
    public Task CopyTracking_RepeatedParallelOwnership()
        => AssertParallelCopyOwnership(256, observeWorkers: false,
            elapsed => Console.WriteLine(FormattableString.Invariant($"route=copy_tracking files=256 bodyElapsedMs={elapsed.TotalMilliseconds:F3}")));

}
