namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>局所 entry 変更で実際に読む参照・捕捉する候補・変更する所属列の量を観測します。</summary>
internal interface IPlaylistEntryMutationWorkObserver
{
    /// <summary>対象値または順序探索のために entry 参照を取得した地点で呼びます。</summary>
    void OnEntryReferenceRead();
    /// <summary>保存対象の一行を候補へ捕捉した地点で呼びます。</summary>
    void OnCandidateCaptured();
    /// <summary>構造共有した所属列の一箇所を追加・削除した地点で呼びます。</summary>
    void OnMembershipChanged();
}
