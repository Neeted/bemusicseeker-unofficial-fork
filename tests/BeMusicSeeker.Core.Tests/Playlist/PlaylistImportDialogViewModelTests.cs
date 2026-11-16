using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.PlaylistImportDialogTestSupport;

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
    [TestMethod]
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
    [TestMethod]
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
    private static IEnumerable<PlaylistImportItem> AllLeaves(IEnumerable<PlaylistImportItem> items) => AllItems(items).Where(x => x.IsLeaf);
    private static IEnumerable<PlaylistImportItem> Leaves(IEnumerable<PlaylistImportItem> items) => AllLeaves(items).Where(x => x.IsVisible);
    private static void AssertMatchingUris(IEnumerable<PlaylistImportCandidate> expected, IEnumerable<PlaylistImportItem> actual) => CollectionAssert.AreEquivalent(
        expected.Select(x => x.Uri.AbsoluteUri).ToArray(), Leaves(actual).Select(x => x.Candidate?.Uri.AbsoluteUri ?? "").ToArray());

}
