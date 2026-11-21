using System.Collections.Concurrent;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 分裂配置の探索部品（判定用の音源集合、関係、グループ）を、ライブラリ・ファイル・ロックなしで確認します。
/// 判定は仕様の数値（10個・20個・80%）の境界で行い、全組の比較で書いた参照実装との一致で取りこぼしを検出します。
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
        IProgress<SplitPlacementSearchProgress>? progress = null, CancellationToken token = default)
        => SplitPlacementSearch.Search(folders, PreviewKeys, progress, token, parallelism);

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
    /// 汎用名は、キーを持つ対象フォルダ（10個未満のフォルダも数える）が20個なら残り、21個なら全集合から除かれ、
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
    /// 小さい方の集合の80%以上を他方が持つ組だけが関係になる境界（s×5≥n×4）を、集合の大きさを変えて確認します。
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

    /// <summary>
    /// 固定の種から作る入力（共有率75〜85%の組、9〜11個の集合、ちょうど19〜22個のフォルダが持つキー、プレビュー名と類似のキー、小さな集まりから選ぶ主ハッシュ）で、
    /// 全組の比較で書いた参照実装とグループ・診断値が一致し、並列度1と既定値で順序まで一致することを確認します。
    /// 境界の組が入力に現れなければ参照実装側で失敗にし、境界を試さないまま一致することを防ぎます。
    /// </summary>
    [TestMethod]
    public void Search_MatchesBruteForceReferenceOnGeneratedInputs()
    {
        var coverage = new ReferenceCoverage();
        for (int seed = 1; seed <= 32; seed++)
        {
            SplitPlacementFolderInput[] folders = GenerateFolders(new Random(seed));
            ReferenceResult expected = Reference(folders, coverage);

            SplitPlacementSearchResult single = Search(folders, parallelism: 1);
            SplitPlacementSearchResult parallel = Search(folders);

            string message = "seed=" + seed;
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

        Assert.IsTrue(coverage.ExactlyEighty > 0, "s×5=n×4の組");
        Assert.IsTrue(coverage.JustBelowEighty > 0, "ちょうど1足りない組");
        Assert.IsTrue(coverage.SizeNine > 0 && coverage.SizeTen > 0, "9個と10個の集合");
        Assert.IsTrue(coverage.KeyHeldByTwenty > 0 && coverage.KeyHeldByTwentyOne > 0, "20個と21個のフォルダが持つキー");
        Assert.IsTrue(coverage.ExcludedByPrimaryHash > 0, "主ハッシュの共有で除外した組");
        Assert.IsTrue(coverage.GroupWithThreeOrMore > 0, "三つ以上のフォルダの連結");
    }

    private static SplitPlacementFolderInput[] GenerateFolders(Random random)
    {
        int folderCount = random.Next(40, 161);
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
        // 特定のキーを、ちょうど19〜22個のフォルダへ配る。
        for (uint special = 8000; special < 8004; special++)
        {
            foreach (int index in Enumerable.Range(0, folderCount).OrderBy(_ => random.Next()).Take(Math.Min(folderCount, random.Next(19, 23))))
            {
                sets[index].Add(special);
            }
        }
        return [.. sets.Select(set => new SplitPlacementFolderInput(
            set.OrderBy(_ => random.Next()).ToArray(),
            [.. Enumerable.Range(0, random.Next(1, 4)).Select(_ => RandomCase(random, hashPool[random.Next(hashPool.Length)]))]))];
    }

    private static string RandomCase(Random random, string value) => random.Next(2) == 0 ? value.ToUpperInvariant() : value.ToLowerInvariant();

    private static uint[] PickDistinct(Random random, uint minimum, uint maximum, int count)
        => [.. Enumerable.Range((int)minimum, (int)(maximum - minimum + 1)).OrderBy(_ => random.Next()).Take(count).Select(value => (uint)value)];

    private sealed class ReferenceCoverage
    {
        internal int ExactlyEighty, JustBelowEighty, SizeNine, SizeTen, KeyHeldByTwenty, KeyHeldByTwentyOne, ExcludedByPrimaryHash, GroupWithThreeOrMore;
    }

    private sealed record ReferenceResult(string GroupText, int Candidates, int Generic, long Relations);

    /// <summary>仕様をそのまま全組の比較で書いた参照実装です。製品の探索部品を使いません。</summary>
    private static ReferenceResult Reference(SplitPlacementFolderInput[] folders, ReferenceCoverage coverage)
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
        HashSet<uint> generic = [.. holders.Where(pair => pair.Value > 20).Select(pair => pair.Key)];
        coverage.KeyHeldByTwenty += holders.Count(pair => pair.Value == 20);
        coverage.KeyHeldByTwentyOne += holders.Count(pair => pair.Value == 21);
        foreach (HashSet<uint> set in sets)
        {
            set.ExceptWith(generic);
        }
        int[] candidates = [.. Enumerable.Range(0, sets.Length).Where(i => sets[i].Count >= 10)];
        coverage.SizeNine += sets.Count(set => set.Count == 9);
        coverage.SizeTen += sets.Count(set => set.Count == 10);

        int[] parent = [.. Enumerable.Range(0, sets.Length)];
        int Find(int node) { while (parent[node] != node) { node = parent[node]; } return node; }
        long relations = 0;
        foreach (int a in candidates)
        {
            foreach (int b in candidates.Where(b => b > a))
            {
                int shared = sets[a].Count(key => sets[b].Contains(key));
                int smaller = Math.Min(sets[a].Count, sets[b].Count);
                bool audio = shared * 5 >= smaller * 4;
                if (shared * 5 == smaller * 4) { coverage.ExactlyEighty++; }
                if (!audio && (shared + 1) * 5 >= smaller * 4) { coverage.JustBelowEighty++; }
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
