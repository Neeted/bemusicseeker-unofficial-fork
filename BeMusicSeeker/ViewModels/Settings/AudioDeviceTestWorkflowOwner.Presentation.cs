using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.ViewModels;

/// <summary>音声下書きの編集・照会・テスト表示を、同じ機能の処理所有者に集約します。</summary>
internal sealed partial class AudioDeviceTestWorkflowOwner
{
    private string audioDeviceTestStatusMessage;

    private string audioDeviceTestDiagnosticMessage;

    private Task audioDeviceTestCompletionTask = Task.CompletedTask;

    private Task audioDeviceListCompletionTask = Task.CompletedTask;

    private Task audioDeviceCapabilityCompletionTask = Task.CompletedTask;

    private bool hasVisitedAudioPage;

    private bool isAudioDeviceListRefreshInProgress;

    private bool audioDeviceListRefreshFailed;

    private AudioDeviceCapabilityResult audioDeviceCapabilityResult;

    private AudioOutputSelection? audioDeviceCapabilitySelection;

    private string audioDeviceCapabilityStatusMessage;

    private string audioDeviceCapabilityDiagnosticMessage;

    private long audioDeviceCapabilityGeneration;

    private bool isAudioSettingsPageVisible;

    private bool isAudioDeviceCapabilityQueryInProgress;

    private bool isAudioDeviceCapabilityQueryPending;

    private bool isAudioDeviceCapabilityQueryBlocked;

    private bool audioDeviceCapabilityQueryBlockedBySave;

    private bool audioSessionReleaseObservedDuringCapabilityQuery;

    private string audioOutputSelectionResetMessage;

    private AudioOutputSelection audioOutputSelectionDraft;

    private SampleRate candidatePlayerSampleRate;
    private SampleFormat candidatePlayerFormat;
    private float candidatePlayerBufferSize;
    private bool candidatePlayerWASAPIParam;
    private bool outputSettingsSaveFailed;



    private List<AudioDeviceInfo> playerDeviceNames;

    private Action<Exception> reportSettingsFailure;
    private Func<Settings> getSettings;
    private Settings ApplicationSettings => getSettings();
    private IAudioDeviceCatalog audioDeviceCatalog;
    private bool isPresentationActive;
    private bool IsEditCompletionInProgress;

    /// <summary>保存担当から下書きへのアクセスと画面の依存を一度だけ接続します。</summary>
    internal void ConfigurePresentation(Func<Settings> getSettings, IAudioDeviceCatalog catalog, Action<Exception> reportSettingsFailure = null)
    {
        this.getSettings = getSettings;
        this.reportSettingsFailure = reportSettingsFailure;
        audioDeviceCatalog = catalog;
        ResetOutputCandidatesToConfirmedSettings();
        OperationReleased += AudioWorkflowOperationReleased;
        Ribbit.Media.BassAudioPlayer.AudioSessionReleased += AudioOutputSessionReleased;
    }

    /// <summary>表示開始・取消時に音声出力の候補だけを確定値へ戻します。</summary>
    internal void ResetOutputCandidatesToConfirmedSettings()
    {
        outputSettingsSaveFailed = false;
        candidatePlayerSampleRate = ApplicationSettings.PlayerSampleRate;
        candidatePlayerFormat = ApplicationSettings.PlayerFormat;
        candidatePlayerBufferSize = ApplicationSettings.PlayerBufferSize;
        candidatePlayerWASAPIParam = ApplicationSettings.PlayerWASAPIParam;
        RaisePropertyChanged(nameof(PlayerResamplingQuality));
        RaisePropertyChanged(nameof(PlayerMixerThreadCount));
        RaisePropertyChanged(nameof(PlayerSampleRate));
        RaisePropertyChanged(nameof(PlayerFormat));
        RaisePropertyChanged(nameof(PlayerBufferSize));
        RaisePropertyChanged(nameof(PlayerWASAPIParam));
    }

    private bool SaveIndependentAudioSetting(string name, int value)
    {
        try { ApplicationSettings.Commit((name, value)); return true; }
        catch (Exception failure)
        {
            RaisePropertyChanged(name);
            if (reportSettingsFailure != null) { reportSettingsFailure(failure); }
            else { Ribbit.Logging.NLogWrapper.FileLogger?.Warn(failure, "Audio setting save failed"); }
            return false;
        }
    }

    /// <summary>選択UIで成立した音声の組だけを保存対象にします。保存済みの利用不能値は編集されるまで保持します。</summary>
    internal bool CanCommitOutputSettings => AudioDriverPolicy.IndexOf(audioOutputSelectionDraft.Backend) >= 0
        && (audioOutputSelectionDraft.IsDefault || SelectedPlayerDevice is { IsAvailable: true }) && float.IsFinite(candidatePlayerBufferSize) && candidatePlayerBufferSize >= 0;

    /// <summary>現在の音声候補が最新確定値と異なるかを示します。</summary>
    internal bool HasOutputSettingsChanges => AudioDriverPolicy.NormalizePersistedSelection(audioOutputSelectionDraft)
        != AudioDriverPolicy.NormalizePersistedSelection(new AudioOutputSelection(BassAudioMapping.FromBassDriver(ApplicationSettings.PlayerDriver),
            ApplicationSettings.PlayerDevice, ApplicationSettings.PlayerDeviceName))
        || candidatePlayerSampleRate != ApplicationSettings.PlayerSampleRate || candidatePlayerFormat != ApplicationSettings.PlayerFormat
        || candidatePlayerBufferSize != ApplicationSettings.PlayerBufferSize || candidatePlayerWASAPIParam != ApplicationSettings.PlayerWASAPIParam;

    /// <summary>実際の保存失敗があり、同じ意味の確定値へ復帰していない場合だけ再試行を表示します。</summary>
    internal bool IsOutputSettingsSaveRetryRequired => outputSettingsSaveFailed && HasOutputSettingsChanges;

    /// <summary>音声の関連組だけを保存します。現再生のTaskやnative playerは切り替えません。</summary>
    internal void CommitOutputSettings()
    {
        if (!HasOutputSettingsChanges) { outputSettingsSaveFailed = false; return; }
        if (!CanCommitOutputSettings) { return; }
        AudioOutputSelection selection = AudioDriverPolicy.NormalizePersistedSelection(audioOutputSelectionDraft);
        try
        {
            ApplicationSettings.Commit(
                (nameof(Settings.PlayerDriver), BassAudioMapping.ToBassDriver(selection.Backend)),
                (nameof(Settings.PlayerDevice), selection.DeviceIdentity),
                (nameof(Settings.PlayerDeviceName), selection.DeviceName),
                (nameof(Settings.PlayerSampleRate), candidatePlayerSampleRate),
                (nameof(Settings.PlayerFormat), candidatePlayerFormat),
                (nameof(Settings.PlayerBufferSize), candidatePlayerBufferSize),
                (nameof(Settings.PlayerWASAPIParam), candidatePlayerWASAPIParam));
            outputSettingsSaveFailed = false;
        }
        catch { outputSettingsSaveFailed = true; throw; }
    }

    /// <summary>保存・取消後に受け渡す現在の出力先です。</summary>
    internal AudioOutputSelection OutputSelection { get => audioOutputSelectionDraft; set => audioOutputSelectionDraft = value; }

    /// <summary>保存受付中の照会保留を、保存処理の完了時だけ再開します。</summary>
    internal void SetSaving(bool saving)
    {
        IsEditCompletionInProgress = saving;
        RaiseAudioDeviceTestOperationPropertiesChanged();
        if (!saving && audioDeviceCapabilityQueryBlockedBySave)
        {
            audioDeviceCapabilityQueryBlockedBySave = false;
            isAudioDeviceCapabilityQueryBlocked = false;
            RunPendingAudioDeviceCapabilityQuery();
        }
    }

