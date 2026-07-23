using System;
using BeMusicSeeker.Views;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class LoadPlaylistURIDialogTests
{
    [TestMethod]
    public void AppendUriInputLine_AppendsToEndWithNewLine()
    {
        string appended = LoadPlaylistURIDialog.AppendUriInputLine("https://example.com/first.json", "  file:///C:/tables/second.json  ");

        Assert.AreEqual("https://example.com/first.json" + Environment.NewLine + "file:///C:/tables/second.json", appended);
        Assert.AreEqual("file:///C:/tables/first.json", LoadPlaylistURIDialog.AppendUriInputLine(string.Empty, "file:///C:/tables/first.json"));
        Assert.AreEqual("existing", LoadPlaylistURIDialog.AppendUriInputLine("existing", " "));
    }
}
