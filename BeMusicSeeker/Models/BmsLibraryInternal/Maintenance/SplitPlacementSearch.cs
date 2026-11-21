using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>探索へ渡す対象フォルダ一件の入力です。フォルダの番号は、入力の列の位置です。</summary>
/// <param name="AudioKeys">フォルダが自身で所有する音声キー。プレビュー名と汎用名を除く前の値で、整列・重複排除済みである必要はありません。</param>
/// <param name="PrimaryHashes">フォルダ直下の所持譜面の主ハッシュ。大文字小文字を無視して比べます。</param>
internal readonly record struct SplitPlacementFolderInput(ReadOnlyMemory<uint> AudioKeys, string[] PrimaryHashes);

/// <summary>探索の結果です。グループは、フォルダの番号の昇順の配列で、先頭の番号の昇順に並びます。</summary>
internal sealed class SplitPlacementSearchResult
{
    internal SplitPlacementSearchResult(
        IReadOnlyList<int[]> groups,
        int targetFolderCount,
        int candidateFolderCount,
        long registeredKeyCount,
        int genericKeyCount,
        long relationCount)
    {
        Groups = groups;
        TargetFolderCount = targetFolderCount;
        CandidateFolderCount = candidateFolderCount;
        RegisteredKeyCount = registeredKeyCount;
        GenericKeyCount = genericKeyCount;
        RelationCount = relationCount;
    }

    internal IReadOnlyList<int[]> Groups { get; }

    internal int TargetFolderCount { get; }

    internal int CandidateFolderCount { get; }

    /// <summary>プレビュー名を除いた後に、索引へ登録したキーと対象フォルダの組の数です。</summary>
    internal long RegisteredKeyCount { get; }

    internal int GenericKeyCount { get; }

    /// <summary>音源の条件を満たし、主ハッシュを共有しない（関係にある）フォルダの組の数です。</summary>
    internal long RelationCount { get; }

    internal int GroupCount => Groups.Count;
}

/// <summary>
/// 分裂配置の探索（判定用の音源集合、関係、グループ）を行う純粋な部品です。
/// ライブラリ・ファイル・ロックに依存せず、渡された入力だけから結果を求めます。
/// </summary>
/// <remarks>
/// 全フォルダの対を総当たりで比べません。キーと対象フォルダ番号の組を整列した索引を探索内だけで作り、汎用名（20個を超える対象フォルダが持つキー）を除くと、
/// どのキーも20個以下のフォルダにしか属さないため、候補フォルダごとに自分のキーの所属フォルダを数え上げる照合の回数は「音源の登録数 × 20」以下になります。
/// 関係の判定は、自分より集合が大きい相手（同数なら番号が大きい相手）についてだけ行い、同じ組を二度数えません。
/// 結果は並列度に依存しません。
/// </remarks>
internal static class SplitPlacementSearch
{
    /// <summary>候補フォルダに必要な判定用の音源集合の最小の個数です。</summary>
    internal const int MinimumKeyCount = 10;

    /// <summary>これを超える対象フォルダが持つキーを汎用名とします。</summary>
    internal const int GenericFolderLimit = 20;

    private static readonly uint[] defaultPreviewKeys = CreateDefaultPreviewKeys();

    /// <summary>
    /// フォルダ直下の <c>preview</c>・<c>_preview</c>・<c>__preview</c> にリソース索引が与える音声キーです。
    /// 索引と同じ正規化とハッシュ化で求めるため、拡張子とキーの大文字小文字は影響しません。
    /// </summary>
    internal static IReadOnlyCollection<uint> PreviewKeys => defaultPreviewKeys;

    private static uint[] CreateDefaultPreviewKeys()
    {
        string[] names = ["preview.wav", "_preview.wav", "__preview.wav"];
        return [.. names.Select(name => ChartResourceKeyHash.GetLookupHash(
            ChartResourcePathNormalizer.NormalizeResourceKeyForLookup(name))).Distinct()];
    }

