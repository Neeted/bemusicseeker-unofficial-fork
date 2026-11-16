using System.Text.RegularExpressions;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Wpf;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using static BeMusicSeeker.TestSupport.Components.SharedPlaylistWorkspaceFixture;
using DialogService = BeMusicSeeker.TestSupport.Components.PlaylistWorkspaceFixturePorts.PlaylistWorkspaceDialogService;

namespace BeMusicSeeker.Tests;

/// <summary>beatoraja の表URL取込みの確認・通知・要約が、プレイリストの窓口へ受付の外で届くことを確認します。</summary>
/// <remarks>
/// 取込み本体（取得・DB・出力・順序）は PlaylistWorkspaceExternalSourceTests が確認します。
/// ここでは、既存の表だけで通信せずに終端まで進む入力で、表示の時機・順序・失敗・終了中の扱いだけを観測します。
/// </remarks>
[TestClass]
public sealed class BeatorajaTableUrlImportPresentationTests
{
    /// <summary>
    /// 保証：確認は受付を取得する前に窓口へ届き、未完了の間は受付を保持しない。
    /// 承認だけが取込みを開始し、取消・表示の失敗（失敗結果・失敗した Task・同期の例外）では受付も実行も要さず、
    /// 失敗は承認にも取消にも読み替えず Error の通知を一件だけ出す。確認を受付の内側で待つ誤りと、失敗を承認とみなす誤りを検出する。
    /// </summary>
    [TestMethod]
    [DataRow("cancel")]
    [DataRow("accept")]
    [DataRow("failed")]
    [DataRow("faulted")]
    [DataRow("throws")]
    public async Task Confirmation_IsAwaitedBeforeAdmissionAndOnlyAcceptanceStartsTheImport(string answer)
    {
        var sentinel = new InvalidOperationException("confirmation display failed");
        var gate = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmationArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogService dialogs = CreateDialogs();
        dialogs.ConfirmationRequestedHook = _ => confirmationArrived.TrySetResult();
        dialogs.ConfirmationTaskFactory = answer == "throws" ? _ => throw sentinel : _ => gate.Task;
        await using ExistingTableImportFixture fixture = await ExistingTableImportFixture.CreateAsync(dialogs);
        string[] configBefore = fixture.ReadConfigUrls();
        int[] orderBefore = fixture.ReadTableOrder();
        int acceptedCount = 0;

        Task<BeatorajaTableUrlImportSummary?> importTask = fixture.Workspace.StartBeatorajaTableUrlImport(
            fixture.BeatorajaRoot, () => Interlocked.Increment(ref acceptedCount));
        try
        {
            if (answer != "throws")
            {
                await TestUiDispatcherHost.AwaitNotificationAsync(confirmationArrived.Task, importTask, "beatoraja import confirmation");
                Assert.AreEqual(1, dialogs.ConfirmationRequests.Count);
                UiConfirmationRequest request = dialogs.ConfirmationRequests[0];
                Assert.AreEqual(Resources.Confirm_import_beatoraja_table_urls, request.MessageBoxText);
                Assert.AreEqual(Resources.Confirm, request.Caption);
                Assert.AreEqual(MessageBoxButton.OKCancel, request.Button);
                Assert.AreEqual(MessageBoxImage.Question, request.Icon);
                Assert.IsNull(request.Owner);
                // 未完了の確認は受付を保持せず、取込みも開始させない。
                fixture.AssertAdmissionIsAvailable();
                Assert.AreEqual(0, Volatile.Read(ref acceptedCount));
                Assert.AreEqual(0, dialogs.MessageRequests.Count);
                Assert.IsFalse(importTask.IsCompleted);
                switch (answer)
                {
                    case "cancel": gate.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel)); break;
                    case "accept": gate.SetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); break;
                    case "failed": gate.SetResult(UiDialogResult.Failed(sentinel)); break;
                    default: gate.SetException(sentinel); break;
                }
            }

            BeatorajaTableUrlImportSummary? summary = await importTask;

            fixture.AssertAdmissionIsAvailable();
            if (answer == "accept")
            {
                Assert.IsNotNull(summary);
                Assert.AreEqual(1, Volatile.Read(ref acceptedCount));
                Assert.AreEqual(2, summary.ExistingCount);
                BeatorajaTableUrlImportTestSupport.AssertOnlyBeatorajaSummaryPresented(dialogs, summary, 1);
                return;
            }

            Assert.IsNull(summary);
            Assert.AreEqual(0, Volatile.Read(ref acceptedCount));
            CollectionAssert.AreEqual(configBefore, fixture.ReadConfigUrls(), "取消・失敗では config を変えない。");
            CollectionAssert.AreEqual(orderBefore, fixture.ReadTableOrder(), "取消・失敗では DB の表の順を変えない。");
            fixture.AssertBmtFilesUnchanged();
            if (answer == "cancel")
            {
                Assert.AreEqual(0, dialogs.MessageRequests.Count, "取消では通知を出さない。");
                return;
            }
            Assert.AreEqual(1, dialogs.MessageRequests.Count, "表示の失敗は Error の通知を一件だけ出す。");
            UiMessageRequest failure = dialogs.MessageRequests[0];
            Assert.AreEqual(Resources.Error, failure.Caption);
            Assert.AreEqual(MessageBoxImage.Hand, failure.Icon);
            Assert.IsNull(failure.Owner);
        }
        finally
        {
            gate.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.Cancel));
            await importTask.ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// 保証：受付を取得した後に決まる通知（ここでは URL なし）は、受付を解放した後に窓口へ依頼され、
    /// 返す Task は表示の完了を待たずに完了する。通知を受付の内側で依頼する誤りと、表示の完了を待って Task を終えない誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task NoticeDecidedAfterAdmission_IsRequestedAfterReleaseWithoutWaitingForDisplay()
    {
        var gate = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool admissionAvailableAtRequest = false;
        DialogService dialogs = CreateDialogs();
        await using ExistingTableImportFixture fixture = await ExistingTableImportFixture.CreateAsync(dialogs, tableUrlIds: []);
        dialogs.MessageRequestedHook = _ =>
        {
            admissionAvailableAtRequest = fixture.IsAdmissionAvailable();
            messageArrived.TrySetResult();
        };
        dialogs.MessageTaskFactory = _ => gate.Task;

        Task<BeatorajaTableUrlImportSummary?> importTask = fixture.Workspace.StartBeatorajaTableUrlImport(fixture.BeatorajaRoot);
        try
        {
            await ConcurrencyProbe.AssertCompletesWhileHeldAsync(messageArrived.Task, "beatoraja-no-urls-notice-request");
            Assert.IsTrue(admissionAvailableAtRequest, "通知の要求の時点で、受付は解放済みである必要がある。");
            await ConcurrencyProbe.AssertCompletesWhileHeldAsync(importTask, "beatoraja-no-urls-task");
            fixture.AssertAdmissionIsAvailable();
            Assert.AreEqual(1, dialogs.MessageRequests.Count);
            UiMessageRequest notice = dialogs.MessageRequests[0];
            Assert.AreEqual(Resources.Beatoraja_table_url_import_no_urls, notice.MessageBoxText);
            Assert.AreEqual(Resources.Information, notice.Caption);
            Assert.AreEqual(MessageBoxImage.Information, notice.Icon);
            Assert.IsNull(notice.Owner);
        }
        finally
        {
            gate.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        Assert.IsNull(await importTask);
        Assert.AreEqual(TaskStatus.RanToCompletion, importTask.Status);
    }

    /// <summary>
    /// 保証：操作通知は受領の順に要約より前へ、どちらも受付の解放後に依頼し、返す Task は表示の完了を待たない。
    /// 表示役の順序・Owner・見出し・アイコンと、受付を保持した依頼や要約の先行、表示の完了待ちを検出する。
    /// 警告は受信側（表示役・順序・受付の解放）の確認のため、取込みの途中の同期フックから実物の通知の積み先へ積む（生成元の確認ではない）。
    /// </summary>
    [TestMethod]
    public async Task OperationNotificationThenSummary_AreRequestedInOrderAfterReleaseWithoutHoldingTheTask()
    {
        var summaryGate = new TaskCompletionSource<UiDialogResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var summaryArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var admissionAvailableAtRequests = new List<bool>();
        DialogService dialogs = CreateDialogs();
        await using ExistingTableImportFixture fixture = await ExistingTableImportFixture.CreateAsync(dialogs);
        QueueOneWarningDuringImport(fixture);
        dialogs.MessageRequestedHook = request =>
        {
            lock (admissionAvailableAtRequests) { admissionAvailableAtRequests.Add(fixture.IsAdmissionAvailable()); }
            if (request.Caption == Resources.Beatoraja_table_url_import_result_title) { summaryArrived.TrySetResult(); }
        };
        dialogs.MessageTaskFactory = request => request.Caption == Resources.Beatoraja_table_url_import_result_title
            ? summaryGate.Task
            : Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));

        Task<BeatorajaTableUrlImportSummary?> importTask = fixture.Workspace.StartBeatorajaTableUrlImport(fixture.BeatorajaRoot);
        try
        {
            await ConcurrencyProbe.AssertCompletesWhileHeldAsync(summaryArrived.Task, "beatoraja-summary-request");
            await ConcurrencyProbe.AssertCompletesWhileHeldAsync(importTask, "beatoraja-summary-task");
            fixture.AssertAdmissionIsAvailable();
            IReadOnlyList<UiMessageRequest> requests = dialogs.MessageRequests;
            Assert.AreEqual(2, requests.Count, "操作通知、要約の順に二件。");
            Assert.AreEqual(Resources.Warn_CustomFolderOutputDirInvalid, requests[0].MessageBoxText);
            Assert.AreEqual(Resources.MessageBoxTitle_Warning, requests[0].Caption);
            Assert.AreEqual(MessageBoxImage.Exclamation, requests[0].Icon);
            Assert.IsNull(requests[0].Owner);
            Assert.AreEqual(Resources.Beatoraja_table_url_import_result_title, requests[1].Caption);
            Assert.AreEqual(MessageBoxImage.Information, requests[1].Icon);
            Assert.AreEqual(string.Format(Resources.Beatoraja_table_url_import_result_summary_format, 2, 0, 0, 0, 0), requests[1].MessageBoxText);
            Assert.IsNull(requests[1].Owner);
            lock (admissionAvailableAtRequests)
            {
                CollectionAssert.AreEqual(new[] { true, true }, admissionAvailableAtRequests, "どちらの要求の時点でも受付は解放済みである必要がある。");
            }
        }
        finally
        {
            summaryGate.TrySetResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK));
        }

        BeatorajaTableUrlImportSummary summary = await BeatorajaTableUrlImportTestSupport.RequireSummaryAsync(importTask);
        Assert.AreEqual(2, summary.ExistingCount);
        Assert.AreEqual(dialogs.MessageRequests[1].MessageBoxText, summary.FormatMessage());
        CollectionAssert.AreEqual(new[] { 3, 1, 2 }, fixture.ReadTableOrder());
    }

    /// <summary>
    /// 保証：通知または要約の表示が失敗（失敗結果・失敗した Task・同期の例外）しても、
    /// 取込みの結果（Task の完了・DB・config）を変えず、残りの表示を続ける。任意の通知の失敗で取込みを失敗にする誤りを検出する。
    /// 失敗した組ごとに、表示役が出す診断（file_db_mutation_notification_failed）が元の例外つきで一件だけ残ることも確認する。
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    [DataRow("notification", "failed")]
    [DataRow("notification", "faulted")]
    [DataRow("notification", "throws")]
    [DataRow("summary", "failed")]
    [DataRow("summary", "faulted")]
    [DataRow("summary", "throws")]
    public async Task PresentationFailure_IsLoggedOnlyAndDoesNotChangeTheImportResult(string target, string form)
    {
        var sentinel = new InvalidOperationException("presentation failed");
        await ApplicationLoggingTestScope.RunAsync(async loggingScope =>
        {
            string logPath = loggingScope.ConfigureFileLog();
            DialogService dialogs = CreateDialogs();
            await using ExistingTableImportFixture fixture = await ExistingTableImportFixture.CreateAsync(dialogs);
            QueueOneWarningDuringImport(fixture);
            bool IsTarget(UiMessageRequest request) => (request.Caption == Resources.Beatoraja_table_url_import_result_title) == (target == "summary");
            dialogs.MessageTaskFactory = request =>
            {
                if (!IsTarget(request)) { return Task.FromResult(UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)); }
                return form switch
                {
                    "failed" => Task.FromResult(UiDialogResult.Failed(sentinel)),
                    "faulted" => Task.FromException<UiDialogResult>(sentinel),
                    _ => throw sentinel,
                };
            };

            BeatorajaTableUrlImportSummary? summary = await fixture.Workspace.StartBeatorajaTableUrlImport(fixture.BeatorajaRoot);
            // 窓口の応答は完了済みの Task または同期の例外なので、表示役の依頼と診断は、取込みの Task の完了までに同期で終わっています。

            Assert.IsNotNull(summary, "表示の失敗で取込みの結果を失わない。");
            Assert.AreEqual(2, summary.ExistingCount);
            Assert.AreEqual(2, dialogs.MessageRequests.Count, "ある表示が失敗しても残りの表示を続ける。");
            string log = ApplicationLoggingTestScope.ReadFileLog(logPath);
            Assert.AreEqual(1, Regex.Count(log, "file_db_mutation_notification_failed"), "失敗した組について診断をちょうど一件残す。");
            StringAssert.Contains(log, nameof(InvalidOperationException), "診断に元の例外の型が付く。");
            StringAssert.Contains(log, sentinel.Message, "診断に元の例外のメッセージが付く。");
            CollectionAssert.AreEqual(new[] { 3, 1, 2 }, fixture.ReadTableOrder());
            CollectionAssert.AreEqual(new[] { "https://example.com/table3", "https://example.com/table1" }, fixture.ReadConfigUrls());
            fixture.AssertAdmissionIsAvailable();
        });
    }

    /// <summary>
    /// 保証：受付の後に終了が要求された場合は、操作通知も要約も表示しない。取込みの結果は保持し、Task は完了する。
    /// 終了中の窓口へ表示を依頼して終了を妨げる誤りと、表示を省く代わりに取込みを巻き戻す誤りを検出する。
    /// </summary>
    [TestMethod]
    public async Task ShutdownRequestedAfterAdmission_SuppressesPresentationButKeepsTheImportResult()
    {
        bool shutdownRequested = false;
        DialogService dialogs = CreateDialogs();
        await using ExistingTableImportFixture fixture = await ExistingTableImportFixture.CreateAsync(
            dialogs, shutdownRequested: () => Volatile.Read(ref shutdownRequested));
        QueueOneWarningDuringImport(fixture);

        BeatorajaTableUrlImportSummary? summary = await fixture.Workspace.StartBeatorajaTableUrlImport(
            fixture.BeatorajaRoot, () => Volatile.Write(ref shutdownRequested, true));

        Assert.IsNotNull(summary, "終了中でも取込みの結果は保持する。");
        Assert.AreEqual(2, summary.ExistingCount);
        Assert.AreEqual(0, dialogs.MessageRequests.Count, "終了中は通知も要約も表示しない。");
        CollectionAssert.AreEqual(new[] { 3, 1, 2 }, fixture.ReadTableOrder());
        CollectionAssert.AreEqual(new[] { "https://example.com/table3", "https://example.com/table1" }, fixture.ReadConfigUrls());
    }

    private static DialogService CreateDialogs() => BeatorajaTableUrlImportTestSupport.CreateBeatorajaImportDialogs();

    /// <summary>取込みの流れの中で同期に呼ばれる進捗の購読から、実物の通知の積み先へ警告を一件だけ積みます。</summary>
    private static void QueueOneWarningDuringImport(ExistingTableImportFixture fixture)
    {
        int queued = 0;
        fixture.Workspace.PlaylistSyncProgressChanged += (_, request) =>
        {
            if (request.Snapshot?.Kind == PlaylistSyncProgressKind.BeatorajaTableUrlImport
                && Interlocked.Exchange(ref queued, 1) == 0)
            {
                fixture.Playlist.OperationNotificationOwner.QueueWarning(
                    PlaylistOperationNotificationOwner.OperationNotificationText.FromResource(nameof(Resources.Warn_CustomFolderOutputDirInvalid)));
            }
        };
    }

    /// <summary>既存の表三件とbeatorajaの設定を、通信なしで取込みの終端まで進む入力として用意します。</summary>
    private sealed class ExistingTableImportFixture : IAsyncDisposable
    {
        private readonly TestWorkspace ownedWorkspace;
        private readonly string directory;
        private readonly string databasePath;
        private readonly string outputDirectory;
        private readonly ApplicationDatabaseScope databaseScope;
        private readonly Dictionary<string, byte[]> bmtBytes;

        private ExistingTableImportFixture(
            TestWorkspace ownedWorkspace,
            string directory,
            string databasePath,
            string beatorajaRoot,
            string outputDirectory,
            ApplicationDatabaseScope databaseScope,
            TestBmsPlaylist playlist,
            PlaylistWorkspaceViewModel workspace,
            Dictionary<string, byte[]> bmtBytes)
        {
            this.ownedWorkspace = ownedWorkspace;
            this.directory = directory;
            this.databasePath = databasePath;
            BeatorajaRoot = beatorajaRoot;
            this.outputDirectory = outputDirectory;
            this.databaseScope = databaseScope;
            Playlist = playlist;
            Workspace = workspace;
            this.bmtBytes = bmtBytes;
        }

        internal string BeatorajaRoot { get; }

        internal TestBmsPlaylist Playlist { get; }

        internal PlaylistWorkspaceViewModel Workspace { get; }

        /// <param name="dialogs">workspace が表示を依頼する窓口。</param>
        /// <param name="tableUrlIds">beatoraja の設定へ入力順で書く既存の表の番号。省略時は 3、1。</param>
        /// <param name="shutdownRequested">終了の要求を表す判定。省略時は終了しない。</param>
        /// <param name="warningLog">取込みの警告の診断先。</param>
        internal static async Task<ExistingTableImportFixture> CreateAsync(
            DialogService dialogs,
            int[]? tableUrlIds = null,
            Func<bool>? shutdownRequested = null,
            Action<Exception, string>? warningLog = null)
        {
            var ownedWorkspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
            var databaseScope = new ApplicationDatabaseScope();
            string directory = Path.Combine(ownedWorkspace.RootDirectory, nameof(BeatorajaTableUrlImportPresentationTests), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string db = PlaylistTestFixture.CreateTempSongDbPath(databaseScope, directory);
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, db);
            string root = Path.Combine(directory, "beatoraja");
            string output = Path.Combine(root, "table");
            Directory.CreateDirectory(output);
            File.WriteAllText(Path.Combine(root, "beatoraja.jar"), string.Empty);
            var options = new BeatorajaBmtOptionsSnapshot { EnableBeatorajaBmtOutput = true, BeatorajaRootPath = root, RegisterBeatorajaBmtUrls = true };
            BMSTable[] tables = Enumerable.Range(1, 3).Select(id => new BMSTable
            {
                playlist_id = id,
                name = "Table " + id,
                bmt_sort = id,
                is_external_sync = true,
                Page_url = new Uri("https://example.com/table" + id),
                last_update = new DateTime(2024, 1, 1),
                Folder_order = ["Folder"],
                entries = [new BMSTableEntry { title = "Song " + id, md5 = new string((char)('a' + id), 32), folder = "Folder" }]
            }).ToArray();
            var playlist = new TestBmsPlaylist(databaseScope, db, null, null, null, () => new PlaylistUrlCompletionOptionsSnapshot(), () => options, () => new CustomFolderOutputSettingsSnapshot(), PlaylistTestFixture.CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, db, CustomFolderOutputPhysicalSurface.Empty), uiScheduler: new TestUiScheduler(() => null))
            { BMSTables = new System.Collections.ObjectModel.ObservableCollection<BMSTable>(tables) };
            playlist.CommitBMSTablesWithEntriesToDB(tables);
            await playlist.BmtOutput.ExportAllAsync("import-initial");
            // 初期の出力が管理するURLをconfigへ登録するため、取込みの入力はその後に書き直す。
            int[] ids = tableUrlIds ?? [3, 1];
            File.WriteAllText(
                Path.Combine(root, BeatorajaConfigService.ConfigFileName),
                new Newtonsoft.Json.Linq.JObject
                {
                    ["tablepath"] = "table",
                    ["tableURL"] = new Newtonsoft.Json.Linq.JArray(ids.Select(id => "https://example.com/table" + id))
                }.ToString());
            Dictionary<string, byte[]> bytes = Directory.GetFiles(output).ToDictionary(path => path, File.ReadAllBytes);
            var library = new TestBmsLibrary(databaseScope, db, null, null, null, new UnexpectedBmsLibraryDialogService(), new TestUiScheduler(() => null));
            PlaylistWorkspaceViewModel workspace = CreateDetailWorkspace(out _, playlistStoreProvider: () => playlist,
                playlistLibraryProvider: () => library,
                playlistSummaryBmtSort: new PlaylistSummaryBmtSortCoordinator(() => playlist, () => playlist.BMSTables),
                reloadCleanupShutdownRequestedProvider: shutdownRequested,
                playlistWorkspaceDialogService: dialogs,
                beatorajaTableUrlImportWarningLog: warningLog);
            workspace.RefreshPlaylistTreeTables(playlist, library);
            workspace.IsPlaylistSummaryMode = true;
            workspace.RequestPlaylistSummarySort(nameof(PlaylistSummaryRow.BmtSort), System.ComponentModel.ListSortDirection.Ascending);
            workspace.ApplyPlaylistSummaryDataRefresh(deferred: false, rebuildAsync: false);
            return new ExistingTableImportFixture(ownedWorkspace, directory, db, root, output, databaseScope, playlist, workspace, bytes);
        }

        internal string[] ReadConfigUrls() => [.. BeatorajaConfigService.ReadTableUrls(BeatorajaRoot)];

        /// <summary>DB に確定した表の並びを、BMT の順で読み戻します。</summary>
        internal int[] ReadTableOrder()
        {
            using LR2SongDBExtended verify = new BmsLibraryDbGateway(databaseScope, new BmsDatabaseLoadOptions(true, true, true), databasePath).OpenSongDbReadOnly();
            return [.. verify.Table<BMSTable>().OrderBy(table => table.bmt_sort).Select(table => table.playlist_id).Select(id => id ?? 0)];
        }

        internal void AssertBmtFilesUnchanged()
        {
            foreach ((string path, byte[] expected) in bmtBytes)
            {
                CollectionAssert.AreEqual(expected, File.ReadAllBytes(path), "取消・失敗では BMT を書き換えない。");
            }
            CollectionAssert.AreEquivalent(bmtBytes.Keys.ToArray(), Directory.GetFiles(outputDirectory));
        }

        /// <summary>受付が空いていることを、実際に受付を取得して確かめ、すぐに解放します。</summary>
        internal bool IsAdmissionAvailable()
        {
            if (!Playlist.TryEnterPlaylistMutation(out IDisposable? admission))
            {
                return false;
            }
            admission.Dispose();
            return true;
        }

        internal void AssertAdmissionIsAvailable() => Assert.IsTrue(IsAdmissionAvailable(), "受付が空いている必要がある。");

        public async ValueTask DisposeAsync()
        {
            try
            {
                Workspace.StopPlaylistSummaryDataBuild();
                await Workspace.WaitForPlaylistSummaryDataBuildIdleAsync();
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
                ownedWorkspace.Dispose();
            }
        }
    }
}

