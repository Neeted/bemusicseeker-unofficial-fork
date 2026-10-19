using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests.Performance;

/// <summary>通常の機能検証から分離し、同じ合成入力の初回プロフィール構築と再利用を明示計測します。</summary>
[TestClass]
[TestCategory("Net10Performance")]
[TestCategory("estimation")]
// プロセス全体の割当と実時間を測るため、別ケースの計測を重ねません。製品内の有界並列は測定対象です。
[DoNotParallelize]
public sealed class InstallEstimationPerformanceTests
{
    // MSTestが各メソッドの開始前に注入する実行結果出力先です。
    public TestContext TestContext { get; set; } = null!;

    private sealed record Scenario(int Required, int Candidates, int BundledPercent, int MaxPairs);

    /// <summary>保証：実カタログの同じ世代から重複する20要求を解決し、初回構築共有と再利用の費用・入力行数を5反復で測ります。</summary>
    /// <remarks>
    /// 測定対象をプロフィールへ限定するため既存の非公開resolverへdelegateを結び、診断counterを観測します。
    /// この反射は準備と計測境界だけに使い、製品の公開APIや通常機能テストへ計測用の窓口を追加しません。
    /// 取得・リソース評価・DB保存・UI終端は別範囲です。
    /// </remarks>
    [TestMethod]
    public void CatalogProfiles_MeasureColdSharingAcrossTwentyRequests()
    {
        string root = Path.Combine(Path.GetTempPath(), "BmsEstimationProfiles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
            {
                int count = scale == Net10PerformanceCorpusScale.Large ? 20_001 : 3_000;
                int maxPairs = scale switch { Net10PerformanceCorpusScale.Small => 1, Net10PerformanceCorpusScale.Medium => 1_000, _ => 4 };
                string databaseDirectory = Path.Combine(root, scale.ToString());
                Directory.CreateDirectory(databaseDirectory);
                string database = Path.Combine(databaseDirectory, "song.db");
                File.WriteAllBytes(database, []);
                var library = new TestBmsLibrary(database);
                string[] directories = [.. Enumerable.Range(0, count).Select(index => $@"C:\Library\Profile{index:D5}")];
                library.BmsCharts = [.. directories.SelectMany((path, index) => Enumerable.Range(0, index == count - 1 ? maxPairs : 1)
                    .Select(pair => ChartTestValues.Empty() with
                    {
                        Token = new OwnedChartToken(),
                        Md5 = (index * 1_001 + pair + 1).ToString("x32", CultureInfo.InvariantCulture),
                        Sha256 = (index * 1_001 + pair + 1).ToString("x64", CultureInfo.InvariantCulture),
                        Path = $"{path}\\chart{pair:D4}.bms", RawTitle = $"Title {index} {pair}",
                        Title = $"Title {index} {pair}",
                        RawArtist = $"Artist {index}", Artist = $"Artist {index}"
                    }))];
                MethodInfo method = typeof(BMSLibrary).GetMethod("ResolveInstallDestinationMetadataProfileThreadSafe", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("プロフィール計測の既存解決境界が見つかりません。");
                Func<string, InstallEstimationMetadataProfile> resolve = method.CreateDelegate<Func<string, InstallEstimationMetadataProfile>>(library);
                Assert.IsTrue(resolve(directories[0]).HasPair, "計測前の実カタログ準備を確認します。");
                FieldInfo field = typeof(BMSLibrary).GetField("catalogOwnedCollectionOwner", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?? throw new InvalidOperationException("プロフィール計測の既存所有者が見つかりません。");
                CatalogOwnedCollectionOwner owner = field.GetValue(library) as CatalogOwnedCollectionOwner
                    ?? throw new InvalidOperationException("プロフィール計測の所有者を取得できません。");
                foreach (int batch in new[] { 1, 20 })
                {
                    foreach (bool cold in new[] { true, false })
                    {
                        var samples = new List<double>();
                        for (int repetition = 0; repetition < 5; repetition++)
                        {
                            lock (owner.MetadataProfileGate) { owner.MetadataProfiles.Clear(); }
                            if (!cold) { foreach (string directory in directories) { resolve(directory); } }
                            long builds = owner.MetadataProfileBuildCount, reused = owner.MetadataProfileReuseCount, records = owner.MetadataProfileRecordCount;
                            var watch = Stopwatch.StartNew();
                            Parallel.For(0, batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, _ =>
                            {
                                foreach (string directory in directories) { Assert.IsTrue(resolve(directory).HasPair); }
                            });
                            watch.Stop();
                            long built = owner.MetadataProfileBuildCount - builds;
                            Assert.AreEqual(cold ? count : 0L, built);
                            samples.Add(watch.Elapsed.TotalMilliseconds);
                            TestContext.WriteLine($"route=install_estimation_catalog_profiles scale={scale} folders={count} maxPairs={maxPairs} batch={batch} cold={cold} repetition={repetition + 1} elapsedMs={watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} builds={built} reused={owner.MetadataProfileReuseCount - reused} inputRecords={owner.MetadataProfileRecordCount - records}");
                        }
                        double[] ordered = [.. samples.Order()];
                        TestContext.WriteLine($"route=install_estimation_catalog_summary scale={scale} folders={count} maxPairs={maxPairs} batch={batch} cold={cold} samplesMs={string.Join(",", samples.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} medianMs={ordered[2].ToString("F3", CultureInfo.InvariantCulture)} minMs={ordered[0].ToString("F3", CultureInfo.InvariantCulture)} maxMs={ordered[^1].ToString("F3", CultureInfo.InvariantCulture)}");
                    }
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 保証：実サービスの全探索で通常と保持の結果を維持し、要求100～1,000、同梱率、2万超候補、
    /// 末尾の大量ペア、単体と同一20入力の初回/再利用を5反復で測ります。秒数を合否にしません。
    /// </summary>
    /// <remarks>
    /// 索引準備、プロフィール構築、候補評価までが測定範囲です。取得済み入力を使い、実FS・DB保存・UI反映は含みません。
    /// プロフィールの供給cacheはfixture所有です。製品のカタログcache共有や実取り込み全体の計測とは区別します。
    /// </remarks>
    [TestMethod]
    public void CapturedInputs_MeasureFullCandidateEvaluationAndProfileReuse()
    {
        foreach (Net10PerformanceCorpusScale scale in Net10PerformanceCorpus.GetConfiguredScales())
        {
            Scenario[] scenarios = scale switch
            {
                Net10PerformanceCorpusScale.Small => [new(100, 3_000, 0, 1), new(300, 3_000, 80, 4)],
                Net10PerformanceCorpusScale.Medium => [new(500, 3_000, 100, 1), new(1_000, 3_000, 0, 4), new(500, 3_000, 80, 1_000)],
                _ => [new(500, 20_001, 80, 1), new(1_000, 20_001, 100, 4)]
            };
            foreach (Scenario scenario in scenarios)
            {
                MeasureScenario(scale, scenario);
            }
        }
    }

    private void MeasureScenario(Net10PerformanceCorpusScale scale, Scenario scenario)
    {
        string[] names = [.. Enumerable.Range(0, scenario.Required).Select(index => $"tone{index:D4}.wav")];
        ChartFile chart = ChartTestValues.Empty() with
        {
            Path = @"C:\Pending\benchmark.bms",
            RawTitle = "Target Work",
            RawArtist = "Composer X",
            Artist = "Composer X",
            Resources = TestChartResources.Create(names, null)
        };
        var resources = ChartResourceSnapshot.Create(chart);
        var cache = new DirectoryResourceLookupCache();
        int suppliedCount = scenario.Required * scenario.BundledPercent / 100;
        string[] directories = [.. Enumerable.Range(0, scenario.Candidates).Select(index => $@"C:\Library\Candidate{index:D5}")];
        for (int index = 0; index < directories.Length; index++)
        {
            // 100%候補を2件、他は候補数を保つ80%以上の一致先にする。頻出要求で全候補を再照合する費用を観測できる。
            IEnumerable<string> owned = index <= 1 ? names
                : scenario.BundledPercent == 0 ? names.Take(scenario.Required * 80 / 100)
                : names.Take(Math.Min(16, suppliedCount));
            cache.AddDir(directories[index], index == 0 ? owned.Append("extra.wav") : owned);
        }
        var snapshot = new PackageInstallEstimationSnapshot
        {
            RepresentativeChart = chart,
            SourceDirectory = @"C:\Pending",
            DefinedResources = resources,
            TargetChartResources = [resources],
            TargetMetadataProfile = InstallEstimationMetadataNormalizer.BuildProfile([("Target Work", "Composer X", chart.Path)]),
            BundledResources = new DirectoryResourceLookupCache.Entry(
                resources.AudioReferences.Take(suppliedCount).Select(reference => reference.RelativePathHash), [], []),
            SourceSurfaceIsComplete = true
        };
        var options = new BmsLibraryOptionsSnapshot { InstallDestinationAutoApplyMinAudioHealth = 100 };
        var service = new BmsLibraryInstallEstimationService(options);
        var indexWatch = Stopwatch.StartNew();
        cache.EnsureAudioRelativeDirectoriesByHashes(resources.AudioReferences.Select(reference => reference.RelativePathHash));
        indexWatch.Stop();
        TestContext.WriteLine($"route=install_estimation_index scale={scale} required={scenario.Required} candidates={scenario.Candidates} indexPrepareMs={indexWatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)}");

        foreach (InstallEstimationCandidatePolicy policy in Enum.GetValues<InstallEstimationCandidatePolicy>())
        {
            // 単体逐次、単体有界並列、20件外側有界並列（候補内側1）を同じ入力で比較する。
            foreach ((int batch, int innerDegree) in new[] { (1, 1), (1, BmsLibraryInstallEstimationService.ResolveDefaultCandidateEvaluationDegree()), (20, 1) })
            {
                foreach (bool cold in new[] { true, false })
                {
                    var elapsed = new List<double>();
                    var allocated = new List<long>();
                    for (int repetition = 0; repetition < 5; repetition++)
                    {
                        var profiles = new ConcurrentDictionary<string, Lazy<InstallEstimationMetadataProfile>>(StringComparer.OrdinalIgnoreCase);
                        int builds = 0;
                        InstallEstimationMetadataProfile Resolve(string path)
                        {
                            return profiles.GetOrAdd(path, key => new Lazy<InstallEstimationMetadataProfile>(() =>
                            {
                                Interlocked.Increment(ref builds);
                                int index = int.Parse(key[^5..], CultureInfo.InvariantCulture);
                                int pairs = scenario.MaxPairs == 1_000 && index != scenario.Candidates - 1 ? 1 : scenario.MaxPairs;
                                IEnumerable<(string Title, string Artist, string Path)> records = Enumerable.Range(0, pairs)
                                    .Select(pair => (Title: $"qqz {index:D5} {pair:D4}", Artist: $"zzz {index:D5}", Path: $"{key}\\chart{pair:D4}.bms"));
                                if (index == 0)
                                {
                                    records = [("Target Work", "Composer X", $"{key}\\chart.bms")];
                                }
                                else if (index == scenario.Candidates - 1 && scenario.MaxPairs > 1)
                                {
                                    records = records.Take(pairs - 1).Append(("Target Work", "Different Author", $"{key}\\last.bms"));
                                }
                                return InstallEstimationMetadataNormalizer.BuildProfile(records);
                            }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
                        }
                        if (!cold)
                        {
                            foreach (string directory in directories) { Resolve(directory); }
                        }
                        int initialBuilds = builds;
                        var results = new InstallEstimationResult[batch];
                        long before = GC.GetTotalAllocatedBytes(precise: true);
                        var watch = Stopwatch.StartNew();
                        if (batch == 1)
                        {
                            results[0] = service.EstimateInstallationDirectory(snapshot, cache, innerDegree,
                                ChartInstallationEstimateMode.Normal, Resolve, candidatePolicy: policy);
                        }
                        else
                        {
                            Parallel.For(0, batch, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, item =>
                            {
                                results[item] = service.EstimateInstallationDirectory(snapshot, cache, 1,
                                    ChartInstallationEstimateMode.Normal, Resolve, candidatePolicy: policy);
                            });
                        }
                        watch.Stop();
                        long bytes = GC.GetTotalAllocatedBytes(precise: true) - before;
                        elapsed.Add(watch.Elapsed.TotalMilliseconds);
                        allocated.Add(bytes);
                        foreach (InstallEstimationResult result in results)
                        {
                            Assert.AreEqual(directories[policy == InstallEstimationCandidatePolicy.RelatedOnly ? 0 : 1], result.SelectedCandidate.DirectoryPath);
                            Assert.AreEqual(policy == InstallEstimationCandidatePolicy.RelatedOnly, result.ShouldAutoApplyDestination);
                            Assert.AreEqual(scenario.Required, result.SelectedCandidate.AudioMatched);
                        }
                        Assert.AreEqual(cold ? scenario.Candidates : 0, builds - initialBuilds);
                        TestContext.WriteLine($"route=install_estimation_sample scale={scale} required={scenario.Required} candidates={scenario.Candidates} bundled={scenario.BundledPercent} maxPairs={scenario.MaxPairs} policy={policy} batch={batch} innerDegree={innerDegree} coldProfiles={cold} repetition={repetition + 1} elapsedMs={watch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)} allocatedBytes={bytes} profileBuilds={builds - initialBuilds} reverseVisits={results.Sum(result => result.ReverseLookupVisits)} pairComparisons={results.Sum(result => (long)result.MetadataPairComparisonCount)}");
                    }
                    double[] ordered = [.. elapsed.Order()];
                    TestContext.WriteLine($"route=install_estimation_summary scale={scale} required={scenario.Required} candidates={scenario.Candidates} bundled={scenario.BundledPercent} maxPairs={scenario.MaxPairs} policy={policy} batch={batch} innerDegree={innerDegree} coldProfiles={cold} samplesMs={string.Join(",", elapsed.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)))} medianMs={ordered[2].ToString("F3", CultureInfo.InvariantCulture)} minMs={ordered[0].ToString("F3", CultureInfo.InvariantCulture)} maxMs={ordered[^1].ToString("F3", CultureInfo.InvariantCulture)} allocatedBytes={string.Join(",", allocated)}");
                }
            }
        }
    }
}
