using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListOrder
{
    private static readonly ChartListOrderColumnDefinition[] columnDefinitions =
    {
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.Title),
            row => row?.Title ?? string.Empty,
            "virtual_title_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true,
            null,
            nameof(BMSFile.Title)),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.path),
            row => row?.Path ?? string.Empty,
            "virtual_path_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true,
            null,
            nameof(BMSFile.path)),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.Folder),
            row => row?.Folder ?? string.Empty,
            "virtual_folder_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true,
            null,
            nameof(BMSFile.Folder)),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.Artist),
            row => row?.Artist ?? string.Empty,
            "virtual_artist_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.genre),
            row => row?.Genre ?? string.Empty,
            "virtual_genre_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.mode),
            row => row?.Mode,
            "virtual_mode_order",
            typeof(int?).Name,
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.tag),
            row => row?.Tag ?? string.Empty,
            "virtual_tag_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.hash),
            row => row?.Hash ?? string.Empty,
            "virtual_hash_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.sha256),
            row => row?.Sha256 ?? string.Empty,
            "virtual_sha256_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.instl_dst),
            row => row?.InstallDestination ?? string.Empty,
            "virtual_instl_dst_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: false,
            null,
            nameof(BMSFile.instl_dst)),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.InstallDestinationTitle),
            row => row?.InstallDestinationTitle ?? string.Empty,
            "virtual_install_destination_title_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.InstallDestinationArtist),
            row => row?.InstallDestinationArtist ?? string.Empty,
            "virtual_install_destination_artist_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.RefTablesSymbols),
            row => row?.RefTablesSymbols ?? string.Empty,
            "virtual_ref_tables_symbols_order",
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: true)
    };

    private ChartListOrder(
        int[] indexes,
        string columnName,
        ListSortDirection direction,
        string sortProfile,
        string propertyTypeName,
        string stringSortKind)
    {
        Indexes = indexes ?? Array.Empty<int>();
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        SortProfile = sortProfile ?? string.Empty;
        PropertyTypeName = propertyTypeName ?? string.Empty;
        StringSortKind = stringSortKind ?? string.Empty;
    }

    internal int[] Indexes { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal string SortProfile { get; }

    internal string PropertyTypeName { get; }

    internal string StringSortKind { get; }

    internal int Count => Indexes.Length;

    internal static ChartListOrder CreateTitleAscending(IReadOnlyList<ChartListSourceRow> rows)
    {
        TryCreate(rows, nameof(LibraryChartRow.Title), ListSortDirection.Ascending, out ChartListOrder order);
        return order;
    }

    internal static bool TryCreate(
        IReadOnlyList<ChartListSourceRow> rows,
        string columnName,
        ListSortDirection direction,
        out ChartListOrder order)
    {
        order = null;
        if (!TryGetVirtualSortColumnDefinition(columnName, out ChartListOrderColumnDefinition definition))
        {
            return false;
        }

        IReadOnlyList<ChartListSourceRow> safeRows = rows ?? Array.Empty<ChartListSourceRow>();
        Func<int, string> titleKeySelector = index => safeRows[index]?.Title ?? string.Empty;
        IOrderedEnumerable<int> orderedIndexes;
        if (definition.KeyKind == ChartListOrderKeyKind.Comparable)
        {
            Func<int, IComparable> primaryKeySelector = index => definition.ComparableKeySelector(safeRows[index]);
            IComparer<IComparable> comparer = Comparer<IComparable>.Create(CompareComparable);
            orderedIndexes = direction == ListSortDirection.Descending
                ? Enumerable.Range(0, safeRows.Count)
                    .OrderByDescending(primaryKeySelector, comparer)
                    .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Range(0, safeRows.Count)
                    .OrderBy(primaryKeySelector, comparer)
                    .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            Func<int, string> primaryKeySelector = index => definition.StringKeySelector(safeRows[index]);
            orderedIndexes = direction == ListSortDirection.Descending
                ? Enumerable.Range(0, safeRows.Count)
                    .OrderByDescending(primaryKeySelector, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Range(0, safeRows.Count)
                    .OrderBy(primaryKeySelector, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase);
        }

        order = new ChartListOrder(
            orderedIndexes.ToArray(),
            definition.NormalizedColumnName,
            direction,
            definition.SortProfile,
            definition.PropertyTypeName,
            definition.StringSortKind);
        return true;
    }

    internal static IReadOnlyList<string> GetDefaultPrewarmColumnNames()
    {
        return columnDefinitions
            .Where(definition => definition.PrewarmByDefault)
            .Select(definition => definition.NormalizedColumnName)
            .ToArray();
    }

    internal static bool TryGetVirtualSortColumnMetadata(string columnName, out ChartListOrderColumnMetadata metadata)
    {
        if (TryGetVirtualSortColumnDefinition(columnName, out ChartListOrderColumnDefinition definition))
        {
            metadata = new ChartListOrderColumnMetadata(
                definition.NormalizedColumnName,
                definition.KeyKind,
                definition.PropertyTypeName,
                definition.SortProfile,
                definition.StringSortKind,
                definition.Dependency);
            return true;
        }

        metadata = default;
        return false;
    }

    internal static IReadOnlyList<ChartListOrderColumnMetadata> GetVirtualSortColumnMetadata()
    {
        return columnDefinitions
            .Select(definition => new ChartListOrderColumnMetadata(
                definition.NormalizedColumnName,
                definition.KeyKind,
                definition.PropertyTypeName,
                definition.SortProfile,
                definition.StringSortKind,
                definition.Dependency))
            .ToArray();
    }

    internal static bool TryNormalizeVirtualSortColumn(string columnName, out string normalizedColumnName)
    {
        if (TryGetVirtualSortColumnDefinition(columnName, out ChartListOrderColumnDefinition definition))
        {
            normalizedColumnName = definition.NormalizedColumnName;
            return true;
        }

        normalizedColumnName = string.Empty;
        return false;
    }

    private static bool TryGetVirtualSortColumnDefinition(string columnName, out ChartListOrderColumnDefinition definition)
    {
        string lookup = string.IsNullOrWhiteSpace(columnName) ? nameof(LibraryChartRow.Title) : columnName;
        foreach (ChartListOrderColumnDefinition candidate in columnDefinitions)
        {
            if (candidate.Matches(lookup))
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    private static int CompareComparable(IComparable left, IComparable right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }
        if (left == null)
        {
            return -1;
        }
        if (right == null)
        {
            return 1;
        }
        return left.CompareTo(right);
    }
}

internal enum ChartListOrderKeyKind
{
    String,
    Comparable
}

internal readonly struct ChartListOrderColumnMetadata
{
    internal ChartListOrderColumnMetadata(
        string normalizedColumnName,
        ChartListOrderKeyKind keyKind,
        string propertyTypeName,
        string sortProfile,
        string stringSortKind,
        MainViewDataDependency dependency)
    {
        NormalizedColumnName = normalizedColumnName ?? string.Empty;
        KeyKind = keyKind;
        PropertyTypeName = propertyTypeName ?? string.Empty;
        SortProfile = sortProfile ?? string.Empty;
        StringSortKind = stringSortKind ?? string.Empty;
        Dependency = dependency;
    }

    internal string NormalizedColumnName { get; }

    internal ChartListOrderKeyKind KeyKind { get; }

    internal string PropertyTypeName { get; }

    internal string SortProfile { get; }

    internal string StringSortKind { get; }

    internal MainViewDataDependency Dependency { get; }
}

internal readonly struct ChartListOrderColumnDefinition
{
    private readonly string[] aliases;

    private ChartListOrderColumnDefinition(
        string normalizedColumnName,
        ChartListOrderKeyKind keyKind,
        Func<ChartListSourceRow, string> stringKeySelector,
        Func<ChartListSourceRow, IComparable> comparableKeySelector,
        string sortProfile,
        string propertyTypeName,
        string stringSortKind,
        MainViewDataDependency dependency,
        bool prewarmByDefault,
        string[] aliases)
    {
        NormalizedColumnName = normalizedColumnName ?? string.Empty;
        KeyKind = keyKind;
        StringKeySelector = stringKeySelector ?? (_ => string.Empty);
        ComparableKeySelector = comparableKeySelector ?? (_ => null);
        SortProfile = sortProfile ?? string.Empty;
        PropertyTypeName = propertyTypeName ?? string.Empty;
        StringSortKind = stringSortKind ?? string.Empty;
        Dependency = dependency;
        PrewarmByDefault = prewarmByDefault;
        this.aliases = aliases ?? Array.Empty<string>();
    }

    internal string NormalizedColumnName { get; }

    internal ChartListOrderKeyKind KeyKind { get; }

    internal Func<ChartListSourceRow, string> StringKeySelector { get; }

    internal Func<ChartListSourceRow, IComparable> ComparableKeySelector { get; }

    internal string SortProfile { get; }

    internal string PropertyTypeName { get; }

    internal string StringSortKind { get; }

    internal MainViewDataDependency Dependency { get; }

    internal bool PrewarmByDefault { get; }

    internal static ChartListOrderColumnDefinition String(
        string normalizedColumnName,
        Func<ChartListSourceRow, string> keySelector,
        string sortProfile,
        MainViewDataDependency dependency,
        bool prewarmByDefault,
        string propertyTypeName = null,
        params string[] aliases)
    {
        return new ChartListOrderColumnDefinition(
            normalizedColumnName,
            ChartListOrderKeyKind.String,
            keySelector,
            null,
            sortProfile,
            propertyTypeName ?? nameof(String),
            "ordinal_ignore_case",
            dependency,
            prewarmByDefault,
            aliases);
    }

    internal static ChartListOrderColumnDefinition Comparable(
        string normalizedColumnName,
        Func<ChartListSourceRow, IComparable> keySelector,
        string sortProfile,
        string propertyTypeName,
        MainViewDataDependency dependency,
        bool prewarmByDefault,
        params string[] aliases)
    {
        return new ChartListOrderColumnDefinition(
            normalizedColumnName,
            ChartListOrderKeyKind.Comparable,
            null,
            keySelector,
            sortProfile,
            propertyTypeName,
            "typed",
            dependency,
            prewarmByDefault,
            aliases);
    }

    internal bool Matches(string columnName)
    {
        if (string.Equals(columnName, NormalizedColumnName, StringComparison.Ordinal))
        {
            return true;
        }
        return aliases.Any(alias => string.Equals(columnName, alias, StringComparison.Ordinal));
    }
}
