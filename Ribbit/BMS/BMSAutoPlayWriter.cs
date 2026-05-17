using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util.Extensions;
using Un4seen.Bass.AddOn.Tags;

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

    public void Write(EncoderType encodetype, float quality, string filePathWithoutExtension, Normalization normalize = Normalization.NONE, float normalizationAmplifier = 1f)
    {
        if (currentTime != TimeSpan.Zero)
        {
            Stop();
        }
        normalizationAmplifier = System.Math.Max(0f, normalizationAmplifier);
        var tAG_INFO = new TAG_INFO
        {
            artist = ((base.Bms.Artist.Trim() ?? string.Empty) + " " + (base.Bms.Subartist?.Trim() ?? string.Empty)).Trim(),
            title = ((base.Bms.Title.Trim() ?? string.Empty) + " " + (base.Bms.Subtitle?.Trim() ?? string.Empty)).Trim(),
            genre = (base.Bms.Genre.Trim() ?? string.Empty),
            duration = base.Duration.TotalSeconds,
            bpm = (base.Bms.Bpm?.ToDecimal().ToString() ?? string.Empty),
            filename = base.Bms.Path,
            comment = base.Bms.Md5 + ((base.Bms.RandomPattern.Count > 0) ? (" \n" + string.Join(", ", [.. base.Bms.RandomPattern.Select(i => i.ToString())])) : string.Empty)
        };
        if (string.IsNullOrWhiteSpace(filePathWithoutExtension))
        {
            filePathWithoutExtension = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }
        if (Directory.Exists(filePathWithoutExtension))
        {
            string input = "[" + tAG_INFO.artist + "] " + tAG_INFO.title;
            filePathWithoutExtension = Path.Combine(filePathWithoutExtension, input.NaturalNormalizationForFileName().ReplaceInvalidFileNameCharsByWide().RemoveInvalidFileNameChars());
        }
        TimeSpan[] first = [.. (from t in new IEnumerable<TimeSpan>[5]
            {
                BgmNotesQueue.Select(n => n.AbsoluteTime),
                from n in VisibleNotes1PQueue.SelectMany(c => c)
                    select n.AbsoluteTime,
                from n in VisibleNotes2PQueue.SelectMany(c => c)
                    select n.AbsoluteTime,
                from n in LongNotes1PQueue.SelectMany(c => c)
                    where ((uint)n.Type & 0xFFFFFFF0u) == 80
                    select n.AbsoluteTime,
                from n in LongNotes2PQueue.SelectMany(c => c)
                    where ((uint)n.Type & 0xFFFFFFF0u) == 96
                    select n.AbsoluteTime
            }.SelectMany(c => c)
                            orderby t
                            select t).SequentialDistinct()];
        float deviceVolume = BassAudioPlayer.DeviceVolume;
        if (normalize != Normalization.NONE)
        {
            float num = 0f;
            foreach (TimeSpan item in first.Concat([base.Duration]))
            {
                float level = BassAudioWriter.GetLevel(item - currentTime, normalize == Normalization.RMS_VALUE);
                num = System.Math.Max(num, level);
                ForwardTo(item);
            }
            ResetPlaybackState();
            if (num != 0f)
            {
                BassAudioPlayer.DeviceVolume /= num / ((normalize == Normalization.RMS_VALUE) ? 0.4f : 0.99f);
            }
            float deviceVolume2 = BassAudioPlayer.DeviceVolume;
            NLogWrapper.DebuggerLogger?.Trace("PEAK LEVEL: " + num + " CURRENT: " + deviceVolume + " CHANGE TO: " + deviceVolume2);
        }
        BassAudioPlayer.DeviceVolume *= normalizationAmplifier;
        switch (encodetype)
        {
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
                BassAudioWriter.CreateEncoderWAV(filePathWithoutExtension);
                break;
        }
        BassAudioWriter.SetTagInfo(new TAG_INFO
        {
            artist = ((base.Bms.Artist.Trim() ?? string.Empty) + " " + (base.Bms.Subartist?.Trim() ?? string.Empty)).Trim(),
            title = ((base.Bms.Title.Trim() ?? string.Empty) + " " + (base.Bms.Subtitle?.Trim() ?? string.Empty)).Trim(),
            genre = (base.Bms.Genre.Trim() ?? string.Empty),
            duration = base.Duration.TotalSeconds,
            bpm = base.Bms.Bpm?.ToDecimal().ToString(),
            filename = base.Bms.Path,
            comment = base.Bms.Md5 + ((base.Bms.RandomPattern.Count > 0) ? (" \n" + string.Join(", ", [.. base.Bms.RandomPattern.Select(i => i.ToString())])) : string.Empty)
        });
        NLogWrapper.DebuggerLogger?.Trace(BassAudioWriter.EncoderCommandLine);
        BassAudioWriter.StartRecording();
        foreach (TimeSpan item2 in first.Concat([base.Duration]))
        {
            BassAudioWriter.RecordToFile(item2 - currentTime);
            ForwardTo(item2);
        }
        BassAudioWriter.StopRecording();
        BassAudioPlayer.DeviceVolume = deviceVolume;
    }
}
