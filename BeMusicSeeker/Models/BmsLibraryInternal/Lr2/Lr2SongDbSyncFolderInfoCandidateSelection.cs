namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2SongDbSyncFolderInfoCandidateSelection(
    Lr2FolderInfoCandidateSnapshot candidates,
    Lr2TextMetadataCandidateSnapshot textMetadataCandidates)
{
    public Lr2FolderInfoCandidateSnapshot Candidates { get; } = candidates;

    public Lr2TextMetadataCandidateSnapshot TextMetadataCandidates { get; } = textMetadataCandidates;
}
