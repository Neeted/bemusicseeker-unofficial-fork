using System.IO;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartResourceKeyHashTests
{
    /// <summary>
    /// 保証：同名ファイルの名前ハッシュを共有しても階層付き照合キーは区別し、別フォルダのリソースを同一候補へ混同しない。
    /// </summary>
    [TestMethod]
    public void LookupHash_DistinguishesPathAwareKeyWhileFileNameHashCollapsesToBaseName()
    {
        uint baseNameHash = ChartResourceKeyHash.GetFileNameHash("bgm1.wav");
        uint nestedBaseNameHash = ChartResourceKeyHash.GetFileNameHash(Path.Combine("sound", "bgm1.wav"));
        uint flatLookupHash = ChartResourceKeyHash.GetLookupHash("bgm1.wav");
        uint nestedLookupHash = ChartResourceKeyHash.GetLookupHash(Path.Combine("sound", "bgm1.wav"));

        Assert.AreEqual(baseNameHash, nestedBaseNameHash);
        Assert.AreNotEqual(flatLookupHash, nestedLookupHash);
    }

}
