using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.AudioDuplicateCandidateTestSupport;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 重複音源の整理候補一覧の管理主体（<see cref="AudioDuplicateCandidateListOwner"/>）の状態と判断（状態の引継ぎ、STATUSとCONFLICTSの値、並べ替え、検索・除外、件数、選択の操作、
/// 整理の対象の捕捉、衝突整理の寿命）を、メモリ上の結果で確認します。重複・分裂配置の一覧とは別の型で、状態を独立に持ちます。
/// </summary>
[TestClass]
public sealed class AudioDuplicateCandidateListOwnerTests
{
    private static AudioDuplicateCandidateListOwner CreateOwner() => new(_ => true, _ => { });

    private static string[] Names(AudioDuplicateCandidateListOwner owner) => [.. owner.RowSet.Select(row => row.Path)];

    /// <summary>
    /// 保証：初期値は未確認・SELECTオフ・CONFLICTSは探索時点の衝突名の数で、行は探索結果の順に一フォルダ一行になる。確認結果の書込みで、安全と要注意のCONFLICTSは確認が確定した数に、
    /// 整理不可は探索時点の数のままになり、書き込んだフォルダのSELECTは段階によらずオフになる。整理不可はSELECTを操作できない。
    /// 検出する誤り：未確認の既定値の誤り、確認で確定した数の不反映・整理不可への反映、SELECTの消費漏れ、整理不可のSELECT可。
    /// </summary>
    [TestMethod]
    public void Adopt_InitialValues_AndVerificationWrites_FollowTheContract()
    {
        object library = new object();
        AudioDuplicateFolder a = Folder(@"C:\S\a", "x", "y", "z"), b = Folder(@"C:\S\b", "x", "y"), c = Folder(@"C:\S\c", "x", "y");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [a, b, c]);

