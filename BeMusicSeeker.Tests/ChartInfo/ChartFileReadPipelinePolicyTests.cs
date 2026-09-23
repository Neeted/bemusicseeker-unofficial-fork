using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartFileReadPipelinePolicyTests
{
    [TestMethod]
    public void ResolveReaderDegree_UsesSingleReaderForSmallOrLowCpuInputs()
    {
        Assert.AreEqual(1, ChartFileReadPipelinePolicy.ResolveReaderDegree(processorCount: 16, targetCount: 1));
        Assert.AreEqual(1, ChartFileReadPipelinePolicy.ResolveReaderDegree(processorCount: 4, targetCount: 100));
    }

    [TestMethod]
    public void ResolveReaderDegree_AllowsTwoReadersForLargeInputs()
    {
        Assert.AreEqual(2, ChartFileReadPipelinePolicy.ResolveReaderDegree(processorCount: 8, targetCount: 100));
        Assert.AreEqual(1, ChartFileReadPipelinePolicy.ResolveReaderDegree(processorCount: 8, targetCount: 100, maxReaderDegree: 1));
    }

    [TestMethod]
    public void ResolveReadQueueCapacity_ScalesWithWorkerAndReaderDegree()
    {
        Assert.AreEqual(12, ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(workerDegree: 3, readerDegree: 2));
        Assert.AreEqual(2, ChartFileReadPipelinePolicy.ResolveReadQueueCapacity(workerDegree: 1, readerDegree: 1));
    }
}
