using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.TestSupport.Components.TemporaryCopyFilesTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class TemporaryCopyFilesTests
{
    /// <summary>既定並列度で256件を転送し、量を増やしても所有fileの追跡と回収、入力保全を維持します。</summary>
    [TestMethod, TestCategory("Performance")]
    public Task CopyTracking_RepeatedParallelOwnership()
        => AssertParallelCopyOwnership(256, observeWorkers: false);

}
