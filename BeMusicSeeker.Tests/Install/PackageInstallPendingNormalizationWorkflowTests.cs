using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>実ownerの親先・子先・同時取込みでA/B単体と子作品へ揃え、SQLiteの所属と一覧を一致させます。借用入力と実確保した管理入力のbytes・物理pathを保持します。</summary>
    [TestMethod]
    [DataRow("parent-first", false)]
    [DataRow("child-first", false)]
    [DataRow("together", false)]
    [DataRow("together", true)]
    public async Task ProductionImport_ParentAndChildOrdersConvergeToCurrentPhysicalUnits(string order, bool managed)
    {
        await using var fixture = new AutomaticContinuationFixture(false, false, temporaryIngress: managed);
        string sourceName = managed ? Path.Combine("ExternalTemp", "Collection") : "Collection";
        string parent = Path.Combine(fixture.Root, sourceName);
        string child = Path.Combine(parent, "Child");
        if (order != "child-first")
        {
            fixture.WriteInput(sourceName, "a.bms", "a");
            fixture.WriteInput(sourceName, "b.bms", "b");
        }
        if (order != "parent-first") { fixture.WriteInput(Path.Combine(sourceName, "Child"), "c.bms", "c"); }
        await fixture.InitializeAsync();
        if (order != "together")
        {
            Assert.IsTrue(fixture.Owner.Enqueue([order == "parent-first" ? parent : child]));
            await fixture.Owner.WaitForIdleAsync();
            Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
            if (order == "parent-first") { fixture.WriteInput(Path.Combine(sourceName, "Child"), "c.bms", "c"); }
            else
            {
                fixture.WriteInput(sourceName, "a.bms", "a");
                fixture.WriteInput(sourceName, "b.bms", "b");
            }
        }
        Dictionary<string, byte[]> before = Directory.GetFiles(parent, "*", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetRelativePath(parent, path), File.ReadAllBytes);
        string[] paths = order == "together" ? [parent, child] : [order == "parent-first" ? child : parent];
        if (managed)
        {
            DroppedInstallIngressAcquisitionResult acquisition = fixture.Owner.AcquireAndTryEnqueueDroppedPaths(paths);
            Assert.IsTrue(acquisition.Succeeded, acquisition.Exception?.ToString());
        }
        else { Assert.IsTrue(fixture.Owner.Enqueue(paths)); }
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        string materialized = managed ? Path.Combine(fixture.Root, "Ingress", "Collection") : parent;
        string[] expected = [Path.Combine(materialized, "a.bms"), Path.Combine(materialized, "b.bms"), Path.Combine(materialized, "Child")];
        CollectionAssert.AreEquivalent(expected, fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray());
        CollectionAssert.AreEquivalent(expected, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
        foreach ((string path, byte[] bytes) in before)
        {
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(parent, path)));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(materialized, path)));
        }
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>legacyの親子重複を読取失敗では旧登録のまま保ち、ロック解放後の同じ親再投入だけで修復します。既存path除外・新規0件の早期終了による修復不能を検出します。</summary>
    [TestMethod]
    public async Task ProductionImport_OverlappingPendingCanBeRepairedBySameRootAfterReadFailure()
    {
        var port = new ObservingInstallMutationPort();
        await using var fixture = new AutomaticContinuationFixture(false, false, mutationPort: port);
        int completions = 0;
        fixture.Owner.CompletionPublished += _ => completions++;
        string parent = fixture.WriteInput("Collection", "a.bms", "a");
        string child = fixture.WriteInput(Path.Combine("Collection", "Child"), "c.bms", "c");
        string a = Path.Combine(parent, "a.bms");
        await fixture.InitializeAsync();
        ChartPackage[] originals = [new ChartPackage { path = parent, delete_parent = true }, new ChartPackage { path = child, delete_parent = true }];
        SeedLegacyPending(fixture, originals);
        using (var held = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsTrue(fixture.Owner.Enqueue([parent]));
            await fixture.Owner.WaitForIdleAsync();
            CollectionAssert.AreEquivalent(originals, fixture.Library.ChartPackagesPending.ToArray());
            CollectionAssert.AreEquivalent(new[] { parent, child }, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
            Assert.IsNotNull(port.LatestResult);
            Assert.IsTrue(port.LatestResult.HasRequiredFailure);
        }
        PackageInstallCommandResult? failedResult = port.LatestResult;
        Assert.IsTrue(fixture.Owner.Enqueue([parent]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(port.LatestResult);
        Assert.AreNotSame(failedResult, port.LatestResult);
        Assert.AreEqual(2, port.CompletedCount);
        Assert.IsFalse(port.LatestResult.HasRequiredFailure);
        Assert.IsTrue(port.LatestResult.HasDurableCommit);
        Assert.AreEqual(0, completions, "保留登録だけの正常終端からUI完了通知を追加しません。");
        CollectionAssert.AreEquivalent(new[] { a, child }, fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray());
        CollectionAssert.AreEquivalent(new[] { a, child }, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
        Assert.IsTrue(File.Exists(a) && File.Exists(Path.Combine(child, "c.bms")));
    }

    /// <summary>健康な混在保留へ非譜面fileだけを実FileDropすると、元選択を保持しつつ親一式へ自動推定・導入資格を広げません。同じPの正当な譜面dropは資源根拠で親へ昇格し、実推定・導入を完了するため、別の自動不可理由による偽の陰性を除きます。</summary>
    [TestMethod]
    [DataRow("readme.txt")]
    [DataRow("sound.wav")]
    public async Task ProductionIngress_NonChartFileDoesNotGrantPendingParentAutomaticEligibility(string selectedName)
    {
        var port = new ObservingInstallMutationPort();
        await using var fixture = new AutomaticContinuationFixture(false, false, temporaryIngress: true, mutationPort: port);
        string source = fixture.WriteInput("Pending", "new.bms", "unowned-non-chart-drop");
        string newChart = Path.Combine(source, "new.bms");
        string ownedSource = Path.Combine(source, "owned-copy.bms");
        string installed = Path.Combine(fixture.Destination, "owned.bms");
        File.Copy(installed, ownedSource);
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1]);
        File.WriteAllText(Path.Combine(source, "readme.txt"), "unselected sibling");
        await fixture.InitializeAsync();
        string newHash = PackageChartEntry.FromPath(newChart).Chart.Md5;
        string ownedHash = fixture.Library.BmsCharts.Single(chart => chart.Path == installed).Md5;
        Assert.AreEqual(ownedHash, PackageChartEntry.FromPath(ownedSource).Chart.Md5);
        Assert.AreNotEqual(ownedHash, newHash);
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => chart.Md5 == newHash));
        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(source, pending.path);
        CollectionAssert.AreEquivalent(new[] { newChart, ownedSource }, pending.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        PackageChartEntry unownedEntry = pending.ChartEntries.Single(entry => entry.Chart.Path == newChart);
        Assert.AreEqual(fixture.Destination, unownedEntry.Chart.InstallDestination, "auto OFFでも実所持hash根拠から推定先が成立する健康なPです。");
        Assert.AreEqual(100, unownedEntry.Chart.WAVHealth);
        Assert.IsTrue(fixture.Observer.PreparedPaths.Contains(source));
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "new.bms")));
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.IsNotNull(db.Find<LR2SongDB.song>(installed));
            Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "new.bms")));
            Assert.AreEqual(source, db.Table<LR2SongDBExtended.install>().Single().path);
        }
        // 初回の実推定成立を確認した後だけ明示クリアし、両dropを同じ未推定状態から比較します。
        // 非空DSTの保全は同物理再投入・属性不能群の既存代表が分担します。
        pending = fixture.Library.ChartPackagesPending.Single(package => package.path == source);
        fixture.Library.RemoveInstallDestination(pending.ChartEntries, pending);
        Assert.IsTrue(pending.ChartEntries.All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)));
        var destinations = pending.ChartEntries.ToDictionary(entry => entry.Chart.Path, entry => entry.CaptureInstallDestinationState());
        Dictionary<string, byte[]> sourceBytes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        Dictionary<string, byte[]> destinationBytes = Directory.GetFiles(fixture.Destination, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        bool deleteParent = pending.delete_parent;
        int preparedCount = fixture.Observer.PreparedPaths.Count;
        int completedCount = port.CompletedCount;
        PackageInstallCommandResult? previousResult = port.LatestResult;
        fixture.Options = fixture.CreateOptions(true, false);
        string selected = Path.Combine(source, selectedName);
        AutoInstallWorkflowResult prepared = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow(
            [selected], [pending], [fixture.LibraryRoot], chart => chart.Md5 == ownedHash);
        Assert.AreEqual(0, prepared.AutoInstallCandidates.Count);
        Assert.IsTrue(prepared.DiscoveredPackages.All(package => prepared.RegistrationOnlyPackages.Contains(package)),
            "非譜面単独入力でPが再出力されても登録専用であり、新しい推定対象へ渡しません。");
        Assert.IsTrue(prepared.PendingPackagesToAdd.All(package => prepared.RegistrationOnlyPackages.Contains(package)));
        DroppedInstallIngressAcquisitionResult acquisition = fixture.Owner.AcquireAndTryEnqueueDroppedPaths([selected]);
        Assert.IsTrue(acquisition.Succeeded, acquisition.Exception?.ToString());
        CollectionAssert.AreEqual(new[] { selected }, acquisition.Request.Paths.ToArray(), "仮想ExternalTemp外の借用元rawfileをそのまま渡します。");
        Assert.IsNotNull(acquisition.Request.FullySelectedDirectories);
        Assert.AreEqual(0, acquisition.Request.FullySelectedDirectories.Count, "兄弟を残した単独選択を全選択へ変えません。");
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(completedCount + 1, port.CompletedCount);
        Assert.IsNotNull(port.LatestResult);
        Assert.AreNotSame(previousResult, port.LatestResult);
        Assert.IsFalse(port.LatestResult.HasRequiredFailure);
        Assert.AreEqual(0, port.LatestResult.RegisteredPackages.Count);
        Assert.AreEqual(preparedCount, fixture.Observer.PreparedPaths.Count, "非譜面dropからPの新推定準備を始めません。");
        ChartPackage current = fixture.Library.ChartPackagesPending.Single(package => package.path == source);
        CollectionAssert.AreEquivalent(destinations.Keys.ToArray(), current.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        Assert.AreEqual(deleteParent, current.delete_parent);
        foreach (PackageChartEntry entry in current.ChartEntries)
        {
            PackageChartInstallDestinationState expected = destinations[entry.Chart.Path];
            Assert.AreEqual(expected.Destination, entry.Chart.InstallDestination);
            Assert.AreEqual(expected.Title, entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(expected.Artist, entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(expected.Suggestions.ToArray(), entry.Chart.InstallDestinationSuggestions.ToArray());
        }
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => chart.Md5 == newHash));
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            LR2SongDBExtended.install row = db.Table<LR2SongDBExtended.install>().Single();
            Assert.AreEqual(source, row.path);
            Assert.AreEqual(deleteParent, row.delete_parent);
            Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "new.bms")));
        }
        AssertBytes(sourceBytes, source);
        AssertBytes(destinationBytes, fixture.Destination);

        DroppedInstallIngressAcquisitionResult legitimate = fixture.Owner.AcquireAndTryEnqueueDroppedPaths([newChart]);
        Assert.IsTrue(legitimate.Succeeded, legitimate.Exception?.ToString());
        CollectionAssert.AreEqual(new[] { newChart }, legitimate.Request.Paths.ToArray());
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsNotNull(port.LatestResult);
        Assert.IsFalse(port.LatestResult.HasRequiredFailure);
        Assert.IsTrue(port.LatestResult.EstimatedInstallReceipt?.DurableCommit == true);
        Assert.IsTrue(fixture.Observer.PreparedPaths.Count > preparedCount, "正当な譜面fileは同じ親の実資源を根拠に推定・導入できます。");
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        string target = Path.Combine(fixture.Destination, "new.bms");
        CollectionAssert.AreEqual(sourceBytes[newChart], File.ReadAllBytes(target));
        Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => chart.Path == target && chart.Md5 == newHash));
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.IsNotNull(db.Find<LR2SongDB.song>(target));
            Assert.IsNotNull(db.Find<LR2SongDB.song>(installed));
            Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
        }

        static void AssertBytes(Dictionary<string, byte[]> expected, string root)
        {
            CollectionAssert.AreEquivalent(expected.Keys.ToArray(), Directory.GetFiles(root, "*", SearchOption.AllDirectories));
            foreach ((string path, byte[] bytes) in expected) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
        }
    }

    /// <summary>無関係な旧重複群は通常追加の同じtransactionで登録だけ揃え、音源が完全でも自動導入・推定準備へ渡しません。実ownerからの入力と旧入力のbytesを保持します。</summary>
    [TestMethod]
    public async Task ProductionImport_UnrelatedOverlapDoesNotGainAutomaticEligibility()
    {
        await using var fixture = new AutomaticContinuationFixture(true, false);
        string unrelated = fixture.WriteInput("Unrelated", "unrelated.bms", "unrelated");
        string oldFile = Path.Combine(unrelated, "unrelated.bms");
        File.WriteAllBytes(Path.Combine(unrelated, "sound.wav"), [2]);
        string input = fixture.WriteInput("Input", "input.bms", "input");
        byte[] before = File.ReadAllBytes(oldFile);
        await fixture.InitializeAsync();
        SeedLegacyPending(fixture, [new ChartPackage { path = unrelated }, new ChartPackage { path = oldFile }]);
        fixture.Options = fixture.CreateOptions(true, false, keepNewPending: false);
        Assert.IsTrue(fixture.Owner.Enqueue([input]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsTrue(fixture.Library.ChartPackagesPending.Any(package => package.path == unrelated));
        Assert.IsFalse(fixture.Library.ChartPackagesPending.Any(package => package.path == oldFile));
        Assert.IsFalse(fixture.Observer.PreparedPaths.Any(path => path.StartsWith(unrelated, StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(fixture.Library.ChartPackagesInstalled.Any(package => package.ChartEntries.Any(entry => entry.Chart.Path.EndsWith("unrelated.bms", StringComparison.OrdinalIgnoreCase))));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(oldFile));
        Assert.IsTrue(new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Any(package => package.path == unrelated));
    }

    /// <summary>旧子fileから、直接fileの親昇格と明示子directoryを同時に要求します。親readlockで群が不完全なら子別探索の成功を導入へ漏らさず、旧行・指定・元bytesを保って独立正常入力だけを導入します。解放後の同じ要求で親一式へ修復できます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_IncompletePromotedParentKeepsExplicitChildAndAllowsIndependentInput(bool childFirst)
    {
        var port = new ObservingInstallMutationPort();
        await using var fixture = new AutomaticContinuationFixture(false, false, mutationPort: port);
        string parent = fixture.WriteInput("Overlap", "promote.bms", "promote");
        fixture.WriteInput("Overlap", "locked.bms", "locked");
        string child = fixture.WriteInput(Path.Combine("Overlap", "Child"), "c.bms", "child");
        string c = Path.Combine(child, "c.bms");
        File.WriteAllText(Path.Combine(child, "readme.txt"), "unselected");
        await fixture.InitializeAsync();

        // 音源を置く前に部分fileとして通常登録し、directory保留では到達できない旧単体の前提を作ります。
        Assert.IsTrue(fixture.Owner.Enqueue([c]));
        await fixture.Owner.WaitForIdleAsync();
        ChartPackage original = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(c, original.path);
        PackageChartInstallDestinationState destination = original.ChartEntries.Single().CaptureInstallDestinationState();
        bool deleteParent = original.delete_parent;
        File.WriteAllBytes(Path.Combine(parent, "sound.wav"), [2]);
        File.WriteAllBytes(Path.Combine(child, "sound.wav"), [3]);
        string independent = fixture.WriteInput("Independent", "normal.bms", "independent");
        File.WriteAllBytes(Path.Combine(independent, "sound.wav"), [4]);
        byte[] independentBytes = File.ReadAllBytes(Path.Combine(independent, "normal.bms"));
        Dictionary<string, byte[]> preserved = Directory.GetFiles(parent, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        fixture.Options = fixture.CreateOptions(false, false, keepNewPending: false);
        string promote = Path.Combine(parent, "promote.bms");
        // 正当な末尾区切り付きdirectory指定を使い、親昇格と明示子の別探索を通常入口で成立させます。
        string childInput = child + Path.DirectorySeparatorChar;
        string[] relatedInputs = childFirst ? [childInput, promote] : [promote, childInput];
        using (var held = new FileStream(Path.Combine(parent, "locked.bms"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.IsTrue(fixture.Owner.Enqueue([.. relatedInputs, independent]));
            await fixture.Owner.WaitForIdleAsync();
            Assert.IsNotNull(port.LatestResult);
            Assert.IsTrue(port.LatestResult.HasDurableCommit);
            Assert.IsTrue(port.LatestResult.HasRequiredFailure);
            Assert.IsTrue(port.LatestResult.WorkflowFailures.Any(failure => failure is IOException));
            Assert.AreEqual(1, port.LatestResult.RegisteredPackages.Count, "独立正常入力だけを実導入します。");
            Assert.AreSame(original, fixture.Library.ChartPackagesPending.Single());
            Assert.AreEqual(destination.Destination, original.ChartEntries.Single().Chart.InstallDestination);
            Assert.AreEqual(destination.Title, original.ChartEntries.Single().Chart.InstallDestinationTitle);
            Assert.AreEqual(destination.Artist, original.ChartEntries.Single().Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(destination.Suggestions.ToArray(), original.ChartEntries.Single().Chart.InstallDestinationSuggestions.ToArray());
            ChartPackage row = new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Single();
            Assert.AreEqual(c, row.path);
            Assert.AreEqual(deleteParent, row.delete_parent);
            Assert.IsFalse(Directory.GetFiles(fixture.LibraryRoot, "c.bms", SearchOption.AllDirectories).Any());
            Assert.IsTrue(port.LatestResult.SessionReceipt.ConfirmedTargets.All(target => !target.SourcePath.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        }
        foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
        ChartPackage installed = fixture.Library.ChartPackagesInstalled.Single();
        string normalDestination = installed.ChartEntries.Single().Chart.Path;
        Assert.AreEqual("normal.bms", Path.GetFileName(normalDestination));
        CollectionAssert.AreEqual(independentBytes, File.ReadAllBytes(normalDestination));
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.IsNotNull(db.Find<LR2SongDB.song>(normalDestination));
        }

        PackageInstallCommandResult? failedRead = port.LatestResult;
        Assert.IsTrue(fixture.Owner.Enqueue(relatedInputs));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(port.LatestResult);
        Assert.AreNotSame(failedRead, port.LatestResult);
        Assert.AreEqual(3, port.CompletedCount);
        Assert.IsFalse(port.LatestResult.HasRequiredFailure);
        Assert.IsTrue(port.LatestResult.HasDurableCommit);
        ChartPackage repaired = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(parent, repaired.path);
        CollectionAssert.AreEquivalent(new[] { promote, Path.Combine(parent, "locked.bms"), c }, repaired.ChartEntries.Select(entry => entry.Chart.Path).ToArray());
        Assert.IsTrue(repaired.HasResourceBearingNestedCharts, "完全読取後も通常の入れ子判定で親一式を保留します。");
        Assert.IsFalse(fixture.Library.ChartPackagesPending.Contains(original));
        Assert.AreEqual(parent, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Single().path);
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>接続B群を独立Cより先に渡し、同hashの先行成功と一致entryだけの既所持警告を保ちます。B物理処理中の取消ではB成功だけを確定し、Cを未実行保留として元bytesとともに保持します。群内directory列挙順は固定しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_ConnectedGroupKeepsEarlierSlotForHashAndCancellation(bool cancel)
    {
        await using var fixture = new AutomaticContinuationFixture(false, false);
        string b = fixture.WriteInput("B", "b.bms", "shared");
        string bChart = Path.Combine(b, "b.bms");
        File.WriteAllBytes(Path.Combine(b, "sound.wav"), [2]);
        string c = fixture.WriteInput("C", "same.bms", "shared");
        fixture.WriteInput("C", "unique.bms", "unique");
        File.WriteAllBytes(Path.Combine(c, "sound.wav"), [3]);
        byte[] bBytes = File.ReadAllBytes(bChart);
        Dictionary<string, byte[]> cBytes = Directory.GetFiles(c).ToDictionary(path => path, File.ReadAllBytes);
        await fixture.InitializeAsync();
        ChartPackage[] originals = [new ChartPackage { path = b, delete_parent = true }, new ChartPackage { path = bChart }];
        SeedLegacyPending(fixture, originals);
        fixture.Options = fixture.CreateOptions(false, false, keepNewPending: false);
        string? startedSource = null;
        fixture.Files.BeforeCopy = path =>
        {
            if (startedSource != null) { return; }
            startedSource = path;
            if (cancel) { fixture.Owner.CancelAll(); }
        };

        Assert.IsTrue(fixture.Owner.Enqueue([b, c]));
        await fixture.Owner.WaitForIdleAsync();

        Assert.IsNotNull(startedSource);
        Assert.IsTrue(string.Equals(startedSource, b, StringComparison.OrdinalIgnoreCase)
            || startedSource.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            "接続B群を独立Cより先に処理します。実コピー元: " + startedSource);
        Assert.IsNotNull(fixture.Result);
        Assert.IsTrue(fixture.Result.SessionReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Result.RegisteredPackages.Count);
        Assert.IsTrue(fixture.Result.SessionReceipt.ConfirmedTargets.Count > 0);
        Assert.IsTrue(fixture.Result.SessionReceipt.ConfirmedTargets.All(target =>
            target.SourcePath == b || target.SourcePath.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        ChartPackage installed = fixture.Library.ChartPackagesInstalled.Single();
        string destination = installed.ChartEntries.Single().Chart.Path;
        Assert.AreEqual("b.bms", Path.GetFileName(destination));
        CollectionAssert.AreEqual(bBytes, File.ReadAllBytes(destination));
        ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(c, pending.path);
        Assert.IsFalse(originals.Any(fixture.Library.ChartPackagesPending.Contains));
        CollectionAssert.AreEqual(new[] { c }, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
        PackageChartEntry same = pending.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "same.bms");
        PackageChartEntry unique = pending.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "unique.bms");
        Assert.AreEqual(!cancel, same.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        Assert.IsFalse(unique.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
        foreach ((string path, byte[] bytes) in cBytes) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
        Assert.IsFalse(Directory.GetFiles(fixture.LibraryRoot, "unique.bms", SearchOption.AllDirectories).Any());
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.IsNotNull(db.Find<LR2SongDB.song>(destination));
            Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(Path.GetDirectoryName(destination) ?? throw new InvalidOperationException(), "unique.bms")));
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>旧親群を分割後、一単位の物理成功中に取消しても成功receiptと同群の残る全新保留を同時確定し、旧親を復活させません。独立した登録専用群は旧参照・DB行を保持します。同じ地点のDB失敗は旧登録・確認候補・失敗receiptを保ち、先行commitや全体rollbackを追加しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_SplitOverlapCommitsPhysicalPrefixAndRemainingUnitsOnCancellation(bool databaseFailure)
    {
        await using var fixture = new AutomaticContinuationFixture(false, false);
        string parent = fixture.WriteInput("Collection", "a.bms", "a");
        string child = fixture.WriteInput(Path.Combine("Collection", "First"), "first.bms", "first");
        string second = fixture.WriteInput(Path.Combine("Collection", "Second"), "second.bms", "second");
        File.WriteAllBytes(Path.Combine(child, "sound.wav"), [2]);
        File.WriteAllBytes(Path.Combine(second, "sound.wav"), [3]);
        string registrationOnly = fixture.WriteInput("RegistrationOnly", "pending.bms", "registration-only");
        string registrationOnlyFile = Path.Combine(registrationOnly, "pending.bms");
        File.WriteAllBytes(Path.Combine(registrationOnly, "sound.wav"), [4]);
        Dictionary<string, byte[]> originalBytes = Directory.GetFiles(parent, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        foreach (string path in Directory.GetFiles(registrationOnly)) { originalBytes.Add(path, File.ReadAllBytes(path)); }
        await fixture.InitializeAsync();
        var registrationOnlyParent = new ChartPackage { path = registrationOnly, delete_parent = true };
        var registrationOnlyChild = new ChartPackage { path = registrationOnlyFile };
        ChartPackage[] originals = [new ChartPackage { path = parent, delete_parent = true }, new ChartPackage { path = child }, registrationOnlyParent, registrationOnlyChild];
        SeedLegacyPending(fixture, originals);
        fixture.Options = fixture.CreateOptions(false, false, keepNewPending: false);
        if (databaseFailure)
        {
            ExecuteSongDbFixtureTransaction(fixture.Database, db => db.Execute("CREATE TRIGGER fail_split_pending AFTER INSERT ON install BEGIN SELECT RAISE(ABORT, 'split-pending'); END;"));
        }
        string? successfulSource = null;
        fixture.Files.BeforeCopy = path =>
        {
            if (successfulSource != null || (!path.StartsWith(child, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(second, StringComparison.OrdinalIgnoreCase))) { return; }
            successfulSource = path.StartsWith(child, StringComparison.OrdinalIgnoreCase) ? child : second;
            fixture.Owner.CancelAll();
        };
        Assert.IsTrue(fixture.Owner.Enqueue([parent]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(successfulSource, "実物理処理を開始してから取消します。");
        Assert.IsNotNull(fixture.Result);
        string remaining = successfulSource == child ? second : child;
        string installedName = successfulSource == child ? "first.bms" : "second.bms";
        string remainingName = successfulSource == child ? "second.bms" : "first.bms";
        Assert.IsTrue(fixture.Result.SessionReceipt.ConfirmedChangeCount > 0);
        Assert.IsTrue(fixture.Result.SessionReceipt.ConfirmedTargets.All(target => target.SourcePath.StartsWith(successfulSource, StringComparison.OrdinalIgnoreCase)));
        LibraryMutationSessionTarget installedTarget = fixture.Result.SessionReceipt.ConfirmedTargets.First(target =>
            target.SourcePath == successfulSource || target.SourcePath == Path.Combine(successfulSource, installedName));
        string installedPath = File.Exists(installedTarget.DestinationPath) ? installedTarget.DestinationPath : Path.Combine(installedTarget.DestinationPath, installedName);
        Assert.IsTrue(File.Exists(installedPath));
        Assert.IsFalse(Directory.GetFiles(fixture.LibraryRoot, remainingName, SearchOption.AllDirectories).Any());
        if (databaseFailure)
        {
            Assert.IsFalse(fixture.Result.SessionReceipt.DurableCommit);
            Assert.IsTrue(fixture.Result.HasRequiredFailure);
            Assert.IsNotNull(fixture.Result.SessionReceipt.ApplyFailure);
            StringAssert.Contains(fixture.Result.SessionReceipt.ApplyFailure.ToString(), "split-pending");
            Assert.AreEqual(0, fixture.Result.RegisteredPackages.Count);
            CollectionAssert.AreEquivalent(originals, fixture.Library.ChartPackagesPending.ToArray());
            CollectionAssert.AreEquivalent(originals.Select(package => package.path).ToArray(), new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
            Assert.IsTrue(fixture.Result.RecoveryPaths.Any(path => path.StartsWith(fixture.LibraryRoot, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            Assert.IsTrue(fixture.Result.SessionReceipt.DurableCommit);
            Assert.AreEqual(1, fixture.Result.RegisteredPackages.Count);
            string[] expected = [Path.Combine(parent, "a.bms"), remaining, registrationOnly, registrationOnlyFile];
            CollectionAssert.AreEquivalent(expected, fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray());
            CollectionAssert.AreEquivalent(expected, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
            Assert.IsFalse(fixture.Library.ChartPackagesPending.Any(package => package.path == parent));
            Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        }
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.AreEqual(!databaseFailure, db.Find<LR2SongDB.song>(installedPath) != null);
            Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(Path.GetDirectoryName(installedPath) ?? throw new InvalidOperationException(), remainingName)));
            Assert.IsTrue(db.Table<LR2SongDBExtended.install>().Single(row => row.path == registrationOnly).delete_parent);
            Assert.IsFalse(db.Table<LR2SongDBExtended.install>().Single(row => row.path == registrationOnlyFile).delete_parent);
        }
        Assert.IsTrue(fixture.Library.ChartPackagesPending.Contains(registrationOnlyParent));
        Assert.IsTrue(fixture.Library.ChartPackagesPending.Contains(registrationOnlyChild));
        foreach ((string path, byte[] bytes) in originalBytes)
        {
            string actual = !databaseFailure && path.StartsWith(successfulSource + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(Path.GetDirectoryName(installedPath) ?? throw new InvalidOperationException(), Path.GetRelativePath(successfulSource, path))
                : path;
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(actual));
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>過去保存された親directoryと内部fileの重複群が属性取得不能でも正常emptyへ変えず、旧2参照・DB・DST・入力bytesを保持して独立入力だけ登録します。ACL復旧後は同じ独立入力から登録専用整理を確認します。</summary>
    /// <remarks>専用containerのListDirectoryと対象rootのReadAttributesだけを非継承Denyにします。旧EntryExistsがfalseとなる実属性失敗と既知子の読取りを事前確認し、ACL非対応・変更権限不足・拒否未再現は理由付き未実施です。開始済み操作を回収後、両ACLを必ず復旧してから専用rootを通常回収します。</remarks>
    [TestMethod]
    public Task ProductionImport_AttributeDeniedOverlapKeepsSourceAndAllowsIndependentInput()
        => VerifyAttributeDeniedScopeAsync("Collection", childInput: false);

    /// <summary>supported拡張子を持つ実directoryのkind不能をfileへ落とさず、内部childdirectory入力の単独採用と親だけのmissing削除を防ぎます。</summary>
    [TestMethod]
    public Task ProductionImport_AttributeDeniedSupportedDirectoryKeepsChildAndIndependentInput()
        => VerifyAttributeDeniedScopeAsync("Collection.bms", childInput: true);

    private static async Task VerifyAttributeDeniedScopeAsync(string parentName, bool childInput)
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows ACLによる実属性拒否を確認するケースです。"); }
        var port = new ObservingInstallMutationPort();
        await using var fixture = new AutomaticContinuationFixture(false, false, mutationPort: port);
        string container = Path.Combine(fixture.Root, "Private");
        string parent = fixture.WriteInput(Path.Combine("Private", parentName), "a.bms", "attribute-denied");
        string chart = Path.Combine(parent, "a.bms");
        string independent = fixture.WriteInput("Independent", "normal.bms", "independent-attribute-denied");
        string nested = childInput ? fixture.WriteInput(Path.Combine("Private", parentName, "Child"), "child.bms", "attribute-child") : chart;
        string nestedChart = childInput ? Path.Combine(nested, "child.bms") : chart;
        await fixture.InitializeAsync();
        // 現在の通常APIが重複を作るとはせず、過去保存状態を既存helperで用意します。
        var parentEntry = PackageChartEntry.FromPath(chart);
        var fileEntry = PackageChartEntry.FromPath(nestedChart);
        var oldParent = ChartPackage.FromChartEntries([parentEntry]);
        oldParent.path = parent;
        oldParent.delete_parent = true;
        var oldFile = ChartPackage.FromChartEntries([fileEntry]);
        oldFile.path = nested;
        oldFile.delete_parent = false;
        string fileOnlyDestination = Path.Combine(fixture.Root, "FileOnlyDestination");
        parentEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(fixture.Destination, "Parent DST", "Parent Artist", [fixture.Destination]));
        fileEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(fileOnlyDestination, "File DST", "File Artist", [fileOnlyDestination]));
        ChartPackage[] originals = [oldParent, oldFile];
        SeedLegacyPending(fixture, originals);
        Dictionary<ChartPackage, PackageChartInstallDestinationState> destinations = originals.ToDictionary(package => package, package => package.ChartEntries.Single().CaptureInstallDestinationState());
        var gateway = new BmsLibraryDbGateway(fixture.Database);
        var beforeRows = gateway.LoadInstallPackages().ToDictionary(row => row.path, row => row.delete_parent, StringComparer.Ordinal);
        Dictionary<string, byte[]> preserved = Directory.GetFiles(parent, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        byte[] independentBytes = File.ReadAllBytes(Path.Combine(independent, "normal.bms"));
        var containerInfo = new DirectoryInfo(container);
        var rootInfo = new DirectoryInfo(parent);
        byte[]? containerAcl = null;
        byte[]? rootAcl = null;
        bool containerChanged = false;
        bool rootChanged = false;
        try
        {
            Assert.IsTrue((LongPathFileSystem.GetAttributes(parent) & FileAttributes.Directory) != 0);
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                SecurityIdentifier user = identity.User ?? throw new AssertFailedException("実行ユーザーのSIDを取得できません。");
                containerAcl = containerInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
                rootAcl = rootInfo.GetAccessControl().GetSecurityDescriptorBinaryForm();
                var deniedContainer = new DirectorySecurity();
                deniedContainer.SetSecurityDescriptorBinaryForm(containerAcl, AccessControlSections.Access);
                deniedContainer.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ListDirectory,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
                containerInfo.SetAccessControl(deniedContainer);
                containerChanged = true;
                var deniedRoot = new DirectorySecurity();
                deniedRoot.SetSecurityDescriptorBinaryForm(rootAcl, AccessControlSections.Access);
                deniedRoot.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAttributes,
                    InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
                rootInfo.SetAccessControl(deniedRoot);
                rootChanged = true;
            }
            catch (Exception failure) when (failure is UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
            { Assert.Inconclusive("専用rootのACL設定が非対応または権限不足のため未実施: " + failure.Message); }

            Exception? attributeFailure = null;
            try { _ = LongPathFileSystem.GetAttributes(parent); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { attributeFailure = failure; }
            if (attributeFailure == null || LongPathFileSystem.IsNotFoundFailure(attributeFailure) || LongPathFileSystem.EntryExists(parent))
            { Assert.Inconclusive("旧存在確認がfalseとなる非NotFound属性拒否を再現できないため未実施: " + attributeFailure); }
            CollectionAssert.AreEqual(preserved[chart], File.ReadAllBytes(chart), "traverseと既知の子読取りを保持します。");
            string[] inputs = childInput ? [nested, independent] : [independent];
            // 一般の出力順を固定せず、source順と同物理aliasを変えて保護・候補集合だけ確認します。
            var alias = new ChartPackage { path = Path.Combine(parent, "."), delete_parent = false };
            ChartPackage[][] snapshots = [originals, [.. originals.Reverse()], [alias, .. originals], [oldParent], [alias]];
            foreach (ChartPackage[] snapshot in snapshots)
            {
                AutoInstallWorkflowResult prepared = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow(inputs, snapshot, [fixture.LibraryRoot], _ => false);
                Assert.IsTrue(prepared.DiscoveryFailures.Any(failure => (failure is IOException or UnauthorizedAccessException)
                    && !LongPathFileSystem.IsNotFoundFailure(failure) && failure.Message.Contains(parent, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(snapshot.All(source => !prepared.PendingPackagesToRemove.Contains(source)));
                CollectionAssert.AreEquivalent(new[] { independent }, prepared.DiscoveredPackages.Select(package => package.path).ToArray());
                Assert.IsFalse(prepared.AutoInstallCandidates.Any(package => IsWithin(package.path, parent)));
            }
            PackageInstallCommandResult? previous = port.LatestResult;
            Assert.IsTrue(fixture.Owner.Enqueue(inputs));
            await fixture.Owner.WaitForIdleAsync();
            Assert.IsNotNull(port.LatestResult);
            Assert.AreNotSame(previous, port.LatestResult);
            Assert.IsTrue(port.LatestResult.HasRequiredFailure);
            Assert.IsTrue(port.LatestResult.HasDurableCommit, "独立正常directoryの登録は確定します。");
            Assert.IsTrue(port.LatestResult.WorkflowFailures.Any(failure => (failure is IOException or UnauthorizedAccessException)
                && !LongPathFileSystem.IsNotFoundFailure(failure) && failure.Message.Contains(parent, StringComparison.OrdinalIgnoreCase)));
            foreach (ChartPackage source in originals)
            {
                Assert.AreSame(source, fixture.Library.ChartPackagesPending.Single(package => package.path == source.path));
                Assert.AreEqual(beforeRows[source.path], source.delete_parent);
                AssertDestination(destinations[source], source.ChartEntries.Single());
                ChartPackage row = gateway.LoadInstallPackages().Single(package => package.path == source.path);
                Assert.AreEqual(beforeRows[source.path], row.delete_parent);
            }
            Assert.IsTrue(fixture.Library.ChartPackagesPending.Any(package => package.path == independent));
            CollectionAssert.AreEquivalent(new[] { parent, nested, independent }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
            Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        }
        finally
        {
            // ACLが有効な操作を先に回収し、復旧失敗はtest失敗として伝えます。
            try { await fixture.Owner.WaitForIdleAsync(); }
            finally
            {
                try { if (rootChanged && rootAcl != null) { RestoreSavedAccess(rootInfo, rootAcl); } }
                finally { if (containerChanged && containerAcl != null) { RestoreSavedAccess(containerInfo, containerAcl); } }
                _ = LongPathFileSystem.GetAttributes(parent);
                CollectionAssert.Contains(Directory.GetFileSystemEntries(container), parent, "containerの列挙権限が実復旧してから回収します。");
                foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }
                CollectionAssert.AreEqual(independentBytes, File.ReadAllBytes(Path.Combine(independent, "normal.bms")));
            }
        }
        Assert.IsTrue(fixture.Owner.Enqueue([independent]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(port.LatestResult);
        Assert.IsFalse(port.LatestResult.HasRequiredFailure);
        if (childInput)
        {
            CollectionAssert.AreEquivalent(new[] { chart, nested, independent }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
            Assert.IsFalse(fixture.Library.ChartPackagesPending.Any(package => package.path == parent));
            ChartPackage direct = fixture.Library.ChartPackagesPending.Single(package => package.path == chart);
            Assert.IsTrue(string.IsNullOrEmpty(direct.ChartEntries.Single().Chart.InstallDestination), "親から分割した単体へ旧親DSTをコピーしません。");
            ChartPackage retainedChild = fixture.Library.ChartPackagesPending.Single(package => package.path == nested);
            Assert.AreEqual(beforeRows[nested], retainedChild.delete_parent);
            AssertDestination(destinations[oldFile], retainedChild.ChartEntries.Single());
        }
        else
        {
            ChartPackage repaired = fixture.Library.ChartPackagesPending.Single(package => package.path == parent);
            Assert.IsFalse(fixture.Library.ChartPackagesPending.Contains(oldFile));
            Assert.AreEqual(beforeRows[parent], repaired.delete_parent);
            AssertDestination(destinations[oldParent], repaired.ChartEntries.Single());
            Assert.AreNotEqual(destinations[oldFile].Destination, repaired.ChartEntries.Single().Chart.InstallDestination,
                "内部fileの宛先を、同unit親の指定がある整理先へ拡散しません。");
            CollectionAssert.AreEquivalent(new[] { parent, independent }, gateway.LoadInstallPackages().Select(package => package.path).ToArray());
        }
        foreach ((string path, byte[] bytes) in preserved) { CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path)); }

        static bool IsWithin(string path, string root) => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        static void AssertDestination(PackageChartInstallDestinationState expected, PackageChartEntry entry)
        {
            Assert.AreEqual(expected.Destination, entry.Chart.InstallDestination);
            Assert.AreEqual(expected.Title, entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(expected.Artist, entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(expected.Suggestions.ToArray(), entry.Chart.InstallDestinationSuggestions.ToArray());
        }

        static void RestoreSavedAccess(DirectoryInfo directory, byte[] descriptor)
        {
            // GetAccessControlで取得した未変更objectのPersistはAccessを書きません。
            // 新objectへAccessを明示設定して変更済みにし、Denyを実際に取り除きます。
            var restored = new DirectorySecurity();
            restored.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            directory.SetAccessControl(restored);
            string expected = restored.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            string actual = directory.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.AreEqual(expected, actual, "保存したDACLの実復旧を確認します: " + directory.FullName);
        }
    }

    /// <summary>過去保存された不存在親子群は正常NotFoundとして登録だけを整理し、独立directoryの通常入力を維持します。現在の通常APIが重複登録を作る前提を置きません。</summary>
    [TestMethod]
    [DataRow("Missing")]
    [DataRow("Missing.bms")]
    public async Task ProductionImport_NotFoundOverlapIsPrunedAndIndependentInputContinues(string missingName)
    {
        var port = new ObservingInstallMutationPort();
        await using var fixture = new AutomaticContinuationFixture(false, false, mutationPort: port);
        string independent = fixture.WriteInput("Independent", "normal.bms", "independent-not-found");
        byte[] bytes = File.ReadAllBytes(Path.Combine(independent, "normal.bms"));
        await fixture.InitializeAsync();
        string parent = Path.Combine(fixture.Root, missingName);
        string child = Path.Combine(parent, "Child");
        ChartPackage[] originals = [new ChartPackage { path = parent, delete_parent = true }, new ChartPackage { path = child }];
        SeedLegacyPending(fixture, originals);
        Exception? missingFailure = null;
        try { _ = LongPathFileSystem.GetAttributes(parent); }
        catch (Exception failure) when (LongPathFileSystem.IsNotFoundFailure(failure)) { missingFailure = failure; }
        Assert.IsNotNull(missingFailure, "過去保存群は正常NotFoundであることを実確認します。");
        Assert.IsTrue(fixture.Owner.Enqueue([independent]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(port.LatestResult);
        Assert.IsFalse(port.LatestResult.HasRequiredFailure);
        Assert.IsTrue(port.LatestResult.HasDurableCommit);
        Assert.IsTrue(fixture.Library.ChartPackagesPending.All(package => !originals.Contains(package)));
        CollectionAssert.AreEquivalent(new[] { independent }, fixture.Library.ChartPackagesPending.Select(package => package.path).ToArray());
        CollectionAssert.AreEquivalent(new[] { independent }, new BmsLibraryDbGateway(fixture.Database).LoadInstallPackages().Select(package => package.path).ToArray());
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(Path.Combine(independent, "normal.bms")));
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
    }

    /// <summary>旧版で登録された重複行だけを同じfixtureのDBと公開集合へ用意し、その後は通常の取込み入口で修復します。</summary>
    private static void SeedLegacyPending(AutomaticContinuationFixture fixture, IReadOnlyList<ChartPackage> packages)
    {
        ExecuteSongDbFixtureTransaction(fixture.Database, db =>
        {
            db.CreateTable<LR2SongDBExtended.install>();
            foreach (ChartPackage package in packages) { db.Insert(package, typeof(LR2SongDBExtended.install)); }
        });
        fixture.Library.ChartPackagesPending = packages;
    }

    /// <summary>本番portへ要求と権限・停止継続をそのまま転送し、UI通知を出さない保留正常終端も実Taskの戻り値から観測します。</summary>
    private sealed class ObservingInstallMutationPort : IPackageInstallMutationPort
    {
        private readonly BmsLibraryPackageInstallMutationPort inner = new();
        internal PackageInstallCommandResult? LatestResult { get; private set; }
        internal int CompletedCount { get; private set; }

        /// <summary>同じ共通権限と入力選択を本番portへ渡し、実終端の結果を捕捉します。</summary>
        public Task<PackageInstallCommandResult> InstallUnderAdmissionAsync(BMSLibrary library, IEnumerable<string> paths, CancellationToken token,
            IPackageInstallProgressWriter progress, LibraryFileMutationCapability capability, IReadOnlySet<string>? fullySelectedDirectories = null)
            => CaptureAsync(inner.InstallUnderAdmissionAsync(library, paths, token, progress, capability, fullySelectedDirectories));

        /// <summary>本番の停止・P引渡し継続を保持したまま、同じ取り込みの実終端を観測します。</summary>
        public Task<PackageInstallCommandResult> InstallUnderAdmissionAsync(BMSLibrary library, IEnumerable<string> paths,
            CancellationToken token, IPackageInstallProgressWriter progress, LibraryFileMutationCapability capability,
            Func<Task> beforePhysicalMutation, Action<LibraryFileMutationLease> retainPlaylistLease, IReadOnlySet<string>? fullySelectedDirectories = null)
            => CaptureAsync(inner.InstallUnderAdmissionAsync(library, paths, token, progress, capability, beforePhysicalMutation, retainPlaylistLease, fullySelectedDirectories));

        /// <summary>通常の本番取り込みをそのまま実行し、戻り値だけを保持します。</summary>
        public Task<PackageInstallCommandResult> InstallWithProgressAsync(BMSLibrary library, IEnumerable<string> paths, CancellationToken token,
            IPackageInstallProgressWriter progress, IReadOnlySet<string>? fullySelectedDirectories = null)
            => CaptureAsync(inner.InstallWithProgressAsync(library, paths, token, progress, fullySelectedDirectories));

        private async Task<PackageInstallCommandResult> CaptureAsync(Task<PackageInstallCommandResult> operation)
        {
            PackageInstallCommandResult result = await operation.ConfigureAwait(false);
            LatestResult = result;
            CompletedCount++;
            return result;
        }
    }
}
