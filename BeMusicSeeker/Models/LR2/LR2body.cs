using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
namespace BeMusicSeeker.Models.LR2;

public class LR2body : ObservableObject, IBMSPlayer, IExternalWindowPlayer, INotifyPropertyChanged
{
    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IExternalPlayerProcessGateway processGateway;

    private readonly ExternalPlayerWaitPolicy waitPolicy;

    private readonly LR2Config lr2Config;

    private string BMSFilePathPlaying;

    private bool isPausing;

    private IExternalPlayerProcessSession LR2bodyProcess;

    private ExternalWindowHandle LR2bodyHandleShowing;

    private EventHandler onExitEventHandlerRegstered;

    private readonly EventHandler onExitEventHandlerDefault;

    private readonly object lockThis = new();

    private LR2Config.PreviewScope previewScope;

    private bool previewPublished;

    private string _exePath;

    private IExternalPlayerWindowHost windowHost;

    public string ExePath
    {
        get
        {
            return _exePath;
        }
        set
        {
            if (!(_exePath == value))
            {
                if (!File.Exists(value) || (!(Path.GetFileName(value) == "LR2body.exe") && !(Path.GetFileName(value) == "LRHbody.exe")))
                {
                    throw new FileNotFoundException(string.Format(BeMusicSeeker.Properties.Resources.Error_InvalidPlayerExecutableFormat, "LR2body.exe/LRHbody.exe"), value);
                }
                _exePath = value;
            }
        }
    }

    public TimeSpan Duration { get; } = TimeSpan.MinValue;

    public TimeSpan CurrentTime
    {
        get
        {
            return TimeSpan.MinValue;
        }
        set
        {
        }
    }

    public TimeSpan StopTime { get; }

    public TimeSpan BmsDuration { get; }

    public TimeSpan MusicDuration { get; }

    public int CurrentVoices { get; }

    public int MaxVoices { get; }

    public int NoteDensity { get; }

    public int NoteDensityMax { get; }

    public int Bpm { get; }

    public int MinBpm { get; }

    public int MaxBpm { get; }

    public double Total { get; }

    public int Combo { get; }

    public int Notes { get; }

    public int Measure { get; }

    public int LastMeasure { get; }

    internal LR2body(
        string exePath,
        LR2Config config,
        IPlayerSettingsGateway playerSettingsGateway,
        IExternalPlayerProcessGateway processGateway,
        ExternalPlayerWaitPolicy waitPolicy = null)
    {
        ExePath = exePath ?? throw new ArgumentNullException("exePath", "引数をnullに出来ません");
        lr2Config = config ?? throw new ArgumentNullException("config", "引数をnullに出来ません");
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.processGateway = processGateway ?? throw new ArgumentNullException(nameof(processGateway));
        this.waitPolicy = waitPolicy ?? ExternalPlayerWaitPolicy.Default;
        onExitEventHandlerDefault = LR2bodyExited;
    }

    void IExternalWindowPlayer.AttachWindowHost(IExternalPlayerWindowHost windowHost)
    {
        this.windowHost = windowHost ?? throw new ArgumentNullException(nameof(windowHost));
    }

    /// <summary>
    /// LR2body を終了し、active 試聴設定を復元します。
    /// </summary>
    public void CloseProcess()
    {
        Exception closeFailure = null;
        EventHandler exitHandler;
        IExternalPlayerProcessSession process;
        lock (lockThis)
        {
            process = LR2bodyProcess;
            if (process == null)
            {
                return;
            }
            Exception handlerFailure = null;
            try
            {
                handlerFailure = UnregisterProcessHandlers(process);
            }
            catch (Exception exception)
            {
                handlerFailure = exception;
            }
            try
            {
                storeWindowPosition();
            }
            catch (Exception exception)
            {
                closeFailure = exception;
            }
            Exception terminateFailure = TryTerminateStartedProcess(process);
            closeFailure = CombineFailures(closeFailure, handlerFailure, terminateFailure);
            exitHandler = onExitEventHandlerDefault;
            if (closeFailure != null)
            {
                if (onExitEventHandlerDefault != null)
                {
                    process.Exited += onExitEventHandlerDefault;
                }
                if (onExitEventHandlerRegstered != null)
                {
                    process.Exited += onExitEventHandlerRegstered;
                }
            }
        }
        if (closeFailure != null)
        {
            throw new InvalidOperationException(
                "LR2body could not be terminated.",
                closeFailure);
        }
        exitHandler?.Invoke(null, null);
    }

