using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// 一覧の構築（UIスレッド外）で作り、UIスレッドで採用する入力です。グループごとの初期状態と、フォルダごとのTITLE・ARTISTを不変の値で持ちます。
/// </summary>
internal sealed class DuplicateMergeCandidateListPreparation
{
    internal DuplicateMergeCandidateListPreparation(
        object library,
        IReadOnlyList<DuplicateMergeCandidateGroupInitial> groups,
        IReadOnlyDictionary<string, (string Title, string Artist)> pairs)
    {
        Library = library;
        Groups = groups;
        Pairs = pairs;
    }

    /// <summary>構築時のライブラリです。採用時に、現在のライブラリと一致する場合だけ採用します。</summary>
    internal object Library { get; }

    /// <summary>重複検索結果の順に並べたグループの初期状態です。</summary>
    internal IReadOnlyList<DuplicateMergeCandidateGroupInitial> Groups { get; }

    /// <summary>フォルダ（大文字小文字を無視）ごとのTITLE・ARTISTです。</summary>
    internal IReadOnlyDictionary<string, (string Title, string Artist)> Pairs { get; }
}

/// <summary>
/// 統合候補一覧の状態と判断を持つ管理主体です。グループごとのSELECT・KEEP・確認結果（状態表）、採用済みの行集合、並べ替え、検索語、
/// 優先統合先、表示中かどうか、件数を所有します。状態表と行集合の読み書きはUIスレッドだけで行い、構築（<see cref="Prepare"/>）だけがUIスレッド外で動きます。
/// </summary>
/// <remarks>
/// 状態は <see cref="DuplicateGroup"/> のインスタンスに対応付けます。重複結果の再検索で同じインスタンスが維持されたグループは状態を保ち、
/// 新しいインスタンスは初期値になり、消えたグループの状態は採用時に破棄します。永続化しません。
/// 並べ替え・検索・SELECT・KEEPの操作は、構築済みの値だけを使い、TITLE・ARTISTの解決を呼びません。
/// </remarks>
public sealed class DuplicateMergeCandidateListOwner : ViewModel
{
    private readonly Func<string, bool> directoryExists;
    private readonly Action<string> openDirectory;
    private Dictionary<DuplicateGroup, DuplicateMergeCandidateGroupState> states = new(ReferenceEqualityComparer.Instance);
    private DuplicateMergeCandidateListPreparation preparation;
    private object library;
    private volatile bool treeSelected;
    private bool isViewActive;
    private DuplicateMergeCandidateRowSet rows = DuplicateMergeCandidateRowSet.Empty;
    private List<DuplicateMergeCandidateGroupState> visibleStates = [];
    private GridKeywordSearchQuery searchQuery = GridKeywordSearchQuery.Parse(null);
    private string searchText = string.Empty;
    private string sortColumnName;
    private ListSortDirection sortDirection = ListSortDirection.Ascending;
    private int visibleGroupCount;
    private int selectedGroupCount;
    private IReadOnlyList<string> preferredDestinations = [];

    /// <param name="directoryExists">フォルダの存在を確認する窓口です。エクスプローラーで開く対象の判定に使います。</param>
    /// <param name="openDirectory">フォルダをエクスプローラーで開く窓口です。</param>
    internal DuplicateMergeCandidateListOwner(Func<string, bool> directoryExists, Action<string> openDirectory)
    {
        this.directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        this.openDirectory = openDirectory ?? throw new ArgumentNullException(nameof(openDirectory));
    }

    /// <summary>表示内容（SELECT・STATUS・KEEP）が変わり、表が再描画する必要があるときに通知します。行集合は変わりません。</summary>
    internal event EventHandler DisplayInvalidated;

    /// <summary>列の幅・順序です。起動中だけ保持し、設定へ保存しません。</summary>
    public DuplicateMergeCandidateColumnSettings ColumnsSettings { get; } = new();

    /// <summary>表に渡す採用済みの行集合です。再構築・並べ替え・検索で別のインスタンスへ置き換わります。</summary>
    public IList Rows => rows;

    /// <summary>採用済みの行集合を型付きで返します。</summary>
    internal DuplicateMergeCandidateRowSet RowSet => rows;

