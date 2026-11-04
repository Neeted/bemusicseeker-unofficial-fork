using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsLibraryDuplicateServiceTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 作成済みの警告が、言語を変えても再評価なしに現在の言語で短文(WARNING列)と詳細(tooltip)を返すことを、
/// 通常一覧・保留・プレイリスト詳細の各行と実ライブラリの重複検索で確認します。
/// </summary>
[TestClass]
public sealed class ChartWarningLanguageTests
{
    private const string FirstCandidate = @"C:\Candidate\P1";
    private const string SecondCandidate = @"C:\Candidate\P2";

    /// <summary>
    /// 日本語で作成した全種類の警告を英語・日本語へ切り替えて読み直し、短文と詳細が現在語になり、
    /// 種別と事実・強調・除外状態・版・重複群・実体参照が変わらず、再評価が起きないことを確認します。
    /// 警告が生成時の言語の文字列を保持する誤りと、言語変更で再評価・通知が走る誤りを検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void ExistingWarnings_FollowCultureInEveryRowWithoutReevaluation()
    {
        var databaseScope = new ApplicationDatabaseScope();
        CultureInfo? previousResourcesCulture = Resources.Culture;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            WithTemporarySongDb(databaseScope, songDbPath =>
            {
                string md5 = new string('a', 32);
                ChartFile a = CreateFile(md5, @"C:\BMS\DirA\a.bms");
                a = ChartFileProjection.WithWarnings(a,
                    [ChartWarning.Create(ChartWarningKind.Lr2PathEncodingUnsupported), ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 50, 1, 2)]);
                ChartFile b = CreateFile(md5, @"C:\BMS\DirB\b.bms");
                ChartFile c = ChartTestValues.Empty(ChartFileKind.Bmson) with
                {
                    Path = @"C:\BMS\DirC\c.bmson",
                    Md5 = md5,
                    Token = new OwnedChartToken()
                };
                ChartFile ignored = CreateFile(new string('c', 32), @"C:\BMS\DirR\r.bms") with { ResourceHealthWarningsIgnored = true };
                ignored = ChartFileProjection.WithWarnings(ignored, [ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 0, 0, 3)]);
                ChartFile imageOnly = ChartFileProjection.WithWarnings(CreateFile(new string('d', 32), @"C:\BMS\DirI\i.bms"),
                    [ChartWarning.Create(ChartWarningKind.ResourceStagefileMissing)]);
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null))
                {
                    BmsCharts = [a, b, ignored, imageOnly],
                    BmsonCharts = [c]
                };
                try
                {
                    library.SearchDuplicateChartGroups();

                    var pendingP = PackageChartEntry.FromChart(CreateFile(new string('e', 32), @"C:\Pending\p.bms"));
                    pendingP.SetWarning(ChartWarningKind.SingleBmsFile);
                    var estimation = new InstallEstimationResult
                    {
                        Confidence = InstallEstimationConfidence.Low,
                        HasViableDestination = true,
                        LowConfidenceKind = InstallEstimationLowConfidenceKind.AmbiguousCandidates
                    };
                    estimation.Candidates.Add(new InstallEstimationCandidate { DirectoryPath = FirstCandidate, MetadataRelation = InstallEstimationMetadataRelation.Related, DisplayTitle = "T", DisplayArtist = "A" });
                    estimation.Candidates.Add(new InstallEstimationCandidate { DirectoryPath = SecondCandidate, MetadataRelation = InstallEstimationMetadataRelation.Related, DisplayTitle = "T", DisplayArtist = "A" });
                    pendingP.ApplyInstallEstimationResult(estimation);
                    var pendingQ = PackageChartEntry.FromChart(CreateFile(new string('f', 32), @"C:\Pending\q.bms"));
                    pendingQ.ApplySourceSurfaceScanLimitExceededWarning(50000);
                    var pendingImage = PackageChartEntry.FromChart(imageOnly);

                    CatalogChartCollectionSnapshot snapshot = library.CaptureNormalLibraryChartSnapshot();
                    ChartFile currentA = snapshot.BmsRows.Single(row => row.Path == a.Path);
                    var pendingA = PackageChartEntry.FromChart(currentA);
                    ChartFile currentIgnored = snapshot.BmsRows.Single(row => row.Path == ignored.Path);
                    ChartFile currentImage = snapshot.BmsRows.Single(row => row.Path == imageOnly.Path);
                    var playlistA = new PlaylistDetailSourceRow(new BMSTableEntry(currentA), currentA);
                    var playlistImage = new PlaylistDetailSourceRow(new BMSTableEntry(currentImage), currentImage);
                    List<DuplicateGroup> duplicateGroups = library.DuplicateChartGroups;
                    Assert.AreEqual(1, duplicateGroups.Count, "同じMD5のBMS二件とBMSONが一つの重複群になります。");
                    Assert.AreEqual(3, duplicateGroups[0].ChartFiles.Count);

                    ChartWarning[][] factsBefore =
                    [
                        [.. currentA.Warnings], [.. pendingP.Chart.Warnings], [.. pendingQ.Chart.Warnings], [.. currentIgnored.Warnings], [.. currentImage.Warnings],
                        .. duplicateGroups[0].ChartFiles.Select(chart => chart.Warnings.ToArray()),
                    ];
                    int revision = library.CatalogRevision;
                    int notificationVersion = library.NormalLibraryRefreshNotificationVersion;

                    void AssertCurrentLanguage(string phase)
                    {
                        // 期待値は現在の資源と、生成時の事実から作ります。
                        string[] aKinds = [Resources.WarningDigest_Lr2PathEncodingUnsupported, Resources.WarningDigest_DuplicateChart, Resources.WarningDigest_ResourceMissing];
                        string aDigest = "[3] " + string.Join(", ", aKinds);
                        string aDetail = string.Join(Environment.NewLine, Resources.Warning_Lr2PathEncodingUnsupported, Resources.Warning_DuplicateBmsFile,
                            string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2));
                        var normalA = LibraryChartRow.FromChartFile(currentA);
                        var pendingRowA = LibraryChartRow.FromPackageChartEntry(pendingA);
                        Assert.AreEqual(aDigest, normalA.WarningDigestText, phase + " 通常行の短文");
                        Assert.AreEqual(aDetail, normalA.WarningTooltipText, phase + " 通常行の詳細");
                        Assert.AreEqual(aDigest, playlistA.WarningDigestText, phase + " プレイリスト詳細の短文");
                        Assert.AreEqual(aDetail, playlistA.WarningTooltipText, phase + " プレイリスト詳細の詳細");
                        Assert.IsTrue(normalA.HasHighlightedWarning);
                        Assert.IsTrue(playlistA.HasHighlightedWarning);
                        Assert.AreEqual(aDigest, pendingRowA.WarningDigestText, phase + " 通常行由来の保留行の短文");
                        Assert.AreEqual(aDetail, pendingRowA.WarningTooltipText, phase + " 通常行由来の保留行の詳細");

                        var pendingRowP = LibraryChartRow.FromPackageChartEntry(pendingP);
                        Assert.AreEqual("[2] " + Resources.WarningDigest_InstallEstimationAmbiguous + ", " + Resources.WarningDigest_SingleBmsFile, pendingRowP.WarningDigestText, phase + " 保留Pの短文");
                        Assert.AreEqual(string.Join(Environment.NewLine, string.Format(Resources.Warning_InstallEstimationAmbiguous, FirstCandidate, SecondCandidate), Resources.Warning_SingleBmsFile),
                            pendingRowP.WarningTooltipText, phase + " 保留Pの詳細");
                        Assert.IsTrue(pendingRowP.HasHighlightedWarning);

                        var pendingRowQ = LibraryChartRow.FromPackageChartEntry(pendingQ);
                        Assert.AreEqual("[1] " + Resources.WarningDigest_SourceSurfaceScanLimitExceeded, pendingRowQ.WarningDigestText, phase + " 保留Qの短文");
                        Assert.AreEqual(string.Format(Resources.Warning_SourceSurfaceScanLimitExceeded, 50000), pendingRowQ.WarningTooltipText, phase + " 保留Qの詳細");
                        Assert.IsFalse(pendingRowQ.HasHighlightedWarning);

                        foreach (ChartFile member in duplicateGroups[0].ChartFiles)
                        {
                            var memberRow = LibraryChartRow.FromChartFile(member);
                            StringAssert.Contains(memberRow.WarningDigestText, Resources.WarningDigest_DuplicateChart, phase + " 重複群の短文");
                            StringAssert.Contains(memberRow.WarningTooltipText, Resources.Warning_DuplicateBmsFile, phase + " 重複群の詳細");
                        }

                        // 除外済みの行も、状態を変えずに現在語で表示します。
                        var ignoredRow = LibraryChartRow.FromChartFile(currentIgnored);
                        Assert.IsTrue(currentIgnored.ResourceHealthWarningsIgnored);
                        Assert.AreEqual("[1] " + Resources.WarningDigest_ResourceMissing, ignoredRow.WarningDigestText, phase + " 除外行の短文");
                        Assert.AreEqual(string.Format(Resources.Warning_WavFilesNotFound, 0, 0, 3), ignoredRow.WarningTooltipText, phase + " 除外行の詳細");

                        // 画像不足だけの行は、どの一覧でも短文が空で、詳細だけをtooltipに出し、三つの一覧で一致します。
                        var normalImage = LibraryChartRow.FromChartFile(currentImage);
                        var pendingImageRow = LibraryChartRow.FromPackageChartEntry(pendingImage);
                        foreach ((string name, string digest, string detail) in new[]
                        {
                            ("通常", normalImage.WarningDigestText, normalImage.WarningTooltipText),
                            ("保留", pendingImageRow.WarningDigestText, pendingImageRow.WarningTooltipText),
                            ("プレイリスト詳細", playlistImage.WarningDigestText, playlistImage.WarningTooltipText),
                        })
                        {
                            Assert.AreEqual(string.Empty, digest, phase + " 画像不足の短文(" + name + ")は空です。");
                            Assert.AreEqual(Resources.Warning_StagefileNotFound, detail, phase + " 画像不足の詳細(" + name + ")");
                        }

                        // 種別と事実・版・参照は言語に依存せず不変で、再評価・通知・再構築が起きません。
                        ChartWarning[][] factsNow =
                        [
                            [.. currentA.Warnings], [.. pendingP.Chart.Warnings], [.. pendingQ.Chart.Warnings], [.. currentIgnored.Warnings], [.. currentImage.Warnings],
                            .. duplicateGroups[0].ChartFiles.Select(chart => chart.Warnings.ToArray()),
                        ];
                        for (int i = 0; i < factsBefore.Length; i++) { CollectionAssert.AreEqual(factsBefore[i], factsNow[i], phase + " 警告の事実[" + i + "]"); }
                        Assert.AreEqual(revision, library.CatalogRevision, phase + " カタログ版");
                        Assert.AreEqual(notificationVersion, library.NormalLibraryRefreshNotificationVersion, phase + " 一覧通知の版");
                        Assert.AreSame(duplicateGroups, library.DuplicateChartGroups, phase + " 重複群");
                        Assert.AreSame(currentA, library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(row => row.Path == a.Path), phase + " 譜面の現在値");
                    }

                    AssertCurrentLanguage("ja");
                    string japaneseDigest = LibraryChartRow.FromChartFile(currentA).WarningDigestText;
                    ResourceService.Current.ChangeCulture("en-US");
                    AssertCurrentLanguage("en");
                    Assert.AreNotEqual(japaneseDigest, LibraryChartRow.FromChartFile(currentA).WarningDigestText, "英語へ切り替えると短文が変わります。");
                    ResourceService.Current.ChangeCulture("ja-JP");
                    AssertCurrentLanguage("ja(再)");
                }
                finally
                {
                    library.RequestShutdown("warning_language_existing_test");
                }
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
        }
    }

    /// <summary>
    /// 言語変更後の通常更新(同じ事実の再評価、重複キャッシュの失効と再検索、警告の事実の変化、新しい警告の作成)でも、
    /// 変わらない事実では版を進めず、すべての行が現在の言語のまま表示されることを確認します。
    /// 再評価のたびに旧語へ戻る誤りと、同じ事実なのに版・通知を進める誤りを検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void ReevaluationAfterCultureChange_KeepsUnchangedFactsAndUsesCurrentLanguage()
    {
        var databaseScope = new ApplicationDatabaseScope();
        CultureInfo? previousResourcesCulture = Resources.Culture;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            WithTemporarySongDb(databaseScope, songDbPath =>
            {
                string md5 = new string('a', 32);
                ChartFile a = ChartFileProjection.WithWarnings(CreateFile(md5, @"C:\BMS\DirA\a.bms"),
                    [ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 50, 1, 2)]);
                ChartFile b = CreateFile(md5, @"C:\BMS\DirB\b.bms");
                ChartFile c = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"C:\BMS\DirC\c.bmson", Md5 = md5, Token = new OwnedChartToken() };
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null))
                {
                    BmsCharts = [a, b],
                    BmsonCharts = [c]
                };
                try
                {
                    library.SearchDuplicateChartGroups();
                    ChartFile Current(string path) => library.CaptureNormalLibraryChartSnapshot().BmsRows.Single(row => row.Path == path);
                    var pendingQ = PackageChartEntry.FromChart(CreateFile(new string('f', 32), @"C:\Pending\q.bms"));

                    ResourceService.Current.ChangeCulture("en-US");

                    // (1) 同じ事実の警告を作り直しても変更なしで、版も通知も進みません。
                    CatalogOwnedCollectionOwner catalog = OwnedChartCollectionTestSupport.GetOwnedCollectionOwner(library);
                    int revision = library.CatalogRevision;
                    int notificationVersion = library.NormalLibraryRefreshNotificationVersion;
                    CatalogCommitReceipt same = catalog.CommitCurrentValues([Current(a.Path)], (current, _) => current with
                    {
                        Warnings = [.. current.Warnings.Select(warning => warning.Kind == ChartWarningKind.ResourceWavMissing
                            ? ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 50, 1, 2) : warning)]
                    });
                    Assert.IsFalse(same.HasChanges);
                    Assert.AreEqual(revision, library.CatalogRevision);
                    Assert.AreEqual(notificationVersion, library.NormalLibraryRefreshNotificationVersion);

                    // (2) 無関係なBMSONの追加で重複キャッシュを失効して再検索しても、BとBMSONの警告は同じ事実のまま現在語で表示します。
                    ChartFile unrelated = ChartTestValues.Empty(ChartFileKind.Bmson) with { Path = @"C:\BMS\DirX\x.bmson", Md5 = new string('9', 32), Token = new OwnedChartToken() };
                    library.BmsonCharts = [c, unrelated];
                    int afterSetter = library.CatalogRevision;
                    library.SearchDuplicateChartGroups();
                    Assert.AreEqual(afterSetter, library.CatalogRevision, "同じ重複の事実ではBの版を増やしません。");
                    foreach (ChartFile member in library.DuplicateChartGroups.Single().ChartFiles)
                    {
                        var memberRow = LibraryChartRow.FromChartFile(member);
                        StringAssert.Contains(memberRow.WarningDigestText, Resources.WarningDigest_DuplicateChart);
                        StringAssert.Contains(memberRow.WarningTooltipText, Resources.Warning_DuplicateBmsFile);
                    }

                    // (3) Aの資源の事実が変わると版が進み、Aのtooltipは新しい件数の現在語になります。他の行も現在語のままです。
                    int beforeResource = library.CatalogRevision;
                    catalog.CommitCurrentValues([Current(a.Path)], (current, _) => current with
                    {
                        Warnings = [.. current.Warnings.Select(warning => warning.Kind == ChartWarningKind.ResourceWavMissing
                            ? ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 75, 3, 4) : warning)]
                    });
                    Assert.AreEqual(beforeResource + 1, library.CatalogRevision);
                    var rowA = LibraryChartRow.FromChartFile(Current(a.Path));
                    StringAssert.Contains(rowA.WarningTooltipText, string.Format(Resources.Warning_WavFilesNotFound, 75, 3, 4));
                    Assert.AreEqual(Resources.Warning_DuplicateBmsFile, LibraryChartRow.FromChartFile(Current(b.Path)).WarningTooltipText.Split(Environment.NewLine)[0]);

                    // (4) 英語で件数1の警告を作り、日本語へ切り替えると日本語になります。
                    pendingQ.ApplySourceSurfaceScanLimitExceededWarning(1);
                    Assert.AreEqual(string.Format(Resources.Warning_SourceSurfaceScanLimitExceeded, 1), LibraryChartRow.FromPackageChartEntry(pendingQ).WarningTooltipText);
                    string englishDetail = LibraryChartRow.FromPackageChartEntry(pendingQ).WarningTooltipText;
                    ResourceService.Current.ChangeCulture("ja-JP");
                    Assert.AreEqual(string.Format(Resources.Warning_SourceSurfaceScanLimitExceeded, 1), LibraryChartRow.FromPackageChartEntry(pendingQ).WarningTooltipText);
                    Assert.AreNotEqual(englishDetail, LibraryChartRow.FromPackageChartEntry(pendingQ).WarningTooltipText);
                }
                finally
                {
                    library.RequestShutdown("warning_language_reevaluation_test");
                }
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
        }
    }

    /// <summary>
    /// 資源健康度の警告を、`Warnings` へ直接入れず実ライブラリの保守と健全性索引の投影経路で作り、
    /// 通常行・保留行・プレイリスト詳細行の短文と詳細が言語の往復で現在語になること、
    /// 同じ事実の `RescanResourceHealthCharts` が更新なし(upsert 0・版・索引・DB・投影版が不変)であることを確認します。
    /// 投影経路の文言固定と、言語変更後の再評価で更新を増やす誤りを検出します。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public void ResourceHealthProjection_FollowsCultureAndRescanOfSameFactsIsNoOp()
    {
        var databaseScope = new ApplicationDatabaseScope();
        CultureInfo? previousResourcesCulture = Resources.Culture;
        try
        {
            ResourceService.Current.ChangeCulture("ja-JP");
            WithTemporarySongDb(databaseScope, songDbPath =>
            {
                string root = Path.GetDirectoryName(songDbPath) ?? throw new InvalidOperationException("Song DB has no parent directory.");
                string chartPath = Path.Combine(root, "rh.bms");
                File.WriteAllText(chartPath, "#PLAYER 1\r\n#TITLE RH\r\n#WAV01 missing.wav\r\n#WAV02 present.wav\r\n#00111:0102\r\n");
                File.WriteAllText(Path.Combine(root, "present.wav"), "audio");
                ChartFile parsed = BmsChartFileParser.ParseSnapshot(ChartFileContentReader.ReadSnapshot(chartPath));
                new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDbPath).UpsertSongs([parsed]);
                var library = new TestBmsLibrary(databaseScope, songDbPath, null, null, new TestFileMutationService(), new RecordingDialogService(), new TestUiScheduler(() => null))
                {
                    BmsCharts = [parsed]
                };
                try
                {
                    MaintenanceWorkflowResult first = library.RescanResourceHealthCharts([library.CaptureNormalLibraryChartSnapshot().BmsRows[0]]);
                    Assert.IsTrue(first.HasUpdates, "初回の保守で資源の事実を保存します。");
                    ChartFile current = library.CaptureNormalLibraryChartSnapshot().BmsRows[0];
                    Assert.IsFalse(current.Warnings.Any(warning => warning.Category == ChartWarningCategory.ResourceHealth),
                        "資源健康度の警告は現在値の Warnings ではなく索引の投影から表示します。");
                    OwnedChartCollectionTestSupport.EnsureCurrentResourceHealthIndex(library);
                    ResourceHealthIndexSnapshot snapshot = library.TryGetCurrentResourceHealthIndexSnapshotForView();
                    ResourceHealthWarningProjection projection = snapshot.GetProjection(current);
                    ChartWarning wav = projection.Warnings.Single(warning => warning.Kind == ChartWarningKind.ResourceWavMissing);
                    Assert.AreEqual((50, 1, 2), (wav.Health, wav.ExistingCount, wav.DefinedCount));

                    var projectionOwner = new MainChartRowProjectionOwner();
                    var pending = PackageChartEntry.FromChart(current);
                    PlaylistDetailSourceRow playlist = new PlaylistDetailSourceRow(new BMSTableEntry(current), current).WithResourceHealthProjection(projection);
                    void AssertCurrentLanguage(string phase)
                    {
                        string digest = "[1] " + Resources.WarningDigest_ResourceMissing;
                        string detail = string.Format(Resources.Warning_WavFilesNotFound, 50, 1, 2);
                        LibraryChartRow normalRow = projectionOwner.CreateLibraryRow(library, current);
                        Assert.AreEqual(digest, normalRow.WarningDigestText, phase + " 通常行の短文");
                        Assert.AreEqual(detail, normalRow.WarningTooltipText, phase + " 通常行の詳細");
                        Assert.AreEqual(digest, playlist.WarningDigestText, phase + " プレイリスト詳細行の短文");
                        Assert.AreEqual(detail, playlist.WarningTooltipText, phase + " プレイリスト詳細行の詳細");
                        LibraryChartRow pendingRow = projectionOwner.CreatePackageRow(library, pending);
                        Assert.AreEqual(normalRow.WarningDigestText, pendingRow.WarningDigestText, phase + " 保留行の短文");
                        Assert.AreEqual(normalRow.WarningTooltipText, pendingRow.WarningTooltipText, phase + " 保留行の詳細");
                    }

                    AssertCurrentLanguage("ja");
                    ResourceService.Current.ChangeCulture("en-US");
                    AssertCurrentLanguage("en");

                    // 同じ事実の再実行は、更新・書込み・版・索引・投影版を変えません。
                    using LR2SongDBExtended observer = databaseScope.OpenSongReadOnly(songDbPath);
                    long dataVersion = observer.ExecuteScalar<long>("PRAGMA data_version;");
                    int revision = library.CatalogRevision;
                    int notificationVersion = library.NormalLibraryRefreshNotificationVersion;
                    int projectionVersion = pending.ProjectionVersion;
                    MaintenanceWorkflowResult second = library.RescanResourceHealthCharts([current]);
                    Assert.IsFalse(second.HasUpdates);
                    Assert.AreEqual(0, second.MaintenanceInfoUpsertCount);
                    Assert.AreEqual(revision, library.CatalogRevision);
                    Assert.AreEqual(notificationVersion, library.NormalLibraryRefreshNotificationVersion);
                    Assert.AreSame(snapshot, library.TryGetCurrentResourceHealthIndexSnapshotForView());
                    Assert.AreEqual(dataVersion, observer.ExecuteScalar<long>("PRAGMA data_version;"), "DBへ書き込みません。");
                    Assert.AreEqual(projectionVersion, pending.ProjectionVersion);
                    AssertCurrentLanguage("en(再評価後)");
                    ResourceService.Current.ChangeCulture("ja-JP");
                    AssertCurrentLanguage("ja(再)");
                }
                finally
                {
                    library.RequestShutdown("warning_language_resource_health_test");
                }
            });
        }
        finally
        {
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
        }
    }
}
