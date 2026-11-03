using System.IO;
using BeMusicSeeker.Models;
using BeMusicSeeker.Runtime;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.TestSupport.Processes;
using BeMusicSeeker.TestSupport.Scenarios;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Scenarios.Tests.Playback;

/// <summary>実Appの通常選曲・制御・一時試聴を、実Readyと受動PCM、実停止解放まで接続します。</summary>
[TestClass]
public sealed class PlaybackScenarioTests
{
    /// <summary>保証: 実AppのBMS activationから通常nextで別曲の実PCMへ進み、そのCompletionと明示Stop解放、再開始を独立に確認します。</summary>
    [TestMethod(DisplayName = "[SC-12] 実BMSの通常選曲・自然完了・明示停止と再開始")]
    [TestCategory("Full")]
    public async Task SC12_NativePlayback_NextCompletionStopAndStart()
    {
        await RunAsync(async (app, fixture, deadline) =>
        {
            ScenarioLibrarySnapshot library = (await app.SnapshotAsync(deadline)).Snapshot;
            Assert.IsTrue(library.Visible.Any(chart => chart.Path == fixture.Bms));
            Assert.IsTrue(library.Visible.Any(chart => chart.Path == fixture.NextBms));
            ScenarioPlaybackReceipt started = await app.PlaybackAsync(ScenarioPlaybackAction.Activate, fixture.Bms, deadline);
            AssertStarted(started, fixture.Bms, temporary: false);
            AssertPcm(started, second: false);
            ScenarioPlaybackReceipt next = await app.PlaybackAsync(ScenarioPlaybackAction.Next, fixture.NextBms, deadline, waitForCompletion: true);
            Assert.AreEqual(fixture.NextBms, next.ChartPath);
            Assert.AreEqual(fixture.NextBms, next.PlaybackPath);
            Assert.IsTrue(next.ReadySucceeded);
            Assert.AreNotEqual(started.Generation, next.Generation);
            Assert.AreEqual("RanToCompletion", next.CompletionStatus);
            AssertPcm(next, second: true);
            AssertStopped(await app.PlaybackAsync(ScenarioPlaybackAction.Stop, fixture.NextBms, deadline), next);
            ScenarioPlaybackReceipt again = await app.PlaybackAsync(ScenarioPlaybackAction.Start, fixture.NextBms, deadline);
            AssertStarted(again, fixture.NextBms, temporary: false);
            Assert.AreNotEqual(next.Generation, again.Generation);
            AssertPcm(again, second: true);
            AssertStopped(await app.PlaybackAsync(ScenarioPlaybackAction.Stop, fixture.NextBms, deadline), again);
        });
    }

