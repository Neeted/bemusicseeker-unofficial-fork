#nullable enable
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using BeMusicSeeker.Models;
using R = BeMusicSeeker.Properties.Resources;

namespace BeMusicSeeker.ViewModels;

/// <summary>取得済みカタログの準備段階を表します。</summary>
internal enum PlaylistImportCatalogState
{
    /// <summary>取得を開始していません。</summary>
    NotStarted,
    /// <summary>取得中です。</summary>
    Loading,
    /// <summary>空の場合を含め取得が完了しました。</summary>
    Loaded,
    /// <summary>取得に失敗しました。</summary>
    Failed
}

/// <summary>画面寿命内で固定する候補情報です。</summary>
/// <param name="Uri">取込み先の絶対URI。</param>
/// <param name="Name">一覧の表示名。直接入力の場合はnull。</param>
/// <param name="Symbol">一覧の記号。</param>
/// <param name="Tag1">第一分類。</param>
/// <param name="Tag2">第二分類。</param>
/// <param name="NameKey">内蔵表の表示リソースキー。</param>
internal sealed record PlaylistImportCandidate(Uri Uri, string? Name, string Symbol = "", string Tag1 = "", string Tag2 = "", string? NameKey = null);

/// <summary>分類・候補・選択済み一覧の表示行です。選択の所有者は画面セッションです。</summary>
internal sealed class PlaylistImportItem : ObservableObject
{
    private readonly PlaylistImportDialogViewModel owner;
    private readonly string name;
    private bool expanded;
    private bool visible = true;
    private readonly string? expansionKey;
    private readonly bool externalGroup;
    private readonly bool localizedGroup;
    /// <summary>固定候補または分類名と子孫を表示行へ接続します。</summary>
    internal PlaylistImportItem(PlaylistImportDialogViewModel owner, string name, PlaylistImportCandidate? candidate = null, IReadOnlyList<PlaylistImportItem>? children = null, string? expansionKey = null, bool externalGroup = false, bool localizedGroup = false)
    {
        this.owner = owner;
        this.name = name;
        Candidate = candidate;
        Children = children ?? [];
        this.expansionKey = expansionKey;
        this.externalGroup = externalGroup;
        this.localizedGroup = localizedGroup;
        expanded = expansionKey != null && owner.GetExpansion(expansionKey, externalGroup, localizedGroup || Children.Any(child => child.IsGroup));
    }
    /// <summary>変更不能な葉候補。分類行ではnullです。</summary>
    internal PlaylistImportCandidate? Candidate { get; }
    /// <summary>表示名です。</summary>
    public string Name => Candidate is { } c ? (c.NameKey is { } key ? owner.Text(key) : c.Name ?? c.Uri.AbsoluteUri) : localizedGroup ? owner.Text(name) : name;
    /// <summary>記号を含む候補名です。</summary>
    public string DisplayName => Candidate is { Symbol.Length: > 0 } c ? c.Symbol + " " + Name : Name;
    /// <summary>元カタログの子孫です。表示条件は各行のIsVisibleが所有します。</summary>
    public IReadOnlyList<PlaylistImportItem> Children { get; }
    /// <summary>条件に一致する候補、または一致候補を含む分類であるか。開閉とは独立です。</summary>
    public bool IsVisible => visible;
    /// <summary>候補行であるか。</summary>
    public bool IsLeaf => Candidate != null;
    /// <summary>分類行であるか。</summary>
    public bool IsGroup => !IsLeaf;
    /// <summary>分類の展開状態です。</summary>
    public bool IsExpanded { get => expanded; set { if (expanded == value) { return; } expanded = value; if (expansionKey != null) { owner.StoreExpansion(expansionKey, externalGroup, value); } RaisePropertyChanged(nameof(IsExpanded)); } }
    /// <summary>URI単位で共有される選択状態です。</summary>
    public bool IsSelected { get => Candidate is { } c && owner.IsSelected(c.Uri); set { if (Candidate is { } c) { owner.SetSelected(c, value); } } }
    /// <summary>表示条件に一致する異なるURIの三状態です。</summary>
    public bool? SelectionState => owner.GetSelectionState([this]);
    /// <summary>操作できる候補が存在するか。</summary>
    public bool CanToggleSelection => IsVisible;
    /// <summary>一部選択も全選択へ進め、全選択だけを解除します。</summary>
    internal void ToggleSelection() => owner.ToggleMatching([this]);
    /// <summary>登録済みとの照合結果です。</summary>
    public string Status => Candidate is { } c ? owner.GetStatus(c) : "";
    /// <summary>行を作り直さずに条件一致を末端から更新し、必要なときだけ検索展開を切り替えます。</summary>
    internal bool UpdateVisibility(Func<PlaylistImportCandidate, bool> matches, bool updateExpansion)
    {
        bool changed = false;
        foreach (PlaylistImportItem child in Children) { changed |= child.UpdateVisibility(matches, updateExpansion); }
        bool next = Candidate is { } candidate ? matches(candidate) : Children.Any(child => child.IsVisible);
        if (visible != next)
        {
            visible = next;
            RaisePropertyChanged(nameof(IsVisible));
            changed = true;
        }
        if (changed)
        {
            RaisePropertyChanged(nameof(SelectionState));
            RaisePropertyChanged(nameof(CanToggleSelection));
        }
        if (updateExpansion && expansionKey != null)
        {
            bool nextExpanded = owner.GetExpansion(expansionKey, externalGroup, localizedGroup || Children.Any(child => child.IsGroup));
            if (expanded != nextExpanded) { expanded = nextExpanded; RaisePropertyChanged(nameof(IsExpanded)); }
        }
        return changed;
    }
    /// <summary>共通URIの選択変更をチェックと行背景だけへ反映します。</summary>
    internal void RefreshSelection()
    {
        if (IsLeaf) { RaisePropertyChanged(nameof(IsSelected)); }
        RaisePropertyChanged(nameof(SelectionState));
        foreach (PlaylistImportItem child in Children) { child.RefreshSelection(); }
    }
    /// <summary>照合または言語の更新を表示名と短い状態文言へ反映します。</summary>
    internal void RefreshPresentation(bool cultureChanged)
    {
        if (cultureChanged && (localizedGroup || Candidate?.NameKey != null))
        {
            RaisePropertyChanged(nameof(Name));
            RaisePropertyChanged(nameof(DisplayName));
        }
        if (IsLeaf) { RaisePropertyChanged(nameof(Status)); }
        foreach (PlaylistImportItem child in Children) { child.RefreshPresentation(cultureChanged); }
    }

}

