using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Tests;

[TestClass]
public sealed class NextSongPreloadOwnerTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task BmsonPreloadDispatchesModernOrLegacyAndKeepsInputFailureUntilAdoption(bool legacy)
    {
        using var fixture = new ChartFixture();
        string path = fixture.Input("next.bmson").Path;
        File.WriteAllText(path, legacy ? "{\"version\":\"0.21\",\"info\":{\"initBPM\":120},\"soundChannel\":[]}"
            : "{\"info\":{\"init_bpm\":120},\"sound_channels\":[]}");
        var owner = new NextSongPreloadOwner((input, token) => PreparedBmsSong.Prepare(PlaybackChart.Load(input.Path), 0.4f, token));
        var input = NextSongPreloadInput.Capture(path);
        PreparedBmsSong prepared = await owner.Request(input);
        Assert.AreSame(prepared, await owner.TakeAsync(input));
        Assert.IsNull(prepared.Chart.Bms);
        File.WriteAllText(path, "{\"info\":{\"init_bpm\":0}}");
        input = NextSongPreloadInput.Capture(path);
        Task<PreparedBmsSong> failing = owner.Request(input);
        InvalidBmsonFileException failure = await Assert.ThrowsExceptionAsync<InvalidBmsonFileException>(() => failing);
        Assert.IsTrue(NextSongPreloadOwner.IsInputFailure(failure));
        Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<InvalidBmsonFileException>(() => owner.TakeAsync(input)));
        Assert.IsNull(await owner.TakeAsync(input));
    }

    [TestMethod]
    public async Task MatchingPreparation_WaitsForTheSameWorkAndTransfersItOnce()
    {
        using var fixture = new ChartFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int preparations = 0;
        bool cancelled = false;
        var owner = new NextSongPreloadOwner((input, token) =>
        {
            Interlocked.Increment(ref preparations);
            using CancellationTokenRegistration registration = token.Register(() => cancelled = true);
            entered.SetResult();
            release.Wait();
            return PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token);
        });
        NextSongPreloadInput input = fixture.Input("next.bms");
        Task<PreparedBmsSong?>? adoption = null;
        Task<PreparedBmsSong> preparation = owner.Request(input);
        try
        {
            await entered.Task;
            adoption = owner.TakeAsync(input);
            Assert.IsFalse(adoption.IsCompleted);
            release.Set();
            PreparedBmsSong prepared = await adoption ?? throw new AssertFailedException("準備結果が必要です。");
            Assert.AreSame(await preparation, prepared);
            Assert.AreEqual(1, preparations, "準備途中のNextでも再デコードしません。");
            Assert.IsFalse(cancelled);
            Assert.IsNull(await owner.TakeAsync(input));
        }
        finally
        {
            release.Set();
            if (adoption != null) { await adoption; }
            await owner.InvalidateAsync();
        }
    }

    [TestMethod]
    public async Task CompletedPreparation_IsReusedWithoutAnotherLoad()
    {
        using var fixture = new ChartFixture();
        int preparations = 0;
        var owner = new NextSongPreloadOwner((input, token) =>
        {
            preparations++;
            return PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token);
        });
        NextSongPreloadInput input = fixture.Input("next.bms");
        PreparedBmsSong prepared = await owner.Request(input);
        Assert.AreSame(prepared, await owner.TakeAsync(input));
        Assert.AreEqual(1, preparations);
        Assert.IsNotNull(prepared.TakeResources());
        Assert.ThrowsException<InvalidOperationException>(() => prepared.TakeResources());
    }

    [TestMethod]
    public async Task ChangedTarget_CancelsAndJoinsBeforeDiscardingThePreparation()
    {
        using var fixture = new ChartFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new ManualResetEventSlim();
        var owner = new NextSongPreloadOwner((input, token) =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            entered.SetResult();
            cleanup.Wait();
            token.ThrowIfCancellationRequested();
            return PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token);
        });
        _ = owner.Request(fixture.Input("next.bms"));
        Task<PreparedBmsSong?>? discard = null;
        try
        {
            await entered.Task;
            discard = owner.TakeAsync(fixture.Input("different.bms"));
            await cancelled.Task;
            Assert.IsFalse(discard.IsCompleted, "取消要求だけでは後片付け完了になりません。");
            cleanup.Set();
            Assert.IsNull(await discard);
        }
        finally
        {
            cleanup.Set();
            if (discard != null) { await discard; }
            await owner.InvalidateAsync();
        }
    }

    [TestMethod]
    public async Task InputFailure_IsReturnedOnAdoptionWithoutRetry()
    {
        using var fixture = new ChartFixture();
        File.WriteAllText(fixture.Input("next.bms").Path, "#BPM 120\n#RANDOM 2147483647\n");
        int preparations = 0;
        var owner = new NextSongPreloadOwner((input, token) =>
        {
            preparations++;
            return PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token);
        });
        NextSongPreloadInput input = fixture.Input("next.bms");
        Task<PreparedBmsSong> preparation = owner.Request(input);
        Ribbit.BMS.BMSFile.InvalidBmsFileException failure = await Assert.ThrowsExceptionAsync<Ribbit.BMS.BMSFile.InvalidBmsFileException>(() => preparation);
        Assert.IsTrue(failure.IsInputFailure);
        Assert.AreSame(failure, await Assert.ThrowsExceptionAsync<Ribbit.BMS.BMSFile.InvalidBmsFileException>(() => owner.TakeAsync(input)));
        Assert.AreEqual(1, preparations);
    }

    [TestMethod]
    public async Task DiscardedFailure_SuppressesOnlyOrdinaryInputErrors()
    {
        using var fixture = new ChartFixture();
        File.WriteAllText(fixture.Input("input.bms").Path, "#BPM 120\n#RANDOM 2147483647\n");
        var inputOwner = new NextSongPreloadOwner((input, token) =>
            PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token));
        Task<PreparedBmsSong> inputTask = inputOwner.Request(fixture.Input("input.bms"));
        await Assert.ThrowsExceptionAsync<Ribbit.BMS.BMSFile.InvalidBmsFileException>(() => inputTask);
        await inputOwner.InvalidateAsync();

        var fatal = new AudioSourceFatalException("decoderの解放を確認できません。");
        var fatalOwner = new NextSongPreloadOwner((_, _) => throw fatal);
        Task<PreparedBmsSong> fatalTask = fatalOwner.Request(fixture.Input("fatal.bms"));
        await Assert.ThrowsExceptionAsync<AudioSourceFatalException>(() => fatalTask);
        Assert.AreSame(fatal, await Assert.ThrowsExceptionAsync<AudioSourceFatalException>(() => fatalOwner.InvalidateAsync()));
    }

    [TestMethod]
    public async Task ChangedFileAttributes_RejectPreparedSnapshot()
    {
        using var fixture = new ChartFixture();
        var owner = new NextSongPreloadOwner((input, token) =>
            PreparedBmsSong.Prepare(new Ribbit.BMS.BMSFile(input.Path), 0.4f, token));
        NextSongPreloadInput original = fixture.Input("next.bms");
        await owner.Request(original);
        File.AppendAllText(original.Path, "#TITLE changed\n");
        Assert.IsNull(await owner.TakeAsync(NextSongPreloadInput.Capture(original.Path)));
    }

    private sealed class ChartFixture : IDisposable
    {
        private readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bms-preload-" + Guid.NewGuid().ToString("N"));

        internal ChartFixture() => Directory.CreateDirectory(directory);

        internal NextSongPreloadInput Input(string name)
        {
            string path = System.IO.Path.Combine(directory, name);
            if (!File.Exists(path)) { File.WriteAllText(path, "#PLAYER 1\n#TITLE test\n#BPM 120\n#00111:00\n"); }
            return NextSongPreloadInput.Capture(path);
        }

        public void Dispose() => Directory.Delete(directory, recursive: true);
    }
}
