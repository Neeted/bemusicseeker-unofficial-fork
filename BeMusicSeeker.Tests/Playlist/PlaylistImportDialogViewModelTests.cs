using System;
using System.Collections.Generic;
using System.Linq;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BeMusicSeeker.Tests;

/// <summary>表示や通信から独立した選択・検索・照合・入力の条件を検証します。</summary>
[TestClass]
public sealed class PlaylistImportDialogViewModelTests
{
    /// <summary>入力元と重複掲載をまたいで正規化URIの初出順を維持し、大小文字の異なるパスを区別します。解除の共有と再追加の末尾移動も確認します。</summary>
    [TestMethod]
    public void Selection_PreservesOrdinalUriOrderAcrossSourcesAndSharesRemoval()
    {
        IReadOnlyList<Uri>? submitted = null;
        using var vm = new PlaylistImportDialogViewModel(uris => { submitted = uris; return new(PlaylistImportStartStatus.Started, uris.Count); });
        var u = new PlaylistImportCandidate(new("https://example.test/A"), "U", Tag1: "one");
        var b = new PlaylistImportCandidate(new("bmseeker:table.estimation?type=easy"), "B");
        var v = new PlaylistImportCandidate(new("file:///C:/tables/V.json"), null);
        PlaylistImportCandidate duplicate = u with { Uri = new("HTTPS://EXAMPLE.TEST:443/A"), Tag1 = "two" };
        PlaylistImportCandidate w = u with { Uri = new("https://example.test/a"), Name = "W" };
        foreach (PlaylistImportCandidate? candidate in new[] { u, b, v, duplicate, w })
        {
            vm.SetSelected(candidate, true);
        }

        Assert.AreEqual(4, vm.SelectedCount);
        vm.Start();
        Assert.IsNotNull(submitted);
        CollectionAssert.AreEqual(new[] { u.Uri.AbsoluteUri, b.Uri.AbsoluteUri, v.Uri.AbsoluteUri, w.Uri.AbsoluteUri }, submitted.Select(x => x.AbsoluteUri).ToArray());
        vm.SetSelected(duplicate, false);
        Assert.IsFalse(vm.IsSelected(u.Uri));
        vm.SetSelected(u, true);
        vm.Start();
        Assert.IsNotNull(submitted);
        CollectionAssert.AreEqual(new[] { b.Uri.AbsoluteUri, v.Uri.AbsoluteUri, w.Uri.AbsoluteUri, u.Uri.AbsoluteUri }, submitted.Select(x => x.AbsoluteUri).ToArray());
        vm.ClearSelection();
        Assert.AreEqual(0, vm.SelectedCount);
        vm.Start();
        Assert.IsNotNull(submitted);
        Assert.AreEqual(0, submitted.Count);
    }

