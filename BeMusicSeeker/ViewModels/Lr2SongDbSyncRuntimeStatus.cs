using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class Lr2SongDbSyncRuntimeStatus
{
    internal Lr2SongDbSyncStatusKind Kind { get; set; }

    internal string StatusText { get; set; } = string.Empty;

    internal string Detail { get; set; } = string.Empty;

    internal string ProgressText { get; set; } = string.Empty;

    internal double ProgressValue { get; set; }

    internal double ProgressMaximum { get; set; } = 1.0;

    internal bool HasProgress { get; set; }

    internal bool HasWarningStatus { get; set; }

    internal bool CanRetry { get; set; }

    internal bool CanCancel { get; set; }

    internal bool CanCleanupStartupScanBlockers { get; set; }

    internal DateTime CheckedAt { get; set; }
}
