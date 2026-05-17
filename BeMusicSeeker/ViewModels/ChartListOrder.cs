using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.LR2;

namespace BeMusicSeeker.ViewModels;

internal sealed class ChartListOrder
{
    private static readonly ChartListOrderColumnDefinition[] columnDefinitions =
    [
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
            nameof(LibraryChartRow.WarningDigestText),
            row => row?.WarningDigestText ?? string.Empty,
            "virtual_warning_digest_order",
            MainViewDataDependency.Warning,
            prewarmByDefault: false),
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
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.WAVHealth),
            row => row?.WAVHealth,
            "virtual_wav_health_order",
            typeof(int?).Name,
            MainViewDataDependency.Maintenance,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.BGAHealth),
            row => row?.BGAHealth,
            "virtual_bga_health_order",
            typeof(int?).Name,
            MainViewDataDependency.Maintenance,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.MovieHealth),
            row => row?.MovieHealth,
            "virtual_movie_health_order",
            typeof(int?).Name,
            MainViewDataDependency.Maintenance,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.encoding),
            row => row?.EncodingName ?? string.Empty,
            "virtual_encoding_order",
            MainViewDataDependency.Maintenance,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.level),
            row => row?.LevelValue ?? TryParseLevel(row?.Level),
            "virtual_level_order",
            typeof(double?).Name,
            MainViewDataDependency.IdentitySortKey,
            prewarmByDefault: false,
            nameof(LibraryChartRow.Level)),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.clear),
            row => row?.Clear,
            "virtual_clear_order",
            typeof(ClearType).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.rateDouble),
            row => row?.RateDouble,
            "virtual_rate_order",
            typeof(double?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: true,
            nameof(LibraryChartRow.rank)),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.score),
            row => row?.Score,
            "virtual_score_order",
            typeof(int?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.maxcombo),
            row => row?.MaxCombo,
            "virtual_maxcombo_order",
            typeof(int?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.minbp),
            row => row?.MinBp,
            "virtual_minbp_order",
            typeof(int?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.String(
            nameof(LibraryChartRow.rankingString),
            row => row?.RankingString ?? string.Empty,
            "virtual_ranking_string_order",
            MainViewDataDependency.Score,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.rankingLastupdate),
            row => row?.RankingLastUpdate,
            "virtual_ranking_lastupdate_order",
            typeof(DateTime?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.stddevVal),
            row => row?.StdDevVal,
            "virtual_stddev_order",
            typeof(double?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.scoreDifficulty),
            row => row?.ScoreDifficulty,
            "virtual_score_difficulty_order",
            typeof(double?).Name,
            MainViewDataDependency.Score,
            prewarmByDefault: false),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartLevelSortKey),
            row => row?.ChartLevelSortKey,
            "virtual_chart_level_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartDifficultySortKey),
            row => row?.ChartDifficultySortKey,
            "virtual_chart_difficulty_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartMainBpmSortKey),
            row => row?.ChartMainBpmSortKey,
            "virtual_chart_main_bpm_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartMaxBpmSortKey),
            row => row?.ChartMaxBpmSortKey,
            "virtual_chart_max_bpm_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartMinBpmSortKey),
            row => row?.ChartMinBpmSortKey,
            "virtual_chart_min_bpm_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartDurationSortKey),
            row => row?.ChartDurationSortKey,
            "virtual_chart_duration_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartJudgeSortKey),
            row => row?.ChartJudgeSortKey,
            "virtual_chart_judge_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartFeatureSortKey),
            row => row?.ChartFeatureSortKey,
            "virtual_chart_feature_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartNotes),
            row => row?.ChartNotes,
            "virtual_chart_notes_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartLongNotes),
            row => row?.ChartLongNotes,
            "virtual_chart_long_notes_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartScratchNotes),
            row => row?.ChartScratchNotes,
            "virtual_chart_scratch_notes_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartTotalSortKey),
            row => row?.ChartTotalSortKey,
            "virtual_chart_total_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartTotalPerNoteSortKey),
            row => row?.ChartTotalPerNoteSortKey,
            "virtual_chart_total_per_note_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartDensitySortKey),
            row => row?.ChartDensitySortKey,
            "virtual_chart_density_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartPeakDensitySortKey),
            row => row?.ChartPeakDensitySortKey,
            "virtual_chart_peak_density_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartEndDensitySortKey),
            row => row?.ChartEndDensitySortKey,
            "virtual_chart_end_density_order",
            typeof(double?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true),
        ChartListOrderColumnDefinition.Comparable(
            nameof(LibraryChartRow.ChartSoflanCount),
            row => row?.ChartSoflanCount,
            "virtual_chart_soflan_count_order",
            typeof(int?).Name,
            MainViewDataDependency.ChartInfo,
            prewarmByDefault: true)
    ];

    private ChartListOrder(
        int[] indexes,
        string columnName,
        ListSortDirection direction,
        string sortProfile,
        string propertyTypeName,
        string stringSortKind)
    {
        Indexes = indexes ?? [];
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

    internal ChartListOrder WithIndexes(int[] indexes)
    {
        return new ChartListOrder(
            indexes ?? [],
            ColumnName,
            Direction,
            SortProfile,
            PropertyTypeName,
            StringSortKind);
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

        IReadOnlyList<ChartListSourceRow> safeRows = rows ?? [];
        string titleKeySelector(int index) => safeRows[index]?.Title ?? string.Empty;
        IOrderedEnumerable<int> orderedIndexes;
        if (definition.KeyKind == ChartListOrderKeyKind.Comparable)
        {
            IComparable primaryKeySelector(int index) => definition.ComparableKeySelector(safeRows[index]);
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
            string primaryKeySelector(int index) => definition.StringKeySelector(safeRows[index]);
            orderedIndexes = direction == ListSortDirection.Descending
                ? Enumerable.Range(0, safeRows.Count)
                    .OrderByDescending(primaryKeySelector, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Range(0, safeRows.Count)
                    .OrderBy(primaryKeySelector, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(titleKeySelector, StringComparer.OrdinalIgnoreCase);
        }

        order = new ChartListOrder(
            [.. orderedIndexes],
            definition.NormalizedColumnName,
            direction,
            definition.SortProfile,
            definition.PropertyTypeName,
            definition.StringSortKind);
        return true;
    }

    internal static IReadOnlyList<string> GetDefaultPrewarmColumnNames()
    {
        return [.. columnDefinitions
            .Where(definition => definition.PrewarmByDefault)
            .Select(definition => definition.NormalizedColumnName)];
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
                definition.Dependency,
                definition.PrewarmPriority);
            return true;
        }

        metadata = default;
        return false;
    }

    internal static IReadOnlyList<ChartListOrderColumnMetadata> GetVirtualSortColumnMetadata()
    {
        return [.. columnDefinitions
            .Select(definition => new ChartListOrderColumnMetadata(
                definition.NormalizedColumnName,
                definition.KeyKind,
                definition.PropertyTypeName,
                definition.SortProfile,
                definition.StringSortKind,
                definition.Dependency,
                definition.PrewarmPriority))];
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

    private static double? TryParseLevel(string levelText)
    {
        if (string.IsNullOrWhiteSpace(levelText))
        {
            return null;
        }
        if (double.TryParse(levelText, NumberStyles.Float, CultureInfo.CurrentCulture, out double currentCultureValue))
        {
            return currentCultureValue;
        }
        if (double.TryParse(levelText, NumberStyles.Float, CultureInfo.InvariantCulture, out double invariantCultureValue))
        {
            return invariantCultureValue;
        }
        return null;
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
        MainViewDataDependency dependency,
        int prewarmPriority)
    {
        NormalizedColumnName = normalizedColumnName ?? string.Empty;
        KeyKind = keyKind;
        PropertyTypeName = propertyTypeName ?? string.Empty;
        SortProfile = sortProfile ?? string.Empty;
        StringSortKind = stringSortKind ?? string.Empty;
        Dependency = dependency;
        PrewarmPriority = prewarmPriority;
    }

    internal string NormalizedColumnName { get; }

    internal ChartListOrderKeyKind KeyKind { get; }

    internal string PropertyTypeName { get; }

    internal string SortProfile { get; }

    internal string StringSortKind { get; }

    internal MainViewDataDependency Dependency { get; }

    internal int PrewarmPriority { get; }
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
        PrewarmPriority = ResolvePrewarmPriority(NormalizedColumnName);
        this.aliases = aliases ?? [];
    }

    internal string NormalizedColumnName { get; }

    internal ChartListOrderKeyKind KeyKind { get; }

    internal Func<ChartListSourceRow, string> StringKeySelector { get; }

    internal Func<ChartListSourceRow, IComparable> ComparableKeySelector { get; }

    internal string SortProfile { get; }

    internal string PropertyTypeName { get; }

    internal string StringSortKind { get; }

    internal MainViewDataDependency Dependency { get; }

    internal int PrewarmPriority { get; }

    internal bool PrewarmByDefault => PrewarmPriority > 0;

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

    private static int ResolvePrewarmPriority(string normalizedColumnName)
    {
        return normalizedColumnName switch
        {
            nameof(LibraryChartRow.Title) or nameof(LibraryChartRow.Folder) or nameof(LibraryChartRow.path) or nameof(LibraryChartRow.Artist) => 1,
            nameof(LibraryChartRow.clear) or nameof(LibraryChartRow.rateDouble) or nameof(LibraryChartRow.minbp) or nameof(LibraryChartRow.ChartJudgeSortKey) or nameof(LibraryChartRow.ChartNotes) or nameof(LibraryChartRow.ChartLongNotes) or nameof(LibraryChartRow.ChartScratchNotes) or nameof(LibraryChartRow.ChartMainBpmSortKey) or nameof(LibraryChartRow.ChartMinBpmSortKey) or nameof(LibraryChartRow.ChartMaxBpmSortKey) or nameof(LibraryChartRow.ChartSoflanCount) or nameof(LibraryChartRow.ChartTotalSortKey) or nameof(LibraryChartRow.ChartTotalPerNoteSortKey) or nameof(LibraryChartRow.ChartDurationSortKey) or nameof(LibraryChartRow.ChartDensitySortKey) or nameof(LibraryChartRow.ChartPeakDensitySortKey) or nameof(LibraryChartRow.ChartEndDensitySortKey) => 2,
            nameof(LibraryChartRow.WAVHealth) or nameof(LibraryChartRow.BGAHealth) or nameof(LibraryChartRow.MovieHealth) or nameof(LibraryChartRow.encoding) or nameof(LibraryChartRow.WarningDigestText) or nameof(LibraryChartRow.level) => 0,
            _ => 3,
        };
    }
}
