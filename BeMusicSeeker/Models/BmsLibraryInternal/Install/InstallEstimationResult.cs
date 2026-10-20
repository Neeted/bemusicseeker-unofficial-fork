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
    InstalledDestinationAmbiguous,
    InstalledDestinationAutoAppliedAmbiguous,
    MetadataMismatch,
    ReinstallNotImproved
}

/// <summary>最終評価の根拠です。ハッシュ候補にも共通の音源完全性条件を適用します。</summary>
internal enum InstallEstimationFinalEvaluationMode
{
    RelativeStrict,
    InstalledChartHash
}

/// <summary>
/// 推定候補 1 件分の評価結果です。
/// 上位候補の比較表示と warning 文言生成に利用します。
/// </summary>
internal sealed class InstallEstimationCandidate
{
    public string DirectoryPath { get; set; }

    public bool IsSourceCandidate { get; set; }

    /// <summary>Strongの検証前に、選択経路固有の候補要件を満たした場合に真です。</summary>
    public bool IsViableDestination { get; set; }

    public int AudioHealth { get; set; }

    /// <summary>充足率の分母となるユニークな要求音源数です。</summary>
    public int AudioRequired { get; set; }

    public int AudioMatched { get; set; }

    public int AudioExactMatched { get; set; }

    public int VisualHealth { get; set; }

    /// <summary>充足率の分母となるユニークな要求画像数です。</summary>
    public int VisualRequired { get; set; }

    public int VisualMatched { get; set; }

    public int VisualExactMatched { get; set; }

    public int MovieHealth { get; set; }

    /// <summary>充足率の分母となるユニークな要求動画数です。</summary>
    public int MovieRequired { get; set; }

    public int MovieMatched { get; set; }

    public int MovieExactMatched { get; set; }

    public int OptionalImageHealth { get; set; }

    /// <summary>充足率の分母となるユニークな要求任意画像数です。</summary>
    public int OptionalImageRequired { get; set; }

    public int OptionalImageMatched { get; set; }

    public int OptionalImageExactMatched { get; set; }

    /// <summary>カテゴリ別の候補側キー件数の合計です。音源だけの件数ではありません。</summary>
    public int CandidateResourceCount { get; set; }

    public int AudioJaccard { get; set; }

    public int VisualJaccard { get; set; }

    public int MovieJaccard { get; set; }

    public int OptionalImageJaccard { get; set; }

    /// <summary>対象群との共通照合で決まった関連性です。音源状態とは独立しています。</summary>
    public InstallEstimationMetadataRelation MetadataRelation { get; set; }

    /// <summary>分類の根拠となった候補側実在ペアの、小文字化前の主題です。</summary>
    public string DisplayTitle { get; set; } = string.Empty;

    /// <summary>表示主題と同じ入力レコードに由来する作者です。</summary>
    public string DisplayArtist { get; set; } = string.Empty;

    public string ToSummary()
        => $"dir={DirectoryPath} viable={IsViableDestination} audio={AudioMatched}/{AudioRequired} H={AudioHealth} M={AudioExactMatched} J={AudioJaccard} visual={VisualMatched}/{VisualRequired} H={VisualHealth} M={VisualExactMatched} J={VisualJaccard} movie={MovieMatched}/{MovieRequired} H={MovieHealth} M={MovieExactMatched} J={MovieJaccard} optional={OptionalImageMatched}/{OptionalImageRequired} H={OptionalImageHealth} M={OptionalImageExactMatched} J={OptionalImageJaccard} candidateResources={CandidateResourceCount} relation={MetadataRelation} title={DisplayTitle} artist={DisplayArtist}";

}

/// <summary>
/// 導入先推定の最終結果です。
/// 自動適用可否と上位候補の概要をあわせて保持します。
/// </summary>
internal sealed class InstallEstimationResult
{
    /// <summary>一操作で一回評価した入力元の観測数・確認状態・Related規則です。永続化しません。</summary>
    internal InstallEstimationSourceAudio SourceAudio { get; set; }

    public bool Success => !string.IsNullOrWhiteSpace(DestinationDirectory);

    public string DestinationDirectory { get; set; }

    public InstallEstimationConfidence Confidence { get; set; } = InstallEstimationConfidence.High;

    public bool ShouldAutoApplyDestination { get; set; }