        CollectionAssert.AreEqual(new[] { @"C:\S\a", @"C:\S\b", @"C:\S\c" }, Names(owner));
        Assert.IsTrue(owner.IsViewActive);
        Assert.IsTrue(owner.RowSet.All(row => !row.State.IsSelected && row.State.Inspection == null));
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_unverified, owner.RowSet[0].StatusText);
        CollectionAssert.AreEqual(new[] { "3", "2", "2" }, owner.RowSet.Select(row => row.ConflictsText).ToArray());
        Assert.IsNull(owner.RowSet[0].StatusTooltipText, "未確認はツールチップなし");

        owner.SelectVisible();
        Assert.AreEqual(3, owner.SelectedGroupCount);
        owner.WriteVerifications(
        [
            new AudioDuplicateVerificationWrite(a.Group, Inspection(AudioDuplicateInspectionStage.Safe, ["x"])),
            new AudioDuplicateVerificationWrite(b.Group, Inspection(AudioDuplicateInspectionStage.Caution, ["x"], [])),
            new AudioDuplicateVerificationWrite(c.Group, Unorganizable(DuplicateMergeVerificationReasonKind.Link, c.Folder))
        ]);

        CollectionAssert.AreEqual(new[] { "1", "1", "2" }, owner.RowSet.Select(row => row.ConflictsText).ToArray(), "安全・要注意は確定した数、整理不可は探索時点の数");
        Assert.IsTrue(owner.RowSet.All(row => !row.State.IsSelected), "確認を終えたフォルダのSELECTは段階によらずオフ");
        Assert.AreEqual(0, owner.SelectedGroupCount);
        Assert.IsFalse(owner.ToggleSelect(owner.RowSet[2]), "整理不可はSELECTを操作できません。");
        Assert.IsTrue(owner.ToggleSelect(owner.RowSet[0]));
        Assert.AreEqual(1, owner.SelectedGroupCount);
    }

    /// <summary>
    /// 保証：STATUSの文言は、未確認・安全（全名が同等なら同等の付記、衝突名0件は付記なし）・要注意（非同等）・整理不可（リンク／解析不能の理由）で、
    /// ツールチップは要注意で同等でない衝突名を昇順に10件まで出して超過分を「ほか{n}件」に省略し、同等と判定した名前があれば末尾に同等の組数を、整理不可では理由の説明と該当のパスを出す。
    /// 診断を持たない確認結果（<c>Inspection</c> の既定）では、名前の行は理由を添えない「- 名前」のままで、内訳・音質の目安の行も出さない（診断を持つ場合は別のケースが確認する）。
    /// 検出する誤り：段階・理由の文言の取り違え、10件を超える名前の無制限表示、同等の文の欠落、整理不可の該当パスの欠落、診断を持たない結果への内訳の混入。
    /// </summary>
    [TestMethod]
    public void StatusText_AndTooltip_FollowTheStage()
    {
        string equivalentNote = string.Format(Resources.Duplicate_merge_candidate_status_note_format, Resources.Duplicate_merge_candidate_status_safe, Resources.Duplicate_merge_candidate_status_audio_equivalent);
        Assert.AreEqual(equivalentNote, AudioDuplicateStatusPresentation.FormatStatus(Inspection(AudioDuplicateInspectionStage.Safe, ["x"])));
        Assert.AreEqual(Resources.Duplicate_merge_candidate_status_safe, AudioDuplicateStatusPresentation.FormatStatus(Inspection(AudioDuplicateInspectionStage.Safe, [])));
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Duplicate_merge_candidate_status_caution, Resources.Audio_duplicate_candidate_reason_non_equivalent),
            AudioDuplicateStatusPresentation.FormatStatus(Inspection(AudioDuplicateInspectionStage.Caution, ["x"], [])));
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Audio_duplicate_candidate_status_unorganizable, Resources.Audio_duplicate_candidate_reason_link),
            AudioDuplicateStatusPresentation.FormatStatus(Unorganizable(DuplicateMergeVerificationReasonKind.Link, @"C:\S\a")));
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_status_reasons_format, Resources.Audio_duplicate_candidate_status_unorganizable, Resources.Audio_duplicate_candidate_reason_unreadable),
            AudioDuplicateStatusPresentation.FormatStatus(Unorganizable(DuplicateMergeVerificationReasonKind.Unreadable, @"C:\S\a", "sub")));

        string[] many = [.. Enumerable.Range(1, 12).Select(index => "n" + (13 - index).ToString("00"))];
        string tooltip = AudioDuplicateStatusPresentation.FormatTooltip(Inspection(AudioDuplicateInspectionStage.Caution, [.. many, "good"], ["good"]), @"C:\S\a")!;
        string[] lines = tooltip.Split(Environment.NewLine);
        Assert.AreEqual("- n01", lines[1], "同等でない名前は昇順。診断を持たない結果は理由を添えず、内訳の行も出さない");
        Assert.AreEqual("- n10", lines[10]);
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_tooltip_more_format, 2), lines[11], "10件を超える分は省略");
        StringAssert.EndsWith(tooltip, string.Format(Resources.Duplicate_merge_candidate_tooltip_audio_equivalent_format, 1));
        StringAssert.Contains(tooltip, Resources.Audio_duplicate_candidate_non_equivalent_description);

        string unorganizable = AudioDuplicateStatusPresentation.FormatTooltip(Unorganizable(DuplicateMergeVerificationReasonKind.Unreadable, @"C:\S\a", "sub"), @"C:\S\a")!;
        StringAssert.Contains(unorganizable, Resources.Duplicate_merge_candidate_reason_unreadable_description);
        StringAssert.Contains(unorganizable, @"C:\S\a\sub");
        Assert.IsNull(AudioDuplicateStatusPresentation.FormatTooltip(null, @"C:\S\a"));
    }

    /// <summary>
    /// 保証：探索し直しで同じインスタンスのフォルダは状態（SELECT・確認結果）を保ち、新しいインスタンスは初期値になり、結果から消えたフォルダの状態は破棄される（再び現れても初期値）。
    /// 結果がない間（ShowNoResult）は行を空にして表示中でなくし、状態を捨てず、次の採用で引き継ぐ。ライブラリの交換ですべて破棄し、衝突整理・検索語は保つ。
    /// 検出する誤り：状態の取りこぼし・取り違え、結果なしでの状態の破棄、消えたフォルダの状態の残存、交換後の状態の引継ぎ、衝突整理・検索語の消失。
    /// </summary>
    [TestMethod]
    public void State_FollowsInstancesAcrossAdoptNoResultAndLibraryExchange()
    {
        object library = new object();
        AudioDuplicateFolder a = Folder(@"C:\S\a"), b = Folder(@"C:\S\b");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [a, b]);
        owner.CollisionCleanupMode = AudioDuplicateCollisionCleanupMode.KeepWav;
        Assert.AreEqual(DuplicateOrganizeDeleteMethod.MoveToRecycleBin, owner.DeleteMethod, "削除の既定はごみ箱へ移動");
        var changed = new List<string?>();
        owner.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        owner.DeleteMethod = DuplicateOrganizeDeleteMethod.MoveToRecycleBin;
        owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
        owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
        Assert.AreEqual(1, changed.Count(name => name == nameof(AudioDuplicateCandidateListOwner.DeleteMethod)), "値が変わったときだけ通知する");
        owner.SearchText = "path:S";
        Verify(owner, a, Inspection(AudioDuplicateInspectionStage.Caution, ["x"], []));
        owner.ToggleSelect(owner.RowSet.Single(row => row.Path == a.Folder));

        Assert.IsTrue(owner.ShowNoResult(library));
        Assert.AreEqual(0, owner.RowSet.Count);
        Assert.IsFalse(owner.IsViewActive);
        Assert.IsFalse(owner.ShowNoResult(new object()), "別のライブラリには作用しません。");

        AudioDuplicateFolder newB = Folder(@"C:\S\b");
        owner.SetTreeSelected(true);
        Assert.IsTrue(owner.Adopt(AudioDuplicateCandidateListOwner.Prepare(library, [a, newB], new MemorySource(), CancellationToken.None)));
        Assert.IsTrue(State(owner, a).IsSelected && State(owner, a).Inspection != null, "同じインスタンスは状態を保つ");
        Assert.IsFalse(State(owner, newB).IsSelected || State(owner, newB).Inspection != null, "新しいインスタンスは初期値");
        Assert.IsFalse(owner.TryGetState(b.Group, out _), "結果から消えたフォルダの状態は破棄");

        Assert.IsTrue(owner.Adopt(AudioDuplicateCandidateListOwner.Prepare(library, [newB], new MemorySource(), CancellationToken.None)));
        Assert.IsTrue(owner.Adopt(AudioDuplicateCandidateListOwner.Prepare(library, [a, newB], new MemorySource(), CancellationToken.None)));
        Assert.IsNull(State(owner, a).Inspection, "消えた後に再び現れても初期値");

        owner.AttachLibrary(new object());
        Assert.AreEqual(0, owner.RowSet.Count);
        Assert.IsFalse(owner.TryGetState(a.Group, out _), "ライブラリの交換ですべて破棄");
        Assert.AreEqual(AudioDuplicateCollisionCleanupMode.KeepWav, owner.CollisionCleanupMode, "衝突整理は交換後も保つ");
        Assert.AreEqual(DuplicateOrganizeDeleteMethod.DeletePermanently, owner.DeleteMethod, "削除は交換後も保つ");
        Assert.AreEqual("path:S", owner.SearchText);
    }

    /// <summary>保証：衝突整理の既定は「OGGを残す」で、変更しても案内などの副作用を持たない。検出する誤り：既定値の誤り。</summary>
    [TestMethod]
    public void CollisionCleanupMode_DefaultsToKeepOgg()
        => Assert.AreEqual(AudioDuplicateCollisionCleanupMode.KeepOgg, CreateOwner().CollisionCleanupMode);

    /// <summary>
    /// 保証：並べ替えはSELECT（オフが先）・STATUS（表示文言）・CONFLICTS（数値で、辞書順にならない）・PATHに従い、同値は探索結果の順を保つ。
    /// 検索（path/folder/title/artist/status）と除外は行単位に当たり、表示中の件数と選択件数（表示中のうちSELECTがオン）に反映され、除外で隠れたフォルダの状態は保たれる。
    /// 検出する誤り：CONFLICTSの辞書順比較、同値の順の崩れ、検索語の欄の取り違え、非表示のフォルダを件数に数える、除外での状態の破棄。
    /// </summary>
    [TestMethod]
    public void SortSearchExcludeAndCounts_ApplyToRows()
    {
        object library = new object();
        AudioDuplicateFolder a = Folder(@"C:\S\alpha", "1", "2", "3", "4", "5", "6", "7", "8", "9", "10"), b = Folder(@"C:\S\beta", "1", "2"), c = Folder(@"C:\S\gamma", "1", "2");
        var pairs = new Dictionary<string, (string Title, string Artist)>(StringComparer.OrdinalIgnoreCase)
        {
            [a.Folder] = ("Song A", "Zed"),
            [b.Folder] = ("Song B", "Amy"),
            [c.Folder] = ("Other", "Amy")
        };
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [a, b, c], new MemorySource(pairs));

        owner.RequestSort("Conflicts", ListSortDirection.Ascending);
        CollectionAssert.AreEqual(new[] { b.Folder, c.Folder, a.Folder }, Names(owner), "CONFLICTSは数値で、同値は結果順");
        owner.RequestSort("Conflicts", ListSortDirection.Descending);
        Assert.AreEqual(a.Folder, owner.RowSet[0].Path);

        owner.ToggleSelect(owner.RowSet.Single(row => row.Path == c.Folder));
        owner.RequestSort("Conflicts", ListSortDirection.Ascending);
        Assert.AreEqual(c.Folder, owner.RowSet[1].Path);
        owner.RequestSort("Select", ListSortDirection.Ascending);
        Assert.AreEqual(c.Folder, owner.RowSet[2].Path, "SELECTはオフが先");

        owner.RequestSort("Path", ListSortDirection.Ascending);
        owner.SearchText = "artist:Amy";
        CollectionAssert.AreEqual(new[] { b.Folder, c.Folder }, Names(owner));
        Assert.AreEqual(2, owner.VisibleGroupCount);
        Assert.AreEqual(1, owner.SelectedGroupCount);
        owner.ExcludeText = "title:Other";
        CollectionAssert.AreEqual(new[] { b.Folder }, Names(owner));
        Assert.AreEqual(0, owner.SelectedGroupCount, "非表示のフォルダは選択件数に数えません。");
        Assert.IsTrue(State(owner, c).IsSelected, "除外で隠れたフォルダの状態は保つ");
        owner.SearchText = string.Empty;
        owner.ExcludeText = string.Empty;
        Assert.AreEqual(3, owner.VisibleGroupCount);
        Assert.AreEqual(1, owner.SelectedGroupCount);

        Verify(owner, b, Inspection(AudioDuplicateInspectionStage.Caution, ["1"], []));
        owner.SearchText = "status:" + Resources.Audio_duplicate_candidate_reason_non_equivalent;
        CollectionAssert.AreEqual(new[] { b.Folder }, Names(owner), "STATUSの語で検索できます。");
    }

    /// <summary>
    /// 保証：「表示中をすべて選択」「表示中をすべて解除」は検索と除外を当てた後の表示中のフォルダだけに作用し、整理不可を選択しない。
    /// Spaceは整理不可を除いてオンとオフが混在していればすべてオン、すべてオンならすべてオフにし、右クリックの「チェックを入れる」「外す」は整理不可を変えない。
    /// 検出する誤り：非表示のフォルダの選択、整理不可の選択、Spaceの混在時の挙動の誤り。
    /// </summary>
    [TestMethod]
    public void SelectionOperations_RespectVisibilityAndUnorganizable()
    {
        object library = new object();
        AudioDuplicateFolder a = Folder(@"C:\S\a"), b = Folder(@"C:\S\b"), c = Folder(@"C:\S\c"), hidden = Folder(@"C:\T\hidden");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [a, b, c, hidden]);
        Verify(owner, c, Unorganizable(DuplicateMergeVerificationReasonKind.Unreadable, c.Folder));
        owner.ExcludeText = "path:hidden";

        owner.SelectVisible();
        Assert.IsTrue(State(owner, a).IsSelected && State(owner, b).IsSelected);
        Assert.IsFalse(State(owner, c).IsSelected, "整理不可は選択しない");
        Assert.IsFalse(State(owner, hidden).IsSelected, "非表示は変えない");
        Assert.AreEqual(2, owner.SelectedGroupCount);

        owner.ClearVisible();
        Assert.AreEqual(0, owner.SelectedGroupCount);
        owner.ToggleSelect(owner.RowSet.Single(row => row.Path == a.Folder));
        Assert.IsTrue(owner.ToggleSelectOfRows(owner.RowSet), "混在していればすべてオン");
        Assert.IsTrue(State(owner, a).IsSelected && State(owner, b).IsSelected && !State(owner, c).IsSelected);
        Assert.IsTrue(owner.ToggleSelectOfRows(owner.RowSet), "すべてオンならすべてオフ");
        Assert.AreEqual(0, owner.SelectedGroupCount);

        Assert.IsTrue(owner.SetSelectOfRows(owner.RowSet, selected: true));
        Assert.IsFalse(State(owner, c).IsSelected, "右クリックでも整理不可は変えない");
        Assert.IsFalse(owner.SetSelectOfRows([owner.RowSet.Single(row => row.Path == c.Folder)], selected: true));
    }

    /// <summary>
    /// 保証：整理の対象の捕捉は、表示中でSELECTがオンのフォルダを表示順に、確認結果と、捕捉時点の衝突整理の値とともに返し、以後の状態や衝突整理の変更は捕捉済みの値へ影響しない。
    /// 右クリックの確認の捕捉は、SELECTの状態によらず行のフォルダを一度ずつ返す。
    /// 検出する誤り：表示順でない捕捉、捕捉後の値の変化の波及、SELECTがオフのフォルダの混入。
    /// </summary>
    [TestMethod]
    public void Capture_ReturnsSelectedFoldersInDisplayOrderAsImmutableValues()
    {
        object library = new object();
        AudioDuplicateFolder a = Folder(@"C:\S\a", "x", "y"), b = Folder(@"C:\S\b", "z");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [a, b]);
        Verify(owner, a, Inspection(AudioDuplicateInspectionStage.Caution, ["x", "y"], ["x"]));
        owner.RequestSort("Path", ListSortDirection.Descending);
        owner.SelectVisible();

        IReadOnlyList<AudioDuplicateCandidateTarget> targets = owner.CaptureSelectedFolders();
        owner.CollisionCleanupMode = AudioDuplicateCollisionCleanupMode.KeepWav;
        owner.DeleteMethod = DuplicateOrganizeDeleteMethod.DeletePermanently;
        owner.ClearVisible();

        CollectionAssert.AreEqual(new[] { b.Folder, a.Folder }, targets.Select(target => target.Folder).ToArray(), "表示順");
        Assert.IsTrue(targets[0].IsUnverified);
        Assert.AreEqual(AudioDuplicateInspectionStage.Caution, targets[1].Inspection!.Stage);
        CollectionAssert.AreEqual(new[] { "x", "y" }, targets[1].Inspection!.CollisionNames.ToArray(), "同等でない名前を含む、確認が確定した衝突名のすべて");
        Assert.IsTrue(targets.All(target => target.CollisionCleanup == AudioDuplicateCollisionCleanupMode.KeepOgg), "捕捉時点の衝突整理");
        Assert.IsTrue(targets.All(target => target.DeleteMethod == DuplicateOrganizeDeleteMethod.MoveToRecycleBin), "捕捉時点の削除（後の変更は影響しない）");
        Assert.AreEqual(2, owner.CaptureFoldersOfRows([owner.RowSet[0], owner.RowSet[0], owner.RowSet[1]]).Count, "SELECTによらず、フォルダごとに一度");
    }

    /// <summary>
    /// 保証：「エクスプローラーで開く」は存在するフォルダごとに一度だけ開き、存在しないフォルダは開かず例外にもしない。
    /// 検出する誤り：存在しないフォルダを開く、同じフォルダの重複オープン。
    /// </summary>
    [TestMethod]
    public void OpenInExplorer_OpensExistingFoldersOnce()
    {
        var opened = new List<string>();
        var owner = new AudioDuplicateCandidateListOwner(path => !path.EndsWith("missing", StringComparison.Ordinal), opened.Add);
        AudioDuplicateFolder a = Folder(@"C:\S\a"), missing = Folder(@"C:\S\missing");
        Adopt(owner, new object(), [a, missing]);

        owner.OpenInExplorer([owner.RowSet[0], owner.RowSet[0], owner.RowSet[1]]);

        CollectionAssert.AreEqual(new[] { a.Folder }, opened);
    }

    /// <summary>
    /// 保証：新しい結果にないフォルダ（整理で解消したフォルダ）の状態と行は、表示中かによらず取り除かれ、結果に残るフォルダの状態（SELECT・確認結果）と並べ替え・衝突整理は保たれる。
    /// 結果が空なら行が0件になる。解消したフォルダがなければ何も変えない。検出する誤り：消えたフォルダの行・状態の残存、残るフォルダの状態の消失。
    /// </summary>
    [TestMethod]
    public void RemoveUnresolvedFolders_DropsVanishedFoldersAndKeepsTheRest()
    {
        object library = new object();
        AudioDuplicateFolder a = Folder(@"C:\S\a"), b = Folder(@"C:\S\b");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [a, b]);
        Verify(owner, b, Inspection(AudioDuplicateInspectionStage.Caution, ["x"], []));
        owner.ToggleSelect(owner.RowSet.Single(row => row.Path == b.Folder));
        owner.RequestSort("Path", ListSortDirection.Descending);
        owner.CollisionCleanupMode = AudioDuplicateCollisionCleanupMode.KeepWav;

        owner.RemoveUnresolvedFolders([a.Group, b.Group]);
        Assert.AreEqual(2, owner.RowSet.Count, "解消したフォルダがなければ何も変えない");

        owner.RemoveUnresolvedFolders([b.Group]);

        CollectionAssert.AreEqual(new[] { b.Folder }, Names(owner));
        Assert.IsFalse(owner.TryGetState(a.Group, out _), "消えたフォルダの状態は破棄");
        Assert.IsTrue(State(owner, b).IsSelected && State(owner, b).Inspection != null, "残るフォルダの状態を保つ");
        Assert.AreEqual("Path", owner.SortColumnName);
        Assert.AreEqual(AudioDuplicateCollisionCleanupMode.KeepWav, owner.CollisionCleanupMode);
        Assert.AreEqual(1, owner.VisibleGroupCount);

        owner.RemoveUnresolvedFolders([]);
        Assert.AreEqual(0, owner.RowSet.Count, "結果が空なら行は0件");
    }

    /// <summary>
    /// 保証：STATUS列の並べ替えは表示文言（段階と理由の語）の比較で、昇順・降順とも同値は既定の結果順（探索結果の順）を保つ。
    /// 表示文言を固定するため日本語の表示で確認し、各段階に同じSTATUSのフォルダを二つずつ置く。
    /// 文言の比較の順は「安全（WAV/OGG同等）」＜「整理不可：リンク」＜「未確認」＜「要注意：WAV/OGG非同等」（先頭の文字の符号位置の順）。
    /// 検出する誤り：STATUSの並べ替えの未対応、段階の列挙の順などの文言以外による比較、同値の順の崩れ（降順で同値の組まで逆にするなど）。
    /// </summary>
    [TestMethod]
    public void RequestSort_Status_ComparesTheDisplayText()
    {
        using IDisposable culture = TestCultureScope.UseJapaneseCulture();
        object library = new object();
        AudioDuplicateFolder caution1 = Folder(@"C:\S\a"), unverified1 = Folder(@"C:\S\b"), safe1 = Folder(@"C:\S\c"), unorganizable1 = Folder(@"C:\S\d"),
            unverified2 = Folder(@"C:\S\e"), safe2 = Folder(@"C:\S\f"), caution2 = Folder(@"C:\S\g"), unorganizable2 = Folder(@"C:\S\h");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, library, [caution1, unverified1, safe1, unorganizable1, unverified2, safe2, caution2, unorganizable2]);
        foreach (AudioDuplicateFolder caution in new[] { caution1, caution2 })
        {
            Verify(owner, caution, Inspection(AudioDuplicateInspectionStage.Caution, ["x"], []));
        }
        foreach (AudioDuplicateFolder safe in new[] { safe1, safe2 })
        {
            Verify(owner, safe, Inspection(AudioDuplicateInspectionStage.Safe, ["x"]));
        }
        foreach (AudioDuplicateFolder unorganizable in new[] { unorganizable1, unorganizable2 })
        {
            Verify(owner, unorganizable, Unorganizable(DuplicateMergeVerificationReasonKind.Link, unorganizable.Folder));
        }

        owner.RequestSort("Status", ListSortDirection.Ascending);
        CollectionAssert.AreEqual(
            new[] { @"C:\S\c", @"C:\S\f", @"C:\S\d", @"C:\S\h", @"C:\S\b", @"C:\S\e", @"C:\S\a", @"C:\S\g" },
            Names(owner),
            "昇順：安全、整理不可、未確認、要注意。同値は探索結果の順");

        owner.RequestSort("Status", ListSortDirection.Descending);
        CollectionAssert.AreEqual(
            new[] { @"C:\S\a", @"C:\S\g", @"C:\S\b", @"C:\S\e", @"C:\S\d", @"C:\S\h", @"C:\S\c", @"C:\S\f" },
            Names(owner),
            "降順：要注意、未確認、整理不可、安全。同値は探索結果の順のまま");
    }

    private static string Item(string name, int count) => string.Format(Resources.Duplicate_merge_candidate_audio_count_item_format, name, count);

    private static string Joined(params string[] items) => string.Join(Resources.Duplicate_merge_candidate_audio_list_separator, items);

    private static string NameWithReason(string name, string reason) => "- " + string.Format(Resources.Duplicate_merge_candidate_audio_name_reason_format, name, reason);

    /// <summary>
    /// 保証：診断を持つ要注意の確認結果のツールチップは、説明行の次に「内訳：理由 件数／…」（理由の順位の順、読取り失敗は最後、0件の理由は出さない）、
    /// 同等と判定しなかった衝突名に音質の目安が成立するものがあれば「音質の目安：目安 件数／…」（目安の並びの順）、その次に「- 名前：理由」を昇順で出す。
    /// 同等の名前は件数にも目安にも数えない（同等の名前の目安は数えない）。同等の組数の文は従来どおり末尾に出る。
    /// 検出する誤り：理由の順位・読取り失敗の位置の誤り、同等の名前の混入、目安の件数の誤り、名前の行の書式の誤り、説明行・名前の行の順序の誤り。
    /// </summary>
    [TestMethod]
    public void StatusTooltip_WithDiagnostics_ShowsTheBreakdownQualityHintsAndTheReasonOfEveryName()
    {
        const string folder = @"C:\S\a";
        DuplicateMergeAudioPairDiagnostic[] diagnostics =
        [
            DuplicateMergeAudioDiagnosticTestSupport.NonEquivalent("a", folder, DuplicateMergeAudioPairReason.Volume, DuplicateMergeAudioQualityHints.OggLowBitrate),
            DuplicateMergeAudioDiagnosticTestSupport.NonEquivalent(
                "b", folder, DuplicateMergeAudioPairReason.LowBandWaveform, DuplicateMergeAudioQualityHints.OggLowBitrate | DuplicateMergeAudioQualityHints.Wav8Bit),
            DuplicateMergeAudioDiagnosticTestSupport.ReadFailure("c", folder),
            DuplicateMergeAudioDiagnosticTestSupport.NonEquivalent("d", folder, DuplicateMergeAudioPairReason.LowBandWaveform),
            DuplicateMergeAudioDiagnosticTestSupport.Equivalent("good", folder, DuplicateMergeAudioQualityHints.WavOnlyMono)
        ];
        AudioDuplicateFolderInspection inspection = Inspection(AudioDuplicateInspectionStage.Caution, ["a", "b", "c", "d", "good"], ["good"], diagnostics);

        string tooltip = AudioDuplicateStatusPresentation.FormatTooltip(inspection, folder)!;

        string[] lines = tooltip.Split(Environment.NewLine);
        Assert.AreEqual(Resources.Audio_duplicate_candidate_non_equivalent_description, lines[0]);
        Assert.AreEqual(
            string.Format(
                Resources.Duplicate_merge_candidate_audio_breakdown_format,
                Joined(
                    Item(Resources.Duplicate_merge_candidate_audio_reason_low_band_waveform, 2),
                    Item(Resources.Duplicate_merge_candidate_audio_reason_volume, 1),
                    Item(Resources.Duplicate_merge_candidate_audio_result_read_failure, 1))),
            lines[1],
            "理由の順位の順で、読取り失敗は最後です。");
        Assert.AreEqual(
            string.Format(
                Resources.Duplicate_merge_candidate_audio_hint_format,
                Joined(Item(Resources.Duplicate_merge_candidate_audio_hint_ogg_low_bitrate, 2), Item(Resources.Duplicate_merge_candidate_audio_hint_wav_8bit, 1))),
            lines[2],
            "同等の名前の目安（WAVだけモノラル）は数えません。");
        CollectionAssert.AreEqual(
            new[]
            {
                NameWithReason("a", Resources.Duplicate_merge_candidate_audio_reason_volume),
                NameWithReason("b", Resources.Duplicate_merge_candidate_audio_reason_low_band_waveform),
                NameWithReason("c", Resources.Duplicate_merge_candidate_audio_result_read_failure),
                NameWithReason("d", Resources.Duplicate_merge_candidate_audio_reason_low_band_waveform)
            },
            lines[3..7]);
        Assert.AreEqual(string.Empty, lines[7]);
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_tooltip_audio_equivalent_format, 1), lines[8]);
    }

    /// <summary>
    /// 保証：名前を11件以上持つ場合も、内訳の件数は同等と判定しなかった全名前を数え（10件に切らない）、名前の列挙は従来どおり昇順で10件まで「- 名前：理由」、超過分は「ほか{n}件」。
    /// 音質の目安が成立する名前がなければ、音質の目安の行を出さない。
    /// 検出する誤り：件数を表示した10件だけで数える誤り、目安がないときの空の行、名前の省略の欠落。
    /// </summary>
    [TestMethod]
    public void StatusTooltip_WithDiagnostics_CountsEveryNameAndOmitsTheHintLineWithoutHints()
    {
        const string folder = @"C:\S\a";
        string[] names = [.. Enumerable.Range(1, 12).Select(index => "n" + (13 - index).ToString("00"))];
        DuplicateMergeAudioPairDiagnostic[] diagnostics =
            [.. names.Select(name => DuplicateMergeAudioDiagnosticTestSupport.NonEquivalent(name, folder, DuplicateMergeAudioPairReason.Volume))];

        string[] lines = AudioDuplicateStatusPresentation.FormatTooltip(
            Inspection(AudioDuplicateInspectionStage.Caution, names, [], diagnostics), folder)!.Split(Environment.NewLine);

        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_audio_breakdown_format, Item(Resources.Duplicate_merge_candidate_audio_reason_volume, 12)),
            lines[1]);
        Assert.AreEqual(NameWithReason("n01", Resources.Duplicate_merge_candidate_audio_reason_volume), lines[2], "目安の行はなく、名前の列挙が続きます。");
        Assert.AreEqual(NameWithReason("n10", Resources.Duplicate_merge_candidate_audio_reason_volume), lines[11]);
        Assert.AreEqual(string.Format(Resources.Duplicate_merge_candidate_tooltip_more_format, 2), lines[12]);
        Assert.AreEqual(13, lines.Length);
    }

    /// <summary>
    /// 保証：整理不可と安全のツールチップは、診断を持っても変わらない（内訳・音質の目安・理由を出さない）。診断を持たない要注意の確認結果は、従来どおり名前だけを出す
    /// （既存の <see cref="StatusText_AndTooltip_FollowTheStage"/> が診断なしの保証）。
    /// 検出する誤り：整理不可・安全への内訳の混入、診断を持たない結果での例外・理由の混入。
    /// </summary>
    [TestMethod]
    public void StatusTooltip_UnorganizableAndSafe_AreUnchangedByDiagnostics()
    {
        const string folder = @"C:\S\a";
        DuplicateMergeAudioPairDiagnostic[] diagnostics = [DuplicateMergeAudioDiagnosticTestSupport.ReadFailure("x", folder)];
        AudioDuplicateFolderInspection unorganizable = Unorganizable(DuplicateMergeVerificationReasonKind.Unreadable, folder, "sub");

        Assert.AreEqual(
            AudioDuplicateStatusPresentation.FormatTooltip(unorganizable, folder),
            AudioDuplicateStatusPresentation.FormatTooltip(unorganizable with { PairDiagnostics = diagnostics }, folder));

        DuplicateMergeAudioPairDiagnostic[] safeDiagnostics = [DuplicateMergeAudioDiagnosticTestSupport.Equivalent("x", folder)];
        Assert.AreEqual(
            string.Format(Resources.Duplicate_merge_candidate_tooltip_audio_equivalent_format, 1),
            AudioDuplicateStatusPresentation.FormatTooltip(Inspection(AudioDuplicateInspectionStage.Safe, ["x"], null, safeDiagnostics), folder));
    }

    private static readonly string[] TsvHeaderColumns =
    [
        "FOLDER", "NAME", "RESULT", "REASON", "QUALITY_HINT", "WAV_PATH", "WAV_FORMAT", "WAV_BITS", "WAV_RATE", "WAV_CHANNELS", "WAV_FRAMES", "WAV_SECONDS", "WAV_BYTES",
        "OGG_PATH", "OGG_RATE", "OGG_CHANNELS", "OGG_FRAMES", "OGG_SECONDS", "OGG_BYTES", "OGG_KBPS", "FRAME_DIFF", "EXCESS_SILENT", "CORRELATION", "VOLUME_DB",
        "LOW_VOLUME_DB", "LOW_ERROR_1K", "LOW_ERROR_4K", "SEGMENT_MIN_DB"
    ];

    private static string Row(params string[] cells)
    {
        Assert.AreEqual(28, cells.Length, "行は28列です。");
        return string.Join("\t", cells);
    }

    /// <summary>
    /// 保証：検査結果の詳細のTSVは、見出し28列（表示言語によらない固定の英字）の次に組ごとの一行を、列をTAB・行をCRLFで区切り（末尾に改行を付けない）、
    /// 衝突名の昇順で出す。数値は <c>CurrentCulture</c> を sv-SE にしても不変書式（小数点はピリオド、負号はASCIIのハイフンマイナス、桁区切りなし）で、小数の桁数は
    /// 秒6・kbps3・相関と誤差比4・dB3、無限大は <c>Infinity</c>・<c>-Infinity</c>、未測定は空、WAV_FORMAT は <c>PCM</c>・<c>FLOAT</c>・<c>0x</c>と4桁の16進、EXCESS_SILENT は <c>TRUE</c>/<c>FALSE</c>。
    /// RESULT・REASON・QUALITY_HINT は現在の表示言語の表示リソースの文言（目安は「, 」区切り）で、同等・読取り失敗の組も全量を出し、制御文字（U+0085）は空白1個に置き換える。
    /// 検出する誤り：現在のカルチャでの書式化（負号・小数点）、桁数・無限大・未測定の表記の誤り、列の欠落・順序の誤り、見出しの翻訳、行の順序の誤り、改行コードの誤り。
    /// </summary>
    [TestMethod]
    public void BuildInspectionDetailsTsv_WritesTheFixedColumnsWithInvariantNumberFormat()
    {
        using IDisposable culture = TestCultureScope.UseCulture("sv-SE");
        const string folder = @"C:\S\a";
        AudioDuplicateFolder a = Folder(folder, "n1");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, new object(), [a]);
        DuplicateMergeAudioPairDiagnostic[] diagnostics =
        [
            DuplicateMergeAudioDiagnosticTestSupport.ReadFailure("n3", folder),
            DuplicateMergeAudioDiagnosticTestSupport.Pair("n5\u0085x", folder, reason: DuplicateMergeAudioPairReason.NonFinite),
            DuplicateMergeAudioDiagnosticTestSupport.Pair(
                "n1", folder, reason: DuplicateMergeAudioPairReason.LowBandWaveform,
                hints: DuplicateMergeAudioQualityHints.OggLowBitrate | DuplicateMergeAudioQualityHints.Wav8Bit,
                wav: new DuplicateMergeWavInfo(1, 8, 22050, 1, 44100, 44144), ogg: new DuplicateMergeOggInfo(44100, 2, 88200, 20000),
                frameDifference: -1152, excessSilent: false,
                comparison: new DuplicateMergeAudioComparison(0.98764, -6.0206, null, 1.23456, double.PositiveInfinity, double.NegativeInfinity)),
            DuplicateMergeAudioDiagnosticTestSupport.Pair(
                "n2", folder, DuplicateMergeAudioPairResult.Equivalent,
                wav: new DuplicateMergeWavInfo(3, 32, 44100, 2, 44100, 352844), ogg: new DuplicateMergeOggInfo(44100, 2, 44100, 12000),
                frameDifference: 0, comparison: new DuplicateMergeAudioComparison(1, 0, 0, 0, 0, 0)),
            DuplicateMergeAudioDiagnosticTestSupport.Pair(
                "n4", folder, reason: DuplicateMergeAudioPairReason.WavUndecodable, wav: new DuplicateMergeWavInfo(2, 4, null, null, null, 100))
        ];
        Verify(owner, a, Inspection(AudioDuplicateInspectionStage.Caution, ["n1", "n2", "n3", "n4", "n5\u0085x"], ["n2"], diagnostics));

        string tsv = owner.BuildInspectionDetailsTsv(owner.RowSet)!;

        Assert.IsFalse(tsv.EndsWith("\r\n", StringComparison.Ordinal), "末尾の行に改行を付けません。");
        Assert.IsFalse(tsv.Contains('\u2212'), "負号はASCIIのハイフンマイナスです。");
        string[] lines = tsv.Split("\r\n");
        Assert.AreEqual(6, lines.Length);
        Assert.AreEqual(string.Join("\t", TsvHeaderColumns), lines[0]);
        string hintSeparator = ", ";
        Assert.AreEqual(
            Row(
                folder, "n1", Resources.Duplicate_merge_candidate_audio_result_non_equivalent, Resources.Duplicate_merge_candidate_audio_reason_low_band_waveform,
                Resources.Duplicate_merge_candidate_audio_hint_ogg_low_bitrate + hintSeparator + Resources.Duplicate_merge_candidate_audio_hint_wav_8bit,
                @"C:\S\a\n1.wav", "PCM", "8", "22050", "1", "44100", "2.000000", "44144",
                @"C:\S\a\n1.ogg", "44100", "2", "88200", "2.000000", "20000", "80.000",
                "-1152", "FALSE", "0.9876", "-6.021", "", "1.2346", "Infinity", "-Infinity"),
            lines[1]);
        Assert.AreEqual(
            Row(
                folder, "n2", Resources.Duplicate_merge_candidate_audio_result_equivalent, "", "",
                @"C:\S\a\n2.wav", "FLOAT", "32", "44100", "2", "44100", "1.000000", "352844",
                @"C:\S\a\n2.ogg", "44100", "2", "44100", "1.000000", "12000", "96.000",
                "0", "", "1.0000", "0.000", "0.000", "0.0000", "0.0000", "0.000"),
            lines[2],
            "同等の組も出ます。");
        Assert.AreEqual(
            Row(
                folder, "n3", Resources.Duplicate_merge_candidate_audio_result_read_failure, "", "",
                @"C:\S\a\n3.wav", "", "", "", "", "", "", "100",
                @"C:\S\a\n3.ogg", "", "", "", "", "200", "",
                "", "", "", "", "", "", "", ""),
            lines[3],
            "読取り失敗の組は理由を持たず、ファイルサイズだけを出します。");
        Assert.AreEqual(
            Row(
                folder, "n4", Resources.Duplicate_merge_candidate_audio_result_non_equivalent, Resources.Duplicate_merge_candidate_audio_reason_wav_undecodable, "",
                @"C:\S\a\n4.wav", "0x0002", "4", "", "", "", "", "100",
                @"C:\S\a\n4.ogg", "44100", "2", "44100", "1.000000", "16000", "128.000",
                "", "", "", "", "", "", "", ""),
            lines[4]);
        Assert.AreEqual(
            Row(
                folder, "n5 x", Resources.Duplicate_merge_candidate_audio_result_non_equivalent, Resources.Duplicate_merge_candidate_audio_reason_non_finite, "",
                @"C:\S\a\n5 x.wav", "PCM", "16", "44100", "2", "44100", "1.000000", "176444",
                @"C:\S\a\n5 x.ogg", "44100", "2", "44100", "1.000000", "16000", "128.000",
                "", "", "", "", "", "", "", ""),
            lines[5],
            "制御文字（U+0085）は空白1個に置き換わります。");
    }

    /// <summary>
    /// 保証：検査結果の詳細は、選択された行のフォルダを、フォルダごとに一度、表の表示順（並べ替えを当てた後）で出し、FOLDER列にはそのフォルダを出す。
    /// 行を逆順に渡しても、同じフォルダの行を重ねて渡しても、表示順で一度だけ出る。診断を持たないフォルダ（未確認、診断なしの確認結果）は行を出さない。
    /// 読取りの失敗で整理不可になったフォルダは、保持した診断を出す。リンクで整理不可になったフォルダ（診断なし）と、対象に診断が一件もない場合は <c>null</c>（コピーしない）。
    /// 検出する誤り：選択順での出力、重複したフォルダの二重出力、診断のないフォルダの空行、整理不可で保持した診断の欠落、対象なしでの空のTSV。
    /// </summary>
    [TestMethod]
    public void BuildInspectionDetailsTsv_FollowsTheDisplayOrderAndSkipsFoldersWithoutDiagnostics()
    {
        AudioDuplicateFolder a = Folder(@"C:\S\a", "x"), b = Folder(@"C:\S\b", "x"), c = Folder(@"C:\S\c", "x"), d = Folder(@"C:\S\d", "x");
        AudioDuplicateCandidateListOwner owner = CreateOwner();
        Adopt(owner, new object(), [a, b, c, d]);
        Verify(owner, a, Inspection(AudioDuplicateInspectionStage.Caution, ["x"], [], [DuplicateMergeAudioDiagnosticTestSupport.NonEquivalent("x", a.Folder, DuplicateMergeAudioPairReason.Volume)]));
        Verify(owner, b, Inspection(AudioDuplicateInspectionStage.Safe, ["x"], null, [DuplicateMergeAudioDiagnosticTestSupport.Equivalent("x", b.Folder)]));
        Verify(owner, c, Inspection(AudioDuplicateInspectionStage.Caution, ["x"], []));
        AudioDuplicateFolderInspection unreadable = Unorganizable(DuplicateMergeVerificationReasonKind.Unreadable, d.Folder, "x.wav")
            with
        { PairDiagnostics = [DuplicateMergeAudioDiagnosticTestSupport.ReadFailure("x", d.Folder)] };
        Verify(owner, d, unreadable);
        string[] FoldersOf(string? tsv) => [.. tsv!.Split("\r\n").Skip(1).Select(line => line.Split('\t')[0])];

        AudioDuplicateCandidateRow[] rows = [.. owner.RowSet];
        CollectionAssert.AreEqual(
            new[] { @"C:\S\a", @"C:\S\b", @"C:\S\d" },
            FoldersOf(owner.BuildInspectionDetailsTsv([rows[3], rows[1], rows[0], rows[1], rows[2]])),
            "逆順・重複した行を渡しても、表示順でフォルダごとに一度です。診断のないフォルダcは行を出しません。");

        owner.RequestSort("Path", ListSortDirection.Descending);
        CollectionAssert.AreEqual(
            new[] { @"C:\S\d", @"C:\S\b", @"C:\S\a" },
            FoldersOf(owner.BuildInspectionDetailsTsv(owner.RowSet)),
            "並べ替えを当てた表示順です。");

        Assert.IsNull(owner.BuildInspectionDetailsTsv([owner.RowSet.Single(row => row.Path == c.Folder)]), "診断のないフォルダだけなら null です。");
        Assert.IsNull(owner.BuildInspectionDetailsTsv([]), "選択なしは null です。");

        AudioDuplicateFolder e = Folder(@"C:\S\e", "x");
        Adopt(owner, new object(), [e]);
        Assert.IsNull(owner.BuildInspectionDetailsTsv(owner.RowSet), "未確認は null です。");
        Verify(owner, e, Unorganizable(DuplicateMergeVerificationReasonKind.Link, e.Folder));
        Assert.IsNull(owner.BuildInspectionDetailsTsv(owner.RowSet), "リンクで整理不可（判定へ進まない）は診断を持ちません。");
    }
}
