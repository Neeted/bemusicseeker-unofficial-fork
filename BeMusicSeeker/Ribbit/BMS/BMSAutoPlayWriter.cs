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
        string artist = ((base.Bms.Artist.Trim() ?? string.Empty) + " " + (base.Bms.Subartist?.Trim() ?? string.Empty)).Trim();
        string title = ((base.Bms.Title.Trim() ?? string.Empty) + " " + (base.Bms.Subtitle?.Trim() ?? string.Empty)).Trim();
        if (string.IsNullOrWhiteSpace(filePathWithoutExtension))
        {
            filePathWithoutExtension = AppContext.BaseDirectory;
        }
        if (LongPathFileSystem.DirectoryExists(filePathWithoutExtension))
        {
            string input = "[" + artist + "] " + title;
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
        long totalFrames = GetRenderFrameCount(renderer.SampleRate);
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
        AudioTagInfo tagInfo = new(
            artist,
            title,
            genre: base.Bms.Genre.Trim() ?? string.Empty,
            durationSeconds: (double)totalFrames / renderer.SampleRate,
            bpm: base.Bms.Bpm?.ToDecimal().ToString() ?? string.Empty,
            fileName: base.Bms.Path,
            comment: base.Bms.Md5 + ((base.Bms.RandomPattern.Count > 0)
                ? (" \n" + string.Join(", ", [.. base.Bms.RandomPattern.Select(i => i.ToString())]))
                : string.Empty));

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

        if (renderedFrames < totalFrames)
        {
            int finalIntervalFrames = checked((int)(totalFrames - renderedFrames));
            renderer.ReadFramesExactly(
                renderedPcm,
                checked((int)renderedFrames),
                finalIntervalFrames);
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
        return [.. GetRenderableAudioNotes()
            .Select(note => note.AbsoluteTime)
            .Where(time => time <= base.Duration)
            .OrderBy(time => time)
            .Select(time => time < TimeSpan.Zero ? TimeSpan.Zero : time)
            .SequentialDistinct()];
    }

    private long GetRenderFrameCount(int outputSampleRate)
    {
        long totalFrames = AudioPcmRenderer.TimeToFrame(base.Duration, outputSampleRate);
        foreach (BMSFile.Chart.Note note in GetRenderableAudioNotes())
        {
            if (note.AbsoluteTime > base.Duration)
            {
                continue;
            }

            BassAudioWriter source = base.AudioPlayers[note.Index];
            if (source == null)
            {
                continue;
            }

            TimeSpan eventTime = note.AbsoluteTime < TimeSpan.Zero ? TimeSpan.Zero : note.AbsoluteTime;
            long eventStartFrame = AudioPcmRenderer.TimeToFrame(eventTime, outputSampleRate);
            long eventEndFrame = checked(eventStartFrame + source.GetOutputFrameCount(outputSampleRate));
            totalFrames = System.Math.Max(totalFrames, eventEndFrame);
        }

        return totalFrames;
    }

    private IEnumerable<BMSFile.Chart.Note> GetRenderableAudioNotes()
    {
        foreach (BMSFile.Chart.Note note in BgmNotesQueue)
        {
            yield return note;
        }
        foreach (BMSFile.Chart.Note note in VisibleNotes1PQueue.SelectMany(queue => queue))
        {
            yield return note;
        }
        foreach (BMSFile.Chart.Note note in VisibleNotes2PQueue.SelectMany(queue => queue))
        {
            yield return note;
        }
        foreach (BMSFile.Chart.Note note in LongNotes1PQueue.SelectMany(queue => queue))
        {
            if (((uint)note.Type & 0xFFFFFFF0u) == 80)
            {
                yield return note;
            }
        }
        foreach (BMSFile.Chart.Note note in LongNotes2PQueue.SelectMany(queue => queue))
        {
            if (((uint)note.Type & 0xFFFFFFF0u) == 96)
            {
                yield return note;
            }
        }
    }

    private static double GetNormalizationGain(
        Normalization normalization,
        AudioPcmLevels levels,
        double amplifier)
    {
        double gain = normalization switch
        {
            Normalization.NONE => 0.16d * amplifier,
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
