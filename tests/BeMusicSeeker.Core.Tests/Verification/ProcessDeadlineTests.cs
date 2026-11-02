using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>子へ渡す単調時計期限を再予算化せず、明示入力の不正を直接実行の既定値へ変えない契約です。</summary>
[TestClass]
public sealed class ProcessDeadlineTests
{
    /// <summary>同じ有効値の再読と局所上限は、元の残時間を延長しません。</summary>
    [TestMethod]
    public void ImportedDeadlineAndLocalLimitNeverExtendOriginalCutoff()
    {
        var original = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        string exported = original.Export();
        TimeSpan before = original.Remaining;
        var first = ProcessDeadline.FromSuiteValue(exported, TimeSpan.FromMinutes(3));
        Assert.IsTrue(first.Remaining > TimeSpan.Zero && first.Remaining <= before);
        before = first.Remaining;
        var second = ProcessDeadline.FromSuiteValue(exported, TimeSpan.FromMinutes(3));
        Assert.IsTrue(second.Remaining > TimeSpan.Zero && second.Remaining <= before);
        var limited = ProcessDeadline.FromSuiteValue(exported, TimeSpan.FromSeconds(1));
        Assert.IsTrue(limited.Remaining > TimeSpan.Zero && limited.Remaining <= TimeSpan.FromSeconds(1));
    }

    /// <summary>未指定だけが直接MTP用の有限既定を使い、明示空・不正・期限切れは拒否されます。</summary>
    [TestMethod]
    public void OnlyMissingValueUsesFiniteLocalDefault()
    {
        var direct = ProcessDeadline.FromSuiteValue(null, TimeSpan.FromSeconds(30));
        Assert.IsTrue(direct.Remaining > TimeSpan.Zero && direct.Remaining <= TimeSpan.FromSeconds(30));
        foreach (string value in new[] { "", " ", "invalid", "9223372036854775808" })
        { Assert.ThrowsExactly<FormatException>(() => ProcessDeadline.FromSuiteValue(value, TimeSpan.FromSeconds(30))); }
        string expired = ProcessDeadline.FromTimeout(TimeSpan.Zero).Export();
        Assert.ThrowsExactly<TimeoutException>(() => ProcessDeadline.FromSuiteValue(expired, TimeSpan.FromSeconds(30)));
    }

    /// <summary>有効中にexportした値でも、親gateを期限経過後に開けると実子のimportが失敗し、起動時間を新予算へ戻しません。</summary>
    [TestMethod]
    public async Task ChildImportAfterParentGateRejectsElapsedOriginalDeadline()
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var shared = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(2));
        string value = shared.Export();
        Assert.IsTrue(shared.Remaining > TimeSpan.Zero);
        var cleanup = new Lazy<ProcessDeadline>(() => ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(10)));
        OwnedProcess? process = null;
        Exception? primary = null;
        try
        {
            process = OwnedProcess.Start(new ProcessStartRequest(
                Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe"),
                ["suite-deadline-after-gate", workspace.GetPath("ready"), workspace.GetPath("release")], workspace.RootDirectory,
                new Dictionary<string, string?> { [ProcessDeadline.SuiteDeadlineEnvironmentVariable] = value }), () => cleanup.Value);
            var observation = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(15));
            while (!File.Exists(workspace.GetPath("ready")))
            {
                if (observation.Remaining == TimeSpan.Zero) { throw new TimeoutException("子がgate待機を通知しません。"); }
                await Task.Delay(10);
            }
            while (shared.Remaining > TimeSpan.Zero) { await Task.Delay(shared.Remaining); }
            File.WriteAllText(workspace.GetPath("release"), "release");
            ProcessCompletion completion = await process.WaitForExitAsync(observation);
            Assert.AreEqual(3, completion.ExitCode);
            Assert.AreEqual("expired", completion.StandardOutput);
            Assert.AreEqual(string.Empty, completion.StandardError);
        }
        catch (Exception failure)
        {
            primary = failure;
            if (failure is OwnedProcessStartException { CleanupFailure: not null }) { workspace.PreserveArtifacts(); }
            throw;
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, cleanup.Value);
                    if (stopped.CleanupFailure is not null) { throw stopped.CleanupFailure; }
                    await process.DisposeAsync();
                }
                catch (Exception failure)
                {
                    workspace.PreserveArtifacts();
                    if (primary is not null) { primary.Data["DeadlineProbeCleanupFailure"] = failure; }
                    else { throw; }
                }
                finally { workspace.Dispose(); }
            }
            else { workspace.Dispose(); }
        }
    }
}