    /// <summary>選択された有効候補への音源完全性です。TITLE/ARTISTの結果とは独立しています。</summary>
    internal InstallAudioCompleteness AudioCompleteness { get; set; }

    /// <summary>音源状態とは独立した、各対象譜面への設定閾値の判定結果です。</summary>
    internal bool AudioAutoApplyAllowed { get; set; }
    /// <summary>設定値不正・閾値未達・確認不能など、音源による許可判断の診断理由です。</summary>
    internal string AudioAutoApplyReason { get; set; } = string.Empty;
    /// <summary>表示上限外も含む競合Strongのパスです。表示順位2位と一致するとは限りません。</summary>
    internal string AmbiguousCandidateDirectory { get; set; }

    /// <summary>評価に使った一操作の候補方針です。永続化しません。</summary>
    internal InstallEstimationCandidatePolicy CandidatePolicy { get; set; }
    /// <summary>要求キーの逆引きで訪問したフォルダ登録数です。候補制限範囲外の訪問も含めます。</summary>
    internal long ReverseLookupVisits { get; set; }
    /// <summary>非関連除外前の全リソース有効候補の分類件数です。</summary>
    internal string MetadataClassificationSummary { get; set; } = string.Empty;
    /// <summary>全候補のプロフィール解決と分類が終端するまでの時間です。</summary>
    internal long MetadataEvaluationMs { get; set; }
    /// <summary>分類器が実際に評価したペア数です。早期Strong確定後の未照合ペアは数えません。</summary>
    internal int MetadataPairComparisonCount { get; set; }
    /// <summary>各譜面へ適用した、捕捉済みの設定閾値です。範囲外値も診断のため保持します。</summary>
    internal int AudioAutoApplyThreshold { get; set; }

    /// <summary>評価で使った候補索引を複製せず共有します。未取得のnullもそのまま反映へ渡し、最新索引で補いません。</summary>
    internal DirectoryResourceLookupCache PendingDirectoryLookupCache { get; set; }

    /// <summary>同じ推定操作で捕捉した同梱索引です。反映時の健康度評価で入力元を再列挙しません。</summary>
    internal DirectoryResourceLookupCache.Entry PendingBundledResources { get; set; }

    /// <summary>同梱の不存在を確定できる完全な列挙結果かを、観測済み索引とともに渡します。</summary>
    internal bool PendingBundledResourcesComplete { get; set; } = true;

    /// <summary>資源充足またはハッシュの単独最多・一意条件を満たす候補の有無です。自動適用にはStrongも必要です。</summary>
    public bool HasViableDestination { get; set; }

    public string ConfidenceReason { get; set; }

    public InstallEstimationLowConfidenceKind LowConfidenceKind { get; set; } = InstallEstimationLowConfidenceKind.None;

    public List<InstallEstimationCandidate> Candidates { get; } = [];

    public InstallEstimationCandidate SelectedCandidate => Candidates.Count > 0 ? Candidates[0] : null;

    public InstallEstimationCandidate SecondCandidate => Candidates.Count > 1 ? Candidates[1] : null;

    public List<string> SuggestedDestinationDirectories { get; } = [];

    public int CandidateDirectoryCount { get; set; }

    public int CandidateEvaluationDegree { get; set; }

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

    public int SourceBaselinePrimaryHealth { get; set; }

    public int SelectedCandidatePrimaryHealth { get; set; }

    public InstallEstimationFinalEvaluationMode FinalEvaluationMode { get; set; } = InstallEstimationFinalEvaluationMode.RelativeStrict;

    public int BundledAudioCount { get; set; }

    public int BundledImageCount { get; set; }

    public int BundledMovieCount { get; set; }

    public string CandidateMode { get; set; }

    public string CoarseFilterMode { get; set; }

    public long EvaluationMs { get; set; }

    public long AncestorShadowEvaluationMs { get; set; }

    public string ResourceSummary { get; set; }

    public string SelectedCandidateSummary { get; set; }

    public string TopCandidateSummary { get; set; }

    public string MetadataValidationSummary { get; set; }

    public bool SelectedCandidateMetadataEvidenceStrong { get; set; }

    public bool DirectoryHashCountTieBreakApplied { get; set; }

    public string DirectoryHashCountTieBreakSummary { get; set; }
}
