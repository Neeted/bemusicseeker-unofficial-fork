using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Identifies cached normal-library sort results, including the data generations that can affect ordering.
/// </summary>
internal readonly struct NormalLibrarySortCacheKey : IEquatable<NormalLibrarySortCacheKey>
{
    internal NormalLibrarySortCacheKey(long sourceGeneration, long sortKeyGeneration, string columnName, ListSortDirection direction, int rowCount)
        : this(sourceGeneration, sortKeyGeneration, 0, 0, 0, 0, 0, 0, columnName, direction, rowCount)
    {
    }

    internal NormalLibrarySortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        long scoreGeneration,
        long chartInfoGeneration,
        long maintenanceGeneration,
        string columnName,
        ListSortDirection direction,
        int rowCount)
        : this(sourceGeneration, sortKeyGeneration, scoreGeneration, chartInfoGeneration, maintenanceGeneration, 0, 0, 0, columnName, direction, rowCount)
    {
    }

    internal NormalLibrarySortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        long scoreGeneration,
        long chartInfoGeneration,
        long maintenanceGeneration,
        long warningGeneration,
        long installDestinationGeneration,
        long referenceTablesGeneration,
        string columnName,
        ListSortDirection direction,
        int rowCount)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        ScoreGeneration = scoreGeneration;
        ChartInfoGeneration = chartInfoGeneration;
        MaintenanceGeneration = maintenanceGeneration;
        WarningGeneration = warningGeneration;
        InstallDestinationGeneration = installDestinationGeneration;
        ReferenceTablesGeneration = referenceTablesGeneration;
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        RowCount = rowCount;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal long ScoreGeneration { get; }

    internal long ChartInfoGeneration { get; }

    internal long MaintenanceGeneration { get; }

    internal long WarningGeneration { get; }

    internal long InstallDestinationGeneration { get; }

    internal long ReferenceTablesGeneration { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int RowCount { get; }

    public bool Equals(NormalLibrarySortCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && ScoreGeneration == other.ScoreGeneration
            && ChartInfoGeneration == other.ChartInfoGeneration
            && MaintenanceGeneration == other.MaintenanceGeneration
            && WarningGeneration == other.WarningGeneration
            && InstallDestinationGeneration == other.InstallDestinationGeneration
            && ReferenceTablesGeneration == other.ReferenceTablesGeneration
            && string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
            && Direction == other.Direction
            && RowCount == other.RowCount;
    }

    public override bool Equals(object obj)
    {
        return obj is NormalLibrarySortCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = SourceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ SortKeyGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ScoreGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ChartInfoGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ MaintenanceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ WarningGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ InstallDestinationGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ReferenceTablesGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(ColumnName ?? string.Empty);
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ RowCount;
            return hashCode;
        }
    }
}

/// <summary>
/// Identifies cached virtual-subset sort results, including source and tree-filter identity.
/// </summary>
internal readonly struct VirtualChartSubsetSortCacheKey : IEquatable<VirtualChartSubsetSortCacheKey>
{
    internal VirtualChartSubsetSortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        long scoreGeneration,
        long chartInfoGeneration,
        long maintenanceGeneration,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        string columnName,
        ListSortDirection direction,
        int rowCount)
        : this(
            sourceGeneration,
            sortKeyGeneration,
            scoreGeneration,
            chartInfoGeneration,
            maintenanceGeneration,
            0,
            0,
            0,
            treeMode,
            subsetName,
            sourceRowsSignature,
            columnName,
            direction,
            rowCount)
    {
    }

    internal VirtualChartSubsetSortCacheKey(
        long sourceGeneration,
        long sortKeyGeneration,
        long scoreGeneration,
        long chartInfoGeneration,
        long maintenanceGeneration,
        long warningGeneration,
        long installDestinationGeneration,
        long referenceTablesGeneration,
        int treeMode,
        string subsetName,
        long sourceRowsSignature,
        string columnName,
        ListSortDirection direction,
        int rowCount)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        ScoreGeneration = scoreGeneration;
        ChartInfoGeneration = chartInfoGeneration;
        MaintenanceGeneration = maintenanceGeneration;
        WarningGeneration = warningGeneration;
        InstallDestinationGeneration = installDestinationGeneration;
        ReferenceTablesGeneration = referenceTablesGeneration;
        TreeMode = treeMode;
        SubsetName = subsetName ?? string.Empty;
        SourceRowsSignature = sourceRowsSignature;
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        RowCount = rowCount;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal long ScoreGeneration { get; }

    internal long ChartInfoGeneration { get; }

    internal long MaintenanceGeneration { get; }

    internal long WarningGeneration { get; }

    internal long InstallDestinationGeneration { get; }

    internal long ReferenceTablesGeneration { get; }

    internal int TreeMode { get; }

    internal string SubsetName { get; }

    internal long SourceRowsSignature { get; }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int RowCount { get; }

    public bool Equals(VirtualChartSubsetSortCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && ScoreGeneration == other.ScoreGeneration
            && ChartInfoGeneration == other.ChartInfoGeneration
            && MaintenanceGeneration == other.MaintenanceGeneration
            && WarningGeneration == other.WarningGeneration
            && InstallDestinationGeneration == other.InstallDestinationGeneration
            && ReferenceTablesGeneration == other.ReferenceTablesGeneration
            && TreeMode == other.TreeMode
            && string.Equals(SubsetName, other.SubsetName, StringComparison.Ordinal)
            && SourceRowsSignature == other.SourceRowsSignature
            && string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
            && Direction == other.Direction
            && RowCount == other.RowCount;
    }

    public override bool Equals(object obj)
    {
        return obj is VirtualChartSubsetSortCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = SourceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ SortKeyGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ScoreGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ChartInfoGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ MaintenanceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ WarningGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ InstallDestinationGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ ReferenceTablesGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ TreeMode;
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(SubsetName ?? string.Empty);
            hashCode = (hashCode * 397) ^ SourceRowsSignature.GetHashCode();
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(ColumnName ?? string.Empty);
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ RowCount;
            return hashCode;
        }
    }
}

