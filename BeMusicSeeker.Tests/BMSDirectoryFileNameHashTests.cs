using System.IO;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BMSDirectoryFileNameHashTests
{
    [TestMethod]
    public void LookupHash_DistinguishesPathAwareKeyWhileFileNameHashCollapsesToBaseName()
    {
        uint baseNameHash = BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav");
        uint nestedBaseNameHash = BMSDirectoryFileNameHash.GetFileNameHash(Path.Combine("sound", "bgm1.wav"));
        uint flatLookupHash = BMSDirectoryFileNameHash.GetLookupHash("bgm1.wav");
        uint nestedLookupHash = BMSDirectoryFileNameHash.GetLookupHash(Path.Combine("sound", "bgm1.wav"));

        Assert.AreEqual(baseNameHash, nestedBaseNameHash);
        Assert.AreNotEqual(flatLookupHash, nestedLookupHash);
    }

}
