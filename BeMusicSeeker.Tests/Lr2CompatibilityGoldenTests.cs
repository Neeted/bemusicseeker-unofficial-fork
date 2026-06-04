using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2CompatibilityGoldenTests
{
    [TestMethod]
    public void RootParentHashMatchesLr2RootSentinel()
    {
        Assert.AreEqual("e2977170", Lr2SongFolderParentNormalizer.RootParentHash);
        Assert.AreEqual("e2977170", Lr2SongFolderParentNormalizer.ComputeRootHash());
    }

    [TestMethod]
    public void Lr2Crc32MatchesRootTextGoldenValue()
    {
        byte[] bytes = System.Text.Encoding.GetEncoding("shift_jis").GetBytes("ROOT");

        Assert.AreEqual("206114ef", LR2CRC32.Compute(bytes).ToString("x"));
    }

    [DataTestMethod]
    [DataRow(@"D:\BMS\Pack\Song\chart.bms", "f002e300", "a777506c")]
    [DataRow(@"D:\root.bms", "876fd4dd", "57d700a7")]
    [DataRow(@"\\server\share\Pack\chart.bms", "34521cee", "fcdbcd31")]
    [DataRow(@"D:\BMS\あいう\chart.bms", "51aa0c67", "8c132896")]
    public void FolderParentHashesMatchLr2DirectoryGoldenValues(string chartPath, string expectedFolder, string expectedParent)
    {
        bool success = Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(chartPath, out string folder, out string parent);

        Assert.IsTrue(success);
        Assert.AreEqual(expectedFolder, folder);
        Assert.AreEqual(expectedParent, parent);
    }

    [TestMethod]
    public void FolderParentHashRejectsCp932UnsupportedPath()
    {
        bool success = Lr2SongFolderParentNormalizer.TryComputeExpectedHashes(
            @"D:\BMS\emoji_😀\chart.bms",
            out string folder,
            out string parent);

        Assert.IsFalse(success);
        Assert.IsNull(folder);
        Assert.IsNull(parent);
    }

    [TestMethod]
    public void UnsupportedPathClearsFolderParentAndMarksWarning()
    {
        TestResourceInitializer.EnsureJapaneseResources();
        var file = new BMSFile
        {
            path = @"D:\BMS\emoji_😀\chart.bms",
            folder = "f002e300",
            parent = "a777506c"
        };

        bool changed = Lr2SongFolderParentNormalizer.ApplyIfMissingOrInvalid(file);

        Assert.IsTrue(changed);
        Assert.IsNull(file.folder);
        Assert.IsNull(file.parent);
        Assert.IsTrue(file.Warnings.Contains(ChartWarningKind.Lr2PathEncodingUnsupported));
    }
}
