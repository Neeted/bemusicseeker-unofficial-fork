using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Ribbit.BMS;
using Ribbit.Media;
using Ribbit.Media.Audio;
using static BeMusicSeeker.TestSupport.Components.AudioContractFixture;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class AudioContractsTests
{
    private readonly BeMusicSeeker.Properties.Settings testSettings = TestSettingsFactory.CreateIsolatedSettings();

    /// <summary>手動・自然終了・準備保留の各切替を先読み有無で測定し、次曲の解析と復号を重複させず一回ずつ行う。</summary>
    /// <remarks>実NULL_DEVICEで解析・復号・切替を測定する性能区分。時間の閾値を固定せず処理重複だけを失敗とする。</remarks>
    [TestMethod]
    [TestCategory("Performance")]
    public async Task PredecodeSwitchMeasurement()
    {
        int unexpectedNotificationCount = 0;
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        string directory = Path.Combine(Path.GetTempPath(), "bms-predecode-measure-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string nextPath = WritePlaybackChart(directory, "next.bms", 400, 1);
        File.AppendAllText(nextPath, "#WAV01 audio.wav\n");
        using (var wave = new BinaryWriter(File.Create(Path.Combine(directory, "audio.wav"))))
        {
            const int frames = 441000;
            wave.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); wave.Write(36 + frames * 2);
            wave.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); wave.Write(16);
            wave.Write((short)1); wave.Write((short)1); wave.Write(44100); wave.Write(88200);
            wave.Write((short)2); wave.Write((short)16); wave.Write(System.Text.Encoding.ASCII.GetBytes("data")); wave.Write(frames * 2);
            for (int frame = 0; frame < frames; frame++) { wave.Write((short)8192); }
        }
        try
        {
            foreach (string route in new[] { "manual", "natural", "pending" })
            {
                foreach (bool usePreload in new[] { false, true })
                {
                    for (int round = 0; round < 3; round++)
                    {
                        string firstPath = WritePlaybackChart(directory, "first.bms", 400, route == "natural" ? 1 : 50);
                        var preparationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        var preparationFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        using var preparationGate = new ManualResetEventSlim(route != "pending");
                        var switchStarted = new TaskCompletionSource<PlaybackStartOperation>(TaskCreationOptions.RunContinuationsAsynchronously);
                        var clock = new System.Diagnostics.Stopwatch();
                        int parses = 0;
                        int decodes = 0;
                        double preparationMs = 0;
                        var events = new List<string>();
                        var runtime = new RecordingAudioPlaybackRuntime(events)
                        {
                            InitializeResult = CreatePlaybackInitializationResult(),
                            InitializeAction = snapshot =>
                            {
                                BassAudioPlayer.Frequency = SampleRate.SAMPLE_RATE_44100Hz;
                                if (BassAudioPlayer.ActiveSession == null) { BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _); }
                            }
                        };
                        var player = new InternalBMSAutoPlayerSoundOnly(new SettingsPlayerSettingsGateway(() => testSettings), runtime,
                            autoPlayerFactory: path =>
                            {
                                if (path == nextPath) { parses++; }
                                var parseClock = System.Diagnostics.Stopwatch.StartNew();
                                var chart = new Ribbit.BMS.BMSFile(path);
                                if (path == nextPath) { preparationMs += parseClock.Elapsed.TotalMilliseconds; }
                                return new MeasuredBmsAutoPlayer(chart, elapsed =>
                                {
                                    if (path == nextPath) { decodes++; preparationMs += elapsed.TotalMilliseconds; }
                                });
                            },
                            songPreparation: (input, token) =>
                            {
                                preparationEntered.SetResult();
                                preparationGate.Wait();
                                var prepareClock = System.Diagnostics.Stopwatch.StartNew();
                                parses++;
                                var chart = new Ribbit.BMS.BMSFile(input.Path);
                                decodes++;
                                var result = PreparedBmsSong.Prepare(chart, 0.4f, token);
                                preparationMs += prepareClock.Elapsed.TotalMilliseconds;
                                preparationFinished.SetResult();
                                return result;
                            });
                        PlaybackStartOperation first = player.BeginStart(firstPath, route == "natural" ? (_, _) =>
                        {
                            clock.Start();
                            switchStarted.TrySetResult(player.BeginStart(nextPath, null, allowPreload: true));
                        }
                        : null, allowPreload: true);
                        PlaybackStartOperation? next = null;
                        try
                        {
                            await first.Ready;
                            if (usePreload)
                            {
                                await ((INextSongPreloadPlayer)player).PrepareNextAsync(NextSongPreloadInput.Capture(nextPath), _ => System.Threading.Interlocked.Increment(ref unexpectedNotificationCount));
                                await preparationEntered.Task;
                                if (route != "pending") { await preparationFinished.Task; }
                            }
                            if (route == "natural") { next = await switchStarted.Task; }
                            else
                            {
                                clock.Start();
                                next = player.BeginStart(nextPath, null, allowPreload: true);
                                preparationGate.Set();
                            }
                            await next.Ready;
                            clock.Stop();
                            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { route, usePreload, round, switchMs = clock.Elapsed.TotalMilliseconds, preparationMs, parses, decodes, initializationCalls = events.Count(value => value == "initialize") }));
                            Assert.AreEqual(1, parses);
                            Assert.AreEqual(1, decodes);
                        }
                        finally
                        {
                            preparationGate.Set();
                            await player.CloseAsync();
                            try { await first.Completion; } catch (OperationCanceledException) { }
                            if (next != null) { try { await next.Completion; } catch (OperationCanceledException) { } }
                            BassAudioPlayer.Free();
                            BassAudioRuntime.Shutdown();
                        }
                    }
                }
            }
        }
        finally
        {
            BassAudioPlayer.Frequency = previousFrequency;
            Directory.Delete(directory, recursive: true);
        }
        Assert.AreEqual(0, unexpectedNotificationCount, "想定外の通知を呼び出しません。");
    }
}