/// <summary>
/// Identifies cached main-view summary calculations for a filtered row set.
/// </summary>
internal readonly struct MainViewSummaryCacheKey : IEquatable<MainViewSummaryCacheKey>
{
    internal MainViewSummaryCacheKey(long sourceGeneration, long sortKeyGeneration, int rowCount, bool includeBmsonRows, string filterIdentity)
    {
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        RowCount = rowCount;
        IncludeBmsonRows = includeBmsonRows;
        FilterIdentity = filterIdentity ?? string.Empty;
    }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal int RowCount { get; }

    internal bool IncludeBmsonRows { get; }

    internal string FilterIdentity { get; }

    public bool Equals(MainViewSummaryCacheKey other)
    {
        return SourceGeneration == other.SourceGeneration
            && SortKeyGeneration == other.SortKeyGeneration
            && RowCount == other.RowCount
            && IncludeBmsonRows == other.IncludeBmsonRows
            && string.Equals(FilterIdentity, other.FilterIdentity, StringComparison.Ordinal);
    }

    public override bool Equals(object obj)
    {
        return obj is MainViewSummaryCacheKey other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = SourceGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ SortKeyGeneration.GetHashCode();
            hashCode = (hashCode * 397) ^ RowCount;
            hashCode = (hashCode * 397) ^ IncludeBmsonRows.GetHashCode();
            hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(FilterIdentity ?? string.Empty);
            return hashCode;
        }
    }
}

/// <summary>
/// Describes a sort that should be prewarmed for the virtual normal library view.
/// </summary>
internal readonly struct VirtualNormalLibrarySortDescriptor : IEquatable<VirtualNormalLibrarySortDescriptor>
{
    internal VirtualNormalLibrarySortDescriptor(string columnName, ListSortDirection direction, int prewarmPriority = 0)
    {
        ColumnName = columnName ?? string.Empty;
        Direction = direction;
        PrewarmPriority = prewarmPriority;
    }

    internal string ColumnName { get; }

    internal ListSortDirection Direction { get; }

    internal int PrewarmPriority { get; }

    public bool Equals(VirtualNormalLibrarySortDescriptor other)
    {
        return string.Equals(ColumnName, other.ColumnName, StringComparison.Ordinal)
            && Direction == other.Direction
            && PrewarmPriority == other.PrewarmPriority;
    }

    public override bool Equals(object obj)
    {
        return obj is VirtualNormalLibrarySortDescriptor other && Equals(other);
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = ColumnName != null ? StringComparer.Ordinal.GetHashCode(ColumnName) : 0;
            hashCode = (hashCode * 397) ^ (int)Direction;
            hashCode = (hashCode * 397) ^ PrewarmPriority;
            return hashCode;
        }
    }
}

/// <summary>
/// Captures library-owned versions that participate in regular chart sort cache identity.
/// </summary>
internal readonly struct RegularChartListExternalVersions
{
    internal RegularChartListExternalVersions(
        long score,
        long chartInfo,
        long maintenanceHydration)
    {
        Score = score;
        ChartInfo = chartInfo;
        MaintenanceHydration = maintenanceHydration;
    }

    internal long Score { get; }

    internal long ChartInfo { get; }

    internal long MaintenanceHydration { get; }
}

/// <summary>
/// Describes one source-row cache lookup and the generations against which a miss was built.
/// </summary>
internal readonly struct RegularVirtualSourceRowsLookup
{
    internal RegularVirtualSourceRowsLookup(
        bool includeBmsonRows,
        long sourceGeneration,
        long sortKeyGeneration,
        IReadOnlyList<ChartListSourceRow> rows,
        bool cacheHit)
    {
        IncludeBmsonRows = includeBmsonRows;
        SourceGeneration = sourceGeneration;
        SortKeyGeneration = sortKeyGeneration;
        Rows = rows;
        CacheHit = cacheHit;
    }

    internal bool IncludeBmsonRows { get; }

    internal long SourceGeneration { get; }

    internal long SortKeyGeneration { get; }

    internal IReadOnlyList<ChartListSourceRow> Rows { get; }

    internal bool CacheHit { get; }
}
