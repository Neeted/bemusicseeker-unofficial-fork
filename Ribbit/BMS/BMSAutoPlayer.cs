using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Ribbit.Media;
using Ribbit.Util;

namespace Ribbit.BMS;

public class BMSAutoPlayer : BMSAutoPlayer<BassAudioPlayer>
{
    public BMSAutoPlayer(BMSFile bms)
        : base(bms)
    {
    }
}
public class BMSAutoPlayer<TBassAudioPlayer> : BMSPlayer<TBassAudioPlayer, NullImageLoader> where TBassAudioPlayer : BassAudioPlayer
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

    public BMSAutoPlayer(BMSFile bms)
        : base(bms)
    {
    }

    public override void LoadResources()
    {
        LoadResources(onMemory: true, asParallel: true);
    }

    public void LoadResources(bool onMemory, bool asParallel)
    {
        string pPath = Path.GetDirectoryName(base.Bms.Path);
        Func<string, TBassAudioPlayer> selector = delegate (string w)
        {
            if (string.IsNullOrWhiteSpace(w))
            {
                return (TBassAudioPlayer)null;
            }
            foreach (string item in Resources.NormalizeExtension(w))
            {
                string text = Path.Combine(pPath, item);
                if (File.Exists(text))
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
                    if (File.Exists(text2))
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
        };
        base.AudioPlayers = (asParallel ? base.Bms.WavArray.AsParallel().Select(selector).ToList()
            .AsReadOnly() : base.Bms.WavArray.Select(selector).ToList().AsReadOnly());
        durationProvider = () => base.MusicDuration;
        base.MusicDuration = base.Bms.Measures.SelectMany((BMSFile.Chart m) => new ReadOnlyCollection<Func<IList<BMSFile.Chart.Note>>>[5] { m.GetPropertiesAllBgmNotes, m.GetPropertiesAll1PVisNotes, m.GetPropertiesAll2PVisNotes, m.GetPropertiesAll1PLngNotes, m.GetPropertiesAll2PLngNotes }.SelectMany((ReadOnlyCollection<Func<IList<BMSFile.Chart.Note>>> ps) => ps).SelectMany((Func<IList<BMSFile.Chart.Note>> p) => from n in p()
                                                                                                                                                                                                                                                                                                                                                                                                                              where n != null
                                                                                                                                                                                                                                                                                                                                                                                                                              select n)).Max((BMSFile.Chart.Note n) => n.AbsoluteTime + (base.AudioPlayers[n.Index]?.Duration ?? TimeSpan.Zero));
        base.BgaDuration = TimeSpan.Zero;
    }
}
