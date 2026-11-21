using System.Collections.Concurrent;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 分裂配置の探索部品（判定用の音源集合、関係、グループ）を、ライブラリ・ファイル・ロックなしで確認します。
/// 判定は既定値（10個・20個・80%）と、探索条件を変えた境界で行い、全組の比較で書いた参照実装との一致で取りこぼしを検出します。
/// </summary>
[TestClass]
public sealed class SplitPlacementSearchTests
{
    private const uint PreviewA = 900001;
    private const uint PreviewB = 900002;
    private const uint PreviewC = 900003;
    private const uint SimilarToPreview = 900004;
    private static readonly uint[] PreviewKeys = [PreviewA, PreviewB, PreviewC];

    private static IEnumerable<uint> Range(uint start, int count) => Enumerable.Range(0, count).Select(i => start + (uint)i);

    private static SplitPlacementFolderInput Folder(IEnumerable<uint> keys, params string[] hashes) => new(keys.ToArray(), hashes);

    private static SplitPlacementSearchResult Search(IReadOnlyList<SplitPlacementFolderInput> folders, int parallelism = 0,
        IProgress<SplitPlacementSearchProgress>? progress = null, CancellationToken token = default,
        SplitPlacementSearchCriteria? criteria = null)
        => SplitPlacementSearch.Search(folders, PreviewKeys, criteria ?? SplitPlacementSearchCriteria.Default, progress, token, parallelism);

    private static string Describe(SplitPlacementSearchResult result) => string.Join(" / ", result.Groups.Select(group => string.Join(",", group)));

    /// <summary>
    /// 渡したプレビュー名のキーだけを除き、除外後に9個なら候補外、10個なら候補になることを確認します。
    /// 類似のキーまで除く、または除外を忘れて9個を候補にする誤りを検出します。
    /// </summary>
    [TestMethod]
    public void Search_RemovesOnlyGivenPreviewKeys()
    {
        SplitPlacementSearchResult nine = Search([Folder(Range(1, 9).Append(PreviewA).Append(PreviewB)), Folder(Range(1, 9).Append(PreviewC))]);
        Assert.AreEqual(0, nine.CandidateFolderCount, "除外後に9個のフォルダは候補外");
        Assert.AreEqual(0, nine.GroupCount);

        SplitPlacementSearchResult ten = Search([Folder(Range(1, 10).Append(PreviewA)), Folder(Range(1, 10).Append(PreviewB))]);
        Assert.AreEqual(2, ten.CandidateFolderCount);
        Assert.AreEqual("0,1", Describe(ten));

        SplitPlacementSearchResult similar = Search([Folder(Range(1, 9).Append(SimilarToPreview)), Folder(Range(1, 9).Append(SimilarToPreview))]);
        Assert.AreEqual(2, similar.CandidateFolderCount, "プレビュー名に似ただけのキーは除かない");
        Assert.AreEqual("0,1", Describe(similar));
        Assert.AreEqual(2L * 10, similar.RegisteredKeyCount);
    }

    /// <summary>
    /// 既定値で、汎用名は、キーを持つ対象フォルダ（10個未満のフォルダも数える）が20個なら残り、21個なら全集合から除かれ、
    /// 除いて9個になったフォルダが候補外になることを確認します。
    /// </summary>
    [TestMethod]
    [DataRow(20, 0, 20, 0)]
    [DataRow(21, 0, 0, 1)]
    [DataRow(18, 2, 18, 0)]
    [DataRow(19, 2, 0, 1)]
    public void Search_RemovesKeysHeldByMoreThanTwentyFolders(int bigFolders, int smallFolders, int expectedCandidates, int expectedGeneric)
    {
        const uint generic = 777;
        List<SplitPlacementFolderInput> folders = [];
        for (int i = 0; i < bigFolders; i++)
        {
            folders.Add(Folder(Range((uint)(1000 * (i + 1)), 9).Append(generic)));
        }
        for (int i = 0; i < smallFolders; i++)
        {
            folders.Add(Folder([generic]));
        }

        SplitPlacementSearchResult result = Search(folders);

        Assert.AreEqual(expectedCandidates, result.CandidateFolderCount);
        Assert.AreEqual(expectedGeneric, result.GenericKeyCount);
        Assert.AreEqual(0, result.GroupCount, "共有するのが汎用名だけなら関係にならない");
    }

