using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;

namespace BeMusicSeeker.Core.Tests.Runtime;

/// <summary>実driverと既存process fixtureで、期待終了と通信故障の境界を検査します。</summary>
[TestClass]
public sealed class ApplicationScenarioDriverTests
{
    /// <summary>期待登録後の実退出だけを許容し、JSON故障・非0退出を成功へ読み替えません。製品のschema判断はDesktop側で検査します。</summary>
    [TestMethod]
    [DataRow("natural")]
    [DataRow("malformed")]
    [DataRow("nonzero")]
    public async Task ExpectedExitPreservesReaderAndExitFailures(string mode)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        ApplicationScenarioDriver driver = await StartAsync(workspace, mode, deadline);
        try
        {
            driver.ExpectNaturalExit();
            File.WriteAllText(workspace.GetPath("release"), "release");
            if (mode == "natural") { await driver.WaitForExpectedExitAsync(deadline); }
            else if (mode == "malformed") { await Assert.ThrowsExactlyAsync<IOException>(() => driver.WaitForExpectedExitAsync(deadline)); }
            else { await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => driver.WaitForExpectedExitAsync(deadline)); }
        }
        finally
        {
            File.WriteAllText(workspace.GetPath("release"), "release");
            if (mode == "malformed") { await Assert.ThrowsExactlyAsync<AggregateException>(() => driver.DisposeAsync().AsTask()); }
            else { await driver.DisposeAsync(); }
        }
    }

    /// <summary>期待期間でも未応答commandを成功補完せず、後から終了期待を登録して故障を消せないことを検査します。</summary>
    [TestMethod]
    public async Task ExpectedExitDoesNotCompleteUnansweredCommand()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        ApplicationScenarioDriver driver = await StartAsync(workspace, "unanswered", deadline);
        Task? pending = null;
        try
        {
            driver.ExpectNaturalExit();
            pending = driver.TableStateAsync(deadline);
            while (!File.Exists(workspace.GetPath("request-received"))) { await Task.Delay(10).WaitAsync(deadline.Remaining); }
            File.WriteAllText(workspace.GetPath("release"), "release");
            await Assert.ThrowsExactlyAsync<IOException>(() => driver.WaitForExpectedExitAsync(deadline));
            Assert.ThrowsExactly<InvalidOperationException>(driver.ExpectNaturalExit);
            await Assert.ThrowsAsync<Exception>(async () => await pending);
        }
        finally
        {
            File.WriteAllText(workspace.GetPath("release"), "release");
            if (pending is not null) { try { await pending; } catch (Exception) { } }
            await Assert.ThrowsExactlyAsync<AggregateException>(() => driver.DisposeAsync().AsTask());
        }
    }

    /// <summary>実応答済みの相関項目が呼出元の回収前でも、期待したEOFを未応答失敗にしないことを確認します。</summary>
    [TestMethod]
    public async Task ExpectedExitAcceptsAnsweredRequestBeforeCallerCleanup()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        ApplicationScenarioDriver driver = await StartAsync(workspace, "answered", deadline);
        ConcurrentDictionary<long, TaskCompletionSource<ScenarioMessage>>? requests = null;
        Exception? primary = null;
        const long requestId = 1;
        try
        {
            // このcaseが通信相関の組立てと回収を所有し、通常到達する応答済み/呼出元回収前を固定します。
            // 業務状態の検査には使いません。通常の安定した境界が整えば、この局所反射を置換します。
            requests = typeof(ApplicationScenarioDriver).GetField("requests", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(driver) as ConcurrentDictionary<long, TaskCompletionSource<ScenarioMessage>>
                ?? throw new InvalidOperationException("The driver request correlation is unavailable.");
            var response = new TaskCompletionSource<ScenarioMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(requests.TryAdd(requestId, response));
            driver.ExpectNaturalExit();
            MethodInfo write = typeof(ApplicationScenarioDriver).GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The driver command writer is unavailable.");
            await (write.Invoke(driver, [new ScenarioTableStateCommand(requestId), deadline]) as Task
                ?? throw new InvalidOperationException("The driver command task is unavailable."));
            ScenarioMessage actual = await response.Task.WaitAsync(deadline.Remaining);
            Assert.IsInstanceOfType<ScenarioTableStateCompleted>(actual);
            Assert.AreEqual(requestId, actual.RequestId);
            File.WriteAllText(workspace.GetPath("release"), "release");
            await driver.WaitForExpectedExitAsync(deadline);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            requests?.TryRemove(requestId, out _);
            File.WriteAllText(workspace.GetPath("release"), "release");
            try { await driver.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
        }
    }

    /// <summary>正常形式の後継helloでも別Jobの生存PIDを拒否し、元owner回収で無関係processを終了させないことを確認します。</summary>
    /// <remarks>正の後継readyは実Appの設定再起動が分担します。fixtureでready成功を生成しません。</remarks>
    [TestMethod]
    public async Task RestartHelloRejectsLiveProcessOutsideOwnedJob()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        string executable = Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe");
        await using var unrelated = OwnedProcess.Start(new ProcessStartRequest(executable,
            ["hang", workspace.GetPath("unrelated-ready"), workspace.GetPath("unrelated-release")], workspace.RootDirectory));
        ApplicationScenarioDriver driver = await StartAsync(workspace, "natural", deadline);
        Exception? primary = null;
        using var manifest = JsonDocument.Parse(File.ReadAllText(workspace.GetPath("session.json")));
        string pipeName = manifest.RootElement.GetProperty("controlPipeName").GetString() ?? throw new InvalidDataException();
        using var peer = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            driver.ExpectNaturalExit();
            File.WriteAllText(workspace.GetPath("release"), "release");
            await driver.WaitForExpectedExitAsync(deadline);
            MethodInfo rebind = typeof(ApplicationScenarioDriver).GetMethod("RebindAfterRestartAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The restart boundary is unavailable.");
            Task adoption = rebind.Invoke(driver, [deadline]) as Task ?? throw new InvalidOperationException("The restart Task is unavailable.");
            using var cancellation = new CancellationTokenSource(deadline.Remaining);
            await peer.ConnectAsync(cancellation.Token);
            using var writer = new StreamWriter(peer, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(JsonSerializer.Serialize<ScenarioMessage>(new ScenarioConnected(unrelated.ProcessId)));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => adoption);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            File.WriteAllText(workspace.GetPath("release"), "release");
            try { await driver.DisposeAsync(); }
            catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
            finally
            {
                Assert.IsFalse(unrelated.RootExit.IsCompleted, "拒否した別Job processを元ownerが回収しています。");
                File.WriteAllText(workspace.GetPath("unrelated-release"), "release");
                Assert.AreEqual(0, (await unrelated.WaitForExitAsync(deadline)).ExitCode);
            }
        }
    }

    /// <summary>再起動元の実退出が応答公開より先でも、旧reader終端後に同じ実応答を返します。</summary>
    /// <remarks>実fixtureの応答とreader終端の公開だけを保留し、awaiterの選択順を固定します。JSON・相関・未応答・非0の境界は既存caseが分担します。</remarks>
    [TestMethod]
    public async Task RestartExitBeforeResponsePublicationDrainsOriginalReader()
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        ApplicationScenarioDriver driver = await StartAsync(workspace, "answered", deadline);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FieldInfo readingField = typeof(ApplicationScenarioDriver).GetField("reading", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The driver reader is unavailable.");
        Task originalReading = readingField.GetValue(driver) as Task ?? throw new InvalidOperationException("The reader Task is unavailable.");
        ConcurrentDictionary<long, TaskCompletionSource<ScenarioMessage>>? requests = null;
        Task<ScenarioMessage>? waiting = null;
        Task<ScenarioMessage>? publishedResponse = null;
        Task? publishedReading = null;
        Exception? primary = null;
        const long requestId = 1;
        try
        {
            requests = typeof(ApplicationScenarioDriver).GetField("requests", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(driver) as ConcurrentDictionary<long, TaskCompletionSource<ScenarioMessage>>
                ?? throw new InvalidOperationException("The driver request correlation is unavailable.");
            var response = new TaskCompletionSource<ScenarioMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.IsTrue(requests.TryAdd(requestId, response));
            FieldInfo restart = typeof(ApplicationScenarioDriver).GetField("restartExpected", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The restart expectation is unavailable.");
            restart.SetValue(driver, true);
            MethodInfo write = typeof(ApplicationScenarioDriver).GetMethod("WriteAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The driver command writer is unavailable.");
            await (write.Invoke(driver, [new ScenarioTableStateCommand(requestId), deadline]) as Task
                ?? throw new InvalidOperationException("The command Task is unavailable."));
            ScenarioMessage expectedResponse = await response.Task.WaitAsync(deadline.Remaining);
            Assert.IsInstanceOfType<ScenarioTableStateCompleted>(expectedResponse);
            File.WriteAllText(workspace.GetPath("release"), "release");
            Assert.AreEqual(0, await driver.RootExit.WaitAsync(deadline.Remaining));
            await originalReading.WaitAsync(deadline.Remaining);

            // 実受信値・実readerの結果をそのまま転送し、公開順だけを遅らせます。
            publishedResponse = PublishResponseAsync(response.Task);
            publishedReading = PublishReadingAsync(originalReading);
            readingField.SetValue(driver, publishedReading);
            MethodInfo awaiter = typeof(ApplicationScenarioDriver).GetMethod("AwaitOrExitAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.MakeGenericMethod(typeof(ScenarioMessage)) ?? throw new InvalidOperationException("The response awaiter is unavailable.");
            waiting = awaiter.Invoke(driver, [publishedResponse, deadline]) as Task<ScenarioMessage>
                ?? throw new InvalidOperationException("The response awaiter Task is unavailable.");
            Assert.IsFalse(waiting.IsCompleted, "旧peer退出だけで応答を成功補完したり早期失敗したりしてはいけません。");
            release.SetResult();
            Assert.AreSame(expectedResponse, await waiting.WaitAsync(deadline.Remaining));
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            release.TrySetResult();
            readingField.SetValue(driver, originalReading);
            requests?.TryRemove(requestId, out _);
            File.WriteAllText(workspace.GetPath("release"), "release");
            try
            {
                foreach (Task? task in new Task?[] { waiting, publishedResponse, publishedReading, originalReading })
                {
                    if (task is null) { continue; }
                    try { await task.WaitAsync(deadline.Remaining); }
                    catch (Exception cleanup) when (primary is not null) { primary.Data["RestartDrainTaskFailure"] = cleanup; }
                }
            }
            finally
            {
                try { await driver.DisposeAsync(); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
            }
        }

        async Task<ScenarioMessage> PublishResponseAsync(Task<ScenarioMessage> source)
        {
            await release.Task;
            return await source;
        }

        async Task PublishReadingAsync(Task source)
        {
            await release.Task;
            await source;
        }
    }

    private static Task<ApplicationScenarioDriver> StartAsync(TestWorkspace workspace, string mode, ProcessDeadline deadline)
        => ApplicationScenarioDriver.StartAsync(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe"),
            workspace.RootDirectory, workspace.GetPath("session.json"), mode, "http://127.0.0.1/unused", deadline);
}
