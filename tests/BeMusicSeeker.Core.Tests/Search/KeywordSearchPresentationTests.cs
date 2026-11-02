using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class KeywordSearchPresentationTests
{
    /// <summary>memoの非対応画面だけに警告を表示し、履歴で有効なfinalizedは警告へ含めないことを確認し、表示先と異なる項目診断を文言化する誤りを検出します。</summary>
    [TestMethod]
    public void BuildKeywordSearchWarningText_UsesContextSpecificFields()
    {
        string chartListWarning = KeywordSearchPresentationText.BuildWarningText("memo:alpha", GridKeywordSearchContext.ChartList);
        string playlistDetailWarning = KeywordSearchPresentationText.BuildWarningText("memo:alpha", GridKeywordSearchContext.PlaylistDetail);
        string summaryWarning = KeywordSearchPresentationText.BuildWarningText("memo:alpha", GridKeywordSearchContext.PlaylistSummary);
        string playHistoryWarning = KeywordSearchPresentationText.BuildWarningText("finalized:false memo:alpha", GridKeywordSearchContext.PlayHistory);

        StringAssert.Contains(chartListWarning, "memo");
        Assert.AreEqual(string.Empty, playlistDetailWarning);
        StringAssert.Contains(summaryWarning, "memo");
        StringAssert.Contains(playHistoryWarning, "memo");
        Assert.IsFalse(playHistoryWarning.Contains("finalized"));
    }

    /// <summary>空title・否定・OR・壊れた正規表現の各原因を表示警告に含めることを確認し、複数診断の一部を表示時に落とす誤りを検出します。</summary>
    [TestMethod]
    public void BuildKeywordSearchWarningText_ReportsInvalidSyntax()
    {
        string warning = KeywordSearchPresentationText.BuildWarningText("title: - | title:re:[", GridKeywordSearchContext.ChartList);

        StringAssert.Contains(warning, "title");
        StringAssert.Contains(warning, "-");
        StringAssert.Contains(warning, "OR");
        StringAssert.Contains(warning, "[");
    }

    /// <summary>四つの表示先に対応するハッシュ・成績・メモ・記号・日時等をヘルプへ含めることを確認し、表示先の検索項目を案内しない誤りを検出します。</summary>
    [TestMethod]
    public void BuildKeywordSearchHelpText_ContainsContextFields()
    {
        string chartListHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.ChartList);
        string playlistDetailHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlaylistDetail);
        string summaryHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlaylistSummary);
        string playHistoryHelp = KeywordSearchPresentationText.BuildHelpText(GridKeywordSearchContext.PlayHistory);

        StringAssert.Contains(chartListHelp, "sha256");
        StringAssert.Contains(chartListHelp, "clear");
        StringAssert.Contains(chartListHelp, "rate");
        StringAssert.Contains(chartListHelp, "bp");
        StringAssert.Contains(playlistDetailHelp, "memo");
        StringAssert.Contains(playlistDetailHelp, "clear");
        StringAssert.Contains(playlistDetailHelp, "rate");
        StringAssert.Contains(playlistDetailHelp, "bp");
        StringAssert.Contains(summaryHelp, "symbol");
        StringAssert.Contains(playHistoryHelp, "finalized");
        StringAssert.Contains(playHistoryHelp, "date");
    }

    /// <summary>空入力ではお気に入り・履歴・項目を順に表示し、重複履歴と非対応memoを隠して表示先変更で戻すことを確認し、保存式の投影や行操作の可否の誤りを検出します。</summary>
    [TestMethod]
    public void KsaEmptyPresentation_OrdersProjectedFavoritesHistoryAndFieldsByContext()
    {
        PresentationSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(["memo:note", "title:chart", "title:pinned"]),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(["title:pinned", "memo:favorite"])
        };
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(7L, ["Alpha"]));

        KeywordSearchPresentationState state = assistance.Focus(string.Empty, 0);

        Assert.IsTrue(state.IsOpen);
        CollectionAssert.AreEqual(
            new[]
            {
                KeywordSearchPresentationSectionKind.Favorites,
                KeywordSearchPresentationSectionKind.History,
                KeywordSearchPresentationSectionKind.Fields
            },
            state.Sections.Select(section => section.Kind).ToArray());
        Assert.AreEqual(Resources.Keyword_search_completion_favorites_header, state.Sections[0].HeaderText);
        Assert.AreEqual(Resources.Keyword_search_completion_history_header, state.Sections[1].HeaderText);
        Assert.AreEqual(Resources.Keyword_search_completion_fields_header, state.Sections[2].HeaderText);
        CollectionAssert.AreEqual(new[] { "title:pinned" }, state.Sections[0].Items.Select(item => item.Query).ToArray());
        CollectionAssert.AreEqual(new[] { "title:chart" }, state.Sections[1].Items.Select(item => item.Query).ToArray());
        Assert.IsTrue(state.Sections[2].Items.Any(item => item.DisplayText == "title:"));
        Assert.IsFalse(state.Sections[0].Items[0].CanAddFavorite);
        Assert.IsTrue(state.Sections[1].Items[0].CanAddFavorite);
        Assert.IsTrue(state.Sections[1].Items[0].CanDeleteHistory);

        KeywordSearchPresentationState detailState = assistance.UpdateContext(
            GridKeywordSearchContext.PlaylistDetail,
            new KeywordSearchCatalogSnapshot(8L, ["Alpha"]));
        Assert.IsTrue(detailState.Sections.Any(section => section.Items.Any(item => item.Query == "memo:note")));
    }

    /// <summary>お気に入り・履歴各6件と月12件の候補を切り捨てず各区画へスクロール必要を渡すことを確認し、表示容量で候補を消す誤りを検出します。</summary>
    [TestMethod]
    public void KsaPresentationSections_ExposeAllRowsAndMarkIndependentScrollCapacity()
    {
        string[] favorites = Enumerable.Range(1, 6).Select(index => "title:fav" + index).ToArray();
        string[] history = Enumerable.Range(1, 6).Select(index => "title:hist" + index).ToArray();
        PresentationSettingsStore store = new()
        {
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize(history),
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize(favorites)
        };
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            KeywordSearchCatalogSnapshot.Empty);

        KeywordSearchPresentationState emptyState = assistance.Focus(string.Empty, 0);
        KeywordSearchPresentationSection favoriteSection = emptyState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Favorites);
        KeywordSearchPresentationSection historySection = emptyState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.History);

        Assert.AreEqual(6, favoriteSection.Items.Count);
        Assert.IsTrue(favoriteSection.IsScrollable);
        Assert.AreEqual(6, historySection.Items.Count);
        Assert.IsTrue(historySection.IsScrollable);

        assistance.UpdateContext(
            GridKeywordSearchContext.PlayHistory,
            KeywordSearchCatalogSnapshot.Empty);
        KeywordSearchPresentationState valuesState = assistance.Focus("month:", "month:".Length);
        KeywordSearchPresentationSection valuesSection = valuesState.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Values);

        Assert.AreEqual(12, valuesSection.Items.Count);
        Assert.IsTrue(valuesSection.IsScrollable);
    }

    /// <summary>項目の先頭・途中・コロン直前と未完入力を全体置換し、否定・値・前後条件を保ちます。caret左だけの置換による項目やコロンの重複を検出します。</summary>
    [TestMethod]
    [DataRow("-Ti¦", "title:", "-title:¦")]
    [DataRow("¦title:foo", "artist:", "artist:¦foo")]
    [DataRow("title¦:foo", "title:", "title:¦foo")]
    [DataRow("title:before -t¦itle:foo artist:after", "table:", "title:before -table:¦foo artist:after")]
    [DataRow("ti¦tle", "title:", "title:¦")]
    [DataRow("ti¦tle:re:^foo$", "title:", "title:¦re:^foo$")]
    public void KsaFieldPresentation_CompletesCaseInsensitivePrefixAndPreservesNegation(
        string input, string insertion, string expected)
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(KeywordSearchSavedQueryScope.Normal, store, store);
        KeywordSearchAssistanceOwner assistance = new(savedQueries, GridKeywordSearchContext.ChartList);
        int caret = input.IndexOf('¦');
        string text = input.Replace("¦", string.Empty);
        KeywordSearchPresentationItem candidate = assistance.Focus(text, caret).VisibleItems.Single(item => item.InsertionText == insertion);
        KeywordSearchApplyResult result = assistance.TryApply(candidate, text, caret, GridKeywordSearchContext.ChartList, 0L);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(expected.Replace("¦", string.Empty), result.Text);
        Assert.AreEqual(expected.IndexOf('¦'), result.CaretIndex);
    }

    /// <summary>空白入り表名候補を重複除去して引用符付きで適用し、一つの空白の後へカーソルを置いて次の項目候補を出すことを確認し、値補完の境界や引用の誤りを検出します。</summary>
    [TestMethod]
    public void KsaValuePresentation_UsesSnapshotAndAppliesOneAsciiSeparatorWithCaretAfterIt()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(12L, ["Alpha One", "Alpine", "alpha one"]));

        const string text = "playlist:a";
        KeywordSearchPresentationState state = assistance.Focus(text, text.Length);
        KeywordSearchPresentationSection values = state.Sections.Single(
            section => section.Kind == KeywordSearchPresentationSectionKind.Values);
        Assert.AreEqual(Resources.Keyword_search_completion_values_header, values.HeaderText);
        KeywordSearchPresentationItem value = values.Items.Single(item => item.DisplayText == "Alpha One");

        KeywordSearchApplyResult result = assistance.TryApply(
            value,
            text,
            text.Length,
            GridKeywordSearchContext.ChartList,
            12L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("playlist:\"Alpha One\" ", result.Text);
        Assert.AreEqual(result.Text.Length, result.CaretIndex);
        Assert.AreEqual(2, state.VisibleItems.Count);
        KeywordSearchPresentationState nextFields = assistance.Refresh(result.Text, result.CaretIndex);
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, nextFields.Sections.Single().Kind);
    }

    /// <summary>通常値とORの各選択肢を先頭・途中・末尾・引用直前から全体置換します。他の選択肢と後続条件を保持し、途中ORへの空白挿入やsuffixの別語化を検出します。</summary>
    [TestMethod]
    [DataRow("playlist:a¦XYZ", "Alpha", "playlist:Alpha ¦")]
    [DataRow("clear:E¦XH", "EC", "clear:EC ¦")]
    [DataRow("playlist:¦Alpha title:x", "Alpha", "playlist:Alpha ¦title:x")]
    [DataRow("playlist:Alpha¦   title:x", "Alpha", "playlist:Alpha ¦title:x")]
    [DataRow("playlist:¦\"Alpha\" title:x", "Alpine", "playlist:\"Alpine\" ¦title:x")]
    [DataRow("playlist:\"Al¦pha\" title:x", "Alpha", "playlist:\"Alpha\" ¦title:x")]
    [DataRow("clear:\"N¦\" title:x", "NP", "clear:\"NP\" ¦title:x")]
    [DataRow("playlist:Al¦pha|Beta|Gamma title:x", "Alpine", "playlist:Alpine¦|Beta|Gamma title:x")]
    [DataRow("playlist:Alpha¦|Beta|Gamma title:x", "Alpha", "playlist:Alpha¦|Beta|Gamma title:x")]
    [DataRow("playlist:Alpha|B¦eta|Gamma title:x", "Bravo", "playlist:Alpha|Bravo¦|Gamma title:x")]
    [DataRow("playlist:Alpha|Beta¦|Gamma title:x", "Beta", "playlist:Alpha|Beta¦|Gamma title:x")]
    [DataRow("playlist:Alpha|Beta|G¦amma title:x", "Gold", "playlist:Alpha|Beta|Gold ¦title:x")]
    [DataRow("playlist:Alpha|Beta|Gamma¦", "Gamma", "playlist:Alpha|Beta|Gamma ¦")]
    [DataRow("playlist:Alpha|¦\"Beta\"|Gamma title:x", "Bravo", "playlist:Alpha|\"Bravo\"¦|Gamma title:x")]
    [DataRow("playlist:Alpha|\"B¦eta\"|Gamma title:x", "Beta", "playlist:Alpha|\"Beta\"¦|Gamma title:x")]
    [DataRow("playlist:Alpha|¦|Gamma", "Beta", "playlist:Alpha|Beta¦|Gamma")]
    public void KsaValueApply_ReplacesWholeAlternativeAndPreservesSeparators(string input, string value, string expected)
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(KeywordSearchSavedQueryScope.Normal, store, store);
        KeywordSearchAssistanceOwner assistance = new(savedQueries, GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(13L, [value]));
        int caret = input.IndexOf('¦');
        string text = input.Replace("¦", string.Empty);
        KeywordSearchPresentationItem candidate = assistance.Focus(text, caret).VisibleItems.Single(item => item.DisplayText == value);
        KeywordSearchApplyResult result = assistance.TryApply(candidate, text, caret, GridKeywordSearchContext.ChartList, 13L);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(expected.Replace("¦", string.Empty), result.Text);
        Assert.AreEqual(expected.IndexOf('¦'), result.CaretIndex);
    }

    /// <summary>正規表現の値先頭・予約prefix途中・本文中で通常候補を抑止し、caret左だけから通常値と誤判定する経路を検出します。</summary>
    [TestMethod]
    [DataRow("playlist:¦re:^Alpha$")]
    [DataRow("playlist:r¦e:^Alpha$")]
    [DataRow("playlist:re¦:^Alpha$")]
    [DataRow("playlist:re:^¦Alpha$")]
    [DataRow("playlist:re:^Alpha$|¦Beta")]
    public void KsaValuePresentation_RegexSuppressesCandidatesAcrossWholeValue(string input)
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(KeywordSearchSavedQueryScope.Normal, store, store);
        KeywordSearchAssistanceOwner assistance = new(savedQueries, GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(1L, ["Alpha", "re:mix", "Beta"]));
        Assert.IsFalse(assistance.Focus(input.Replace("¦", string.Empty), input.IndexOf('¦')).IsOpen);
    }

    /// <summary>引用されないクリア値を補完した後も後続語を保持し、空白後の項目候補へ移ることを確認し、値候補の状態を次の条件まで持ち越す誤りを検出します。</summary>
    [TestMethod]
    public void KsaValueApply_ExistingUnquotedSuffixTransitionsToFields()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            KeywordSearchCatalogSnapshot.Empty);

        const string text = "clear:N tail";
        int caretIndex = "clear:N".Length;
        KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);
        KeywordSearchPresentationItem candidate = state.VisibleItems.Single(
            item => item.DisplayText == "NP");

        KeywordSearchApplyResult result = assistance.TryApply(
            candidate,
            text,
            caretIndex,
            GridKeywordSearchContext.ChartList,
            0L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("clear:NP tail", result.Text);
        Assert.AreEqual("clear:NP ".Length, result.CaretIndex);

        KeywordSearchPresentationState next = assistance.Refresh(result.Text, result.CaretIndex);
        KeywordSearchPresentationSection fields = next.Sections.Single();
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, fields.Kind);
        Assert.IsTrue(fields.Items.Any(item => item.DisplayText == "title:"));
    }

    /// <summary>空白入り引用表名の内部にカーソルがある間は表名値候補を出して項目候補を出さないことを確認し、引用内空白を条件の区切りとして扱う誤りを検出します。</summary>
    [TestMethod]
    public void KsaValuePresentation_WhitespaceInsideQuotedValueRemainsValueContext()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(16L, ["Alpha One"]));

        const string text = "playlist:\"Alpha One\" tail";
        int caretIndex = "playlist:\"Alpha ".Length;
        KeywordSearchPresentationState state = assistance.Focus(text, caretIndex);

        Assert.AreEqual(KeywordSearchPresentationSectionKind.Values, state.Sections.Single().Kind);
        Assert.IsFalse(state.Sections.Any(section => section.Kind == KeywordSearchPresentationSectionKind.Fields));
        Assert.IsTrue(state.VisibleItems.Any(item => item.DisplayText == "Alpha One"));
    }

    /// <summary>regexお気に入り行の原文全体と末尾カーソルを適用し、反対文字クラスの既存履歴も残すことを確認し、部分置換・大小変換・別式の重複除去を検出します。</summary>
    [TestMethod]
    public void KsaSavedRowApply_ReplacesWholeQueryAndCommitsHistory()
    {
        PresentationSettingsStore store = new()
        {
            KeywordSearchFavorites = KeywordSearchFavoritesStore.Serialize([@"TiTlE:Re:\d"]),
            KeywordSearchHistory = KeywordSearchHistoryStore.Serialize([@"title:re:\D"])
        };
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(4L, []));

        KeywordSearchPresentationState state = assistance.Focus(string.Empty, 0);
        KeywordSearchPresentationItem favorite = state.VisibleItems.Single(
            item => item.Kind == KeywordSearchPresentationItemKind.Favorite);

        KeywordSearchApplyResult result = assistance.TryApply(
            favorite,
            string.Empty,
            0,
            GridKeywordSearchContext.ChartList,
            4L);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(@"TiTlE:Re:\d", result.Text);
        Assert.AreEqual(result.Text.Length, result.CaretIndex);
        CollectionAssert.AreEqual(
            new[] { @"TiTlE:Re:\d", @"title:re:\D" },
            KeywordSearchHistoryStore.Deserialize(store.KeywordSearchHistory).ToArray());
    }

    /// <summary>一覧のou入力にはoutput項目候補だけを表示することを確認し、譜面用の分類値候補を一覧へ流用する誤りを検出します。</summary>
    [TestMethod]
    public void KsaSummaryPresentation_IncludesOutputFieldAndNeverValues()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.PlaylistSummary,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.PlaylistSummary,
            KeywordSearchCatalogSnapshot.Empty);

        KeywordSearchPresentationState state = assistance.Focus("ou", 2);

        Assert.IsTrue(state.IsOpen);
        Assert.AreEqual(1, state.Sections.Count);
        Assert.AreEqual(KeywordSearchPresentationSectionKind.Fields, state.Sections[0].Kind);
        Assert.IsTrue(state.VisibleItems.Any(item => item.DisplayText == "output:"));
        Assert.IsFalse(state.Sections.Any(section => section.Kind == KeywordSearchPresentationSectionKind.Values));
    }

    /// <summary>表名集合の版が変わった後で旧版の候補を適用すると元入力を保持して古い候補として拒否することを確認し、候補形状だけで有効性を判定する誤りを検出します。</summary>
    [TestMethod]
    public void KsaRevisionCandidate_RejectsSameShapeCandidateAfterSnapshotChanges()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(20L, ["Alpha"]));
        const string text = "playlist:a";
        KeywordSearchPresentationItem candidate = assistance.Focus(text, text.Length).VisibleItems.Single();

        assistance.UpdateCatalogSnapshot(new KeywordSearchCatalogSnapshot(21L, ["Beta"]));
        KeywordSearchApplyResult result = assistance.TryApply(
            candidate,
            text,
            text.Length,
            GridKeywordSearchContext.ChartList,
            20L);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, result.Failure);
        Assert.AreEqual(text, result.Text);
    }

    /// <summary>候補生成後に本文・カーソル・表示先のいずれかが違う要求を拒否することを確認し、旧置換範囲を別の入力状態へ適用する誤りを検出します。</summary>
    [TestMethod]
    public void KsaRevisionCandidate_RejectsTextCaretAndContextMismatches()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(22L, ["Alpha"]));
        const string text = "playlist:a";
        KeywordSearchPresentationItem candidate = assistance.Focus(text, text.Length).VisibleItems.Single();

        KeywordSearchApplyResult textResult = assistance.TryApply(
            candidate,
            "playlist:b",
            text.Length,
            GridKeywordSearchContext.ChartList,
            22L);
        KeywordSearchApplyResult caretResult = assistance.TryApply(
            candidate,
            text,
            text.Length - 1,
            GridKeywordSearchContext.ChartList,
            22L);
        KeywordSearchApplyResult contextResult = assistance.TryApply(
            candidate,
            text,
            text.Length,
            GridKeywordSearchContext.PlaylistDetail,
            22L);

        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, textResult.Failure);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, caretResult.Failure);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, contextResult.Failure);
    }

    /// <summary>右側の文字数が変わった入力へ旧候補を適用せず本文とcaretを保持し、再計算後は新しい選択肢全体を置換します。</summary>
    [TestMethod]
    public void KsaRevisionCandidate_RejectsChangedSuffixAndRecalculatesReplacement()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(KeywordSearchSavedQueryScope.Normal, store, store);
        KeywordSearchAssistanceOwner assistance = new(savedQueries, GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(22L, ["Alpha"]));
        int caret = "playlist:a".Length;
        KeywordSearchPresentationItem old = assistance.Focus("playlist:aX|Beta", caret).VisibleItems.Single();
        const string changed = "playlist:aLonger|Beta";
        KeywordSearchApplyResult stale = assistance.TryApply(old, changed, caret, GridKeywordSearchContext.ChartList, 22L);
        Assert.AreEqual(KeywordSearchApplyFailure.StaleCandidate, stale.Failure);
        Assert.AreEqual(changed, stale.Text);
        Assert.AreEqual(caret, stale.CaretIndex);
        KeywordSearchPresentationItem current = assistance.Presentation.VisibleItems.Single();
        KeywordSearchApplyResult result = assistance.TryApply(current, changed, caret, GridKeywordSearchContext.ChartList, 22L);
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual("playlist:Alpha|Beta", result.Text);
        Assert.AreEqual("playlist:Alpha".Length, result.CaretIndex);
    }

    /// <summary>抑制中の同じ状態は再表示せず、本文・カーソル・表示先・版・明示更新で再開しblurは一回だけ通知することを確認し、候補抑制の解除条件や重複通知の誤りを検出します。</summary>
    [TestMethod]
    public void KsaLifecycle_SuppressionReopensOnInputContextCatalogAndForceRefreshAndBlur()
    {
        PresentationSettingsStore store = new();
        KeywordSearchSavedQueryOwner savedQueries = new(
            KeywordSearchSavedQueryScope.Normal,
            store,
            store);
        KeywordSearchAssistanceOwner assistance = new(
            savedQueries,
            GridKeywordSearchContext.ChartList,
            new KeywordSearchCatalogSnapshot(30L, []));

        int changed = 0;
        assistance.PresentationChanged += (_, _) => changed++;
        KeywordSearchPresentationState focused = assistance.Focus("ti", 2);
        Assert.IsTrue(focused.IsOpen);
        Assert.AreEqual(1, changed);

        Assert.IsTrue(assistance.Refresh("ti", 2).IsOpen);
        Assert.AreEqual(1, changed);

        KeywordSearchPresentationState suppressed = assistance.SuppressCurrentSnapshot();
        Assert.IsFalse(suppressed.IsOpen);
        Assert.AreEqual(2, changed);

        Assert.IsFalse(assistance.Refresh("ti", 2).IsOpen);
        Assert.AreEqual(2, changed);

        KeywordSearchPresentationState changedText = assistance.Refresh("tit", 3);
        Assert.IsTrue(changedText.IsOpen);
        Assert.AreEqual(3, changed);

        Assert.IsFalse(assistance.SuppressCurrentSnapshot().IsOpen);
        Assert.AreEqual(4, changed);

        KeywordSearchPresentationState changedCaret = assistance.Refresh("tit", 2);
        Assert.IsTrue(changedCaret.IsOpen);
        Assert.AreEqual(5, changed);

        Assert.IsFalse(assistance.SuppressCurrentSnapshot().IsOpen);
        Assert.AreEqual(6, changed);

        KeywordSearchPresentationState changedContext = assistance.UpdateContext(
            GridKeywordSearchContext.PlaylistDetail,
            new KeywordSearchCatalogSnapshot(30L, []));
        Assert.IsTrue(changedContext.IsOpen);
        Assert.AreEqual(GridKeywordSearchContext.PlaylistDetail, changedContext.Context);
        Assert.AreEqual(7, changed);

        Assert.IsFalse(assistance.SuppressCurrentSnapshot().IsOpen);
        Assert.AreEqual(8, changed);

        KeywordSearchPresentationState changedCatalog = assistance.UpdateCatalogSnapshot(
            new KeywordSearchCatalogSnapshot(31L, ["Alpha"]));
        Assert.IsTrue(changedCatalog.IsOpen);
        Assert.AreEqual(31L, changedCatalog.CatalogRevision);
        Assert.AreEqual(9, changed);

        Assert.IsFalse(assistance.SuppressCurrentSnapshot().IsOpen);
        Assert.AreEqual(10, changed);
        KeywordSearchPresentationState forced = assistance.ForceRefresh();
        Assert.IsTrue(forced.IsOpen);
        Assert.AreEqual(11, changed);

        Assert.IsFalse(assistance.Blur().IsOpen);
        Assert.AreEqual(12, changed);
        Assert.IsFalse(assistance.Blur().IsOpen);
        Assert.AreEqual(12, changed);
    }

    private sealed class PresentationSettingsStore : IKeywordSearchHistorySettingsStore, IKeywordSearchFavoritesSettingsStore
    {
        public string KeywordSearchHistory { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchHistory { get; set; } = string.Empty;

        public string KeywordSearchFavorites { get; set; } = string.Empty;

        public string PlaylistSummaryKeywordSearchFavorites { get; set; } = string.Empty;
    }

}
