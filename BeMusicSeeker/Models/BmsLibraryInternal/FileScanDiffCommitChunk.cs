using System.Collections.Generic;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class FileScanDiffCommitChunk
{
    public List<string> DeletedBmsPaths { get; } = [];

    public List<BMSFile> AddedBmsFiles { get; } = [];

    public List<BmsDateOnlyUpdate> UpdatedBmsDates { get; } = [];

    public List<string> DeletedBmsonPaths { get; } = [];

    public List<LR2SongDBExtended.bmson_song> UpsertBmsonSongs { get; } = [];

    public List<BMSFileMaintenanceInfo> MaintenanceInfoRows { get; } = [];

    public List<LR2SongDBExtended.chart_info> ChartInfoRows { get; } = [];

    public List<LR2SongDBExtended.chart_info> AppliedChartInfoRows { get; } = [];

    public List<LR2SongDBExtended.chart_info_parse_failure> ParseFailureRows { get; } = [];

    public List<string> ParseFailureDeleteMd5s { get; } = [];

    public int MutationCount { get; private set; }

    public bool HasItems => MutationCount > 0
        || ChartInfoRows.Count > 0
        || UpdatedBmsDates.Count > 0
        || MaintenanceInfoRows.Count > 0
        || AppliedChartInfoRows.Count > 0
        || ParseFailureRows.Count > 0
        || ParseFailureDeleteMd5s.Count > 0;

    public void AddDeletedBmsPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        DeletedBmsPaths.Add(path);
        MutationCount++;
    }

    public void AddAddedBmsFile(BMSFile file)
    {
        if (file == null)
        {
            return;
        }
        AddedBmsFiles.Add(file);
        MutationCount++;
    }

    public void AddUpdatedBmsDate(string path, int date)
    {
        AddUpdatedBmsMetadata(path, date, null);
    }

    public void AddUpdatedBmsMetadata(string path, int date, int? textFlag)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        UpdatedBmsDates.Add(new BmsDateOnlyUpdate(path, date, textFlag));
        MutationCount++;
    }

    public void AddDeletedBmsonPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }
        DeletedBmsonPaths.Add(path);
        MutationCount++;
    }

    public void AddUpsertBmsonSong(LR2SongDBExtended.bmson_song song)
    {
        if (song == null)
        {
            return;
        }
        UpsertBmsonSongs.Add(song);
        MutationCount++;
    }

    public void AddMaintenanceInfoRow(BMSFileMaintenanceInfo row, bool countMutation = false)
    {
        if (row == null || string.IsNullOrWhiteSpace(row.path))
        {
            return;
        }
        MaintenanceInfoRows.Add(row);
        if (countMutation)
        {
            MutationCount++;
        }
    }

    public void AddChartInfoRow(LR2SongDBExtended.chart_info row, bool countMutation = true)
    {
        if (row == null)
        {
            return;
        }
        ChartInfoRows.Add(row);
        if (countMutation)
        {
            MutationCount++;
        }
    }

    public void AddAppliedChartInfoRow(LR2SongDBExtended.chart_info row)
    {
        if (row == null)
        {
            return;
        }
        AppliedChartInfoRows.Add(row);
    }

    public void AddParseFailureRow(LR2SongDBExtended.chart_info_parse_failure row, bool countMutation = true)
    {
        if (row == null)
        {
            return;
        }
        ParseFailureRows.Add(row);
        if (countMutation)
        {
            MutationCount++;
        }
    }

    public void AddParseFailureDeleteMd5(string md5, bool countMutation = true)
    {
        if (string.IsNullOrWhiteSpace(md5))
        {
            return;
        }
        ParseFailureDeleteMd5s.Add(md5);
        if (countMutation)
        {
            MutationCount++;
        }
    }

    public void AddFrom(FileScanDiffCommitChunk source)
    {
        if (source == null || !source.HasItems)
        {
            return;
        }
        DeletedBmsPaths.AddRange(source.DeletedBmsPaths);
        AddedBmsFiles.AddRange(source.AddedBmsFiles);
        UpdatedBmsDates.AddRange(source.UpdatedBmsDates);
        DeletedBmsonPaths.AddRange(source.DeletedBmsonPaths);
        UpsertBmsonSongs.AddRange(source.UpsertBmsonSongs);
        MaintenanceInfoRows.AddRange(source.MaintenanceInfoRows);
        ChartInfoRows.AddRange(source.ChartInfoRows);
        AppliedChartInfoRows.AddRange(source.AppliedChartInfoRows);
        ParseFailureRows.AddRange(source.ParseFailureRows);
        ParseFailureDeleteMd5s.AddRange(source.ParseFailureDeleteMd5s);
        MutationCount += source.MutationCount;
    }
}

internal sealed class BmsDateOnlyUpdate(string path, int date, int? textFlag = null)
{
    public string Path { get; } = path;

    public int Date { get; } = date;

    public int? TextFlag { get; } = textFlag;
}