/// <summary>検索とは独立したURI順序集合と、一回のインポート受付を所有する一時セッションです。</summary>
internal sealed class PlaylistImportDialogViewModel : ObservableObject, IDisposable
{
    private readonly Func<IReadOnlyList<Uri>, PlaylistImportStartResult> start;
    private readonly List<PlaylistImportCandidate> selected = [];
    private readonly HashSet<string> selectedKeys = new(StringComparer.Ordinal);
    private readonly HashSet<string> registeredUrls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> expansionStates = new(StringComparer.Ordinal);
    private readonly HashSet<string> registeredNames = new(StringComparer.Ordinal);
    private IReadOnlyList<PlaylistImportCandidate> external = [];
    private string searchText = "";
    private string appliedSearchText = "";
    private string uriInput = "";
    private bool hideRegisteredUrls = true;
    private bool hideSameNames;
    private Func<string> messageProvider = () => "";
    private Action? unsubscribe;
    private bool disposed;
    private PlaylistImportCatalogState catalogState;

    /// <summary>固定候補と共通受付で一時セッションを作成します。</summary>
    internal PlaylistImportDialogViewModel(Func<IReadOnlyList<Uri>, PlaylistImportStartResult> start)
    {
        this.start = start ?? throw new ArgumentNullException(nameof(start));
        BuiltInGroups = BuildGroups(BuiltIns(), true);
        UpdateVisibility(false);
    }
    /// <summary>表示先が所有する購読解除処理を設定します。</summary>
    internal void SetUnsubscribe(Action action) => unsubscribe = action;
    /// <summary>検索文字列。前後空白を除いた一文字列で照合します。</summary>
    public string SearchText
    {
        get => searchText;
        set
        {
            string next = value ?? "";
            if (searchText == next) { return; }
            searchText = next;
            RaisePropertyChanged(nameof(SearchText));
            string term = next.Trim();
            if (appliedSearchText == term) { return; }
            appliedSearchText = term;
            UpdateVisibility(true);
        }
    }
    /// <summary>まだ選択へ追加していない改行区切り入力です。</summary>
    public string UriInput { get => uriInput; set { string next = value ?? ""; if (uriInput == next) { return; } uriInput = next; RaisePropertyChanged(nameof(UriInput)); } }
    /// <summary>登録済みURLと同じ候補を非表示にします。</summary>
    public bool HideRegisteredUrls { get => hideRegisteredUrls; set { if (hideRegisteredUrls == value) { return; } hideRegisteredUrls = value; RaisePropertyChanged(nameof(HideRegisteredUrls)); UpdateVisibility(false); } }
    /// <summary>登録済み名と同じ候補を非表示にします。</summary>
    public bool HideSameNames { get => hideSameNames; set { if (hideSameNames == value) { return; } hideSameNames = value; RaisePropertyChanged(nameof(HideSameNames)); UpdateVisibility(false); } }
    /// <summary>元カタログの外部階層です。条件に一致する行だけを表示します。</summary>
    public IReadOnlyList<PlaylistImportItem> ExternalGroups { get; private set; } = [];
    /// <summary>内蔵7表を二分類で表示します。</summary>
    public IReadOnlyList<PlaylistImportItem> BuiltInGroups { get; private set; } = [];
    /// <summary>外部の現在表示条件に一致する候補の三状態です。</summary>
    public bool? ExternalSelectionState => GetSelectionState(ExternalGroups);
    /// <summary>外部の条件一致候補が存在するか。</summary>
    public bool CanToggleExternalSelection => ExternalGroups.Any(group => group.IsVisible);
    /// <summary>初めて選択した順の全候補です。</summary>
    public ObservableCollection<PlaylistImportItem> SelectedItems { get; } = [];
    /// <summary>選択総数です。</summary>
    public int SelectedCount => selected.Count;
    /// <summary>現在の既存表とURLが一致する選択数です。</summary>
    public int SkippedUrlCount => selected.Count(IsRegistered);
    /// <summary>現在の取得候補数です。</summary>
    public int ExecutableCount => SelectedCount - SkippedUrlCount;
    /// <summary>件数の説明です。</summary>
    public string CountsText => string.Format(R.Playlist_import_counts_format, SelectedCount, ExecutableCount, SkippedUrlCount);
    /// <summary>拒否理由や入力検査の結果です。</summary>
    public string Message => messageProvider();
    private void SetMessage(Func<string> provider) { messageProvider = provider; RaisePropertyChanged(nameof(Message)); }
    /// <summary>カタログの準備・取得・空・失敗を区別した説明です。</summary>
    public string CatalogStatus => catalogState switch { PlaylistImportCatalogState.NotStarted => R.Playlist_import_catalog_not_started, PlaylistImportCatalogState.Loading => R.Playlist_import_catalog_loading, PlaylistImportCatalogState.Failed => R.Playlist_import_catalog_failed, _ => external.Count == 0 ? R.Playlist_import_catalog_empty : "" };
    /// <summary>カタログと状態を値として差し替えます。既存選択は維持します。</summary>
    internal void UpdateCatalog(IReadOnlyList<PlaylistImportCandidate> candidates, PlaylistImportCatalogState state)
    {
        external = candidates;
        catalogState = state;
        ExternalGroups = BuildGroups(candidates, false);
        RaisePropertyChanged(nameof(ExternalGroups));
        RaisePropertyChanged(nameof(CatalogStatus));
        UpdateVisibility(false);
    }
    /// <summary>既存表の固定値を最新化します。</summary>
    internal void UpdateExisting(IReadOnlyList<PlaylistImportExistingTable> tables)
    {
        registeredUrls.Clear(); registeredNames.Clear();
        foreach (PlaylistImportExistingTable table in tables)
        {
            registeredNames.Add(table.Name);
            if (table.PageUrl?.IsAbsoluteUri == true)
            {
                registeredUrls.Add(Key(table.PageUrl));
            }

            if (table.HeaderUrl?.IsAbsoluteUri == true)
            {
                registeredUrls.Add(Key(table.HeaderUrl));
            }
        }
        UpdateVisibility(false);
        RefreshPresentation(false);
        RefreshCounts();
    }
    /// <summary>同定規則に従う選択状態を取得します。</summary>
    internal bool IsSelected(Uri uri) => selectedKeys.Contains(Key(uri));
    /// <summary>候補の選択をURI単位で設定します。</summary>
    internal void SetSelected(PlaylistImportCandidate candidate, bool value)
    {
        string key = Key(candidate.Uri);
        if (value)
        {
            if (!selectedKeys.Add(key))
            {
                return;
            }

            selected.Add(candidate); SelectedItems.Add(new(this, "", candidate));
        }
        else
        {
            if (!selectedKeys.Remove(key))
            {
                return;
            }

            int index = selected.FindIndex(c => Key(c.Uri) == key);
            selected.RemoveAt(index); SelectedItems.RemoveAt(index);
        }
        RefreshSelection();
    }
    private static IEnumerable<PlaylistImportCandidate> MatchingDescendants(IEnumerable<PlaylistImportItem> items)
    {
        foreach (PlaylistImportItem item in items)
        {
            if (!item.IsVisible) { continue; }
            if (item.Candidate is { } candidate) { yield return candidate; }
            else { foreach (PlaylistImportCandidate child in MatchingDescendants(item.Children)) { yield return child; } }
        }
    }
    /// <summary>表示中の異なるURIについて未選択・一部・全選択を返します。空は未選択です。</summary>
    internal bool? GetSelectionState(IEnumerable<PlaylistImportItem> items)
    {
        bool hasSelected = false, hasUnselected = false;
        // 同じURIの状態は全掲載箇所で共通なので、三状態の判定に重複除去は不要です。
        foreach (PlaylistImportCandidate candidate in MatchingDescendants(items))
        {
            if (IsSelected(candidate.Uri)) { hasSelected = true; } else { hasUnselected = true; }
            if (hasSelected && hasUnselected) { return null; }
        }
        return hasSelected;

    }
    /// <summary>条件一致の外部候補を一括操作します。内蔵と非表示候補は保持します。</summary>
    internal void ToggleExternalSelection() => ToggleMatching(ExternalGroups);
    /// <summary>現在の条件一致URIが全選択なら解除し、それ以外では全選択します。</summary>
    internal void ToggleMatching(IEnumerable<PlaylistImportItem> items)
    {
        PlaylistImportCandidate[] targets = MatchingDescendants(items).DistinctBy(c => Key(c.Uri), StringComparer.Ordinal).ToArray();
        if (targets.Length == 0) { return; }
        bool select = targets.Any(c => !IsSelected(c.Uri));
        foreach (PlaylistImportCandidate candidate in targets)
        {
            string key = Key(candidate.Uri);
            if (select)
            {
                if (selectedKeys.Add(key)) { selected.Add(candidate); SelectedItems.Add(new(this, "", candidate)); }
            }
            else if (selectedKeys.Remove(key))
            {
                int index = selected.FindIndex(c => Key(c.Uri) == key);
                selected.RemoveAt(index); SelectedItems.RemoveAt(index);
            }
        }
        RefreshSelection();
    }
    /// <summary>通常時の開閉を分類の安定した識別へ保存します。検索中の外部開閉は一時状態です。</summary>
    internal void StoreExpansion(string key, bool externalGroup, bool expanded)
    {
        if (!externalGroup || appliedSearchText.Length == 0) { expansionStates[key] = expanded; }
    }
    /// <summary>検索一致祖先は展開し、通常表示では保存済みまたは元構造から定めた初期状態を返します。</summary>
    internal bool GetExpansion(string key, bool externalGroup, bool defaultExpanded) => externalGroup && appliedSearchText.Length > 0
        || (expansionStates.TryGetValue(key, out bool expanded) ? expanded : defaultExpanded);
    /// <summary>選択集合を空にします。</summary>
    internal void ClearSelection() { selected.Clear(); selectedKeys.Clear(); SelectedItems.Clear(); RefreshSelection(); }
    /// <summary>有効な絶対URIを選択へ追加し、無効行がある場合は案内と入力原文を残します。</summary>
    internal bool AddUriInput()
    {
        string[] lines = UriInput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) { SetMessage(() => R.Playlist_import_input_empty); return false; }
        List<Uri> uris = [];
        List<string> invalid = [];
        foreach (string line in lines)
        {
            if (Uri.TryCreate(line, UriKind.Absolute, out Uri? uri))
            {
                uris.Add(uri);
            }
            else
            {
                invalid.Add(line);
            }
        }
        foreach (Uri uri in uris)
        {
            SetSelected(new(uri, null), true);
        }

