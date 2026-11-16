using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;

namespace BeMusicSeeker.ViewModels;

internal enum RegularChartFolderFilterKind
{
    Directory,
    Artist
}

internal sealed class RegularNormalLibraryTreeFilter
{
    private RegularNormalLibraryTreeFilter(RegularChartFolderFilterKind kind, string term)
    {
        Kind = kind;
        Term = term ?? string.Empty;
    }

    internal RegularChartFolderFilterKind Kind { get; }

    internal string Term { get; }

    internal static RegularNormalLibraryTreeFilter Create(RegularChartFolderFilterKind kind, string filterKey)
    {
        if (string.IsNullOrWhiteSpace(filterKey))
        {
            return null;
        }

        if (kind == RegularChartFolderFilterKind.Directory)
        {
            string directoryTerm = EnsureTrailingDirectorySeparator(filterKey);
            return new RegularNormalLibraryTreeFilter(kind, directoryTerm);
        }
        return new RegularNormalLibraryTreeFilter(kind, filterKey);
    }

    internal bool Matches(ChartListSourceRow row)
    {
        return row != null && Matches(row.Path, row.Artist);
    }

    internal bool Matches(LibraryChartRow row)
    {
        return row != null && Matches(row.path, row.Artist);
    }

    internal bool Matches(ChartFile chart)
    {
        return chart != null && Matches(chart.Path, chart.Artist);
    }

    private bool Matches(string path, string artist)
    {
        return Kind == RegularChartFolderFilterKind.Directory
            ? ContainsIgnoreCase(path, Term)
            : ContainsIgnoreCase(artist, Term);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        string normalizedPath = (path ?? string.Empty).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return normalizedPath + Path.DirectorySeparatorChar;
    }

