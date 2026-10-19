namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>一操作で表示する候補と順位の方針です。評価用途や永続設定とは独立しています。</summary>
internal enum InstallEstimationCandidatePolicy
{
    /// <summary>関連候補だけを残し、関連性を優先します。</summary>
    RelatedOnly,
    /// <summary>非関連候補も残し、リソース順位を優先します。</summary>
    KeepUnrelated
}