    /// <summary>画面の寿命と初回訪問を更新し、終了後の結果反映を切り離します。</summary>
    internal void SetPresentationActive(bool active)
    {
        bool opening = active && !isPresentationActive;
        isPresentationActive = active;
        if (!active)
        {
            InvalidateAudioDeviceCapabilities();
        }
        else if (opening)
        {
            hasVisitedAudioPage = false;
            AudioDeviceTestStatusMessage = null;
            AudioDeviceTestDiagnosticMessage = null;
            PresentAudioSettingsPage();
        }
    }

    /// <summary>一覧の取得中または直近の更新失敗を示す翻訳文を取得します。</summary>
    public string AudioDeviceListStatusMessage => isAudioDeviceListRefreshInProgress
        ? Resources.AudioDeviceListLoading
        : audioDeviceListRefreshFailed ? Resources.AudioDeviceListRefreshFailed : null;

    private void PresentAudioSettingsPage()
    {
        if (!isPresentationActive || !isAudioSettingsPageVisible)
        {
            return;
        }

        if (!hasVisitedAudioPage)
        {
            hasVisitedAudioPage = true;
            // 閉じた画面の列挙が残っていれば同じTaskを使い、追加取得を予約しない。
            if (audioDeviceListCompletionTask.IsCompleted)
            {
                StartAudioDeviceListRefresh();
            }
        }
        RaisePropertyChanged(nameof(AudioDeviceListStatusMessage));
        RaiseAudioDeviceTestOperationPropertiesChanged();
        if (audioDeviceListCompletionTask.IsCompleted)
        {
            RebuildAudioDeviceSelection();
            RequestAudioDeviceCapabilityQuery();
        }
    }

    private void StartAudioDeviceListRefresh()
    {
        Task priorQuery = audioDeviceCapabilityCompletionTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 失効通知やworker開始より先に登録し、再入・終了待機が実処理を取りこぼさないようにする。
        audioDeviceListCompletionTask = completion.Task;
        isAudioDeviceListRefreshInProgress = true;
        audioDeviceListRefreshFailed = false;
        InvalidateAudioDeviceCapabilities();
        RefreshAudioDeviceListAsync(priorQuery, completion).ObserveFault("settings audio device list refresh");
    }

    private async Task RefreshAudioDeviceListAsync(Task priorQuery, TaskCompletionSource completion)
    {
        try
        {
            // 旧照会の取消要求後もnativeの実終端を待つ。列挙用leaseは各enumeratorが所有する。
            await priorQuery;
            await Task.Run(() =>
            {
                var execution = Stopwatch.StartNew();
                try
                {
                    audioDeviceCatalog.Refresh();
                }
                finally
                {
                    LogAudioDeviceListRefresh(execution);
                }
            });
        }
        catch (Exception exception)
        {
            audioDeviceListRefreshFailed = true;
            try
            {
                Ribbit.Logging.NLogWrapper.FileLogger?.Warn(exception,
                    "Audio device list refresh failed; retaining current selection and last good devices.");
            }
            catch
            {
                // 診断の失敗は一覧状態と保存・閉鎖を変更しない。
            }
        }
        finally
        {
            isAudioDeviceListRefreshInProgress = false;
            try
            {
                if (isPresentationActive && isAudioSettingsPageVisible)
                {
                    RebuildAudioDeviceSelection();
                    RaisePropertyChanged(nameof(AudioDeviceListStatusMessage));
                }
            }
            finally
            {
                completion.TrySetResult();
            }
        }
        if (isPresentationActive && isAudioSettingsPageVisible)
        {
            RaiseAudioDeviceTestOperationPropertiesChanged();
            RequestAudioDeviceCapabilityQuery();
        }
    }

    private static void LogAudioDeviceListRefresh(Stopwatch execution)
    {
        try
        {
            Ribbit.Logging.NLogWrapper.FileLogger?.Info(
                "settings_audio_device_list_worker elapsedMs=" + execution.ElapsedMilliseconds);
        }
        catch
        {
            // 性能記録は列挙結果を変更しない。
        }
    }

    private void RebuildAudioDeviceSelection()
    {
        // 開始時の選択を復元せず、完了時点のdraftから候補を作る。明示編集のリセット理由も保持する。
        playerDeviceNames = BuildPlayerDeviceNames(audioOutputSelectionDraft.Backend);
        RefreshSelectionProperties();
    }

    /// <summary>保存済み選択の復元を明示編集と分け、Autoリセットを起こしません。</summary>
    internal void RestoreSelection()
    {
        audioOutputSelectionResetMessage = null;
        RebuildAudioDeviceSelection();
    }

    /// <summary>保存・取消後に下書きの表示だけを再通知します。</summary>
    internal void RefreshSelectionProperties()
    {
        RaisePlayerDriverStateProperties();
        RaisePropertyChanged(nameof(UnavailablePlayerDriverDescription));
        RaisePropertyChanged(nameof(PlayerDeviceNames));
        RaisePropertyChanged(nameof(PlayerDevice));
        RaisePropertyChanged(nameof(SelectedPlayerDevice));
        RaiseAudioDeviceCapabilityPropertiesChanged();
    }

