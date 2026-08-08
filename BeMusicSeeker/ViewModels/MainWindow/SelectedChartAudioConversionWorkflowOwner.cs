using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using BeMusicSeeker.Views.Dialogs;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util.Extensions;
using ModelBmsFile = BeMusicSeeker.Models.BMSFile;
using RibbitBmsAutoPlayWriter = Ribbit.BMS.BMSAutoPlayWriter;

namespace BeMusicSeeker.ViewModels;

internal interface ISelectedChartAudioConversionPlaybackPort
{
    void StopPlayback();
}

internal interface ISelectedChartAudioConversionExecutor
{
    void Execute(
        IReadOnlyList<ModelBmsFile> bmsFiles,
        string saveDirectory,
        SelectedChartAudioConversionSettingsSnapshot settings,
        CancellationToken cancellationToken,
        Action<EncoderType> applyEncoderFallback,
        Action<bool> reportFileCompleted);
}

internal sealed class SelectedChartAudioConversionRequest
{
    internal SelectedChartAudioConversionRequest(IEnumerable<ChartOperationTarget> targets)
    {
        Targets = (targets ?? [])
            .Where(target => target?.HasCapability(ChartOperationCapabilities.ConvertToAudio) == true
                && ChartFileKindResolver.IsBmsChartFile(target.Chart)
                && !string.IsNullOrWhiteSpace(target.Chart.Path)
                && LongPathFileSystem.FileExists(target.Chart.Path))
            .ToArray();
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal bool HasTargets => Targets.Count > 0;
}

internal sealed class SelectedChartAudioConversionSettingsSnapshot
{
    internal SelectedChartAudioConversionSettingsSnapshot(
        EncoderType encoder,
        SampleRate encoderSampleRate,
        SampleFormat encoderFormat,
        AudioNormalization encoderNormalization,
        float encoderQuality,
        string encoderExeDirectory,
        float encoderAmplifier,
        string encodeFileNameFormat,
        string encoderDisplayName,
        string sampleRateDisplayName,
        string sampleFormatDisplayName)
    {
        Encoder = encoder;
        EncoderSampleRate = encoderSampleRate;
        EncoderFormat = encoderFormat;
        EncoderNormalization = encoderNormalization;
        EncoderQuality = encoderQuality;
        EncoderExeDirectory = encoderExeDirectory;
        EncoderAmplifier = encoderAmplifier;
        EncodeFileNameFormat = encodeFileNameFormat;
        EncoderDisplayName = encoderDisplayName;
        SampleRateDisplayName = sampleRateDisplayName;
        SampleFormatDisplayName = sampleFormatDisplayName;
    }

    internal EncoderType Encoder { get; }

    internal SampleRate EncoderSampleRate { get; }

    internal SampleFormat EncoderFormat { get; }

    internal AudioNormalization EncoderNormalization { get; }

    internal float EncoderQuality { get; }

    internal string EncoderExeDirectory { get; }

    internal float EncoderAmplifier { get; }

    internal string EncodeFileNameFormat { get; }

    internal string EncoderDisplayName { get; }

    internal string SampleRateDisplayName { get; }

    internal string SampleFormatDisplayName { get; }

    internal static SelectedChartAudioConversionSettingsSnapshot CreateCurrent(AudioEncodingSettingsSnapshot settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }
        return new SelectedChartAudioConversionSettingsSnapshot(
            settings.Encoder,
            settings.EncoderSampleRate,
            settings.EncoderFormat,
            settings.EncoderNormalization,
            settings.EncoderQuality,
            settings.EncoderExeDirectory,
            settings.EncoderAmplifier,
            settings.EncodeFileNameFormat,
            GetEncoderDisplayName(settings.Encoder),
            GetSampleRateDisplayName(settings.EncoderSampleRate),
            GetSampleFormatDisplayName(settings.EncoderFormat));
    }