    private static bool ContainsIgnoreCase(string value, string term)
    {
        return !string.IsNullOrEmpty(value)
            && !string.IsNullOrEmpty(term)
            && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>一回の正規受付を一覧採用まで識別し、重複の選択対象を短命に保持します。</summary>
internal sealed class RegularChartNavigationRequest
{
    private readonly RegularChartListOwner owner;
    /// <summary>受付した重複選択の対象を保持します。重複以外の画面対象は既存の所有者が管理します。</summary>
    internal RegularChartNavigationRequest(RegularChartListOwner owner, DuplicateViewContext duplicateContext)
    { this.owner = owner; DuplicateContext = duplicateContext; }
    /// <summary>データ完成時に表示対象を解決するための重複選択です。重複以外の受付ではnullです。</summary>
    internal DuplicateViewContext DuplicateContext { get; }
    /// <summary>同じ正規受付が現在かを確認します。依存入口は適用済参照を明示して使います。</summary>
    internal bool IsCurrent => owner.IsCurrentNavigation(this);
}

/// <summary>準備元ライブラリと完成結果をUIへ通知します。表示先は、通知受信時の現在選択から決めます。</summary>
internal sealed class RegularDuplicateDataReadyEventArgs(BMSLibrary library, RegularDuplicateViewInput input) : EventArgs
{
    /// <summary>データを準備したライブラリです。接続先を交換した後は旧ライブラリの通知を採用しません。</summary>
    internal BMSLibrary Library { get; } = library;
    /// <summary>完成したグループ集合と既存の失効版です。データ未準備と解析済み0件を区別します。</summary>
    internal RegularDuplicateViewInput Input { get; } = input ?? throw new ArgumentNullException(nameof(input));

}

/// <summary>完成した重複結果の参照と失効版を、UIでの採用まで照合するための入力です。未準備時には作成しません。</summary>
internal sealed class RegularDuplicateViewInput
{
    private readonly BMSLibrary library;
    private readonly int invalidationVersion;
    private RegularDuplicateViewInput(BMSLibrary library, IReadOnlyList<DuplicateGroup> groups, int version)
    { this.library = library; Groups = groups; invalidationVersion = version; }
    /// <summary>完成した結果です。0件も準備済み結果として保持します。</summary>
    internal IReadOnlyList<DuplicateGroup> Groups { get; }
    /// <summary>捕捉後の再失効・結果交換を最終UIで検出し、再試行せず候補を失効させます。</summary>
    internal bool IsCurrent => ReferenceEquals(library?.DuplicateChartGroups, Groups)
        && library.DuplicateChartGroupsInvalidationVersion == invalidationVersion;
    /// <summary>完成した参照と既存失効版だけを捕捉します。未準備時には入力自体を作りません。</summary>
    internal static RegularDuplicateViewInput Capture(BMSLibrary library)
    {
        int version = library?.DuplicateChartGroupsInvalidationVersion ?? 0;
        List<DuplicateGroup> groups = library?.DuplicateChartGroups;
        return groups == null ? null : new(library, groups, version);
    }
}

/// <summary>整列条件と受付時の画面参照をUIへ渡し、受信側が開始した表示Taskを実行元へ返します。</summary>
internal sealed class RegularChartSortRefreshRequestedEventArgs(
    MainChartListSortRequestedEventArgs sortRequest, RegularChartNavigationRequest navigation) : EventArgs
{
    /// <summary>既存の列・方向・区分・並べ替え版を持つ入力です。履歴との共有型は変更しません。</summary>
    internal MainChartListSortRequestedEventArgs SortRequest { get; } = sortRequest;
    /// <summary>並べ替えを受理した時点の画面受付です。後発画面へ移動した場合は表示を更新しません。</summary>
    internal RegularChartNavigationRequest Navigation { get; } = navigation;
    /// <summary>UIでの行採用と必要な通知配送までを表すTaskです。並べ替え処理が完了まで回収します。</summary>
    internal Task Presentation { get; set; } = Task.CompletedTask;
}

internal sealed class RegularChartTreeNavigationPresentationRequestedEventArgs : EventArgs
{
    /// <summary>受付からUI実行・行採用まで引き継ぐ短命な要求です。</summary>
    internal RegularChartNavigationRequest Navigation { get; init; }

    /// <summary>受信VMが開始した同じ表示Taskです。配送元が実終端まで回収します。</summary>
    internal Task Presentation { get; set; } = Task.CompletedTask;

    internal RegularChartTreeNavigationPresentationRequestedEventArgs(
        bool keywordPresentationRefreshRequired,
        MainViewUpdateMode refreshMode)
    {
        KeywordPresentationRefreshRequired = keywordPresentationRefreshRequired;
        RefreshMode = refreshMode;
    }

    internal bool KeywordPresentationRefreshRequired { get; }

    internal MainViewUpdateMode RefreshMode { get; }
}

internal sealed class RegularChartMaintenanceNavigationPresentationRequestedEventArgs : EventArgs
{
    /// <summary>受付からUI実行・行採用まで引き継ぐ短命な要求です。</summary>
    internal RegularChartNavigationRequest Navigation { get; init; }

    /// <summary>受信VMが開始した同じ表示Taskです。配送元が実終端まで回収します。</summary>
    internal Task Presentation { get; set; } = Task.CompletedTask;

    internal RegularChartMaintenanceNavigationPresentationRequestedEventArgs(
        MainViewUpdateMode mode,
        object parameter,
        bool keywordPresentationRefreshRequired,
        bool refreshRequested)
    {
        Mode = mode;
        Parameter = parameter;
        KeywordPresentationRefreshRequired = keywordPresentationRefreshRequired;
        RefreshRequested = refreshRequested;
    }

    internal MainViewUpdateMode Mode { get; }

    internal object Parameter { get; }

    internal bool KeywordPresentationRefreshRequired { get; }

    internal bool RefreshRequested { get; }
}

internal sealed class RegularChartInstallNavigationPresentationRequestedEventArgs : EventArgs
{
    /// <summary>受付からUI実行・行採用まで引き継ぐ短命な要求です。</summary>
    internal RegularChartNavigationRequest Navigation { get; init; }

    /// <summary>受信VMが開始した同じ表示Taskです。配送元が実終端まで回収します。</summary>
    internal Task Presentation { get; set; } = Task.CompletedTask;

    internal RegularChartInstallNavigationPresentationRequestedEventArgs(
        MainViewUpdateMode mode,
        object parameter,
        bool keywordPresentationRefreshRequired)
    {
        Mode = mode;
        Parameter = parameter;
        KeywordPresentationRefreshRequired = keywordPresentationRefreshRequired;
    }

    internal MainViewUpdateMode Mode { get; }

    internal object Parameter { get; }

    internal bool KeywordPresentationRefreshRequired { get; }
}

/// <summary>
/// Captures the normalized inputs for the materialized regular chart-list pipeline.
/// </summary>
internal readonly struct RegularChartListRefreshRequest
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RegularChartListRefreshRequest"/> struct.
    /// </summary>
    /// <param name="mode">Mode currently used by the regular pipeline.</param>
    /// <param name="requestedMode">Mode originally requested by the caller.</param>
    /// <param name="parameter">Parameter associated with the current mode.</param>
    /// <param name="currentTreeMode">Current tree selection mode.</param>
    /// <param name="treeParameter">Parameter associated with the current tree selection.</param>
    /// <param name="includeBmsonRows">Whether normal library rows include bmson-backed charts.</param>
    /// <param name="virtualSubsetRequiredFailure">Whether a virtual subset request could not be fulfilled.</param>
    /// <param name="keywordFilter">Keyword filter text captured for the request.</param>
    /// <param name="modeFilter">Mode filter captured for the request.</param>
    /// <param name="sort">Sort specification captured for the request.</param>
    internal RegularChartListRefreshRequest(
        MainViewUpdateMode mode,
        MainViewUpdateMode requestedMode,
        object parameter,
        MainViewUpdateMode currentTreeMode,
        object treeParameter,
        bool includeBmsonRows,
        bool virtualSubsetRequiredFailure,
        string keywordFilter,
        ChartModeFilter modeFilter,
        ChartListSortSpecification sort)
    {
        Mode = mode;
        RequestedMode = requestedMode;
        Parameter = parameter;
        CurrentTreeMode = currentTreeMode;
        TreeParameter = treeParameter;
        IncludeBmsonRows = includeBmsonRows;
        VirtualSubsetRequiredFailure = virtualSubsetRequiredFailure;
        KeywordFilter = keywordFilter ?? string.Empty;
        ModeFilter = modeFilter;
        SortColumnName = string.IsNullOrWhiteSpace(sort.ColumnName)
            ? nameof(LibraryChartRow.Title)
            : sort.ColumnName;
        RequestedSortColumnName = sort.RequestedColumnName;
        SortDirection = sort.Direction;
        HasSortParameters = sort.HasValue;
        Sort = sort;
    }

    /// <summary>
    /// Gets the mode currently used by the regular pipeline.
    /// </summary>
    internal MainViewUpdateMode Mode { get; }

    /// <summary>
    /// Gets the mode originally requested by the caller.
    /// </summary>
    internal MainViewUpdateMode RequestedMode { get; }

    /// <summary>
    /// Gets the parameter associated with <see cref="Mode"/>.
    /// </summary>
    internal object Parameter { get; }

    /// <summary>
    /// Gets the current tree selection mode.
    /// </summary>
    internal MainViewUpdateMode CurrentTreeMode { get; }

    /// <summary>
    /// Gets the parameter associated with the current tree selection.
    /// </summary>
    internal object TreeParameter { get; }

    /// <summary>
    /// Gets a value indicating whether bmson-backed rows are included.
    /// </summary>
    internal bool IncludeBmsonRows { get; }

    /// <summary>
    /// Gets a value indicating whether a required virtual subset request failed.
    /// </summary>
    internal bool VirtualSubsetRequiredFailure { get; }

    /// <summary>
    /// Gets the keyword filter captured for the request.
    /// </summary>
    internal string KeywordFilter { get; }

    /// <summary>
    /// Gets the mode filter captured for the request.
    /// </summary>
    internal ChartModeFilter ModeFilter { get; }

    /// <summary>
    /// Gets a value indicating whether explicit sort parameters were supplied.
    /// </summary>
    internal bool HasSortParameters { get; }

    /// <summary>
    /// Gets the normalized sort column name captured for diagnostics and later coordinator extraction.
    /// </summary>
    internal string SortColumnName { get; }

    /// <summary>
    /// Gets the raw requested sort column name for diagnostics that must preserve the caller-facing value.
    /// </summary>
    internal string RequestedSortColumnName { get; }

    /// <summary>
    /// Gets the requested sort direction.
    /// </summary>
    internal ListSortDirection SortDirection { get; }

    /// <summary>
    /// Gets a cloned sort parameter snapshot for the regular pipeline.
    /// </summary>
    internal ChartListSortSpecification Sort { get; }
}

/// <summary>
/// Carries the mutable row stages produced by the materialized regular chart-list pipeline.
/// </summary>
internal sealed class RegularChartListStageState
{
    /// <summary>
    /// Gets or sets the rows after the folder/tree stage.
    /// </summary>
    internal IReadOnlyList<LibraryChartRow> FolderRows { get; set; } = Array.Empty<LibraryChartRow>();

