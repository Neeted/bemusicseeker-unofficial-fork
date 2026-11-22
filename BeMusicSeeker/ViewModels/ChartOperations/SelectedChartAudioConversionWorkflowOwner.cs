using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using Ribbit.Logging;
using Ribbit.Media;
using Ribbit.Media.Audio;
using Ribbit.Util.Extensions;
using MessageBoxButton = BeMusicSeeker.Models.UiDialogButton;
using MessageBoxImage = BeMusicSeeker.Models.UiDialogIcon;
using MessageBoxResult = BeMusicSeeker.Models.UiDialogDefaultResult;
using ModelChartFile = BeMusicSeeker.Models.ChartFile;
using RibbitBmsAutoPlayWriter = Ribbit.BMS.BMSAutoPlayWriter;

namespace BeMusicSeeker.ViewModels;

internal interface ISelectedChartAudioConversionPlaybackPort
{
    /// <summary>ファイル変更・runtime解放前に、再生と先読みの必要な停止を終端まで待ちます。</summary>
    Task StopPlayback();
}

internal interface ISelectedChartAudioConversionExecutor
{
    /// <summary>
    /// 渡されたスナップショットの形式をそのまま使って変換します。WAVEへの代替はしません。
    /// 実行ファイルが見つからない場合は、そのファイルの通常の失敗として結果に残し、残りのファイルは続行します。
    /// </summary>
    /// <param name="bmsFiles">変換する譜面。</param>
    /// <param name="saveDirectory">出力先フォルダ。</param>
    /// <param name="settings">この変換で使う設定のスナップショット。</param>
    /// <param name="cancellationToken">変換を取り消すトークン。取消は譜面の音源の読込み・合成・書出しの途中へ届き、取消で完全な書出しに至らなかった譜面は結果へ報告しません。</param>
    /// <param name="reportFileCompleted">ファイルごとの結果の通知先。</param>
    /// <param name="reportProgress">譜面ごとの段階と合成の進み具合の通知先。音声処理の呼出し側から呼ぶため、受け側は軽い処理だけを行います。</param>
    void Execute(
        IReadOnlyList<ModelChartFile> bmsFiles,
        string saveDirectory,
        SelectedChartAudioConversionSettingsSnapshot settings,
        CancellationToken cancellationToken,
        Action<SelectedChartAudioConversionFileResult> reportFileCompleted,
        Action<SelectedChartAudioConversionProgress> reportProgress);
}

/// <summary>音声変換の進捗画面に表示する段階です。</summary>
internal enum SelectedChartAudioConversionStage
{
    /// <summary>再生と先読みの停止です。譜面を特定しない、進捗画面が扱う最初の段階です。</summary>
    StoppingPlayback,

    /// <summary>譜面の解析です。</summary>
    Analyzing,

    /// <summary>音源の読込みです。</summary>
    LoadingSources,

    /// <summary>音声の合成です。</summary>
    Synthesizing,

    /// <summary>ファイルの書出しです。</summary>
    Writing
}

/// <summary>実行側が報告する、現在処理中の譜面・段階・合成の進み具合を保持する変更不能な値です。</summary>
internal sealed class SelectedChartAudioConversionProgress
{
    /// <summary>進捗の値を初期化します。</summary>
    /// <param name="chartIndex">全体での譜面の位置(0始まり)。</param>
    /// <param name="stage">現在の段階。</param>
    /// <param name="synthesisFraction">現在の譜面の合成済みフレームの割合(0以上1以下)。合成以外の段階では参照されません。</param>
    internal SelectedChartAudioConversionProgress(int chartIndex, SelectedChartAudioConversionStage stage, double synthesisFraction = 0d)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(chartIndex);
        ChartIndex = chartIndex;
        Stage = stage;
        SynthesisFraction = Math.Clamp(synthesisFraction, 0d, 1d);
    }

    /// <summary>全体での譜面の位置(0始まり)です。</summary>
    internal int ChartIndex { get; }

    /// <summary>現在の段階です。</summary>
    internal SelectedChartAudioConversionStage Stage { get; }

    /// <summary>現在の譜面の合成済みフレームの割合です。</summary>
    internal double SynthesisFraction { get; }

    /// <summary>
    /// 現在の譜面内の割合を返します。解析と読込みの間は0、合成の間は合成済みの割合、書出しの間は1です。
    /// 段階の所要時間には重みを付けません。
    /// </summary>
    internal double ChartFraction => Stage switch
    {
        SelectedChartAudioConversionStage.Synthesizing => SynthesisFraction,
        SelectedChartAudioConversionStage.Writing => 1d,
        _ => 0d
    };
}

/// <summary>音声変換した1譜面の成否、対象名、失敗原因、音源省略数を保持します。</summary>
internal sealed class SelectedChartAudioConversionFileResult
{
    /// <summary>譜面の処理結果と、変換時に省略した音源数を初期化します。</summary>
    internal SelectedChartAudioConversionFileResult(
        string fileName,
        Exception error = null,
        int omittedAudioSourceCount = 0)
    {
        FileName = fileName ?? throw new ArgumentNullException(nameof(fileName));
        ArgumentOutOfRangeException.ThrowIfNegative(omittedAudioSourceCount);
        Error = error;
        OmittedAudioSourceCount = omittedAudioSourceCount;
    }

    /// <summary>処理対象譜面のパスです。</summary>
    internal string FileName { get; }

    /// <summary>変換が失敗した場合の例外です。</summary>
    internal Exception Error { get; }

    /// <summary>入力失敗のために変換対象譜面から省略した一意音源数です。</summary>
    internal int OmittedAudioSourceCount { get; }