    /// <summary>分裂配置のグループを探索します。</summary>
    /// <param name="folders">対象フォルダの入力。</param>
    /// <param name="previewKeyValues">除くプレビュー名のキー値。</param>
    /// <param name="progress">進捗の受け手。探索を実行するスレッドから同期に呼びます。受け手の例外は探索の失敗として伝わります。</param>
    /// <param name="cancellationToken">取消の合図。準備と照合の途中で <see cref="OperationCanceledException"/> を送出します。</param>
    /// <param name="maxDegreeOfParallelism">照合の並列度。0以下は既定（論理プロセッサ数）です。</param>
    internal static SplitPlacementSearchResult Search(
        IReadOnlyList<SplitPlacementFolderInput> folders,
        IReadOnlyCollection<uint> previewKeyValues,
        IProgress<SplitPlacementSearchProgress> progress = null,
        CancellationToken cancellationToken = default,
        int maxDegreeOfParallelism = 0)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(previewKeyValues);
        cancellationToken.ThrowIfCancellationRequested();

        int folderCount = folders.Count;
        progress?.Report(new SplitPlacementSearchProgress(SplitPlacementSearchStage.Prepare, 0, folderCount));
        uint[] preview = [.. previewKeyValues.Distinct()];
        Array.Sort(preview);

        // 1. プレビュー名を除いた各フォルダのキーと、索引へ登録する組の数。
        var kept = new ReadOnlyMemory<uint>[folderCount];
        long registered = 0;
        for (int i = 0; i < folderCount; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            kept[i] = FilterKeys(folders[i].AudioKeys, preview);
            registered += kept[i].Length;
        }

