using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Files;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 警告が翻訳文ではなく種別と事実だけを保持する契約（ファクトリの形、等価性、既定属性、永続化しないこと）を確認します。
/// </summary>
[TestClass]
public sealed class ChartWarningTests
{
    private TestWorkspace? ownedWorkspace;

    /// <summary>ケース開始後だけ利用できる、可変ファイル領域の所有者です。</summary>
    private TestWorkspace CaseWorkspace => ownedWorkspace
        ?? throw new InvalidOperationException("ケースの可変媒体はTestInitialize後に利用します。");

    /// <summary>constructorと既存field準備の成功後にケース専有領域を取得します。</summary>
    [TestInitialize]
    public void InitializeWorkspace() => ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);

    /// <summary>既存finallyと接続・Task終端の後にケース専有領域を回収します。</summary>
    [TestCleanup]
    public void CleanupWorkspace() => ownedWorkspace?.Dispose();

    /// <summary>仕様が定める事実の形ごとの種別です。</summary>
    private static readonly Dictionary<ChartWarningShape, ChartWarningKind[]> ExpectedKindsByShape = new()
    {
        [ChartWarningShape.None] =
        [
            ChartWarningKind.NestedChartFileInPackage, ChartWarningKind.ResourceBearingNestedCharts, ChartWarningKind.AlreadyInstalled,
            ChartWarningKind.SingleBmsFile, ChartWarningKind.SingleBmsonFile, ChartWarningKind.ResourceStagefileMissing,
            ChartWarningKind.ResourceBackbmpMissing, ChartWarningKind.ResourceBannerMissing, ChartWarningKind.UnsupportedResourcePath,
            ChartWarningKind.InstalledDestinationResolveFailed, ChartWarningKind.InstallEstimationLowConfidence, ChartWarningKind.DuplicateChart,
            ChartWarningKind.ZeroNoteMismatch, ChartWarningKind.Lr2PathEncodingUnsupported, ChartWarningKind.Lr2PathTooLong,
            ChartWarningKind.Lr2ResourcePathUnsupported, ChartWarningKind.Lr2ResourcePathTooLong
        ],
        [ChartWarningShape.Path] =
        [
            ChartWarningKind.InstallEstimationMetadataMismatch, ChartWarningKind.InstallEstimationUnrelated,
            ChartWarningKind.InstallEstimationMetadataUnavailable, ChartWarningKind.InstallEstimationReinstallNotImproved,
            ChartWarningKind.InstallEstimationAudioMissing, ChartWarningKind.InstallEstimationAudioUnverifiable
        ],
        [ChartWarningShape.PathPair] = [ChartWarningKind.InstallEstimationAmbiguous],
        [ChartWarningShape.Candidates] = [ChartWarningKind.InstalledDestinationAmbiguous, ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous],
        [ChartWarningShape.Count] = [ChartWarningKind.SourceSurfaceScanLimitExceeded],
        [ChartWarningShape.ResourceHealth] = [ChartWarningKind.ResourceWavMissing, ChartWarningKind.ResourceBgaMissing, ChartWarningKind.ResourceMovieMissing],
        [ChartWarningShape.ParseFailure] = [ChartWarningKind.ChartInfoParseFailure],
    };

    /// <summary>
    /// 全種別で、種類に合う形のファクトリだけが警告を作り、形の合わない引数は例外で拒否することを確認します。
    /// 形違いを黙って受理して、翻訳文や不正な事実を持つ警告が作られる誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Factories_AcceptOnlyKindsOfMatchingShape()
    {
        ChartWarningKind[] allKinds = Enum.GetValues<ChartWarningKind>();
        CollectionAssert.AreEquivalent(allKinds, ExpectedKindsByShape.Values.SelectMany(kinds => kinds).ToArray(), "全種別がいずれかの形に属します。");

        var factories = new (ChartWarningShape Shape, Func<ChartWarningKind, ChartWarning> Create)[]
        {
            (ChartWarningShape.None, kind => ChartWarning.Create(kind)),
            (ChartWarningShape.Path, kind => ChartWarning.CreateWithPath(kind, @"C:\Dest\A")),
            (ChartWarningShape.PathPair, kind => ChartWarning.CreateWithPathPair(kind, @"C:\Dest\A", @"C:\Dest\B")),
            (ChartWarningShape.Candidates, kind => ChartWarning.CreateWithCandidates(kind, [@"C:\Dest\A", @"C:\Dest\B"])),
            (ChartWarningShape.ResourceHealth, kind => ChartWarning.CreateResourceHealth(kind, 50, 1, 2)),
        };
        foreach (ChartWarningKind kind in allKinds)
        {
            foreach ((ChartWarningShape shape, Func<ChartWarningKind, ChartWarning> create) in factories)
            {
                if (ExpectedKindsByShape[shape].Contains(kind))
                {
                    ChartWarning created = create(kind);
                    Assert.AreEqual(kind, created.Kind);
                    Assert.AreEqual(shape, created.Shape);
                }
                else
                {
                    Assert.ThrowsExactly<ArgumentException>(() => create(kind), kind + " は " + shape + " の形のファクトリで作成できません。");
                }
            }
        }

        Assert.AreEqual(ChartWarningKind.SourceSurfaceScanLimitExceeded, ChartWarning.CreateSourceSurfaceScanLimitExceeded(50000).Kind);
        Assert.AreEqual(ChartWarningKind.ChartInfoParseFailure, ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "message").Kind);

        // 候補は2件以上3件以下で、nullの項目を含めません。
        Assert.ThrowsExactly<ArgumentException>(() => ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A"]));
        Assert.ThrowsExactly<ArgumentException>(() => ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, []));
        Assert.ThrowsExactly<ArgumentException>(() => ChartWarning.CreateWithCandidates(
            ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\B", @"C:\Dest\C", @"C:\Dest\D"]));
        Assert.ThrowsExactly<ArgumentException>(() => ChartWarning.CreateWithCandidates(
            ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", null!]));
        Assert.ThrowsExactly<ArgumentNullException>(() => ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, null!));
        Assert.AreEqual(3, ChartWarning.CreateWithCandidates(
            ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, [@"C:\Dest\A", @"C:\Dest\B", @"C:\Dest\C"]).Candidates.Length);
    }

    /// <summary>
    /// 等価性が種別と事実だけで決まり、同じ事実の別インスタンスは等価、事実を一つ変えると不一致になることを確認します。
    /// 比較箇所（カタログ確定・通知・保守結果・並べ替えキャッシュ）が言語や生成回に依存する誤りと、事実の変化を見落とす誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Equality_UsesKindAndFactsOnly()
    {
        // 同じ事実で別に作った警告は等価で、ハッシュも一致します。
        (ChartWarning First, ChartWarning Second)[] sameFacts =
        [
            (ChartWarning.Create(ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.DuplicateChart)),
            (ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationUnrelated, @"C:\Dest\A"),
                ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationUnrelated, @"C:\Dest\A")),
            (ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, @"C:\Dest\A", @"C:\Dest\B"),
                ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, @"C:\Dest\A", @"C:\Dest\B")),
            (ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\B"]),
                ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\B"])),
            (ChartWarning.CreateSourceSurfaceScanLimitExceeded(50000), ChartWarning.CreateSourceSurfaceScanLimitExceeded(50000)),
            (ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4),
                ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4)),
            (ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "message"),
                ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "message")),
        ];
        foreach ((ChartWarning first, ChartWarning second) in sameFacts)
        {
            Assert.AreEqual(first, second);
            Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
            Assert.IsTrue(first.Equals((object)second));
        }

        // 事実を一つだけ変えると不一致になります。
        var pathBase = ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationUnrelated, @"C:\Dest\A");
        var pairBase = ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, @"C:\Dest\A", @"C:\Dest\B");
        var candidateBase = ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\B"]);
        var healthBase = ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 4);
        var parseBase = ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "message");
        (string Name, ChartWarning Base, ChartWarning Changed)[] differentFacts =
        [
            ("種別(引数なし)", ChartWarning.Create(ChartWarningKind.DuplicateChart), ChartWarning.Create(ChartWarningKind.ZeroNoteMismatch)),
            ("種別(パス1つ)", pathBase, ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationMetadataUnavailable, @"C:\Dest\A")),
            ("パス1", pathBase, ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationUnrelated, @"C:\Dest\B")),
            ("パスの大小文字", pathBase, ChartWarning.CreateWithPath(ChartWarningKind.InstallEstimationUnrelated, @"C:\dest\a")),
            ("パス1(2つ)", pairBase, ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, @"C:\Dest\C", @"C:\Dest\B")),
            ("パス2", pairBase, ChartWarning.CreateWithPathPair(ChartWarningKind.InstallEstimationAmbiguous, @"C:\Dest\A", @"C:\Dest\C")),
            ("候補の内容", candidateBase, ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\C"])),
            ("候補の順序", candidateBase, ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\B", @"C:\Dest\A"])),
            ("候補の件数", candidateBase, ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAmbiguous, [@"C:\Dest\A", @"C:\Dest\B", @"C:\Dest\C"])),
            ("候補の種別", candidateBase, ChartWarning.CreateWithCandidates(ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, [@"C:\Dest\A", @"C:\Dest\B"])),
            ("件数", ChartWarning.CreateSourceSurfaceScanLimitExceeded(50000), ChartWarning.CreateSourceSurfaceScanLimitExceeded(50001)),
            ("health", healthBase, ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 26, 1, 4)),
            ("existing", healthBase, ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 2, 4)),
            ("defined", healthBase, ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceWavMissing, 25, 1, 5)),
            ("healthの種別", healthBase, ChartWarning.CreateResourceHealth(ChartWarningKind.ResourceBgaMissing, 25, 1, 4)),
            ("exception_type", parseBase, ChartWarning.CreateChartInfoParseFailure("JsonReaderException", "parse_failed", "message")),
            ("failure_kind", parseBase, ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "read_failed", "message")),
            ("message", parseBase, ChartWarning.CreateChartInfoParseFailure("InvalidDataException", "parse_failed", "other")),
        ];
        foreach ((string name, ChartWarning baseline, ChartWarning changed) in differentFacts)
        {
            Assert.AreNotEqual(baseline, changed, name + "を変えると不一致になります。");
        }
        Assert.IsFalse(pathBase.Equals(null));
    }

    /// <summary>
    /// 全種別の分類・優先順位・行強調・要約への表示・詳細への表示が、警告仕様の定義表どおりであることを確認します。
    /// </summary>
    [TestMethod]
    public void DefinitionTable_MatchesWarningSpecification()
    {
        (ChartWarningKind Kind, ChartWarningCategory Category, int Priority, bool Highlight, bool Digest)[] expected =
        [
            (ChartWarningKind.ResourceBearingNestedCharts, ChartWarningCategory.PackageLayout, 9, true, true),
            (ChartWarningKind.NestedChartFileInPackage, ChartWarningCategory.PackageLayout, 10, false, true),
            (ChartWarningKind.ChartInfoParseFailure, ChartWarningCategory.ChartMetadata, 15, true, true),
            (ChartWarningKind.Lr2PathEncodingUnsupported, ChartWarningCategory.Lr2Compatibility, 18, true, true),
            (ChartWarningKind.Lr2PathTooLong, ChartWarningCategory.Lr2Compatibility, 19, true, true),
            (ChartWarningKind.Lr2ResourcePathUnsupported, ChartWarningCategory.Lr2Compatibility, 21, true, true),
            (ChartWarningKind.Lr2ResourcePathTooLong, ChartWarningCategory.Lr2Compatibility, 22, true, true),
            (ChartWarningKind.ZeroNoteMismatch, ChartWarningCategory.ChartContent, 20, true, true),
            (ChartWarningKind.DuplicateChart, ChartWarningCategory.Duplicate, 30, true, true),
            (ChartWarningKind.InstallEstimationAmbiguous, ChartWarningCategory.InstallEstimation, 40, true, true),
            (ChartWarningKind.InstallEstimationMetadataMismatch, ChartWarningCategory.InstallEstimation, 41, true, true),
            (ChartWarningKind.InstallEstimationUnrelated, ChartWarningCategory.InstallEstimation, 42, true, true),
            (ChartWarningKind.InstallEstimationMetadataUnavailable, ChartWarningCategory.InstallEstimation, 42, true, true),
            (ChartWarningKind.InstallEstimationAudioMissing, ChartWarningCategory.InstallEstimation, 42, true, true),
            (ChartWarningKind.InstallEstimationAudioUnverifiable, ChartWarningCategory.InstallEstimation, 42, true, true),
            (ChartWarningKind.InstallEstimationReinstallNotImproved, ChartWarningCategory.InstallEstimation, 43, true, true),
            (ChartWarningKind.InstalledDestinationAmbiguous, ChartWarningCategory.InstallEstimation, 43, true, true),
            (ChartWarningKind.InstalledDestinationAutoAppliedAmbiguous, ChartWarningCategory.InstallEstimation, 43, true, true),
            (ChartWarningKind.UnsupportedResourcePath, ChartWarningCategory.InstallEstimation, 44, false, true),
            (ChartWarningKind.InstalledDestinationResolveFailed, ChartWarningCategory.InstallEstimation, 45, false, true),
            (ChartWarningKind.SourceSurfaceScanLimitExceeded, ChartWarningCategory.InstallEstimation, 46, false, true),
            (ChartWarningKind.InstallEstimationLowConfidence, ChartWarningCategory.InstallEstimation, 47, true, true),
            (ChartWarningKind.AlreadyInstalled, ChartWarningCategory.InstalledState, 50, false, true),
            (ChartWarningKind.SingleBmsFile, ChartWarningCategory.PackageLayout, 60, false, true),
            (ChartWarningKind.SingleBmsonFile, ChartWarningCategory.PackageLayout, 60, false, true),
            (ChartWarningKind.ResourceWavMissing, ChartWarningCategory.ResourceHealth, 80, false, true),
            (ChartWarningKind.ResourceBgaMissing, ChartWarningCategory.ResourceHealth, 80, false, true),
            (ChartWarningKind.ResourceMovieMissing, ChartWarningCategory.ResourceHealth, 80, false, true),
            (ChartWarningKind.ResourceStagefileMissing, ChartWarningCategory.ResourceHealth, 83, false, false),
            (ChartWarningKind.ResourceBackbmpMissing, ChartWarningCategory.ResourceHealth, 83, false, false),
            (ChartWarningKind.ResourceBannerMissing, ChartWarningCategory.ResourceHealth, 83, false, false),
        ];
        CollectionAssert.AreEquivalent(Enum.GetValues<ChartWarningKind>(), expected.Select(row => row.Kind).ToArray(), "定義表は全種別を含みます。");
        foreach ((ChartWarningKind kind, ChartWarningCategory category, int priority, bool highlight, bool digest) in expected)
        {
            var definition = ChartWarningDefinition.ForKind(kind);
            Assert.AreEqual(category, definition.Category, kind + " の分類");
            Assert.AreEqual(priority, definition.Priority, kind + " の優先順位");
            Assert.AreEqual(highlight, definition.HighlightRow, kind + " の行強調");
            Assert.AreEqual(digest, definition.ShowInDigest, kind + " の要約表示");
            Assert.IsTrue(definition.ShowInTooltip, kind + " は詳細に表示します。");
        }
    }

    /// <summary>
    /// 実DBのsong・install・maintenanceに一時警告の列を作らず、投影状態が永続スキーマへ混入する誤りを検出する。
    /// </summary>
    /// <remarks>保存スキーマへの混入を検出するため、実SQLiteテーブルの列を観測する。</remarks>
    [TestMethod]
    public void Warning_IsNotPersistedInSongInstallOrMaintenanceTables()
    {
        var databaseScope = new ApplicationDatabaseScope();
        string tempDirectoryPath = Path.Combine(CaseWorkspace.RootDirectory, "BeMusicSeeker_WarningSchema_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectoryPath);
        string songDbPath = Path.Combine(tempDirectoryPath, "song.db");
        try
        {
            using LR2SongDBExtended songDb = databaseScope.OpenSongWrite(songDbPath);
            songDb.CreateTable<LR2SongDB.song>();
            songDb.CreateTable<LR2SongDBExtended.install>();
            songDb.CreateTable<LR2SongDBExtended.maintenance>();

            AssertNoWarningColumn(songDb, "song");
            AssertNoWarningColumn(songDb, "install");
            AssertNoWarningColumn(songDb, "maintenance");
        }
        finally
        {
            if (Directory.Exists(tempDirectoryPath))
            {
                Directory.Delete(tempDirectoryPath, recursive: true);
            }
        }
    }

    private static void AssertNoWarningColumn(LR2SongDBExtended songDb, string tableName)
    {
        string[] columnNames = [.. songDb.Query<ColumnNameRow>("PRAGMA table_info(" + tableName + ");").Select(row => row.name)];
        CollectionAssert.DoesNotContain(columnNames, "warning");
    }

    private sealed class ColumnNameRow
    {
        public string name { get; set; } = string.Empty;
    }
}
