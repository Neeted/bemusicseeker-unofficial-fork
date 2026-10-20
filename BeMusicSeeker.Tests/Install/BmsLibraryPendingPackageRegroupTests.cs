using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryPendingPackageRegroupTests
{
    /// <summary>単独ファイルのOR確定表示を再編成で引き継ぎ、その後の明示選択は新ディレクトリ群の捕捉済み90%と同一ペアANDを使います。</summary>
    [TestMethod]
    public void TryRegroup_NewDirectorySourceKeepsFinalizedDisplayThenUsesCapturedGroupRule()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string source = Path.Combine(root, "Pending");
            string destination = Path.Combine(root, "Installed");
            string firstRequirements = string.Join("", Enumerable.Range(1, 9).Select(i => $"#WAV{i:D2} tone{i}.wav\n"))
                + "#00111:010203040506070809\n";
            string firstPath = CreateBmsFileWithContents(source, "first.bms", "#TITLE abc\n#ARTIST abc\n" + firstRequirements);
            string secondPath = CreateBmsFileWithContents(source, "second.bms", "#TITLE abc\n#ARTIST abc\n#WAV01 tone10.wav\n#00111:01\n");
            library.BmsCharts = [
                ReadChart(CreateBmsFileWithContents(destination, "a.bms", "#TITLE abc\n#ARTIST xyz\n")),
                ReadChart(CreateBmsFileWithContents(destination, "b.bms", "#TITLE abd\n#ARTIST abe\n"))];
            foreach (int i in Enumerable.Range(1, 10))
            {
                File.WriteAllText(Path.Combine(destination, $"tone{i}.ogg"), "audio");
                if (i <= 9) { File.WriteAllText(Path.Combine(source, $"tone{i}.ogg"), "audio"); }
            }
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            ChartPackage[] packages = new[] { firstPath, secondPath }.Select(path =>
            {
                var package = ChartPackage.FromChartEntries([PackageChartEntry.FromPath(path)]);
                package.path = path;
                package.delete_parent = false;
                return package;
            }).ToArray();
            SeedPendingPackages(library, db, packages);
            // 単独ファイルでは親の音源を持ち込まないため、元の操作はORの片側一致ペアを使います。
            foreach (ChartPackage package in packages)
            {
                library.SearchEstimatedInstallationDirectory(package);
                PackageChartEntry entry = package.ChartEntries.Single();
                Assert.AreEqual("abc", entry.Chart.InstallDestinationTitle);
                Assert.IsTrue(library.SetPendingInstallDestination(entry, destination));
            }
            InvokeRegroupForSourceDirectories(library, source);
            ChartPackage regrouped = AssertRegroupedPendingPackage(library, source, destination, expectedFileCount: 2);
            Assert.IsTrue(regrouped.ChartEntries.All(entry => entry.Chart.InstallDestinationTitle == "abc"));
            Assert.IsTrue(regrouped.ChartEntries.All(entry => entry.Chart.WAVHealth == 100));
            // 新しいdirectoryの同梱は群で9/10。後の物理変更を、取得済み入力の暗黙再走査で取り込みません。
            File.Delete(Path.Combine(source, "tone9.ogg"));
            Assert.IsTrue(library.SetPendingInstallDestination(regrouped.ChartEntries[0], destination));
            Assert.IsTrue(regrouped.ChartEntries.All(entry => entry.Chart.InstallDestinationTitle == "abd"
                && entry.Chart.InstallDestinationArtist == "abe"));
        });
    }


    /// <summary>同じ群で捕捉時点が異なる同梱入力を先頭一件で代表せず、不明Anyとして明示指定を受理します。</summary>
    [TestMethod]
    public void SelectDestination_DifferentCapturedSourceContextsAreUnverifiable()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string source = Path.Combine(root, "Pending");
            string destination = Path.Combine(root, "Installed");
            string[] names = [.. Enumerable.Range(0, 10).Select(i => $"tone{i}.wav")];
            PackageChartEntry[] entries = Enumerable.Range(0, 2).Select(i =>
            {
                ChartFile chart = ReadChart(CreateBmsFileWithContents(source, $"target{i}.bms", "#TITLE abc\n#ARTIST abc\n"))
                    with
                { Resources = TestChartResources.Create(names) };
                var entry = PackageChartEntry.FromChart(chart);
                var bundled = new DirectoryResourceLookupCache.Entry(entry.ResourceSnapshot.AudioReferences.Take(9 - i).Select(r => r.RelativePathHash), [], []);
                entry.ReplaceResourceHealthProjection(null, [], new ChartResourcePlacement(ChartResourcePlacementBasis.Current, source, bundled));
                return entry;
            }).ToArray();
            var package = ChartPackage.FromChartEntries(entries);
            package.path = source;
            library.BmsCharts = [
                ReadChart(CreateBmsFileWithContents(destination, "a.bms", "#TITLE abc\n#ARTIST xyz\n")),
                ReadChart(CreateBmsFileWithContents(destination, "b.bms", "#TITLE abd\n#ARTIST abe\n"))];
            SeedPendingPackages(library, db, package);
            Assert.IsTrue(library.SetPendingInstallDestination(entries[0], destination));
            Assert.IsTrue(entries.All(entry => entry.Chart.InstallDestinationTitle == "abc"));
        });
    }

    /// <summary>同一バッチの89%と90%を別々に分類し、候補0件でCurrentになった項目も捕捉済み源側を失いません。</summary>
    [TestMethod]
    public void SearchBatch_UsesIndependentSourceRulesAndRetainsCurrentContext()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "candidate.bms", "#TITLE xyz\n#ARTIST abc\n"))];
            string[] names = [.. Enumerable.Range(0, 100).Select(i => $"tone{i}.wav")];
            foreach (string name in names) { File.WriteAllText(Path.Combine(destination, name), "audio"); }
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            ChartPackage[] packages = Enumerable.Range(89, 2).Select(supplied =>
            {
                string source = Path.Combine(root, "Pending" + supplied);
                ChartFile chart = ReadChart(CreateBmsFileWithContents(source, "target.bms", "#TITLE abc\n#ARTIST abc\n"))
                    with
                { Resources = TestChartResources.Create(names) };
                foreach (string name in names.Take(supplied)) { File.WriteAllText(Path.Combine(source, name), "audio"); }
                var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
                package.path = source;
                return package;
            }).ToArray();
            SeedPendingPackages(library, db, packages);
            library.SearchEstimatedInstallationDirectory(packages);
            for (int index = 0; index < packages.Length; index++)
            {
                PackageChartEntry entry = packages[index].ChartEntries.Single();
                Assert.AreEqual(index == 0 ? 1 : 0, entry.Chart.InstallDestinationSuggestions.Count);
                Assert.AreEqual(index == 0 ? "xyz" : string.Empty, entry.Chart.InstallDestinationTitle);
                PackageChartEntry detached = entry.ToChartEntrySnapshot();
                var audio = InstallEstimationSourceAudio.Evaluate(detached.ResourceSnapshot, detached.ResourcePlacement.BundledResources, detached.ResourcePlacement.BundledResourcesComplete);
                Assert.AreEqual(index == 0 ? InstallEstimationRelatedRule.AnyField : InstallEstimationRelatedRule.BothFields, audio.RelatedRule);
                Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
                packages[index].InvalidateInstallEstimationSnapshot();
                foreach (string name in names) { File.WriteAllText(Path.Combine(packages[index].path, name), "later"); }
                Assert.IsTrue(library.SetPendingInstallDestination(entry, destination));
                Assert.IsNull(packages[index].CaptureInstallEstimationSurfaceSnapshot(), "捕捉済みのバッチ入力を手動表示と健康度へ共用し、追加列挙しません。");
                Assert.AreEqual(index == 0 ? InstallEstimationRelatedRule.AnyField : InstallEstimationRelatedRule.BothFields,
                    InstallEstimationSourceAudio.Evaluate(entry.ResourceSnapshot, entry.ResourcePlacement.BundledResources, entry.ResourcePlacement.BundledResourcesComplete).RelatedRule);
            }
        });
    }

    /// <summary>源側80/90%の表示根拠を推定・選択・空編集・再選択で維持し、配置先込み100%や入力元の後続変更から逆算しません。</summary>
    [DataTestMethod]
    [DataRow(8, "abc")]
    [DataRow(9, "abd")]
    public void SelectDestination_UsesCapturedSourceRuleAcrossEditingAndExplicitReestimation(int supplied, string expectedTitle)
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string source = Path.Combine(root, "Pending");
            string destination = Path.Combine(root, "Installed");
            string requirements = string.Join("", Enumerable.Range(1, 10).Select(i => $"#WAV{i:D2} tone{i}.wav\n"))
                + "#00111:01020304050607080910\n";
            var entry = PackageChartEntry.FromPath(CreateBmsFileWithContents(source, "target.bms", "#TITLE abc\n#ARTIST abc\n" + requirements));
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            library.BmsCharts = [
                ReadChart(CreateBmsFileWithContents(destination, "a.bms", "#TITLE abc\n#ARTIST xyz\n")),
                ReadChart(CreateBmsFileWithContents(destination, "b.bms", "#TITLE abd\n#ARTIST abe\n"))];
            foreach (int i in Enumerable.Range(1, 10))
            {
                File.WriteAllText(Path.Combine(destination, $"tone{i}.ogg"), "audio");
                if (i <= supplied) { File.WriteAllText(Path.Combine(source, $"tone{i}.ogg"), "audio"); }
            }
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            SeedPendingPackages(library, db, package);
            library.SearchEstimatedInstallationDirectory(package);
            Assert.AreEqual(expectedTitle, entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(100, entry.Chart.WAVHealth);
            // 捕捉後にFSを変えても、手動表示のために入力元を再列挙しません。
            foreach (int i in Enumerable.Range(1, 10)) { File.WriteAllText(Path.Combine(source, $"tone{i}.ogg"), "later"); }
            Assert.IsTrue(library.SetPendingInstallDestination(entry, destination));
            Assert.AreEqual(expectedTitle, entry.Chart.InstallDestinationTitle);
            Assert.IsTrue(library.SetPendingInstallDestination(entry, string.Empty));
            Assert.IsTrue(library.SetPendingInstallDestination(entry, destination));
            Assert.AreEqual(expectedTitle, entry.Chart.InstallDestinationTitle);
            // 明示クリアでCurrentへ戻った後、detached要求境界でも同じ源側評価を再現できます。
            library.RemoveInstallDestination([entry], package);
            Assert.AreEqual(ChartResourcePlacementBasis.Current, entry.ResourcePlacement.Basis);
            PackageChartEntry detached = entry.ToChartEntrySnapshot();
            var sourceAudio = InstallEstimationSourceAudio.Evaluate(detached.ResourceSnapshot,
                detached.ResourcePlacement.BundledResources, detached.ResourcePlacement.BundledResourcesComplete);
            Assert.AreEqual(supplied == 9 ? InstallEstimationRelatedRule.BothFields : InstallEstimationRelatedRule.AnyField, sourceAudio.RelatedRule);
            Assert.IsTrue(library.SetPendingInstallDestination(detached, destination));
            Assert.AreEqual(expectedTitle, entry.Chart.InstallDestinationTitle);
            library.RemoveInstallDestination([entry], package);
            package.InvalidateInstallEstimationSnapshot();
            library.SearchEstimatedInstallationDirectory(package);
            Assert.AreEqual("abd", entry.Chart.InstallDestinationTitle);
        });
    }

    /// <summary>未推定の手入力は源側不明でも受理し、表示照合のために隠れた入力元取得を始めません。</summary>
    [TestMethod]
    public void SelectDestination_UncapturedSourceUsesAnyFieldAndAcceptsExplicitPath()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string source = Path.Combine(root, "Pending");
            string destination = Path.Combine(root, "Installed");
            var entry = PackageChartEntry.FromPath(CreateBmsFileWithContents(source, "target.bms", "#TITLE abc\n#ARTIST abc\n#WAV01 tone.wav\n#00111:01\n"));
            File.WriteAllText(Path.Combine(source, "tone.ogg"), "audio");
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            library.BmsCharts = [
                ReadChart(CreateBmsFileWithContents(destination, "a.bms", "#TITLE abc\n#ARTIST xyz\n")),
                ReadChart(CreateBmsFileWithContents(destination, "b.bms", "#TITLE abd\n#ARTIST abe\n"))];
            SeedPendingPackages(library, db, package);
            Assert.IsNull(package.CaptureInstallEstimationSurfaceSnapshot());
            Assert.IsTrue(library.SetPendingInstallDestination(entry, destination));
            Assert.AreEqual("abc", entry.Chart.InstallDestinationTitle);
        });
    }
    /// <summary>手動編集は解決済みのパッケージ同梱を使い、パッケージクリアは対象の現在配置へ戻します。別の保留パッケージや通常所持の投影は変更しません。</summary>
    [DataTestMethod]
    [DataRow("packages")]
    [DataRow("pending")]
    [DataRow("correct")]
    public void SetAndClearPendingInstallDestination_RefreshesResolvedPackagePlacement(string clearRoute)
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string source = Path.Combine(root, "Source");
            var entry = PackageChartEntry.FromPath(CreateBmsFileWithContents(source, "chart.bms", "#WAV01 tone.wav\n#00111:01\n"));
            File.WriteAllText(Path.Combine(source, "tone.ogg"), "audio");
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var otherEntry = PackageChartEntry.FromPath(CreateBmsFileWithContents(Path.Combine(root, "Other"), "other.bms", "#WAV01 absent.wav\n#00111:01\n"));
            var other = ChartPackage.FromChartEntries([otherEntry]);
            other.path = Path.GetDirectoryName(otherEntry.Chart.Path);
            string destination = Path.Combine(root, "Destination");
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "owned.bms", "#TITLE Owned\n"))];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            SeedPendingPackages(library, db, package, other);
            int otherChanges = 0;
            otherEntry.PropertyChanged += (_, _) => otherChanges++;
            Assert.IsTrue(library.SetPendingInstallDestination(entry, destination));
            Assert.AreEqual(100, entry.Chart.WAVHealth);
            Assert.AreEqual(ChartResourcePlacementBasis.Destination, entry.ResourcePlacement.Basis);
            File.Delete(Path.Combine(source, "tone.ogg"));
            var store = new BeMusicSeeker.ViewModels.BmsLibraryPendingPackageStore();
            var target = new BeMusicSeeker.ViewModels.ChartOperationTarget(entry.Chart, null,
                BeMusicSeeker.ViewModels.ChartOperationSourceScope.PendingPackage, false, true, false,
                BeMusicSeeker.ViewModels.ChartOperationCapabilities.UpdateInstallDestination | BeMusicSeeker.ViewModels.ChartOperationCapabilities.RepairInstalledLocation, entry);
            if (clearRoute == "packages") { store.ClearPackages([package]); }
            else if (clearRoute == "pending")
            {
                Assert.IsTrue(BeMusicSeeker.ViewModels.PendingInstallDestinationClearRequest.TryCreate([target], out PendingInstallDestinationClearRequest? request));
                store.ClearPending(library, request);
            }
            else
            {
                Assert.IsTrue(BeMusicSeeker.ViewModels.RepairInstalledLocationRequest.TryCreate([target], out RepairInstalledLocationRequest? request));
                store.ClearCorrect(library, request);
            }
            Assert.AreEqual(100, entry.Chart.WAVHealth);
            Assert.AreEqual(ChartResourcePlacementBasis.Current, entry.ResourcePlacement.Basis);
            Assert.AreEqual(0, otherChanges);
            Assert.IsNull(otherEntry.ResourcePlacement);
            var owned = PackageChartEntry.FromChart(library.BmsCharts.Single());
            Assert.IsTrue(library.SetPendingInstallDestination(owned, destination));
            Assert.IsNull(owned.ResourcePlacement);
            library.RemoveInstallDestination([owned], null);
            Assert.IsNull(owned.ResourcePlacement);
        });
    }

    /// <summary>ハッシュ候補への推定で兄弟だけに欠損があっても全推定対象へ理由を配送し、実編集・空編集・再推定・明示クリアで理由と現在健康度を独立して更新します。</summary>
    [TestMethod]
    public void SearchAndEdit_GroupAudioReasonRemainsIndependentOfCurrentHealth()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            const string metadata = "#TITLE Song\n#ARTIST Artist\n";
            string destination = Path.Combine(root, "Installed");
            string complete = Path.Combine(root, "Complete");
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "owned.bms", metadata)),
                ReadChart(CreateBmsFileWithContents(complete, "other.bms", metadata + "#GENRE other\n"))];
            File.WriteAllText(Path.Combine(destination, "tone.ogg"), "audio");
            File.WriteAllText(Path.Combine(complete, "tone.ogg"), "audio");
            File.WriteAllText(Path.Combine(complete, "absent.ogg"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination, complete));
            string source = Path.Combine(root, "Pending");
            var package = ChartPackage.FromChartEntries([
                PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "owned.bms", metadata))),
                PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "full.bms", metadata + "#WAV01 tone.wav\n#00111:01\n"))),
                PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "missing.bms", metadata + "#WAV01 absent.wav\n#00111:01\n")))]);
            package.path = source;
            SeedPendingPackages(library, db, package);
            library.SearchEstimatedInstallationDirectory(package);
            PackageChartEntry full = GetEntryByFileName(package, "full.bms");
            PackageChartEntry missing = GetEntryByFileName(package, "missing.bms");
            Assert.AreEqual(100, full.Chart.WAVHealth);
            Assert.AreEqual(0, missing.Chart.WAVHealth);
            foreach (PackageChartEntry entry in new[] { full, missing })
            {
                Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            }
            Assert.IsFalse(GetEntryByFileName(package, "owned.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            Assert.IsTrue(library.SetPendingInstallDestination(missing, complete));
            Assert.AreEqual(100, missing.Chart.WAVHealth);
            Assert.IsTrue(missing.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            Assert.IsTrue(library.SetPendingInstallDestination(missing, destination));
            Assert.AreEqual(0, missing.Chart.WAVHealth);
            Assert.IsTrue(library.SetPendingInstallDestination(missing, string.Empty));
            Assert.AreEqual(0, missing.Chart.WAVHealth);
            Assert.AreEqual(ChartResourcePlacementBasis.FirstCandidate, missing.ResourcePlacement.Basis);
            library.SearchEstimatedInstallationDirectory(package);
            Assert.AreEqual(1, missing.Chart.Warnings.Count(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            library.RemoveInstallDestination([full, missing], package);
            Assert.IsTrue(new[] { full, missing }.All(entry => !entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation)));
            Assert.AreEqual(ChartResourcePlacementBasis.Current, missing.ResourcePlacement.Basis);
            library.SearchEstimatedInstallationDirectory(package);
            Assert.IsTrue(missing.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            File.WriteAllText(Path.Combine(destination, "absent.ogg"), "new audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination, complete));
            library.SearchEstimatedInstallationDirectory(package);
            Assert.AreEqual(destination, missing.Chart.InstallDestination);
            Assert.AreEqual(100, missing.Chart.WAVHealth);
            Assert.IsFalse(new[] { full, missing }.Any(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing)));
        });
    }

    /// <summary>全譜面が既所持の複数パッケージも通常一括入口で全対象を評価・反映します。未所持だけへ再フィルターして結果を捨てる誤りを検出します。</summary>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_AllOwnedBatchEvaluatesAndAppliesAllTargets()
    {
        var observer = new RecordingInstallEstimationExecutionObserver();
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            const string body = "#TITLE Song\n#ARTIST Artist\n";
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "base.bms", body))];
            ChartPackage[] packages = Enumerable.Range(0, 2).Select(i =>
            {
                string source = Path.Combine(root, "Pending", i.ToString());
                var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "base.bms", body)))]);
                package.path = source;
                return package;
            }).ToArray();
            SeedPendingPackages(library, db, packages);
            library.SearchEstimatedInstallationDirectory(packages);
            foreach (ChartPackage package in packages)
            {
                ChartFile chart = package.ChartEntries.Single().Chart;
                Assert.AreEqual(destination, chart.InstallDestination);
                Assert.AreEqual("Song", chart.InstallDestinationTitle);
                Assert.IsFalse(chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
        }, observer);
        Assert.AreEqual(2, observer.WorkItems.Count);
        Assert.IsFalse(observer.Progress.Last().IsActive);
        Assert.IsTrue(observer.Progress.Any(progress => progress.CompletedWorkCount == 2));
    }

    /// <summary>実hash候補の音源不足を個別・一括入口で保留し、Strongな候補表示を残します。開始した推定のSEARCHING・進捗も終端します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchEstimatedInstallationDirectory_HashIncompleteAudioPreservesCandidatesAndCompletes(bool batch)
    {
        var observer = new RecordingInstallEstimationExecutionObserver();
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            const string owned = "#TITLE Song\n#ARTIST Artist\n";
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "base.bms", owned))];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            var packages = new List<ChartPackage>();
            for (int i = 0; i < (batch ? 2 : 1); i++)
            {
                string source = Path.Combine(root, "Pending", i.ToString());
                var package = ChartPackage.FromChartEntries([
                    PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "base.bms", owned))),
                    PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "missing.bms", owned + "#WAV01 absent.wav\n#00111:01\n")))]);
                package.path = source;
                packages.Add(package);
            }
            SeedPendingPackages(library, db, packages.ToArray());
            if (batch)
            {
                library.SearchEstimatedInstallationDirectory(packages);
            }
            else
            {
                library.SearchEstimatedInstallationDirectory(packages.Single());
            }

            foreach (ChartPackage package in packages)
            {
                PackageChartEntry entry = GetEntryByFileName(package, "missing.bms");
                Assert.IsTrue(string.IsNullOrEmpty(entry.Chart.InstallDestination));
                Assert.AreEqual("Song", entry.Chart.InstallDestinationTitle);
                CollectionAssert.AreEqual(new[] { destination }, entry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
        }, observer);
        Assert.IsFalse(observer.Progress.Last().IsActive);
        Assert.IsTrue(observer.Progress.Any(progress => progress.CompletedWorkCount == (batch ? 2 : 1)));
    }

    /// <summary>実builderの上限超過を個別・二件一括の手動ハッシュ経路へ渡し、Strongかつ候補音源完備でも導入先を設定せず、全対象へ上限WARNING・保留理由を反映して進捗を終端します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchEstimatedInstallationDirectory_HashSourceSurfaceLimitWarnsAndCompletes(bool batch)
    {
        var observer = new RecordingInstallEstimationExecutionObserver();
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            const string owned = "#TITLE Song\n#ARTIST Artist\n";
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "base.bms", owned))];
            File.WriteAllText(Path.Combine(destination, "sound.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            var packages = new List<ChartPackage>();
            for (int i = 0; i < (batch ? 2 : 1); i++)
            {
                string source = Path.Combine(root, "Pending", i.ToString());
                var package = ChartPackage.FromChartEntries([
                    PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "base.bms", owned))),
                    PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "missing.bms", owned + "#WAV01 sound.wav\n#00111:01\n")))]);
                File.WriteAllText(Path.Combine(source, "partial.wav"), "audio");
                package.path = source;
                packages.Add(package);
            }
            SeedPendingPackages(library, db, packages.ToArray());
            foreach (ChartPackage package in packages) { SeedHashSourceSurfaceLimitFixture(package); }
            if (batch) { library.SearchEstimatedInstallationDirectory(packages); }
            else { library.SearchEstimatedInstallationDirectory(packages.Single()); }
            foreach (ChartPackage package in packages)
            {
                PackageChartEntry target = GetEntryByFileName(package, "missing.bms");
                Assert.IsTrue(string.IsNullOrEmpty(target.Chart.InstallDestination));
                Assert.AreEqual(PendingEstimateDeferredReason.SourceSurfaceScanLimitExceeded, package.DeferredEstimateReason);
                ChartWarning warning = target.Chart.Warnings.Single(item => item.Kind == ChartWarningKind.SourceSurfaceScanLimitExceeded);
                Assert.AreEqual(string.Format(BeMusicSeeker.Properties.Resources.Warning_SourceSurfaceScanLimitExceeded, 1), warning.Message);
                Assert.IsTrue(package.ChartEntries.All(entry => !entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING)));
            }
        }, observer);
        Assert.IsFalse(observer.Progress.Last().IsActive);
        Assert.IsTrue(observer.Progress.Any(progress => progress.CompletedWorkCount == (batch ? 2 : 1)));
    }

    /// <summary>この局所fixtureだけが実builderで取得した低上限surfaceを既存package cache一フィールドへ接続します。自然な上限入力境界が使えるようになった場合、またはcache契約変更時にこの反射準備を退役します。</summary>
    private static void SeedHashSourceSurfaceLimitFixture(ChartPackage package)
    {
        PackageInstallSurfaceSnapshot surface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(package.path,
            maxVisitedFileSystemEntryCount: 1);
        Assert.IsTrue(surface.ScanLimitExceeded);
        Assert.AreEqual(package.path, surface.SourcePath);
        Assert.IsTrue(surface.VisitedFileSystemEntryCount > surface.MaxVisitedFileSystemEntryCount);
        FieldInfo field = typeof(ChartPackage).GetField("packageInstallSurfaceSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Package source-surface cache field is unavailable.");
        field.SetValue(package, surface);
    }

    /// <summary>実索引・保留DBの20パッケージで保持方針を末端まで渡し、次の通常操作では方針を残しません。BMS/BMSON混在と外側バッチの適用を確認し、先頭反映で設定97から100へ変更しても全兄弟に捕捉済み97を適用します。</summary>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_TwentyPackagesKeepPolicyIsOperationLocal()
    {
        bool normalPhase = false;
        int changed = 0;
        using var observer = new RecordingInstallEstimationExecutionObserver(attemptObserver: _ =>
        {
            if (normalPhase && Interlocked.CompareExchange(ref changed, 1, 0) == 0) { testSettings.InstallDestinationAutoApplyMinAudioHealth = 100; }
        });
        testSettings.InstallDestinationAutoApplyMinAudioHealth = 97;
        WithTemporaryLibrary((root, db, library) =>
        {
            string strong = Path.Combine(root, "Strong");
            string unrelated = Path.Combine(root, "Unrelated");
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(strong, "base.bms", "#TITLE Song\n#ARTIST Artist\n")),
                ReadChart(CreateBmsFileWithContents(unrelated, "base.bms", "#TITLE ZZZZ\n#ARTIST XXXX\n"))];
            string[] names = Enumerable.Range(0, 100).Select(index => "sound" + index + ".wav").ToArray();
            foreach (string name in names) { File.WriteAllText(Path.Combine(unrelated, name), "audio"); }
            foreach (string name in names.Take(97)) { File.WriteAllText(Path.Combine(strong, name), "audio"); }
            File.WriteAllText(Path.Combine(strong, "extra.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(strong, unrelated));
            ChartPackage[] packages = Enumerable.Range(0, 20).Select(index =>
            {
                string source = Path.Combine(root, "Pending", index.ToString());
                string bmsBody = "#TITLE Song\n#ARTIST Artist\n" + string.Concat(names.Select((name, soundIndex) =>
                    "#WAV" + (soundIndex + 1).ToString("X2") + " " + name + "\n#00101:" + (soundIndex + 1).ToString("X2") + "\n"));
                string path = CreateBmsFileWithContents(source, index % 2 == 0 ? "pending.bms" : "pending.bmson", bmsBody);
                if (index % 2 != 0)
                {
                    File.WriteAllText(path, "{\"info\":{\"title\":\"Song\",\"artist\":\"Artist\"},\"sound_channels\":["
                        + string.Join(",", names.Select(name => "{\"name\":\"" + name + "\",\"notes\":[{\"x\":1,\"y\":0}]}")) + "]}");
                }
                ChartFile chart = index % 2 == 0 ? ReadChart(path) : ChartTestValues.ReadBmson(path);
                var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart(chart)]);
                package.path = source;
                return package;
            }).ToArray();
            SeedPendingPackages(library, db, packages);
            library.SearchEstimatedInstallationDirectory(packages, InstallEstimationCandidatePolicy.KeepUnrelated);
            foreach (ChartPackage package in packages)
            {
                ChartFile chart = package.ChartEntries.Single().Chart;
                Assert.IsTrue(string.IsNullOrWhiteSpace(chart.InstallDestination));
                Assert.AreEqual("ZZZZ", chart.InstallDestinationTitle);
                CollectionAssert.AreEqual(new[] { unrelated, strong }, chart.InstallDestinationSuggestions.ToArray());
                Assert.IsFalse(chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
            normalPhase = true;
            library.SearchEstimatedInstallationDirectory(packages);
            foreach (ChartPackage package in packages)
            {
                ChartFile chart = package.ChartEntries.Single().Chart;
                Assert.AreEqual(strong, chart.InstallDestination);
                Assert.AreEqual("Song", chart.InstallDestinationTitle);
                Assert.AreEqual(0, chart.InstallDestinationSuggestions.Count);
                Assert.IsFalse(chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
            Assert.AreEqual(1, changed);
            Assert.AreEqual(100, testSettings.InstallDestinationAutoApplyMinAudioHealth);
        }, observer);
    }

    /// <summary>混在パッケージの未所持群で決まるRelatedの根拠を、選択・空編集・再選択では適用先全群へ一括投影します。所持多数派による比較対象の逆転を防ぎ、少数ペアの追加・削除・所属変更後も同じ比較群で明示再評価します。</summary>
    [TestMethod]
    public void SetPendingInstallDestination_RelatedEvidenceFollowsGroupAndCatalogChanges()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Destination");
            string other = Path.Combine(root, "Other");
            string minorityPath = CreateBmsFileWithContents(Path.Combine(destination, "Child"), "minority.bms", "#TITLE Song\n#ARTIST Original Artist\n");
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "a.bms", "#TITLE ZZZZ\n#ARTIST XXXX\n")),
                ReadChart(CreateBmsFileWithContents(destination, "b.bms", "#TITLE ZZZZ\n#ARTIST XXXX\n#GENRE other\n")),
                ReadChart(CreateBmsFileWithContents(destination, "c.bms", "#TITLE ZZZZ\n#ARTIST XXXX\n#GENRE third\n")),
                ReadChart(minorityPath), ReadChart(CreateBmsFileWithContents(other, "other.bms", "#TITLE Other\n#ARTIST Other\n"))];
            File.WriteAllText(Path.Combine(destination, "tone.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            string source = Path.Combine(root, "Pending");
            PackageChartEntry[] entries = Enumerable.Range(0, 2).Select(index => PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source,
                index + ".bms", "#TITLE Song\n#ARTIST Artist\n#WAV01 tone.wav\n#00111:01\n#GENRE " + index + "\n")))).ToArray();
            PackageChartEntry[] ownedEntries = library.BmsCharts.Where(chart => Path.GetDirectoryName(chart.Path) == destination)
                .Select(chart => PackageChartEntry.FromPath(CreateBmsFileWithContents(source, Path.GetFileName(chart.Path), File.ReadAllText(chart.Path)))).ToArray();
            var package = ChartPackage.FromChartEntries(entries.Concat(ownedEntries));
            package.path = source;
            SeedPendingPackages(library, db, package);
            library.SearchEstimatedInstallationDirectory(package);
            Assert.IsTrue(entries.All(entry => entry.Chart.InstallDestinationTitle == "Song" && entry.Chart.InstallDestinationArtist == "Original Artist"));
            Assert.IsTrue(ownedEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
            Assert.IsTrue(library.SetPendingInstallDestination(entries[0], destination));
            AssertEvidence("Song", "Original Artist");
            Assert.IsTrue(library.SetPendingInstallDestination(entries[0], string.Empty));
            AssertEvidence(string.Empty, string.Empty);
            Assert.IsTrue(entries.All(entry => entry.Chart.InstallDestinationSuggestions.Contains(destination)));
            Assert.IsTrue(library.SetPendingInstallDestination(entries[1], destination));
            AssertEvidence("Song", "Original Artist");

            CatalogOwnedCollectionOwner owner = typeof(BMSLibrary).GetField("catalogOwnedCollectionOwner", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(library)
                as CatalogOwnedCollectionOwner ?? throw new InvalidOperationException("Catalog owner is unavailable.");
            ChartFile added = ReadChart(CreateBmsFileWithContents(destination, "new.bms", "#TITLE Song\n#ARTIST Artist\n"));
            owner.ApplyCommittedChartUpsert([added], out _);
            Assert.IsTrue(library.SetPendingInstallDestination(entries[0], destination));
            AssertEvidence("Song", "Artist");
            owner.ApplyMutation([OwnedChartRemoveRequest.FromChart(owner.BmsRows.Single(chart => chart.Path == added.Path))], []);
            Assert.IsTrue(library.SetPendingInstallDestination(entries[0], destination));
            AssertEvidence("Song", "Original Artist");
            ChartFile minority = owner.BmsRows.Single(chart => chart.Path == minorityPath);
            string movedPath = Path.Combine(other, "moved.bms");
            File.Move(minorityPath, movedPath);
            owner.ApplyMutation([], [new LibraryChartPathChange { Chart = minority, OldPath = minorityPath, NewPath = movedPath }]);
            Assert.IsTrue(library.SetPendingInstallDestination(entries[0], destination));
            AssertEvidence("ZZZZ", "XXXX");
            Assert.IsTrue(library.SetPendingInstallDestination(entries[1], other));
            AssertEvidence("Song", "Original Artist");

            void AssertEvidence(string title, string artist)
            {
                foreach (PackageChartEntry entry in package.ChartEntries)
                {
                    Assert.AreEqual(title, entry.Chart.InstallDestinationTitle);
                    Assert.AreEqual(artist, entry.Chart.InstallDestinationArtist);
                }
            }
        });
    }

    /// <summary>複合入口と再導入の実入口で設定97を一回捕捉します。先頭反映で100へ変更しても、後続の独立譜面を含む兄弟は97%の候補を同じ判断で自動設定します。</summary>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchInstallationDirectory_CombinedAndReinstallKeepCapturedAudioThreshold(bool reinstall)
    {
        testSettings.InstallDestinationAutoApplyMinAudioHealth = 97;
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "base.bms", "#TITLE Song\n#ARTIST Artist\n"))];
            string[] names = Enumerable.Range(0, 100).Select(index => "sound" + index + ".wav").ToArray();
            foreach (string name in names.Take(97)) { File.WriteAllText(Path.Combine(destination, name), "audio"); }
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            string body = "#TITLE Song\n#ARTIST Artist\n" + string.Concat(names.Select((name, index) =>
                "#WAV" + (index + 1).ToString("X2") + " " + name + "\n#00101:" + (index + 1).ToString("X2") + "\n"));
            PackageChartEntry[] entries = Enumerable.Range(0, 2).Select(index => PackageChartEntry.FromPath(
                CreateBmsFileWithContents(Path.Combine(root, "Pending", index.ToString()), "pending.bms", body))).ToArray();
            int changed = 0;
            entries[0].PropertyChanged += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(entries[0].Chart.InstallDestination) && Interlocked.CompareExchange(ref changed, 1, 0) == 0)
                {
                    testSettings.InstallDestinationAutoApplyMinAudioHealth = 100;
                }
            };
            if (reinstall)
            {
                library.SearchCorrectInstallationDirectoryCharts(entries);
            }
            else
            {
                var package = ChartPackage.FromChartEntries([entries[0]]);
                package.path = Path.GetDirectoryName(entries[0].Chart.Path);
                SeedPendingPackages(library, db, package);
                library.SearchEstimatedInstallationDirectory([package], [entries[1]], InstallEstimationCandidatePolicy.RelatedOnly);
            }
            Assert.AreEqual(1, changed);
            Assert.AreEqual(100, testSettings.InstallDestinationAutoApplyMinAudioHealth);
            foreach (PackageChartEntry entry in entries)
            {
                Assert.AreEqual(destination, entry.Chart.InstallDestination);
                Assert.AreEqual("Song", entry.Chart.InstallDestinationTitle);
                Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
                Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
        });
    }

    /// <summary>loose譜面の一意ハッシュ入口へ明示保持を伝播します。TITLEのない実BMSONは判定不能の候補を保持操作だけで示し、通常操作では表示を空に戻します。</summary>
    [TestMethod]
    public void SearchEstimatedInstallationDirectoryForLooseCharts_KeepUnavailableHashCandidateOnlyForCurrentOperation()
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            string source = Path.Combine(root, "Pending");
            ChartFile owned = ChartTestValues.ReadBmson(CreateBmsonFile(destination, "owned.bmson", string.Empty, "Artist"));
            library.BmsonCharts = [owned];
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "base.bms", "#TITLE Song\n#ARTIST Artist\n"))];
            File.WriteAllText(Path.Combine(destination, "sound.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            var entry = PackageChartEntry.FromChart(ChartTestValues.ReadBmson(CreateBmsonFile(source, "pending.bmson", string.Empty, "Artist")));
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            SeedPendingPackages(library, db, package);
            library.SearchEstimatedInstallationDirectoryForLooseCharts([entry], InstallEstimationCandidatePolicy.KeepUnrelated);
            Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
            Assert.AreEqual("Song", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { destination }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataUnavailable));
            library.SearchEstimatedInstallationDirectoryForLooseCharts([entry]);
            Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
            Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, entry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, entry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
        });
    }

    private readonly BeMusicSeeker.Properties.Settings testSettings = MainWindowViewModelTestFactory.CreateIsolatedSettings();

    /// <summary>混在の対象は未所持群だけの最頻ペアとし、所持群を混ぜた逆転や全譜面一致の要求を検出する。使用音源が完全な候補だけをStrongで設定する。</summary>
    /// <remarks>実譜面・実保留DBから個別入口を通します。Strongの文字列条件表はサービス側へ分担します。</remarks>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SearchEstimatedInstallationDirectory_HashCandidateUsesMissingGroupDominantPairWithCompleteAudio(bool strong)
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string source = Path.Combine(root, "Pending", "Mixed");
            string destination = Path.Combine(root, "Installed", "Candidate");
            var installed = new List<ChartFile>();
            var pending = new List<PackageChartEntry>();
            // 所持4件を混ぜるとOtherが最頻になり、未所持群だけならSongが最頻になる。
            for (int i = 0; i < 4; i++)
            {
                string contents = "#TITLE Other\r\n#ARTIST Artist\r\n#GENRE " + i + "\r\n";
                installed.Add(ReadChart(CreateBmsFileWithContents(destination, "owned" + i + ".bms", contents)));
                pending.Add(PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "owned" + i + ".bms", contents))));
            }
            for (int i = 0; i < 5; i++)
            {
                installed.Add(ReadChart(CreateBmsFileWithContents(destination, "representative" + i + ".bms", "#TITLE Song\r\n#ARTIST Artist\r\n#GENRE rep" + i + "\r\n")));
            }
            for (int i = 0; i < 3; i++)
            {
                string title = i < 2 ? (strong ? "Song" : "Different") : "Other";
                pending.Add(PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "missing" + i + ".bms", "#TITLE " + title + "\r\n#ARTIST Artist\r\n#WAV01 absent.wav\r\n#00111:01\r\n#GENRE missing" + i + "\r\n"))));
            }
            library.BmsCharts = installed;
            var package = ChartPackage.FromChartEntries(pending);
            package.path = source;
            SeedPendingPackages(library, db, package);
            File.WriteAllText(Path.Combine(destination, "absent.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            library.SearchEstimatedInstallationDirectory(package);
            foreach (PackageChartEntry entry in package.ChartEntries.Where(entry => Path.GetFileName(entry.Chart.Path).StartsWith("missing", StringComparison.Ordinal)))
            {
                Assert.AreEqual(strong ? destination : string.Empty, entry.Chart.InstallDestination ?? string.Empty);
                Assert.AreEqual(strong ? "Song" : "Other", entry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", entry.Chart.InstallDestinationArtist);
                Assert.AreEqual(!strong, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                if (!strong) { CollectionAssert.AreEqual(new[] { destination }, entry.Chart.InstallDestinationSuggestions.ToArray()); }
            }
        });
    }

    /// <summary>一括ハッシュ評価の拒否を実pipelineからentryへ反映し、候補・表示・警告、SEARCHING解除と進捗終端を保証する。</summary>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_BatchHashMetadataRejectionPublishesCandidatesAndCompletes()
    {
        var observer = new RecordingInstallEstimationExecutionObserver();
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            const string owned = "#TITLE Base\r\n#ARTIST Artist\r\n";
            library.BmsCharts = [ReadChart(CreateBmsFileWithContents(destination, "base.bms", owned))];
            var packages = new List<ChartPackage>();
            for (int i = 0; i < 2; i++)
            {
                string source = Path.Combine(root, "Pending", i.ToString());
                var package = ChartPackage.FromChartEntries([
                    PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "base.bms", owned))),
                    PackageChartEntry.FromChart(ReadChart(CreateBmsFileWithContents(source, "missing.bms", "#TITLE Other\r\n#ARTIST Artist\r\n")))]);
                package.path = source;
                packages.Add(package);
            }
            SeedPendingPackages(library, db, packages.ToArray());
            library.SearchEstimatedInstallationDirectory(packages);
            foreach (ChartPackage package in packages)
            {
                PackageChartEntry entry = GetEntryByFileName(package, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
                Assert.AreEqual("Base", entry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", entry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { destination }, entry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
            }
        }, observer);
        Assert.AreEqual(2, observer.WorkItems.Count);
        Assert.IsTrue(observer.Progress.Any(progress => progress.CompletedWorkCount == 2));
        Assert.IsFalse(observer.Progress.Last().IsActive);
    }

    /// <summary>単独bmsonの一意ハッシュ先でも所持フォルダの実在最頻ペアを比較し、異なる代表が多数なら候補・表示・警告を保って拒否する。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SearchEstimatedInstallationDirectory_StandaloneBmsonHashRequiresFolderStrong(bool strong)
    {
        WithTemporaryLibrary((root, db, library) =>
        {
            string destination = Path.Combine(root, "Installed");
            string source = Path.Combine(root, "Pending");
            string pendingPath = CreateBmsonFile(source, "pending.bmson", "Song", "Artist");
            ChartFile owned = ChartTestValues.ReadBmson(CreateBmsonFile(destination, "owned.bmson", "Song", "Artist"));
            library.BmsonCharts = [owned];
            string title = strong ? "Song" : "Other";
            library.BmsCharts = Enumerable.Range(0, 2).Select(i => ReadChart(CreateBmsFileWithContents(destination, i + ".bms", "#TITLE " + title + "\r\n#ARTIST Artist\r\n#GENRE " + i + "\r\n"))).ToList();
            var entry = PackageChartEntry.FromChart(ChartTestValues.ReadBmson(pendingPath));
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = pendingPath;
            SeedPendingPackages(library, db, package);
            File.WriteAllText(Path.Combine(destination, "sound.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destination));
            library.SearchEstimatedInstallationDirectory(entry, asParallel: false);
            Assert.AreEqual(strong ? destination : string.Empty, entry.Chart.InstallDestination ?? string.Empty);
            Assert.AreEqual("Song", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist", entry.Chart.InstallDestinationArtist);
            if (!strong)
            {
                CollectionAssert.AreEqual(new[] { destination }, entry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            }
        });
    }

    /// <summary>分割した二つの保留に同じ導入先があっても、単一パッケージの再推定が公開集合と保存行を勝手に結合しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageA");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageA");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(firstPackage);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>譜面指定の再推定でも、同じ導入先を持つ別の保留パッケージを公開集合や保存行から失わせない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectoryByFile_DoesNotRegroupSplitPackagesWhenPendingDestinationsMatch()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageB");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageB");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory(PackageChartEntry.FromChart((firstPackage.GetBmsChartsForTest().Single())), asParallel: false, fixMode: false);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>別entryから同じBmsonパスを指定すると、保留パッケージを解決して保留理由を解除し、対象パスを保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectoryByFiles_MatchesAdapterlessBmsonPackageByChartPath()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonAdapterless");
            string chartPath = CreateBmsonFile(sourceDirectoryPath, "chart.bmson", "Adapterless", "Test");
            ChartFile bmsonSong = ChartTestValues.ReadBmson(chartPath);
            var pendingPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart((bmsonSong))
            ]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            pendingPackage.DeferredEstimateReason = PendingEstimateDeferredReason.CompleteSourceAudio;
            var selectedChart = PackageChartEntry.FromChart((bmsonSong));

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, pendingPackage);


            library.SearchEstimatedInstallationDirectory([selectedChart], asParallel: false, fixMode: false);

            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);

            Assert.AreEqual(chartPath, entry.Chart.Path);
        });
    }



    /// <summary>二件の一括推定が外側並列・内側単一の契約で両対象を処理し、対象名付きの進捗を全件適用から非稼働へ終える。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MultiPackageBatchReportsExecutionPolicyAndProgress()
    {

        const int packageCount = 2;
        int expectedWorkItemDegree = Math.Max(1, Environment.ProcessorCount - 1);
        int expectedMaxActive = Math.Min(packageCount, expectedWorkItemDegree);
        using var observer = new RecordingInstallEstimationExecutionObserver(expectedMaxActive);

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "ObserverFirst");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "ObserverSecond");
            string firstPendingPath = CreateBmsFile(firstSourceDirectoryPath, "first.bms", "Observer First");
            string secondPendingPath = CreateBmsFile(secondSourceDirectoryPath, "second.bms", "Observer Second");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(firstPendingPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(secondPendingPath);

            // 未所持の対応譜面を一件ずつ持つ二つのパッケージから、二件の評価要求を実受付する。
            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);
        }, observer);

        Assert.AreEqual(packageCount, observer.WorkItems.Count);
        Assert.AreEqual(expectedMaxActive, observer.MaxActive);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount).ToArray(),
            observer.WorkItems.Select(item => item.OrderIndex).OrderBy(order => order).ToArray());
        Assert.IsTrue(observer.WorkItems.All(item => item.Source == PendingInstallEstimateBatchSource.ManualReestimate));
        Assert.IsTrue(observer.WorkItems.All(item => item.WorkItemDegree == expectedWorkItemDegree));
        Assert.IsTrue(observer.WorkItems.All(item => item.CandidateEvaluationDegree == 1));
        CollectionAssert.AreEquivalent(
            new[] { "first.bms", "second.bms" },
            observer.WorkItems.Select(item => item.DisplayName).ToArray());

        List<InstallEstimationProgressObservation> activeProgress =
            [.. observer.Progress.Where(progress => progress.IsActive)];
        Assert.IsTrue(activeProgress.Count > 0);
        Assert.IsTrue(activeProgress.All(progress =>
            progress.Source == InstallEstimationProgressSource.ManualReestimate
            && progress.TotalWorkCount == packageCount
            && progress.CompletedWorkCount >= 0
            && progress.CompletedWorkCount <= packageCount));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount + 1).ToArray(),
            activeProgress.Select(progress => progress.CompletedWorkCount).Distinct().ToArray());
        InstallEstimationProgressObservation inactiveProgress = observer.Progress.Last();
        Assert.IsFalse(inactiveProgress.IsActive);
        Assert.AreEqual(InstallEstimationProgressSource.None, inactiveProgress.Source);
        Assert.AreEqual(0, inactiveProgress.TotalWorkCount);
        Assert.AreEqual(0, inactiveProgress.CompletedWorkCount);

        Assert.AreEqual(packageCount, observer.Attempts.Count);
        Assert.IsTrue(observer.Attempts.All(attempt =>
            attempt.Source == PendingInstallEstimateBatchSource.ManualReestimate
            && !string.IsNullOrWhiteSpace(attempt.DisplayName)));
        CollectionAssert.AreEqual(
            Enumerable.Range(0, packageCount).ToArray(),
            observer.Attempts.Select(attempt => attempt.OrderIndex).OrderBy(order => order).ToArray());
    }

    /// <summary>一括推定の観測先が失敗したとき、元例外をそのまま呼出し元へ伝え、正常終了に置き換えない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ObserverFailureIsPropagated()
    {

        var observer = new ThrowingInstallEstimationExecutionObserver();
        InvalidOperationException failure = Assert.ThrowsException<InvalidOperationException>(() =>
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                ChartPackage firstPackage = CreatePendingSingleFilePackage(
                    CreateBmsFile(Path.Combine(tempRootPath, "Pending", "FailureFirst"), "first.bms", "Failure First"));
                ChartPackage secondPackage = CreatePendingSingleFilePackage(
                    CreateBmsFile(Path.Combine(tempRootPath, "Pending", "FailureSecond"), "second.bms", "Failure Second"));
                library.BmsCharts = [];
                SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

                library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);
            }, observer));
        Assert.AreSame(observer.Failure, failure);
    }

    /// <summary>単一譜面の相対リソースから入力元を除く導入先を選び、無関係な下位フォルダの走査警告を付けない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_SingleFileRelativeResourceSelectsExternalCandidateWithoutSourceScanWarning()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "RelativeSingleFile");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "RelativeCandidate");
            string sourceSoundDirectoryPath = Path.Combine(sourceDirectoryPath, "sound");
            string candidateSoundDirectoryPath = Path.Combine(candidateDirectoryPath, "sound");
            string unrelatedNestedDirectoryPath = Path.Combine(sourceDirectoryPath, "Unrelated", "Nested");
            Directory.CreateDirectory(sourceSoundDirectoryPath);
            Directory.CreateDirectory(candidateSoundDirectoryPath);
            Directory.CreateDirectory(unrelatedNestedDirectoryPath);
            string pendingPath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "pending.bms",
                "#PLAYER 1\r\n#TITLE Relative\r\n#ARTIST Test\r\n#WAVAA sound/00.wav\r\n#00111:AA\r\n");
            string candidatePath = CreateBmsFileWithContents(
                candidateDirectoryPath,
                "candidate.bms",
                "#PLAYER 1\r\n#TITLE Relative\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(sourceSoundDirectoryPath, "00.wav"), "source");
            File.WriteAllText(Path.Combine(candidateSoundDirectoryPath, "00.wav"), "candidate");
            File.WriteAllText(Path.Combine(unrelatedNestedDirectoryPath, "noise.bin"), "noise");
            ChartPackage pendingPackage = CreatePendingSingleFilePackage(pendingPath);
            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(candidatePath))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(sourceDirectoryPath, ["pending.bms", Path.Combine("sound", "00.wav")]);
            cache.AddDir(candidateDirectoryPath, ["candidate.bms", Path.Combine("sound", "00.wav")]);
            SetLibraryResourceIndex(library, cache);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry entry = GetOnlyEntry(pendingPackage);
            Assert.AreEqual(candidateDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreNotEqual(sourceDirectoryPath, entry.Chart.InstallDestination);
            Assert.IsTrue(entry.ResourceSnapshot.AudioReferences.Single().IsPathAware);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SourceSurfaceScanLimitExceeded));
        });
    }

    /// <summary>明示された適格ディレクトリの分割保留を二譜面の一パッケージに再編成し、保存行もディレクトリ単位へ揃える。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_RegroupsSplitPackagesWhenEligibleDirectoryIsSupplied()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageC");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageC");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>BMSとBmsonの分割保留を同じディレクトリへ再編成しても、所持Bmsonに未所持BMSの導入先を転用しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_UsesAdapterlessBmsonEntriesForEligibility()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageBmsonRegroup");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Regroup Bmson", "Bmson Artist");
            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var adapterlessPackage = ChartPackage.FromChartEntries(
            [
                PackageChartEntry.FromChart((pendingSong))
            ]);
            adapterlessPackage.path = pendingBmsonPath;
            adapterlessPackage.delete_parent = true;
            ChartPackage bmsPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "chart.bms", "Regroup Bms"), destinationDirectoryPath);
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Regroup Bmson", "Bmson Artist");
            ChartFile installedSong = ChartTestValues.ReadBmson(installedBmsonPath);


            library.BmsCharts = [];
            library.BmsonCharts = [installedSong];
            SeedPendingPackages(library, songDbPath, adapterlessPackage, bmsPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            PackageChartEntry regroupedBmson = regroupedPackage.ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bmson);
            PackageChartEntry regroupedBms = regroupedPackage.ChartEntries
                .Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
            Assert.AreEqual(pendingBmsonPath, regroupedBmson.Chart.Path);

            Assert.IsTrue(string.IsNullOrWhiteSpace(regroupedBmson.Chart.InstallDestination));
            Assert.AreEqual(destinationDirectoryPath, regroupedBms.Chart.InstallDestination);
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>古い捕捉パスを持つentryも現在のBmson対象で重複排除し、再編成した公開集合と保存行に一件だけ残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DeduplicatesAdapterlessBmsonByChartTarget()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonDuplicateRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageBmsonDuplicateRegroup");
            string pendingBmsonPath = CreateHealthyBmsonFile(sourceDirectoryPath, "pending.bmson", "Duplicate Bmson", "Bmson Artist");
            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var firstEntry = PackageChartEntry.FromChart((pendingSong));
            string staleSnapshotPath = Path.Combine(sourceDirectoryPath, "pending-stale-snapshot.bmson");
            ChartFile staleCapture = WithChartPath(pendingSong, staleSnapshotPath);
            var secondEntry = PackageChartEntry.FromChart(staleCapture);
            secondEntry.ApplyCurrentChart(pendingSong);
            var firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = pendingBmsonPath;
            firstPackage.delete_parent = true;
            var secondPackage = ChartPackage.FromChartEntries([secondEntry]);
            secondPackage.path = staleSnapshotPath;
            secondPackage.delete_parent = true;
            string installedBmsonPath = CreateHealthyBmsonFile(destinationDirectoryPath, "installed.bmson", "Duplicate Bmson", "Bmson Artist");
            ChartFile installedSong = ChartTestValues.ReadBmson(installedBmsonPath);



            library.BmsCharts = [];
            library.BmsonCharts = [installedSong];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = library.ChartPackagesPending.Single();
            Assert.AreEqual(sourceDirectoryPath, regroupedPackage.path);
            Assert.AreEqual(1, regroupedPackage.ChartEntries.Count);

            Assert.AreEqual(pendingBmsonPath, regroupedPackage.ChartEntries.Single().Chart.Path);
            Assert.IsTrue(string.IsNullOrWhiteSpace(regroupedPackage.ChartEntries.Single().Chart.InstallDestination));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>全譜面が所持済みの再編成では導入先を付けず、所持済み警告と元行の音源見送り理由・評価先を保って保存行を結合する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DoesNotApplyInstallDestinationToInstalledOnlyEntries()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "InstalledOnlyRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "InstalledOnlyRegroup");
            string firstContents = "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n";
            string secondContents = "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n";
            string pendingAPath = CreateBmsFileWithContents(sourceDirectoryPath, "a.bms", firstContents);
            string pendingBPath = CreateBmsFileWithContents(sourceDirectoryPath, "b.bms", secondContents);
            string installedAPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed-a.bms", firstContents);
            string installedBPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed-b.bms", secondContents);
            ChartPackage firstPackage = CreatePendingSingleFilePackage(pendingAPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(pendingBPath);

            string originalAudioReason = string.Format(BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAudioUnverifiable, @"C:\OriginalCandidate");
            GetOnlyEntry(firstPackage).SetWarning(ChartWarningKind.InstallEstimationAudioUnverifiable, originalAudioReason);
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => string.IsNullOrWhiteSpace(entry.Chart.InstallDestination)));
            Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled)));
            Assert.AreEqual(originalAudioReason, GetEntryByFileName(regroupedPackage, "a.bms").Chart.Warnings.Single(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioUnverifiable).Message);
            Assert.IsFalse(GetEntryByFileName(regroupedPackage, "b.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioUnverifiable));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>所持・未所持の混在を再編成すると、未所持entryだけへ導入先を付け、所持entryの警告と空の導入先を保つ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_AppliesInstallDestinationOnlyToMissingEntries()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedRegroup");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedRegroup");
            string installedContents = "#PLAYER 1\r\n#TITLE Installed\r\n#ARTIST Test\r\n";
            string pendingInstalledPath = CreateBmsFileWithContents(sourceDirectoryPath, "installed.bms", installedContents);
            string pendingMissingPath = CreateBmsFile(sourceDirectoryPath, "missing.bms", "Missing");
            string installedPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", installedContents);
            ChartPackage installedPackage = CreatePendingSingleFilePackage(pendingInstalledPath);
            ChartPackage missingPackage = CreatePendingSingleFilePackage(pendingMissingPath, destinationDirectoryPath);

            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedPath))
            ];
            SeedPendingPackages(library, songDbPath, installedPackage, missingPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, expectedFileCount: 2);
            PackageChartEntry installedEntry = GetEntryByFileName(regroupedPackage, "installed.bms");
            PackageChartEntry missingEntry = GetEntryByFileName(regroupedPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(installedEntry.Chart.InstallDestination));
            Assert.IsTrue(installedEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.AreEqual(destinationDirectoryPath, missingEntry.Chart.InstallDestination);
            Assert.IsFalse(missingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            CollectionAssert.AreEqual(new[] { sourceDirectoryPath }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>再編成で単一ファイルの親が同梱範囲になれば現在の不足を解消し、元の音源見送り理由と評価先は元行だけへ保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_ReevaluatesBundledResourcesAndPreservesOriginalAudioReason()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageD");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageD");
            ChartPackage strictWarningPackage = CreatePendingSingleFilePackage(
                CreateBmsFileWithContents(
                    sourceDirectoryPath,
                    "strict.bms",
                    "#PLAYER 1\r\n#TITLE Strict\r\n#ARTIST Test\r\n#WAVAA sound_missing.wav\r\n#00111:AA\r\n"),
                destinationDirectoryPath);
            ChartPackage normalPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "normal.bms", "Normal"), destinationDirectoryPath);

            foreach (PackageChartEntry entry in strictWarningPackage.ChartEntries.Concat(normalPackage.ChartEntries))
            {
                BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
            }

            File.WriteAllText(Path.Combine(sourceDirectoryPath, "sound_missing.ogg"), "bundled after regroup");
            string originalAudioReason = string.Format(BeMusicSeeker.Properties.Resources.Warning_InstallEstimationAudioMissing, @"C:\OriginalCandidate");
            GetOnlyEntry(strictWarningPackage).SetWarning(ChartWarningKind.InstallEstimationAudioMissing, originalAudioReason);
            library.BmsCharts = [];
            var resourceIndex = new DirectoryResourceLookupCache();
            resourceIndex.AddDir(destinationDirectoryPath, []);
            SetLibraryResourceIndex(library, resourceIndex);
            ApplySingleFileWarnings(strictWarningPackage, normalPackage);
            SeedInitializedPendingPackages(library, songDbPath, strictWarningPackage, normalPackage);
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth(strictWarningPackage.ChartEntries, strictWarningPackage, resourceIndex);
            Assert.AreEqual(0, GetOnlyEntry(strictWarningPackage).Chart.WAVHealth);
            Assert.IsTrue(GetOnlyEntry(strictWarningPackage).Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            PackageChartEntry strictWarningEntry = GetEntryByFileName(regroupedPackage, "strict.bms");
            PackageChartEntry normalEntry = GetEntryByFileName(regroupedPackage, "normal.bms");
            Assert.AreEqual(100, strictWarningEntry.Chart.WAVHealth);
            Assert.IsFalse(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual(originalAudioReason, strictWarningEntry.Chart.Warnings.Single(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing).Message);
            Assert.IsFalse(normalEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            Assert.IsFalse(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.SingleBmsFile));
            Assert.AreEqual(string.Empty, ChartWarningTestHelpers.BuildDigestText(normalEntry));
            library.RemoveInstallDestination(regroupedPackage.ChartEntries, regroupedPackage);
            Assert.AreEqual(0, strictWarningEntry.Chart.WAVHealth);
            Assert.IsTrue(strictWarningEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(strictWarningEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        });
    }

    /// <summary>再編成は項目ごとの確定済み表示ペアと空欄・候補を保持し、フォルダ代表値への再投影を行わない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_PreservesFinalizedMetadataAndSuggestions()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageRegroupMetadata");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageRegroupMetadata");
            string minorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "a.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            string installedFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#SUBTITLE Alternate Name\r\n#ARTIST Installed Artist OBJ: Diff\r\n");
            string majorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "z.bms", "#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            PackageChartEntry firstPendingEntry = GetOnlyEntry(firstPackage);
            firstPendingEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(destinationDirectoryPath, "Minority", "Other", [destinationDirectoryPath]));
            GetOnlyEntry(secondPackage).RestoreInstallDestinationState(new PackageChartInstallDestinationState(destinationDirectoryPath, string.Empty, string.Empty, []));
            firstPendingEntry.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);

            library.BmsCharts = [.. new[] { minorityPath, installedFilePath, majorityPath }
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, sourceDirectoryPath, destinationDirectoryPath, expectedFileCount: 2);
            foreach (PackageChartEntry regroupedEntry in regroupedPackage.ChartEntries)
            {
                Assert.AreEqual(Path.GetFileName(regroupedEntry.Chart.Path) == "a.bms" ? "Minority" : string.Empty, regroupedEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual(Path.GetFileName(regroupedEntry.Chart.Path) == "a.bms" ? "Other" : string.Empty, regroupedEntry.Chart.InstallDestinationArtist);
            }
            Assert.AreEqual(1, firstPendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.AreEqual(destinationDirectoryPath, firstPendingEntry.Chart.InstallDestinationSuggestions.Single());
        });
    }

    /// <summary>再編成のDB保存を失敗させると、元entryの導入先・候補・警告と二つの公開パッケージを変更しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DurableFailureLeavesSourceEntryStateUnchanged()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageRegroupDbFailure");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageRegroupDbFailure");
            string installedFilePath = CreateBmsFileWithContents(
                destinationDirectoryPath,
                "representative.bms",
                "#PLAYER 1\r\n#TITLE Resolved Title\r\n#ARTIST Resolved Artist\r\n");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            PackageChartEntry firstPendingEntry = GetOnlyEntry(firstPackage);
            firstPendingEntry.RestoreInstallDestinationState(new PackageChartInstallDestinationState(
                destinationDirectoryPath,
                "Original Title",
                "Original Artist",
                [Path.Combine(tempRootPath, "Suggested")]));
            firstPendingEntry.SetWarning(ChartWarningKind.InstallEstimationLowConfidence, BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationLowConfidence);
            PackageChartInstallDestinationState expectedDestinationState = firstPendingEntry.CaptureInstallDestinationState();
            string[] expectedWarnings = [.. firstPendingEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message)];

            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedFilePath))];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            // 再編成結果の準備後、最初の永続反映をDBパスのディレクトリで失敗させ、元entryへの先行適用を検出する。
            File.Delete(songDbPath);
            Directory.CreateDirectory(songDbPath);

            TargetInvocationException exception = Assert.ThrowsException<TargetInvocationException>(
                () => InvokeRegroupForSourceDirectories(library, sourceDirectoryPath));
            Assert.IsInstanceOfType<SQLite.SQLiteException>(exception.InnerException);

            PackageChartInstallDestinationState actualDestinationState = firstPendingEntry.CaptureInstallDestinationState();
            Assert.AreEqual(expectedDestinationState.Destination, actualDestinationState.Destination);
            Assert.AreEqual(expectedDestinationState.Title, actualDestinationState.Title);
            Assert.AreEqual(expectedDestinationState.Artist, actualDestinationState.Artist);
            CollectionAssert.AreEqual(expectedDestinationState.Suggestions.ToArray(), actualDestinationState.Suggestions.ToArray());
            CollectionAssert.AreEqual(
                expectedWarnings,
                firstPendingEntry.Chart.Warnings.Select(warning => warning.Kind + "|" + warning.Message).ToArray());
            CollectionAssert.AreEqual(new[] { firstPackage, secondPackage }, library.ChartPackagesPending.ToArray());
        });
    }

    /// <summary>同じ入力元のディレクトリパッケージが既存なら、分割保留を追加結合せず公開集合と保存行を保つ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_DoesNotRegroupWhenDirectoryPackageAlreadyExists()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageE");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageE");
            CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A");
            CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B");
            var directoryPackage = new ChartPackage
            {
                path = sourceDirectoryPath,
                delete_parent = false
            };
            ChartPackage splitPackage = CreatePendingSingleFilePackage(Path.Combine(sourceDirectoryPath, "a.bms"), destinationDirectoryPath);

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, directoryPackage, splitPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, sourceDirectoryPath, splitPackage.path);
            CollectionAssert.AreEquivalent(new[] { sourceDirectoryPath, splitPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>健全な入力元として推定保留になったパッケージが混在すると、明示ディレクトリでも分割状態と保存行を維持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void TryRegroupPendingPackagesForSourceDirectories_SkipsDeferredEstimatePackages()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageDeferred");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageDeferred");
            ChartPackage firstPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "a.bms", "Same A"), destinationDirectoryPath);
            ChartPackage secondPackage = CreatePendingSingleFilePackage(CreateBmsFile(sourceDirectoryPath, "b.bms", "Same B"), destinationDirectoryPath);
            firstPackage.DeferredEstimateReason = PendingEstimateDeferredReason.CompleteSourceAudio;

            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);

            InvokeRegroupForSourceDirectories(library, sourceDirectoryPath);

            AssertPendingPackagePaths(library, firstPackage.path, secondPackage.path);
            CollectionAssert.AreEquivalent(new[] { firstPackage.path, secondPackage.path }, LoadInstallPaths(songDbPath));
        });
    }

    /// <summary>明示保持操作では非関連の二候補を未確定にし、候補の表示ペア・両候補・非関連理由を表示用警告へ残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_LowConfidenceLeavesInstallDestinationEmptyAndShowsRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidence");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage, InstallEstimationCandidatePolicy.KeepUnrelated);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist A", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationUnrelated));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), string.Format(BeMusicSeeker.Properties.Resources.Warning_InstallEstimationUnrelated, candidateADirectoryPath));
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationUnrelated);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
        });
    }

    /// <summary>BmsonのStrong不成立の二候補も自動確定せず、同じentryへ代表メタデータと不一致候補を反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_AdapterlessBmsonLowConfidenceStaysOnChartEntry()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonLowConfidence");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var entry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.IsTrue(string.IsNullOrWhiteSpace(entry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist A", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

        });
    }

    /// <summary>複数Bmsonの一括推定でも、Strong不成立の対象に二候補と不一致警告を残し、先頭候補へ確定しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_BatchAdapterlessBmsonLowConfidenceStaysOnChartEntry()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string firstSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonBatchLowConfidence");
            string secondSourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonBatchOther");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string firstPendingBmsonPath = CreateBmsonFile(firstSourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            string secondPendingBmsonPath = CreateBmsonFile(secondSourceDirectoryPath, "other.bmson", "Other Bmson", "Other Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            var firstEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(firstPendingBmsonPath)));
            var firstPackage = ChartPackage.FromChartEntries([firstEntry]);
            firstPackage.path = firstSourceDirectoryPath;
            firstPackage.delete_parent = false;
            var secondEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(secondPendingBmsonPath)));
            var secondPackage = ChartPackage.FromChartEntries([secondEntry]);
            secondPackage.path = secondSourceDirectoryPath;
            secondPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, firstPackage, secondPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(firstSourceDirectoryPath, secondSourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory([firstPackage, secondPackage]);

            Assert.IsTrue(string.IsNullOrWhiteSpace(firstEntry.Chart.InstallDestination));
            Assert.AreEqual("Candidate A", firstEntry.Chart.InstallDestinationTitle);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, firstEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(firstEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    /// <summary>混在パッケージの所持先を解決できないと、未所持entryの一般推定へ迂回せず専用の保留理由と警告を付ける。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageUnresolvedInstalledDestination_DefersWithoutFallbackEstimation()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedUnresolved");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA missing.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.InstalledDestinationResolveFailed, pendingPackage.DeferredEstimateReason);
            Assert.IsTrue(GetEntryByFileName(pendingPackage, "installedA.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            Assert.IsTrue(GetEntryByFileName(pendingPackage, "installedB.bms").Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.AlreadyInstalled));
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestinationTitle));
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationResolveFailed);
        });
    }

    /// <summary>親ディレクトリ参照を持つ入力は推定を保留し、導入先や低確度候補を捏造せず未対応理由を通知用警告へ残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_UnsupportedParentResourcePath_WarnsAndSkipsEstimation()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageUnsupportedResourcePath");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "Candidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "unsupported.bms",
                "#PLAYER 1\r\n"
                + "#TITLE Unsupported Resource Path\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA ..\\Base\\sound.wav\r\n"
                + "#00111:AA\r\n");
            string candidateFilePath = CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Current Directory Resource Path\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath))]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;

            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(candidateFilePath))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.UnsupportedResourcePath, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingEntry = GetEntryByFileName(pendingPackage, "unsupported.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_UnsupportedResourcePath);
        });
    }

    /// <summary>現在ディレクトリ付き参照を正規化して候補を選びます。拡張子だけの使用参照は候補を保って自動適用を止め、未使用時は妨げず、双方で親参照非対応と誤分類しません。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchEstimatedInstallationDirectory_CurrentDirectoryResourcePath_NormalizesAndEstimates(bool usesEmptyKey)
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageCurrentDirectoryResourcePath");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "Candidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "current-dir.bms",
                "#PLAYER 1\r\n"
                + "#TITLE Current Directory Resource Path\r\n"
                + "#ARTIST Test\r\n"
                + "#WAVAA .\\sound.wav\r\n"
                + "#WAVAB .wav\r\n#WAVAC .ogg\r\n#BMPAA .png\r\n#BMPAB mystery.xyz\r\n#00104:AB\r\n"
                + (usesEmptyKey ? "#00101:ABAC\r\n#00104:AA\r\n" : "")
                + "#00111:AA\r\n");
            string candidateFilePath = CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Current Directory Resource Path\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "audio");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath))]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;

            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(candidateFilePath))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingEntry = GetEntryByFileName(pendingPackage, "current-dir.bms");
            if (usesEmptyKey)
            {
                Assert.IsTrue(string.IsNullOrEmpty(pendingEntry.Chart.InstallDestination));
                CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            }
            else
            {
                Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            }
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.UnsupportedResourcePath));
        });
    }

    /// <summary>混在パッケージでは所持ハッシュの候補順だけで確定せず、未所持譜面の最終評価に合う導入先を選ぶ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageMultipleCandidates_UsesFinalEvaluationWinner()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedWinner");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.AreEqual(installedBDirectoryPath, pendingMissingEntry.Chart.InstallDestination);
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
        });
    }

    /// <summary>混在パッケージの実在ペアと資源評価が同等なら、BMSとBmsonを通したディレクトリ固有ハッシュ数で一意の候補を選ぶ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageEquivalentCandidates_UsesDirectoryUniquePrimaryHashCount()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedCountTieBreak");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE A\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE B\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE A\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Song\r\n#ARTIST Test\r\n#GENRE B\r\n");
            // 追加bmsonは基本情報を持たせず、両候補の支持数を同等にして一意ハッシュ数だけの差を作ります。
            string extraABmsonPath = CreateBmsonFile(installedADirectoryPath, "extraA.bmson", "", "");
            File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(extraABmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.AreEqual(installedADirectoryPath, pendingMissingEntry.Chart.InstallDestination);
            Assert.AreEqual(0, pendingMissingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
        });
    }

    /// <summary>混在パッケージの複数有力候補を未確定で残し、通常の推定曖昧ではなく所持先曖昧の警告と両パスを示す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageMultipleViableCandidates_ShowsInstalledDestinationAmbiguousWarning()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAmbiguous");
            string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Target Song (A)\r\n#ARTIST Test\r\n");
            string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Target Song (B)\r\n#ARTIST Test\r\n");
            string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Missing)\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Target Song (A)\r\n#ARTIST Test\r\n");
            string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Target Song (B)\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
            ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
            ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            Assert.AreEqual(PendingEstimateDeferredReason.None, pendingPackage.DeferredEstimateReason);
            PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
            CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
            Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
            Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAmbiguous);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAmbiguous.Split('\n')[0]);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedADirectoryPath);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedBDirectoryPath);
        });
    }

    /// <summary>強いメタデータ一致と自動適用設定がある混在パッケージは先頭候補へ確定しても、全候補と自動適用の曖昧警告を保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageStrongMetadataWithSetting_AutoAppliesFirstCandidateAndKeepsAutoAppliedWarning()
    {

        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyStrong");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string installedAContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE A\r\n";
                string installedBContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE B\r\n";
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", installedAContents);
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", installedBContents);
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", installedAContents);
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", installedBContents);
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
                ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
                ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.AreEqual(installedADirectoryPath, pendingMissingEntry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", pendingMissingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingMissingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstalledDestinationAutoAppliedAmbiguous);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), BeMusicSeeker.Properties.Resources.Warning_InstalledDestinationAutoAppliedAmbiguous.Split('\n')[0]);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedADirectoryPath);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingMissingEntry), installedBDirectoryPath);
            });
        });
    }

    /// <summary>自動適用設定が無効なら、強いメタデータ一致があっても混在パッケージを確定せず両候補と所持先曖昧警告を残す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageStrongMetadataWithSettingOff_LeavesInstallDestinationEmpty()
    {

        WithAutoApplyAmbiguousInstallDestination(false, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyStrongOff");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string installedAContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE A\r\n";
                string installedBContents = "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n#GENRE B\r\n";
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", installedAContents);
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", installedBContents);
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", installedAContents);
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", installedBContents);
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
                ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
                ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
                Assert.AreEqual("Target Song", pendingMissingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingMissingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            });
        });
    }

    /// <summary>自動適用設定が有効でもStrong不成立の混在パッケージは未確定にし、不一致警告を残して自動適用済みとしない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedPackageWeakMetadataWithSetting_LeavesInstallDestinationEmpty()
    {

        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMixedAutoApplyWeak");
                string installedADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string installedBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string pendingInstalledAPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
                string pendingInstalledBPath = CreateBmsFileWithContents(sourceDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
                string pendingMissingPath = CreateBmsFileWithContents(sourceDirectoryPath, "missing.bms", "#PLAYER 1\r\n#TITLE Missing\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                string installedAPath = CreateBmsFileWithContents(installedADirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed A\r\n#ARTIST Test\r\n");
                string installedBPath = CreateBmsFileWithContents(installedBDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed B\r\n#ARTIST Test\r\n");
                File.WriteAllText(Path.Combine(installedADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(installedBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingInstalledA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledAPath));
                ChartFile pendingInstalledB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingInstalledBPath));
                ChartFile pendingMissing = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingMissingPath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingInstalledA, pendingInstalledB, pendingMissing]);
                pendingPackage.path = sourceDirectoryPath;
                pendingPackage.delete_parent = false;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedAPath)),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBPath))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, installedADirectoryPath, installedBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingMissingEntry = GetEntryByFileName(pendingPackage, "missing.bms");
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingMissingEntry.Chart.InstallDestination));
                CollectionAssert.AreEquivalent(new[] { installedADirectoryPath, installedBDirectoryPath }, pendingMissingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingMissingEntry));
                Assert.IsTrue(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous));
                Assert.IsFalse(pendingMissingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            });
        });
    }

    /// <summary>リソース同順位からStrong候補を適用し、所持現在値の変更後はキャッシュの失効で照合とMIX切断表示を同時に更新する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MetadataTieBreakSelectsMatchingCandidate()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataTieBreak");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA1.bms", "#PLAYER 1\r\n#TITLE Another Song\r\n#ARTIST Someone\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA2.bms", "#PLAYER 1\r\n#TITLE Another Song [7K]\r\n#ARTIST Someone\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB1.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB2.bms", "#PLAYER 1\r\n#TITLE Target Song (Hyper)\r\n#ARTIST Artist / obj: diff\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA1.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA2.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB1.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB2.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.AreEqual(candidateBDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Target Song", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);

            // 同じ実パスの所持現在値を変更し、既存キャッシュの失効でStrongと表示が同時に更新されることを確認します。
            library.RemoveInstallDestination([pendingEntry], pendingPackage);
            library.BmsCharts = [.. library.BmsCharts.Select(chart => chart with { RawTitle = "Ark (remix/5keys)", Artist = string.Empty, RawArtist = string.Empty })];
            library.SearchEstimatedInstallationDirectory(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));

        });
    }

    /// <summary>通常推定の強いメタデータ一致を自動適用する設定では先頭候補を選びつつ、曖昧警告と全候補を維持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_AmbiguousStrongMetadataWithSetting_AppliesDestinationAndKeepsWarning()
    {

        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
            {
                string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageAmbiguousStrongMetadata");
                string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
                string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
                string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
                CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Artist\r\n");
                File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
                File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

                ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
                ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
                pendingPackage.path = pendingFilePath;
                pendingPackage.delete_parent = true;
                library.BmsCharts =
                [
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                    BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
                ];
                SeedPendingPackages(library, songDbPath, pendingPackage);
                SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

                library.SearchEstimatedInstallationDirectory(pendingPackage);

                PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
                Assert.AreEqual(candidateADirectoryPath, pendingEntry.Chart.InstallDestination);
                Assert.AreEqual("Target Song", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist", pendingEntry.Chart.InstallDestinationArtist);
                Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
                CollectionAssert.AreEqual(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
                StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateBDirectoryPath);
            });
        });
    }

    /// <summary>リソースの単一候補でも別題・別作者または片側空作者なら確定せず、空作者の候補もTITLEを表示して不一致専用警告を渡す。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchEstimatedInstallationDirectory_MetadataMismatchLeavesInstallDestinationEmptyAndShowsSingleSuggestion(bool emptyArtist)
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataMismatch");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "OnlyCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", emptyArtist ? "#TITLE Target Song\r\n#ARTIST OBJ: Diff\r\n" : "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "dst");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateDirectoryPath, "candidate.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(emptyArtist ? "Target Song" : "Completely Different", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(emptyArtist ? string.Empty : "Another Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), BeMusicSeeker.Properties.Resources.Warning_InstallEstimationMetadataMismatchPrefix);
            StringAssert.Contains(ChartWarningTestHelpers.BuildDigestText(pendingEntry), BeMusicSeeker.Properties.Resources.WarningDigest_InstallEstimationMetadataMismatch);
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateDirectoryPath);
        });
    }

    /// <summary>候補が一件もないと、空の導入先・代表情報・候補一覧を保ち、曖昧さや不一致の警告を誤って付けない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_HighConfidenceWithoutCandidate_DoesNotSetLowConfidenceState()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageNoCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts = [];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            Assert.AreEqual(0, pendingEntry.Chart.WAVHealth);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    /// <summary>所持ハッシュで結合先が解決したBMSパッケージでは、全entryの導入先と代表タイトル・作者を揃える。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ResolvedPathUpdatesRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "PackageMergeResolved");
            string pendingFileAPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingA.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string pendingFileBPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingB.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string installedFileAPath = CreateBmsFileWithContents(destinationDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            string installedFileBPath = CreateBmsFileWithContents(destinationDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");

            string minorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "a.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            ChartFile pendingFileA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFileAPath));
            ChartFile pendingFileB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFileBPath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFileA, pendingFileB]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(minorityPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedFileAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedFileBPath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
                Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            }
        });
    }

    /// <summary>Bmsonの所持ハッシュから結合先を解決し、非空RawArtistに引きずられずArtistを正規化して代表表示へ渡す。空Artistも補完しない。</summary>
    /// <remarks>chart役割付きsubartistsを実解析し、対象と所持のRawArtistを異なる現在値へ替え、空作者行ではArtistを空にして、RawArtistを誤採用した一致・補完を検出する。実SQLiteと所持索引を接続し、falseは主作者、trueは空作者のStrongによる自動適用を確認する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SearchEstimatedInstallationDirectory_ResolvesBmsonFromInstalledBmsonHash(bool emptyArtist)
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonMergeResolved");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Installed Bmson", "Bmson Artist", "chart: Contributor");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Installed Bmson", "Bmson Artist", "chart: Contributor");
            ChartFile pendingBmson = ChartTestValues.ReadBmson(pendingBmsonPath);
            ChartFile installedBmson = ChartTestValues.ReadBmson(installedBmsonPath);
            Assert.AreNotEqual(installedBmson.RawArtist, installedBmson.Artist);
            pendingBmson = pendingBmson with { RawArtist = "Pending Raw Artist", Artist = emptyArtist ? string.Empty : pendingBmson.Artist };
            installedBmson = installedBmson with { RawArtist = "Installed Raw Artist", Artist = emptyArtist ? string.Empty : installedBmson.Artist };
            var pendingBmsonEntry = PackageChartEntry.FromChart(pendingBmson);
            var pendingPackage = ChartPackage.FromChartEntries([pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts = [];
            library.BmsonCharts = [installedBmson];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            File.WriteAllText(Path.Combine(destinationDirectoryPath, "sound.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destinationDirectoryPath));
            library.SearchEstimatedInstallationDirectory(pendingPackage);

            PackageChartEntry entry = pendingPackage.ChartEntries.Single();
            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual(emptyArtist ? string.Empty : "Bmson Artist", entry.Chart.InstallDestinationArtist);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

        });
    }

    /// <summary>所持Bmsonの修復推定は現在パスの自己一致を候補から外し、リソースを満たす別ディレクトリを選ぶ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchCorrectInstallationDirectoryCharts_BmsonOwnedChartIgnoresCurrentInstalledIndexSelfMatch()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonWrong");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonCorrect");
            string sourceBmsonPath = CreateBmsonFile(sourceDirectoryPath, "source.bmson", "Repair Bmson", "Bmson Artist");
            string destinationBmsPath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Repair Bmson\r\n#ARTIST Bmson Artist\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            Directory.CreateDirectory(destinationDirectoryPath);
            File.WriteAllText(Path.Combine(destinationDirectoryPath, "sound.wav"), "sound");
            ChartFile sourceSong = ChartTestValues.ReadBmson(sourceBmsonPath);
            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationBmsPath))];
            library.BmsonCharts = [sourceSong];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, destinationDirectoryPath));

            var entry = PackageChartEntry.FromChart((sourceSong));
            library.SearchCorrectInstallationDirectoryCharts([entry]);

            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);

        });
    }

    /// <summary>BMSとBmsonが混在しても両種の所持ハッシュを用いて、同じ結合先を全entryへ反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_ResolvesMixedBmsAndBmsonPackage()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeResolved");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergeResolved");
            string pendingBmsPath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Mixed BMS\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsPath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Mixed BMS\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Mixed Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(destinationDirectoryPath, "installed.bmson", "Mixed Bmson", "Mixed Artist");
            ChartFile pendingBms = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsPath));
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingBms)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            File.WriteAllText(Path.Combine(destinationDirectoryPath, "sound.wav"), "audio");
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destinationDirectoryPath));
            library.SearchEstimatedInstallationDirectory(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }

        });
    }

    /// <summary>混在譜面の所持先が分割されている場合、最多候補がStrong偽なら、その候補を保持して未設定にし、Strong真の少数候補や別の資源候補へ逃がさない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedSplitInstalledDirectoriesUsesMostMatchingDirectory()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeSplit");
            string primaryDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergePrimary");
            string secondaryDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "MixedMergeSecondary");
            string pendingBmsAPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingA.bms", "#PLAYER 1\r\n#TITLE Majority BMS A\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsBPath = CreateBmsFileWithContents(sourceDirectoryPath, "pendingB.bms", "#PLAYER 1\r\n#TITLE Majority BMS B\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsAPath = CreateBmsFileWithContents(primaryDestinationDirectoryPath, "installedA.bms", "#PLAYER 1\r\n#TITLE Majority BMS A\r\n#ARTIST Mixed Artist\r\n");
            string installedBmsBPath = CreateBmsFileWithContents(primaryDestinationDirectoryPath, "installedB.bms", "#PLAYER 1\r\n#TITLE Majority BMS B\r\n#ARTIST Mixed Artist\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Minority Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(secondaryDestinationDirectoryPath, "installed.bmson", "Minority Bmson", "Mixed Artist");
            ChartFile pendingBmsA = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsAPath));
            ChartFile pendingBmsB = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsBPath));
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingBmsA)), PackageChartEntry.FromChart((pendingBmsB)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsAPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsBPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            // 同一ハッシュの譜面自体を偽装せず、所持フォルダの別譜面で代表を変更する。
            library.BmsCharts = library.BmsCharts.Concat(Enumerable.Range(0, 3).Select(i => ReadChart(
                CreateBmsFileWithContents(primaryDestinationDirectoryPath, "extra" + i + ".bms", "#TITLE Unrelated\r\n#ARTIST Other\r\n#GENRE " + i + "\r\n")))).ToList();
            // 要求群の最頻ペアを少数側と揃える。既所持一致数は2対1のまま。
            pendingPackage.ReplaceChartEntries(pendingPackage.ChartEntries.Concat(Enumerable.Range(0, 3).Select(i => PackageChartEntry.FromChart(ReadChart(
                CreateBmsFileWithContents(sourceDirectoryPath, "missing" + i + ".bms", "#TITLE Minority Bmson\r\n#ARTIST Mixed Artist\r\n#WAV01 sound.wav\r\n#00101:01\r\n#00111:01\r\n#GENRE missing" + i + "\r\n"))))));
            string resourceDirectory = Path.Combine(tempRootPath, "Installed", "ResourceCandidate");
            library.BmsCharts = library.BmsCharts.Append(ReadChart(CreateBmsFileWithContents(resourceDirectory, "resource.bms", "#TITLE Minority Bmson\r\n#ARTIST Mixed Artist\r\n"))).ToList();
            File.WriteAllText(Path.Combine(resourceDirectory, "sound.wav"), "resource");
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(resourceDirectory));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries.Where(entry => Path.GetFileName(entry.Chart.Path).StartsWith("missing", StringComparison.Ordinal)))
            {
                Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
                Assert.AreEqual("Majority BMS A", pendingEntry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Mixed Artist", pendingEntry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEqual(new[] { primaryDestinationDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
            }

        });
    }

    /// <summary>混在譜面の所持ハッシュ数が同数なら、実リソースを満たす候補へ評価を進め、ハッシュだけの候補を選ばない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectory_MixedSplitTieFallsBackToResourceCandidate()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "MixedMergeTie");
            string resourceDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "ResourceWinner");
            string hashOnlyDestinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "HashOnlyTie");
            string pendingBmsPath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Tie BMS\r\n#ARTIST Mixed Artist\r\n#WAVAA sound.wav\r\n#00101:AA\r\n#00111:AA\r\n");
            string installedBmsPath = CreateBmsFileWithContents(resourceDestinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Tie BMS\r\n#ARTIST Mixed Artist\r\n#WAVAA sound.wav\r\n#00101:AA\r\n#00111:AA\r\n");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Tie Bmson", "Mixed Artist");
            string installedBmsonPath = CreateBmsonFile(hashOnlyDestinationDirectoryPath, "installed.bmson", "Tie Bmson", "Mixed Artist");
            File.WriteAllText(Path.Combine(resourceDestinationDirectoryPath, "sound.wav"), "resource");
            ChartFile pendingBms = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingBmsPath));
            var pendingBmsonEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(pendingBmsonPath)));
            var pendingPackage = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingBms)), pendingBmsonEntry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(installedBmsPath))
            ];
            library.BmsonCharts =
            [
                ChartTestValues.ReadBmson(installedBmsonPath)
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, resourceDestinationDirectoryPath, hashOnlyDestinationDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            foreach (PackageChartEntry pendingEntry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(resourceDestinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            }

        });
    }

    /// <summary>譜面一覧からの結合先探索では入力元を候補から外し、外部候補の導入先・代表情報を反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SearchEstimatedInstallationDirectoryForLooseCharts_UsesExternalCandidateOnly()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMergeByFile");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "MergeCandidate");
            string pendingFilePath = CreateBmsFileWithContents(
                sourceDirectoryPath,
                "pending.bms",
                "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist obj: Diff\r\n#WAVAA 00.wav\r\n#00101:AA\r\n#WAVAB 01.wav\r\n#00101:AB\r\n#00111:AAAB\r\n");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "00.wav"), "src");
            File.WriteAllText(Path.Combine(sourceDirectoryPath, "01.wav"), "src");
            CreateBmsFileWithContents(candidateDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "01.wav"), "dst");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts = [BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateDirectoryPath, "installed.bms")))];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            var pendingEntry = PackageChartEntry.FromChart((pendingFile));
            library.SearchEstimatedInstallationDirectoryForLooseCharts([pendingEntry]);

            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
        });
    }

    /// <summary>所持BMSのRawTitle・RawArtistから実在最頻ペアを手動導入先の代表表示へ渡し、合成表示の未知SUBARTIST混入と同じパスの現在値変更後のキャッシュ残存を検出する。</summary>
    /// <remarks>所持索引と実SQLiteの保留entryを接続し、副題と未知の副作者を含む実BMSを使う。全RawTitleが空なら合成Titleから補わず代表情報を空にする。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_UpdatesRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageManual");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string pendingFilePath = CreateBmsFileWithContents(pendingDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            string minorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "a.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            string installedFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "installed.bms", "#PLAYER 1\r\n#TITLE Installed Title\r\n#SUBTITLE Alternate Name\r\n#ARTIST Installed Artist\r\n#SUBARTIST Unrelated Contribution\r\n");
            string majorityPath = CreateBmsFileWithContents(destinationDirectoryPath, "z.bms", "#TITLE Installed Title\r\n#ARTIST Installed Artist\r\n");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts = [.. new[] { minorityPath, installedFilePath, majorityPath }
                .Select(path => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path)))];
            SeedPendingPackages(library, songDbPath, pendingPackage);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Installed Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));

            // 同じ所持パスの現在値を入れ替え、比較と表示の共通キャッシュが同じ失効で更新されることを確認します。
            library.BmsCharts = [.. library.BmsCharts.Select(chart => chart with { RawTitle = "Updated", RawArtist = "Updated Artist", Artist = "Updated Artist Unrelated Contribution" })];
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath));
            Assert.AreEqual("Updated", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Updated Artist", pendingEntry.Chart.InstallDestinationArtist);
            library.BmsCharts = [.. library.BmsCharts.Select(chart => chart with { RawTitle = string.Empty })];
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, destinationDirectoryPath));
            Assert.AreEqual(destinationDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
        });
    }

    /// <summary>Bmson保留へ親ディレクトリを手動指定すると、下位ディレクトリの所持譜面から代表情報を解決する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_UpdatesAdapterlessBmsonEntryWithoutMaterializingAdapter()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string pendingDirectoryPath = Path.Combine(tempRootPath, "Pending", "BmsonManual");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Installed", "BmsonManual");
            string pendingBmsonPath = CreateBmsonFile(pendingDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            string nestedDestinationDirectoryPath = Path.Combine(destinationDirectoryPath, "Nested");
            string installedBmsonPath = CreateBmsonFile(nestedDestinationDirectoryPath, "installed.bmson", "Installed Bmson", "Installed Artist");
            File.WriteAllText(installedBmsonPath, """
                {"version":"1.0.0","info":{"title":"Installed Bmson","subtitle":"Alternate Name","chart_name":"ANOTHER","artist":"Installed Artist"},"sound_channels":[]}
                """);
            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var entry = PackageChartEntry.FromChart((pendingSong));
            var pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts = [];
            library.BmsonCharts = [ChartTestValues.ReadBmson(installedBmsonPath)];
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(destinationDirectoryPath, nestedDestinationDirectoryPath));
            SeedPendingPackages(library, songDbPath, pendingPackage);
            bool succeeded = library.SetPendingInstallDestination(entry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);

            Assert.AreEqual(destinationDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Installed Bmson", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Installed Artist", entry.Chart.InstallDestinationArtist);
        });
    }

    /// <summary>パッケージに属さない所持BMSも手動指定を受理し、所持候補の導入先と代表情報を反映する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_AllowsStandaloneLibraryFileForFullScan()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            ChartFile sourceFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceFilePath));
            library.BmsCharts =
            [
                sourceFile,
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationFilePath))
            ];

            var sourceEntry = PackageChartEntry.FromChart((sourceFile));
            bool succeeded = library.SetPendingInstallDestination(sourceEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(destinationDirectoryPath, sourceEntry.Chart.InstallDestination);
            Assert.AreEqual("Destination Title", sourceEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, sourceEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(sourceEntry));
        });
    }

    /// <summary>パッケージに属さない所持Bmsonも手動指定を受理し、導入先・代表情報を更新して推定警告を残さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_AllowsStandaloneLibraryBmsonForFullScanWithoutAdapter()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Library", "BmsonSource");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "BmsonDestination");
            string sourceBmsonPath = CreateBmsonFile(sourceDirectoryPath, "source.bmson", "Source Bmson", "Source Artist");
            string destinationBmsonPath = CreateBmsonFile(destinationDirectoryPath, "destination.bmson", "Destination Bmson", "Destination Artist");
            ChartFile sourceSong = ChartTestValues.ReadBmson(sourceBmsonPath);
            ChartFile destinationSong = ChartTestValues.ReadBmson(destinationBmsonPath);
            var sourceEntry = PackageChartEntry.FromChart((sourceSong));
            library.BmsCharts = [];
            library.BmsonCharts = [sourceSong, destinationSong];

            bool succeeded = library.SetPendingInstallDestination(sourceEntry, destinationDirectoryPath);

            Assert.IsTrue(succeeded);

            Assert.AreEqual(destinationDirectoryPath, sourceEntry.Chart.InstallDestination);
            Assert.AreEqual("Destination Bmson", sourceEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Destination Artist", sourceEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, sourceEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(sourceEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        });
    }

    /// <summary>所持集合にも保留にも属さない外部譜面の手動導入先指定を拒否し、管理対象として受理しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_RejectsStandaloneFileThatIsNotInLibrary()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "External", "Source");
            string destinationDirectoryPath = Path.Combine(tempRootPath, "Library", "Destination");
            string sourceFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "source.bms", "#PLAYER 1\r\n#TITLE Source\r\n#ARTIST Test\r\n");
            string destinationFilePath = CreateBmsFileWithContents(destinationDirectoryPath, "destination.bms", "#PLAYER 1\r\n#TITLE Destination Title\r\n#ARTIST Destination Artist\r\n");

            ChartFile sourceFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(sourceFilePath));
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(destinationFilePath))
            ];

            bool succeeded = library.SetPendingInstallDestination(PackageChartEntry.FromChart((sourceFile)), destinationDirectoryPath);

            Assert.IsFalse(succeeded);
        });
    }

    /// <summary>不一致候補からの手動選択は同じパッケージの全entryへ反映し、候補と理由を保ちながら別パッケージを変更しない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_FromLowConfidenceCandidates_PreservesWarningAndSuggestions()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceManual");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            string minorityPath = CreateBmsFileWithContents(candidateBDirectoryPath, "a-minority.bms", "#TITLE Minority\r\n#ARTIST Other\r\n");
            string majorityPath = CreateBmsFileWithContents(candidateBDirectoryPath, "z-majority.bms", "#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            string secondFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "second.bms", "#PLAYER 1\r\n#TITLE Second\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile, BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondFilePath))]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(minorityPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(majorityPath)),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            string otherFilePath = CreateBmsFileWithContents(Path.Combine(tempRootPath, "Other"), "other.bms", "#PLAYER 1\r\n#TITLE Other\r\n");
            ChartPackage otherPackage = ChartPackageTestExtensions.CreatePackage([BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(otherFilePath))]);
            otherPackage.path = otherFilePath;
            otherPackage.delete_parent = true;
            PackageChartEntry otherEntry = GetOnlyEntry(otherPackage);
            otherEntry.ApplyInstallDestinationMetadata(candidateADirectoryPath, "Other title", "Other artist");
            SeedPendingPackages(library, songDbPath, pendingPackage, otherPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage, InstallEstimationCandidatePolicy.KeepUnrelated);

            PackageChartEntry pendingEntry = pendingPackage.ChartEntries.First();
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);
            foreach (PackageChartEntry entry in pendingPackage.ChartEntries)
            {
                Assert.AreEqual(candidateBDirectoryPath, entry.Chart.InstallDestination);
                Assert.AreEqual("Candidate B", entry.Chart.InstallDestinationTitle);
                Assert.AreEqual("Artist B", entry.Chart.InstallDestinationArtist);
                CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
                Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationUnrelated));
            }
            Assert.AreEqual(candidateADirectoryPath, otherEntry.Chart.InstallDestination);
            Assert.AreEqual("Other title", otherEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Other artist", otherEntry.Chart.InstallDestinationArtist);
            Assert.AreEqual(0, otherEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationUnrelated));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), string.Format(BeMusicSeeker.Properties.Resources.Warning_InstallEstimationUnrelated, candidateADirectoryPath));
            StringAssert.Contains(ChartWarningTestHelpers.BuildTooltipText(pendingEntry), candidateADirectoryPath);
        });
    }

    /// <summary>Bmsonの不一致候補を手動選択しても、選択先の代表情報と元の両候補・不一致警告を同じentryに保持する。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_FromAdapterlessBmsonLowConfidenceCandidate_PreservesWarningAndSuggestions()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageBmsonLowConfidenceManual");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingBmsonPath = CreateBmsonFile(sourceDirectoryPath, "pending.bmson", "Pending Bmson", "Pending Artist");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingSong = ChartTestValues.ReadBmson(pendingBmsonPath);
            var entry = PackageChartEntry.FromChart((pendingSong));
            var pendingPackage = ChartPackage.FromChartEntries([entry]);
            pendingPackage.path = sourceDirectoryPath;
            pendingPackage.delete_parent = false;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);

            bool succeeded = library.SetPendingInstallDestination(entry, candidateBDirectoryPath);

            Assert.IsTrue(succeeded);

            Assert.AreEqual(candidateBDirectoryPath, entry.Chart.InstallDestination);
            Assert.AreEqual("Candidate B", entry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Artist B", entry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, entry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    /// <summary>手動解除と任意の実ディレクトリ指定は不一致候補を保ち、存在しない後続指定を拒否して確定済み情報を戻さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_WithManualDirectory_PreservesLowConfidenceState()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceManualFree");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string manualDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Candidate A\r\n#ARTIST Artist A\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Candidate B\r\n#ARTIST Artist B\r\n");
            string manualInstalledFilePath = CreateBmsFileWithContents(manualDirectoryPath, "manual.bms", "#PLAYER 1\r\n#TITLE Manual Title\r\n#ARTIST Manual Artist\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(manualInstalledFilePath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath, manualDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage, InstallEstimationCandidatePolicy.KeepUnrelated);

            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, string.Empty));
            Assert.IsTrue(string.IsNullOrEmpty(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationUnrelated));
            bool succeeded = library.SetPendingInstallDestination(pendingEntry, manualDirectoryPath);

            Assert.IsTrue(succeeded);
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            Assert.IsTrue(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationUnrelated));
            ChartWarning[] warningsBeforeRejection = pendingEntry.Chart.Warnings.ToArray();
            Assert.IsFalse(library.SetPendingInstallDestination(pendingEntry, Path.Combine(tempRootPath, "Missing")));
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEquivalent(new[] { candidateADirectoryPath, candidateBDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(warningsBeforeRejection, pendingEntry.Chart.Warnings.ToArray());
        });
    }

    /// <summary>導入先の明示消去は曖昧推定の代表情報・候補・表示警告をすべて除き、古い警告行を残さない。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void RemoveInstallDestination_ClearsAllAmbiguousWarningLinesAndRepresentativeMetadata()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageLowConfidenceClear");
            string candidateADirectoryPath = Path.Combine(tempRootPath, "Installed", "A");
            string candidateBDirectoryPath = Path.Combine(tempRootPath, "Installed", "B");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateADirectoryPath, "candidateA.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            CreateBmsFileWithContents(candidateBDirectoryPath, "candidateB.bms", "#PLAYER 1\r\n#TITLE Pending\r\n#ARTIST Test\r\n");
            File.WriteAllText(Path.Combine(candidateADirectoryPath, "sound.wav"), "a");
            File.WriteAllText(Path.Combine(candidateBDirectoryPath, "sound.wav"), "b");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateADirectoryPath, "candidateA.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateBDirectoryPath, "candidateB.bms")))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateADirectoryPath, candidateBDirectoryPath));

            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));

            library.RemoveInstallDestination([pendingEntry], pendingPackage);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAmbiguous));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
            Assert.AreEqual(0, pendingEntry.Chart.WAVHealth);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        });
    }

    /// <summary>メタデータ不一致からの手動変更や空指定は不一致の文脈を保持し、明示消去時だけ推定警告を除いて他の警告を保つ。</summary>
    /// <remarks>実譜面とリソースを候補評価へ渡し、実SQLiteの保留行とライブラリ窓口を接続する。画面構築は不要で、設定はケース固有に分離する。</remarks>
    [TestMethod]
    public void SetPendingInstallDestination_MetadataMismatchEditsPreserveContextUntilExplicitClear()
    {

        WithTemporaryLibrary(delegate (string tempRootPath, string songDbPath, BMSLibrary library)
        {
            string sourceDirectoryPath = Path.Combine(tempRootPath, "Pending", "PackageMetadataMismatchClear");
            string candidateDirectoryPath = Path.Combine(tempRootPath, "Installed", "OnlyCandidate");
            string pendingFilePath = CreateBmsFileWithContents(sourceDirectoryPath, "pending.bms", "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            CreateBmsFileWithContents(candidateDirectoryPath, "candidate.bms", "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDirectoryPath, "sound.wav"), "dst");
            string manualDirectoryPath = Path.Combine(tempRootPath, "Installed", "Manual");
            string manualFilePath = CreateBmsFileWithContents(manualDirectoryPath, "manual.bms", "#PLAYER 1\r\n#TITLE Manual Title\r\n#ARTIST Manual Artist\r\n");

            ChartFile pendingFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(pendingFilePath));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingFilePath;
            pendingPackage.delete_parent = true;
            library.BmsCharts =
            [
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(Path.Combine(candidateDirectoryPath, "candidate.bms"))),
                BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(manualFilePath))
            ];
            SeedPendingPackages(library, songDbPath, pendingPackage);
            SetLibraryResourceIndex(library, BuildDirectoryLookupCache(sourceDirectoryPath, candidateDirectoryPath));

            pendingPackage.ChartEntries.Single().SetWarning(ChartWarningKind.SingleBmsFile, BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile);
            library.SearchEstimatedInstallationDirectory(pendingPackage);
            PackageChartEntry pendingEntry = GetOnlyEntry(pendingPackage);
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));

            ChartWarning[] estimationWarnings = pendingEntry.Chart.Warnings.Where(warning => warning.Category == ChartWarningCategory.InstallEstimation).ToArray();
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, candidateDirectoryPath));
            Assert.AreEqual(candidateDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Completely Different", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Another Artist", pendingEntry.Chart.InstallDestinationArtist);
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, string.Empty));
            Assert.IsTrue(string.IsNullOrEmpty(pendingEntry.Chart.InstallDestination));
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual(string.Empty, pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, pendingEntry.Chart.Warnings.Where(warning => warning.Category == ChartWarningCategory.InstallEstimation).ToArray());
            Assert.IsTrue(library.SetPendingInstallDestination(pendingEntry, manualDirectoryPath));
            Assert.AreEqual(manualDirectoryPath, pendingEntry.Chart.InstallDestination);
            Assert.AreEqual("Manual Title", pendingEntry.Chart.InstallDestinationTitle);
            Assert.AreEqual("Manual Artist", pendingEntry.Chart.InstallDestinationArtist);
            CollectionAssert.AreEqual(new[] { candidateDirectoryPath }, pendingEntry.Chart.InstallDestinationSuggestions.ToArray());
            CollectionAssert.AreEqual(estimationWarnings, pendingEntry.Chart.Warnings.Where(warning => warning.Category == ChartWarningCategory.InstallEstimation).ToArray());
            ChartWarning[] otherWarnings = pendingEntry.Chart.Warnings.Where(warning => warning.Category is not (ChartWarningCategory.InstallEstimation or ChartWarningCategory.ResourceHealth)).ToArray();
            Assert.IsTrue(otherWarnings.Length > 0);
            library.RemoveInstallDestination([pendingEntry], pendingPackage);
            CollectionAssert.AreEqual(otherWarnings, pendingEntry.Chart.Warnings.Where(warning => warning.Category != ChartWarningCategory.ResourceHealth).ToArray());
            Assert.IsTrue(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual(0, pendingEntry.Chart.WAVHealth);

            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestination));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationTitle));
            Assert.IsTrue(string.IsNullOrWhiteSpace(pendingEntry.Chart.InstallDestinationArtist));
            Assert.AreEqual(0, pendingEntry.Chart.InstallDestinationSuggestions.Count);
            Assert.IsFalse(ChartWarningTestHelpers.ContainsLowConfidenceInstallEstimationWarning(pendingEntry));
            Assert.IsFalse(pendingEntry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    private static void InvokeRegroupForSourceDirectories(BMSLibrary library, params string[] sourceDirectoryPaths)
    {
        MethodInfo? regroupMethod = typeof(BMSLibrary).GetMethod("TryRegroupPendingPackagesForSourceDirectoriesUnsafe", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.IsNotNull(regroupMethod);
        regroupMethod!.Invoke(library, [sourceDirectoryPaths, null]);
    }

    private static ChartPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, string expectedDestinationDirectory, int expectedFileCount)
    {
        ChartPackage regroupedPackage = AssertRegroupedPendingPackage(library, expectedPackagePath, expectedFileCount);
        Assert.IsTrue(regroupedPackage.ChartEntries.All(entry => string.Equals(entry.Chart.InstallDestination, expectedDestinationDirectory, StringComparison.OrdinalIgnoreCase)));
        return regroupedPackage;
    }

    private static ChartPackage AssertRegroupedPendingPackage(BMSLibrary library, string expectedPackagePath, int expectedFileCount)
    {
        Assert.AreEqual(1, library.ChartPackagesPending.Count);
        ChartPackage regroupedPackage = library.ChartPackagesPending.Single();
        Assert.AreEqual(expectedPackagePath, regroupedPackage.path);
        Assert.IsFalse(regroupedPackage.delete_parent);
        Assert.AreEqual(expectedFileCount, regroupedPackage.ChartEntries.Count);
        return regroupedPackage;
    }

    private static void AssertPendingPackagePaths(BMSLibrary library, params string[] expectedPaths)
    {
        Assert.AreEqual(expectedPaths.Length, library.ChartPackagesPending.Count);
        CollectionAssert.AreEquivalent(
            expectedPaths,
            library.ChartPackagesPending.Select(package => package.path).ToArray());
    }

    private static void SeedPendingPackages(BMSLibrary library, string songDbPath, params ChartPackage[] packages)
    {
        // 本番の保留登録に相当するfixture境界で、元配置の初期評価を明示します。
        foreach (PackageChartEntry entry in packages.SelectMany(package => package.ChartEntries))
        {
            BmsLibraryPackageInstallService.InitializePendingResourceHealth(entry);
        }
        SeedInitializedPendingPackages(library, songDbPath, packages);
    }

    private static void SeedInitializedPendingPackages(BMSLibrary library, string songDbPath, params ChartPackage[] packages)
    {
        library.ChartPackagesPending = CreatePackageCollection(packages);
        // pending packageのschemaとseedだけを一つのtransactionへまとめ、本番の保存契約は変更せずfixtureのautocommit反復を減らす。
        BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
        {
            songDb.CreateTable<LR2SongDBExtended.install>();
            foreach (ChartPackage package in packages)
            {
                songDb.InsertOrReplace(package, typeof(LR2SongDBExtended.install));
            }
        });
    }

    private static DirectoryResourceLookupCache BuildDirectoryLookupCache(params string[] directories)
    {
        var cache = new DirectoryResourceLookupCache();
        foreach (string directoryPath in directories.Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)))
        {
            cache.AddDir(directoryPath, Directory.GetFiles(directoryPath, "*", SearchOption.TopDirectoryOnly).Select(Path.GetFileName));
        }
        return cache;
    }

    private static void SetLibraryResourceIndex(BMSLibrary library, DirectoryResourceLookupCache cache)
    {
        var index = new LibraryResourceIndex();
        typeof(LibraryResourceIndex)
            .GetProperty(nameof(LibraryResourceIndex.DirectoryLookupCache), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(index, cache);
        FieldInfo ownerField = typeof(BMSLibrary).GetField(
            "libraryResourceIndexOwner",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var owner = (LibraryResourceIndexOwner)ownerField.GetValue(library)!;
        owner.Replace(index);
    }

    private static string[] LoadInstallPaths(string songDbPath)
    {
        // 全呼出し元はSeedPendingPackagesでinstall表を準備済みなので、SELECT専用read-only接続で観測する。
        using LR2SongDBExtended songDb = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
        return [.. songDb.Query<InstallRowRecord>("SELECT path FROM install")
            .Select(row => row.path)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];
    }

    private static ChartPackage CreatePendingSingleFilePackage(string filePath, string installDestination = "")
    {
        ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(filePath));
        var entry = PackageChartEntry.FromChart((file));
        if (!string.IsNullOrWhiteSpace(installDestination))
        {
            entry.SetInstallDestinationPathOnly(installDestination);
        }
        var package = ChartPackage.FromChartEntries([entry]);
        package.path = filePath;
        package.delete_parent = true;
        return package;
    }

    private static PackageChartEntry GetOnlyEntry(ChartPackage package)
    {
        Assert.IsNotNull(package);
        return package.ChartEntries.Single();
    }

    private static PackageChartEntry GetEntryByFileName(ChartPackage package, string fileName)
    {
        Assert.IsNotNull(package);
        return package.ChartEntries.Single(entry => Path.GetFileName(entry.Chart.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateBmsFile(string directoryPath, string fileName, string titleSuffix)
    {
        return CreateBmsFileWithContents(directoryPath, fileName, "#PLAYER 1\r\n#TITLE " + titleSuffix + "\r\n#ARTIST Test\r\n");
    }

    private static string CreateBmsFileWithContents(string directoryPath, string fileName, string contents)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, contents);
        return filePath;
    }

    /// <summary>テスト入力の実BMSを現在の解析器で読み取ります。</summary>
    private static ChartFile ReadChart(string path) => BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));

    private static string CreateBmsonFile(string directoryPath, string fileName, string title, string artist, string? subartist = null)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\""
            + (subartist == null ? string.Empty : ",\"subartists\":[\"" + subartist + "\"]")
            + ",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[{\"name\":\"sound.wav\",\"notes\":[{\"x\":1,\"y\":0,\"l\":0}]}]"
            + "}");
        return filePath;
    }

    private static string CreateHealthyBmsonFile(string directoryPath, string fileName, string title, string artist)
    {
        Directory.CreateDirectory(directoryPath);
        string filePath = Path.Combine(directoryPath, fileName);
        File.WriteAllText(filePath, "{"
            + "\"version\":\"1.0.0\","
            + "\"info\":{\"title\":\"" + title + "\",\"artist\":\"" + artist + "\",\"mode_hint\":\"beat-7k\"},"
            + "\"sound_channels\":[]"
            + "}");
        return filePath;
    }

    private static ChartFile WithChartPath(ChartFile source, string path)
    {
        return new ChartFile(source.Kind, path, source.Md5, source.Sha256, source.Title, source.RawTitle, source.Artist, source.Genre, source.Folder, source.Tag, source.LevelText, source.Level, source.Mode, source.ChartInfo, source.Subtitle, source.Resources, source.Stagefile, source.Backbmp, source.Banner, source.InstallDestination, source.InstallDestinationTitle, source.InstallDestinationArtist, source.InstallDestinationSuggestions, source.Warnings, source.WAVHealth, source.BGAHealth, source.MovieHealth, source.StagefileHealth, source.BannerHealth, source.BackbmpHealth, source.EncodingName);
    }

    private static void ApplySingleFileWarnings(params ChartPackage[] packages)
    {
        foreach (PackageChartEntry entry in (packages ?? []).Where(package => package != null).SelectMany(package => package.ChartEntries).Where(entry => entry.Chart.Kind == ChartFileKind.Bms))
        {
            entry.SetWarning(ChartWarningKind.SingleBmsFile, BeMusicSeeker.Properties.Resources.Warning_SingleBmsFile);
        }
    }

    private static ObservableCollection<ChartPackage> CreatePackageCollection(IEnumerable<ChartPackage> packages)
    {
        return new ObservableCollection<ChartPackage>([.. (packages ?? [])]);
    }

    private void WithTemporaryLibrary(
        Action<string, string, BMSLibrary> testAction,
        IInstallEstimationExecutionObserver? installEstimationExecutionObserver = null)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_PendingRegroupTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        File.WriteAllBytes(songDbPath, []);
        try
        {
            TestBmsLibrary library = installEstimationExecutionObserver == null
                ? new TestBmsLibrary(songDbPath, null!, null, null!, new RecordingDialogService(), settings: testSettings)
                : new TestBmsLibrary(songDbPath, null!, null, null!, new RecordingDialogService(), installEstimationExecutionObserver, settings: testSettings);
            testAction(tempRootPath, songDbPath, library);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private void WithAutoApplyAmbiguousInstallDestination(bool enabled, Action action)
    {
        bool original = testSettings.AutoApplyAmbiguousInstallDestination;
        try
        {
            testSettings.AutoApplyAmbiguousInstallDestination = enabled;
            action();
        }
        finally
        {
            testSettings.AutoApplyAmbiguousInstallDestination = original;
        }
    }

    private sealed class RecordingInstallEstimationExecutionObserver : IInstallEstimationExecutionObserver, IDisposable
    {
        private readonly object gate = new();
        private readonly Barrier? workItemBarrier;
        private readonly Action<InstallEstimationAppliedObservation>? attemptObserver;
        private int activeWorkItems;
        private int maxActive;

        internal RecordingInstallEstimationExecutionObserver(
            int expectedMaxActive = 0,
            Action<InstallEstimationAppliedObservation>? attemptObserver = null)
        {
            this.attemptObserver = attemptObserver;
            if (expectedMaxActive > 1)
            {
                workItemBarrier = new Barrier(expectedMaxActive);
            }
        }

        internal List<InstallEstimationWorkItemObservation> WorkItems { get; } = [];

        internal List<InstallEstimationProgressObservation> Progress { get; } = [];

        internal List<InstallEstimationAppliedObservation> Attempts { get; } = [];

        internal int MaxActive => Volatile.Read(ref maxActive);

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            lock (gate)
            {
                WorkItems.Add(observation);
            }
            int currentActive = Interlocked.Increment(ref activeWorkItems);
            UpdateMaxActive(currentActive);
            workItemBarrier?.SignalAndWait();
            return new WorkItemScope(this);
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation)
        {
            lock (gate)
            {
                Progress.Add(observation);
            }
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
            lock (gate)
            {
                Attempts.Add(observation);
            }
            attemptObserver?.Invoke(observation);
        }

        /// <summary>実一括処理の終端後に、同時到達を観測する局所barrierを回収する。</summary>
        public void Dispose()
        {
            workItemBarrier?.Dispose();
        }

        private void UpdateMaxActive(int currentActive)
        {
            while (true)
            {
                int previousMax = Volatile.Read(ref maxActive);
                if (currentActive <= previousMax
                    || Interlocked.CompareExchange(ref maxActive, currentActive, previousMax) == previousMax)
                {
                    return;
                }
            }
        }

        private void EndWorkItem()
        {
            Interlocked.Decrement(ref activeWorkItems);
        }

        private sealed class WorkItemScope(RecordingInstallEstimationExecutionObserver owner) : IDisposable
        {
            private RecordingInstallEstimationExecutionObserver? owner = owner;

            public void Dispose()
            {
                Interlocked.Exchange(ref owner, null)?.EndWorkItem();
            }
        }
    }

    private sealed class ThrowingInstallEstimationExecutionObserver : IInstallEstimationExecutionObserver
    {
        internal readonly InvalidOperationException Failure = new("install-estimation observer failed");

        public IDisposable BeginWorkItem(InstallEstimationWorkItemObservation observation)
        {
            throw Failure;
        }

        public void ObserveProgress(InstallEstimationProgressObservation observation)
        {
        }

        public void ObserveResultApplied(InstallEstimationAppliedObservation observation)
        {
        }
    }

    private sealed class RecordingDialogService : IBmsLibraryDialogService
    {
        public UiDialogDefaultResult Show(string messageBoxText, string caption, UiDialogButton button, UiDialogIcon icon, UiDialogDefaultResult defaultResult = UiDialogDefaultResult.None)
        {
            return UiDialogDefaultResult.OK;
        }
    }

    private sealed class InstallRowRecord
    {
        public string path { get; set; } = string.Empty;
    }
}
