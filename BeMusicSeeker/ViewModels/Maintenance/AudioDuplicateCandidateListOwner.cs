using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using Livet;

namespace BeMusicSeeker.ViewModels;

/// <summary>統合候補一覧と整理候補一覧が共用する検索欄の補完状態の窓口です。画面の検索欄が、表示中の一覧の種類によらず接続するために使います。</summary>
internal interface ICandidateListSearchSurface
{
    /// <summary>検索欄独自の補完状態です。</summary>
    KeywordSearchAssistanceOwner SearchAssistance { get; }

    /// <summary>除外欄独自の補完状態です。</summary>
    KeywordSearchAssistanceOwner ExcludeAssistance { get; }
}

/// <summary>一覧の構築（UIスレッド外）で作り、UIスレッドで採用する入力です。フォルダごとの初期状態とTITLE・ARTISTを不変の値で持ちます。</summary>
internal sealed class AudioDuplicateCandidateListPreparation
{
    internal AudioDuplicateCandidateListPreparation(
        object library,
        IReadOnlyList<AudioDuplicateCandidateInitial> folders,
        IReadOnlyDictionary<string, (string Title, string Artist)> pairs)
    {
        Library = library;
        Folders = folders;
        Pairs = pairs;
    }

    /// <summary>構築時のライブラリです。採用時に、現在のライブラリと一致する場合だけ採用します。</summary>
    internal object Library { get; }

    /// <summary>探索結果の順に並べたフォルダの初期状態です。</summary>
    internal IReadOnlyList<AudioDuplicateCandidateInitial> Folders { get; }

    /// <summary>フォルダ（大文字小文字を無視）ごとのTITLE・ARTISTです。</summary>
    internal IReadOnlyDictionary<string, (string Title, string Artist)> Pairs { get; }
}

/// <summary>
/// 重複音源の整理候補一覧の状態と判断を持つ管理主体です。フォルダごとのSELECT・確認結果（状態表）、採用済みの行集合、並べ替え、検索語、
/// 除外語、衝突整理、表示中かどうか、件数を所有します。状態表と行集合の読み書きはUIスレッドだけで行い、構築（<see cref="Prepare"/>）だけがUIスレッド外で動きます。
/// </summary>
/// <remarks>
/// 状態は <see cref="DuplicateGroup"/> のインスタンスに対応付けます。探索や整理で同じインスタンスが維持されたフォルダは状態を保ち、
/// 新しいインスタンスは初期値（未確認・SELECTオフ）になり、消えたフォルダの状態は採用時に破棄します。永続化しません。
/// 並べ替え・検索・除外・SELECTの操作は、構築済みの値だけを使い、TITLE・ARTISTの解決を呼びません。重複・分裂配置の一覧とは独立の状態です。
/// </remarks>
public sealed class AudioDuplicateCandidateListOwner : ViewModel, ICandidateListSearchSurface
{
    private const GridKeywordSearchContext SearchContext = GridKeywordSearchContext.AudioDuplicateCandidate;

    private readonly Func<string, bool> directoryExists;
    private readonly Action<string> openDirectory;
    private Dictionary<DuplicateGroup, AudioDuplicateCandidateState> states = new(ReferenceEqualityComparer.Instance);
    private AudioDuplicateCandidateListPreparation preparation;
    private object library;
    private volatile bool treeSelected;
    private bool isViewActive;
    private AudioDuplicateCandidateRowSet rows = AudioDuplicateCandidateRowSet.Empty;
    private List<AudioDuplicateCandidateState> visibleStates = [];
    private GridKeywordSearchQuery searchQuery = GridKeywordSearchQuery.Parse(null);
    private string searchText = string.Empty;
    private GridKeywordSearchQuery excludeQuery = GridKeywordSearchQuery.Parse(null);
    private string excludeText = string.Empty;
    private string sortColumnName;
    private ListSortDirection sortDirection = ListSortDirection.Ascending;
    private int visibleFolderCount;
    private int selectedFolderCount;
    private AudioDuplicateCollisionCleanupMode collisionCleanupMode;
    private DuplicateOrganizeDeleteMethod deleteMethod;

