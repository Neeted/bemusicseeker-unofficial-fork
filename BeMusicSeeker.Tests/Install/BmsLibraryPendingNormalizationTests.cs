using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsLibraryPackageInstallServiceTests
{
    /// <summary>同path・SHAの固定指定を発見前に適用し、非ASCII音声の実在を親一式への根拠に使います。内容変更時は旧指定を渡さず、元の単体・子単位を保ちます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrepareAutoInstallWorkflow_AppliesSameInputFixedEncodingBeforePackageDiscovery(bool changedContent)
    {
        WithTemporaryDirectory(root =>
        {
            string parent = Path.Combine(root, "Collection");
            Directory.CreateDirectory(parent);
            string path = Path.Combine(parent, "a.bms");
            File.WriteAllText(path, "#TITLE ASCII\n#WAV01 é.wav\n#00111:01\n", Encoding.GetEncoding("windows-1252"));
            File.WriteAllBytes(Path.Combine(parent, "é.ogg"), []);
            string child = Path.Combine(parent, "Child");
            string childPath = CreateBmsFile(child, "child.bms", "#TITLE Child\n");
            var entry = PackageChartEntry.FromPath(path);
            ChartFile fixedChart = BmsChartFileParser.ApplyEncoding(ChartFileContentReader.ReadSnapshot(path), entry.Chart, "windows-1252");
            entry.ApplyCommittedEncoding(ChartFileProjection.WithMaintenance(fixedChart,
                BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(fixedChart) with { Encoding = "windows-1252", EncodingFixed = true }));
            var oldFile = ChartPackage.FromChartEntries([entry]);
            oldFile.path = path;
            var oldChild = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(childPath)]);
            oldChild.path = child;
            if (changedContent) { File.AppendAllText(path, "#GENRE changed\n", Encoding.ASCII); }
            AutoInstallWorkflowResult result = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([parent], [oldFile, oldChild], [], _ => false);
            CollectionAssert.AreEquivalent(changedContent ? new[] { path, child } : new[] { parent }, result.DiscoveredPackages.Select(package => package.path).ToArray(), string.Join(";", result.DiscoveredPackages.SelectMany(package => package.ChartEntries).Select(item => $"{item.Chart.Path} / {item.Chart.EncodingName} / {item.Chart.ResourceHealthMaintenanceSnapshot?.EncodingFixed} / {string.Join(",", item.Chart.Resources?.Select(r => r.RawPath) ?? [])}")));
            PackageChartEntry current = result.DiscoveredPackages.SelectMany(package => package.ChartEntries).Single(item => item.Chart.Path == path);
            Assert.AreEqual(!changedContent, current.Chart.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(changedContent ? 0 : 100, current.Chart.WAVHealth);
            if (!changedContent) { Assert.AreEqual("é.wav", current.Chart.Resources.Single().RawPath); }
        });
    }

    /// <summary>旧親と子の重複をA/B単体と子作品へ分割し、変化した単位のDSTを解除します。同じ子単位の指定と同path同SHAの確定復号だけを維持し、無関係な未具体化保留を読みません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PrepareAutoInstallWorkflow_OverlappingPendingSplitsAndPreservesOnlySameUnitState(bool changedContent)
    {
        WithTemporaryDirectory(root =>
        {
            var service = new BmsLibraryPackageInstallService();
            string parent = Path.Combine(root, "Collection");
            string a = CreateBmsFile(parent, "a.bms", CreateValidBmsText("Café"));
            string b = Path.Combine(parent, "b.bmson");
            File.WriteAllText(b, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"B\",\"artist\":\"Artist\"},\"sound_channels\":[]}", new System.Text.UTF8Encoding(false));
            string child = Path.Combine(parent, "C");
            string c = CreateBmsFile(child, "c.bms", CreateValidBmsText("C"));
            var oldA = PackageChartEntry.FromPath(a);
            var oldC = PackageChartEntry.FromPath(c);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(oldA);
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(oldC);
            ChartFile fixedA = BmsChartFileParser.ApplyEncoding(ChartFileContentReader.ReadSnapshot(a), oldA.Chart, "windows-1252") with
            {
                ResourceHealthMaintenanceSnapshot = oldA.Chart.ResourceHealthMaintenanceSnapshot with { Encoding = "windows-1252", EncodingFixed = true }
            };
            oldA.ApplyCommittedEncoding(fixedA);
            oldA.SetInstallDestinationPathOnly(Path.Combine(root, "OldParentDestination"));
            oldC.RestoreInstallDestinationState(new PackageChartInstallDestinationState(Path.Combine(root, "ChildDestination"), "C", "Artist", [Path.Combine(root, "Candidate")]));
            var oldParent = ChartPackage.FromChartEntries([oldA, PackageChartEntry.FromPath(b), PackageChartEntry.FromPath(c)]);
            oldParent.path = parent;
            oldParent.delete_parent = true;
            var oldChild = ChartPackage.FromChartEntries([oldC]);
            oldChild.path = child;
            oldChild.delete_parent = true;
            string lazyRoot = Path.Combine(root, "Unrelated");
            string lazyPath = CreateBmsFile(lazyRoot, "lazy.bms", CreateValidBmsText("Unrelated"));
            var lazy = new ChartPackage { path = lazyRoot, delete_parent = true };
            if (changedContent) { File.AppendAllText(a, "\n#GENRE changed\n", Encoding.ASCII); }
            byte[] before = File.ReadAllBytes(a);
            using var held = new FileStream(lazyPath, FileMode.Open, FileAccess.Read, FileShare.None);

            AutoInstallWorkflowResult workflow = service.PrepareAutoInstallWorkflow([parent], [oldParent, oldChild, lazy], [], _ => false);

            CollectionAssert.AreEquivalent(new[] { a, b, child }, workflow.DiscoveredPackages.Select(package => package.path).ToArray());
            PendingPackageNormalizationGroup group = workflow.PendingPackageNormalizations.Single();
            Assert.IsTrue(group.IsComplete);
            CollectionAssert.AreEquivalent(new[] { oldParent, oldChild }, group.Sources);
            ChartPackage newChild = workflow.DiscoveredPackages.Single(package => package.path == child);
            Assert.IsTrue(newChild.delete_parent, "同じdirectory単位のdelete_parentを保ちます。");
            Assert.AreEqual(oldC.Chart.InstallDestination, newChild.ChartEntries.Single().Chart.InstallDestination);
            CollectionAssert.AreEqual(oldC.Chart.InstallDestinationSuggestions.ToArray(), newChild.ChartEntries.Single().Chart.InstallDestinationSuggestions.ToArray());
            PackageChartEntry newA = workflow.DiscoveredPackages.Single(package => package.path == a).ChartEntries.Single();
            Assert.IsTrue(string.IsNullOrWhiteSpace(newA.Chart.InstallDestination));
            Assert.AreEqual(0, newA.Chart.InstallDestinationSuggestions.Count);
            if (!changedContent)
            {
                Assert.AreEqual(oldA.Chart.Title, newA.Chart.Title);
                Assert.AreEqual("windows-1252", newA.Chart.EncodingName);
            }
            Assert.AreEqual(!changedContent, newA.Chart.ResourceHealthMaintenanceSnapshot.EncodingFixed, "同pathでもSHAが変わった内容へ確定復号を移しません。");
            Assert.AreEqual(0, lazy.CaptureMaterializedChartEntries().Count);
            Assert.IsFalse(workflow.PendingPackagesToRemove.Contains(lazy));
            Assert.AreEqual(Path.Combine(root, "OldParentDestination"), oldA.Chart.InstallDestination);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(a));
        });
    }

    /// <summary>親の実在資源により旧子群を全譜面入りの一式へ統合し、旧DSTとdelete_parentのORを持ち込みません。再分類した親子の全定義で入れ子禁止を確定します。</summary>
    [TestMethod]
    public void PrepareAutoInstallWorkflow_ParentResourcesMergePendingAndRecheckNestedLayout()
    {
        WithTemporaryDirectory(root =>
        {
            string parent = Path.Combine(root, "Collection");
            string a = CreateBmsFile(parent, "a.bms", CreateValidBmsText("A"));
            string child = Path.Combine(parent, "C");
            string c = CreateBmsFile(child, "c.bms", CreateValidBmsText("C"));
            File.WriteAllBytes(Path.Combine(parent, "sound.wav"), [1]);
            File.WriteAllBytes(Path.Combine(child, "sound.wav"), [2]);
            var oldA = new ChartPackage { path = a, delete_parent = true };
            var oldC = new ChartPackage { path = child, delete_parent = true };
            AutoInstallWorkflowResult workflow = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([parent], [oldA, oldC], [], _ => false);
            ChartPackage merged = workflow.DiscoveredPackages.Single();
            Assert.AreEqual(parent, merged.path);
            CollectionAssert.AreEquivalent(new[] { a, c }, merged.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
            Assert.IsFalse(merged.delete_parent);
            Assert.IsTrue(merged.HasResourceBearingNestedCharts);
            Assert.AreEqual(0, workflow.AutoInstallCandidates.Count);
            Assert.IsTrue(workflow.PendingPackagesToAdd.Contains(merged));
            Assert.IsTrue(merged.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
            Assert.AreEqual(0, oldA.CaptureMaterializedChartEntries().Count);
            Assert.AreEqual(0, oldC.CaptureMaterializedChartEntries().Count);
        });
    }

    /// <summary>今回入力と無関係な旧重複群も登録だけ整理し、完全な音源があっても新しい自動導入・推定の資格を与えません。独立した読取失敗群とmissing子は保全します。</summary>
    [TestMethod]
    public void PrepareAutoInstallWorkflow_UnrelatedOverlapIsRegistrationOnlyAndIncompleteGroupIsProtected()
    {
        WithTemporaryDirectory(root =>
        {
            string good = Path.Combine(root, "Good");
            string a = CreateBmsFile(good, "a.bms", CreateValidBmsText("A"));
            File.WriteAllBytes(Path.Combine(good, "sound.wav"), [1]);
            string bad = Path.Combine(root, "Bad");
            string locked = CreateBmsFile(bad, "locked.bms", CreateValidBmsText("Locked"));
            string missing = Path.Combine(bad, "Missing", "missing.bms");
            var oldGood = new ChartPackage { path = good };
            var oldGoodChild = new ChartPackage { path = a };
            var oldBad = new ChartPackage { path = bad };
            var oldMissing = new ChartPackage { path = missing };
            string input = CreateBmsFile(Path.Combine(root, "Input"), "input.bms", CreateValidBmsText("Input"));
            using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            AutoInstallWorkflowResult workflow = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([input], [oldGood, oldGoodChild, oldBad, oldMissing], [], _ => false);
            Assert.IsTrue(workflow.DiscoveryFailures.Any(exception => exception is IOException));
            Assert.IsFalse(workflow.DiscoveredPackages.Any(package => package.path.StartsWith(bad, StringComparison.OrdinalIgnoreCase)));
            Assert.IsFalse(workflow.PendingPackagesToRemove.Contains(oldMissing));
            ChartPackage normalized = workflow.DiscoveredPackages.Single(package => package.path == good);
            Assert.IsTrue(workflow.RegistrationOnlyPackages.Contains(normalized));
            Assert.IsTrue(workflow.PendingPackagesToAdd.Contains(normalized));
            Assert.IsFalse(workflow.AutoInstallCandidates.Contains(normalized));
            CollectionAssert.AreEquivalent(new[] { a }, normalized.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        });
    }

    /// <summary>旧複数登録の削除と新複数登録の追加を実state applierとSQLiteで一括確定します。最初のINSERT直後のabortでも旧全行・公開集合・入力を保持します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ApplyPendingPackageMutationDelta_NormalizationUsesOneTransactionBeforePublication(bool abortAfterInsert)
    {
        WithTemporarySongDb((database, root) =>
        {
            string parent = Path.Combine(root, "Collection");
            string a = CreateBmsFile(parent, "a.bms", CreateValidBmsText("A"));
            string b = CreateBmsFile(parent, "b.bms", CreateValidBmsText("B"));
            string child = Path.Combine(parent, "C");
            string c = CreateBmsFile(child, "c.bms", CreateValidBmsText("C"));
            var oldParent = new ChartPackage { path = parent, delete_parent = true };
            var oldChild = new ChartPackage { path = child };
            var unrelated = new ChartPackage { path = Path.Combine(root, "Unrelated"), delete_parent = true };
            ChartPackage[] originals = [oldParent, oldChild, unrelated];
            ExecuteSongDbFixtureTransaction(database, db =>
            {
                db.CreateTable<LR2SongDBExtended.install>();
                foreach (ChartPackage package in originals) { db.Insert(package, typeof(LR2SongDBExtended.install)); }
            });
            var gateway = new BmsLibraryDbGateway(database);
            int publications = 0;
            var owner = new PackageLifecycleOwner(gateway, new TestUiScheduler(() => null), _ => publications++,
                packages => new ObservableCollection<ChartPackage>(packages ?? []), () => { }, _ => { });
            owner.ReplacePendingPackages(originals);
            publications = 0;
            var service = new BmsLibraryPackageInstallService();
            PendingPackageNormalizationGroup group = service.DiscoverPendingPackageNormalizations([oldParent, oldChild]).Single();
            PendingPackageMutationDelta delta = service.BuildPendingPackageMutationDelta(originals, group.Sources);
            if (abortAfterInsert)
            {
                ExecuteSongDbFixtureTransaction(database, db =>
                    db.Execute("CREATE TRIGGER fail_normalization AFTER INSERT ON install BEGIN SELECT RAISE(ABORT, 'normalization-insert'); END;"));
            }
            if (abortAfterInsert)
            {
                SQLite.SQLiteException failure = Assert.ThrowsException<SQLite.SQLiteException>(() => owner.ApplyPendingPackageMutationDelta(delta, group.Packages, group.Packages));
                StringAssert.Contains(failure.Message, "normalization-insert");
                CollectionAssert.AreEqual(originals, owner.PendingPackages.ToArray());
                Assert.AreEqual(0, publications);
            }
            else
            {
                owner.ApplyPendingPackageMutationDelta(delta, group.Packages, group.Packages);
                CollectionAssert.AreEquivalent(new[] { a, b, child, unrelated.path }, owner.PendingPackages.Select(package => package.path).ToArray());
                Assert.IsTrue(owner.PendingPackages.Contains(unrelated));
            }
            List<ChartPackage> rows = gateway.LoadInstallPackages();
            string[] expectedRows = abortAfterInsert ? [.. originals.Select(package => package.path)] : [a, b, child, unrelated.path];
            CollectionAssert.AreEquivalent(expectedRows, rows.Select(package => package.path).ToArray());
            Assert.IsTrue(rows.Single(package => package.path == unrelated.path).delete_parent);
            Assert.IsTrue(File.Exists(a) && File.Exists(b) && File.Exists(c));
        });
    }
}