    /// <summary>変換が成功したかを取得します。</summary>
    internal bool Succeeded => Error == null;
}

internal sealed class SelectedChartAudioConversionRequest
{
    internal SelectedChartAudioConversionRequest(IEnumerable<ChartOperationTarget> targets)
    {
        Targets = (targets ?? [])
            .Where(target => target?.HasCapability(ChartOperationCapabilities.ConvertToAudio) == true
                && !string.IsNullOrWhiteSpace(target.Chart.Path)
                && LongPathFileSystem.FileExists(target.Chart.Path))
            .ToArray();
    }

    internal IReadOnlyList<ChartOperationTarget> Targets { get; }

    internal bool HasTargets => Targets.Count > 0;
}

internal sealed class SelectedChartAudioConversionSettingsSnapshot
{
    /// <summary>1回の音声変換で利用する設定を変更不能な値として初期化します。</summary>
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
        string sampleFormatDisplayName,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
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
        SampleRateConversionQuality = AudioResamplingQuality.Validate(sampleRateConversionQuality);
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

    /// <summary>この変換開始時に捕捉したサンプルレート変換品質です。</summary>
    internal int SampleRateConversionQuality { get; }

    /// <summary>
    /// 形式だけをWAVEへ置き換え、他の設定値をすべて保った今回の変換専用のスナップショットを返します。
    /// 共有設定は変更しません。
    /// </summary>
    internal SelectedChartAudioConversionSettingsSnapshot WithWaveEncoder()
    {
        return new SelectedChartAudioConversionSettingsSnapshot(
            EncoderType.WAVE,
            EncoderSampleRate,
            EncoderFormat,
            EncoderNormalization,
            EncoderQuality,
            EncoderExeDirectory,
            EncoderAmplifier,
            EncodeFileNameFormat,
            GetEncoderDisplayName(EncoderType.WAVE),
            SampleRateDisplayName,
            SampleFormatDisplayName,
            SampleRateConversionQuality);
    }

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
            GetSampleFormatDisplayName(settings.EncoderFormat),
            settings.SampleRateConversionQuality);
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
            SampleRate.AUTO => BeMusicSeeker.Properties.Resources.Auto,
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
            SampleFormat.AUTO => BeMusicSeeker.Properties.Resources.Auto,
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
    /// <summary>Lで未受理のため変換・停止・保存を開始しません。</summary>
    Busy,
    Completed,
    Cancelled
}

internal sealed class SelectedChartAudioConversionResult
{
    private SelectedChartAudioConversionResult(
        SelectedChartAudioConversionStatus status,
        int totalCount,
        int completedCount,
        int failedCount,
        IReadOnlyList<SelectedChartAudioConversionFileResult> fileResults)
    {
        Status = status;
        TotalCount = totalCount;
        CompletedCount = completedCount;
        FailedCount = failedCount;
        FileResults = fileResults;
    }

    internal SelectedChartAudioConversionStatus Status { get; }

    internal int TotalCount { get; }

    internal int CompletedCount { get; }

    internal int FailedCount { get; }

    internal IReadOnlyList<SelectedChartAudioConversionFileResult> FileResults { get; }

    internal int SucceededCount => CompletedCount - FailedCount;

    internal int UnprocessedCount => TotalCount - CompletedCount;

    internal static SelectedChartAudioConversionResult Empty { get; } =
        new(SelectedChartAudioConversionStatus.Completed, 0, 0, 0, Array.Empty<SelectedChartAudioConversionFileResult>());

    internal static SelectedChartAudioConversionResult Create(
        SelectedChartAudioConversionStatus status,
        int totalCount,
        int completedCount,
        int failedCount,
        IReadOnlyList<SelectedChartAudioConversionFileResult> fileResults = null)
    {
        return new SelectedChartAudioConversionResult(
            status,
            Math.Max(0, totalCount),
            Math.Max(0, completedCount),
            Math.Max(0, failedCount),
            fileResults ?? Array.Empty<SelectedChartAudioConversionFileResult>());
    }
}

internal sealed class SelectedChartAudioConversionWorkflowOwner
{
    private readonly Func<SelectedChartAudioConversionSettingsSnapshot> settingsProvider;

    private readonly Func<EncoderType, string, bool> isEncoderAvailable;

    private readonly ISelectedChartAudioConversionPlaybackPort playback;

    private readonly IUiDialogService dialogs;

    private readonly ISelectedChartAudioConversionExecutor executor;

    private readonly ChartFileOperationSynchronizer operationAdmission;

    /// <summary>実行側の進捗を画面へ渡す間隔です。音声処理の呼出し側から画面を更新せず、最新値だけを間引いて渡します。</summary>
    private static readonly TimeSpan ProgressPollInterval = TimeSpan.FromMilliseconds(50);

    /// <param name="settingsProvider">変換ごとに一回だけ呼ぶ設定のスナップショット提供元。</param>
    /// <param name="isEncoderAvailable">形式と検索先から、外部エンコーダーを利用できるかを判定します。</param>
    /// <param name="playback">変換前に停止する再生の窓口。</param>
    /// <param name="dialogs">確認・出力先選択・進捗・通知の窓口。</param>
    /// <param name="executor">変換の実行側。</param>
    /// <param name="operationAdmission">ライブラリ操作の受付。</param>
    internal SelectedChartAudioConversionWorkflowOwner(
        Func<SelectedChartAudioConversionSettingsSnapshot> settingsProvider,
        Func<EncoderType, string, bool> isEncoderAvailable,
        ISelectedChartAudioConversionPlaybackPort playback,
        IUiDialogService dialogs,
        ISelectedChartAudioConversionExecutor executor,
        ChartFileOperationSynchronizer operationAdmission)
    {
        this.settingsProvider = settingsProvider ?? throw new ArgumentNullException(nameof(settingsProvider));
        this.isEncoderAvailable = isEncoderAvailable ?? throw new ArgumentNullException(nameof(isEncoderAvailable));
        this.playback = playback ?? throw new ArgumentNullException(nameof(playback));
        this.dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        this.executor = executor ?? throw new ArgumentNullException(nameof(executor));
        this.operationAdmission = operationAdmission ?? throw new ArgumentNullException(nameof(operationAdmission));
    }

