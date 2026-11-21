using System.ComponentModel;
using System.Windows;
using BeMusicSeeker.Models;
using BeMusicSeeker.TestSupport.Files;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views.Dialogs;
using Parago.Windows;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;

/// <summary>複数の実行区分で同じ準備・観測を共有します。</summary>
internal static class SelectedChartAudioConversionWorkflowOwnerTestSupport
{
    /// <summary>各ケースが渡す依存を使って検証対象の管理主体を構成し、既存の通知接続を維持します。</summary>
    /// <param name="isEncoderAvailable">形式と検索先の可用性判定。省略時は常に利用可能です。</param>
    /// <param name="operationAdmission">共有する受付。省略時は専用の受付を作ります。</param>
    /// <param name="settingsProvider">設定提供値。指定時は形式・表示名・検索先の個別指定より優先します。</param>
    internal static SelectedChartAudioConversionWorkflowOwner CreateOwner(
        RecordingDialogService dialogs,
        RecordingPlayback playback,
        ISelectedChartAudioConversionExecutor executor,
        EventLog events,
        Func<EncoderType, string, bool>? isEncoderAvailable = null,
        ChartFileOperationSynchronizer? operationAdmission = null,
        EncoderType encoder = EncoderType.MP3_LAME,
        string encoderDisplayName = "MP3 LAME",
        string encoderExeDirectory = "",
        Func<SelectedChartAudioConversionSettingsSnapshot>? settingsProvider = null,
        float encoderAmplifier = 1f,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
    {
        return new SelectedChartAudioConversionWorkflowOwner(
            settingsProvider ?? (() => CreateSettings(
                encoder,
                encoderDisplayName,
                encoderExeDirectory,
                encoderAmplifier,
                sampleRateConversionQuality)),
            isEncoderAvailable ?? ((_, _) => true),
            playback,
            dialogs,
            executor,
            operationAdmission ?? new ChartFileOperationSynchronizer());
    }

    /// <summary>形式・表示名・検索先以外は共通の値を持つ設定スナップショットを作ります。</summary>
    internal static SelectedChartAudioConversionSettingsSnapshot CreateSettings(
        EncoderType encoder = EncoderType.MP3_LAME,
        string encoderDisplayName = "MP3 LAME",
        string encoderExeDirectory = "",
        float encoderAmplifier = 1f,
        int sampleRateConversionQuality = AudioResamplingQuality.Default)
    {
        return new SelectedChartAudioConversionSettingsSnapshot(
            encoder,
            SampleRate.SAMPLE_RATE_44100Hz,
            SampleFormat.SAMPLE_INT_16BIT,
            AudioNormalization.None,
            0.8f,
            encoderExeDirectory,
            encoderAmplifier,
            "%TITLE%",
            encoderDisplayName,
            "44100Hz",
            "16bit",
            sampleRateConversionQuality);
    }

    /// <summary>指定された譜面と操作条件を、そのまま検証対象へ渡す操作対象にまとめます。</summary>
    internal static ChartOperationTarget CreateTarget(string path, ChartOperationCapabilities capabilities)
    {
        return new ChartOperationTarget(
            CreateChart(path),
            null,
            ChartOperationSourceScope.Library,
            isOwned: true,
            isPending: false,
            isPlaylistMissing: false,
            capabilities);
    }

    /// <summary>指定された識別・配置条件から、各ケースが観測する譜面値を作ります。</summary>
    internal static ChartFile CreateChart(string path)
    {
        bool bmson = string.Equals(Path.GetExtension(path), ".bmson", StringComparison.OrdinalIgnoreCase);
        return new ChartFile(bmson ? ChartFileKind.Bmson : ChartFileKind.Bms, path, "hash-" + Path.GetFileNameWithoutExtension(path), null, "Title", "Title", "Artist", "Genre", "Folder", string.Empty, string.Empty, null, null, null);
    }

    /// <summary>呼出元が所有するWorkspace内に入力rootを作り、ケース終端まで利用できるパスを返します。</summary>
    internal static string CreateRoot(TestWorkspace workspace)
    {
        string root = Path.Combine(
            workspace.RootDirectory,
            "SelectedChartAudioConversionWorkflowOwnerTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>ケース所有rootへ検証用の小さいBMS入力を作ります。</summary>
    internal static string CreateChartFile(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        File.WriteAllText(path, "#TITLE:Test;\n");
        return path;
    }

    /// <summary>ケースが終了した専用入力rootだけを回収します。</summary>
    internal static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingPlayback : ISelectedChartAudioConversionPlaybackPort
    {
        private readonly EventLog events;

        internal RecordingPlayback(EventLog events)
        {
            this.events = events;
        }

        internal int StopCalls { get; private set; }

        internal Action? StopAction { get; set; }

        internal Func<Task>? StopCompletion { get; set; }

        public Task StopPlayback()
        {
            StopCalls++;
            events.Add("playback");
            StopAction?.Invoke();
            return StopCompletion?.Invoke() ?? Task.CompletedTask;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class RecordingDialogService : IUiDialogService
    {
        private readonly EventLog events;

        internal RecordingDialogService(EventLog events)
        {
            this.events = events;
        }

        internal UiFolderPickerResult FolderResult { get; set; } = new(UiDialogStatus.CancelledByUser);

        internal UiProgressResult ProgressResult { get; set; } = new(UiDialogStatus.Accepted);

        internal UiDialogResult ConfirmResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal Func<Task<UiDialogResult>>? ConfirmHandler { get; set; }

        internal UiConfirmationRequest? ConfirmRequest { get; private set; }

        internal int ConfirmCalls { get; private set; }

        internal UiDialogResult MessageResult { get; set; } = UiDialogResult.FromMessageBoxResult(MessageBoxResult.OK);

        internal string ProgressLabel { get; private set; } = null!;

        internal int MessageCalls { get; private set; }

        internal Exception? MessageFailure { get; set; }

        internal string LastMessage { get; private set; } = string.Empty;

        /// <summary>案内として要求された内容を、要求の順に保持します。</summary>
        internal List<UiMessageRequest> Messages { get; } = [];

        /// <summary>案内の要求を受けた時点で呼ぶ通知です。要求の時点の状態（受付の解放など）を観測するために使います。</summary>
        internal Action<UiMessageRequest>? MessageObserver { get; set; }

        /// <summary>直近の進捗画面の要求です。題名・ラベル・所有者を観測するために使います。</summary>
        internal UiProgressRequest? ProgressRequest { get; private set; }

        private DoWorkEventArgs? progressArguments;

        /// <summary>進捗画面の操作が取消を実際に検知した（取消の確認が取消として観測された）かです。</summary>
        internal bool ProgressCancellationObserved => Volatile.Read(ref progressArguments)?.Cancel == true;

        internal int PickerCalls { get; private set; }

        internal Func<Task<UiFolderPickerResult>>? FolderPickerHandler { get; set; }

        /// <summary>
        /// 進捗画面を表示できたかを表します。falseでは画面が操作を呼ばず、<see cref="ProgressResult"/> をそのまま返します。
        /// </summary>
        internal bool ProgressDisplayed { get; set; } = true;

        /// <summary>
        /// 開始済みの操作のTaskを受け取り、画面の結果を返す処理です。指定時は操作の終端を待たず、この処理の結果を返します。
        /// 未指定なら操作の終端を待ち、<see cref="ProgressResult"/> を返します。
        /// </summary>
        internal Func<Task, Task<UiProgressResult>>? ProgressHandler { get; set; }

        /// <summary>操作が進捗画面へ報告した割合と文言を、報告順に保持します。</summary>
        internal IReadOnlyList<(int Percent, string Message)> ProgressReports
        {
            get { lock (progressReports) { return progressReports.ToArray(); } }
        }

        /// <summary>報告を記録した直後に呼ぶ通知です。段階の表示が届いたことを待つために使います。</summary>
        internal Action<int, string>? ProgressReported { get; set; }

        private readonly List<(int Percent, string Message)> progressReports = [];

        /// <summary>
        /// 操作を呼ぶ直前に、利用者が取消ボタンを押した状態にします。操作の開始前の取消の要求を、時機に依存せず確定的に作るために使います。
        /// </summary>
        internal bool CancelProgressBeforeOperation { get; set; }

        private BackgroundWorker? progressWorker;

        /// <summary>利用者が進捗画面の取消ボタンを押した状態にします。次の報告の取消確認で取消として観測されます。</summary>
        internal void RequestProgressCancellation()
        {
            (Volatile.Read(ref progressWorker) ?? throw new InvalidOperationException("The progress operation has not started."))
                .CancelAsync();
        }

        public Task<UiDialogResult> ShowMessageAsync(UiMessageRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("message");
            MessageCalls++;
            LastMessage = request.MessageBoxText;
            Messages.Add(request);
            MessageObserver?.Invoke(request);
            if (MessageFailure != null) { throw MessageFailure; }
            return Task.FromResult(MessageResult);
        }

        public Task<UiDialogResult> ConfirmAsync(UiConfirmationRequest request, CancellationToken cancellationToken = default)
        {
            events.Add("confirm");
            ConfirmCalls++;
            ConfirmRequest = request;
            return ConfirmHandler?.Invoke() ?? Task.FromResult(ConfirmResult);
        }

        public Task<UiWindowDialogResult<TResult>> ShowWindowAsync<TWindow, TResult>(
            UiWindowDialogRequest<TWindow, TResult> request,
            CancellationToken cancellationToken = default)
            where TWindow : Window => throw new NotSupportedException();

        public Task<UiFilePickerResult> PickFileAsync(UiFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<UiFolderPickerResult> PickFolderAsync(
            UiFolderPickerRequest request,
            CancellationToken cancellationToken = default)
        {
            events.Add("picker");
            PickerCalls++;
            return FolderPickerHandler?.Invoke() ?? Task.FromResult(FolderResult);
        }

        public Task<UiSaveFilePickerResult> PickSaveFileAsync(UiSaveFilePickerRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async Task<UiProgressResult> RunWithProgressAsync(
            UiProgressRequest request,
            Func<UiProgressContext, Task> operation,
            CancellationToken cancellationToken = default)
        {
            ProgressLabel = request.Label;
            ProgressRequest = request;
            events.Add("progress");
            if (!ProgressDisplayed)
            {
                return ProgressResult;
            }
            using var worker = new BackgroundWorker { WorkerReportsProgress = true, WorkerSupportsCancellation = true };
            Volatile.Write(ref progressWorker, worker);
            worker.ProgressChanged += (_, e) =>
            {
                (int ProgressPercentage, string) report = (e.ProgressPercentage, e.UserState?.ToString() ?? string.Empty);
                lock (progressReports) { progressReports.Add(report); }
                ProgressReported?.Invoke(report.ProgressPercentage, report.Item2);
            };
            var arguments = new DoWorkEventArgs(null);
            Volatile.Write(ref progressArguments, arguments);
            if (CancelProgressBeforeOperation) { worker.CancelAsync(); }
            Task operationTask = operation(new UiProgressContext(new ProgressDialogContext(worker, arguments)));
            if (ProgressHandler != null)
            {
                return await ProgressHandler(operationTask);
            }
            await operationTask;
            return ProgressResult;
        }
    }

    /// <summary>各実行区分が同じ準備と観測を使うための共通補助です。</summary>
    internal sealed class EventLog
    {
        private readonly List<string> entries = [];

        internal void Add(string value)
        {
            lock (entries)
            {
                entries.Add(value);
            }
        }

        internal int IndexOf(string value)
        {
            lock (entries)
            {
                return entries.IndexOf(value);
            }
        }

        internal string Join(string separator)
        {
            lock (entries)
            {
                return string.Join(separator, entries);
            }
        }
    }

}
