using System;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class PlaylistSyncRuntimeStatus
{
    internal PlaylistSyncStatusKind Kind { get; set; }

    internal string StatusText { get; set; } = string.Empty;

    internal string Detail { get; set; } = string.Empty;

    internal int StatusSortOrder { get; set; }

    internal bool HasFailureStatus { get; set; }

    internal DateTime CheckedAt { get; set; }
}
