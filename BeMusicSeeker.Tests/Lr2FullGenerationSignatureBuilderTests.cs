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

    [TestMethod]
    public void Build_ChangesWhenLr2FolderDiscoveryRootSetChanges()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            EnableLR2SongDbFullGeneration = true
        };

        string first = Lr2FullGenerationSignatureBuilder.Build(
            options,
            [@"D:\BMS"],
            [@"D:\BMS", @"D:\LR2files\CustomFolder"]);
        string second = Lr2FullGenerationSignatureBuilder.Build(
            options,
            [@"D:\BMS"],
            [@"D:\BMS", @"E:\LR2files\CustomFolder"]);

        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public void Build_IncludesSchemaParserAndGeneratorVersions()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            EnableLR2SongDbFullGeneration = true
        };

        string signature = Lr2FullGenerationSignatureBuilder.Build(options, [@"D:\BMS"]);

        StringAssert.Contains(signature, "|appSchema=");
        StringAssert.Contains(signature, "|chartInfoSchema=");
        StringAssert.Contains(signature, "|chartInfoParser=");
        StringAssert.Contains(signature, "|songFolderGenerator=");
        StringAssert.Contains(signature, "|lr2FolderFileParser=");
        StringAssert.Contains(signature, "|lr2CompatibilityFacts=");
    }
}
