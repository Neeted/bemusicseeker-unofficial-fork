using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class InstallDestinationStateOwnerTests
{
    /// <summary>別名pathのhashが一致しても完全一致pathの現token・hashへ再接続し、宛先とtitleの混入・欠落を検出します。</summary>
    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_UsesExactPathWhenAliasHashMatches()
    {
        string path = "C:\\Library\\Chart.bms";
        ChartFile first = CreateChart(path);
        ChartFile alias = CreateChart(path.ToLowerInvariant()) with { Md5 = new string('b', 32) };
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

    /// <summary>同pathでも形式が違う残存宛先を再接続せず、BMSからBMSONへの状態漏れを検出します。</summary>
    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_RejectsCrossKindLeakage()
    {
        ChartFile bms = CreateChart("C:\\Library\\Chart.bms");
        CatalogOwnedCollectionOwner collection = CreateCollection(bms);
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile wrongKind = WithDestination(bms with { Kind = ChartFileKind.Bmson }, "C:\\Install\\Other", "");

        Assert.AreEqual(0, owner.ReattachFileScanResidualInstallDestinationCharts([wrongKind]).Count);
    }

    /// <summary>残存BMSON宛先を現在の所持tokenへ再接続し、形式別の接続漏れを検出します。</summary>
    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_ReattachesBmsonToken()
    {
        ChartFile parsed = CreateChart("C:\\Library\\Chart.bmson") with { Kind = ChartFileKind.Bmson };
        CatalogOwnedCollectionOwner collection = CreateCollection(parsed);
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile detached = WithDestination(parsed, "C:\\Install\\Bmson", "");

        ChartFile reattached = owner.ReattachFileScanResidualInstallDestinationCharts([detached]).Single();

        Assert.AreSame(collection.Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(parsed)).Token, reattached.Token);
        Assert.AreEqual("C:\\Install\\Bmson", reattached.InstallDestination);
    }

    /// <summary>宛先overlayは完全一致pathと大文字小文字非依存hashを使い、別名path・置換後tokenへの救済を検出します。</summary>
    [TestMethod]
    public void OverlayRuntimeStates_UsesExactPathAndCaseInsensitiveHashButRejectsReplacementToken()
    {
        ChartFile parsed = CreateChart("C:\\Library\\Chart.bms");
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

    /// <summary>同hashのcase-only別DB行を二件のoverlayへ残し、path別の宛先を重複として落とす誤りを検出します。</summary>
    [TestMethod]
    public void CreateOverlaySnapshot_PreservesCaseOnlyRowsWithSameHash()
    {
        ChartFile first = CreateChart("C:\\Library\\Chart.bms");
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

    /// <summary>所持集合から消えた譜面の残存宛先を再接続せず、退役項目の状態復活を検出します。</summary>
    [TestMethod]
    public void ReattachFileScanResidualInstallDestinationCharts_DropsMissingItem()
    {
        CatalogOwnedCollectionOwner collection = CreateCollection();
        var owner = new InstallDestinationStateOwner(collection, () => []);
        ChartFile detached = WithDestination(CreateChart("C:\\Library\\Missing.bms"), "C:\\Install\\Missing", "");

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

    private static ChartFile CreateChart(string path)
        => ChartTestValues.Empty() with { Path = path, Md5 = new string('a', 32) };
}
