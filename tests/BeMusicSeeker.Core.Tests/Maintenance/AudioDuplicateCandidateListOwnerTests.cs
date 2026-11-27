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
    /// 検出する誤り：段階・理由の文言の取り違え、10件を超える名前の無制限表示、同等の文の欠落、整理不可の該当パスの欠落。
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
        Assert.AreEqual("- n01", lines[1], "同等でない名前は昇順");
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
}
