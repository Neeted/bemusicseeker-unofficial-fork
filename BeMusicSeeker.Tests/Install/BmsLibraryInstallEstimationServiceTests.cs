using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryInstallEstimationServiceTests
{
    /// <summary>通常の部分列挙は観測済み資源を候補評価へ渡し、全参照が観測できれば自動確定します。未観測の参照は欠損へ変換せず、直接・共有入力と通常・ハッシュ入口の両方で確認不能理由を保ちます。</summary>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void Estimate_PartialSourceSurfacePreservesObservedResourcesAndUnverifiableReason(bool hash, bool observedComplete)
    {
        WithWorkspace((root, service) =>
        {
            string source = Path.Combine(root, "Source");
            Directory.CreateDirectory(source);
            ChartFile chart = CreateFile("md5", Path.Combine(source, "chart.bms"), "one.wav", "two.wav", "three.wav", "four.wav", "last.wav")
                with
            { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
            var entry = PackageChartEntry.FromChart(chart);
            var package = ChartPackage.FromChartEntries([entry]);
            package.path = source;
            var enumeration = new RootFileEnumerationResult { Success = false, IsComplete = false, ErrorReason = "access_denied" };
            enumeration.PathsByGroup[ChartDirectoryScanBuilder.AudioGroupName] = [Path.Combine(source, observedComplete ? "last.ogg" : "one.ogg")];
            PackageInstallSurfaceSnapshot surface = PackageInstallEstimationSnapshotBuilder.BuildDirectoryInstallSurfaceSnapshot(source, source, enumeration);
            Assert.IsFalse(surface.IsComplete);
            Assert.IsFalse(surface.ScanLimitExceeded);
            Assert.AreEqual(1, surface.BundledResources.AudioFileNameHashCount);
            // 自動推定のルート共有も同じ部分性を渡し、入力面を完全へ昇格しません。
            PackageInstallSurfaceSnapshot shared = PackageInstallEstimationSnapshotBuilder.BuildSharedInstallSurfaceSnapshot(source, source,
                new SourceSurfaceEntryView { ResourceEntry = surface.SourceCandidateResources, IsComplete = surface.IsComplete }, true);
            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(package, [entry], hash ? surface : shared, false);
            Assert.IsFalse(snapshot.SourceSurfaceIsComplete);
            var index = new DirectoryResourceLookupCache();
            string candidate = Path.Combine(root, "Candidate");
            index.AddDir(candidate, ["one.wav", "two.wav", "three.wav", "four.wav"]);
            InstallEstimationResult result = hash
                ? service.EvaluateInstalledChartHashDestination(candidate, [entry], _ => snapshot.TargetMetadataProfile, snapshot, index)
                : service.EstimateInstallationDirectory(snapshot, index, false, ChartInstallationEstimateMode.Normal, _ => snapshot.TargetMetadataProfile);
            Assert.IsTrue(result.HasViableDestination);
            Assert.AreEqual(observedComplete, result.ShouldAutoApplyDestination);
            Assert.AreEqual(observedComplete ? InstallAudioCompleteness.Complete : InstallAudioCompleteness.Unverifiable, result.AudioCompleteness);
            Assert.IsFalse(result.PendingBundledResourcesComplete);
            entry.ApplyInstallEstimationResult(result);
            BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], package, result.PendingDirectoryLookupCache, result.PendingBundledResources, result.PendingBundledResourcesComplete);
            Assert.AreEqual(observedComplete ? 100 : (int?)null, entry.Chart.WAVHealth);
            Assert.AreEqual(observedComplete ? 5 : (int?)null, entry.Chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind is ChartWarningKind.InstallEstimationAudioMissing or ChartWarningKind.ResourceWavMissing));
            Assert.AreEqual(!observedComplete, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioUnverifiable));
        });
    }

    /// <summary>メタデータ不一致でも音源の欠損理由を評価して併記します。通常とハッシュの両入口でStrongや候補順位を変更しません。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Estimate_AudioReasonIsIndependentOfMetadataMismatch(bool hash)
    {
        ChartFile target = CreateFile("md5", @"C:\Source\chart.bms", "one.wav", "two.wav", "three.wav", "four.wav", "missing.wav") with { RawTitle = "Target", Artist = "Artist", RawArtist = "Artist" };
        var snapshot = new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = target,
            DefinedResources = ChartResourceSnapshot.Create(target),
            TargetMetadataProfile = InstallEstimationMetadataNormalizer.BuildProfile([("Target", "Artist", "chart.bms")])
        };
        var index = new DirectoryResourceLookupCache();
        index.AddDir(@"C:\Candidate", ["one.ogg", "two.wav", "three.wav", "four.wav"]);
        InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([("Other", "Other artist", "base.bms")]);
        var entry = PackageChartEntry.FromChart(target);
        InstallEstimationResult result = hash
            ? CreateService().EvaluateInstalledChartHashDestination(@"C:\Candidate", [entry], _ => profile, snapshot, index)
            : CreateService().EstimateInstallationDirectory(snapshot, index, false, ChartInstallationEstimateMode.Normal, _ => profile);
        Assert.IsTrue(result.HasViableDestination);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
        Assert.AreEqual(InstallAudioCompleteness.Missing, result.AudioCompleteness);
        entry.ApplyInstallEstimationResult(result);
        CollectionAssert.AreEquivalent(new[] { ChartWarningKind.InstallEstimationMetadataMismatch, ChartWarningKind.InstallEstimationAudioMissing },
            entry.Chart.Warnings.Select(warning => warning.Kind).ToArray());
    }

    /// <summary>同梱98件または100件で両候補が100%になる場合も、候補自身の一致90件を2件より優先します。同梱との重複を二重計数しません。</summary>
    [TestMethod]
    [DataRow(98)]
    [DataRow(100)]
    public void EstimateInstallationDirectory_UnionFulfillmentRanksCandidateOwnMatches(int supplied)
    {
        BmsLibraryInstallEstimationService service = CreateService();
        string[] names = Enumerable.Range(0, 100).Select(i => "r" + i + ".wav").ToArray();
        ChartFile target = CreateFile("md5", @"C:\Source\chart.bms", names) with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
        var index = new DirectoryResourceLookupCache();
        index.AddDir(@"C:\Source", names.Take(supplied));
        index.AddDir(@"C:\Candidate2", names.Skip(98));
        index.AddDir(@"C:\Candidate90", names.Skip(10));
        var snapshot = new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = target,
            DefinedResources = ChartResourceSnapshot.Create(target),
            SourceDirectory = @"C:\Source",
            BundledResources = index.GetEntryOrNull(@"C:\Source"),
            TargetMetadataProfile = InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "chart.bms")])
        };
        InstallEstimationResult result = service.EstimateInstallationDirectory(snapshot, index, false, ChartInstallationEstimateMode.Normal,
            _ => snapshot.TargetMetadataProfile);
        Assert.AreEqual(@"C:\Candidate90", result.DestinationDirectory);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.AreEqual(100, result.SelectedCandidate.AudioMatched);
        Assert.AreEqual(90, result.SelectedCandidate.AudioExactMatched);
        Assert.AreEqual(100, result.Candidates[1].AudioMatched);
        Assert.AreEqual(2, result.Candidates[1].AudioExactMatched);
    }

    /// <summary>候補提示の80%境界は表示整数でなく実比率で判定し、Strongかつ曖昧自動適用ONでも欠損音源を自動確定しません。</summary>
    [TestMethod]
    [DataRow(100, 79, false)]
    [DataRow(100, 80, true)]
    [DataRow(101, 80, false)]
    public void EstimateInstallationDirectory_UsesExactViabilityRatioAndRequiresCompleteAudio(int required, int matched, bool viable)
    {
        WithAutoApplyAmbiguousInstallDestination(true, () =>
        {
            string[] names = Enumerable.Range(0, required).Select(i => "r" + i + ".wav").ToArray();
            ChartFile target = CreateFile("md5", @"C:\Source\chart.bms", names) with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
            InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "chart.bms")]);
            var snapshot = new PackageInstallEstimationSnapshot { RepresentativeChart = target, DefinedResources = ChartResourceSnapshot.Create(target), TargetMetadataProfile = profile };
            var index = new DirectoryResourceLookupCache();
            index.AddDir(@"C:\Candidate", names.Take(matched));
            InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, index, false, ChartInstallationEstimateMode.Normal, _ => profile);
            Assert.AreEqual(viable, result.HasViableDestination);
            Assert.AreEqual(viable ? InstallAudioCompleteness.Missing : InstallAudioCompleteness.NotEvaluated, result.AudioCompleteness);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            var projection = PackageChartEntry.FromChart(target);
            projection.ApplyInstallEstimationResult(result);
            Assert.AreEqual(viable, projection.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            Assert.IsFalse(projection.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioUnverifiable));
            if (viable)
            {
                CollectionAssert.Contains(result.SuggestedDestinationDirectories, @"C:\Candidate");
            }
        });
    }

    /// <summary>所持hashの直接候補も音源完全性を必要とし、欠損・照合不能・未取得では候補を保持します。実BMSの未定義ID追加は完全音源の自動適用を妨げず、取得済みの音源0件は完全として扱い、未捕捉入力は確認不能として拒否します。候補索引が未取得でも二譜面の全音源を同梱だけで満たせれば許可し、代表譜面だけの充足では許可しません。</summary>
    [TestMethod]
    [DataRow("complete", true)]
    [DataRow("missing", false)]
    [DataRow("undefined", true)]
    [DataRow("empty-key", false)]
    [DataRow("unavailable", false)]
    [DataRow("zero", true)]
    [DataRow("bundled-complete", true)]
    [DataRow("bundled-partial", false)]
    public void EvaluateInstalledChartHashDestination_RequiresCommonAudioCompleteness(string state, bool apply)
    {
        WithAutoApplyAmbiguousInstallDestination(true, () =>
        {
            ChartFile target = CreateFile("md5", @"C:\Source\chart.bms", state == "zero" ? [] : state == "empty-key" ? [".wav"] : ["sound.wav"]) with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
            if (state == "undefined")
            {
                target = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"C:\Source\chart.bms",
                    Encoding.ASCII.GetBytes("#TITLE Song\n#ARTIST Artist\n#WAV01 sound.wav\n#00111:01ZZ\n#00104:ZZ\n"), DateTime.UnixEpoch, "md5", "sha256"));
            }

            InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "chart.bms")]);
            List<PackageChartEntry> entries = [PackageChartEntry.FromChart(target)];
            bool bundledOnly = state is "bundled-complete" or "bundled-partial";
            DirectoryResourceLookupCache.Entry? bundled = null;
            if (bundledOnly)
            {
                ChartFile second = CreateFile("second", @"C:\Source\second.bms", "second.wav") with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
                entries.Add(PackageChartEntry.FromChart(second));
                var sourceIndex = new DirectoryResourceLookupCache();
                sourceIndex.AddDir(@"C:\Source", state == "bundled-complete" ? ["sound.wav", "second.wav"] : ["sound.wav"]);
                bundled = sourceIndex.GetEntryOrNull(@"C:\Source");
                Assert.IsNotNull(bundled);
            }
            var snapshot = new PackageInstallEstimationSnapshot
            {
                RepresentativeChart = target,
                DefinedResources = ChartResourceSnapshot.CreateAggregate(entries.Select(entry => entry.Chart)),
                BundledResources = bundled
            };
            var index = new DirectoryResourceLookupCache();
            index.AddDir(@"C:\Candidate", state is "complete" or "undefined" ? ["sound.ogg"] : []);
            InstallEstimationResult result = CreateService().EvaluateInstalledChartHashDestination(@"C:\Candidate", entries, _ => profile,
                state == "unavailable" ? null : snapshot, state == "unavailable" || bundledOnly ? null : index);
            Assert.AreEqual(apply, result.ShouldAutoApplyDestination);
            Assert.AreEqual(apply ? InstallAudioCompleteness.Complete
                : state is "empty-key" or "unavailable" or "bundled-partial" ? InstallAudioCompleteness.Unverifiable : InstallAudioCompleteness.Missing,
                result.AudioCompleteness);
            Assert.AreEqual(apply, result.Success);
            entries[0].ApplyInstallEstimationResult(result);
            Assert.AreEqual(result.AudioCompleteness == InstallAudioCompleteness.Missing,
                entries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
            Assert.AreEqual(result.AudioCompleteness == InstallAudioCompleteness.Unverifiable,
                entries[0].Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioUnverifiable));
            Assert.AreEqual(@"C:\Candidate", result.SelectedCandidate.DirectoryPath);
            if (!apply)
            {
                CollectionAssert.AreEqual(new[] { @"C:\Candidate" }, result.SuggestedDestinationDirectories);
            }
        });
    }

    /// <summary>実BMSONの完全音源は未定義BGA/LAYER/POORの追加で変わりません。定義済み画像不足だけは厳格ONで保留し、音源不足・空nameは両設定で保留とします。導入先の共通評価は音源完全性とStrongを使います。</summary>
    [TestMethod]
    [DataRow("complete")]
    [DataRow("undefined")]
    [DataRow("missing-image")]
    [DataRow("missing-audio")]
    [DataRow("empty-name")]
    public void BmsonCompleteness_ControlsAutomaticInstallationWithoutUndefinedIdEffects(string condition)
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsonCompleteness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "sound.wav"), "sound");
            string name = condition == "empty-name" ? "" : condition == "missing-audio" ? "missing.wav" : "sound.wav";
            string bga = condition == "missing-image"
                ? "\"bga_header\":[{\"id\":1,\"name\":\"missing.png\"}],\"bga_events\":[{\"id\":1,\"y\":0}]"
                : condition == "undefined"
                    ? "\"bga_events\":[{\"id\":99,\"y\":0}],\"layer_events\":[{\"id\":98,\"y\":0}],\"poor_events\":[{\"id\":97,\"y\":0}]" : "";
            string text = "{\"info\":{\"title\":\"Song\",\"artist\":\"Artist\"},\"sound_channels\":[{\"name\":\"" + name
                + "\",\"notes\":[{\"x\":1,\"y\":0}]}],\"bga\":{" + bga + "}}";
            string path = Path.Combine(root, "chart.bmson");
            File.WriteAllText(path, text, new UTF8Encoding(false));
            bool completeAudio = condition is not ("missing-audio" or "empty-name");
            foreach (bool strict in new[] { false, true })
            {
                AutoInstallWorkflowResult workflow = new BmsLibraryPackageInstallService().PrepareAutoInstallWorkflow([root], [], [], _ => false, 0.6,
                    requireCompleteUsedResourcesForAutoInstall: strict);
                bool pending = !completeAudio || (strict && condition == "missing-image");
                Assert.AreEqual(pending ? 1 : 0, workflow.PendingPackagesToAdd.Count, condition + "/" + strict);
                Assert.AreEqual(pending ? 0 : 1, workflow.AutoInstallCandidates.Count, condition + "/" + strict);
                ChartFile chart = workflow.DiscoveredPackages.Single().ChartEntries.Single().Chart;
                Assert.AreEqual(condition is "missing-image" or "missing-audio", chart.Warnings.Any(w => w.Category == ChartWarningCategory.ResourceHealth));
            }
            ChartFile target = BmsonChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(path));
            var resources = new PackageInstallEstimationSnapshot { RepresentativeChart = target, DefinedResources = ChartResourceSnapshot.Create(target) };
            var index = new DirectoryResourceLookupCache();
            index.AddDir(@"C:\Candidate", ["sound.wav"]);
            InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "chart.bmson")]);
            InstallEstimationResult estimate = CreateService().EvaluateInstalledChartHashDestination(@"C:\Candidate", [PackageChartEntry.FromChart(target)], _ => profile, resources, index);
            Assert.AreEqual(completeAudio, estimate.ShouldAutoApplyDestination);
            Assert.IsTrue(estimate.SelectedCandidateMetadataEvidenceStrong);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // 値の変更だけを試すfixtureなので、他のtesthostの実設定ファイルは読み込まない。
    private readonly BeMusicSeeker.Properties.Settings testSettings =
        PortableSettingsPersistenceTests.OpenSettings(Path.Combine(
            Path.GetTempPath(),
            "InstallEstimationSettings-" + Guid.NewGuid().ToString("N"),
            "user.config"));
    public TestContext TestContext { get; set; } = null!;

    /// <summary>明示した8・16の並列度を縮めず、非正の候補並列度を1へ補正し、設定値の勝手な制限を検出する。実同時実行は一括推定のケースが分担する。</summary>
    [TestMethod]
    public void InstallEstimationDegreeResolvers_PreserveExplicitDegreesAndNormalizeNonPositiveInputs()
    {

        Assert.AreEqual(1, BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(0));
        Assert.AreEqual(1, BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(-10));
        Assert.AreEqual(16, BmsLibraryInstallEstimationService.NormalizeCandidateEvaluationDegree(16));

        Assert.AreEqual(1, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(-10));
        Assert.AreEqual(1, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(1));
        Assert.AreEqual(8, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(8));
        Assert.AreEqual(16, BMSLibrary.ResolvePendingInstallEstimateParallelPackageDegree(16));
    }

    /// <summary>選択済みハッシュ候補にも共通Strongを要求し、不成立・比較不能で候補と代表表示を保ったまま停止する。資源条件や曖昧設定で救済しない。</summary>
    /// <remarks>サービスの判断境界を直接動かし、入口の接続・表示とTask終端はライブラリ側の代表ケースへ分担します。</remarks>
    [DataTestMethod]
    [DataRow("Song [ANOTHER]", "Artist", "Song", "Artist", true)]
    [DataRow("Song", "", "Song", "", true)]
    [DataRow("Other", "Artist", "Song", "Artist", false)]
    [DataRow("Song", "Other", "Song", "Artist", false)]
    [DataRow("", "Artist", "Song", "Artist", false)]
    [DataRow("Song", "Artist", "", "Artist", false)]
    public void EvaluateInstalledChartHashDestination_RequiresStrongAndPreservesKnownCandidate(string targetTitle, string targetArtist, string candidateTitle, string candidateArtist, bool strong)
    {
        WithAutoApplyAmbiguousInstallDestination(true, () =>
        {
            BmsLibraryInstallEstimationService service = CreateService();
            const string destination = @"C:\Installed\Song";
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\Pending\chart.bms", "missing.wav") with { RawTitle = targetTitle, Artist = targetArtist, RawArtist = targetArtist };
            InstallEstimationMetadataProfile profile = InstallEstimationMetadataNormalizer.BuildProfile([(candidateTitle, candidateArtist, "candidate.bms")]);
            var resourceSnapshot = new PackageInstallEstimationSnapshot { RepresentativeChart = target, DefinedResources = ChartResourceSnapshot.Create(target) };
            var resourceIndex = new DirectoryResourceLookupCache();
            resourceIndex.AddDir(destination, ["missing.wav"]);
            int resolverCalls = 0;
            InstallEstimationResult result = service.EvaluateInstalledChartHashDestination(destination, [PackageChartEntry.FromChart(target)], path =>
            {
                Assert.AreEqual(destination, path);
                resolverCalls++;
                return profile;
            }, resourceSnapshot, resourceIndex);
            Assert.AreEqual(1, resolverCalls);
            Assert.AreEqual(strong, result.Success);
            Assert.AreEqual(strong, result.ShouldAutoApplyDestination);
            Assert.AreEqual(strong, result.SelectedCandidateMetadataEvidenceStrong);
            Assert.IsTrue(result.HasViableDestination);
            Assert.AreEqual(InstallEstimationFinalEvaluationMode.InstalledChartHash, result.FinalEvaluationMode);
            InstallEstimationCandidate candidate = result.SelectedCandidate;
            if (targetTitle.Length == 0 || candidateTitle.Length == 0)
            {
                Assert.IsNull(candidate);
                Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
                Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
                return;
            }
            Assert.IsNotNull(candidate);
            Assert.IsTrue(candidate.IsViableDestination);
            Assert.AreEqual(destination, candidate.DirectoryPath);
            Assert.AreEqual(profile.DominantPairDisplayTitle, candidate.DisplayTitle);
            Assert.AreEqual(profile.DominantPairDisplayArtist, candidate.DisplayArtist);
            Assert.AreEqual(0, candidate.AudioHealth);
            Assert.AreEqual(0, candidate.AudioMatched);
            Assert.AreEqual(0, result.TargetResourceCount);
            Assert.AreEqual(strong ? InstallEstimationConfidence.High : InstallEstimationConfidence.Low, result.Confidence);
            Assert.AreEqual(strong ? InstallEstimationLowConfidenceKind.None : InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
            if (!strong)
            {
                Assert.IsTrue(string.IsNullOrEmpty(result.DestinationDirectory));
                CollectionAssert.AreEqual(new[] { destination }, result.SuggestedDestinationDirectories);
            }
        });
    }

    /// <summary>同一MD5の所持先が分散しているとき、一致譜面が最も多いフォルダだけを候補選択し、先に見つかった少数一致への誤決定を検出する。</summary>
    /// <remarks>候補選択だけを保証します。自動設定のStrong条件は共通評価のケースへ分担します。</remarks>
    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_PrefersDirectoryWithMostMatchingCharts()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<ChartFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirA, "b.bms")),
            CreateFile("cccccccccccccccccccccccccccccccc", Path.Combine(dirB, "c.bms"))
        ];
        ChartFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        ChartFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        ChartFile pendingC = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\c.bms");
        ChartPackage package = ChartPackageTestExtensions.CreatePackage([pendingA, pendingB, pendingC]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            [PackageChartEntry.FromChart((pendingC))],
            CreateInstalledChartLookupIndexSnapshot(installedFiles));

        Assert.AreEqual(dirA, result.InstallDirectory);
        Assert.AreEqual(3, result.MatchedHashCount);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        Assert.AreEqual(InstalledDirectoryResolveReason.None, result.Reason);
    }

    /// <summary>BMSとBmsonを含む入力でも種類をまたいでMD5一致を数え、最多の所持フォルダを選び、Bmsonの集計漏れを検出する。</summary>
    /// <remarks>実ファイルからの候補選択を保証します。自動設定のStrong条件は共通評価のケースへ分担します。</remarks>
    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_MixedBmsAndBmsonPackagePrefersDirectoryWithMostMatchingCharts()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<ChartFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirA, "b.bms"))
        ];
        ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine(dirB, "c.bmson"),
            Folder = dirB,
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('c', 64)
        };
        ChartFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        ChartFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\c.bmson",
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('c', 64)
        };
        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart((pendingA)),
            PackageChartEntry.FromChart((pendingB)),
            PackageChartEntry.FromChart((pendingBmson))
        ]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            package.ChartEntries,
            CreateInstalledChartLookupIndexSnapshot(installedFiles, [installedBmson]));

        Assert.AreEqual(dirA, result.InstallDirectory);
        Assert.AreEqual(3, result.MatchedHashCount);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        Assert.AreEqual(InstalledDirectoryResolveReason.None, result.Reason);
    }

    /// <summary>一致数が同じ所持先を一つに確定せず全候補を返し、同点の先頭だけを確定する誤りを検出する。</summary>
    /// <remarks>候補選択だけを保証します。自動設定のStrong条件は共通評価のケースへ分担します。</remarks>
    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_ReturnsMultipleCandidateDirectoriesForTopTie()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<ChartFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms")),
            CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(dirB, "b.bms"))
        ];
        ChartFile pendingA = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        ChartFile pendingB = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "C:\\Pending\\b.bms");
        ChartFile pendingMissing = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\missing.bms", "sound.wav");
        ChartPackage package = ChartPackageTestExtensions.CreatePackage([pendingA, pendingB, pendingMissing]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            [PackageChartEntry.FromChart((pendingMissing))],
            CreateInstalledChartLookupIndexSnapshot(installedFiles));

        Assert.AreEqual(InstalledDirectoryResolveReason.MultipleCandidateDirectories, result.Reason);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        CollectionAssert.AreEqual(new[] { dirA, dirB }, result.CandidateDirectories.ToArray());
    }

    /// <summary>BMSとBmsonを合わせた所持先が同点なら複数候補として残し、種類による同点判定の偏りを検出する。</summary>
    /// <remarks>実ファイルからの候補選択を保証します。自動設定のStrong条件は共通評価のケースへ分担します。</remarks>
    [TestMethod]
    public void TryResolveInstalledDestinationFromPackage_MixedBmsAndBmsonTopTieReturnsMultipleCandidateDirectories()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string dirA = Path.Combine("C:\\Installed", "DirA");
        string dirB = Path.Combine("C:\\Installed", "DirB");
        List<ChartFile> installedFiles =
        [
            CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(dirA, "a.bms"))
        ];
        ChartFile installedBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine(dirB, "b.bmson"),
            Folder = dirB,
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        ChartFile pendingBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "C:\\Pending\\a.bms");
        ChartFile pendingBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\b.bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        var package = ChartPackage.FromChartEntries(
        [
            PackageChartEntry.FromChart((pendingBms)),
            PackageChartEntry.FromChart((pendingBmson))
        ]);
        package.path = "C:\\Pending";
        package.delete_parent = false;

        InstalledDirectoryLookupResult result = service.TryResolveInstalledDestinationFromPackage(
            package,
            package.ChartEntries,
            CreateInstalledChartLookupIndexSnapshot(installedFiles, [installedBmson]));

        Assert.AreEqual(InstalledDirectoryResolveReason.MultipleCandidateDirectories, result.Reason);
        Assert.IsFalse(result.Success);
        Assert.AreEqual(2, result.CandidateDirectoryCount);
        CollectionAssert.AreEqual(new[] { dirA, dirB }, result.CandidateDirectories.ToArray());
    }

    /// <summary>候補フォルダを指定してもリソース索引がなければ推定不能を返し、索引を迂回した自動適用を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectoryForCandidateDirectories_RequiresResourceIndex()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);
            package.path = sourceDir;
            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            InstallEstimationResult result = service.EstimateInstallationDirectoryForCandidateDirectories(
                snapshot,
                [candidateDir],
                directoryLookupCache: null,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.AreEqual("resource_index_unavailable", result.ConfidenceReason);
            Assert.AreEqual("candidate_limited_resource_index_unavailable", result.CandidateMode);
            Assert.AreEqual("resource_index_unavailable", result.CoarseFilterMode);
        });
    }

    /// <summary>合流先だけを探すモードでは入力元を候補から除き、外部のリソース一致先を選び、元フォルダへの自己合流を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_NormalMode_SelectsExternalCandidate()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string mergeDir = Path.Combine(tempRoot, "merge");
            string soundDir = Path.Combine(sourceDir, "sound");
            string mergeSoundDir = Path.Combine(mergeDir, "sound");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(mergeDir);
            Directory.CreateDirectory(soundDir);
            Directory.CreateDirectory(mergeSoundDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(soundDir, "01.wav"), "src");
            File.WriteAllText(Path.Combine(mergeSoundDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(mergeSoundDir, "01.wav"), "dst");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), Path.Combine("sound", "00.wav"), Path.Combine("sound", "01.wav"));
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 1)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", Path.Combine("sound", "01.wav")]);
            lookupCache.AddDir(mergeDir, [Path.Combine("sound", "00.wav"), Path.Combine("sound", "01.wav")]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(mergeDir, result.SelectedCandidate?.DirectoryPath);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
        });
    }

    /// <summary>名前の粗い絞込みが一致しなくてもリソースで一致する候補を評価し、名前だけで有効な所持先を捨てる誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_UsesFallbackCandidateExpansion_WhenNameHashFilterMisses()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["sound.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(1, result.CandidateDirectoryCount, debugSummary);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath, debugSummary);
        });
    }

    /// <summary>粗い絞込みで一致がなければ候補なしを返し、無関係な全フォルダの評価や自動適用を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_DoesNotFallbackToAllCandidates_WhenBroadFilterFindsNoCandidates()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["other.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(0, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterHashFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.IsFalse(result.HasViableDestination);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        });
    }

    /// <summary>音源一致は一件以上を要求し、二件要求の半分しか満たさない候補を80%条件で除外する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_UsesOneMatchAndEightyPercentFulfillment()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string weakCandidateDir = Path.Combine(tempRoot, "weak");
            string strongCandidateDir = Path.Combine(tempRoot, "strong");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
            file = file with { Resources = TestChartResources.ReplaceVisual(file.Resources, new HashSet<string>(["title.png"], StringComparer.OrdinalIgnoreCase)) };
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(weakCandidateDir, ["00.wav", "title.png"]);
            lookupCache.AddDir(strongCandidateDir, ["00.wav", "01.wav", "title.png"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(2, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.AreEqual(1, result.AudioMinimumMatchRequired);
            Assert.AreEqual(strongCandidateDir, result.SelectedCandidate?.DirectoryPath);
        });
    }

    /// <summary>音声参照が一つだけなら一つの一致で候補を受け付け、複数参照用の下限を誤適用する拒否を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_AllowsSingleAudioMatch_WhenAudioRefsIsOne()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav");
            file = file with { Resources = TestChartResources.ReplaceVisual(file.Resources, new HashSet<string>(["title.png"], StringComparer.OrdinalIgnoreCase)) };
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "title.png"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.AudioMinimumMatchRequired);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        });
    }

    /// <summary>音声参照のない譜面は画像一致から推定し、音声一致下限による全候補の拒否を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_IsSkipped_WhenAudioRefsIsZero()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"));
            file = file with { Resources = TestChartResources.ReplaceVisual(file.Resources, new HashSet<string>(["bg.png"], StringComparer.OrdinalIgnoreCase)) };
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 0, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["bg.png"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(0, result.AudioMinimumMatchRequired);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        });
    }

    /// <summary>入力元を補った一致率も閾値未満なら候補を除き、不足リソースを残すフォルダの適用を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioHealthGate_DropsCandidateBelowThreshold_WhenPackageUnionStillInsufficient()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        });
    }

    /// <summary>通常推定では同梱音声を一致率に加え、合流候補では加えず、モード間の評価入力の混同を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioHealthGate_UsesBundledResourcesInNormalMode_ButNotInReinstallCorrectionMode()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateDir, "01.wav"), "dst");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "02.wav"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

            InstallEstimationResult normalResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            InstallEstimationResult mergeResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.ReinstallCorrection, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(1, normalResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateDir, normalResult.SelectedCandidate?.DirectoryPath);

            Assert.AreEqual(0, mergeResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, mergeResult.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", mergeResult.ConfidenceReason);
        });
    }

    /// <summary>同梱音声だけで一致率を満たしても外部音声一致のない候補を拒否し、同梱分だけによる誤採用を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_DropsCandidateSatisfiedOnlyByBundledResources()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(candidateDir, "title.png"), "visual");
            for (int i = 0; i < 71; i++)
            {
                File.WriteAllText(Path.Combine(sourceDir, i.ToString("D2") + ".wav"), "bundled");
            }

            ChartFile file = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(sourceDir, "chart.bms"),
                [.. Enumerable.Range(0, 100).Select(i => i.ToString("D2") + ".wav")]);
            file = file with { Resources = TestChartResources.ReplaceVisual(file.Resources, new HashSet<string>(["title.png"], StringComparer.OrdinalIgnoreCase)) };
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 100, wavExisting: 71)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", .. Enumerable.Range(0, 71).Select(i => i.ToString("D2") + ".wav")]);
            lookupCache.AddDir(candidateDir, ["title.png"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        });
    }

    /// <summary>同じbasenameを含む音声でも相対パスの完全一致を数え、有効な候補を一致不足として落とす誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_AudioGate_RelativePathExactCanRescueViability()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");

            ChartFile file = CreateFile(
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Path.Combine(sourceDir, "chart.bms"),
                Path.Combine("a", "00.wav"),
                Path.Combine("b", "01.wav"),
                Path.Combine("c", "01.wav"));
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, [Path.Combine("a", "00.wav"), Path.Combine("b", "01.wav"), Path.Combine("c", "01.wav")]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(1, result.CandidateDirectoryCount);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
            Assert.AreEqual(3, result.SelectedCandidate?.AudioMatched);
            Assert.AreEqual(3, result.SelectedCandidate?.AudioExactMatched);
        });
    }

    /// <summary>実在しないフォルダの索引だけから一致先を選び、推定時のファイル再列挙への依存を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_UsesCachedHashesWithoutRuntimeDirectoryEnumeration()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Temp", "source");
        string candidateDir = Path.Combine("C:\\Library", "candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "keysound.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["keysound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(1, result.CandidateDirectoryCount);
    }

    /// <summary>実ファイルを用意しない索引の候補でも音声一致率不足を拒否し、キャッシュ経路で健全性判定を省く誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_CachedCandidateBelowAudioThresholdIsRejectedWithoutFileSystemInput()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Temp", "source");
        string candidateDir = Path.Combine("C:\\Library", "candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate);
        Assert.AreEqual(0, result.CandidateDirectoryCount);
        Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
    }

    /// <summary>実フォルダの音声・画像・動画を同梱集合へ取り込み、譜面の定義集合と区別して、同梱資源の欠落や混同を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_DirectoryPackageBuildsBundledResources()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string soundDir = Path.Combine(sourceDir, "sound");
            string imageDir = Path.Combine(sourceDir, "image");
            string movieDir = Path.Combine(sourceDir, "movie");
            Directory.CreateDirectory(soundDir);
            Directory.CreateDirectory(imageDir);
            Directory.CreateDirectory(movieDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "chart2.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");
            File.WriteAllText(Path.Combine(imageDir, "bg.png"), "image");
            File.WriteAllText(Path.Combine(movieDir, "pv.mp4"), "movie");

            ChartFile primary = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart1.bms"), Path.Combine("sound", "00.wav"));
            primary = primary with { Resources = TestChartResources.ReplaceVisual(primary.Resources, new HashSet<string>([Path.Combine("image", "bg.png")], StringComparer.OrdinalIgnoreCase)) };
            ChartFile secondary = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine(sourceDir, "chart2.bms"), Path.Combine("sound", "00.wav"));
            primary = ChartFileProjection.WithMaintenance(primary, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(primary, wavDefined: 1, wavExisting: 0)));
            secondary = ChartFileProjection.WithMaintenance(secondary, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(secondary, wavDefined: 1, wavExisting: 0)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([primary, secondary]);

            package.path = sourceDir;

            package.delete_parent = true;

            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [primary, secondary]);
            uint expectedAudioRelativePathHash = ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(soundDir, "00.wav"))));
            uint expectedImageRelativePathHash = ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(imageDir, "bg.png"))));
            uint expectedMovieRelativePathHash = ChartResourceKeyHash.GetLookupHash(ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(ChartResourcePathNormalizer.NormalizeRelativePathForLookup(sourceDir, Path.Combine(movieDir, "pv.mp4"))));

            Assert.AreEqual(ChartFileKind.Bms, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(primary.Path, snapshot.RepresentativeChart.Path);

            Assert.AreEqual(2, snapshot.ChartCount);
            var expectedDefinedResources = ChartResourceSnapshot.CreateAggregate(package.ChartEntries.Select(entry => entry.Chart));
            CollectionAssert.AreEquivalent(expectedDefinedResources.AudioRelativePathHashes.ToArray(), snapshot.DefinedResources.AudioRelativePathHashes.ToArray());
            CollectionAssert.AreEquivalent(expectedDefinedResources.VisualRelativePathHashes.ToArray(), snapshot.DefinedResources.VisualRelativePathHashes.ToArray());
            CollectionAssert.AreEquivalent(expectedDefinedResources.MovieRelativePathHashes.ToArray(), snapshot.DefinedResources.MovieRelativePathHashes.ToArray());
            Assert.AreEqual(1, snapshot.BundledAudioCount);
            Assert.AreEqual(1, snapshot.BundledImageCount);
            Assert.AreEqual(1, snapshot.BundledMovieCount);
            CollectionAssert.Contains(snapshot.BundledResources.AudioRelativePathHashArray, expectedAudioRelativePathHash);
            CollectionAssert.Contains(snapshot.BundledResources.ImageRelativePathHashArray, expectedImageRelativePathHash);
            CollectionAssert.Contains(snapshot.BundledResources.MovieRelativePathHashArray, expectedMovieRelativePathHash);
        });
    }

    /// <summary>形式ごとの原題・作者入力を対象プロファイルへ渡し、BMSの合成作者採用とbmsonのRawArtist採用・空作者補完を検出する。</summary>
    /// <remarks>BMSは未知のSUBARTISTを実解析し、bmsonはchart役割を含むsubartistsを実解析した後にRawArtistが異なる現在値を渡し、空作者行ではArtistを空にする。副題・譜面名付きのTitleからRawTitleを補完しない。</remarks>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void PackageInstallEstimationSnapshotBuilder_BuildsTargetMetadataProfileFromFormatSpecificMetadata(bool bmson, bool emptyArtist)
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            string chartPath = Path.Combine(sourceDir, bmson ? "chart.bmson" : "chart.bms");
            ChartFile chart;
            if (bmson)
            {
                File.WriteAllText(chartPath, """
                    {"version":"1.0.0","info":{"title":"Song Name","subtitle":"Other Name","chart_name":"ANOTHER","artist":"Artist","subartists":["chart: Contributor"]},"sound_channels":[]}
                    """);
                chart = BmsonChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                Assert.AreNotEqual(chart.RawArtist, chart.Artist);
                // 現在値のArtistを採用する契約を、RawArtistの誤選択と空作者補完の両方から識別する。
                chart = chart with { RawArtist = "Different Raw Artist", Artist = emptyArtist ? string.Empty : chart.Artist };
            }
            else
            {
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Song Name\r\n#SUBTITLE Alternate Name\r\n#ARTIST Artist\r\n#SUBARTIST Unrelated Contribution\r\n#WAVAA 00.wav\r\n#00111:AA\r\n");
                chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                Assert.AreNotEqual(chart.RawArtist, chart.Artist);
            }
            Assert.AreNotEqual(chart.RawTitle, chart.Title);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([chart]);
            package.path = sourceDir;
            package.delete_parent = true;

            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [chart]);

            Assert.AreEqual("song name", snapshot.TargetMetadataProfile.DominantNormalizedTitle);
            Assert.AreEqual(emptyArtist ? string.Empty : "artist", snapshot.TargetMetadataProfile.DominantNormalizedArtist);
            Assert.AreEqual("Song Name", snapshot.TargetMetadataProfile.DominantPairDisplayTitle);
            Assert.AreEqual(emptyArtist ? string.Empty : "Artist", snapshot.TargetMetadataProfile.DominantPairDisplayArtist);

            ChartFile noRawTitle = chart with { RawTitle = string.Empty };
            PackageInstallEstimationSnapshot emptyTitleSnapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(noRawTitle)]);
            Assert.IsFalse(emptyTitleSnapshot.TargetMetadataProfile.HasPair);
        });
    }

    /// <summary>単一ファイルのパッケージでは同じ親の音声・画像・動画を同梱扱いせず、兄弟資源による一致率の水増しを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_FilePackageDoesNotIncludeSiblingBundledResources()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string nestedDir = Path.Combine(sourceDir, "Nested");
            Directory.CreateDirectory(nestedDir);
            Directory.CreateDirectory(sourceDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "audio");
            File.WriteAllText(Path.Combine(sourceDir, "background.png"), "image");
            File.WriteAllText(Path.Combine(sourceDir, "preview.mp4"), "movie");
            File.WriteAllText(Path.Combine(sourceDir, "sibling.bmson"), "{}");
            File.WriteAllText(Path.Combine(nestedDir, "nested.wav"), "audio");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, "00.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = file.Path;

            package.delete_parent = false;

            PackageInstallSurfaceSnapshot surface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(chartPath);
            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(
                package,
                package.ChartEntries,
                surface,
                sourceSurfaceCacheHit: false);

            Assert.AreEqual(chartPath, surface.SourcePath);
            Assert.AreEqual(0, snapshot.BundledAudioCount);
            Assert.AreEqual(0, snapshot.BundledImageCount);
            Assert.AreEqual(0, snapshot.BundledMovieCount);
            Assert.AreEqual(sourceDir, snapshot.SourceDirectory);

            Assert.AreEqual(0, snapshot.SourceCandidateResources.AudioFileNameHashCount);
            Assert.AreEqual(0, snapshot.SourceCandidateResources.ImageFileNameHashCount);
            Assert.AreEqual(0, snapshot.SourceCandidateResources.MovieFileNameHashCount);
            Assert.AreEqual(0L, snapshot.SourceSurfaceScanMs);
            Assert.AreEqual(0, snapshot.SourceSurfaceChartFileCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceResourceFileCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceTrackedFileCount);
            Assert.AreEqual(0L, snapshot.SourceSurfaceHashMaterializeMs);
            Assert.AreEqual(string.Empty, snapshot.SourceSurfaceScanBackend);
            Assert.IsFalse(snapshot.SourceSurfaceScanLimitExceeded);
            Assert.AreEqual(0, snapshot.SourceSurfaceVisitedFileSystemEntryCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceMaxVisitedFileSystemEntryCount);
        });
    }

    /// <summary>単一ファイル入力は走査上限に達していても親フォルダを走査せず、無関係な兄弟列挙の発生を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_FilePackageDoesNotScanSourceSurfaceAtLimit()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "audio");
            File.WriteAllText(Path.Combine(sourceDir, "01.wav"), "audio");
            File.WriteAllText(Path.Combine(sourceDir, "ignore.txt"), "text");

            PackageInstallSurfaceSnapshot surface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(
                chartPath,
                maxVisitedFileSystemEntryCount: 3);

            Assert.IsFalse(surface.ScanLimitExceeded);
            Assert.AreEqual(0, surface.MaxVisitedFileSystemEntryCount);
            Assert.AreEqual(0, surface.VisitedFileSystemEntryCount);
            Assert.AreEqual(0, surface.TrackedFileCount);
            Assert.AreEqual(string.Empty, surface.ScanBackend);
            Assert.AreEqual(0, surface.SourceCandidateResources.AudioFileNameHashCount);

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, "00.wav");
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(
                package,
                package.ChartEntries,
                surface,
                sourceSurfaceCacheHit: false);

            Assert.IsFalse(snapshot.SourceSurfaceScanLimitExceeded);
            Assert.AreEqual(0, snapshot.SourceSurfaceVisitedFileSystemEntryCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceMaxVisitedFileSystemEntryCount);
        });
    }

    /// <summary>フォルダ走査が上限を超えたら途中までの資源集合を捨て、部分集合を完全な同梱資源として評価する誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_DirectoryPackageDiscardsPartialSurfaceAtLimit()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "DirectoryPackage");
            Directory.CreateDirectory(sourceDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "audio");
            File.WriteAllText(Path.Combine(sourceDir, "01.wav"), "audio");

            PackageInstallSurfaceSnapshot surface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(
                sourceDir,
                maxVisitedFileSystemEntryCount: 1);
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, "00.wav");
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);
            package.path = sourceDir;
            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(
                package,
                package.ChartEntries,
                surface,
                sourceSurfaceCacheHit: false);

            Assert.IsTrue(surface.ScanLimitExceeded);
            Assert.AreEqual(1, surface.MaxVisitedFileSystemEntryCount);
            Assert.IsTrue(surface.VisitedFileSystemEntryCount > surface.MaxVisitedFileSystemEntryCount);
            Assert.AreEqual(0, surface.BundledResources.AudioFileNameHashCount);
            Assert.AreEqual(0, surface.SourceCandidateResources.AudioFileNameHashCount);
            Assert.IsTrue(snapshot.SourceSurfaceScanLimitExceeded);
            Assert.AreEqual(surface.VisitedFileSystemEntryCount, snapshot.SourceSurfaceVisitedFileSystemEntryCount);
            Assert.AreEqual(surface.MaxVisitedFileSystemEntryCount, snapshot.SourceSurfaceMaxVisitedFileSystemEntryCount);
        });
    }

    /// <summary>単独Bmsonの相対音声参照を保持し、親フォルダを同梱走査して入力範囲を広げる誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_LooseBmsonKeepsRelativeResourcesWithoutScanningParent()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "LooseBmson");
            string nestedDir = Path.Combine(sourceDir, "Nested");
            Directory.CreateDirectory(nestedDir);
            string bmsonPath = Path.Combine(sourceDir, "chart.bmson");
            File.WriteAllText(
                bmsonPath,
                "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Loose Title\",\"artist\":\"Loose Artist\",\"mode_hint\":\"beat-7k\"},\"sound_channels\":[{\"name\":\"sound/00.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}");
            File.WriteAllText(Path.Combine(sourceDir, "sibling.wav"), "audio");
            File.WriteAllText(Path.Combine(nestedDir, "nested.wav"), "audio");
            ChartFile song = ChartTestValues.ReadBmson(bmsonPath);
            var entry = PackageChartEntry.FromChart((song));

            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([entry]);
            string expectedResourceKey = ChartResourcePathNormalizer.NormalizeResourceKeyForLookup("sound/00.wav");

            Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(bmsonPath, snapshot.RepresentativeChart.Path);
            Assert.AreEqual(sourceDir, snapshot.SourceDirectory);
            Assert.AreEqual(1, snapshot.ChartCount);
            Assert.AreEqual("loose title", snapshot.TargetMetadataProfile.DominantNormalizedTitle);
            CollectionAssert.Contains(snapshot.DefinedResources.AudioRelativePaths.ToArray(), expectedResourceKey);
            Assert.IsTrue(snapshot.DefinedResources.AudioReferences.Single().IsPathAware);

            Assert.AreEqual(0, snapshot.BundledResources.AudioFileNameHashCount);
            Assert.AreEqual(0, snapshot.SourceCandidateResources.AudioFileNameHashCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceTrackedFileCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceVisitedFileSystemEntryCount);
            Assert.AreEqual(0, snapshot.SourceSurfaceMaxVisitedFileSystemEntryCount);
            Assert.AreEqual(string.Empty, snapshot.SourceSurfaceScanBackend);
            Assert.IsFalse(snapshot.SourceSurfaceScanLimitExceeded);
        });
    }

    /// <summary>単一ファイルの走査しない入力面を再取得時に再利用し、毎回親を走査する回帰を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_FilePackageCachesNoScanSurface()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "FilePackageCache");
            Directory.CreateDirectory(sourceDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath);
            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);
            package.path = chartPath;

            PackageInstallEstimationSnapshot firstSnapshot = package.GetOrBuildInstallEstimationSnapshotFromEntries(package.ChartEntries);
            PackageInstallEstimationSnapshot secondSnapshot = package.GetOrBuildInstallEstimationSnapshotFromEntries(package.ChartEntries);

            Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
            Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
            Assert.AreEqual(sourceDir, firstSnapshot.SourceDirectory);
            Assert.AreEqual(string.Empty, firstSnapshot.SourceSurfaceScanBackend);
            Assert.AreEqual(0, firstSnapshot.SourceSurfaceTrackedFileCount);
            Assert.AreEqual(0, firstSnapshot.SourceSurfaceVisitedFileSystemEntryCount);
            Assert.AreEqual(0, firstSnapshot.SourceSurfaceMaxVisitedFileSystemEntryCount);
            Assert.AreEqual(string.Empty, secondSnapshot.SourceSurfaceScanBackend);
        });
    }

    /// <summary>実BMS・BME・Bmsonの共通エントリと音声入力面を再利用し、種類の欠落や二度目の走査を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_PathPackage_ReusesCommonEntriesAndSourceSurface()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bme"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart3.bmson"), "{}");
                File.WriteAllText(Path.Combine(soundDir, "00.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                List<PackageChartEntry> firstEntries = package.ChartEntries;
                List<PackageChartEntry> secondEntries = package.ChartEntries;
                PackageInstallEstimationSnapshot firstSnapshot = package.GetOrBuildInstallEstimationSnapshotFromEntries(firstEntries);
                PackageInstallEstimationSnapshot secondSnapshot = package.GetOrBuildInstallEstimationSnapshotFromEntries(firstEntries);

                CollectionAssert.AreEqual(
                    firstEntries.Select(entry => entry.Chart.Path).ToList(),
                    secondEntries.Select(entry => entry.Chart.Path).ToList());
                Assert.AreEqual(3, firstEntries.Count);
                Assert.AreEqual(3, secondEntries.Count);
                Assert.IsTrue(firstEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.Token == null));
                Assert.IsTrue(secondEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.Token == null));
                Assert.IsTrue(firstEntries.Any(entry => entry.Chart.Kind == ChartFileKind.Bmson));

                Assert.AreEqual(1, firstEntries.Count(entry => entry.Chart.Kind == ChartFileKind.Bmson));
                Assert.AreEqual(3, firstSnapshot.ChartCount);
                Assert.AreEqual(sourceDir, firstSnapshot.SourceDirectory);
                Assert.AreEqual("bounded_fast_source_surface", firstSnapshot.SourceSurfaceScanBackend);
                Assert.IsFalse(firstSnapshot.SourceSurfaceCacheHit);
                Assert.IsTrue(firstSnapshot.SourceSurfaceTrackedFileCount >= 4);
                Assert.AreEqual(1, firstSnapshot.BundledAudioCount);
                Assert.IsTrue(secondSnapshot.SourceSurfaceCacheHit);
            });
    }

    /// <summary>取得済みエントリの現在パスを変更した後も再取得で変更を保ち、キャッシュ再読込みによる現在値の巻戻しを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_PathPackage_PreservesEntryCurrentValuesAcrossReads()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(Path.Combine(sourceDir, "chart1.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDir, "chart2.bmson"), "{}");

                var package = new ChartPackage
                {
                    path = sourceDir,
                    delete_parent = true
                };

                List<PackageChartEntry> firstEntries = package.ChartEntries;
                string updatedPath = Path.Combine(sourceDir, "updated.bms");
                PackageChartEntry changedEntry = firstEntries.Single(entry => entry.Chart.Kind == ChartFileKind.Bms);
                changedEntry.ApplyCurrentChart(changedEntry.Chart with { Path = updatedPath });
                List<PackageChartEntry> secondEntries = package.ChartEntries;

                Assert.AreEqual(2, firstEntries.Count);
                Assert.AreEqual(2, secondEntries.Count);
                Assert.IsTrue(secondEntries.Any(entry => string.Equals(entry.Chart.Path, updatedPath, StringComparison.OrdinalIgnoreCase)));
                Assert.IsTrue(firstEntries.Any(entry => entry.Chart.Kind == ChartFileKind.Bmson));
                Assert.IsTrue(firstEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.Token == null));
                Assert.IsTrue(secondEntries.Where(entry => entry.Chart.Kind == ChartFileKind.Bms).All(entry => entry.Chart.Token == null));
                CollectionAssert.AreEqual(
                    secondEntries.Select(entry => entry.Chart.Path).OrderBy(path => path).ToList(),
                    package.ChartEntries.Select(entry => entry.Chart.Path).OrderBy(path => path).ToList());
            });
    }

    /// <summary>Bmsonの共通エントリから譜面数・音声定義・タイトル群を作り、BMS専用経路を要求する入力欠落を検出する。</summary>
    [TestMethod]
    public void PackageInstallEstimationSnapshotBuilder_AcceptsChartEntryWithoutCompatibilityAdapter()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            Directory.CreateDirectory(sourceDir);
            ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = Path.Combine(sourceDir, "chart.bmson"),
                Folder = Path.GetFileName(sourceDir),
                Title = "BMSON",
                RawTitle = "BMSON",
                Artist = "Artist",
                RawArtist = "Artist",
                Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                Level = 7,
                LevelText = "7",
                Resources = TestChartResources.Create([Path.Combine("sound", "keysound.wav")])
            };
            ChartFile chart = (song);
            var entry = PackageChartEntry.FromChart(chart);
            var package = new ChartPackage
            {
                path = sourceDir
            };
            PackageInstallSurfaceSnapshot sourceSurface = PackageInstallEstimationSnapshotBuilder.BuildPackageInstallSurfaceSnapshot(sourceDir);

            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.Build(package, [entry], sourceSurface, sourceSurfaceCacheHit: false);

            Assert.AreEqual(1, snapshot.ChartCount);
            Assert.AreEqual(chart.Path, snapshot.RepresentativeChart.Path);
            Assert.AreEqual(chart.Kind, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(1, snapshot.DefinedResources.TotalReferenceCount);
            Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual("bmson", snapshot.TargetMetadataProfile.DominantNormalizedTitle);

        });
    }

    /// <summary>BMSとBmsonの双方で同一ハッシュでも別パスは別対象とし、ハッシュだけによる対象の取り違えを検出する。</summary>
    [TestMethod]
    public void ChartFileIdentity_IsSameChartTarget_DoesNotMatchSameHashDifferentPath()
    {
        ChartFile firstBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\PendingA", "chart.bms"));
        ChartFile secondBms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\PendingB", "chart.bms"));
        ChartFile firstBmsChart = (firstBms);
        ChartFile secondBmsChart = (secondBms);
        ChartFile firstBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine("C:\\PendingA", "chart.bmson"),
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('b', 64)
        };
        ChartFile secondBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine("C:\\PendingB", "chart.bmson"),
            Md5 = firstBmson.Md5,
            Sha256 = firstBmson.Sha256
        };
        ChartFile firstBmsonChart = (firstBmson);
        ChartFile secondBmsonChart = (secondBmson);

        Assert.IsFalse(ChartFileIdentity.IsSameChartTarget(firstBmsChart, secondBmsChart));
        Assert.IsFalse(ChartFileIdentity.IsSameChartTarget(firstBmsonChart, secondBmsonChart));
    }

    /// <summary>Bmsonエントリのインストール先と警告をスナップショットへ引き渡し、共通入力への投影漏れを検出する。</summary>
    [TestMethod]
    public void PackageChartEntry_ToChartEntrySnapshot_CapturesBmsonDestinationAndWarnings()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine("C:\\Pending", "chart.bmson"),
            Title = "BMSON",
            RawTitle = "BMSON",
            Artist = "Artist",
            RawArtist = "Artist",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var entry = PackageChartEntry.FromChart((song));
        string destinationDirectory = Path.Combine("C:\\Installed", "Package");
        entry.ApplyInstallDestinationMetadata(destinationDirectory, "Resolved Title", "Resolved Artist");
        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");

        PackageChartEntry snapshot = entry.ToChartEntrySnapshot();

        Assert.IsNotNull(snapshot);

        Assert.AreEqual(ChartFileKind.Bmson, snapshot.Chart.Kind);
        Assert.AreEqual(destinationDirectory, snapshot.Chart.InstallDestination);
        Assert.IsTrue(snapshot.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
    }

    /// <summary>インストール先のパス・タイトル・作者・警告を現在譜面へ反映し、元譜面の情報で適用先を上書きする誤りを検出する。</summary>
    [TestMethod]
    public void PackageChartEntry_BmsCurrentChartKeepsInstallDestinationOnChartState()
    {
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"));
        var entry = PackageChartEntry.FromChart((file));
        string destinationDirectory = Path.Combine("C:\\Installed", "Package");
        string installedPath = Path.Combine(destinationDirectory, "chart.bms");

        entry.ApplyInstallDestinationMetadata(destinationDirectory, "Resolved Title", "Resolved Artist");
        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");
        entry.ApplyInstalledPath(installedPath);

        Assert.AreNotEqual(installedPath, file.Path);
        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
        Assert.AreEqual(installedPath, entry.Chart.Path);
        Assert.AreEqual(destinationDirectory, entry.Chart.InstallDestination);
        Assert.AreEqual("Resolved Title", entry.Chart.InstallDestinationTitle);
        Assert.AreEqual("Resolved Artist", entry.Chart.InstallDestinationArtist);
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
    }

    /// <summary>適用先にあるreadmeから現在譜面のテキスト件数を再計算し、入力元の未計算値を残す誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageChartEntry_BmsCurrentChartRecomputesTextGroupOnInstalledPath()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            File.WriteAllText(installedPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(destinationDirectory, "readme.txt"), "text group");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            var entry = PackageChartEntry.FromChart((file));

            entry.ApplyInstalledPath(installedPath);

            Assert.AreEqual(1, entry.Chart.Txt);
        });
    }

    /// <summary>readmeのない適用先では元譜面のテキスト件数を消し、古いフォルダの件数が残る誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageChartEntry_BmsCurrentChartClearsStaleTextGroupOnInstalledPath()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            File.WriteAllText(installedPath, "#PLAYER 1");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            file = file with { Txt = 1 };
            var entry = PackageChartEntry.FromChart((file));

            entry.ApplyInstalledPath(installedPath);

            Assert.AreEqual(0, entry.Chart.Txt);
        });
    }

    /// <summary>適用先の実更新日時を現在譜面のDateへ反映し、移動後も日時が未設定になる誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageChartEntry_BmsCurrentChartSetsDateFromInstalledFileMtime()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            DateTime installedTimestamp = new(2026, 6, 9, 2, 30, 0, DateTimeKind.Utc);
            File.WriteAllText(installedPath, "#PLAYER 1");
            File.SetLastWriteTimeUtc(installedPath, installedTimestamp);
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            var entry = PackageChartEntry.FromChart((file));

            entry.ApplyInstalledPath(installedPath);

            int expectedDate = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(installedPath));

            Assert.AreEqual(expectedDate, entry.Chart.Date);
        });
    }

    /// <summary>適用先の更新日時で古いDateを置き換えAddDateを消し、入力元の日時を引き継ぐ誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void PackageChartEntry_BmsCurrentChartReplacesStaleDateOnInstalledPath()
    {
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService _)
        {
            string destinationDirectory = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(destinationDirectory);
            string installedPath = Path.Combine(destinationDirectory, "chart.bms");
            DateTime installedTimestamp = new(2026, 6, 9, 3, 45, 0, DateTimeKind.Utc);
            File.WriteAllText(installedPath, "#PLAYER 1");
            File.SetLastWriteTimeUtc(installedPath, installedTimestamp);
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(tempRoot, "Pending", "chart.bms"));
            file = file with { Date = 12345 };
            file = file with { AddDate = 67890 };
            var entry = PackageChartEntry.FromChart((file));

            entry.ApplyInstalledPath(installedPath);

            int expectedDate = Lr2SongRowEnricher.ToLr2UnixSeconds(File.GetLastWriteTimeUtc(installedPath));

            Assert.AreEqual(expectedDate, entry.Chart.Date);
            Assert.IsFalse(entry.Chart.AddDate.HasValue);
        });
    }

    /// <summary>パッケージ警告と保存側警告を現在譜面へ合わせ、片方の警告を隠す投影の誤りを検出する。</summary>
    [TestMethod]
    public void PackageChartEntry_BmsCurrentChartProjectsPackageWarningsWithoutHidingStorageWarnings()
    {
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"));
        var entry = PackageChartEntry.FromChart((file));

        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, "duplicate warning")] };
        entry.ApplyCurrentChart(file);

        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstalledDestinationResolveFailed));
        Assert.IsTrue(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.DuplicateChart));
    }

    /// <summary>検索中の表示を既存BMS状態へ重ね、その間の現在状態変更も解除後に保ち、検索フラグによる状態の巻戻しを検出する。</summary>
    [TestMethod]
    public void PackageChartEntry_SetSearchingStatus_ProjectsBmsStatusPreservesCapturedChart()
    {
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "chart.bms"));
        file = file with { Status = ChartFileStatus.PLAY };
        var entry = PackageChartEntry.FromChart((file));

        entry.SetSearchingStatus(isSearching: true);

        Assert.AreEqual(ChartFileStatus.PLAY, file.Status);
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PLAY));
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

        file = file with { Status = file.Status | ChartFileStatus.PAUSE };
        entry.ApplyCurrentChart(file);

        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PAUSE));
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

        entry.SetSearchingStatus(isSearching: false);

        Assert.AreEqual(ChartFileStatus.PLAY | ChartFileStatus.PAUSE, file.Status);
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PLAY));
        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.PAUSE));
        Assert.IsFalse(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));
    }

    /// <summary>Bmsonエントリで検索開始・解除が現在状態へ反映され、BMS専用表示への依存を検出する。</summary>
    [TestMethod]
    public void PackageChartEntry_SetSearchingStatus_ProjectsAdapterlessBmsonStatus()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine("C:\\Pending", "chart.bmson"),
            Title = "BMSON",
            RawTitle = "BMSON",
            Artist = "Artist",
            RawArtist = "Artist",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        };
        var entry = PackageChartEntry.FromChart((song));

        entry.SetSearchingStatus(isSearching: true);

        Assert.IsTrue(entry.Chart.Status.HasFlag(ChartFileStatus.SEARCHING));

        entry.SetSearchingStatus(isSearching: false);

        Assert.AreEqual(ChartFileStatus.NONE, entry.Chart.Status);
    }

    /// <summary>実Bmsonファイルを共通エントリとして発見し音声定義へつなぎ、Bmsonの発見・解析の接続漏れを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_PathPackage_DiscoversBmsonAndItsResourceReferences()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string soundDir = Path.Combine(sourceDir, "sound");
                Directory.CreateDirectory(soundDir);
                File.WriteAllText(
                    Path.Combine(sourceDir, "chart.bmson"),
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7},"
                        + "\"sound_channels\":[{\"name\":\"sound/keysound.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}");

                var package = new ChartPackage
                {
                    path = sourceDir
                };

                List<PackageChartEntry> entries = package.ChartEntries;
                PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, []);

                Assert.AreEqual(1, entries.Count);
                Assert.AreEqual(ChartFileKind.Bmson, entries[0].Chart.Kind);

                Assert.AreEqual(1, snapshot.ChartCount);
                Assert.AreEqual(1, snapshot.DefinedResources.AudioReferenceCount);
            });
    }

    /// <summary>実Bmsonのタイトルをパッケージ表示へ反映し、譜面種類によって表示情報を失う誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_DisplayTitle_UsesDiscoveredBmsonTitle()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(
                    Path.Combine(sourceDir, "chart.bmson"),
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                var package = new ChartPackage
                {
                    path = sourceDir
                };
                List<PackageChartEntry> entries = package.ChartEntries;

                string displayTitle = package.DisplayTitle;

                Assert.AreEqual("BMSON", displayTitle);
                Assert.AreEqual(1, entries.Count);

            });
    }

    /// <summary>実Bmson二件のうち条件に合う一件だけを除き、種類による削除漏れや無関係なエントリの削除を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_RemoveChartEntriesPredicate_RemovesAdapterlessBmsonEntryWithoutMaterializing()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "PathPackage");
                string keepBmsonPath = Path.Combine(sourceDir, "keep.bmson");
                string removeBmsonPath = Path.Combine(sourceDir, "remove.bmson");
                Directory.CreateDirectory(sourceDir);
                File.WriteAllText(
                    keepBmsonPath,
                    "{\"info\":{\"title\":\"Keep\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                File.WriteAllText(
                    removeBmsonPath,
                    "{\"info\":{\"title\":\"Remove\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                var package = new ChartPackage
                {
                    path = sourceDir
                };
                List<PackageChartEntry> entries = package.ChartEntries;

                package.RemoveChartEntries(entry =>
                    !string.IsNullOrWhiteSpace(entry?.Chart?.Path)
                    && string.Equals(entry?.Chart?.Path, removeBmsonPath, StringComparison.OrdinalIgnoreCase));

                Assert.AreEqual(1, package.ChartEntries.Count);
                Assert.AreEqual(keepBmsonPath, package.ChartEntries[0].Chart.Path);
            });
    }

    /// <summary>複数譜面の表示タイトルでは先頭譜面の主題を保ち、主題表示の欠落や他譜面への取り違えを検出する。</summary>
    [TestMethod]
    public void ChartPackage_DisplayTitle_PreservesMultiChartRawTitle()
    {
        ChartFile primary = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Pending", "a.bms"));
        primary = primary with { RawTitle = "Main Title", RawSubtitle = "Sub Title", Title = "Main Title Sub Title", Subtitle = "Sub Title" };
        ChartFile secondary = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Path.Combine("C:\\Pending", "b.bms"));
        ChartPackage package = ChartPackageTestExtensions.CreatePackage([primary, secondary]);

        Assert.AreEqual("Main Title", package.DisplayTitle);
    }

    /// <summary>パッケージのパス変更後は新フォルダの譜面を発見し、新しい入力面を構築してから再利用することで、古い発見・入力面キャッシュの使い回しを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ChartPackage_PathPackage_InvalidatesChartDiscoveryAndSourceSurfaceSnapshots_WhenPathChanges()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDirA = Path.Combine(tempRoot, "PathPackageA");
                string sourceDirB = Path.Combine(tempRoot, "PathPackageB");
                Directory.CreateDirectory(sourceDirA);
                Directory.CreateDirectory(sourceDirB);
                File.WriteAllText(Path.Combine(sourceDirA, "a.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDirB, "b.bms"), "#PLAYER 1");
                File.WriteAllText(Path.Combine(sourceDirB, "01.wav"), "audio");

                var package = new ChartPackage
                {
                    path = sourceDirA,
                    delete_parent = true
                };

                List<ChartFile> filesFromA = package.GetBmsChartsForTest();
                PackageInstallEstimationSnapshot snapshotA = BuildPackageSnapshot(package, filesFromA);

                package.path = sourceDirB;
                List<ChartFile> filesFromB = package.GetBmsChartsForTest();
                PackageInstallEstimationSnapshot snapshotB = BuildPackageSnapshot(package, filesFromB);
                PackageInstallEstimationSnapshot cachedSnapshotB = BuildPackageSnapshot(package, filesFromB);

                Assert.AreEqual(1, filesFromA.Count);
                Assert.AreEqual(1, filesFromB.Count);
                Assert.AreEqual(Path.Combine(sourceDirB, "b.bms"), filesFromB[0].Path);
                Assert.AreEqual(sourceDirA, snapshotA.SourceDirectory);
                Assert.AreEqual(sourceDirB, snapshotB.SourceDirectory);
                Assert.IsFalse(snapshotA.SourceSurfaceCacheHit);
                Assert.IsFalse(snapshotB.SourceSurfaceCacheHit);
                Assert.IsTrue(cachedSnapshotB.SourceSurfaceCacheHit);
                Assert.AreEqual("bounded_fast_source_surface", snapshotB.SourceSurfaceScanBackend);
            });
    }

    /// <summary>入力元と候補の音声を合わせて不足分を補える適用先を選び、片方だけの一致率で有効先を落とす誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_PackageUnionPrefersDestinationWithBaseResourcesMissingFromSourcePackage()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string expectedDir = Path.Combine(tempRoot, "ExpectedInstall");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(expectedDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(expectedDir, "00.wav"), "existing-base");
            File.WriteAllText(Path.Combine(expectedDir, "01.wav"), "existing-base");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "02.wav"]);
            lookupCache.AddDir(expectedDir, ["00.wav", "01.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(expectedDir, result.SelectedCandidate?.DirectoryPath, debugSummary);

        });
    }

    /// <summary>同梱音声も相対パスを区別して合わせ、別階層の同名音声による一致率の水増しを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_PackageUnionDoesNotMatchBundledResourceByBasenameOnly()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string sourceSoundDir = Path.Combine(sourceDir, "sound");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceSoundDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceSoundDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "existing");
            File.WriteAllText(Path.Combine(candidateDir, "01.wav"), "existing");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav", "02.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 0)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "sound\\02.wav"]);
            lookupCache.AddDir(candidateDir, ["00.wav", "01.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.IsFalse(result.ShouldAutoApplyDestination, result.SelectedCandidateSummary);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory), result.SelectedCandidateSummary);
            Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter, result.SelectedCandidateSummary);
            Assert.AreEqual(0, result.CandidateDirectoryCountAfterAudioGate, result.SelectedCandidateSummary);
            Assert.IsNull(result.SelectedCandidate, result.SelectedCandidateSummary);
        });
    }

    /// <summary>有効な外部候補がなければ適用先なし・自動適用なしで返し、不足先を低信頼候補として適用する誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_BelowThresholdReturnsHighWithoutDestination()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string weakDir = Path.Combine(tempRoot, "WeakCandidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(weakDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(weakDir, "00.wav"), "candidate");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(weakDir, ["00.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
        });
    }

    /// <summary>保留パッケージの指定先が有効なら解決済みパスと対象エントリを返し、受理した対象の取り違えを検出する。</summary>
    [TestMethod]
    public void ValidateInstallDestination_ReturnsResolvedDirectoryForPendingPackage()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
            string installDirectoryPath = Path.Combine(tempRoot, "install");
            Directory.CreateDirectory(pendingDirectoryPath);
            Directory.CreateDirectory(installDirectoryPath);
            ChartFile pendingFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(pendingDirectoryPath, "chart.bms"));
            ChartPackage pendingPackage = ChartPackageTestExtensions.CreatePackage([pendingFile]);
            pendingPackage.path = pendingDirectoryPath;
            pendingPackage.delete_parent = false;

            PendingInstallDestinationSelectionResult result = service.ValidateInstallDestination(
                PackageChartEntry.FromChart((pendingFile)),
                [pendingPackage],
                [installDirectoryPath],
                installDirectoryPath);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(installDirectoryPath, result.ValidatedDestinationDirectory);
            Assert.AreEqual(1, result.TargetEntries.Count);
            Assert.AreEqual(pendingFile.Path, result.TargetEntries[0].GetBmsChartForTest().Path);
        });
    }

    /// <summary>別インスタンスの同一パスBmsonをパッケージへ照合し、参照同一性だけで有効な適用先を拒否する誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ValidateInstallDestination_MatchesAdapterlessBmsonPackageByChartPath()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
                string installDirectoryPath = Path.Combine(tempRoot, "install");
                Directory.CreateDirectory(pendingDirectoryPath);
                Directory.CreateDirectory(installDirectoryPath);
                string bmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
                File.WriteAllText(
                    bmsonPath,
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                var targetEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(bmsonPath)));
                var pendingPackage = new ChartPackage
                {
                    path = pendingDirectoryPath,
                    delete_parent = false
                };
                List<PackageChartEntry> entries = pendingPackage.ChartEntries;
                Assert.AreEqual(1, entries.Count);

                PendingInstallDestinationSelectionResult result = service.ValidateInstallDestination(
                    targetEntry,
                    [pendingPackage],
                    [installDirectoryPath],
                    installDirectoryPath);

                Assert.IsTrue(result.Success);
                Assert.AreEqual(installDirectoryPath, result.ValidatedDestinationDirectory);
                Assert.AreEqual(1, result.TargetEntries.Count);
                Assert.AreEqual(bmsonPath, result.TargetEntries[0].Chart.Path);

            });
    }

    /// <summary>存在しない適用先をBmsonエントリでも拒否し、種類によるパス検査の迂回を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void ValidateInstallDestination_RejectsMissingDestinationForBmsonEntry()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string pendingDirectoryPath = Path.Combine(tempRoot, "pending");
                Directory.CreateDirectory(pendingDirectoryPath);
                string bmsonPath = Path.Combine(pendingDirectoryPath, "chart.bmson");
                File.WriteAllText(
                    bmsonPath,
                    "{\"info\":{\"title\":\"BMSON\",\"artist\":\"Artist\",\"mode_hint\":\"beat-7k\",\"level\":7}}");
                var targetEntry = PackageChartEntry.FromChart((ChartTestValues.ReadBmson(bmsonPath)));
                var pendingPackage = new ChartPackage
                {
                    path = pendingDirectoryPath,
                    delete_parent = false
                };
                List<PackageChartEntry> entries = pendingPackage.ChartEntries;
                Assert.AreEqual(1, entries.Count);

                PendingInstallDestinationSelectionResult result = service.ValidateInstallDestination(
                    targetEntry,
                    [pendingPackage],
                    [Path.Combine(tempRoot, "install")],
                    Path.Combine(tempRoot, "missing"));

                Assert.IsFalse(result.Success);
                Assert.AreEqual(1, result.TargetEntries.Count);

            });
    }

    /// <summary>推定処理が元フォルダを示しても補正後は適用先を消し、同一フォルダへの適用を残す誤りを検出する。</summary>
    [TestMethod]
    public void CorrectChartInstallationDirectory_ClearsSameDirectorySuggestion()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Music", "FolderA", "chart.bms"));
        var entry = PackageChartEntry.FromChart((file));

        service.CorrectChartInstallationDirectory([entry], delegate (PackageChartEntry target)
        {
            target.SetInstallDestinationPathOnly(Path.Combine("C:\\Music", "FolderA"));
        });

        Assert.AreEqual(string.Empty, entry.Chart.InstallDestination);
    }

    /// <summary>適用先解除で解決失敗の警告も消し、解除済みエントリへ古い警告を残す誤りを検出する。</summary>
    [TestMethod]
    public void ClearInstallDestinations_ClearsResolveFailedWarning()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine("C:\\Music", "FolderA", "chart.bms"));
        var entry = PackageChartEntry.FromChart((file));
        entry.SetWarning(ChartWarningKind.InstalledDestinationResolveFailed, "resolve failed");

        service.ClearInstallDestinations([entry]);

        Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
        Assert.IsFalse(file.Warnings.Any(warning => warning.Category == ChartWarningCategory.InstallEstimation));
    }

    /// <summary>再推定では元より音声が改善する一つの候補を自動適用し、有効な改善先を候補だけに留める誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_AutoAppliesOnlyImprovedUniqueCandidate()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file = file with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection,
            metadataProfileResolver: _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "candidate")]));

        Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
        Assert.AreEqual("reinstall_single_improved_candidate", result.ConfidenceReason);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.AreEqual(candidateDir, result.DestinationDirectory);
        Assert.AreEqual(0, result.SourceBaselinePrimaryHealth);
        Assert.AreEqual(100, result.SelectedCandidatePrimaryHealth);
        Assert.AreEqual("candidate_only_RelatedOnly", result.CandidateMode);
    }

    /// <summary>元と同じ音声健全性の再推定先は自動適用せず、改善しない変更を実行する誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_DoesNotAutoApplyWhenCandidateDoesNotImproveHealth()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file = file with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms", "sound.wav"]);
        lookupCache.AddDir(candidateDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection,
            metadataProfileResolver: _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "candidate")]));

        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.AreEqual(InstallEstimationLowConfidenceKind.ReinstallNotImproved, result.LowConfidenceKind);
        Assert.AreEqual("reinstall_candidate_not_improved", result.ConfidenceReason);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
        CollectionAssert.AreEqual(new[] { candidateDir }, result.SuggestedDestinationDirectories.ToArray());
    }

    /// <summary>再推定の候補に入力元の同梱音声を加えず、適用先自身の不足を隠す誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_DoesNotUseSourceBundledResources()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Music", "Current");
        string candidateDir = Path.Combine("C:\\Music", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms", "sound.wav"]);
        lookupCache.AddDir(candidateDir, []);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.ReinstallCorrection);

        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
        Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
        Assert.AreEqual(100, result.SourceBaselinePrimaryHealth);
    }

    /// <summary>曖昧先の自動適用設定が有効でも再推定の複数候補は確定せず、通常推定の設定を誤用する変更を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_DoesNotAutoApplyMultipleViableCandidatesEvenWhenAmbiguousSettingEnabled()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            BmsLibraryInstallEstimationService service = CreateService();
            string sourceDir = Path.Combine("C:\\Music", "Current");
            string candidateADir = Path.Combine("C:\\Music", "CandidateA");
            string candidateBDir = Path.Combine("C:\\Music", "CandidateB");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file = file with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateADir, ["sound.wav"]);
            lookupCache.AddDir(candidateBDir, ["sound.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.ReinstallCorrection,
            metadataProfileResolver: _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "candidate")]));

            Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
            Assert.AreEqual(InstallEstimationLowConfidenceKind.AmbiguousCandidates, result.LowConfidenceKind);
            Assert.AreEqual("reinstall_multiple_strong_candidates", result.ConfidenceReason);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.AreEqual(candidateADir, result.DestinationDirectory);
            CollectionAssert.AreEqual(new[] { candidateADir, candidateBDir }, result.SuggestedDestinationDirectories.ToArray());
        });
    }

    /// <summary>再推定先のメタデータが違えば自動適用せず候補として示し、音声一致だけで別曲へ変更する誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_ReinstallCorrection_MetadataMismatchDoesNotAutoApply()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "Current");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
            File.WriteAllText(Path.Combine(candidateDir, "installed.bms"), "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
            File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateDir, ["installed.bms", "sound.wav"]);

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.ReinstallCorrection,
                delegate (string directoryPath)
                {
                    return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                        ? InstallEstimationMetadataNormalizer.BuildProfile([("Completely Different", "Another Artist", Path.Combine(candidateDir, "installed.bms"))])
                        : InstallEstimationMetadataProfile.Empty;
                });

            Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
            Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
            Assert.AreEqual("selected_candidate_metadata_mismatch", result.ConfidenceReason);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            CollectionAssert.AreEqual(new[] { candidateDir }, result.SuggestedDestinationDirectories.ToArray());
        });
    }

    /// <summary>Bmsonを所持索引へ登録するとMD5・SHA256の両フォルダ参照を持ち、種類による索引漏れを検出する。</summary>
    [TestMethod]
    public void CreateInstalledChartLookupIndexSnapshot_IncludesBmsonMd5AndSha256Directories()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string installDir = Path.Combine("C:\\Installed", "Bmson");
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine(installDir, "chart.bmson"),
            Folder = installDir,
            Title = "Title",
            RawTitle = "Title",
            Artist = "Artist",
            RawArtist = "Artist",
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Sha256 = new string('b', 64)
        };

        InstalledChartLookupIndexSnapshot result = CreateInstalledChartLookupIndexSnapshot([], [bmsonSong]);

        CollectionAssert.AreEqual(new[] { installDir }, result.Md5Directories[bmsonSong.Md5].ToArray());
        CollectionAssert.AreEqual(new[] { installDir }, result.Sha256Directories[bmsonSong.Sha256].ToArray());
        CollectionAssert.AreEqual(new[] { installDir }, result.KnownChartDirectories.ToList());
    }

    /// <summary>MD5のない譜面はSHA256があっても所持索引へ登録せず、主ハッシュ条件を迂回する登録を検出する。</summary>
    [TestMethod]
    public void CreateInstalledChartLookupIndexSnapshot_DoesNotRegisterMd5lessRows()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string installDir = Path.Combine("C:\\Installed", "ShaOnly");
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine(installDir, "chart.bmson"),
            Folder = installDir,
            Md5 = null,
            Sha256 = new string('b', 64)
        };

        InstalledChartLookupIndexSnapshot result = CreateInstalledChartLookupIndexSnapshot([], [bmsonSong]);

        Assert.AreEqual(0, result.DistinctPrimaryHashCount);
        Assert.IsFalse(result.Sha256Directories.ContainsKey(bmsonSong.Sha256));
        Assert.AreEqual(0, result.KnownChartDirectories.Count);
    }

    /// <summary>重複MD5の件数から除外件数を差し引き、残所有者があるハッシュの消失や全除外後の残存を検出する。</summary>
    [TestMethod]
    public void CreateInstalledChartLookupIndexSnapshot_CountsPrimaryHashesForExcludingLookup()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string duplicateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string otherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        ChartFile first = CreateFile(duplicateHash, "C:\\Installed\\A\\chart.bms");
        ChartFile second = CreateFile(duplicateHash, "C:\\Installed\\B\\chart.bms");
        ChartFile other = CreateFile(otherHash, "C:\\Installed\\C\\chart.bms");

        InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([first, second, other]);

        Assert.AreEqual(2, snapshot.DistinctPrimaryHashCount);
        Assert.AreEqual(2, snapshot.GetPrimaryHashCount(duplicateHash));
        Assert.IsTrue(snapshot.ContainsPrimaryHashAfterExcluding(duplicateHash, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash.ToUpperInvariant()] = 1
        }));
        Assert.IsFalse(snapshot.ContainsPrimaryHashAfterExcluding(duplicateHash, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash] = 2
        }));
        IPrimaryHashLookup excludingOne = snapshot.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash] = 1
        });

        Assert.IsTrue(excludingOne.ContainsPrimaryHash(duplicateHash));
        Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(duplicateHash));
        Assert.IsTrue(excludingOne.ContainsPrimaryHash(otherHash));
        IPrimaryHashLookup excludingAll = snapshot.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [duplicateHash] = 2
        });

        Assert.IsFalse(excludingAll.ContainsPrimaryHash(duplicateHash));
        Assert.AreEqual(0, excludingAll.GetPrimaryHashCount(duplicateHash));
        IPrimaryHashLookup excludingUnknown = snapshot.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["cccccccccccccccccccccccccccccccc"] = 1
        });

        Assert.AreEqual(2, excludingUnknown.DistinctPrimaryHashCount);
    }

    /// <summary>所持集合からBMS・Bmson双方のMD5・SHA256索引を作り、パスのない譜面を数える誤りと種類の欠落を検出する。</summary>
    [TestMethod]
    public void OwnedChartCollectionState_CreateInstalledChartLookupIndexBuildsBmsAndBmsonLookup()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string bmsDir = Path.Combine("C:\\Installed", "Bms");
        string bmsonDir = Path.Combine("C:\\Installed", "Bmson");
        ChartFile bmsFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(bmsDir, "chart.bms"));
        bmsFile = bmsFile with { Sha256 = new string('b', 64) };
        ChartFile pathlessBms = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee", string.Empty);
        pathlessBms = pathlessBms with { Sha256 = new string('f', 64) };
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine(bmsonDir, "chart.bmson"),
            Folder = bmsonDir,
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('d', 64)
        };
        ChartFile pathlessBmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = string.Empty,
            Md5 = "11111111111111111111111111111111",
            Sha256 = new string('2', 64)
        };

        InstalledChartLookupIndexSnapshot snapshot = OwnedChartCollectionState.FromCharts(ChartTestValues.Combine([bmsFile, pathlessBms], [bmsonSong, pathlessBmson]))
            .CreateInstalledChartLookupIndexState(out int bmsCount, out int bmsonCount)
            .CreateSnapshot();

        Assert.AreEqual(1, bmsCount);
        Assert.AreEqual(1, bmsonCount);
        CollectionAssert.AreEqual(new[] { bmsDir }, snapshot.Md5Directories[bmsFile.Md5].ToArray());
        CollectionAssert.AreEqual(new[] { bmsDir }, snapshot.Sha256Directories[bmsFile.Sha256].ToArray());
        CollectionAssert.AreEqual(new[] { bmsonDir }, snapshot.Md5Directories[bmsonSong.Md5].ToArray());
        CollectionAssert.AreEqual(new[] { bmsonDir }, snapshot.Sha256Directories[bmsonSong.Sha256].ToArray());
        Assert.IsTrue(snapshot.KnownChartDirectories.Contains(bmsDir));
        Assert.IsTrue(snapshot.KnownChartDirectories.Contains(bmsonDir));
        Assert.AreEqual(2, snapshot.DistinctPrimaryHashCount);
        Assert.IsFalse(snapshot.ContainsPrimaryHash(pathlessBms.Md5));
        Assert.IsFalse(snapshot.ContainsPrimaryHash(pathlessBmson.Md5));
    }

    /// <summary>同じハッシュの所有者を段階的に除いて参照数とフォルダ集合を更新し、過剰削除や取得済みスナップショットの書換えを検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_DecrementsPrimaryHashAndDirectoryCounts()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstDir = Path.Combine("C:\\Installed", "First");
        string secondDir = Path.Combine("C:\\Installed", "Second");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(Path.Combine(secondDir, "b.bms"), hash, null);
        state.AddChart(Path.Combine(firstDir, "a.bms"), hash, null);

        InstalledChartLookupIndexSnapshot initial = state.CreateSnapshot();

        Assert.AreEqual(2, state.DirectoryReferenceCount);
        CollectionAssert.AreEqual(new[] { firstDir, secondDir }, initial.Md5Directories[hash].ToArray());
        Assert.AreEqual(2, initial.GetPrimaryHashCount(hash));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(secondDir));
        Assert.IsTrue(initial.KnownChartDirectories.Contains(firstDir));
        Assert.IsTrue(initial.KnownChartDirectories.Contains(secondDir));
        Assert.AreEqual(2, initial.DirectoryReferenceCount);
        IPrimaryHashLookup excludingOne = initial.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [hash] = 1
        });

        state.RemoveChart(Path.Combine(firstDir, "a.bms"), hash, null);
        InstalledChartLookupIndexSnapshot partial = state.CreateSnapshot();

        Assert.IsTrue(partial.ContainsPrimaryHash(hash));
        Assert.AreEqual(1, partial.GetPrimaryHashCount(hash));
        Assert.AreEqual(1, state.DirectoryReferenceCount);
        CollectionAssert.AreEqual(new[] { secondDir }, partial.Md5Directories[hash].ToArray());
        Assert.IsFalse(partial.KnownChartDirectories.Contains(firstDir));
        Assert.IsTrue(partial.KnownChartDirectories.Contains(secondDir));
        Assert.AreEqual(0, partial.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(1, partial.GetUniquePrimaryHashCountByDirectory(secondDir));
        Assert.AreEqual(1, partial.DirectoryReferenceCount);
        Assert.IsTrue(excludingOne.ContainsPrimaryHash(hash));
        Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(hash));

        state.RemoveChart(Path.Combine(secondDir, "b.bms"), hash, null);
        InstalledChartLookupIndexSnapshot empty = state.CreateSnapshot();

        Assert.IsFalse(empty.ContainsPrimaryHash(hash));
        Assert.AreEqual(0, state.DirectoryReferenceCount);
        Assert.IsFalse(empty.Md5Directories.ContainsKey(hash));
        Assert.IsFalse(empty.KnownChartDirectories.Contains(firstDir));
        Assert.IsFalse(empty.KnownChartDirectories.Contains(secondDir));

        CollectionAssert.AreEqual(new[] { firstDir, secondDir }, initial.Md5Directories[hash].ToArray());
        Assert.AreEqual(2, initial.GetPrimaryHashCount(hash));
        Assert.IsTrue(initial.KnownChartDirectories.Contains(firstDir));
        Assert.IsTrue(initial.KnownChartDirectories.Contains(secondDir));
        Assert.AreEqual(2, initial.DirectoryReferenceCount);
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(secondDir));
        Assert.IsTrue(excludingOne.ContainsPrimaryHash(hash));
        Assert.AreEqual(1, excludingOne.GetPrimaryHashCount(hash));

        CollectionAssert.AreEqual(new[] { secondDir }, partial.Md5Directories[hash].ToArray());
        Assert.AreEqual(1, partial.GetPrimaryHashCount(hash));
        Assert.AreEqual(1, partial.DirectoryReferenceCount);
    }

    /// <summary>同一ハッシュを段階的に除いて現在件数を減らし、過去の件数スナップショットが追随して変わる誤りを検出する。</summary>
    [TestMethod]
    public void PrimaryHashLookupState_SharesImmutableCountRootAndKeepsPreviousSnapshots()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        const string sharedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string otherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var state = new PrimaryHashLookupState();
        state.AddPrimaryHash(sharedHash);
        state.AddPrimaryHash(sharedHash);
        state.AddPrimaryHash(otherHash);

        PrimaryHashLookupSnapshot initial = state.CreateSnapshot();
        PrimaryHashLookupSnapshot cached = state.CreateSnapshot();
        Assert.AreSame(initial, cached);
        Assert.AreEqual(2, initial.GetPrimaryHashCount(sharedHash));
        Assert.AreEqual(1, initial.GetPrimaryHashCount(otherHash));
        Assert.AreEqual(2, initial.PrimaryHashCounts[sharedHash]);

        state.RemovePrimaryHash(sharedHash);
        PrimaryHashLookupSnapshot oneOwner = state.CreateSnapshot();
        Assert.AreEqual(1, oneOwner.GetPrimaryHashCount(sharedHash));
        Assert.AreEqual(1, oneOwner.GetPrimaryHashCount(otherHash));
        Assert.AreEqual(2, initial.GetPrimaryHashCount(sharedHash));
        Assert.AreEqual(2, initial.PrimaryHashCounts[sharedHash]);

        state.RemovePrimaryHash(sharedHash);
        PrimaryHashLookupSnapshot empty = state.CreateSnapshot();
        Assert.AreEqual(0, empty.GetPrimaryHashCount(sharedHash));
        Assert.IsFalse(empty.ContainsPrimaryHash(sharedHash));
        Assert.AreEqual(1, empty.GetPrimaryHashCount(otherHash));
        Assert.AreEqual(1, oneOwner.GetPrimaryHashCount(sharedHash));
        Assert.AreEqual(2, initial.GetPrimaryHashCount(sharedHash));
    }

    /// <summary>大文字小文字の違う同一フォルダの所有者を除き、最後の所有者まで両ハッシュ参照を保つことと過去の集合の不変性を確認する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_KeepsDigestBucketsAndCountsConsistentAcrossLastOwnerRemoval()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string sharedMd5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string sharedSha256 = new string('b', 64);
        string otherMd5 = "cccccccccccccccccccccccccccccccc";
        string sharedDirectory = Path.Combine("C:\\Installed", "Shared");
        string otherDirectory = Path.Combine("C:\\Installed", "Other");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(Path.Combine(sharedDirectory, "bms-chart.bms"), sharedMd5, sharedSha256);
        state.AddChart(Path.Combine(sharedDirectory.ToUpperInvariant(), "bmson-chart.bmson"), sharedMd5, sharedSha256);
        state.AddChart(Path.Combine(otherDirectory, "other-chart.bms"), otherMd5, null);

        InstalledChartLookupIndexSnapshot initial = state.CreateSnapshot();

        CollectionAssert.AreEqual(new[] { sharedDirectory }, initial.Md5Directories[sharedMd5].ToArray());
        CollectionAssert.AreEqual(new[] { sharedDirectory }, initial.Sha256Directories[sharedSha256].ToArray());
        Assert.AreEqual(2, initial.GetPrimaryHashCount(sharedMd5));
        Assert.AreEqual(1, initial.GetPrimaryHashCount(otherMd5));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(sharedDirectory));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(otherDirectory));
        Assert.IsTrue(initial.KnownChartDirectories.Contains(sharedDirectory));
        Assert.IsTrue(initial.KnownChartDirectories.Contains(otherDirectory));
        Assert.AreEqual(3, initial.DirectoryReferenceCount);

        state.RemoveChart(Path.Combine(sharedDirectory, "bms-chart.bms"), sharedMd5, sharedSha256);
        InstalledChartLookupIndexSnapshot oneOwner = state.CreateSnapshot();

        CollectionAssert.AreEqual(new[] { sharedDirectory }, oneOwner.Md5Directories[sharedMd5].ToArray());
        CollectionAssert.AreEqual(new[] { sharedDirectory }, oneOwner.Sha256Directories[sharedSha256].ToArray());
        Assert.AreEqual(1, oneOwner.GetPrimaryHashCount(sharedMd5));
        Assert.AreEqual(3, oneOwner.DirectoryReferenceCount);
        Assert.IsTrue(oneOwner.KnownChartDirectories.Contains(sharedDirectory));
        Assert.AreEqual(1, oneOwner.GetUniquePrimaryHashCountByDirectory(sharedDirectory));

        state.RemoveChart(Path.Combine(sharedDirectory.ToUpperInvariant(), "bmson-chart.bmson"), sharedMd5, sharedSha256);
        InstalledChartLookupIndexSnapshot lastOwnerRemoved = state.CreateSnapshot();

        Assert.IsFalse(lastOwnerRemoved.Md5Directories.ContainsKey(sharedMd5));
        Assert.IsFalse(lastOwnerRemoved.Sha256Directories.ContainsKey(sharedSha256));
        Assert.AreEqual(0, lastOwnerRemoved.GetPrimaryHashCount(sharedMd5));
        Assert.AreEqual(1, lastOwnerRemoved.GetPrimaryHashCount(otherMd5));
        Assert.AreEqual(1, lastOwnerRemoved.DirectoryReferenceCount);
        Assert.IsFalse(lastOwnerRemoved.KnownChartDirectories.Contains(sharedDirectory));
        Assert.IsTrue(lastOwnerRemoved.KnownChartDirectories.Contains(otherDirectory));
        Assert.AreEqual(0, lastOwnerRemoved.GetUniquePrimaryHashCountByDirectory(sharedDirectory));
        Assert.AreEqual(1, lastOwnerRemoved.GetUniquePrimaryHashCountByDirectory(otherDirectory));

        CollectionAssert.AreEqual(new[] { sharedDirectory }, initial.Md5Directories[sharedMd5].ToArray());
        CollectionAssert.AreEqual(new[] { sharedDirectory }, initial.Sha256Directories[sharedSha256].ToArray());
        Assert.AreEqual(2, initial.GetPrimaryHashCount(sharedMd5));
        Assert.AreEqual(3, initial.DirectoryReferenceCount);
        Assert.IsTrue(initial.KnownChartDirectories.Contains(sharedDirectory));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(sharedDirectory));

        CollectionAssert.AreEqual(new[] { sharedDirectory }, oneOwner.Md5Directories[sharedMd5].ToArray());
        CollectionAssert.AreEqual(new[] { sharedDirectory }, oneOwner.Sha256Directories[sharedSha256].ToArray());
        Assert.AreEqual(1, oneOwner.GetPrimaryHashCount(sharedMd5));
        Assert.AreEqual(3, oneOwner.DirectoryReferenceCount);
    }

    /// <summary>背景譜面16・128件に一件の削除を加え、影響したハッシュだけを更新し、全索引走査や背景件数に比例する複製を検出する。</summary>
    [DataTestMethod]
    // 背景件数を増やしても一件の削除で全索引を走査しない。
    [DataRow(16)]
    [DataRow(128)]
    public void InstalledChartLookupIndexState_ObservesOnlyAffectedBucketWork(int backgroundChartCount)
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string rootDirectory = Path.Combine("C:\\Installed", "Background", backgroundChartCount.ToString());
        string targetHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string targetSha256 = new string('b', 64);
        string targetDirectory = Path.Combine(rootDirectory, "Target");
        string removedTargetPath = Path.Combine(targetDirectory, "removed.bms");
        string retainedTargetDirectory = Path.Combine(rootDirectory, "TargetRetained");
        string retainedTargetPath = Path.Combine(retainedTargetDirectory, "retained.bms");
        var state = new InstalledChartLookupIndexState();
        List<string> coldWork = [];
        state.StoreWorkObserver = operation => coldWork.Add(operation);

        for (int index = 0; index < backgroundChartCount; index++)
        {
            string backgroundHash = index.ToString("x8") + new string('0', 24);
            string backgroundSha256 = index.ToString("x8") + new string('1', 56);
            string backgroundPath = Path.Combine(rootDirectory, "Chart-" + index.ToString(), "background.bms");
            state.AddChart(backgroundPath, backgroundHash, backgroundSha256);
        }
        state.AddChart(removedTargetPath, targetHash, targetSha256);
        state.AddChart(retainedTargetPath, targetHash, targetSha256);
        InstalledChartLookupIndexSnapshot initial = state.CreateSnapshot();
        _ = initial.Md5Directories.ToArray();
        _ = initial.Sha256Directories.ToArray();
        _ = initial.PrimaryHashCounts.ToArray();
        _ = initial.UniquePrimaryHashCountsByDirectory.ToArray();
        _ = initial.KnownChartDirectories.ToArray();
        state.StoreWorkObserver = null;
        initial.StoreWorkObserver = null;
        Assert.IsTrue(coldWork.Count(operation => operation == "installed_directory_bucket_update") > 0);
        Assert.IsTrue(coldWork.Count(operation => operation == "installed_root_map_enumeration") > 0);
        Assert.IsTrue(coldWork.Count(operation => operation == "installed_root_map_key_visited") > 0);

        List<string> storeWork = [];
        state.StoreWorkObserver = operation => storeWork.Add(operation);
        state.RemoveChart(removedTargetPath, targetHash, targetSha256);
        InstalledChartLookupIndexSnapshot updated = state.CreateSnapshot();
        _ = updated.Md5Directories.Count;
        _ = updated.Sha256Directories.Count;
        _ = updated.GetDistinctDirectoriesByPrimaryHash(targetHash).Count;
        _ = updated.GetPrimaryHashCount(targetHash);
        _ = updated.GetUniquePrimaryHashCountByDirectory(retainedTargetDirectory);
        _ = updated.PrimaryHashCounts.Count;
        _ = updated.UniquePrimaryHashCountsByDirectory.Count;
        _ = updated.KnownChartDirectories.Count;
        _ = updated.DirectoryReferenceCount;
        state.StoreWorkObserver = null;
        updated.StoreWorkObserver = null;

        CollectionAssert.AreEqual(new[] { retainedTargetDirectory }, updated.Md5Directories[targetHash].ToArray());
        CollectionAssert.AreEqual(new[] { retainedTargetDirectory }, updated.Sha256Directories[targetSha256].ToArray());
        Assert.AreEqual(1, updated.GetPrimaryHashCount(targetHash));
        Assert.AreEqual(1, updated.GetUniquePrimaryHashCountByDirectory(retainedTargetDirectory));
        Assert.AreEqual(2 * backgroundChartCount + 2, updated.DirectoryReferenceCount);
        Assert.AreEqual(2, storeWork.Count(operation => operation == "installed_directory_bucket_update"));
        int affectedBucketEntryCopies = storeWork.Count(operation => operation == "installed_directory_bucket_entry_copied");
        Assert.IsTrue(affectedBucketEntryCopies <= 2);
        Assert.AreEqual(1, storeWork.Count(operation => operation == "installed_snapshot_root_capture"));
        Assert.AreEqual(1, storeWork.Count(operation => operation == "installed_directory_reference_count_read"));
        Assert.IsTrue(affectedBucketEntryCopies < backgroundChartCount);
        Assert.AreEqual(0, storeWork.Count(operation => operation == "installed_root_map_enumeration"));
        Assert.AreEqual(0, storeWork.Count(operation => operation == "installed_root_map_key_visited"));

        CollectionAssert.AreEqual(new[] { targetDirectory, retainedTargetDirectory }, initial.Md5Directories[targetHash].ToArray());
        CollectionAssert.AreEqual(new[] { targetDirectory, retainedTargetDirectory }, initial.Sha256Directories[targetSha256].ToArray());
        Assert.AreEqual(2, initial.GetPrimaryHashCount(targetHash));
        Assert.AreEqual(2 * backgroundChartCount + 4, initial.DirectoryReferenceCount);
    }

    /// <summary>同じMD5・SHA256とフォルダを共有する二件を段階的に除き、最後の所有者より前にフォルダ参照数を減らす誤りを検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_CountsDistinctDirectoryReferences()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string sha256 = new string('b', 64);
        string dir = Path.Combine("C:\\Installed", "SameDir");
        string firstPath = Path.Combine(dir, "a.bms");
        string secondPath = Path.Combine(dir, "b.bms");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(firstPath, md5, sha256);
        state.AddChart(secondPath, md5, sha256);

        Assert.AreEqual(2, state.DirectoryReferenceCount);

        state.RemoveChart(firstPath, md5, sha256);

        Assert.AreEqual(2, state.DirectoryReferenceCount);

        state.RemoveChart(secondPath, md5, sha256);

        Assert.AreEqual(0, state.DirectoryReferenceCount);
    }

    /// <summary>重複MD5を一つに数え、削除と別フォルダからの移動後に各フォルダの異なるMD5数を更新し、重複の二重計数を検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_CountsUniquePrimaryHashesByDirectory()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string duplicateHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string otherHash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string bmsonHash = "cccccccccccccccccccccccccccccccc";
        string firstDir = Path.Combine("C:\\Installed", "First");
        string secondDir = Path.Combine("C:\\Installed", "Second");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(Path.Combine(firstDir, "a.bms"), duplicateHash, null);
        state.AddChart(Path.Combine(firstDir, "a-copy.bms"), duplicateHash, null);
        state.AddChart(Path.Combine(firstDir, "b.bms"), otherHash, null);
        state.AddChart(Path.Combine(firstDir, "c.bmson"), bmsonHash, null);
        state.AddChart(Path.Combine(secondDir, "a.bms"), duplicateHash, null);

        InstalledChartLookupIndexSnapshot initial = state.CreateSnapshot();

        Assert.AreEqual(3, initial.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(1, initial.GetUniquePrimaryHashCountByDirectory(secondDir));

        state.RemoveChart(Path.Combine(firstDir, "b.bms"), otherHash, null);
        state.MoveChart(Path.Combine(secondDir, "a.bms"), Path.Combine(firstDir, "moved-a.bms"), duplicateHash, null);
        InstalledChartLookupIndexSnapshot updated = state.CreateSnapshot();

        Assert.AreEqual(2, updated.GetUniquePrimaryHashCountByDirectory(firstDir));
        Assert.AreEqual(0, updated.GetUniquePrimaryHashCountByDirectory(secondDir));
    }

    /// <summary>同じMD5の複数パスを取得し、一件削除後は残りだけを返すことで、ハッシュ単位の過剰削除を検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_TracksPathsByPrimaryHash()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstPath = Path.Combine("C:\\Installed", "First", "a.bms");
        string secondPath = Path.Combine("C:\\Installed", "Second", "b.bms");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(firstPath, hash, null);
        state.AddChart(secondPath, hash, null);

        CollectionAssert.AreEqual(new[] { firstPath, secondPath }, state.GetPathsByPrimaryHash(hash).ToArray());

        state.RemoveChart(firstPath, hash, null);

        CollectionAssert.AreEqual(new[] { secondPath }, state.GetPathsByPrimaryHash(hash).ToArray());
    }

    /// <summary>除外付きの検索入力を作った後に所持元を変更しても捕捉時の件数を保ち、計算途中に基準件数が変わる誤りを検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_CreateExcludingLookupFreezesBaselineCounts()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstPath = Path.Combine("C:\\Installed", "First", "a.bms");
        string secondPath = Path.Combine("C:\\Installed", "Second", "b.bms");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(firstPath, hash, null);
        state.AddChart(secondPath, hash, null);

        IPrimaryHashLookup lookup = state.CreateExcludingLookup(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [hash] = 1
        });
        state.RemoveChart(firstPath, hash, null);

        Assert.IsTrue(lookup.ContainsPrimaryHash(hash));
        Assert.AreEqual(1, lookup.GetPrimaryHashCount(hash));
    }

    /// <summary>同じMD5の別パスを一件ずつ除き、最後の削除まで主ハッシュを保ち、その後のパス・件数残存を検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_KeepsPrimaryHashUntilAllDistinctPathsAreRemoved()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string firstPath = Path.Combine("C:\\Installed", "First", "chart.bms");
        string secondPath = Path.Combine("C:\\Installed", "Second", "chart.bms");
        var state = new InstalledChartLookupIndexState();

        state.AddChart(firstPath, hash, null);
        state.AddChart(secondPath, hash, null);

        state.RemoveChart(firstPath, hash, null);

        CollectionAssert.AreEqual(new[] { secondPath }, state.GetPathsByPrimaryHash(hash).ToArray());
        Assert.AreEqual(1, state.GetPrimaryHashCount(hash));

        state.RemoveChart(secondPath, hash, null);

        Assert.AreEqual(0, state.GetPathsByPrimaryHash(hash).Count);
        Assert.AreEqual(0, state.GetPrimaryHashCount(hash));
    }

    /// <summary>譜面のフォルダ移動で旧参照を除き新パス・新フォルダへ更新し、ハッシュ件数の増減や古い所持先の残存を検出する。</summary>
    [TestMethod]
    public void InstalledChartLookupIndexState_MoveChartUpdatesDirectoryLookup()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string hash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string oldDir = Path.Combine("C:\\Installed", "Old");
        string newDir = Path.Combine("C:\\Installed", "New");
        var state = new InstalledChartLookupIndexState();
        state.AddChart(Path.Combine(oldDir, "chart.bms"), hash, null);

        state.MoveChart(Path.Combine(oldDir, "chart.bms"), Path.Combine(newDir, "chart.bms"), hash, null);
        InstalledChartLookupIndexSnapshot snapshot = state.CreateSnapshot();

        CollectionAssert.AreEqual(new[] { newDir }, snapshot.Md5Directories[hash].ToArray());
        Assert.IsFalse(snapshot.KnownChartDirectories.Contains(oldDir));
        Assert.IsTrue(snapshot.KnownChartDirectories.Contains(newDir));
        Assert.AreEqual(1, snapshot.GetPrimaryHashCount(hash));
        CollectionAssert.AreEqual(new[] { Path.Combine(newDir, "chart.bms") }, state.GetPathsByPrimaryHash(hash).ToArray());
    }

    /// <summary>実Bmsonの音声定義を共通推定へ接続し一致フォルダを返し、Bmsonだけが対象や診断から落ちる誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_BmsonUsesCommonHealthBasedSearch()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "source");
            string candidateDir = Path.Combine(tempRoot, "candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            string bmsonPath = Path.Combine(sourceDir, "chart.bmson");
            File.WriteAllText(bmsonPath, "{\"version\":\"1.0.0\",\"info\":{\"title\":\"Title\",\"artist\":\"Artist\"},\"sound_channels\":[{\"name\":\"keysound.wav\",\"notes\":[{\"x\":1,\"y\":0}]}],\"lines\":[{\"y\":0}]}");
            File.WriteAllText(Path.Combine(candidateDir, "keysound.wav"), "dummy");

            ChartFile pendingSong = ChartTestValues.ReadBmson(bmsonPath);
            var package = ChartPackage.FromChartEntries([PackageChartEntry.FromChart((pendingSong))]);
            package.path = sourceDir;
            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, []);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bmson"]);
            lookupCache.AddDir(candidateDir, ["keysound.wav"]);

            Assert.AreEqual(ChartFileKind.Bmson, snapshot.RepresentativeChart.Kind);
            Assert.AreEqual(pendingSong.Path, snapshot.RepresentativeChart.Path);

            CollectionAssert.Contains(snapshot.DefinedResources.AudioRelativePaths.ToArray(), "keysound");

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
            StringAssert.Contains(result.ResourceSummary ?? string.Empty, "chart=" + bmsonPath);
        });
    }

    /// <summary>77音声とpreviewを含む実Bmsonから78音声参照を評価し、少数だけ合う囮を選ぶ誤りとpreviewの欠落を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_BmsonSampleLikeOggSetSelectsMatchingDirectory()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "bmson");
            string candidateDir = Path.Combine(tempRoot, "piece_of_mine");
            string decoyDir = Path.Combine(tempRoot, "decoy");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            Directory.CreateDirectory(decoyDir);

            List<string> oggNames = [.. Enumerable.Range(1, 77).Select(index => string.Format("sound{0:00}.ogg", index))];
            var soundChannelsBuilder = new StringBuilder();
            for (int i = 0; i < oggNames.Count; i++)
            {
                if (i > 0)
                {
                    soundChannelsBuilder.Append(",");
                }
                soundChannelsBuilder.Append("{\"name\":\"").Append(oggNames[i]).Append("\",\"notes\":[{\"x\":1,\"y\":0}]}");
            }
            string bmsonPath = Path.Combine(sourceDir, "_circ_double_hard.bmson");
            File.WriteAllText(
                bmsonPath,
                "{"
                + "\"version\":\"1.0.0\","
                + "\"info\":{\"title\":\"Title\",\"artist\":\"Artist\",\"preview_music\":\"preview.ogg\",\"banner_image\":\"banner.png\"},"
                + "\"sound_channels\":[" + soundChannelsBuilder + "],"
                + "\"bga\":{\"bga_header\":[{\"id\":1,\"name\":\"cover.jpg\"}],\"bga_events\":[{\"id\":1,\"y\":0}]},"
                + "\"lines\":[{\"y\":0}]"
                + "}");

            foreach (string oggName in oggNames)
            {
                File.WriteAllText(Path.Combine(candidateDir, oggName), "candidate");
            }
            File.WriteAllText(Path.Combine(candidateDir, "preview.ogg"), "candidate");
            File.WriteAllText(Path.Combine(candidateDir, "banner.png"), "candidate");
            File.WriteAllText(Path.Combine(candidateDir, "cover.jpg"), "candidate");
            for (int i = 0; i < 10; i++)
            {
                File.WriteAllText(Path.Combine(decoyDir, oggNames[i]), "decoy");
            }

            ChartFile pending = ChartTestValues.ReadBmson(bmsonPath);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["_circ_double_hard.bmson"]);
            lookupCache.AddDir(candidateDir, oggNames.Concat(["preview.ogg", "banner.png", "cover.jpg"]));
            lookupCache.AddDir(decoyDir, oggNames.Take(10));

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [PackageChartEntry.FromChart((pending))],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            string debugSummary = (result.ResourceSummary ?? string.Empty) + " || " + (result.TopCandidateSummary ?? string.Empty) + " || " + (result.SelectedCandidateSummary ?? string.Empty);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath, debugSummary);
            Assert.AreEqual(1, result.CandidateDirectoryCount, debugSummary);
            StringAssert.Contains(result.ResourceSummary ?? string.Empty, "audioRefs=78");
            StringAssert.Contains(result.SelectedCandidateSummary ?? string.Empty, candidateDir, debugSummary);
        });
    }

    /// <summary>内容指標が同点の候補はパス順で表示を決めても自動適用せず、並び順を信頼性と取り違える誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_ReturnsLowConfidenceWhenOnlyDirectoryPathBreaksTie()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateADir = Path.Combine("C:\\Installed", "A");
        string candidateBDir = Path.Combine("C:\\Installed", "B");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file = file with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateADir, ["sound.wav"]);
        lookupCache.AddDir(candidateBDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal,
            metadataProfileResolver: _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "candidate")]));

        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.AreEqual(candidateADir, result.DestinationDirectory);
        Assert.AreEqual(candidateADir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(candidateBDir, result.SecondCandidate?.DirectoryPath);
        Assert.AreEqual("tie_on_strong_primary_metrics", result.ConfidenceReason);
    }

    /// <summary>メタデータも一致する同点候補は明示の自動適用設定に従い適用し、候補一覧と曖昧分類を失う誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_AmbiguousStrongMetadataWithSetting_AutoAppliesFirstCandidate()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "Pending", "Source");
                string candidateADir = Path.Combine(tempRoot, "Installed", "A");
                string candidateBDir = Path.Combine(tempRoot, "Installed", "B");
                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(candidateADir);
                Directory.CreateDirectory(candidateBDir);
                string chartPath = Path.Combine(sourceDir, "chart.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

                var lookupCache = new DirectoryResourceLookupCache();
                lookupCache.AddDir(sourceDir, ["chart.bms"]);
                lookupCache.AddDir(candidateADir, ["sound.wav"]);
                lookupCache.AddDir(candidateBDir, ["sound.wav"]);

                var metadataProfiles = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase)
                {
                    [candidateADir] = InstallEstimationMetadataNormalizer.BuildProfile([("Target Song", "Artist", Path.Combine(candidateADir, "a.bms"))]),
                    [candidateBDir] = InstallEstimationMetadataNormalizer.BuildProfile([("Target Song", "Artist", Path.Combine(candidateBDir, "b.bms"))])
                };

                InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                    [file],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    lookupCache,
                    asParallel: false,
                    ChartInstallationEstimateMode.Normal,
                    delegate (string directoryPath)
                    {
                        return metadataProfiles.TryGetValue(directoryPath, out InstallEstimationMetadataProfile? profile)
                            ? profile!
                            : InstallEstimationMetadataProfile.Empty;
                    });

                Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
                Assert.AreEqual(InstallEstimationLowConfidenceKind.AmbiguousCandidates, result.LowConfidenceKind);
                Assert.IsTrue(result.ShouldAutoApplyDestination);
                Assert.AreEqual(candidateADir, result.DestinationDirectory);
                Assert.AreEqual("tie_on_strong_primary_metrics", result.ConfidenceReason);
                CollectionAssert.AreEqual(new[] { candidateADir, candidateBDir }, result.SuggestedDestinationDirectories.ToArray());
            });
        });
    }

    /// <summary>先頭三件と同点の四件目も所持ハッシュ数で比較し、途中で同点集合を切って有力候補を見落とす誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_CandidateDirectoryHashCountTieBreakUsesFullTiedFrontier()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateADir = Path.Combine("C:\\Installed", "A");
        string candidateBDir = Path.Combine("C:\\Installed", "B");
        string candidateCDir = Path.Combine("C:\\Installed", "C");
        string candidateDDir = Path.Combine("C:\\Installed", "D");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
        file = file with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateADir, ["sound.wav"]);
        lookupCache.AddDir(candidateBDir, ["sound.wav"]);
        lookupCache.AddDir(candidateCDir, ["sound.wav"]);
        lookupCache.AddDir(candidateDDir, ["sound.wav"]);
        var directoryHashCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            [candidateADir] = 1,
            [candidateBDir] = 1,
            [candidateCDir] = 1,
            [candidateDDir] = 5
        };

        InstallEstimationResult result = service.EstimateInstallationDirectoryForCandidateDirectories(
            BuildLooseChartSnapshot([file], new HashSet<string>(StringComparer.OrdinalIgnoreCase), ChartInstallationEstimateMode.Normal)!,
            [candidateADir, candidateBDir, candidateCDir, candidateDDir],
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal,
            metadataProfileResolver: _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "candidate")]),
            candidateDirectoryUniquePrimaryHashCountResolver: path => directoryHashCounts.TryGetValue(path, out int count) ? count : 0);

        Assert.AreEqual(candidateDDir, result.DestinationDirectory);
        Assert.AreEqual(candidateDDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.IsTrue(result.DirectoryHashCountTieBreakApplied);
        Assert.AreEqual("distinct_strong_primary_metrics", result.ConfidenceReason);
        StringAssert.Contains(result.DirectoryHashCountTieBreakSummary ?? string.Empty, candidateDDir + ":5");
    }

    /// <summary>有効ペア欠如・非関連を通常では表示から除き、明示保持だけが候補と分類別警告を表示します。資源の候補解決と自動設定許可を混同しません。</summary>
    [DataTestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public void Estimate_NonRelatedCandidatesRequireExplicitKeepPolicy(bool unavailable, bool keep)
    {
        ChartFile chart = CreateFile("target", @"C:\Source\chart.bms", "sound.wav") with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        var entry = PackageChartEntry.FromChart(chart);
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([entry]);
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Candidate", ["sound.wav"]);
        InstallEstimationMetadataProfile profile = unavailable ? InstallEstimationMetadataProfile.Empty
            : InstallEstimationMetadataNormalizer.BuildProfile([("ZZZZ", "XXXX", "candidate")]);
        InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal, _ => profile,
            candidatePolicy: keep ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
        Assert.IsTrue(result.HasViableDestination);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.IsFalse(result.SelectedCandidateMetadataEvidenceStrong);
        Assert.AreEqual(keep ? 1 : 0, result.Candidates.Count);
        Assert.AreEqual(keep ? 1 : 0, result.SuggestedDestinationDirectories.Count);
        entry.ApplyInstallEstimationResult(result);
        Assert.AreEqual(keep && !unavailable ? "ZZZZ" : string.Empty, entry.Chart.InstallDestinationTitle);
        Assert.AreEqual(keep && !unavailable ? "XXXX" : string.Empty, entry.Chart.InstallDestinationArtist);
        Assert.AreEqual(keep, entry.Chart.Warnings.Any(warning => warning.Kind == (unavailable ? ChartWarningKind.InstallEstimationMetadataUnavailable : ChartWarningKind.InstallEstimationUnrelated)));
        if (!keep) { Assert.AreEqual("no_related_destination", result.ConfidenceReason); }
    }

    /// <summary>TITLEが完全一致でもARTISTが異なれば低確信度・自動適用不可となり、TITLE単独での昇格を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_ExactTitleWithDifferentArtistsIsMetadataMismatch()
    {
        BmsLibraryInstallEstimationService service = CreateService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\Pending\chart.bms", "sound.wav")
            with
        { RawTitle = "Song", Artist = "Target Artist", RawArtist = "Target Artist" };
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Installed", ["sound.wav"]);
        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service, [file], [], cache, false,
            ChartInstallationEstimateMode.Normal,
            _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Other Artist", "a")]));
        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.IsFalse(result.SelectedCandidateMetadataEvidenceStrong);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
        Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
    }

    /// <summary>副題・副役割を含む実BMSを解析して対象プロファイルへ渡し、完全な単一候補をStrongとして自動適用する。</summary>
    /// <remarks>Extreme Z4のMIX終端、From Megalopolis with LoveのSliced副役割、Oath:ReturnのSUBTITLEとfeat・複合副役割を別々の実入力で再現する。候補入力の形式選択はライブラリ接続テストへ分担する。</remarks>
    [DataTestMethod]
    [DataRow("#TITLE Extreme Z4 (Aggressive HC mix) [Enterprising]\n#ARTIST remixed. by gmtn.\n#SUBARTIST Graphic : koavenit (invert colors) obj:ugd", "Extreme Z4 (Aggressive HC mix", "remixed. by gmtn.")]
    [DataRow("#TITLE From Megalopolis with Love [Z_style]\n#ARTIST 牧野\n#SUBARTIST Sliced by И / obj: zakoPeko", "From Megalopolis with Love", "牧野")]
    [DataRow("#TITLE Oath:Return\n#SUBTITLE [Citrine]\n#ARTIST MiKaDo feat. はらもりよしな\n#SUBARTIST Guitar:裸(ﾗｵｳ)王 BGA:MiKaDo Illust&obj:JURUMA", "Oath:Return", "MiKaDo")]
    public void EstimateInstallationDirectory_ParsedBmsSubrolesRemainStrongAndAutoApply(string definitions, string candidateTitle, string candidateArtist)
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace((tempRoot, service) =>
        {
            string sourceDir = Path.Combine(tempRoot, "Pending");
            string candidateDir = Path.Combine(tempRoot, "Installed");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\n" + definitions + "\n#WAV01 sound.wav\n#00111:01\n", Encoding.GetEncoding("shift_jis"));
            ChartFile chart = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
            File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "audio");
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(candidateDir, ["sound.wav"]);
            InstallEstimationMetadataProfile candidateProfile = InstallEstimationMetadataNormalizer.BuildProfile([(candidateTitle, candidateArtist, Path.Combine(candidateDir, "base.bms"))]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal, _ => candidateProfile);

            Assert.AreEqual(1, result.Candidates.Count);
            Assert.AreEqual(candidateDir, result.DestinationDirectory);
            Assert.IsTrue(result.SelectedCandidateMetadataEvidenceStrong);
            Assert.IsTrue(result.ShouldAutoApplyDestination);
            Assert.AreEqual(InstallEstimationLowConfidenceKind.None, result.LowConfidenceKind);
            var entry = PackageChartEntry.FromChart(chart);
            entry.ApplyInstallEstimationResult(result);
            Assert.IsFalse(entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationMetadataMismatch));
        });
    }

    /// <summary>実サービスの単一候補で、Dice閾値・空作者同値・再切断禁止を強一致と自動適用の必要条件として確認する。</summary>
    [DataTestMethod]
    [DataRow("abcdefghijklmnopqrs", "Artist", "abcdefghijklmnopqrstuvwxyz0123456", "Artist", true)]
    [DataRow("abcdefghijklmnopqrs", "Artist", "abcdefghijklmnopqrstuvwxyz01234567", "Artist", false)]
    [DataRow("Song", "", "Song", "", true)]
    [DataRow("Song", "", "Song", "Artist", false)]
    [DataRow("Song", "Artist", "Song", "", false)]
    [DataRow("", "Artist", "Song", "Artist", false)]
    [DataRow("Song", "Artist", "", "Artist", false)]
    [DataRow("Title (A) 続き [差分]", "Artist", "Title", "Artist", false)]
    [DataRow("Ark (remix/5keys)", "", "Ark (remix/10lines)", "", true)]
    // 両側の異なる役割接尾を共通プロファイルへ通し、実サービスのStrongと自動適用へ接続する。
    [DataRow("Ice Festival", "Nota BGI:雪椿", "Ice Festival[札幌雪祭り]", "Nota/obj:みあ BGI:雪椿", true)]
    public void EstimateInstallationDirectory_StrongRequiresPairArtistAndTitleThreshold(string title, string artist, string candidateTitle, string candidateArtist, bool strong)
    {
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\Pending\chart.bms", "sound.wav")
            with
        { RawTitle = title, Artist = artist, RawArtist = artist };
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Installed", ["sound.wav"]);
        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(CreateService(), [file], [], cache, false,
            ChartInstallationEstimateMode.Normal,
            _ => InstallEstimationMetadataNormalizer.BuildProfile([(candidateTitle, candidateArtist, "a")]));
        Assert.AreEqual(strong, result.SelectedCandidateMetadataEvidenceStrong);
        Assert.AreEqual(strong, result.ShouldAutoApplyDestination);
        bool visible = title.Length > 0 && candidateTitle.Length > 0;
        Assert.AreEqual(strong || !visible ? InstallEstimationConfidence.High : InstallEstimationConfidence.Low, result.Confidence);
        Assert.AreEqual(strong || !visible ? InstallEstimationLowConfidenceKind.None : InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
    }

    /// <summary>同リソースではStrong真偽だけでメタデータ優劣を決め、完全一致・類似度・支持数やハッシュ数でStrong偽を昇格しない。</summary>
    /// <remarks>bothは先頭Aが0.88一致、後続BがTITLE完全一致・支持3件でも双方Strongとして同順位を保つ。neitherは双方不成立、mixedは少数ハッシュのStrong候補が優先する。曖昧設定とハッシュ補助も同じ結果境界で観測する。</remarks>
    [DataTestMethod]
    [DataRow("both", false, false)]
    [DataRow("both", true, false)]
    [DataRow("both", false, true)]
    [DataRow("neither", false, false)]
    [DataRow("neither", true, true)]
    [DataRow("mixed", false, true)]
    public void EstimateInstallationDirectory_TiedCandidatesUseOnlyStrongThenHashCount(string kind, bool autoApply, bool hashes)
    {
        WithAutoApplyAmbiguousInstallDestination(autoApply, () =>
        {
            const string candidateA = @"C:\Installed\A";
            const string candidateB = @"C:\Installed\B";
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\Pending\chart.bms", "sound.wav")
                with
            { RawTitle = "abcdefghijklmnopqrstuvw", Artist = "Artist", RawArtist = "Artist" };
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(candidateA, ["sound.wav"]);
            cache.AddDir(candidateB, ["sound.wav"]);
            InstallEstimationMetadataProfile Profile(string path) => InstallEstimationMetadataNormalizer.BuildProfile(
                path == candidateA
                    ? [("abcdefghijklmnopqrstuvwxyz012", kind == "neither" ? "Other" : "Artist", "a")]
                    : Enumerable.Repeat(("abcdefghijklmnopqrstuvw", kind == "both" ? "Artist" : "Other", "b"), 3));
            InstallEstimationResult result = CreateService().EstimateInstallationDirectoryForCandidateDirectories(
                BuildLooseChartSnapshot([file], [], ChartInstallationEstimateMode.Normal)!, [candidateA, candidateB], cache,
                false, ChartInstallationEstimateMode.Normal, Profile, hashes ? path => path == candidateB ? 5 : 1 : null);
            bool strong = kind != "neither";
            bool distinct = kind == "mixed" || hashes && strong;
            Assert.AreEqual(hashes && kind != "mixed" ? candidateB : candidateA, result.SelectedCandidate?.DirectoryPath);
            Assert.AreEqual(strong, result.SelectedCandidateMetadataEvidenceStrong);
            Assert.AreEqual(distinct || strong && autoApply, result.ShouldAutoApplyDestination);
            Assert.AreEqual(distinct ? InstallEstimationConfidence.High : InstallEstimationConfidence.Low, result.Confidence);
            Assert.AreEqual(!strong ? InstallEstimationLowConfidenceKind.MetadataMismatch : distinct ? InstallEstimationLowConfidenceKind.None : InstallEstimationLowConfidenceKind.AmbiguousCandidates, result.LowConfidenceKind);
            Assert.AreEqual(kind == "both" && hashes, result.DirectoryHashCountTieBreakApplied);
        });
    }

    /// <summary>通常はStrongをRelatedより優先し、明示保持では資源上位のRelatedを先頭に残して自動適用を止める。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EstimateInstallationDirectory_StrongOverridesResourceRankingOnlyInNormalPolicy(bool keep)
    {
        const string candidateA = @"C:\Installed\A";
        const string candidateB = @"C:\Installed\B";
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", @"C:\Pending\chart.bms", "sound.wav")
            with
        { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(candidateA, ["sound.wav"]);
        cache.AddDir(candidateB, ["sound.wav", "extra.wav"]);
        InstallEstimationResult result = CreateService().EstimateInstallationDirectoryForCandidateDirectories(
            BuildLooseChartSnapshot([file], [], ChartInstallationEstimateMode.Normal)!, [candidateA, candidateB], cache,
            false, ChartInstallationEstimateMode.Normal,
            path => InstallEstimationMetadataNormalizer.BuildProfile([("Song", path == candidateA ? "Other" : "Artist", "a")]),
            candidatePolicy: keep ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
        Assert.AreEqual(keep ? candidateA : candidateB, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(keep ? InstallEstimationConfidence.Low : InstallEstimationConfidence.High, result.Confidence);
        Assert.AreEqual(keep ? InstallEstimationLowConfidenceKind.MetadataMismatch : InstallEstimationLowConfidenceKind.None, result.LowConfidenceKind);
        Assert.AreEqual(!keep, result.ShouldAutoApplyDestination);
    }

    /// <summary>音声が同点でも曲名・作者の一致先を高信頼へ選び、無関係なメタデータの候補を適用する誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_MetadataTieBreakPromotesMatchingCandidateToHigh()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "Pending", "Source");
            string candidateADir = Path.Combine(tempRoot, "Installed", "A");
            string candidateBDir = Path.Combine(tempRoot, "Installed", "B");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateADir);
            Directory.CreateDirectory(candidateBDir);
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song (Another)\r\n#ARTIST Artist / Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");

            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms"]);
            lookupCache.AddDir(candidateADir, ["sound.wav"]);
            lookupCache.AddDir(candidateBDir, ["sound.wav"]);

            var metadataProfiles = new Dictionary<string, InstallEstimationMetadataProfile>(StringComparer.OrdinalIgnoreCase)
            {
                [candidateADir] = InstallEstimationMetadataNormalizer.BuildProfile([("Another Song", "Someone", Path.Combine(candidateADir, "a.bms"))]),
                [candidateBDir] = InstallEstimationMetadataNormalizer.BuildProfile([("Target Song", "Artist", Path.Combine(candidateBDir, "b.bms"))])
            };

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal,
                delegate (string directoryPath)
                {
                    return metadataProfiles.TryGetValue(directoryPath, out InstallEstimationMetadataProfile? profile)
                        ? profile!
                        : InstallEstimationMetadataProfile.Empty;
                });

            Assert.AreEqual(candidateBDir, result.DestinationDirectory);
            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.IsTrue(result.ShouldAutoApplyDestination);
            Assert.AreEqual("distinct_strong_primary_metrics", result.ConfidenceReason);
        });
    }

    /// <summary>健全な入力元が外部候補と同点でも外部だけを確定し、元フォルダを比較集合へ残す誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_WhenSourceDirectoryWouldTieExternalCandidate_SourceIsExcluded()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "A_Source");
            string candidateDir = Path.Combine(tempRoot, "Z_Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "sound.wav"), "src");
            File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav");
            file = file with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" };
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 1)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "sound.wav"]);
            lookupCache.AddDir(candidateDir, ["sound.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal,
            metadataProfileResolver: _ => InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "candidate")]));

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.IsTrue(result.ShouldAutoApplyDestination);
            Assert.AreEqual(candidateDir, result.DestinationDirectory);
            Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
            Assert.IsNull(result.SecondCandidate);
            Assert.AreEqual("distinct_strong_primary_metrics", result.ConfidenceReason);
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
            Assert.AreEqual(1, result.Candidates.Count);
        });
    }

    /// <summary>音声一致先が一件でも曲名・作者が違えば適用せず候補表示に留め、単一候補を無条件で高信頼にする誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_SingleCandidateWithMetadataMismatch_ReturnsLowWithoutDestination()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithAutoApplyAmbiguousInstallDestination(true, delegate
        {
            WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
            {
                string sourceDir = Path.Combine(tempRoot, "Pending", "Source");
                string candidateDir = Path.Combine(tempRoot, "Installed", "A");
                Directory.CreateDirectory(sourceDir);
                Directory.CreateDirectory(candidateDir);
                string chartPath = Path.Combine(sourceDir, "chart.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE Target Song\r\n#ARTIST Base Artist obj: Diff\r\n#WAVAA sound.wav\r\n#00111:AA\r\n");
                File.WriteAllText(Path.Combine(candidateDir, "installed.bms"), "#PLAYER 1\r\n#TITLE Completely Different\r\n#ARTIST Another Artist\r\n");
                File.WriteAllText(Path.Combine(candidateDir, "sound.wav"), "dst");

                ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

                var lookupCache = new DirectoryResourceLookupCache();
                lookupCache.AddDir(sourceDir, ["chart.bms"]);
                lookupCache.AddDir(candidateDir, ["installed.bms", "sound.wav"]);

                InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                    [file],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    lookupCache,
                    asParallel: false,
                    ChartInstallationEstimateMode.Normal,
                    delegate (string directoryPath)
                    {
                        return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                            ? InstallEstimationMetadataNormalizer.BuildProfile([("Completely Different", "Another Artist", Path.Combine(candidateDir, "installed.bms"))])
                            : InstallEstimationMetadataProfile.Empty;
                    });

                Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
                Assert.AreEqual(InstallEstimationLowConfidenceKind.MetadataMismatch, result.LowConfidenceKind);
                Assert.AreEqual("selected_candidate_metadata_mismatch", result.ConfidenceReason);
                Assert.IsFalse(result.ShouldAutoApplyDestination);
                Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
                CollectionAssert.AreEqual(new[] { candidateDir }, result.SuggestedDestinationDirectories.ToArray());
            });
        });
    }

    /// <summary>入力元が健全でも外部一致率が不足なら適用先なしを返し、入力元の健全性で外部候補を通す誤りを検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_WhenNoExternalViableCandidate_ReturnsHighWithoutDestinationEvenIfSourceIsHealthy()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "A_Source");
            string chartPath = Path.Combine(sourceDir, "chart.bms");
            string candidateDir = Path.Combine(tempRoot, "Z_Candidate");
            Directory.CreateDirectory(sourceDir);
            Directory.CreateDirectory(candidateDir);
            File.WriteAllText(chartPath, "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceDir, "00.wav"), "src");
            File.WriteAllText(Path.Combine(sourceDir, "01.wav"), "src");
            File.WriteAllText(Path.Combine(candidateDir, "00.wav"), "dst");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, "00.wav", "01.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 2)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = chartPath;

            package.delete_parent = false;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", Path.Combine("sound", "00.wav")]);
            lookupCache.AddDir(candidateDir, ["00.wav"]);

            InstallEstimationResult result = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
            Assert.AreEqual("no_viable_destination_below_threshold", result.ConfidenceReason);
            Assert.IsFalse(result.HasViableDestination);
            Assert.IsFalse(result.ShouldAutoApplyDestination);
            Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
            Assert.IsNull(result.SelectedCandidate);
            Assert.AreEqual(0, result.CandidateDirectoryCount);
            Assert.AreEqual(0, result.SuggestedDestinationDirectories.Count);
        });
    }

    /// <summary>表示上の丸め値が同じ二候補を丸め前の比率で選び、パス順で精度の劣る候補を優先する誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_WhenRoundedRatiosTie_UsesRawRatiosBeforeDirectoryPath()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "Source");
            string candidateDir = Path.Combine(tempRoot, "A_Candidate");
            string otherCandidateDir = Path.Combine(tempRoot, "Z_Candidate");

            string chartPath = Path.Combine(sourceDir, "chart.bms");

            List<string> wavReferences = [];
            List<string> sourceFiles = ["chart.bms"];
            List<string> candidateFiles = [];
            for (int i = 0; i < 399; i++)
            {
                string fileName = i.ToString("000") + ".wav";
                wavReferences.Add(fileName);
                candidateFiles.Add(fileName);

            }
            candidateFiles.Add("candidate-extra-a.wav");
            candidateFiles.Add("candidate-extra-b.wav");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", chartPath, [.. wavReferences]);
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: wavReferences.Count, wavExisting: wavReferences.Count)));

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, sourceFiles);
            lookupCache.AddDir(candidateDir, candidateFiles);
            lookupCache.AddDir(otherCandidateDir, Enumerable.Range(0, 399).Select(i => i.ToString("000") + ".wav").Concat(["candidate-extra.wav"]));

            InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
                [file],
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal);

            Assert.AreEqual(otherCandidateDir, result.SelectedCandidate?.DirectoryPath);

        });
    }

    /// <summary>役割接尾をまとめた実在最頻ペアを小文字化前の表記で候補表示へ渡し、少数派の採用や原ARTISTの流出を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_PopulatesRepresentativeMetadataForTopCandidates()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Chosen");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound.wav")
            with
        { RawTitle = "Representative Title", Artist = "Representative Artist", RawArtist = "Representative Artist" };
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(candidateDir, ["sound.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal,
            delegate (string directoryPath)
            {
                return string.Equals(directoryPath, candidateDir, StringComparison.OrdinalIgnoreCase)
                    ? InstallEstimationMetadataNormalizer.BuildProfile([
                        ("Minority", "Other", Path.Combine(candidateDir, "a.bms")),
                        ("Representative Title [ANOTHER]", "Representative Artist feat. Guest (Movie: Visual)", Path.Combine(candidateDir, "b.bms")),
                        ("Representative Title", "Representative Artist", Path.Combine(candidateDir, "c.bms"))])
                    : InstallEstimationMetadataProfile.Empty;
            });

        Assert.AreEqual(InstallEstimationConfidence.High, result.Confidence);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.AreEqual("Representative Title", result.SelectedCandidate?.DisplayTitle);
        Assert.AreEqual("Representative Artist", result.SelectedCandidate?.DisplayArtist);
    }

    /// <summary>階層付き音声参照では同名だけの平坦候補を粗い絞込みで除き、相対パスを無視した誤一致を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAudioBroadFilter_DropsBasenameOnlyCandidate_WhenLookupCachePresent()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(flatCandidateDir, ["bgm1.wav"]);
        lookupCache.AddDir(nestedCandidateDir, ["sound\\bgm1.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.TargetPathAwareHashCount);
        Assert.AreEqual(1, result.TargetPathAwareAudioHashCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(nestedCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    /// <summary>相対音声参照があっても索引がなければ候補と導入先を選ばず、索引不足の推定不能理由を返す。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAudioBroadFilter_RequiresLookupCache()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual("resource_index_unavailable", result.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", result.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", result.CoarseFilterMode);
        Assert.AreEqual(0, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.IsNull(result.SelectedCandidate);
        Assert.IsNull(result.DestinationDirectory);
    }

    /// <summary>階層付き画像参照を相対パスで絞り込み、別フォルダの同名画像を候補へ混ぜる誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareVisualBroadFilter_UsesImageRelativeHashes()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"));
        file = file with { Resources = TestChartResources.ReplaceVisual(file.Resources, new HashSet<string>(["clock\\00_001_00.bmp"], StringComparer.OrdinalIgnoreCase)) };
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateVisualMaintenanceInfo(file, bgaDefined: 1, bgaExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(flatCandidateDir, ["00_001_00.bmp"]);
        lookupCache.AddDir(nestedCandidateDir, ["clock\\00_001_00.bmp"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.TargetPathAwareVisualHashCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(nestedCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    /// <summary>任意画像の階層付き参照も絞込みへ含め、stagefileの参照を無視した候補選択を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareOptionalImageBroadFilter_UsesImageRelativeHashes()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Flat");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "Nested");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"));
        file = file with { Stagefile = "image\\logo.bmp" };
        file = file with { Resources = TestChartResources.Create(stagefile: "image\\logo.bmp") };
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateVisualMaintenanceInfo(file, stagefileDefined: true, stagefileExisting: false)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(sourceDir, ["chart.bms"]);
        lookupCache.AddDir(flatCandidateDir, ["logo.bmp"]);
        lookupCache.AddDir(nestedCandidateDir, ["image\\logo.bmp"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.TargetPathAwareOptionalImageHashCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(nestedCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    /// <summary>平坦参照と階層参照を含む入力では階層側も満たす候補を選び、basenameだけで受付を通す誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAdmissionGate_DropsCandidateThatOnlyMatchesBasenameRefs()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string basenameOnlyCandidateDir = Path.Combine("C:\\Installed", "BaseOnly");
        string pathAwareCandidateDir = Path.Combine("C:\\Installed", "PathAware");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\bgm1.wav", "bgm2.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(basenameOnlyCandidateDir, ["bgm1.wav", "bgm2.wav"]);
        lookupCache.AddDir(pathAwareCandidateDir, ["sound\\bgm1.wav", "bgm2.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(2, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(pathAwareCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    /// <summary>子フォルダを含む相対音声参照では親の集約索引を選び、子を根に読み替えた候補への誤適用を検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_NestedRootStylePathAwareAudio_SelectsParentAggregateCandidate()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint parentZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\01");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "Child\\sound\\00.wav", "Child\\sound\\01.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            [parentZeroRelativeHash, parentOneRelativeHash],
            [],
            []);
        lookupCache.AddDir(
            childDir,
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            [],
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            []);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(parentDir, result.SelectedCandidate?.DirectoryPath);
    }

    /// <summary>平坦な音声参照は親子のどちらでも階層内の同名音声に合わず、階層補正やビュー構築で偽候補を作る誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_FlatAudioReferencesRejectNestedParentAndChildCandidates()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint parentZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\01");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            [parentZeroRelativeHash, parentOneRelativeHash],
            [],
            []);
        lookupCache.AddDir(
            childDir,
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            [],
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            []);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(0, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, result.HierarchyCandidateDirectoryCount);
        Assert.AreEqual(0, result.AncestorShadowSuppressedCount);

        Assert.IsNull(result.SelectedCandidate);
        Assert.IsTrue(string.IsNullOrWhiteSpace(result.DestinationDirectory));
    }

    /// <summary>平坦な音声参照は候補直下だけに一致し、子フォルダの同名音声を一致に数える誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_FlatAudioRef_MatchesOnlyFlatRelativePath()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string nestedCandidateDir = Path.Combine("C:\\Installed", "A_Nested");
        string flatCandidateDir = Path.Combine("C:\\Installed", "Z_Flat");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(nestedCandidateDir, ["sound\\bgm1.wav"]);
        lookupCache.AddDir(flatCandidateDir, ["bgm1.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(1, result.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, result.HierarchyCandidateDirectoryCount);
        Assert.AreEqual(0, result.AncestorShadowSuppressedCount);

        Assert.AreEqual(1, result.SelectedCandidate?.AudioMatched);
        Assert.AreEqual(1, result.SelectedCandidate?.AudioExactMatched);
        Assert.AreEqual(flatCandidateDir, result.SelectedCandidate?.DirectoryPath);
    }

    /// <summary>平坦参照と階層参照を混在させても二件を一度ずつ数え、経路別の二重計数による一致率の誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_MixedBasenameAndPathAwareAudioRefs_CountEachReferenceOnce()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav", "sound\\bgm2.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav", "sound\\bgm2.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(2, result.TargetResourceCount);
        Assert.AreEqual(1, result.CandidateDirectoryCountAfterAudioGate);
        Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(2, result.SelectedCandidate?.AudioMatched);
    }

    /// <summary>同じbasenameが直下と子にある候補では別資源二件と数え、精度・Jaccardの分母を縮める誤りを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_CandidateCount_TreatsSameBasenameDifferentRelativePathsAsDistinct()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav", "sound\\bgm1.wav"]);

        InstallEstimationResult result = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(candidateDir, result.SelectedCandidate?.DirectoryPath);
        Assert.AreEqual(1, result.SelectedCandidate?.AudioMatched);
        Assert.AreEqual(50, result.SelectedCandidate?.AudioJaccard);
        Assert.AreEqual(2, result.SelectedCandidate?.CandidateResourceCount);

    }

    /// <summary>階層付き音声の不足分は通常推定で同梱分を補い合流推定では補わず、相対参照でのモード差の欠落を検出する。</summary>
    /// <remarks>実ファイルの発見・解析または適用先の情報を入力へ取り込む境界を確認するため、対象フォルダを用いる。</remarks>
    [TestMethod]
    public void EstimateInstallationDirectory_PathAwareAudioGate_UsesRelativePathAwareMatchesForNormalAndMerge()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        WithWorkspace(delegate (string tempRoot, BmsLibraryInstallEstimationService service)
        {
            string sourceDir = Path.Combine(tempRoot, "SourcePackage");
            string sourceSoundDir = Path.Combine(sourceDir, "sound");
            string candidateDir = Path.Combine(tempRoot, "Candidate");
            string candidateSoundDir = Path.Combine(candidateDir, "sound");
            Directory.CreateDirectory(sourceSoundDir);
            Directory.CreateDirectory(candidateSoundDir);
            File.WriteAllText(Path.Combine(sourceDir, "chart.bms"), "#PLAYER 1");
            File.WriteAllText(Path.Combine(sourceSoundDir, "02.wav"), "bundled");
            File.WriteAllText(Path.Combine(candidateSoundDir, "00.wav"), "dst");
            File.WriteAllText(Path.Combine(candidateSoundDir, "01.wav"), "dst");

            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "sound\\00.wav", "sound\\01.wav", "sound\\02.wav");
            file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 3, wavExisting: 1)));

            ChartPackage package = ChartPackageTestExtensions.CreatePackage([file]);

            package.path = sourceDir;

            package.delete_parent = true;
            PackageInstallEstimationSnapshot snapshot = BuildPackageSnapshot(package, [file]);

            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(sourceDir, ["chart.bms", "sound\\02.wav"]);
            lookupCache.AddDir(candidateDir, ["sound\\00.wav", "sound\\01.wav"]);

            InstallEstimationResult normalResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.Normal, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            InstallEstimationResult mergeResult = service.EstimateInstallationDirectory(
                snapshot,
                lookupCache,
                asParallel: false,
                ChartInstallationEstimateMode.ReinstallCorrection, candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);

            Assert.AreEqual(1, normalResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(candidateDir, normalResult.SelectedCandidate?.DirectoryPath);
            Assert.AreEqual(3, normalResult.SelectedCandidate?.AudioMatched);

            Assert.AreEqual(0, mergeResult.CandidateDirectoryCountAfterAudioGate);
            Assert.AreEqual(0, mergeResult.CandidateDirectoryCount);
            Assert.AreEqual("no_viable_destination_below_threshold", mergeResult.ConfidenceReason);
        });
    }

    /// <summary>平坦・階層音声を含む入力は索引があれば推定でき、索引なしでは不能とし、索引を省いた同等扱いを検出する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_WithoutResourceIndex_ReturnsUnavailableForRelativePathSemantics()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav", "sound\\bgm2.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav", "sound\\bgm2.wav"]);

        InstallEstimationResult lookupResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(candidateDir, lookupResult.SelectedCandidate?.DirectoryPath);
        Assert.IsNull(unavailableResult.DestinationDirectory);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CoarseFilterMode);
    }

    /// <summary>平坦な音声でも索引なしでは候補ビューを構築せず索引不足の不能を返し、索引ありの候補ビュー1件と導入先選択を区別する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_WithoutResourceIndex_DoesNotUseChartRelativeFallback()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string candidateDir = Path.Combine("C:\\Installed", "Candidate");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "bgm1.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 1, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(candidateDir, ["bgm1.wav"]);

        InstallEstimationResult lookupResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.AreEqual(candidateDir, lookupResult.SelectedCandidate?.DirectoryPath);
        Assert.IsNull(unavailableResult.DestinationDirectory);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CoarseFilterMode);


    }

    /// <summary>親子索引があっても相対音声が一致しなければ候補を作らず、索引なしでも階層補正で候補を捏造しないことを確認する。</summary>
    [TestMethod]
    public void EstimateInstallationDirectory_WithoutResourceIndex_SkipsAncestorShadowDiagnostics()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        BmsLibraryInstallEstimationService service = CreateService();
        string sourceDir = Path.Combine("C:\\Pending", "Source");
        string parentDir = Path.Combine("C:\\Installed", "Parent");
        string childDir = Path.Combine(parentDir, "Child");
        uint parentZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\00");
        uint parentOneRelativeHash = ChartResourceKeyHash.GetLookupHash("Child\\sound\\01");
        uint childZeroRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\00");
        uint childOneRelativeHash = ChartResourceKeyHash.GetLookupHash("sound\\01");
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(sourceDir, "chart.bms"), "00.wav", "01.wav");
        file = ChartFileProjection.WithMaintenance(file, MaintenanceStorageMapping.ToCommon(CreateMaintenanceInfo(file, wavDefined: 2, wavExisting: 0)));

        var lookupCache = new DirectoryResourceLookupCache();
        lookupCache.AddDir(
            parentDir,
            [parentZeroRelativeHash, parentOneRelativeHash],
            [],
            []);
        lookupCache.AddDir(
            childDir,
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            [],
            [childZeroRelativeHash, childOneRelativeHash],
            [],
            []);

        InstallEstimationResult lookupResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            lookupCache,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        InstallEstimationResult unavailableResult = EstimateLooseChartInstallationDirectory(service,
            [file],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            directoryLookupCache: null,
            asParallel: false,
            ChartInstallationEstimateMode.Normal);

        Assert.IsNull(unavailableResult.DestinationDirectory);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.ConfidenceReason);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CandidateMode);
        Assert.AreEqual("resource_index_unavailable", unavailableResult.CoarseFilterMode);
        Assert.AreEqual(0, lookupResult.CandidateDirectoryCountAfterBroadFilter);
        Assert.AreEqual(0, unavailableResult.CandidateDirectoryCountAfterBroadFilter);


    }

    /// <summary>MD5が違いSHA256だけ一致する所持譜面からは所持先を返さず、主ハッシュ条件の迂回を検出する。</summary>
    [TestMethod]
    public void GetDistinctInstalledDirectoriesForChart_ChartWithMd5DoesNotFallBackToSha256()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string installDir = Path.Combine("C:\\Installed", "PrimaryOnly");
        ChartFile installedFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Path.Combine(installDir, "chart.bms"));
        installedFile = installedFile with { Sha256 = new string('b', 64) };
        ChartFile pendingFile = CreateFile("cccccccccccccccccccccccccccccccc", "C:\\Pending\\chart.bms");
        pendingFile = pendingFile with { Sha256 = new string('b', 64) };
        ChartFile pendingChart = (pendingFile);

        InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([installedFile]);
        List<string> directories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(snapshot, pendingChart);

        Assert.AreEqual(0, directories.Count);
    }

    /// <summary>MD5のない保留Bmsonは一致SHA256があっても所持先なしとし、種類による主ハッシュ制約の迂回を検出する。</summary>
    [TestMethod]
    public void GetDistinctInstalledDirectoriesForChart_Sha256OnlyChartDoesNotUseSha256()
    {
        using IDisposable cultureScope = TestResourceInitializer.UseJapaneseCulture();
        string installDir = Path.Combine("C:\\Installed", "ShaOnly");
        ChartFile bmsonSong = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = Path.Combine(installDir, "chart.bmson"),
            Folder = installDir,
            Md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            Sha256 = new string('c', 64)
        };
        ChartFile pendingChart = (ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = "C:\\Pending\\chart.bmson",
            Md5 = null,
            Sha256 = new string('c', 64)
        });

        InstalledChartLookupIndexSnapshot snapshot = CreateInstalledChartLookupIndexSnapshot([], [bmsonSong]);
        List<string> directories = BmsLibraryInstallEstimationService.GetDistinctInstalledDirectoriesForChart(snapshot, pendingChart);

        Assert.AreEqual(0, directories.Count);
    }


    /// <summary>単純集合R/S/CからU・M・Jを検算します。同梱重複・残りだけの補完・候補単独80%不要・一致一件を同じ評価で確認します。</summary>
    [DataTestMethod]
    [DataRow(5, 4, 4, 1)]
    [DataRow(5, 4, 0, 5)]
    [DataRow(5, 5, 0, 1)]
    [DataRow(500, 400, 400, 100)]
    [DataRow(5, 0, 0, 4)]
    public void Estimate_ReverseAggregationMatchesSimpleSets(int required, int supplied, int candidateStart, int candidateCount)
    {
        string[] names = Enumerable.Range(0, required).Select(i => "r" + i + ".wav").ToArray();
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms", names) with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Source", names.Take(supplied));
        cache.AddDir(@"C:\Candidate", names.Skip(candidateStart).Take(candidateCount).Concat(["extra.wav"]));
        var snapshot = new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = chart,
            DefinedResources = ChartResourceSnapshot.Create(chart),
            TargetMetadataProfile = InstallEstimationMetadataNormalizer.BuildProfile([("Song", "Artist", "target")]),
            BundledResources = cache.GetEntryOrNull(@"C:\Source"),
            SourceDirectory = @"C:\Pending"
        };
        InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal, _ => snapshot.TargetMetadataProfile);
        InstallEstimationCandidate candidate = result.Candidates.Single(value => value.DirectoryPath == @"C:\Candidate");
        HashSet<string> r = names.ToHashSet();
        var suppliedSet = names.Take(supplied).ToHashSet();
        var c = names.Skip(candidateStart).Take(candidateCount).Append("extra.wav").ToHashSet();
        int m = r.Intersect(c).Count();
        int u = r.Intersect(suppliedSet.Union(c)).Count();
        int union = r.Union(c).Count();
        Assert.AreEqual(m, candidate.AudioExactMatched);
        Assert.AreEqual(u, candidate.AudioMatched);
        Assert.AreEqual((int)(100L * u / required), candidate.AudioHealth);
        Assert.AreEqual((int)Math.Round(100d * m / union, MidpointRounding.AwayFromZero), candidate.AudioJaccard);
        if (candidateStart >= supplied && supplied < required)
        { Assert.AreEqual(@"C:\Candidate", result.SelectedCandidate.DirectoryPath); }
    }

    /// <summary>全候補の分類を通常は優先し、明示保持は資源優先にします。先頭が非Strongなら下位Strongへ自動乗換えしません。</summary>
    [DataTestMethod]
    [DataRow(false, 99, 98)]
    [DataRow(false, 97, 100)]
    [DataRow(true, 99, 98)]
    [DataRow(true, 97, 100)]
    public void Estimate_CandidatePolicySeparatesDisplayRankingFromAutomaticPermission(bool keep, int strongHealth, int relatedHealth)
    {
        string[] names = Enumerable.Range(0, 100).Select(i => "r" + i + ".wav").ToArray();
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms", names) with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Unrelated", names);
        cache.AddDir(@"C:\Strong", names.Take(strongHealth));
        cache.AddDir(@"C:\Related", names.Take(relatedHealth).Concat(relatedHealth == 100 ? new[] { "extra.wav" } : []));
        var service = new BmsLibraryInstallEstimationService(new BmsLibraryOptionsSnapshot { InstallDestinationAutoApplyMinAudioHealth = 97 });
        InstallEstimationResult result = service.EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal,
            path => InstallEstimationMetadataNormalizer.BuildProfile([(path.EndsWith("Unrelated") ? "ZZZZ" : "Song", path.EndsWith("Strong") ? "Artist" : "XXXX", path)]),
            candidatePolicy: keep ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
        CollectionAssert.AreEqual(keep ? relatedHealth == 100 ? new[] { @"C:\Unrelated", @"C:\Related", @"C:\Strong" } : new[] { @"C:\Unrelated", @"C:\Strong", @"C:\Related" } : new[] { @"C:\Strong", @"C:\Related" },
            result.Candidates.Select(candidate => candidate.DirectoryPath).ToArray());
        Assert.AreEqual(!keep, result.ShouldAutoApplyDestination);
        Assert.AreEqual(keep ? InstallEstimationLowConfidenceKind.MetadataMismatch : InstallEstimationLowConfidenceKind.None, result.LowConfidenceKind);
        Assert.AreEqual(keep ? InstallAudioCompleteness.Complete : InstallAudioCompleteness.Missing, result.AudioCompleteness);
    }

    /// <summary>リソース順位の11位・170位にだけStrongがあっても発見します。表示三件へ切る前に全候補を分類します。</summary>
    [DataTestMethod]
    [DataRow(11)]
    [DataRow(170)]
    public void Estimate_ExaminesStrongBeyondFormerCandidateFrontier(int strongPosition)
    {
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms", "tone.wav") with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
        var cache = new DirectoryResourceLookupCache();
        for (int i = 1; i <= strongPosition; i++) { cache.AddDir(@"C:\Candidates\" + i.ToString("D3"), new[] { "tone.wav" }.Concat(Enumerable.Range(0, i).Select(n => "extra" + n + ".wav"))); }
        InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal,
            path => InstallEstimationMetadataNormalizer.BuildProfile([("Song", path.EndsWith(strongPosition.ToString("D3")) ? "Artist" : "ZZZZ", path)]));
        Assert.IsTrue(result.ShouldAutoApplyDestination);
        Assert.IsTrue(result.DestinationDirectory.EndsWith(strongPosition.ToString("D3")));
        Assert.AreEqual(3, result.Candidates.Count);
    }

    /// <summary>保持モードで表示外の四番目Strongも競合へ数え、Jaccardとパスの差だけでは曖昧性を解消しません。Relatedと非関連はStrongの競合に数えません。</summary>
    [TestMethod]
    public void Estimate_AmbiguityUsesAllStrongCandidatesBeyondDisplayLimit()
    {
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms", "tone.wav") with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
        var cache = new DirectoryResourceLookupCache();
        for (int i = 1; i <= 4; i++) { cache.AddDir(@"C:\Candidate" + i, new[] { "tone.wav" }.Concat(Enumerable.Range(0, i).Select(n => "extra" + n + ".wav"))); }
        InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal,
            path => InstallEstimationMetadataNormalizer.BuildProfile([(path.EndsWith("2") ? "ZZZZ" : "Song", path.EndsWith("3") || path.EndsWith("2") ? "XXXX" : "Artist", path)]),
            candidatePolicy: InstallEstimationCandidatePolicy.KeepUnrelated);
        Assert.AreEqual(3, result.Candidates.Count);
        Assert.AreEqual(@"C:\Candidate1", result.SelectedCandidate.DirectoryPath);
        Assert.AreEqual(@"C:\Candidate4", result.AmbiguousCandidateDirectory);
        Assert.AreEqual(InstallEstimationConfidence.Low, result.Confidence);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
    }

    /// <summary>各譜面へ丸め前の90/97/100%閾値を適用します。Missingのまま許可した場合も一般欠損警告を保ち、自動設定見送り警告は出しません。不正保存値は丸めず拒否します。</summary>
    [DataTestMethod]
    [DataRow(90, 97, true, false)]
    [DataRow(90, 90, true, false)]
    [DataRow(100, 100, true, false)]
    [DataRow(97, 97, true, false)]
    [DataRow(97, 97, true, true)]
    [DataRow(97, 96, false, false)]
    [DataRow(100, 97, false, false)]
    [DataRow(89, 100, false, false)]
    [DataRow(101, 100, false, false)]
    public void Estimate_PerChartAudioThresholdPreservesMissingAndWarnings(int threshold, int existing, bool apply, bool hash)
    {
        string[] names = Enumerable.Range(0, 100).Select(i => "r" + i + ".wav").ToArray();
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms", names) with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        var entry = PackageChartEntry.FromChart(chart);
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([entry]);
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Candidate", names.Take(existing));
        var service = new BmsLibraryInstallEstimationService(new BmsLibraryOptionsSnapshot { InstallDestinationAutoApplyMinAudioHealth = threshold });
        InstallEstimationResult result = hash
            ? service.EvaluateInstalledChartHashDestination(@"C:\Candidate", [entry], _ => snapshot.TargetMetadataProfile, snapshot, cache)
            : service.EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal, _ => snapshot.TargetMetadataProfile);
        Assert.AreEqual(hash ? InstallEstimationFinalEvaluationMode.InstalledChartHash : InstallEstimationFinalEvaluationMode.RelativeStrict, result.FinalEvaluationMode);
        Assert.AreEqual(apply, result.ShouldAutoApplyDestination);
        Assert.AreEqual(existing == 100 ? InstallAudioCompleteness.Complete : InstallAudioCompleteness.Missing, result.AudioCompleteness);
        entry.ApplyInstallEstimationResult(result);
        BmsLibraryPackageInstallService.RefreshPendingResourceHealth([entry], null, cache, snapshot.BundledResources, true);
        Assert.AreEqual(existing < 100 && !apply, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.InstallEstimationAudioMissing));
        Assert.AreEqual(existing < 100, entry.Chart.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
    }

    /// <summary>集約99%でも一譜面が50%なら自動確定しません。各譜面の不足を大きい兄弟の集合で隠す誤りを検出します。</summary>
    [TestMethod]
    public void Estimate_AggregateAudioDoesNotHideIncompleteSibling()
    {
        string[] names = Enumerable.Range(0, 100).Select(i => "r" + i + ".wav").ToArray();
        PackageChartEntry[] entries = new[] { CreateFile("first", @"C:\Pending\a.bms", names.Take(99).ToArray()), CreateFile("second", @"C:\Pending\b.bms", names.Skip(98).ToArray()) }
            .Select(chart => PackageChartEntry.FromChart(chart with { RawTitle = "Song", Artist = "Artist", RawArtist = "Artist" })).ToArray();
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries(entries);
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Candidate", names.Take(99));
        var service = new BmsLibraryInstallEstimationService(new BmsLibraryOptionsSnapshot { InstallDestinationAutoApplyMinAudioHealth = 90 });
        InstallEstimationResult result = service.EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal, _ => snapshot.TargetMetadataProfile);
        Assert.AreEqual(99, result.SelectedCandidate.AudioHealth);
        Assert.AreEqual(InstallAudioCompleteness.Missing, result.AudioCompleteness);
        Assert.IsFalse(result.ShouldAutoApplyDestination);
    }

    /// <summary>再導入は無関係候補を除外し、唯一Strongだけを改善と各譜面閾値で確定します。Relatedは妨げず、異順位の複数Strongと同梱による不足の隠蔽を拒否します。</summary>
    [DataTestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public void Estimate_ReinstallRequiresOneImprovedStrongWithoutBundledResources(bool secondStrong, bool bundledMasksMissing)
    {
        string[] names = Enumerable.Range(0, 100).Select(i => "r" + i + ".wav").ToArray();
        ChartFile chart = CreateFile("target", @"C:\Current\chart.bms", names) with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Current", bundledMasksMissing ? names.Take(90) : []);
        cache.AddDir(@"C:\Strong", names.Take(bundledMasksMissing ? 80 : 97));
        if (!bundledMasksMissing)
        {
            cache.AddDir(@"C:\Related", names);
            cache.AddDir(@"C:\Unrelated", names);
            cache.AddDir(@"C:\Unavailable", names);
        }
        snapshot.BundledResources = new DirectoryResourceLookupCache.Entry(names.Select(name => ChartResourceKeyHash.GetLookupHash(Path.GetFileNameWithoutExtension(name))), [], []);
        var service = new BmsLibraryInstallEstimationService(new BmsLibraryOptionsSnapshot { InstallDestinationAutoApplyMinAudioHealth = bundledMasksMissing || secondStrong ? 90 : 97, AutoApplyAmbiguousInstallDestination = true });
        InstallEstimationResult result = service.EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.ReinstallCorrection,
            path => path.EndsWith("Unavailable") ? InstallEstimationMetadataProfile.Empty : InstallEstimationMetadataNormalizer.BuildProfile([
                (path.EndsWith("Unrelated") ? "ZZZZ" : "Song", path.EndsWith("Strong") || secondStrong && path.EndsWith("Related") ? "Artist" : "XXXX", path)]));
        Assert.AreEqual(!secondStrong && !bundledMasksMissing, result.ShouldAutoApplyDestination);
        Assert.IsFalse(result.Candidates.Any(candidate => candidate.MetadataRelation < InstallEstimationMetadataRelation.Related));
        Assert.AreEqual(bundledMasksMissing ? 80 : secondStrong ? 100 : 97, result.SelectedCandidate.AudioHealth);
    }

    /// <summary>同じ相対キーでもカテゴリを分離し、複数ID・拡張子違い・同梱重複は各カテゴリで一件だけ数えます。</summary>
    [TestMethod]
    public void Estimate_AggregationSeparatesCategoriesAndDeduplicatesReferences()
    {
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms") with
        {
            RawTitle = "Song",
            RawArtist = "Artist",
            Artist = "Artist",
            Resources = TestChartResources.Create(["same.wav", "same.ogg"], ["same.png", "same.jpg", "same.mp4"], "same.png")
        };
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
        var cache = new DirectoryResourceLookupCache();
        cache.AddDir(@"C:\Candidate", ["same.wav", "same.png", "same.mp4"]);
        cache.AddDir(@"C:\Source", ["same.wav", "same.png"]);
        snapshot.BundledResources = cache.GetEntryOrNull(@"C:\Source");
        snapshot.SourceDirectory = @"C:\Source";
        InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal, _ => snapshot.TargetMetadataProfile);
        InstallEstimationCandidate candidate = result.SelectedCandidate;
        Assert.AreEqual(4, result.TargetResourceCount);
        Assert.AreEqual(1, candidate.AudioMatched);
        Assert.AreEqual(1, candidate.VisualMatched);
        Assert.AreEqual(1, candidate.MovieMatched);
        Assert.AreEqual(1, candidate.OptionalImageMatched);
        Assert.AreEqual(1, candidate.AudioExactMatched);
        Assert.AreEqual(1, candidate.VisualExactMatched);
        Assert.AreEqual(1, candidate.MovieExactMatched);
        Assert.AreEqual(1, candidate.OptionalImageExactMatched);
        Assert.AreEqual(3, candidate.CandidateResourceCount);
        Assert.IsTrue(result.ShouldAutoApplyDestination);
    }

    /// <summary>祖先抑制は分類後に行い、関連性の弱い子をStrong親の抑制理由にしません。同じStrongなら自身の所有条件で子へ絞ります。</summary>
    [DataTestMethod]
    [DataRow(0, false)]
    [DataRow(1, false)]
    [DataRow(2, false)]
    [DataRow(0, true)]
    [DataRow(1, true)]
    [DataRow(2, true)]
    public void Estimate_AncestorSuppressionRequiresDescendantRelationAtLeastAsStrong(int childKind, bool keep)
    {
        ChartFile chart = CreateFile("target", @"C:\Pending\chart.bms", "tone.wav") with { RawTitle = "Song", RawArtist = "Artist", Artist = "Artist" };
        PackageInstallEstimationSnapshot snapshot = PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries([PackageChartEntry.FromChart(chart)]);
        var cache = new DirectoryResourceLookupCache();
        uint tone = ChartResourceKeyHash.GetLookupHash("tone");
        cache.AddDir(@"C:\Parent", [tone], [], [], [], [], []);
        cache.AddDir(@"C:\Parent\Child", [tone], [], [], [tone], [], []);
        InstallEstimationResult result = CreateService().EstimateInstallationDirectory(snapshot, cache, false, ChartInstallationEstimateMode.Normal,
            path => InstallEstimationMetadataNormalizer.BuildProfile([(
                path.EndsWith("Child") && childKind == 0 ? "ZZZZ" : "Song",
                path.EndsWith("Child") && childKind != 2 ? "XXXX" : "Artist", path)]),
            candidatePolicy: keep ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
        Assert.AreEqual(childKind == 2 ? @"C:\Parent\Child" : @"C:\Parent", result.SelectedCandidate.DirectoryPath);
        Assert.AreEqual(childKind == 2 ? 1 : keep || childKind == 1 ? 2 : 1, result.Candidates.Count);
    }

    private void WithWorkspace(Action<string, BmsLibraryInstallEstimationService> testAction)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_InstallEstimateTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            testAction(tempRoot, CreateService());
        }
        finally
        {
            DeleteDirectoryIfExists(tempRoot);
        }
    }

    private BmsLibraryInstallEstimationService CreateService()
    {
        return new BmsLibraryInstallEstimationService(BmsLibraryOptionsSnapshot.CreateCurrent(testSettings));
    }

    private static InstalledChartLookupIndexSnapshot CreateInstalledChartLookupIndexSnapshot(IEnumerable<ChartFile> installedFiles, IEnumerable<ChartFile>? installedBmsonSongs = null)
    {
        var state = new InstalledChartLookupIndexState();
        foreach (ChartFile file in installedFiles ?? [])
        {
            if (file != null)
            {
                state.AddChart(file.Path, file.Md5, file.Sha256);
            }
        }
        foreach (ChartFile song in installedBmsonSongs ?? [])
        {
            if (song != null)
            {
                state.AddChart(song.Path, song.Md5, song.Sha256);
            }
        }
        return state.CreateSnapshot();
    }

    private static InstallEstimationResult EstimateLooseChartInstallationDirectory(BmsLibraryInstallEstimationService service, IEnumerable<ChartFile> chartFiles, HashSet<string> installedHashes, DirectoryResourceLookupCache? directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile>? metadataProfileResolver = null)
    {
        return service.EstimateInstallationDirectory(
            BuildLooseChartSnapshot(chartFiles, installedHashes, estimateMode)!,
            directoryLookupCache!,
            asParallel,
            estimateMode,
            metadataProfileResolver, candidatePolicy: metadataProfileResolver == null ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
    }

    private static InstallEstimationResult EstimateLooseChartInstallationDirectory(BmsLibraryInstallEstimationService service, IEnumerable<PackageChartEntry> chartEntries, HashSet<string> installedHashes, DirectoryResourceLookupCache? directoryLookupCache, bool asParallel, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile>? metadataProfileResolver = null)
    {
        return service.EstimateInstallationDirectory(
            BuildLooseChartSnapshot(chartEntries, installedHashes, estimateMode)!,
            directoryLookupCache!,
            asParallel,
            estimateMode,
            metadataProfileResolver, candidatePolicy: metadataProfileResolver == null ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
    }

    private static InstallEstimationResult EstimateLooseChartInstallationDirectory(BmsLibraryInstallEstimationService service, IEnumerable<ChartFile> chartFiles, HashSet<string> installedHashes, DirectoryResourceLookupCache? directoryLookupCache, int candidateEvaluationDegree, ChartInstallationEstimateMode estimateMode, Func<string, InstallEstimationMetadataProfile>? metadataProfileResolver = null)
    {
        return service.EstimateInstallationDirectory(
            BuildLooseChartSnapshot(chartFiles, installedHashes, estimateMode)!,
            directoryLookupCache!,
            candidateEvaluationDegree,
            estimateMode,
            metadataProfileResolver, candidatePolicy: metadataProfileResolver == null ? InstallEstimationCandidatePolicy.KeepUnrelated : InstallEstimationCandidatePolicy.RelatedOnly);
    }

    private static PackageInstallEstimationSnapshot? BuildLooseChartSnapshot(IEnumerable<ChartFile> chartFiles, HashSet<string> installedHashes, ChartInstallationEstimateMode estimateMode)
    {
        List<PackageChartEntry> targetEntries = [.. (chartFiles ?? [])
            .Select(file => PackageChartEntry.FromChart((file)))
            .Where(entry => entry?.Chart != null)];
        return BuildLooseChartSnapshot(targetEntries, installedHashes, estimateMode);
    }

    private static PackageInstallEstimationSnapshot? BuildLooseChartSnapshot(IEnumerable<PackageChartEntry> chartEntries, HashSet<string> installedHashes, ChartInstallationEstimateMode estimateMode)
    {
        List<PackageChartEntry> targetEntries = [.. (chartEntries ?? []).Where(entry => entry?.Chart != null)];
        if (targetEntries.Count == 0 || targetEntries.Any(entry => !string.IsNullOrWhiteSpace(entry.Chart?.InstallDestination)))
        {
            return null;
        }
        bool isCorrectionLikeMode = estimateMode == ChartInstallationEstimateMode.ReinstallCorrection;
        if (!isCorrectionLikeMode && installedHashes != null)
        {
            targetEntries = [.. targetEntries.Where(entry => !installedHashes.Contains(ChartLookupKey.GetPrimaryHash(entry.Chart)))];
        }
        return targetEntries.Count == 0 ? null : PackageInstallEstimationSnapshotBuilder.BuildForLooseEntries(targetEntries);
    }

    private static PackageInstallEstimationSnapshot BuildPackageSnapshot(ChartPackage package, IEnumerable<ChartFile> targetFiles)
    {
        List<ChartFile> targetFileList = [.. (targetFiles ?? []).Where(file => file != null)];
        List<PackageChartEntry> targetEntries = targetFileList.Count == 0
            ? package.ChartEntries
            : ResolvePackageEntries(package, targetFileList);
        return package.GetOrBuildInstallEstimationSnapshotFromEntries(targetEntries);
    }

    private static List<PackageChartEntry> ResolvePackageEntries(ChartPackage package, IEnumerable<ChartFile> targetFiles)
    {
        List<PackageChartEntry> packageEntries = package.ChartEntries;
        var result = new List<PackageChartEntry>();
        foreach (ChartFile targetFile in (targetFiles ?? []).Where(file => file != null))
        {
            PackageChartEntry? packageEntry = packageEntries.FirstOrDefault(entry => IsSamePackageChartTarget(entry, targetFile));
            result.Add(packageEntry ?? PackageChartEntry.FromChart((targetFile)));
        }
        return [.. result.Where(entry => entry?.Chart != null)];
    }

    private static bool IsSamePackageChartTarget(PackageChartEntry entry, ChartFile targetFile)
    {
        if (entry?.Chart == null || targetFile == null)
        {
            return false;
        }
        return entry.IsSameChartTarget(targetFile);
    }

    private static ChartFile CreateFile(string? hash, string path, params string[] wavFiles)
    {
        ChartFile file = ChartTestValues.Empty() with
        {
            Path = path,
            Resources = TestChartResources.Create(wavFiles, null)
        };
        file = file with { Md5 = hash };
        return file;
    }

    private static LR2SongDBExtended.maintenance CreateMaintenanceInfo(ChartFile file, int wavDefined, int wavExisting)
    {
        return new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = wavDefined, wav_files_existing = wavExisting, bga_files_defined = 0, bga_files_existing = 0, movie_files_defined = 0, movie_files_existing = 0, is_stagefile_defined = false, is_stagefile_existing = false, is_backbmp_defined = false, is_backbmp_existing = false, is_banner_defined = false, is_banner_existing = false };
    }

    private static LR2SongDBExtended.maintenance CreateVisualMaintenanceInfo(ChartFile file, int bgaDefined = 0, int bgaExisting = 0, bool stagefileDefined = false, bool stagefileExisting = false, bool backbmpDefined = false, bool backbmpExisting = false, bool bannerDefined = false, bool bannerExisting = false)
    {
        return new LR2SongDBExtended.maintenance { path = file.Path, hash = file.Md5, wav_files_defined = 0, wav_files_existing = 0, bga_files_defined = bgaDefined, bga_files_existing = bgaExisting, movie_files_defined = 0, movie_files_existing = 0, is_stagefile_defined = stagefileDefined, is_stagefile_existing = stagefileExisting, is_backbmp_defined = backbmpDefined, is_backbmp_existing = backbmpExisting, is_banner_defined = bannerDefined, is_banner_existing = bannerExisting };
    }

    private static void DeleteDirectoryIfExists(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }
        foreach (string childFilePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(childFilePath, FileAttributes.Normal);
        }
        foreach (string childDirectoryPath in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(childDirectoryPath, FileAttributes.Normal);
        }
        File.SetAttributes(directoryPath, FileAttributes.Normal);
        Directory.Delete(directoryPath, recursive: true);
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

}
