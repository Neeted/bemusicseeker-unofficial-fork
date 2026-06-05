using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

internal sealed class Lr2FullGenerationRuntimeStatus
{
    internal Lr2FullGenerationStatusKind Kind { get; set; }

    internal string StatusText { get; set; } = string.Empty;

    internal string Detail { get; set; } = string.Empty;

    internal bool HasWarningStatus { get; set; }

    internal bool CanRetry { get; set; }

    internal bool CanCleanupStartupScanBlockers { get; set; }

    internal DateTime CheckedAt { get; set; }
}
