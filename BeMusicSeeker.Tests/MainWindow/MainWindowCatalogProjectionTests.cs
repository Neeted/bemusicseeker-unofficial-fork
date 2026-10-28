using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SQLite;

namespace BeMusicSeeker.Tests;

/// <summary>標準構成の初期化・実走査保存・通知・通常一覧とプレイリスト表示の接続を検証します。</summary>
[TestClass]
[DoNotParallelize] // 標準構成の共有通知購読をshutdownで解放するまで所有します。
public sealed class MainWindowCatalogProjectionTests
{
    /// <summary>二配置の削除・最後の配置の削除・再加入が実手動リロードから全表示へ収束し、エントリー保存値を変えないことを確認します。</summary>
    /// <remarks>通知欠落と画面接続の誤りを観測するため、Windowなしの実Dispatcherで標準構成とDBを動かします。通常ルートの表示完了は要求が返すTaskから確認し、外部の譜面発見だけを完全な捕捉入力へ代替します。</remarks>
    [TestMethod]
    public void ManualReload_StandardCompositionConvergesNormalDetailAndSummary()
    {
        TestUiDispatcherHost.Invoke(() =>
        {
            string root = Path.Combine(Path.GetTempPath(), "CatalogProjection-" + Guid.NewGuid().ToString("N"));
            string music = Path.Combine(root, "Music");
            Directory.CreateDirectory(music);
            string first = Path.Combine(music, "first.bms");
            string second = Path.Combine(music, "second.bms");
            string keep = Path.Combine(music, "keep.bms");
            const string ownedContents = "#PLAYER 1\n#TITLE Owned chart\n#ARTIST Fixture\n#BPM 120\n#00111:01\n";
            File.WriteAllText(first, ownedContents);
            File.WriteAllText(second, ownedContents);
            File.WriteAllText(keep, "#PLAYER 1\n#TITLE Keep\n#ARTIST Fixture\n#BPM 120\n#00111:0101\n");
            var scanner = new ReloadScanner(music);
            scanner.Capture(first, second, keep);
            Settings settings = MainWindowViewModelTestFactory.CreateIsolatedSettings(values =>
            {
                values.OperationModeLR2DB = false;
                values.BMSRootPath = music;
                values.StandaloneBmsRootPaths = music;
                values.BMSInstallDir = music;
                values.ScanBmsFilesOnStartup = true;
                values.SkipInitPlaylistLoad = true;
                values.EnablePlaylistUrlCompletion = false;
                values.EnableBeatorajaBmtOutput = false;
                values.UseBeatorajaScoreDb = false;
                values.UsePlayeruBMplay = false;
                values.UsePlayerLR2body = false;
                values.UsePlayerBMIIDXView = false;
                values.IsLR2BackupEnabled = false;
                values.TableListURL = new Uri("http://127.0.0.1:1/table-list.json");
            });
            var paths = ApplicationPathSnapshot.FromExecutablePath(Path.Combine(root, "BeMusicSeeker.exe"));
            Directory.CreateDirectory(paths.DataDirectoryPath);
            StartupLibraryConstructionTestSupport.CreateSongDatabase(paths.StandaloneSongDbPath);
            string hash = Convert.ToHexStringLower(MD5.HashData(File.ReadAllBytes(first)));
            var entry = new BMSTableEntry { playlist_id = 1, md5 = hash, title = "Saved entry", artist = "Saved artist", level = 12, folder = "Local", memo = "Saved memo" };
            var savedTable = new BMSTable
            {
                playlist_id = 1,
                name = "Local table",
                symbol = "L",
                is_external_sync = false,
                ignore_folder_output = LR2SongDBExtended.playlist.CustomFolderType.AllFolders,
                entries = [entry]
            };
            PlaylistPersistenceRepository.EnsureSchema(paths.StandaloneSongDbPath);
            new PlaylistPersistenceRepository(paths.StandaloneSongDbPath).ReplaceTablesWithEntries([savedTable]);
            var dialogs = new MainWindowViewModelStartupProgressTests.CompletionDialogs();
            var delayedUi = new DelayedDrainUiScheduler(new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher));
            var composition = new ApplicationComposition(settingsEditSession: new NoOpSettingsEditSession(settings),
                uiScheduler: delayedUi,
                applicationLifetime: new TestApplicationLifetime(firstStartup: false),
                cultureCatalog: TestApplicationContext.CreateCultureCatalog(), applicationPathSnapshot: paths,
                chartFileScanner: scanner, rootFileEnumerator: new FastRootFileEnumerator(),
                fileDbMutationDialogService: dialogs, settingsDialogService: dialogs, playlistWorkspaceDialogService: dialogs);
            var owner = new MainWindowViewModel(composition, composition);
            try
            {
                Task<bool> initialization = owner.InitializeAsync();
                Await(initialization, "catalog-projection-startup");
                Assert.IsTrue(initialization.GetAwaiter().GetResult());
                Await(WaitForStartupBackgroundAsync(owner), "catalog-projection-startup-background");
                BMSPlaylist store = Field<BMSPlaylist>(owner, "tables");
                RegularChartListOwner regular = Field<RegularChartListOwner>(owner, "regularChartListOwner");
                ShowNormal();
                AssertNormal(first, second, keep);
                BMSTable table = store.BMSTables.Single(item => item.playlist_id == savedTable.playlist_id);
                string[] saved = ReadSavedEntries();
                ShowDetail();
                Assert.IsTrue(Detail().IsOwned);
                AssertSummary(1);
                ShowNormal();

                File.Delete(first);
                scanner.Capture(second, keep);
                Reload();
                AssertNormal(second, keep);
                ShowDetail();
                Assert.IsTrue(Detail().IsOwned);
                Assert.AreEqual(second, Detail().path);

                File.Delete(second);
                scanner.Capture(keep);
                Reload();
                Assert.IsFalse(Detail().IsOwned);
                Assert.AreEqual(string.Empty, Detail().path);
                Assert.AreEqual("Saved entry", Detail().Title);
                Assert.AreEqual("12", Detail().Level);
                Assert.IsTrue(GridRowResolver.TryGetChartOperationTarget(Detail(), out ChartOperationTarget missing));
                Assert.IsFalse(missing.HasCapability(ChartOperationCapabilities.OpenFile));
                owner.PlaylistWorkspace.RequestDetailSelection(table, PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
                Await(owner.PlaylistWorkspace.WaitForDetailBuildIdleAsync(), "catalog-projection-missing");
                Assert.AreEqual(1, owner.MainChartList.Rows.Count);
                AssertSummary(0);
                ShowNormal();
                AssertNormal(keep);

                string added = Path.Combine(music, "added.bms");
                File.WriteAllText(added, ownedContents);
                scanner.Capture(keep, added);
                // 実通常drainだけを遅らせ、外側の再読込みTaskが同操作の通知と必要表示を終えるまで返らないことを確認する。
                delayedUi.Arm();
                Task reload = owner.ReloadFileDiffAsync();
                Task drain = Task.CompletedTask;
                Exception? reloadFailure = null;
                try
                {
                    Await(TestUiDispatcherHost.AwaitNotificationAsync(delayedUi.Reserved, reload, "catalog-projection-drain-reserved"), "catalog-projection-drain-reserved");
                    Assert.IsFalse(reload.IsCompleted, "通常通知が未配送の間は再読込みの必要表示が完了しません。");
                    drain = delayedUi.Release();
                    Await(reload, "catalog-projection-delayed-reload");
                    Await(drain, "catalog-projection-delayed-drain");
                }
                catch (Exception failure) { reloadFailure = failure; throw; }
                finally
                {
                    drain = delayedUi.Release();
                    try { Await(Task.WhenAll(reload, drain), "catalog-projection-delayed-cleanup"); }
                    catch (Exception cleanupFailure) when (reloadFailure != null) { reloadFailure.Data["CatalogProjectionReloadCleanupFailure"] = cleanupFailure; }
                }
                AssertNormal(keep, added);
                owner.PlaylistWorkspace.RequestDetailSelection(table, PlaylistFolderNode.CreateSpecial(PlaylistFolderNodeSpecialKind.NotOwned));
                Await(owner.PlaylistWorkspace.WaitForDetailBuildIdleAsync(), "catalog-projection-missing-after-add");
                Assert.AreEqual(0, owner.MainChartList.Rows.Count);
                ShowDetail();
                Assert.IsTrue(Detail().IsOwned);
                Assert.AreEqual(added, Detail().path);
                AssertSummary(1);
                CollectionAssert.AreEqual(saved, ReadSavedEntries());

                void Reload() => Await(owner.ReloadFileDiffAsync(), "catalog-projection-manual-reload");
                void ShowNormal()
                {
                    var arrived = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
                    EventHandler<RegularChartTreeNavigationPresentationRequestedEventArgs> requested = (_, args) => arrived.TrySetResult(args.Presentation);
                    regular.TreeNavigationPresentationRequested += requested;
                    Exception? failure = null;
                    try
                    {
                        Assert.IsTrue(regular.NavigateTree(null));
                        Await(arrived.Task, "catalog-projection-normal-typed-arrival");
                        Await(arrived.Task.GetAwaiter().GetResult(), "catalog-projection-normal-presentation");
                    }
                    catch (Exception exception) { failure = exception; throw; }
                    finally
                    {
                        try
                        {
                            // 実際に開始した表示Taskだけを回収する。画面移動の外側Taskは既存の画面停止処理が回収する。
                            if (arrived.Task.IsCompletedSuccessfully)
                            { Await(arrived.Task.GetAwaiter().GetResult(), "catalog-projection-normal-presentation-cleanup"); }
                        }
                        catch (Exception cleanupFailure) when (failure != null) { failure.Data["CatalogProjectionNormalCleanupFailure"] = cleanupFailure; }
                        finally { regular.TreeNavigationPresentationRequested -= requested; }
                    }
                }
                void ShowDetail()
                {
                    owner.PlaylistWorkspace.RequestDetailSelection(table);
                    Await(owner.PlaylistWorkspace.WaitForDetailBuildIdleAsync(), "catalog-projection-detail");
                    Assert.AreEqual(1, owner.MainChartList.Rows.Count);
                }
                PlaylistDetailRow Detail() => owner.MainChartList.Rows[0] as PlaylistDetailRow ?? throw new AssertFailedException("詳細行がありません。");
                void AssertSummary(int owned)
                {
                    var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void Applied(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
                    {
                        if (args.PropertyName is nameof(PlaylistWorkspaceViewModel.PlaylistSummaryView) or nameof(PlaylistWorkspaceViewModel.IsPlaylistSummaryMode)
                            && owner.PlaylistWorkspace.IsPlaylistSummaryMode) { applied.TrySetResult(); }
                    }
                    owner.PlaylistWorkspace.PropertyChanged += Applied;
                    try
                    {
                        owner.PlaylistWorkspace.RequestSummarySelection();
                        Await(owner.PlaylistWorkspace.WaitForPlaylistSummaryDataBuildIdleAsync(), "catalog-projection-summary-data");
                        // データworkerの終了後にもDispatcherへの実採用が残るため、採用済みmode/Rowsの通知まで観測します。
                        TestUiDispatcherHost.AwaitPresentationOnDispatcher(applied.Task, "catalog-projection-summary-applied");
                        PlaylistSummaryRow row = owner.PlaylistWorkspace.PlaylistSummaryView.Single(candidate => candidate.PlaylistId == table.playlist_id);
                        Assert.AreEqual(1, row.TotalCharts);
                        Assert.AreEqual(owned, row.OwnedCharts);
                        Assert.AreEqual(1 - owned, row.MissingCharts);
                    }
                    finally { owner.PlaylistWorkspace.PropertyChanged -= Applied; }
                }
                void AssertNormal(params string[] expected)
                {
                    CollectionAssert.AreEquivalent(expected, owner.MainChartList.Rows.Cast<LibraryChartRow>().Select(row => row.path).ToArray());
                    using var read = new LR2SongDBExtended(paths.StandaloneSongDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                    CollectionAssert.AreEquivalent(expected, read.Table<LR2SongDB.song>().Select(song => song.path).ToArray());
                }
                string[] ReadSavedEntries()
                {
                    using var read = new LR2SongDBExtended(paths.StandaloneSongDbPath, SQLiteOpenFlags.ReadOnly, acquireProcessLock: false);
                    return read.Table<LR2SongDBExtended.playlist_entry>().Select(item => $"{item.playlist_id}|{item.md5}|{item.title}|{item.artist}|{item.level}|{item.folder}|{item.memo}").ToArray();
                }
            }
            finally
            {
                try { Await(owner.ShellShutdownWorkflow.RequestWindowCloseAsync(), "catalog-projection-shutdown"); }
                finally { owner.SettingDialog.Dispose(); Directory.Delete(root, recursive: true); }
            }
        });
    }

    /// <summary>実schedulerの任意作業の終端を観測し、次の手動操作の受付と競合させません。</summary>
    private static async Task WaitForStartupBackgroundAsync(MainWindowViewModel owner)
    {
        StartupBackgroundTaskSchedulerOwner scheduler = Field<StartupBackgroundTaskSchedulerOwner>(owner, "startupBackgroundTaskScheduler");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (!owner.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle) { completion.TrySetResult(); }
        }
        owner.ProgressHub.PropertyChanged += Changed;
        try
        {
            if (!owner.ProgressHub.IsStartupBackgroundInitializationActive && scheduler.IsFullyIdle) { return; }
            await completion.Task;
        }
        finally { owner.ProgressHub.PropertyChanged -= Changed; }
    }

    private static void Await(Task task, string reason) => TestUiDispatcherHost.AwaitTaskOnDispatcher(task, reason);
    private static T Field<T>(object target, string name) => (T)(target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)
        ?? throw new AssertFailedException(name));