    /// <summary>画面購読を終了し、受理済みの処理は完了まで所有します。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SetPresentationActive(false);
            OperationReleased -= AudioWorkflowOperationReleased;
            Ribbit.Media.BassAudioPlayer.AudioSessionReleased -= AudioOutputSessionReleased;
        }
        base.Dispose(disposing);
    }
    /// <summary>
    /// 機器テストが準備開始から解放完了までの間にあるかを取得します。
    /// </summary>
    public bool IsAudioDeviceTestInProgress => IsTestRunning == true;

    /// <summary>
    /// 現在の編集状態で機器テストを受け付けられるかを取得します。
    /// </summary>
    public bool IsAudioDeviceTestAvailable => !IsEditCompletionInProgress
        && audioDeviceListCompletionTask.IsCompleted
        && IsRunning != true;

    /// <summary>機器テストボタンの開始を示す翻訳文を取得します。</summary>
    public string AudioDeviceTestButtonContent => BeMusicSeeker.Properties.Resources.AudioDeviceTestStartButton;

    /// <summary>音声の出力条件を編集中のテスト中に変更できるかを取得します。</summary>
    public bool IsAudioOutputSelectionEnabled => !IsEditCompletionInProgress && !IsAudioDeviceTestInProgress;

    /// <summary>出力先変更によるAutoへのリセット理由を取得します。</summary>
    public string AudioOutputSelectionResetMessage => audioOutputSelectionResetMessage;

    /// <summary>最新の機器テストの結果を示す翻訳文を取得します。</summary>
    public string AudioDeviceTestStatusMessage
    {
        get => audioDeviceTestStatusMessage;
        private set
        {
            if (string.Equals(audioDeviceTestStatusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            audioDeviceTestStatusMessage = value;
            RaisePropertyChanged(nameof(AudioDeviceTestStatusMessage));
        }
    }

    /// <summary>最新のテスト結果に展開可能な技術診断があるかを取得します。</summary>
    public bool HasAudioDeviceTestDiagnostics => !string.IsNullOrWhiteSpace(AudioDeviceTestDiagnosticMessage);

    /// <summary>最新のデバイステストが保持した技術診断を取得します。</summary>
    public string AudioDeviceTestDiagnosticMessage
    {
        get => audioDeviceTestDiagnosticMessage;
        private set
        {
            if (string.Equals(audioDeviceTestDiagnosticMessage, value, StringComparison.Ordinal))
            {
                return;
            }
            audioDeviceTestDiagnosticMessage = value;
            RaisePropertyChanged(nameof(AudioDeviceTestDiagnosticMessage));
            RaisePropertyChanged(nameof(HasAudioDeviceTestDiagnostics));
        }
    }

    /// <summary>明示的な能力照会の状態を取得します。</summary>
    public string AudioDeviceCapabilityStatusMessage
    {
        get => audioDeviceCapabilityStatusMessage;
        private set
        {
            if (string.Equals(audioDeviceCapabilityStatusMessage, value, StringComparison.Ordinal))
            {
                return;
            }

            audioDeviceCapabilityStatusMessage = value;
            RaisePropertyChanged(nameof(AudioDeviceCapabilityStatusMessage));
        }
    }

    /// <summary>最新の照会結果に展開可能な技術診断があるかを取得します。</summary>
    public bool HasAudioDeviceCapabilityDiagnostics =>
        !string.IsNullOrWhiteSpace(AudioDeviceCapabilityDiagnosticMessage);

    /// <summary>最新の能力照会が保持したネイティブ診断を取得します。</summary>
    public string AudioDeviceCapabilityDiagnosticMessage
    {
        get => audioDeviceCapabilityDiagnosticMessage;
        private set
        {
            if (string.Equals(audioDeviceCapabilityDiagnosticMessage, value, StringComparison.Ordinal))
            {
                return;
            }
            audioDeviceCapabilityDiagnosticMessage = value;
            RaisePropertyChanged(nameof(AudioDeviceCapabilityDiagnosticMessage));
            RaisePropertyChanged(nameof(HasAudioDeviceCapabilityDiagnostics));
        }
    }

    /// <summary>能力照会で読み取ったエンドポイント形式を取得します。</summary>
    public string AudioDeviceCapabilityFormatDescription =>
        FormatAudioDeviceCapabilityEndpoint(audioDeviceCapabilityResult);

    /// <summary>選択機器から読み取った共有モードのサンプルレートを取得します。</summary>
    public string AudioDeviceCapabilityRateDescription =>
        FormatAudioDeviceCapabilityRate(audioDeviceCapabilityResult);


    /// <summary>選択機器が受理した再生レートと保存済みの選択肢を取得します。</summary>
    public ReadOnlyDictionary<SampleRate, string> PlayerSampleRateNames => CreatePlayerSampleRateNames();

    /// <summary>一覧取得・能力照会・テストの実処理と表示終端を待つTaskを取得します。終了時は先に表示を失効させます。</summary>
    internal Task AudioSettingsCompletionTask => Task.WhenAll(
        audioDeviceListCompletionTask, audioDeviceCapabilityCompletionTask, audioDeviceTestCompletionTask);

    /// <summary>再生レート欄が機器情報の表示専用かどうかを取得します。</summary>
    public bool IsPlayerSampleRateReadOnly => !IsPlayerSampleRateSelectionEnabled;

    /// <summary>再生形式欄が機器情報の表示専用かどうかを取得します。</summary>
    public bool IsPlayerFormatReadOnly => !IsPlayerFormatSelectionEnabled;

    private ReadOnlyDictionary<SampleRate, string> CreatePlayerSampleRateNames()
    {
        var names = new Dictionary<SampleRate, string>
        {
            [SampleRate.AUTO] = BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityAuto
        };
        AudioDeviceCapabilityResult result = GetCurrentAudioDeviceCapabilityResult();
        if (result?.Status == AudioDeviceCapabilityStatus.Available)
        {
            foreach (SampleRate rate in result.SupportedRates.Distinct().OrderBy(rate => (int)rate))
            {
                names[rate] = string.Format(
                    CultureInfo.CurrentCulture,
                    BeMusicSeeker.Properties.Resources.AudioSampleRateOptionFormat,
                    (int)rate);
            }
        }

        SampleRate selectedRate = PlayerSampleRate;
        if (selectedRate != SampleRate.AUTO && !names.ContainsKey(selectedRate))
        {
            names[selectedRate] = FormatUnconfirmedCapabilityChoice(
                string.Format(
                    CultureInfo.CurrentCulture,
                    BeMusicSeeker.Properties.Resources.AudioSampleRateOptionFormat,
                    (int)selectedRate),
                result);
        }
        return new ReadOnlyDictionary<SampleRate, string>(names);
    }

    /// <summary>選択レートでWASAPI排他機器が受理した形式と保存済みの選択肢を取得します。</summary>
    public ReadOnlyDictionary<SampleFormat, string> PlayerFormatNames => CreatePlayerFormatNames();

    private ReadOnlyDictionary<SampleFormat, string> CreatePlayerFormatNames()
    {
        var names = new Dictionary<SampleFormat, string>
        {
            [SampleFormat.AUTO] = BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityAuto
        };
        AudioDeviceCapabilityResult result = GetCurrentAudioDeviceCapabilityResult();
        if (result?.Status == AudioDeviceCapabilityStatus.Available
            && audioOutputSelectionDraft.Backend == AudioDriver.WasapiExclusive)
        {
            foreach (AudioDeviceFormatCapability capability in result.FormatCapabilities
                         .Where(capability => (PlayerSampleRate == SampleRate.AUTO || capability.Rate == PlayerSampleRate) && capability.IsSupported)
                         .DistinctBy(capability => capability.Format))
            {
                names[capability.Format] = FormatSampleFormat(capability.Format);
            }
        }

        SampleFormat selectedFormat = PlayerFormat;
        if (selectedFormat != SampleFormat.AUTO && !names.ContainsKey(selectedFormat))
        {
            names[selectedFormat] = FormatUnconfirmedCapabilityChoice(
                FormatSampleFormat(selectedFormat),
                result);
        }
        return new ReadOnlyDictionary<SampleFormat, string>(names);
    }

    private AudioDeviceCapabilityResult GetCurrentAudioDeviceCapabilityResult()
    {
        return audioDeviceCapabilityResult != null
            && audioDeviceCapabilitySelection == audioOutputSelectionDraft
            ? audioDeviceCapabilityResult
            : null;
    }

    private static string FormatUnconfirmedCapabilityChoice(
        string choice,
        AudioDeviceCapabilityResult result)
    {
        string qualifier = result?.Status switch
        {
            AudioDeviceCapabilityStatus.Available => BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityUnsupportedChoice,
            AudioDeviceCapabilityStatus.Unsupported => BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityUnsupportedChoice,
            _ => BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityUnqueriedChoice
        };
        return string.Format(
            CultureInfo.CurrentCulture,
            BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityUnavailableChoiceFormat,
            choice,
            qualifier);
    }

    private static string FormatSampleFormat(SampleFormat format) => format switch
    {
        SampleFormat.SAMPLE_INT_8BIT => BeMusicSeeker.Properties.Resources.AudioSampleFormat8Bit,
        SampleFormat.SAMPLE_INT_16BIT => BeMusicSeeker.Properties.Resources.AudioSampleFormat16Bit,
        SampleFormat.SAMPLE_INT_24BIT => BeMusicSeeker.Properties.Resources.AudioSampleFormat24Bit,
        SampleFormat.SAMPLE_INT_32BIT => BeMusicSeeker.Properties.Resources.AudioSampleFormat32Bit,
        SampleFormat.SAMPLE_FLOAT_32BIT => BeMusicSeeker.Properties.Resources.AudioSampleFormatFloat32,
        _ => format.ToString()
    };


    /// <summary>選択可能な出力方式の順序に対応する翻訳名を取得します。</summary>
    public ReadOnlyObservableCollection<string> PlayerDriverNames
        => new(
        [.. AudioDriverPolicy.SelectableDrivers.Select(AudioDriverDisplayNames.Get)]);

    /// <summary>選択中の方式で再生レートを指定できるかどうかを取得します。</summary>
    public bool IsPlayerSampleRateSelectionEnabled
        => audioOutputSelectionDraft.Backend is AudioDriver.WasapiExclusive or AudioDriver.Asio;

    /// <summary>選択した方式でエンドポイント形式を指定できるかどうかを取得します。</summary>
    public bool IsPlayerFormatSelectionEnabled
        => audioOutputSelectionDraft.Backend == AudioDriver.WasapiExclusive;

    /// <summary>選択中の方式にイベント駆動の指定が適用されるかを取得します。</summary>
    public bool IsPlayerWasapiDriver
        => audioOutputSelectionDraft.Backend is AudioDriver.WasapiShared or AudioDriver.WasapiExclusive;

    /// <summary>選択中の方式にバッファサイズの指定が適用されるかを取得します。</summary>
    public bool IsPlayerBufferControlEnabled
        => audioOutputSelectionDraft.Backend is AudioDriver.WasapiExclusive or AudioDriver.Asio;

    /// <summary>保存された方式で可聴出力できない場合の説明を取得します。</summary>
    public string UnavailablePlayerDriverDescription => AudioDriverPolicy.IsSelectable(audioOutputSelectionDraft.Backend)
        ? null
        : string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceUnavailableFormat,
            audioOutputSelectionDraft.Backend);

    public int PlayerDriverIndex
    {
        get
        {
            AudioDriver draftDriver = audioOutputSelectionDraft.Backend;
            return AudioDriverPolicy.IndexOf(draftDriver);
        }
        set
        {
            if (value < 0 || value >= AudioDriverPolicy.SelectableDrivers.Count)
            {
                return;
            }
            AudioDriver driver = AudioDriverPolicy.SelectableDrivers[value];
            if (audioOutputSelectionDraft.Backend != driver)
            {
                audioOutputSelectionDraft = new AudioOutputSelection(driver, null, null);
                ResetAudioOutputConditions(
                    BeMusicSeeker.Properties.Resources.AudioOutputSelectionResetMessage);
                InvalidateAudioDeviceCapabilities();
                playerDeviceNames = BuildPlayerDeviceNames(driver);
                RaisePlayerDriverStateProperties();
                RaisePropertyChanged(nameof(UnavailablePlayerDriverDescription));
                RaisePropertyChanged(nameof(PlayerDeviceNames));
                RaisePropertyChanged(nameof(PlayerDevice));
                RaisePropertyChanged(nameof(SelectedPlayerDevice));
                InvalidateAudioDeviceTestResult();
                RequestAudioDeviceCapabilityQuery();
            }
        }
    }

    private void RaisePlayerDriverStateProperties()
    {
        RaisePropertyChanged(nameof(PlayerDriverNames));
        RaisePropertyChanged(nameof(PlayerDriverIndex));
        RaisePropertyChanged(nameof(IsPlayerFormatSelectionEnabled));
        RaisePropertyChanged(nameof(IsPlayerFormatReadOnly));
        RaisePropertyChanged(nameof(IsPlayerSampleRateSelectionEnabled));
        RaisePropertyChanged(nameof(IsPlayerSampleRateReadOnly));
        RaisePropertyChanged(nameof(IsPlayerWasapiDriver));
        RaisePropertyChanged(nameof(IsPlayerBufferControlEnabled));
    }

    public List<AudioDeviceInfo> PlayerDeviceNames
    {
        get
        {
            return playerDeviceNames ??= BuildPlayerDeviceNames(audioOutputSelectionDraft.Backend);
        }
        private set
        {
            playerDeviceNames = value;
        }
    }

    /// <summary>
    /// 下書きで選択した機器の保存識別子を取得または設定します。
    /// Defaultの選択意図はnull識別子で保持します。
    /// </summary>
    public string PlayerDevice
    {
        get
        {
            return ResolvePlayerDeviceDescriptor().Driver ?? audioOutputSelectionDraft.DeviceIdentity;
        }
        set
        {
            int selectedIndex = FindPlayerDeviceIndex(value);
            if (selectedIndex < 0)
            {
                return;
            }

            SelectedPlayerDevice = PlayerDeviceNames[selectedIndex];
        }
    }

    /// <summary>
    /// 設定画面で選択した機器を取得または設定します。
    /// WPFが一覧を差し替える間の一時的なnullは編集として扱いません。
    /// </summary>
    public AudioDeviceInfo? SelectedPlayerDevice
    {
        get
        {
            int selectedIndex = FindPlayerDeviceIndex(audioOutputSelectionDraft.DeviceIdentity);
            return selectedIndex < 0 ? null : PlayerDeviceNames[selectedIndex];
        }
        set
        {
            if (!value.HasValue)
            {
                return;
            }

            AudioDeviceInfo deviceDescriptor = value.Value;
            if (!deviceDescriptor.IsDefaultPlaceholder
                && string.IsNullOrWhiteSpace(deviceDescriptor.Driver))
            {
                return;
            }

            string nextDevice = deviceDescriptor.IsDefaultPlaceholder
                ? null
                : deviceDescriptor.Driver;
            string nextDeviceName = deviceDescriptor.IsDefaultPlaceholder
                ? null
                : deviceDescriptor.Name;
            var nextSelection = new AudioOutputSelection(
                audioOutputSelectionDraft.Backend,
                nextDevice,
                nextDeviceName);
            if (audioOutputSelectionDraft.Backend == nextSelection.Backend
                && string.Equals(
                    audioOutputSelectionDraft.DeviceIdentity,
                    nextSelection.DeviceIdentity,
                    StringComparison.Ordinal))
            {
                return;
            }

            audioOutputSelectionDraft = nextSelection;
            ResetAudioOutputConditions(
                BeMusicSeeker.Properties.Resources.AudioOutputSelectionResetMessage);
            InvalidateAudioDeviceCapabilities();
            RaisePropertyChanged(nameof(PlayerDevice));
            RaisePropertyChanged(nameof(SelectedPlayerDevice));
            RaisePlayerDriverStateProperties();
            InvalidateAudioDeviceTestResult();
            RequestAudioDeviceCapabilityQuery();
        }
    }

    private int FindPlayerDeviceIndex(string deviceIdentity)
    {
        if (string.IsNullOrWhiteSpace(deviceIdentity))
        {
            return PlayerDeviceNames.FindIndex(device => device.IsDefaultPlaceholder);
        }

        return PlayerDeviceNames.FindIndex(device =>
            !device.IsDefaultPlaceholder
            && string.Equals(device.Driver, deviceIdentity, StringComparison.Ordinal));
    }

    private AudioDeviceInfo ResolvePlayerDeviceDescriptor()
    {
        return PlayerDeviceNames.FirstOrDefault(d =>
            string.Equals(d.Driver, audioOutputSelectionDraft.DeviceIdentity, StringComparison.Ordinal));
    }

    private List<AudioDeviceInfo> BuildPlayerDeviceNames(AudioDriver driver)
    {
        List<AudioDeviceInfo> devices = AudioDriverPolicy.IsSelectable(driver)
            ? new List<AudioDeviceInfo>(audioDeviceCatalog.GetDevices(driver))
            : [];
        string savedIdentity = audioOutputSelectionDraft.DeviceIdentity;
        if (!string.IsNullOrWhiteSpace(savedIdentity)
            && !devices.Any(device => string.Equals(device.Driver, savedIdentity, StringComparison.Ordinal)))
        {
            devices.Add(new AudioDeviceInfo(
                string.IsNullOrWhiteSpace(audioOutputSelectionDraft.DeviceName)
                    ? savedIdentity
                    : audioOutputSelectionDraft.DeviceName,
                savedIdentity,
                -1,
                isDefaultPlaceholder: false,
                isNativeDefault: false,
                isAvailable: false));
        }
        return devices;
    }

    public SampleRate PlayerSampleRate
    {
        get
        {
            return candidatePlayerSampleRate;
        }
        set
        {
            if (candidatePlayerSampleRate != value)
            {
                candidatePlayerSampleRate = value;
                candidatePlayerFormat = SampleFormat.AUTO;
                audioOutputSelectionResetMessage =
                    BeMusicSeeker.Properties.Resources.AudioSampleRateSelectionResetMessage;
                RaisePropertyChanged(nameof(AudioOutputSelectionResetMessage));
                RaisePropertyChanged(nameof(PlayerSampleRate));
                RaisePropertyChanged(nameof(SelectedPlayerSampleRate));
                RaisePropertyChanged(nameof(PlayerFormat));
                RaisePropertyChanged(nameof(SelectedPlayerFormat));
                RaisePropertyChanged(nameof(PlayerFormatNames));
                InvalidateAudioDeviceTestResult();
            }
        }
    }

    /// <summary>再Binding時の一時的なnullを無視するサンプルレート選択値です。</summary>
    public SampleRate? SelectedPlayerSampleRate
    {
        get => PlayerSampleRate;
        set
        {
            if (value.HasValue)
            {
                PlayerSampleRate = value.Value;
            }
        }
    }

    public SampleFormat PlayerFormat
    {
        get
        {
            return candidatePlayerFormat;
        }
        set
        {
            if (candidatePlayerFormat != value)
            {
                candidatePlayerFormat = value;
                audioOutputSelectionResetMessage = null;
                RaisePropertyChanged(nameof(AudioOutputSelectionResetMessage));
                RaisePropertyChanged(nameof(PlayerFormat));
                RaisePropertyChanged(nameof(SelectedPlayerFormat));
                InvalidateAudioDeviceTestResult();
            }
        }
    }

    /// <summary>再Binding時の一時的なnullを無視する形式選択値です。</summary>
    public SampleFormat? SelectedPlayerFormat
    {
        get => PlayerFormat;
        set
        {
            if (value.HasValue)
            {
                PlayerFormat = value.Value;
            }
        }
    }

    public float PlayerBufferSize
    {
        get
        {
            return candidatePlayerBufferSize;
        }
        set
        {
            if (candidatePlayerBufferSize != value)
            {
                candidatePlayerBufferSize = value;
                RaisePropertyChanged("PlayerBufferSize");
                InvalidateAudioDeviceTestResult();
            }
        }
    }

    /// <summary>音声のサンプルレート変換品質を取得または設定します。</summary>
    public int PlayerResamplingQuality
    {
        get => ApplicationSettings.PlayerResamplingQuality;
        set
        {
            if (AudioResamplingQuality.IsValid(value) && ApplicationSettings.PlayerResamplingQuality != value)
            {
                if (!SaveIndependentAudioSetting(nameof(Settings.PlayerResamplingQuality), value)) { return; }
                RaisePropertyChanged(nameof(PlayerResamplingQuality));
                InvalidateAudioDeviceTestResult();
            }
        }
    }

    /// <summary>現在の言語でサンプルレート変換品質と対応するsinc点数の選択肢を取得します。</summary>
    public ReadOnlyDictionary<int, string> PlayerResamplingQualityNames =>
        new(new Dictionary<int, string>
        {
            [2] = FormatResamplingQualityOption(2),
            [3] = FormatResamplingQualityOption(3),
            [4] = FormatResamplingQualityOption(4),
            [5] = FormatResamplingQualityOption(5),
            [6] = FormatResamplingQualityOption(6)
        });

    private static string FormatResamplingQualityOption(int quality) =>
        string.Format(
            CultureInfo.CurrentCulture,
            Resources.AudioResamplingQualityOptionFormat,
            quality,
            AudioResamplingQuality.GetSincPointCount(quality));

    /// <summary>通常再生用SRCミキサーのnative thread数を取得または設定します。</summary>
    public int PlayerMixerThreadCount
    {
        get => ApplicationSettings.PlayerMixerThreadCount;
        set
        {
            if (BassMixerThreadConfigurator.IsValidRealtimeThreadCount(value) && ApplicationSettings.PlayerMixerThreadCount != value)
            {
                if (!SaveIndependentAudioSetting(nameof(Settings.PlayerMixerThreadCount), value)) { return; }
                RaisePropertyChanged(nameof(PlayerMixerThreadCount));
                InvalidateAudioDeviceTestResult();
            }
        }
    }

    /// <summary>現在の言語で通常再生用nativeミキサーthread数の選択肢を取得します。</summary>
    public ReadOnlyDictionary<int, string> PlayerMixerThreadCountNames =>
        new(new Dictionary<int, string>
        {
            [1] = FormatMixerThreadCountOption(1),
            [2] = FormatMixerThreadCountOption(2),
            [3] = FormatMixerThreadCountOption(3),
            [4] = FormatMixerThreadCountOption(4)
        });

    /// <summary>資源変更時に表示名と選択キーを再通知し、音声の設定値・下書き・処理結果を保ちます。</summary>
    internal void RefreshLocalizedOptionNames()
    {
        RaisePropertyChanged(nameof(PlayerResamplingQualityNames));
        // 表示名を含む辞書項目の交換でComboBoxの選択が外れるため、保存済みキーをBindingへ読み直させます。
        RaisePropertyChanged(nameof(PlayerResamplingQuality));
        RaisePropertyChanged(nameof(PlayerMixerThreadCountNames));
        RaisePropertyChanged(nameof(PlayerMixerThreadCount));
    }

    private static string FormatMixerThreadCountOption(int threadCount) => threadCount == BassMixerThreadConfigurator.RealtimeDefaultThreadCount
        ? string.Format(CultureInfo.CurrentCulture, Resources.AudioMixerThreadCountDefaultOptionFormat, threadCount)
        : threadCount.ToString(CultureInfo.CurrentCulture);

    public bool PlayerWASAPIParam
    {
        get
        {
            return candidatePlayerWASAPIParam;
        }
        set
        {
            if (candidatePlayerWASAPIParam != value)
            {
                candidatePlayerWASAPIParam = value;
                RaisePropertyChanged("PlayerWASAPIParam");
                RaisePropertyChanged(nameof(IsPlayerBufferControlEnabled));
                InvalidateAudioDeviceTestResult();
            }
        }
    }


    internal Task RunAudioDeviceTestAsync()
    {
        if (!IsAudioDeviceTestAvailable)
        {
            return Task.CompletedTask;
        }

        AudioDeviceTestRequest request = new(
            audioOutputSelectionDraft.Backend,
            audioOutputSelectionDraft.DeviceIdentity,
            audioOutputSelectionDraft.DeviceName,
            candidatePlayerSampleRate,
            candidatePlayerFormat,
            candidatePlayerBufferSize,
            candidatePlayerWASAPIParam,
            ApplicationSettings.uBMplayVolume,
            playSound: true,
            PlayerResamplingQuality,
            PlayerMixerThreadCount);
        AudioDeviceTestStatusMessage = null;
        AudioDeviceTestDiagnosticMessage = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        audioDeviceTestCompletionTask = completion.Task;
        PublishAudioDeviceTestCompletionAsync(request, completion).ObserveFault("settings audio device test");
        RaiseAudioDeviceTestOperationPropertiesChanged();
        return audioDeviceTestCompletionTask;
    }

    private async Task PublishAudioDeviceTestCompletionAsync(AudioDeviceTestRequest request, TaskCompletionSource completion)
    {
        try
        {
            await RunAudioDeviceTestCoreAsync(request);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task RunAudioDeviceTestCoreAsync(AudioDeviceTestRequest request)
    {
        Task<AudioDeviceTestResult> testTask = TryRunAsync(request);
        RaiseAudioDeviceTestOperationPropertiesChanged();
        try
        {
            AudioDeviceTestResult result = await testTask;
            if (result == null || !isPresentationActive)
            {
                return;
            }

            AudioDeviceTestStatusMessage = FormatAudioDeviceTestResult(result);
            AudioDeviceTestDiagnosticMessage = FormatAudioDeviceTestDiagnostics(result);
        }
        finally
        {
            RaiseAudioDeviceTestOperationPropertiesChanged();
        }
    }

    private static string FormatAudioDeviceTestResult(AudioDeviceTestResult result)
    {
        if (result.Initialization == null)
        {
            if (HasAudioDeviceTestCleanupFailure(result))
            {
                string cleanupReason = FormatAudioDeviceTestFailureReason(result);
                return result.PrimaryFailure is AudioInitializationException initializationFailure
                    ? cleanupReason + Environment.NewLine
                        + FormatAudioInitializationFailure(initializationFailure, result.Request)
                    : cleanupReason;
            }
            return result.PrimaryFailure is AudioInitializationException failure && result.Request != null
                ? FormatAudioInitializationFailure(failure, result.Request)
                : FormatAudioDeviceTestFailureReason(result);
        }
        string requestedDevice = DescribeAudioDevice(result.RequestedDevice, result.RequestedDeviceName);
        string actualDevice = DescribeAudioDevice(result.ActualDevice, result.ActualDeviceName);
        string outcome = !result.Succeeded
            ? FormatAudioDeviceTestFailureReason(result)
            : result.FallbackOccurred
                ? BeMusicSeeker.Properties.Resources.AudioDeviceTestFallbackReason
                : BeMusicSeeker.Properties.Resources.AudioDeviceTestSucceeded;
        string details = string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceTestResultDetailsFormat,
            AudioDriverDisplayNames.Get(result.RequestedBackend),
            requestedDevice,
            FormatAudioSampleRate(result.RequestedRate),
            FormatSampleFormat(result.RequestedFormat),
            AudioDriverDisplayNames.Get(result.Initialization.ActualBackend),
            actualDevice,
            FormatAudioSampleRate(result.Initialization.ActualRate),
            FormatSampleFormat(result.Initialization.EngineFormat),
            FormatSampleFormat(result.Initialization.EndpointFormat),
            FormatEndpointPrecision(
                result.Initialization.EndpointEffectiveBits,
                result.Initialization.EndpointContainerBits),
            result.ActualChannels,
            result.Latency);
        return outcome + Environment.NewLine + details;
    }

    private static string FormatAudioDeviceTestDiagnostics(AudioDeviceTestResult result)
    {
        var details = (result.Initialization?.Attempts ?? [])
            .Select(attempt => FormatAudioAttemptDiagnostic(attempt))
            .ToList();
        if (result.PrimaryFailure is AudioInitializationException initializationFailure)
        {
            details.Add(FormatAudioInitializationDiagnostics(initializationFailure));
        }
        else if (result.PrimaryFailure != null)
        {
            details.Add("primaryFailure=" + result.PrimaryFailure.GetType().Name);
        }
        if (result.CleanupFailure != null)
        {
            details.Add("cleanupFailure=" + result.CleanupFailure.GetType().Name);
            string cleanupDetails = result.CleanupFailure switch
            {
                AudioInitializationException cleanupInitializationFailure
                    => FormatAudioInitializationDiagnostics(cleanupInitializationFailure),
                BassAudioPlaybackException playbackFailure
                    => FormatAudioPlaybackDiagnostics(playbackFailure),
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(cleanupDetails))
            {
                details.Add(cleanupDetails);
            }
        }
        foreach (BassAudioCleanupDiagnostic cleanupDiagnostic in result.CleanupDiagnostics)
        {
            details.Add(FormatAudioCleanupDiagnostic(cleanupDiagnostic));
        }
        if (!string.IsNullOrWhiteSpace(result.FallbackReason))
        {
            details.Insert(0, "fallbackReason=" + result.FallbackReason);
        }
        if (result.PlaybackStage.HasValue
            || result.NativeErrorSource != null
            || result.NativeErrorCode.HasValue)
        {
            details.Add(FormatNativeDiagnostic(
                result.PlaybackStage?.ToString(),
                result.NativeErrorSource,
                result.NativeErrorCode));
        }
        return details.Count == 0 ? null : string.Join(Environment.NewLine, details);
    }

    private static string FormatAudioInitializationDiagnostics(AudioInitializationException exception)
    {
        var details = new List<string>
        {
            FormatNativeDiagnostic(exception.Stage, exception.NativeErrorSource, exception.NativeErrorCode)
        };
        details.AddRange(exception.Attempts.Select(FormatAudioAttemptDiagnostic));
        return string.Join(Environment.NewLine, details);
    }

    private static string FormatAudioPlaybackDiagnostics(BassAudioPlaybackException exception)
        => FormatNativeDiagnostic(exception.Stage.ToString(), exception.NativeErrorSource, exception.NativeErrorCode);

    private static string FormatAudioCleanupDiagnostic(BassAudioCleanupDiagnostic diagnostic)
    {
        var details = new List<string>
        {
            "stage=" + diagnostic.Stage,
            "nativeErrorSource=" + diagnostic.NativeErrorSource
        };
        if (diagnostic.NativeErrorCode.HasValue)
        {
            details.Add("nativeError=" + BassNativeErrorFormatter.Format(diagnostic.NativeErrorCode));
        }
        if (!string.IsNullOrWhiteSpace(diagnostic.ExceptionType))
        {
            details.Add("exceptionType=" + diagnostic.ExceptionType);
        }
        return string.Join(" | ", details);
    }

    private static string FormatAudioAttemptDiagnostic(BassAudioBackendAttempt attempt)
        => attempt.Stage + " | " + (attempt.Outcome ?? string.Empty)
            + " | " + FormatNativeDiagnostic(null, attempt.NativeErrorSource, attempt.NativeErrorCode);

    private static string FormatNativeDiagnostic(string stage, string source, ManagedBass.Errors? error)
        => string.Join(
            " | ",
            new[]
            {
                string.IsNullOrWhiteSpace(stage) ? null : "stage=" + stage,
                string.IsNullOrWhiteSpace(source) ? null : "nativeErrorSource=" + source,
                "nativeError=" + BassNativeErrorFormatter.Format(error)
            }.Where(value => value != null));

    private void RequestAudioDeviceCapabilityQuery()
    {
        if (!isPresentationActive || !isAudioSettingsPageVisible)
        {
            return;
        }

        isAudioDeviceCapabilityQueryPending = true;
        isAudioDeviceCapabilityQueryBlocked = false;
        audioDeviceCapabilityQueryBlockedBySave = false;
        RunPendingAudioDeviceCapabilityQuery();
    }

    private void RunPendingAudioDeviceCapabilityQuery()
    {
        if (!isAudioDeviceCapabilityQueryPending
            || isAudioDeviceCapabilityQueryBlocked
            || isAudioDeviceCapabilityQueryInProgress
            || !audioDeviceListCompletionTask.IsCompleted
            || !isPresentationActive
            || !isAudioSettingsPageVisible)
        {
            return;
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        audioDeviceCapabilityCompletionTask = completion.Task;
        PublishAudioDeviceCapabilityQueryCompletionAsync(completion).ObserveFault("settings audio device capability query");
    }

    private async Task PublishAudioDeviceCapabilityQueryCompletionAsync(TaskCompletionSource completion)
    {
        try
        {
            await RunAudioDeviceCapabilityQueryAsync();
        }
        finally
        {
            completion.TrySetResult();
        }
        RunPendingAudioDeviceCapabilityQuery();
    }

    private async Task RunAudioDeviceCapabilityQueryAsync()
    {
        if (!isAudioDeviceCapabilityQueryPending
            || isAudioDeviceCapabilityQueryInProgress
            || !audioDeviceListCompletionTask.IsCompleted
            || !isPresentationActive
            || !isAudioSettingsPageVisible)
        {
            return;
        }
        if (IsEditCompletionInProgress)
        {
            isAudioDeviceCapabilityQueryBlocked = true;
            audioDeviceCapabilityQueryBlockedBySave = true;
            return;
        }
        if (IsRunning)
        {
            isAudioDeviceCapabilityQueryBlocked = true;
            audioDeviceCapabilityQueryBlockedBySave = false;
            AudioDeviceCapabilityStatusMessage = BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityBusy;
            return;
        }

        AudioOutputSelection selection = audioOutputSelectionDraft;
        long generation = Interlocked.Increment(ref audioDeviceCapabilityGeneration);
        var request = new AudioDeviceCapabilityRequest(
            selection.Backend,
            selection.DeviceIdentity,
            selection.DeviceName,
            PlayerSampleRate,
            PlayerFormat);
        isAudioDeviceCapabilityQueryPending = false;
        isAudioDeviceCapabilityQueryBlocked = false;
        audioDeviceCapabilityQueryBlockedBySave = false;
        audioSessionReleaseObservedDuringCapabilityQuery = false;
        isAudioDeviceCapabilityQueryInProgress = true;
        AudioDeviceCapabilityStatusMessage = BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityQueryInProgress;
        AudioDeviceCapabilityDiagnosticMessage = null;
        audioDeviceCapabilityResult = null;
        audioDeviceCapabilitySelection = null;
        RaiseAudioDeviceCapabilityPropertiesChanged();
        Task<AudioDeviceCapabilityResult> queryTask = TryQueryCapabilitiesAsync(request);
        RaiseAudioDeviceTestOperationPropertiesChanged();
        try
        {
            AudioDeviceCapabilityResult result = await queryTask;
            if (!IsCurrentAudioDeviceCapabilityQuery(generation, selection))
            {
                isAudioDeviceCapabilityQueryPending =
                    isPresentationActive && isAudioSettingsPageVisible;
                return;
            }

            if (result == null || result.Status == AudioDeviceCapabilityStatus.Busy)
            {
                isAudioDeviceCapabilityQueryPending = true;
                isAudioDeviceCapabilityQueryBlocked = true;
                audioDeviceCapabilityQueryBlockedBySave = false;
                AudioDeviceCapabilityStatusMessage = BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityBusy;
                return;
            }

            audioDeviceCapabilityResult = result;
            audioDeviceCapabilitySelection = selection;
            AudioDeviceCapabilityStatusMessage = GetAudioDeviceCapabilityStatusMessage(result.Status);
            AudioDeviceCapabilityDiagnosticMessage = FormatAudioDeviceCapabilityDiagnostics(result);
            RaiseAudioDeviceCapabilityPropertiesChanged();
        }
        catch (Exception exception)
        {
            if (IsCurrentAudioDeviceCapabilityQuery(generation, selection))
            {
                isAudioDeviceCapabilityQueryPending = false;
                isAudioDeviceCapabilityQueryBlocked = false;
                audioDeviceCapabilityQueryBlockedBySave = false;
                AudioDeviceCapabilityStatusMessage = BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityFailed;
                AudioDeviceCapabilityDiagnosticMessage = "exceptionType=" + exception.GetType().FullName;
            }
        }
        finally
        {
            isAudioDeviceCapabilityQueryInProgress = false;
            if (audioSessionReleaseObservedDuringCapabilityQuery)
            {
                audioSessionReleaseObservedDuringCapabilityQuery = false;
                if (isAudioDeviceCapabilityQueryPending)
                {
                    isAudioDeviceCapabilityQueryBlocked = false;
                }
            }
            RaiseAudioDeviceTestOperationPropertiesChanged();
        }
    }

    private bool IsCurrentAudioDeviceCapabilityQuery(long generation, AudioOutputSelection selection)
        => Interlocked.Read(ref audioDeviceCapabilityGeneration) == generation
            && isPresentationActive
            && isAudioSettingsPageVisible
            && audioOutputSelectionDraft == selection;

    private void InvalidateAudioDeviceCapabilities()
    {
        Interlocked.Increment(ref audioDeviceCapabilityGeneration);
        CancelCurrentQuery();
        isAudioDeviceCapabilityQueryPending = false;
        isAudioDeviceCapabilityQueryBlocked = false;
        audioDeviceCapabilityQueryBlockedBySave = false;
        audioSessionReleaseObservedDuringCapabilityQuery = false;
        audioDeviceCapabilityResult = null;
        audioDeviceCapabilitySelection = null;
        AudioDeviceCapabilityStatusMessage = null;
        AudioDeviceCapabilityDiagnosticMessage = null;
        RaiseAudioDeviceCapabilityPropertiesChanged();
    }

    private void RaiseAudioDeviceCapabilityPropertiesChanged()
    {
        RaisePropertyChanged(nameof(PlayerSampleRateNames));
        RaisePropertyChanged(nameof(PlayerFormatNames));
        RaisePropertyChanged(nameof(AudioDeviceCapabilityFormatDescription));
        RaisePropertyChanged(nameof(AudioDeviceCapabilityRateDescription));
    }

    private void RaiseAudioDeviceTestOperationPropertiesChanged()
    {
        RaisePropertyChanged(nameof(IsAudioDeviceTestInProgress));
        RaisePropertyChanged(nameof(IsAudioDeviceTestAvailable));
        RaisePropertyChanged(nameof(AudioDeviceTestButtonContent));
        RaisePropertyChanged(nameof(IsAudioOutputSelectionEnabled));
        RaisePropertyChanged("IsEditCompletionEnabled");
        RaisePropertyChanged("IsEditCancellationEnabled");
    }

    private void ResetAudioOutputConditions(string resetMessage)
    {
        candidatePlayerSampleRate = SampleRate.AUTO;
        candidatePlayerFormat = SampleFormat.AUTO;
        audioOutputSelectionResetMessage = resetMessage;
        RaisePropertyChanged(nameof(AudioOutputSelectionResetMessage));
        RaisePropertyChanged(nameof(PlayerSampleRate));
        RaisePropertyChanged(nameof(SelectedPlayerSampleRate));
        RaisePropertyChanged(nameof(PlayerFormat));
        RaisePropertyChanged(nameof(SelectedPlayerFormat));
        RaisePropertyChanged(nameof(PlayerSampleRateNames));
        RaisePropertyChanged(nameof(PlayerFormatNames));
    }

    private void InvalidateAudioDeviceTestResult()
    {
        AudioDeviceTestStatusMessage = null;
        AudioDeviceTestDiagnosticMessage = null;
    }

    private void AudioWorkflowOperationReleased()
        => DispatchAudioDeviceOperationReleased(isNativeSessionRelease: false);

    private void AudioOutputSessionReleased()
        => DispatchAudioDeviceOperationReleased(isNativeSessionRelease: true);

    private void DispatchAudioDeviceOperationReleased(bool isNativeSessionRelease)
    {
        Dispatcher dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
        {
            return;
        }

        Action callback = () => HandleAudioDeviceOperationReleased(isNativeSessionRelease);
        if (dispatcher.CheckAccess())
        {
            callback();
        }
        else
        {
            _ = dispatcher.BeginInvoke(DispatcherPriority.Normal, callback);
        }
    }

    private void HandleAudioDeviceOperationReleased(bool isNativeSessionRelease)
    {
        RaiseAudioDeviceTestOperationPropertiesChanged();
        if (!isNativeSessionRelease)
        {
            return;
        }
        if (isAudioDeviceCapabilityQueryInProgress)
        {
            if (isNativeSessionRelease)
            {
                audioSessionReleaseObservedDuringCapabilityQuery = true;
            }
            return;
        }
        if (!isAudioDeviceCapabilityQueryPending
            || !isAudioDeviceCapabilityQueryBlocked
            || !isPresentationActive
            || !isAudioSettingsPageVisible)
        {
            return;
        }

        isAudioDeviceCapabilityQueryBlocked = false;
        audioDeviceCapabilityQueryBlockedBySave = false;
        RunPendingAudioDeviceCapabilityQuery();
    }

    private static string GetAudioDeviceCapabilityStatusMessage(AudioDeviceCapabilityStatus status) => status switch
    {
        AudioDeviceCapabilityStatus.Available => null,
        AudioDeviceCapabilityStatus.Unsupported => BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityUnsupported,
        AudioDeviceCapabilityStatus.Busy => BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityBusy,
        _ => BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityFailed
    };

    private static string FormatAudioDeviceCapabilityDiagnostics(AudioDeviceCapabilityResult result)
    {
        var details = result.Attempts
            .Select(FormatAudioAttemptDiagnostic)
            .ToList();
        if (result.FailureStage != null
            || result.NativeErrorSource != null
            || result.NativeErrorCode.HasValue)
        {
            details.Insert(0, FormatNativeDiagnostic(
                result.FailureStage,
                result.NativeErrorSource,
                result.NativeErrorCode));
        }
        return details.Count == 0 ? null : string.Join(Environment.NewLine, details);
    }

    private static string FormatAudioDeviceCapabilityEndpoint(AudioDeviceCapabilityResult result)
    {
        if (result == null)
        {
            return BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityNotQueried;
        }
        if (result.EndpointFormat == SampleFormat.UNKNOWN)
        {
            return BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityNotQueried;
        }

        string left = FormatCapabilityPrecision(result.EndpointFormat, result.EndpointContainerBits, result.EndpointEffectiveBits);
        if (result.RightEndpointFormat != SampleFormat.UNKNOWN
            && (result.RightEndpointFormat != result.EndpointFormat
                || result.RightEndpointContainerBits != result.EndpointContainerBits
                || result.RightEndpointEffectiveBits != result.EndpointEffectiveBits))
        {
            string right = FormatCapabilityPrecision(
                result.RightEndpointFormat,
                result.RightEndpointContainerBits,
                result.RightEndpointEffectiveBits);
            return string.Format(
                BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityStereoFormatsFormat,
                left,
                right,
                result.EndpointChannels);
        }
        return string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityFormatFormat,
            left,
            result.EndpointChannels);
    }

    private static string FormatAudioDeviceCapabilityRate(AudioDeviceCapabilityResult result)
        => result?.Backend == AudioDriver.WasapiShared && result.SupportedRates.Count > 0
            ? string.Format(
                BeMusicSeeker.Properties.Resources.AudioDeviceCapabilitySharedRateFormat,
                (int)result.SupportedRates[0])
            : BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityNotQueried;

    private static string FormatAudioSampleRate(SampleRate rate)
        => rate == SampleRate.AUTO
            ? BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityAuto
            : string.Format(
                CultureInfo.CurrentCulture,
                BeMusicSeeker.Properties.Resources.AudioSampleRateOptionFormat,
                (int)rate);

    private static string FormatEndpointPrecision(int effectiveBits, int containerBits)
        => effectiveBits > 0 && containerBits > 0
            ? string.Format(
                BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityPrecisionPairFormat,
                effectiveBits,
                containerBits)
            : BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityPrecisionUnknown;

    private static string FormatCapabilityPrecision(SampleFormat format, int containerBits, int effectiveBits)
        => containerBits > 0
            ? string.Format(
                BeMusicSeeker.Properties.Resources.AudioDeviceCapabilityPrecisionFormat,
                FormatSampleFormat(format),
                effectiveBits,
                containerBits)
            : FormatSampleFormat(format);

    private static string FormatAudioDeviceTestFailureReason(AudioDeviceTestResult result)
    {
        if (HasAudioDeviceTestCleanupFailure(result))
        {
            return BeMusicSeeker.Properties.Resources.AudioDeviceTestCleanupFailure;
        }
        return result.FailureKind switch
        {
            AudioDeviceTestFailureKind.TestSoundUnavailable
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestTestSoundUnavailableReason,
            AudioDeviceTestFailureKind.PlayerCreationFailed
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestPlayerCreationFailureReason,
            AudioDeviceTestFailureKind.InvalidDuration
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestInvalidDurationReason,
            AudioDeviceTestFailureKind.PlaybackStartFailed
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestPlaybackFailureReason,
            AudioDeviceTestFailureKind.PlaybackPositionMovedBackwards
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestPlaybackPositionFailureReason,
            AudioDeviceTestFailureKind.PlaybackStoppedEarly
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestPlaybackStoppedEarlyReason,
            AudioDeviceTestFailureKind.PlaybackRateOutOfRange
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestRateFailureReason,
            AudioDeviceTestFailureKind.ObservationTimedOut
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestObservationTimeoutReason,
            AudioDeviceTestFailureKind.PlaybackDidNotAdvance
                => BeMusicSeeker.Properties.Resources.AudioDeviceTestStreamProgressFailureReason,
            _ => BeMusicSeeker.Properties.Resources.AudioDeviceTestUnexpectedFailureReason
        };
    }

    private static bool HasAudioDeviceTestCleanupFailure(AudioDeviceTestResult result)
        => result.CleanupFailure != null
            || (result.PrimaryFailure is AudioInitializationException initializationFailure
                && string.Equals(initializationFailure.Stage, "audio session cleanup", StringComparison.Ordinal));

    private static string FormatAudioInitializationFailure(
        AudioInitializationException exception,
        AudioDeviceTestRequest request)
    {
        AudioOutputRequest outputRequest = request.AudioOutputRequest;
        return string.Format(
            BeMusicSeeker.Properties.Resources.AudioDeviceTestInitializationErrorFormat,
            AudioDriverDisplayNames.Get(outputRequest.Backend),
            DescribeAudioDevice(outputRequest.DeviceIdentity, outputRequest.DeviceName),
            outputRequest.Rate,
            outputRequest.Format,
            AudioDriverDisplayNames.Get(exception.ActualBackend),
            DescribeAudioDevice(exception.ActualDevice.Driver, exception.ActualDevice.Name));
    }

    private static string DescribeAudioDevice(string identity, string name)
    {
        if (string.IsNullOrWhiteSpace(identity) && string.IsNullOrWhiteSpace(name))
        {
            return BeMusicSeeker.Properties.Resources.AudioDeviceDefault;
        }
        return string.IsNullOrWhiteSpace(name) ? identity : name;
    }

    /// <summary>オーディオ設定ページの表示状態を受け取り、自動照会の契機を更新します。</summary>
    /// <param name="visible">ページが表示されている場合はtrue。</param>
    internal void SetAudioSettingsPageVisible(bool visible)
    {
        if (isAudioSettingsPageVisible == visible)
        {
            return;
        }

        isAudioSettingsPageVisible = visible;
        if (!visible)
        {
            InvalidateAudioDeviceCapabilities();
            return;
        }

        PresentAudioSettingsPage();
    }

}
