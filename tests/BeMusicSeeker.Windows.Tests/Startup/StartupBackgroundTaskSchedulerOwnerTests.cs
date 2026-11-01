using System.Collections.Concurrent;
using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Models.LR2;
using BeMusicSeeker.TestSupport.Components;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using static BeMusicSeeker.TestSupport.Components.StartupBackgroundTaskSchedulerOwnerTestSupport;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class StartupBackgroundTaskSchedulerOwnerTests
{
    private readonly ApplicationDatabaseScope databaseScope = new();

    /// <summary>
    /// 実playlist URL補完の受付を新旧ExecutionContextから呼び、捕捉世代を保ちながら現行要求だけを独立行へ表示する。
    /// </summary>
    /// <remarks>実playlist受付と進捗hubへの接続を使い、scheduler単体では観測できない世代捕捉と表示選別を確認する。</remarks>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaylistUrlCompletion_RealAcceptanceCapturesIndependentCurrentGenerationOrRetainsOldContext(bool oldContext)
    {
        using var caseWorkspace1 = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        string directory = Path.Combine(caseWorkspace1.RootDirectory, nameof(StartupBackgroundTaskSchedulerOwnerTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? acceptance = null;
        StartupBackgroundTaskSchedulerOwner owner = CreateOwner();
        try
        {
            string songDbPath = Path.Combine(directory, "song.db");
            using (LR2SongDBExtended db = databaseScope.OpenSongWrite(songDbPath)) { }
            PlaylistPersistenceRepository.EnsureSchema(databaseScope, songDbPath);
            var playlist = new TestBmsPlaylist(databaseScope, songDbPath, null, null, null,
                () => new PlaylistUrlCompletionOptionsSnapshot(),
                () => new BeatorajaBmtOptionsSnapshot(),
                () => new CustomFolderOutputSettingsSnapshot(),
                new TestLr2PlaylistFolderSynchronizationPort(databaseScope, songDbPath))
            { BMSTables = [] };
            playlist.StartupBackgroundTaskScheduler = (name, reason, dependency, work) => owner.Queue(name, reason, dependency, work);
            var hub = new OperationProgressHubViewModel(TestStartupProgressOwnerFactory.Create());
            var notifications = new ConcurrentQueue<StartupBackgroundTaskProgressSnapshot>();
            bool displayed = false;
            bool displayedAsChild = false;
            owner.ProgressChanged += status =>
            {
                if (status.Name != "playlist_url_completion") { return; }
                notifications.Enqueue(status);
                hub.UpdateBackgroundTaskProgress(status);
                if (status.IsRunning)
                {
                    displayed = hub.Rows.Any(row => row.Key.StartsWith("background:playlist_url_completion:", StringComparison.Ordinal));
                    displayedAsChild = hub.Rows.Any(row => row.Key.StartsWith("background:playlist_url_completion:", StringComparison.Ordinal) && row.IsChild);
                }
            };
            owner.Reset(false, 11);
            long originalGeneration = owner.CurrentGeneration;
            async Task accept()
            {
                await release.Task;
                playlist.SchedulePlaylistUrlCompletionRefresh("SettingDialog.SaveSettings");
            }
            if (oldContext) { acceptance = Task.Run(accept); }
            else { using (ExecutionContext.SuppressFlow()) { acceptance = Task.Run(accept); } }
            owner.Reset(false, 22);
            long currentGeneration = owner.CurrentGeneration;
            hub.BeginBackgroundProgressGeneration(currentGeneration);
            hub.BeginStartupBackgroundInitializationPresentation(22, currentGeneration);
            owner.Queue("chart_info_hydration", "startup_completed", null, () => Task.CompletedTask);
            owner.Start();
            await WaitForFullyIdleAsync(owner);
            release.TrySetResult(true);
            await acceptance;
            await WaitForFullyIdleAsync(owner);
            StartupBackgroundTaskProgressSnapshot[] captured = notifications.ToArray();
            Assert.AreEqual(2, captured.Length);
            Assert.AreEqual(oldContext ? originalGeneration : currentGeneration, captured[0].Generation);
            Assert.AreEqual(oldContext ? 11L : 0L, captured[0].Request.OperationToken);
            Assert.AreEqual("scheduler:playlist_url_completion", captured[0].Request.Source);
            Assert.AreEqual(captured[0].Request, captured[1].Request);
            Assert.AreEqual(!oldContext, displayed);
            Assert.IsFalse(displayedAsChild);
            Assert.IsFalse(hub.Rows.Any(row => row.Key.StartsWith("background:playlist_url_completion:", StringComparison.Ordinal)));
        }
        finally
        {
            release.TrySetResult(true);
            if (acceptance != null) { await acceptance; }
            owner.RequestShutdown("test_cleanup");
            if (owner.IsStarted) { await WaitForFullyIdleAsync(owner); }
            Directory.Delete(directory, recursive: true);
        }
    }

}
