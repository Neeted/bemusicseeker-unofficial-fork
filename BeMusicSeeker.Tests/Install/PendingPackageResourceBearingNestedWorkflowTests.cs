using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PendingPackageWorkflowOwnerTests
{
    /// <summary>実発見した禁止一式を親行・子行の選択から解決し、実owner/storeの手動・強制導入で拒否します。正常な別packageの導入と元bytes・install行・所持・受付終端を確認します。</summary>
    [DataTestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, true, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, true)]
    [DataRow(true, true, true)]
    public async Task InstallPendingAsync_ResourceBearingNestedChartsCannotBeBypassedBySelectedParentOrChild(bool manual, bool childRow, bool failNormal)
    {
        await using var fixture = new PackageInstallWorkflowOwnerTests.AutomaticContinuationFixture(false, false);
        string source = fixture.WriteInput("Nested", "parent.bms", "nested-parent");
        string child = fixture.WriteInput(Path.Combine("Nested", "child"), "child.bms", "nested-child");
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [3, 4]);
        File.WriteAllBytes(Path.Combine(child, "sound.wav"), [5, 6]);
        string normal = fixture.WriteInput("Normal", "normal.bms", "independent-normal");
        Dictionary<string, byte[]> bytes = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        await fixture.InitializeAsync();
        Assert.IsTrue(fixture.Owner.Enqueue([source, normal]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure);
        ChartPackage blocked = fixture.Library.ChartPackagesPending.Single(package => package.path == source);
        ChartPackage normalPackage = fixture.Library.ChartPackagesPending.Single(package => package.path == normal);
        foreach (PackageChartEntry entry in blocked.ChartEntries.Concat(normalPackage.ChartEntries)) { entry.SetInstallDestinationPathOnly(fixture.Destination); }
        FakeUiDialogService dialogs = AcceptedDialogs();
        dialogs.ConfirmationResult = UiDialogResult.FromMessageBoxResult(manual ? MessageBoxResult.OK : MessageBoxResult.Yes);
        PendingPackageWorkflowOwner owner = CreateOwner(() => fixture.Library, [], new BmsLibraryPendingPackageStore(), dialogs,
            chartFileOperations: fixture.Library.OperationAdmission, playback: new NoOpChartMutationPlaybackPort());
        PackageChartEntry selected = blocked.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == (childRow ? "child.bms" : "parent.bms"));
        ChartOperationTarget Target(PackageChartEntry entry) => new(entry.Chart, null, ChartOperationSourceScope.PendingPackage,
            false, true, false, ChartOperationCapabilities.UpdateInstallDestination, entry);
        ChartOperationTarget[] targets = childRow
            ? [Target(normalPackage.ChartEntries.Single()), Target(selected), Target(blocked.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "parent.bms"))]
            : [Target(selected), Target(normalPackage.ChartEntries.Single())];
        PendingInstallPackageOperationRequest request = manual ? PendingInstallPackageOperationRequest.CreateManualInstall(targets)
            : PendingInstallPackageOperationRequest.CreateForceInstall(targets);

        if (failNormal)
        {
            Directory.Delete(normal, recursive: true);
            dialogs.MessageResult = UiDialogResult.Failed(new IOException("refusal notification failure"));
        }
        PendingPackageMutationResult result = await owner.InstallPendingAsync(request);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.HasInstallRefusal);
        Assert.AreEqual(1, dialogs.MessageRequests.Count(message => message.MessageBoxText == BeMusicSeeker.Properties.Resources.Warning_ResourceBearingNestedCharts));
        if (failNormal)
        {
            Assert.IsInstanceOfType<FileNotFoundException>(result.Failure);
            Assert.AreSame(result.SessionReceipt.PrimaryFailure, result.Failure);
            Assert.AreEqual(normal, result.SessionReceipt.ItemFailures.Single().Target.SourcePath);
        }
        else { Assert.IsNull(result.Failure); Assert.IsTrue(result.HasDurableCommit); }
        Assert.AreEqual(failNormal ? 2 : 1, fixture.Library.ChartPackagesPending.Count);
        Assert.IsTrue(fixture.Library.ChartPackagesPending.Contains(blocked));
        Assert.IsTrue(blocked.HasResourceBearingNestedCharts);
        Assert.AreEqual(failNormal ? 0 : 1, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(!failNormal, fixture.Library.BmsCharts.Any(chart => Path.GetFileName(chart.Path) == "normal.bms"));
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => Path.GetFileName(chart.Path) is "parent.bms" or "child.bms"));
        foreach ((string path, byte[] before) in bytes) { CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(failNormal ? new[] { source, normal } : new[] { source }, db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
        Assert.AreEqual(!failNormal, db.Table<LR2SongDB.song>().Any(row => Path.GetFileName(row.path) == "normal.bms"));
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>実手動・強制の全拒否を非例外の失敗結果へ接続し、成功0件のreceiptを物理失敗で偽装しません。受付解放後の重要警告一回と元入力・DB保持を確認します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InstallPendingAsync_AllResourceBearingNestedChartsReportNonExceptionalRefusalAfterRelease(bool manual)
    {
        await using var fixture = new PackageInstallWorkflowOwnerTests.AutomaticContinuationFixture(false, false);
        string source = fixture.WriteInput("Nested", "parent.bms", "nested-parent");
        string child = fixture.WriteInput(Path.Combine("Nested", "child"), "child.bms", "nested-child");
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [3, 4]);
        File.WriteAllBytes(Path.Combine(child, "sound.wav"), [5, 6]);
        string second = fixture.WriteInput("Nested2", "parent.bms", "second-parent");
        string secondChild = fixture.WriteInput(Path.Combine("Nested2", "child"), "child.bms", "second-child");
        File.WriteAllBytes(Path.Combine(second, "sound.wav"), [7]);
        File.WriteAllBytes(Path.Combine(secondChild, "sound.wav"), [8]);
        var preserved = new[] { source, second }.SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories)).ToDictionary(path => path, File.ReadAllBytes);
        await fixture.InitializeAsync();
        Assert.IsTrue(fixture.Owner.Enqueue([source, second]));
        await fixture.Owner.WaitForIdleAsync();
        ChartPackage[] packages = fixture.Library.ChartPackagesPending.ToArray();
        Assert.AreEqual(2, packages.Length);
        foreach (PackageChartEntry entry in packages.SelectMany(package => package.ChartEntries)) { entry.SetInstallDestinationPathOnly(fixture.Destination); }
        var dialogs = new FileDbReportRecordingDialogs { OnMessage = () => Assert.IsFalse(fixture.Library.OperationAdmission.IsActive) };
        PendingPackageWorkflowOwner owner = CreateOwner(() => fixture.Library, [], new BmsLibraryPendingPackageStore(), dialogs,
            chartFileOperations: fixture.Library.OperationAdmission, playback: new NoOpChartMutationPlaybackPort());

        PendingPackageMutationResult result = manual ? await owner.ManualInstallPackagesAsync(packages) : await owner.ForceInstallPackagesAsync(packages);

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.HasInstallRefusal);
        Assert.IsNull(result.Failure);
        Assert.AreEqual(0, result.SessionReceipt.ConfirmedChangeCount);
        Assert.IsNull(result.SessionReceipt.PhysicalFailure);
        Assert.AreEqual(0, result.SessionReceipt.ItemFailures.Count);
        Assert.AreEqual(1, dialogs.Messages.Count);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Warning_ResourceBearingNestedCharts, dialogs.Messages.Single().MessageBoxText);
        CollectionAssert.AreEquivalent(packages, fixture.Library.ChartPackagesPending.ToArray());
        foreach ((string path, byte[] before) in preserved) { CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        CollectionAssert.AreEquivalent(new[] { source, second }, db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>非例外拒否の結果合成は、先行成功receiptと既存の物理・反映・終端Failureをそのまま保ちます。実ownerの失敗併存は上の入力消失代表へ分担します。</summary>
    [DataTestMethod]
    [DataRow("none")]
    [DataRow("physical")]
    [DataRow("apply")]
    [DataRow("finalization")]
    public void InstallRefusalResult_PreservesExistingReceiptAndFailure(string failureStage)
    {
        var failure = new IOException("existing failure");
        var receipt = new LibraryMutationSessionReceipt([new LibraryMutationSessionTarget("success-source", "success-destination")], true,
            physicalFailure: failureStage == "physical" ? failure : null,
            applyFailure: failureStage == "apply" ? failure : null,
            finalizationFailure: failureStage == "finalization" ? failure : null);

        PendingPackageMutationResult result = PendingPackageMutationResult.FromTerminal(receipt).WithInstallRefusal();

        Assert.IsFalse(result.Succeeded);
        Assert.IsTrue(result.HasInstallRefusal);
        Assert.AreSame(receipt, result.SessionReceipt);
        Assert.AreEqual(1, result.SessionReceipt.ConfirmedChangeCount);
        Assert.AreSame(failureStage == "none" ? null : failure, result.Failure);
    }

    /// <summary>全既所持の資源上書きと、推定先・全既所持の清掃だけの実経路を同じ元条件で拒否します。清掃0件は発見後に所有mediaを退避して自然に作り、正常別packageの成功は維持します。</summary>
    [DataTestMethod]
    [DataRow("overwrite")]
    [DataRow("overwrite-cleanup")]
    [DataRow("manual-cleanup")]
    public async Task InstalledOnlyAndCleanup_ResourceBearingNestedChartsPreserveOriginalInput(string operation)
    {
        string[] identities = ["nested-parent", "nested-child", "independent-normal"];
        string[] bodies = identities.Select(identity => CreateValidBmsText("Song") + "#GENRE " + identity + "\n").ToArray();
        await using var fixture = new PackageInstallWorkflowOwnerTests.AutomaticContinuationFixture(false, true, installedChartBodies: bodies);
        string source = fixture.WriteInput("Nested", "parent.bms", identities[0]);
        string child = fixture.WriteInput(Path.Combine("Nested", "child"), "child.bms", identities[1]);
        string normal = fixture.WriteInput("Normal", "normal.bms", identities[2]);
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [3, 4]);
        File.WriteAllBytes(Path.Combine(child, "sound.wav"), [5, 6]);
        File.WriteAllBytes(Path.Combine(normal, "sound.wav"), [8]);
        await fixture.InitializeAsync();
        Assert.IsTrue(fixture.Owner.Enqueue([source, normal]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure);
        ChartPackage blocked = fixture.Library.ChartPackagesPending.Single(package => package.path == source);
        ChartPackage normalPackage = fixture.Library.ChartPackagesPending.Single(package => package.path == normal);
        Assert.IsTrue(blocked.HasResourceBearingNestedCharts);
        if (operation != "overwrite")
        {
            string retreat = Path.Combine(fixture.Root, "RetreatedMedia");
            Directory.CreateDirectory(retreat);
            int index = 0;
            foreach (string path in new[] { Path.Combine(source, "sound.wav"), Path.Combine(child, "sound.wav"), Path.Combine(normal, "sound.wav") })
            { File.Move(path, Path.Combine(retreat, (index++) + ".wav")); }
            Assert.IsTrue(Directory.GetFiles(source, "*", SearchOption.AllDirectories).All(path => Path.GetExtension(path) == ".bms"));
        }
        Dictionary<string, byte[]> preserved = Directory.GetFiles(source, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var owned = fixture.Library.BmsCharts.ToDictionary(chart => chart.Path, chart => File.ReadAllBytes(chart.Path));
        foreach (PackageChartEntry entry in blocked.ChartEntries.Concat(normalPackage.ChartEntries)) { entry.SetInstallDestinationPathOnly(fixture.Destination); }
        PendingPackageWorkflowOwner owner = CreateOwner(() => fixture.Library, [], new BmsLibraryPendingPackageStore(), AcceptedDialogs(),
            chartFileOperations: fixture.Library.OperationAdmission, playback: new NoOpChartMutationPlaybackPort(),
            settingsProvider: () => new InstallDestinationWorkflowSettingsSnapshot(false, true, false));

        if (operation == "manual-cleanup") { await owner.ManualInstallPackagesAsync([blocked, normalPackage]); }
        else { await owner.OverwriteInstalledOnlyPendingPackageResourcesAsync(); }

        Assert.AreSame(blocked, fixture.Library.ChartPackagesPending.Single());
        foreach ((string path, byte[] before) in preserved) { CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); }
        foreach ((string path, byte[] before) in owned) { CollectionAssert.AreEqual(before, File.ReadAllBytes(path)); }
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "child", "sound.wav")));
        if (operation == "overwrite") { CollectionAssert.AreEqual(new byte[] { 8 }, File.ReadAllBytes(Path.Combine(fixture.Destination, "sound.wav"))); }
        else { Assert.IsFalse(Directory.Exists(normal), "正常別packageの清掃だけの分岐を実行します。"); }
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(source, db.Table<LR2SongDBExtended.install>().Single().path);
        Assert.AreEqual(owned.Count, fixture.Library.BmsCharts.Count);
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }
    /// <summary>重要警告の実判定値を持つpackageでも、保留行の操作能力と実削除・0note無効改名を維持します。導入禁止を一律の能力剥奪へ広げる誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ResourceBearingNestedCharts_AllowExplicitSourceDeletionAndInvalidExtensionRename(bool rename)
    {
        string root = Path.Combine(Path.GetTempPath(), "nested-resolution-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "Input");
        string child = Path.Combine(source, "child");
        Directory.CreateDirectory(child);
        string parentChart = Path.Combine(source, "parent.bms");
        string childChart = Path.Combine(child, "child.pms");
        File.WriteAllText(parentChart, "#TITLE Parent\n#BPM 120\n#WAV01 tone.wav\n");
        File.WriteAllText(childChart, "#TITLE Child\n#BPM 120\n#WAV01 tone.wav\n");
        File.WriteAllBytes(Path.Combine(source, "tone.wav"), [1, 2]);
        File.WriteAllBytes(Path.Combine(child, "tone.wav"), [3, 4]);
        TestBmsLibrary? library = null;
        try
        {
            ChartPackage package = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([source], [], [], _ => false).DiscoveredPackages.Single();
            Assert.IsTrue(package.HasResourceBearingNestedCharts);
            foreach (PackageChartEntry entry in package.ChartEntries)
            {
                Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(LibraryChartRow.FromPackageChartEntry(entry), ChartOperationSourceScope.PendingPackage, out ChartOperationTarget target));
                Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.UpdateInstallDestination));
                Assert.IsTrue(target.HasCapability(ChartOperationCapabilities.RenameInvalidExtension));
            }
            string installed = Path.Combine(root, "Library");
            Directory.CreateDirectory(installed);
            ChartFile[] owned = rename ? [] : package.ChartEntries.Select((entry, index) =>
            {
                string path = Path.Combine(installed, index + Path.GetExtension(entry.Chart.Path));
                File.Copy(entry.Chart.Path, path);
                return PackageChartEntry.FromPath(path).Chart;
            }).ToArray();
            library = CreateNormalPendingLibrary(root, [package], owned);
            new BmsLibraryDbGateway(Path.Combine(root, "song.db")).UpsertSongs(owned);
            PendingPackageWorkflowOwner owner = CreateOwner(() => library, [], new BmsLibraryPendingPackageStore(), AcceptedDialogs(),
                chartFileOperations: library.OperationAdmission, playback: new NoOpChartMutationPlaybackPort());

            if (rename) { await owner.RenamePendingZeroNoteChartsAsync(); }
            else { await owner.DeleteInstalledOnlyPendingPackageSourcesAsync(); }

            Assert.IsFalse(File.Exists(parentChart));
            Assert.IsFalse(File.Exists(childChart));
            if (rename)
            {
                Assert.IsTrue(File.Exists(Path.ChangeExtension(parentChart, ".bmx")));
                Assert.IsTrue(File.Exists(Path.ChangeExtension(childChart, ".pmx")));
                CollectionAssert.AreEqual(new byte[] { 1, 2 }, File.ReadAllBytes(Path.Combine(source, "tone.wav")));
                CollectionAssert.AreEqual(new byte[] { 3, 4 }, File.ReadAllBytes(Path.Combine(child, "tone.wav")));
                Assert.IsTrue(package.HasResourceBearingNestedCharts, "解消操作に新しい再捕捉を追加しません。");
            }
            else
            {
                Assert.IsFalse(Directory.Exists(source));
                Assert.AreEqual(0, library.ChartPackagesPending.Count);
            }
            Assert.AreEqual(owned.Length, library.BmsCharts.Count);
            Assert.IsFalse(library.OperationAdmission.IsActive);
        }
        finally { library?.RequestShutdown("nested-resolution-fixture"); Directory.Delete(root, recursive: true); }
    }

}