    private static string GetEncoderDisplayName(EncoderType encoder)
    {
        return encoder switch
        {
            EncoderType.WAVE => "WAVE",
            EncoderType.MP3_LAME => "MP3 LAME",
            EncoderType.AAC_NERO => "AAC Nero",
            EncoderType.OPUS => "Opus",
            EncoderType.FLAC => "FLAC",
            EncoderType.OGG_VORBIS => "Ogg Vorbis",
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, "Unknown encoder type.")
        };
    }

    private static string GetSampleRateDisplayName(SampleRate sampleRate)
    {
        return sampleRate switch
        {
            SampleRate.AUTO => "Auto",
            SampleRate.SAMPLE_RATE_11025Hz => "11025Hz",
            SampleRate.SAMPLE_RATE_22050Hz => "22050Hz",
            SampleRate.SAMPLE_RATE_32000Hz => "32000Hz",
            SampleRate.SAMPLE_RATE_44100Hz => "44100Hz",
            SampleRate.SAMPLE_RATE_48000Hz => "48000Hz",
            SampleRate.SAMPLE_RATE_88200Hz => "88200Hz",
            SampleRate.SAMPLE_RATE_96000Hz => "96000Hz",
            SampleRate.SAMPLE_RATE_176400Hz => "176400Hz",
            SampleRate.SAMPLE_RATE_192000Hz => "192000Hz",
            _ => throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Unknown sample rate.")
        };
    }

    private static string GetSampleFormatDisplayName(SampleFormat sampleFormat)
    {
        return sampleFormat switch
        {
            SampleFormat.AUTO => "Auto",
            SampleFormat.SAMPLE_INT_8BIT => "8bit",
            SampleFormat.SAMPLE_INT_16BIT => "16bit",
            SampleFormat.SAMPLE_INT_24BIT => "24bit",
            SampleFormat.SAMPLE_INT_32BIT => "32bit",
            SampleFormat.SAMPLE_FLOAT_32BIT => "32bit (IEEE Float)",
            _ => throw new ArgumentOutOfRangeException(nameof(sampleFormat), sampleFormat, "Unknown sample format.")
        };
    }
}

internal enum SelectedChartAudioConversionStatus
{
    Completed,
    Cancelled
}

internal sealed class SelectedChartAudioConversionResult
{
    private SelectedChartAudioConversionResult(
        SelectedChartAudioConversionStatus status,
        int totalCount,
        int completedCount,
        int failedCount)
    {
        Status = status;
        TotalCount = totalCount;
        CompletedCount = completedCount;
        FailedCount = failedCount;
    }

    internal SelectedChartAudioConversionStatus Status { get; }

    internal int TotalCount { get; }

    internal int CompletedCount { get; }

    internal int FailedCount { get; }

    internal int SucceededCount => CompletedCount - FailedCount;

    internal int UnprocessedCount => TotalCount - CompletedCount;

    internal static SelectedChartAudioConversionResult Empty { get; } =
        new(SelectedChartAudioConversionStatus.Completed, 0, 0, 0);

    internal static SelectedChartAudioConversionResult Create(
        SelectedChartAudioConversionStatus status,
        int totalCount,
        int completedCount,
        int failedCount)
    {
        return new SelectedChartAudioConversionResult(
            status,
            Math.Max(0, totalCount),
            Math.Max(0, completedCount),
            Math.Max(0, failedCount));
    }
}

internal sealed class SelectedChartAudioConversionWorkflowOwner
{
    private readonly Func<SelectedChartAudioConversionSettingsSnapshot> settingsProvider;

    private readonly Action<EncoderType> applyEncoderFallback;

    private readonly ISelectedChartAudioConversionPlaybackPort playback;

    private readonly IUiDialogService dialogs;

    private readonly ISelectedChartAudioConversionExecutor executor;

    private int isRunning;

