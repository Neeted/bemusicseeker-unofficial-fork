using System.Collections.ObjectModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.BmsPlaylistTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;


[TestClass]
public sealed class ManagedPlaylistOutputPlacementTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 条件：二つの実効先を表記差（大文字小文字、区切り、相対要素）を含めて並べる。
    /// 期待結果：同一・親子は方向や表記差によらず衝突とし、接頭辞が似る兄弟・別基点は衝突としない。衝突した場合は両方の表を衝突集合に含める。
    /// 検出する誤り：親子判定の方向・表記差の取りこぼし、接頭辞だけが似る別の出力先の巻き込み、衝突の片方だけを除外して相手を残す誤り。
    /// 条件網羅は副作用を持たない共通判定へ集約し、各入口は接続だけを確認します。
    /// </summary>
    [TestMethod]
    [DataRow(@"C:\Output\Parent", @"C:\Output\Parent\Child", true)]
    [DataRow(@"C:\Output\Parent\Child", @"C:\Output\Parent", true)]
    [DataRow(@"C:\Output\Parent", "c:/output/parent/./", true)]
    [DataRow(@"C:\Output\Parent", @"C:\Output\Other\..\Parent\Child", true)]
    [DataRow(@"C:\Output\Parent", @"C:\Output\Parent2", false)]
    [DataRow(@"C:\Output\Parent\A", @"C:\Output\Parent\B", false)]
    [DataRow(@"C:\Output\Parent", @"D:\Other\Parent", false)]
    [DataRow(@"C:\Output\Parent", @"C:\Other\Parent", false)]
    public void FindColliding_ReportsBothSidesOfIdenticalOrNestedPaths(string first, string second, bool colliding)
    {
        HashSet<object> result = ManagedPlaylistOutputPlacement.FindColliding([new("first", first), new("second", second), new("unrelated", @"E:\Unrelated")]);

        CollectionAssert.AreEquivalent(colliding ? new object[] { "first", "second" } : Array.Empty<object>(), result.ToArray());
    }

    /// <summary>
    /// 条件：公開済みの配置（前）と候補の配置（後）を並べ、配置が変わる表・新規の表・変わらない表の組合せで衝突を作る。
    /// 期待結果：変更なし同士の既存の衝突は問わず、衝突を解消する編集は受理する。配置が変わる表が後の集合で他の表と同一・親子になる場合、新規の表が重なる場合は拒否する。未解決の配置は他の表を止めない。
    /// 検出する誤り：保存済みの衝突が無関係な編集を止める誤り、衝突を解消する編集を拒否する誤り、変更した表・新規の表の衝突を見逃す誤り。
    /// </summary>
    [TestMethod]
    [DataRow("unchangedCollision", false)]
    [DataRow("resolvingEdit", false)]
    [DataRow("changedOverlapsUnchanged", true)]
    [DataRow("changedIdentical", true)]
    [DataRow("newOverlaps", true)]
    [DataRow("newSafeBesideCollision", false)]
    [DataRow("changedUnresolved", false)]
    [DataRow("newOverlapNewEntries", true)]
    public void EditPrecheck_RejectsOnlyChangedOrNewTablesOverlappingAfterSet(string scenario, bool rejected)
    {
        const string a = @"C:\Out\AB";
        const string b = @"C:\Out\AB";
        const string c = @"C:\Out\Safe";
        ManagedPlaylistOutputPlacement.Placement[] collidingBefore = [new("T1", a), new("T2", b), new("T3", c)];
        ManagedPlaylistOutputPlacement.Placement[] before = collidingBefore;
        ManagedPlaylistOutputPlacement.Placement[] after;
        switch (scenario)
        {
            // T1とT2の保存済みの衝突は、T3の無関係な編集では問わない。
            case "unchangedCollision": after = [new("T1", a), new("T2", b), new("T3", @"C:\Out\Safe2")]; break;
            // T2が別の出力先へ移ると衝突が解消する。
            case "resolvingEdit": after = [new("T1", a), new("T2", @"C:\Out\Moved"), new("T3", c)]; break;
            // T3がT1の配下へ移る。
            case "changedOverlapsUnchanged": after = [new("T1", a), new("T2", b), new("T3", @"C:\Out\AB\Child")]; break;
            // T2が別の出力先からT3と同一になる。
            case "changedIdentical":
                before = [new("T1", @"C:\Out\One"), new("T2", @"C:\Out\Two"), new("T3", c)];
                after = [new("T1", @"C:\Out\One"), new("T2", c), new("T3", c)];
                break;
            // 新規のT4が既存のT3と重なる。
            case "newOverlaps": after = [.. collidingBefore, new("T4", c)]; break;
            // 新規のT4はT1・T2の衝突と無関係である。
            case "newSafeBesideCollision": after = [.. collidingBefore, new("T4", @"C:\Out\Fresh")]; break;
            // T3の基点が未登録になり配置が決まらない。
            case "changedUnresolved": after = [new("T1", a), new("T2", b), new("T3", null)]; break;
            // 新規のT4とT5が互いに重なる。
            default: after = [.. collidingBefore, new("T4", @"C:\Out\New"), new("T5", @"C:\Out\New\Sub")]; break;
        }

        Assert.AreEqual(rejected, ManagedPlaylistOutputPlacement.IsEditRejected(before, after), scenario);
    }

    /// <summary>編集セッションからの名前・出力名・基点・ルート型変更と新規登録を、DB・正本通知・物理出力前に拒否します。画面の事前検査だけに依存する誤りを検出します。</summary>
    [TestMethod]
    [DataRow("name")]
    [DataRow("directory")]
    [DataRow("base")]
    [DataRow("root")]
    [DataRow("draft")]
    public async Task PropertySave_RejectsOverlappingFinalPlacementBeforeMutation(string edit)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(ManagedPlaylistOutputPlacementTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = root;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "Normal");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "Root");
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(root, "Extra")]);
            });
            var first = new BMSTable { playlist_id = 801, name = "Parent", Output_dir = edit is "name" or "draft" ? "Parent" : "Parent", is_root_folder = edit == "root" };
            var edited = new BMSTable
            {
                playlist_id = edit == "draft" ? null : 802,
                name = "Other",
                Output_dir = edit is "base" or "root" ? "Parent" : "Other",
                custom_folder_output_base_name = edit == "base" ? "Extra" : null
            };
            string songDb = CreateTempSongDbPath(databaseScope, root);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDb);
            var playlist = new TestBmsPlaylist(databaseScope, songDb, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDb), uiScheduler: new TestUiScheduler(() => null), settings: settings)
            { BMSTables = new ObservableCollection<BMSTable>(edit == "draft" ? [first] : [first, edited]) };
            playlist.CommitBMSTableHeadersToDB(playlist.BMSTables);
            var service = new PlaylistPropertySaveService(() => playlist, () => null!, () => null!,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
            using PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(edited, edit == "draft")
                ?? throw new AssertFailedException("編集対象がありません。");
            switch (edit)
            {
                case "name": case "draft": session.Values.Name = "Parent"; break;
                case "directory": session.Values.OutputDirectory = "Parent"; break;
                case "base": session.Values.CustomFolderOutputBaseName = null; break;
                case "root": session.Values.IsRootFolder = true; break;
            }
            int notices = 0;
            edited.PropertyChanged += (_, _) => notices++;
            Assert.IsNull((await service.TrySaveAsync(session, session.Values)).Commit);
            Assert.AreEqual(0, notices);
            Assert.AreEqual("Other", edited.name);
            Assert.AreEqual(edit == "base" ? "Extra" : null, edited.custom_folder_output_base_name);
            Assert.IsFalse(edited.is_root_folder);
            using LR2SongDBExtended reader = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly();
            Assert.AreEqual(edit == "draft" ? 1 : 2, reader.Table<BMSTable>().Count());
            if (edit != "draft") { Assert.AreEqual(edited.name, reader.Table<BMSTable>().Single(table => table.playlist_id == 802).name); }
            Assert.IsFalse(Directory.Exists(settings.LR2CustomFolderOutputBaseDir));
            Assert.IsFalse(Directory.Exists(settings.LR2CustomFolderOutputBaseDirRootType));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>一括ルート切替では対象同士、一括基点変更では未選択表との衝突を検査し、ヘッダーを変更する前に全件拒否します。</summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BulkPlacement_RejectsSelectedAndUnselectedConflicts(bool rootSwitch)
    {
        using var migrationWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string root = Path.Combine(migrationWorkspace.RootDirectory, nameof(ManagedPlaylistOutputPlacementTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = root;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "Normal");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "Root");
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(root, "Extra")]);
            });
            string songDb = CreateTempSongDbPath(databaseScope, root);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDb);
            var first = new BMSTable { playlist_id = 811, name = "Parent" };
            var second = new BMSTable { playlist_id = 812, name = "Child", Output_dir = "Parent", custom_folder_output_base_name = "Extra" };
            var playlist = new TestBmsPlaylist(databaseScope, songDb, lr2PlaylistFolderSynchronization: new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDb), uiScheduler: new TestUiScheduler(() => null), settings: settings)
            { BMSTables = new ObservableCollection<BMSTable>([first, second]) };
            playlist.CommitBMSTableHeadersToDB(playlist.BMSTables);
            var library = new TestBmsLibrary(databaseScope, songDb, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null), settings: settings);
            using PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, library, () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
            int notices = 0;
            first.PropertyChanged += (_, _) => notices++;
            second.PropertyChanged += (_, _) => notices++;
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => rootSwitch
                ? workspace.ApplyPlaylistSummaryRootFolder([new() { TableRef = first }, new() { TableRef = second }], true)
                : workspace.ApplyPlaylistSummaryOutputBase([new() { TableRef = second }], null!));
            Assert.AreEqual(0, notices);
            Assert.IsFalse(first.is_root_folder);
            Assert.IsFalse(second.is_root_folder);
            Assert.AreEqual("Extra", second.custom_folder_output_base_name);
            using LR2SongDBExtended reader = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), songDb).OpenSongDbReadOnly();
            Assert.AreEqual("Extra", reader.Table<BMSTable>().Single(table => table.playlist_id == 812).custom_folder_output_base_name);
            Assert.IsFalse(Directory.Exists(settings.LR2CustomFolderOutputBaseDirRootType));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// 条件：変更しない既存の二表が保存済みの出力先（旧値の単一名化で同一）で衝突している状態から、衝突しない選択表の基点を一括変更する。
    /// 期待結果：保存済みの衝突に関係なく一括編集を受理し、選択した表だけ新しい基点の出力先へ生成する。衝突している表の共有先の本文は変更しない。例外にならない。
    /// 検出する誤り：未選択表同士の保存済みの衝突を理由に、無関係な一括編集を拒否する誤り、衝突している表の出力を巻き込んで書き換える誤り。
    /// 選択表が既存の表と重なる場合の拒否は <see cref="BulkPlacement_RejectsSelectedAndUnselectedConflicts"/> が担います。
    /// </summary>
    [TestMethod]
    public async Task BulkPlacement_AcceptsSafeCandidateWhenUnselectedTablesAlreadyCollide()
    {
        await using var fixture = new PlaylistRestoreOutputFixture();
        BMSTable first = PlaylistRestoreOutputFixture.Table(8201, "Shared", "Folder 1");
        BMSTable second = PlaylistRestoreOutputFixture.Table(8202, "Other", "Folder 2");
        second.output_dir = @"Sha\red";
        BMSTable selected = PlaylistRestoreOutputFixture.Table(8203, "Third", "Folder 3");
        Assert.AreEqual(first.Output_dir, second.Output_dir, "旧値の区切りを除いた単一名が同一の出力先になります。");
        fixture.Playlist.BMSTables = new ObservableCollection<BMSTable>([first, second, selected]);
        fixture.Playlist.CommitBMSTablesWithEntriesToDB([first, second, selected]);
        string shared = fixture.OutputDirectory(first);
        string sharedFile = Path.Combine(shared, "0000.lr2folder");
        Directory.CreateDirectory(shared);
        File.WriteAllText(sharedFile, "shared preserved output");
        DateTime sharedTimestamp = File.GetLastWriteTimeUtc(sharedFile);
        using PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(fixture.Playlist, fixture.Library, () => fixture.OutputSettings, () => fixture.Config);

        Assert.IsTrue(await workspace.ApplyPlaylistSummaryOutputBase([new() { TableRef = selected }], "Additional"));

        Assert.AreEqual("Additional", selected.custom_folder_output_base_name);
        Assert.IsTrue(Directory.GetFiles(fixture.OutputDirectory(selected), "*.lr2folder", SearchOption.AllDirectories).Length > 0, "選択した表は新しい基点の出力先へ生成します。");
        Assert.AreEqual("shared preserved output", File.ReadAllText(sharedFile), "衝突している表の共有先は変更しません。");
        Assert.AreEqual(sharedTimestamp, File.GetLastWriteTimeUtc(sharedFile));
        using LR2SongDBExtended reader = new BmsLibraryDbGateway(fixture.DatabaseScope, new BmsDatabaseLoadOptions(true, true, true), fixture.SongDb).OpenSongDbReadOnly();
        Assert.AreEqual("Additional", reader.Table<BMSTable>().Single(table => table.playlist_id == 8203).custom_folder_output_base_name);
    }

}