    /// <summary>10個の条件は両方のフォルダに課し、9個の集合が10個以上の集合に完全に含まれても関係にしないことを確認します。</summary>
    [TestMethod]
    public void Search_RequiresTenKeysInBothFolders()
    {
        SplitPlacementSearchResult result = Search([Folder(Range(1, 9)), Folder(Range(1, 12))]);

        Assert.AreEqual(1, result.CandidateFolderCount);
        Assert.AreEqual(0L, result.RelationCount);
        Assert.AreEqual(0, result.GroupCount);
    }

    /// <summary>
    /// 既定値で、小さい方の集合の80%以上を他方が持つ組だけが関係になる境界（s×5≥n×4）を、集合の大きさを変えて確認します。
    /// 小さい方の個数で判定する誤りと、境界の一つ上下の取り違えを検出します。
    /// </summary>
    [TestMethod]
    [DataRow(10, 8, 10, true)]
    [DataRow(10, 7, 10, false)]
    [DataRow(11, 9, 11, true)]
    [DataRow(11, 8, 11, false)]
    [DataRow(100, 80, 100, true)]
    [DataRow(100, 79, 100, false)]
    [DataRow(10, 8, 20, true)]
    [DataRow(10, 7, 20, false)]
    public void Search_RelatesFoldersSharingEightyPercentOfTheSmallerSet(int smaller, int shared, int larger, bool related)
    {
        uint[] first = [.. Range(1, smaller)];
        uint[] second = [.. first.Take(shared), .. Range(10000, larger - shared)];

        SplitPlacementSearchResult result = Search([Folder(first), Folder(second)]);

        Assert.AreEqual(related ? 1L : 0L, result.RelationCount);
        Assert.AreEqual(related ? "0,1" : string.Empty, Describe(result));
    }

    /// <summary>主ハッシュ（大文字小文字だけが違う場合を含む）を共有する組は関係にせず、共有しない組は関係にすることを確認します。</summary>
    [TestMethod]
    [DataRow("abc123", "ABC123", false)]
    [DataRow("abc123", "abc123", false)]
    [DataRow("abc123", "def456", true)]
    public void Search_DoesNotRelateFoldersSharingPrimaryHash(string first, string second, bool related)
    {
        SplitPlacementSearchResult result = Search([Folder(Range(1, 10), "zzz", first), Folder(Range(1, 10), second)]);

        Assert.AreEqual(related ? 1L : 0L, result.RelationCount);
        Assert.AreEqual(related ? 1 : 0, result.GroupCount);
    }

    /// <summary>
    /// 連結を確認します。A-B、B-Cが関係でA-Cが関係でなければ三つが一つのグループです。
    /// A-Cが主ハッシュの共有で関係にならなくても、Bが両方と関係なら同じグループ、Bがなければグループなしです。
    /// 無関係な二つの成分は別のグループで、一フォルダだけのグループは作りません。
    /// </summary>
    [TestMethod]
    public void Search_ConnectsFoldersThroughRelations()
    {
        SplitPlacementSearchResult chain = Search([Folder(Range(1, 10)), Folder(Range(3, 10)), Folder(Range(5, 10))]);
        Assert.AreEqual("0,1,2", Describe(chain));
        Assert.AreEqual(2L, chain.RelationCount, "A-Cは6個の共有で関係にならない");

        SplitPlacementSearchResult viaMiddle = Search([Folder(Range(1, 10), "same"), Folder(Range(2, 10)), Folder(Range(1, 10), "SAME")]);
        Assert.AreEqual("0,1,2", Describe(viaMiddle));
        Assert.AreEqual(2L, viaMiddle.RelationCount, "A-Cは主ハッシュの共有で関係にならない");

        SplitPlacementSearchResult withoutMiddle = Search([Folder(Range(1, 10), "same"), Folder(Range(1, 10), "SAME")]);
        Assert.AreEqual(0, withoutMiddle.GroupCount);

        SplitPlacementSearchResult separate = Search([Folder(Range(1, 10)), Folder(Range(1, 10)), Folder(Range(5000, 10)), Folder(Range(5000, 10)), Folder(Range(9000, 10))]);
        Assert.AreEqual("0,1 / 2,3", Describe(separate));
        Assert.AreEqual(5, separate.TargetFolderCount);
        Assert.AreEqual(5, separate.CandidateFolderCount);
    }