    internal SelectedChartAudioConversionWorkflowOwner(
        Func<SelectedChartAudioConversionSettingsSnapshot> settingsProvider,
        Action<EncoderType> applyEncoderFallback,
        ISelectedChartAudioConversionPlaybackPort playback,
        IUiDialogService dialogs,
        ISelectedChartAudioConversionExecutor executor)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.applyEncoderFallback = applyEncoderFallback ?? throw new ArgumentNullException(nameof(applyEncoderFallback));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    internal async Task<SelectedChartAudioConversionResult> RunAsync(
        SelectedChartAudioConversionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }
        if (!request.HasTargets)
        {
            return SelectedChartAudioConversionResult.Empty;
        }
        if (Interlocked.CompareExchange(ref isRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("An audio conversion workflow is already running.");
        }

        try
        {
            UiFolderPickerResult folderResult = await dialogs.PickFolderAsync(
            new UiFolderPickerRequest(BeMusicSeeker.Properties.Resources.Save_to),
            cancellationToken);
            ThrowIfPickerFailed(folderResult?.Status ?? UiDialogStatus.Failed, folderResult?.Error, "Audio conversion output folder picker");
            if (folderResult.Status != UiDialogStatus.Accepted)
            {
                return SelectedChartAudioConversionResult.Create(
                    SelectedChartAudioConversionStatus.Cancelled,
                    request.Targets.Count,
                    0,
                    0);
            }

            IReadOnlyList<ModelBmsFile> bmsFiles = request.Targets
                .Select(target => target.Chart.GetBmsStorageOwner())
                .Where(ChartFileKindResolver.IsBmsChartFile)
                .ToArray();
            if (bmsFiles.Count == 0)
            {
                return SelectedChartAudioConversionResult.Empty;
            }

            SelectedChartAudioConversionSettingsSnapshot settings = settingsProvider();
            if (settings == null)
            {
                throw new InvalidOperationException("Audio conversion settings snapshot was not provided.");
            }
            playback.StopPlayback();
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            int completedCount = 0;
            int failedCount = 0;
            Action<bool> reportFileCompleted = succeeded =>
            {
                Interlocked.Increment(ref completedCount);
                if (!succeeded)
                {
                    Interlocked.Increment(ref failedCount);
                }
            };
            Task conversionTask = Task.Run(
                () => executor.Execute(
                    bmsFiles,
                    folderResult.FolderPath,
                    settings,
                    operationCancellation.Token,
                    applyEncoderFallback,
                    reportFileCompleted),
                CancellationToken.None);
            _ = conversionTask.Logging("tableContextMenuItemConvertToAudioFileClick");

            UiProgressResult progressResult;
            try
            {
                progressResult = await dialogs.RunWithProgressAsync(
                    new UiProgressRequest(
                        BeMusicSeeker.Properties.Resources.Converting,
                        BuildProgressLabel(settings),
                        new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false)),
                    context => ObserveConversionProgressAsync(
                        conversionTask,
                        operationCancellation,
                        context,
                        () => Volatile.Read(ref completedCount),
                        bmsFiles),
                    operationCancellation.Token);
            }
            catch (Exception exception)
            {
                await CancelAndDrainConversionAsync(
                    conversionTask,
                    operationCancellation,
                    exception);
                throw;
            }
            if (progressResult == null)
            {
                progressResult = new UiProgressResult(
                    UiDialogStatus.Failed,
                    error: new InvalidOperationException("Audio conversion progress route returned no result."));
            }

            if (progressResult.Status == UiDialogStatus.Accepted)
            {
                await conversionTask;
            }
            else if (progressResult.Status == UiDialogStatus.CancelledByUser)
            {
                await CancelAndDrainConversionAsync(conversionTask, operationCancellation);
            }
            else
            {
                var progressFailure = new InvalidOperationException(
                    "Audio conversion progress route failed: " + progressResult.Status,
                    progressResult.Error);
                await CancelAndDrainConversionAsync(
                    conversionTask,
                    operationCancellation,
                    progressFailure);
                throw progressFailure;
            }

            bool cancelled = operationCancellation.IsCancellationRequested
                || progressResult.Status == UiDialogStatus.CancelledByUser;
            SelectedChartAudioConversionResult result = SelectedChartAudioConversionResult.Create(
                cancelled ? SelectedChartAudioConversionStatus.Cancelled : SelectedChartAudioConversionStatus.Completed,
                bmsFiles.Count,
                Volatile.Read(ref completedCount),
                Volatile.Read(ref failedCount));
            UiDialogResult completionResult = await dialogs.ShowMessageAsync(new UiMessageRequest(
                (cancelled
                    ? BeMusicSeeker.Properties.Resources.Msg_conversion_stopped
                    : BeMusicSeeker.Properties.Resources.Msg_conversion_completed)
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Success + ": " + result.SucceededCount
                    + Environment.NewLine
                    + BeMusicSeeker.Properties.Resources.Failure + ": " + (result.UnprocessedCount + result.FailedCount),
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OK,
                cancelled ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk,
                MessageBoxResult.OK));
            UiDialogRoute.ThrowIfNotShown(completionResult, "Audio conversion completion notification");
            return result;
        }
        finally
        {
            Volatile.Write(ref isRunning, 0);
        }
    }

    private static string BuildProgressLabel(SelectedChartAudioConversionSettingsSnapshot settings)
    {
        return settings.EncoderDisplayName
            + " - "
            + BeMusicSeeker.Properties.Resources.Sampling_rate
            + ":"
            + settings.SampleRateDisplayName
            + " "
            + BeMusicSeeker.Properties.Resources.Sampling_format
            + ":"
            + settings.SampleFormatDisplayName;
    }

    private static async Task ObserveConversionProgressAsync(
        Task conversionTask,
        CancellationTokenSource operationCancellation,
        UiProgressContext context,
        Func<int> completedCountProvider,
        IReadOnlyList<ModelBmsFile> bmsFiles)
    {
        while (!conversionTask.IsCompleted)
        {
            try
            {
                int completedCount = completedCountProvider();
                context.ReportWithCancellationCheck(
                    100 * (completedCount + 1) / (bmsFiles.Count + 1),
                    "[{0}/{1}] {2}",
                    Math.Min(completedCount + 1, bmsFiles.Count),
                    bmsFiles.Count,
                    bmsFiles[Math.Min(completedCount, bmsFiles.Count - 1)].path);
            }
            catch (ProgressDialogCancellationExcpetion)
            {
                await CancelAndDrainConversionAsync(conversionTask, operationCancellation);
                return;
            }

            await Task.WhenAny(conversionTask, Task.Delay(100));
        }
    }

    private static async Task CancelAndDrainConversionAsync(
        Task conversionTask,
        CancellationTokenSource cancellation,
        Exception primaryException = null)
    {
        Exception cancellationFailure = null;
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            cancellationFailure = exception;
        }

        Exception workerFailure = null;
        try
        {
            await conversionTask;
        }
        catch (OperationCanceledException exception)
            when (cancellation.IsCancellationRequested
                && exception.CancellationToken == cancellation.Token)
        {
        }
        catch (Exception exception)
        {
            workerFailure = exception;
        }

        if (cancellationFailure != null)
        {
            TryLogConversionSecondaryFailure("cancellation callback", cancellationFailure);
        }
        if (primaryException != null)
        {
            if (workerFailure != null)
            {
                TryLogConversionSecondaryFailure("worker drain", workerFailure);
            }
            ExceptionDispatchInfo.Capture(primaryException).Throw();
        }
        if (workerFailure != null)
        {
            ExceptionDispatchInfo.Capture(workerFailure).Throw();
        }
    }

    private static void TryLogConversionSecondaryFailure(string stage, Exception exception)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(SelectedChartAudioConversionWorkflowOwner)).Warn(
                "Audio conversion " + stage + " failed while preserving the terminal result: "
                + exception.Message);
        }
        catch
        {
            // Diagnostics must not replace progress or worker terminal behavior.
        }
    }

    private static void ThrowIfPickerFailed(UiDialogStatus status, Exception error, string routeName)
    {
        if (status is UiDialogStatus.Accepted or UiDialogStatus.CancelledByUser)
        {
            return;
        }
        throw new InvalidOperationException(routeName + " failed: " + status, error);
    }
}

