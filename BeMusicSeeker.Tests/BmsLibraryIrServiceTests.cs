using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryIrServiceTests
{
    [TestMethod]
    public void ApplyKnownScoresToFiles_AssignsMatchingScoreOnly()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile matched = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        TestableBmsFile unmatched = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        BMSScore score = new BMSScore
        {
            hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            perfect = 500,
            great = 234
        };

        service.ApplyKnownScoresToFiles(new BMSFile[] { matched, unmatched }, new BMSScore[] { score });

        Assert.AreSame(score, matched.bmsScore);
        Assert.IsNull(unmatched.bmsScore);
    }

    private static TestableBmsFile CreateFile(string hash)
    {
        TestableBmsFile file = new TestableBmsFile();
        file.SetHash(hash);
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        public void SetHash(string value)
        {
            hash = value;
        }
    }
}
