using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using BeMusicSeeker.Properties;
using Livet;
using Ribbit.Windows;

namespace BeMusicSeeker.Models.LR2;

public class LR2body : NotificationObject, IBMSPlayer, INotifyPropertyChanged
{
    private LR2Config lr2Config;

    private string BMSFilePathPlaying;

    private bool isPausing;

    private Process LR2bodyProcess;

    private IntPtr LR2bodyHandleShowing = IntPtr.Zero;

    private EventHandler onExitEventHandlerRegstered;

    private EventHandler onExitEventHandlerDefault;

    private object lockThis = new object();

    private int winSizeX;

    private int winSizeY;

    private int volume = -1;

    private bool? isWinMode;

    private bool? isVolumeEnabled;

    private string _exePath;

    private IntPtr _parentHandle;

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

    public LR2body(string exePath, LR2Config config)
    {
        if (config == null)
        {
            throw new ArgumentNullException("config", "引数をnullに出来ません");
        }
        if (exePath == null)
        {
            throw new ArgumentNullException("exePath", "引数をnullに出来ません");
        }
        ExePath = exePath;
        lr2Config = config;
        onExitEventHandlerDefault = LR2bodyExited;
    }

    public void CloseProcess()
    {
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
            try
            {
                LR2bodyProcess.CloseMainWindow();
                int num = 0;
                while (!LR2bodyProcess.HasExited)
                {
                    Thread.Sleep(100);
                    if (num == 50)
                    {
                        try
                        {
                            LR2bodyProcess.Kill();
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
            isPausing = false;
            Process[] processesByName = Process.GetProcessesByName("LR2body");
            Process[] processesByName2 = Process.GetProcessesByName("LRHbody");
            List<Process> list = processesByName.Concat(processesByName2).ToList();
            if (list.Count != 0)
            {
                if (LR2bodyProcess == null)
                {
                    foreach (Process item in list)
                    {
                        LR2bodyProcess = item;
                        CloseProcess();
                    }
                }
                else
                {
                    CloseProcess();
                }
                LR2bodyHandleShowing = IntPtr.Zero;
                LR2bodyProcess = null;
            }
            ProcessStartInfo processStartInfo = new ProcessStartInfo(ExePath);
            processStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            processStartInfo.Arguments = "-A -NS \"" + bmsFilePath + "\"";
            LR2bodyProcess = new Process();
            LR2bodyProcess.StartInfo = processStartInfo;
            LR2bodyProcess.EnableRaisingEvents = true;
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
            setConfig((int)Settings.Default.LR2bodyResolution.X, (int)Settings.Default.LR2bodyResolution.Y, isWinMode: true, Settings.Default.uBMplayVolume);
            IntPtr foregroundWindow = Win32API.GetForegroundWindow();
            LR2bodyProcess.Start();
            DateTime now = DateTime.Now;
            while (!LR2bodyProcess.HasExited && LR2bodyProcess.MainWindowHandle == IntPtr.Zero)
            {
                if (DateTime.Now - now > new TimeSpan(0, 0, 5))
                {
                    if (onExitEventHandlerRegstered != null)
                    {
                        LR2bodyProcess.Exited -= onExitEventHandlerRegstered;
                    }
                    if (onExitEventHandlerDefault != null)
                    {
                        LR2bodyProcess.Exited -= onExitEventHandlerDefault;
                    }
                    LR2bodyProcess.Kill();
                    now = DateTime.Now;
                }
                Thread.Yield();
            }
            if (!LR2bodyProcess.HasExited)
            {
                LR2bodyHandleShowing = LR2bodyProcess.MainWindowHandle;
                setWindowStyle();
                restoreWindowPosition();
                while (foregroundWindow != IntPtr.Zero && Win32API.IsWindow(foregroundWindow) && !Win32API.SetForegroundWindow(foregroundWindow))
                {
                    Thread.Sleep(50);
                }
                BMSFilePathPlaying = bmsFilePath;
                restoreConfig(isTempClear: false);
                return;
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

    private void restoreWindowPosition()
    {
        if (LR2bodyHandleShowing != IntPtr.Zero && Settings.Default.IsSaveLR2bodyWindowPosition)
        {
            Win32API.WINDOWPLACEMENT lpwndpl = Settings.Default.LR2bodyWindowPlacement;
            lpwndpl.Length = Marshal.SizeOf(typeof(Win32API.WINDOWPLACEMENT));
            lpwndpl.Flags = 0;
            lpwndpl.ShowCmd = Win32API.ShowWindowCommands.Normal;
            lpwndpl.NormalPosition.Width = (int)Settings.Default.LR2bodyResolution.X;
            lpwndpl.NormalPosition.Height = (int)Settings.Default.LR2bodyResolution.Y;
            Win32API.SetWindowPlacement(LR2bodyHandleShowing, ref lpwndpl);
        }
    }

    private void storeWindowPosition()
    {
        if (LR2bodyHandleShowing != IntPtr.Zero)
        {
            Win32API.WINDOWPLACEMENT lpwndpl = default(Win32API.WINDOWPLACEMENT);
            Win32API.GetWindowPlacement(LR2bodyHandleShowing, ref lpwndpl);
            Settings.Default.LR2bodyWindowPlacement = lpwndpl;
            Settings.Default.Save();
        }
    }

    private void setWindowStyle()
    {
        if (LR2bodyHandleShowing != IntPtr.Zero)
        {
            uint num = 2495610880u;
            while (!LR2bodyProcess.HasExited && Win32API.GetWindowLong(LR2bodyHandleShowing, -16) != num)
            {
                Win32API.SetWindowLong(LR2bodyHandleShowing, -16, num);
                Thread.Yield();
            }
            Win32API.SetWindowLong(LR2bodyHandleShowing, -20, 129u);
        }
    }

    public void VolumeChanged()
    {
    }

    private void LR2bodyExited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
            LR2bodyHandleShowing = IntPtr.Zero;
            LR2bodyProcess = null;
            if (!isPausing)
            {
                BMSFilePathPlaying = null;
            }
            restoreConfig();
        }
    }
}