    /// <summary>
    /// 選択譜面の音声変換を実行します。保存形式がWAVE以外で利用できない場合は、出力先の選択前に一度だけ警告し、
    /// 承認時は今回の変換だけをWAVEで続行し、取消時は変換しません。共有設定の形式は書き換えません。
    /// </summary>
    /// <param name="request">変換する譜面の要求。</param>
    /// <param name="cancellationToken">処理を取り消すトークン。</param>
    /// <returns>変換結果。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> が <see langword="null"/> の場合。</exception>
    /// <exception cref="DirectoryNotFoundException">選択後、再生停止前に出力先が存在しない場合。</exception>
    /// <exception cref="InvalidOperationException">設定を取得できない場合、または警告確認が成否を判断できない結果を返した場合。</exception>
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
        SelectedChartAudioConversionSettingsSnapshot settings = settingsProvider()
            ?? throw new InvalidOperationException("Audio conversion settings snapshot was not provided.");
        if (settings.Encoder != EncoderType.WAVE
            && !isEncoderAvailable(settings.Encoder, settings.EncoderExeDirectory))
        {
            UiDialogResult confirmation = await dialogs.ConfirmAsync(
                new UiConfirmationRequest(
                    string.Format(
                        BeMusicSeeker.Properties.Resources.AudioConversionEncoderUnavailableConfirmFormat,
                        settings.EncoderDisplayName,
                        settings.Encoder.GetEncoderFileName()),
                    BeMusicSeeker.Properties.Resources.Warning,
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Exclamation,
                    MessageBoxResult.Cancel),
                cancellationToken);
            switch (confirmation?.Status)
            {
                case UiDialogStatus.Accepted:
                    settings = settings.WithWaveEncoder();
                    break;
                case UiDialogStatus.CancelledByUser:
                case UiDialogStatus.Rejected:
                case UiDialogStatus.ClosedByUser:
                    return SelectedChartAudioConversionResult.Create(
                        SelectedChartAudioConversionStatus.Cancelled,
                        request.Targets.Count,
                        0,
                        0);
                default:
                    throw new InvalidOperationException(
                        "Audio conversion encoder unavailable confirmation failed: "
                        + (confirmation?.Status.ToString() ?? "no result"),
                        confirmation?.Exception);
            }
        }

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