        // 2. キーと対象フォルダ番号の組を一つの配列へ整列する。同じキーの組が連続し、各キーの所属フォルダを数え上げられる。
        ulong[] packed = new ulong[checked((int)registered)];
        int position = 0;
        for (int i = 0; i < folderCount; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
            foreach (uint key in kept[i].Span)
            {
                packed[position++] = ((ulong)key << 32) | (uint)i;
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(packed);
        cancellationToken.ThrowIfCancellationRequested();

        // 3. 汎用名を除いた集合の大きさ。汎用名は、10個未満のフォルダも含む対象フォルダの数で決める。
        int[] size = new int[folderCount];
        int genericCount = 0;
        for (int start = 0; start < packed.Length;)
        {
            int end = FindRunEnd(packed, start);
            if (end - start > GenericFolderLimit)
            {
                genericCount++;
            }
            else
            {
                for (int t = start; t < end; t++)
                {
                    size[(int)(uint)packed[t]]++;
                }
            }
            start = end;
            cancellationToken.ThrowIfCancellationRequested();
        }

        // 4. 候補フォルダのうち二つ以上が持つキーだけを、照合用の「キー → フォルダ」「フォルダ → キー」の対応にする。
        int[] runFolders = new int[packed.Length];
        List<int> runStarts = [];
        int used = 0;
        int[] folderRunOffsets = new int[folderCount + 1];
        for (int start = 0; start < packed.Length;)
        {
            int end = FindRunEnd(packed, start);
            if (end - start <= GenericFolderLimit)
            {
                int begin = used;
                for (int t = start; t < end; t++)
                {
                    int folder = (int)(uint)packed[t];
                    if (size[folder] >= MinimumKeyCount)
                    {
                        runFolders[used++] = folder;
                    }
                }
                if (used - begin >= 2)
                {
                    runStarts.Add(begin);
                    for (int p = begin; p < used; p++)
                    {
                        folderRunOffsets[runFolders[p] + 1]++;
                    }
                }
                else
                {
                    used = begin;
                }
            }
            start = end;
            cancellationToken.ThrowIfCancellationRequested();
        }
        runStarts.Add(used);
        packed = null;
        for (int i = 0; i < folderCount; i++)
        {
            folderRunOffsets[i + 1] += folderRunOffsets[i];
        }
        int[] folderRuns = new int[folderRunOffsets[folderCount]];
        int[] cursor = new int[folderCount];
        for (int run = 0; run + 1 < runStarts.Count; run++)
        {
            for (int p = runStarts[run]; p < runStarts[run + 1]; p++)
            {
                int folder = runFolders[p];
                folderRuns[folderRunOffsets[folder] + cursor[folder]++] = run;
            }
        }

        int[] candidates = [.. Enumerable.Range(0, folderCount).Where(i => size[i] >= MinimumKeyCount)];
        progress?.Report(new SplitPlacementSearchProgress(SplitPlacementSearchStage.Prepare, folderCount, folderCount));
        cancellationToken.ThrowIfCancellationRequested();

        // 5. 候補フォルダごとの照合。
        var context = new MatchContext(size, [.. runStarts], runFolders, folderRunOffsets, folderRuns, folders);
        var relations = new List<long>();
        int done = 0;
        int step = Math.Max(1, candidates.Length / 200);
        object progressGate = new object();
        progress?.Report(new SplitPlacementSearchProgress(SplitPlacementSearchStage.Match, 0, candidates.Length));
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount,
        };
        try
        {
            Parallel.For(
                0,
                candidates.Length,
                options,
                () => new MatchWorker(folderCount),
                (index, _, worker) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    context.Match(candidates[index], worker);
                    int finished = Interlocked.Increment(ref done);
                    if (progress != null && (finished % step == 0 || finished == candidates.Length))
                    {
                        // 報告は直列にし、受け手へ同時に入らせない。値は報告時点の完了数で、単調に増える。
                        lock (progressGate)
                        {
                            progress.Report(new SplitPlacementSearchProgress(
                                SplitPlacementSearchStage.Match, Volatile.Read(ref done), candidates.Length));
                        }
                    }
                    return worker;
                },
                worker =>
                {
                    lock (relations)
                    {
                        relations.AddRange(worker.Relations);
                    }
                });
        }
        catch (AggregateException exception)
        {
            Exception failure = exception.Flatten().InnerExceptions.FirstOrDefault(inner => inner is not OperationCanceledException);
            if (failure == null)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        cancellationToken.ThrowIfCancellationRequested();

        // 6. 関係で連結し、二つ以上のフォルダを持つ成分をグループにする。
        IReadOnlyList<int[]> groups = BuildGroups(folderCount, relations);
        return new SplitPlacementSearchResult(groups, folderCount, candidates.Length, registered, genericCount, relations.Count);
    }

    private static int FindRunEnd(ulong[] packed, int start)
    {
        uint key = (uint)(packed[start] >> 32);
        int end = start + 1;
        while (end < packed.Length && (uint)(packed[end] >> 32) == key)
        {
            end++;
        }
        return end;
    }

    /// <summary>プレビュー名を除きます。整列・重複排除済みで除く値がなければ、複製せず同じ配列を返します。</summary>
    private static ReadOnlyMemory<uint> FilterKeys(ReadOnlyMemory<uint> keys, uint[] preview)
    {
        ReadOnlySpan<uint> span = keys.Span;
        bool clean = true;
        for (int i = 0; i < span.Length && clean; i++)
        {
            clean = (i == 0 || span[i] > span[i - 1]) && Array.BinarySearch(preview, span[i]) < 0;
        }
        if (clean)
        {
            return keys;
        }
        uint[] filtered = [.. span.ToArray().Where(key => Array.BinarySearch(preview, key) < 0).Distinct()];
        Array.Sort(filtered);
        return filtered;
    }