    /// <summary>表名・記号・分類・URLを個別フィールドの一文字列部分一致で照合し、一致URIと祖先展開を確認します。単語分割・フィールド跨ぎはせず、折畳み中も一致候補を選択します。</summary>
    [TestMethod]
    public void Search_FiltersLeavesAndAncestorsWithoutTokenizingAndSelectsCollapsedMatches()
    {
        using PlaylistImportDialogViewModel vm = Create();
        PlaylistImportCandidate plain = C("plain", "Alpha Beta", "★", "Group A", "");
        PlaylistImportCandidate nested = C("nested", "Gamma", "◆", "Group A", "Nested X");
        PlaylistImportCandidate other = C("other", "Other", "", "Group B", "Nested Y");
        vm.UpdateCatalog([plain, nested, other], PlaylistImportCatalogState.Loaded);
        foreach ((string query, PlaylistImportCandidate[] expected) in new (string, PlaylistImportCandidate[])[]
        {
            (" alpha beta ", [plain]), ("★", [plain]), ("group a", [plain, nested]), ("nested x", [nested]),
            ("alpha gamma", []), ("Beta ★", []), ("Group A Nested X", []),
            ("  HTTPS://EXAMPLE.TEST/PLAIN  ", [plain]), ("EXAMPLE.TEST", [plain, nested, other]), (" /NeStEd ", [nested])
        })
        {
            vm.SearchText = query;
            AssertMatchingUris(expected, vm.ExternalGroups);
            Assert.IsTrue(AllItems(vm.ExternalGroups).Where(g => g.IsGroup && g.IsVisible).All(g => g.IsExpanded && Leaves(g.Children).Any()), query);
        }
        vm.SearchText = "Nested X";
        vm.ExternalGroups[0].IsExpanded = false;
        vm.ExternalGroups[0].ToggleSelection();
        Assert.IsTrue(vm.IsSelected(nested.Uri));
        Assert.IsFalse(vm.IsSelected(plain.Uri));
        vm.SearchText = "";
        AssertMatchingUris([plain, nested, other], vm.ExternalGroups);
        Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsLeaf, "tag2なしの葉は第一分類直下に残る。");
    }

    /// <summary>検索・両非表示条件・カタログ差替えで候補が消えても選択と順序を維持します。同URL別分類の再表示は共通選択を示します。</summary>
    [TestMethod]
    public void Selection_SurvivesSearchFiltersAndCatalogReplacement()
    {
        using PlaylistImportDialogViewModel vm = Create();
        PlaylistImportCandidate u = C("u", "N", "", "A", "");
        PlaylistImportCandidate v = C("v", "V", "", "B", "");
        vm.UpdateCatalog([u, v, u with { Tag1 = "C" }], PlaylistImportCatalogState.Loaded);
        vm.SetSelected(u, true);
        vm.SetSelected(v, true);
        vm.UpdateExisting([new("N", u.Uri, null)]);
        vm.SearchText = "absent";
        vm.HideSameNames = true;
        vm.UpdateCatalog([], PlaylistImportCatalogState.Loaded);
        Assert.AreEqual(2, vm.SelectedCount);
        vm.SearchText = "";
        vm.HideSameNames = false;
        vm.HideRegisteredUrls = false;
        vm.UpdateCatalog([u, v, u with { Tag1 = "C" }], PlaylistImportCatalogState.Loaded);
        Assert.IsTrue(Leaves(vm.ExternalGroups).All(x => x.IsSelected));
        CollectionAssert.AreEqual(new[] { "N", "V" }, vm.SelectedItems.Select(x => x.Name).ToArray());
    }

    /// <summary>ページと絶対ヘッダーだけを同URLと判定し、Ordinal同名は注意にとどめます。フィルター変更は実行候補数と受付URIを変えません。</summary>
    [TestMethod]
    public void ExistingSnapshot_SeparatesUrlSkipsNameWarningsAndUnknownNames()
    {
        IReadOnlyList<Uri>? submitted = null;
        using var vm = new PlaylistImportDialogViewModel(uris => { submitted = uris; return new(PlaylistImportStartStatus.Started); });
        PlaylistImportCandidate p = C("p", "P"); PlaylistImportCandidate h = C("h", "H"); PlaylistImportCandidate n = C("n", "N"); PlaylistImportCandidate lower = C("lower", "n");
        var direct = new PlaylistImportCandidate(new("https://example.test/direct"), null);
        vm.UpdateExisting([new("N", p.Uri, h.Uri)]);
        vm.UpdateCatalog([p, h, n, lower], PlaylistImportCatalogState.Loaded);
        Assert.AreEqual(2, Leaves(vm.ExternalGroups).Count());
        foreach (PlaylistImportCandidate? c in new[] { p, h, n, lower, direct })
        {
            vm.SetSelected(c, true);
        }

        Assert.AreEqual(2, vm.SkippedUrlCount);
        Assert.AreEqual(3, vm.ExecutableCount);
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_import_same_name, vm.GetStatus(n));
        Assert.AreEqual("", vm.GetStatus(lower));
        Assert.AreEqual("", vm.GetStatus(direct));
        vm.HideRegisteredUrls = false;
        vm.HideSameNames = true;
        Assert.AreEqual(3, Leaves(vm.ExternalGroups).Count());
        vm.Start();
        Assert.IsNotNull(submitted);
        Assert.AreEqual(5, submitted.Count, "最終URL再照合は共通受付へ委ね、画面は選択全体を渡す。");
        Assert.AreEqual(2, vm.SkippedUrlCount);
    }

    /// <summary>混在入力の有効行だけを既存選択の末尾へ初出順で追加し、無効行の案内と原文を保持します。修正再追加でも既存順とURI重複除去を維持し、開始操作だけが受付を呼びます。</summary>
    [TestMethod]
    public void UriInput_MixedLinesAddValidUrisAndCorrectionPreservesOrderUntilStart()
    {
        int calls = 0;
        IReadOnlyList<Uri>? submitted = null;
        using var vm = new PlaylistImportDialogViewModel(uris => { calls++; submitted = uris; return new(PlaylistImportStartStatus.Started); });
        PlaylistImportCandidate initial = C("initial", "initial");
        var builtIn = new PlaylistImportCandidate(new("bmseeker:table.estimation?type=easy"), "Built-in");
        vm.SetSelected(initial, true);
        vm.SetSelected(builtIn, true);
        string input = "\r\n  HTTPS://EXAMPLE.TEST:443/initial \r\n not a uri \n https://example.test/A \n\t\nalso not a uri\r\n https://example.test/a \nhttps://example.test/A";
        vm.UriInput = input;
        vm.AddUriInput();

        string[] expected = [initial.Uri.AbsoluteUri, builtIn.Uri.AbsoluteUri, "https://example.test/A", "https://example.test/a"];
        CollectionAssert.AreEqual(expected, vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
        Assert.AreEqual(input, vm.UriInput);
        StringAssert.Contains(vm.Message, "not a uri");
        StringAssert.Contains(vm.Message, "also not a uri");
        foreach (string uri in expected) { Assert.IsFalse(vm.Message.Contains(uri, StringComparison.Ordinal)); }
        Assert.IsFalse(vm.Message.Contains("HTTPS://EXAMPLE.TEST:443/initial", StringComparison.Ordinal));
        Assert.AreEqual(0, calls);

        vm.UriInput = "https://example.test/new\nhttps://example.test/a\nHTTPS://EXAMPLE.TEST:443/initial\nhttps://example.test/A";
        vm.AddUriInput();
        string[] corrected = [.. expected, "https://example.test/new"];
        CollectionAssert.AreEqual(corrected, vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
        Assert.AreEqual("", vm.UriInput);
        Assert.AreEqual("", vm.Message);
        vm.UriInput = "https://example.test/new\nhttps://example.test/initial";
        vm.AddUriInput();
        CollectionAssert.AreEqual(corrected, vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
        Assert.AreEqual("", vm.UriInput, "全行が既存選択でも有効な入力は消去します。");
        Assert.AreEqual("", vm.Message);
        Assert.AreEqual(0, calls);
        vm.Start();
        Assert.AreEqual(1, calls);
        Assert.IsNotNull(submitted);
        CollectionAssert.AreEqual(corrected, submitted.Select(uri => uri.AbsoluteUri).ToArray());
    }

    /// <summary>全行が無効なら既存選択と入力原文を保持し、無効行を案内して受付を呼びません。</summary>
    [TestMethod]
    public void UriInput_AllInvalidKeepsInputAndSelectionWithoutStarting()
    {
        int calls = 0;
        using var vm = new PlaylistImportDialogViewModel(_ => { calls++; return new(PlaylistImportStartStatus.Started); });
        PlaylistImportCandidate initial = C("initial", "initial");
        vm.SetSelected(initial, true);
        const string input = " not a uri\r\n\t\nalso not a uri ";
        vm.UriInput = input;
        vm.AddUriInput();
        Assert.AreEqual(input, vm.UriInput);
        CollectionAssert.AreEqual(new[] { initial.Uri.AbsoluteUri }, vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
        StringAssert.Contains(vm.Message, "not a uri");
        StringAssert.Contains(vm.Message, "also not a uri");
        Assert.AreEqual(0, calls);
    }

    /// <summary>空または空白だけの入力は既存選択と原文を保持し、入力案内を表示して受付を呼びません。</summary>
    [DataTestMethod]
    [DataRow("")]
    [DataRow(" \r\n\t")]
    public void UriInput_EmptyKeepsInputAndSelectionWithoutStarting(string input)
    {
        int calls = 0;
        using var vm = new PlaylistImportDialogViewModel(_ => { calls++; return new(PlaylistImportStartStatus.Started); });
        PlaylistImportCandidate initial = C("initial", "initial"); vm.SetSelected(initial, true);
        vm.UriInput = input;
        vm.AddUriInput();
        Assert.AreEqual(input, vm.UriInput);
        Assert.AreEqual(1, vm.SelectedCount);
        Assert.IsTrue(vm.IsSelected(initial.Uri));
        Assert.IsFalse(string.IsNullOrEmpty(vm.Message));
        Assert.AreEqual(0, calls);
    }

    /// <summary>各絶対URI形式を空行と前後空白を除いて同じ取得元の選択へ追加し、全有効入力と案内を消去します。追加だけでは受付を呼びません。</summary>
    [DataTestMethod]
    [DataRow("https://example.test/table.json", "https://example.test/table.json")]
    [DataRow("bmseeker:table.recommended?base=failed", "bmseeker:table.recommended?base=failed")]
    [DataRow("custom-scheme:table", "custom-scheme:table")]
    [DataRow("file:///C:/tables/local.json", "file:///C:/tables/local.json")]
    [DataRow(@"C:\tables\local.json", "file:///C:/tables/local.json")]
    [DataRow(@"\\server\tables\local.json", "file://server/tables/local.json")]
    public void UriInput_AllAbsoluteSchemesAddAndClear(string input, string expectedUri)
    {
        int calls = 0;
        using var vm = new PlaylistImportDialogViewModel(_ => { calls++; return new(PlaylistImportStartStatus.Started); });
        vm.UriInput = "\n  " + input + " \r\n";
        vm.AddUriInput();
        Assert.AreEqual("", vm.UriInput);
        Assert.AreEqual("", vm.Message);
        Assert.AreEqual(1, vm.SelectedCount);
        PlaylistImportCandidate? candidate = vm.SelectedItems.Single().Candidate;
        Assert.IsNotNull(candidate);
        Assert.AreEqual(expectedUri, candidate.Uri.AbsoluteUri);
        Assert.AreEqual(0, calls);
    }

    /// <summary>内蔵の難度推定4種と推薦3種のURIをそのまま共通選択へ渡します。</summary>
    [TestMethod]
    public void BuiltIns_ContainAllSevenCompatibleUrisInTwoGroups()
    {
        using PlaylistImportDialogViewModel vm = Create();
        Assert.AreEqual(2, vm.BuiltInGroups.Count);
        List<string> builtInUris = [];
        foreach (PlaylistImportItem leaf in AllLeaves(vm.BuiltInGroups))
        {
            PlaylistImportCandidate? candidate = leaf.Candidate;
            Assert.IsNotNull(candidate);
            builtInUris.Add(candidate.Uri.AbsoluteUri);
        }
        CollectionAssert.AreEqual(new[] { "bmseeker:table.estimation?type=easy", "bmseeker:table.estimation?type=normal", "bmseeker:table.estimation?type=hard", "bmseeker:table.estimation?type=fc", "bmseeker:table.recommended", "bmseeker:table.recommended?base=failed", "bmseeker:table.recommended?failed=noplay" }, builtInUris.ToArray());
    }

    /// <summary>重複URIを一度だけ数え、中間から全選択、全選択から解除します。非表示と別入力元の選択、折畳み状態は操作対象を広げません。</summary>
    [TestMethod]
    public void AggregateSelection_UsesDistinctVisibleUrisAndPreservesOtherSources()
    {
        using PlaylistImportDialogViewModel vm = Create();
        PlaylistImportCandidate a = C("a", "A", tag1: "Parent");
        PlaylistImportCandidate b = C("b", "B", tag1: "Parent", tag2: "Child");
        PlaylistImportCandidate hidden = C("hidden", "Hidden", tag1: "Parent");
        PlaylistImportCandidate separate = new(new("file:///C:/other.json"), null);
        vm.UpdateExisting([new("Existing", hidden.Uri, null)]);
        vm.UpdateCatalog([a, a with { Tag2 = "Child" }, b, hidden], PlaylistImportCatalogState.Loaded);
        vm.SetSelected(hidden, true); vm.SetSelected(separate, true);
        PlaylistImportItem parent = vm.ExternalGroups.Single();
        Assert.AreEqual(false, parent.SelectionState);
        vm.SetSelected(a, true);
        Assert.IsNull(parent.SelectionState);
        Assert.IsNull(vm.ExternalSelectionState);
        Assert.IsTrue(Leaves([parent]).Where(x => x.Candidate?.Uri == a.Uri).All(x => x.IsSelected));
        parent.IsExpanded = false;
        parent.ToggleSelection();
        Assert.AreEqual(true, parent.SelectionState);
        Assert.AreEqual(4, vm.SelectedCount);
        Assert.IsTrue(Leaves([parent]).All(x => x.IsSelected));
        vm.ToggleExternalSelection();
        Assert.AreEqual(false, parent.SelectionState);
        Assert.IsTrue(vm.IsSelected(hidden.Uri)); Assert.IsTrue(vm.IsSelected(separate.Uri));
        Assert.IsFalse(vm.IsSelected(a.Uri)); Assert.IsFalse(vm.IsSelected(b.Uri));
        PlaylistImportItem builtIn = vm.BuiltInGroups[0];
        builtIn.ToggleSelection();
        Assert.AreEqual(true, builtIn.SelectionState);
        vm.ToggleExternalSelection();
        Assert.AreEqual(true, vm.ExternalSelectionState);
        Assert.AreEqual(true, builtIn.SelectionState, "外部全体は内蔵に影響しない。");
        vm.SearchText = "no matching candidate";
        Assert.AreEqual(false, vm.ExternalSelectionState);
        Assert.IsFalse(vm.CanToggleExternalSelection);
        Assert.IsFalse(parent.IsVisible, "全子孫が条件外の分類も隠す。");
        Assert.AreEqual(false, parent.SelectionState);
        Assert.IsFalse(parent.CanToggleSelection);
        parent.ToggleSelection();
        int count = vm.SelectedCount;
        vm.ToggleExternalSelection();
        Assert.AreEqual(count, vm.SelectedCount);
        builtIn.ToggleSelection();
        Assert.AreEqual(false, builtIn.SelectionState);
    }

    /// <summary>外部と内蔵のURL・同名・両方一致・通常候補へ両フィルターを適用し、選択・件数・受付は維持します。外部検索は内蔵に適用しません。</summary>
    [TestMethod]
    public void Filters_ApplyToBothCatalogsWithoutChangingSelectionOrSubmission()
    {
        IReadOnlyList<Uri>? submitted = null;
        using var vm = new PlaylistImportDialogViewModel(uris => { submitted = uris; return new(PlaylistImportStartStatus.Busy); });
        PlaylistImportCandidate[] builtIn = AllLeaves(vm.BuiltInGroups).Select(x => x.Candidate ?? throw new AssertFailedException("内蔵葉に候補が必要です。")).ToArray();
        PlaylistImportCandidate[] external = [C("url", "URL"), C("name", "Name"), C("both", "Both"), C("normal", "Normal")];
        vm.UpdateCatalog(external, PlaylistImportCatalogState.Loaded);
        vm.UpdateExisting([new("Name", external[0].Uri, null), new("Both", external[2].Uri, null),
            new(vm.Text(builtIn[1].NameKey ?? ""), builtIn[0].Uri, null), new(vm.Text(builtIn[2].NameKey ?? ""), builtIn[2].Uri, null)]);
        foreach (PlaylistImportCandidate candidate in external.Concat(builtIn.Take(4))) { vm.SetSelected(candidate, true); }
        vm.UriInput = "https://example.test/direct\nfile:///C:/direct.json";
        vm.AddUriInput();
        string[] orderedSelection = vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray();
        int selected = vm.SelectedCount, skipped = vm.SkippedUrlCount, executable = vm.ExecutableCount;
        foreach ((bool urls, bool names, int[] indices) in new (bool, bool, int[])[]
        {
            (false, false, [0, 1, 2, 3]), (true, false, [1, 3]), (false, true, [0, 3]), (true, true, [3])
        })
        {
            vm.HideRegisteredUrls = urls; vm.HideSameNames = names;
            PlaylistImportCandidate[] expectedBuiltIn = indices.Select(i => builtIn[i]).Concat(builtIn.Skip(4)).ToArray();
            AssertMatchingUris(indices.Select(i => external[i]), vm.ExternalGroups);
            AssertMatchingUris(expectedBuiltIn, vm.BuiltInGroups);
            Assert.AreEqual(selected, vm.SelectedCount); Assert.AreEqual(skipped, vm.SkippedUrlCount); Assert.AreEqual(executable, vm.ExecutableCount);
            CollectionAssert.AreEqual(orderedSelection, vm.SelectedItems.Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
            vm.SearchText = "no external matches";
            Assert.IsFalse(vm.ExternalGroups.Any(x => x.IsVisible));
            AssertMatchingUris(expectedBuiltIn, vm.BuiltInGroups);
            vm.SearchText = "";
        }
        vm.Start(); Assert.IsNotNull(submitted);
        CollectionAssert.AreEqual(orderedSelection, submitted.Select(x => x.AbsoluteUri).ToArray());
        Assert.AreEqual(BeMusicSeeker.Properties.Resources.Playlist_import_registered_url, vm.GetStatus(external[2]), "URL一致を同名より優先する。");
    }

    /// <summary>空白だけの第二分類は直下の葉として扱い、非空の分類名は前後空白を保持します。元構造の子分類を持つ親だけを初期展開し、通常開閉を非表示・カタログ・検索解除・実翻訳変更後にも復元します。</summary>
    [TestMethod]
    [DoNotParallelize]
    public void Expansion_RestoresNormalStateAfterFiltersSearchAndCultureRefresh()
    {
        System.Globalization.CultureInfo? previous = BeMusicSeeker.Properties.Resources.Culture;
        try
        {
            BeMusicSeeker.Properties.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("ja-JP");
            using PlaylistImportDialogViewModel vm = Create();
            PlaylistImportCandidate a = C("a", "Alpha", tag1: "A", tag2: "Same child");
            PlaylistImportCandidate b = C("b", "Beta", tag1: "B", tag2: "Same child");
            PlaylistImportCandidate leafOnly = C("leaf", "Leaf only", tag1: "Leaf parent", tag2: " \t\u3000");
            PlaylistImportCandidate mixedDirect = C("mixed-direct", "Direct", tag1: "Mixed", tag2: " \t\u3000");
            const string nestedName = " \tChild\u3000 ";
            PlaylistImportCandidate mixedChild = C("mixed-child", "Nested", tag1: "Mixed", tag2: nestedName);
            PlaylistImportCandidate[] catalog = [a, b, leafOnly, mixedDirect, mixedChild];
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            Assert.IsTrue(vm.ExternalGroups.Where(x => x.Name is "A" or "B" or "Mixed").All(x => x.IsExpanded));
            PlaylistImportItem leafParent = vm.ExternalGroups.Single(x => x.Name == "Leaf parent");
            Assert.IsFalse(leafParent.IsExpanded);
            Assert.IsTrue(leafParent.Children.Single().IsLeaf);
            Assert.AreEqual(leafOnly.Uri.AbsoluteUri, leafParent.Children.Single().Candidate?.Uri.AbsoluteUri);
            PlaylistImportItem initialMixed = vm.ExternalGroups.Single(x => x.Name == "Mixed");
            Assert.AreEqual(mixedDirect.Uri.AbsoluteUri, initialMixed.Children.Single(x => x.IsLeaf).Candidate?.Uri.AbsoluteUri);
            PlaylistImportItem nested = initialMixed.Children.Single(x => x.IsGroup);
            Assert.AreEqual(nestedName, nested.Name);
            Assert.AreEqual(mixedChild.Uri.AbsoluteUri, nested.Children.Single().Candidate?.Uri.AbsoluteUri);
            Assert.IsTrue(AllItems(vm.ExternalGroups).Where(x => x.IsGroup).All(x => !string.IsNullOrWhiteSpace(x.Name)), "空白名の子分類を生成しません。");
            Assert.IsTrue(AllItems(vm.ExternalGroups).Where(x => x.IsGroup && (x.Name == "Same child" || x.Name == nestedName)).All(x => !x.IsExpanded));
            Assert.IsTrue(vm.BuiltInGroups.All(x => x.IsExpanded));
            string japaneseCategoryName = vm.BuiltInGroups[0].Name;
            Assert.AreEqual("難度推定表", japaneseCategoryName, "初期表示は実際の日本語訳を使う。");
            Uri categoryFirstUri = vm.BuiltInGroups[0].Children[0].Candidate?.Uri ?? throw new AssertFailedException("内蔵カテゴリの候補が必要です。");
            // 既定と反対へ操作し、開く・閉じるの両方向の保持を確認します。
            vm.ExternalGroups[0].IsExpanded = false; vm.ExternalGroups[0].Children[0].IsExpanded = true;
            vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded = true;
            vm.BuiltInGroups[0].IsExpanded = false;
            vm.UpdateExisting([new("Registered A", a.Uri, null), new("Registered child", mixedChild.Uri, null)]);
            Assert.IsFalse(vm.ExternalGroups[0].IsVisible);
            PlaylistImportItem mixed = vm.ExternalGroups.Single(x => x.Name == "Mixed");
            Assert.IsTrue(mixed.IsVisible && mixed.IsExpanded, "子分類が全部隠れても元構造の既定展開を維持する。");
            Assert.IsFalse(mixed.Children.Single(x => x.IsGroup).IsVisible);
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Mixed").IsExpanded, "フィルター中のカタログ更新も元構造で既定を決める。");
            vm.HideRegisteredUrls = false;
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsFalse(vm.ExternalGroups[1].Children[0].IsExpanded, "別親の同名分類を混同しない。");
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded);
            vm.UpdateCatalog(catalog, PlaylistImportCatalogState.Loaded);
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded);
            vm.SearchText = "Alpha";
            Assert.IsTrue(vm.ExternalGroups[0].IsExpanded && vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsFalse(vm.BuiltInGroups[0].IsExpanded);
            vm.ExternalGroups[0].Children[0].IsExpanded = false;
            vm.SearchText = "";
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            BeMusicSeeker.Properties.Resources.Culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            vm.RefreshCulture();
            Assert.AreEqual("Difficulty estimation", vm.BuiltInGroups[0].Name, "英語JSONが未配置で日本語へフォールバックしても成功しない。");
            Assert.AreNotEqual(japaneseCategoryName, vm.BuiltInGroups[0].Name, "カテゴリの翻訳名が実際に変化する。");
            Assert.AreEqual(categoryFirstUri, vm.BuiltInGroups[0].Children[0].Candidate?.Uri, "翻訳後も同じ内蔵カテゴリを観測する。");
            Assert.IsFalse(vm.BuiltInGroups[0].IsExpanded);
            Assert.IsTrue(vm.BuiltInGroups[1].IsExpanded);
            Assert.IsFalse(vm.ExternalGroups[0].IsExpanded); Assert.IsTrue(vm.ExternalGroups[0].Children[0].IsExpanded);
            Assert.IsTrue(vm.ExternalGroups.Single(x => x.Name == "Leaf parent").IsExpanded);
        }
        finally { BeMusicSeeker.Properties.Resources.Culture = previous; }
    }

    private static PlaylistImportDialogViewModel Create() => new(_ => new(PlaylistImportStartStatus.Busy));
    private static PlaylistImportCandidate C(string path, string name, string symbol = "", string tag1 = "A", string tag2 = "") => new(new("https://example.test/" + path), name, symbol, tag1, tag2);
    private static IEnumerable<PlaylistImportItem> AllItems(IEnumerable<PlaylistImportItem> items) => items.SelectMany(x => new[] { x }.Concat(AllItems(x.Children)));
    private static IEnumerable<PlaylistImportItem> AllLeaves(IEnumerable<PlaylistImportItem> items) => AllItems(items).Where(x => x.IsLeaf);
    private static IEnumerable<PlaylistImportItem> Leaves(IEnumerable<PlaylistImportItem> items) => AllLeaves(items).Where(x => x.IsVisible);
    private static void AssertMatchingUris(IEnumerable<PlaylistImportCandidate> expected, IEnumerable<PlaylistImportItem> actual) => CollectionAssert.AreEquivalent(
        expected.Select(x => x.Uri.AbsoluteUri).ToArray(), Leaves(actual).Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());
}
