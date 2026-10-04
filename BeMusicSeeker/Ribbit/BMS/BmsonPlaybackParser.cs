#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ribbit.Logging;
using Ribbit.Math;

namespace Ribbit.BMS;

/// <summary>再生用bmson入力の分類済み失敗です。資源・native・原因不明の失敗はこの型へ隠しません。</summary>
public sealed class InvalidBmsonFileException : Exception
{
    /// <summary>選択言語のメッセージに入力pathを渡し、元の構文・数値・I/O失敗を内部例外として保持します。</summary>
    internal InvalidBmsonFileException(string path, Exception cause)
        : base(string.Format(CultureInfo.CurrentCulture, BeMusicSeeker.Properties.Resources.Error_InvalidBmsonInputFormat, path), cause) => FileName = path;
    /// <summary>失敗した入力の実pathです。</summary>
    public string FileName { get; }
}

/// <summary>chart_infoから独立した、十進字句と全曲pulseに基づくbmson再生parserです。</summary>
internal static class BmsonPlaybackParser
{
    private static readonly Regex NumberSyntax = new(@"^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?$", RegexOptions.CultureInvariant);

    /// <summary>一回のbyte読取りからmetadata、固定刻み時計、共有slice計画を作ります。playable競合は進行統計だけを回復し、正常完了時にWARNを集約します。</summary>
    internal static PlaybackChart Load(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            ReadOnlyMemory<byte> json = bytes;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            {
                json = json[3..];
            }

            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement, path, Convert.ToHexStringLower(MD5.HashData(bytes)));
        }
        catch (Exception cause) when (cause is JsonException or FormatException or InvalidDataException
            or IOException or UnauthorizedAccessException or ArithmeticException)
        {
            throw new InvalidBmsonFileException(path, cause);
        }
    }

    private static PlaybackChart Parse(JsonElement root, string path, string hash)
    {
        RequireObject(root);
        JsonElement info = Get(root, "info");
        RequireObject(info);
        JsonElement version = Get(root, "version");
        if (version.ValueKind == JsonValueKind.Null)
        {
            throw new InvalidDataException("version cannot be null.");
        }

        string versionText = Text(version);
        bool legacy = versionText == "0.21" || (version.ValueKind == JsonValueKind.Undefined
            && (Has(root, "soundChannel") || Has(root, "bpmNotes") || Has(root, "stopNotes") || Has(info, "initBPM")));
        BigInteger resolution = legacy ? 240 : Integer(Get(info, "resolution"), 240, allowNull: true);
        resolution = BigInteger.Abs(resolution);
        if (resolution.IsZero)
        {
            resolution = 240;
        }

        Fraction initialBpm = Positive(Number(Alias(info, "init_bpm", "initBPM")), "initial BPM");
        Fraction minBpm = initialBpm, maxBpm = initialBpm;
        var boundaries = new SortedDictionary<BigInteger, Boundary>();
        Boundary At(BigInteger pulse)
        {
            if (!boundaries.TryGetValue(pulse, out Boundary? value))
            {
                boundaries.Add(pulse, value = new Boundary());
            }

            return value;
        }
        foreach (JsonElement item in Array(Alias(root, "bpm_events", "bpmNotes")))
        {
            BigInteger y = Position(Get(item, "y"));
            Fraction bpm = Number(Alias(item, "bpm", "v"));
            // 実譜面にある有限の0/負BPM変更は通常プレイヤー同様に無効として飛ばします。
            // 初期BPMの必須・正値契約とは分け、旧有効BPMとmin/maxを維持します。
            if (bpm <= Fraction.Zero)
            {
                continue;
            }

            At(y).Bpm = bpm;
            if (bpm < minBpm)
            {
                minBpm = bpm;
            }

            if (bpm > maxBpm)
            {
                maxBpm = bpm;
            }
        }
        foreach (JsonElement item in Array(Alias(root, "stop_events", "stopNotes")))
        {
            BigInteger y = Position(Get(item, "y"));
            Fraction duration = Number(Alias(item, "duration", "v"));
            if (duration < Fraction.Zero)
            {
                throw new InvalidDataException("STOP duration must be nonnegative.");
            }

            At(y).Stop += duration;
        }
        At(BigInteger.Zero);
        var anchors = new List<Anchor>(boundaries.Count);
        var controls = new List<PlaybackControl>();
        PlaybackTime clock = default;
        BigInteger previousPulse = 0;
        Fraction effectiveBpm = initialBpm;
        Fraction Ticks(Fraction pulses, Fraction bpm) => pulses * 600000000 / (new Fraction(resolution, BigInteger.One) * bpm);
        foreach ((BigInteger pulse, Boundary boundary) in boundaries)
        {
            clock += PlaybackTime.FromTicks(Ticks(new Fraction(pulse - previousPulse, BigInteger.One), effectiveBpm));
            _ = clock.ToTimeSpan();
            PlaybackTime arrival = clock;
            if (boundary.Bpm is Fraction bpm)
            {
                effectiveBpm = bpm;
                controls.Add(new PlaybackControl(arrival, PlaybackControlKind.Bpm, bpm.ToDouble(), default));
            }
            var stop = PlaybackTime.FromTicks(Ticks(boundary.Stop, effectiveBpm));
            if (stop.Subticks.Sign > 0)
            {
                controls.Add(new PlaybackControl(arrival, PlaybackControlKind.Stop, 0, stop));
            }

            clock += stop;
            _ = clock.ToTimeSpan();
            anchors.Add(new Anchor(pulse, arrival, clock, effectiveBpm));
            previousPulse = pulse;
        }
        PlaybackTime TimeAt(BigInteger pulse)
        {
            int low = 0, high = anchors.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (anchors[middle].Pulse <= pulse)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }
            Anchor anchor = anchors[low - 1];
            PlaybackTime time = pulse == anchor.Pulse ? anchor.Arrival
                : anchor.Departure + PlaybackTime.FromTicks(Ticks(new Fraction(pulse - anchor.Pulse, BigInteger.One), anchor.Bpm));
            _ = time.ToTimeSpan();
            return time;
        }
        var resources = new List<string>();
        var audio = new List<PlaybackAudioEvent>();
        var playableByLane = new Dictionary<BigInteger, SortedSet<ProgressNote>>();
        var headOrder = Comparer<ProgressNote>.Create(static (left, right) => left.Head.CompareTo(right.Head));
        int ignoredPlayableNotes = 0;
        void RegisterPlayable(BigInteger lane, ProgressNote note)
        {
            if (!playableByLane.TryGetValue(lane, out SortedSet<ProgressNote>? registered))
            {
                playableByLane.Add(lane, registered = new SortedSet<ProgressNote>(headOrder));
            }
            ProgressNote? previous = registered.GetViewBetween(new ProgressNote(0, 0), note).Max;
            if (previous != null && previous.Head == note.Head)
            {
                // 同長の正常layerは一つに数え、競合では先に採用したnormal/LNを維持します。
                if (previous.Length != note.Length)
                {
                    ignoredPlayableNotes++;
                }

                return;
            }
            // 採用LNは重ならないため、直前headの末尾と次headの範囲検索だけで判定できます。
            if ((previous != null && note.Head <= previous.Head + previous.Length)
                || (note.Length > 0 && registered.GetViewBetween(note, new ProgressNote(note.Head + note.Length, 0)).Min != null))
            {
                ignoredPlayableNotes++;
                return;
            }
            registered.Add(note);
        }
        BigInteger lastPulse = boundaries.Keys.Last();
        long stableOrder = 0;
        foreach (JsonElement channel in Array(Alias(root, "sound_channels", "soundChannel")))
        {
            RequireObject(channel);
            int resourceIndex = resources.Count;
            resources.Add(Text(Get(channel, "name")));
            var groups = new SortedDictionary<BigInteger, bool>();
            var playable = new List<(BigInteger Lane, ProgressNote Note)>();
            foreach (JsonElement note in Array(Get(channel, "notes")))
            {
                RequireObject(note);
                BigInteger y = Position(Get(note, "y"));
                BigInteger length = Position(Get(note, "l"), 0);
                BigInteger x = Integer(Get(note, "x"), 0, allowNull: true);
                bool continuation = Boolean(Get(note, "c"));
                groups[y] = groups.TryGetValue(y, out bool existing) ? existing && continuation : continuation;
                lastPulse = BigInteger.Max(lastPulse, y + length);
                if (x > 0 && !Boolean(Get(note, "up")))
                {
                    playable.Add((x, new ProgressNote(y, length)));
                }
            }
            // channel順を保ち、channel内だけyでstableに登録します。音声groupの採否には使いません。
            foreach ((BigInteger lane, ProgressNote note) in playable.OrderBy(item => item.Note.Head))
            {
                RegisterPlayable(lane, note);
            }

            KeyValuePair<BigInteger, bool>[] values = groups.ToArray();
            PlaybackTime restartTime = default;
            for (int i = 0; i < values.Length; i++)
            {
                PlaybackTime time = TimeAt(values[i].Key);
                if (i == 0 || !values[i].Value)
                {
                    restartTime = time;
                }

                PlaybackTime? end = i + 1 < values.Length && values[i + 1].Value ? TimeAt(values[i + 1].Key) : null;
                audio.Add(new PlaybackAudioEvent(resourceIndex, time, time - restartTime,
                    end is PlaybackTime finite ? finite - restartTime : null, end, stableOrder++));
            }
        }
        var countPositions = new List<BigInteger>();
        foreach (ProgressNote note in playableByLane.Values.SelectMany(notes => notes))
        {
            countPositions.Add(note.Head);
            if (note.Length > 0)
            {
                countPositions.Add(note.Head + note.Length);
            }
        }
        // release layerは既存のLN終端へ対応する表示情報であり、追加判定位置を生成しません。
        JsonElement lines = Get(root, "lines");
        if (lines.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            BigInteger step = resolution * 4;
            int count = checked((int)(lastPulse / step));
            for (int i = 1; i <= count; i++)
            {
                controls.Add(new PlaybackControl(TimeAt(step * i), PlaybackControlKind.BarLine, 0, default));
            }
        }
        else
        {
            foreach (JsonElement line in Array(lines))
            {
                BigInteger y = Position(Get(line, "y"));
                lastPulse = BigInteger.Max(lastPulse, y);
                controls.Add(new PlaybackControl(TimeAt(y), y.IsZero ? PlaybackControlKind.InitialBarLine : PlaybackControlKind.BarLine, 0, default));
            }
        }
        PlaybackTime durationTime = TimeAt(lastPulse);
        if (clock.CompareTo(durationTime) > 0)
        {
            durationTime = clock;
        }

        var chart = new PlaybackChart(path, hash, Text(Get(info, "title")), Text(Get(info, "subtitle")),
            Text(Get(info, "artist")), string.Join(" ", Array(Get(info, "subartists")).Select(Text)), Text(Get(info, "genre")),
            initialBpm, minBpm, maxBpm, OptionalNumber(Get(info, "total"), 100).ToDouble(),
            Get(info, "mode_hint").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? "beat-7k" : Text(Get(info, "mode_hint")),
            durationTime, resources.ToArray(), audio.OrderBy(item => item.Start).ThenBy(item => item.StableOrder).ToArray(),
            controls.OrderBy(item => item.Time).ToArray(), countPositions.Select(TimeAt).Order().ToArray());
        if (ignoredPlayableNotes > 0)
        {
            NLogWrapper.GetLogger(nameof(BmsonPlaybackParser)).Warn("bmson進行統計の競合ノートを不採用にしました: {IgnoredPlayableNotes}", ignoredPlayableNotes);
        }
        return chart;
    }
    private static JsonElement Get(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement value) ? value : default;
    private static bool Has(JsonElement obj, string name) => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out _);
    private static JsonElement Alias(JsonElement obj, string modern, string legacy) => Has(obj, modern) ? Get(obj, modern) : Get(obj, legacy);
    private static void RequireObject(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Expected a JSON object.");
        }
    }
    private static string Text(JsonElement value) => value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? string.Empty
        : value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    private static IEnumerable<JsonElement> Array(JsonElement value)
    {
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("Expected an array.");
        }

        return value.EnumerateArray();
    }
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True;
    private static Fraction Positive(Fraction value, string name) => value > Fraction.Zero ? value : throw new InvalidDataException(name + " must be positive.");
    private static Fraction OptionalNumber(JsonElement value, long fallback) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? fallback : Number(value);
    private static BigInteger Position(JsonElement value, long? fallback = null)
    {
        BigInteger position = Integer(value, fallback, allowNull: false);
        return position.Sign >= 0 ? position : throw new InvalidDataException("Pulse must be nonnegative.");
    }
    private static BigInteger Integer(JsonElement value, long? fallback = null, bool allowNull = false)
    {
        if (fallback is long number && (value.ValueKind == JsonValueKind.Undefined || (allowNull && value.ValueKind == JsonValueKind.Null)))
        {
            return number;
        }

        Fraction result = Number(value);
        if (!result.Denominator.IsOne)
        {
            throw new InvalidDataException("Expected an integer.");
        }

        return result.Numerator;
    }
    private static Fraction Number(JsonElement value)
    {
        string token = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty
            : value.ValueKind == JsonValueKind.Number ? value.GetRawText() : throw new InvalidDataException("Expected a finite number.");
        if (!NumberSyntax.IsMatch(token))
        {
            throw new FormatException("Invalid finite JSON number.");
        }

        int exponentIndex = token.IndexOfAny(['e', 'E']);
        string coefficient = exponentIndex < 0 ? token : token[..exponentIndex];
        int digits = coefficient.Length - (coefficient[0] == '-' ? 1 : 0) - (coefficient.Contains('.') ? 1 : 0);
        if (digits > 4096)
        {
            throw new InvalidDataException("The decimal coefficient exceeds 4096 digits.");
        }

        int exponent = 0;
        if (exponentIndex >= 0)
        {
            ReadOnlySpan<char> explicitExponent = token.AsSpan(exponentIndex + 1);
            bool negative = explicitExponent[0] == '-';
            if (explicitExponent[0] is '+' or '-')
            {
                explicitExponent = explicitExponent[1..];
            }
            // 指数の先頭0は字句長ではなく値として扱い、巨大整数・べき乗を作る前に検査します。
            foreach (char digit in explicitExponent)
            {
                exponent = exponent * 10 + digit - '0';
                if (exponent > 4096)
                {
                    throw new InvalidDataException("The explicit decimal exponent exceeds the range [-4096, 4096].");
                }
            }
            if (negative)
            {
                exponent = -exponent;
            }
        }
        int dot = coefficient.IndexOf('.');
        int scale = dot < 0 ? 0 : coefficient.Length - dot - 1;
        var numerator = BigInteger.Parse(coefficient.Replace(".", string.Empty), CultureInfo.InvariantCulture);
        if (numerator.IsZero)
        {
            return Fraction.Zero;
        }

        int power = checked(exponent - scale);
        return power >= 0 ? new Fraction(numerator * BigInteger.Pow(10, power), BigInteger.One)
            : new Fraction(numerator, BigInteger.Pow(10, checked(-power)));
    }
    private sealed class Boundary { internal Fraction? Bpm; internal Fraction Stop; }
    /// <summary>解析中だけ保持する採用済みの進行ノートです。head順でLNの範囲衝突を調べ、音声には適用しません。</summary>
    private sealed record ProgressNote(BigInteger Head, BigInteger Length);
    private readonly record struct Anchor(BigInteger Pulse, PlaybackTime Arrival, PlaybackTime Departure, Fraction Bpm);
}