internal sealed class BassSelectedChartAudioConversionExecutor : ISelectedChartAudioConversionExecutor
{
    private readonly BassAudioSessionLease sessionLease;
    private readonly Func<bool> tryReleaseEncoder;
    private readonly Func<BassAudioSession, bool> releaseSession;

    /// <summary>Creates an executor backed by the production audio cleanup boundaries.</summary>
    internal BassSelectedChartAudioConversionExecutor()
        : this(
            BassAudioWriter.TryReleaseEncoder,
            session => BassAudioPlayer.Free(session),
            new BassAudioSessionLease())
    {
    }

    /// <summary>
    /// Creates an executor with explicit cleanup boundaries so native ownership behavior can
    /// be verified without replacing the workflow's session lease.
    /// </summary>
    internal BassSelectedChartAudioConversionExecutor(
        Func<bool> tryReleaseEncoder,
        Func<BassAudioSession, bool> releaseSession,
        BassAudioSessionLease sessionLease)
    {
        this.tryReleaseEncoder = tryReleaseEncoder ?? throw new ArgumentNullException(nameof(tryReleaseEncoder));
        this.releaseSession = releaseSession ?? throw new ArgumentNullException(nameof(releaseSession));
        this.sessionLease = sessionLease ?? throw new ArgumentNullException(nameof(sessionLease));
    }