    /// <param name="directoryExists">フォルダの存在を確認する窓口です。エクスプローラーで開く対象の判定に使います。</param>
    /// <param name="openDirectory">フォルダをエクスプローラーで開く窓口です。</param>
    internal AudioDuplicateCandidateListOwner(Func<string, bool> directoryExists, Action<string> openDirectory)
    {
        this.directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
        this.openDirectory = openDirectory ?? throw new ArgumentNullException(nameof(openDirectory));
    }

    /// <summary>表示内容（SELECT・STATUS・CONFLICTS）が変わり、表が再描画する必要があるときに通知します。行集合は変わりません。</summary>
    internal event EventHandler DisplayInvalidated;

    /// <summary>列の幅・順序です。起動中だけ保持し、設定へ保存しません。</summary>
    public AudioDuplicateCandidateColumnSettings ColumnsSettings { get; } = new();

    /// <summary>表に渡す採用済みの行集合です。再構築・並べ替え・検索で別のインスタンスへ置き換わります。</summary>
    public IList Rows => rows;

    /// <summary>採用済みの行集合を型付きで返します。</summary>
    internal AudioDuplicateCandidateRowSet RowSet => rows;

    /// <summary>整理候補一覧を表示中かです。選択した画面が一覧で、その画面の一覧を採用した後にだけ真になります。</summary>
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

    /// <summary>表示中（検索で残り、除外で除かれなかった）のフォルダ数です。</summary>
    public int VisibleGroupCount
    {
        get => visibleFolderCount;
        private set
        {
            if (visibleFolderCount == value) { return; }
            visibleFolderCount = value;
            RaisePropertyChanged(nameof(VisibleGroupCount));
        }
    }

    /// <summary>表示中でSELECTがオンのフォルダ数です。</summary>
    public int SelectedGroupCount
    {
        get => selectedFolderCount;
        private set
        {
            if (selectedFolderCount == value) { return; }
            selectedFolderCount = value;
            RaisePropertyChanged(nameof(SelectedGroupCount));
        }
    }

