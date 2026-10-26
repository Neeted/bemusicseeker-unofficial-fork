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

/// <summary>確認済みの音源件数と完全性です。閾値による許可は欠損状態を変更しません。</summary>
internal readonly record struct InstallAudioEvaluation(InstallAudioCompleteness Completeness, int Existing, int Required)
{
    /// <summary>確認可能な各譜面だけを、丸めずに設定閾値と比較します。</summary>
    internal bool MeetsThreshold(int threshold) => threshold is >= 90 and <= 100
        && Completeness is InstallAudioCompleteness.Complete or InstallAudioCompleteness.Missing
        && (Required == 0 || 100L * Existing >= (long)threshold * Required);
}

/// <summary>一操作で捕捉した不変の配置文脈です。同梱・候補の索引を共有し、索引ルートから譜面までの相対位置を両方の照合へ適用します。</summary>
internal sealed class ChartResourcePlacement(
    ChartResourcePlacementBasis basis,
    string directory,
    DirectoryResourceLookupCache.Entry bundled = null,
    DirectoryResourceLookupCache.Entry candidate = null,
    bool bundledComplete = true,
    string relativeDirectory = "")
{
    /// <summary>確認基準です。</summary>
    internal ChartResourcePlacementBasis Basis { get; } = basis;
    /// <summary>資源を配置したとみなすディレクトリです。</summary>
    internal string Directory { get; } = directory;
    /// <summary>同梱・候補索引のルートから譜面までの相対ディレクトリです。現在配置への復元や同じ項目の再評価にも引き継ぎます。</summary>
    internal string RelativeDirectory { get; } = relativeDirectory ?? string.Empty;

    /// <summary>捕捉した同梱だけの索引です。現在配置や候補の物理存在とは独立しています。</summary>
    internal DirectoryResourceLookupCache.Entry BundledResources => bundled;
    /// <summary>捕捉した同梱列挙の完全性です。</summary>
    internal bool BundledResourcesComplete => bundledComplete;

    /// <summary>捕捉済み索引・完全性・譜面相対位置を保ち、配置の基準pathだけを成功先へ追従させます。</summary>
    internal ChartResourcePlacement Relocate(string source, string destination)
        => Basis != ChartResourcePlacementBasis.Current && LibraryInstallDestinationChange.IsUnderFolder(Directory, source)
            ? new(Basis, LibraryInstallDestinationChange.RelocatePath(Directory, source, destination), bundled, candidate, bundledComplete, RelativeDirectory)
            : this;

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
        uint hash = ChartResourceKeyHash.GetLookupHash(string.IsNullOrEmpty(RelativeDirectory) ? key : Path.Combine(RelativeDirectory, key));
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
    internal InstallAudioCompleteness EvaluateAudio(ChartResourceSnapshot resources) => EvaluateAudioResources(resources).Completeness;

    /// <summary>存在確認を一回だけ行い、許可と警告で同じ結果を共有します。</summary>
    internal InstallAudioEvaluation EvaluateAudioResources(ChartResourceSnapshot resources)
    {
        if (resources == null || resources.HasUnresolvedAudio) { return new(InstallAudioCompleteness.Unverifiable, 0, resources?.AudioReferenceCount ?? 0); }
        int? existing = CountExisting(resources.AudioReferences, ChartResourceKind.Audio);
        return new(!existing.HasValue ? InstallAudioCompleteness.Unverifiable
            : existing.Value == resources.AudioReferenceCount ? InstallAudioCompleteness.Complete : InstallAudioCompleteness.Missing,
            existing ?? 0, resources.AudioReferenceCount);
    }

    /// <summary>設定先、先頭候補、現在配置の順で選びます。導入先には譜面相対配置を適用し、入力元は実際の譜面親を使います。索引は導入先ルートのまま共有します。</summary>
    internal static ChartResourcePlacement Select(ChartFile chart, DirectoryResourceLookupCache.Entry bundled, DirectoryResourceLookupCache cache, bool bundledComplete = true, string relativeDirectory = "")
    {
        bool configured = !string.IsNullOrWhiteSpace(chart.InstallDestination);
        string destination = configured ? chart.InstallDestination : chart.InstallDestinationSuggestions.FirstOrDefault();
        return string.IsNullOrWhiteSpace(destination)
            ? new(ChartResourcePlacementBasis.Current, Path.GetDirectoryName(chart.Path), bundled, bundledComplete: bundledComplete, relativeDirectory: relativeDirectory)
            : new(configured ? ChartResourcePlacementBasis.Destination : ChartResourcePlacementBasis.FirstCandidate,
                Path.Combine(destination, relativeDirectory ?? string.Empty), bundled, cache?.GetEntryOrNull(destination), bundledComplete, relativeDirectory);
    }
}
