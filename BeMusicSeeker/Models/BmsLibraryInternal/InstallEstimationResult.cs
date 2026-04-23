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

internal enum InstallEstimationLowConfidenceKind
{
    None,
    AmbiguousCandidates,
    MetadataMismatch
}

internal enum InstallEstimationFinalEvaluationMode
{
    RelativeStrict,
    BasenameOnlyFastPath
}

/// <summary>
/// 推定候補 1 件分の評価結果です。
/// 上位候補の比較表示と warning 文言生成に利用します。
/// </summary>
internal sealed class InstallEstimationCandidate
{
    public string DirectoryPath { get; set; }

    public bool IsSourceCandidate { get; set; }

    public bool IsViableDestination { get; set; }

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

    public int AudioPrecision { get; set; }

    public int AudioJaccard { get; set; }

    public int VisualPrecision { get; set; }

    public int VisualJaccard { get; set; }

    public int MoviePrecision { get; set; }

    public int MovieJaccard { get; set; }

    public int OptionalImagePrecision { get; set; }

    public int OptionalImageJaccard { get; set; }

    public string RepresentativeTitle { get; set; } = string.Empty;

    public string RepresentativeArtist { get; set; } = string.Empty;

    public string ToSummary()
    {
        return string.Format(
            "dir={0} source={1} viable={2} audio={3}/{4} exact={5} visual={6}/{7} exact={8} movie={9}/{10} exact={11} optional={12}/{13} exact={14} audioCount={15} audioPrecision={16} audioJaccard={17} visualPrecision={18} visualJaccard={19} moviePrecision={20} movieJaccard={21} optionalPrecision={22} optionalJaccard={23} title={24} artist={25}",
            DirectoryPath ?? string.Empty,
            IsSourceCandidate ? 1 : 0,
            IsViableDestination ? 1 : 0,
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
            AudioPrecision,
            AudioJaccard,
            VisualPrecision,
            VisualJaccard,
            MoviePrecision,
            MovieJaccard,
            OptionalImagePrecision,
            OptionalImageJaccard,
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

    public bool HasViableDestination { get; set; }

    public string ConfidenceReason { get; set; }

    public InstallEstimationLowConfidenceKind LowConfidenceKind { get; set; } = InstallEstimationLowConfidenceKind.None;

    public List<InstallEstimationCandidate> Candidates { get; } = new List<InstallEstimationCandidate>();

    public InstallEstimationCandidate SelectedCandidate => Candidates.Count > 0 ? Candidates[0] : null;

    public InstallEstimationCandidate SecondCandidate => Candidates.Count > 1 ? Candidates[1] : null;

    public List<string> SuggestedDestinationDirectories { get; } = new List<string>();

    public int CandidateDirectoryCount { get; set; }

    public bool UsedFallbackCandidateExpansion { get; set; }

    public int CandidateDirectoryCountBeforeHashFilter { get; set; }

    public int CandidateDirectoryCountAfterHashFilter { get; set; }

    public int CandidateDirectoryCountAfterBroadFilter { get; set; }

    public int CandidateDirectoryCountAfterAudioGate { get; set; }

    public int HierarchyCandidateDirectoryCount { get; set; }

    public int AncestorShadowSuppressedCount { get; set; }

    public int LazySelfOwnedEvaluationCount { get; set; }

    public int TargetResourceCount { get; set; }

    public int TargetResourceHashCount { get; set; }

    public int TargetPathAwareHashCount { get; set; }

    public int TargetPathAwareAudioHashCount { get; set; }

    public int TargetPathAwareVisualHashCount { get; set; }

    public int TargetPathAwareMovieHashCount { get; set; }

    public int TargetPathAwareOptionalImageHashCount { get; set; }

    public int AudioReferenceCount { get; set; }

    public int VisualReferenceCount { get; set; }

    public int MovieReferenceCount { get; set; }

    public int OptionalImageReferenceCount { get; set; }

    public int AudioMinimumMatchRequired { get; set; }

    public InstallEstimationFinalEvaluationMode FinalEvaluationMode { get; set; } = InstallEstimationFinalEvaluationMode.RelativeStrict;

    public int BundledAudioCount { get; set; }

    public int BundledImageCount { get; set; }

    public int BundledMovieCount { get; set; }

    public string CandidateMode { get; set; }

    public string CoarseFilterMode { get; set; }

    public long EvaluationMs { get; set; }

    public long CandidateViewBuildMs { get; set; }

    public long CandidateMatchMs { get; set; }

    public int CandidateViewBuildCount { get; set; }

    public int CandidateViewFallbackCount { get; set; }

    public long AncestorShadowEvaluationMs { get; set; }

    public string ResourceSummary { get; set; }

    public string SelectedCandidateSummary { get; set; }

    public string TopCandidateSummary { get; set; }

    public string MetadataFrontierSummary { get; set; }

    public string MetadataTieBreakSummary { get; set; }

    public string MetadataValidationSummary { get; set; }
}
