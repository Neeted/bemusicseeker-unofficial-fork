using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class SongTableLoadResult
{
    public List<string> Pragmas { get; } = [];

    public List<BMSFile> LoadedFiles { get; } = [];

    public List<LR2SongDBExtended.bmson_song> LoadedBmsonSongs { get; } = [];

    public List<BMSFile> UpdatedSongs { get; } = [];

    public List<string> DeletedSongPaths { get; } = [];

    public List<LR2SongDB.folder> UpdatedFolders { get; } = [];

    public List<string> DeletedFolderPaths { get; } = [];

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
