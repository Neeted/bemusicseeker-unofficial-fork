using System.Text;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.BmsLibraryInternal;
using BeMusicSeeker.TestSupport.Components;
using Ribbit.Net;

namespace BeMusicSeeker.Tests;

public sealed partial class BmsPlaylistExternalLoadTests
{
    /// <summary>
    /// 外部表のHTML・header・dataの各HTTP要求が、送信開始から本文完了までの総期限として設定秒数（10秒・3600秒）を使い、
    /// ヘッダー受信で期限を更新せず、時間切れでは本文の実読取りを取り消して応答を解放します。
    /// </summary>
    /// <remarks>
    /// 既定300秒で構成した手動時計の <see cref="AppHttpClient"/> と、URIごとに段階を制御するhandlerを渡します。停止させる文書の前の文書は、
    /// それぞれ設定秒数の1秒前までヘッダーを待っても成功し、停止させる文書はヘッダーを設定秒数の1秒前まで保留した後の本文待ちが、さらに1秒で取り消されます。
    /// ヘッダー受信で期限を延ばす実装、文書をまたいで期限を共有する実装、既定の300秒へ固定する実装を検出します。
    /// </remarks>
    [TestMethod]
    [TestCategory("Playlist")]
    [DataRow(10, 0)]
    [DataRow(10, 1)]
    [DataRow(10, 2)]
    [DataRow(3600, 0)]
    [DataRow(3600, 1)]
    [DataRow(3600, 2)]
    public async Task ExternalRequest_TotalDeadlineUsesSettingFromSendToBodyCompletion(int seconds, int stalledIndex)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        operation.CancelAfter(TimeSpan.FromSeconds(30));
        var clock = new ManualRequestTimeProvider();
        var handler = new StagedResponseHandler();
        Task<BMSTable>? load = null;
        Exception? primaryFailure = null;
        var nearlyDue = TimeSpan.FromSeconds(seconds - 1);
        try
        {
            Uri pageUri = new("https://example.invalid/table/index.html");
            Uri headerUri = new("https://example.invalid/table/header.json");
            Uri dataUri = new("https://example.invalid/table/data.json");
            string[] completeBodies =
            [
                "<html><head><meta name=\"bmstable\" content=\"header.json\"></head></html>",
                "{\"name\":\"Deadline\",\"symbol\":\"D\",\"data_url\":\"data.json\"}",
                "[{\"md5\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Song\",\"artist\":\"Artist\",\"level\":\"1\"}]"
            ];
            string[] stalledPrefixes = ["<html>", "{\"name\":\"Deadline\",", "["];
            Uri[] uris = [pageUri, headerUri, dataUri];
            StagedResponse[] stages =
            [
                .. uris.Select((uri, index) => handler.Stage(
                    uri, Encoding.UTF8.GetBytes(index == stalledIndex ? stalledPrefixes[index] : completeBodies[index]), holdBody: index == stalledIndex))
            ];
            var recommended = new PlaylistRecommendedTableOwner(
                static _ => Task.FromException<WalkureScoreInput>(new InvalidOperationException("内蔵推薦はこのケースの対象外です。")),
                new PlaylistOperationNotificationOwner(), static () => new CustomFolderOutputSettingsSnapshot());
            var options = new PlaylistExternalNetworkOptionsSnapshot { RequestTimeout = TimeSpan.FromSeconds(seconds) };
            var owner = new PlaylistExternalSyncOwner(new AppHttpClient(handler, 300000, clock), recommended,
                (_, _) => { }, static () => false, static _ => { },
                customFolderOutputSettingsProvider: static () => new CustomFolderOutputSettingsSnapshot(),
                networkOptionsProvider: () => options);

            load = owner.LoadExternalTableAsync(pageUri, cancellationToken: operation.Token);
            for (int index = 0; index < stalledIndex; index++)
            {
                await AwaitExternalPhaseAsync(stages[index].RequestStarted.Task, load, operation.Token);
                clock.Advance(nearlyDue);
                Assert.IsFalse(stages[index].RequestCancellation.IsCancellationRequested, "各文書の期限は、前の文書の経過時間を引き継ぎません。");
                stages[index].ReleaseHeaders.TrySetResult();
            }

            StagedResponse stalled = stages[stalledIndex];
            await AwaitExternalPhaseAsync(stalled.RequestStarted.Task, load, operation.Token);
            clock.Advance(nearlyDue);
            Assert.IsFalse(stalled.RequestCancellation.IsCancellationRequested);
            stalled.ReleaseHeaders.TrySetResult();
            await AwaitExternalPhaseAsync(stalled.Body.ReadStarted.Task, load, operation.Token);
            Assert.IsFalse(stalled.Body.ReadCancellation.IsCancellationRequested, "ヘッダー受信では期限を更新せず、本文待ちの時点では期限の1秒前です。");

            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.IsTrue(stalled.Body.ReadCancellation.IsCancellationRequested, "総期限で本文の実読取りを取り消します。");
            await Assert.ThrowsAsync<OperationCanceledException>(() => load.WaitAsync(operation.Token));
            Assert.IsTrue(stalled.Body.IsDisposed, "時間切れで応答を解放します。");
            Assert.IsFalse(operation.IsCancellationRequested);
            for (int index = stalledIndex + 1; index < stages.Length; index++)
            {
                Assert.IsFalse(stages[index].RequestStarted.Task.IsCompleted, "時間切れ後の文書は要求しません。");
            }
        }
        catch (Exception failure) { primaryFailure = failure; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            operation.Cancel();
            handler.ReleaseAll();
            Task[] owned = load == null ? [] : [load];
            try { await SingleRequestHttpServer.CompleteCleanupAsync(cleanup.Token, primaryFailure, owned); }
            finally { if (owned.All(task => task.IsCompleted)) { handler.Dispose(); } }
        }
    }

    // 段階の到達は合図で待ち、取得側の失敗が先に起きた場合はその場で検出します。
    private static async Task AwaitExternalPhaseAsync(Task phase, Task load, CancellationToken operationToken)
    {
        await Task.WhenAny(phase, load).WaitAsync(operationToken);
        if (load.IsCompleted) { await load; }
        Assert.IsTrue(phase.IsCompleted, "取得が目的の段階より先に終了しました。");
    }
}
