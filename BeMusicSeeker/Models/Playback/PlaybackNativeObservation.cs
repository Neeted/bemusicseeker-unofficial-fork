#nullable enable
using System;
using System.Collections.Immutable;
using System.Threading.Tasks;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Models;

/// <summary>実取得が成功したPCMだけを、そのsessionの短い受動観測へ渡します。</summary>
internal delegate void PlaybackPcmCallback(BassAudioSession session, int rate, int channels, ReadOnlySpan<float> samples);

/// <summary>追加pullをせず、実sessionと登録後の曲PCMを観測します。</summary>
internal interface IPlaybackNativeObservation
{
    /// <summary>runtimeが現在所有するsessionです。解放後の同一tokenも解放証拠として使えます。</summary>
    BassAudioSession? Session { get; }
    /// <summary>期待sessionへ一回登録します。登録解除は業務再生を取り消しません。</summary>
    IDisposable ObservePcm(BassAudioSession expectedSession, out Task<PlaybackPcmSnapshot> completion);
}

/// <summary>実processorを通った有限・非zeroの短いPCM窓です。全曲を蓄積しません。</summary>
internal sealed record PlaybackPcmSnapshot(int SampleRate, int Channels, ImmutableArray<float> Samples);