    /// <summary>再加入の通常ownerのdrainだけを一回保留し、解放後は実Dispatcherへ配送します。</summary>
    private sealed class DelayedDrainUiScheduler(IUiScheduler inner) : IUiScheduler
    {
        private readonly TaskCompletionSource reserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ScheduledScoreOperation? held;
        private Task release = Task.CompletedTask;
        private int armed;
        internal Task Reserved => reserved.Task;
        internal void Arm() => Interlocked.Exchange(ref armed, 1);
        internal Task Release()
        {
            Interlocked.Exchange(ref armed, 0);
            ScheduledScoreOperation? operation = Interlocked.Exchange(ref held, null);
            if (operation != null)
            {
                release = inner.InvokeAsync(() =>
                {
                    try { operation.Run(); }
                    finally { operation.Complete(); }
                });
            }
            return release;
        }
        public bool IsAvailable => inner.IsAvailable;
        public bool CanExecuteInline => inner.CanExecuteInline;
        public bool CheckAccess() => inner.CheckAccess();
        public IUiScheduledOperation Schedule(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal)
        {
            // 通常owner自身のdrainだけを識別し、foldertreeの遅延配送をgateと取り違えない。
            if (priority == UiSchedulePriority.Normal && action.Target?.GetType().DeclaringType == typeof(RegularChartListOwner)
                && Interlocked.CompareExchange(ref armed, 0, 1) == 1)
            {
                var operation = new ScheduledScoreOperation(action, accepted: true);
                held = operation;
                reserved.TrySetResult();
                return operation;
            }
            return inner.Schedule(action, priority);
        }
        public void Invoke(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public T Invoke<T>(Func<T> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.Invoke(action, priority);
        public Task InvokeAsync(Action action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
        public Task InvokeAsync(Func<Task> action, UiSchedulePriority priority = UiSchedulePriority.Normal) => inner.InvokeAsync(action, priority);
    }

    /// <summary>操作前に捕捉した完全な発見結果だけを返し、走査以降の解析・保存・通知は本番へ渡します。</summary>
    private sealed class ReloadScanner(string root) : IChartFileScanner
    {
        private CapturedChartFileScanner captured = CapturedChartFileScanner.FromFixture([], new Dictionary<string, IEnumerable<string>>(), []);
        internal void Capture(params string[] paths) => captured = CapturedChartFileScanner.FromFixture(paths,
            new Dictionary<string, IEnumerable<string>> { [root] = [] }, [root]);
        public ChartScanExecutionResult Scan(IEnumerable<string> roots, IEnumerable<string> extensions, bool verboseLog = false,
            bool includeTextSurface = true, bool includeDirectorySurface = false)
            => captured.Scan(roots, extensions, verboseLog, includeTextSurface, includeDirectorySurface);
    }
}
