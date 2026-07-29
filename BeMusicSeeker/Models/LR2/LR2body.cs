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

    private int winSizeX;

    private int winSizeY;

    private int volume = -1;

    private bool? isWinMode;

    private bool? isVolumeEnabled;

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
                    throw new FileNotFoundException("実行ファイルが見つからないか、LR2body.exe/LRHbody.exe ではありません。", value);
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

    public void CloseProcess()
    {
        Exception closeFailure;
        EventHandler exitHandler;
        lock (lockThis)
        {
            if (LR2bodyProcess == null)
            {
                return;
            }
            if (onExitEventHandlerRegstered != null)
            {
                LR2bodyProcess.Exited -= onExitEventHandlerRegstered;
            }
            if (onExitEventHandlerDefault != null)
            {
                LR2bodyProcess.Exited -= onExitEventHandlerDefault;
            }
            storeWindowPosition();
            closeFailure = null;
            exitHandler = onExitEventHandlerDefault;
            try
            {
                LR2bodyProcess.CloseMainWindow();
                waitPolicy.WaitForProcessExit(
                    () => LR2bodyProcess.HasExited,
                    LR2bodyProcess.Kill,
                    "LR2body did not terminate after graceful close and kill.");
            }
            catch (Exception exception)
            {
                closeFailure = exception;
            }
            if (closeFailure != null)
            {
                if (onExitEventHandlerDefault != null)
                {
                    LR2bodyProcess.Exited += onExitEventHandlerDefault;
                }
                if (onExitEventHandlerRegstered != null)
                {
                    LR2bodyProcess.Exited += onExitEventHandlerRegstered;
                }
            }
        }
        if (closeFailure != null)
        {
            throw new InvalidOperationException("LR2body could not be terminated.", closeFailure);
        }
        exitHandler?.Invoke(null, null);
    }

    public Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        return PlayStart(bmsFilePath, (onExitEventHandler != null) ? new EventHandler(onExitEventHandler.Invoke) : null);
    }

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
            LR2bodyProcess = processGateway.Prepare(launchRequest);
            LR2bodyProcess.Exited += onExitEventHandlerDefault;
            if (onExitEventHandler != null)
            {
                onExitEventHandlerRegstered = onExitEventHandler;
                LR2bodyProcess.Exited += onExitEventHandlerRegstered;
            }
            else
            {
                onExitEventHandlerRegstered = null;
            }
            storeConfig();
            PlayerSettingsSnapshot settings = playerSettingsGateway.CaptureSnapshot();
            setConfig((int)settings.LR2bodyResolution.Width, (int)settings.LR2bodyResolution.Height, isWinMode: true, settings.PlayerVolume);
            ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
            LR2bodyProcess.Start();
            waitPolicy.WaitUntil(
                () => !LR2bodyProcess.MainWindowHandle.IsEmpty,
                () => LR2bodyProcess.HasExited,
                () => { },
                "LR2のメインウィンドウ待機がタイムアウトしました。",
                pollMilliseconds: 0);
            if (!LR2bodyProcess.HasExited)
            {
                LR2bodyHandleShowing = LR2bodyProcess.MainWindowHandle;
                if (!setWindowStyle() || LR2bodyProcess == null || LR2bodyProcess.HasExited)
                {
                    if (LR2bodyProcess != null)
                    {
                        LR2bodyExited(LR2bodyProcess, EventArgs.Empty);
                    }
                    throw new InvalidOperationException(
                        "LR2 exited before its window style could be applied.");
                }
                restoreWindowPosition(settings);
                RestoreForegroundWindow(foregroundWindow);
                BMSFilePathPlaying = bmsFilePath;
                restoreConfig(isTempClear: false);
                return Task.CompletedTask;
            }
            onExitEventHandlerDefault?.Invoke(null, null);
            throw new TimeoutException("LR2の起動がタイムアウトしました。");
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

    private void storeConfig()
    {
        try
        {
            winSizeX = lr2Config.GetWindowSizeX();
        }
        catch
        {
            winSizeX = 0;
        }
        try
        {
            winSizeY = lr2Config.GetWindowSizeY();
        }
        catch
        {
            winSizeY = 0;
        }
        try
        {
            isWinMode = lr2Config.IsScreenModeWindow();
        }
        catch
        {
            isWinMode = null;
        }
        try
        {
            volume = lr2Config.GetMasterVolume();
        }
        catch
        {
            volume = -1;
        }
        try
        {
            isVolumeEnabled = lr2Config.IsVolumeEnabled();
        }
        catch
        {
            isVolumeEnabled = null;
        }
    }

    private void setConfig(int winSizeX, int winSizeY, bool isWinMode = true, int volume = 100, bool isVolumeEnabled = true)
    {
        try
        {
            lr2Config.SetWindowSizeX(winSizeX);
        }
        catch
        {
        }
        try
        {
            lr2Config.SetWindowSizeY(winSizeY);
        }
        catch
        {
        }
        try
        {
            lr2Config.SetScreenMode(isWinMode);
        }
        catch
        {
        }
        try
        {
            lr2Config.SetMasterVolume(volume);
        }
        catch
        {
        }
        try
        {
            lr2Config.SetVolumeFlag(isVolumeEnabled);
        }
        catch
        {
        }
        lr2Config.Save();
    }

    private void restoreConfig(bool isTempClear = true)
    {
        if (lr2Config == null)
        {
            return;
        }
        if (winSizeX != 0)
        {
            try
            {
                lr2Config.SetWindowSizeX(winSizeX);
            }
            catch
            {
            }
            finally
            {
                if (isTempClear)
                {
                    winSizeX = 0;
                }
            }
        }
        if (winSizeY != 0)
        {
            try
            {
                lr2Config.SetWindowSizeY(winSizeY);
            }
            catch
            {
            }
            finally
            {
                if (isTempClear)
                {
                    winSizeY = 0;
                }
            }
        }
        if (isWinMode.HasValue)
        {
            try
            {
                lr2Config.SetScreenMode(isWinMode.Value);
            }
            catch
            {
            }
            finally
            {
                if (isTempClear)
                {
                    isWinMode = null;
                }
            }
        }
        if (volume != -1)
        {
            try
            {
                lr2Config.SetMasterVolume(volume);
            }
            catch
            {
            }
            finally
            {
                if (isTempClear)
                {
                    volume = -1;
                }
            }
        }
        if (isVolumeEnabled.HasValue)
        {
            try
            {
                lr2Config.SetVolumeFlag(isVolumeEnabled.Value);
            }
            catch
            {
            }
            finally
            {
                if (isTempClear)
                {
                    isVolumeEnabled = null;
                }
            }
        }
        lr2Config.Save();
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
            playerSettingsGateway.SaveWindowPlacement(RequireWindowHost().CaptureWindowPlacement(LR2bodyHandleShowing));
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
            "LR2のwindow style適用がタイムアウトしました。",
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
            "LR2起動後のforeground復元がタイムアウトしました.",
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
            restoreConfig();
        }
    }
}
