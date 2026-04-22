using System;
using System.IO;
using System.Linq;
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

    [TestMethod]
    public void CreateFromHashedDirectories_AndAddDirHashed_ProduceSameBasenameOnlyIndex()
    {
        string dirA = Path.Combine("C:\\Library", "A");
        string dirB = Path.Combine("C:\\Library", "B");
        uint[] hashesA = new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm1.wav"),
            BMSDirectoryFileNameHash.GetFileNameHash("logo.bmp")
        };
        uint[] hashesB = new[]
        {
            BMSDirectoryFileNameHash.GetFileNameHash("bgm2.wav")
        };

        BMSDirectoryFileNameHash fromCreate = BMSDirectoryFileNameHash.CreateFromHashedDirectories(
            new[] { dirA, dirB },
            new System.Collections.Generic.Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { dirA, hashesA },
                { dirB, hashesB }
            });
        BMSDirectoryFileNameHash fromAdd = new BMSDirectoryFileNameHash();
        fromAdd.AddDirHashed(dirA, hashesA);
        fromAdd.AddDirHashed(dirB, hashesB);

        CollectionAssert.AreEquivalent(hashesA, fromCreate.TryGetCachedFileNameHashArray(dirA));
        CollectionAssert.AreEquivalent(hashesB, fromCreate.TryGetCachedFileNameHashArray(dirB));
        CollectionAssert.AreEquivalent(hashesA, fromAdd.TryGetCachedFileNameHashArray(dirA));
        CollectionAssert.AreEquivalent(hashesB, fromAdd.TryGetCachedFileNameHashArray(dirB));
    }
}

