using System;
using System.Collections.Concurrent;
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
using static BeMusicSeeker.Tests.ChartInfoMetadataTestSupport;

namespace BeMusicSeeker.Tests;

public sealed partial class PackageInstallWorkflowOwnerTests
{
    /// <summary>元音源99%でも欠損1件を補う候補へ実自動推定します。画像不足またはBMSONの親参照補助だけの厳格保留は自動推定を省略し、照合不能を実欠損の画像警告へ変換しません。通常画像不足で手動推定を許し、準備中のprovider変更は次操作へ反映します。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ProductionImport_UsesCapturedCompletenessPolicyAndEstimatesMissingAudio(bool onlyVisualMissing, bool invalidAuxiliary)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string libraryRoot = Path.Combine(root, "Library");
        string candidate = Path.Combine(libraryRoot, "Candidate");
        string source = Path.Combine(root, "Input");
        Directory.CreateDirectory(candidate);
        Directory.CreateDirectory(source);
        string[] sounds = Enumerable.Range(0, onlyVisualMissing ? 1 : 100).Select(i => "sound" + i + ".wav").ToArray();
        string body = "#TITLE Song\n#ARTIST Artist\n#BPM 120\n" + string.Join("", sounds.Select((name, i) =>
            "#WAV" + (i + 1).ToString("X2") + " " + name + "\n#00101:" + (i + 1).ToString("X2") + "\n"))
            + (onlyVisualMissing ? "#BMP01 missing.png\n#00104:01\n" : "");
        if (invalidAuxiliary)
        {
            body = "{\"info\":{\"title\":\"Song\",\"artist\":\"Artist\",\"init_bpm\":120,\"eyecatch_image\":\"../missing.png\"},"
                + "\"sound_channels\":[{\"name\":\"sound0.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}";
        }
        string installedChart = Path.Combine(candidate, "chart.bms");
        File.WriteAllText(installedChart, "#TITLE Song\n#ARTIST Artist\n#BPM 120\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(source, "diff." + (invalidAuxiliary ? "bmson" : "bms")), body, Encoding.ASCII);
        foreach (string sound in sounds.Take(onlyVisualMissing ? 1 : 99))
        {
            File.WriteAllBytes(Path.Combine(source, sound), [1]);
        }

        string[] candidateSounds = onlyVisualMissing ? sounds : sounds.Skip(98).ToArray();
        foreach (string sound in candidateSounds)
        {
            File.WriteAllBytes(Path.Combine(candidate, sound), [1]);
        }

        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        BmsLibraryOptionsSnapshot Options(bool strict) => new()
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            BMSInstallDir = libraryRoot,
            FolderNameFormat = "%TITLE%",
            RequireCompleteUsedResourcesForAutoInstall = strict
        };
        BmsLibraryOptionsSnapshot options = Options(onlyVisualMissing);
        var background = new ConcurrentQueue<Func<Task>>();
        var admission = new ChartFileOperationSynchronizer();
        var observer = new CompletenessEstimationObserver();
        var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([installedChart], new Dictionary<string, IEnumerable<string>> { [candidate] = candidateSounds }, [candidate]), observer, admission)
        {
            SearchTargets = [libraryRoot],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
            new BmsLibraryPackageInstallMutationPort(), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(library);
        PackageInstallFailure? failure = null;
        owner.FailurePublished += value => failure = value;
        int changed = 0;
        owner.StatusChanged += status =>
        {
            if (status.IsActive && status.CompletedPathCount == 1 && Interlocked.Exchange(ref changed, 1) == 0)
            {
                options = Options(!onlyVisualMissing);
            }
        };
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work))
            {
                await work();
            }

            Assert.IsTrue(owner.Enqueue([source]));
            await owner.WaitForIdleAsync();
            Assert.IsNull(failure, failure?.Exception.ToString());
            ChartPackage package = library.ChartPackagesPending.Single();
            Assert.AreEqual(1, changed);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
            }

            Assert.AreEqual(onlyVisualMissing ? 0 : 1, observer.WorkCount);
            if (onlyVisualMissing)
            {
                // 親参照の既存非対応判定も自動推定を省略します。通常画像不足では音源完全による省略を確認します。
                Assert.AreEqual(invalidAuxiliary ? PendingEstimateDeferredReason.UnsupportedResourcePath : PendingEstimateDeferredReason.CompleteSourceAudio,
                    package.DeferredEstimateReason);
                Assert.IsTrue(string.IsNullOrEmpty(package.ChartEntries.Single().Chart.InstallDestination));
                ChartFile pendingChart = package.ChartEntries.Single().Chart;
                if (invalidAuxiliary)
                {
                    Assert.IsNull(pendingChart.StagefileHealth);
                    Assert.IsFalse(pendingChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceStagefileMissing));
                    Assert.IsTrue(pendingChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
                }
                else
                {
                    Assert.IsTrue(pendingChart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
                }
                if (!invalidAuxiliary)
                {
                    library.SearchEstimatedInstallationDirectory(package);
                    Assert.AreEqual(candidate, package.ChartEntries.Single().Chart.InstallDestination);
                    string next = Path.Combine(root, "NextInput");
                    Directory.CreateDirectory(next);
                    File.WriteAllText(Path.Combine(next, "next.bms"), body + "#PLAYLEVEL 12\n", Encoding.ASCII);
                    File.WriteAllBytes(Path.Combine(next, sounds[0]), [1]);
                    Assert.IsTrue(owner.Enqueue([next]));
                    await owner.WaitForIdleAsync();
                    Assert.IsNull(failure, failure?.Exception.ToString());
                    Assert.AreEqual(1, library.ChartPackagesInstalled.Count);
                    Assert.AreEqual(1, library.ChartPackagesPending.Count);
                }
            }
            else
            {
                Assert.AreEqual(candidate, package.ChartEntries.Single().Chart.InstallDestination);
            }

            Assert.IsTrue(package.ChartEntries.All(entry => (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
        }
        finally
        {
            try
            {
                await owner.WaitForIdleAsync(); while (background.TryDequeue(out Func<Task>? work))
                {
                    await work();
                }
            }
            finally { library.RequestShutdown("resource-completeness-test"); Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>音源完全な実混在取り込みでも既知ハッシュ候補を評価します。未所持群だけの最頻ペアで一意先のStrong可否を判定し、同点は限定集合を維持して候補外の強一致先へ迂回しません。</summary>
    /// <remarks>本番ownerの受理からidleまでを通し、一意先は同梱だけに音源を用意します。同点は候補自身の既存最低一致条件も満たし、詳細条件表は下位・手動入口の既存系列へ分担します。</remarks>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    public async Task ProductionImport_CompleteSourceAudioStillEvaluatesKnownHashCandidates(bool strong, bool tied)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string libraryRoot = Path.Combine(root, "Library");
        string candidate = Path.Combine(libraryRoot, "A");
        string second = Path.Combine(libraryRoot, "B");
        string outside = Path.Combine(libraryRoot, "Outside");
        string source = Path.Combine(root, "Input");
        foreach (string directory in new[] { candidate, second, outside, source }) { Directory.CreateDirectory(directory); }
        var installed = new List<string>();
        void WriteInstalled(string directory, string name, string text)
        {
            string path = Path.Combine(directory, name);
            File.WriteAllText(path, text, Encoding.ASCII);
            installed.Add(path);
        }
        // 所持Other 4件を混ぜるとOtherが最頻になる。未所持群だけならSong 2件が最頻。
        for (int i = 0; i < 4; i++)
        {
            string text = "#TITLE Other\n#ARTIST Artist\n#GENRE owned" + i + "\n";
            string name = "owned" + i + ".bms";
            File.WriteAllText(Path.Combine(source, name), text, Encoding.ASCII);
            WriteInstalled(candidate, name, text);
            if (tied) { WriteInstalled(second, name, text); }
        }
        for (int i = 0; i < 3; i++)
        {
            string text = "#TITLE " + (i < 2 ? "Song" : "Another") + "\n#ARTIST Artist\n#GENRE missing" + i
                + "\n#WAV01 sound.wav\n#00111:01\n";
            File.WriteAllText(Path.Combine(source, "missing" + i + ".bms"), text, Encoding.ASCII);
        }
        if (strong)
        {
            for (int i = 0; i < 5; i++)
            {
                string text = "#TITLE Song\n#ARTIST Artist\n#GENRE candidate" + i + "\n";
                WriteInstalled(candidate, "candidate" + i + ".bms", text);
                if (tied) { WriteInstalled(second, "candidate" + i + ".bms", text); }
            }
        }
        WriteInstalled(outside, "outside.bms", "#TITLE Song\n#ARTIST Artist\n#GENRE outside\n");
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1]);
        File.WriteAllBytes(Path.Combine(outside, "sound.wav"), [1]);
        if (tied)
        {
            File.WriteAllBytes(Path.Combine(candidate, "sound.wav"), [1]);
            File.WriteAllBytes(Path.Combine(second, "sound.wav"), [1]);
        }
        var indexed = new Dictionary<string, IEnumerable<string>> { [candidate] = tied ? ["sound.wav"] : [], [outside] = ["sound.wav"] };
        if (tied) { indexed[second] = ["sound.wav"]; }
        var background = new ConcurrentQueue<Func<Task>>();
        var admission = new ChartFileOperationSynchronizer();
        var observer = new CompletenessEstimationObserver();
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            KeepInstallablePackagesPending = true,
            AutoApplyAmbiguousInstallDestination = true
        };
        string dbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(dbPath, []);
        var library = new TestBmsLibrary(dbPath, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture(installed, indexed, indexed.Keys), observer, admission)
        {
            SearchTargets = [libraryRoot],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
            new BmsLibraryPackageInstallMutationPort(), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(library);
        PackageInstallFailure? failure = null;
        owner.FailurePublished += value => failure = value;
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            Assert.IsTrue(owner.Enqueue([source]));
            await owner.WaitForIdleAsync();
            Assert.IsNull(failure, failure?.Exception.ToString());
            Assert.AreEqual(1, observer.WorkCount);
            ChartPackage package = library.ChartPackagesPending.Single();
            Assert.AreEqual(PendingEstimateDeferredReason.None, package.DeferredEstimateReason);
            var missing = package.ChartEntries.Where(entry => Path.GetFileName(entry.Chart.Path).StartsWith("missing", StringComparison.Ordinal)).ToList();
            Assert.AreEqual(3, missing.Count);
            foreach (PackageChartEntry entry in missing)
            {
                Assert.AreEqual(strong ? candidate : string.Empty, entry.Chart.InstallDestination ?? string.Empty);
                Assert.AreEqual(strong ? "Song" : "Other", entry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", entry.Chart.InstallDestinationArtist);
                Assert.AreEqual(!strong, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                Assert.AreEqual(tied, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(entry.Chart.InstallDestinationSuggestions.Contains(outside));
                if (!strong) { CollectionAssert.AreEqual(new[] { candidate }, entry.Chart.InstallDestinationSuggestions.ToArray()); }
                if (tied) { CollectionAssert.AreEqual(new[] { candidate, second }, entry.Chart.InstallDestinationSuggestions.ToArray()); }
                Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
        }
        finally
        {
            try { await owner.WaitForIdleAsync(); }
            finally
            {
                try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                finally { library.RequestShutdown("complete-source-hash-test"); Directory.Delete(root, recursive: true); }
            }
        }
    }

    /// <summary>自動取り込みは全既所持と設定保留の新規対象を準備前に除外し、同じバッチの混在だけを推定します。除外した新規対象は明示手動推定で設定できます。</summary>
    /// <remarks>小さい実FS・SQLiteと本番ownerの受理からidleを通し、準備到達・評価数・保留確定・受付解放を観測します。Strong条件表と手動全既所持系列は既存のサービス・再グループ化テストへ分担します。</remarks>
    [TestMethod]
    public async Task ProductionImport_SkipsOwnedAndNewPackagesBeforePreparationButEstimatesMixedSibling()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string libraryRoot = Path.Combine(root, "Library");
        string candidate = Path.Combine(libraryRoot, "Candidate");
        string ownedSource = Path.Combine(root, "Owned");
        string newSource = Path.Combine(root, "New");
        string mixedSource = Path.Combine(root, "Mixed");
        foreach (string directory in new[] { candidate, ownedSource, newSource, mixedSource }) { Directory.CreateDirectory(directory); }
        string body = "#TITLE Song\n#ARTIST Artist\n#BPM 120\n#WAV01 sound.wav\n#00111:01\n";
        string installedChart = Path.Combine(candidate, "owned.bms");
        File.WriteAllText(installedChart, body, Encoding.ASCII);
        File.WriteAllText(Path.Combine(ownedSource, "owned.bms"), body, Encoding.ASCII);
        File.WriteAllText(Path.Combine(mixedSource, "owned.bms"), body, Encoding.ASCII);
        File.WriteAllText(Path.Combine(mixedSource, "missing.bms"), body + "#GENRE mixed\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(newSource, "new.bms"), body + "#GENRE new\n", Encoding.ASCII);
        foreach (string directory in new[] { candidate, ownedSource, newSource, mixedSource })
        {
            File.WriteAllBytes(Path.Combine(directory, "sound.wav"), [1]);
        }
        string dbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(dbPath, []);
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            KeepInstallablePackagesPending = true
        };
        var background = new ConcurrentQueue<Func<Task>>();
        var admission = new ChartFileOperationSynchronizer();
        var observer = new CompletenessEstimationObserver();
        var library = new TestBmsLibrary(dbPath, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([installedChart],
                new Dictionary<string, IEnumerable<string>> { [candidate] = ["sound.wav"] }, [candidate]), observer, admission)
        {
            SearchTargets = [libraryRoot],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
            new BmsLibraryPackageInstallMutationPort(), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(library);
        PackageInstallFailure? failure = null;
        owner.FailurePublished += value => failure = value;
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            Assert.IsTrue(owner.Enqueue([ownedSource, newSource, mixedSource]));
            await owner.WaitForIdleAsync();
            Assert.IsNull(failure, failure?.Exception.ToString());
            CollectionAssert.AreEqual(new[] { new InstallEstimationPreparationObservation(PendingInstallEstimateBatchSource.AutoInstall, mixedSource) },
                observer.Preparations.ToArray());
            Assert.AreEqual(1, observer.WorkCount);
            Assert.AreEqual(3, library.ChartPackagesPending.Count);
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly())
            {
                Assert.AreEqual(3, db.Table<LR2SongDBExtended.install>().Count());
            }
            ChartPackage owned = library.ChartPackagesPending.Single(package => package.path == ownedSource);
            ChartPackage fresh = library.ChartPackagesPending.Single(package => package.path == newSource);
            ChartPackage mixed = library.ChartPackagesPending.Single(package => package.path == mixedSource);
            Assert.IsTrue(owned.ChartEntries.Concat(fresh.ChartEntries).All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)));
            Assert.IsTrue(string.IsNullOrEmpty(mixed.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "owned.bms").Chart.InstallDestination));
            Assert.AreEqual(candidate, mixed.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path) == "missing.bms").Chart.InstallDestination);
            Assert.IsTrue(library.ChartPackagesPending.SelectMany(package => package.ChartEntries)
                .All(entry => (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
            Assert.IsFalse(admission.IsActive);
            library.SearchEstimatedInstallationDirectory(fresh);
            Assert.AreEqual(candidate, fresh.ChartEntries.Single().Chart.InstallDestination);
            Assert.IsFalse(admission.IsActive);
        }
        finally
        {
            try { await owner.WaitForIdleAsync(); }
            finally
            {
                try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                finally { library.RequestShutdown("auto-estimation-eligibility-test"); Directory.Delete(root, recursive: true); }
            }
        }
    }

    /// <summary>推定可能な兄弟がなくても全既所持の分割入力を再グループ化します。登録後の取消では確定済み分割と保留行を維持し、どちらも推定準備・評価を開始しません。</summary>
    /// <remarks>実ownerへ元ディレクトリだけを渡して発見・登録・公開・終端を通します。分割公開で取消し、通常行の再グループ化条件表は既存の直接系列へ分担します。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_AllOwnedSplitWithoutEstimableSiblingRegroupsUnlessCanceledAfterRegistration(bool cancelAfterRegistration)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string libraryRoot = Path.Combine(root, "Library");
        string installed = Path.Combine(libraryRoot, "Installed");
        string source = Path.Combine(root, "Input");
        Directory.CreateDirectory(installed);
        Directory.CreateDirectory(source);
        string[] sourceCharts = [Path.Combine(source, "a.bms"), Path.Combine(source, "b.bms")];
        string[] installedCharts = [Path.Combine(installed, "a.bms"), Path.Combine(installed, "b.bms")];
        string[] sounds = ["sound_a.wav", "sound_b.wav"];
        for (int i = 0; i < sourceCharts.Length; i++)
        {
            string body = "#PLAYER 1\n#TITLE Song " + i + "\n#ARTIST Artist\n#BPM 120\n#WAVAA " + sounds[i] + "\n#00111:AA\n";
            File.WriteAllText(sourceCharts[i], body, Encoding.ASCII);
            File.WriteAllText(installedCharts[i], body, Encoding.ASCII);
            File.WriteAllBytes(Path.Combine(installed, sounds[i]), [1]);
        }
        string dbPath = Path.Combine(root, "song.db");
        File.WriteAllBytes(dbPath, []);
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true };
        var background = new ConcurrentQueue<Func<Task>>();
        var admission = new ChartFileOperationSynchronizer();
        var observer = new CompletenessEstimationObserver();
        var library = new TestBmsLibrary(dbPath, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture(installedCharts,
                new Dictionary<string, IEnumerable<string>> { [installed] = sounds }, [installed]), observer, admission)
        {
            SearchTargets = [libraryRoot],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission, new ChartMutationActivityOwner(),
            new BmsLibraryPackageInstallMutationPort(), new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(library);
        PackageInstallFailure? failure = null;
        owner.FailurePublished += value => failure = value;
        var publishedSplits = new ConcurrentQueue<string[]>();
        int canceled = 0;
        void ObserveSplit(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName != nameof(BMSLibrary.ChartPackagesPending)) { return; }
            string[] paths = library.ChartPackagesPending.Select(package => package.path).ToArray();
            if (paths.Length != 2 || !paths.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(sourceCharts)) { return; }
            publishedSplits.Enqueue(paths);
            if (cancelAfterRegistration && Interlocked.Exchange(ref canceled, 1) == 0) { owner.CancelAll(); }
        }
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            library.PropertyChanged += ObserveSplit;
            Assert.IsTrue(owner.Enqueue([source]));
            await owner.WaitForIdleAsync();
            Assert.IsFalse(publishedSplits.IsEmpty, "発見された2譜面を分割保留として公開してから継続します。");
            Assert.AreEqual(0, observer.Preparations.Count);
            Assert.AreEqual(0, observer.WorkCount);
            Assert.AreEqual(cancelAfterRegistration ? 2 : 1, library.ChartPackagesPending.Count);
            string[] expectedPaths = cancelAfterRegistration ? sourceCharts : [source];
            CollectionAssert.AreEquivalent(expectedPaths, library.ChartPackagesPending.Select(package => package.path).ToArray());
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(dbPath).OpenSongDbReadOnly())
            {
                CollectionAssert.AreEquivalent(expectedPaths, db.Table<LR2SongDBExtended.install>().Select(row => row.path).ToArray());
            }
            PackageChartEntry[] entries = library.ChartPackagesPending.SelectMany(package => package.ChartEntries).ToArray();
            CollectionAssert.AreEquivalent(sourceCharts, entries.Select(entry => entry.Chart.Path).ToArray());
            Assert.IsTrue(entries.All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)
                && entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled)
                && (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
            Assert.IsFalse(admission.IsActive);
            if (cancelAfterRegistration)
            {
                Assert.AreEqual(1, canceled);
                Assert.IsNotNull(failure);
                Assert.IsInstanceOfType<OperationCanceledException>(failure.Exception);
                Assert.IsTrue(failure.CommandResult.SessionReceipt.DurableCommit);
            }
            else { Assert.IsNull(failure, failure?.Exception.ToString()); }
        }
        finally
        {
            library.PropertyChanged -= ObserveSplit;
            try { await owner.WaitForIdleAsync(); }
            finally
            {
                try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                finally { library.RequestShutdown("all-owned-regroup-test"); Directory.Delete(root, recursive: true); }
            }
        }
    }

    private sealed class CompletenessEstimationObserver : IInstallEstimationExecutionObserver
    {
        internal ConcurrentQueue<InstallEstimationPreparationObservation> Preparations { get; } = new();
        public void ObservePreparation(InstallEstimationPreparationObservation observation) => Preparations.Enqueue(observation);
        private int workCount;
        internal int WorkCount => Volatile.Read(ref workCount);
        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            Interlocked.Increment(ref workCount);
            return new WorkScope();
        }
        public void ObserveProgress(InstallEstimationProgressObservation observation) { }
        public void ObserveResultApplied(InstallEstimationAppliedObservation observation) { }
        private sealed class WorkScope : IDisposable { public void Dispose() { } }
    }

    /// <summary>実候補探索後の交差Busyは停止・物理変更を行わず、受理Lをworker cleanup終端まで保持します。純推定へPは追加しません。</summary>
    /// <remarks>実ライブラリのDB確定・譜面情報公開と共通受付を接続する。開始した背景Taskとownerのidle終端を回収し、通知の到達だけで受付解放としない。</remarks>
    [TestMethod]
    public async Task AutoInstall_ManagedBusyAfterInputPreparationKeepsAdmissionUntilActualCleanupWithoutReplay()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string installRoot = Path.Combine(root, "Library");
        string managed = Path.Combine(root, "Managed");
        string source = Path.Combine(managed, "Crossing");
        Directory.CreateDirectory(installRoot);
        Directory.CreateDirectory(source);
        string sourceFile = Path.Combine(source, "chart.bms");
        File.WriteAllText(sourceFile, CreateValidBmsText("Crossing"), Encoding.ASCII);
        // 欠損音源による保留分類ではなく、実auto-install候補を交差判定まで通します。
        File.WriteAllBytes(Path.Combine(source, "sound.wav"), [1]);
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = true,
            BMSInstallDir = installRoot,
            LR2CustomFolderOutputBaseDir = managed,
            FolderNameFormat = "%TITLE%",
            KeepInstallablePackagesPending = false
        };
        var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null!), () => options)
        {
            BmsCharts = [],
            BmsonCharts = [],
            SearchTargets = [installRoot],
            ChartPackagesPending = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>(),
            ChartPackagesInstalled = new System.Collections.ObjectModel.ObservableCollection<ChartPackage>()
        };
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePreparation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = new ChartMutationActivityOwner();
        var playback = new NoOpChartMutationPlaybackPort();
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), library.OperationAdmission,
            activity, new BmsLibraryPackageInstallMutationPort(), playback, action => { action(); return true; });
        owner.AttachLibrary(library);
        LibraryMutationSessionReceipt? result = null;
        Exception? terminalFailure = null;
        int prepareHeld = 0;
        owner.CompletionPublished += receipt => result = receipt.SessionReceipt;
        owner.FailurePublished += failure => { result = failure.CommandResult.SessionReceipt; terminalFailure = failure.Exception; };
        owner.StatusChanged += snapshot =>
        {
            if (snapshot.IsActive && snapshot.CompletedPathCount == 1 && Interlocked.Exchange(ref prepareHeld, 1) == 0)
            {
                prepared.TrySetResult();
                releasePreparation.Task.GetAwaiter().GetResult();
            }
        };
        activity.ActivityChanged += (_, _) =>
        {
            if (!activity.IsActive)
            {
                cleanup.TrySetResult();
                releaseCleanup.Task.GetAwaiter().GetResult();
            }
        };
        IDisposable? held = null;
        Task? idle = null;
        try
        {
            Assert.IsTrue(owner.Enqueue([source]));
            idle = owner.WaitForIdleAsync();
            await Task.WhenAny(prepared.Task, idle);
            Assert.IsTrue(prepared.Task.IsCompletedSuccessfully, "実準備通知に先行する失敗終端を成功と扱いません。");
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out IDisposable probe));
            probe.Dispose();
            Assert.IsTrue(library.Lr2Synchronization.PlaylistOperationAdmission.TryEnter(out held));
            Assert.AreEqual(0, playback.StopCount);
            releasePreparation.TrySetResult();
            await Task.WhenAny(cleanup.Task, idle);
            Assert.IsTrue(cleanup.Task.IsCompletedSuccessfully, "実cleanupに先行する終端を成功と扱いません。");
            Assert.IsFalse(idle.IsCompleted);
            Assert.IsTrue(library.OperationAdmission.IsActive);
            Assert.IsFalse(owner.Enqueue([source]));
            Assert.AreEqual(0, playback.StopCount);
            Assert.IsTrue(File.Exists(sourceFile));
            Assert.IsFalse(Directory.Exists(Path.Combine(installRoot, "Crossing")));
            Assert.IsTrue(library.ChartPackagesPending.SelectMany(package => package.ChartEntries)
                .All(entry => (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(1, db.Table<LR2SongDBExtended.install>().Count());
                Assert.IsNull(db.Find<LR2SongDB.song>(Path.Combine(installRoot, "Crossing", "chart.bms")));
            }
            releaseCleanup.TrySetResult();
            await idle;
            Assert.IsNotNull(result, terminalFailure?.ToString());
            Assert.IsTrue(result.HasRequiredFailure);
            StringAssert.Contains(result.ItemFailures.Single().Failure.Message, BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy);
            Assert.IsFalse(library.OperationAdmission.IsActive);
            held.Dispose(); held = null;
            Assert.IsTrue(File.Exists(sourceFile), "P解放だけでBusy対象を再実行しません。");
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
            Assert.IsTrue(library.OperationAdmission.TryEnter(out IDisposable next));
            next.Dispose();
        }
        finally
        {
            releasePreparation.TrySetResult();
            releaseCleanup.TrySetResult();
            try { if (idle != null) { await idle; } await owner.WaitForIdleAsync(); }
            finally { held?.Dispose(); library.RequestShutdown("managed-busy-test"); Directory.Delete(root, recursive: true); }
        }
    }

    /// <summary>実管理ZIPを含む保留確定から直接推定へ進み、取消・例外後も既適用結果と未適用管理入力を保持する。開始済み兄弟のjoin中は受付を保持し、受理済み必須更新の完了も確認します。</summary>
    /// <remarks>実ライブラリのDB確定・譜面情報公開と共通受付を接続する。開始した背景Taskとownerのidle終端を回収し、通知の到達だけで受付解放としない。</remarks>
    // falseは利用者取消、trueは二件目の実評価例外。どちらも既適用Aを残し、成功終端Cを新たに適用しない。
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProductionImport_StopsDispatchAndApplyThenJoinsSuccessfulSiblingBeforeAdmissionRelease(bool fault)
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        string installed = Path.Combine(root, "Library", "Song");
        Directory.CreateDirectory(installed);
        string installedChart = Path.Combine(installed, "chart.bms");
        File.WriteAllText(installedChart, CreateValidBmsText("Song"), Encoding.ASCII);
        File.WriteAllBytes(Path.Combine(installed, "sound.wav"), [1]);
        string[] sources = Enumerable.Range(0, 5).Select(index => Path.Combine(root, "Input" + index)).ToArray();
        foreach (string source in sources)
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "diff.bms"), CreateValidBmsText("Song") + "#PLAYLEVEL " + (10 + Array.IndexOf(sources, source)) + "\r\n", Encoding.ASCII);
        }
        string managedArchiveRoot = TempDirectoryPublisher.Get();
        string managedArchive = Path.Combine(managedArchiveRoot, "Input3.zip");
        ZipFile.CreateFromDirectory(sources[3], managedArchive);
        sources[3] = managedArchive;
        var extractedRoots = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var files = new ArchiveInputFileMutationService(path =>
        {
            string? extractedRoot = Path.GetDirectoryName(path);
            if (extractedRoot != null && TempDirectoryPublisher.IsManagedPath(extractedRoot)) { extractedRoots.Add(extractedRoot); }
        });
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var background = new ConcurrentQueue<Func<Task>>();
        var admission = new ChartFileOperationSynchronizer();
        var observer = new HeldSuccessfulEvaluations(fault);
        var options = new BmsLibraryOptionsSnapshot
        {
            OperationModeLR2DB = false,
            ScanBmsFilesOnStartup = true,
            KeepInstallablePackagesPending = true,
            PendingInstallEstimateMaxParallelPackages = 2
        };
        var library = new TestBmsLibrary(songDb, null, null, files, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([installedChart],
                new Dictionary<string, IEnumerable<string>> { [installed] = ["sound.wav"] }, [installed]),
            observer, admission)
        {
            SearchTargets = [Path.Combine(root, "Library")],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission,
            new ChartMutationActivityOwner(), new BmsLibraryPackageInstallMutationPort(),
            new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        PackageInstallFailure? failure = null;
        owner.FailurePublished += value => failure = value;
        owner.AttachLibrary(library);
        Task? acceptedHydration = null;
        string newSha = new('9', 64);
        string newMd5 = new('8', 32);
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            Assert.IsTrue(owner.Enqueue(sources.Take(4)));
            var arrivals = Task.WhenAll(observer.FirstApplied.Task, observer.SecondEvaluated.Task, observer.ThirdEvaluated.Task);
            Task initialIdle = owner.WaitForIdleAsync();
            await Task.WhenAny(arrivals, observer.Stopped.Task, initialIdle);
            Assert.IsTrue(arrivals.IsCompletedSuccessfully, "実停止・終端が評価到達通知に先行したため、finallyで兄弟を解放して全joinします。");
            using (LR2SongDBExtended connection = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly())
            {
                Assert.AreEqual(4, connection.Table<LR2SongDBExtended.install>().Count(), "保留登録の実確定後も同じ操作が推定を所有します。");
            }
            Assert.IsTrue(admission.IsActive);
            new BmsLibraryDbGateway(songDb).UpsertChartInfos([CreateChartInfoRow(newSha, newMd5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            InvokeDeferredChartInfoHydration(library, "accepted_during_estimation", queueFullBackfillAfterHydration: false);
            Assert.IsTrue(background.TryDequeue(out Func<Task>? hydrationWork));
            acceptedHydration = hydrationWork();
            Assert.IsFalse(acceptedHydration.IsCompleted, "受理済みの実読込みは推定終端まで入力を書き換えません。");
            Assert.IsNull(library.ResolveChartInfo(newSha, newMd5));
            Assert.IsFalse(owner.Enqueue([sources[4]]), "実評価中の追加要求は予約しません。");
            if (!fault) { owner.CancelAll(); }
            observer.ReleaseSecond.TrySetResult();
            await Task.WhenAny(observer.Stopped.Task, owner.WaitForIdleAsync());
            Assert.IsTrue(observer.Stopped.Task.IsCompletedSuccessfully, "dispatch停止のtyped観測より前に終端しました。");
            Task idle = owner.WaitForIdleAsync();
            Assert.IsFalse(idle.IsCompleted, "取消・例外観測後も開始済み成功評価Cの終端を待ちます。");
            Assert.IsTrue(admission.IsActive);
            Assert.AreEqual(0, observer.FourthStarted);
            observer.ReleaseThird.TrySetResult();
            await idle;
            await acceptedHydration;
            Assert.AreEqual(newSha, library.ResolveChartInfo(newSha, newMd5).sha256, "受理済み必須更新をBusyで捨てず実入力へ適用します。");
            Assert.IsNotNull(failure);
            Assert.IsTrue(failure.CommandResult.SessionReceipt.DurableCommit);
            if (fault) { Assert.AreSame(observer.ExpectedFailure, failure.Exception); }
            else { Assert.IsInstanceOfType<OperationCanceledException>(failure.Exception); }
            Assert.IsFalse(admission.IsActive);
            Assert.AreEqual(0, observer.FourthStarted);
            ChartPackage first = library.ChartPackagesPending.Single(package => package.path == sources[0]);
            Assert.AreEqual(installed, first.ChartEntries.Single().Chart.InstallDestination);
            foreach (ChartPackage package in library.ChartPackagesPending.Where(package => package.path != sources[0]))
            {
                Assert.IsTrue(package.ChartEntries.All(entry => string.IsNullOrEmpty(entry.Chart.InstallDestination)), "停止後に正常終了したCも次の明示推定へ残します。");
            }
            Assert.IsTrue(library.ChartPackagesPending.SelectMany(package => package.ChartEntries).All(entry => (entry.Chart.Status & ChartFileStatus.SEARCHING) == 0));
            Assert.AreEqual(4, library.ChartPackagesPending.Count);
            Assert.IsFalse(File.Exists(managedArchive));
            Assert.AreEqual(1, extractedRoots.Count);
            ChartPackage managedPending = library.ChartPackagesPending.Single(package => extractedRoots.Contains(package.path));
            Assert.IsTrue(managedPending.ChartEntries.All(entry => File.Exists(entry.Chart.Path)));
            Assert.IsTrue(string.IsNullOrEmpty(managedPending.ChartEntries.Single().Chart.InstallDestination), "未開始の管理入力は次の明示推定へ保全します。");
            failure = null;
            Assert.IsTrue(owner.Enqueue([sources[4]]));
            await owner.WaitForIdleAsync();
            Assert.IsNull(failure);
            Assert.AreEqual(5, library.ChartPackagesPending.Count);
            Assert.AreEqual(installed, library.ChartPackagesPending.Single(package => package.path == sources[4]).ChartEntries.Single().Chart.InstallDestination);
        }
        finally
        {
            observer.ReleaseSecond.TrySetResult();
            observer.ReleaseThird.TrySetResult();
            try { await owner.WaitForIdleAsync(); }
            finally
            {
                try { if (acceptedHydration != null) { await acceptedHydration; } }
                finally
                {
                    try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                    finally
                    {
                        library.RequestShutdown("accepted-estimation-test");
                        foreach (string path in extractedRoots) { TempDirectoryPublisher.TryDeleteManagedPath(path); }
                        TempDirectoryPublisher.TryDeleteManagedPath(managedArchiveRoot);
                        Directory.Delete(root, recursive: true);
                    }
                }
            }
        }
    }

    /// <summary>実譜面情報の公開通知を保持している間は新規取り込みを拒否し、入力公開の実終端後に同じ要求を受理して保留へ登録する。</summary>
    /// <remarks>実ライブラリのDB確定・譜面情報公開と共通受付を接続する。開始した背景Taskとownerのidle終端を回収し、通知の到達だけで受付解放としない。</remarks>
    [TestMethod]
    public async Task ProductionHydration_BlocksNewImportUntilActualInputPublicationCompletes()
    {

        string root = Path.Combine(Path.GetTempPath(), nameof(PackageInstallWorkflowOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Library"));
        string source = Path.Combine(root, "input");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "diff.bms"), CreateValidBmsText("Song"), Encoding.ASCII);
        string songDb = Path.Combine(root, "song.db");
        File.WriteAllBytes(songDb, []);
        var admission = new ChartFileOperationSynchronizer();
        var background = new ConcurrentQueue<Func<Task>>();
        var options = new BmsLibraryOptionsSnapshot { OperationModeLR2DB = false, ScanBmsFilesOnStartup = true, KeepInstallablePackagesPending = true };
        var observer = new HeldSuccessfulEvaluations(false);
        var library = new TestBmsLibrary(songDb, null, null, null, new FileDbReportRecordingDialogs(),
            new TestUiScheduler(() => null), () => options,
            CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), []), observer, admission)
        {
            SearchTargets = [Path.Combine(root, "Library")],
            StartupBackgroundTaskScheduler = (_, _, _, work) => { background.Enqueue(work); return true; }
        };
        var owner = new PackageInstallWorkflowOwner(new FileDbReportRecordingDialogs(), admission,
            new ChartMutationActivityOwner(), new BmsLibraryPackageInstallMutationPort(),
            new NoOpChartMutationPlaybackPort(), action => { action(); return true; });
        owner.AttachLibrary(library);
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? hydration = null;
        void HoldPublication(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(BMSLibrary.ChartInfoIndexVersion))
            {
                publishing.TrySetResult();
                release.Task.GetAwaiter().GetResult();
            }
        }
        try
        {
            library.Initialize(null, null, BMSLibrary.LibraryInitializeMode.Startup);
            while (background.TryDequeue(out Func<Task>? work)) { await work(); }
            string sha = new('9', 64);
            string md5 = new('8', 32);
            new BmsLibraryDbGateway(songDb).UpsertChartInfos([CreateChartInfoRow(sha, md5, BmsLibraryDbGateway.CurrentChartInfoParserVersion)]);
            library.PropertyChanged += HoldPublication;
            InvokeDeferredChartInfoHydration(library, "prior_input_update", queueFullBackfillAfterHydration: false);
            Assert.IsTrue(background.TryDequeue(out Func<Task>? hydrationWork));
            hydration = Task.Run(hydrationWork);
            await Task.WhenAny(publishing.Task, hydration);
            if (!publishing.Task.IsCompleted) { await hydration; Assert.Fail("実hydrationが入力公開前に終端しました。"); }
            Assert.IsFalse(hydration.IsCompleted);
            Assert.IsTrue(admission.IsActive);
            Assert.IsFalse(owner.Enqueue([source]), "実背景入力更新が公開終端へ到達するまで新しい取り込み・推定を開始しません。");
            Assert.IsFalse(observer.FirstApplied.Task.IsCompleted);
            Assert.AreEqual(0, library.ChartPackagesPending.Count);
            release.TrySetResult();
            await hydration;
            Assert.AreEqual(sha, library.ResolveChartInfo(sha, md5).sha256);
            Assert.IsFalse(admission.IsActive);
            Assert.IsTrue(owner.Enqueue([source]));
            await owner.WaitForIdleAsync();
            Assert.AreEqual(1, library.ChartPackagesPending.Count);
        }
        finally
        {
            release.TrySetResult();
            try { if (hydration != null) { await hydration; } }
            finally
            {
                library.PropertyChanged -= HoldPublication;
                try { await owner.WaitForIdleAsync(); }
                finally
                {
                    try { while (background.TryDequeue(out Func<Task>? work)) { await work(); } }
                    finally { library.RequestShutdown("prior-hydration-test"); Directory.Delete(root, recursive: true); }
                }
            }
        }
    }

    private sealed class HeldSuccessfulEvaluations(bool fault) : IInstallEstimationExecutionObserver
    {
        internal readonly TaskCompletionSource FirstApplied = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource SecondEvaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ThirdEvaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseThird = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly IOException ExpectedFailure = new("実評価の終端失敗");
        internal int FourthStarted;

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            if (observation.OrderIndex == 3) { Interlocked.Increment(ref FourthStarted); }
            return new EvaluationScope();
        }

        public void ObserveEvaluationCompleted(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 1)
            {
                SecondEvaluated.TrySetResult();
                ReleaseSecond.Task.GetAwaiter().GetResult();
                if (fault) { throw ExpectedFailure; }
            }
            if (observation.OrderIndex == 2)
            {
                ThirdEvaluated.TrySetResult();
                ReleaseThird.Task.GetAwaiter().GetResult();
            }
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
            if (observation.OrderIndex == 0) { FirstApplied.TrySetResult(); }
        }

        public void ObserveDispatchStopped() => Stopped.TrySetResult();

        public void ObserveProgress(InstallEstimationProgressObservation observation) { }

        private sealed class EvaluationScope : IDisposable { public void Dispose() { } }
    }
}