    /// <summary>保証: 実Drop・保留・宛先指定から入れ子試聴へ到達し、元名とbytes・非所有物を保全して停止後に再試聴できます。</summary>
    [TestMethod(DisplayName = "[SC-12] 入れ子bmsonの一時試聴・停止解放と再開始")]
    [TestCategory("Full")]
    public async Task SC12_TemporaryBmson_PreservesNestedInputAndReleasesOwnedCopies()
    {
        await RunAsync(async (app, fixture, deadline) =>
        {
            Assert.IsTrue((await app.SnapshotAsync(deadline)).Snapshot.Catalog
                .Any(chart => Path.GetDirectoryName(chart.Path) == fixture.Destination), "宛先は起動時に読み込んだ所持譜面のdirectoryです。");
            ScenarioDropCompleted drop = await app.DropAsync([fixture.Package], deadline);
            Assert.IsNull(drop.Failure, drop.Failure?.StackTrace);
            Assert.IsNotNull(drop.Result);
            Assert.IsFalse(drop.Result.HasRequiredFailure);
            ScenarioPackageViewCompleted pending = await app.NavigatePackagesAsync(true, deadline);
            Assert.IsTrue(pending.Rows.Any(row => row.Chart.Path == fixture.Preview));
            ScenarioPendingCompleted selected = await app.ApplyPendingAsync(ScenarioPendingAction.Set, [fixture.Preview], deadline, fixture.Destination);
            Assert.AreEqual(fixture.Destination, selected.View.Rows.Single(row => row.Chart.Path == fixture.Preview).Destination);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                ScenarioPlaybackReceipt started = await app.PlaybackAsync(attempt == 0 ? ScenarioPlaybackAction.Activate : ScenarioPlaybackAction.Start, fixture.Preview, deadline);
                AssertStarted(started, fixture.Preview, temporary: true);
                Assert.AreEqual(Path.Combine(fixture.Destination, "nested"), Path.GetDirectoryName(started.PlaybackPath));
                AssertPcm(started, second: false);
                Assert.AreNotEqual("RanToCompletion", started.CompletionStatus, "演奏中の明示停止を確認します。");
                // Ready後の回収は許容し、Completionまで一時copyが存在し続けるとは要求しません。
                AssertStopped(await app.PlaybackAsync(ScenarioPlaybackAction.Stop, fixture.Preview, deadline), started);
                AssertFiles(fixture.Package, fixture.SourceBytes);
                AssertFiles(fixture.Destination, fixture.DestinationBytes);
            }
        }, temporaryPlayback: true);
    }

    private static async Task RunAsync(Func<ApplicationScenarioDriver, PlaybackScenarioFixture, ProcessDeadline, Task> body, bool temporaryPlayback = false)
    {
        using var workspace = TestWorkspace.Create(TestRunDirectory.RootDirectory);
        try
        {
            await using var fixture = new PlaybackScenarioFixture(workspace, temporaryPlayback);
            var deadline = ProcessDeadline.FromSuiteEnvironment(TimeSpan.FromMinutes(3));
            string executable = ScenarioApplicationPaths.ExecutablePath;
            ApplicationScenarioDriver app = await ApplicationScenarioDriver.StartAsync(executable, fixture.Profile,
                workspace.GetPath("session.json"), workspace.CaseId, fixture.UpdateUrl, deadline, nullOutputPlayback: true);
            Exception? primary = null;
            ProcessDeadline? failureCleanup = null;
            try
            {
                ScenarioInitializationResult startup = await app.WaitForStartupAsync(deadline);
                Assert.AreEqual(StartupInitializationOutcome.Succeeded, startup.Outcome);
                Assert.IsTrue(startup.IsInitializationCompleted && startup.CompletionPublished);
                Assert.IsNull(startup.Failure);
                await body(app, fixture, deadline);
                await app.CloseAsync(deadline);
                AssertFiles(fixture.Root, fixture.RootBytes);
            }
            catch (Exception failure)
            {
                primary = failure;
                failureCleanup = deadline.AddGrace(TimeSpan.FromSeconds(10)).LimitRemaining(TimeSpan.FromSeconds(10));
                throw;
            }
            finally
            {
                try { await app.DisposeAsync(failureCleanup ?? deadline); }
                catch (Exception cleanup) when (primary is not null) { primary.Data["ScenarioCleanupFailure"] = cleanup; }
            }
        }
        catch { workspace.PreserveArtifacts(); throw; }
    }

    private static void AssertStarted(ScenarioPlaybackReceipt value, string path, bool temporary)
    {
        Assert.IsTrue(value.Generation > 0 && value.ReadySucceeded);
        Assert.AreEqual(path, value.ChartPath);
        Assert.AreEqual(temporary, !string.Equals(path, value.PlaybackPath, StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(value.NativeReleased || value.StopCompleted);
        Assert.AreNotEqual("Faulted", value.CompletionStatus);
        Assert.AreNotEqual("Canceled", value.CompletionStatus);
    }

    private static void AssertStopped(ScenarioPlaybackReceipt stopped, ScenarioPlaybackReceipt started)
    {
        Assert.AreEqual(started.Generation, stopped.Generation);
        Assert.AreEqual(started.ChartPath, stopped.ChartPath);
        Assert.AreEqual(started.PlaybackPath, stopped.PlaybackPath);
        Assert.IsTrue(stopped.StopCompleted && stopped.NativeReleased && stopped.ReadySucceeded);
        Assert.IsTrue(stopped.CompletionStatus is "RanToCompletion" or "Canceled", stopped.CompletionStatus);
    }

    private static void AssertPcm(ScenarioPlaybackReceipt receipt, bool second)
    {
        PlaybackPcmSnapshot pcm = receipt.Pcm ?? throw new AssertFailedException("開始後の実PCMがありません。");
        Assert.AreEqual(48000, pcm.SampleRate);
        Assert.AreEqual(2, pcm.Channels);
        Assert.IsTrue(pcm.Samples.Length is > 0 and <= 128);
        Assert.IsTrue(pcm.Samples.All(float.IsFinite));
        // WAVの整数振幅、製品の譜面gain 0.4、明示した出力gain 1から独立に算出します。
        float expectedLeft = (second ? 4096f : 8192f) / 32768f * 0.4f;
        float expectedRight = (second ? 12288f : -4096f) / 32768f * 0.4f;
        Assert.IsTrue(pcm.Samples.Where((_, index) => index % 2 == 0).Any(value => Math.Abs(value - expectedLeft) < 0.0001f));
        Assert.IsTrue(pcm.Samples.Where((_, index) => index % 2 == 1).Any(value => Math.Abs(value - expectedRight) < 0.0001f));
    }

    private static void AssertFiles(string root, IReadOnlyDictionary<string, byte[]> expected)
    {
        IReadOnlyDictionary<string, byte[]> actual = PlaybackScenarioFixture.CaptureFiles(root);
        CollectionAssert.AreEquivalent(expected.Keys.ToArray(), actual.Keys.ToArray());
        foreach (KeyValuePair<string, byte[]> entry in expected) { CollectionAssert.AreEqual(entry.Value, actual[entry.Key], entry.Key); }
    }
}
