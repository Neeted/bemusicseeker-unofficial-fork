using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using ManagedBass;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util.Extensions;

namespace Ribbit.BMS;

public class BMSAutoPlayWriter(BMSFile bms) : BMSAutoPlayer<BassAudioWriter>(bms)
{
    public enum Normalization
    {
        NONE,
        PEAK_LEVEL,
        RMS_VALUE
    }

    public override void Pause()
    {
        throw new NotImplementedException();
    }

    public override Task Start()
    {
        throw new NotImplementedException();
    }

    public override void Stop()
    {
        throw new NotImplementedException();
    }

    /// <summary>曲を一度だけfloat PCMへrenderし、同じPCMを測定・正規化してencoderへ供給します。</summary>
    /// <param name="encodetype">使用するencoder形式です。</param>
    /// <param name="quality">encoder固有の品質値です。</param>
    /// <param name="filePathWithoutExtension">拡張子を除いた出力pathまたは出力directoryです。</param>
    /// <param name="normalize">PCM全体へ適用する正規化方式です。</param>
    /// <param name="normalizationAmplifier">正規化後に適用する追加amplifierです。</param>
    public void Write(
        EncoderType encodetype,
        float quality,
        string filePathWithoutExtension,
        Normalization normalize = Normalization.NONE,
        float normalizationAmplifier = 1f)
    {
        if (normalize is not Normalization.NONE
            and not Normalization.PEAK_LEVEL
            and not Normalization.RMS_VALUE)
        {
            throw new ArgumentOutOfRangeException(nameof(normalize));
        }
        if (!float.IsFinite(normalizationAmplifier) || normalizationAmplifier < 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(normalizationAmplifier),
                "Normalization amplifier must be finite and non-negative.");
        }

        ResetPlaybackState();
        AudioTagInfo tagInfo = new(
            artist: ((base.Bms.Artist.Trim() ?? string.Empty) + " " + (base.Bms.Subartist?.Trim() ?? string.Empty)).Trim(),
            title: ((base.Bms.Title.Trim() ?? string.Empty) + " " + (base.Bms.Subtitle?.Trim() ?? string.Empty)).Trim(),
            genre: base.Bms.Genre.Trim() ?? string.Empty,
            durationSeconds: base.Duration.TotalSeconds,
            bpm: base.Bms.Bpm?.ToDecimal().ToString() ?? string.Empty,
            fileName: base.Bms.Path,
            comment: base.Bms.Md5 + ((base.Bms.RandomPattern.Count > 0)
                ? (" \n" + string.Join(", ", [.. base.Bms.RandomPattern.Select(i => i.ToString())]))
                : string.Empty));
        if (string.IsNullOrWhiteSpace(filePathWithoutExtension))
        {
            filePathWithoutExtension = AppContext.BaseDirectory;
        }
        if (LongPathFileSystem.DirectoryExists(filePathWithoutExtension))
        {
            string input = "[" + tagInfo.Artist + "] " + tagInfo.Title;
            filePathWithoutExtension = Path.Combine(
                filePathWithoutExtension,
                input.NaturalNormalizationForFileName()
                    .ReplaceInvalidFileNameCharsByWide()
                    .RemoveInvalidFileNameChars());
        }

        if (base.Duration < TimeSpan.Zero)
        {
            throw new InvalidOperationException("The BMS render duration must not be negative.");
        }

        AudioPcmRenderer renderer = BassAudioWriter.CreatePcmRenderer();
        long totalFrames = AudioPcmRenderer.TimeToFrame(base.Duration, renderer.SampleRate);
        if (totalFrames == 0)
        {
            // BASSencはPCMを供給しないとWAVヘッダーも出力しません。
            throw new InvalidOperationException("The chart contains no audio frames to encode.");
        }
        if (totalFrames < 0)
        {
            throw new InvalidOperationException("The BMS render duration resolved to a negative frame count.");
        }
        int totalFrameCount = checked((int)totalFrames);
        int totalSampleCount = checked(totalFrameCount * renderer.ChannelCount);
        float[] renderedPcm = new float[totalSampleCount];

