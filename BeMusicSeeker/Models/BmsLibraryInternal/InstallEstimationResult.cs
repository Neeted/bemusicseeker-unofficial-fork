using System.Collections.Generic;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// 導入先推定の信頼度です。
/// 候補が主要一致指標で並び、弱い tie-break だけで順位が決まった場合は Low とします。
/// </summary>
internal enum InstallEstimationConfidence
{
    High,
    Low
}

/// <summary>
/// 推定候補 1 件分の評価結果です。
/// 上位候補の比較表示と warning 文言生成に利用します。
/// </summary>
internal sealed class InstallEstimationCandidate
{
    public string DirectoryPath { get; set; }

    public int AudioHealth { get; set; }

    public int AudioMatched { get; set; }

    public int AudioExactMatched { get; set; }

    public int VisualHealth { get; set; }

    public int VisualMatched { get; set; }

    public int VisualExactMatched { get; set; }

    public int MovieHealth { get; set; }

    public int MovieMatched { get; set; }

    public int MovieExactMatched { get; set; }

    public int OptionalImageHealth { get; set; }

    public int OptionalImageMatched { get; set; }

    public int OptionalImageExactMatched { get; set; }

    public int AudioFileCount { get; set; }

    public string RepresentativeTitle { get; set; } = string.Empty;

    public string RepresentativeArtist { get; set; } = string.Empty;

    public string ToSummary()
    {
        return string.Format(
            "dir={0} audio={1}/{2} exact={3} visual={4}/{5} exact={6} movie={7}/{8} exact={9} optional={10}/{11} exact={12} audioCount={13} title={14} artist={15}",
            DirectoryPath ?? string.Empty,
            AudioMatched,
            AudioHealth,
            AudioExactMatched,
            VisualMatched,
            VisualHealth,
            VisualExactMatched,
            MovieMatched,
            MovieHealth,
            MovieExactMatched,
            OptionalImageMatched,
            OptionalImageHealth,
            OptionalImageExactMatched,
            AudioFileCount,
            RepresentativeTitle ?? string.Empty,
            RepresentativeArtist ?? string.Empty);
    }
}

/// <summary>
/// 推定候補ディレクトリの代表譜面 metadata です。
/// Pending 画面の確認列へ流すため、ディスク I/O なしでメモリ上のライブラリから解決します。
/// </summary>
internal sealed class InstallDestinationRepresentativeMetadata
{
    public static InstallDestinationRepresentativeMetadata Empty { get; } = new InstallDestinationRepresentativeMetadata();

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;
}

/// <summary>
/// 導入先推定の最終結果です。
/// 自動適用可否と上位候補の概要をあわせて保持します。
/// </summary>
internal sealed class InstallEstimationResult
{
    public bool Success => !string.IsNullOrWhiteSpace(DestinationDirectory);

    public string DestinationDirectory { get; set; }

    public InstallEstimationConfidence Confidence { get; set; } = InstallEstimationConfidence.High;

    public bool ShouldAutoApplyDestination { get; set; }

    public string ConfidenceReason { get; set; }

    public List<InstallEstimationCandidate> Candidates { get; } = new List<InstallEstimationCandidate>();

    public InstallEstimationCandidate SelectedCandidate => Candidates.Count > 0 ? Candidates[0] : null;

    public InstallEstimationCandidate SecondCandidate => Candidates.Count > 1 ? Candidates[1] : null;

    public int CandidateDirectoryCount { get; set; }

    public bool UsedFallbackCandidateExpansion { get; set; }

    public int CandidateDirectoryCountBeforeHashFilter { get; set; }

    public int CandidateDirectoryCountAfterHashFilter { get; set; }

    public int TargetResourceHashCount { get; set; }

    public long EvaluationMs { get; set; }

    public string ResourceSummary { get; set; }

    public string SelectedCandidateSummary { get; set; }

    public string TopCandidateSummary { get; set; }
}