    /// <summary>
    /// Gets or sets the rows after the keyword stage.
    /// </summary>
    internal IReadOnlyList<LibraryChartRow> KeywordRows { get; set; } = Array.Empty<LibraryChartRow>();

    /// <summary>
    /// Gets or sets the rows after the mode filter stage.
    /// </summary>
    internal IReadOnlyList<LibraryChartRow> ModeRows { get; set; } = Array.Empty<LibraryChartRow>();

    /// <summary>
    /// Gets the number of rows after the folder/tree stage.
    /// </summary>
    internal int FolderCount => FolderRows?.Count ?? 0;

    /// <summary>
    /// Gets the number of rows after the keyword stage.
    /// </summary>
    internal int KeywordCount => KeywordRows?.Count ?? 0;

    /// <summary>
    /// Gets the number of rows after the mode filter stage.
    /// </summary>
    internal int ModeCount => ModeRows?.Count ?? 0;

    /// <summary>
    /// Creates a stage-state snapshot from the legacy root-owned row caches.
    /// </summary>
    /// <param name="folderRows">Rows after the folder/tree stage.</param>
    /// <param name="keywordRows">Rows after the keyword stage.</param>
    /// <param name="modeRows">Rows after the mode filter stage.</param>
    /// <returns>A stage-state object that exposes materialized rows and counts.</returns>
    internal static RegularChartListStageState FromCaches(
        IEnumerable<LibraryChartRow> folderRows,
        IEnumerable<LibraryChartRow> keywordRows,
        IEnumerable<LibraryChartRow> modeRows)
    {
        return new RegularChartListStageState
        {
            FolderRows = Materialize(folderRows),
            KeywordRows = Materialize(keywordRows),
            ModeRows = Materialize(modeRows),
        };
    }

    /// <summary>
    /// Converts a row stage to a stable list while preserving existing list instances when possible.
    /// </summary>
    /// <param name="rows">Rows produced by a regular chart-list stage.</param>
    /// <returns>A stable read-only list for downstream stages.</returns>
    internal static List<LibraryChartRow> Materialize(IEnumerable<LibraryChartRow> rows)
    {
        if (rows == null)
        {
            return new List<LibraryChartRow>();
        }
        if (rows is List<LibraryChartRow> list)
        {
            return list;
        }
        return rows.ToList();
    }
}
