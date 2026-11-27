using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.Properties;
using BeMusicSeeker.TestSupport.Components;
using static BeMusicSeeker.TestSupport.Components.PlaylistTestFixture;

namespace BeMusicSeeker.Tests;

/// <summary>外部難度表の同時取得数が、操作の開始時の設定値に従うことを実際のloopbackサーバーで確認します。</summary>
public sealed partial class BmsPlaylistExternalReloadTests
{
    private TestBmsPlaylist CreateConcurrencyPlaylist(Settings settings)
    {
        string directory = Path.Combine(CaseWorkspace.RootDirectory, "external-concurrency-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string songPath = CreateTempSongDbPath(databaseScope, directory);
        PlaylistPersistenceRepository.EnsureSchema(databaseScope, songPath);
        return new TestBmsPlaylist(databaseScope, songPath, null, null, null,
            () => new PlaylistUrlCompletionOptionsSnapshot(), () => new BeatorajaBmtOptionsSnapshot(),
            () => new CustomFolderOutputSettingsSnapshot(),
            CreateDeterministicLr2PlaylistFolderSynchronizationPort(databaseScope, songPath, CustomFolderOutputPhysicalSurface.Empty),
            settings: settings, uiScheduler: new TestUiScheduler(() => null));
    }

    /// <summary>登録（複数表の取得）の対象を作ります。再読込みでは、保留なしで先に登録した表を対象にします。</summary>
    private static async Task<BMSTable[]> PrepareConcurrencyTargetsAsync(string operation, ConcurrentTableHttpServer server, TestBmsPlaylist playlist, int tableCount)
    {
        BMSTable[] sources = [.. Enumerable.Range(0, tableCount).Select(index => new BMSTable { Page_url = server.TableUri(index) })];
        if (operation == "register") { return sources; }
        List<PlaylistExternalSyncOwner.PlaylistExternalTableLoadResult> loaded = await playlist.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(sources, false);
        await playlist.ExternalSyncOwner.RegistrateExternalTablesAsync(loaded.Select(item => item.ExternalTable), false, "external-concurrency");
        Assert.AreEqual(tableCount, playlist.BMSTables.Count);
        return [.. playlist.BMSTables];
    }

    private static Task StartConcurrencyOperation(string operation, TestBmsPlaylist playlist, BMSTable[] targets)
        => operation == "register"
            ? (Task)playlist.ExternalSyncOwner.LoadExternalTableSnapshotsAsync(targets, false)
            : playlist.ExternalSyncOwner.ReloadPlaylistTargetsAsync(targets);

    /// <summary>保留中の要求を1件ずつ解放し、各時点で取得中のはずの数（設定値と未完了の表の少ない方）に達してから次へ進みます。</summary>
    /// <remarks>超過がないことは固定時間の経過から推測せず、サーバーが記録した最大の取得中の数で判定します。</remarks>
    private static async Task DriveConcurrencyOperationAsync(ConcurrentTableHttpServer server, Task operation, int concurrency, int tableCount, CancellationToken token)
    {
        for (int released = 0; released < tableCount * 2; released++)
        {
            int target = Math.Min(concurrency, tableCount - server.FinishedTables);
            Task reached = server.WaitForInFlightAsync(target, token);
            await Task.WhenAny(reached, operation).WaitAsync(token);
            if (operation.IsCompleted && !reached.IsCompleted) { await operation; Assert.Fail("操作が全要求の解放前に終了しました。"); }
            await reached;
            server.ReleaseOldest();
        }
        await operation.WaitAsync(token);
    }

    /// <summary>登録と再読込みの両方で、同時に取得する表の数は設定値を超えず、対象が十分あれば設定値（1と3）まで同時に取得します。</summary>
    /// <remarks>
    /// 対象は設定値+2件で、応答ヘッダーを送って本文を保留している要求の数が各時点で期待値に達するのを待ち、全体での最大値が設定値と一致することを確認します。
    /// 固定の32や、設定値を超える、または下回る並列度を検出します。
    /// </remarks>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow("register", 1)]
    [DataRow("register", 3)]
    [DataRow("reload", 1)]
    [DataRow("reload", 3)]
    public async Task ExternalTableFetch_ConcurrencyFollowsSetting(string operation, int concurrency)
    {
        using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operationToken.CancelAfter(TimeSpan.FromSeconds(30));
        ConcurrentTableHttpServer? server = null;
        Task? running = null;
        try
        {
            server = new ConcurrentTableHttpServer(operationToken.Token);
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.ExternalTableSyncMaxConcurrency = concurrency);
            TestBmsPlaylist playlist = CreateConcurrencyPlaylist(settings);
            int tableCount = concurrency + 2;
            BMSTable[] targets = await PrepareConcurrencyTargetsAsync(operation, server, playlist, tableCount);
            server.Hold = true;
            server.ResetRecord();

            running = StartConcurrencyOperation(operation, playlist, targets);
            await DriveConcurrencyOperationAsync(server, running, concurrency, tableCount, operationToken.Token);

            Assert.AreEqual(concurrency, server.MaxInFlight);
        }
        finally { await CleanupConcurrencyAsync(server, running, operationToken); }
    }

    /// <summary>進行中の操作は開始時の同時取得数（1）を使い続け、途中で確定した設定値（3）は次の操作から使います。</summary>
    /// <remarks>登録と再読込みの両方で、1件目の保留中に設定を変えても最大の取得中の数が1のままであり、続く操作では3に達することを確認します。</remarks>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow("register")]
    [DataRow("reload")]
    public async Task ExternalTableFetch_InProgressOperationKeepsStartConcurrencyAndNextUsesNewValue(string operation)
    {
        using var operationToken = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operationToken.CancelAfter(TimeSpan.FromSeconds(30));
        ConcurrentTableHttpServer? server = null;
        Task? running = null;
        try
        {
            server = new ConcurrentTableHttpServer(operationToken.Token);
            Settings settings = TestSettingsFactory.CreateIsolatedSettings(values => values.ExternalTableSyncMaxConcurrency = 1);
            TestBmsPlaylist playlist = CreateConcurrencyPlaylist(settings);
            const int tableCount = 5;
            BMSTable[] targets = await PrepareConcurrencyTargetsAsync(operation, server, playlist, tableCount);
            server.Hold = true;
            server.ResetRecord();

            running = StartConcurrencyOperation(operation, playlist, targets);
            await server.WaitForInFlightAsync(1, operationToken.Token);
            settings.ExternalTableSyncMaxConcurrency = 3;
            await DriveConcurrencyOperationAsync(server, running, 1, tableCount, operationToken.Token);
            Assert.AreEqual(1, server.MaxInFlight, "進行中の操作は開始時の値のままです。");

            server.ResetRecord();
            targets = operation == "register" ? (await PrepareConcurrencyTargetsAsync("register", server, playlist, tableCount)) : [.. playlist.BMSTables];
            running = StartConcurrencyOperation(operation, playlist, targets);
            await DriveConcurrencyOperationAsync(server, running, 3, tableCount, operationToken.Token);
            Assert.AreEqual(3, server.MaxInFlight, "次の操作は新しい値まで同時に取得します。");
        }
        finally { await CleanupConcurrencyAsync(server, running, operationToken); }
    }

    private static async Task CleanupConcurrencyAsync(ConcurrentTableHttpServer? server, Task? running, CancellationTokenSource operationToken)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        operationToken.Cancel();
        if (server != null) { await server.DisposeAsync(cleanup.Token); }
        if (running != null) { try { await running.WaitAsync(cleanup.Token); } catch (OperationCanceledException) when (!cleanup.IsCancellationRequested) { } }
    }
}
