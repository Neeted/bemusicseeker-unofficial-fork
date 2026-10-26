using System.Globalization;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2SongDbSyncSignatureBuilderTests
{

    /// <summary>
    /// 保存署名にアプリ・譜面情報・解析・フォルダ生成・互換性の版を含め、異なる保存契約を同じ同期済み状態として再利用する誤りを検出します。
    /// </summary>
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
        StringAssert.Contains(signature, "|chartInfoParser=" + BmsLibraryDbGateway.CurrentChartInfoParserVersion.ToString(CultureInfo.InvariantCulture) + "|");
        StringAssert.Contains(signature, "|songFolderGenerator=");
        StringAssert.Contains(signature, "|lr2FolderFileParser=");
        StringAssert.Contains(signature, "|lr2CompatibilityFacts=1");
    }
}
