using System.Collections.Concurrent;
using System.Reflection;
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

    private static Task<ApplicationScenarioDriver> StartAsync(TestWorkspace workspace, string mode, ProcessDeadline deadline)
        => ApplicationScenarioDriver.StartAsync(Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe"),
            workspace.RootDirectory, workspace.GetPath("session.json"), mode, "http://127.0.0.1/unused", deadline);
}
