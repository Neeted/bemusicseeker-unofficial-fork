using System;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistSyncProgressSnapshot
{
    public bool IsActive { get; set; }

    public int TotalTableCount { get; set; }

    public int CompletedTableCount { get; set; }

    public string CurrentTableName { get; set; }

    public Uri CurrentUri { get; set; }

    public string LabelFormat { get; set; }

    public string SingleLabel { get; set; }
}
