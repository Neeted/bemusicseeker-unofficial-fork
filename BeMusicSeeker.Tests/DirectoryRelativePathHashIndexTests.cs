using System;
using System.Collections.Generic;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class DirectoryRelativePathHashIndexTests
{
    [TestMethod]
    public void CreateFromScanResult_AndAddDirFromSameScanResult_ProduceEquivalentEntries()
    {
        string chartDir = @"C:\Songs\Relative";
        uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"sound\bgm1.wav");
        uint imageRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"image\logo.png");
        uint movieRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"bga\movie.mpg");
        BmsScanResult scanResult = new BmsScanResult
        {
            ChartDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chartDir },
            AudioRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { audioRelativeHash } }
            },
            ImageRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { imageRelativeHash } }
            },
            MovieRelativePathHashesByChartDirectory = new Dictionary<string, uint[]>(StringComparer.OrdinalIgnoreCase)
            {
                { chartDir, new[] { movieRelativeHash } }
            }
        };

        DirectoryRelativePathHashIndex fromCreate = DirectoryRelativePathHashIndex.CreateFromScanResult(scanResult);
        DirectoryRelativePathHashIndex fromAdd = new DirectoryRelativePathHashIndex();
        fromAdd.AddDir(chartDir, scanResult);

        AssertEntriesEqual(fromCreate.GetEntryOrNull(chartDir), fromAdd.GetEntryOrNull(chartDir));
    }

    [TestMethod]
    public void ReplaceDir_AndRemoveDir_UpdateKeys()
    {
        string oldDir = @"C:\Songs\Old";
        string newDir = @"C:\Songs\New";
        uint audioRelativeHash = BMSDirectoryFileNameHash.GetLookupHash(@"sound\bgm1.wav");
        DirectoryRelativePathHashIndex index = new DirectoryRelativePathHashIndex();
        index.AddDir(oldDir, new[] { audioRelativeHash }, Array.Empty<uint>(), Array.Empty<uint>());

        bool replaced = index.ReplaceDir(oldDir, newDir);

        Assert.IsTrue(replaced);
        Assert.IsNull(index.GetEntryOrNull(oldDir));
        Assert.IsNotNull(index.GetEntryOrNull(newDir));
        CollectionAssert.AreEquivalent(new[] { audioRelativeHash }, index.GetEntryOrNull(newDir).AudioRelativePathHashArray);

        bool removed = index.RemoveDir(newDir);

        Assert.IsTrue(removed);
        Assert.IsNull(index.GetEntryOrNull(newDir));
    }

    private static void AssertEntriesEqual(DirectoryRelativePathHashIndex.Entry expected, DirectoryRelativePathHashIndex.Entry actual)
    {
        Assert.IsNotNull(expected);
        Assert.IsNotNull(actual);
        CollectionAssert.AreEquivalent(expected.AudioRelativePathHashArray, actual.AudioRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.ImageRelativePathHashArray, actual.ImageRelativePathHashArray);
        CollectionAssert.AreEquivalent(expected.MovieRelativePathHashArray, actual.MovieRelativePathHashArray);
    }
}
