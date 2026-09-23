using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncSignatureBuilderTests
{
    [TestMethod]
    public void Build_DoesNotIncludeRuntimeRootSet()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        };

        string first = Lr2SongDbSyncSignatureBuilder.Build(options);
        string second = Lr2SongDbSyncSignatureBuilder.Build(options);

        Assert.AreEqual(first, second);
        Assert.IsFalse(first.Contains("|roots="));
    }

    [TestMethod]
    public void Build_DoesNotChangeWhenRootSetChanges()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        };

        string first = Lr2SongDbSyncSignatureBuilder.Build(options);
        string second = Lr2SongDbSyncSignatureBuilder.Build(options);

        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public void Build_DoesNotIncludeLr2FolderDiscoveryRootSet()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        };

        string first = Lr2SongDbSyncSignatureBuilder.Build(options);
        string second = Lr2SongDbSyncSignatureBuilder.Build(options);

        Assert.AreEqual(first, second);
        Assert.IsFalse(first.Contains("|lr2folderRoots="));
    }

    [TestMethod]
    public void Build_DoesNotIncludeMutableBuiltinCustomFolderSettings()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        };

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);

        Assert.IsFalse(signature.Contains("lr2CustomFolderMask"));
        Assert.IsFalse(signature.Contains("lr2TitleFlashHours"));
        Assert.IsFalse(signature.Contains("lr2IncludeNewSongFolder"));
    }

    [TestMethod]
    public void Build_IncludesSchemaParserAndGeneratorVersions()
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
        };

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);

        StringAssert.Contains(signature, "|appSchema=");
        StringAssert.Contains(signature, "|chartInfoSchema=");
        StringAssert.Contains(signature, "|chartInfoParser=");
        StringAssert.Contains(signature, "|songFolderGenerator=");
        StringAssert.Contains(signature, "|lr2FolderFileParser=");
        StringAssert.Contains(signature, "|lr2CompatibilityFacts=1");
    }
}