    /// <summary>入力の整列・重複・並び順に依存せず同じ結果になることを確認します（索引が整列済みでない入力の取込み）。</summary>
    [TestMethod]
    public void Search_AcceptsUnsortedAndDuplicatedKeys()
    {
        uint[] shuffled = [.. Range(1, 10).Reverse(), 3, 3, PreviewA];

        SplitPlacementSearchResult result = Search([Folder(shuffled), Folder(Range(1, 10))]);

        Assert.AreEqual("0,1", Describe(result));
        Assert.AreEqual(20L, result.RegisteredKeyCount);
    }

    /// <summary>事前の取消と、進捗の報告の中での取消が <see cref="OperationCanceledException"/> になることを確認します。</summary>
    [TestMethod]
    public void Search_ThrowsWhenCancelledBeforeOrDuringProgress()
    {
        SplitPlacementFolderInput[] folders = [Folder(Range(1, 10)), Folder(Range(1, 10))];
        using (var before = new CancellationTokenSource())
        {
            before.Cancel();
            Assert.ThrowsExactly<OperationCanceledException>(() => Search(folders, token: before.Token));
        }

        foreach (SplitPlacementSearchStage stage in new[] { SplitPlacementSearchStage.Prepare, SplitPlacementSearchStage.Match })
        {
            using var during = new CancellationTokenSource();
            var progress = new SyncProgress(value => { if (value.Stage == stage) { during.Cancel(); } });
            Assert.ThrowsExactly<OperationCanceledException>(() => Search(folders, progress: progress, token: during.Token), stage.ToString());
        }
    }

    /// <summary>進捗の受け手が送出した例外が、探索の失敗として元の型で呼出し側へ届くことを確認します。</summary>
    [TestMethod]
    public void Search_PropagatesProgressReceiverFailure()
    {
        SplitPlacementFolderInput[] folders = [Folder(Range(1, 10)), Folder(Range(1, 10))];
        var progress = new SyncProgress(value => { if (value.Stage == SplitPlacementSearchStage.Match && value.Completed > 0) { throw new InvalidOperationException("receiver"); } });

        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => Search(folders, parallelism: 1, progress: progress));

