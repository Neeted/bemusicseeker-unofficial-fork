using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncSignatureBuilderTests
{

    /// <summary>
    /// 保存署名の固定prefix・要素・順序・版と通常modeの0・1を全文で照合し、永続署名の意味を承認なく変える誤りを検出します。
    /// </summary>
    [DataTestMethod]
    [DataRow(false, "lr2_song_db_sync_v2|appSchema=1|chartInfoSchema=5|chartInfoParser=21|songFolderGenerator=1|lr2FolderFileParser=1|lr2CompatibilityFacts=1|operationModeLR2DB=0")]
    [DataRow(true, "lr2_song_db_sync_v2|appSchema=1|chartInfoSchema=5|chartInfoParser=21|songFolderGenerator=1|lr2FolderFileParser=1|lr2CompatibilityFacts=1|operationModeLR2DB=1")]
    public void Build_PreservesPersistedSignatureForEachOperationMode(bool operationModeLr2Db, string expectedSignature)
    {
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = operationModeLr2Db,
        };

        string signature = Lr2SongDbSyncSignatureBuilder.Build(options);

        Assert.AreEqual(expectedSignature, signature);
    }
}
