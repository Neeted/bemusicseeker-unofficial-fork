using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 同じディレクトリに、拡張子を除いた名前が一致する <c>.wav</c> と <c>.ogg</c> が両方ある名前です。
/// </summary>
/// <param name="Directory">ディレクトリのパス。表記は <c>.wav</c> のファイルのものです。</param>
/// <param name="Name">拡張子を含まない名前。表記は <c>.wav</c> のファイルのものです。</param>
internal readonly record struct WavOggCollision(string Directory, string Name);

/// <summary>衝突名の列挙の結果です。成功は0組でも完全な結果で、失敗の結果は部分集合を持ちません。</summary>
internal sealed class WavOggCollisionSearchResult
{
    private WavOggCollisionSearchResult()
    {
    }

    /// <summary>列挙が完全に成功したか。</summary>
    internal bool Success { get; private init; }

    /// <summary>列挙の経路の名前（診断用）。</summary>
    internal string BackendName { get; private init; } = string.Empty;

    /// <summary>失敗の理由。成功時は空です。</summary>
    internal string FailureReason { get; private init; } = string.Empty;

    /// <summary>成功時の組。ディレクトリ、名前の序数順です。</summary>
    internal IReadOnlyList<WavOggCollision> Collisions { get; private init; } = [];

    /// <summary>列挙（問い合わせまたは走査）の所要時間。</summary>
    internal long EnumerationMs { get; private init; }

    /// <summary>集計の所要時間。ネイティブ側で集計した場合はネイティブ側の値です。</summary>
    internal long AggregateMs { get; private init; }

    internal static WavOggCollisionSearchResult Succeeded(
        string backendName, IReadOnlyList<WavOggCollision> collisions, long enumerationMs, long aggregateMs)
        => new()
        {
            Success = true,
            BackendName = backendName ?? string.Empty,
            Collisions = collisions ?? [],
            EnumerationMs = enumerationMs,
            AggregateMs = aggregateMs
        };

    internal static WavOggCollisionSearchResult Failed(string backendName, string reason)
        => new()
        {
            Success = false,
            BackendName = backendName ?? string.Empty,
            FailureReason = string.IsNullOrWhiteSpace(reason) ? "unknown" : reason
        };
}

/// <summary>
/// 音源のパスから、同じディレクトリで拡張子なしの名前が一致する <c>.wav</c> と <c>.ogg</c> の組を集めます。
/// ネイティブ側の集計（<c>EBridge_FindWavOggCollisions</c>）と同じ条件表を持ち、管理側の列挙の経路で使います。
/// </summary>
internal static class WavOggCollisionAggregator
{
    /// <summary>
    /// ファイルのパスを集計し、<c>.wav</c> と <c>.ogg</c> が両方ある名前を、ディレクトリ、名前の序数順で返します。
    /// </summary>
    /// <remarks>
    /// 名前・拡張子・ディレクトリの比較は大文字小文字を区別しません。拡張子は最後のピリオドより後ろで、拡張子なしの名前が空のファイル（<c>.wav</c>）、
    /// および <c>.wav</c>・<c>.ogg</c> 以外のファイルは数えません。表記は <c>.wav</c> のファイルのものを返します。
    /// </remarks>
    /// <param name="filePaths">ファイル（ディレクトリを含まない）のパス。</param>
    internal static List<WavOggCollision> Collect(IEnumerable<string> filePaths)
    {
        var slots = new Dictionary<string, Slot>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in filePaths ?? [])
        {
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }
            int separator = path.LastIndexOfAny(['\\', '/']);
            if (separator <= 0)
            {
                continue;
            }
            int nameStart = separator + 1;
            int dot = path.LastIndexOf('.');
            if (dot <= nameStart)
            {
                // ピリオドがない、または拡張子なしの名前が空。
                continue;
            }
            ReadOnlySpan<char> extension = path.AsSpan(dot + 1);
            bool isWav = extension.Equals("wav", StringComparison.OrdinalIgnoreCase);
            bool isOgg = !isWav && extension.Equals("ogg", StringComparison.OrdinalIgnoreCase);
            if (!isWav && !isOgg)
            {
                continue;
            }

            // ドライブのルート直下（C:\x.wav）のディレクトリは C:\ とする。
            string directory = separator == 2 && path[1] == ':' ? path[..3] : path[..separator];
            if (directory.Contains('/'))
            {
                directory = directory.Replace('/', '\\');
            }
            string stem = path[nameStart..dot];
            string key = directory + "\0" + stem;
            if (!slots.TryGetValue(key, out Slot slot))
            {
                slot = new Slot();
                slots.Add(key, slot);
            }
            if (isWav)
            {
                if (!slot.HasWav)
                {
                    slot.HasWav = true;
                    slot.Directory = directory;
                    slot.Name = stem;
                }
            }
            else
            {
                slot.HasOgg = true;
            }
        }

        List<WavOggCollision> collisions = [.. slots.Values
            .Where(slot => slot.HasWav && slot.HasOgg)
            .Select(slot => new WavOggCollision(slot.Directory, slot.Name))];
        collisions.Sort(static (x, y) =>
        {
            int byDirectory = string.CompareOrdinal(x.Directory, y.Directory);
            return byDirectory != 0 ? byDirectory : string.CompareOrdinal(x.Name, y.Name);
        });
        return collisions;
    }

    private sealed class Slot
    {
        internal bool HasWav;

        internal bool HasOgg;

        internal string Directory;

        internal string Name;
    }
}

