using System.IO;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>存在確認に使う配置の由来です。譜面本体の読取り元とは独立しています。</summary>
internal enum ChartResourcePlacementBasis
{
    Current,
    Destination,
    FirstCandidate
}

/// <summary>音源完全性の独立した評価結果です。照合不能を欠損件数へ変換しません。</summary>
internal enum InstallAudioCompleteness
{
    NotEvaluated,
    Complete,
    Missing,
    Unverifiable
}

/// <summary>一操作で捕捉した不変の配置文脈です。同梱・候補の変更不能な索引を共有し、行ごとに複製しません。</summary>
internal sealed class ChartResourcePlacement(
    ChartResourcePlacementBasis basis,
    string directory,
    DirectoryResourceLookupCache.Entry bundled = null,
    DirectoryResourceLookupCache.Entry candidate = null,
    bool bundledComplete = true)
{
    /// <summary>確認基準です。</summary>
    internal ChartResourcePlacementBasis Basis { get; } = basis;
    /// <summary>資源を配置したとみなすディレクトリです。</summary>
    internal string Directory { get; } = directory;

    /// <summary>現在配置は物理確認し、導入後配置は種別別の候補と同梱の和集合で確認します。</summary>
    internal bool? Exists(string key, ChartResourceKind kind)
    {
        if (string.IsNullOrWhiteSpace(key)) { return null; }
        if (Basis == ChartResourcePlacementBasis.Current)
        {
            string[] extensions = kind switch
            {
                ChartResourceKind.Audio => ChartResourceExtensions.AudioExtensions,
                ChartResourceKind.Image => ChartResourceExtensions.ImageExtensions,
                ChartResourceKind.Movie => ChartResourceExtensions.MovieExtensions,
                _ => []
            };
            return extensions.Length == 0 ? null : ChartResourceExistence.ExistsWithCompatibleExtensions(Directory, key, extensions);
        }
        uint hash = ChartResourceKeyHash.GetLookupHash(key);
        bool Contains(DirectoryResourceLookupCache.Entry entry) => kind switch
        {
            ChartResourceKind.Audio => entry?.ContainsAudioRelativePathHash(hash) == true,
            ChartResourceKind.Image => entry?.ContainsImageRelativePathHash(hash) == true,
            ChartResourceKind.Movie => entry?.ContainsMovieRelativePathHash(hash) == true,
            _ => false
        };
        if (Contains(candidate) || Contains(bundled)) { return true; }
        return candidate == null || !bundledComplete ? null : false;
    }

    /// <summary>照合不能な参照の存在確認は未確認として返します。</summary>
    internal bool? Exists(ChartResourceReference reference)
        => reference.Status != ChartResourcePathNormalizationStatus.Valid ? null
            : Exists(reference.LookupKey, reference.Usage == ChartResourceUsage.Normal ? reference.Kind : ChartResourceKind.Image);

    /// <summary>使用集合の重複を除いた件数を評価し、未取得を正常な0件へ置き換えません。</summary>
    internal int? CountExisting(System.Collections.Generic.IReadOnlyList<ChartResourceSnapshot.ResourceReference> references, ChartResourceKind kind)
    {
        int count = 0;
        foreach (ChartResourceSnapshot.ResourceReference reference in references)
        {
            bool? exists = Exists(reference.LookupKey, kind);
            if (!exists.HasValue) { return null; }
            if (exists.Value) { count++; }
        }
        return count;
    }

    /// <summary>メタデータ判定と独立して、推定対象群の導入後音源完全性を返します。</summary>
    internal InstallAudioCompleteness EvaluateAudio(ChartResourceSnapshot resources)
    {
        if (resources == null || resources.HasUnresolvedAudio) { return InstallAudioCompleteness.Unverifiable; }
        int? existing = CountExisting(resources.AudioReferences, ChartResourceKind.Audio);
        return !existing.HasValue ? InstallAudioCompleteness.Unverifiable
            : existing.Value == resources.AudioReferenceCount ? InstallAudioCompleteness.Complete : InstallAudioCompleteness.Missing;
    }

    /// <summary>設定先、先頭候補、現在配置の順で選びます。入力元親全体を同梱とはみなしません。</summary>
    internal static ChartResourcePlacement Select(ChartFile chart, DirectoryResourceLookupCache.Entry bundled, DirectoryResourceLookupCache cache, bool bundledComplete = true)
    {
        bool configured = !string.IsNullOrWhiteSpace(chart.InstallDestination);
        string destination = configured ? chart.InstallDestination : chart.InstallDestinationSuggestions.FirstOrDefault();
        return string.IsNullOrWhiteSpace(destination)
            ? new(ChartResourcePlacementBasis.Current, Path.GetDirectoryName(chart.Path))
            : new(configured ? ChartResourcePlacementBasis.Destination : ChartResourcePlacementBasis.FirstCandidate,
                destination, bundled, cache?.GetEntryOrNull(destination), bundledComplete);
    }
}
