using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;
using Ribbit.Media;
using Ribbit.Util;

namespace Ribbit.BMS;

public class BMSAutoPlayer(BMSFile bms) : BMSAutoPlayer<BassAudioPlayer>(bms)
{
}
public class BMSAutoPlayer<TBassAudioPlayer>(BMSFile bms) : BMSPlayer<TBassAudioPlayer, NullImageLoader>(bms) where TBassAudioPlayer : BassAudioPlayer
{
    public override float PlaybackRate
    {
        get
        {
            return base.PlaybackRate;
        }
        set
        {
            if ((double)value < 0.05 || value > 50f)
            {
                throw new ArgumentOutOfRangeException("value");
            }
            base.PlaybackRate = value;
            BassAudioPlayer.SetTempoChange(value);
        }
    }

    public override void LoadResources()
    {
        LoadResources(onMemory: true, asParallel: true);
    }

    public void LoadResources(bool onMemory, bool asParallel)
    {
        string pPath = Path.GetDirectoryName(base.Bms.Path);
        TBassAudioPlayer selector(string w)
        {
            if (string.IsNullOrWhiteSpace(w))
            {
                return (TBassAudioPlayer)null;
            }
            foreach (string item in Resources.NormalizeExtension(w))
            {
                string text = Path.Combine(pPath, item);
                if (LongPathFileSystem.FileExists(text))
                {
                    try
                    {
                        return InstanceCreator<TBassAudioPlayer>.Create(text, onMemory);
                    }
                    catch
                    {
                        return (TBassAudioPlayer)null;
                    }
                }
            }
            string fileName = Path.GetFileName(w);
            if (w != fileName)
            {
                foreach (string item2 in Resources.NormalizeExtension(fileName))
                {
                    string text2 = Path.Combine(pPath, item2);
                    if (LongPathFileSystem.FileExists(text2))
                    {
                        try
                        {
                            return InstanceCreator<TBassAudioPlayer>.Create(text2, onMemory);
                        }
                        catch
                        {
                            return (TBassAudioPlayer)null;
                        }
                    }
                }
            }
            return (TBassAudioPlayer)null;
        }
        base.AudioPlayers = (asParallel ? base.Bms.WavArray.AsParallel().Select(selector).ToList()
            .AsReadOnly() : base.Bms.WavArray.Select(selector).ToList().AsReadOnly());
        durationProvider = () => base.MusicDuration;
        base.MusicDuration = base.Bms.Measures.SelectMany(m => new ReadOnlyCollection<Func<IList<BMSFile.Chart.Note>>>[5] { m.GetPropertiesAllBgmNotes, m.GetPropertiesAll1PVisNotes, m.GetPropertiesAll2PVisNotes, m.GetPropertiesAll1PLngNotes, m.GetPropertiesAll2PLngNotes }.SelectMany(ps => ps).SelectMany(p => from n in p()
                                                                                                                                                                                                                                                                                                                      where n != null
                                                                                                                                                                                                                                                                                                                      select n)).Max(n => n.AbsoluteTime + (base.AudioPlayers[n.Index]?.Duration ?? TimeSpan.Zero));
        base.BgaDuration = TimeSpan.Zero;
    }
}
