using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BeMusicSeeker.Tests.BmsLibraryInitializationTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>通常発見したrootと子の全hash未所持packageを共有事前集約から逆引き推定し、root同梱と子候補の和集合で階層を保って導入・DB確定します。</summary>
    [TestMethod]
    public async Task AutomaticContinuation_NestedUnownedPackageUsesPlacedResourceRequests()
    {
        await using var fixture = new AutomaticContinuationFixture(true, false, destinationSounds: [Path.Combine("sub", "kick.wav")]);
        string source = fixture.WriteInput("Input", "root.bms", "unowned-root");
        string childDirectory = fixture.WriteInput(Path.Combine("Input", "sub"), "child.bms", "unowned-child");
        string[] sourceCharts = [Path.Combine(source, "root.bms"), Path.Combine(childDirectory, "child.bms")];
        foreach (string path in sourceCharts) { File.WriteAllText(path, File.ReadAllText(path).Replace("sound.wav", "kick.wav"), Encoding.ASCII); }
        File.WriteAllBytes(Path.Combine(source, "kick.wav"), [2]);
        byte[][] sourceBytes = sourceCharts.Select(File.ReadAllBytes).ToArray();
        await fixture.InitializeAsync();
        string ownedHash = fixture.Library.BmsCharts.Single().Md5;
        Assert.IsTrue(sourceCharts.All(path => PackageChartEntry.FromPath(path).Chart.Md5 != ownedHash));

        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();

        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(0, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        string[] destinations = [Path.Combine(fixture.Destination, "root.bms"), Path.Combine(fixture.Destination, "sub", "child.bms")];
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        for (int index = 0; index < destinations.Length; index++)
        {
            CollectionAssert.AreEqual(sourceBytes[index], File.ReadAllBytes(destinations[index]));
            Assert.IsNotNull(db.Find<LR2SongDB.song>(destinations[index]));
            Assert.IsTrue(fixture.Library.BmsCharts.Any(chart => chart.Path == destinations[index]));
        }
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "kick.wav")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "sub", "kick.wav")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "child.bms")));
        Assert.AreEqual(0, db.Table<LR2SongDBExtended.install>().Count());
        Assert.IsNotNull(fixture.Result);
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt?.DurableCommit == true);
    }

    /// <summary>既所持の親譜面から解決した候補でも、子譜面の実配置で音源閾値を満たさなければ元FS・保留・installと候補を保ち、子項目の健康度0・不足警告へ揃えます。子配置に音源がある場合だけ階層を保って導入し、song・所持集合へ反映します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticContinuation_NestedChartUsesFinalAudioPlacement(bool audioInChild)
    {
        await using var fixture = new AutomaticContinuationFixture(true, false, nestedAudio: audioInChild);
        string child = fixture.WriteInput(Path.Combine("Input", "sub"), "new.bms", "new");
        string source = Path.Combine(fixture.Root, "Input");
        string existingSource = Path.Combine(source, "owned.bms");
        File.Copy(Path.Combine(fixture.Destination, "owned.bms"), existingSource);
        string sourceChart = Path.Combine(child, "new.bms");
        byte[] sourceBytes = File.ReadAllBytes(sourceChart);
        byte[] existingBytes = File.ReadAllBytes(existingSource);
        await fixture.InitializeAsync();

        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();

        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(audioInChild ? 0 : 1, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(audioInChild ? 1 : 0, fixture.Library.ChartPackagesInstalled.Count);
        string destinationChart = Path.Combine(fixture.Destination, "sub", "new.bms");
        Assert.AreEqual(audioInChild, File.Exists(destinationChart));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "new.bms")), "子譜面を候補直下へ平坦化しません。");
        Assert.AreEqual(audioInChild, fixture.Library.BmsCharts.Any(chart => chart.Path == destinationChart));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(audioInChild, db.Find<LR2SongDB.song>(destinationChart) != null);
        Assert.AreEqual(audioInChild ? 0 : 1, db.Table<LR2SongDBExtended.install>().Count());
        if (audioInChild)
        {
            Assert.IsNotNull(fixture.Result);
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(destinationChart));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "sub", "sound.wav")));
            Assert.IsTrue(fixture.Result.EstimatedInstallReceipt?.DurableCommit == true);
        }
        else
        {
            Assert.AreEqual(source, fixture.Library.ChartPackagesPending.Single().path);
            PackageChartEntry pendingChild = fixture.Library.ChartPackagesPending.Single().ChartEntries.Single(entry => entry.Chart.Path == sourceChart);
            Assert.AreEqual(1, pendingChild.Chart.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, pendingChild.Chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(0, pendingChild.Chart.WAVHealth);
            Assert.IsTrue(pendingChild.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            CollectionAssert.AreEqual(new[] { fixture.Destination }, pendingChild.Chart.InstallDestinationSuggestions.ToArray());
            Assert.AreEqual(source, db.Table<LR2SongDBExtended.install>().Single().path);
            CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(sourceChart));
            CollectionAssert.AreEqual(existingBytes, File.ReadAllBytes(existingSource));
            // 導入0件の正常終端は完了通知を発行しないため、保留・DB・FSを上で直接観測します。
            Assert.IsNull(fixture.Result);
        }
    }

    /// <summary>開始時の自動導入・元削除設定を推定中の変更から分離し、再グループ化後のディレクトリと同梱物を導入します。次操作だけが変更後設定を使います。</summary>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, false)]
    public async Task AutomaticContinuation_UsesCapturedSettingsAndRegroupedSource(bool enabled, bool deleteSource)
    {
        await using var fixture = new AutomaticContinuationFixture(enabled, deleteSource);
        string source = fixture.WriteInput("Input", "a.bms", "a");
        fixture.WriteInput("Input", "b.bms", "b");
        foreach (string name in new[] { "a", "b" })
        {
            string path = Path.Combine(source, name + ".bms");
            File.WriteAllText(path, File.ReadAllText(path).Replace("sound.wav", name + ".wav"), Encoding.ASCII);
        }
        File.WriteAllText(Path.Combine(source, "owned.bms"), CreateValidBmsText("Song"), Encoding.ASCII);
        // 分割公開を実観測してから再グループ化後の一ディレクトリ導入を確認します。
        int splitCount = 0;
        fixture.Library.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(BMSLibrary.ChartPackagesPending)
                && fixture.Library.ChartPackagesPending.Count == 3) { Interlocked.Increment(ref splitCount); }
        };
        File.WriteAllBytes(Path.Combine(source, "extra.wav"), [2]);
        File.WriteAllBytes(Path.Combine(source, "image.png"), [3]);
        fixture.Observer.Applied = () => fixture.Options = fixture.CreateOptions(!enabled, !deleteSource);
        await fixture.InitializeAsync();
        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsTrue(splitCount > 0);
        Assert.AreEqual(enabled ? 0 : 1, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(enabled ? 1 : 0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(!enabled || !deleteSource, Directory.Exists(source));
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.AreEqual(enabled ? 0 : 1, db.Table<LR2SongDBExtended.install>().Count());
            foreach (string name in new[] { "a.bms", "b.bms" })
            {
                string destination = Path.Combine(fixture.Destination, name);
                Assert.AreEqual(enabled, File.Exists(destination));
                Assert.AreEqual(enabled, db.Find<LR2SongDB.song>(destination) != null);
                Assert.AreEqual(enabled, fixture.Library.BmsCharts.Any(chart => chart.Path == destination));
            }
        }
        if (enabled)
        {
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "extra.wav")));
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "image.png")));
            Assert.IsNotNull(fixture.Result?.EstimatedInstallReceipt);
            Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        }
        else { Assert.AreEqual(source, fixture.Library.ChartPackagesPending.Single().path); }
        fixture.Observer.Applied = null;
        string next = fixture.WriteInput("Next", "next.bms", "next");
        Assert.IsTrue(fixture.Owner.Enqueue([next]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(!enabled, File.Exists(Path.Combine(fixture.Destination, "next.bms")));
        Assert.AreEqual(enabled || deleteSource, Directory.Exists(next));
    }

    /// <summary>初段保留からの後段書込みも停止の実Taskを待ち、取消は開始した一パッケージを完了して次を保留に残します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticContinuation_WaitsForStopAndCancelsOnlyBetweenPackages(bool lr2)
    {
        await using var fixture = new AutomaticContinuationFixture(true, true, holdPlayback: true);
        string first = fixture.WriteInput("First", "a.bms", "a");
        string second = fixture.WriteInput("Second", "b.bms", "b");
        int cancelled = 0;
        string? processedSource = null;
        fixture.Files.BeforeCopy = path =>
        {
            if ((path.StartsWith(first, StringComparison.OrdinalIgnoreCase) || path.StartsWith(second, StringComparison.OrdinalIgnoreCase))
                && Interlocked.Exchange(ref cancelled, 1) == 0)
            {
                processedSource = path.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? first : second;
                fixture.Owner.CancelAll();
            }
        };
        await fixture.InitializeAsync();
        fixture.Options = fixture.CreateOptions(true, true, lr2: lr2, managed: lr2 ? fixture.Destination : null);
        Assert.IsTrue(fixture.Owner.Enqueue([first, second]));
        Task idle = fixture.Owner.WaitForIdleAsync();
        await Task.WhenAny(fixture.PlaybackEntered.Task, idle);
        Assert.IsTrue(fixture.PlaybackEntered.Task.IsCompletedSuccessfully);
        Assert.IsFalse(idle.IsCompleted);
        Assert.IsTrue(fixture.Library.OperationAdmission.IsActive);
        Assert.AreEqual(lr2, fixture.Library.Lr2Synchronization.PlaylistOperationAdmission.IsActive, "後段で必要なPを先に取得し、停止Task中も保持します。");
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "a.bms")));
        Assert.IsFalse(fixture.Owner.Enqueue([second]));
        fixture.ReleasePlayback.TrySetResult();
        await idle;
        Assert.AreEqual(1, cancelled);
        Assert.IsNotNull(processedSource);
        string remaining = processedSource == first ? second : first;
        string successfulChart = processedSource == first ? "a.bms" : "b.bms";
        string remainingChart = processedSource == first ? "b.bms" : "a.bms";
        Assert.IsNotNull(fixture.Failure);
        Assert.IsNotNull(fixture.Result);
        Assert.IsTrue(fixture.Result.SessionReceipt.DurableCommit);
        Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt);
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Result.UnprocessedPackages.Count);
        Assert.IsTrue(string.Equals(remaining, fixture.Result.UnprocessedPackages.Single().path, StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, successfulChart)));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, remainingChart)));
        Assert.IsFalse(Directory.Exists(processedSource));
        Assert.IsTrue(File.Exists(Path.Combine(remaining, remainingChart)));
        Assert.IsTrue(string.Equals(fixture.Destination, fixture.Library.ChartPackagesPending.Single().ChartEntries.Single().Chart.InstallDestination, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(1, fixture.Result.RegisteredPackages.Count);
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.IsNotNull(db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, successfulChart)));
        Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, remainingChart)));
        Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
        Assert.IsFalse(fixture.Result.SynchronizationIncomplete);
        Assert.AreEqual(lr2 ? 1 : 0, fixture.Library.Lr2SongDbSyncCompletedVersion);
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsFalse(fixture.Library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
    }

    /// <summary>後段DB失敗では全体を未確定に、確定後の保守失敗ではdurable事実を保ち、初段の通常導入・回復対象・安全条件による未同期を失いません。対象ごとのcommitと不完全成功公開への退行を検出します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AutomaticContinuation_RetainsFirstStageWhenSecondStageTransactionFails(bool finalizationFails)
    {
        await using var fixture = new AutomaticContinuationFixture(true, false);
        fixture.Options = fixture.CreateOptions(true, false, keepNewPending: false);
        string normal = fixture.WriteInput("Normal", "normal.bms", "normal");
        File.WriteAllText(Path.Combine(normal, "normal.bms"), CreateValidBmsText("NewSong"), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(normal, "sound.wav"), [1]);
        string first = fixture.WriteInput("First", "a.bms", "a");
        string second = fixture.WriteInput("Second", "b.bms", "b");
        await fixture.InitializeAsync();
        if (finalizationFails) { fixture.Options = fixture.CreateOptions(true, false, keepNewPending: false, lr2: true); }
        ExecuteSongDbFixtureTransaction(fixture.Database, db => db.Execute(
            "CREATE TRIGGER fail_automatic_continuation BEFORE INSERT ON " + (finalizationFails ? "maintenance" : "song") + " WHEN NEW.path = '"
            + Path.Combine(fixture.Destination, "b.bms").Replace("'", "''")
            + "' " + (finalizationFails ? "AND NEW.wav_files_existing = 1 " : "")
            + "BEGIN SELECT RAISE(ABORT, 'automatic-continuation-transaction'); END;"));
        Assert.IsTrue(fixture.Owner.Enqueue([normal, first, second]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(fixture.Result);
        Assert.IsTrue(fixture.Result.SessionReceipt.DurableCommit);
        Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt);
        Assert.AreEqual(finalizationFails, fixture.Result.EstimatedInstallReceipt.DurableCommit);
        StringAssert.Contains(fixture.Result.EstimatedInstallReceipt.PrimaryFailure.ToString(), "automatic-continuation-transaction");
        Assert.AreEqual(finalizationFails, fixture.Result.HasDurableFinalizationFailure);
        Assert.AreEqual(finalizationFails, fixture.Result.SynchronizationIncomplete);
        Assert.AreEqual(0, fixture.Library.Lr2SongDbSyncRequestedVersion, "必須反映に失敗した状態の同期を迂回しません。");
        Assert.IsTrue(fixture.Result.HasDurableCommit);
        Assert.IsTrue(fixture.Result.HasRequiredFailure);
        Assert.AreEqual(1, fixture.Result.RegisteredPackages.Count);
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(2, fixture.Library.ChartPackagesPending.Count);
        Assert.IsTrue(fixture.Result.RecoveryPaths.Any(path => path.StartsWith(fixture.Destination, StringComparison.OrdinalIgnoreCase)));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(1, db.Table<LR2SongDB.song>().Count(row => row.path.EndsWith("normal.bms", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(finalizationFails, db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "a.bms")) != null);
        Assert.AreEqual(finalizationFails, db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "b.bms")) != null);
        Assert.AreEqual(finalizationFails ? 0 : 2, db.Table<LR2SongDBExtended.install>().Count());
    }

    /// <summary>同じ元へ再結合される以前の手動設定項目を、今回の自動確定へ取り込みません。全体を保留し、今回の兄弟の抜取り導入も行いません。</summary>
    [TestMethod]
    public async Task AutomaticContinuation_DoesNotExpandIntoUntrackedRegroupedSibling()
    {
        await using var fixture = new AutomaticContinuationFixture(true, false);
        string source = fixture.WriteInput("Input", "new.bms", "new");
        fixture.WriteInput("Input", "previous.bms", "previous");
        string previousFile = Path.Combine(source, "previous.bms");
        File.WriteAllText(previousFile, File.ReadAllText(previousFile).Replace("sound.wav", "previous.wav"), Encoding.ASCII);
        await fixture.InitializeAsync();
        string previousPath = Path.Combine(source, "previous.bms");
        ChartPackage previous = ChartPackageTestExtensions.CreatePackage(
            ChartPackageTestExtensions.CreateEntryWithInstallDestination(
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(previousPath)), fixture.Destination));
        previous.path = previousPath;
        fixture.Library.ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>([previous]);
        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.AreEqual(1, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(source, fixture.Library.ChartPackagesPending.Single().path);
        Assert.AreEqual(2, fixture.Library.ChartPackagesPending.Single().ChartEntries.Count);
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "new.bms")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "previous.bms")));
        Assert.IsNull(fixture.Result?.EstimatedInstallReceipt);
    }

    /// <summary>交差するXだけをBusyで保留し、独立A/Bを一バッチで確定します。必要同期の実P競合は必須失敗と同期未完了案内を保ち、P解放後もXや同期を再実行しません。</summary>
    [TestMethod]
    public async Task AutomaticContinuation_ExcludesOnlyPlaylistBusyTargetWithoutReplay()
    {
        await using var fixture = new AutomaticContinuationFixture(true, false);
        string crossing = fixture.WriteInput(Path.Combine("Managed", "X"), "x.bms", "x");
        string first = fixture.WriteInput("A", "a.bms", "a");
        string second = fixture.WriteInput("B", "b.bms", "b");
        await fixture.InitializeAsync();
        fixture.Options = fixture.CreateOptions(true, false, lr2: true, managed: Path.Combine(fixture.Root, "Managed"));
        Assert.IsTrue(fixture.Library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable held));
        try
        {
            Assert.IsTrue(fixture.Owner.Enqueue([crossing, first, second]));
            await fixture.Owner.WaitForIdleAsync();
            Assert.IsNotNull(fixture.Result, fixture.Failure?.Exception.ToString());
            Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt, fixture.Result.EstimationFailure?.ToString());
            Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
            Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.InstalledTargetApplyCount);
            Assert.IsTrue(string.Equals(crossing, fixture.Result.UnprocessedPackages.Single().path, StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(string.Equals(crossing, fixture.Library.ChartPackagesPending.Single().path, StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(2, fixture.Library.ChartPackagesInstalled.Count);
            Assert.IsTrue(fixture.Result.SynchronizationIncomplete, "末尾の同期も外部Pを迂回しません。");
            Assert.IsTrue(fixture.Result.HasRequiredFailure);
            UiMessageRequest? report = FileDbMutationReport.CreatePackageInstall("playlist-busy-install", fixture.Result);
            Assert.IsNotNull(report);
            Assert.AreEqual(System.Windows.MessageBoxImage.Hand, report.Icon);
            StringAssert.Contains(report.MessageBoxText, BeMusicSeeker.Properties.Resources.PackageInstallReport_SynchronizationIncomplete);
            Assert.AreEqual(0, fixture.Library.Lr2SongDbSyncRequestedVersion);
            Assert.AreEqual(0, fixture.Library.Lr2SongDbSyncCompletedVersion);
        }
        finally { held.Dispose(); }
        Assert.IsTrue(File.Exists(Path.Combine(crossing, "x.bms")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "x.bms")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "a.bms")));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "b.bms")));
        Assert.AreEqual(1, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(0, fixture.Library.Lr2SongDbSyncRequestedVersion, "P解放は必要同期を自動再開しません。");
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
        Assert.IsFalse(fixture.Library.Lr2Synchronization.PlaylistOperationAdmission.IsActive);
    }

    /// <summary>実取り込みから実coordinator/SQLiteへ一回同期し、推定失敗・取消や後段の境界取消でも安全に確定した分を失いません。同期失敗も元原因を保持します。正常行は同じ初期化で次の取り込みも実行し、Completed省略を成功結果・非エラー案内へ接続します。</summary>
    [TestMethod]
    [DataRow(0)] // 正常後段。
    [DataRow(1)] // 初段成功後の推定失敗。
    [DataRow(2)] // 初段成功後の推定取消。
    [DataRow(3)] // 後段の先行成功後の取消。
    [DataRow(4)] // 推定失敗と同期失敗の重畳。
    [DataRow(5)] // 補償成功の物理失敗でも成功prefixを同期。
    public async Task AutomaticContinuation_SynchronizesCommittedStagesAtTerminal(int failureKind)
    {
        await using var fixture = new AutomaticContinuationFixture(true, false);
        string normal = fixture.WriteInput("Normal", "normal.bms", "normal");
        File.WriteAllText(Path.Combine(normal, "normal.bms"), CreateValidBmsText("NewSong"), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(normal, "sound.wav"), [1]);
        string first = fixture.WriteInput("First", "a.bms", "a");
        string second = fixture.WriteInput("Second", "b.bms", "b");
        await fixture.InitializeAsync();
        fixture.Options = fixture.CreateOptions(true, false, keepNewPending: false, lr2: true);
        var expected = new IOException("automatic-estimation-before-sync");
        if (failureKind is 1 or 4) { fixture.Observer.Applied = () => throw expected; }
        if (failureKind == 2) { fixture.Observer.Applied = fixture.Owner.CancelAll; }
        int cancelled = 0;
        string successfulChart = "a.bms";
        string? firstPhysicalSource = null;
        if (failureKind == 3)
        {
            fixture.Files.BeforeCopy = path =>
            {
                if ((path.StartsWith(first, StringComparison.OrdinalIgnoreCase) || path.StartsWith(second, StringComparison.OrdinalIgnoreCase))
                    && Interlocked.Exchange(ref cancelled, 1) == 0)
                {
                    successfulChart = path.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? "a.bms" : "b.bms";
                    fixture.Owner.CancelAll();
                }
            };
        }
        if (failureKind == 5)
        {
            fixture.Files.BeforeCopy = path =>
            {
                string? source = path.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? first
                    : path.StartsWith(second, StringComparison.OrdinalIgnoreCase) ? second : null;
                if (source == null) { return; }
                firstPhysicalSource ??= source;
                successfulChart = firstPhysicalSource == first ? "a.bms" : "b.bms";
                if (source != firstPhysicalSource) { throw new IOException("automatic-second-package-physical"); }
            };
        }
        if (failureKind == 4)
        {
            ExecuteSongDbFixtureTransaction(fixture.Database, db =>
            {
                db.CreateTable<LR2SongDBExtended.lr2_song_db_sync_status>();
                db.Execute("CREATE TRIGGER fail_automatic_terminal_sync BEFORE INSERT ON lr2_song_db_sync_status WHEN NEW.status = 'Completed' "
                    + "BEGIN SELECT RAISE(ABORT, 'automatic-terminal-sync'); END;");
            });
        }
        Assert.IsTrue(fixture.Owner.Enqueue([normal, first, second]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNotNull(fixture.Result);
        Assert.IsTrue(fixture.Result.SessionReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Library.Lr2SongDbSyncRequestedVersion);
        Assert.AreEqual(failureKind == 4, fixture.Result.SynchronizationIncomplete);
        if (failureKind != 4)
        {
            Assert.AreEqual(1, fixture.Library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status);
        }
        if (failureKind is 1 or 4) { Assert.AreSame(expected, fixture.Result.EstimationFailure); }
        if (failureKind == 4)
        {
            Assert.IsTrue(fixture.Result.WorkflowFailures.Any(failure => failure.ToString().Contains("automatic-terminal-sync", StringComparison.Ordinal)));
        }
        using (LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            Assert.IsNotNull(db.Table<LR2SongDB.song>().Single(row => row.path.EndsWith("normal.bms", StringComparison.OrdinalIgnoreCase)));
            foreach (string name in new[] { "a.bms", "b.bms" })
            {
                bool installed = failureKind == 0 || (failureKind is 3 or 5 && name == successfulChart);
                Assert.AreEqual(installed, db.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, name)) != null);
            }
            Assert.IsTrue(db.Table<LR2SongDB.folder>().Any(row => row.path.TrimEnd(Path.DirectorySeparatorChar) == fixture.Destination));
            if (failureKind == 5)
            {
                Assert.AreEqual(2, fixture.Result.RegisteredPackages.Count);
                Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt);
                Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt.PhysicalFailure);
                Assert.IsFalse(fixture.Result.ManualRecoveryRequired);
            }
        }
        if (failureKind == 0)
        {
            PackageInstallCommandResult firstResult = fixture.Result;
            string followup = fixture.WriteInput("Followup", "followup.bms", "followup");
            Assert.IsTrue(fixture.Owner.Enqueue([followup]));
            await fixture.Owner.WaitForIdleAsync();
            Assert.IsNotNull(fixture.Result);
            Assert.AreNotSame(firstResult, fixture.Result, "二回目の実終端結果を観測します。");
            Assert.IsNull(fixture.Failure);
            Assert.IsFalse(fixture.Result.SynchronizationIncomplete);
            Assert.IsFalse(fixture.Result.HasRequiredFailure);
            Assert.IsNull(fixture.Result.EstimationFailure);
            Assert.AreEqual(0, fixture.Result.WorkflowFailures.Count);
            Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt);
            Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
            Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "followup.bms")));
            using (LR2SongDBExtended verify = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
            { Assert.IsNotNull(verify.Find<LR2SongDB.song>(Path.Combine(fixture.Destination, "followup.bms"))); }
            Assert.AreEqual(1, fixture.Library.Lr2SongDbSyncRequestedVersion, "署名有効のCompletedでは全体同期を新規実行しません。");
            Assert.AreEqual(1, fixture.Library.Lr2SongDbSyncCompletedVersion);
            Assert.AreEqual(Lr2SongDbSyncStatusKind.Completed, fixture.Library.GetLr2SongDbSyncStatusSnapshot().Status);
            Assert.IsNull(FileDbMutationReport.CreatePackageInstall("completed-noop-install", fixture.Result));
        }
        Assert.IsFalse(fixture.Library.OperationAdmission.IsActive);
    }

    /// <summary>後段開始時は未所持の二候補でも、先行成功した同hashを二重導入せず後続を片付けのみへ接続します。別入力元の以前から設定済みの未所持保留は、今回の自動確定へ含めず実元・推定先・保留DB行を維持します。</summary>
    [TestMethod]
    public async Task AutomaticContinuation_ReclassifiesLaterOwnedCandidateInsideSingleBatch()
    {
        await using var fixture = new AutomaticContinuationFixture(false, true);
        string existingSource = fixture.WriteInput("ExistingPending", "previous.bms", "independent-previous");
        string existingFile = Path.Combine(existingSource, "previous.bms");
        byte[] existingBytes = File.ReadAllBytes(existingFile);
        string first = fixture.WriteInput("First", "shared.bms", "same");
        string second = fixture.WriteInput("Second", "shared.bms", "same");
        await fixture.InitializeAsync();
        // 先の実取り込みで設定済みの保留DB行を作り、次のON操作が今回外を遡及導入しないことを確認します。
        Assert.IsTrue(fixture.Owner.Enqueue([existingSource]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        ChartPackage existingPending = fixture.Library.ChartPackagesPending.Single();
        string existingPendingPath = existingPending.path;
        string existingDestination = existingPending.ChartEntries.Single().Chart.InstallDestination;
        string existingHash = existingPending.ChartEntries.Single().Chart.Md5;
        Assert.AreEqual(fixture.Destination, existingDestination);
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => chart.Md5 == existingHash));
        LR2SongDBExtended.install originalRow;
        using (LR2SongDBExtended before = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly())
        {
            originalRow = before.Table<LR2SongDBExtended.install>().Single();
            Assert.AreEqual(existingPendingPath, originalRow.path);
            Assert.AreEqual(0, before.Table<LR2SongDB.song>().Count(row => row.hash == existingHash));
        }
        fixture.Options = fixture.CreateOptions(true, true);
        Assert.IsTrue(fixture.Owner.Enqueue([first, second]));
        await fixture.Owner.WaitForIdleAsync();
        Assert.IsNull(fixture.Failure, fixture.Failure?.Exception.ToString());
        Assert.IsNotNull(fixture.Result);
        Assert.IsNotNull(fixture.Result.EstimatedInstallReceipt);
        Assert.IsTrue(fixture.Result.EstimatedInstallReceipt.DurableCommit);
        Assert.AreEqual(1, fixture.Result.EstimatedInstallReceipt.ApplyCounts.InstalledTargetApplyCount);
        ChartPackage remaining = fixture.Library.ChartPackagesPending.Single();
        Assert.AreEqual(existingPendingPath, remaining.path);
        Assert.AreEqual(existingDestination, remaining.ChartEntries.Single().Chart.InstallDestination);
        Assert.AreEqual(existingHash, remaining.ChartEntries.Single().Chart.Md5);
        Assert.AreEqual(1, fixture.Library.ChartPackagesInstalled.Count);
        Assert.AreEqual(1, fixture.Result.RegisteredPackages.Count);
        Assert.IsFalse(Directory.Exists(first));
        Assert.IsFalse(Directory.Exists(second));
        Assert.IsTrue(File.Exists(Path.Combine(fixture.Destination, "shared.bms")));
        CollectionAssert.AreEqual(existingBytes, File.ReadAllBytes(existingFile));
        Assert.IsFalse(File.Exists(Path.Combine(existingDestination, "previous.bms")));
        Assert.IsFalse(fixture.Library.BmsCharts.Any(chart => chart.Md5 == existingHash));
        using LR2SongDBExtended db = new BmsLibraryDbGateway(fixture.Database).OpenSongDbReadOnly();
        Assert.AreEqual(1, db.Table<LR2SongDB.song>().Count(row => row.path.EndsWith("shared.bms", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(0, db.Table<LR2SongDB.song>().Count(row => row.hash == existingHash));
        LR2SongDBExtended.install remainingRow = db.Table<LR2SongDBExtended.install>().Single();
        Assert.AreEqual(originalRow.path, remainingRow.path);
        Assert.AreEqual(originalRow.delete_parent, remainingRow.delete_parent);
    }

    /// <summary>新設定ONでも手動推定の実適用だけからは導入を開始せず、保留と元ファイルを維持します。</summary>
    [TestMethod]
    public async Task AutomaticContinuation_DoesNotRunAfterManualEstimation()
    {
        await using var fixture = new AutomaticContinuationFixture(false, false);
        string source = fixture.WriteInput("Manual", "manual.bms", "manual");
        await fixture.InitializeAsync();
        Assert.IsTrue(fixture.Owner.Enqueue([source]));
        await fixture.Owner.WaitForIdleAsync();
        ChartPackage pending = fixture.Library.ChartPackagesPending.Single();
        fixture.Library.RemoveInstallDestination(pending.ChartEntries, pending);
        fixture.Options = fixture.CreateOptions(true, false);
        fixture.Library.SearchEstimatedInstallationDirectory(pending);
        Assert.AreEqual(fixture.Destination, pending.ChartEntries.Single().Chart.InstallDestination);
        Assert.AreEqual(1, fixture.Library.ChartPackagesPending.Count);
        Assert.AreEqual(0, fixture.Library.ChartPackagesInstalled.Count);
        Assert.IsTrue(File.Exists(Path.Combine(source, "manual.bms")));
        Assert.IsFalse(File.Exists(Path.Combine(fixture.Destination, "manual.bms")));
    }

    internal sealed class AutomaticContinuationObserver : IInstallEstimationExecutionObserver
    {
        internal Action? Applied { get; set; }
        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation) => new EmptyScope();
        public void ObserveProgress(InstallEstimationProgressObservation observation) { }
        public void ObserveResultApplied(InstallEstimationAppliedObservation observation) => Applied?.Invoke();
        private sealed class EmptyScope : IDisposable { public void Dispose() { } }
    }

    /// <summary>本番ownerと実推定・実FS/SQLiteを接続し、設定と待合せの寿命を操作終端まで所有する局所fixtureです。</summary>
    internal sealed class AutomaticContinuationFixture : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "automatic-continuation-" + Guid.NewGuid().ToString("N"));
        internal string LibraryRoot => Path.Combine(Root, "Library");
        internal string Destination => Path.Combine(LibraryRoot, "Song");
        internal string Database => Path.Combine(Root, "song.db");
        private readonly Lr2ConfigOwner lr2Owner;
        private BmsLibraryOptionsSnapshot options;

        /// <summary>既存fixtureの設定切替を、正式ownerの公開配置へも同じ接点で反映します。受理済みsnapshotは変更しません。</summary>
        internal BmsLibraryOptionsSnapshot Options
        {
            get => options;
            set
            {
                lr2Owner.Publish(new Lr2RootChange([], []), value.CaptureOutputPlacement());
                options = value;
            }
        }
        internal AutomaticContinuationObserver Observer { get; } = new();
        internal ArchiveInputFileMutationService Files { get; } = new(_ => { });
        internal TaskCompletionSource PlaybackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleasePlayback { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TestBmsLibrary Library { get; }
        internal PackageInstallWorkflowOwner Owner { get; }
        internal PackageInstallFailure? Failure { get; private set; }
        internal PackageInstallCommandResult? Result { get; private set; }
        private readonly ConcurrentQueue<Func<Task>> background = new();

        /// <summary>実FSと捕捉する走査結果を同じ内容で準備し、必要な場合だけ最終子配置にも音源を置きます。</summary>
        internal AutomaticContinuationFixture(bool enabled, bool deleteSource, bool holdPlayback = false,
            ChartFileOperationSynchronizer? operationAdmission = null, ChartFileOperationSynchronizer? playlistOperationAdmission = null, IUiScheduler? uiScheduler = null, bool nestedAudio = false,
            IEnumerable<string>? destinationSounds = null)
        {
            Directory.CreateDirectory(Destination);
            string installed = Path.Combine(Destination, "owned.bms");
            File.WriteAllText(installed, CreateValidBmsText("Song"), Encoding.ASCII);
            List<string> sounds = [.. destinationSounds ?? ["sound.wav", "a.wav", "b.wav", "previous.wav"]];
            if (nestedAudio)
            {
                Directory.CreateDirectory(Path.Combine(Destination, "sub"));
                sounds.Add(Path.Combine("sub", "sound.wav"));
            }
            foreach (string sound in sounds)
            {
                string path = Path.Combine(Destination, sound);
                if (Path.GetDirectoryName(path) is string directory) { Directory.CreateDirectory(directory); }
                File.WriteAllBytes(path, [1]);
            }
            File.WriteAllBytes(Database, []);
            options = CreateOptions(enabled, deleteSource);
            lr2Owner = new Lr2ConfigOwner(BmsPlaylistTestSupport.CreateLr2Config(Root, LibraryRoot));
            ChartFileOperationSynchronizer admission = operationAdmission ?? new ChartFileOperationSynchronizer();
            Library = new TestBmsLibrary(Database, lr2Owner, null, Files, new FileDbReportRecordingDialogs(),
                uiScheduler ?? new TestUiScheduler(() => null), () => Options,
                CapturedChartFileScanner.FromFixture([installed], new Dictionary<string, IEnumerable<string>> { [Destination] = sounds }, [Destination]),
                Observer, admission, playlistOperationAdmission, rootFileEnumerator: new FastRootFileEnumerator(), settings: TestBmsFactory.CreateRootSettings([LibraryRoot]))
            {

                StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
            };
            Owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
                new BmsLibraryPackageInstallMutationPort(), new AwaitablePlaybackStop(PlaybackEntered, ReleasePlayback.Task), action => { action(); return true; });
            Owner.AttachLibrary(Library);
            Owner.CompletionPublished += receipt => Result = receipt.CommandResult;
            Owner.FailurePublished += failure => { Failure = failure; Result = failure.CommandResult; };
            if (!holdPlayback) { ReleasePlayback.SetResult(); }
        }

        internal BmsLibraryOptionsSnapshot CreateOptions(bool enabled, bool deleteSource, bool keepNewPending = true, bool lr2 = false, string? managed = null) => new()
        {
            OperationModeLR2DB = lr2,
            LR2CustomFolderOutputBaseDir = managed,
            ScanBmsFilesOnStartup = true,
            AutoInstallAfterAutomaticEstimation = enabled,
            InstallDestinationAutoApplyMinAudioHealth = 100,
            DeletePendingPackageSourceAfterInstall = deleteSource,
            KeepInstallablePackagesPending = keepNewPending,
            BMSInstallDir = LibraryRoot,
            FolderNameFormat = "%TITLE%"
        };
        internal string WriteInput(string directoryName, string chartName, string identity)
        {
            string directory = Path.Combine(Root, directoryName);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, chartName), CreateValidBmsText("Song") + "#GENRE " + identity + "\n", Encoding.ASCII);
            return directory;
        }
        internal async Task InitializeAsync()
        {
            Library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
        }
        public async ValueTask DisposeAsync()
        {
            ReleasePlayback.TrySetResult();
            try { await Owner.WaitForIdleAsync(); while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
            finally { Library.RequestShutdown("automatic-continuation-test"); Directory.Delete(Root, recursive: true); }
        }
    }
}
