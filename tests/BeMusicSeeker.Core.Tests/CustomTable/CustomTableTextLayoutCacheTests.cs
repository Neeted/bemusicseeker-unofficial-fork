using BeMusicSeeker.Views;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class CustomTableTextLayoutCacheTests
{

    /// <summary>描画要求なしは未知率、ヒットと失敗が一件ずつなら半分とすることを確認し、未測定を成功率として記録する誤りを検出します。</summary>
    [TestMethod]
    public void CalculateHitRate_ReturnsMinusOneWhenNoTextWasDrawn()
    {
        Assert.AreEqual(-1d, CustomTableTextLayoutCache.CalculateHitRate(0, 0));
        Assert.AreEqual(0.5d, CustomTableTextLayoutCache.CalculateHitRate(1, 1));
    }

}
