#nullable enable annotations
using System;
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
    private static readonly ConstructorInfo DecodedSourcePlayerConstructor = FindDecodedSourceConstructor();

    private IReadOnlyList<AudioSourceOmission> omittedAudioSources = Array.Empty<AudioSourceOmission>();

    /// <summary>今回の譜面読み込みで入力失敗により省略した音源を変更不能な一覧で取得します。</summary>
    internal IReadOnlyList<AudioSourceOmission> OmittedAudioSources => omittedAudioSources;

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

    /// <summary>再生に使う音源を事前に復号します。</summary>
    public override void LoadResources() => LoadResources(asParallel: true);

    /// <summary>再生に使う音源を、指定に応じた並列度で事前に復号します。</summary>
    public void LoadResources(bool asParallel) => LoadResources(asParallel, observer: null);

    internal void LoadResources(bool asParallel, AudioSourceLoadPipelineObserver? observer)
    {
        string basePath = Path.GetDirectoryName(base.Bms.Path) ?? string.Empty;
        int[] requiredIndices = GetRequiredAudioIndices(base.Bms).OrderBy(index => index).ToArray();
        var requests = new List<AudioLoadRequest>(requiredIndices.Length);
        foreach (int index in requiredIndices)
        {
            string resourceName = base.Bms.WavArray[index];
            string? path = FindAudioPath(basePath, resourceName);
            if (path != null)
            {
                requests.Add(new AudioLoadRequest(index, resourceName, path));
            }
        }

        AudioSourceLoadResult<TBassAudioPlayer> result = AudioSourceLoadPipeline.Load(
            base.Bms.WavArray.Length,
            requests,
            asParallel,
            CreatePlayer,
            observer);

        foreach (AudioLoadFailure failure in result.Failures)
        {
            if (failure.Exception is not AudioSourceLoadException { IsInputFailure: true })
            {
                throw new AudioSourceFatalException(
                    "The audio source pipeline returned a failure that is not caused by an input file.",
                    failure.Exception);
            }
        }

        AudioSourceOmission[] omissions = result.Failures
            .OrderBy(failure => failure.Index)
            .GroupBy(failure => GetNormalizedPath(failure.Path), StringComparer.OrdinalIgnoreCase)
            .Select(group => new AudioSourceOmission(group.First().ResourceName, group.Key))
            .ToArray();
        LogAudioOmissions(omissions, result.Failures);

        int requestedPathCount = requests
            .Select(request => GetNormalizedPath(request.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (requestedPathCount > 0 && omissions.Length == requestedPathCount)
        {
            AudioLoadFailure failure = result.Failures
                .OrderBy(item => item.Index)
                .First();
            string stage = GetFailureStage(failure.Exception);
            string nativeError = GetNativeError(failure.Exception);
            var chartFailure = new InvalidDataException(
                string.Format(BeMusicSeeker.Properties.Resources.AudioRequiredResourceLoadFailureFormat,
                    failure.ResourceName, failure.Path, stage, nativeError),
                failure.Exception);
            DisposeLoadedPlayers(result.Players, chartFailure);
            throw chartFailure;
        }

        omittedAudioSources = Array.AsReadOnly(omissions);
        base.AudioPlayers = Array.AsReadOnly(result.Players);
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

    /// <summary>次曲の解析前に旧曲sourceを停止・解放し、未確認をSourceReleaseで通知します。</summary>
    internal virtual void DisposeBeforeNextSong()
    {
        // 再生taskのResetPlaybackStateも旧配列を参照するため、配列を置き換える前に停止・合流します。
        Stop();
        DisposeAudioSourcesAfterUse();
    }

    /// <summary>音声使用終了後に譜面資源を解放し、全sourceのnative解放を確認します。</summary>
    /// <remarks>
    /// 呼出し前に再生または変換が終了し、sourceを参照する処理がないことが必要です。
    /// sourceをすべて解放試行し、native解放を確認できない場合はsessionが所有を維持したまま失敗します。
    /// </remarks>
    internal void DisposeAudioSourcesAfterUse()
    {
        TBassAudioPlayer[] previousPlayers = base.AudioPlayers?.ToArray() ?? [];
        // base.Disposeは音源のDispose例外が一つ出ると残りを処理しません。
        // sourceは個別に全件処理し、base側には画像だけを解放させます。
        base.AudioPlayers = Array.AsReadOnly(Array.Empty<TBassAudioPlayer>());
        Exception? disposalFailure = null;
        try
        {
            base.Dispose();
        }
        catch (Exception exception)
        {
            disposalFailure = exception;
        }

        foreach (TBassAudioPlayer? player in previousPlayers)
        {
            try
            {
                player?.Dispose();
            }
            catch (Exception exception)
            {
                disposalFailure = disposalFailure == null
                    ? exception
                    : new AggregateException(disposalFailure, exception);
            }
        }

        BassAudioPlayer? unconfirmed = previousPlayers
            .OfType<BassAudioPlayer>()
            .FirstOrDefault(player => !player.NativeReleaseConfirmed);
        if (unconfirmed != null)
        {
            throw unconfirmed.CreateSourceReleaseFailure(disposalFailure);
        }
        if (disposalFailure != null)
        {
            ExceptionDispatchInfo.Capture(disposalFailure).Throw();
        }
    }

    private static ConstructorInfo FindDecodedSourceConstructor()
    {
        return typeof(TBassAudioPlayer).GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(DecodedAudio), typeof(BassAudioSession)],
                modifiers: null)
            ?? throw new InvalidOperationException(
                $"{typeof(TBassAudioPlayer).Name} must provide a decoded-source constructor.");
    }

    private static TBassAudioPlayer CreatePlayer(
        DecodedAudio source,
        string path,
        BassAudioSession session)
    {
        try
        {
            return (TBassAudioPlayer)DecodedSourcePlayerConstructor.Invoke([path, source, session]);
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

        string? firstCandidate = null;
        foreach (string item in Resources.NormalizeExtension(resourceName))
        {
            string path = Path.Combine(basePath, item);
            firstCandidate ??= path;
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
                firstCandidate ??= path;
                if (LongPathFileSystem.FileExists(path))
                {
                    return path;
                }
            }
        }

        // 明示された非空参照が見つからない場合も、最初に探索したpathをpipelineへ渡して
        // 読取り失敗として警告・省略数・全件失敗判定へ一貫して反映します。
        return firstCandidate;
    }

    private static string GetNormalizedPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }

    private static void LogAudioOmissions(
        IReadOnlyList<AudioSourceOmission> omissions,
        IReadOnlyList<AudioLoadFailure> failures)
    {
        if (omissions.Count == 0)
        {
            return;
        }

        var failuresByPath = failures
            .GroupBy(failure => GetNormalizedPath(failure.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        foreach (AudioSourceOmission omission in omissions)
        {
            if (!failuresByPath.TryGetValue(omission.Path, out AudioLoadFailure? failure))
            {
                continue;
            }
            try
            {
                NLogWrapper.GetLogger(nameof(BMSAutoPlayer)).Warn(
                    failure.Exception,
                    "Skipped an audio source that BASS/Vorbis could not decode. resource="
                    + omission.ResourceName
                    + " path=" + omission.Path
                    + " stage=" + GetFailureStage(failure.Exception)
                    + " nativeError=" + GetNativeError(failure.Exception));
            }
            catch
            {
                // ログ障害で譜面の再生や変換結果を置き換えません。
            }
        }
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

    private static void DisposeLoadedPlayers(TBassAudioPlayer[] players, Exception primaryFailure)
    {
        var cleanupFailures = new List<Exception>();
        var disposedPlayers = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (TBassAudioPlayer? player in players)
        {
            if (player == null || !disposedPlayers.Add(player))
            {
                continue;
            }

            try
            {
                player.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
            if (!player.NativeReleaseConfirmed)
            {
                try
                {
                    cleanupFailures.Add(player.CreateSourceReleaseFailure());
                }
                catch (Exception exception)
                {
                    cleanupFailures.Add(exception);
                }
            }
        }

        if (cleanupFailures.Count == 0)
        {
            return;
        }
        foreach (Exception cleanupFailure in cleanupFailures)
        {
            try
            {
                NLogWrapper.GetLogger(nameof(BMSAutoPlayer)).Warn(
                    "Audio source cleanup failed after load failure. primary=" + primaryFailure.Message
                    + " cleanup=" + cleanupFailure);
            }
            catch
            {
                // 後片付けのログで主たる読み込み失敗を置き換えません。
            }
        }
        throw new AudioSourceFatalException(
            "Failed audio loading could not confirm release of a created source.",
            new AggregateException(new[] { primaryFailure }.Concat(cleanupFailures)));
    }

}
