using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2FullGenerationSignatureBuilderTests
{
    [TestMethod]
    public void Build_NormalizesRootOrderDuplicatesAndCase()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            EnableLR2SongDbFullGeneration = true
        };

        string first = Lr2FullGenerationSignatureBuilder.Build(
            options,
            [@"d:\bms\", @"C:\Charts", @"D:\BMS"]);
        string second = Lr2FullGenerationSignatureBuilder.Build(
            options,
            [@"C:\Charts\", @"D:\BMS"]);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void Build_ChangesWhenRootSetChanges()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            EnableLR2SongDbFullGeneration = true
        };

        string first = Lr2FullGenerationSignatureBuilder.Build(options, [@"D:\BMS"]);
        string second = Lr2FullGenerationSignatureBuilder.Build(options, [@"D:\BMS", @"E:\BMS"]);

        Assert.AreNotEqual(first, second);
    }
}
