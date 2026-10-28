using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>譜面と内包ZIPのfolder／全選択を実確保・保留登録・通常推定・後段導入へ通します。投入前の独立treeと照合し、ZIP自身を同梱して内部譜面を別packageへ二重生成しないことを確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionIngress_FullChartScopeKeepsEmbeddedArchiveWithoutSeparateExpansion(bool selectAll)
    {
        await using var fixture = new AutomaticContinuationFixture(true, false, temporaryIngress: true, destinationSounds: ["a.wav", "b.wav"]);
        string source = fixture.WriteInput(Path.Combine("ExternalTemp", "Pack"), "a.bms", "a");
        fixture.WriteInput(Path.Combine("ExternalTemp", "Pack"), "b.bms", "b");
        foreach (string name in new[] { "a", "b" })
        {
            string path = Path.Combine(source, name + ".bms");
            File.WriteAllText(path, File.ReadAllText(path).Replace("sound.wav", name + ".wav"), Encoding.ASCII);
        }
        File.WriteAllBytes(Path.Combine(source, "embedded.zip"), CreateEmbeddedChartArchive("embedded-only.bms"));
        Dictionary<string, byte[]> expected = CaptureArchiveTree(source);
        Dictionary<string, byte[]> destinationBefore = CaptureArchiveTree(fixture.Destination);
        string[] selected = selectAll ? Directory.GetFileSystemEntries(source) : [source];
        await fixture.InitializeAsync();
        int ownedBefore = fixture.Library.BmsCharts.Count;
        bool registered = false;
        string staged = Path.Combine(fixture.Root, "Ingress", "Pack");
        fixture.Observer.Applied = () =>
        {
            if (registered) { return; }
            ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
            Assert.AreEqual(staged, pending.path);
            CollectionAssert.AreEquivalent(new[] { Path.Combine(staged, "a.bms"), Path.Combine(staged, "b.bms") },
                pending.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
            using LR2SongDBExtended before = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
            Assert.AreEqual(staged, before.Table<LR2SongDBExtended.install>().Single().path);
            AssertArchiveTree(expected, staged);
            registered = true;
        };
        try
        {
            DroppedInstallIngressAcquisitionResult acquisition = fixture.Owner.AcquireAndTryEnqueueDroppedPaths(selected);
            Assert.IsTrue(acquisition.Succeeded, acquisition.Exception?.ToString());
            await fixture.Owner.WaitForIdleAsync();
        }
        finally { fixture.Observer.Applied = null; }
        Assert.IsTrue(registered, "通常登録後・後段導入前の推定適用へ到達します。");
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsNotNull(fixture.Result?.EstimatedInstallReceipt);
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.InstalledTargetApplyCount);
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        ChartPackage installed = fixture.Library.ChartPackagesInstalled.Single();
        CollectionAssert.AreEquivalent(new[] { Path.Combine(fixture.Destination, "a.bms"), Path.Combine(fixture.Destination, "b.bms") },
            installed.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        Assert.AreEqual(ownedBefore + 2, fixture.Library.BmsCharts.Count);
        AssertArchiveTree(destinationBefore.Concat(expected).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase), fixture.Destination);
        AssertArchiveTree(expected, source);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
        Assert.IsNotNull(db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "a.bms")));
        Assert.IsNotNull(db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "b.bms")));
        Assert.IsFalse(db.Table<LR2SongDB.song>().Any(row => row.path.EndsWith("embedded-only.bms", StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>部分譜面＋ZIPと、直下譜面なしの全ZIP選択は従来の個別展開を維持します。未選択兄弟の混入、親一式への誤統合、書庫順序の変更を実確保から保留DBまで確認します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionIngress_PartialAndChartlessArchiveSelectionsKeepSeparateExpansion(bool chartless)
    {
        await using var fixture = new AutomaticContinuationFixture(false, false, temporaryIngress: true);
        string source = Path.Combine(fixture.Root, "ExternalTemp", "Pack");
        Directory.CreateDirectory(source);
        string firstArchive = Path.Combine(source, "first.zip");
        File.WriteAllBytes(firstArchive, CreateEmbeddedChartArchive("first.bms"));
        string[] selected;
        if (chartless)
        {
            string secondArchive = Path.Combine(source, "second.zip");
            File.WriteAllBytes(secondArchive, CreateEmbeddedChartArchive("second.bms"));
            selected = [firstArchive, secondArchive];
        }
        else
        {
            fixture.WriteInput(Path.Combine("ExternalTemp", "Pack"), "a.bms", "a");
            fixture.WriteInput(Path.Combine("ExternalTemp", "Pack"), "b.bms", "unselected");
            File.WriteAllText(Path.Combine(source, "readme.txt"), "unselected");
            selected = [Path.Combine(source, "a.bms"), firstArchive];
        }
        Dictionary<string, byte[]> expected = CaptureArchiveTree(source);
        var extractedNames = new System.Collections.Generic.List<string>();
        fixture.Files.BeforeTimestamp = path => extractedNames.Add(Path.GetFileName(path));
        await fixture.InitializeAsync();
        try
        {
            DroppedInstallIngressAcquisitionResult acquisition = fixture.Owner.AcquireAndTryEnqueueDroppedPaths(selected);
            Assert.IsTrue(acquisition.Succeeded, acquisition.Exception?.ToString());
            await fixture.Owner.WaitForIdleAsync();
        }
        finally { fixture.Files.BeforeTimestamp = null; }
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(2, fixture.Library.ChartPackagesPending.Count);
        CollectionAssert.AreEqual(chartless ? new[] { "first.bms", "second.bms" } : new[] { "first.bms" }, extractedNames.ToArray());
        string staged = Path.Combine(fixture.Root, "Ingress", "Pack");
        Assert.IsFalse(fixture.Library.ChartPackagesPending.Any(package => package.path == staged));
        CollectionAssert.AreEquivalent(chartless ? new[] { "first.bms", "second.bms" } : new[] { "a.bms", "first.bms" },
            fixture.Library.ChartPackagesPending.SelectMany(package => package.ChartEntries).Select(entry => Path.GetFileName(entry.Chart.Path)).ToArray());
        if (!chartless)
        {
            Assert.IsTrue(fixture.Library.ChartPackagesPending.Any(package => package.path == Path.Combine(staged, "a.bms")));
            Assert.IsFalse(File.Exists(Path.Combine(staged, "b.bms")));
            Assert.IsFalse(File.Exists(Path.Combine(staged, "readme.txt")));
        }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray(),
            db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
        AssertArchiveTree(expected, source);
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>展開の有無を一意に観測できる、譜面一つだけの人工書庫を作ります。</summary>
    private static byte[] CreateEmbeddedChartArchive(string name)
    {
        using var content = new MemoryStream();
        using (var zip = new ZipArchive(content, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream stream = zip.CreateEntry(name).Open();
            stream.Write(Encoding.ASCII.GetBytes(CreateValidBmsText("Embedded " + name)));
        }
        return content.ToArray();
    }

    /// <summary>元のfolder／中身全選択／部分選択を実materializerから実owner・port・SQLiteへ通します。部分copyで隣接ファイルが消えても、確保先の見かけで全選択へ逆転させません。</summary>
    [TestMethod]
    [DataRow("folder")]
    [DataRow("all")]
    [DataRow("partial")]
    [DataRow("partial-preview")]
    public async Task ProductionIngress_PreservesOriginalSelectionAcrossPrivateCopy(string selection)
    {
        await using var fixture = new AutomaticContinuationFixture(false, false, temporaryIngress: true);
        string source = fixture.WriteInput(Path.Combine("ExternalTemp", "Pack"), "a.bms", "a");
        fixture.WriteInput(Path.Combine("ExternalTemp", "Pack"), "b.bms", "b");
        string a = Path.Combine(source, "a.bms");
        string b = Path.Combine(source, "b.bms");
        File.WriteAllText(a, File.ReadAllText(a).Replace("sound.wav", "missing-a.wav"), Encoding.ASCII);
        File.WriteAllText(b, File.ReadAllText(b).Replace("sound.wav", "missing-b.wav"), Encoding.ASCII);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "unselected");
        string sub = Path.Combine(source, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllBytes(Path.Combine(sub, "omakefile.txt"), [7, 4]);
        if (selection == "partial-preview") { File.WriteAllBytes(Path.Combine(source, "preview.ogg"), [9]); }
        string[] selected = selection switch { "folder" => [source], "all" => Directory.GetFileSystemEntries(source), _ => [a, b] };
        await fixture.InitializeAsync();
        DroppedInstallIngressAcquisitionResult acquisition = fixture.Owner.AcquireAndTryEnqueueDroppedPaths(selected);
        Assert.IsTrue(acquisition.Succeeded, acquisition.Exception?.ToString());
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        string staged = Path.Combine(fixture.Root, "Ingress", "Pack");
        bool partial = selection.StartsWith("partial", StringComparison.Ordinal);
        string[] expectedPackages = partial ? [Path.Combine(staged, "a.bms"), Path.Combine(staged, "b.bms")] : [staged];
        CollectionAssert.AreEquivalent(expectedPackages, fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray());
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(expectedPackages, db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
        foreach (string name in new[] { "a.bms", "b.bms" })
        { CollectionAssert.AreEqual(File.ReadAllBytes(Path.Combine(source, name)), File.ReadAllBytes(Path.Combine(staged, name))); }
        Assert.AreEqual(!partial, File.Exists(Path.Combine(staged, "readme.txt")));
        Assert.AreEqual(!partial, File.Exists(Path.Combine(staged, "sub", "omakefile.txt")));
        Assert.IsFalse(File.Exists(Path.Combine(staged, "preview.ogg")));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>同物理入力の二回の実取込みで、完全読取時だけ旧BMS/BMSON単体を一式へ登録置換します。DST非継承・同内容の指定文字コード保持・無関係保留を確認し、読取り・準備後取消・実DB失敗では旧群を先行変更しません。</summary>
    [TestMethod]
    [DataRow("success", false)]
    [DataRow("success", true)]
    [DataRow("read", false)]
    [DataRow("read-missing", false)]
    [DataRow("cancel", false)]
    [DataRow("database", false)]
    public async Task ProductionImport_ReplacesSamePhysicalPendingScopeOnlyAfterAcceptedCompleteRead(string failureKind, bool selectAll)
    {
        await using var fixture = new AutomaticContinuationFixture(false, false);
        string source = fixture.WriteInput("Reimport", "a.bms", "a");
        string a = Path.Combine(source, "a.bms");
        File.WriteAllText(a, CreateValidBmsText("Café").Replace("sound.wav", "missing-a.wav"), new UTF8Encoding(false));
        string b = Path.Combine(source, "b.bmson");
        File.WriteAllText(b, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Café\",\"artist\":\"Artist\"},\"sound_channels\":[{\"name\":\"missing-b.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]}", new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(source, "readme.txt"), "unselected");
        string unrelated = fixture.WriteInput("Unrelated", "other.bms", "other");
        byte[] aBytes = File.ReadAllBytes(a);
        byte[] bBytes = File.ReadAllBytes(b);
        await fixture.InitializeAsync();
        Assert.IsTrue(fixture.Owner.Enqueue([a, b, unrelated]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        ChartPackage[] originals = fixture.Library.ChartPackagesPending.ToArray();
        Assert.AreEqual(3, originals.Length);
        ChartPackage first = originals.Single(package => package.path == a);
        ChartPackage other = originals.Single(package => package.path == unrelated);
        PackageChartEntry oldEntry = first.ChartEntries.Single();
        ChartFile specified = BmsChartFileParser.ApplyMetadataEncoding(ChartFileContentReader.ReadSnapshot(a), oldEntry.Chart, "windows-1252") with
        {
            ResourceHealthMaintenanceSnapshot = oldEntry.Chart.ResourceHealthMaintenanceSnapshot with { Encoding = "windows-1252", EncodingFixed = true }
        };
        oldEntry.ApplyCommittedEncoding(specified);
        oldEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(fixture.Destination, "Previous", "Previous", [fixture.Destination]));
        string expectedTitle = oldEntry.Chart.Title;
        string[] originalRows;
        using (LR2SongDBExtended before = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        { originalRows = before.Table<LR2SongDBExtended.install>().Select(row => row.path + "|" + row.delete_parent).ToArray(); }
        string? independent = null;
        using var cancellation = new CancellationTokenSource();
        FileStream? held = null;
        try
        {
            if (failureKind.StartsWith("read", StringComparison.Ordinal))
            {
                held = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None);
                independent = fixture.WriteInput("Independent", "new.bms", "independent");
                if (failureKind == "read-missing") { File.Delete(b); }
            }
            if (failureKind == "database")
            {
                ExecuteSongDbFixtureTransaction(fixture.Database, db => db.Execute(
                    "CREATE TRIGGER fail_scope_replacement BEFORE INSERT ON install WHEN NEW.path = '" + source.Replace("'", "''") + "' BEGIN SELECT RAISE(ABORT, 'scope-replacement'); END;"));
            }
            if (failureKind == "cancel")
            {
                File.WriteAllBytes(Path.Combine(source, "missing-a.wav"), [1]);
                File.WriteAllBytes(Path.Combine(source, "missing-b.wav"), [2]);
                fixture.Options = fixture.CreateOptions(false, false, keepNewPending: false);
                PackageInstallCommandResult canceled = await fixture.Library.InstallChartPackagesAutoWithProgressAsync([source], cancellation.Token,
                    new ScopeProgressWriter(), beforePhysicalMutation: () => { cancellation.Cancel(); return Task.CompletedTask; });
                Assert.IsTrue(cancellation.IsCancellationRequested, "準備した実候補が停止境界へ到達します。");
                Assert.IsTrue(canceled.WorkflowFailures.Any(failure => failure is OperationCanceledException));
            }
            else
            {
                string[] selected = selectAll ? Directory.GetFileSystemEntries(source) : independent == null ? [source] : [source, independent];
                Assert.IsTrue(fixture.Owner.Enqueue(selected));
                await fixture.Owner.WaitForIdleAsync();
            }
        }
        finally { held?.Dispose(); }
        using LR2SongDBExtended after = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        if (failureKind == "success")
        {
            Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
            ChartPackage replacement = fixture.Library.ChartPackagesPending.Single(package => package.path == source);
            Assert.AreEqual(2, replacement.ChartEntries.Count);
            Assert.IsTrue(replacement.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
            PackageChartEntry current = replacement.ChartEntries.Single(entry => entry.Chart.Path == a);
            Assert.AreEqual("windows-1252", current.Chart.EncodingName);
            Assert.AreEqual(expectedTitle, current.Chart.Title);
            Assert.IsTrue(current.Chart.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(0, current.Chart.WAVHealth);
            CollectionAssert.AreEquivalent(new[] { source, unrelated }, after.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
            Assert.IsFalse(fixture.Library.ChartPackagesPending.Contains(first));
        }
        else
        {
            CollectionAssert.AreEquivalent(originals, fixture.Library.ChartPackagesPending.Where(package => package.path != independent).ToArray());
            CollectionAssert.AreEquivalent(originalRows, after.Table<LR2SongDBExtended.install>().Where(row => row.path != independent).Select(row => row.path + "|" + row.delete_parent).ToArray());
            if (independent != null)
            {
                Assert.IsTrue(fixture.Library.ChartPackagesPending.Any(package => package.path == independent));
                Assert.IsTrue(after.Table<LR2SongDBExtended.install>().Any(row => row.path == independent));
                Assert.IsNotNull(fixture.Result);
                Assert.IsTrue(fixture.Result.HasDurableCommit);
                Assert.IsTrue(fixture.Result.HasRequiredFailure);
                Assert.IsTrue(fixture.Result.WorkflowFailures.Any(failure => failure is IOException));
            }
            Assert.AreEqual(fixture.Destination, oldEntry.Chart.InstallDestination);
            CollectionAssert.AreEqual(new[] { fixture.Destination }, oldEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.AreEqual(expectedTitle, oldEntry.Chart.Title);
            if (failureKind == "database")
            {
                Assert.IsNotNull(fixture.Result);
                Assert.IsTrue(fixture.Result.HasRequiredFailure);
                StringAssert.Contains(fixture.Failure?.Exception.ToString() ?? fixture.Result.SessionReceipt.PrimaryFailure.ToString(), "scope-replacement");
            }
        }
        CollectionAssert.AreEqual(aBytes, File.ReadAllBytes(a));
        if (failureKind == "read-missing") { Assert.IsFalse(File.Exists(b), "不完全scopeの旧missing登録は別scopeの成功で解除しません。"); }
        else { CollectionAssert.AreEqual(bBytes, File.ReadAllBytes(b)); }
        Assert.IsTrue(fixture.Library.ChartPackagesPending.Contains(other));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }
    private sealed class ScopeProgressWriter : IPackageInstallProgressWriter
    {
        public void TryWrite(PackageInstallProgressUpdate update) { }
    }
}
