using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using Livet;
using Ribbit.Logging;
using Ribbit.Windows;

namespace BeMusicSeeker.Models;

public class BMIIDXView2015 : NotificationObject, IBMSPlayer, INotifyPropertyChanged
{
    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private enum KeyCode
    {
        NONE = 0,
        UP = 72,
        DOWN = 80,
        ADD = 78,
        SUBTRACT = 74,
        ENTER = 28
    }

    private const uint WM_USER = 1024u;

    private const uint WM_ANOTHER_CLOSE = 1125u;

    private const uint WM_ANOTHER_PLAY = 1127u;

    private const uint WM_ANOTHER_RESET = 1128u;

    private Process BMIIDXView2015Process;

    private string BMSFilePathPlaying;

    private IntPtr BMIIDXView2015HandleShowing = IntPtr.Zero;

    private EventHandler onExitEventHandlerRegstered;

    private readonly EventHandler onExitEventHandlerDefault;

    private readonly object lockThis = new();

    private string _exePath;

    private IntPtr _parentHandle;

    private static readonly Dictionary<KeyCode, DirectInputSendKey.KEYEVENTF> KeyEventFlags = new()
    {
        {
            KeyCode.UP,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_EXTENDEDKEY | DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.DOWN,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_EXTENDEDKEY | DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.ADD,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.SUBTRACT,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.ENTER,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
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

    public IntPtr ParentHandle
    {
        private get
        {
            return _parentHandle;
        }
        set
        {
            if (!(_parentHandle == value))
            {
                _parentHandle = value;
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

    internal BMIIDXView2015(string exePath, IPlayerSettingsGateway playerSettingsGateway)
    {
        ExePath = exePath;
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        onExitEventHandlerDefault = BMIIDXView2015Exited;
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

    public void PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        PlayStart(bmsFilePath, (onExitEventHandler != null) ? new EventHandler(onExitEventHandler.Invoke) : null);
    }

    public void PlayStart(string bmsFilePath, EventHandler onExitEventHandler = null)
    {
        lock (lockThis)
        {
            Process[] processesByName = Process.GetProcessesByName("BMIIDXView2015");
            Process[] processesByName2 = Process.GetProcessesByName("BMIIDXView2015_64");
            Process[] array = [.. processesByName, .. processesByName2];
            if (array.Length != 0)
            {
                if (BMIIDXView2015HandleShowing == IntPtr.Zero)
                {
                    Process[] array2 = array;
                    foreach (Process bMIIDXView2015Process in array2)
                    {
                        BMIIDXView2015Process = bMIIDXView2015Process;
                        CloseProcess();
                    }
                }
                else
                {
                    CloseProcess();
                }
                BMIIDXView2015HandleShowing = IntPtr.Zero;
                BMIIDXView2015Process = null;
            }
            var processStartInfo = new ProcessStartInfo(ExePath)
            {
                WindowStyle = ProcessWindowStyle.Minimized,
                Arguments = "-S \"" + bmsFilePath + "\""
            };
            BMIIDXView2015Process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };
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
            IntPtr foregroundWindow = Win32API.GetForegroundWindow();
            string iniFilePath = Path.Combine(DirectoryExt.GetDirectoryNameSimple(ExePath), "BMIIDXView2015.ini");
            temporarilyRewriteSettings(iniFilePath, playerSettingsGateway.CaptureSnapshot().PlayerVolume);
            BMIIDXView2015Process.Start();
            while (!BMIIDXView2015Process.HasExited && BMIIDXView2015Process.MainWindowHandle == IntPtr.Zero)
            {
                Thread.Sleep(50);
            }
            while (foregroundWindow != IntPtr.Zero && Win32API.IsWindow(foregroundWindow) && !Win32API.SetForegroundWindow(foregroundWindow))
            {
                Thread.Sleep(50);
            }
            if (BMIIDXView2015Process.HasExited)
            {
                throw new InvalidOperationException("BMIIDXView2015の起動に失敗しました。");
            }
            BMIIDXView2015HandleShowing = BMIIDXView2015Process.MainWindowHandle;
            Win32API.SetWindowLong(BMIIDXView2015HandleShowing, -16, 1342177280u);
            Win32API.SetParent(BMIIDXView2015HandleShowing, ParentHandle);
            Win32API.SetWindowPos(BMIIDXView2015HandleShowing, IntPtr.Zero, 0, 0, 587, 256, Win32API.SetWindowPosFlags.DoNotActivate | Win32API.SetWindowPosFlags.ShowWindow);
            Thread.Sleep(100);
            Win32API.PostMessage(new HandleRef(this, BMIIDXView2015HandleShowing), 1127u, IntPtr.Zero, IntPtr.Zero);
            while (foregroundWindow != IntPtr.Zero && Win32API.IsWindow(foregroundWindow) && !Win32API.SetForegroundWindow(foregroundWindow))
            {
                Thread.Sleep(50);
            }
            NLogWrapper.DebuggerLogger?.Trace("7 " + foregroundWindow + " " + Win32API.GetForegroundWindow());
            BMSFilePathPlaying = bmsFilePath;
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
        if (!(BMIIDXView2015HandleShowing != IntPtr.Zero))
        {
            return;
        }
        lock (lockThis)
        {
            IntPtr foregroundWindow = Win32API.GetForegroundWindow();
            while (Win32API.IsWindow(BMIIDXView2015HandleShowing) && (!Win32API.SetForegroundWindow(BMIIDXView2015HandleShowing) || Win32API.GetForegroundWindow() != BMIIDXView2015HandleShowing))
            {
                Thread.Sleep(10);
            }
            DirectInputSendKey.SendKey((short)code, DirectInputSendKey.KEYEVENTF.KEYEVENTF_KEYDOWN | KeyEventFlags[code], 0);
            NLogWrapper.DebuggerLogger?.Trace("pushed");
            Thread.Sleep(40);
            DirectInputSendKey.SendKey((short)code, DirectInputSendKey.KEYEVENTF.KEYEVENTF_KEYUP | KeyEventFlags[code], 0);
            while (Win32API.IsWindow(foregroundWindow) && (!Win32API.SetForegroundWindow(foregroundWindow) || Win32API.GetForegroundWindow() != foregroundWindow))
            {
                Thread.Sleep(10);
            }
        }
    }

    private void BMIIDXView2015Exited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
        }
        lock (lockThis)
        {
            BMIIDXView2015HandleShowing = IntPtr.Zero;
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
