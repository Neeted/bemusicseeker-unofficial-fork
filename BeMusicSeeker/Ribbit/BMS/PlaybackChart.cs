#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Ribbit.Math;

namespace Ribbit.BMS;

/// <summary>論理発音sliceと共有PCM内の窓です。継続列はrender時に一つのvoiceとなり、null終端は音源EOFを表します。</summary>
internal readonly record struct PlaybackAudioEvent(int ResourceIndex, PlaybackTime Start,
    PlaybackTime SourceStart, PlaybackTime? SourceEnd, PlaybackTime? End, long StableOrder);
/// <summary>表示時計へ接続する制御です。InitialBarLineは元pulse0の初期線で、小節を進めません。</summary>
internal enum PlaybackControlKind { Bpm, Stop, BarLine, InitialBarLine }
/// <summary>表示専用の時刻確定済み制御です。音声時計を再計算しません。</summary>
internal readonly record struct PlaybackControl(PlaybackTime Time, PlaybackControlKind Kind, double Bpm, PlaybackTime Stop);

/// <summary>BMSとbmsonの一回の解析結果を、音声・表示・書出しへ渡す変更不能の境界です。</summary>
public sealed class PlaybackChart
{
    internal PlaybackChart(string path, string hash, string title, string subtitle, string artist, string subartist,
        string genre, Fraction bpm, Fraction minBpm, Fraction maxBpm, double total, string modeHint,
        PlaybackTime duration, string[] resourceNames, PlaybackAudioEvent[] audioEvents,
        PlaybackControl[] controls, PlaybackTime[] countTimes, BMSFile? bms = null)
    {
        Path = path; Hash = hash; Title = title; Subtitle = subtitle; Artist = artist; Subartist = subartist;
        Genre = genre; Bpm = bpm; MinBpm = minBpm; MaxBpm = maxBpm; Total = total; ModeHint = modeHint;
        Duration = duration.ToTimeSpan(); ResourceNames = Array.AsReadOnly(resourceNames);
        AudioEvents = Array.AsReadOnly(audioEvents); Controls = Array.AsReadOnly(controls);
        CountTimes = Array.AsReadOnly(countTimes); Bms = bms;
        LastMeasure = bms?.Measures.LastIndex ?? controls.Count(control => control.Kind == PlaybackControlKind.BarLine);
    }
    /// <summary>入力譜面の実pathです。</summary>
    public string Path { get; }
    /// <summary>入力bytesのMD5です。BMSの既存書出しタグ契約を維持します。</summary>
    public string Hash { get; }
    /// <summary>解析した曲名です。</summary>
    public string Title { get; }
    /// <summary>解析した副題です。</summary>
    public string Subtitle { get; }
    /// <summary>解析した作者です。</summary>
    public string Artist { get; }
    /// <summary>解析した副作者です。</summary>
    public string Subartist { get; }
    /// <summary>解析したジャンルです。</summary>
    public string Genre { get; }
    /// <summary>bmsonの厳密な初期BPMとBMSの有限部分です。BMSの正∞表示にはDisplayBpmを使います。</summary>
    public Fraction Bpm { get; }
    /// <summary>bmsonの最小有効BPMとBMSの有限部分です。BMSの正∞表示にはDisplayMinBpmを使います。</summary>
    public Fraction MinBpm { get; }
    /// <summary>bmsonの最大有効BPMとBMSの有限部分です。BMSの正∞表示にはDisplayMaxBpmを使います。</summary>
    public Fraction MaxBpm { get; }
    /// <summary>BMSの正∞入力も従来どおり表示する初期BPM値です。</summary>
    public double DisplayBpm => Bms?.Bpm?.ToDouble() ?? Bpm.ToDouble();
    /// <summary>BMSの正∞入力も従来どおり表示する最小BPM値です。</summary>
    public double DisplayMinBpm => Bms?.MinBpm?.ToDouble() ?? MinBpm.ToDouble();
    /// <summary>BMSの正∞入力も従来どおり表示する最大BPM値です。</summary>
    public double DisplayMaxBpm => Bms?.MaxBpm?.ToDouble() ?? MaxBpm.ToDouble();
    /// <summary>プレビュー表示用TOTAL値です。</summary>
    public double Total { get; }
    /// <summary>表示用モード指定です。音声の受理条件には使いません。</summary>
    public string ModeHint { get; }
    /// <summary>譜面位置と表示制御の終端です。音源EOFは復号後に解決します。</summary>
    public TimeSpan Duration { get; }
    /// <summary>プレビューで数える判定位置数です。BMSでは既存のLN両端集計を保持します。</summary>
    public int TotalNoteCount => Bms?.TotalNoteCount ?? CountTimes.Count;
    /// <summary>表示する最終小節番号です。bmsonのpulse0初期線は加算しません。</summary>
    public int LastMeasure { get; }
    /// <summary>BMSだけが持つ従来の画像・分岐データです。bmsonのために偽造しません。</summary>
    internal BMSFile? Bms { get; }
    /// <summary>音声探索で使用するindex順の音源名です。</summary>
    internal IReadOnlyList<string> ResourceNames { get; }
    /// <summary>解析済み発音とsource窓です。同一pathはvoice identityを変更しません。</summary>
    internal IReadOnlyList<PlaybackAudioEvent> AudioEvents { get; }
    /// <summary>時刻確定済みの表示制御です。</summary>
    internal IReadOnlyList<PlaybackControl> Controls { get; }
    /// <summary>layerをまとめたプレビュー判定位置の時系列です。</summary>
    internal IReadOnlyList<PlaybackTime> CountTimes { get; }
    /// <summary>実pathの形式を選択し、一回だけ解析します。metadata用parserは使いません。</summary>
    public static PlaybackChart Load(string path) => string.Equals(System.IO.Path.GetExtension(path), ".bmson", StringComparison.OrdinalIgnoreCase)
        ? BmsonPlaybackParser.Load(path) : FromBms(new BMSFile(path));
    /// <summary>既存BMSの時刻と五群の列挙順を変更せず共通境界へ投影します。</summary>
    public static PlaybackChart FromBms(BMSFile bms)
    {
        ArgumentNullException.ThrowIfNull(bms);
        PlaybackAudioEvent[] events = BmsAudioFrameSchedule.EnumerateAudioNotes(bms).Select((note, index) =>
            new PlaybackAudioEvent(note.Index, PlaybackTime.FromTimeSpan(note.AbsoluteTime), default, null, null, index)).ToArray();
        Fraction bpm = bms.Bpm?.FiniteValue ?? Fraction.Zero;
        return new PlaybackChart(bms.Path, bms.Md5, bms.Title, bms.Subtitle ?? string.Empty,
            bms.Artist, bms.Subartist ?? string.Empty, bms.Genre, bpm, bms.MinBpm?.FiniteValue ?? bpm,
            bms.MaxBpm?.FiniteValue ?? bpm, bms.Total ?? 0, string.Empty, PlaybackTime.FromTimeSpan(bms.Duration),
            bms.WavArray.ToArray(), events, [], [], bms);
    }
}
