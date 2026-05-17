using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class PlaylistReferenceDisplay
{
    internal static readonly PlaylistReferenceDisplay Empty = new([]);

    internal PlaylistReferenceDisplay(IReadOnlyList<BMSTable> tables)
    {
        Tables = tables ?? [];
        Symbols = string.Join(" ", Tables.Select(table => table?.symbol));
        string names = string.Join(Environment.NewLine, Tables.Select(table => table?.name));
        Names = string.IsNullOrWhiteSpace(names) ? string.Empty : names;
    }

    internal IReadOnlyList<BMSTable> Tables { get; }

    internal string Symbols { get; }

    internal string Names { get; }
}