    /// <summary>
    /// 指定譜面を LR2body で再生し、試聴用設定を短時間だけ公開します。
    /// </summary>
    /// <param name="bmsFilePath">再生する譜面の絶対 path です。</param>
    /// <param name="onExitEventHandler">終了時に呼び出す既存 callback です。</param>
    /// <returns>起動処理の完了を表す task です。</returns>
    public Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        return PlayStart(bmsFilePath, (onExitEventHandler != null) ? new EventHandler(onExitEventHandler.Invoke) : null);
    }

    /// <summary>
    /// 指定譜面を LR2body で再生し、試聴用設定を短時間だけ公開します。
    /// </summary>
    /// <param name="bmsFilePath">再生する譜面の絶対 path です。</param>
    /// <param name="onExitEventHandler">終了時に呼び出す既存 callback です。</param>
    /// <returns>起動処理の完了を表す task です。</returns>
    public Task PlayStart(string bmsFilePath, EventHandler onExitEventHandler = null)
    {
        lock (lockThis)
        {
            isPausing = false;
            IReadOnlyList<IExternalPlayerProcessSession> existingProcesses = processGateway.FindExisting(
                ExternalPlayerProcessDiscoveryRequest.Create("LR2body", "LRHbody"));
            if (existingProcesses.Count != 0)
            {
                if (LR2bodyProcess == null)
                {
                    foreach (IExternalPlayerProcessSession item in existingProcesses)
                    {
                        LR2bodyProcess = item;
                        CloseProcess();
                    }
                }
                else
                {
                    CloseProcess();
                }
                LR2bodyHandleShowing = default;
                LR2bodyProcess = null;
            }
            ExternalPlayerProcessLaunchRequest launchRequest = ExternalPlayerProcessLaunchRequest.Create(
                ExePath,
                "-A -NS \"" + bmsFilePath + "\"",
                System.Diagnostics.ProcessWindowStyle.Hidden);
            IExternalPlayerProcessSession process = processGateway.Prepare(launchRequest);
            LR2bodyProcess = process;
            bool processStarted = false;
            Exception primaryFailure = null;
            Exception processCleanupFailure = null;
            Exception previewRestoreFailure = null;
            Exception previewEndFailure = null;
            bool previewRestoreAttempted = false;
            try
            {
                process.Exited += onExitEventHandlerDefault;
                if (onExitEventHandler != null)
                {
                    onExitEventHandlerRegstered = onExitEventHandler;
                    process.Exited += onExitEventHandlerRegstered;
                }
                else
                {
                    onExitEventHandlerRegstered = null;
                }

                previewScope = lr2Config.BeginPreview();
                PlayerSettingsSnapshot settings = playerSettingsGateway.CaptureSnapshot();
                lr2Config.PublishPreview(
                    previewScope,
                    (int)settings.LR2bodyResolution.Width,
                    (int)settings.LR2bodyResolution.Height,
                    isWindowMode: true,
                    masterVolume: settings.PlayerVolume,
                    isVolumeEnabled: true);
                previewPublished = true;
                ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
                process.Start();
                processStarted = true;
                waitPolicy.WaitUntil(
                    () => !process.MainWindowHandle.IsEmpty,
                    () => process.HasExited,
                    () => { },
                    string.Format(BeMusicSeeker.Properties.Resources.Error_PlayerMainWindowTimeoutFormat, "LR2"),
                    pollMilliseconds: 0);
                if (process.HasExited)
                {
                    throw new TimeoutException(string.Format(BeMusicSeeker.Properties.Resources.Error_PlayerStartupTimeoutFormat, "LR2"));
                }

                LR2bodyHandleShowing = process.MainWindowHandle;
                if (!setWindowStyle() || LR2bodyProcess == null || process.HasExited)
                {
                    throw new InvalidOperationException(
                        "LR2 exited before its window style could be applied.");
                }
                restoreWindowPosition(settings);
                RestoreForegroundWindow(foregroundWindow);
                BMSFilePathPlaying = bmsFilePath;
                previewRestoreAttempted = true;
                lr2Config.RestorePreview(previewScope);
                return Task.CompletedTask;
            }
            catch (Exception exception)
            {
                primaryFailure = exception;
                if (processStarted)
                {
                    processCleanupFailure = TryTerminateStartedProcess(process);
                    if (processCleanupFailure == null)
                    {
                        processCleanupFailure = UnregisterProcessHandlers(process);
                        if (processCleanupFailure == null && ReferenceEquals(LR2bodyProcess, process))
                        {
                            LR2bodyProcess = null;
                            LR2bodyHandleShowing = default;
                            onExitEventHandlerRegstered = null;
                        }
                    }
                }
                else
                {
                    processCleanupFailure = UnregisterProcessHandlers(process);
                    if (ReferenceEquals(LR2bodyProcess, process))
                    {
                        LR2bodyProcess = null;
                        LR2bodyHandleShowing = default;
                        onExitEventHandlerRegstered = null;
                    }
                }

                if (previewPublished && previewScope != null && !previewRestoreAttempted)
                {
                    previewRestoreAttempted = true;
                    try
                    {
                        lr2Config.RestorePreview(previewScope);
                    }
                    catch (Exception restoreException)
                    {
                        previewRestoreFailure = restoreException;
                    }
                }

                if (processCleanupFailure == null || !processStarted)
                {
                    if (previewScope != null)
                    {
                        try
                        {
                            lr2Config.EndPreview(previewScope);
                        }
                        catch (Exception endException)
                        {
                            previewEndFailure = endException;
                        }
                        if (previewEndFailure == null)
                        {
                            previewScope = null;
                            previewPublished = false;
                        }
                    }
                }

                Exception secondaryFailure = CombineFailures(
                    processCleanupFailure,
                    previewRestoreFailure,
                    previewEndFailure);
                if (secondaryFailure == null)
                {
                    throw;
                }
                throw CreatePreviewFailure(primaryFailure, secondaryFailure);
            }
        }
    }

    public void RestartPlayingBMSfile()
    {
        if (!string.IsNullOrWhiteSpace(BMSFilePathPlaying))
        {
            PlayStart(BMSFilePathPlaying, onExitEventHandlerRegstered);
        }
    }

    public void PausePlayingBMSfileToggle()
    {
        if (!isPausing)
        {
            isPausing = true;
            CloseProcess();
        }
        else
        {
            PlayStart(BMSFilePathPlaying, onExitEventHandlerRegstered);
        }
    }

    public void FastForwardPlayingBMSfileStart()
    {
    }

    public void FastForwardPlayingBMSfileEnd()
    {
    }

    public void FastBackwardPlayingBMSfileStart()
    {
    }

    public void FastBackwardPlayingBMSfileEnd()
    {
    }

    public void ShowInfo()
    {
    }

    public void ShowEffect()
    {
    }

    public void ChangePlayside()
    {
    }

    public void IncreaseHighSpeed()
    {
    }

    public void DecreaseHighSpeed()
    {
    }

    private Exception TryTerminateStartedProcess(IExternalPlayerProcessSession process)
    {
        Exception closeFailure = null;
        try
        {
            process.CloseMainWindow();
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }

        Exception waitFailure = null;
        try
        {
            waitPolicy.WaitForProcessExit(
                () => process.HasExited,
                process.Kill,
                "LR2body did not terminate after graceful close and kill.");
        }
        catch (Exception exception)
        {
            waitFailure = exception;
        }
        return CombineFailures(closeFailure, waitFailure);
    }

    private Exception UnregisterProcessHandlers(IExternalPlayerProcessSession process)
    {
        Exception firstFailure = null;
        try
        {
            if (onExitEventHandlerRegstered != null)
            {
                process.Exited -= onExitEventHandlerRegstered;
            }
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }
        try
        {
            if (onExitEventHandlerDefault != null)
            {
                process.Exited -= onExitEventHandlerDefault;
            }
        }
        catch (Exception exception)
        {
            firstFailure = CombineFailures(firstFailure, exception);
        }
        return firstFailure;
    }

    private static Exception CombineFailures(params Exception[] failures)
    {
        Exception[] actualFailures = failures.Where(failure => failure != null).ToArray();
        if (actualFailures.Length == 0)
        {
            return null;
        }
        if (actualFailures.Length == 1)
        {
            return actualFailures[0];
        }
        return new AggregateException(actualFailures);
    }

    private InvalidOperationException CreatePreviewFailure(
        Exception primaryFailure,
        Exception secondaryFailure)
    {
        return new InvalidOperationException(
            "config=" + lr2Config.ConfigFilePath
                + " primary=" + primaryFailure.Message
                + " secondary=" + secondaryFailure.Message,
            new AggregateException(primaryFailure, secondaryFailure));
    }

    private void restoreWindowPosition(PlayerSettingsSnapshot settings)
    {
        if (!LR2bodyHandleShowing.IsEmpty && settings.IsSaveLR2bodyWindowPosition)
        {
            RequireWindowHost().ApplyWindowPlacement(
                LR2bodyHandleShowing,
                settings.LR2bodyWindowPlacement,
                (int)settings.LR2bodyResolution.Width,
                (int)settings.LR2bodyResolution.Height);
        }
    }

    private void storeWindowPosition()
    {
        if (!LR2bodyHandleShowing.IsEmpty)
        {
            playerSettingsGateway.UpdateWindowPlacement(RequireWindowHost().CaptureWindowPlacement(LR2bodyHandleShowing));
        }
    }

    private bool setWindowStyle()
    {
        if (LR2bodyHandleShowing.IsEmpty)
        {
            return false;
        }
        return waitPolicy.WaitUntil(
            () => RequireWindowHost().IsLr2WindowStyleApplied(LR2bodyHandleShowing),
            () => LR2bodyProcess == null || LR2bodyProcess.HasExited,
            () => RequireWindowHost().ApplyLr2WindowStyle(LR2bodyHandleShowing),
            BeMusicSeeker.Properties.Resources.Error_LR2WindowStyleTimeout,
            pollMilliseconds: 0);
    }

    private void RestoreForegroundWindow(ExternalWindowHandle foregroundWindow)
    {
        if (foregroundWindow.IsEmpty || !RequireWindowHost().IsWindow(foregroundWindow))
        {
            return;
        }
        waitPolicy.WaitUntil(
            () => RequireWindowHost().GetForegroundWindow() == foregroundWindow,
            () => !RequireWindowHost().IsWindow(foregroundWindow),
            () => RequireWindowHost().SetForegroundWindow(foregroundWindow),
            string.Format(BeMusicSeeker.Properties.Resources.Error_PlayerForegroundRestoreAfterStartupTimeoutFormat, "LR2"),
            pollMilliseconds: 50);
    }

    private IExternalPlayerWindowHost RequireWindowHost()
    {
        return windowHost ?? throw new InvalidOperationException("LR2の再生ホストが接続されていません。");
    }

    public void VolumeChanged()
    {
    }

    private void LR2bodyExited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
            LR2bodyHandleShowing = default;
            LR2bodyProcess = null;
            if (!isPausing)
            {
                BMSFilePathPlaying = null;
            }
            if (previewScope == null)
            {
                return;
            }

            Exception restoreFailure = null;
            try
            {
                if (previewPublished)
                {
                    lr2Config.RestorePreview(previewScope);
                }
            }
            catch (Exception exception)
            {
                restoreFailure = exception;
            }
            try
            {
                lr2Config.EndPreview(previewScope);
            }
            catch (Exception exception)
            {
                restoreFailure = CombineFailures(restoreFailure, exception);
            }
            previewScope = null;
            previewPublished = false;
            if (restoreFailure != null)
            {
                throw CreatePreviewFailure(
                    new InvalidOperationException("LR2 preview restore failed."),
                    restoreFailure);
            }
        }
    }
}
