using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public class BMIIDXView2015 : ObservableObject, IBMSPlayer, IExternalWindowPlayer, INotifyPropertyChanged
{
    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IExternalPlayerProcessGateway processGateway;

    private enum KeyCode
    {
        NONE = 0,
        UP = 72,
        DOWN = 80,
        ADD = 78,
        SUBTRACT = 74,
        ENTER = 28
    }

    private IExternalPlayerProcessSession BMIIDXView2015Process;

    private string BMSFilePathPlaying;

    private ExternalWindowHandle BMIIDXView2015HandleShowing;

    private EventHandler onExitEventHandlerRegstered;

    private readonly EventHandler onExitEventHandlerDefault;

    private readonly object lockThis = new();

    private string _exePath;

    private IExternalPlayerWindowHost windowHost;

    private static readonly Dictionary<KeyCode, bool> KeyEventFlags = new()
    {
        {
            KeyCode.UP,
            true
        },
        {
            KeyCode.DOWN,
            true
        },
        {
            KeyCode.ADD,
            false
        },
        {
            KeyCode.SUBTRACT,
            false
        },
        {
            KeyCode.ENTER,
            false
        }
    };

    private static readonly Regex iniVolume = new("^BMSVOLUME=[0-9]+", RegexOptions.Multiline | RegexOptions.Compiled);

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
                if (!File.Exists(value) || !Path.GetFileName(value).StartsWith("BMIIDXView2015", StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileNotFoundException("実行ファイルが見つからないか、BMIIDXView2015(_64).exe ではありません。", value);
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

    internal BMIIDXView2015(
        string exePath,
        IPlayerSettingsGateway playerSettingsGateway,
        IExternalPlayerProcessGateway processGateway)
    {
        ExePath = exePath;
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.processGateway = processGateway ?? throw new ArgumentNullException(nameof(processGateway));
        onExitEventHandlerDefault = BMIIDXView2015Exited;
    }

    void IExternalWindowPlayer.AttachWindowHost(IExternalPlayerWindowHost windowHost)
    {
        this.windowHost = windowHost ?? throw new ArgumentNullException(nameof(windowHost));
    }

    public void CloseProcess()
    {
        lock (lockThis)
        {
            if (BMIIDXView2015Process == null)
            {
                return;
            }
            if (onExitEventHandlerRegstered != null)
            {
                BMIIDXView2015Process.Exited -= onExitEventHandlerRegstered;
            }
            if (onExitEventHandlerDefault != null)
            {
                BMIIDXView2015Process.Exited -= onExitEventHandlerDefault;
            }
            try
            {
                BMIIDXView2015Process.CloseMainWindow();
                int num = 0;
                while (!BMIIDXView2015Process.HasExited)
                {
                    Thread.Sleep(100);
                    if (num == 50)
                    {
                        try
                        {
                            BMIIDXView2015Process.Kill();
                        }
                        catch
                        {
                        }
                        num = 0;
                    }
                    num++;
                }
            }
            catch
            {
            }
            onExitEventHandlerDefault?.Invoke(null, null);
        }
    }

    public Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        return PlayStart(bmsFilePath, (onExitEventHandler != null) ? new EventHandler(onExitEventHandler.Invoke) : null);
    }

    public Task PlayStart(string bmsFilePath, EventHandler onExitEventHandler = null)
    {
        lock (lockThis)
        {
            IReadOnlyList<IExternalPlayerProcessSession> existingProcesses = processGateway.FindExisting(
                ExternalPlayerProcessDiscoveryRequest.Create("BMIIDXView2015", "BMIIDXView2015_64"));
            if (existingProcesses.Count != 0)
            {
                if (BMIIDXView2015HandleShowing.IsEmpty)
                {
                    foreach (IExternalPlayerProcessSession process in existingProcesses)
                    {
                        BMIIDXView2015Process = process;
                        CloseProcess();
                    }
                }
                else
                {
                    CloseProcess();
                }
                BMIIDXView2015HandleShowing = default;
                BMIIDXView2015Process = null;
            }
            ExternalPlayerProcessLaunchRequest launchRequest = ExternalPlayerProcessLaunchRequest.Create(
                ExePath,
                "-S \"" + bmsFilePath + "\"",
                System.Diagnostics.ProcessWindowStyle.Minimized);
            BMIIDXView2015Process = processGateway.Prepare(launchRequest);
            BMIIDXView2015Process.Exited += onExitEventHandlerDefault;
            if (onExitEventHandler != null)
            {
                onExitEventHandlerRegstered = onExitEventHandler;
                BMIIDXView2015Process.Exited += onExitEventHandlerRegstered;
            }
            else
            {
                onExitEventHandlerRegstered = null;
            }
            ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
            string iniFilePath = Path.Combine(DirectoryExt.GetDirectoryNameSimple(ExePath), "BMIIDXView2015.ini");
            temporarilyRewriteSettings(iniFilePath, playerSettingsGateway.CaptureSnapshot().PlayerVolume);
            BMIIDXView2015Process.Start();
            while (!BMIIDXView2015Process.HasExited && BMIIDXView2015Process.MainWindowHandle.IsEmpty)
            {
                Thread.Sleep(50);
            }
            while (!foregroundWindow.IsEmpty && RequireWindowHost().IsWindow(foregroundWindow) && !RequireWindowHost().SetForegroundWindow(foregroundWindow))
            {
                Thread.Sleep(50);
            }
            if (BMIIDXView2015Process.HasExited)
            {
                throw new InvalidOperationException("BMIIDXView2015の起動に失敗しました。");
            }
            BMIIDXView2015HandleShowing = BMIIDXView2015Process.MainWindowHandle;
            RequireWindowHost().AttachBmiIdxWindow(BMIIDXView2015HandleShowing);
            Thread.Sleep(100);
            RequireWindowHost().NotifyBmiIdxPlaybackStarted(BMIIDXView2015HandleShowing);
            while (!foregroundWindow.IsEmpty && RequireWindowHost().IsWindow(foregroundWindow) && !RequireWindowHost().SetForegroundWindow(foregroundWindow))
            {
                Thread.Sleep(50);
            }
            NLogWrapper.DebuggerLogger?.Trace("7 " + foregroundWindow + " " + RequireWindowHost().GetForegroundWindow());
            BMSFilePathPlaying = bmsFilePath;
            return Task.CompletedTask;
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
        lock (lockThis)
        {
            SendPressAndReleaseKeyEvent(KeyCode.ENTER);
        }
    }

    public void FastForwardPlayingBMSfileStart()
    {
        SendPressAndReleaseKeyEvent(KeyCode.UP);
    }

    public void FastForwardPlayingBMSfileEnd()
    {
        PausePlayingBMSfileToggle();
    }

    public void FastBackwardPlayingBMSfileStart()
    {
        SendPressAndReleaseKeyEvent(KeyCode.DOWN);
    }

    public void FastBackwardPlayingBMSfileEnd()
    {
        PausePlayingBMSfileToggle();
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
        SendPressAndReleaseKeyEvent(KeyCode.ADD);
    }

    public void DecreaseHighSpeed()
    {
        SendPressAndReleaseKeyEvent(KeyCode.SUBTRACT);
    }

    public void VolumeChanged()
    {
    }

    private void SendPressAndReleaseKeyEvent(KeyCode code)
    {
        if (BMIIDXView2015HandleShowing.IsEmpty)
        {
            return;
        }
        lock (lockThis)
        {
            ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
            while (RequireWindowHost().IsWindow(BMIIDXView2015HandleShowing) && (!RequireWindowHost().SetForegroundWindow(BMIIDXView2015HandleShowing) || RequireWindowHost().GetForegroundWindow() != BMIIDXView2015HandleShowing))
            {
                Thread.Sleep(10);
            }
            RequireWindowHost().SendKey((short)code, KeyEventFlags[code], keyDown: true);
            NLogWrapper.DebuggerLogger?.Trace("pushed");
            Thread.Sleep(40);
            RequireWindowHost().SendKey((short)code, KeyEventFlags[code], keyDown: false);
            while (RequireWindowHost().IsWindow(foregroundWindow) && (!RequireWindowHost().SetForegroundWindow(foregroundWindow) || RequireWindowHost().GetForegroundWindow() != foregroundWindow))
            {
                Thread.Sleep(10);
            }
        }
    }

    private IExternalPlayerWindowHost RequireWindowHost()
    {
        return windowHost ?? throw new InvalidOperationException("BMIIDXView2015の再生ホストが接続されていません。");
    }

    private void BMIIDXView2015Exited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
        }
        lock (lockThis)
        {
            BMIIDXView2015HandleShowing = default;
            BMIIDXView2015Process = null;
            BMSFilePathPlaying = null;
        }
    }

    private void temporarilyRewriteSettings(string iniFilePath, int playerVolume)
    {
        var encoding = Encoding.GetEncoding("shift_jis");
        string input;
        try
        {
            input = File.ReadAllText(iniFilePath, encoding);
        }
        catch
        {
            return;
        }
        input = iniVolume.Replace(input, "BMSVOLUME=" + Math.Min(100, Math.Max(0, playerVolume)));
        try
        {
            File.WriteAllText(iniFilePath, input, encoding);
        }
        catch
        {
        }
    }
}