/// <summary>
/// 登録BMSルートの配下にある、同名の <c>.wav</c> と <c>.ogg</c> の組を列挙します。
/// Everythingを利用できる場合はネイティブブリッジで列挙と集計を行い、利用できない場合は管理側の列挙で同じ範囲を同じ規則で集めます。
/// </summary>
internal static class WavOggCollisionFinder
{
    private const string AudioGroupName = "wav_ogg";

    private const string ManagedBackendName = "fast";

    private static readonly string[] BridgeExtensions = ["wav", "ogg"];

    /// <summary>
    /// 組を列挙します。範囲と除外はライブラリの走査と同じ（登録ルートの配下全体）です。
    /// </summary>
    /// <remarks>
    /// <paramref name="rootFileEnumerator"/> があればそれだけで列挙し、Everythingを使いません。なければEverythingで列挙し、
    /// 結果の採用と切替えは <see cref="SelectResult"/> に従います。登録ルートが存在しないなど、列挙が不完全な場合は失敗です。
    /// </remarks>
    /// <param name="rootDirectories">登録BMSルート。</param>
    /// <param name="everythingNative">ネイティブブリッジの窓口。</param>
    /// <param name="rootFileEnumerator">呼出し元が所有する列挙役。本番では <see langword="null"/> です。</param>
    internal static WavOggCollisionSearchResult Find(
        IEnumerable<string> rootDirectories,
        EverythingNative everythingNative,
        IRootFileEnumerator rootFileEnumerator = null)
    {
        List<string> requested = [.. rootDirectories ?? []];
        if (rootFileEnumerator != null)
        {
            return EnumerateManaged(rootFileEnumerator, requested);
        }

        var rootState = new RootFileEnumerationResult();
        List<string> roots = RootFileEnumerationService.NormalizeExecutionRoots(requested, rootState);
        if (!rootState.IsComplete)
        {
            // 管理側の列挙でも同じ登録ルートが不完全になるため、切り替えずに失敗とする。
            return WavOggCollisionSearchResult.Failed(ManagedBackendName, RootFileEnumerationService.GetNonAuthoritativeReason(rootState));
        }
        if (roots.Count == 0)
        {
            return WavOggCollisionSearchResult.Succeeded(ManagedBackendName, [], 0L, 0L);
        }

        WavOggCollisionSearchResult everything = TryFindWithEverything(roots, everythingNative);
        return SelectResult(everything, () => EnumerateManaged(new FastRootFileEnumerator(), roots));
    }

    /// <summary>
    /// Everythingの結果を採用するか、管理側の列挙へ切り替えるか、探索の失敗とするかを選びます。
    /// </summary>
    /// <remarks>
    /// 成功は組が0件でも完全な結果として採用し、管理側で確かめ直しません。ブリッジに入口がない場合と契約・版の不一致は配置の異常のため、
    /// 切り替えずにその失敗を返します（<see cref="RootFileEnumerationService.IsBridgeContractFailure"/>）。
    /// Everythingを利用できない場合と問い合わせの失敗だけ、<paramref name="fallbackFactory"/> を一回実行します。
    /// </remarks>
    /// <param name="everything">Everythingによる列挙の結果。</param>
    /// <param name="fallbackFactory">管理側の列挙を実行する処理。</param>
    internal static WavOggCollisionSearchResult SelectResult(
        WavOggCollisionSearchResult everything,
        Func<WavOggCollisionSearchResult> fallbackFactory)
    {
        if (everything != null && everything.Success)
        {
            return everything;
        }
        if (RootFileEnumerationService.IsBridgeContractFailure(everything?.FailureReason))
        {
            return everything;
        }
        return fallbackFactory?.Invoke()
            ?? WavOggCollisionSearchResult.Failed(ManagedBackendName, "enumeration_result_missing");
    }

    private static WavOggCollisionSearchResult TryFindWithEverything(IReadOnlyList<string> roots, EverythingNative everythingNative)
    {
        string query = EverythingNative.BuildFilesQuery([.. roots], BridgeExtensions);
        if (!everythingNative.TryFindWavOggCollisions(query, out EverythingNative.BridgeWavOggCollisionResult result, out string reason))
        {
            return WavOggCollisionSearchResult.Failed(EverythingNative.GroupedEnumerationBackendName, reason);
        }
        return WavOggCollisionSearchResult.Succeeded(
            EverythingNative.GroupedEnumerationBackendName, result.Collisions, result.QueryMs, result.AggregateMs);
    }

    private static WavOggCollisionSearchResult EnumerateManaged(IRootFileEnumerator enumerator, IEnumerable<string> roots)
    {
        RootFileEnumerationGroup[] groups = [new RootFileEnumerationGroup(AudioGroupName, [".wav", ".ogg"])];
        RootFileEnumerationResult enumeration = enumerator.EnumerateFiles(roots, groups, verboseLog: false);
        string backend = string.IsNullOrWhiteSpace(enumeration?.BackendName) ? ManagedBackendName : enumeration.BackendName;
        if (!RootFileEnumerationService.IsAuthoritativeComplete(enumeration))
        {
            return WavOggCollisionSearchResult.Failed(backend, RootFileEnumerationService.GetNonAuthoritativeReason(enumeration));
        }
        var aggregateStopwatch = Stopwatch.StartNew();
        List<WavOggCollision> collisions = WavOggCollisionAggregator.Collect(enumeration.GetPaths(AudioGroupName));
        aggregateStopwatch.Stop();
        return WavOggCollisionSearchResult.Succeeded(backend, collisions, enumeration.EnumerationMs, aggregateStopwatch.ElapsedMilliseconds);
    }
}
