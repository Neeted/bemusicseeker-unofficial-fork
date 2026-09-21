using System.IO;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ChartResourceKeyHashTests
{
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