        long renderedFrames = 0;
        foreach (TimeSpan eventTime in GetRenderTimes())
        {
            long eventFrame = AudioPcmRenderer.TimeToFrame(eventTime, renderer.SampleRate);
            if (eventFrame < renderedFrames || eventFrame > totalFrames)
            {
                throw new InvalidOperationException("BMS event frames must stay within the ordered render interval.");
            }

            int intervalFrames = checked((int)(eventFrame - renderedFrames));
            if (intervalFrames > 0)
            {
                renderer.ReadFramesExactly(
                    renderedPcm,
                    checked((int)renderedFrames),
                    intervalFrames);
            }

            ForwardTo(eventTime);
            renderedFrames = eventFrame;
        }

        if (renderedFrames != totalFrames)
        {
            throw new InvalidOperationException("The BMS event timeline did not reach the declared duration.");
        }

        AudioPcmLevels levels = AudioPcmRenderer.Measure(renderedPcm, renderer.ChannelCount);
        double gain = GetNormalizationGain(normalize, levels, normalizationAmplifier);
        double finalPeak = AudioOutputProcessor.ApplyConstantGain(renderedPcm, gain);

        switch (encodetype)
        {
            case EncoderType.WAVE:
                BassAudioWriter.CreateEncoderWAV(filePathWithoutExtension);
                break;
            case EncoderType.MP3_LAME:
                BassAudioWriter.CreateEncoderLAME(filePathWithoutExtension, quality);
                break;
            case EncoderType.AAC_NERO:
                BassAudioWriter.CreateEncoderNeroAAC(filePathWithoutExtension, quality);
                break;
            case EncoderType.OPUS:
                BassAudioWriter.CreateEncoderOPUS(filePathWithoutExtension, quality);
                break;
            case EncoderType.FLAC:
                BassAudioWriter.CreateEncoderFLAC(filePathWithoutExtension, quality);
                break;
            case EncoderType.OGG_VORBIS:
                BassAudioWriter.CreateEncoderOGG(filePathWithoutExtension, quality);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(encodetype), encodetype, "Unknown encoder type.");
        }

        BassAudioWriter.ValidateOutputPeakForEncoder(finalPeak);
        BassAudioWriter.SetTagInfo(tagInfo);
        NLogWrapper.DebuggerLogger?.Trace(BassAudioWriter.EncoderCommandLine);
        BassAudioWriter.StartRecording();
        BassAudioWriter.WritePcm(renderedPcm);
        BassAudioWriter.StopRecording();
        ResetPlaybackState();
    }

    private TimeSpan[] GetRenderTimes()
    {
        TimeSpan[] events = [.. (from time in new IEnumerable<TimeSpan>[5]
        {
            BgmNotesQueue.Select(note => note.AbsoluteTime),
            from note in VisibleNotes1PQueue.SelectMany(queue => queue)
                select note.AbsoluteTime,
            from note in VisibleNotes2PQueue.SelectMany(queue => queue)
                select note.AbsoluteTime,
            from note in LongNotes1PQueue.SelectMany(queue => queue)
                where ((uint)note.Type & 0xFFFFFFF0u) == 80
                select note.AbsoluteTime,
            from note in LongNotes2PQueue.SelectMany(queue => queue)
                where ((uint)note.Type & 0xFFFFFFF0u) == 96
                select note.AbsoluteTime
        }.SelectMany(times => times)
            where time <= base.Duration
            orderby time
            select time < TimeSpan.Zero ? TimeSpan.Zero : time).SequentialDistinct()];

        if (events.Length == 0 || events[^1] != base.Duration)
        {
            Array.Resize(ref events, events.Length + 1);
            events[^1] = base.Duration;
        }

        return events;
    }

    private static double GetNormalizationGain(
        Normalization normalization,
        AudioPcmLevels levels,
        double amplifier)
    {
        double gain = normalization switch
        {
            Normalization.NONE => 0.4d * amplifier,
            Normalization.PEAK_LEVEL => levels.Peak == 0d
                ? amplifier
                : (0.99d / levels.Peak) * amplifier,
            Normalization.RMS_VALUE => levels.Rms == 0d
                ? amplifier
                : (0.4d / levels.Rms) * amplifier,
            _ => throw new ArgumentOutOfRangeException(nameof(normalization))
        };
        if (!double.IsFinite(gain))
        {
            throw new InvalidOperationException("The normalization gain is not finite.");
        }

        return gain;
    }
}