    private static IReadOnlyList<int[]> BuildGroups(int folderCount, List<long> relations)
    {
        if (relations.Count == 0)
        {
            return [];
        }
        int[] parent = new int[folderCount];
        for (int i = 0; i < folderCount; i++)
        {
            parent[i] = i;
        }
        bool[] inRelation = new bool[folderCount];
        foreach (long relation in relations)
        {
            int a = (int)(relation >> 32);
            int b = (int)(uint)relation;
            inRelation[a] = true;
            inRelation[b] = true;
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB)
            {
                parent[Math.Max(rootA, rootB)] = Math.Min(rootA, rootB);
            }
        }
        var byRoot = new Dictionary<int, List<int>>();
        var ordered = new List<List<int>>();
        for (int i = 0; i < folderCount; i++)
        {
            if (!inRelation[i])
            {
                continue;
            }
            int root = Find(parent, i);
            if (!byRoot.TryGetValue(root, out List<int> members))
            {
                members = [];
                byRoot[root] = members;
                ordered.Add(members);
            }
            members.Add(i);
        }
        return [.. ordered.Select(members => members.ToArray())];
    }

    private static int Find(int[] parent, int node)
    {
        while (parent[node] != node)
        {
            parent[node] = parent[parent[node]];
            node = parent[node];
        }
        return node;
    }

    /// <summary>スレッドごとの共有数の計数配列と、触れた番号の一覧、見つけた関係です。</summary>
    private sealed class MatchWorker(int folderCount)
    {
        internal int[] Counts { get; } = new int[folderCount];

        internal List<int> Touched { get; } = [];

        internal List<long> Relations { get; } = [];
    }

    private sealed class MatchContext(
        int[] size,
        int[] runStarts,
        int[] runFolders,
        int[] folderRunOffsets,
        int[] folderRuns,
        IReadOnlyList<SplitPlacementFolderInput> folders)
    {
        private readonly HashSet<string>[] hashSets = new HashSet<string>[folders.Count];

        /// <summary>候補フォルダ <paramref name="i"/> と、自分より後ろの相手との関係を判定します。</summary>
        internal void Match(int i, MatchWorker worker)
        {
            int[] counts = worker.Counts;
            List<int> touched = worker.Touched;
            int sizeI = size[i];
            for (int r = folderRunOffsets[i]; r < folderRunOffsets[i + 1]; r++)
            {
                int run = folderRuns[r];
                for (int p = runStarts[run]; p < runStarts[run + 1]; p++)
                {
                    int other = runFolders[p];
                    if (other == i || (size[other] < sizeI) || (size[other] == sizeI && other < i))
                    {
                        continue;
                    }
                    if (counts[other]++ == 0)
                    {
                        touched.Add(other);
                    }
                }
            }
            foreach (int other in touched)
            {
                int shared = counts[other];
                counts[other] = 0;
                if ((long)shared * 5 >= (long)sizeI * 4 && !SharesPrimaryHash(i, other))
                {
                    worker.Relations.Add(((long)i << 32) | (uint)other);
                }
            }
            touched.Clear();
        }

        private bool SharesPrimaryHash(int a, int b)
        {
            string[] x = folders[a].PrimaryHashes ?? [];
            string[] y = folders[b].PrimaryHashes ?? [];
            if (x.Length == 0 || y.Length == 0)
            {
                return false;
            }
            if (x.Length > y.Length)
            {
                (x, y, b) = (y, x, a);
            }
            if (y.Length <= 8)
            {
                foreach (string left in x)
                {
                    foreach (string right in y)
                    {
                        if (IsSame(left, right))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            HashSet<string> set = Volatile.Read(ref hashSets[b]);
            if (set == null)
            {
                var created = new HashSet<string>(y.Where(hash => !string.IsNullOrWhiteSpace(hash)), StringComparer.OrdinalIgnoreCase);
                set = Interlocked.CompareExchange(ref hashSets[b], created, null) ?? created;
            }
            return x.Any(hash => !string.IsNullOrWhiteSpace(hash) && set.Contains(hash));
        }

        private static bool IsSame(string left, string right)
            => !string.IsNullOrWhiteSpace(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
