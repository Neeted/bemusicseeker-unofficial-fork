using System.Collections.Generic;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallDestinationStateOwnerTests
{
    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_UsesExactPathWhenAliasHashMatches()
    {
        string path = "C:\\Library\\Chart.bms";
        ChartFile first = Parse(path);
        ChartFile alias = Parse(path.ToLowerInvariant()) with { Md5 = new string('b', 32) };
        CatalogOwnedCollectionOwner collection = CreateCollection(first, alias);
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile detached = WithDestination(first with { Md5 = alias.Md5 }, "C:\\Install\\Chart", "Overlay title");

        ChartFile reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]).Single();

        ChartFile current = collection.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(first));
        Assert.AreSame(current.Token, reattached.Token);
        Assert.AreEqual(first.Md5, reattached.Md5);
        Assert.AreEqual("C:\\Install\\Chart", reattached.InstallDestination);
        Assert.AreEqual("Overlay title", reattached.InstallDestinationTitle);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_RejectsCrossKindLeakage()
    {
        ChartFile bms = Parse("C:\\Library\\Chart.bms");
        CatalogOwnedCollectionOwner collection = CreateCollection(bms);
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile wrongKind = WithDestination(bms with { Kind = ChartFileKind.Bmson }, "C:\\Install\\Other", "");

        Assert.AreEqual(0, owner.ReattachFileScanResidualInstallDestinationCharts([wrongKind]).Count);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_ReattachesBmsonToken()
    {
        ChartFile parsed = Parse("C:\\Library\\Chart.bmson") with { Kind = ChartFileKind.Bmson };
        CatalogOwnedCollectionOwner collection = CreateCollection(parsed);
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile detached = WithDestination(parsed, "C:\\Install\\Bmson", "");

        ChartFile reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]).Single();

        Assert.AreSame(collection.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(parsed)).Token, reattached.Token);
        Assert.AreEqual("C:\\Install\\Bmson", reattached.InstallDestination);
    }

    [TestMethod]
    public void OverlayRuntimeStates_UsesExactPathAndCaseInsensitiveHashButRejectsReplacementToken()
    {
        ChartFile parsed = Parse("C:\\Library\\Chart.bms");
        CatalogOwnedCollectionOwner collection = CreateCollection(parsed);
        ChartFile current = collection.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(parsed));
        var owner = new InstallDestinationStateOwner(collection, () => []);
        var mutation = new InstallDestinationRuntimeStateMutation();
        mutation.AppliedCharts.Add(WithDestination(current, "C:\\Install\\Exact", ""));
        owner.Apply(mutation);

        ChartFile exact = current with { Md5 = current.Md5.ToUpperInvariant() };
        ChartFile alias = current with { Path = current.Path.ToLowerInvariant() };
        Assert.AreEqual("C:\\Install\\Exact", owner.OverlayRuntimeStates([exact]).Single().InstallDestination);
        Assert.IsTrue(string.IsNullOrWhiteSpace(owner.OverlayRuntimeStates([alias]).Single().InstallDestination));
        collection.Collection.UpsertCharts([current]);
        // 置換で退役したtokenを救済せず、fixtureの新しい現在値はDB完全一致パスで取得します。
        ChartFile replacement = collection.Collection.ResolveCurrentChart(
            LibraryChartRef.FromPath(current.Kind, current.Path, current.Md5, current.Sha256));
        Assert.IsNotNull(replacement);
        Assert.AreNotSame(current.Token, replacement.Token);
        Assert.IsTrue(string.IsNullOrWhiteSpace(owner.OverlayRuntimeStates([replacement]).Single().InstallDestination));
    }

    [TestMethod]
    public void CreateOverlaySnapshot_PreservesCaseOnlyRowsWithSameHash()
    {
        ChartFile first = Parse("C:\\Library\\Chart.bms");
        ChartFile second = first with { Path = "C:\\Library\\chart.bms" };
        CatalogOwnedCollectionOwner collection = CreateCollection(first, second);
        var owner = new InstallDestinationStateOwner(collection, () => []);
        var mutation = new InstallDestinationRuntimeStateMutation();
        mutation.AppliedCharts.Add(WithDestination(collection.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(first)), "C:\\Install\\First", ""));
        mutation.AppliedCharts.Add(WithDestination(collection.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(second)), "C:\\Install\\Second", ""));
        owner.Apply(mutation);

        InstallDestinationOverlayChartRefSnapshot snapshot = owner.CreateOverlaySnapshot(out bool wasCached);

        Assert.IsFalse(wasCached);
        Assert.AreEqual(2, snapshot.ChartCount);
    }

    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_DropsMissingItem()
    {
        CatalogOwnedCollectionOwner collection = CreateCollection();
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile detached = WithDestination(Parse("C:\\Library\\Missing.bms"), "C:\\Install\\Missing", "");

        IReadOnlyList<ChartFile> reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]);

        Assert.AreEqual(0, reattached.Count);
    }

    private static CatalogOwnedCollectionOwner CreateCollection(params ChartFile[] charts)
    {
        var owner = new CatalogOwnedCollectionOwner();
        owner.ApplyBuiltCollection(OwnedChartCollectionState.FromCharts(charts));
        return owner;
    }

    private static ChartFile WithDestination(ChartFile chart, string destination, string title)
        => ChartFileProjection.WithPackageState(chart, destination, title, "Overlay artist", [], []);

    private static ChartFile Parse(string path)
        => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.CreateSnapshot(new ChartFileReadBuffer(
            path, Encoding.ASCII.GetBytes("#TITLE Initial\n#ARTIST Author\n"), default)));
}
