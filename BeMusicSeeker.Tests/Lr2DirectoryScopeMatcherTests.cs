using System.IO;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class Lr2DirectoryScopeMatcherTests
{
    [TestMethod]
    public void ContainsDirectory_MatchesScopeAndDescendantsOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "BMS");
        string scopeDirectory = Path.Combine(root, "Output");
        Lr2DirectoryScopeMatcher matcher = Lr2DirectoryScopeMatcher.Create([scopeDirectory]);

        Assert.IsTrue(matcher.ContainsDirectory(scopeDirectory));
        Assert.IsTrue(matcher.ContainsDirectory(Path.Combine(scopeDirectory, "Playlist")));
        Assert.IsFalse(matcher.ContainsDirectory(Path.Combine(root, "OutputOther")));
    }

    [TestMethod]
    public void ContainsFilePath_UsesParentDirectoryScope()
    {
        string root = Path.Combine(Path.GetTempPath(), "BMS");
        string scopeDirectory = Path.Combine(root, "Output");
        Lr2DirectoryScopeMatcher matcher = Lr2DirectoryScopeMatcher.Create([scopeDirectory]);

        Assert.IsTrue(matcher.ContainsFilePath(Path.Combine(scopeDirectory, "Playlist", "item.lr2folder")));
        Assert.IsFalse(matcher.ContainsFilePath(Path.Combine(root, "OutputOther", "item.lr2folder")));
    }

    [TestMethod]
    public void ContainsDirectory_MatchesDriveRootDescendant()
    {
        string root = Path.GetPathRoot(Path.GetTempPath());
        Assert.IsFalse(string.IsNullOrWhiteSpace(root));
        Lr2DirectoryScopeMatcher matcher = Lr2DirectoryScopeMatcher.Create([root]);

        Assert.IsTrue(matcher.ContainsDirectory(Path.Combine(root, "BMS")));
    }
}
