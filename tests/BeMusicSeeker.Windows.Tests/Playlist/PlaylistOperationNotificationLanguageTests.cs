using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.PlaylistCatalogViewTestSupport;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;
using static BeMusicSeeker.TestSupport.Components.SharedPlaylistWorkspaceFixture;

namespace BeMusicSeeker.Tests;

/// <summary>
/// 操作中に確定した通知を、積んだ時点ではなく表示を依頼する時点の選択言語で整形することを、各生成元の実通知で検証します。
/// 言語Aで操作を始め、通知の依頼の直前（終了の要求の判定）で言語Bへ切り替え、窓口へ届いた本文・見出しが言語Bの資源と
/// 生の引数から作った文字列で、言語Aの文字列ではないことを確認します。
/// 一部の試験（custom folder 出力保守・出力保守の警告）は、収集した通知を直接 <c>FormatMessage</c> で整形して同じ規則を確認します。
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PlaylistOperationNotificationLanguageTests
{
    private const string LanguageA = "ja-JP";

    private const string LanguageB = "en-US";

    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>テスト内で変更した表示言語と現在の文化圏を、必ず開始前の状態へ戻します。</summary>
    private sealed class LanguageScope : IDisposable
    {
        private readonly CultureInfo? previousResourcesCulture = Resources.Culture;

        private readonly CultureInfo previousCurrentCulture = CultureInfo.CurrentCulture;

        internal LanguageScope() => Change(LanguageA);

        internal static void Change(string name)
        {
            ResourceService.Current.ChangeCulture(name);
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            ResourceService.Current.ChangeCulture(previousResourcesCulture?.Name ?? "en-US");
            Resources.Culture = previousResourcesCulture;
            CultureInfo.CurrentCulture = previousCurrentCulture;
        }
    }

    /// <summary>
    /// 言語Aで確定した通知が、言語Bへの切替後に言語Bの本文・見出しで整形され、言語Aの文字列では表示されないことを確認します。
    /// 期待値は呼出元が現在の言語で組み立てる資源と生の引数から作ります。
    /// </summary>
    private static void AssertFormatsInLanguageAtFormattingTime(
        PlaylistOperationNotificationOwner.OperationNotification notification,
        PlaylistOperationNotificationOwner.OperationNotificationSeverity severity,
        Func<string> expectedMessage,
        Func<string> expectedCaption,
        Func<string, string>? normalize = null)
    {
        normalize ??= text => text;
        LanguageScope.Change(LanguageA);
        string messageA = expectedMessage();
        string captionA = expectedCaption();
        Assert.AreEqual(normalize(messageA), normalize(notification.FormatMessage()));
        Assert.AreEqual(captionA, notification.FormatCaption());

        LanguageScope.Change(LanguageB);
        string messageB = expectedMessage();
        string captionB = expectedCaption();
        Assert.AreNotEqual(messageA, messageB, "言語Aと言語Bの期待値が異なる前提です。");
        Assert.AreNotEqual(captionA, captionB, "言語Aと言語Bの見出しが異なる前提です。");
        Assert.AreEqual(normalize(messageB), normalize(notification.FormatMessage()));
        Assert.AreEqual(captionB, notification.FormatCaption());
        Assert.AreEqual(severity, notification.Severity);
    }

    /// <summary>
    /// 操作を言語Aで始め、通知の依頼の直前（表示役が評価する終了の要求の判定）で言語Bへ切り替える仕掛けです。
    /// 判定は作業領域の終了の判定として注入しますが、再読込みの後片付けなど、ほかの評価点からも呼ばれます。
    /// そのため、構えた後に表示役の依頼の観測口（<c>PlaylistOperationNoticeRequestCount</c>）が増えた後の評価だけで切り替え、
    /// ほかの評価点による、通知の確定より前の切替（偽の合格）を防ぎます。表示役は、数えた後に判定を評価します。
    /// </summary>
    private sealed class LanguageSwitchAtRequest
    {
        private Func<long> requestCount = () => 0;

        private long countAtArm;

        private int armed;

        /// <summary>言語Aへ戻し、表示役の次の依頼で言語Bへ切り替えるよう構えます。操作を始める直前に呼びます。</summary>
        /// <param name="workspaceRequestCount">操作を行う作業領域の依頼の観測口。</param>
        internal void Arm(Func<long> workspaceRequestCount)
        {
            LanguageScope.Change(LanguageA);
            requestCount = workspaceRequestCount;
            Volatile.Write(ref countAtArm, workspaceRequestCount());
            Volatile.Write(ref armed, 1);
        }

        /// <summary>作業領域へ注入する終了の要求の判定です。依頼が数えられた後の評価で、一度だけ言語Bへ切り替え、常に偽を返します。</summary>
        internal bool Provider()
        {
            if (Volatile.Read(ref armed) == 1 && requestCount() > Volatile.Read(ref countAtArm)
                && Interlocked.Exchange(ref armed, 0) == 1)
            {
                LanguageScope.Change(LanguageB);
            }
            return false;
        }

        /// <summary>構えた後に数えられた依頼の数です。一回の操作の通知は、一回の依頼です。</summary>
        internal long RequestsSinceArm => requestCount() - Volatile.Read(ref countAtArm);

        /// <summary>言語Bへの切替が済んだか。</summary>
        internal bool Switched => Volatile.Read(ref armed) == 0;
    }

    /// <summary>
    /// 経路が窓口へ依頼した通知の本文・見出し・アイコンが、依頼の時点の言語（B）の資源と生の引数から作った内容であり、
    /// 操作を始めた言語（A）の文字列ではないことを確認します。切替は <see cref="LanguageSwitchAtRequest"/> が依頼の直前に行います。
    /// </summary>
    private static void AssertDisplayed(
        LanguageSwitchAtRequest language,
        UiMessageRequest displayed,
        System.Windows.MessageBoxImage icon,
        Func<string> expectedMessage,
        Func<string> expectedCaption,
        Func<string, string>? normalize = null)
    {
        normalize ??= text => text;
        Assert.AreEqual(1, language.RequestsSinceArm, "操作の通知は、一回の依頼です。");
        Assert.IsTrue(language.Switched, "表示役の依頼の直前の評価で、言語Bへ切り替わっている前提です。");
        LanguageScope.Change(LanguageA);
        string messageA = expectedMessage();
        string captionA = expectedCaption();
        LanguageScope.Change(LanguageB);
        string messageB = expectedMessage();
        string captionB = expectedCaption();
        Assert.AreNotEqual(messageA, messageB, "言語Aと言語Bの期待値が異なる前提です。");
        Assert.AreNotEqual(captionA, captionB, "言語Aと言語Bの見出しが異なる前提です。");
        Assert.AreEqual(normalize(messageB), normalize(displayed.MessageBoxText));
        Assert.AreEqual(captionB, displayed.Caption);
        Assert.AreEqual(icon, displayed.Icon);
    }

    /// <summary>custom folder出力保守の警告（結果由来の失敗と、直接積む未登録の追加基点）が、整形時の言語で表示されます。</summary>
    [TestMethod]
    public void CustomFolderWarnings_FormatInLanguageAtFormattingTime()
    {
        using var language = new LanguageScope();
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(workspace.RootDirectory, nameof(PlaylistOperationNotificationLanguageTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        TestBmsPlaylist? playlist = null;
        try
        {
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = true;
                values.LR2CustomFolderOutputBaseDir = Path.Combine(directory, "CustomFolder");
            });
            string songDbPath = Path.Combine(directory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath)) { }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var table = new BMSTable
            {
                playlist_id = 7302,
                name = "FailureTable",
                symbol = "FTF",
                Output_dir = "FailureTable",
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders
                    & ~LR2SongDBExtended.playlist.CustomFolderType.UserFolder
                    & ~LR2SongDBExtended.playlist.CustomFolderType.AllSongsFolder,
                entries = [new BMSTableEntry { md5 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", title = "Folder A", folder = "Folder A" }],
                Folder_order = ["Folder A"]
            };
            TestLr2PlaylistFolderSynchronizationPort synchronization = CreateDeterministicLr2PlaylistFolderSynchronizationPort(
                databaseScope, songDbPath, CustomFolderOutputPhysicalSurface.Empty);
            synchronization.Failure = new InvalidOperationException("forced custom-folder sync failure");
            playlist = new TestBmsPlaylist(databaseScope, songDbPath, synchronization, uiScheduler: new TestUiScheduler(() => null), settings: settings)
            {
                BMSTables = new ObservableCollection<BMSTable>([table])
            };

            // (a) 結果由来の警告: 同期ポートの失敗が表名と出力先を事実に持つ警告になる。
            using (PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession())
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => playlist.ReOutputCustomFolder(table));
                PlaylistOperationNotificationOwner.OperationNotification warning = session.TakeReceipt().Notifications.Single();
                string outputDirectory = Path.Combine(directory, "CustomFolder", "FailureTable");
                Dictionary<string, byte[]> outputBefore = ReadOutputFiles(directory);
                AssertFormatsInLanguageAtFormattingTime(
                    warning,
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                    () => string.Format(Resources.Warn_CustomFolderOutputFailed, table.name, outputDirectory),
                    () => Resources.MessageBoxTitle_Warning);
                AssertSameOutput(outputBefore, ReadOutputFiles(directory));
            }

            // (b) 直接積む警告: 未登録の追加基点は対象外にして警告だけを通知する。
            LanguageScope.Change(LanguageA);
            var unregistered = new BMSTable
            {
                playlist_id = 7303,
                name = "Unregistered",
                symbol = "UR",
                Output_dir = "Unregistered",
                custom_folder_output_base_name = "unregistered-base",
                entries = [],
                Folder_order = []
            };
            playlist.BMSTables.Add(unregistered);
            using (PlaylistOperationNotificationOwner.OperationNotificationSession session = playlist.OperationNotificationOwner.BeginSession())
            {
                playlist.ReOutputCustomFolder(unregistered);
                PlaylistOperationNotificationOwner.OperationNotification warning = session.TakeReceipt().Notifications.Single();
                Dictionary<string, byte[]> unregisteredBefore = ReadOutputFiles(directory);
                AssertFormatsInLanguageAtFormattingTime(
                    warning,
                    PlaylistOperationNotificationOwner.OperationNotificationSeverity.Warning,
                    () => string.Format(Resources.Warn_UnregisteredCustomFolderOutputBase, unregistered.name, "unregistered-base"),
                    () => Resources.MessageBoxTitle_Warning);
                AssertSameOutput(unregisteredBefore, ReadOutputFiles(directory));
            }

            // 言語変更と整形で出力ファイルは増減・変化しない。
        }
        finally
        {
            playlist?.RequestShutdown("custom-folder-language");
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>言語変更と整形の前後で、出力ファイルの一覧とバイト列が変わらないことを確認します。</summary>
    private static void AssertSameOutput(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        CollectionAssert.AreEquivalent(before.Keys.ToArray(), after.Keys.ToArray());
        foreach (KeyValuePair<string, byte[]> file in before)
        {
            CollectionAssert.AreEqual(file.Value, after[file.Key], file.Key);
        }
    }

    /// <summary>出力ファイルの内容を、DBを除いてバイト列で読みます。</summary>
    private static Dictionary<string, byte[]> ReadOutputFiles(string directory) => Directory
        .GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(file => !Path.GetFileName(file).StartsWith("song.db", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(file => file, File.ReadAllBytes);

    /// <summary>JSON出力のheader失敗とdata書込み失敗が、どちらも依頼時の言語の本文と見出しで表示されます。</summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaylistExportFailure_FormatsInLanguageAtRequestTime(bool dataFails)
    {
        using var language = new LanguageScope();
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(workspace.RootDirectory, nameof(PlaylistOperationNotificationLanguageTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        FileStream? locked = null;
        try
        {
            var table = new BMSTable { name = "Export failure", Folder_order = [] };
            var dialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
            var switcher = new LanguageSwitchAtRequest();
            PlaylistWorkspaceViewModel viewModel = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: dialogs,
                reloadCleanupShutdownRequestedProvider: switcher.Provider);
            string headerPath = Path.Combine(directory, "header.json");
            string dataPath = Path.Combine(directory, "data.json");
            string lockedPath = dataFails ? dataPath : headerPath;
            File.WriteAllText(lockedPath, "old");
            locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            dialogs.JsonExportResults.Enqueue(new(UiDialogStatus.Accepted, new(headerPath, dataPath)));
            switcher.Arm(() => viewModel.PlaylistOperationNoticeRequestCount);
            await viewModel.ExportPlaylistTableAsync(table);

            UiMessageRequest failure = dialogs.MessageRequests.Single();
            AssertDisplayed(
                switcher,
                failure,
                System.Windows.MessageBoxImage.Hand,
                () => dataFails ? Resources.Playlist_json_export_partial_failure : Resources.Msg_failed_save_playlist,
                () => Resources.Error);
        }
        finally
        {
            locked?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>バックアップ（空・成功・失敗）と復元（成功・失敗）の通知が依頼時の言語になり、失敗の詳細は言語によらず同じ原文です。</summary>
    [TestMethod]
    public async Task PlaylistBackupAndRestoreNotifications_FormatInLanguageAtRequestTime()
    {
        using var language = new LanguageScope();
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(workspace.RootDirectory, nameof(PlaylistOperationNotificationLanguageTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // storeなしかつ表0件: 警告（見出しはWarning）。
            var switcher = new LanguageSwitchAtRequest();
            var unavailableDialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel unavailable = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: unavailableDialogs,
                reloadCleanupShutdownRequestedProvider: switcher.Provider);
            switcher.Arm(() => unavailable.PlaylistOperationNoticeRequestCount);
            await unavailable.BackupPlaylistAsync(Path.Combine(directory, "unavailable.sql"));
            AssertDisplayed(
                switcher,
                unavailableDialogs.MessageRequests.Single(),
                System.Windows.MessageBoxImage.Exclamation,
                () => Resources.Msg_warn_playlist_backup,
                () => Resources.Warning);

            // storeなしで表がある: エラー（見出しはFailure、詳細なし、通知は1件）。
            LanguageScope.Change(LanguageA);
            BMSPlaylist? absentStore = null;
            var storelessDialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
            PlaylistWorkspaceViewModel storeless = CreateDetailWorkspace(out _, playlistStoreProvider: () => absentStore,
                playlistWorkspaceDialogService: storelessDialogs, reloadCleanupShutdownRequestedProvider: switcher.Provider);
            Directory.CreateDirectory(Path.Combine(directory, "tree"));
            string treeDbPath = Path.Combine(directory, "tree", "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(treeDbPath)) { }
            CreateBackupWorkspace(databaseScope, treeDbPath,
                [new BMSTable { playlist_id = 9, name = "Tree", symbol = "T" }],
                out BMSPlaylist treeSource, out _, modelUiScheduler: new TestUiScheduler(() => null));
            storeless.RefreshPlaylistTreeTables(treeSource);
            switcher.Arm(() => storeless.PlaylistOperationNoticeRequestCount);
            await storeless.BackupPlaylistAsync(Path.Combine(directory, "storeless.sql"));
            AssertDisplayed(
                switcher,
                storelessDialogs.MessageRequests.Single(),
                System.Windows.MessageBoxImage.Hand,
                () => Resources.Msg_failed_playlist_backup,
                () => Resources.Failure);

            // 成功と書込み失敗。
            LanguageScope.Change(LanguageA);
            string songDbPath = Path.Combine(directory, "song.db");
            using (LR2SongDBExtended _ = databaseScope.OpenSongWrite(songDbPath)) { }
            PlaylistWorkspaceViewModel viewModel = CreateBackupWorkspace(databaseScope, songDbPath,
                [new BMSTable { playlist_id = 1, name = "バックアップ", symbol = "B", bmt_sort = 1 }],
                out BMSPlaylist playlist, out PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService dialogs,
                modelUiScheduler: new TestUiScheduler(() => null),
                reloadCleanupShutdownRequestedProvider: switcher.Provider);
            playlist.CommitBMSTableHeaderToDB(playlist.BMSTables.Single());
            string backupPath = Path.Combine(directory, "backup.sql");
            switcher.Arm(() => viewModel.PlaylistOperationNoticeRequestCount);
            await viewModel.BackupPlaylistAsync(backupPath);
            AssertDisplayed(
                switcher,
                dialogs.MessageRequests[0],
                System.Windows.MessageBoxImage.Asterisk,
                () => Resources.Msg_success_playlist_backup,
                () => Resources.Success);

            string missingPath = Path.Combine(directory, "missing", "backup.sql");
            switcher.Arm(() => viewModel.PlaylistOperationNoticeRequestCount);
            await viewModel.BackupPlaylistAsync(missingPath);
            UiMessageRequest backupFailure = dialogs.MessageRequests[1];
            // 詳細は通知から切り出さず、同じ書込み先へ別に書込んで得た例外の原文から期待値を作る。
            string backupDetail = Assert.Throws<IOException>(
                () => BeMusicSeeker.Models.Utils.AtomicFileWriter.Write(missingPath, stream => stream.WriteByte(0))).Message;
            AssertDisplayed(
                switcher,
                backupFailure,
                System.Windows.MessageBoxImage.Hand,
                () => Resources.Msg_failed_playlist_backup + Environment.NewLine + Environment.NewLine + backupDetail,
                () => Resources.Failure,
                // 一時ファイル名の乱数部分だけは実行ごとに異なる。
                text => System.Text.RegularExpressions.Regex.Replace(text, "atomicsave-[0-9a-f]{32}", "atomicsave-X"));

            // 復元の成功: 書き出したバックアップを戻す。
            int before = dialogs.MessageRequests.Count;
            switcher.Arm(() => viewModel.PlaylistOperationNoticeRequestCount);
            Assert.IsTrue(await viewModel.RestorePlaylistBackupAsync(backupPath));
            UiMessageRequest restored = dialogs.MessageRequests.Skip(before).Single(request => request.Caption == Resources.Success);
            AssertDisplayed(
                switcher,
                restored,
                System.Windows.MessageBoxImage.Asterisk,
                () => Resources.Msg_success_playlist_restore,
                () => Resources.Success);

            // 復元の失敗: 存在しないファイルの原文を同じファイルの読込で得た例外文と照合する。
            string absent = Path.Combine(directory, "absent.sql");
            string expectedDetail = Assert.ThrowsExactly<FileNotFoundException>(() => File.ReadAllText(absent)).Message;
            before = dialogs.MessageRequests.Count;
            switcher.Arm(() => viewModel.PlaylistOperationNoticeRequestCount);
            Assert.IsFalse(await viewModel.RestorePlaylistBackupAsync(absent));
            UiMessageRequest restoreFailure = dialogs.MessageRequests.Skip(before).Single();
            AssertDisplayed(
                switcher,
                restoreFailure,
                System.Windows.MessageBoxImage.Hand,
                () => Resources.Msg_failed_playlist_restore + Environment.NewLine + Environment.NewLine + expectedDetail,
                () => Resources.Error);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>BMT出力の失敗通知が、依頼時の言語の本文と警告見出しで表示されます。</summary>
    [TestMethod]
    public async Task BmtOutputFailure_FormatsInLanguageAtRequestTime()
    {
        using var language = new LanguageScope();
        using var notices = new PlaylistNoticeProbe(() => TestUiDispatcherHost.Dispatcher.CheckAccess());
        var switcher = new LanguageSwitchAtRequest();
        PlaylistWorkspaceViewModel viewModel = CreateDetailWorkspace(out _, playlistWorkspaceDialogService: notices.Dialogs,
            reloadCleanupShutdownRequestedProvider: switcher.Provider);

        switcher.Arm(() => viewModel.PlaylistOperationNoticeRequestCount);
        viewModel.ReportBmtOutputFailures(Array.AsReadOnly(new[]
        {
            new BmtTableExportService.FileOperationFailure("owned-output/locked.bmt", "sharing-denied")
        }));
        await notices.WaitForRequestsAsync(1);

        AssertDisplayed(
            switcher,
            notices.Messages.Single(),
            System.Windows.MessageBoxImage.Exclamation,
            () => string.Format(Resources.Beatoraja_bmt_output_failure_format, "owned-output/locked.bmt", "sharing-denied"),
            () => Resources.MessageBoxTitle_Warning);
        Assert.IsFalse(notices.BlockedOnUiContext, "表示の依頼をUIの文脈から行いません。");
    }

    /// <summary>ドロップ終端の予期しないエラー通知が、依頼時の言語の文と、言語によらない主失敗の原文で表示されます。</summary>
    [TestMethod]
    public async Task PlaylistDropUnexpectedFailure_FormatsInLanguageAtRequestTime()
    {
        using var language = new LanguageScope();
        var dropDialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
        var switcher = new LanguageSwitchAtRequest();
        await using var fixture = new Fixture(2, modelUiScheduler: new TestUiScheduler(() => null), playlistWorkspaceDialogService: dropDialogs,
            shutdownRequestedProvider: switcher.Provider);
        var reference = new BMSTable
        {
            playlist_id = 4189,
            name = "Reference",
            symbol = "REF",
            is_external_sync = false,
            Folder_order = ["added"],
            entries = []
        };
        fixture.Playlist.BMSTables.Add(reference);
        fixture.Playlist.CommitBMSTableWithEntriesToDB(reference);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        using (LR2SongDBExtended db = fixture.DatabaseScope.OpenSongWrite(fixture.SongDb))
        {
            db.Execute("CREATE TRIGGER fail_drop_insert BEFORE INSERT ON playlist_entry WHEN NEW.playlist_id = 4189 BEGIN SELECT RAISE(ABORT, 'forced drop failure'); END;");
            db.Execute("CREATE TRIGGER fail_drop_update BEFORE UPDATE ON playlist_entry WHEN NEW.playlist_id = 4189 BEGIN SELECT RAISE(ABORT, 'forced drop failure'); END;");
        }

        switcher.Arm(() => fixture.Workspace.PlaylistOperationNoticeRequestCount);
        Exception caught = await Assert.ThrowsAsync<Exception>(
            async () => await fixture.Workspace.AddRowsToFolderAsync([target], reference, PlaylistFolderNode.CreateFolder("added")));

        AssertDisplayed(
            switcher,
            dropDialogs.MessageRequests.Single(),
            System.Windows.MessageBoxImage.Hand,
            () => Resources.Msg_error_unexpected + Environment.NewLine + caught.Message,
            () => Resources.Error);
    }

    /// <summary>inline Levelの不正入力の警告が、既定見出しも含めて依頼時の言語で表示されます。</summary>
    [TestMethod]
    public async Task DetailLevelRejection_DefaultCaptionFollowsLanguageAtRequestTime()
    {
        using var language = new LanguageScope();
        var levelDialogs = new PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService();
        var switcher = new LanguageSwitchAtRequest();
        await using var fixture = new Fixture(2, modelUiScheduler: new TestUiScheduler(() => null), playlistWorkspaceDialogService: levelDialogs,
            shutdownRequestedProvider: switcher.Provider);
        await fixture.Refresh();
        var rows = (PlaylistDetailVirtualView)fixture.Workspace.DetailViewState.View.Rows;
        var target = (PlaylistDetailRow)rows[0];
        var level = new MainChartListCellEditContext(target, nameof(PlaylistDetailRow.Level),
            ChartOperationSourceScope.PlaylistOwned, MainViewOperationSection.Playlist);
        fixture.Workspace.BeginDetailEdit(level);
        target.Level = "invalid-level";

        switcher.Arm(() => fixture.Workspace.PlaylistOperationNoticeRequestCount);
        await fixture.Workspace.CompleteDetailEdit(new MainChartListCellEditEndedEventArgs(level, "invalid-level", commit: true));

        AssertDisplayed(
            switcher,
            levelDialogs.MessageRequests.Last(),
            System.Windows.MessageBoxImage.Exclamation,
            () => Resources.Error_PlaylistEntryLevelFormat,
            () => Resources.MessageBoxTitle_Warning);
    }
}
