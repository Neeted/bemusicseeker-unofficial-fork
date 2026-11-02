using System.Runtime.ExceptionServices;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;

namespace BeMusicSeeker.Core.Tests.Verification;

/// <summary>実native故障を注入せず、同じ音声終了処理の非成功exit・診断・媒体・後続到達を実子processで検査します。</summary>
[TestClass]
public sealed class AudioEncoderTestTerminationTests
{
    /// <summary>子processの元出力を表明前に記録し、最初の失敗でも終端診断をTRXへ残します。</summary>
    public TestContext? TestContext { get; set; }

    /// <summary>実owner未解放を表す入力だけが次初期化を止め、通常失敗と終端済みhandle破棄失敗は呼出元へ戻ります。</summary>
    [TestMethod]
    [DataRow("stop-unconfirmed", true, -1)]
    [DataRow("exit-unconfirmed", true, -1)]
    [DataRow("writer-unreleased", true, -1)]
    [DataRow("diagnostic-write-failed", true, -1)]
    [DataRow("not-started", false, 0)]
    [DataRow("initialization-failed", false, 7)]
    [DataRow("assertion", false, 7)]
    [DataRow("fault", false, 7)]
    [DataRow("cancel", false, 7)]
    [DataRow("released", false, 0)]
    [DataRow("handle-dispose-failed", false, 7)]
    public async Task UnconfirmedOwnerStopsNextInitializationAndPreservesDiagnostics(string mode, bool terminated, int expectedExit)
    {
        var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        var deadline = ProcessDeadline.FromTimeout(TimeSpan.FromSeconds(30));
        var cleanup = new Lazy<ProcessDeadline>(() => deadline.AddGrace(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(10)));
        OwnedProcess? process = null;
        ExceptionDispatchInfo? primary = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            process = OwnedProcess.Start(new ProcessStartRequest(
                Path.Combine(AppContext.BaseDirectory, "ProcessFixture", "ProcessFixture.exe"),
                ["audio-owner-cleanup", mode, workspace.RootDirectory], workspace.RootDirectory,
                new Dictionary<string, string?> { [ProcessDeadline.SuiteDeadlineEnvironmentVariable] = deadline.Export() }), () => cleanup.Value);
            ProcessCompletion result = await process.WaitForExitAsync(deadline);
            TestContext?.WriteLine("ProcessFixture stdout:" + Environment.NewLine + result.StandardOutput);
            TestContext?.WriteLine("ProcessFixture stderr:" + Environment.NewLine + result.StandardError);
            Assert.IsTrue(process.RootExit.IsCompletedSuccessfully);
            Assert.IsTrue(process.Completion.IsCompletedSuccessfully, "root・Job・両pipeを回収した結果で判定します。");
            string media = File.ReadAllText(workspace.GetPath("audio-owner-reached"));
            Assert.AreEqual(!terminated, File.Exists(workspace.GetPath("audio-next-initialization")));
            if (terminated)
            {
                Assert.AreNotEqual(0, result.ExitCode);
                StringAssert.Contains(result.StandardError, "audio-primary-" + mode);
                StringAssert.Contains(result.StandardError, "audio-secondary-first");
                StringAssert.Contains(result.StandardError, "audio-secondary-second");
                StringAssert.Contains(result.StandardError, "suite-cutoff=" + deadline.Export());
                StringAssert.Contains(result.StandardError, "workspace=" + media);
                StringAssert.Contains(result.StandardError, "managed cleanup observation: " + mode);
                Assert.AreEqual("owned input", File.ReadAllText(Path.Combine(media, "source-retained")));
                if (mode == "diagnostic-write-failed")
                {
                    StringAssert.Contains(result.StandardError, "audio-owner-unreleased.txt");
                    StringAssert.Contains(result.StandardError, nameof(UnauthorizedAccessException));
                }
                else
                {
                    string diagnostic = File.ReadAllText(Path.Combine(media, "audio-owner-unreleased.txt"));
                    StringAssert.Contains(diagnostic, "audio-primary-" + mode);
                    StringAssert.Contains(diagnostic, "audio-secondary-first");
                    StringAssert.Contains(diagnostic, "audio-secondary-second");
                }
            }
            else
            {
                Assert.AreEqual(expectedExit, result.ExitCode);
                Assert.AreEqual(string.Empty, result.StandardOutput);
                Assert.AreEqual(string.Empty, result.StandardError);
                Assert.IsFalse(Directory.Exists(media), "通常終端の媒体はfixtureのusingで回収します。");
            }
        }
        catch (Exception failure)
        {
            primary = ExceptionDispatchInfo.Capture(failure);
            if (failure is OwnedProcessStartException { CleanupFailure: not null }) { workspace.PreserveArtifacts(); }
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    ProcessStopResult stopped = await process.StopAsync(null, cleanup.Value);
                    if (stopped.CleanupFailure is not null) { throw stopped.CleanupFailure; }
                }
                catch (Exception failure) { workspace.PreserveArtifacts(); cleanupFailures.Add(failure); }
                try { await process.DisposeAsync(); }
                catch (Exception failure) { workspace.PreserveArtifacts(); cleanupFailures.Add(failure); }
            }
            try { workspace.Dispose(); }
            catch (Exception failure) { cleanupFailures.Add(failure); }
        }
        if (primary is not null)
        {
            if (cleanupFailures.Count > 0) { primary.SourceException.Data["AudioTerminationProbeCleanup"] = new AggregateException(cleanupFailures); }
            primary.Throw();
        }
        if (cleanupFailures.Count > 0) { throw new AggregateException(cleanupFailures); }
    }
}