        Assert.AreEqual("receiver", failure.Message);
    }

    /// <summary>準備の段階の報告があり、照合の報告は総数が候補フォルダ数、完了数が0以上総数以下であることを確認します。</summary>
    [TestMethod]
    public void Search_ReportsPrepareAndMatchProgress()
    {
        var reports = new ConcurrentQueue<SplitPlacementSearchProgress>();
        SplitPlacementFolderInput[] folders = [.. Enumerable.Range(0, 30).Select(i => Folder(Range((uint)(i / 2 * 100 + 1), 10))), Folder(Range(60000, 3))];

        SplitPlacementSearchResult result = Search(folders, progress: new SyncProgress(reports.Enqueue));

        Assert.IsTrue(reports.Any(report => report.Stage == SplitPlacementSearchStage.Prepare), "準備の報告");
        SplitPlacementSearchProgress[] match = [.. reports.Where(report => report.Stage == SplitPlacementSearchStage.Match)];
        Assert.IsTrue(match.Length > 0);
        Assert.IsTrue(match.All(report => report.Total == result.CandidateFolderCount && report.Total == 30 && report.Completed >= 0 && report.Completed <= report.Total));
    }

    /// <summary>共有率は50〜100の5刻みの全値、汎用名とみなすフォルダ数は2・20・100、基準は定義済みの値を受け付けることを確認します。</summary>
    [TestMethod]
    public void Criteria_AcceptsValuesInRange()
    {
        foreach (int percent in Enumerable.Range(10, 11).Select(i => i * 5))
        {
            Assert.AreEqual(percent, new SplitPlacementSearchCriteria(percent, 20, SplitPlacementShareBasis.SmallerSide).SharePercent);
        }
        foreach (int limit in new[] { 2, 20, 100 })
        {
            Assert.AreEqual(limit, new SplitPlacementSearchCriteria(80, limit, SplitPlacementShareBasis.BothSides).GenericFolderLimit);
        }
        Assert.AreEqual(80, SplitPlacementSearchCriteria.Default.SharePercent);
        Assert.AreEqual(20, SplitPlacementSearchCriteria.Default.GenericFolderLimit);
        Assert.AreEqual(SplitPlacementShareBasis.SmallerSide, SplitPlacementSearchCriteria.Default.Basis);
    }

    /// <summary>範囲の外・刻みに合わない共有率、範囲の外のフォルダ数、列挙の外の基準を、構築時に拒否することを確認します。</summary>
    [TestMethod]
    [DataRow(49, 20, 0)]
    [DataRow(105, 20, 0)]
    [DataRow(51, 20, 0)]
    [DataRow(52, 20, 0)]
    [DataRow(77, 20, 0)]
    [DataRow(0, 20, 0)]
    [DataRow(110, 20, 0)]
    [DataRow(80, 1, 0)]
    [DataRow(80, 0, 0)]
    [DataRow(80, 101, 0)]
    [DataRow(80, 20, 2)]
    [DataRow(80, 20, -1)]
    public void Criteria_RejectsInvalidValues(int percent, int limit, int basis)
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new SplitPlacementSearchCriteria(percent, limit, (SplitPlacementShareBasis)basis));
    }

    /// <summary>同じ値で別々に作った二つの探索条件が等しく、一つでも値が違えば等しくないことを確認します（結果の有効性は値の等価で判定する）。</summary>
    [TestMethod]
    public void Criteria_ComparesByValue()
    {
        var first = new SplitPlacementSearchCriteria(65, 30, SplitPlacementShareBasis.BothSides);
        var second = new SplitPlacementSearchCriteria(65, 30, SplitPlacementShareBasis.BothSides);

        Assert.AreNotSame(first, second);
        Assert.AreEqual(first, second);
        Assert.AreEqual(first.GetHashCode(), second.GetHashCode());
        Assert.AreNotEqual(first, new SplitPlacementSearchCriteria(70, 30, SplitPlacementShareBasis.BothSides));
        Assert.AreNotEqual(first, new SplitPlacementSearchCriteria(65, 31, SplitPlacementShareBasis.BothSides));
        Assert.AreNotEqual(first, new SplitPlacementSearchCriteria(65, 30, SplitPlacementShareBasis.SmallerSide));
    }

    /// <summary>
    /// 探索条件の共有率と基準による境界（s×100≥n×p、整数演算）を確認します。小さい方・大きい方の個数と共有数を変え、
    /// 浮動小数点の誤差（p=55、n=100）と、基準の取り違え（小さい方／大きい方）を検出します。
    /// </summary>
    [TestMethod]
    [DataRow(50, SplitPlacementShareBasis.SmallerSide, 10, 10, 5, true)]
    [DataRow(50, SplitPlacementShareBasis.SmallerSide, 10, 10, 4, false)]
    [DataRow(75, SplitPlacementShareBasis.SmallerSide, 12, 12, 9, true)]
    [DataRow(75, SplitPlacementShareBasis.SmallerSide, 12, 12, 8, false)]
    [DataRow(75, SplitPlacementShareBasis.SmallerSide, 10, 10, 8, true)]
    [DataRow(75, SplitPlacementShareBasis.SmallerSide, 10, 10, 7, false)]
    [DataRow(55, SplitPlacementShareBasis.SmallerSide, 11, 11, 7, true)]
    [DataRow(55, SplitPlacementShareBasis.SmallerSide, 11, 11, 6, false)]
    [DataRow(55, SplitPlacementShareBasis.SmallerSide, 100, 100, 55, true)]
    [DataRow(55, SplitPlacementShareBasis.SmallerSide, 100, 100, 54, false)]
    [DataRow(100, SplitPlacementShareBasis.SmallerSide, 10, 10, 10, true)]
    [DataRow(100, SplitPlacementShareBasis.SmallerSide, 10, 10, 9, false)]
    [DataRow(100, SplitPlacementShareBasis.SmallerSide, 10, 12, 10, true)]
    [DataRow(100, SplitPlacementShareBasis.BothSides, 10, 10, 10, true)]
    [DataRow(100, SplitPlacementShareBasis.BothSides, 10, 10, 9, false)]
    [DataRow(75, SplitPlacementShareBasis.BothSides, 12, 12, 9, true)]
    [DataRow(75, SplitPlacementShareBasis.BothSides, 12, 12, 8, false)]
    [DataRow(55, SplitPlacementShareBasis.BothSides, 100, 100, 55, true)]
    [DataRow(55, SplitPlacementShareBasis.BothSides, 100, 100, 54, false)]
    [DataRow(80, SplitPlacementShareBasis.BothSides, 10, 12, 10, true)]
    [DataRow(85, SplitPlacementShareBasis.BothSides, 10, 12, 10, false)]
    [DataRow(80, SplitPlacementShareBasis.SmallerSide, 10, 12, 10, true)]
    [DataRow(85, SplitPlacementShareBasis.SmallerSide, 10, 12, 10, true)]
    [DataRow(50, SplitPlacementShareBasis.BothSides, 10, 20, 10, true)]
    [DataRow(55, SplitPlacementShareBasis.BothSides, 10, 20, 10, false)]
    public void Search_AppliesShareRateAndBasis(int percent, SplitPlacementShareBasis basis, int smaller, int larger, int shared, bool related)
    {
        uint[] first = [.. Range(1, smaller)];
        uint[] second = [.. first.Take(shared), .. Range(10000, larger - shared)];

        SplitPlacementSearchResult result = Search([Folder(first), Folder(second)], criteria: new SplitPlacementSearchCriteria(percent, 20, basis));

        Assert.AreEqual(related ? 1L : 0L, result.RelationCount);
        Assert.AreEqual(related ? "0,1" : string.Empty, Describe(result));
    }

    /// <summary>10個の集合が20個の集合に完全に含まれる組は、基準が「音源の少ない方」ならすべての共有率で関係になることを確認します。</summary>
    [TestMethod]
    public void Search_SmallerSideBasisRelatesContainedSetAtEveryShareRate()
    {
        foreach (int percent in Enumerable.Range(10, 11).Select(i => i * 5))
        {
            SplitPlacementSearchResult result = Search(
                [Folder(Range(1, 10)), Folder(Range(1, 20))], criteria: new SplitPlacementSearchCriteria(percent, 20, SplitPlacementShareBasis.SmallerSide));
            Assert.AreEqual(1L, result.RelationCount, "共有率" + percent);
        }
    }

    /// <summary>
    /// 汎用名とみなすフォルダ数 N（2・20・100）で、キーを持つ対象フォルダがちょうど N 個なら残り、N+1 個なら全集合から除かれることを確認します。
    /// 10個未満の対象フォルダも数え、除いて9個になったフォルダは候補外になります。
    /// </summary>
    [TestMethod]
    [DataRow(2, 2, 0, 2, 0)]
    [DataRow(2, 3, 0, 0, 1)]
    [DataRow(2, 0, 2, 0, 0)]
    [DataRow(2, 1, 2, 0, 1)]
    [DataRow(20, 20, 0, 20, 0)]
    [DataRow(20, 21, 0, 0, 1)]
    [DataRow(20, 18, 2, 18, 0)]
    [DataRow(20, 19, 2, 0, 1)]
    [DataRow(100, 100, 0, 100, 0)]
    [DataRow(100, 101, 0, 0, 1)]
    [DataRow(100, 98, 2, 98, 0)]
    [DataRow(100, 99, 2, 0, 1)]
    public void Search_RemovesKeysHeldByMoreThanTheGenericFolderLimit(int limit, int bigFolders, int smallFolders, int expectedCandidates, int expectedGeneric)
    {
        const uint generic = 777;
        List<SplitPlacementFolderInput> folders = [];
        for (int i = 0; i < bigFolders; i++)
        {
            folders.Add(Folder(Range((uint)(1000 * (i + 1)), 9).Append(generic)));
        }
        for (int i = 0; i < smallFolders; i++)
        {
            folders.Add(Folder([generic]));
        }

        SplitPlacementSearchResult result = Search(folders, criteria: new SplitPlacementSearchCriteria(80, limit, SplitPlacementShareBasis.SmallerSide));

        Assert.AreEqual(expectedCandidates, result.CandidateFolderCount);
        Assert.AreEqual(expectedGeneric, result.GenericKeyCount);
        Assert.AreEqual(0, result.GroupCount, "共有するのが汎用名だけなら関係にならない");
    }

    /// <summary>
    /// 固定の種から作る入力と複数の探索条件（既定値、最小の共有率と最小の汎用名、基準「両方」、境界の組合せ）で、
    /// 全組の比較で書いた参照実装とグループ・診断値が一致し、並列度1と既定値で順序まで一致することを確認します。
    /// 探索条件ごとに、共有率の境界の組（s×100=n×p と1足りない組）、汎用名の上限ちょうどと1つ上のフォルダが持つキー、
    /// 基準で結果が分かれる組が入力に現れなければ失敗にし、境界を試さないまま一致することを防ぎます。既定値では従来の網羅項目（9個と10個の集合、主ハッシュの除外、三つ以上の連結）も維持します。
    /// </summary>
    [TestMethod]
    public void Search_MatchesBruteForceReferenceOnGeneratedInputs()
    {
        SplitPlacementSearchCriteria[] criteriaList =
        [
            SplitPlacementSearchCriteria.Default,
            new(50, 2, SplitPlacementShareBasis.SmallerSide),
            new(75, 20, SplitPlacementShareBasis.BothSides),
            new(55, 5, SplitPlacementShareBasis.BothSides),
            new(100, 100, SplitPlacementShareBasis.SmallerSide),
        ];
        foreach (SplitPlacementSearchCriteria criteria in criteriaList)
        {
            var coverage = new ReferenceCoverage();
            for (int seed = 1; seed <= 16; seed++)
            {
                SplitPlacementFolderInput[] folders = GenerateFolders(new Random(seed), criteria);
                ReferenceResult expected = Reference(folders, criteria, coverage);

                SplitPlacementSearchResult single = Search(folders, parallelism: 1, criteria: criteria);
                SplitPlacementSearchResult parallel = Search(folders, criteria: criteria);

                string message = criteria + " seed=" + seed;
                Assert.AreEqual(expected.GroupText, Describe(single), message);
                Assert.AreEqual(expected.GroupText, Describe(parallel), message + " (既定の並列度)");
                foreach (SplitPlacementSearchResult actual in new[] { single, parallel })
                {
                    Assert.AreEqual(expected.Candidates, actual.CandidateFolderCount, message);
                    Assert.AreEqual(expected.Generic, actual.GenericKeyCount, message);
                    Assert.AreEqual(expected.Relations, actual.RelationCount, message);
                    Assert.AreEqual(expected.GroupText.Length == 0 ? 0 : expected.GroupText.Split(" / ").Length, actual.GroupCount, message);
                }
            }

            string name = criteria.ToString();
            Assert.IsTrue(coverage.ExactBoundary > 0, name + ": s×100=n×p の組");
            Assert.IsTrue(coverage.JustBelowBoundary > 0, name + ": ちょうど1足りない組");
            Assert.IsTrue(coverage.KeyHeldByLimit > 0 && coverage.KeyHeldByLimitPlusOne > 0, name + ": 上限ちょうどと1つ上のフォルダが持つキー");
            Assert.IsTrue(coverage.SizeNine > 0 && coverage.SizeTen > 0, name + ": 9個と10個の集合");
            Assert.IsTrue(coverage.ExcludedByPrimaryHash > 0, name + ": 主ハッシュの共有で除外した組");
            if (criteria.Basis == SplitPlacementShareBasis.BothSides)
            {
                Assert.IsTrue(coverage.BasisDecides > 0, name + ": 基準で結果が分かれる組");
            }
            if (criteria.Equals(SplitPlacementSearchCriteria.Default))
            {
                Assert.IsTrue(coverage.GroupWithThreeOrMore > 0, "既定値: 三つ以上のフォルダの連結");
            }
        }
    }

    private static SplitPlacementFolderInput[] GenerateFolders(Random random, SplitPlacementSearchCriteria criteria)
    {
        int limit = criteria.GenericFolderLimit;
        int folderCount = random.Next(limit + 20, limit + 80);
        uint[][] songs = [.. Enumerable.Range(0, random.Next(5, 9)).Select(_ => PickDistinct(random, 1, 300, random.Next(9, 12)))];
        string[] hashPool = [.. Enumerable.Range(0, 25).Select(i => "Hash" + i.ToString("x2"))];
        List<HashSet<uint>> sets = [];
        for (int i = 0; i < folderCount; i++)
        {
            HashSet<uint> set;
            if (random.Next(10) < 8)
            {
                uint[] song = songs[random.Next(songs.Length)];
                set = [.. song];
                int changes = random.Next(0, 5);
                foreach (uint removed in set.OrderBy(_ => random.Next()).Take(changes).ToArray())
                {
                    set.Remove(removed);
                }
                foreach (uint added in PickDistinct(random, 1, 300, changes))
                {
                    set.Add(added);
                }
            }
            else
            {
                set = [.. PickDistinct(random, 1, 300, random.Next(3, 14))];
            }
            if (random.Next(2) == 0)
            {
                set.UnionWith(PreviewKeys.OrderBy(_ => random.Next()).Take(random.Next(1, 4)));
            }
            if (random.Next(3) == 0)
            {
                set.Add(SimilarToPreview);
            }
            sets.Add(set);
        }
        // 特定のキーを、汎用名の上限の一つ下から二つ上までのフォルダへ配る（上限ちょうどと1つ上を含む）。
        for (int offset = -1; offset <= 2; offset++)
        {
            uint special = (uint)(8000 + offset + 1);
            foreach (int index in Enumerable.Range(0, folderCount).OrderBy(_ => random.Next()).Take(Math.Min(folderCount, Math.Max(1, limit + offset))))
            {
                sets[index].Add(special);
            }
        }
        // 共有率の境界の組。20個の集合なので、5%刻みのどの共有率でも s×100=n×p が整数の共有数で成り立つ。
        int exact = 20 * criteria.SharePercent / 100;
        uint next = 20000;
        sets.Add([.. Range(next, 20)]);
        sets.Add([.. Range(next, exact), .. Range(next + 100, 20 - exact)]);
        next += 1000;
        sets.Add([.. Range(next, 20)]);
        sets.Add([.. Range(next, exact - 1), .. Range(next + 100, 20 - exact + 1)]);
        next += 1000;
        // 10個の集合が20個の集合に完全に含まれる組。基準が「音源の少ない方」なら関係があり、「両方」では共有率が50%を超えると関係がない。
        sets.Add([.. Range(next, 10)]);
        sets.Add([.. Range(next, 10), .. Range(next + 100, 10)]);
        return [.. sets.Select(set => new SplitPlacementFolderInput(
            set.OrderBy(_ => random.Next()).ToArray(),
            [.. Enumerable.Range(0, random.Next(1, 4)).Select(_ => RandomCase(random, hashPool[random.Next(hashPool.Length)]))]))];
    }

    private static string RandomCase(Random random, string value) => random.Next(2) == 0 ? value.ToUpperInvariant() : value.ToLowerInvariant();

    private static uint[] PickDistinct(Random random, uint minimum, uint maximum, int count)
        => [.. Enumerable.Range((int)minimum, (int)(maximum - minimum + 1)).OrderBy(_ => random.Next()).Take(count).Select(value => (uint)value)];

    private sealed class ReferenceCoverage
    {
        internal int ExactBoundary, JustBelowBoundary, BasisDecides, SizeNine, SizeTen, KeyHeldByLimit, KeyHeldByLimitPlusOne, ExcludedByPrimaryHash, GroupWithThreeOrMore;
    }

    private sealed record ReferenceResult(string GroupText, int Candidates, int Generic, long Relations);

    /// <summary>仕様をそのまま全組の比較で書いた参照実装です。製品の探索部品を使いません。</summary>
    private static ReferenceResult Reference(SplitPlacementFolderInput[] folders, SplitPlacementSearchCriteria criteria, ReferenceCoverage coverage)
    {
        HashSet<uint>[] sets = [.. folders.Select(folder => folder.AudioKeys.ToArray().Where(key => !PreviewKeys.Contains(key)).ToHashSet())];
        Dictionary<uint, int> holders = [];
        foreach (HashSet<uint> set in sets)
        {
            foreach (uint key in set)
            {
                holders[key] = holders.GetValueOrDefault(key) + 1;
            }
        }
        HashSet<uint> generic = [.. holders.Where(pair => pair.Value > criteria.GenericFolderLimit).Select(pair => pair.Key)];
        coverage.KeyHeldByLimit += holders.Count(pair => pair.Value == criteria.GenericFolderLimit);
        coverage.KeyHeldByLimitPlusOne += holders.Count(pair => pair.Value == criteria.GenericFolderLimit + 1);
        foreach (HashSet<uint> set in sets)
        {
            set.ExceptWith(generic);
        }
        int[] candidates = [.. Enumerable.Range(0, sets.Length).Where(i => sets[i].Count >= 10)];
        coverage.SizeNine += sets.Count(set => set.Count == 9);
        coverage.SizeTen += sets.Count(set => set.Count == 10);

        int[] parent = [.. Enumerable.Range(0, sets.Length)];
        int Find(int node) { while (parent[node] != node) { node = parent[node]; } return node; }
        bool both = criteria.Basis == SplitPlacementShareBasis.BothSides;
        long relations = 0;
        foreach (int a in candidates)
        {
            foreach (int b in candidates.Where(b => b > a))
            {
                int shared = sets[a].Count(key => sets[b].Contains(key));
                int smaller = Math.Min(sets[a].Count, sets[b].Count);
                int larger = Math.Max(sets[a].Count, sets[b].Count);
                int basis = both ? larger : smaller;
                bool audio = shared * 100 >= basis * criteria.SharePercent;
                if (shared * 100 == basis * criteria.SharePercent) { coverage.ExactBoundary++; }
                if (!audio && (shared + 1) * 100 >= basis * criteria.SharePercent) { coverage.JustBelowBoundary++; }
                if ((shared * 100 >= smaller * criteria.SharePercent) != (shared * 100 >= larger * criteria.SharePercent)) { coverage.BasisDecides++; }
                if (!audio) { continue; }
                bool sameHash = folders[a].PrimaryHashes.Any(x => folders[b].PrimaryHashes.Any(y => string.Equals(x, y, StringComparison.OrdinalIgnoreCase)));
                if (sameHash) { coverage.ExcludedByPrimaryHash++; continue; }
                relations++;
                parent[Find(a)] = Find(b);
            }
        }
        List<List<int>> groups = [.. Enumerable.Range(0, sets.Length).GroupBy(Find).Select(group => group.OrderBy(i => i).ToList())
            .Where(group => group.Count >= 2).OrderBy(group => group[0])];
        coverage.GroupWithThreeOrMore += groups.Count(group => group.Count >= 3);
        return new ReferenceResult(string.Join(" / ", groups.Select(group => string.Join(",", group))), candidates.Length, generic.Count, relations);
    }

    private sealed class SyncProgress(Action<SplitPlacementSearchProgress> action) : IProgress<SplitPlacementSearchProgress>
    {
        public void Report(SplitPlacementSearchProgress value) => action(value);
    }
}