/// <summary>beatoraja の取込みの窓口への表示を観測する、複数のテストクラスで共用する補助です。</summary>
internal static class BeatorajaTableUrlImportTestSupport
{
    /// <summary>確認へ OK を返し、通知を即時に完了させる窓口を作ります。届いた要求は件数と内容で確認します。</summary>
    internal static DialogService CreateBeatorajaImportDialogs() => new()
    {
        ConfirmationResult = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK)
    };

    /// <summary>取込みを開始した場合に返る要約を取り出します。開始しなかった場合は失敗にします。</summary>
    internal static async Task<BeatorajaTableUrlImportSummary> RequireSummaryAsync(Task<BeatorajaTableUrlImportSummary?> operation)
        => await operation ?? throw new AssertFailedException("取込みを開始した場合は、実終端の Task が要約を返す必要があります。");

    /// <summary>取込みを開始した場合に返る要約を、所有する操作として回収しながら取り出します。</summary>
    internal static async Task<BeatorajaTableUrlImportSummary> RequireSummaryAsync(
        Task<BeatorajaTableUrlImportSummary?> operation, ICollection<Task> ownedOperations, CancellationToken operationToken)
        => await SingleRequestHttpServer.AwaitOperationAsync(operation, ownedOperations, operationToken)
            ?? throw new AssertFailedException("取込みを開始した場合は、実終端の Task が要約を返す必要があります。");

    /// <summary>
    /// 窓口へ届いた通知が要約だけであり、最後の要求が渡した要約の本文・見出し・アイコン・Owner なしであることを確認します。
    /// 取込みの開始前の拒否や失敗の通知（Busy・初期化中・root不正・URLなし・開始失敗）は届きません。
    /// </summary>
    internal static void AssertOnlyBeatorajaSummaryPresented(DialogService dialogs, BeatorajaTableUrlImportSummary summary, int expectedSummaryCount)
    {
        IReadOnlyList<UiMessageRequest> requests = dialogs.MessageRequests;
        Assert.AreEqual(expectedSummaryCount, requests.Count, "要約以外の通知が届いてはいけない。");
        UiMessageRequest last = requests[^1];
        Assert.AreEqual(Resources.Beatoraja_table_url_import_result_title, last.Caption);
        Assert.AreEqual(summary.FormatMessage(), last.MessageBoxText);
        Assert.AreEqual(summary.HasFailureOrWarning ? MessageBoxImage.Exclamation : MessageBoxImage.Information, last.Icon);
        Assert.IsNull(last.Owner);
        string[] notificationTexts =
        [
            Resources.Warn_LibraryOperationBusy,
            Resources.Error_InvalidBeatorajaRootPath,
            Resources.Beatoraja_table_url_import_no_urls,
            Resources.Msg_settings_apply_blocked_during_initialization,
        ];
        foreach (UiMessageRequest request in requests)
        {
            CollectionAssert.DoesNotContain(notificationTexts, request.MessageBoxText);
            Assert.IsFalse(request.MessageBoxText.StartsWith(Resources.Msg_failed_load_playlist, StringComparison.Ordinal));
        }
    }
}