    /// <summary>統合候補一覧を表示中かです。選択した画面が一覧で、その画面の一覧を採用した後にだけ真になります。</summary>
    public bool IsViewActive
    {
        get => isViewActive;
        private set
        {
            if (isViewActive == value) { return; }
            isViewActive = value;
            RaisePropertyChanged(nameof(IsViewActive));
        }
    }

    /// <summary>表示中（検索で残った）のグループ数です。</summary>
    public int VisibleGroupCount
    {
        get => visibleGroupCount;
        private set
        {
            if (visibleGroupCount == value) { return; }
            visibleGroupCount = value;
            RaisePropertyChanged(nameof(VisibleGroupCount));
        }
    }

    /// <summary>表示中でSELECTがオンのグループ数です。</summary>
    public int SelectedGroupCount
    {
        get => selectedGroupCount;
        private set
        {
            if (selectedGroupCount == value) { return; }
            selectedGroupCount = value;
            RaisePropertyChanged(nameof(SelectedGroupCount));
        }
    }

    /// <summary>専用の検索欄の入力です。既存のキーワード検索の構文を使い、一致した行を持つグループの全行を残します。</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(searchText, next, StringComparison.Ordinal)) { return; }
            searchText = next;
            searchQuery = GridKeywordSearchQuery.Parse(next);
            RaisePropertyChanged(nameof(SearchText));
            RebuildView();
        }
    }

    /// <summary>並べ替えの列の識別子です。並べ替えていなければnullです。</summary>
    public string SortColumnName => sortColumnName;

    /// <summary>並べ替えの方向です。</summary>
    public ListSortDirection SortDirection => sortDirection;

    /// <summary>優先統合先として登録したディレクトリです。アプリの起動中だけ保持し、ライブラリの交換後も残ります。</summary>
    internal IReadOnlyList<string> PreferredDestinations => preferredDestinations;

    /// <summary>
    /// 接続中のライブラリを知らせます。別のライブラリへ交換した場合と解除した場合は、すべてのグループの状態と採用済みの行を破棄して非表示にします。
    /// 優先統合先・並べ替え・検索語は保ちます。
    /// </summary>
    internal void AttachLibrary(object value)
    {
        if (ReferenceEquals(library, value)) { return; }
        library = value;
        states = new(ReferenceEqualityComparer.Instance);
        preparation = null;
        visibleStates = [];
        rows = DuplicateMergeCandidateRowSet.Empty;
        treeSelected = false;
        IsViewActive = false;
        RaisePropertyChanged(nameof(Rows));
        RefreshCounts();
    }

    /// <summary>
    /// 重複ツリーで統合候補一覧が選択されているかを知らせます。他の画面へ移るとただちに非表示にし、一覧を選び直したときは、
    /// その画面の新しい一覧を採用するまで表示しません。
    /// </summary>
    internal void SetTreeSelected(bool selected)
    {
        if (treeSelected == selected) { return; }
        treeSelected = selected;
        IsViewActive = false;
    }

    /// <summary>
    /// 一覧を構築します。UIスレッド外で呼び、状態表には触れません。ライブラリへの問合せは、登録範囲の捕捉と、全フォルダのTITLE・ARTISTの一括解決の一回ずつです。
    /// </summary>
    /// <param name="libraryToken">構築時のライブラリ。採用時に現在のライブラリと照合します。</param>
    /// <param name="groups">重複検索結果です。順序を保ちます。</param>
    /// <param name="source">ライブラリの入力の窓口です。</param>
    /// <param name="cancellationToken">取消です。</param>
    internal static DuplicateMergeCandidateListPreparation Prepare(
        object libraryToken, IEnumerable<DuplicateGroup> groups, IDuplicateMergeCandidateListSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(source);
        DuplicateGroup[] input = [.. groups.Where(group => group != null)];
        Func<string, bool> isRegistered = source.CaptureRegisteredBmsRootPredicate();
        var initials = new List<DuplicateMergeCandidateGroupInitial>(input.Length);
        var folders = new List<string>();
        foreach (DuplicateGroup group in input)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DuplicateMergeCandidateGroupInitial initial = CreateInitial(group, isRegistered);
            initials.Add(initial);
            folders.AddRange(initial.Folders);
        }
        IReadOnlyDictionary<string, (string Title, string Artist)> pairs = source.ResolveDestinationDisplayPairs(folders, cancellationToken);
        return new DuplicateMergeCandidateListPreparation(libraryToken, initials, pairs);
    }

    private static DuplicateMergeCandidateGroupInitial CreateInitial(DuplicateGroup group, Func<string, bool> isRegistered)
    {
        string[] folders = [.. (group.Folders ?? [])
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
        string[] registered = [.. folders.Where(isRegistered)];
        if (folders.Length == 0)
        {
            return new DuplicateMergeCandidateGroupInitial(group, folders, null, true, false, []);
        }
        if (registered.Length >= 1)
        {
            return new DuplicateMergeCandidateGroupInitial(group, folders, registered[0], true, registered.Length >= 2, registered);
        }
        // グループの譜面（兄弟譜面を含む）のうち、そのフォルダ直下にある所持譜面数が最多のフォルダを選ぶ。同数ならPATH昇順の先頭。
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (ChartFile chart in group.ChartFiles ?? [])
        {
            string directory = chart?.Directory;
            if (string.IsNullOrWhiteSpace(directory)) { continue; }
            directory = TrimTrailingSeparators(directory);
            counts[directory] = counts.GetValueOrDefault(directory) + 1;
        }
        string keep = folders[0];
        int keepCount = counts.GetValueOrDefault(TrimTrailingSeparators(keep));
        foreach (string folder in folders.Skip(1))
        {
            int count = counts.GetValueOrDefault(TrimTrailingSeparators(folder));
            if (count > keepCount)
            {
                keep = folder;
                keepCount = count;
            }
        }
        return new DuplicateMergeCandidateGroupInitial(group, folders, keep, folders.Length == 1, false, []);
    }

    /// <summary>
    /// 構築済みの一覧を採用します。UIスレッドで呼びます。構築時のライブラリが現在のものでなければ何も変えずfalseを返します。
    /// 採用する結果にないグループの状態は破棄し、同じインスタンスのグループは状態を保ち、新しいインスタンスは初期値にします。
    /// 並べ替えと検索語は保ち、新しい結果へ当て直します。
    /// </summary>
    internal bool Adopt(DuplicateMergeCandidateListPreparation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Library, library)) { return false; }
        var next = new Dictionary<DuplicateGroup, DuplicateMergeCandidateGroupState>(ReferenceEqualityComparer.Instance);
        foreach (DuplicateMergeCandidateGroupInitial initial in value.Groups)
        {
            if (!states.TryGetValue(initial.Group, out DuplicateMergeCandidateGroupState state))
            {
                state = new DuplicateMergeCandidateGroupState(initial);
            }
            next[initial.Group] = state;
        }
        states = next;
        preparation = value;
        RebuildView();
        IsViewActive = treeSelected;
        return true;
    }

    /// <summary>
    /// 列見出しの指定で、グループの先頭行（PATH昇順の先頭）の値を使ってグループ単位に並べ替えます。
    /// SELECTとSTATUSはグループの値を使います。KEEPなど並べ替えの対象外の列は無視します。
    /// </summary>
    /// <param name="memberPath">列の並べ替えの識別子（<c>Select</c>、<c>Status</c>、<c>Path</c>、<c>Folder</c>、<c>Title</c>、<c>Artist</c>）。</param>
    /// <param name="direction">方向です。</param>
    internal void RequestSort(string memberPath, ListSortDirection direction)
    {
        if (!IsSortable(memberPath)) { return; }
        sortColumnName = memberPath;
        sortDirection = direction;
        RaisePropertyChanged(nameof(SortColumnName));
        RaisePropertyChanged(nameof(SortDirection));
        RebuildView();
    }

    private static bool IsSortable(string memberPath)
        => memberPath is "Select" or "Status" or "Path" or "Folder" or "Title" or "Artist";

    /// <summary>SELECTのセルの操作です。そのグループのオン・オフを切り替えます。統合不可は変わりません。</summary>
    internal bool ToggleSelect(DuplicateMergeCandidateRow row)
    {
        if (!IsCurrent(row) || !row.State.CanSelect) { return false; }
        row.State.IsSelected = !row.State.IsSelected;
        NotifyStateChanged();
        return true;
    }

    /// <summary>KEEPのセルの操作です。そのフォルダをグループのKEEPにします。固定のグループは変わりません。</summary>
    internal bool SetKeep(DuplicateMergeCandidateRow row)
    {
        if (!IsCurrent(row) || row.State.KeepFixed || row.IsKeep) { return false; }
        string folder = row.State.Folders.FirstOrDefault(candidate => string.Equals(candidate, row.Path, StringComparison.OrdinalIgnoreCase));
        if (folder == null) { return false; }
        row.State.KeepFolder = folder;
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Spaceキーの操作です。行が属するグループごとに一度だけ作用し、統合不可を除いたグループにオンとオフが混在していればすべてオンにします。
    /// すべてオンならすべてオフ、すべてオフならすべてオンです。統合不可は変えません。
    /// </summary>
    internal bool ToggleSelectOfRows(IEnumerable<DuplicateMergeCandidateRow> targetRows)
    {
        List<DuplicateMergeCandidateGroupState> targets = CollectSelectableGroups(targetRows);
        if (targets.Count == 0) { return false; }
        bool next = !targets.All(state => state.IsSelected);
        foreach (DuplicateMergeCandidateGroupState state in targets) { state.IsSelected = next; }
        NotifyStateChanged();
        return true;
    }

    /// <summary>右クリックメニューの「チェックを入れる」「チェックを外す」です。行が属するグループごとに一度だけ作用し、統合不可は変えません。</summary>
    internal bool SetSelectOfRows(IEnumerable<DuplicateMergeCandidateRow> targetRows, bool selected)
    {
        List<DuplicateMergeCandidateGroupState> targets = CollectSelectableGroups(targetRows);
        bool changed = false;
        foreach (DuplicateMergeCandidateGroupState state in targets)
        {
            if (state.IsSelected == selected) { continue; }
            state.IsSelected = selected;
            changed = true;
        }
        if (changed) { NotifyStateChanged(); }
        return changed;
    }

    /// <summary>ツールバーの「表示中をすべて選択」です。検索で表示中のグループだけに作用し、非表示のグループと統合不可は変えません。</summary>
    internal bool SelectVisible() => SetVisible(selected: true);

    /// <summary>ツールバーの「表示中をすべて解除」です。検索で表示中のグループだけに作用し、非表示のグループは変えません。</summary>
    internal bool ClearVisible() => SetVisible(selected: false);

    private bool SetVisible(bool selected)
    {
        bool changed = false;
        foreach (DuplicateMergeCandidateGroupState state in visibleStates)
        {
            if (!state.CanSelect || state.IsSelected == selected) { continue; }
            state.IsSelected = selected;
            changed = true;
        }
        if (changed) { NotifyStateChanged(); }
        return changed;
    }

    private List<DuplicateMergeCandidateGroupState> CollectSelectableGroups(IEnumerable<DuplicateMergeCandidateRow> targetRows)
    {
        var seen = new HashSet<DuplicateMergeCandidateGroupState>(ReferenceEqualityComparer.Instance);
        var targets = new List<DuplicateMergeCandidateGroupState>();
        foreach (DuplicateMergeCandidateRow row in targetRows ?? [])
        {
            if (!IsCurrent(row) || !row.State.CanSelect || !seen.Add(row.State)) { continue; }
            targets.Add(row.State);
        }
        return targets;
    }

    /// <summary>
    /// 「優先統合先…」のウィンドウで確定した登録内容を保持し、一覧の全グループ（非表示を含む）へ適用します。
    /// 登録したどれかの優先先と同じかその配下にあるフォルダがグループ内でちょうど一つなら、そのフォルダをKEEPにします。
    /// 該当なし・二つ以上・KEEPが固定のグループは変えません。判定はパス成分の境界で行い、大文字小文字と末尾の区切りを無視します。
    /// 確定後に現れた新しいインスタンスへは自動で当てません。
    /// </summary>
    /// <param name="destinations">確定した優先統合先です。重複した登録は一つにまとめます。</param>
    internal void ApplyPreferredDestinations(IEnumerable<string> destinations)
    {
        string[] normalized = [.. (destinations ?? [])
            .Where(destination => !string.IsNullOrWhiteSpace(destination))
            .Select(NormalizeForComparison)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        preferredDestinations = [.. (destinations ?? [])
            .Where(destination => !string.IsNullOrWhiteSpace(destination))
            .DistinctBy(NormalizeForComparison, StringComparer.OrdinalIgnoreCase)];
        bool changed = false;
        foreach (DuplicateMergeCandidateGroupState state in states.Values)
        {
            if (state.KeepFixed) { continue; }
            string matched = null;
            int matchCount = 0;
            foreach (string folder in state.Folders)
            {
                string key = NormalizeForComparison(folder);
                if (!normalized.Any(destination => IsSameOrDescendant(destination, key))) { continue; }
                matchCount++;
                matched = folder;
            }
            if (matchCount != 1 || string.Equals(state.KeepFolder, matched, StringComparison.OrdinalIgnoreCase)) { continue; }
            state.KeepFolder = matched;
            changed = true;
        }
        if (changed) { NotifyStateChanged(); }
    }

    private static bool IsSameOrDescendant(string ancestor, string path)
        => string.Equals(ancestor, path, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(ancestor.EndsWith('\\') ? ancestor : ancestor + '\\', StringComparison.OrdinalIgnoreCase);

    private static string NormalizeForComparison(string path)
        => TrimTrailingSeparators(path.Trim().Replace('/', '\\'));

    private static string TrimTrailingSeparators(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        // ドライブのルート（C:\）は区切りを残し、空文字にしない。
        return trimmed.Length == 2 && trimmed[1] == ':' ? trimmed + '\\' : trimmed.Length == 0 ? path : trimmed;
    }

    /// <summary>右クリックメニューの「エクスプローラーで開く」です。存在する選択フォルダごとに一度だけ開き、存在しないフォルダは開かず案内も出しません。</summary>
    internal void OpenInExplorer(IEnumerable<DuplicateMergeCandidateRow> targetRows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (DuplicateMergeCandidateRow row in targetRows ?? [])
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Path) || !seen.Add(NormalizeForComparison(row.Path))) { continue; }
            if (directoryExists(row.Path)) { openDirectory(row.Path); }
        }
    }

    /// <summary>
    /// 後続の単位が使う窓口です。表示中でSELECTがオンのグループを、グループ・PATH昇順のフォルダ・KEEP・固定の有無を持つ変更不能な対象として、表示順に捕捉します。
    /// 以後の状態の変更は捕捉した値へ影響しません。
    /// </summary>
    internal IReadOnlyList<DuplicateMergeCandidateGroupTarget> CaptureSelectedGroups()
        => [.. visibleStates
            .Where(state => state.IsSelected)
            .Select(state => new DuplicateMergeCandidateGroupTarget(state.Group, [.. state.Folders], state.KeepFolder, state.KeepFixed))];

    /// <summary>
    /// 後続の単位が使う窓口です。グループの確認結果をUIスレッドで書き込みます。そのグループが現在の状態表になければ捨ててfalseを返します。
    /// 統合不可を書き込んだグループはSELECTをオフにします。
    /// </summary>
    internal bool TryWriteVerification(DuplicateGroup group, DuplicateMergeVerificationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (group == null || !states.TryGetValue(group, out DuplicateMergeCandidateGroupState state)) { return false; }
        state.Verification = result;
        if (!state.CanSelect) { state.IsSelected = false; }
        // STATUSは検索と並べ替えの対象のため、それらが有効なときは行集合を当て直す。
        if (searchQuery.HasTokens || sortColumnName is "Status" or "Select") { RebuildView(); }
        else { NotifyStateChanged(); }
        return true;
    }

    /// <summary>現在の状態表にあるグループの状態を返します。後続の単位と画面が、状態表を介さずに参照する窓口です。</summary>
    internal bool TryGetState(DuplicateGroup group, out DuplicateMergeCandidateGroupState state)
    {
        state = null;
        return group != null && states.TryGetValue(group, out state);
    }

    private bool IsCurrent(DuplicateMergeCandidateRow row)
        => row != null && states.TryGetValue(row.State.Group, out DuplicateMergeCandidateGroupState current) && ReferenceEquals(current, row.State);

    private void NotifyStateChanged()
    {
        RefreshCounts();
        DisplayInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshCounts()
    {
        VisibleGroupCount = visibleStates.Count;
        SelectedGroupCount = visibleStates.Count(state => state.IsSelected);
    }

    /// <summary>採用済みの結果・検索語・並べ替えから、表示する行集合を作り直します。TITLE・ARTISTの解決は呼びません。</summary>
    private void RebuildView()
    {
        if (preparation == null) { return; }
        IReadOnlyDictionary<string, (string Title, string Artist)> pairs = preparation.Pairs;
        (string Title, string Artist) PairOf(string folder)
            => pairs.TryGetValue(folder, out (string Title, string Artist) pair) ? pair : (string.Empty, string.Empty);

        IEnumerable<DuplicateMergeCandidateGroupState> candidates = preparation.Groups.Select(initial => states[initial.Group]);
        if (searchQuery.HasTokens)
        {
            candidates = candidates.Where(state =>
            {
                string status = state.Verification.FormatStatus();
                return state.Folders.Any(folder =>
                {
                    (string title, string artist) = PairOf(folder);
                    return searchQuery.MatchesDuplicateMergeCandidate(folder, GetFolderName(folder), title, artist, status);
                });
            });
        }
        List<DuplicateMergeCandidateGroupState> ordered = [.. candidates];
        if (sortColumnName != null)
        {
            ordered = Sort(ordered, PairOf);
        }

        int rowCount = ordered.Sum(state => state.Folders.Count);
        var builtRows = new DuplicateMergeCandidateRow[rowCount];
        var firstIndexes = new List<int>(ordered.Count);
        var counts = new List<int>(ordered.Count);
        int index = 0;
        foreach (DuplicateMergeCandidateGroupState state in ordered)
        {
            int groupIndex = firstIndexes.Count;
            firstIndexes.Add(index);
            counts.Add(state.Folders.Count);
            foreach (string folder in state.Folders)
            {
                (string title, string artist) = PairOf(folder);
                builtRows[index] = new DuplicateMergeCandidateRow(state, folder, GetFolderName(folder), title, artist, index, groupIndex);
                index++;
            }
        }
        visibleStates = ordered;
        rows = new DuplicateMergeCandidateRowSet(builtRows, [.. firstIndexes], [.. counts]);
        RaisePropertyChanged(nameof(Rows));
        RefreshCounts();
    }

    private List<DuplicateMergeCandidateGroupState> Sort(
        List<DuplicateMergeCandidateGroupState> source, Func<string, (string Title, string Artist)> pairOf)
    {
        bool descending = sortDirection == ListSortDirection.Descending;
        // 同値は既定の結果順を保つため、安定な並べ替え（LINQのOrderBy）を使う。グループ内の行順は変えない。
        if (sortColumnName == "Select")
        {
            return descending
                ? [.. source.OrderByDescending(state => state.IsSelected)]
                : [.. source.OrderBy(state => state.IsSelected)];
        }
        Func<DuplicateMergeCandidateGroupState, string> key = sortColumnName switch
        {
            "Status" => state => state.Verification.FormatStatus(),
            "Path" => state => state.Folders.Count > 0 ? state.Folders[0] : string.Empty,
            "Folder" => state => state.Folders.Count > 0 ? GetFolderName(state.Folders[0]) : string.Empty,
            "Title" => state => state.Folders.Count > 0 ? pairOf(state.Folders[0]).Title : string.Empty,
            "Artist" => state => state.Folders.Count > 0 ? pairOf(state.Folders[0]).Artist : string.Empty,
            _ => state => string.Empty
        };
        return descending
            ? [.. source.OrderByDescending(key, StringComparer.OrdinalIgnoreCase)]
            : [.. source.OrderBy(key, StringComparer.OrdinalIgnoreCase)];
    }

    private static string GetFolderName(string path)
    {
        string trimmed = path.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
