using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistReferenceDisplay
{
    internal static readonly PlaylistReferenceDisplay Empty = new([]);

    internal PlaylistReferenceDisplay(IReadOnlyList<PlaylistReferenceTableDisplaySnapshot> snapshots)
    {
        IReadOnlyList<PlaylistReferenceTableDisplaySnapshot> sourceSnapshots = snapshots ?? [];
        Symbols = string.Join(" ", sourceSnapshots.Where(snapshot => snapshot != null).Select(snapshot => snapshot.Symbol));
        string names = string.Join(Environment.NewLine, sourceSnapshots.Where(snapshot => snapshot != null).Select(snapshot => snapshot.Name));
        Names = string.IsNullOrWhiteSpace(names) ? string.Empty : names;
    }

    internal string Symbols { get; }

    internal string Names { get; }
}