        if (invalid.Count > 0) { SetMessage(() => string.Format(R.Playlist_uri_input_invalid_lines_format, string.Join(Environment.NewLine, invalid))); return false; }
        UriInput = "";
        SetMessage(() => "");
        return true;
    }
    /// <summary>選択順の絶対URIを一度固定し共通受付へ渡します。</summary>
    internal PlaylistImportStartResult Start()
    {
        PlaylistImportStartResult result = start(selected.Select(c => c.Uri).ToArray());
        SetMessage(() => result.Status switch { PlaylistImportStartStatus.Busy => R.Playlist_import_busy, PlaylistImportStartStatus.NotReady => R.Playlist_import_not_ready, PlaylistImportStartStatus.ShuttingDown => R.Playlist_import_shutting_down, PlaylistImportStartStatus.NoTargets => string.Format(R.Playlist_import_no_targets_format, result.SkippedDuplicateUrlCount), _ => "" });
        return result;
    }
    /// <summary>pickerの受理行を既存入力末尾へ追加します。</summary>
    internal void AppendInputLine(string? line)
    {
        string trimmed = (line ?? "").Trim(); if (trimmed.Length > 0)
        {
            UriInput = string.IsNullOrWhiteSpace(UriInput) ? trimmed : UriInput.TrimEnd('\r', '\n') + Environment.NewLine + trimmed;
        }
    }
    /// <summary>照合状態を表示します。同名は実行対象から除きません。</summary>
    internal string GetStatus(PlaylistImportCandidate c) => IsRegistered(c) ? R.Playlist_import_registered_url : HasSameName(c) ? R.Playlist_import_same_name : "";
    /// <summary>現在の言語のリソース値を取得します。</summary>
    internal string Text(string key) => R.ResourceManager.GetString(key, R.Culture) ?? key;
    private static string Key(Uri uri) => uri.AbsoluteUri;
    private bool IsRegistered(PlaylistImportCandidate c) => registeredUrls.Contains(Key(c.Uri));
    private bool HasSameName(PlaylistImportCandidate c) => (c.NameKey is { } key ? Text(key) : c.Name) is { } name && registeredNames.Contains(name);
    private bool Matches(PlaylistImportCandidate c) => appliedSearchText.Length == 0
        || (c.Name?.Contains(appliedSearchText, StringComparison.OrdinalIgnoreCase) ?? false)
        || c.Symbol.Contains(appliedSearchText, StringComparison.OrdinalIgnoreCase)
        || c.Tag1.Contains(appliedSearchText, StringComparison.OrdinalIgnoreCase)
        || c.Tag2.Contains(appliedSearchText, StringComparison.OrdinalIgnoreCase)
        || c.Uri.AbsoluteUri.Contains(appliedSearchText, StringComparison.OrdinalIgnoreCase);
    private void UpdateVisibility(bool updateExpansion)
    {
        bool Visible(PlaylistImportCandidate c) => (!HideRegisteredUrls || !IsRegistered(c)) && (!HideSameNames || !HasSameName(c));
        bool ExternalVisible(PlaylistImportCandidate c) => Matches(c) && Visible(c);
        foreach (PlaylistImportItem item in ExternalGroups) { item.UpdateVisibility(ExternalVisible, updateExpansion); }
        foreach (PlaylistImportItem item in BuiltInGroups) { item.UpdateVisibility(Visible, false); }
        RaisePropertyChanged(nameof(ExternalSelectionState));
        RaisePropertyChanged(nameof(CanToggleExternalSelection));
    }
    private IReadOnlyList<PlaylistImportItem> BuildGroups(IEnumerable<PlaylistImportCandidate> source, bool builtIn) => source.GroupBy(c => c.Tag1).Select(g =>
    {
        // 長さを含めた親子パスにより同名の子分類と区切り文字を含む分類名を区別します。
        string parentKey = (builtIn ? "builtin:" : "external:") + g.Key.Length + ":" + g.Key;
        return new PlaylistImportItem(this, g.Key, children: g.Where(c => string.IsNullOrWhiteSpace(c.Tag2)).Select(c => new PlaylistImportItem(this, "", c))
            .Concat(g.Where(c => !string.IsNullOrWhiteSpace(c.Tag2)).GroupBy(c => c.Tag2).Select(n => new PlaylistImportItem(this, n.Key,
                children: n.Select(c => new PlaylistImportItem(this, "", c)).ToArray(), expansionKey: parentKey + ":" + n.Key.Length + ":" + n.Key, externalGroup: !builtIn))).ToArray(),
            expansionKey: parentKey, externalGroup: !builtIn, localizedGroup: builtIn);
    }).ToArray();
    private IEnumerable<PlaylistImportCandidate> BuiltIns()
    {
        string[] types = ["easy", "normal", "hard", "fc"];
        foreach (string type in types)
        {
            yield return new(new Uri("bmseeker:table.estimation?type=" + type), null, Tag1: "Playlist_import_estimation_group", NameKey: "Insane_estimation_table_" + type);
        }

        string[] uris = ["bmseeker:table.recommended", "bmseeker:table.recommended?base=failed", "bmseeker:table.recommended?failed=noplay"];
        string[] keys = ["Recommended_standard", "Recommended_unplayed_as_failed", "Recommended_failed_as_unplayed"];
        for (int i = 0; i < uris.Length; i++)
        {
            yield return new(new Uri(uris[i]), null, Tag1: "Playlist_import_recommendation_group", NameKey: keys[i]);
        }
    }
    private void RefreshSelection()
    {
        foreach (PlaylistImportItem item in ExternalGroups.Concat(BuiltInGroups).Concat(SelectedItems)) { item.RefreshSelection(); }
        RefreshCounts();
        RaisePropertyChanged(nameof(ExternalSelectionState));
    }
    private void RefreshCounts()
    {
        RaisePropertyChanged(nameof(SelectedCount)); RaisePropertyChanged(nameof(ExecutableCount));
        RaisePropertyChanged(nameof(SkippedUrlCount)); RaisePropertyChanged(nameof(CountsText));
    }
    private void RefreshPresentation(bool cultureChanged)
    {
        foreach (PlaylistImportItem item in ExternalGroups.Concat(BuiltInGroups).Concat(SelectedItems)) { item.RefreshPresentation(cultureChanged); }
    }
    /// <summary>画面寿命の言語通知から表示文言と翻訳名の照合を再評価します。分類行は維持します。</summary>
    internal void RefreshCulture()
    {
        UpdateVisibility(false);
        RefreshPresentation(true);
        RaisePropertyChanged(nameof(CatalogStatus));
        RaisePropertyChanged(nameof(CountsText));
        RaisePropertyChanged(nameof(Message));
    }
    /// <summary>画面専用購読を解除します。開始済みの取込み処理には関与しません。</summary>
    public void Dispose() { if (disposed) { return; } disposed = true; unsubscribe?.Invoke(); unsubscribe = null; }
}