    public void Execute(
        IReadOnlyList<ModelBmsFile> bmsFiles,
        string saveDirectory,
        SelectedChartAudioConversionSettingsSnapshot settings,
        CancellationToken cancellationToken,
        Action<EncoderType> applyEncoderFallback,
        Action<bool> reportFileCompleted)
    {
        if (!LongPathFileSystem.DirectoryExists(saveDirectory))
        {
            throw new DirectoryNotFoundException("Directory " + saveDirectory + " not found");
        }

        if (!tryReleaseEncoder())
        {
            throw new InvalidOperationException(
                "A previous audio conversion encoder still owns native resources after cleanup failed.");
        }
        if (!sessionLease.TryRelease(releaseSession))
        {
            throw new InvalidOperationException(
                "A previous audio conversion still owns native resources after cleanup failed.");
        }

        BassAudioSession ownedSession = null;
        Exception primaryException = null;
        BassAudioOperationLease operation = default;
        bool operationEntered = false;
        try
        {
            BassAudioPlayer.Frequency = settings.EncoderSampleRate;
            BassAudioPlayer.Format = settings.EncoderFormat;
            BassAudioWriter.EncoderDirectory = settings.EncoderExeDirectory;
            BassAudioWriter.InitializeOwnedSession(out ownedSession);
            sessionLease.Attach(ownedSession);
            operation = BassAudioRuntime.EnterAudioOperation();
            operationEntered = true;
            EncoderType encoder = settings.Encoder;
            int index = 0;
            int totalCount = bmsFiles.Count;
            foreach (ModelBmsFile bmsFile in bmsFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                bool succeeded = true;
                RibbitBmsAutoPlayWriter writer = null;
                try
                {
                    index++;
                    var source = new Ribbit.BMS.BMSFile(bmsFile.path);
                    string fileName = new Dictionary<string, string>
                    {
                        ["%ARTIST%"] = ((source.Artist.Trim() ?? string.Empty) + " " + (source.Subartist?.Trim() ?? string.Empty)).Trim(),
                        ["%TITLE%"] = ((source.Title.Trim() ?? string.Empty) + " " + (source.Subtitle?.Trim() ?? string.Empty)).Trim(),
                        ["%GENRE%"] = source.Genre.Trim() ?? string.Empty,
                        ["%NO%"] = index.ToString().PadLeft(Math.Max(2, totalCount.ToString().Length), '0'),
                        ["%FILE%"] = Path.GetFileName(bmsFile.path),
                        ["%HASH%"] = source.Md5
                    }
                        .Aggregate(settings.EncodeFileNameFormat, (current, replacement) => current.Replace(replacement.Key, replacement.Value))
                        .NaturalNormalizationForFileName()
                        .ReplaceInvalidFileNameCharsByWide()
                        .RemoveInvalidFileNameChars()
                        .Trim();
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        fileName = index.ToString();
                    }
                    int pathLength = saveDirectory.Length + fileName.Length;
                    if (pathLength > 240 && fileName.Length > pathLength - 240 + 5)
                    {
                        fileName = fileName.Substring(0, fileName.Length - (pathLength - 235));
                    }
                    string outputPath = Path.Combine(saveDirectory, fileName);
                    writer = new RibbitBmsAutoPlayWriter(source);
                    if (!BassAudioWriter.IsEncoderAvailable(encoder))
                    {
                        encoder = EncoderType.WAVE;
                        applyEncoderFallback(encoder);
                    }
                    writer.LoadResources();
                    writer.Write(
                        encoder,
                        settings.EncoderQuality,
                        outputPath,
                        BassAudioMapping.ToBassNormalization(settings.EncoderNormalization),
                        settings.EncoderAmplifier);
                }
                catch (Exception ex)
                {
                    succeeded = false;
                    NLogWrapper.GetLogger()?.Warn(ex.ToString());
                }
                finally
                {
                    writer?.Dispose();
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    reportFileCompleted?.Invoke(succeeded);
                }
            }
        }
        catch (Exception exception)
        {
            primaryException = exception;
            throw;
        }
        finally
        {
            ReleaseConversionSession(ownedSession, primaryException, ref operation, operationEntered);
        }
    }

    /// <summary>
    /// Releases the encoder before the source session and retains the session lease when the
    /// encoder still owns native resources.
    /// </summary>
    internal void ReleaseConversionSession(BassAudioSession ownedSession, Exception primaryException)
    {
        BassAudioOperationLease operation = default;
        ReleaseConversionSession(ownedSession, primaryException, ref operation, operationEntered: false);
    }

    /// <summary>
    /// Releases conversion resources while an already-entered shared operation is still held.
    /// The operation is disposed before the source session can be released.
    /// </summary>
    internal void ReleaseConversionSession(
        BassAudioSession ownedSession,
        Exception primaryException,
        ref BassAudioOperationLease operation,
        bool operationEntered)
    {
        try
        {
            if (ownedSession != null && sessionLease.Session == null)
            {
                sessionLease.Attach(ownedSession);
            }
            if (!tryReleaseEncoder())
            {
                InvalidOperationException encoderCleanupException = new(
                    "Audio conversion encoder cleanup failed; retaining the native audio session for retry.");
                if (primaryException == null)
                {
                    throw encoderCleanupException;
                }

                TryLogSessionCleanupFailure(encoderCleanupException);
                return;
            }

            if (operationEntered)
            {
                operationEntered = false;
                operation.Dispose();
            }

            if (!sessionLease.TryRelease(releaseSession) && primaryException == null)
            {
                throw new InvalidOperationException(
                    "Audio conversion completed without confirming native cleanup.");
            }
        }
        catch (Exception cleanupException) when (primaryException != null)
        {
            TryLogSessionCleanupFailure(cleanupException);
        }
        finally
        {
            if (operationEntered)
            {
                operationEntered = false;
                operation.Dispose();
            }
        }
    }

    private static void TryLogSessionCleanupFailure(Exception exception)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor)).Warn(
                "Audio conversion session cleanup failed while preserving the primary error: "
                + exception.Message);
        }
        catch
        {
            // Diagnostics must not replace the conversion exception.
        }
    }
}
