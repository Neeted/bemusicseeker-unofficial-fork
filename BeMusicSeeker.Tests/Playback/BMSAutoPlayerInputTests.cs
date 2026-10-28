using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NLog;
using NLog.Config;
using NLog.Targets;
using Ribbit.BMS;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using NextSongPreloadInput = BeMusicSeeker.Models.NextSongPreloadInput;
using NextSongPreloadOwner = BeMusicSeeker.Models.NextSongPreloadOwner;

namespace BeMusicSeeker.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BMSAutoPlayerInputTests
{
    /// <summary>実在する各階層と譜面側に同名・別PCMを置き、BMS/PMSの親参照とdir経由の親参照が意図した階層の音源だけを採用する。</summary>
    /// <remarks>実PlaybackChart.LoadからNULL_DEVICEの音源読込み・decoderまで接続し、採用絶対path・元PCM・frame・rate・省略なしを確認するため共有native状態を直列所有する。</remarks>
    [DataTestMethod]
    [DataRow("../tone.wav", "a/b", 3, 0.375f, "bms")]
    [DataRow("../../tone.wav", "a", 2, 0.25f, "bms")]
    [DataRow("../../../tone.wav", "", 1, 0.125f, "bms")]
    [DataRow("dir/../../tone.wav", "a/b", 3, 0.375f, "bms")]
    [DataRow("dir/../tone.wav", "a/b/c", 4, 0.5f, "bms")]
    [DataRow("..\\..\\tone.wav", "a", 2, 0.25f, "pms")]
    [DataRow("dir\\..\\..\\tone.wav", "a/b", 3, 0.375f, "pms")]
    public void BmsParentResourceReferencesLoadTheIntendedHierarchyPcm(string reference, string expectedDirectory,
        int expectedFrames, float expectedSample, string extension)
    {
        using var directory = new TemporaryDirectory();
        foreach ((string folder, int frames, short sample) in new (string, int, short)[]
        {
            ("", 1, 4096), ("a", 2, 8192), ("a/b", 3, 12288), ("a/b/c", 4, 16384), ("a/b/c/dir", 5, 20480)
        })
        {
            Directory.CreateDirectory(directory.File(folder));
            byte[] wave = BuildPcmWave(frames);
            for (int frame = 0; frame < frames; frame++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + frame * sizeof(short)), sample);
            }
            File.WriteAllBytes(Path.Combine(directory.File(folder), "tone.wav"), wave);
        }
        string path = directory.File("a/b/c/chart." + extension);
        WriteChart(path, "#BPM 120\n#WAV01 " + reference + "\n#00011:01\n");
        using var player = new BMSAutoPlayer(PlaybackChart.Load(path));
        player.LoadResources();
        BmsAudioResource resource = player.AudioResourcesByIndex.Single(item => item != null)
            ?? throw new AssertFailedException("親参照の音源が採用されませんでした。");
        // 期待配置は入力参照から導出せず、fixtureで用意した既知の階層を使います。
        Assert.AreEqual(Path.GetFullPath(Path.Combine(directory.File(expectedDirectory), "tone.wav")), resource.Path);
        Assert.AreEqual((long)expectedFrames, resource.Audio.FrameCount);
        Assert.AreEqual(44100, resource.Audio.SampleRate);
        for (int frame = 0; frame < expectedFrames; frame++)
        {
            Assert.AreEqual(expectedSample, resource.Audio.GetSample(frame), 0f);
        }
        Assert.AreEqual(0, player.OmittedAudioSources.Count);
    }

    /// <summary>BMS／bmsonでwav→ogg→mp3の最初の復号成功を採用し、選んだ候補の元rate・frame・PCMを保って途中失敗を省略に出さない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [DataTestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public void AudioFallbackUsesFirstDecodedCandidateAndKeepsItsPcm(bool bmson, int successfulCandidate)
    {
        using var directory = new TemporaryDirectory();
        string[] names = ["tone.wav", "tone.ogg", "tone.mp3"];
        for (int index = 0; index < names.Length; index++)
        {
            // コンテナー判定はsignatureの契約なので、拡張子候補ごとに異なる小さいWAVEを使います。
            byte[] wave = BuildPcmWave(frameCount: index + 1);
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44), (short)(8192 * (index + 1)));
            File.WriteAllBytes(directory.File(names[index]), index < successfulCandidate ? [1, 2, 3] : wave);
        }
        using var player = new BMSAutoPlayer(CreateFallbackChart(directory, bmson, "tone.mp3"));
        player.LoadResources();
        BmsAudioResource resource = player.AudioResourcesByIndex.Single(item => item != null)
            ?? throw new AssertFailedException("The fallback source is missing.");
        Assert.AreEqual(directory.File(names[successfulCandidate]), resource.Path);
        Assert.AreEqual(successfulCandidate + 1L, resource.Audio.FrameCount);
        Assert.AreEqual(44100, resource.Audio.SampleRate);
        Assert.AreEqual(0.25f * (successfulCandidate + 1), resource.Audio.GetSample(0), 0f);
        Assert.AreEqual(0, player.OmittedAudioSources.Count);
    }

    /// <summary>サブフォルダの全拡張子候補をbasenameより先に試し、失敗を尽くしてからbasenameの正常候補を採用する。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AudioFallbackExhaustsSubdirectoryBeforeBasenameCandidates(bool bmson)
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.File("sub"));
        File.WriteAllBytes(directory.File("sub/tone.wav"), [1, 2, 3]);
        File.WriteAllBytes(directory.File("sub/tone.ogg"), [4, 5, 6]);
        File.WriteAllBytes(directory.File("sub/tone.mp3"), BuildPcmWave(3));
        File.WriteAllBytes(directory.File("tone.wav"), BuildPcmWave(4));
        File.WriteAllBytes(directory.File("tone.ogg"), BuildPcmWave(5));
        PlaybackChart chart = CreateFallbackChart(directory, bmson, "sub/tone.wav");
        BmsAudioResourceLoadResult first = BmsAudioResourceLoader.Load(chart, directory.File(""), 1f);
        Assert.AreEqual(Path.GetFullPath(directory.File("sub/tone.mp3")), first.ResourcesByIndex.Single(item => item != null)?.Path);
        File.WriteAllBytes(directory.File("sub/tone.mp3"), [7, 8, 9]);
        BmsAudioResourceLoadResult rescued = BmsAudioResourceLoader.Load(chart, directory.File(""), 1f);
        Assert.AreEqual(directory.File("tone.wav"), rescued.ResourcesByIndex.Single(item => item != null)?.Path);
        File.WriteAllBytes(directory.File("tone.wav"), [1, 2, 3]);
        BmsAudioResourceLoadResult next = BmsAudioResourceLoader.Load(chart, directory.File(""), 1f);
        Assert.AreEqual(directory.File("tone.ogg"), next.ResourcesByIndex.Single(item => item != null)?.Path);
        Assert.AreEqual(0, next.Failures.Count);
    }

    /// <summary>候補readの共有違反は入力由来失敗として次候補へ進み、正常代替を省略と扱わない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void AudioFallbackReadFailureContinuesToNextCandidate()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("tone.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("tone.ogg"), BuildPcmWave(2));
        PlaybackChart chart = CreateFallbackChart(directory, false, "tone.wav");
        using (var locked = new FileStream(directory.File("tone.wav"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            BmsAudioResourceLoadResult result = BmsAudioResourceLoader.Load(chart, directory.File(""), 1f);
            Assert.AreEqual(directory.File("tone.ogg"), result.ResourcesByIndex[1]?.Path);
            Assert.AreEqual(0, result.Failures.Count);
        }
    }

    /// <summary>候補段階を跨ぐ大文字／相対path aliasの成功・失敗を一回のread／decodeへ集約し、同じ正常PCMを共有する。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void AudioFallbackSharesSuccessAndFailurePathsAcrossCandidateStages()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.File("sub"));
        File.WriteAllBytes(directory.File("tone.wav"), [1, 2, 3]);
        File.WriteAllBytes(directory.File("tone.ogg"), BuildPcmWave(3));
        WriteChart(directory.File("chart.bms"), "#WAV01 sub/tone.wav\n#WAV02 ./TONE.WAV\n#00011:0102\n");
        var reads = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var decodes = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        BmsAudioResourceLoadResult result = BmsAudioResourceLoader.Load(PlaybackChart.Load(directory.File("chart.bms")), directory.File(""), 1f,
            readInput: path => { reads.AddOrUpdate(path, 1, (_, count) => count + 1); return AudioInputFile.Read(path); },
            decode: (input, session) => { decodes.AddOrUpdate(input.Path, 1, (_, count) => count + 1); return AudioSourceLoader.Decode(input, session); });
        Assert.IsTrue(reads.Values.All(count => count == 1));
        Assert.AreEqual(1, reads[directory.File("tone.wav")]);
        Assert.AreEqual(1, reads[directory.File("tone.ogg")]);
        Assert.AreEqual(1, decodes[directory.File("tone.wav")]);
        Assert.AreEqual(1, decodes[directory.File("tone.ogg")]);
        Assert.AreSame(result.ResourcesByIndex[1], result.ResourcesByIndex[2]);
        Assert.IsTrue(string.Equals(directory.File("tone.ogg"), result.ResourcesByIndex[1]?.Path, StringComparison.OrdinalIgnoreCase));
        Assert.AreEqual(0, result.Failures.Count);
    }

    /// <summary>入力以外のread／decode故障は元原因を保持してロード全体を失敗させ、正常代替を試さず入力・decoderを回収する。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AudioFallbackFatalReadOrDecodeFailureNeverTriesHealthyAlternative(bool duringDecode)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("tone.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("tone.ogg"), BuildPcmWave(2));
        PlaybackChart chart = CreateFallbackChart(directory, false, "tone.wav");
        BassAudioSession session = BmsAudioResourceLoader.CaptureActiveSession();
        int streams = session.OwnedStreamCount;
        foreach (Exception cause in new Exception[] { new OutOfMemoryException(), new DllNotFoundException(),
            new InvalidOperationException("session/device"), new AudioSourceFatalException("release"), new Exception("unknown") })
        {
            AudioInputFile? held = null;
            int alternatives = 0;
            AudioSourceFatalException fatal = Assert.ThrowsException<AudioSourceFatalException>(() => BmsAudioResourceLoader.Load(chart, directory.File(""), 1f,
                readInput: path =>
                {
                    if (path.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref alternatives);
                    }

                    if (!duringDecode)
                    {
                        throw cause;
                    }

                    return held = AudioInputFile.Read(path);
                }, decode: (input, activeSession) =>
                {
                    _ = AudioSourceLoader.Decode(input, activeSession);
                    throw cause;
                }));
            Assert.IsTrue(ContainsCause(fatal, cause));
            Assert.AreEqual(0, alternatives);
            Assert.AreEqual(streams, session.OwnedStreamCount);
            if (held != null)
            {
                Assert.ThrowsException<ObjectDisposedException>(() => held.OpenReadView());
            }
        }
    }

    /// <summary>復号中の取消は復号worker実終端へ合流し、入力とdecoderを解放してsessionを維持した取消として返す。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public async Task AudioFallbackCancellationDuringDecodeJoinsAndReleasesInputAndDecoder()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("tone.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("tone.ogg"), BuildPcmWave(2));
        PlaybackChart chart = CreateFallbackChart(directory, false, "tone.wav");
        BassAudioSession session = BmsAudioResourceLoader.CaptureActiveSession();
        int streams = session.OwnedStreamCount;
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<AudioInputFile>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<BmsAudioResourceLoadResult> loading = Task.Run(() => BmsAudioResourceLoader.Load(chart, directory.File(""), 1f, cancellation.Token, session,
            decode: (input, activeSession) =>
            {
                DecodedAudio audio = AudioSourceLoader.Decode(input, activeSession);
                entered.TrySetResult(input);
                release.Wait();
                return audio;
            }));
        try
        {
            Task first = await Task.WhenAny(entered.Task, loading);
            if (ReferenceEquals(first, loading))
            {
                await loading;
            }

            AudioInputFile held = await entered.Task;
            cancellation.Cancel();
            Assert.IsFalse(loading.IsCompleted);
            release.Set();
            await Assert.ThrowsExceptionAsync<OperationCanceledException>(() => loading);
            Assert.ThrowsException<ObjectDisposedException>(() => held.OpenReadView());
            Assert.AreEqual(streams, session.OwnedStreamCount);
            Assert.AreSame(session, BassAudioPlayer.ActiveSession);
        }
        finally
        {
            cancellation.Cancel();
            release.Set();
            try { await loading; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>BMSの親参照と譜面側同名別音源、bmsonの通常参照で正常代替を先読みし、入力消失後も本来の準備PCMを採用して再読込みしない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NextSongFallbackPreparationAdoptsDecodedResultAfterInputsDisappear(bool bmson)
    {
        using var directory = new TemporaryDirectory();
        string sourceDirectory = directory.File(bmson ? "" : "a");
        string chartDirectory = directory.File(bmson ? "" : "a/b/c");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(chartDirectory);
        string firstCandidate = Path.Combine(sourceDirectory, "tone.wav");
        string expectedAudioPath = Path.Combine(sourceDirectory, "tone.ogg");
        File.WriteAllBytes(firstCandidate, [1, 2, 3]);
        File.WriteAllBytes(expectedAudioPath, BuildPcmWave(2));
        PlaybackChart chart;
        if (bmson)
        {
            chart = CreateFallbackChart(directory, true, "tone.wav");
        }
        else
        {
            byte[] decoy = BuildPcmWave(4);
            for (int frame = 0; frame < 4; frame++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(decoy.AsSpan(44 + frame * sizeof(short)), 8192);
            }
            File.WriteAllBytes(Path.Combine(chartDirectory, "tone.wav"), decoy);
            string path = Path.Combine(chartDirectory, "chart.bms");
            WriteChart(path, "#BPM 120\n#WAV01 ../../tone.wav\n#00011:01\n");
            chart = PlaybackChart.Load(path);
        }
        var input = NextSongPreloadInput.Capture(chart.Path);
        var owner = new NextSongPreloadOwner((request, token) => PreparedBmsSong.Prepare(PlaybackChart.Load(request.Path), 1f, token));
        try
        {
            PreparedBmsSong expected = await owner.Request(input);
            File.Delete(chart.Path);
            File.Delete(firstCandidate);
            File.Delete(expectedAudioPath);
            PreparedBmsSong prepared = await owner.TakeAsync(input) ?? throw new AssertFailedException("Missing prepared fallback.");
            Assert.AreSame(expected, prepared);
            using var player = new BMSAutoPlayer(prepared.Chart);
            player.AdoptPreparedSong(prepared);
            BmsAudioResource resource = player.AudioResourcesByIndex.Single(item => item != null)
                ?? throw new AssertFailedException("準備済みの音源が採用されませんでした。");
            Assert.AreEqual(expectedAudioPath, resource.Path);
            Assert.AreEqual(2L, resource.Audio.FrameCount);
            Assert.AreEqual(44100, resource.Audio.SampleRate);
            Assert.AreEqual(0.5f, resource.Audio.GetSample(0), 0f);
            Assert.AreEqual(0.5f, resource.Audio.GetSample(1), 0f);
            Assert.AreEqual(0, player.OmittedAudioSources.Count);
        }
        finally { await owner.InvalidateAsync(); }
    }

    private static bool ContainsCause(Exception failure, Exception cause) => ReferenceEquals(failure, cause)
        || (failure is AggregateException aggregate && aggregate.InnerExceptions.Any(item => ContainsCause(item, cause)))
        || (failure.InnerException is Exception inner && ContainsCause(inner, cause));

    private static PlaybackChart CreateFallbackChart(TemporaryDirectory directory, bool bmson, string resource)
    {
        string path = directory.File(bmson ? "chart.bmson" : "chart.bms");
        if (bmson)
        {
            File.WriteAllText(path, "{\"info\":{\"init_bpm\":120},\"sound_channels\":[{\"name\":\"" + resource + "\",\"notes\":[{\"y\":0}]}]}");
        }
        else
        {
            WriteChart(path, "#BPM 120\n#WAV01 " + resource + "\n#00011:01\n");
        }

        return PlaybackChart.Load(path);
    }
    [TestInitialize]
    public void InitializeAudioRuntime()
    {
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
        BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
    }

    [TestCleanup]
    public void ShutdownAudioRuntime()
    {
        BassAudioPlayer.Free();
        BassAudioRuntime.Shutdown();
    }

    /// <summary>bmson準備の共有PCMと解析sliceを入力消失後も採用し、二つの発音を保ってnative先行voiceと二重採用を防ぐ。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void BmsonPreparationKeepsSharedPcmAndParsedSlicesAfterInputsDisappear()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("chart.bmson");
        string wave = directory.File("used.ogg");
        File.WriteAllBytes(directory.File("used.wav"), [1, 2, 3]);
        File.WriteAllBytes(wave, BuildPcmWave(frameCount: 2205));
        File.WriteAllText(path, "{\"info\":{\"init_bpm\":120},\"sound_channels\":[{\"name\":\"used.wav\",\"notes\":[{\"y\":0}]},{\"name\":\"used.wav\",\"notes\":[{\"y\":0}]}]}");
        var chart = PlaybackChart.Load(path);
        BassAudioSession session = BmsAudioResourceLoader.CaptureActiveSession();
        int streams = session.OwnedStreamCount;
        var prepared = PreparedBmsSong.Prepare(chart, 0.1f, expectedSession: session);
        File.Delete(wave);
        File.Delete(path);
        using var player = new BMSAutoPlayer(prepared.Chart);
        player.AdoptPreparedSong(prepared);
        Assert.AreSame(chart, player.Chart);
        Assert.IsNull(player.Bms);
        Assert.AreSame(player.AudioResourcesByIndex[0], player.AudioResourcesByIndex[1]);
        Assert.AreEqual(2, player.AudioSchedule.Events.Count);
        Assert.AreEqual(streams, session.OwnedStreamCount);
        Assert.ThrowsException<InvalidOperationException>(() => prepared.TakeResources());
    }

    /// <summary>BMSの選択済みRANDOM分岐と共有PCMを入力消失後も使い、次出力rateへscheduleを結び直して一回だけ採用する。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [DataTestMethod]
    [DataRow(44100)]
    [DataRow(48000)]
    public async Task PreparedSong_UsesParsedBranchAndDecodedPcmAfterInputsDisappear(int outputRate)
    {
        SampleRate previousFrequency = BassAudioPlayer.Frequency;
        try
        {
            using var directory = new TemporaryDirectory();
            string path = directory.File("chart.bms");
            string wave = directory.File("used.ogg");
            File.WriteAllBytes(directory.File("used.wav"), [1, 2, 3]);
            File.WriteAllBytes(wave, BuildPcmWave(frameCount: 2205));
            WriteChart(path, "#BPM 400\n#RANDOM 2\n#IF 1\n#WAV01 used.wav\n#WAV02 ./USED.WAV\n#00011:0102\n#ENDIF\n#IF 2\n#WAV01 absent.wav\n#00011:01\n#ENDIF\n#ENDRANDOM\n");
            var chart = new BMSFile(path, new Queue<int>([1]));
            BassAudioSession session = BassAudioPlayer.ActiveSession
                ?? throw new AssertFailedException("The audio session is missing.");
            int ownedStreams = session.OwnedStreamCount;
            var prepared = PreparedBmsSong.Prepare(chart, 0.1f, expectedSession: session);
            Assert.AreEqual(ownedStreams, session.OwnedStreamCount, "Preparation must leave no native decoder or voice.");
            File.Delete(wave);
            File.Delete(path);
            BassAudioPlayer.Free();
            BassAudioPlayer.Frequency = outputRate == 48000 ? SampleRate.SAMPLE_RATE_48000Hz : SampleRate.SAMPLE_RATE_44100Hz;
            BassAudioPlayer.InitializeOwned(BassAudioPlayer.DeviceDriver.NULL_DEVICE, default, 0f, out _);
            BassAudioSession playbackSession = BassAudioPlayer.ActiveSession
                ?? throw new AssertFailedException("The playback session is missing.");
            int playbackOwnedStreams = playbackSession.OwnedStreamCount;
            using var player = new BMSAutoPlayer(prepared.Chart);
            player.AdoptPreparedSong(prepared);
            Assert.AreSame(chart, player.Bms);
            BmsAudioResource first = player.AudioResourcesByIndex[1]
                ?? throw new AssertFailedException("The selected branch was not decoded.");
            Assert.AreSame(first, player.AudioResourcesByIndex[2]);
            Assert.AreEqual(2205L, first.Audio.FrameCount);
            Assert.AreEqual(outputRate, player.AudioSchedule.SampleRate);
            CollectionAssert.AreEqual(new long[] { 0, outputRate * 3L / 10 }, player.AudioSchedule.Events.Select(item => item.StartFrame).ToArray());
            Assert.AreEqual(BassAudioPlayer.DefaultVolume, first.SourceGain,
                "The next start binds source gain without copying or scaling PCM.");
            Assert.AreEqual(0, player.OmittedAudioSources.Count);
            Assert.AreEqual(playbackOwnedStreams, playbackSession.OwnedStreamCount, "Adoption must not create voices before Start.");
            await player.Start();
            Assert.ThrowsException<InvalidOperationException>(() => prepared.TakeResources());
        }
        finally { BassAudioPlayer.Frequency = previousFrequency; }
    }

    /// <summary>事前取消した準備は取消分類と既存sessionを保ち、native streamを増減させない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void PreparedSong_CancellationPreservesSessionAndCancellationClassification()
    {
        using var directory = new TemporaryDirectory();
        WriteChart(directory.File("chart.bms"), "#WAV01 used.wav\n#00111:01\n");
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        BassAudioSession session = BassAudioPlayer.ActiveSession ?? throw new AssertFailedException("Audio session is missing.");
        int streams = session.OwnedStreamCount;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsException<OperationCanceledException>(() => PreparedBmsSong.Prepare(new BMSFile(directory.File("chart.bms")), 0.4f, cancellation.Token, session));
        Assert.AreEqual(streams, session.OwnedStreamCount);
        Assert.AreSame(session, BassAudioPlayer.ActiveSession);
    }

    /// <summary>準備時は省略WARNを出さず、採用時に最終欠落だけ一度通知して途中代替失敗を警告に含めない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void PreparedSong_DefersOmissionWarningsUntilAdoption()
    {
        using var directory = new TemporaryDirectory();
        WriteChart(directory.File("chart.bms"), "#WAV01 missing.wav\n#WAV02 used.wav\n#WAV03 rescued.wav\n#00111:010203\n");
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("rescued.wav"), [1, 2, 3]);
        File.WriteAllBytes(directory.File("rescued.ogg"), BuildPcmWave(2));
        _ = NLogWrapper.GetLogger(nameof(BMSAutoPlayer));
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        var warnings = new MemoryTarget { Layout = "${message}" };
        var configuration = new LoggingConfiguration();
        configuration.AddRule(LogLevel.Warn, LogLevel.Warn, warnings, nameof(BMSAutoPlayer));
        LogManager.Configuration = configuration;
        try
        {
            var chart = new BMSFile(directory.File("chart.bms"));
            var prepared = PreparedBmsSong.Prepare(chart, 0.4f);
            Assert.AreEqual(0, warnings.Logs.Count);
            using var player = new BMSAutoPlayer(chart);
            player.AdoptPreparedSong(prepared);
            Assert.AreEqual(1, warnings.Logs.Count);
            Assert.AreEqual(1, player.OmittedAudioSources.Count);
            Assert.AreEqual(directory.File("rescued.ogg"), player.AudioResourcesByIndex[3]?.Path);
        }
        finally { LogManager.Configuration = originalConfiguration; }
    }

    /// <summary>使用中の欠落・破損pathをalias単位に集約して一度WARNし、未使用定義を開かず正常・代替PCMで処理を続ける。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void LoadResources_WarnsOncePerMissingOrCorruptPathWhileContinuing()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("unused-broken.wav"), [0x01, 0x02, 0x03]);
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("broken.wav"), [0x04, 0x05, 0x06]);
        File.WriteAllBytes(directory.File("rescued.wav"), [1, 2, 3]);
        File.WriteAllBytes(directory.File("rescued.mp3"), BuildPcmWave(3));
        WriteChart(directory.File("chart.bms"),
            "#WAV01 unused-broken.wav\n"
            + "#WAV02 used.wav\n"
            + "#WAV03 missing.wav\n"
            + "#WAV04 broken.wav\n"
            + "#WAV05 ./BROKEN.WAV\n"
            + "#WAV06 rescued.wav\n"
            + "#00111:0203040506\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        _ = NLogWrapper.GetLogger(nameof(BMSAutoPlayer));
        LoggingConfiguration? originalConfiguration = LogManager.Configuration;
        var warningTarget = new MemoryTarget { Layout = "${message}|${exception:format=tostring}" };
        var testConfiguration = new LoggingConfiguration();
        testConfiguration.AddRule(LogLevel.Warn, LogLevel.Warn, warningTarget, nameof(BMSAutoPlayer));
        LogManager.Configuration = testConfiguration;

        try
        {
            player.LoadResources();

            Assert.IsNull(player.GetAudioResource(1), "An unused malformed definition must not be decoded.");
            Assert.IsNotNull(player.GetAudioResource(2), "The used audio file must be loaded.");
            Assert.IsNull(player.GetAudioResource(3), "A missing explicit source is omitted after its read failure.");
            Assert.IsNull(player.GetAudioResource(4));
            Assert.IsNull(player.GetAudioResource(5));
            Assert.AreEqual(directory.File("rescued.mp3"), player.GetAudioResource(6)?.Path);
            Assert.AreEqual(2, player.OmittedAudioSources.Count);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    Path.GetFullPath(directory.File("missing.wav")),
                    Path.GetFullPath(directory.File("broken.wav"))
                },
                player.OmittedAudioSources.Select(omission => omission.Path).ToArray());
            Assert.AreEqual(2, warningTarget.Logs.Count, "The missing source and broken aliases each produce one warning.");
            string warnings = string.Join(Environment.NewLine, warningTarget.Logs);
            StringAssert.Contains(warnings, "missing.wav");
            StringAssert.Contains(warnings, "broken.wav");
            StringAssert.Contains(warnings, nameof(AudioSourceLoadException));
            StringAssert.Contains(warnings, nameof(FileNotFoundException));
        }
        finally
        {
            try
            {
                player.DisposeLoadedAudio();
            }
            finally
            {
                try
                {
                    LogManager.Flush();
                }
                finally
                {
                    LogManager.Configuration = originalConfiguration;
                }
            }
        }
    }

    /// <summary>使用する明示音源が全欠落なら元read段階付きでロードを失敗させ、部分省略や空resourceを正常公開しない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void LoadResources_AbortsWhenEveryExplicitMissingAudioFails()
    {
        using var directory = new TemporaryDirectory();
        WriteChart(directory.File("chart.bms"),
            "#WAV02 missing.wav\n"
            + "#WAV03 also-missing.wav\n"
            + "#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            InvalidDataException failure = Assert.ThrowsException<InvalidDataException>(
                player.LoadResources);

            StringAssert.Contains(failure.Message, "missing.wav");
            StringAssert.Contains(failure.Message, nameof(AudioSourceLoadStage.InspectContainer));
            Assert.IsInstanceOfType<AudioSourceLoadException>(failure.InnerException);
            Assert.AreEqual(AudioSourceLoadStage.InspectContainer,
                ((AudioSourceLoadException)failure.InnerException).Stage);
            Assert.IsNull(player.GetAudioResource(2));
            Assert.IsNull(player.GetAudioResource(3));
            Assert.AreEqual(0, player.OmittedAudioSources.Count, "An all-failed chart does not publish partial omission state.");
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    /// <summary>全候補失敗では最初に存在した候補のdecode原因を残し、後続欠落で上書きせず全失敗として返す。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void AudioFallbackAllFailedKeepsFirstExistingDecodeCauseAfterLaterMissingCandidates()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(directory.File("sub"));
        File.WriteAllBytes(directory.File("sub/tone.ogg"), [1, 2, 3]);
        PlaybackChart chart = CreateFallbackChart(directory, true, "sub/tone.wav");
        BmsAudioResourceLoadResult result = BmsAudioResourceLoader.Load(chart, directory.File(""), 1f);
        Assert.AreEqual(1, result.Failures.Count);
        var cause = (AudioSourceLoadException)result.Failures[0].Exception;
        Assert.AreEqual(AudioSourceLoadStage.DecodeWithBass, cause.Stage);
        Assert.AreEqual(Path.GetFullPath(directory.File("sub/tone.ogg")), result.Failures[0].NormalizedPath);
        using var player = new BMSAutoPlayer(chart);
        InvalidDataException failure = Assert.ThrowsException<InvalidDataException>(() => player.LoadResources());
        Assert.IsInstanceOfType<AudioSourceLoadException>(failure.InnerException);
        Assert.AreEqual(AudioSourceLoadStage.DecodeWithBass, ((AudioSourceLoadException)failure.InnerException).Stage);
        Assert.AreEqual(0, player.OmittedAudioSources.Count);
    }

    /// <summary>未定義・空定義のWAV参照を入力要求や省略へ変えず、音源なしの譜面読込みを成功させる。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void LoadResources_UndefinedAndEmptyWavDefinitionsDoNotRequestFiles()
    {
        using var directory = new TemporaryDirectory();
        WriteChart(directory.File("chart.bms"), "#WAV02 \n#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        try
        {
            player.LoadResources();

            Assert.AreEqual(0, player.OmittedAudioSources.Count);
            Assert.IsNull(player.GetAudioResource(2));
            Assert.IsNull(player.GetAudioResource(3));
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    /// <summary>使用中のゼロframe Waveを正常resourceと扱い、後続候補へ置換も省略計上もしない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void LoadResources_LoadsUsedZeroFrameWaveWithoutOmissions()
    {
        using var directory = new TemporaryDirectory();
        using var wave = AudioMixerSignalTests.TemporaryFloatWave.Create(
            48000,
            0,
            _ => 0f);
        File.Copy(wave.Path, directory.File("empty.wav"));
        File.WriteAllBytes(directory.File("empty.ogg"), BuildPcmWave(3));
        WriteChart(
            directory.File("chart.bms"),
            "#WAV02 empty.wav\n#00111:02\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));

        try
        {
            player.LoadResources();

            BmsAudioResource loaded = player.GetAudioResource(2)
                ?? throw new AssertFailedException("The explicitly used zero-frame WAVE did not create a resource.");
            Assert.AreEqual(TimeSpan.Zero, loaded.Duration);
            Assert.AreEqual(0, player.OmittedAudioSources.Count);
        }
        finally
        {
            player.DisposeLoadedAudio();
        }
    }

    /// <summary>一曲内の正規化path aliasは同じPCMを共有し、次playerでは変更済みファイルを読み直して古いcacheを使わない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public void LoadResources_SharesPcmWithinSongAndReloadsChangedWaveForNextPlayer()
    {
        var failures = new List<Exception>();
        var directory = new TemporaryDirectory();
        TestBMSAutoPlayer? firstPlayer = null;
        TestBMSAutoPlayer? nextPlayer = null;
        try
        {
            string audioPath = directory.File("shared.wav");
            string chartPath = directory.File("chart.bms");
            WriteChart(
                chartPath,
                "#WAV02 shared.wav\n"
                + "#WAV03 ./shared.wav\n"
                + "#00111:0203\n");

            float[] firstSamples = [0.25f, -0.5f, 0.75f, -0.125f];
            using (var wave = AudioMixerSignalTests.TemporaryFloatWave.Create(
                48000,
                4,
                frame => firstSamples[checked((int)frame)]))
            {
                File.Copy(wave.Path, audioPath);
            }

            firstPlayer = new TestBMSAutoPlayer(new BMSFile(chartPath));
            firstPlayer.LoadResources();
            AssertSharedAudioMatches(firstPlayer, firstSamples);
            firstPlayer.DisposeLoadedAudio();
            firstPlayer = null;

            float[] nextSamples = [-0.75f, 0.125f, -0.25f, 0.5f];
            using (var wave = AudioMixerSignalTests.TemporaryFloatWave.Create(
                48000,
                4,
                frame => nextSamples[checked((int)frame)]))
            {
                File.Copy(wave.Path, audioPath, overwrite: true);
            }

            nextPlayer = new TestBMSAutoPlayer(new BMSFile(chartPath));
            nextPlayer.LoadResources();
            AssertSharedAudioMatches(nextPlayer, nextSamples);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            if (firstPlayer is not null)
            {
                CaptureCleanup(failures, firstPlayer.DisposeLoadedAudio);
            }
            if (nextPlayer is not null)
            {
                CaptureCleanup(failures, nextPlayer.DisposeLoadedAudio);
            }
            CaptureCleanup(failures, directory.Dispose);
        }

        ThrowFailures(failures);
    }

    /// <summary>次曲向けDisposeは旧再生のStopとTask合流が終わるまで公開PCMを維持し、停止処理から資源を先に奪わない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [TestMethod]
    public async Task DisposeBeforeNextSong_StopsAndJoinsBeforeDroppingPublishedAudioResources()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#BPM 400\n#WAV02 used.wav\n#10011:02\n");
        var player = new StopObservingBMSAutoPlayer(
            new BMSFile(directory.File("chart.bms")),
            watchedIndex: 2);
        player.LoadResources();

        Task play = player.Start();
        var failures = new List<Exception>();
        try
        {
            player.DisposeBeforeNextSong();
            Assert.IsTrue(player.SawResourceBeforeStop, "Stop must see the old decoded resources.");
            Assert.IsTrue(player.SawResourceAfterStop, "Resources must remain published through Stop's task join and reset.");
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        await CaptureTaskCompletionAsync(play, failures, exception => exception is OperationCanceledException);
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    /// <summary>UI dispatcher上の次曲Disposeは旧scheduler・voice・tempo graphを実終端し、再利用sessionで次曲を開始・停止できる。</summary>
    /// <remarks>共有UI dispatcherと実NULL_DEVICEを接続し、UI同期の曲切替でもnative graph解放と再受付を完了できることを検査する。</remarks>
    [TestMethod]
    public void DisposeBeforeNextSongOnUiDispatcher_ReleasesOldRealtimeGraphBeforeStartingNextSong()
    {
        var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("used.wav"), BuildPcmWave(frameCount: 44100 * 8));
        WriteChart(directory.File("first.bms"), "#WAV02 used.wav\n#BPM 400\n#00011:02\n");
        WriteChart(directory.File("next.bms"), "#WAV02 used.wav\n#BPM 400\n#00011:02\n");
        BMSAutoPlayer? firstPlayer = null;
        BMSAutoPlayer? nextPlayer = null;
        var failures = new List<Exception>();

        try
        {
            TestUiDispatcherHost.Invoke(() =>
            {
                Assert.IsInstanceOfType(SynchronizationContext.Current, typeof(DispatcherSynchronizationContext));

                firstPlayer = new BMSAutoPlayer(new BMSFile(directory.File("first.bms")));
                firstPlayer.LoadResources();
                BassAudioSession session = firstPlayer.ResourceSession;
                int baselineOwnedStreams = session.OwnedStreamCount;
                int baselinePlayerStreams = session.GetPlayerStreams().Count;
                int baselineAdditionalStreams = session.AdditionalStreamHandles.Count;

                Task firstPlayback = firstPlayer.Start();
                BmsRealtimeAudioScheduler oldScheduler = firstPlayer.RealtimeScheduler
                    ?? throw new AssertFailedException("The first BMS playback did not create its realtime scheduler.");
                Assert.IsTrue(session.GetPlayerStreams().Count > baselinePlayerStreams,
                    "The active song must retain its scheduled voice handles.");
                Assert.AreNotEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle,
                    "The active song must own a tempo output stream even at unity speed.");

                firstPlayer.DisposeBeforeNextSong();

                Assert.IsNull(firstPlayer.RealtimeScheduler,
                    "The next-song release must finish the old scheduler before dropping its session reference.");
                Assert.ThrowsException<ObjectDisposedException>(() => _ = oldScheduler.CurrentSongFrame);
                Assert.AreEqual(baselinePlayerStreams, session.GetPlayerStreams().Count,
                    "The old scheduled voice handles must be released before the next song starts.");
                Assert.AreEqual(baselineAdditionalStreams, session.AdditionalStreamHandles.Count,
                    "The old tempo output handle must be released before the next song starts.");
                Assert.AreEqual(baselineOwnedStreams, session.OwnedStreamCount);
                Assert.AreEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);

                AssertPlaybackCanceledOnUiDispatcher(firstPlayback, "first-song playback cancellation");

                nextPlayer = new BMSAutoPlayer(new BMSFile(directory.File("next.bms")));
                nextPlayer.LoadResources();
                Task nextPlayback = nextPlayer.Start();
                Assert.IsNotNull(nextPlayer.RealtimeScheduler,
                    "The next song must be able to create a new scheduler on the reused session.");
                Assert.AreNotEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);

                nextPlayer.DisposeBeforeNextSong();
                Assert.IsNull(nextPlayer.RealtimeScheduler);
                Assert.AreEqual(baselinePlayerStreams, session.GetPlayerStreams().Count);
                Assert.AreEqual(baselineAdditionalStreams, session.AdditionalStreamHandles.Count);
                Assert.AreEqual(baselineOwnedStreams, session.OwnedStreamCount);
                Assert.AreEqual(session.MixerHandle, BassAudioPlayer.OutputMixerHandle);
                AssertPlaybackCanceledOnUiDispatcher(nextPlayback, "next-song playback cancellation");
            });
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (firstPlayer != null)
        {
            CaptureCleanup(failures, firstPlayer.Dispose);
        }
        if (nextPlayer != null)
        {
            CaptureCleanup(failures, nextPlayer.Dispose);
        }
        CaptureCleanup(failures, directory.Dispose);
        ThrowFailures(failures);
    }

    /// <summary>通常Disposeと次曲Disposeのどちらでも共有PCMを解除し、ロードだけでprototype native voiceを作らない。</summary>
    /// <remarks>実NULL_DEVICEの共通読込み・decoder所有または音声graphを通し、共有native状態を直列所有する。WARNを観測するケースは共有NLog設定も復元する。</remarks>
    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ResourceDisposal_DropsSharedPcmWithoutCreatingPrototypeVoices(bool beforeNextSong)
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllBytes(directory.File("first.wav"), BuildPcmWave());
        File.WriteAllBytes(directory.File("second.wav"), BuildPcmWave());
        WriteChart(directory.File("chart.bms"),
            "#WAV02 first.wav\n#WAV03 second.wav\n#00111:0203\n");
        var player = new TestBMSAutoPlayer(new BMSFile(directory.File("chart.bms")));
        player.LoadResources();
        Assert.IsNotNull(player.GetAudioResource(2));
        Assert.IsNotNull(player.GetAudioResource(3));
        BassAudioSession session = BassAudioPlayer.ActiveSession
            ?? throw new AssertFailedException("The test audio session was not initialized.");
        Assert.AreEqual(0, session.GetPlayerStreams().Count,
            "Loading chart resources must not create an eagerly attached native voice per WAV index.");

        if (beforeNextSong)
        {
            player.DisposeBeforeNextSong();
        }
        else
        {
            player.Dispose();
        }

        Assert.AreEqual(0, session.GetPlayerStreams().Count);
        Assert.AreEqual(0, player.AudioResourcesByIndex.Count);
    }

    private static void WriteChart(string path, string body)
    {
        File.WriteAllText(
            path,
            "#PLAYER 1\n#TITLE audio input\n#ARTIST test\n" + body,
            Encoding.ASCII);
    }

    private static void AssertSharedAudioMatches(TestBMSAutoPlayer player, float[] expectedSamples)
    {
        BmsAudioResource first = player.GetAudioResource(2)
            ?? throw new AssertFailedException("The first shared WAVE resource was not loaded.");
        BmsAudioResource alias = player.GetAudioResource(3)
            ?? throw new AssertFailedException("The aliased WAVE resource was not loaded.");
        Assert.AreSame(first.Audio, alias.Audio, "Aliases in one player must share the decoded PCM.");

        DecodedAudio audio = first.Audio;
        Assert.AreEqual(48000, audio.SampleRate);
        Assert.AreEqual(1, audio.ChannelCount);
        Assert.AreEqual(4L, audio.FrameCount);
        Assert.AreEqual(4, expectedSamples.Length);
        for (int sampleIndex = 0; sampleIndex < expectedSamples.Length; sampleIndex++)
        {
            Assert.AreEqual(
                expectedSamples[sampleIndex],
                audio.GetSample(sampleIndex),
                0f,
                $"Decoded WAVE sample {sampleIndex} did not match the fixture.");
        }
    }

    private static async Task CaptureTaskCompletionAsync(
        Task task,
        List<Exception> failures,
        Func<Exception, bool>? expectedException = null)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception) when (expectedException?.Invoke(exception) == true)
        {
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void CaptureCleanup(List<Exception> failures, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static void ThrowFailures(List<Exception> failures)
    {
        if (failures.Count == 1)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        else if (failures.Count > 1)
        {
            throw new AggregateException("The test failure and cleanup failures are retained.", failures);
        }
    }

    private static void AssertPlaybackCanceledOnUiDispatcher(Task playback, string operationName)
    {
        try
        {
            TestUiDispatcherHost.AwaitTaskOnDispatcher(playback, operationName);
            Assert.Fail("The stopped playback task should report cancellation.");
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static byte[] BuildPcmWave(int frameCount = 1)
    {
        int dataLength = checked(frameCount * sizeof(short));
        byte[] wave = new byte[checked(44 + dataLength)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(4), checked((uint)(wave.Length - 8)));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(24), 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(28), 88200);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(wave.AsSpan(40), checked((uint)dataLength));
        for (int frame = 0; frame < frameCount; frame++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + frame * sizeof(short)), 16384);
        }
        return wave;
    }

    private sealed class TestBMSAutoPlayer(BMSFile bms) : BMSAutoPlayer(bms)
    {
        internal BmsAudioResource? GetAudioResource(int index) => AudioResourcesByIndex[index];

        internal void DisposeLoadedAudio() => DisposeAudioSourcesAfterUse();
    }

    private sealed class StopObservingBMSAutoPlayer(BMSFile bms, int watchedIndex)
        : BMSAutoPlayer(bms)
    {
        internal bool SawResourceBeforeStop { get; private set; }

        internal bool SawResourceAfterStop { get; private set; }

        public override void Stop()
        {
            SawResourceBeforeStop = AudioResourcesByIndex[watchedIndex] != null;
            base.Stop();
            SawResourceAfterStop = AudioResourcesByIndex[watchedIndex] != null;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        private readonly string path = Path.Combine(
            Path.GetTempPath(),
            "BeMusicSeeker-" + Guid.NewGuid().ToString("N"));

        internal TemporaryDirectory()
        {
            Directory.CreateDirectory(path);
        }

        internal string File(string name) => Path.Combine(path, name);

        public void Dispose()
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
