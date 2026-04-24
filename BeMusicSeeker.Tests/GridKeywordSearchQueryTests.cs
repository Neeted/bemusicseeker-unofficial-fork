using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class GridKeywordSearchQueryTests
{
    [TestMethod]
    public void MatchesBmsFile_GlobalKeywordSearchesMetadataPathPlaylistAndHashes()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("genrex").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("abcdef").MatchesBmsFile(file));
        Assert.IsTrue(GridKeywordSearchQuery.Parse("1234567890").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_UsesAndForMultipleTokens()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("alpha artistx abcdef").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("alpha missing").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_FieldQueryLimitsSearchTarget()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsTrue(GridKeywordSearchQuery.Parse("title:alpha artist:artistx md5:abcdef sha256:123456").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:artistx").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("memo:alpha").MatchesBmsFile(file));
    }

    [TestMethod]
    public void MatchesBmsFile_UnknownOrEmptyFieldQueryDoesNotMatch()
    {
        TestableBmsFile file = CreateFile();

        Assert.IsFalse(GridKeywordSearchQuery.Parse("unknown:alpha").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse("title:").MatchesBmsFile(file));
        Assert.IsFalse(GridKeywordSearchQuery.Parse(":alpha").MatchesBmsFile(file));
    }

    private static TestableBmsFile CreateFile()
    {
        TestableBmsFile file = new TestableBmsFile();
        file.ApplySnapshot();
        return file;
    }

    private sealed class TestableBmsFile : BMSFile
    {
        internal void ApplySnapshot()
        {
            Title = "Alpha Title";
            Artist = "ArtistX";
            genre = "GenreX";
            tag = "TagX";
            path = @"C:\Songs\Alpha\chart.bms";
            hash = "abcdefabcdefabcdefabcdefabcdefab";
            sha256 = "1234567890123456789012345678901234567890123456789012345678901234";
        }
    }
}
