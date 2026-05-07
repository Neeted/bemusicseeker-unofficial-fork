using System;
using System.Linq;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LoadPlaylistURIDialogTests
{
    [TestMethod]
    public void ParsePlaylistUriInput_ReturnsValidUrisInInputOrderAndIgnoresBlankLines()
    {
        LoadPlaylistURIDialog.PlaylistUriInputParseResult result = LoadPlaylistURIDialog.ParsePlaylistUriInput("  https://example.com/first.json  \r\n\r\nfile:///C:/tables/second.json\nhttps://example.com/third.json");

        Assert.IsTrue(result.HasValidUris);
        Assert.IsFalse(result.HasInvalidLines);
        Assert.AreEqual(3, result.ValidUris.Count);
        Assert.AreEqual("https://example.com/first.json", result.ValidUris[0].ToString());
        Assert.AreEqual("file:///C:/tables/second.json", result.ValidUris[1].ToString());
        Assert.AreEqual("https://example.com/third.json", result.ValidUris[2].ToString());
    }

    [TestMethod]
    public void ParsePlaylistUriInput_ReturnsValidUrisAndInvalidLinesSeparately()
    {
        LoadPlaylistURIDialog.PlaylistUriInputParseResult result = LoadPlaylistURIDialog.ParsePlaylistUriInput("https://example.com/valid.json\nnot-a-uri\n  also not uri  ");

        Assert.IsTrue(result.HasValidUris);
        Assert.IsTrue(result.HasInvalidLines);
        Assert.AreEqual(1, result.ValidUris.Count);
        Assert.AreEqual(new Uri("https://example.com/valid.json"), result.ValidUris.Single());
        CollectionAssert.AreEqual(new[] { "not-a-uri", "also not uri" }, result.InvalidLines.ToList());
    }

    [TestMethod]
    public void ParsePlaylistUriInput_AllInvalidOrBlankHasNoValidUris()
    {
        LoadPlaylistURIDialog.PlaylistUriInputParseResult blank = LoadPlaylistURIDialog.ParsePlaylistUriInput(" \r\n\t ");
        LoadPlaylistURIDialog.PlaylistUriInputParseResult invalid = LoadPlaylistURIDialog.ParsePlaylistUriInput("not-a-uri");

        Assert.IsFalse(blank.HasValidUris);
        Assert.IsFalse(blank.HasInvalidLines);
        Assert.IsFalse(invalid.HasValidUris);
        Assert.IsTrue(invalid.HasInvalidLines);
    }

    [TestMethod]
    public void AppendUriInputLine_AppendsToEndWithNewLine()
    {
        string appended = LoadPlaylistURIDialog.AppendUriInputLine("https://example.com/first.json", "  file:///C:/tables/second.json  ");

        Assert.AreEqual("https://example.com/first.json" + Environment.NewLine + "file:///C:/tables/second.json", appended);
        Assert.AreEqual("file:///C:/tables/first.json", LoadPlaylistURIDialog.AppendUriInputLine(string.Empty, "file:///C:/tables/first.json"));
        Assert.AreEqual("existing", LoadPlaylistURIDialog.AppendUriInputLine("existing", " "));
    }
}
