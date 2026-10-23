using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using static BeMusicSeeker.Tests.BmsPlaylistTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class ManagedPlaylistOutputPlacementTests
{
    /// <summary>同一・包含は方向や表記差によらず拒否し、接頭辞が似る兄弟・別基点は許可します。条件網羅は副作用を持たない共通判定へ集約します。</summary>
    [DataTestMethod]
    [DataRow(@"C:\Output\Parent", @"C:\Output\Parent\Child", false)]
    [DataRow(@"C:\Output\Parent\Child", @"C:\Output\Parent", false)]
    [DataRow(@"C:\Output\Parent", "c:/output/parent/./", false)]
    [DataRow(@"C:\Output\Parent", @"C:\Output\Other\..\Parent\Child", false)]
    [DataRow(@"C:\Output\Parent", @"C:\Output\Parent2", true)]
    [DataRow(@"C:\Output\Parent\A", @"C:\Output\Parent\B", true)]
    [DataRow(@"C:\Output\Parent", @"D:\Other\Parent", true)]
    [DataRow(@"C:\Output\Parent", @"C:\Other\Parent", true)]
    public void FinalDirectories_RejectOnlyIdenticalOrNestedPaths(string first, string second, bool expected)
    {
        Assert.AreEqual(expected, ManagedPlaylistOutputPlacement.IsValid([first, second]));
    }

    /// <summary>編集セッションからの名前・出力名・基点・ルート型変更と新規登録を、DB・正本通知・物理出力前に拒否します。画面の事前検査だけに依存する誤りを検出します。</summary>
    [DataTestMethod]
    [DataRow("name")]
    [DataRow("directory")]
    [DataRow("base")]
    [DataRow("root")]
    [DataRow("draft")]
    public async Task PropertySave_RejectsOverlappingFinalPlacementBeforeMutation(string edit)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ManagedPlaylistOutputPlacementTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = root;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "Normal");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "Root");
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(root, "Extra")]);
            });
            var first = new BMSTable { playlist_id = 801, name = "Parent", Output_dir = edit is "name" or "draft" ? @"Parent\Child" : "Parent", is_root_folder = edit == "root" };
            var edited = new BMSTable
            {
                playlist_id = edit == "draft" ? null : 802,
                name = "Other",
                Output_dir = edit is "base" or "root" ? @"Parent\Child" : "Other",
                custom_folder_output_base_name = edit == "base" ? "Extra" : null
            };
            string songDb = CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(songDb);
            var playlist = new TestBmsPlaylist(songDb, settings: settings)
            { BMSTables = new ObservableCollection<BMSTable>(edit == "draft" ? [first] : [first, edited]) };
            playlist.CommitBMSTableHeadersToDB(playlist.BMSTables);
            var service = new PlaylistPropertySaveService(() => playlist, () => null!, () => null!,
                () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
            using PlaylistPropertyEditSession session = await service.CreateEditSessionAsync(edited, edit == "draft")
                ?? throw new AssertFailedException("編集対象がありません。");
            switch (edit)
            {
                case "name": case "draft": session.Values.Name = "Parent"; break;
                case "directory": session.Values.OutputDirectory = @"Parent\Child"; break;
                case "base": session.Values.CustomFolderOutputBaseName = null; break;
                case "root": session.Values.IsRootFolder = true; break;
            }
            int notices = 0;
            edited.PropertyChanged += (_, _) => notices++;
            Assert.IsNull(await service.TrySaveAsync(session, session.Values));
            Assert.AreEqual(0, notices);
            Assert.AreEqual("Other", edited.name);
            Assert.AreEqual(edit == "base" ? "Extra" : null, edited.custom_folder_output_base_name);
            Assert.IsFalse(edited.is_root_folder);
            using LR2SongDBExtended reader = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly();
            Assert.AreEqual(edit == "draft" ? 1 : 2, reader.Table<BMSTable>().Count());
            if (edit != "draft") { Assert.AreEqual(edited.name, reader.Table<BMSTable>().Single(table => table.playlist_id == 802).name); }
            Assert.IsFalse(Directory.Exists(settings.LR2CustomFolderOutputBaseDir));
            Assert.IsFalse(Directory.Exists(settings.LR2CustomFolderOutputBaseDirRootType));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>一括ルート切替では対象同士、一括基点変更では未選択表との衝突を検査し、ヘッダーを変更する前に全件拒否します。</summary>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task BulkPlacement_RejectsSelectedAndUnselectedConflicts(bool rootSwitch)
    {
        string root = Path.Combine(Path.GetTempPath(), nameof(ManagedPlaylistOutputPlacementTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2RootPath = root;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(root, "Normal");
                values.LR2CustomFolderOutputBaseDirRootType = Path.Combine(root, "Root");
                values.LR2CustomFolderAdditionalOutputBaseDirs = CustomFolderOutputBaseRegistry.SerializeBaseDirectories([Path.Combine(root, "Extra")]);
            });
            string songDb = CreateTempSongDbPath(root);
            PlaylistPersistenceRepository.EnsureSchema(songDb);
            var first = new BMSTable { playlist_id = 811, name = "Parent" };
            var second = new BMSTable { playlist_id = 812, name = "Child", Output_dir = @"Parent\Child", custom_folder_output_base_name = "Extra" };
            var playlist = new TestBmsPlaylist(songDb, settings: settings)
            { BMSTables = new ObservableCollection<BMSTable>([first, second]) };
            playlist.CommitBMSTableHeadersToDB(playlist.BMSTables);
            var library = new TestBmsLibrary(songDb, settings: settings);
            using PlaylistWorkspaceViewModel workspace = CreatePlaylistWorkspace(playlist, library, () => CustomFolderOutputSettingsSnapshot.CreateCurrent(settings));
            int notices = 0;
            first.PropertyChanged += (_, _) => notices++;
            second.PropertyChanged += (_, _) => notices++;
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rootSwitch
                ? workspace.ApplyPlaylistSummaryRootFolder([new() { TableRef = first }, new() { TableRef = second }], true)
                : workspace.ApplyPlaylistSummaryOutputBase([new() { TableRef = second }], null!));
            Assert.AreEqual(0, notices);
            Assert.IsFalse(first.is_root_folder);
            Assert.IsFalse(second.is_root_folder);
            Assert.AreEqual("Extra", second.custom_folder_output_base_name);
            using LR2SongDBExtended reader = new BmsLibraryDbGateway(songDb).OpenSongDbReadOnly();
            Assert.AreEqual("Extra", reader.Table<BMSTable>().Single(table => table.playlist_id == 812).custom_folder_output_base_name);
            Assert.IsFalse(Directory.Exists(settings.LR2CustomFolderOutputBaseDirRootType));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
