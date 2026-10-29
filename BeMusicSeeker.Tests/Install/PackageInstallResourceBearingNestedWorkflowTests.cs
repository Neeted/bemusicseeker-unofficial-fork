using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>実発見から初段・推定後自動の両方を通し、資源付き親子一式だけを保留します。独立正常入力の導入、所持・SQLite・元bytes、論理受付終端を一緒に観測します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_ResourceBearingNestedChartsRemainPendingWhileIndependentInputInstalls(bool keepNewPending)
    {
        await using var fixture = new AutomaticContinuationFixture(true, true);
        fixture.Options = fixture.CreateOptions(true, true, keepNewPending);
        string source = fixture.WriteInput("Nested", "parent.bms", "nested-parent");
        string child = fixture.WriteInput(Path.Combine("Nested", "child"), "child.bms", "nested-child");
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [3, 4]);
        File.WriteAllBytes(Path.Combine(child, "sound.wav"), [5, 6]);
        string normal = fixture.WriteInput("Normal", "normal.bms", "independent-normal");
        if (!keepNewPending) { File.WriteAllBytes(Path.Combine(normal, "sound.wav"), [7]); }
        Dictionary<string, byte[]> preserved = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        byte[] owned = File.ReadAllBytes(Path.Combine(fixture.Destination, "owned.bms"));
        await fixture.InitializeAsync();

        Assert.IsTrue(fixture.Owner.Enqueue([source, normal]));
        await fixture.Owner.WaitForIdleAsync();

        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        ChartPackage blocked = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(source, blocked.path);
        Assert.IsTrue(blocked.HasResourceBearingNestedCharts);
        Assert.IsTrue(blocked.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBearingNestedCharts)));
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => chart.Path == Path.Combine(fixture.Destination, "parent.bms") || chart.Path == Path.Combine(fixture.Destination, "child", "child.bms")));
        Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => Path.GetFileName(chart.Path) == "normal.bms"));
        foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
        CollectionAssert.AreEqual(owned, File.ReadAllBytes(Path.Combine(fixture.Destination, "owned.bms")));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(source, db.Table<LR2SongDBExtended.install>().Single().path);
        Assert.IsTrue(db.Table<LR2SongDB.song>().Any(row => Path.GetFileName(row.path) == "normal.bms"));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }
}
