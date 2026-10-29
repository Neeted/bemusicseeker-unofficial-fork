using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.OwnedChartCollectionTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class BmsLibraryMaintenanceServiceTests
{
    /// <summary>三用途とも先頭非空値を分類後に繰り上げず、後続の実在・不足・親参照を健康度や全定義探索へ混ぜません。</summary>
    [TestMethod]
    [DataRow(".png", "live.png", false, false)]
    [DataRow("missing.png", "live.png", true, false)]
    [DataRow("live.png", "../missing.png", true, true)]
    public void BuildResourceHealthSnapshot_OptionalImagesUseOnlyFirstNonEmptyDefinition(string first, string later, bool defined, bool existing)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "live.png"), []);
            string text = string.Join("\n", new[] { "STAGEFILE", "BACKBMP", "BANNER" }.Select(role => $"#{role} \n#{role} {first}\n#{role} {later}\n#{role} live.png"));
            ChartFile chart = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(Path.Combine(root, "chart.bms"), Encoding.ASCII.GetBytes(text), DateTime.UnixEpoch, "md5", "sha256"));
            ResourceHealthMaintenanceSnapshot result = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
            Assert.AreEqual(defined, result.StagefileDefined);
            Assert.AreEqual(defined ? existing : (bool?)null, result.StagefileExisting);
            Assert.AreEqual(defined, result.BackbmpDefined);
            Assert.AreEqual(defined ? existing : (bool?)null, result.BackbmpExisting);
            Assert.AreEqual(defined, result.BannerDefined);
            Assert.AreEqual(defined ? existing : (bool?)null, result.BannerExisting);
            var all = ChartResourceSnapshot.Create(chart.Resources, includeUnused: true);
            Assert.AreEqual(defined ? 1 : 0, all.OptionalImageReferenceCount);
            Assert.IsFalse(all.HasUnsupportedParentTraversalReference);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>同じ正常集合の存在結果を共有しても、譜面固有の非照合0/1/2件と任意画像の不足を返却時に合成します。順序による分母混同を検出します。</summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_CacheCombinesOnlyMatchableCountsWithIndividualUnmatchableRequests()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "tone.ogg"), []);
            var context = new ResourceHealthLookupContext(null);
            foreach (int count in new[] { 2, 0, 1, 2, 1, 0 })
            {
                var references = new List<ChartResourceReference> { ChartResourceReference.Parse("tone.wav", ChartResourceKind.Audio) };
                for (int index = 0; index < count; index++) { references.Add(ChartResourceReference.Parse($"../nonmatch{index}.wav", ChartResourceKind.Audio)); }
                references.Add(ChartResourceReference.Parse(count == 0 ? ".png" : "../stage.png", ChartResourceKind.Image, ChartResourceUsage.Stagefile));
                ChartFile chart = ChartTestValues.Empty() with { Path = Path.Combine(root, $"{count}.bms"), Resources = references.ToImmutableList() };
                ResourceHealthMaintenanceSnapshot result = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, context);
                Assert.AreEqual(count + 1, result.WavFilesDefined);
                Assert.AreEqual(1, result.WavFilesExisting);
                Assert.AreEqual(count > 0, result.StagefileDefined);
                Assert.AreEqual(count > 0 ? false : (bool?)null, result.StagefileExisting);
            }
            Assert.AreEqual(1, context.ResourceHealthSetCacheEntryCount);
            Assert.AreEqual(5L, context.ResourceHealthSetCacheHitCount);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>対応拡張子だけを照合し、stemの短縮や相対配置を重ねません。物理・取得済み索引・候補配置で同じ正負を確認します。</summary>
    [TestMethod]
    [DataRow("stem", "stem", 0)]
    [DataRow("stem.", "stem..wav", 0)]
    [DataRow("stem.wav", "stem.ogg", 1)]
    [DataRow("foo.v2.wav", "foo.v2.ogg", 1)]
    [DataRow("foo.v2.wav", "foo.ogg", 0)]
    [DataRow(".hidden.wav", ".hidden.ogg", 1)]
    [DataRow("audio.wav/tone.wav", "audio.wav/tone.ogg", 1)]
    [DataRow("sub/tone.wav", "sibling/tone.ogg", 0)]
    [DataRow("sub/tone.wav", "tone.ogg", 0)]
    public void BuildResourceHealthSnapshot_PhysicalIndexAndPlacementUseOneLookupKey(string request, string asset, int expected)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string actual = Path.Combine(root, asset.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(actual) ?? root);
            File.WriteAllBytes(actual, []);
            string path = Path.Combine(root, "chart.bms");
            ChartFile chart = ChartTestValues.Empty() with { Path = path, Resources = [ChartResourceReference.Parse(request, ChartResourceKind.Audio)] };
            ChartScanResult scan = ChartDirectoryScanBuilder.BuildFromAbsolutePaths([path],
                ChartResourceExtensions.IsAudioExtension(Path.GetExtension(actual)) ? [actual] : [], [], []);
            DirectoryResourceLookupCache cache = LibraryResourceIndex.CreateFromScanResult(scan).DirectoryLookupCache;
            var placement = new ChartResourcePlacement(ChartResourcePlacementBasis.Destination, root, candidate: cache.GetEntryOrNull(root));
            foreach (ResourceHealthMaintenanceSnapshot result in new[]
            {
                BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart),
                BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, new ResourceHealthLookupContext(cache)),
                BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, placement: placement)
            })
            {
                Assert.AreEqual(1, result.WavFilesDefined);
                Assert.AreEqual(expected, result.WavFilesExisting);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// 確定した具体名だけを保持して生5項目と合成表示を再復号し、固定印だけの推定名と未確定の具体名は従来検出します。
    /// ASCIIをUTF-8確定からShift_JISへ戻す誤り、確定CP932の復号漏れ、同値再評価の余分な保存を検出します。
    /// </summary>
    [TestMethod]
    [DataRow("utf-8", true, false, "utf-8")]
    [DataRow("utf-8", true, true, "utf-8")]
    [DataRow("shift_jis", true, true, "shift_jis")]
    [DataRow("shift_jis?", true, false, "shift_jis")]
    [DataRow("utf-8", false, false, "shift_jis")]
    public void UpdateMaintenanceInfo_ForceUpdatePreservesOnlyConcreteFixedEncoding(string savedEncoding, bool fixedEncoding, bool nonAscii, string expectedEncoding)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string path = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException("DBの親が必要です。"), "fixed.bms");
            string prefix = nonAscii ? "日本語" : "ASCII";
            File.WriteAllText(path, $"#TITLE {prefix} title\n#SUBTITLE {prefix} subtitle\n#ARTIST {prefix} artist\n#SUBARTIST {prefix} subartist\n#GENRE {prefix} genre\n#WAV01 missing.wav\n#00111:01\n",
                savedEncoding == "shift_jis" ? Encoding.GetEncoding(932) : new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc));
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(path);
            if (!nonAscii)
            {
                Assert.AreEqual("shift_jis", BmsEncodingDetector.Detect(snapshot.Bytes).EncodingName, "ASCIIの自動判定とUTF-8確定保持を区別します。");
            }
            bool preserve = fixedEncoding && !savedEncoding.EndsWith("?", StringComparison.Ordinal);
            ChartFile chart = BmsChartFileParser.ParseSnapshot(snapshot);
            if (preserve)
            {
                chart = chart with { RawTitle = "old", RawSubtitle = "old", RawArtist = "old", Subartist = "old", Genre = "old", Title = "old", Subtitle = "old", Artist = "old" };
            }
            chart = ChartFileProjection.WithMaintenance(chart, new ResourceHealthMaintenanceSnapshot
            { Path = path, Hash = chart.Md5, Encoding = savedEncoding, EncodingFixed = fixedEncoding });
            var requests = new List<CatalogMaintenanceWriteRequest>();
            CatalogMaintenanceWriteReceipt Write(CatalogMaintenanceWriteRequest request)
            {
                requests.Add(request);
                return new CatalogMaintenanceWriteReceipt(true, request.MaintenanceInfos.Count, request.SongMetadataUpdates.Count, 0, 0);
            }
            var service = new BmsLibraryMaintenanceService();
            MaintenanceWorkflowResult first = service.UpdateMaintenanceInfo([chart], true, Write, null);
            ChartFile current = first.ChangedCharts.Single();
            Assert.AreEqual(expectedEncoding, current.EncodingName);
            Assert.AreEqual(fixedEncoding, current.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(preserve ? 1 : 0, first.SongMetadataUpdateCount);
            Assert.AreEqual(chart.Md5, current.Md5);
            Assert.AreEqual(chart.Sha256, current.Sha256);
            if (preserve)
            {
                Assert.AreEqual(prefix + " title", current.RawTitle);
                Assert.AreEqual(prefix + " subtitle", current.RawSubtitle);
                Assert.AreEqual(prefix + " artist", current.RawArtist);
                Assert.AreEqual(prefix + " subartist", current.Subartist);
                Assert.AreEqual(prefix + " genre", current.Genre);
                Assert.AreEqual(prefix + " title " + prefix + " subtitle", current.Title);
                Assert.AreEqual(prefix + " artist " + prefix + " subartist", current.Artist);
                Assert.AreEqual(1, requests.Single().SongMetadataUpdates.Count);
            }
            requests.Clear();
            MaintenanceWorkflowResult second = service.UpdateMaintenanceInfo([current], true, Write, null);
            Assert.AreEqual(0, requests.Count, "固定mtime・同値の保守再評価はwriterを呼びません。");
            Assert.AreEqual(0, second.SongMetadataUpdateCount);
            Assert.AreEqual(0, second.MaintenanceInfoUpsertCount);
            Assert.IsFalse(second.HasUpdates);
        });
    }

    /// <summary>drive root・通常directory・UNCの取得済み索引を正負とも照会し、末尾除去によるcache missと実FS fallbackを検出する。</summary>
    [DataTestMethod]
    [DataRow(@"C:\")]
    [DataRow(@"C:\root-identity")]
    [DataRow(@"\\server\share")]
    public void BuildResourceHealthSnapshot_PreservesRootIdentityWithoutFileFallback(string root)
    {
        string path = Path.Combine(root, "root.bms");
        ChartScanResult scan = ChartDirectoryScanBuilder.BuildFromAbsolutePaths([path], [Path.Combine(root, "tone.ogg")], [], []);
        var context = new ResourceHealthLookupContext(LibraryResourceIndex.CreateFromScanResult(scan).DirectoryLookupCache);
        foreach (string raw in new[] { "tone.wav", "missing.wav" })
        {
            ChartFile chart = ChartTestValues.Empty() with { Path = path, Resources = TestChartResources.Create([raw], []) };
            ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, context);
            Assert.AreEqual(1, health.WavFilesDefined);
            Assert.AreEqual(raw == "tone.wav" ? 1 : 0, health.WavFilesExisting);
            Assert.AreEqual(raw, chart.Resources.Single().RawPath);
        }
        Assert.AreEqual(0, context.FileExistsFallbackCount);
    }

    /// <summary>
    /// 取得済み参照は末尾aliasで存在し、字面の異なる親は不存在となり、元記述を保持して両方を索引だけで判定する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_DistinguishesAliasLikeDirectoriesWithoutFileFallback()
    {
        const string root = @"C:\identity";
        ChartScanResult scan = ChartDirectoryScanBuilder.BuildFromAbsolutePaths(
            [root + @"\root.bms"], [root + @"\audio.ogg\tone.ogg"], [], []);
        var context = new ResourceHealthLookupContext(LibraryResourceIndex.CreateFromScanResult(scan).DirectoryLookupCache);
        foreach (string raw in new[] { @"audio.ogg\tone.wav", @"audio.wav\tone.wav" })
        {
            ChartFile chart = ChartTestValues.Empty() with { Path = root + @"\root.bms", Resources = TestChartResources.Create([raw], []) };
            ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, context);
            Assert.AreEqual(1, health.WavFilesDefined);
            Assert.AreEqual(raw == @"audio.ogg\tone.wav" ? 1 : 0, health.WavFilesExisting);
            Assert.AreEqual(raw, chart.Resources.Single().RawPath);
        }
        Assert.AreEqual(0, context.FileExistsFallbackCount);
    }

    /// <summary>保守件数から単純充足率を計算し、不足を100へ丸めません。補助3役割と両値あり・0/null・null/nullの件数を検査済みとし、片側欠落・補助未取得・encoding-onlyを区別します。</summary>
    [TestMethod]
    public void ResourceHealthCounts_DistinguishZeroUncheckedAndIncompleteRatios()
    {
        ResourceHealthMaintenanceSnapshot Complete(int? defined, int? existing) => new()
        {
            WavFilesDefined = defined,
            WavFilesExisting = existing,
            BgaFilesDefined = 0,
            MovieFilesDefined = 0,
            StagefileDefined = false,
            BannerDefined = false,
            BackbmpDefined = false
        };
        Assert.AreEqual(25, Complete(4, 1).GetWavHealth());
        Assert.IsTrue(Complete(1000, 999).GetWavHealth() < 100);
        foreach (int? existing in new int?[] { 0, null })
        {
            ResourceHealthMaintenanceSnapshot zero = Complete(0, existing);
            Assert.IsTrue(zero.IsInformationChecked);
            Assert.IsNull(zero.GetWavHealth());
        }
        // 正常行から件数または補助の一要素だけを変え、由来に依存しない完了条件を確認します。
        foreach (MaintenanceInfoOrigin origin in new[] { MaintenanceInfoOrigin.Calculated, MaintenanceInfoOrigin.DbHydrated })
        {
            foreach ((int? defined, int? existing, bool completed) in new (int?, int?, bool)[]
                { (1, 1, true), (2, 1, true), (0, 0, true), (0, null, true), (null, null, true), (1, null, false), (null, 0, false), (null, 1, false) })
            {
                ResourceHealthMaintenanceSnapshot baseline = Complete(1, 1) with { Origin = origin };
                foreach (ResourceHealthMaintenanceSnapshot candidate in new[]
                {
                    baseline with { WavFilesDefined = defined, WavFilesExisting = existing },
                    baseline with { BgaFilesDefined = defined, BgaFilesExisting = existing },
                    baseline with { MovieFilesDefined = defined, MovieFilesExisting = existing }
                })
                {
                    Assert.AreEqual(completed, candidate.IsInformationChecked);
                }
            }
            foreach ((bool? defined, bool? existing, bool completed) in new (bool?, bool?, bool)[]
                { (false, null, true), (false, false, true), (false, true, true), (true, false, true), (true, true, true), (true, null, false), (null, null, false), (null, true, false), (null, false, false) })
            {
                ResourceHealthMaintenanceSnapshot baseline = Complete(1, 1) with { Origin = origin };
                foreach (ResourceHealthMaintenanceSnapshot candidate in new[]
                {
                    baseline with { StagefileDefined = defined, StagefileExisting = existing },
                    baseline with { BannerDefined = defined, BannerExisting = existing },
                    baseline with { BackbmpDefined = defined, BackbmpExisting = existing }
                })
                {
                    Assert.AreEqual(completed, candidate.IsInformationChecked);
                }
            }
            Assert.IsFalse((new ResourceHealthMaintenanceSnapshot { Origin = origin, Encoding = "shift_jis" }).IsInformationChecked);
        }
        using IDisposable culture = TestResourceInitializer.UseJapaneseCulture();
        ChartWarning warning = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(Complete(4, 1)).Single();
        Assert.AreEqual(string.Format(Resources.Warning_WavFilesNotFound, 25, 1, 4), warning.Message);
    }

    /// <summary>使用集合ごとに保守キャッシュを分離します。未定義IDを混ぜても定義先二件中一件の50%を保ち、未定義だけなら0件・N/A・不足警告なし、名前付き親参照は個別の未充足へ計数します。</summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_UsedSetsAndUndefinedIdsDoNotMixInCacheOrProjection()
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "present.wav"), "");
            ChartFile Parse(string used) => BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(Path.Combine(root, used + ".bms"),
                Encoding.ASCII.GetBytes("#WAV01 present.wav\n#WAV02 missing.wav\n#WAV03 ../unused.wav\n#00101:" + used + "\n#00104:99\n"), DateTime.UtcNow, used, used));
            var context = new ResourceHealthLookupContext(null);
            var service = new BmsLibraryMaintenanceService();
            ResourceHealthMaintenanceSnapshot first = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(Parse("01"), context);
            ResourceHealthMaintenanceSnapshot second = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(Parse("02"), context);
            Assert.AreEqual(1, first.WavFilesDefined);
            Assert.AreEqual(1, first.WavFilesExisting);
            Assert.AreEqual(0, second.WavFilesExisting);
            Assert.AreEqual(true, first.Lr2ResourceHasParentTraversal);
            ResourceHealthMaintenanceSnapshot mixed = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(Parse("010204"), context);
            Assert.AreEqual(2, mixed.WavFilesDefined);
            Assert.AreEqual(1, mixed.WavFilesExisting);
            Assert.AreEqual(50, mixed.GetWavHealth());
            Assert.AreEqual(0, mixed.BgaFilesDefined);
            Assert.AreEqual(ChartWarningKind.ResourceWavMissing, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(mixed).Single().Kind);
            ChartFile undefined = Parse("04");
            ResourceHealthMaintenanceSnapshot empty = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(undefined, context);
            Assert.AreEqual(0, empty.WavFilesDefined);
            Assert.AreEqual(0, empty.WavFilesExisting);
            Assert.IsNull(empty.GetWavHealth());
            Assert.IsTrue(empty.IsInformationChecked);
            ChartFile projected = ChartFileProjection.WithMaintenance(undefined, empty) with { Resources = null };
            Assert.AreEqual(0, service.BuildResourceHealthWarnings(projected).Count);
            Assert.IsFalse(ResourceHealthIndexSnapshot.Build([projected], service, 1).GetProjection(projected).HasIssues);
            ChartFile old = projected with { WAVHealth = 100 };
            ChartFile updated = ChartFileProjection.WithTransientState(old, ChartFileTransientState.FromResourceHealthSnapshot(empty));
            Assert.IsNull(updated.WAVHealth);
            Assert.IsNull(LibraryChartRow.FromChartFile(updated).WAVHealth);
            Assert.AreEqual(100, old.WAVHealth);
            Assert.AreEqual(1, BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(Parse("03"), context).WavFilesDefined);
            Assert.AreEqual(0, BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(Parse("03"), context).WavFilesExisting);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>BMSONの未定義BGAイベントは0件・N/Aとして警告を出さず、定義済み画像がある場合だけ実在する一件/二件の不足を表示します。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void BuildResourceHealthSnapshot_BmsonUndefinedEventsDoNotAffectDefinedImageHealth(bool definedImages)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "present.png"), [1]);
            string bga = definedImages
                ? "\"bga_header\":[{\"id\":1,\"name\":\"present.png\"},{\"id\":2,\"name\":\"missing.png\"}],\"bga_events\":[{\"id\":1},{\"id\":2},{\"id\":99}]"
                : "\"bga_events\":[{\"id\":99}],\"layer_events\":[{\"id\":98}],\"poor_events\":[{\"id\":97}]";
            ChartFile chart = BmsonChartFileParser.ParseSnapshot(new ChartFileSnapshot(Path.Combine(root, "chart.bmson"),
                Encoding.UTF8.GetBytes("{\"info\":{},\"bga\":{" + bga + "}}"), DateTime.UnixEpoch, "md5", "sha256"));
            ResourceHealthMaintenanceSnapshot health = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);
            Assert.AreEqual(0, health.WavFilesDefined);
            Assert.IsNull(health.GetWavHealth());
            Assert.AreEqual(definedImages ? 2 : 0, health.BgaFilesDefined);
            Assert.AreEqual(definedImages ? 1 : 0, health.BgaFilesExisting);
            Assert.AreEqual(definedImages ? (int?)50 : null, health.GetBgaHealth());
            IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(health);
            Assert.AreEqual(definedImages ? 1 : 0, warnings.Count);
            if (definedImages) { Assert.AreEqual(ChartWarningKind.ResourceBgaMissing, warnings.Single().Kind); }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// 音声定義二件のうち一件が不足する保守値から不足警告を一件作り、欠落件数に対応する警告生成の見逃しを検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthWarnings_MissingAudioProducesResourceWarning()
    {

        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1 };

        IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info);

        Assert.AreEqual(1, warnings.Count);
        Assert.AreEqual(ChartWarningKind.ResourceWavMissing, warnings[0].Kind);
    }

    /// <summary>
    /// 未評価の保守値から不足警告や資源索引の有効・除外対象を作らず、未計算を成功した健全性結果へ混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void LazyMaintenancePlaceholder_IsNotAValidResourceHealthSnapshot()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        _ = file.ResourceHealthMaintenanceSnapshot;
        ChartFile chart = (file);
        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(chart);
        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 1);

        Assert.AreEqual(0, warnings.Count);
        Assert.AreEqual(0, snapshot.ActiveTargets.Count);
        Assert.AreEqual(0, snapshot.IgnoredTargets.Count);
    }

    /// <summary>
    /// BMSの資源パスからLR2互換の警告bits・CP932長・親参照有無を保守値へ取り込み、Bmsonでは互換値を持たせず、形式を越えた互換判定を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_PersistsLr2CompatibilityFactsForBmsOnly()
    {
        ChartFile bms = BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(@"D:\BMS\Pack\Song\chart.bms",
            Encoding.ASCII.GetBytes("#WAV01 sound.wav\n#00111:01\n"), DateTime.UnixEpoch, "0123456789abcdef0123456789abcdef", "sha256"));
        ChartFile bmsChart = (bms);

        ResourceHealthMaintenanceSnapshot bmsInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bmsChart);

        Assert.AreEqual((int)Lr2CompatibilityWarningFlags.None, bmsInfo.Lr2WarningFlags);
        Assert.AreEqual(Lr2SongFolderParentNormalizer.ComputeDirectoryHash(@"D:\BMS\Pack\Song"), Lr2CompatibilityEvaluator.EvaluateChartPath(bms.Path).FolderHash);
        Assert.AreEqual(Encoding.GetEncoding("shift_jis").GetByteCount("sound.wav"), bmsInfo.Lr2ResourceMaxRelativeCp932Bytes);
        Assert.AreEqual(false, bmsInfo.Lr2ResourceHasParentTraversal);

        ChartFile bmson = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"D:\BMS\Pack\Song\chart.bmson",
            Md5 = "fedcba9876543210fedcba9876543210",
            Resources = TestChartResources.Create(["sound.wav"], [])
        };
        ChartFile bmsonChart = (bmson);

        ResourceHealthMaintenanceSnapshot bmsonInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(bmsonChart);

        Assert.IsNull(bmsonInfo.Lr2WarningFlags);
        Assert.IsNull(bmsonInfo.Lr2ResourceMaxRelativeCp932Bytes);
        Assert.IsNull(bmsonInfo.Lr2ResourceHasParentTraversal);
    }

    /// <summary>
    /// 保存されたLR2のパス・資源互換bitsを対応する四警告と要約へ投影し、bitの取り違えや警告欠落を検出する。
    /// </summary>
    [TestMethod]
    public void SetMaintenanceInfo_ProjectsLr2CompatibilityWarningsFromFacts()
    {

        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new ResourceHealthMaintenanceSnapshot
        {
            Origin = MaintenanceInfoOrigin.Calculated,
            Path = file.Path,
            Hash = file.Md5,
            Lr2WarningFlags = (int)(Lr2CompatibilityWarningFlags.PathEncodingUnsupported
                | Lr2CompatibilityWarningFlags.PathTooLong
                | Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported
                | Lr2CompatibilityWarningFlags.ResourcePathTooLong)
        };

        file = ChartFileProjection.WithMaintenance(file, info);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathTooLong));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathUnsupported));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathTooLong));
        Assert.AreEqual("[4] LR2パス非対応, LR2パス長超過, LR2リソース非対応, LR2リソースパス長超過", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    /// <summary>
    /// LR2互換判定済みで警告なしなら古い非対応警告と要約を消し、評価済み正常値の反映漏れを検出する。
    /// </summary>
    [TestMethod]
    public void SetMaintenanceInfo_ClearsLr2CompatibilityWarningsWhenEvaluatedClean()
    {

        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported)] };
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Lr2WarningFlags = (int)Lr2CompatibilityWarningFlags.None };

        file = ChartFileProjection.WithMaintenance(file, info);

        Assert.IsFalse(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.AreEqual(string.Empty, ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    /// <summary>
    /// LR2互換事実が未取得なら既存警告を保持し、判定不能を正常へ読み替える解除を検出する。
    /// </summary>
    [TestMethod]
    public void SetMaintenanceInfo_DoesNotClearLr2CompatibilityWarningsWithoutFacts()
    {

        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported, Resources.Warning_Lr2PathEncodingUnsupported)] };
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5 };

        file = ChartFileProjection.WithMaintenance(file, info);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathEncodingUnsupported));
        Assert.AreEqual("[1] LR2パス非対応", ChartWarningCollection.BuildDigestText(file.Warnings, file.InstallDestination));
    }

    /// <summary>計算済み件数を実DBから読み戻して同値再スキャンし、DB書込み・不要な表示更新を発生させません。未定義IDだけの0件も同じ保存値を保ちます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UpdateMaintenanceInfo_ForceUpdateDoesNotPersistUnchangedHydratedValues(bool undefined)
    {
        WithTemporarySongDb(songDbPath =>
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException("Song DB has no parent directory."), "chart.bms");
            File.WriteAllText(chartPath, "#TITLE test\r\n#WAV01 missing.wav\r\n#00101:" + (undefined ? "02" : "01") + "\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult first =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(file, snapshot, lookupContext: null, forceUpdate: true);
            using (var db = new LR2SongDBExtended(songDbPath))
            {
                db.InsertOrReplace(MaintenanceStorageMapping.ToStorage(first.MaintenanceInfo), typeof(LR2SongDBExtended.maintenance));
            }
            ResourceHealthMaintenanceSnapshot hydrated;
            using (LR2SongDBExtended db = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly())
            {
                hydrated = MaintenanceStorageMapping.ToCommon(db.Table<LR2SongDBExtended.maintenance>().Single(), MaintenanceInfoOrigin.DbHydrated);
            }
            Assert.IsTrue(hydrated.IsInformationChecked);
            ChartFile reloaded = ChartFileProjection.WithMaintenance(file with { Resources = null }, hydrated);
            BmsLibraryMaintenanceService.MaintenanceEvaluationResult second =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(reloaded, snapshot, lookupContext: null, forceUpdate: true);
            Assert.IsTrue(first.MaintenanceInfoChanged);
            Assert.IsFalse(second.MaintenanceInfoChanged);
            Assert.IsTrue(second.MaintenanceInfo.IsInformationChecked);

            int writes = 0;
            MaintenanceWorkflowResult result = new BmsLibraryMaintenanceService().UpdateMaintenanceInfo([reloaded], forceUpdate: true,
                request =>
                {
                    writes++;
                    return CreateDurableWriter(songDbPath)(request);
                }, null);
            Assert.AreEqual(1, result.CheckedFileCount);
            Assert.AreEqual(0, writes);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            Assert.IsFalse(result.HasUpdates);
        });
    }

    /// <summary>
    /// 資源不足を無視済みの譜面を強制保守すると無視を解除して変更ありを返し、明示再評価で古い無視が残る誤りを検出する。
    /// </summary>
    [TestMethod]
    public void EvaluateMaintenanceForInline_ForceUpdateReportsChangedWhenIgnoredFlagIsReset()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#WAV01 missing.wav\r\n#00101:01\r\n", Encoding.ASCII);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);
            file = BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(file, snapshot, lookupContext: null, forceUpdate: true).CurrentChart;
            file = ChartFileProjection.WithMaintenance(file, file.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = true });

            BmsLibraryMaintenanceService.MaintenanceEvaluationResult result =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(file, snapshot, lookupContext: null, forceUpdate: true);

            Assert.IsTrue(result.MaintenanceInfoChanged);
            Assert.IsFalse(result.MaintenanceInfo.FilesWarningIgnored);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実譜面の古い不足参照より現在譜面へ適用済みの参照を使い、索引の存在件数を反映して、保存済み参照の再上書きを検出する。
    /// </summary>
    [TestMethod]
    public void EvaluateMaintenanceForInline_UsesAlreadyAppliedResourceReferences()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bms");
        try
        {
            File.WriteAllText(chartPath, "#TITLE test\r\n#WAV01 missing.wav\r\n#00101:01\r\n", Encoding.ASCII);
            File.WriteAllText(Path.Combine(tempDirectoryPath, "hit.wav"), string.Empty);
            ChartFileSnapshot snapshot = ChartFileContentReader.ReadSnapshot(chartPath);
            ChartFile file = BmsChartFileParser.ParseSnapshot(snapshot);
            file = file with { Resources = TestChartResources.Create(["hit.wav"]) };
            var lookupCache = new DirectoryResourceLookupCache();
            lookupCache.AddDir(tempDirectoryPath, ["hit.wav"]);
            var lookupContext = new ResourceHealthLookupContext(lookupCache);

            BmsLibraryMaintenanceService.MaintenanceEvaluationResult result =
                BmsLibraryMaintenanceService.EvaluateMaintenanceForInline(
                    file,
                    snapshot,
                    lookupContext,
                    forceUpdate: true);

            Assert.AreEqual(1, result.MaintenanceInfo.WavFilesDefined);
            Assert.AreEqual(1, result.MaintenanceInfo.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 文字コードだけのBmson保存行は未評価としてパスとハッシュを保持し、資源件数を未取得のまま返して、未計算を健全性評価済みへ混同する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void PendingBmsonEncodingOnlyMaintenance_IsPlaceholder()
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\song.bmson",
            Md5 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            Sha256 = new string('c', 64)
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" }) };

        ChartFile chart = (song);
        ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.GetResourceHealthSnapshot(chart);

        Assert.IsNotNull(info);
        Assert.AreEqual(song.Md5, info.Hash);
        Assert.AreEqual(song.Path, info.Path);
        Assert.IsFalse(song.ResourceHealthMaintenanceSnapshot.IsInformationChecked);
        Assert.IsFalse(info.IsInformationChecked);
        Assert.IsNull(info.WavFilesDefined);
        Assert.IsNull(info.BgaFilesDefined);
        Assert.IsNull(info.MovieFilesDefined);
        Assert.IsNull(chart.Resources);
    }

    /// <summary>
    /// 共通Bmsonの音声・画像・動画・stagefile参照から定義と不足件数を求め、種類別の参照欠落を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_ProjectionOnlyBmsonUsesChartResources()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string chartPath = Path.Combine(tempDirectoryPath, "chart.bmson");
        try
        {
            var chart = new ChartFile(kind: ChartFileKind.Bmson, path: chartPath, md5: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", sha256: new string('c', 64), title: "Title", rawTitle: "Title", artist: "Artist", genre: string.Empty, folder: "Folder", tag: string.Empty, levelText: string.Empty, level: null, mode: null, chartInfo: null, resources: TestChartResources.Create(["missing.wav"], ["missing.png", "missing.mp4"], "stage.png"), stagefile: "stage.png");

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart);

            Assert.IsNotNull(info);
            Assert.AreEqual("utf-8", info.Encoding);
            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
            Assert.AreEqual(1, info.BgaFilesDefined);
            Assert.AreEqual(0, info.BgaFilesExisting);
            Assert.AreEqual(1, info.MovieFilesDefined);
            Assert.AreEqual(0, info.MovieFilesExisting);
            Assert.AreEqual(true, info.StagefileDefined);
            Assert.AreEqual(false, info.StagefileExisting);
        }
        finally
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
    }

    /// <summary>実BMSONの3役割で非空不正/親参照を定義あり・既存なしと対応する不足警告へ投影します。同一contextで対象外→無効→対象外を評価し、未指定/null/空とのキャッシュ混同を検出します。</summary>
    [TestMethod]
    [DataRow("eyecatch_image")]
    [DataRow("banner_image")]
    [DataRow("back_image")]
    public void BuildResourceHealthSnapshot_BmsonOptionalImagesDistinguishAbsentAndInvalidReferences(string field)
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(directory, [], [], []);
            var context = new ResourceHealthLookupContext(cache);
            // null要素はプロパティ省略、文字列nullはJSONの明示null。NULは有効JSONのエスケープで渡します。
            foreach (string? value in new string?[] { null, "null", "\"\"", "\"../image.png\"", "\"bad\\u0000.png\"", "\"\"", "null", null })
            {
                bool defined = value is not (null or "null" or "\"\"");
                string json = "{\"info\":{" + (value == null ? "" : "\"" + field + "\":" + value) + "},\"sound_channels\":[]}";
                ChartFile chart = BmsonChartFileParser.ParseSnapshot(new ChartFileSnapshot(Path.Combine(directory, "chart.bmson"),
                    Encoding.UTF8.GetBytes(json), DateTime.UtcNow, "md5", "sha256"));
                ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(chart, context);
                Assert.AreEqual(defined && field == "eyecatch_image", info.StagefileDefined);
                Assert.AreEqual(defined && field == "banner_image", info.BannerDefined);
                Assert.AreEqual(defined && field == "back_image", info.BackbmpDefined);
                bool? existing = field switch
                {
                    "eyecatch_image" => info.StagefileExisting,
                    "banner_image" => info.BannerExisting,
                    _ => info.BackbmpExisting
                };
                Assert.AreEqual(defined ? false : (bool?)null, existing);
                IReadOnlyList<ChartWarning> warnings = BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info);
                Assert.AreEqual(defined ? 1 : 0, warnings.Count);
                if (defined)
                {
                    ChartWarningKind expected = field switch
                    {
                        "eyecatch_image" => ChartWarningKind.ResourceStagefileMissing,
                        "banner_image" => ChartWarningKind.ResourceBannerMissing,
                        _ => ChartWarningKind.ResourceBackbmpMissing
                    };
                    Assert.AreEqual(expected, warnings.Single().Kind);
                }
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// 同じ画像名をstagefileとbannerへ入れ替えた二譜面は役割ごとに存在判定を返し、役割を捨てたキャッシュ共有を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_ResourceSetCacheKeepsOptionalImageRolesDistinct()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [],
                [ChartResourceKeyHash.GetLookupHash("stage")],
                []);
            var lookupContext = new ResourceHealthLookupContext(cache);
            ChartFile first = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "first.bms"),
                md5: "11111111111111111111111111111111",
                stagefile: "stage.png",
                banner: "missing.png");
            ChartFile second = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "second.bms"),
                md5: "22222222222222222222222222222222",
                stagefile: "missing.png",
                banner: "stage.png");

            ResourceHealthMaintenanceSnapshot firstInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(first, lookupContext);
            ResourceHealthMaintenanceSnapshot secondInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(second, lookupContext);

            Assert.AreEqual(true, firstInfo.StagefileDefined);
            Assert.AreEqual(true, firstInfo.StagefileExisting);
            Assert.AreEqual(true, firstInfo.BannerDefined);
            Assert.AreEqual(false, firstInfo.BannerExisting);
            Assert.AreEqual(true, secondInfo.StagefileDefined);
            Assert.AreEqual(false, secondInfo.StagefileExisting);
            Assert.AreEqual(true, secondInfo.BannerDefined);
            Assert.AreEqual(true, secondInfo.BannerExisting);
            Assert.AreEqual(2, lookupContext.ResourceHealthSetCacheEntryCount);
        }
        finally
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
    }

    /// <summary>
    /// lookup hashが衝突する既存・不足資源を実ファイルで区別し、二参照集合の定義・存在件数を別結果に保って衝突によるキャッシュ混同を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_ResourceSetCacheDoesNotMergeHashCollisions()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        try
        {
            const string ExistingResource = "res_xf2a7hbc7_";
            const string MissingResource = "res_0ltou99kgv";
            Assert.AreEqual(ChartResourceKeyHash.GetLookupHash(ExistingResource), ChartResourceKeyHash.GetLookupHash(MissingResource));
            File.WriteAllText(Path.Combine(tempDirectoryPath, ExistingResource + ".wav"), string.Empty);

            var lookupContext = new ResourceHealthLookupContext(null);
            ChartFile first = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "first.bms"),
                md5: "11111111111111111111111111111111",
                audioResourcePaths: [ExistingResource + ".wav"]);
            ChartFile second = CreateProjectionOnlyBmsChart(
                Path.Combine(tempDirectoryPath, "second.bms"),
                md5: "22222222222222222222222222222222",
                audioResourcePaths: [ExistingResource + ".wav", MissingResource + ".wav"]);

            ResourceHealthMaintenanceSnapshot firstInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(first, lookupContext);
            ResourceHealthMaintenanceSnapshot secondInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(second, lookupContext);

            Assert.AreEqual(1, firstInfo.WavFilesDefined);
            Assert.AreEqual(1, firstInfo.WavFilesExisting);
            Assert.AreEqual(2, secondInfo.WavFilesDefined);
            Assert.AreEqual(1, secondInfo.WavFilesExisting);
            Assert.AreEqual(2, lookupContext.ResourceHealthSetCacheEntryCount);
        }
        finally
        {
            Directory.Delete(tempDirectoryPath, recursive: true);
        }
    }

    /// <summary>
    /// 保守未取得のBmsonを無視指定すると変更結果へ無視状態を持たせ、未取得を理由に無視要求を落とす誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonPlaceholderAttachesMaintenanceInfoToSourceSong()
    {
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\song.bmson",
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('d', 64)
        };

        ChartFile chart = (song);
        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    /// <summary>
    /// 文字コードだけのBmsonに無視を指定すると現在参照の定義・不足件数も求め、無視状態とともに変更結果へ反映して未評価のまま保存する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonAttachesComputedMaintenanceInfoToSourceSong()
    {
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = "dddddddddddddddddddddddddddddddd",
            Sha256 = new string('e', 64),
            Resources = TestChartResources.Create(["missing.wav"])
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" }) };
        ChartFile chart = (song);

        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(1, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesDefined);
        Assert.AreEqual(0, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    /// <summary>
    /// 古いハッシュの保守値に無視指定すると現在ハッシュと不足件数へ再評価し、古い健全性値の誤再利用を検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_ChartBmsonReplacesStaleMaintenanceHash()
    {
        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee",
            Sha256 = new string('f', 64),
            Resources = TestChartResources.Create(["missing.wav"])
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = "ffffffffffffffffffffffffffffffff", encoding = "utf-8" }) };
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 1 });
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 1 });
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = true });
        ChartFile chart = (song);

        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([chart], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual(song.Md5, changes[0].ResourceHealthMaintenanceSnapshot.Hash);
        Assert.AreEqual(1, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesDefined);
        Assert.AreEqual(0, changes[0].ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        Assert.IsTrue(changes[0].ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    /// <summary>
    /// 譜面ルートの資源参照は子フォルダの同名資源と照合せず、不足を返してファイル名だけの誤一致を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_RootReferenceDoesNotMatchNestedResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash(@"sound\foo")],
                [],
                []);

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 子フォルダの参照はルートの同名資源と照合せず、不足を返して相対パスを捨てた誤一致を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_NestedReferenceDoesNotMatchRootResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 sound\\foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash("foo")],
                [],
                []);

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// ルートの相対資源参照を同じ位置の索引キーへ照合し、存在件数を返して正しい相対参照の見落としを検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_RootReferenceMatchesRootRelativeResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(
                tempDirectoryPath,
                [ChartResourceKeyHash.GetLookupHash("foo")],
                [],
                []);

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, new ResourceHealthLookupContext(cache), forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(1, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 索引がない実FS照合でもルート参照を子フォルダの同名ファイルへ代替せず、誤った存在扱いを検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_WithoutLookupContextRootReferenceDoesNotMatchNestedResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        string soundDirectoryPath = Path.Combine(tempDirectoryPath, "sound");
        Directory.CreateDirectory(soundDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(Path.Combine(soundDirectoryPath, "foo.wav"), string.Empty);
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, null, forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 索引がない実FS照合でも子フォルダ参照をルートの同名ファイルへ代替せず、誤った存在扱いを検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_WithoutLookupContextNestedReferenceDoesNotMatchRootResource()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#WAV01 sound\\foo.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(Path.Combine(tempDirectoryPath, "foo.wav"), string.Empty);
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, null, forceUpdate: true);

            Assert.AreEqual(1, info.WavFilesDefined);
            Assert.AreEqual(0, info.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 参照未取得のBMS実入力が消失したら保守結果をnullとして返し、読取り失敗を資源ゼロの成功へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_MissingBmsFileReturnsNull()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 hit.wav\r\n"
            + "#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            File.Delete(bmsFilePath);
            file = file with { Resources = null };

            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, null, forceUpdate: true);

            Assert.IsNull(info);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 一覧行の現在健全性投影は古い資源警告を置換して重複警告を保ち、導入先がある場合は要約の資源警告だけを抑えることで、警告の混線を検出する。
    /// </summary>
    [TestMethod]
    public void LibraryChartRow_ProjectsResourceHealthWarningsWithoutMutatingSource()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart, Resources.Warning_DuplicateBmsFile)] };
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "stale resource warning")] };
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, BgaFilesDefined = 4, BgaFilesExisting = 3 };
        file = ChartFileProjection.WithMaintenance(file, info);
        var row = LibraryChartRow.FromChartFile(file);
        row.SetResourceHealthProjectionProvider(_ => new ResourceHealthWarningProjection(1, BmsLibraryMaintenanceService.BuildResourceHealthWarnings(info), isIgnored: false));

        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_ResourceMissing);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
        Assert.IsFalse(row.WarningTooltipText.Contains("stale resource warning"));

        row.SetChartTransientStateProvider((chart, _) => ChartFileTransientState.FromInstallDestinationState(
            ChartFileProjection.WithPackageState(
                chart,
                @"C:\Installed",
                string.Empty,
                string.Empty,
                []),
            forceInstallDestinationProjection: true));
        Assert.IsFalse(row.WarningDigestText.Contains(Resources.WarningDigest_ResourceMissing));
        StringAssert.Contains(row.WarningDigestText, Resources.WarningDigest_DuplicateChart);
        StringAssert.Contains(row.WarningTooltipText, "BGA");
    }

    /// <summary>
    /// 正常な現在資源投影を一覧行へ渡すと古い不足警告を要約・tooltipから除き、旧警告の表示残存を検出する。
    /// </summary>
    [TestMethod]
    public void LibraryChartRow_ResourceProjectionSuppressesStaleSourceResourceWarnings()
    {

        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Warnings = [.. file.Warnings.Where(warning => warning.Kind != ChartWarningKind.ResourceWavMissing), ChartWarning.Create(ChartWarningKind.ResourceWavMissing, "pending resource warning")] };
        file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 2 });
        var row = LibraryChartRow.FromChartFile(file);
        row.SetResourceHealthProjectionProvider(_ => ResourceHealthWarningProjection.Empty);

        Assert.AreEqual(string.Empty, row.WarningDigestText);
        Assert.IsFalse(row.WarningTooltipText.Contains("pending resource warning"));
    }

    /// <summary>
    /// 不足のある所持譜面を無視指定の有無で有効・除外対象へ分け、対象ごとの投影も一致させて無視状態の混線を検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_GroupsActiveAndIgnoredWithoutMutatingWarnings()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active = active with { Path = @"C:\Library\active.bms" };
        active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
        ChartFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored = ignored with { Path = @"C:\Library\ignored.bms" };
        ignored = ChartFileProjection.WithMaintenance(ignored, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = ignored.Path, Hash = ignored.Md5, BgaFilesDefined = 2, BgaFilesExisting = 1, FilesWarningIgnored = true });

        ChartFile activeChart = (active);
        ChartFile ignoredChart = (ignored);
        var snapshot = ResourceHealthIndexSnapshot.Build([activeChart, ignoredChart], service, version: 3);

        CollectionAssert.AreEqual(new[] { activeChart.Path }, snapshot.ActiveTargets.Select(reference => reference.Path).ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, snapshot.IgnoredTargets.Select(reference => reference.Path).ToArray());
        Assert.IsTrue(snapshot.GetProjection(activeChart).HasIssues);
        Assert.IsFalse(snapshot.GetProjection(activeChart).IsIgnored);
        Assert.IsTrue(snapshot.GetProjection(ignoredChart).IsIgnored);
    }

    /// <summary>
    /// LR2互換警告だけの譜面は資源不足の有効・除外対象へ含めず、異なる警告責務の混同を検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_DoesNotTreatLr2CompatibilityWarningsAsResourceTargets()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Path = @"C:\Library\active.bms" };
        file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
        {
            Origin = MaintenanceInfoOrigin.Calculated,
            Path = file.Path,
            Hash = file.Md5,
            Lr2WarningFlags = (int)(Lr2CompatibilityWarningFlags.PathTooLong
                | Lr2CompatibilityWarningFlags.ResourcePathEncodingUnsupported)
        });
        ChartFile chart = (file);

        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 4);

        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2PathTooLong));
        Assert.IsTrue(file.Warnings.Any(warning => warning.Kind == ChartWarningKind.Lr2ResourcePathUnsupported));
        Assert.AreEqual(0, snapshot.ActiveTargets.Count);
        Assert.AreEqual(0, snapshot.IgnoredTargets.Count);
        Assert.IsFalse(snapshot.GetProjection(chart).HasIssues);
    }

    /// <summary>
    /// 共通Bmsonの保存済み音声不足を資源索引の有効対象と警告投影へ反映し、形式片側の対象欠落を検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_BuildsBmsonTargetFromChartFile()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = @"C:\Library\chart.bmson",
            Md5 = "cccccccccccccccccccccccccccccccc",
            Sha256 = new string('c', 64)
        };
        song = song with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" }) };
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 3 });
        song = ChartFileProjection.WithMaintenance(song, song.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 1 });
        ChartFile chart = (song);

        var snapshot = ResourceHealthIndexSnapshot.Build([chart], service, version: 5);

        CollectionAssert.AreEqual(new[] { chart.Path }, snapshot.ActiveTargets.Select(reference => reference.Path).ToArray());
        Assert.IsTrue(snapshot.GetProjection(chart).HasIssues);
        Assert.IsTrue(snapshot.GetProjection(chart).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
    }

    /// <summary>
    /// 現在の所持健全性索引から不足譜面の実パスを返し、保守対象解決が現在索引から外れる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_UsesCurrentResourceHealthIndexSnapshot()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            active = active with { Path = @"C:\Library\active.bms" };
            active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1 });
            ChartFile activeChart = (active);
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [active],
                BmsonCharts = []
            };
            EnsureCurrentResourceHealthIndex(library);

            List<ChartFile> result = library.GetChartsNeedResourceFix(null);

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(activeChart.Path, result[0].Path);
        });
    }

    /// <summary>
    /// 所持hashだけを温めたcold健全性読取りは実保存行writerの保持中にreader待機へ入り、解放後に不足対象を返して保護なしの読取りを検出する。
    /// </summary>
    /// <remarks>実所持の保存行writerとreader待機を観測する。10秒は保護中の到達観測の期限で、通常完了の固定待機ではない。</remarks>
    [TestMethod]
    public void ChartFilesNeedResourceFix_WaitsForCatalogStorageWriterBeforeColdResourceHealthRead()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "missing-resource.bms");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = chartPath };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 1, WavFilesExisting = 0, FilesWarningIgnored = false });
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [file],
                BmsonCharts = []
            };

            // 所持 collection だけを通常の hash 読取りで温め、resource health は cold のままにする。
            _ = library.GetOwnedChartHashIndexSnapshot();

            ReaderWriterLockSlimWrapper storageWriteGate = RegularChartListOwnerTestSupport.GetCatalogStorageRowsWriteGate(library);
            List<ChartFile>? result = null;
            Task worker = null!;
            try
            {
                using (storageWriteGate.GetWriterGuard())
                {
                    worker = RegularChartListOwnerTestSupport.StartLongRunning(
                        () => result = [.. library.ChartFilesNeedResourceFix]);
                    bool readerWaitOrEarlyCompletion = SpinWait.SpinUntil(
                        () => storageWriteGate.WaitingReadCount > 0 || worker.IsCompleted,
                        TimeSpan.FromSeconds(10));

                    Assert.IsTrue(
                        readerWaitOrEarlyCompletion,
                        "resource health cold read did not reach the storage reader or complete within the watchdog.");
                    Assert.IsFalse(
                        worker.IsCompleted,
                        "resource health cold read completed while the catalog storage writer was held.");
                }

                worker.GetAwaiter().GetResult();
                Assert.IsNotNull(result);
                Assert.AreEqual(1, result!.Count);
                Assert.AreEqual(ChartFileKind.Bms, result[0].Kind);
                Assert.AreEqual(chartPath, result[0].Path);
            }
            finally
            {
                if (worker != null)
                {
                    worker.GetAwaiter().GetResult();
                }
            }
        });
    }
    /// <summary>
    /// 同一MD5でも大小文字だけ異なる正本パスの不足投影を別に保持し、MD5は大小文字を区別せずパスは完全一致で解決して誤った対象共有を検出する。
    /// </summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_CaseOnlyExactPathsKeepIndependentResourceHealthProjections()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            const string sharedHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            ChartFile upperCasePath = CreateFile(sharedHash);
            upperCasePath = upperCasePath with { Path = @"C:\Library\Case.bms" };
            upperCasePath = ChartFileProjection.WithMaintenance(upperCasePath, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperCasePath.Path, Hash = sharedHash, WavFilesDefined = 2, WavFilesExisting = 1 });
            ChartFile lowerCasePath = CreateFile(sharedHash);
            lowerCasePath = lowerCasePath with { Path = @"c:\library\case.bms" };
            lowerCasePath = ChartFileProjection.WithMaintenance(lowerCasePath, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerCasePath.Path, Hash = sharedHash, BgaFilesDefined = 3, BgaFilesExisting = 1 });
            var library = new TestBmsLibrary(songDbPath)
            {
                BmsCharts = [upperCasePath, lowerCasePath],
                BmsonCharts = []
            };

            ResourceHealthIndexSnapshot snapshot = library.GetResourceHealthIndexSnapshotForView("r2c_exact_full");
            List<ChartFile> result = library.GetChartsNeedResourceFix(null);
            ResourceHealthWarningProjection upperProjection = snapshot.GetProjection(
                ChartFileKind.Bms,
                upperCasePath.Path,
                sharedHash);
            ResourceHealthWarningProjection lowerProjection = snapshot.GetProjection(
                ChartFileKind.Bms,
                lowerCasePath.Path,
                sharedHash);

            Assert.AreEqual(2, snapshot.TargetCount);
            Assert.IsTrue(new HashSet<string>(result.Select(chart => chart.Path), StringComparer.Ordinal).SetEquals(
                [upperCasePath.Path, lowerCasePath.Path]));
            Assert.IsTrue(upperProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(upperProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsTrue(lowerProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
            Assert.IsFalse(lowerProjection.Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsTrue(snapshot.GetProjection(
                ChartFileKind.Bms,
                upperCasePath.Path,
                sharedHash.ToUpperInvariant()).Warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
            Assert.IsFalse(snapshot.GetProjection(
                ChartFileKind.Bms,
                @"C:\Library\CASE.bms",
                sharedHash).HasIssues);
        });
    }

    /// <summary>
    /// 実選択保守の保存値を同じ所持現在値へ反映して警告・保守表示を一回通知し、保守markerだけなら集合版とSourceChangedを進めず、不要な全体更新を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    [TestMethod]
    public void RescanResourceHealthCharts_PublishesMaintenanceRefreshThroughOwnedDispatcher()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string chartPath = Path.Combine(Path.GetDirectoryName(songDbPath)!, "chart.bms");
            File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE maintenance\r\n", Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot
            {
                Origin = MaintenanceInfoOrigin.Calculated,
                Path = file.Path,
                Hash = file.Md5,
                Encoding = "unknown"
            });
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int ownedVersion = library.CatalogRevision;
            int refreshNotificationChanged = 0;
            System.ComponentModel.PropertyChangedEventHandler handler = delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.PropertyChanged += handler;
            try
            {
                MaintenanceWorkflowResult result = library.RescanResourceHealthCharts([file]);
                NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
                Assert.IsTrue(result.HasUpdates);
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
                Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
                Assert.AreEqual(1, refreshNotificationChanged);
                Assert.AreEqual(ownedVersion + 1, library.CatalogRevision);
                Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                Assert.AreEqual("unknown", file.ResourceHealthMaintenanceSnapshot.Encoding);
                Assert.AreEqual("shift_jis", OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library).Collection
                    .ResolveCurrentChart(LibraryChartRef.FromChartFile(file)).ResourceHealthMaintenanceSnapshot.Encoding);
                ChartFile current = GetOwnedCollectionOwner(library).Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(file));
                ResourceHealthIndexSnapshot index = library.GetResourceHealthIndexSnapshotForView("same-maintenance-before-force");
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    int beforeRepeat = library.NormalLibraryRefreshNotificationVersion;
                    MaintenanceWorkflowResult unchanged = library.RescanResourceHealthCharts([current]);
                    Assert.IsFalse(unchanged.HasUpdates);
                    Assert.AreEqual(0, unchanged.MaintenanceInfoUpsertCount);
                    Assert.AreEqual(0, unchanged.SongMetadataUpdateCount);
                    Assert.AreSame(index, library.TryGetCurrentResourceHealthIndexSnapshotForView());
                    NormalLibraryRefreshNotificationBatch noChange = library.GetNormalLibraryRefreshNotificationsAfter(beforeRepeat);
                    Assert.IsFalse(noChange.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
                    Assert.IsFalse(noChange.HasEffect(LibraryChartRefreshEffects.SourceChanged));
                    Assert.AreEqual(ownedVersion + 1, library.CatalogRevision);
                    Assert.AreSame(current.Token, GetOwnedCollectionOwner(library).Collection.ResolveCurrentChart(LibraryChartRef.FromChartFile(current)).Token);
                }
                Assert.AreSame(index, library.GetResourceHealthIndexSnapshotForView("same-maintenance-after-force"));
            }
            finally
            {
                library.PropertyChanged -= handler;
                library.RequestShutdown("maintenance_marker_only_test");
            }
        });
    }

    /// <summary>
    /// 背景16・128件がある二対象の資源追加を順に局所保守し、取得済みsnapshotを保って現在投影だけ更新し、全件警告走査やSourceChangedを起こす誤りを検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    // 16件と128件の背景を使い、二対象の更新を全件列挙で代用する反例を見分ける。
    [DataTestMethod]
    [DataRow(16)]
    [DataRow(128)]
    public void RescanResourceHealthCharts_UpdatesCurrentResourceHealthIndexByDelta(int backgroundCount)
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string firstPath = Path.Combine(root, "target-first.bms");
            string secondPath = Path.Combine(root, "target-second.bms");
            string firstResourcePath = Path.Combine(root, "first.wav");
            string secondResourcePath = Path.Combine(root, "second.wav");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE target first\r\n#WAV01 first.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE target second\r\n#WAV01 second.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile first = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(firstPath));
            ChartFile second = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(secondPath));
            first = ChartFileProjection.WithMaintenance(first, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = first.Path, Hash = first.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
            second = ChartFileProjection.WithMaintenance(second, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = second.Path, Hash = second.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
            ChartFile firstChart = (first);
            ChartFile secondChart = (second);
            List<ChartFile> files = [first, second];
            for (int index = 0; index < backgroundCount; index++)
            {
                ChartFile background = CreateFile(
                    index.ToString("x8") + new string('0', 24));
                background = background with { Path = Path.Combine(root, "background-" + index + ".bms") };
                background = ChartFileProjection.WithMaintenance(background, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = background.Path, Hash = background.Md5, WavFilesDefined = 1, WavFilesExisting = 0 });
                files.Add(background);
            }
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, files, []);
            EnsureCurrentResourceHealthIndex(library);
            List<string> storeWork = [];
            ResourceHealthIndexSnapshot beforeFirst = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            beforeFirst.StoreWorkObserver = operation => storeWork.Add(operation);
            int initialTargetCount = beforeFirst.TargetCount;
            int ownedVersion = library.CatalogRevision;
            Assert.AreEqual(backgroundCount + 2, initialTargetCount);
            Assert.IsTrue(beforeFirst.GetProjection(firstChart).HasIssues);
            Assert.IsTrue(beforeFirst.GetProjection(secondChart).HasIssues);

            File.WriteAllBytes(firstResourcePath, [1, 2, 3]);
            int firstNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            MaintenanceWorkflowResult firstResult = library.RescanResourceHealthCharts([firstChart]);
            ResourceHealthIndexSnapshot afterFirst = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            ResourceHealthIndexSnapshot firstGetter = library.GetResourceHealthIndexSnapshotForView("test_rescan_first_getter");
            List<ChartFile> firstFixTargets = library.GetChartsNeedResourceFix([firstChart]);
            NormalLibraryRefreshNotificationBatch firstBatch = library.GetNormalLibraryRefreshNotificationsAfter(firstNotificationVersion);

            Assert.IsTrue(firstResult.HasUpdates);
            Assert.AreEqual(initialTargetCount, afterFirst.TargetCount);
            Assert.IsFalse(afterFirst.GetProjection(firstChart).HasIssues);
            Assert.IsTrue(afterFirst.GetProjection(secondChart).HasIssues);
            Assert.AreEqual(afterFirst.Version, firstGetter.Version);
            Assert.IsFalse(firstGetter.GetProjection(firstChart).HasIssues);
            Assert.AreEqual(0, firstFixTargets.Count);
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(firstBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(ownedVersion + 1, library.CatalogRevision);
            Assert.IsFalse(firstBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(beforeFirst.GetProjection(firstChart).HasIssues);

            beforeFirst.StoreWorkObserver = null;
            afterFirst.StoreWorkObserver = operation => storeWork.Add(operation);
            File.WriteAllBytes(secondResourcePath, [4, 5, 6]);
            int secondNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            MaintenanceWorkflowResult secondResult = library.RescanResourceHealthCharts([secondChart]);
            ResourceHealthIndexSnapshot afterSecond = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            ResourceHealthIndexSnapshot thirdGetter = library.GetResourceHealthIndexSnapshotForView("test_rescan_second_getter");
            List<ChartFile> secondFixTargets = library.GetChartsNeedResourceFix([secondChart]);
            NormalLibraryRefreshNotificationBatch secondBatch = library.GetNormalLibraryRefreshNotificationsAfter(secondNotificationVersion);

            Assert.IsTrue(secondResult.HasUpdates);
            Assert.AreEqual(initialTargetCount, afterSecond.TargetCount);
            Assert.IsFalse(afterSecond.GetProjection(firstChart).HasIssues);
            Assert.IsFalse(afterSecond.GetProjection(secondChart).HasIssues);
            Assert.AreEqual(afterSecond.Version, thirdGetter.Version);
            Assert.AreEqual(0, secondFixTargets.Count);
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(secondBatch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.AreEqual(ownedVersion + 2, library.CatalogRevision);
            Assert.IsFalse(secondBatch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.IsTrue(afterFirst.GetProjection(secondChart).HasIssues);
            Assert.IsTrue(storeWork.Contains("entry_lookup"));
            Assert.IsFalse(storeWork.Contains("warning_sequence_enumeration"));
            Assert.IsFalse(storeWork.Contains("warning_sequence_entry_visited"));
        });
    }

    /// <summary>
    /// 実全所持保守で二件の保存・現在値・終端進捗を確定し、更新購読例外でも一回通知した結果を保持して、先行通知失敗による後続反映欠落を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    [TestMethod]
    public void RescanAllOwnedChartMaintenance_PreservesBothCurrentValuesAfterRefreshSubscriberFailure()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string firstPath = Path.Combine(root, "first.bms");
            string secondPath = Path.Combine(root, "second.bms");
            File.WriteAllText(
                firstPath,
                "#PLAYER 1\r\n#TITLE first\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            File.WriteAllText(
                secondPath,
                "#PLAYER 1\r\n#TITLE second\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);

            ChartFile first = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            first = first with { Path = firstPath };
            first = first with { Resources = TestChartResources.ReplaceAudio(first.Resources, ["missing.wav"]) };
            first = ChartFileProjection.WithMaintenance(first, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = first.Path, Hash = first.Md5, WavFilesDefined = 0, WavFilesExisting = 0, BgaFilesDefined = 0, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = false, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = false, BackbmpExisting = false, Encoding = "shift_jis", EncodingFixed = true });
            ChartFile second = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            second = second with { Path = secondPath };
            second = second with { Resources = TestChartResources.ReplaceAudio(second.Resources, ["missing.wav"]) };
            second = ChartFileProjection.WithMaintenance(second, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = second.Path, Hash = second.Md5, WavFilesDefined = 0, WavFilesExisting = 0, BgaFilesDefined = 0, BgaFilesExisting = 0, MovieFilesDefined = 0, MovieFilesExisting = 0, StagefileDefined = false, StagefileExisting = false, BannerDefined = false, BannerExisting = false, BackbmpDefined = false, BackbmpExisting = false, Encoding = "shift_jis", EncodingFixed = true });

            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [first, second], []);
            int handledVersion = library.NormalLibraryRefreshNotificationVersion;
            int failedNotificationCount = 0;
            library.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    Interlocked.Increment(ref failedNotificationCount);
                    throw new InvalidOperationException("maintenance refresh subscriber failure");
                }
            };
            var progress = new List<MaintenanceWorkflowProgress>();

            MaintenanceWorkflowResult result = library.RescanAllOwnedChartMaintenance(progress.Add);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, Volatile.Read(ref failedNotificationCount));
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledVersion);
            CollectionAssert.AreEquivalent(new[] { firstPath, secondPath }, batch.ChangedCharts.Select(chart => chart.Path).ToArray());
            Assert.IsTrue(library.BmsCharts.All(chart => chart.ResourceHealthMaintenanceSnapshot.WavFilesExisting == 0));
            Assert.IsTrue(progress.Any(update => update?.IsCompleted == true));
            using var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", firstPath));
            Assert.AreEqual(1L, verify.ExecuteScalar<long>("SELECT COUNT(1) FROM maintenance WHERE path = ?;", secondPath));
        });
    }

    /// <summary>
    /// 現在健全性索引がない選択強制検査は選択譜面の不足だけを返し、全所持索引を構築せず、少数要求による全件準備を検出する。
    /// </summary>
    [TestMethod]
    public void GetChartsNeedResourceFix_SubsetForceUpdateDoesNotBuildFullOwnedIndexWhenCurrentIndexUnavailable()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string targetPath = Path.Combine(root, "target-missing.bms");
            string unrelatedPath = Path.Combine(root, "unrelated-missing.bms");
            File.WriteAllText(targetPath, "#PLAYER 1\r\n#TITLE target\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.ASCII);
            File.WriteAllText(unrelatedPath, "#PLAYER 1\r\n#TITLE unrelated\r\n#WAV01 missing.wav\r\n#00111:01\r\n", Encoding.ASCII);
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            target = target with { Path = targetPath };
            ChartFile unrelated = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            unrelated = unrelated with { Path = unrelatedPath };
            ChartFile targetChart = (target);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [target, unrelated], []);
            library.BmsCharts = new List<ChartFile> { target, unrelated };

            List<ChartFile> result = library.GetChartsNeedResourceFix([targetChart], forceUpdate: true);
            ResourceHealthIndexSnapshot currentSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();

            Assert.AreEqual(1, result.Count);
            Assert.AreEqual(targetPath, result[0].Path);
            Assert.AreEqual(0, currentSnapshot.TargetCount);
        });
    }

    /// <summary>
    /// 所持譜面の不足を無視すると有効対象から除外対象へ移し、警告表示だけを一回通知して保守・Source更新の誤混入を検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_PublishesWarningRefreshThroughOwnedDispatcher()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = @"C:\Library\warning.bms" };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile chart = (file);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            int refreshNotificationChanged = 0;
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                if (args.PropertyName == nameof(BMSLibrary.NormalLibraryRefreshNotificationVersion))
                {
                    refreshNotificationChanged++;
                }
            };

            library.SetChartResourceWarningsIgnored([chart], unset: false);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            ResourceHealthIndexSnapshot updatedSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
            Assert.IsFalse(batch.HasEffect(LibraryChartRefreshEffects.SourceChanged));
            Assert.AreEqual(1, refreshNotificationChanged);
            Assert.AreEqual(0, updatedSnapshot.ActiveTargets.Count);
            Assert.AreEqual(1, updatedSnapshot.IgnoredTargets.Count);
        });
    }

    /// <summary>
    /// 保存保守のhydrate要求を現在schedulerへ渡し、同じ要求識別子で開始・完了を報告し、版と稼働状態を通知して、要求の混線や未終端を検出する。
    /// </summary>
    [TestMethod]
    public void DeferredMaintenanceHydration_UsesCurrentSchedulerAndPublishesState()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            var library = new TestBmsLibrary(songDbPath);
            var execution = new List<(OperationProgressRequest Request, bool Running)>();
            library.StartupProgressRequestFactory = (name, version) => new(3, 19, name, version);
            library.StartupRequestProgressReporter = (request, running) => execution.Add((request, running));
            library.AttachStartupRequestProgressSources();
            var changedProperties = new List<string>();
            library.PropertyChanged += delegate (object? _, System.ComponentModel.PropertyChangedEventArgs args)
            {
                changedProperties.Add(args.PropertyName!);
            };
            bool schedulerInvoked = false;
            library.StartupBackgroundTaskScheduler = delegate (string _, string _, string _, Func<System.Threading.Tasks.Task> work)
            {
                schedulerInvoked = true;
                work().GetAwaiter().GetResult();
                return true;
            };

            MethodInfo? queueMethod = typeof(BMSLibrary).GetMethod(
                "QueueDeferredMaintenanceHydration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueMethod);
            queueMethod!.Invoke(library, ["test_scheduler"]);

            Assert.IsTrue(schedulerInvoked);
            var expectedRequest = new OperationProgressRequest(3, 19, "maintenance_hydration", 1);
            Assert.AreEqual(expectedRequest, library.MaintenanceHydrationProgressRequest);
            CollectionAssert.AreEqual(new[] { (expectedRequest, true), (expectedRequest, false) }, execution);
            Assert.AreEqual(1, library.MaintenanceHydrationRequestedVersion);
            Assert.AreEqual(1, library.MaintenanceHydrationCompletedVersion);
            Assert.IsFalse(library.MaintenanceHydrationRunning);
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationRequestedVersion));
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationCompletedVersion));
            CollectionAssert.Contains(changedProperties, nameof(BMSLibrary.MaintenanceHydrationRunning));
        });
    }

    /// <summary>
    /// 保存保守行を現在所持へ反映して古い対象外行を実DBから除き、健全性索引と警告・保守通知へ接続して、保存・現在値・表示の不整合を検出する。
    /// </summary>
    /// <remarks>実ライブラリの保存・所持現在値・表示通知の接続を確認するため独立したSQLite DBを用いる。</remarks>
    [TestMethod]
    public void DeferredMaintenanceHydration_AttachesRowsRemovesStaleAndPublishesResourceHealth()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string chartPath = Path.Combine(root, "hydrated.bms");
            string stalePath = Path.Combine(root, "stale.bms");
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = chartPath };
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = 0, WavFilesExisting = 0 });

            var gateway = new BmsLibraryDbGateway(songDbPath);
            gateway.UpsertMaintenanceInfos(
            [
                new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = chartPath, Hash = file.Md5, WavFilesDefined = 2, WavFilesExisting = 1 },
                new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = stalePath, Hash = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", WavFilesDefined = 1, WavFilesExisting = 0 }
            ]);

            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;
            library.StartupBackgroundTaskScheduler = delegate (string _, string _, string _, Func<System.Threading.Tasks.Task> work)
            {
                work().GetAwaiter().GetResult();
                return true;
            };
            MethodInfo? queueMethod = typeof(BMSLibrary).GetMethod(
                "QueueDeferredMaintenanceHydration",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(queueMethod);
            queueMethod!.Invoke(library, ["test_hydration"]);

            file = library.BmsCharts.Single();
            Assert.AreEqual(2, file.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(1, file.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            using (var verify = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(1, verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance;").Count());
                Assert.AreEqual(chartPath, verify.Query<LR2SongDBExtended.maintenance>("SELECT * FROM maintenance;").Single().path);
            }
            ResourceHealthIndexSnapshot snapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, snapshot.TargetCount);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.MaintenancePresentationChanged));
        });
    }

    /// <summary>
    /// 現在索引がない選択無視指定は対象現在値だけ変更し索引を失効のまま保って、少数変更から全件索引を構築する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_InvalidatesInsteadOfFullRebuildWhenCurrentIndexUnavailable()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile target = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            target = target with { Path = @"C:\Library\target-warning.bms" };
            target = ChartFileProjection.WithMaintenance(target, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = target.Path, Hash = target.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile unrelated = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            unrelated = unrelated with { Path = @"C:\Library\unrelated-warning.bms" };
            unrelated = ChartFileProjection.WithMaintenance(unrelated, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = unrelated.Path, Hash = unrelated.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
            ChartFile targetChart = (target);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [target, unrelated], []);
            library.BmsCharts = new List<ChartFile> { target, unrelated };

            library.SetChartResourceWarningsIgnored([targetChart], unset: false);

            ResourceHealthIndexSnapshot currentSnapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
            Assert.AreEqual(0, currentSnapshot.TargetCount);
            Assert.IsTrue(library.BmsCharts.Single(chart => chart.Path == target.Path).ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
        });
    }

    /// <summary>
    /// 無視指定の後に実入力の内容更新を保存してhashを反映しても、累積通知の警告表示効果を保ち、別生産者の更新で効果を取り落とす誤りを検出する。
    /// </summary>
    [TestMethod]
    public void NormalLibraryRefreshNotificationBatch_MixedWarningProducersKeepWarningRefreshEffect()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            string root = Path.GetDirectoryName(songDbPath)!;
            string chartPath = Path.Combine(root, "warning.bms");
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warning before\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
            string oldMd5 = file.Md5;
            File.WriteAllText(
                chartPath,
                "#PLAYER 1\r\n#TITLE warning after\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
                Encoding.ASCII);
            ChartFile chart = (file);
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [file], []);
            EnsureCurrentResourceHealthIndex(library);
            int handledNotificationVersion = library.NormalLibraryRefreshNotificationVersion;

            library.SetChartResourceWarningsIgnored([chart], unset: false);
            ChartInfoInlineBuildResult result = InvokeBuildAndPersistInlineChartInfoForInstalledCharts(
                library,
                "test_warning_property_covered_digest",
                [(file)]);

            NormalLibraryRefreshNotificationBatch batch = library.GetNormalLibraryRefreshNotificationsAfter(handledNotificationVersion);
            Assert.AreEqual(1, result.DigestChanges.Count);
            Assert.AreEqual(oldMd5, result.DigestChanges.Single().OldMd5);
            file = library.BmsCharts.Single();
            Assert.AreNotEqual(oldMd5, file.Md5);
            Assert.IsTrue(batch.HasEffect(LibraryChartRefreshEffects.WarningPresentationChanged));
        });
    }

    /// <summary>
    /// 文字化けしたBOM付きBMSを明示保守の自動判定で保存し直し、現在値・DB原文の欠落やdigest・利用者列・CP932資源の変更を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsEncodingMaintenanceDoesNotMutateDigest()
    {

        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#TITLE 保守する曲\r\n#SUBTITLE 副題\r\n#ARTIST 保守作者\r\n#SUBARTIST 共同作者\r\n#GENRE 音楽\r\n#WAV01 sub\\é.wav\r\n#00111:01\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath), new BmsEncodingDetectionResult("unknown", BmsEncodingDetectionOutcome.Unknown, false, null));
            Assert.AreNotEqual("保守する曲", bmsFile.RawTitle);
            bmsFile = bmsFile with { Favorite = 3, AddDate = 12345, Tag = "keep-user-tag" };
            Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "sub"));
            File.WriteAllBytes(Path.Combine(tempDirectoryPath, "sub", "é.wav"), [1]);
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            string originalMd5 = bmsFile.Md5;
            string originalSha256 = bmsFile.Sha256;
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BgaFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { MovieFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { StagefileDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BackbmpDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BannerDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = null });
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
                songDb.InsertOrReplace(ChartSongStorageMapping.ToBmsRow(bmsFile), typeof(LR2SongDB.song));
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null,
                new ResourceHealthLookupContext(null));
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(1, result.MissingEncodingTargetCount);
            Assert.AreEqual(originalMd5, bmsFile.Md5);
            Assert.AreEqual(originalSha256, bmsFile.Sha256);
            Assert.AreEqual("保守する曲", bmsFile.RawTitle);
            Assert.AreEqual("保守する曲 副題", bmsFile.Title);
            Assert.AreEqual("保守作者 共同作者", bmsFile.Artist);
            Assert.AreEqual("utf-8", bmsFile.EncodingName);
            Assert.AreEqual(1, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(1, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(3, bmsFile.Favorite);
            Assert.AreEqual(12345, bmsFile.AddDate);
            Assert.AreEqual("keep-user-tag", bmsFile.Tag);
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(songDbPath).OpenSongDbReadOnly();
            LR2SongDB.song row = verify.Table<LR2SongDB.song>().Single();
            Assert.AreEqual("保守する曲", row.title);
            Assert.AreEqual("副題", row.subtitle);
            Assert.AreEqual("保守作者", row.artist);
            Assert.AreEqual("共同作者", row.subartist);
            Assert.AreEqual("音楽", row.genre);
            Assert.AreEqual(originalMd5, row.hash);
            Assert.AreEqual(3, row.favorite);
            Assert.AreEqual(12345, row.adddate);
            Assert.AreEqual("keep-user-tag", row.tag);
            Assert.AreEqual("utf-8", verify.Table<LR2SongDBExtended.maintenance>().Single().encoding);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 健全性差分のhash変更・改善・追加・重複入力・削除を現在索引へ局所反映し、対象外の無視状態と旧snapshotを保持して、古いhash残存や二重登録を検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_ApplyDeltaUpdatesOnlyAffectedTargets()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile active = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        active = active with { Path = @"C:\Library\active.bms" };
        active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
        ChartFile ignored = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        ignored = ignored with { Path = @"C:\Library\ignored.bms" };
        ignored = ChartFileProjection.WithMaintenance(ignored, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = ignored.Path, Hash = ignored.Md5, BgaFilesDefined = 2, BgaFilesExisting = 1, FilesWarningIgnored = true });
        ChartFile activeChart = (active);
        ChartFile ignoredChart = (ignored);
        var snapshot = ResourceHealthIndexSnapshot.Build([activeChart, ignoredChart], service, version: 1);
        var duplicateBuild = ResourceHealthIndexSnapshot.Build([activeChart, activeChart], service, version: 0);

        Assert.AreEqual(1, duplicateBuild.TargetCount);
        CollectionAssert.AreEqual(new[] { activeChart.Path }, duplicateBuild.ActiveTargets.Select(reference => reference.Path).ToArray());

        ChartFile rehashed = CreateFile("dddddddddddddddddddddddddddddddd");
        rehashed = rehashed with { Path = active.Path };
        rehashed = ChartFileProjection.WithMaintenance(rehashed, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = rehashed.Path, Hash = rehashed.Md5, WavFilesDefined = 2, WavFilesExisting = 1, FilesWarningIgnored = false });
        ChartFile rehashedChart = (rehashed);
        ResourceHealthIndexSnapshot afterRehash = snapshot.ApplyDelta([rehashedChart], null, service, version: 2);

        Assert.AreEqual(2, afterRehash.TargetCount);
        Assert.IsFalse(afterRehash.GetProjection(activeChart).HasIssues);
        Assert.IsTrue(afterRehash.GetProjection(rehashedChart).HasIssues);
        CollectionAssert.AreEqual(new[] { rehashedChart.Path }, afterRehash.ActiveTargets.Select(reference => reference.Path).ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, afterRehash.IgnoredTargets.Select(reference => reference.Path).ToArray());

        ChartFile initialActiveChart = activeChart;
        active = ChartFileProjection.WithMaintenance(active, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = active.Path, Hash = active.Md5, WavFilesDefined = 2, WavFilesExisting = 2 });
        activeChart = (active);
        ResourceHealthIndexSnapshot afterFix = snapshot.ApplyDelta([activeChart], null, service, version: 2);

        Assert.AreEqual(2, afterFix.TargetCount);
        Assert.IsFalse(afterFix.GetProjection(activeChart).HasIssues);
        Assert.AreEqual(0, afterFix.ActiveTargets.Count);
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, afterFix.IgnoredTargets.Select(reference => reference.Path).ToArray());

        ResourceHealthIndexSnapshot afterHealthyRemove = afterFix.ApplyDelta(null, [activeChart], service, version: 3);

        Assert.AreEqual(1, afterHealthyRemove.TargetCount);
        Assert.IsFalse(afterHealthyRemove.GetProjection(activeChart).HasIssues);
        Assert.AreEqual(0, afterHealthyRemove.ActiveTargets.Count);
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, afterHealthyRemove.IgnoredTargets.Select(reference => reference.Path).ToArray());

        ChartFile added = CreateFile("cccccccccccccccccccccccccccccccc");
        added = added with { Path = @"C:\Library\added.bms" };
        added = ChartFileProjection.WithMaintenance(added, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = added.Path, Hash = added.Md5, BgaFilesDefined = 4, BgaFilesExisting = 3, FilesWarningIgnored = false });
        ChartFile addedChart = (added);
        ResourceHealthIndexSnapshot afterAdd = afterFix.ApplyDelta([addedChart], null, service, version: 3);

        Assert.AreEqual(3, afterAdd.TargetCount);
        Assert.IsTrue(afterAdd.GetProjection(addedChart).HasIssues);
        CollectionAssert.AreEqual(new[] { addedChart.Path }, afterAdd.ActiveTargets.Select(reference => reference.Path).ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, afterAdd.IgnoredTargets.Select(reference => reference.Path).ToArray());

        ResourceHealthIndexSnapshot afterDuplicateUpdate = afterAdd.ApplyDelta([addedChart, addedChart], null, service, version: 4);

        Assert.AreEqual(3, afterDuplicateUpdate.TargetCount);
        CollectionAssert.AreEqual(new[] { addedChart.Path }, afterDuplicateUpdate.ActiveTargets.Select(reference => reference.Path).ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, afterDuplicateUpdate.IgnoredTargets.Select(reference => reference.Path).ToArray());

        ResourceHealthIndexSnapshot afterRemove = afterDuplicateUpdate.ApplyDelta([ignoredChart], [ignoredChart], service, version: 5);

        Assert.AreEqual(2, afterRemove.TargetCount);
        Assert.IsFalse(afterRemove.GetProjection(ignoredChart).HasIssues);
        CollectionAssert.AreEqual(new[] { addedChart.Path }, afterRemove.ActiveTargets.Select(reference => reference.Path).ToArray());
        Assert.AreEqual(0, afterRemove.IgnoredTargets.Count);

        Assert.AreEqual(2, snapshot.TargetCount);
        CollectionAssert.AreEqual(new[] { initialActiveChart.Path }, snapshot.ActiveTargets.Select(reference => reference.Path).ToArray());
        CollectionAssert.AreEqual(new[] { ignoredChart.Path }, snapshot.IgnoredTargets.Select(reference => reference.Path).ToArray());
        Assert.IsTrue(snapshot.GetProjection(initialActiveChart).HasIssues);
        Assert.IsTrue(snapshot.GetProjection(ignoredChart).HasIssues);
    }
    /// <summary>
    /// 警告・無視対象を入替える局所差分でも未変更対象の表示順と旧snapshotの警告・件数を保持し、差分による順序崩れや過去結果の書換えを検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_PreservesWarningOrderAndOldSnapshotAcrossLocalDelta()
    {

        var service = new BmsLibraryMaintenanceService();
        const string activeAPath = @"C:\Library\active-a.bms";
        const string activeUnchangedPath = @"C:\Library\active-unchanged.bms";
        const string activeBPath = @"C:\Library\active-b.bms";
        const string activeCPath = @"C:\Library\active-c.bms";
        const string ignoredAPath = @"C:\Library\ignored-a.bms";
        const string ignoredUnchangedPath = @"C:\Library\ignored-unchanged.bms";
        const string ignoredBPath = @"C:\Library\ignored-b.bms";

        ChartFile activeA = CreateResourceHealthChart(
            activeAPath,
            new string('a', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1);
        ChartFile activeUnchanged = CreateResourceHealthChart(
            activeUnchangedPath,
            new string('b', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1);
        ChartFile activeB = CreateResourceHealthChart(
            activeBPath,
            new string('c', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1);
        ChartFile activeC = CreateResourceHealthChart(
            activeCPath,
            new string('d', 32),
            movieFilesDefined: 2,
            movieFilesExisting: 1);
        ChartFile ignoredA = CreateResourceHealthChart(
            ignoredAPath,
            new string('e', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1,
            isIgnored: true);
        ChartFile ignoredUnchanged = CreateResourceHealthChart(
            ignoredUnchangedPath,
            new string('f', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1,
            isIgnored: true);
        ChartFile ignoredB = CreateResourceHealthChart(
            ignoredBPath,
            new string('1', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1,
            isIgnored: true);

        var initial = ResourceHealthIndexSnapshot.Build(
            [activeA, activeUnchanged, activeB, activeC, ignoredA, ignoredUnchanged, ignoredB],
            service,
            version: 10);

        ChartFile activeCUpdated = CreateResourceHealthChart(
            activeCPath,
            new string('d', 32),
            movieFilesDefined: 3,
            movieFilesExisting: 1);
        ChartFile ignoredAUpdated = CreateResourceHealthChart(
            ignoredAPath,
            new string('e', 32),
            wavFilesDefined: 3,
            wavFilesExisting: 1);
        ChartFile ignoredBHealthy = CreateResourceHealthChart(
            ignoredBPath,
            new string('1', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 2,
            isIgnored: true);
        ChartFile activeBUpdated = CreateResourceHealthChart(
            activeBPath,
            new string('c', 32),
            bgaFilesDefined: 3,
            bgaFilesExisting: 1,
            isIgnored: true);
        ChartFile added = CreateResourceHealthChart(
            @"C:\Library\added.bms",
            new string('2', 32),
            wavFilesDefined: 2,
            wavFilesExisting: 1);

        ResourceHealthIndexSnapshot updated = initial.ApplyDelta(
            [activeCUpdated, ignoredAUpdated, ignoredBHealthy, activeBUpdated, added],
            null,
            service,
            version: 11);

        CollectionAssert.AreEqual(
            new[] { activeAPath, activeUnchangedPath, activeCPath, ignoredAPath, @"C:\Library\added.bms" },
            updated.ActiveTargets.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(
            new[] { ignoredUnchangedPath, activeBPath },
            updated.IgnoredTargets.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(8, updated.TargetCount);
        Assert.IsFalse(updated.GetProjection(ignoredBHealthy).HasIssues);
        Assert.IsTrue(updated.GetProjection(activeCUpdated).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        Assert.IsTrue(updated.GetProjection(ignoredAUpdated).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(updated.GetProjection(activeBUpdated).IsIgnored);

        CollectionAssert.AreEqual(
            new[] { activeAPath, activeUnchangedPath, activeBPath, activeCPath },
            initial.ActiveTargets.Select(chart => chart.Path).ToArray());
        CollectionAssert.AreEqual(
            new[] { ignoredAPath, ignoredUnchangedPath, ignoredBPath },
            initial.IgnoredTargets.Select(chart => chart.Path).ToArray());
        Assert.AreEqual(7, initial.TargetCount);
        Assert.IsTrue(initial.GetProjection(activeC).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        Assert.IsTrue(initial.GetProjection(ignoredA).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(initial.GetProjection(activeB).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
    }
    /// <summary>
    /// 健全・不足の背景規模を別々に増やしても一対象差分のlookupと比較だけで表示件数・先頭を取得し、全警告列挙や全件訪問を避けることを確認する。cold列挙を正対照として全件走査の観測漏れを検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_LocalDeltaSharesUnchangedSequenceAndProjectionStorage()
    {

        var service = new BmsLibraryMaintenanceService();
        (int HealthyBackground, int WarningBackground)[] backgrounds =
        [
            (16, 16),
            (128, 16),
            (16, 128)
        ];
        List<(
            int HealthyBackground,
            int WarningBackground,
            int EntryLookups,
            int Comparisons,
            int EntryVisits)> observations = [];

        foreach ((int healthyBackgroundCount, int warningBackgroundCount) in backgrounds)
        {
            List<ChartFile> targets = [];
            for (int index = 0; index < warningBackgroundCount; index++)
            {
                targets.Add(CreateResourceHealthChart(
                    $@"C:\Library\warning-background-{warningBackgroundCount}-{index}.bms",
                    index.ToString("x8") + new string('0', 24),
                    wavFilesDefined: 2,
                    wavFilesExisting: 1,
                    isIgnored: index % 2 == 0));
            }
            ChartFile updated = CreateResourceHealthChart(
                $@"C:\Library\updated-{healthyBackgroundCount}-{warningBackgroundCount}.bms",
                new string('a', 32),
                wavFilesDefined: 2,
                wavFilesExisting: 1);
            ChartFile unchanged = CreateResourceHealthChart(
                $@"C:\Library\unchanged-{healthyBackgroundCount}-{warningBackgroundCount}.bms",
                new string('b', 32),
                bgaFilesDefined: 2,
                bgaFilesExisting: 1);
            targets.Add(updated);
            targets.Add(unchanged);
            for (int index = 0; index < healthyBackgroundCount; index++)
            {
                targets.Add(CreateResourceHealthChart(
                    $@"C:\Library\healthy-background-{healthyBackgroundCount}-{index}.bms",
                    index.ToString("x8") + new string('f', 24)));
            }

            var initial = ResourceHealthIndexSnapshot.Build(targets, service, version: 20);
            List<string> storeWork = [];
            initial.StoreWorkObserver = operation => storeWork.Add(operation);
            ChartFile updatedHealthy = CreateResourceHealthChart(
                updated.Path,
                updated.Md5,
                wavFilesDefined: 2,
                wavFilesExisting: 2);
            ResourceHealthIndexSnapshot after = initial.ApplyDelta(
                [updatedHealthy],
                null,
                service,
                version: 21);

            ResourceHealthWarningProjection unchangedProjection = after.GetProjection(unchanged);
            ResourceHealthWarningProjection updatedProjection = after.GetProjection(updatedHealthy);
            int activeCount = after.ActiveTargets.Count;
            int ignoredCount = after.IgnoredTargets.Count;
            string activeFirstPath = activeCount == 0 ? string.Empty : after.ActiveTargets[0].Path;
            string ignoredFirstPath = ignoredCount == 0 ? string.Empty : after.IgnoredTargets[0].Path;
            after.StoreWorkObserver = null;
            initial.StoreWorkObserver = null;

            Assert.AreEqual(targets.Count, initial.TargetCount);
            Assert.AreEqual(initial.TargetCount, after.TargetCount);
            Assert.IsTrue(unchangedProjection.HasIssues);
            Assert.IsFalse(updatedProjection.HasIssues);
            Assert.IsTrue(activeCount > 0);
            Assert.IsTrue(ignoredCount > 0);
            Assert.IsFalse(string.IsNullOrEmpty(activeFirstPath));
            Assert.IsFalse(string.IsNullOrEmpty(ignoredFirstPath));

            int entryLookups = storeWork.Count(operation => operation == "entry_lookup");
            int enumerations = storeWork.Count(operation => operation == "warning_sequence_enumeration");
            int comparisons = storeWork.Count(operation => operation == "warning_sequence_index_comparison");
            int entryVisits = storeWork.Count(operation => operation == "warning_sequence_entry_visited");
            Assert.AreEqual(3, entryLookups);
            Assert.AreEqual(0, enumerations);
            Assert.AreEqual(1, storeWork.Count(operation => operation == "warning_sequence_remove"));
            Assert.IsTrue(comparisons > 0);
            Assert.IsTrue(comparisons < warningBackgroundCount);
            Assert.AreEqual(0, entryVisits);
            observations.Add((
                healthyBackgroundCount,
                warningBackgroundCount,
                entryLookups,
                comparisons,
                entryVisits));
        }

        Assert.IsTrue(observations.All(result => result.EntryLookups == observations[0].EntryLookups));
        Assert.IsTrue(observations.All(result => result.EntryVisits == 0));

        ChartFile coldActive = CreateResourceHealthChart(
            @"C:\Library\cold-active.bms",
            new string('c', 32),
            bgaFilesDefined: 2,
            bgaFilesExisting: 1);
        ChartFile coldIgnored = CreateResourceHealthChart(
            @"C:\Library\cold-ignored.bms",
            new string('d', 32),
            movieFilesDefined: 2,
            movieFilesExisting: 1,
            isIgnored: true);
        ChartFile coldHealthy = CreateResourceHealthChart(
            @"C:\Library\cold-healthy.bms",
            new string('e', 32));
        var cold = ResourceHealthIndexSnapshot.Build(
            [coldActive, coldIgnored, coldHealthy],
            service,
            version: 22);
        List<string> coldWork = [];
        cold.StoreWorkObserver = operation => coldWork.Add(operation);
        int coldActiveCount = cold.ActiveTargets.Count;
        int coldIgnoredCount = cold.IgnoredTargets.Count;
        LibraryChartRef[] coldActiveMaterialized = cold.ActiveTargets.ToArray();
        LibraryChartRef[] coldIgnoredMaterialized = cold.IgnoredTargets.ToArray();
        cold.StoreWorkObserver = null;

        Assert.AreEqual(coldActiveCount, coldActiveMaterialized.Length);
        Assert.AreEqual(coldIgnoredCount, coldIgnoredMaterialized.Length);
        Assert.IsTrue(coldWork.Count(operation => operation == "warning_sequence_enumeration") > 0);
        Assert.IsTrue(coldWork.Count(operation => operation == "warning_sequence_entry_visited") > 0);
    }
    /// <summary>
    /// 大小文字だけ異なる二パスを同MD5へ更新しても警告を独立に保ち、一方の再hash・削除で他方と旧snapshotを変えず、対象混同を検出する。
    /// </summary>
    [TestMethod]
    public void ResourceHealthIndexSnapshot_CaseOnlyExactPathsRemainIndependentAcrossDeltaAndRehash()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile upperInitial = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        upperInitial = upperInitial with { Path = @"C:\Library\Delta.bms" };
        upperInitial = ChartFileProjection.WithMaintenance(upperInitial, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperInitial.Path, Hash = upperInitial.Md5, WavFilesDefined = 2, WavFilesExisting = 1 });
        ChartFile lowerInitial = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        lowerInitial = lowerInitial with { Path = @"c:\library\delta.bms" };
        lowerInitial = ChartFileProjection.WithMaintenance(lowerInitial, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerInitial.Path, Hash = lowerInitial.Md5, BgaFilesDefined = 2, BgaFilesExisting = 1 });
        ChartFile upperInitialChart = (upperInitial);
        ChartFile lowerInitialChart = (lowerInitial);
        var initial = ResourceHealthIndexSnapshot.Build(
            [upperInitialChart, lowerInitialChart],
            service,
            version: 1);

        const string sharedRehash = "cccccccccccccccccccccccccccccccc";
        ChartFile upperShared = CreateFile(sharedRehash);
        upperShared = upperShared with { Path = upperInitial.Path };
        upperShared = ChartFileProjection.WithMaintenance(upperShared, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperShared.Path, Hash = sharedRehash, MovieFilesDefined = 2, MovieFilesExisting = 1 });
        ChartFile lowerShared = CreateFile(sharedRehash);
        lowerShared = lowerShared with { Path = lowerInitial.Path };
        lowerShared = ChartFileProjection.WithMaintenance(lowerShared, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lowerShared.Path, Hash = sharedRehash, BgaFilesDefined = 4, BgaFilesExisting = 2 });
        ChartFile upperSharedChart = (upperShared);
        ChartFile lowerSharedChart = (lowerShared);

        ResourceHealthIndexSnapshot bothRehashed = initial.ApplyDelta(
            [upperSharedChart, lowerSharedChart],
            null,
            service,
            version: 2);

        Assert.AreEqual(2, bothRehashed.TargetCount);
        Assert.IsTrue(bothRehashed.GetProjection(upperSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));
        Assert.IsTrue(bothRehashed.GetProjection(lowerSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
        Assert.IsFalse(bothRehashed.GetProjection(upperInitialChart).HasIssues);
        Assert.IsFalse(bothRehashed.GetProjection(lowerInitialChart).HasIssues);
        Assert.IsTrue(initial.GetProjection(upperInitialChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(initial.GetProjection(lowerInitialChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));

        ChartFile upperRehashedAgain = CreateFile("dddddddddddddddddddddddddddddddd");
        upperRehashedAgain = upperRehashedAgain with { Path = upperInitial.Path };
        upperRehashedAgain = ChartFileProjection.WithMaintenance(upperRehashedAgain, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = upperRehashedAgain.Path, Hash = upperRehashedAgain.Md5, WavFilesDefined = 3, WavFilesExisting = 1 });
        ChartFile upperRehashedAgainChart = (upperRehashedAgain);

        ResourceHealthIndexSnapshot upperUpdated = bothRehashed.ApplyDelta(
            [upperRehashedAgainChart],
            null,
            service,
            version: 3);

        Assert.AreEqual(2, upperUpdated.TargetCount);
        Assert.IsFalse(upperUpdated.GetProjection(upperSharedChart).HasIssues);
        Assert.IsTrue(upperUpdated.GetProjection(upperRehashedAgainChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        Assert.IsTrue(upperUpdated.GetProjection(lowerSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
        Assert.IsTrue(bothRehashed.GetProjection(upperSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceMovieMissing));

        ResourceHealthIndexSnapshot upperRemoved = upperUpdated.ApplyDelta(
            null,
            [upperRehashedAgainChart],
            service,
            version: 4);

        Assert.AreEqual(1, upperRemoved.TargetCount);
        Assert.IsFalse(upperRemoved.GetProjection(upperRehashedAgainChart).HasIssues);
        Assert.IsTrue(upperRemoved.GetProjection(lowerSharedChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceBgaMissing));
        Assert.IsTrue(upperUpdated.GetProjection(upperRehashedAgainChart).Warnings.Any(
            warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
    }

    /// <summary>
    /// 現在索引がある少数保守は差分失敗時の失効を指示し、現在索引がなければ失効だけを指示して、全件構築への不要な切替えを検出する。
    /// </summary>
    [TestMethod]
    public void MaintenanceResourceHealthDelta_InvalidatesInsteadOfFullRebuildWhenDeltaCannotApply()
    {
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        file = file with { Path = @"C:\Library\delta.bms" };
        ChartFile chart = (file);

        ResourceHealthIndexMutation currentMutation = BuildMaintenanceResourceHealthMutation(
            [chart],
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
            resourceHealthIndexCurrent: true,
            workflowHasUpdates: true);
        Assert.IsFalse(currentMutation.RebuildFull);
        Assert.IsFalse(currentMutation.Invalidate);
        Assert.IsTrue(currentMutation.InvalidateIfDeltaFails);
        Assert.AreEqual(1, currentMutation.UpdatedTargets.Count);

        ResourceHealthIndexMutation unavailableMutation = BuildMaintenanceResourceHealthMutation(
            [chart],
            resourceHealthIndexUpdateMode: ResourceHealthIndexUpdateMode.DeltaOnUpdates,
            resourceHealthIndexCurrent: false,
            workflowHasUpdates: true);
        Assert.IsFalse(unavailableMutation.RebuildFull);
        Assert.IsTrue(unavailableMutation.Invalidate);
        Assert.IsFalse(unavailableMutation.InvalidateIfDeltaFails);
        Assert.AreEqual(0, unavailableMutation.UpdatedTargets.Count);
    }

    /// <summary>
    /// 現在resolverのノート数でゼロノートBMSだけを選び、古い埋込み情報・未解決情報・Bmsonを採用せず、ゼロノート対象の誤分類を検出する。
    /// </summary>
    [TestMethod]
    public void GetZeroNoteCharts_FiltersOnlyZeroNoteCharts()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = ChartFileProjection.WithChartInfo(zeroNoteFile, new BeMusicSeeker.Models.ChartDetails { md5 = zeroNoteFile.Md5, sha256 = zeroNoteFile.Sha256, notes = 1200 });
        BeMusicSeeker.Models.ChartDetails zeroNoteInfo = CreateChartInfo(zeroNoteFile.Md5, notes: 0);
        ChartFile zeroNoteChart = (zeroNoteFile);
        ChartFile normalFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        normalFile = ChartFileProjection.WithChartInfo(normalFile, new BeMusicSeeker.Models.ChartDetails { md5 = normalFile.Md5, sha256 = normalFile.Sha256, notes = 0 });
        BeMusicSeeker.Models.ChartDetails normalInfo = CreateChartInfo(normalFile.Md5, notes: 1200);
        ChartFile normalChart = (normalFile);
        ChartFile missingChartInfoFile = CreateFile("dddddddddddddddddddddddddddddddd");
        missingChartInfoFile = ChartFileProjection.WithChartInfo(missingChartInfoFile, new BeMusicSeeker.Models.ChartDetails { md5 = missingChartInfoFile.Md5, sha256 = missingChartInfoFile.Sha256, notes = 0 });
        ChartFile missingChartInfoChart = (missingChartInfoFile);
        ChartFile zeroNoteBmson = CreateBmsonSong("C:\\Library\\chart.bmson", "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        BeMusicSeeker.Models.ChartDetails zeroNoteBmsonInfo = CreateChartInfo(zeroNoteBmson.Md5, notes: 0);
        ChartFile zeroNoteBmsonChart = (zeroNoteBmson);

        List<ChartFile> result = service.GetZeroNoteCharts(
            [zeroNoteChart, normalChart, missingChartInfoChart, zeroNoteBmsonChart],
            CreateChartInfoResolver(zeroNoteInfo, normalInfo, zeroNoteBmsonInfo));

        CollectionAssert.AreEqual(new[] { zeroNoteChart }, result);
    }

    /// <summary>
    /// 共通Bmsonの保存済み不足警告を取得して無視指定を変更結果へ反映し、形式片側の保守入口欠落を検出する。
    /// </summary>
    [TestMethod]
    public void BmsonMaintenanceOperations_UseChartEntryPoints()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile bmsonSong = CreateBmsonSong("C:\\Library\\chart.bmson", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        bmsonSong = bmsonSong with { ResourceHealthMaintenanceSnapshot = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = bmsonSong.Path, hash = bmsonSong.Md5, encoding = "utf-8" }) };
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { Encoding = "gb2312" });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { EncodingFixed = false });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = false });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 1 });
        bmsonSong = ChartFileProjection.WithMaintenance(bmsonSong, bmsonSong.ResourceHealthMaintenanceSnapshot with { WavFilesExisting = 0 });
        ChartFile bmsonChart = (bmsonSong);

        IReadOnlyList<ChartWarning> warnings = service.BuildResourceHealthWarnings(bmsonChart);
        Assert.IsTrue(warnings.Any(warning => warning.Kind == ChartWarningKind.ResourceWavMissing));
        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([bmsonChart], unset: false);
        Assert.AreEqual(1, changes.Count);

        Assert.IsTrue(changes.Single().ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    /// <summary>
    /// 選択BMSの無視指定を一件の変更結果へ反映し、指定対象への変更漏れを検出する。
    /// </summary>
    [TestMethod]
    public void SetChartResourceWarningsIgnored_TogglesSelectedEntry()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, FilesWarningIgnored = false };
        file = ChartFileProjection.WithMaintenance(file, info);

        List<ChartFile> changes = service.SetChartResourceWarningsIgnored([(file)], unset: false);

        Assert.AreEqual(1, changes.Count);
        Assert.IsTrue(changes.Single().ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
    }

    /// <summary>
    /// unknown・GB2312・Big5の未修正譜面を文字化け一覧へ、修正済みunknownだけを修正済み一覧へ分け、CP932系の誤採用や修正状態の混線を検出する。
    /// </summary>
    [TestMethod]
    public void GetGarbledFiles_IncludesUnknownEncodingInRegularAndFixedLists()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile unknownFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        unknownFile = ChartFileProjection.WithMaintenance(unknownFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = unknownFile.Path, Hash = unknownFile.Md5, Encoding = "unknown", EncodingFixed = false });
        ChartFile fixedUnknownFile = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        fixedUnknownFile = ChartFileProjection.WithMaintenance(fixedUnknownFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = fixedUnknownFile.Path, Hash = fixedUnknownFile.Md5, Encoding = "unknown", EncodingFixed = true });
        ChartFile shiftJisFile = CreateFile("cccccccccccccccccccccccccccccccc");
        shiftJisFile = ChartFileProjection.WithMaintenance(shiftJisFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = shiftJisFile.Path, Hash = shiftJisFile.Md5, Encoding = "shift_jis", EncodingFixed = false });
        ChartFile shiftJisQuestionFile = CreateFile("dddddddddddddddddddddddddddddddd");
        shiftJisQuestionFile = ChartFileProjection.WithMaintenance(shiftJisQuestionFile, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = shiftJisQuestionFile.Path, Hash = shiftJisQuestionFile.Md5, Encoding = "shift_jis?", EncodingFixed = false });
        ChartFile gb2312File = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        gb2312File = ChartFileProjection.WithMaintenance(gb2312File, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = gb2312File.Path, Hash = gb2312File.Md5, Encoding = "gb2312", EncodingFixed = false });
        ChartFile big5File = CreateFile("ffffffffffffffffffffffffffffffff");
        big5File = ChartFileProjection.WithMaintenance(big5File, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = big5File.Path, Hash = big5File.Md5, Encoding = "big5", EncodingFixed = false });

        List<ChartFile> regularList = service.GetGarbledFiles([unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File], isInFixedList: false);
        List<ChartFile> fixedList = service.GetGarbledFiles([unknownFile, fixedUnknownFile, shiftJisFile, shiftJisQuestionFile, gb2312File, big5File], isInFixedList: true);

        CollectionAssert.AreEquivalent(new ChartFile[] { unknownFile, gb2312File, big5File }, regularList);
        CollectionAssert.AreEquivalent(new ChartFile[] { fixedUnknownFile }, fixedList);
    }

    /// <summary>
    /// 実所持から文字化け・修正済み・LR2非対応の専用一覧を同tokenで返し、親情報が空なだけの正常譜面を未登録へ混ぜる誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ChartFileSubsets_ProjectGarbledAndLr2CompatibilityWarningRows()
    {

        WithTemporarySongDb(delegate (string songDbPath)
        {
            ChartFile garbled = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            garbled = garbled with { Path = @"C:\Library\garbled.bms" };
            garbled = ChartFileProjection.WithMaintenance(garbled, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = garbled.Path, Hash = garbled.Md5, Encoding = "unknown", EncodingFixed = false });
            ChartFile fixedGarbled = CreateFile("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            fixedGarbled = fixedGarbled with { Path = @"C:\Library\fixed.bms" };
            fixedGarbled = ChartFileProjection.WithMaintenance(fixedGarbled, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = fixedGarbled.Path, Hash = fixedGarbled.Md5, Encoding = "gb2312", EncodingFixed = true });
            ChartFile lr2Warning = CreateFile("cccccccccccccccccccccccccccccccc");
            lr2Warning = lr2Warning with { Path = @"C:\Library\lr2-warning.bms" };
            lr2Warning = ChartFileProjection.WithMaintenance(lr2Warning, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = lr2Warning.Path, Hash = lr2Warning.Md5, Encoding = "shift_jis", EncodingFixed = false });
            lr2Warning = lr2Warning with { Warnings = [.. lr2Warning.Warnings.Where(warning => warning.Kind != ChartWarningKind.Lr2ResourcePathTooLong), ChartWarning.Create(ChartWarningKind.Lr2ResourcePathTooLong, "resource path is too long for LR2")] };
            ChartFile parentBlankWithoutWarning = CreateFile("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
            parentBlankWithoutWarning = parentBlankWithoutWarning with { Path = @"C:\Library\parent-blank.bms" };
            parentBlankWithoutWarning = ChartFileProjection.WithMaintenance(parentBlankWithoutWarning, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = parentBlankWithoutWarning.Path, Hash = parentBlankWithoutWarning.Md5, Encoding = "shift_jis", EncodingFixed = false });
            ChartFile registered = CreateFile("dddddddddddddddddddddddddddddddd");
            registered = registered with { Path = @"C:\Library\registered.bms" };
            registered = ChartFileProjection.WithMaintenance(registered, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = registered.Path, Hash = registered.Md5, Encoding = "shift_jis", EncodingFixed = false });
            var library = new TestBmsLibrary(songDbPath);
            SetStorageRows(library, [garbled, fixedGarbled, lr2Warning, parentBlankWithoutWarning, registered], []);

            garbled = library.BmsCharts.Single(chart => chart.Path == garbled.Path);
            fixedGarbled = library.BmsCharts.Single(chart => chart.Path == fixedGarbled.Path);
            lr2Warning = library.BmsCharts.Single(chart => chart.Path == lr2Warning.Path);
            List<ChartFile> garbledCharts = [.. library.ChartFilesGarbled];
            List<ChartFile> fixedCharts = [.. library.ChartFilesGarbledFixed];
            List<ChartFile> unregisteredCharts = [.. library.ChartFilesUnregistered];

            CollectionAssert.AreEqual(new[] { garbled.Path }, garbledCharts.Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { fixedGarbled.Path }, fixedCharts.Select(chart => chart.Path).ToArray());
            CollectionAssert.AreEqual(new[] { lr2Warning.Path }, unregisteredCharts.Select(chart => chart.Path).ToArray());
            Assert.AreSame(garbled.Token, garbledCharts[0].Token);
            Assert.AreSame(fixedGarbled.Token, fixedCharts[0].Token);
            Assert.AreSame(lr2Warning.Token, unregisteredCharts[0].Token);
        });
    }

    /// <summary>
    /// 保存ノート数ゼロの実入力がない場合は検査をskipとして集計し古い不整合警告を変更結果から解除して、消失入力の不整合保持を検出する。
    /// </summary>
    [TestMethod]
    public void RecheckZeroNoteWarnings_SkipsMissingFilesAndClearsWarning()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = ChartFileProjection.WithChartInfo(zeroNoteFile, new BeMusicSeeker.Models.ChartDetails { md5 = zeroNoteFile.Md5, sha256 = zeroNoteFile.Sha256, notes = 0 });
        BeMusicSeeker.Models.ChartDetails zeroNoteInfo = CreateChartInfo(zeroNoteFile.Md5, notes: 0);
        zeroNoteFile = zeroNoteFile with { Path = "C:\\missing\\chart.bms" };
        zeroNoteFile = zeroNoteFile with { Warnings = [.. zeroNoteFile.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [(zeroNoteFile)],
            chartInfoResolver: CreateChartInfoResolver(zeroNoteInfo));

        Assert.AreEqual(1, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.SkippedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(result.ChangedCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
    }

    /// <summary>
    /// 現在resolverが非ゼロ情報を返す対象はゼロノート再検査の対象数に含めず、既存の不整合警告を解除する。
    /// </summary>
    [TestMethod]
    public void RecheckZeroNoteWarnings_UsesResolverChartInfo()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = zeroNoteFile with { Warnings = [.. zeroNoteFile.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };
        ChartFile chart = (zeroNoteFile);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [chart],
            chartInfoResolver: candidate => ReferenceEquals(candidate.Token, zeroNoteFile.Token)
                ? CreateChartInfo(zeroNoteFile.Md5, notes: 1200)
                : null);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(result.ChangedCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
    }

    /// <summary>
    /// 現在resolverが未解決ならゼロノート検査を始めず古い不整合警告を解除し、未解決情報への代替判定を検出する。
    /// </summary>
    [TestMethod]
    public void RecheckZeroNoteWarnings_DoesNotFallbackWhenResolverMisses()
    {

        var service = new BmsLibraryMaintenanceService();
        ChartFile zeroNoteFile = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        zeroNoteFile = zeroNoteFile with { Warnings = [.. zeroNoteFile.Warnings.Where(warning => warning.Kind != ChartWarningKind.ZeroNoteMismatch), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch, Resources.Warning_ZeroNoteMismatch)] };
        ChartFile chart = (zeroNoteFile);

        ZeroNoteRecheckResult result = service.RecheckZeroNoteWarnings(
            [chart],
            chartInfoResolver: _ => null);

        Assert.AreEqual(0, result.Total);
        Assert.AreEqual(1, result.ClearedCount);
        Assert.AreEqual(1, result.ChangedCount);
        Assert.IsFalse(result.ChangedCharts.Single().Warnings.Any(warning => warning.Kind == ChartWarningKind.ZeroNoteMismatch));
    }

    /// <summary>
    /// 同じ指定文字コードでも実入力の生・合成メタデータが違えば再読解して保存更新対象へ返し、文字コード一致だけで古い値を維持する誤りを検出する。
    /// </summary>
    [TestMethod]
    public void ApplyEncoding_ReloadsWhenEncodingMatchesButDecodedMetadataDiffers()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "同一Encoding再読込";
        const string expectedSubtitle = "[Manual]";
        const string expectedArtist = "差分あり";
        const string expectedSubartist = "obj:Manual";
        const string expectedGenre = "GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#SUBTITLE " + expectedSubtitle + "\r\n#ARTIST " + expectedArtist + "\r\n#SUBARTIST " + expectedSubartist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = "stale", RawTitle = "stale" };
            file = file with { Artist = "stale", RawArtist = "stale" };
            file = file with { Genre = "stale" };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "gb2312", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.RawTitle);
            Assert.AreEqual(expectedSubtitle, file.RawSubtitle);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, file.Title);
            Assert.AreEqual(expectedArtist, file.RawArtist);
            Assert.AreEqual(expectedSubartist, file.Subartist);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, file.Artist);
            Assert.AreEqual(expectedGenre, file.Genre);
            CollectionAssert.AreEqual(new SongMetadataUpdate[] { SongMetadataUpdate.FromChart(file) }, result.SongMetadataUpdates);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実入力メタデータが同じ文字コード指定は譜面保存を増やさず、文字コードと修正済み保守値だけ返して、同値の不要保存や修正状態欠落を検出する。
    /// </summary>
    [TestMethod]
    public void ApplyEncoding_SkipsSongReloadWhenDecodedMetadataMatchesButUpdatesEncoding()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "一致タイトル";
        const string expectedArtist = "一致アーティスト";
        const string expectedGenre = "一致GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n#WAV01 资源.wav\n#WAV02 ascii.wav\n#00111:0102\n",
            Encoding.GetEncoding("gb2312", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllBytes(Path.Combine(tempDirectoryPath, "资源.ogg"), []);
        File.WriteAllBytes(Path.Combine(tempDirectoryPath, "ascii.ogg"), []);
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle, RawTitle = expectedTitle };
            file = file with { Artist = expectedArtist, RawArtist = expectedArtist };
            file = file with { Genre = expectedGenre };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "unknown", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "gb2312");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(expectedGenre, file.Genre);
            Assert.AreEqual("gb2312", file.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(0, result.SongMetadataUpdates.Count);
            Assert.AreEqual(2, file.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(2, file.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            CollectionAssert.AreEquivalent(new[] { "资源.wav", "ascii.wav" }, file.Resources.Select(value => value.RawPath).ToArray());
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 合成題名・artistが同じでも生メタデータが違えば保存対象へ反映し、利用者の追加日・お気に入り・tagは保持して、合成値だけの同値判断や利用者情報の消失を検出する。
    /// </summary>
    [TestMethod]
    public void ApplyEncoding_ReloadsWhenComposedMetadataMatchesButRawMetadataDiffers()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "RawTitle";
        const string expectedSubtitle = "[SP HYPER]";
        const string expectedArtist = "RawArtist";
        const string expectedSubartist = "obj:Raw";
        const string expectedGenre = "GENRE";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#SUBTITLE " + expectedSubtitle + "\r\n#ARTIST " + expectedArtist + "\r\n#SUBARTIST " + expectedSubartist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("shift_jis"));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle + " " + expectedSubtitle, RawTitle = expectedTitle + " " + expectedSubtitle };
            file = file with { Artist = expectedArtist + " " + expectedSubartist, RawArtist = expectedArtist + " " + expectedSubartist };
            file = file with { Genre = expectedGenre, AddDate = 123456, Favorite = 7, Tag = "retained-tag" };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "shift_jis", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], "shift_jis");
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.RawTitle);
            Assert.AreEqual(expectedSubtitle, file.RawSubtitle);
            Assert.AreEqual(expectedTitle + " " + expectedSubtitle, file.Title);
            Assert.AreEqual(expectedArtist, file.RawArtist);
            Assert.AreEqual(expectedSubartist, file.Subartist);
            Assert.AreEqual(expectedArtist + " " + expectedSubartist, file.Artist);
            Assert.AreEqual(expectedGenre, file.Genre);
            Assert.AreEqual(123456, file.AddDate);
            Assert.AreEqual(7, file.Favorite);
            Assert.AreEqual("retained-tag", file.Tag);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            CollectionAssert.AreEqual(new SongMetadataUpdate[] { SongMetadataUpdate.FromChart(file) }, result.SongMetadataUpdates);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 同一メタデータとShift_JISでは空指定・同一文字コード指定とも未確定の修正印だけを確定し、既に確定済みなら完全な同値更新を省略する。
    /// </summary>
    [TestMethod]
    [DataRow("", false)]
    [DataRow("shift_jis", false)]
    [DataRow("", true)]
    [DataRow("shift_jis", true)]
    public void ApplyEncoding_MatchingMetadataAndEncodingOnlyCommitsMissingFixedMark(string requestedEncoding, bool alreadyFixed)
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        const string expectedTitle = "完全一致";
        const string expectedArtist = "一致";
        const string expectedGenre = "MATCH";
        File.WriteAllText(
            bmsFilePath,
            "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE " + expectedGenre + "\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            file = file with { Title = expectedTitle, RawTitle = expectedTitle };
            file = file with { Artist = expectedArtist, RawArtist = expectedArtist };
            file = file with { Genre = expectedGenre };
            file = file with { Token = new OwnedChartToken(), Resources = [], ResourceHealthMaintenanceSnapshot = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath)).ResourceHealthMaintenanceSnapshot };
            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file) with { Encoding = "shift_jis", EncodingFixed = alreadyFixed };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], requestedEncoding);
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(0, result.SongMetadataUpdates.Count);
            Assert.AreEqual(alreadyFixed ? 0 : 1, result.MaintenanceInfosToUpsert.Count);
            Assert.AreEqual(alreadyFixed ? 0 : 1, result.ChangedCharts.Count);
            Assert.AreEqual("shift_jis", file.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(expectedTitle, file.RawTitle);
            Assert.AreEqual(expectedArtist, file.RawArtist);
            Assert.AreEqual(expectedGenre, file.Genre);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実BMSの保守書込みに未適用receiptが返ったら明示的に失敗し、保存未確定を正常な保守結果へ読み替える誤りを検出する。
    /// </summary>
    [TestMethod]
    public void UpdateMaintenanceInfo_RejectsDurableWriterThatReturnsNoReceipt()
    {

        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Durable writer check\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.ASCII);
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> noOpWriter = _ => CatalogMaintenanceWriteReceipt.NotApplied;
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "shift_jis" });

            Assert.ThrowsException<InvalidOperationException>(() =>
                new BmsLibraryMaintenanceService(1).UpdateMaintenanceInfo(
                    [(file)],
                    forceUpdate: true,
                    noOpWriter,
                    null));

        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実保守保存の確定後に不足stagefile・banner・backbmpを変更現在値へ返し、取得不要な参照を解放して保守結果の欠落や短命データの残存を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_ReturnsChangedCurrentValueAfterDurableCommit()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#STAGEFILE missing.png\r\n#BANNER missing_banner.png\r\n#BACKBMP missing_back.bmp\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5 });

            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(file)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.ChangedCharts.Count);
            ChartFile changed = result.ChangedCharts.Single();
            Assert.IsFalse(changed.StagefileHealth.GetValueOrDefault(true));
            Assert.IsFalse(changed.BannerHealth.GetValueOrDefault(true));
            Assert.IsFalse(changed.BackbmpHealth.GetValueOrDefault(true));
            Assert.IsNull(changed.Resources);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実Bmsonの資源不足と任意画像を保守保存し、BMSのsong行や文字コード修正は作らず、形式を越えた保存と評価欠落を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonResourceHealth_UpsertsMaintenanceOnly()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\", \"eyecatch_image\": \"missing.png\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.SongMetadataUpdateCount);
            Assert.AreEqual("Bmson", song.RawTitle);
            Assert.AreEqual("Artist", song.RawArtist);
            Assert.AreEqual("utf-8", song.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsFalse(song.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, song.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(true, song.ResourceHealthMaintenanceSnapshot.StagefileDefined);
            Assert.AreEqual(false, song.ResourceHealthMaintenanceSnapshot.StagefileExisting);

            using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(1, songDb.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.AreEqual(0, songDb.Table<LR2SongDB.song>().Count());
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実Bmsonの保守結果へ入力パス・MD5と音声不足を反映し、BMS対象に集計せず、対象識別の差替えや形式誤分類を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_ReturnsCurrentMaintenanceForBmson()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(0, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.IsNotNull(song.ResourceHealthMaintenanceSnapshot);
            Assert.AreEqual(song.Path, song.ResourceHealthMaintenanceSnapshot.Path);
            Assert.AreEqual(song.Md5, song.ResourceHealthMaintenanceSnapshot.Hash);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, song.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 日時が同じBmsonは取得済み参照を再利用して保守保存し、保守結果では解放してもパッケージは必要期間の参照を保持し、壊れた本文の再取得や早期解放を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonUsesFreshResourceReferencesWithoutReparse()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\", \"eyecatch_image\": \"missing.png\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            ImmutableList<ChartResourceReference>? resources = song.Resources;
            var packageEntry = PackageChartEntry.FromChart((song));
            DateTime timestamp = song.LastWriteTimeUtc;
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, timestamp);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [packageEntry.Chart],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, song.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(true, song.ResourceHealthMaintenanceSnapshot.StagefileDefined);
            Assert.AreEqual(false, song.ResourceHealthMaintenanceSnapshot.StagefileExisting);
            Assert.IsNull(song.Resources);
            Assert.AreSame(resources, packageEntry.Chart.Resources);
            // 本文は既に壊れているため、必要期間の共有が失われて再取得するとこの呼出しは失敗します。
            packageEntry.AcquireResources();
            Assert.AreSame(resources, packageEntry.Chart.Resources);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// Bmsonの実更新日時が変わったら古い取得参照を再利用せず再解析し、壊れた本文では変更なし・再解析失敗を返して、古い成功結果の採用を検出する。
    /// </summary>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonFreshReferencesAreStaleWhenTimestampChanges()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, song.LastWriteTimeUtc.AddMinutes(1));
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(0, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(0, result.ChangedCharts.Count);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 通常・強制とも更新後Bmsonの解析が失敗したら古い保守値を実DBへ保存せず、再解析失敗と変更なしを返して失敗入力の成功保存を検出する。
    /// </summary>
    // falseは通常、trueは明示強制。どちらも更新日時が変わった壊れた本文を入力にする。
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void UpdateMaintenanceInfo_BmsonParseFailureDoesNotPersistStaleMaintenanceInfo(bool forceUpdate)
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            song = song with { ResourceHealthMaintenanceSnapshot = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = song.Path, Hash = "ffffffffffffffffffffffffffffffff", Encoding = "utf-8", WavFilesDefined = 1, WavFilesExisting = 1, BgaFilesDefined = 0, MovieFilesDefined = 0, StagefileDefined = false, BannerDefined = false, BackbmpDefined = false } };
            File.WriteAllText(bmsonFilePath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(bmsonFilePath, song.LastWriteTimeUtc.AddMinutes(1));
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: forceUpdate,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsFalse(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            Assert.AreEqual(0, result.ChangedCharts.Count);
            using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(0, songDb.Table<LR2SongDBExtended.maintenance>().Count());
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保存行相当の参照未取得Bmsonは本文を再解析して保守値を求め、終端では参照を解放して、参照欠落の成功扱いや短命データの残存を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonNonFreshRowReparsesResourceReferences()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile parsed = ChartTestValues.ReadBmson(bmsonFilePath);
            ChartFile loadedLikeRow = ChartTestValues.Empty(ChartFileKind.Bmson) with
            {
                Path = parsed.Path,
                Folder = parsed.Folder,
                RawTitle = parsed.RawTitle,
                Md5 = parsed.Md5,
                Sha256 = parsed.Sha256,
                LastWriteTimeUtc = parsed.LastWriteTimeUtc
            };
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(loadedLikeRow)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null);

            loadedLikeRow = result.ChangedCharts.Single();
            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonReparseFailedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.AreEqual(1, loadedLikeRow.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.IsNull(loadedLikeRow.Resources);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 取得済み参照があっても明示強制ならBmson本文を再解析して保守値を更新し、通常再利用への読み替えを検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonForceUpdateReparsesFreshResourceReferences()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile song = ChartTestValues.ReadBmson(bmsonFilePath);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(song)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == song.Path)) { song = result.ChangedCharts.Single(chart => chart.Path == song.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(0, result.BmsonResourceReferenceReusedCount);
            Assert.IsNull(song.Resources);
            Assert.AreEqual(1, song.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 参照取得済み・未取得のBmsonを通常・強制保守した書込みが未適用なら、一件の保存要求後に明示的に失敗させ、保存未確定の成功化を検出する。
    /// </summary>
    // 第一値は明示強制、第二値は元の参照取得有無。更新後本文の保存未適用を同じ境界で拒否する。
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public void UpdateMaintenanceInfo_BmsonDurableFailureRejectsUnappliedWrite(bool forceUpdate, bool hadResources)
    {

        string directory = Path.Combine(Path.GetTempPath(), nameof(BmsLibraryMaintenanceServiceTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "chart.bmson");
            File.WriteAllText(path, "{\"info\":{\"title\":\"Before\",\"eyecatch_image\":\"before.png\"},\"sound_channels\":[{\"name\":\"before.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}");
            ChartFile song = ChartTestValues.ReadBmson(path);
            song = song with { Resources = hadResources ? song.Resources : null };
            ResourceHealthMaintenanceSnapshot originalInfo = MaintenanceStorageMapping.ToCommon(new LR2SongDBExtended.maintenance { path = song.Path, hash = song.Md5, encoding = "utf-8" });
            song = song with { ResourceHealthMaintenanceSnapshot = originalInfo };
            File.WriteAllText(path, "{\"info\":{\"title\":\"After\",\"eyecatch_image\":\"after.png\"},\"sound_channels\":[{\"name\":\"after.wav\",\"notes\":[{\"x\":1,\"y\":0}]}]}");
            File.SetLastWriteTimeUtc(path, song.LastWriteTimeUtc.AddMinutes(1));
            int writeCount = 0;

            Assert.ThrowsException<InvalidOperationException>(() =>
                new BmsLibraryMaintenanceService(1).UpdateMaintenanceInfo(
                    [(song)],
                    forceUpdate,
                    request =>
                    {
                        writeCount++;
                        Assert.AreEqual(1, request.MaintenanceInfos.Count);
                        return CatalogMaintenanceWriteReceipt.NotApplied;
                    },
                    null));

            Assert.AreEqual(1, writeCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// BMS・Bmson混在の実資源保守を一回の保存へ集め、両形式の不足と短命参照の解放を反映し、BmsonをBMS保存行へ混ぜる誤りや対象別確定を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_MixedBmsAndBmsonResourceHealth_ScansBothButDoesNotCreateBmsonSongRows()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string bmsonFilePath = Path.Combine(tempDirectoryPath, "chart.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#ARTIST Artist\r\n#WAV01 missing-bms.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        File.WriteAllText(
            bmsonFilePath,
            "{ \"info\": { \"title\": \"Bmson\", \"artist\": \"Artist\" }, \"sound_channels\": [{ \"name\": \"missing-bmson.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            ChartFile bmsonSong = ChartTestValues.ReadBmson(bmsonFilePath);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            int durableWriteCount = 0;
            Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> durableWriter = request =>
            {
                durableWriteCount++;
                return CreateDurableWriter(songDbPath)(request);
            };
            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile), (bmsonSong)],
                forceUpdate: true,
                durableWriter,
                null);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsonSong.Path)) { bmsonSong = result.ChangedCharts.Single(chart => chart.Path == bmsonSong.Path); }
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(1, durableWriteCount);
            Assert.AreEqual(1, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.BmsonResourceTargetCount);
            Assert.AreEqual("Bmson", bmsonSong.RawTitle);
            Assert.AreEqual("Artist", bmsonSong.RawArtist);
            Assert.AreEqual("utf-8", bmsonSong.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.AreEqual(1, bmsonSong.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, bmsonSong.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual(1, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.IsNull(bmsFile.Resources);
            Assert.IsNull(bmsonSong.Resources);

            using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(2, songDb.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.IsFalse(songDb.Table<LR2SongDB.song>().ToList().Any(song => song.hash == bmsonSong.Md5));
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 非正の指定並列度を一へ正規化して実保守を行い、資源存在fallbackの件数と診断を接続して、不正並列度の未処理や観測欠落を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_NormalizesNonPositiveHealthDegreeAndReportsFallback()
    {

        var service = new BmsLibraryMaintenanceService(0);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            var lookupContext = new ResourceHealthLookupContext(null);
            var logs = new List<string>();

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null,
                lookupContext,
                progressLogger: logs.Add);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(1, result.HealthDegree);
            Assert.AreEqual(1, result.HealthTargetCount);
            Assert.IsTrue(result.HealthFileExistsFallbackCount > 0);
            Assert.AreEqual(result.HealthFileExistsFallbackCount, lookupContext.FileExistsFallbackCount);
            Assert.IsTrue(logs.Any(log => log.Contains("maintenance_rescan_chunk") && log.Contains("cacheHit=") && log.Contains("resourceIndexHit=") && log.Contains("fileExistsFallback=")));
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 保守・文字コードが取得済みの実BMSは通常要求で検査・保存件数ゼロと対象なし診断を返し、不要な再保守を検出する。
    /// </summary>
    [TestMethod]
    public void UpdateMaintenanceInfo_NoTargetsReportsSummaryAndSkipsWork()
    {

        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        List<string> logs = [];
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BgaFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { MovieFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { StagefileDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BackbmpDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BannerDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = "shift_jis" });
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null,
                progressLogger: logs.Add);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(0, result.CheckedFileCount);
            Assert.AreEqual(0, result.HealthTargetCount);
            Assert.AreEqual(0, result.MaintenanceInfoUpsertCount);
            Assert.IsTrue(logs.Any(log => log.Contains("maintenance_target_summary") && log.Contains("total=0")));
            Assert.IsTrue(logs.Any(log => log.Contains("maintenance_update no_targets")));
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 資源保守値がある文字コード未取得BMSは文字コードだけ保存し、資源検査とFS fallbackを始めず、独立した保守条件の混同を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsMissingEncodingReevaluatesResourcesAfterAdoptingText()
    {

        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 sub\\missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { WavFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BgaFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { MovieFilesDefined = 0 });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { StagefileDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BackbmpDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { BannerDefined = false });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = null });
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });
            var lookupContext = new ResourceHealthLookupContext(null);

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null,
                lookupContext);
            if (result.ChangedCharts.Any(chart => chart.Path == bmsFile.Path)) { bmsFile = result.ChangedCharts.Single(chart => chart.Path == bmsFile.Path); }

            Assert.AreEqual(1, result.CheckedFileCount);
            Assert.AreEqual(0, result.MissingInfoTargetCount);
            Assert.AreEqual(1, result.MissingEncodingTargetCount);
            Assert.AreEqual(1, result.HealthTargetCount);
            Assert.AreEqual(1, result.BmsResourceTargetCount);
            Assert.AreEqual(1, result.HealthFileExistsFallbackCount);
            Assert.AreEqual(1, lookupContext.FileExistsFallbackCount);
            Assert.AreEqual(1, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, bmsFile.ResourceHealthMaintenanceSnapshot.WavFilesExisting);
            Assert.AreEqual("shift_jis", bmsFile.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.AreEqual(1, result.MaintenanceInfoUpsertCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 通常資源保守では無視と既存文字コードを保持し、明示強制の再評価だけ無視を解除して、要求種別の混同を検出する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsResourceHealthPreservesIgnoredFlagUnlessForced()
    {

        var service = new BmsLibraryMaintenanceService(1);
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n#TITLE Bms\r\n#WAV01 missing.wav\r\n#00111:01\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile bmsFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, new ResourceHealthMaintenanceSnapshot { Path = bmsFile.Path, Hash = bmsFile.Md5, Origin = MaintenanceInfoOrigin.Placeholder });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { Encoding = "shift_jis" });
            bmsFile = ChartFileProjection.WithMaintenance(bmsFile, bmsFile.ResourceHealthMaintenanceSnapshot with { FilesWarningIgnored = true });
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            bmsFile = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: false,
                CreateDurableWriter(songDbPath),
                null).ChangedCharts.Single();

            Assert.IsTrue(bmsFile.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
            Assert.AreEqual("shift_jis", bmsFile.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(bmsFile.ResourceHealthMaintenanceSnapshot.GetWavHealth() < 100);

            bmsFile = service.UpdateMaintenanceInfo(
                [(bmsFile)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null).ChangedCharts.Single();

            Assert.IsFalse(bmsFile.ResourceHealthMaintenanceSnapshot.FilesWarningIgnored);
            Assert.AreEqual("shift_jis", bmsFile.ResourceHealthMaintenanceSnapshot.Encoding);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 拡張子別名を含む音声・画像・動画・任意画像の実FS照合と索引照合が同じ健全性を返し、索引利用時はFS fallbackを避けて意味差や二重照合を検出する。
    /// </summary>
    [TestMethod]
    public void UpdateMaintenanceInfo_CacheAwareHealthMatchesFileExistsPathAndAvoidsFallback()
    {

        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "audio"));
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "image"));
        Directory.CreateDirectory(Path.Combine(tempDirectoryPath, "movie"));
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "audio", "hit.ogg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "image", "pic.jpg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "movie", "clip.mp4"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "stage.jpg"), "");
        File.WriteAllText(Path.Combine(tempDirectoryPath, "banner.bmp"), "");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 audio\\hit.wav\r\n"
            + "#WAV02 audio\\missing.wav\r\n#00101:02\r\n"
            + "#BMP01 image\\pic.png\r\n"
            + "#BMP02 movie\\clip.mp4\r\n"
            + "#STAGEFILE stage.png\r\n"
            + "#BANNER banner.png\r\n"
            + "#BACKBMP missing_back.png\r\n"
            + "#00111:01\r\n"
            + "#00104:0102\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile legacyFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            ChartFile cacheAwareFile = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            ResourceHealthMaintenanceSnapshot legacyInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(legacyFile, null, forceUpdate: false);

            var directoryLookupCache = new DirectoryResourceLookupCache();
            string[] relativeResources =
            [
                "audio\\hit.ogg",
                "image\\pic.jpg",
                "movie\\clip.mp4",
                "stage.jpg",
                "banner.bmp"
            ];
            directoryLookupCache.AddDir(tempDirectoryPath, relativeResources);
            var lookupContext = new ResourceHealthLookupContext(directoryLookupCache);
            ResourceHealthMaintenanceSnapshot cacheInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(cacheAwareFile, lookupContext, forceUpdate: false);

            Assert.AreEqual(legacyInfo.WavFilesDefined, cacheInfo.WavFilesDefined);
            Assert.AreEqual(legacyInfo.WavFilesExisting, cacheInfo.WavFilesExisting);
            Assert.AreEqual(legacyInfo.BgaFilesDefined, cacheInfo.BgaFilesDefined);
            Assert.AreEqual(legacyInfo.BgaFilesExisting, cacheInfo.BgaFilesExisting);
            Assert.AreEqual(legacyInfo.MovieFilesDefined, cacheInfo.MovieFilesDefined);
            Assert.AreEqual(legacyInfo.MovieFilesExisting, cacheInfo.MovieFilesExisting);
            Assert.AreEqual(legacyInfo.StagefileExisting, cacheInfo.StagefileExisting);
            Assert.AreEqual(legacyInfo.BannerExisting, cacheInfo.BannerExisting);
            Assert.AreEqual(legacyInfo.BackbmpExisting, cacheInfo.BackbmpExisting);
            Assert.IsTrue(lookupContext.CacheHitCount >= 6);
            Assert.AreEqual(0, lookupContext.FileExistsFallbackCount);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 索引がない実資源照合は音声・画像・動画・任意画像ごとのFS fallbackを診断へ残し、種類別の観測欠落を検出する。
    /// </summary>
    [TestMethod]
    public void UpdateMaintenanceInfo_ReportsFallbackKinds()
    {

        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        File.WriteAllText(
            bmsFilePath,
            "#PLAYER 1\r\n"
            + "#WAV01 sub\\missing.wav\r\n"
            + "#BMP01 image\\missing.png\r\n"
            + "#BMP02 movie\\missing.mp4\r\n"
            + "#STAGEFILE missing_stage.png\r\n"
            + "#00111:01\r\n"
            + "#00104:0102\r\n",
            Encoding.GetEncoding("shift_jis", new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(bmsFilePath));
            var lookupContext = new ResourceHealthLookupContext(null);
            ResourceHealthMaintenanceSnapshot info = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(file, lookupContext, forceUpdate: false);

            Assert.IsTrue(lookupContext.FileExistsFallbackCount >= 4);
            Assert.IsTrue(lookupContext.AudioFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.ImageFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.MovieFileExistsFallbackCount >= 1);
            Assert.IsTrue(lookupContext.OptionalImageFileExistsFallbackCount >= 1);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 壊れたBmsonと正常Bmsonが混在しても正常対象だけを実保守保存し、解析失敗一件と正常対象の保守値・保存行を確認する。
    /// </summary>
    /// <remarks>実入力の読取りと実SQLite書込みを保ち、保存receiptを通った現在値を観測する。</remarks>
    [TestMethod]
    public void UpdateMaintenanceInfo_BmsonParseFailure_DoesNotAbortMaintenance()
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string invalidBmsonPath = Path.Combine(tempDirectoryPath, "invalid.bmson");
        string validBmsonPath = Path.Combine(tempDirectoryPath, "valid.bmson");
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        File.WriteAllText(invalidBmsonPath, "{ \"info\": { \"title\": \"Broken\" }, \"bga\": \"unterminated", new UTF8Encoding(false));
        File.WriteAllText(
            validBmsonPath,
            "{ \"info\": { \"title\": \"Valid\", \"artist\": \"Artist\" }, \"sound_channels\": [{ \"name\": \"missing.wav\", \"notes\": [{\"x\":1,\"y\":0}] }] }",
            new UTF8Encoding(false));
        try
        {
            ChartFile invalidRow = CreateBmsonSong(invalidBmsonPath, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            ChartFile validRow = ChartTestValues.ReadBmson(validBmsonPath);
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDB.song>();
            });

            MaintenanceWorkflowResult result = service.UpdateMaintenanceInfo(
                [(invalidRow), (validRow)],
                forceUpdate: true,
                CreateDurableWriter(songDbPath),
                null);

            Assert.IsTrue(result.HasUpdates);
            Assert.AreEqual(2, result.BmsonResourceTargetCount);
            Assert.AreEqual(1, result.BmsonReparsedCount);
            Assert.AreEqual(1, result.BmsonReparseFailedCount);
            Assert.AreEqual(1, result.MaintenanceInfoUpsertCount);
            Assert.AreEqual(0, result.SongMetadataUpdateCount);
            validRow = result.ChangedCharts.Single(chart => chart.Path == validRow.Path);
            Assert.AreEqual("Valid", validRow.RawTitle);
            Assert.AreEqual(1, validRow.ResourceHealthMaintenanceSnapshot.WavFilesDefined);
            Assert.AreEqual(0, validRow.ResourceHealthMaintenanceSnapshot.WavFilesExisting);

            using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                List<LR2SongDBExtended.maintenance> rows = [.. songDb.Table<LR2SongDBExtended.maintenance>()];
                Assert.AreEqual(1, rows.Count);
                Assert.AreEqual(validRow.Path, rows[0].path);
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 実DBの指定した古い保守行だけを削除し、現在BMS・Bmsonの行を保持して、形式や対象範囲を越えた削除を検出する。
    /// </summary>
    [TestMethod]
    public void DeleteMaintenanceRows_DeletesOnlyRequestedRows()
    {
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        try
        {
            ChartFile bms = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            bms = bms with { Path = Path.Combine(tempDirectoryPath, "chart.bms") };
            ChartFile bmson = CreateBmsonSong(Path.Combine(tempDirectoryPath, "chart.bmson"), "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
            using (var songDb = new LR2SongDBExtended(songDbPath))
            {
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = bms.Path, hash = bms.Md5 }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = bmson.Path, hash = bmson.Md5, encoding = "utf-8" }, typeof(LR2SongDBExtended.maintenance));
                songDb.InsertOrReplace(new LR2SongDBExtended.maintenance { path = Path.Combine(tempDirectoryPath, "stale.bms"), hash = "cccccccccccccccccccccccccccccccc" }, typeof(LR2SongDBExtended.maintenance));
            }

            int deleted = new BmsLibraryDbGateway(songDbPath).DeleteMaintenanceRows([Path.Combine(tempDirectoryPath, "stale.bms")]);

            Assert.AreEqual(1, deleted);
            using (var songDb = new LR2SongDBExtended(songDbPath, SQLite.SQLiteOpenFlags.ReadOnly, acquireProcessLock: false))
            {
                Assert.AreEqual(2, songDb.Table<LR2SongDBExtended.maintenance>().Count());
                Assert.IsTrue(songDb.Table<LR2SongDBExtended.maintenance>().Any(info => info.path == bms.Path));
                Assert.IsTrue(songDb.Table<LR2SongDBExtended.maintenance>().Any(info => info.path == bmson.Path));
            }
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// GB2312・Big5・韓国語CP949の実入力を利用者指定文字コードで再読解し、題名・artist・修正済み状態と保存対象へ反映して、指定無視や非ASCII文字の破損を検出する。
    /// </summary>
    [DataTestMethod]
    [DataRow("gb2312", "\u7b80\u4f53\u6807\u9898", "\u7b80\u4f53\u4f5c\u8005")]
    [DataRow("big5", "\u7e41\u9ad4\u6a19\u984c", "\u7e41\u9ad4\u4f5c\u8005")]
    [DataRow("ks_c_5601-1987", "\ud55c\uad6d\uc5b4\uc81c\ubaa9", "\ud55c\uad6d\uc5b4\uc791\uac00")]
    public void ApplyEncoding_ReloadsRequestedEncodingAndMarksFixed(string encoding, string expectedTitle, string expectedArtist)
    {

        var service = new BmsLibraryMaintenanceService();
        string tempDirectoryPath = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string bmsFilePath = Path.Combine(tempDirectoryPath, "chart.bms");
        string bmsContent = "#TITLE " + expectedTitle + "\r\n#ARTIST " + expectedArtist + "\r\n#GENRE TEST\r\n";
        File.WriteAllText(
            bmsFilePath,
            bmsContent,
            Encoding.GetEncoding(encoding, new EncoderExceptionFallback(), new DecoderExceptionFallback()));
        try
        {
            ChartFile file = CreateFile("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
            file = file with { Path = bmsFilePath };
            var info = new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, Encoding = "unknown", EncodingFixed = false };
            file = ChartFileProjection.WithMaintenance(file, info);

            MaintenanceEncodingUpdateResult result = service.ApplyEncoding([file], encoding);
            if (result.ChangedCharts.Count > 0) { file = result.ChangedCharts.Single(); }

            Assert.AreEqual(expectedTitle, file.Title);
            Assert.AreEqual(expectedArtist, file.Artist);
            Assert.AreEqual(encoding, file.ResourceHealthMaintenanceSnapshot.Encoding);
            Assert.IsTrue(file.ResourceHealthMaintenanceSnapshot.EncodingFixed);
            CollectionAssert.AreEqual(new SongMetadataUpdate[] { SongMetadataUpdate.FromChart(file) }, result.SongMetadataUpdates);
            CollectionAssert.AreEqual(new ResourceHealthMaintenanceSnapshot[] { file.ResourceHealthMaintenanceSnapshot }, result.MaintenanceInfosToUpsert);
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    /// <summary>
    /// 拡張子別名の資源存在結果を共有しても原記述のCP932長でLR2警告を別に判定し、存在キャッシュの再利用による互換長の混同を検出する。
    /// </summary>
    [TestMethod]
    public void BuildResourceHealthSnapshot_SharedExistenceCountsKeepOriginalLr2LengthsDistinct()
    {
        string directory = Path.Combine(Path.GetTempPath(), "BeMusicSeekerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string stem = new string('a', 259 - directory.Length - 1 - ".wav".Length);
            var cache = new DirectoryResourceLookupCache();
            cache.AddDir(directory, [stem + ".wav"]);
            var context = new ResourceHealthLookupContext(cache);
            ChartFile Parse(string extension, string hash) => BmsChartFileParser.ParseSnapshot(new ChartFileSnapshot(Path.Combine(directory, "chart.bms"),
                Encoding.ASCII.GetBytes("#WAV01 " + stem + extension + "\n#00111:01\n"), DateTime.UnixEpoch, hash, "sha256"));
            ChartFile wav = Parse(".wav", new string('a', 32));
            ChartFile flac = Parse(".flac", new string('b', 32));
            ResourceHealthMaintenanceSnapshot wavInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(wav, context);
            ResourceHealthMaintenanceSnapshot flacInfo = BmsLibraryMaintenanceService.BuildResourceHealthSnapshot(flac, context);
            Assert.AreEqual(1, wavInfo.WavFilesExisting);
            Assert.AreEqual(1, flacInfo.WavFilesExisting);
            Assert.AreEqual(1L, context.ResourceHealthSetCacheHitCount);
            Assert.AreEqual((int)Lr2CompatibilityWarningFlags.None, wavInfo.Lr2WarningFlags);
            Assert.AreEqual((int)Lr2CompatibilityWarningFlags.ResourcePathTooLong, flacInfo.Lr2WarningFlags);
            Assert.AreEqual(wavInfo.Lr2ResourceMaxRelativeCp932Bytes + 1, flacInfo.Lr2ResourceMaxRelativeCp932Bytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ChartFile CreateProjectionOnlyBmsChart(
        string path,
        string md5,
        string stagefile = "",
        string backbmp = "",
        string banner = "",
        IReadOnlyList<string>? audioResourcePaths = null,
        IReadOnlyList<string>? visualResourcePaths = null)
    {
        return new ChartFile(kind: ChartFileKind.Bms, path: path, md5: md5, sha256: new string('a', 64), title: "Title", rawTitle: "Title", artist: "Artist", genre: string.Empty, folder: Path.GetFileName(Path.GetDirectoryName(path)), tag: string.Empty, levelText: string.Empty, level: null, mode: null, chartInfo: null, resources: TestChartResources.Create(audioResourcePaths, visualResourcePaths, stagefile, backbmp, banner), stagefile: stagefile, backbmp: backbmp, banner: banner);
    }

    private static ChartFile CreateFile(string hash)
    {
        ChartFile file = ChartTestValues.Empty();
        file = file with { Md5 = hash };
        return file;
    }

    private static ChartFile CreateResourceHealthChart(
        string path,
        string hash,
        int wavFilesDefined = 0,
        int wavFilesExisting = 0,
        int bgaFilesDefined = 0,
        int bgaFilesExisting = 0,
        int movieFilesDefined = 0,
        int movieFilesExisting = 0,
        bool isIgnored = false)
    {
        ChartFile file = CreateFile(hash);
        file = file with { Path = path };
        file = ChartFileProjection.WithMaintenance(file, new ResourceHealthMaintenanceSnapshot { Origin = MaintenanceInfoOrigin.Calculated, Path = file.Path, Hash = file.Md5, WavFilesDefined = wavFilesDefined, WavFilesExisting = wavFilesExisting, BgaFilesDefined = bgaFilesDefined, BgaFilesExisting = bgaFilesExisting, MovieFilesDefined = movieFilesDefined, MovieFilesExisting = movieFilesExisting, FilesWarningIgnored = isIgnored });
        return (file);
    }

    private static ChartFile CreateBmsonSong(string path, string md5)
    {
        ChartFile song = ChartTestValues.Empty(ChartFileKind.Bmson) with
        {
            Path = path,
            Folder = Path.GetDirectoryName(path),
            RawTitle = "Bmson Title",
            RawArtist = "Bmson Artist",
            Md5 = md5,
            Sha256 = new string('b', 64)
        };
        return song;
    }

    private static BeMusicSeeker.Models.ChartDetails CreateChartInfo(string md5, int notes)
    {
        return new BeMusicSeeker.Models.ChartDetails
        {
            md5 = md5,
            sha256 = new string('a', 64),
            parser_version = BmsLibraryDbGateway.CurrentChartInfoParserVersion,
            notes = notes
        };
    }

    private static Func<ChartFile, BeMusicSeeker.Models.ChartDetails> CreateChartInfoResolver(params BeMusicSeeker.Models.ChartDetails[] rows)
    {
        return chart =>
        {
            if (chart == null)
            {
                return null!;
            }
            return (rows ?? [])
                .Where(row => row != null)
                .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Sha256) && string.Equals(row.sha256, chart.Sha256, StringComparison.OrdinalIgnoreCase))
                ?? (rows ?? [])
                    .Where(row => row != null)
                    .FirstOrDefault(row => !string.IsNullOrWhiteSpace(chart.Md5) && string.Equals(row.md5, chart.Md5, StringComparison.OrdinalIgnoreCase))
                ?? null!;
        };
    }

    private static void SetStorageRows(
        BMSLibrary library,
        IReadOnlyList<ChartFile> bmsFiles,
        IReadOnlyList<ChartFile> bmsonSongs)
    {
        library.BmsCharts = bmsFiles;
        library.BmsonCharts = bmsonSongs;
    }

    private static void EnsureCurrentResourceHealthIndex(BMSLibrary library)
    {
        _ = library.GetResourceHealthIndexSnapshotForView("test_resource_health");
    }

    private static ResourceHealthIndexMutation BuildMaintenanceResourceHealthMutation(
        List<ChartFile> charts,
        ResourceHealthIndexUpdateMode resourceHealthIndexUpdateMode,
        bool resourceHealthIndexCurrent,
        bool workflowHasUpdates)
    {
        var targetSet = ResourceMaintenanceTargetSet.ForSubset(charts);
        return ResourceHealthIndexMutationPlanner.BuildMaintenanceMutation(
            targetSet,
            resourceHealthIndexUpdateMode,
            resourceHealthIndexCurrent,
            workflowHasUpdates);
    }

    private static void WithTemporarySongDb(Action<string> testAction)
    {
        string tempRootPath = Path.Combine(Path.GetTempPath(), "BeMusicSeeker_MaintenanceTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRootPath);
        string songDbPath = Path.Combine(tempRootPath, "song.db");
        try
        {
            BmsLibraryInitializationTestSupport.ExecuteSongDbFixtureTransaction(songDbPath, songDb =>
            {
                songDb.CreateTable<LR2SongDB.song>();
                songDb.CreateTable<LR2SongDB.folder>();
                songDb.CreateTable<LR2SongDBExtended.maintenance>();
                songDb.CreateTable<LR2SongDBExtended.bmson_song>();
            });
            testAction(songDbPath);
        }
        finally
        {
            if (Directory.Exists(tempRootPath))
            {
                Directory.Delete(tempRootPath, recursive: true);
            }
        }
    }

    private static Func<CatalogMaintenanceWriteRequest, CatalogMaintenanceWriteReceipt> CreateDurableWriter(string songDbPath)
    {
        var owner = new CatalogMutationOwner(new CatalogOwnedCollectionOwner(), new BmsLibraryDbGateway(songDbPath));
        return owner.ApplyMaintenanceWrite;
    }

}