        if (!operationAdmission.TryEnter(out IDisposable accepted))
        {
            await ShowOptionalNotificationAsync(UiMessageRequest.CreateWarning(
                BeMusicSeeker.Properties.Resources.Warn_LibraryOperationBusy, BeMusicSeeker.Properties.Resources.Warning),
                "Audio conversion Busy notification");
            return SelectedChartAudioConversionResult.Create(SelectedChartAudioConversionStatus.Busy, request.Targets.Count, 0, 0);
        }
        using (accepted)
        {
            IReadOnlyList<ModelChartFile> bmsFiles = request.Targets
                .Select(target => target.Chart)
                .ToArray();
            if (bmsFiles.Count == 0)
            {
                return SelectedChartAudioConversionResult.Empty;
            }

            if (!LongPathFileSystem.DirectoryExists(folderResult.FolderPath))
            {
                throw new DirectoryNotFoundException();
            }
            using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var run = new ConversionRun(bmsFiles, operationCancellation, SynchronizationContext.Current);
            Action<SelectedChartAudioConversionFileResult> reportFileCompleted = fileResult =>
            {
                if (fileResult == null)
                {
                    throw new ArgumentNullException(nameof(fileResult));
                }
                Interlocked.Increment(ref run.CompletedCount);
                if (!fileResult.Succeeded)
                {
                    Interlocked.Increment(ref run.FailedCount);
                }
                lock (run.FileResults)
                {
                    run.FileResults.Add(fileResult);
                }
            };

            UiProgressResult progressResult;
            try
            {
                // 進捗画面を先に開き、再生停止は画面が扱う処理の最初の段階として実行します。
                progressResult = await dialogs.RunWithProgressAsync(
                    new UiProgressRequest(
                        BeMusicSeeker.Properties.Resources.Converting,
                        BuildProgressLabel(settings),
                        new ProgressDialogSettings(showSubLabel: true, showCancelButton: true, showProgressBarIndeterminate: false)),
                    context =>
                    {
                        Task operation = RunProgressOperationAsync(
                            run,
                            context,
                            folderResult.FolderPath,
                            settings,
                            reportFileCompleted);
                        Volatile.Write(ref run.Operation, operation);
                        return operation;
                    },
                    operationCancellation.Token);
            }
            catch (Exception exception)
            {
                await CancelAndDrainConversionAsync(run, exception);
                throw;
            }
            if (progressResult == null)
            {
                progressResult = new UiProgressResult(
                    UiDialogStatus.Failed,
                    error: new InvalidOperationException("Audio conversion progress route returned no result."));
            }
            else if (progressResult.Status == UiDialogStatus.Accepted && Volatile.Read(ref run.Operation) == null)
            {
                progressResult = new UiProgressResult(
                    UiDialogStatus.Failed,
                    error: new InvalidOperationException("Audio conversion progress route did not run the operation."));
            }

            if (progressResult.Status == UiDialogStatus.Accepted)
            {
                await run.Operation;
                if (run.StopFailure != null)
                {
                    ExceptionDispatchInfo.Capture(run.StopFailure).Throw();
                }
                if (run.ConversionTask != null)
                {
                    await run.ConversionTask;
                }
            }
            else if (progressResult.Status == UiDialogStatus.CancelledByUser)
            {
                await CancelAndDrainConversionAsync(run);
                if (run.StopFailure != null)
                {
                    ExceptionDispatchInfo.Capture(run.StopFailure).Throw();
                }
            }
            else
            {
                var progressFailure = new InvalidOperationException(
                    "Audio conversion progress route failed: " + progressResult.Status,
                    progressResult.Error);
                await CancelAndDrainConversionAsync(run, progressFailure);
                throw progressFailure;
            }

            bool cancelled = operationCancellation.IsCancellationRequested
                || progressResult.Status == UiDialogStatus.CancelledByUser;
            var result = SelectedChartAudioConversionResult.Create(
                cancelled ? SelectedChartAudioConversionStatus.Cancelled : SelectedChartAudioConversionStatus.Completed,
                bmsFiles.Count,
                Volatile.Read(ref run.CompletedCount),
                Volatile.Read(ref run.FailedCount),
                run.FileResults.ToArray());
            await ShowOptionalNotificationAsync(new UiMessageRequest(
                BuildCompletionMessage(result, settings, cancelled),
                BeMusicSeeker.Properties.Resources.Confirm,
                MessageBoxButton.OK,
                cancelled ? MessageBoxImage.Exclamation : MessageBoxImage.Asterisk,
                MessageBoxResult.OK), "Audio conversion completion notification");
            return result;
        }
    }

    private async Task ShowOptionalNotificationAsync(UiMessageRequest request, string routeName)
    {
        try
        {
            UiDialogResult result = await dialogs.ShowMessageAsync(request).ConfigureAwait(false);
            UiDialogRoute.ThrowIfNotShown(result, routeName);
        }
        catch (Exception exception)
        {
            try { NLogWrapper.FileLogger?.Warn(exception, routeName); }
            catch { /* 任意通知の診断失敗を変換結果へ合成しません。 */ }
        }
    }

    private static string BuildCompletionMessage(
        SelectedChartAudioConversionResult result,
        SelectedChartAudioConversionSettingsSnapshot settings,
        bool cancelled)
    {
        var message = new System.Text.StringBuilder(
            (cancelled
                ? BeMusicSeeker.Properties.Resources.Msg_conversion_stopped
                : BeMusicSeeker.Properties.Resources.Msg_conversion_completed)
                + Environment.NewLine
                + BeMusicSeeker.Properties.Resources.Success + ": " + result.SucceededCount
                + Environment.NewLine
                + BeMusicSeeker.Properties.Resources.Failure + ": " + (result.UnprocessedCount + result.FailedCount)
                + Environment.NewLine
                + string.Format(
                    BeMusicSeeker.Properties.Resources.AudioConversionOutputFormatFormat,
                    settings.EncoderDisplayName));

        SelectedChartAudioConversionFileResult[] failures = result.FileResults
            .Where(fileResult => !fileResult.Succeeded)
            .ToArray();
        SelectedChartAudioConversionFileResult[] rangeFailures = failures
            .Where(fileResult => fileResult.Error is AudioOutputRangeException)
            .ToArray();
        if (rangeFailures.Length > 0)
        {
            (SelectedChartAudioConversionFileResult FileResult, AudioOutputRangeException Error) maximumPeak = rangeFailures
                .Select(fileResult =>
                {
                    if (fileResult.Error is not AudioOutputRangeException rangeError)
                    {
                        throw new InvalidOperationException("A range failure did not retain its range error.");
                    }
                    return (FileResult: fileResult, Error: rangeError);
                })
                .OrderByDescending(value => value.Error.Peak)
                .First();
            SelectedChartAudioConversionFileResult maximumPeakResult = maximumPeak.FileResult;
            AudioOutputRangeException rangeException = maximumPeak.Error;
            string peakDb = Math.Round(
                20d * Math.Log10(rangeException.Peak),
                2,
                MidpointRounding.AwayFromZero).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            double additionalGainUpperBound = settings.EncoderAmplifier / rangeException.Peak;
            if (additionalGainUpperBound < 0.5d)
            {
                message.Append(Environment.NewLine).AppendFormat(
                    BeMusicSeeker.Properties.Resources.AudioConversionRangeFailureImpossibleFormat,
                    rangeFailures.Length,
                    peakDb,
                    Path.GetFileName(maximumPeakResult.FileName));
            }
            else
            {
                message.Append(Environment.NewLine).AppendFormat(
                    BeMusicSeeker.Properties.Resources.AudioConversionRangeFailureFormat,
                    rangeFailures.Length,
                    peakDb,
                    Path.GetFileName(maximumPeakResult.FileName),
                    Math.Floor(100d / rangeException.Peak).ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                    GetSafeAdditionalGainSuggestion(additionalGainUpperBound));
            }
        }

        SelectedChartAudioConversionFileResult[] otherFailures = failures
            .Where(fileResult => fileResult.Error is not AudioOutputRangeException)
            .ToArray();
        foreach (SelectedChartAudioConversionFileResult failure in otherFailures.Take(3))
        {
            Exception error = failure.Error
                ?? throw new InvalidOperationException("A failed audio conversion result did not retain its error.");
            message.Append(Environment.NewLine).AppendFormat(
                BeMusicSeeker.Properties.Resources.AudioConversionOtherFailureFormat,
                Path.GetFileName(failure.FileName),
                error.Message);
        }
        if (otherFailures.Length > 3)
        {
            message.Append(Environment.NewLine).AppendFormat(
                BeMusicSeeker.Properties.Resources.AudioConversionOtherFailuresRemainingFormat,
                otherFailures.Length,
                otherFailures.Length - 3);
        }

        SelectedChartAudioConversionFileResult[] omittedAudioFiles = result.FileResults
            .Where(fileResult => fileResult.Succeeded && fileResult.OmittedAudioSourceCount > 0)
            .ToArray();
        foreach (SelectedChartAudioConversionFileResult fileResult in omittedAudioFiles.Take(3))
        {
            message.Append(Environment.NewLine).AppendFormat(
                BeMusicSeeker.Properties.Resources.AudioConversionOmittedSourcesFormat,
                Path.GetFileName(fileResult.FileName),
                fileResult.OmittedAudioSourceCount);
        }
        if (omittedAudioFiles.Length > 3)
        {
            message.Append(Environment.NewLine).AppendFormat(
                BeMusicSeeker.Properties.Resources.AudioConversionOmittedSourcesRemainingFormat,
                omittedAudioFiles.Length,
                omittedAudioFiles.Length - 3);
        }

        return message.ToString();
    }

    private static string GetSafeAdditionalGainSuggestion(double upperBound)
    {
        double candidate = Math.Floor(upperBound * 10d) / 10d;
        float candidateFloat = (float)candidate;
        while ((double)candidateFloat > upperBound)
        {
            candidate = Math.Round(candidate - 0.1d, 10);
            candidateFloat = (float)candidate;
        }
        return candidateFloat.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
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

    /// <summary>進捗画面の操作です。再生停止の段階を終えてから変換を開始し、変換の終端まで進捗を表示します。</summary>
    private async Task RunProgressOperationAsync(
        ConversionRun run,
        UiProgressContext context,
        string saveDirectory,
        SelectedChartAudioConversionSettingsSnapshot settings,
        Action<SelectedChartAudioConversionFileResult> reportFileCompleted)
    {
        if (!await StopPlaybackStageAsync(run, context))
        {
            return;
        }

        var conversionTask = Task.Run(
            () => executor.Execute(
                run.Files,
                saveDirectory,
                settings,
                run.Cancellation.Token,
                reportFileCompleted,
                run.ReportProgress),
            CancellationToken.None);
        run.ConversionTask = conversionTask;
        conversionTask.ObserveFault("tableContextMenuItemConvertToAudioFileClick");
        await ObserveConversionProgressAsync(run, conversionTask, context);
    }

    /// <summary>
    /// 再生停止の段階を表示しながら停止の完了を待ちます。停止の失敗は元の例外として記録し、変換を始めません。
    /// 取消は停止の完了を待ってから、変換を始めずに返します。
    /// </summary>
    /// <returns>変換を始めてよい場合だけtrueです。</returns>
    private async Task<bool> StopPlaybackStageAsync(ConversionRun run, UiProgressContext context)
    {
        Task stopTask = InvokeOnCapturedContext(run.CallerContext, playback.StopPlayback);
        bool cancelRequested = false;
        do
        {
            try
            {
                context.ReportWithCancellationCheck(
                    0,
                    BeMusicSeeker.Properties.Resources.AudioConversionStageStoppingPlayback);
            }
            catch (ProgressDialogCancellationExcpetion)
            {
                cancelRequested = true;
            }
            if (cancelRequested || run.Cancellation.IsCancellationRequested)
            {
                cancelRequested = true;
                break;
            }
            await Task.WhenAny(stopTask, Task.Delay(ProgressPollInterval));
        }
        while (!stopTask.IsCompleted);

        // 停止は取消でも途中で捨てず、完了まで待ちます。
        await Task.WhenAny(stopTask);
        if (!cancelRequested)
        {
            // 停止の完了と同時に押された取消も、変換を始める前に拾います。
            try
            {
                context.ThrowIfCancellationPending();
            }
            catch (ProgressDialogCancellationExcpetion)
            {
                cancelRequested = true;
            }
            cancelRequested |= run.Cancellation.IsCancellationRequested;
        }
        if (cancelRequested)
        {
            CancelConversion(run.Cancellation);
        }
        if (stopTask.IsFaulted || stopTask.IsCanceled)
        {
            try
            {
                await stopTask;
            }
            catch (Exception exception)
            {
                run.StopFailure = exception;
            }
            return false;
        }
        return !cancelRequested;
    }

    /// <summary>呼出し元の同期コンテキストがあればそこで、なければ呼出し元のまま再生停止を始めます。</summary>
    private static Task InvokeOnCapturedContext(SynchronizationContext callerContext, Func<Task> action)
    {
        if (callerContext == null)
        {
            try
            {
                return action();
            }
            catch (Exception exception)
            {
                return Task.FromException(exception);
            }
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        callerContext.Post(
            _ =>
            {
                Task task;
                try
                {
                    task = action();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                    return;
                }
                task.ContinueWith(
                    completed =>
                    {
                        if (completed.IsFaulted)
                        {
                            completion.TrySetException(completed.Exception.InnerExceptions);
                        }
                        else if (completed.IsCanceled)
                        {
                            completion.TrySetCanceled();
                        }
                        else
                        {
                            completion.TrySetResult();
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            },
            null);
        return completion.Task;
    }

    private static async Task ObserveConversionProgressAsync(
        ConversionRun run,
        Task conversionTask,
        UiProgressContext context)
    {
        SelectedChartAudioConversionProgress reported = null;
        while (!conversionTask.IsCompleted)
        {
            try
            {
                // 実行側の通知は最新値だけを保持し、画面へは一定間隔で間引いて渡します。
                SelectedChartAudioConversionProgress latest = Volatile.Read(ref run.LatestProgress);
                if (ReferenceEquals(latest, reported))
                {
                    context.ThrowIfCancellationPending();
                }
                else
                {
                    reported = latest;
                    int total = run.Files.Count;
                    context.ReportWithCancellationCheck(
                        FormatPercent(latest, total),
                        "[{0}/{1}] {2} {3}",
                        Math.Min(latest.ChartIndex + 1, total),
                        total,
                        GetStageName(latest.Stage),
                        run.Files[Math.Min(latest.ChartIndex, total - 1)].Path);
                }
            }
            catch (ProgressDialogCancellationExcpetion)
            {
                // 実作業と資源解放の終端を待ってから、進捗画面を閉じます。
                CancelConversion(run.Cancellation);
                await Task.WhenAny(conversionTask);
                return;
            }

            await Task.WhenAny(conversionTask, Task.Delay(ProgressPollInterval));
        }
    }

    /// <summary>(完了した譜面数 + 現在の譜面内の割合) / 全体数 を百分率にします。</summary>
    private static int FormatPercent(SelectedChartAudioConversionProgress progress, int total)
    {
        double percent = 100d * (progress.ChartIndex + progress.ChartFraction) / Math.Max(1, total);
        return Math.Clamp((int)Math.Floor(percent + 1e-9), 0, 100);
    }

    private static string GetStageName(SelectedChartAudioConversionStage stage)
    {
        return stage switch
        {
            SelectedChartAudioConversionStage.StoppingPlayback => BeMusicSeeker.Properties.Resources.AudioConversionStageStoppingPlayback,
            SelectedChartAudioConversionStage.Analyzing => BeMusicSeeker.Properties.Resources.AudioConversionStageAnalyzing,
            SelectedChartAudioConversionStage.LoadingSources => BeMusicSeeker.Properties.Resources.AudioConversionStageLoadingSources,
            SelectedChartAudioConversionStage.Synthesizing => BeMusicSeeker.Properties.Resources.AudioConversionStageSynthesizing,
            SelectedChartAudioConversionStage.Writing => BeMusicSeeker.Properties.Resources.AudioConversionStageWriting,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown audio conversion stage.")
        };
    }

    private static void CancelConversion(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (Exception exception)
        {
            TryLogConversionSecondaryFailure("cancellation callback", exception);
        }
    }

    /// <summary>
    /// 取消を要求し、進捗画面の操作と変換の実作業が終端に達するまで待ちます。
    /// 取消による終了は結果として扱い、実行側の例外は主原因がなければ返し、あれば診断へ残します。
    /// </summary>
    private static async Task CancelAndDrainConversionAsync(
        ConversionRun run,
        Exception primaryException = null)
    {
        CancelConversion(run.Cancellation);

        Task operation = Volatile.Read(ref run.Operation);
        if (operation != null)
        {
            await Task.WhenAny(operation);
            if (operation.IsFaulted)
            {
                TryLogConversionSecondaryFailure("progress operation", operation.Exception.GetBaseException());
            }
        }

        Task conversionTask = run.ConversionTask;
        Exception workerFailure = null;
        if (conversionTask != null)
        {
            try
            {
                await conversionTask;
            }
            catch (OperationCanceledException exception)
                when (run.Cancellation.IsCancellationRequested
                    && exception.CancellationToken == run.Cancellation.Token)
            {
            }
            catch (Exception exception)
            {
                workerFailure = exception;
            }
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

    /// <summary>一回の変換で、進捗画面の操作・変換の実作業・計数が共有する状態です。</summary>
    private sealed class ConversionRun
    {
        internal ConversionRun(
            IReadOnlyList<ModelChartFile> files,
            CancellationTokenSource cancellation,
            SynchronizationContext callerContext)
        {
            Files = files;
            Cancellation = cancellation;
            CallerContext = callerContext;
            LatestProgress = new SelectedChartAudioConversionProgress(0, SelectedChartAudioConversionStage.Analyzing);
            ReportProgress = progress =>
            {
                if (progress != null)
                {
                    Volatile.Write(ref LatestProgress, progress);
                }
            };
        }

        internal IReadOnlyList<ModelChartFile> Files { get; }

        internal CancellationTokenSource Cancellation { get; }

        /// <summary>再生停止を呼出し元の文脈で行うための同期コンテキストです。なければnullです。</summary>
        internal SynchronizationContext CallerContext { get; }

        internal List<SelectedChartAudioConversionFileResult> FileResults { get; } = [];

        internal int CompletedCount;

        internal int FailedCount;

        /// <summary>実行側が最後に通知した進捗です。</summary>
        internal SelectedChartAudioConversionProgress LatestProgress;

        internal Action<SelectedChartAudioConversionProgress> ReportProgress { get; }

        /// <summary>進捗画面へ渡した操作のTaskです。画面が操作を呼ばなかった場合はnullです。</summary>
        internal Task Operation;

        /// <summary>変換の実作業のTaskです。再生停止で終わった場合はnullです。</summary>
        internal Task ConversionTask;

        /// <summary>再生停止が失敗した場合の元の例外です。</summary>
        internal Exception StopFailure;
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

    /// <summary>
    /// 譜面ごとに、解析・音源の読込み・合成・書出しの順に変換します。取消は音源の読込みと合成の途中、書出しの供給ごとに届き、
    /// 取消で完全な書出しに至らなかった譜面は結果へ報告せず、その譜面の未完成の出力は削除して処理を終えます。
    /// </summary>
    /// <param name="reportProgress">段階と合成の割合の通知先です。省略時は通知しません。</param>
    public void Execute(
        IReadOnlyList<ModelChartFile> bmsFiles,
        string saveDirectory,
        SelectedChartAudioConversionSettingsSnapshot settings,
        CancellationToken cancellationToken,
        Action<SelectedChartAudioConversionFileResult> reportFileCompleted,
        Action<SelectedChartAudioConversionProgress> reportProgress = null)
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
        string currentFileName = null;
        SelectedChartAudioConversionFileResult terminalFileResult = null;
        try
        {
            BassAudioPlayer.Frequency = settings.EncoderSampleRate;
            BassAudioPlayer.Format = settings.EncoderFormat;
            BassAudioWriter.EncoderDirectory = settings.EncoderExeDirectory;
            BassAudioWriter.InitializeOwnedSession(
                out ownedSession,
                settings.SampleRateConversionQuality);
            sessionLease.Attach(ownedSession);
            operation = BassAudioRuntime.EnterAudioOperation();
            operationEntered = true;
            int index = 0;
            int totalCount = bmsFiles.Count;
            foreach (ModelChartFile bmsFile in bmsFiles)
            {
                currentFileName = bmsFile.Path;
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                Exception fileException = null;
                Exception fatalFileException = null;
                bool cancelledFile = false;
                int omittedAudioSourceCount = 0;
                RibbitBmsAutoPlayWriter writer = null;
                try
                {
                    index++;
                    int chartIndex = index - 1;
                    reportProgress?.Invoke(new SelectedChartAudioConversionProgress(
                        chartIndex, SelectedChartAudioConversionStage.Analyzing));
                    var source = Ribbit.BMS.PlaybackChart.Load(bmsFile.Path);
                    string fileName = new Dictionary<string, string>
                    {
                        ["%ARTIST%"] = ((source.Artist.Trim() ?? string.Empty) + " " + (source.Subartist?.Trim() ?? string.Empty)).Trim(),
                        ["%TITLE%"] = ((source.Title.Trim() ?? string.Empty) + " " + (source.Subtitle?.Trim() ?? string.Empty)).Trim(),
                        ["%GENRE%"] = source.Genre.Trim() ?? string.Empty,
                        ["%NO%"] = index.ToString().PadLeft(Math.Max(2, totalCount.ToString().Length), '0'),
                        ["%FILE%"] = Path.GetFileName(bmsFile.Path),
                        ["%HASH%"] = source.Hash
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
                    reportProgress?.Invoke(new SelectedChartAudioConversionProgress(
                        chartIndex, SelectedChartAudioConversionStage.LoadingSources));
                    writer.LoadResources(cancellationToken);
                    omittedAudioSourceCount = writer.OmittedAudioSources.Count;
                    writer.Write(
                        settings.Encoder,
                        settings.EncoderQuality,
                        outputPath,
                        BassAudioMapping.ToBassNormalization(settings.EncoderNormalization),
                        settings.EncoderAmplifier,
                        cancellationToken,
                        fraction => reportProgress?.Invoke(new SelectedChartAudioConversionProgress(
                            chartIndex, SelectedChartAudioConversionStage.Synthesizing, fraction)),
                        () => reportProgress?.Invoke(new SelectedChartAudioConversionProgress(
                            chartIndex, SelectedChartAudioConversionStage.Writing)));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // 取消で途中終了した譜面は未処理として数え、失敗の結果にも診断にも出しません。
                    cancelledFile = true;
                }
                catch (Exception ex) when (IsFatalConversionFailure(ex))
                {
                    fatalFileException = ex;
                }
                catch (Exception ex)
                {
                    fileException = ex;
                    TryLogConversionSecondaryFailure("file", bmsFile.Path, ex);
                }
                finally
                {
                    try
                    {
                        writer?.DisposeAudioSourcesAfterUse();
                    }
                    catch (Exception disposeException)
                    {
                        fatalFileException = CombineFailures(
                            "Audio conversion cleanup failed after processing a chart.",
                            fatalFileException,
                            fileException,
                            disposeException);
                        TryLogConversionSecondaryFailure("writer disposal", bmsFile.Path, disposeException);
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }

                if (fatalFileException != null)
                {
                    ExceptionDispatchInfo.Capture(fatalFileException).Throw();
                }
                if (cancelledFile)
                {
                    break;
                }

                terminalFileResult = null;
                bool canContinue = CompleteFile(
                    bmsFile.Path,
                    fileException,
                    tryReleaseEncoder,
                    fileResult =>
                    {
                        terminalFileResult = fileResult;
                        reportFileCompleted?.Invoke(fileResult);
                    },
                    omittedAudioSourceCount);
                if (terminalFileResult?.Error is Exception fileFailure)
                {
                    primaryException ??= fileFailure;
                }
                if (!canContinue)
                {
                    primaryException ??= fileException
                        ?? new InvalidOperationException(
                            "Audio conversion stopped because encoder cleanup could not be confirmed.");
                    break;
                }
            }
        }
        catch (Exception exception)
        {
            // 先行譜面の通常失敗は結果へ記録済みです。バッチを中断する現在の例外を
            // session cleanupのprimaryとして渡し、cleanup失敗時にも原因連鎖へ残します。
            primaryException = exception;
            throw;
        }
        finally
        {
            ReleaseConversionSession(
                ownedSession,
                primaryException,
                ref operation,
                operationEntered,
                currentFileName);
        }
    }

    /// <summary>encoderをsessionより先に解放し、失敗時はsession所有権を保持します。</summary>
    internal void ReleaseConversionSession(BassAudioSession ownedSession, Exception primaryException)
    {
        BassAudioOperationLease operation = default;
        ReleaseConversionSession(
            ownedSession,
            primaryException,
            ref operation,
            operationEntered: false,
            fileName: null);
    }

    /// <summary>音声処理operationを先に閉じ、file名付きで変換資源の解放を試みます。</summary>
    /// <param name="ownedSession">変換処理が取得した音源sessionです。</param>
    /// <param name="primaryException">ファイル処理で発生した主原因です。</param>
    /// <param name="operation">解放前に保持している音声処理operationです。</param>
    /// <param name="operationEntered">operationを取得している場合はtrueです。</param>
    /// <param name="fileName">最後に処理した譜面のpathです。</param>
    internal void ReleaseConversionSession(
        BassAudioSession ownedSession,
        Exception primaryException,
        ref BassAudioOperationLease operation,
        bool operationEntered,
        string fileName = null)
    {
        var cleanupFailures = new List<Exception>();
        try
        {
            if (ownedSession != null && sessionLease.Session == null)
            {
                sessionLease.Attach(ownedSession);
            }
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        bool encoderReleased = false;
        try
        {
            encoderReleased = tryReleaseEncoder();
            if (!encoderReleased)
            {
                cleanupFailures.Add(new InvalidOperationException(
                    "Audio conversion encoder cleanup failed; retaining the native audio session for retry."));
            }
        }
        catch (Exception exception)
        {
            cleanupFailures.Add(exception);
        }

        if (encoderReleased && cleanupFailures.Count == 0 && operationEntered)
        {
            try
            {
                operationEntered = false;
                operation.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (encoderReleased && cleanupFailures.Count == 0)
        {
            try
            {
                if (!sessionLease.TryRelease(releaseSession))
                {
                    cleanupFailures.Add(new InvalidOperationException(
                        "Audio conversion completed without confirming native cleanup."));
                }
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (operationEntered)
        {
            try
            {
                operationEntered = false;
                operation.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }
        }

        if (cleanupFailures.Count > 0)
        {
            foreach (Exception cleanupFailure in cleanupFailures)
            {
                TryLogSessionCleanupFailure(cleanupFailure, primaryException, fileName);
            }
            var causes = new List<Exception>();
            if (primaryException != null)
            {
                causes.Add(primaryException);
            }
            causes.AddRange(cleanupFailures);
            throw new AudioSourceFatalException(
                "Audio conversion cleanup could not be confirmed.",
                new AggregateException(causes));
        }
    }

    private static void TryLogSessionCleanupFailure(
        Exception exception,
        Exception primaryException,
        string fileName)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor)).Warn(
                "Audio conversion session cleanup failed"
                + (string.IsNullOrWhiteSpace(fileName) ? string.Empty : " for " + fileName)
                + " while preserving the primary error: " + primaryException
                + " cleanup=" + exception);
        }
        catch
        {
            // Diagnostics must not replace the conversion exception.
        }
    }

    private static void TryLogConversionSecondaryFailure(string stage, string fileName, Exception exception)
    {
        try
        {
            NLogWrapper.GetLogger(nameof(BassSelectedChartAudioConversionExecutor)).Warn(
                "Audio conversion " + stage + " failed for " + fileName + ": " + exception);
        }
        catch
        {
            // Diagnostics must not replace progress or worker terminal behavior.
        }
    }

    private static bool IsFatalConversionFailure(Exception exception)
    {
        if (exception is AudioSourceFatalException
            or BassAudioPlaybackException
            or OutOfMemoryException
            or DllNotFoundException
            or BadImageFormatException
            or EntryPointNotFoundException
            or TypeLoadException)
        {
            return true;
        }
        return exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(IsFatalConversionFailure);
    }

    private static Exception CombineFailures(string message, params Exception[] exceptions)
    {
        Exception[] causes = exceptions.Where(exception => exception != null).ToArray();
        return new AudioSourceFatalException(
            message,
            causes.Length == 1 ? causes[0] : new AggregateException(causes));
    }

    /// <summary>
    /// ファイル結果を通知する前にencoderの解放を確認し、失敗時は主原因を保ってバッチを停止します。
    /// </summary>
    internal static bool CompleteFile(
        string fileName,
        Exception fileException,
        Func<bool> tryReleaseEncoder,
        Action<SelectedChartAudioConversionFileResult> reportFileCompleted,
        int omittedAudioSourceCount = 0)
    {
        ArgumentNullException.ThrowIfNull(tryReleaseEncoder);

        Exception cleanupException = null;
        try
        {
            if (!tryReleaseEncoder())
            {
                cleanupException = new InvalidOperationException(
                    "Audio conversion encoder cleanup failed; stopping before the next file.");
            }
        }
        catch (Exception exception)
        {
            cleanupException = exception;
        }

        if (cleanupException != null)
        {
            try
            {
                reportFileCompleted?.Invoke(new SelectedChartAudioConversionFileResult(
                    fileName,
                    fileException ?? cleanupException,
                    omittedAudioSourceCount));
            }
            catch (Exception reportException)
            {
                TryLogConversionSecondaryFailure("file completion report", fileName, reportException);
            }

            TryLogConversionSecondaryFailure("encoder cleanup", fileName, cleanupException);
            return false;
        }

        reportFileCompleted?.Invoke(new SelectedChartAudioConversionFileResult(
            fileName,
            fileException,
            omittedAudioSourceCount));
        return true;
    }
}
