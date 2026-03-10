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

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_ReturnsMatchedScoreCount()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile matched = CreateFile("cccccccccccccccccccccccccccccccc");
        TestableBmsFile unmatched = CreateFile("dddddddddddddddddddddddddddddddd");
        BMSScore score = new BMSScore
        {
            hash = "cccccccccccccccccccccccccccccccc"
        };

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[] { matched, unmatched }, new BMSScore[] { score });

        Assert.AreEqual(1, matchedScoreCount);
        Assert.AreSame(score, matched.bmsScore);
        Assert.IsNull(unmatched.bmsScore);
    }

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_ReturnsZeroWhenTargetsAreEmpty()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[0], new BMSScore[0]);

        Assert.AreEqual(0, matchedScoreCount);
    }

    [TestMethod]
    public void ApplyKnownScoresToFilesAndCount_UsesHashIndexSnapshot()
    {
        BmsLibraryIrService service = new BmsLibraryIrService();
        TestableBmsFile matched = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        TestableBmsFile unmatched = CreateFile("ffffffffffffffffffffffffffffffff");
        BMSScore score = new BMSScore
        {
            hash = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            perfect = 321,
            great = 123
        };
        Dictionary<string, BMSScore> scoresByHash = new Dictionary<string, BMSScore>(System.StringComparer.OrdinalIgnoreCase)
        {
            [score.hash] = score
        };

        int matchedScoreCount = service.ApplyKnownScoresToFilesAndCount(new BMSFile[] { matched, unmatched }, scoresByHash);

        Assert.AreEqual(1, matchedScoreCount);
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
