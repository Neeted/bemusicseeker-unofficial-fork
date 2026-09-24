#nullable enable annotations
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util;

namespace Ribbit.BMS;

public class BMSAutoPlayer(BMSFile bms) : BMSAutoPlayer<BassAudioPlayer>(bms)
{
}

public class BMSAutoPlayer<TBassAudioPlayer>(BMSFile bms)
    : BMSPlayer<TBassAudioPlayer, NullImageLoader>(bms)
    where TBassAudioPlayer : BassAudioPlayer
{
    private static readonly ConstructorInfo CacheAwarePlayerConstructor = FindCacheAwareConstructor();

    /// <summary>再生速度を取得・設定し、再生ループの観測時に出力故障を伝えます。</summary>
    public override float PlaybackRate
    {
        get
        {
            BassAudioPlayer.CheckOutputHealth();
            return base.PlaybackRate;
        }
        set
        {
            if ((double)value < 0.05 || value > 50f)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            base.PlaybackRate = value;
            BassAudioPlayer.SetTempoChange(value);
        }
    }

    /// <summary>再生に使う音源を読み込み、この曲ロード内で復号済み入力を共有します。</summary>
    public override void LoadResources() => LoadResources(asParallel: true);

    /// <summary>再生に使う音源を、指定に応じて並列に読み込みます。</summary>
    public void LoadResources(bool asParallel)
    {
        string basePath = Path.GetDirectoryName(base.Bms.Path) ?? string.Empty;
        int[] requiredIndices = GetRequiredAudioIndices(base.Bms).OrderBy(index => index).ToArray();
        var audioPlayers = new TBassAudioPlayer[base.Bms.WavArray.Length];
        var failures = new ConcurrentQueue<FailedAudioResource>();
        var sourceCache = new AudioSourceCache();

        void LoadOne(int index)
        {
            string resourceName = base.Bms.WavArray[index];
            string? path = null;
            try
            {
                path = FindAudioPath(basePath, resourceName);
                if (path == null)
                {
                    return;
                }

                audioPlayers[index] = CreatePlayer(path, sourceCache);
            }
            catch (Exception exception)
            {
                failures.Enqueue(new FailedAudioResource(index, resourceName, path ?? resourceName, exception));
            }
        }

        if (asParallel)
        {
            requiredIndices.AsParallel().ForAll(LoadOne);
        }
        else
        {
            foreach (int index in requiredIndices)
            {
                LoadOne(index);
            }
        }

        if (!failures.IsEmpty)
        {
            FailedAudioResource failure = failures.OrderBy(item => item.Index).First();
            DisposeLoadedPlayers(audioPlayers, failure);
            string stage = GetFailureStage(failure.Exception);
            string nativeError = GetNativeError(failure.Exception);
            throw new InvalidDataException(
                string.Format(BeMusicSeeker.Properties.Resources.AudioRequiredResourceLoadFailureFormat,
                    failure.ResourceName, failure.Path, stage, nativeError),
                failure.Exception);
        }

        base.AudioPlayers = Array.AsReadOnly(audioPlayers);
        durationProvider = () => base.MusicDuration;
        base.MusicDuration = base.Bms.Measures.SelectMany(measure =>
            new ReadOnlyCollection<Func<IList<BMSFile.Chart.Note>>>[5]
            {
                measure.GetPropertiesAllBgmNotes,
                measure.GetPropertiesAll1PVisNotes,
                measure.GetPropertiesAll2PVisNotes,
                measure.GetPropertiesAll1PLngNotes,
                measure.GetPropertiesAll2PLngNotes
            }
            .SelectMany(properties => properties)
            .SelectMany(getter => getter())
            .Select(note => note.AbsoluteTime + (base.AudioPlayers[note.Index]?.Duration ?? TimeSpan.Zero)))
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();
        base.BgaDuration = TimeSpan.Zero;
    }

    private static ConstructorInfo FindCacheAwareConstructor()
    {
        return typeof(TBassAudioPlayer).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(AudioSourceCache)],
                modifiers: null)
            ?? throw new InvalidOperationException(
                $"{typeof(TBassAudioPlayer).Name} must provide the song-scoped audio-source constructor.");
    }

    private static TBassAudioPlayer CreatePlayer(string path, AudioSourceCache sourceCache)
    {
        try
        {
            return (TBassAudioPlayer)CacheAwarePlayerConstructor.Invoke([path, sourceCache]);
        }
        catch (TargetInvocationException exception) when (exception.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
            throw;
        }
    }

    private static string? FindAudioPath(string basePath, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(resourceName))
        {
            return null;
        }

        foreach (string item in Resources.NormalizeExtension(resourceName))
        {
            string path = Path.Combine(basePath, item);
            if (LongPathFileSystem.FileExists(path))
            {
                return path;
            }
        }

        string fileName = Path.GetFileName(resourceName);
        if (!string.Equals(resourceName, fileName, StringComparison.Ordinal))
        {
            foreach (string item in Resources.NormalizeExtension(fileName))
            {
                string path = Path.Combine(basePath, item);
                if (LongPathFileSystem.FileExists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static HashSet<int> GetRequiredAudioIndices(BMSFile bms)
    {
        var indices = new HashSet<int>();
        foreach (BMSFile.Chart measure in bms.Measures)
        {
            AddAllIndices(measure.GetPropertiesAllBgmNotes, indices);
            AddAllIndices(measure.GetPropertiesAll1PVisNotes, indices);
            AddAllIndices(measure.GetPropertiesAll2PVisNotes, indices);
            AddLongStartIndices(measure.GetPropertiesAll1PLngNotes, 80u, indices);
            AddLongStartIndices(measure.GetPropertiesAll2PLngNotes, 96u, indices);
        }
        return indices;
    }

    private static void AddAllIndices(
        IEnumerable<Func<IList<BMSFile.Chart.Note>>> getters,
        HashSet<int> indices)
    {
        foreach (Func<IList<BMSFile.Chart.Note>> getter in getters)
        {
            foreach (BMSFile.Chart.Note note in getter())
            {
                indices.Add(note.Index);
            }
        }
    }

    private static void AddLongStartIndices(
        IEnumerable<Func<IList<BMSFile.Chart.Note>>> getters,
        uint noteTypePrefix,
        HashSet<int> indices)
    {
        foreach (Func<IList<BMSFile.Chart.Note>> getter in getters)
        {
            foreach (BMSFile.Chart.Note note in getter())
            {
                if (((uint)note.Type & 0xFFFFFFF0u) == noteTypePrefix)
                {
                    indices.Add(note.Index);
                }
            }
        }
    }

    private static string GetFailureStage(Exception exception)
    {
        return exception switch
        {
            AudioSourceLoadException sourceFailure => sourceFailure.Stage.ToString(),
            BassAudioPlaybackException playbackFailure => playbackFailure.Stage.ToString(),
            _ => exception.GetType().Name
        };
    }

    private static string GetNativeError(Exception exception)
    {
        return exception switch
        {
            AudioSourceLoadException sourceFailure => sourceFailure.NativeErrorCode?.ToString() ?? "none",
            BassAudioPlaybackException playbackFailure => playbackFailure.NativeErrorCode?.ToString() ?? "none",
            _ => "none"
        };
    }

    private static void DisposeLoadedPlayers(TBassAudioPlayer[] players, FailedAudioResource primaryFailure)
    {
        foreach (TBassAudioPlayer? player in players)
        {
            if (player == null)
            {
                continue;
            }

            try
            {
                player.Dispose();
            }
            catch (Exception exception)
            {
                try
                {
                    NLogWrapper.GetLogger(nameof(BMSAutoPlayer)).Warn(
                        "Audio source cleanup failed after load failure. primary=" + primaryFailure.ResourceName
                        + " cleanup=" + exception.Message);
                }
                catch
                {
                    // 後片付けのログで主たる読み込み失敗を置き換えません。
                }
            }
        }
    }

    private sealed record FailedAudioResource(int Index, string ResourceName, string Path, Exception Exception);
}
