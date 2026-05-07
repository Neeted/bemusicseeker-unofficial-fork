using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableLoadResult
{
    public List<string> Pragmas { get; } = new List<string>();

    public List<BMSFile> LoadedFiles { get; } = new List<BMSFile>();

    public List<LR2SongDBExtended.bmson_song> LoadedBmsonSongs { get; } = new List<LR2SongDBExtended.bmson_song>();

    public List<BMSFile> UpdatedSongs { get; } = new List<BMSFile>();

    public List<string> DeletedSongPaths { get; } = new List<string>();

    public List<LR2SongDB.folder> UpdatedFolders { get; } = new List<LR2SongDB.folder>();

    public List<string> DeletedFolderPaths { get; } = new List<string>();

    public Dictionary<string, string> ChartDigestMap { get; } = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

    public bool DbWriteRequired { get; set; }

    public bool LeapYearDetected { get; set; }

    public int RelativePathFixedCount { get; set; }

    public int CrcRecalculatedCount { get; set; }

    public int CrcSkippedCount { get; set; }

    public long SongTableCount { get; set; }

    public long SongTableLoadMs { get; set; }

    public long SongCountMs { get; set; }

    public long SongMaterializeMs { get; set; }

    public string SongMaterializeMode { get; set; } = string.Empty;

    public long SongRawReadMs { get; set; }

    public long SongRawObjectMs { get; set; }

    public int SongRawRows { get; set; }

    public long SongNormalizeLoopMs { get; set; }

    public long FolderTableLoadMs { get; set; }

    public long FolderNormalizeLoopMs { get; set; }

    public long FixApplyMs { get; set; }

    public long ChartDigestMapLoadMs { get; set; }

    public long ChartDigestApplyMs { get; set; }

    public long BmsonTableLoadMs { get; set; }

    public long DbWriteMs { get; set; }

    public long CommitMs { get; set; }

    public long BmsFilesAssignMs { get; set; }
}
