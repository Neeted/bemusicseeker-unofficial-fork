using BeMusicSeeker.Models;
using Ribbit.BMS;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数moduleで共有する既存音声テストの準備・観測境界を一か所で所有します。</summary>
internal static class AudioContractFixture
{
    /// <summary>既存の再生寿命検査で使う固定BMS本文を、呼出元が所有する領域へ書きます。</summary>
    internal static string WritePlaybackChart(string directory, string name, int bpm, int measure)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(
            path,
            $"#PLAYER 1\n#TITLE lifecycle\n#ARTIST test\n#BPM {bpm}\n#{measure:000}11:01\n",
            System.Text.Encoding.ASCII);
        return path;
    }

    /// <summary>再生寿命検査が明示注入する既存のNULL_DEVICE初期化結果を作ります。</summary>
    internal static AudioPlaybackInitializationResult CreatePlaybackInitializationResult() => new(
        AudioDriver.NullDevice,
        string.Empty,
        string.Empty,
        SampleRate.AUTO,
        SampleFormat.AUTO,
        10,
        false,
        50,
        AudioDriver.NullDevice,
        string.Empty,
        string.Empty,
        SampleRate.SAMPLE_RATE_44100Hz,
        SampleFormat.SAMPLE_FLOAT_32BIT,
        SampleFormat.UNKNOWN,
        2,
        0,
        string.Empty,
        isSilentFallback: false);

    /// <summary>再生寿命の呼出順と明示故障を記録し、呼出元が指定した初期化・解放処理へ接続します。</summary>
    internal sealed class RecordingAudioPlaybackRuntime : IAudioPlaybackRuntime
    {
        private readonly List<string> events;

        internal RecordingAudioPlaybackRuntime(List<string> events)
        {
            this.events = events;
        }

        public int CurrentVoices => 0;

        public int MaxVoices => 0;

        /// <summary>
        /// 初期化時に送出する例外。例外を注入しない場合は <see langword="null"/>。
        /// </summary>
        internal Exception? InitializeException { get; init; }

        /// <summary>初期化成功時に返す結果です。</summary>
        internal AudioPlaybackInitializationResult? InitializeResult { get; init; }

        internal Action<PlayerSettingsSnapshot>? InitializeAction { get; init; }

        internal Func<Task>? OutputReady { get; init; }

        public Task WaitForOutputReadyAsync() => OutputReady?.Invoke() ?? Task.CompletedTask;

        internal Exception? FreeException { get; init; }
        internal Func<int, Exception?>? FreeFailureForCall { get; set; }

        public AudioPlaybackInitializationResult Initialize(PlayerSettingsSnapshot settings)
        {
            events.Add("initialize");
            InitializeAction?.Invoke(settings);
            if (InitializeException != null)
            {
                throw InitializeException;
            }
            return InitializeResult
                ?? throw new AssertFailedException("Playback initialization should not be called by this lifecycle test.");
        }

        public void ClearMaxVoices()
        {
            events.Add("clear-max-voices");
        }

        public void SetVolume(int volume)
        {
            events.Add("volume:" + volume);
        }

        public void Free()
        {
            events.Add("free");
            Exception? injected = FreeFailureForCall?.Invoke(events.Count(value => value == "free"));
            if (injected != null) { throw injected; }
            if (FreeException != null) { throw FreeException; }
        }
    }

    /// <summary>実資源読込みの所要時間を呼出元へ返し、既存の性能検査と再生検査で共用します。</summary>
    internal sealed class MeasuredBmsAutoPlayer(Ribbit.BMS.BMSFile bms, Action<TimeSpan> report) : BMSAutoPlayer(bms)
    {
        public override void LoadResources()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            base.LoadResources();
            report(clock.Elapsed);
        }
    }
}