    /// <summary>専用の検索欄の入力です。既存のキーワード検索の構文を使います。</summary>
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
            RaisePropertyChanged(nameof(SearchWarningText));
            RebuildView();
        }
    }

    /// <summary>除外用の検索欄の入力です。条件を満たすフォルダを一覧から除きます。空なら何も除きません。除外で非表示になったフォルダの状態は保ちます。</summary>
    public string ExcludeText
    {
        get => excludeText;
        set
        {
            string next = value ?? string.Empty;
            if (string.Equals(excludeText, next, StringComparison.Ordinal)) { return; }
            excludeText = next;
            excludeQuery = GridKeywordSearchQuery.Parse(next);
            RaisePropertyChanged(nameof(ExcludeText));
            RaisePropertyChanged(nameof(ExcludeWarningText));
            RebuildView();
        }
    }

    /// <summary>検索欄の構文警告です。空なら警告を表示しません。</summary>
    public string SearchWarningText => KeywordSearchPresentationText.BuildWarningText(searchQuery, SearchContext);

    /// <summary>除外欄の構文警告です。空なら警告を表示しません。</summary>
    public string ExcludeWarningText => KeywordSearchPresentationText.BuildWarningText(excludeQuery, SearchContext);

    /// <inheritdoc />
    KeywordSearchAssistanceOwner ICandidateListSearchSurface.SearchAssistance => SearchAssistance;

    /// <inheritdoc />
    KeywordSearchAssistanceOwner ICandidateListSearchSurface.ExcludeAssistance => ExcludeAssistance;

    /// <summary>検索欄独自の補完状態です。保存式の管理主体は接続しません。</summary>
    internal KeywordSearchAssistanceOwner SearchAssistance { get; } = new(null, SearchContext);

    /// <summary>除外欄独自の補完状態です。保存式の管理主体は接続しません。</summary>
    internal KeywordSearchAssistanceOwner ExcludeAssistance { get; } = new(null, SearchContext);

    /// <summary>画面の寿命に合わせ、言語変更を警告の再表示へ接続します。</summary>
    internal void AttachKeywordSearchPresentation()
    {
        ResourceService.Current.PropertyChanged -= KeywordSearchResourcesChanged;
        ResourceService.Current.PropertyChanged += KeywordSearchResourcesChanged;
    }

    /// <summary>画面の表示寿命が終わった検索欄を言語通知から切り離します。</summary>
    internal void DetachKeywordSearchPresentation()
        => ResourceService.Current.PropertyChanged -= KeywordSearchResourcesChanged;

    private void KeywordSearchResourcesChanged(object sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(ResourceService.Resources))
        {
            RaisePropertyChanged(nameof(SearchWarningText));
            RaisePropertyChanged(nameof(ExcludeWarningText));
        }
    }

    /// <summary>並べ替えの列の識別子です。並べ替えていなければnullです。</summary>
    public string SortColumnName => sortColumnName;

    /// <summary>並べ替えの方向です。</summary>
    public ListSortDirection SortDirection => sortDirection;

    /// <summary>
    /// 整理で、同名で衝突したWAVとOGGのどちらを残すか（衝突整理）です。既定は「OGGを残す」で、アプリの起動中だけ保持し、ライブラリの交換後も残ります。
    /// 整理の対象の捕捉と同じ時点で一度だけ読み、整理の途中の変更は進行中の整理へ影響しません。確認には使いません。切替えで案内は出しません。
    /// </summary>
    public AudioDuplicateCollisionCleanupMode CollisionCleanupMode
    {
        get => collisionCleanupMode;
        set
        {
            if (collisionCleanupMode == value) { return; }
            collisionCleanupMode = value;
            RaisePropertyChanged(nameof(CollisionCleanupMode));
        }
    }

    /// <summary>
    /// 整理で捨てる形式の音源を、ごみ箱へ移動するか完全に削除するか（削除）です。既定はごみ箱へ移動で、<see cref="CollisionCleanupMode"/> と同じく、アプリの起動中だけ保持し、ライブラリの交換後も残ります。
    /// 設定へは保存しません。整理の対象の捕捉と同じ時点で一度だけ読み、整理の途中の変更は進行中の整理へ影響しません。確認には使いません。切替えで案内は出しません。
    /// </summary>
    public DuplicateOrganizeDeleteMethod DeleteMethod
    {
        get => deleteMethod;
        set
        {
            if (deleteMethod == value) { return; }
            deleteMethod = value;
            RaisePropertyChanged(nameof(DeleteMethod));
        }
    }

    /// <summary>
    /// 接続中のライブラリを知らせます。別のライブラリへ交換した場合と解除した場合は、すべてのフォルダの状態と採用済みの行を破棄して非表示にします。
    /// 衝突整理・削除・並べ替え・検索語・除外語は保ちます。
    /// </summary>
    internal void AttachLibrary(object value)
    {
        if (ReferenceEquals(library, value)) { return; }
        library = value;
        states = new(ReferenceEqualityComparer.Instance);
        preparation = null;
        visibleStates = [];
        rows = AudioDuplicateCandidateRowSet.Empty;
        treeSelected = false;
        IsViewActive = false;
        RaisePropertyChanged(nameof(Rows));
        RefreshCounts();
    }

    /// <summary>
    /// 重複音源ツリーで整理候補一覧が選択されているかを知らせます。他の画面へ移るとただちに非表示にし、一覧を選び直したときは、
    /// その画面の新しい一覧を採用するまで表示しません。
    /// </summary>
    internal void SetTreeSelected(bool selected)
    {
        if (treeSelected == selected) { return; }
        treeSelected = selected;
        IsViewActive = false;
    }

    /// <summary>
    /// 一覧を構築します。UIスレッド外で呼び、状態表には触れません。ライブラリへの問合せは、全フォルダのTITLE・ARTISTの一括解決の一回だけです。
    /// </summary>
    /// <param name="libraryToken">構築時のライブラリ。採用時に現在のライブラリと照合します。</param>
    /// <param name="folders">探索結果です。順序を保ちます。</param>
    /// <param name="source">ライブラリの入力の窓口です。</param>
    /// <param name="cancellationToken">取消です。</param>
    internal static AudioDuplicateCandidateListPreparation Prepare(
        object libraryToken, IEnumerable<AudioDuplicateFolder> folders, IDuplicateMergeCandidateListSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(source);
        AudioDuplicateCandidateInitial[] initials =
        [
            .. folders.Where(folder => folder != null)
                .Select(folder => new AudioDuplicateCandidateInitial(folder.Group, folder.Folder, folder.CollisionNames))
        ];
        IReadOnlyDictionary<string, (string Title, string Artist)> pairs =
            source.ResolveDestinationDisplayPairs([.. initials.Select(initial => initial.Folder)], cancellationToken);
        return new AudioDuplicateCandidateListPreparation(libraryToken, initials, pairs);
    }

    /// <summary>
    /// 構築済みの一覧を採用します。UIスレッドで呼びます。構築時のライブラリが現在のものでなければ何も変えずfalseを返します。
    /// 採用する結果にないフォルダの状態は破棄し、同じインスタンスのフォルダは状態を保ち、新しいインスタンスは初期値にします。
    /// 並べ替え・検索語・除外語は保ち、新しい結果へ当て直します。
    /// </summary>
    internal bool Adopt(AudioDuplicateCandidateListPreparation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Library, library)) { return false; }
        var next = new Dictionary<DuplicateGroup, AudioDuplicateCandidateState>(ReferenceEqualityComparer.Instance);
        foreach (AudioDuplicateCandidateInitial initial in value.Folders)
        {
            if (!states.TryGetValue(initial.Group, out AudioDuplicateCandidateState state))
            {
                state = new AudioDuplicateCandidateState(initial);
            }
            state.CollisionNames = initial.CollisionNames;
            next[initial.Group] = state;
        }
        states = next;
        preparation = value;
        RebuildView();
        IsViewActive = treeSelected;
        return true;
    }

    /// <summary>
    /// 一覧の元になる結果がない間（結果の失効・未探索）の入口です。UIスレッドで呼びます。
    /// フォルダの状態・並べ替え・検索語・除外語・衝突整理は保ったまま、行を0件にして表示中でない状態にします。
    /// 空の結果を <see cref="Adopt"/> すると状態が捨てられるため、別の入口にしています。
    /// </summary>
    /// <param name="libraryToken">構築時のライブラリ。現在のライブラリと一致しなければ何も変えずfalseを返します。</param>
    internal bool ShowNoResult(object libraryToken)
    {
        if (!ReferenceEquals(libraryToken, library)) { return false; }
        preparation = null;
        visibleStates = [];
        rows = AudioDuplicateCandidateRowSet.Empty;
        IsViewActive = false;
        RaisePropertyChanged(nameof(Rows));
        RefreshCounts();
        return true;
    }

    /// <summary>
    /// 列見出しの指定で並べ替えます。SELECTはオフが先、STATUSは表示文言、CONFLICTSは数値の比較で、同値は既定の結果順です。
    /// </summary>
    /// <param name="memberPath">列の並べ替えの識別子（<c>Select</c>、<c>Status</c>、<c>Conflicts</c>、<c>Path</c>、<c>Folder</c>、<c>Title</c>、<c>Artist</c>）。</param>
    /// <param name="direction">方向です。</param>
    internal void RequestSort(string memberPath, ListSortDirection direction)
    {
        if (memberPath is not ("Select" or "Status" or "Conflicts" or "Path" or "Folder" or "Title" or "Artist")) { return; }
        sortColumnName = memberPath;
        sortDirection = direction;
        RaisePropertyChanged(nameof(SortColumnName));
        RaisePropertyChanged(nameof(SortDirection));
        RebuildView();
    }

    /// <summary>SELECTのセルの操作です。そのフォルダのオン・オフを切り替えます。整理不可は変わりません。</summary>
    internal bool ToggleSelect(AudioDuplicateCandidateRow row)
    {
        if (!IsCurrent(row) || !row.State.CanSelect) { return false; }
        row.State.IsSelected = !row.State.IsSelected;
        NotifyStateChanged();
        return true;
    }

    /// <summary>
    /// Spaceキーの操作です。行のフォルダごとに一度だけ作用し、整理不可を除いたフォルダにオンとオフが混在していればすべてオンにします。
    /// すべてオンならすべてオフ、すべてオフならすべてオンです。整理不可は変えません。
    /// </summary>
    internal bool ToggleSelectOfRows(IEnumerable<AudioDuplicateCandidateRow> targetRows)
    {
        List<AudioDuplicateCandidateState> targets = CollectSelectable(targetRows);
        if (targets.Count == 0) { return false; }
        bool next = !targets.All(state => state.IsSelected);
        foreach (AudioDuplicateCandidateState state in targets) { state.IsSelected = next; }
        NotifyStateChanged();
        return true;
    }

    /// <summary>右クリックメニューの「チェックを入れる」「チェックを外す」です。行のフォルダごとに一度だけ作用し、整理不可は変えません。</summary>
    internal bool SetSelectOfRows(IEnumerable<AudioDuplicateCandidateRow> targetRows, bool selected)
    {
        bool changed = false;
        foreach (AudioDuplicateCandidateState state in CollectSelectable(targetRows))
        {
            if (state.IsSelected == selected) { continue; }
            state.IsSelected = selected;
            changed = true;
        }
        if (changed) { NotifyStateChanged(); }
        return changed;
    }

    /// <summary>ツールバーの「表示中をすべて選択」です。検索と除外を当てた後の表示中のフォルダだけに作用し、非表示のフォルダと整理不可は変えません。</summary>
    internal bool SelectVisible() => SetVisible(selected: true);

    /// <summary>ツールバーの「表示中をすべて解除」です。検索と除外を当てた後の表示中のフォルダだけに作用し、非表示のフォルダは変えません。</summary>
    internal bool ClearVisible() => SetVisible(selected: false);

    private bool SetVisible(bool selected)
    {
        bool changed = false;
        foreach (AudioDuplicateCandidateState state in visibleStates)
        {
            if (!state.CanSelect || state.IsSelected == selected) { continue; }
            state.IsSelected = selected;
            changed = true;
        }
        if (changed) { NotifyStateChanged(); }
        return changed;
    }

    private List<AudioDuplicateCandidateState> CollectSelectable(IEnumerable<AudioDuplicateCandidateRow> targetRows)
    {
        var seen = new HashSet<AudioDuplicateCandidateState>(ReferenceEqualityComparer.Instance);
        var targets = new List<AudioDuplicateCandidateState>();
        foreach (AudioDuplicateCandidateRow row in targetRows ?? [])
        {
            if (!IsCurrent(row) || !row.State.CanSelect || !seen.Add(row.State)) { continue; }
            targets.Add(row.State);
        }
        return targets;
    }

    /// <summary>右クリックメニューの「エクスプローラーで開く」です。存在する選択フォルダごとに一度だけ開き、存在しないフォルダは開かず案内も出しません。</summary>
    internal void OpenInExplorer(IEnumerable<AudioDuplicateCandidateRow> targetRows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioDuplicateCandidateRow row in targetRows ?? [])
        {
            if (row == null || string.IsNullOrWhiteSpace(row.Path) || !seen.Add(row.Path.Trim().TrimEnd('\\', '/'))) { continue; }
            if (directoryExists(row.Path)) { openDirectory(row.Path); }
        }
    }

    /// <summary>表示中でSELECTがオンのフォルダを、確認結果と、捕捉時点の衝突整理とともに、表示順に捕捉します。以後の状態の変更は影響しません。</summary>
    internal IReadOnlyList<AudioDuplicateCandidateTarget> CaptureSelectedFolders()
        => [.. visibleStates.Where(state => state.IsSelected).Select(CaptureTarget)];

    /// <summary>指定した行のフォルダを、フォルダごとに一度、行の順で捕捉します。SELECTの状態によらず、現在の状態表にあるフォルダだけが対象です。</summary>
    internal IReadOnlyList<AudioDuplicateCandidateTarget> CaptureFoldersOfRows(IEnumerable<AudioDuplicateCandidateRow> targetRows)
    {
        var seen = new HashSet<AudioDuplicateCandidateState>(ReferenceEqualityComparer.Instance);
        var targets = new List<AudioDuplicateCandidateTarget>();
        foreach (AudioDuplicateCandidateRow row in targetRows ?? [])
        {
            if (!IsCurrent(row) || !seen.Add(row.State)) { continue; }
            targets.Add(CaptureTarget(row.State));
        }
        return targets;
    }

    private AudioDuplicateCandidateTarget CaptureTarget(AudioDuplicateCandidateState state)
        => new(state.Group, state.Folder, state.Inspection, collisionCleanupMode, deleteMethod);

    /// <summary>
    /// 確認結果を、複数のフォルダ分まとめてUIスレッドで書き込みます。現在の状態表にないフォルダの結果は捨てます。
    /// 書き込んだフォルダのSELECTは、段階によらずオフにします（整理不可はオフで無効）。
    /// 行集合の差し替えは、検索・除外・並べ替えがSTATUS・SELECT・CONFLICTSに依存するときだけ、全体で一回です。
    /// </summary>
    /// <returns>書き込んだ結果。捨てた結果は含みません。</returns>
    internal IReadOnlyList<AudioDuplicateVerificationWrite> WriteVerifications(IEnumerable<AudioDuplicateVerificationWrite> writes)
    {
        ArgumentNullException.ThrowIfNull(writes);
        var accepted = new List<AudioDuplicateVerificationWrite>();
        foreach (AudioDuplicateVerificationWrite write in writes)
        {
            ArgumentNullException.ThrowIfNull(write);
            if (write.Group == null || !states.TryGetValue(write.Group, out AudioDuplicateCandidateState state)) { continue; }
            state.Inspection = write.Inspection;
            state.IsSelected = false;
            accepted.Add(write);
        }
        if (accepted.Count == 0) { return accepted; }
        if (searchQuery.HasTokens || excludeQuery.HasTokens || sortColumnName is "Status" or "Select" or "Conflicts") { RebuildView(); }
        else { NotifyStateChanged(); }
        return accepted;
    }

    /// <summary>
    /// 整理で処理したフォルダのSELECTを、まとめてオフにします。UIスレッドで呼びます。現在の状態表にないフォルダは捨て、状態を作りません。
    /// 確認結果は書き換えません。行集合の差し替えは、SELECTでの並べ替えが有効なときだけ、全体で一回です。
    /// </summary>
    /// <returns>SELECTが変わったときは true。</returns>
    internal bool ClearSelectOfGroups(IEnumerable<DuplicateGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        bool changed = false;
        foreach (DuplicateGroup group in groups)
        {
            if (group == null || !states.TryGetValue(group, out AudioDuplicateCandidateState state) || !state.IsSelected) { continue; }
            state.IsSelected = false;
            changed = true;
        }
        if (!changed) { return false; }
        if (sortColumnName == "Select") { RebuildView(); }
        else { NotifyStateChanged(); }
        return true;
    }

    /// <summary>
    /// 新しい結果にないフォルダ（整理で解消したフォルダ）の状態と行を、画面の表示中かによらず取り除きます。UIスレッドで呼びます。
    /// 結果が空なら、一覧の行は0件になります。結果に残るフォルダの状態と、並べ替え・検索語・除外語・衝突整理は変えません。解消したフォルダを持たなければ何もしません。
    /// </summary>
    /// <param name="currentGroups">新しい結果のグループです。</param>
    internal void RemoveUnresolvedFolders(IEnumerable<DuplicateGroup> currentGroups)
    {
        ArgumentNullException.ThrowIfNull(currentGroups);
        if (preparation == null || states.Count == 0) { return; }
        var current = new HashSet<DuplicateGroup>(currentGroups.Where(group => group != null), ReferenceEqualityComparer.Instance);
        if (states.Keys.All(current.Contains)) { return; }
        var remaining = new Dictionary<DuplicateGroup, AudioDuplicateCandidateState>(ReferenceEqualityComparer.Instance);
        foreach ((DuplicateGroup group, AudioDuplicateCandidateState state) in states)
        {
            if (current.Contains(group)) { remaining[group] = state; }
        }
        states = remaining;
        preparation = new AudioDuplicateCandidateListPreparation(
            preparation.Library, [.. preparation.Folders.Where(initial => remaining.ContainsKey(initial.Group))], preparation.Pairs);
        RebuildView();
    }

    /// <summary>現在の状態表にあるフォルダの状態を返します。画面と手順の主体が、状態表を介さずに参照する窓口です。</summary>
    internal bool TryGetState(DuplicateGroup group, out AudioDuplicateCandidateState state)
    {
        state = null;
        return group != null && states.TryGetValue(group, out state);
    }

    private bool IsCurrent(AudioDuplicateCandidateRow row)
        => row != null && states.TryGetValue(row.State.Group, out AudioDuplicateCandidateState current) && ReferenceEquals(current, row.State);

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

    /// <summary>採用済みの結果・検索語・除外語・並べ替えから、表示する行集合を作り直します。TITLE・ARTISTの解決は呼びません。</summary>
    private void RebuildView()
    {
        if (preparation == null) { return; }
        IReadOnlyDictionary<string, (string Title, string Artist)> pairs = preparation.Pairs;
        (string Title, string Artist) PairOf(string folder) => pairs.GetValueOrDefault(folder, (string.Empty, string.Empty));

        IEnumerable<AudioDuplicateCandidateState> candidates = preparation.Folders.Select(initial => states[initial.Group]);
        bool Matches(GridKeywordSearchQuery query, AudioDuplicateCandidateState state)
        {
            (string title, string artist) = PairOf(state.Folder);
            return query.MatchesDuplicateMergeCandidate(state.Folder, GetFolderName(state.Folder), title, artist, state.StatusText);
        }
        if (searchQuery.HasTokens) { candidates = candidates.Where(state => Matches(searchQuery, state)); }
        if (excludeQuery.HasTokens) { candidates = candidates.Where(state => !Matches(excludeQuery, state)); }
        List<AudioDuplicateCandidateState> ordered = [.. candidates];
        if (sortColumnName != null) { ordered = Sort(ordered, PairOf); }

        var built = new AudioDuplicateCandidateRow[ordered.Count];
        for (int index = 0; index < ordered.Count; index++)
        {
            AudioDuplicateCandidateState state = ordered[index];
            (string title, string artist) = PairOf(state.Folder);
            built[index] = new AudioDuplicateCandidateRow(state, GetFolderName(state.Folder), title, artist, index);
        }
        visibleStates = ordered;
        rows = new AudioDuplicateCandidateRowSet(built);
        RaisePropertyChanged(nameof(Rows));
        RefreshCounts();
    }

    private List<AudioDuplicateCandidateState> Sort(
        List<AudioDuplicateCandidateState> source, Func<string, (string Title, string Artist)> pairOf)
    {
        bool descending = sortDirection == ListSortDirection.Descending;
        // 同値は既定の結果順を保つため、安定な並べ替え（LINQのOrderBy）を使う。
        if (sortColumnName == "Select")
        {
            return descending ? [.. source.OrderByDescending(state => state.IsSelected)] : [.. source.OrderBy(state => state.IsSelected)];
        }
        if (sortColumnName == "Conflicts")
        {
            return descending ? [.. source.OrderByDescending(state => state.ConflictCount)] : [.. source.OrderBy(state => state.ConflictCount)];
        }
        Func<AudioDuplicateCandidateState, string> key = sortColumnName switch
        {
            "Status" => state => state.StatusText,
            "Path" => state => state.Folder,
            "Folder" => state => GetFolderName(state.Folder),
            "Title" => state => pairOf(state.Folder).Title,
            "Artist" => state => pairOf(state.Folder).Artist,
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
