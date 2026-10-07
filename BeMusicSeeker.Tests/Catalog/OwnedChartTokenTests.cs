using System.Collections.Generic;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class OwnedChartTokenTests
{
    [TestMethod]
    public void CurrentValueApplicationKeepsTokenAndCapturedValueWhileReplacingDigestIndex()
    {
        ChartFile parsed = Parse("C:\\Songs\\chart.bms");
        Assert.IsNull(parsed.Token);
        var suggestions = new List<string> { @"C:\Target" };
        var warnings = new List<ChartWarning> { ChartWarning.Create(ChartWarningKind.DuplicateChart, "captured warning") };
        parsed = parsed with { InstallDestinationSuggestions = suggestions, Warnings = warnings };
        suggestions.Clear();
        warnings.Clear();
        Assert.AreEqual(1, parsed.InstallDestinationSuggestions.Count);
        Assert.AreEqual(1, parsed.Warnings.Count);
        var state = OwnedChartCollectionState.FromCharts([parsed]);
        LibraryChartRef captured = state.CreateLibraryChartRefsForPaths([parsed.Path]).Single();
        ChartFile original = state.ResolveCurrentChart(captured);
        IReadOnlyDictionary<string, OwnedChartToken> originalPaths = state.CapturePathMembershipIndex();
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        ChartFile updated = original with { Title = "Current", Md5 = new string('a', 32), Sha256 = new string('b', 64) };

        Assert.IsTrue(state.ApplyCurrentChartValue(updated));

        Assert.AreSame(originalPaths, state.CapturePathMembershipIndex());
        ChartFile current = state.ResolveCurrentChart(captured);
        Assert.AreSame(original.Token, current.Token);
        Assert.AreEqual("Initial", original.Title);
        Assert.AreEqual("Current", current.Title);
        Assert.AreEqual(parsed.Md5, captured.Md5);
        LibraryChartRef currentReference = index.ResolveCanonicalCharts([captured]).CanonicalCharts.Single();
        Assert.AreSame(captured.Token, currentReference.Token);
        Assert.AreEqual(updated.Md5, currentReference.Md5);
        Assert.AreEqual(updated.Sha256, currentReference.Sha256);
        Assert.AreEqual(current.Md5, state.CreateOwnedHashIndexSnapshot().Md5Hashes.Single());
    }

    [TestMethod]
    public void InternalRelocationKeepsTokenButSamePathAndHashReplacementRetiresReference()
    {
        ChartFile parsed = Parse("C:\\Songs\\chart.bms");
        var state = OwnedChartCollectionState.FromCharts([parsed]);
        ChartFile original = state.ResolveCurrentChart(LibraryChartRef.FromChartFile(parsed));
        var captured = LibraryChartRef.FromChartFile(original);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        state.ApplyPathChanges([new LibraryChartPathChange
        {
            Chart = original,
            OldPath = original.Path,
            NewPath = "C:\\Songs\\moved.bms"
        }]);
        ChartFile moved = state.ResolveCurrentChart(captured);
        Assert.AreSame(original.Token, moved.Token);
        Assert.AreEqual("C:\\Songs\\chart.bms", original.Path);
        Assert.AreEqual("C:\\Songs\\moved.bms", moved.Path);
        Assert.AreEqual(moved.Path, index.ResolveCanonicalCharts([captured]).CanonicalCharts.Single().Path);
        var fixedReference = LibraryChartRef.FromChartFile(moved);

        state.UpsertCharts([moved]);

        ChartFile replacement = state.ResolveCurrentChart(LibraryChartRef.FromPath(moved.Kind, moved.Path, moved.Md5, moved.Sha256));
        Assert.AreEqual(moved.Path, replacement.Path);
        Assert.AreEqual(moved.Md5, replacement.Md5);
        Assert.AreNotSame(moved.Token, replacement.Token);
        Assert.IsNull(state.ResolveCurrentChart(fixedReference));
        Assert.IsFalse(state.ApplyCurrentChartValue(moved));
        ChartFile loaded = ChartSongStorageMapping.FromBmsRow(ChartSongStorageMapping.ToBmsRow(replacement), replacement.Sha256);
        var reload = OwnedChartCollectionState.FromCharts([loaded]);
        Assert.AreNotSame(replacement.Token, reload.ResolveCurrentChart(LibraryChartRef.FromPath(replacement.Kind, replacement.Path, replacement.Md5, replacement.Sha256)).Token);
    }

    /// <summary>二つのcanonical rootが退役tokenを別項目へ読替えず、tokenなしDB完全一致と物理比較を分けます。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void CanonicalRoots_RejectRetiredTokenButKeepTokenlessExactPath(bool useIndex, bool isBmson)
    {
        ChartFileKind kind = isBmson ? ChartFileKind.Bmson : ChartFileKind.Bms;
        ChartFile parsed = ChartTestValues.Empty(kind) with { Path = @"C:\Songs\chart.bms", Md5 = new string('a', 32) };
        var state = OwnedChartCollectionState.FromCharts([parsed]);
        ChartFile original = state.ResolveCurrentChart(LibraryChartRef.FromChartFile(parsed));
        var captured = LibraryChartRef.FromChartFile(original);
        LibraryChartRefIndexSnapshot index = state.CreateLibraryChartRefIndexSnapshot();
        state.UpsertCharts([original]);
        var exact = LibraryChartRef.FromPath(kind, original.Path, original.Md5, original.Sha256);
        ChartFile current = state.ResolveCurrentChart(exact);
        Assert.AreNotSame(original.Token, current.Token);
        Assert.IsFalse(state.ContainsKnownChart(original));
        Assert.IsTrue(state.ContainsKnownChart(current));
        if (useIndex)
        {
            Assert.AreEqual(0, index.ResolveCanonicalCharts([captured]).CanonicalCharts.Count);
            Assert.AreSame(current.Token, index.ResolveCanonicalCharts([exact]).CanonicalCharts.Single().Token);
            Assert.AreEqual(0, index.ResolveCanonicalCharts([LibraryChartRef.FromPath(kind, original.Path.ToUpperInvariant(), null, null)]).CanonicalCharts.Count);
        }
        else
        {
            Assert.IsNull(state.ResolveCurrentChart(captured));
            Assert.AreSame(current, state.ResolveCurrentChart(exact));
            Assert.IsNull(state.ResolveCurrentChart(LibraryChartRef.FromPath(kind, original.Path.ToUpperInvariant(), null, null)));
        }
        Assert.AreEqual(parsed.Md5, captured.Md5);
        Assert.IsTrue(ChartFileIdentity.IsSameChartTarget(current, parsed with { Path = original.Path.ToUpperInvariant() }));
    }

    private static ChartFile Parse(string path)
    {
        ChartFileSnapshot snapshot = ChartFileContentReader.CreateSnapshot(new ChartFileReadBuffer(
            path, Encoding.ASCII.GetBytes("#TITLE Initial\n#ARTIST Author\n"), default));
        return BmsChartFileParser.ParseSnapshot(snapshot);
    }
}
