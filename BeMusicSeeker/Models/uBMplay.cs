using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Ini;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using BeMusicSeeker.Models.Utils;
using BeMusicSeeker.Properties;
using Livet;
using Ribbit.Logging;
using Ribbit.Util.Extensions;
using Ribbit.Windows;

namespace BeMusicSeeker.Models;

public class uBMplay : NotificationObject, IBMSPlayer, INotifyPropertyChanged
{
    private enum KeyCode
    {
        NONE = 0,
        UP = 72,
        DOWN = 80,
        SPACE = 57,
        HOME = 71,
        END = 79,
        KEY0 = 11,
        KEY1 = 2,
        KEY2 = 3,
        KEY3 = 4,
        KEY4 = 5,
        KEY5 = 6,
        KEY6 = 7,
        KEY7 = 8,
        KEY8 = 9,
        KEY9 = 10,
        F1 = 59,
        F2 = 60
    }

    private class temporarilyRewriteSettings
    {
        private readonly IniDocument backup = new();

        private readonly IniDocument settings = new();

        private readonly string iniFilePath;

        private readonly IniSyntaxDefinition syntax = new()
        {
            CommentStartChar = ';',
            NameValueDelimiter = '=',
            QuoteChar = '"',
            RequireQuotes = false
        };

        private readonly IniWriterFormattingSettings format = new()
        {
            IndentParameters = false,
            SpaceBeforeDelimiter = false,
            SpaceAfterDelimiter = false,
            SpaceBeforeCommentStart = false,
            SpaceAfterCommentStart = false,
            SeparateSections = false
        };

        public void revertSettings()
        {
            if (backup == null)
            {
                return;
            }
            try
            {
                using var tw = new StreamWriter(iniFilePath, append: false, Encoding.GetEncoding("shift_jis"));
                backup.Save(tw, format);
            }
            catch
            {
            }
        }

        public temporarilyRewriteSettings(string iniFilePath)
        {
            this.iniFilePath = iniFilePath;
            try
            {
                backup.SyntaxDefinition = syntax;
                settings.SyntaxDefinition = syntax;
                using (var tr = new StreamReader(iniFilePath, Encoding.GetEncoding("shift_jis")))
                {
                    settings.Load(tr);
                }
                using var tr2 = new StreamReader(iniFilePath, Encoding.GetEncoding("shift_jis"));
                backup.Load(tr2);
            }
            catch
            {
                backup = null;
                settings = new IniDocument
                {
                    SyntaxDefinition = syntax
                };
            }
            if ((1u & (setSectionParameterValue("Main", "AlwaysOnTop", "False") ? 1u : 0u) & (setSectionParameterValue("Main", "VSYNC", "False") ? 1u : 0u) & (setSectionParameterValue("Option", "BGA", "3") ? 1u : 0u) & (setSectionParameterValue("Option", "AutoSeparate", "True") ? 1u : 0u) & (setSectionParameterValue("Option", "SkinType", "0") ? 1u : 0u) & (setSectionParameterValue("Option", "Volume", Math.Min(100, Math.Max(0, Settings.Default.uBMplayVolume)).ToString()) ? 1u : 0u)) == 0)
            {
                backup = null;
            }
            try
            {
                using var tw = new StreamWriter(iniFilePath, append: false, Encoding.GetEncoding("shift_jis"));
                settings.Save(tw, format);
            }
            catch
            {
                backup = null;
            }
        }

        private bool setSectionParameterValue(string sectionName, string parameterName, string value)
        {
            if (!settings.Sections.Any(s => s.Header == sectionName))
            {
                var iniSection = new IniSection(sectionName);
                iniSection.Parameters.Add(new IniParameter(parameterName, value));
                settings.Sections.Add(iniSection);
                return false;
            }
            if (!settings.Sections[sectionName].Parameters.Any(p => p.Name == parameterName))
            {
                settings.Sections[sectionName].AddParameter(parameterName, value);
                return false;
            }
            settings.Sections[sectionName].Parameters[parameterName].Value = value;
            return true;
        }
    }

    private Process uBMplayProcess;

    private IntPtr uBMplayHandleShowing = IntPtr.Zero;

    private IntPtr foregroundWindowHandle = IntPtr.Zero;

    private readonly uint uBMplayHandleWindowStatusOrg = 382337024u;

    private EventHandler onExitEventHandlerRegstered;

    private readonly EventHandler onExitEventHandlerDefault;

    private readonly bool IS_WIN8OR10 = Environment.OSVersion.IsLaterOrEqual(OperatingSystemExt.WindowsProductName.WindowsServer2012);

    private readonly object lockThis = new();

    private temporarilyRewriteSettings iniFile;

    private string _exePath;

    private IntPtr _parentHandle;

    private IntPtr CurrentFocusForKeyEvent;

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
            KeyCode.SPACE,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.HOME,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_EXTENDEDKEY | DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.END,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_EXTENDEDKEY | DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY0,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY1,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY2,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY3,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY4,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY5,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY6,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY7,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY8,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.KEY9,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.F1,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        },
        {
            KeyCode.F2,
            DirectInputSendKey.KEYEVENTF.KEYEVENTF_SCANCODE
        }
    };

    private KeyCode nowPressed;

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
                if (!File.Exists(value) || !(Path.GetFileName(value) == "uBMplay.exe"))
                {
                    throw new FileNotFoundException("実行ファイルが見つからないか、uBMplay.exe ではありません。", value);
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

    private List<IntPtr> uBMplayHandles()
    {
        return [.. (from ProcessThread pt in uBMplayProcess.Threads
                from wh in new ThreadWindowHandles((uint)pt.Id)
                where wh != IntPtr.Zero
                select wh)];
    }

    public uBMplay(string exePath)
    {
        ExePath = exePath;
        onExitEventHandlerDefault = uBMplayExited;
    }

    public void CloseProcess()
    {
        lock (lockThis)
        {
            if (uBMplayProcess == null)
            {
                return;
            }
            SendReleaseKeyEvent(KeyCode.NONE);
            if (onExitEventHandlerRegstered != null)
            {
                uBMplayProcess.Exited -= onExitEventHandlerRegstered;
            }
            if (onExitEventHandlerDefault != null)
            {
                uBMplayProcess.Exited -= onExitEventHandlerDefault;
            }
            try
            {
                uBMplayProcess.CloseMainWindow();
                int num = 0;
                while (!uBMplayProcess.HasExited)
                {
                    Thread.Sleep(100);
                    if (num == 50)
                    {
                        try
                        {
                            uBMplayProcess.Kill();
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
        if (!File.Exists(bmsFilePath))
        {
            throw new FileNotFoundException("BMS ファイルが見つかりません。", bmsFilePath);
        }
        if (string.IsNullOrEmpty(ExePath))
        {
            throw new FileNotFoundException("実行ファイルが見つかりません。", "");
        }
        string iniFilePath = Path.Combine(DirectoryExt.GetDirectoryNameSimple(ExePath), "ubm.ini");
        lock (lockThis)
        {
            iniFile = new temporarilyRewriteSettings(iniFilePath);
            createProcess(bmsFilePath, onExitEventHandler);
            waitForLoading(bmsFilePath);
            setParent();
        }
    }

    private bool createProcess(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        bool flag = false;
        foregroundWindowHandle = Win32API.GetForegroundWindow();
        Process[] processesByName = Process.GetProcessesByName("uBMplay");
        if (processesByName.Length != 0)
        {
            if (uBMplayHandleShowing == IntPtr.Zero)
            {
                uBMplayProcess = processesByName[0];
                CloseProcess();
            }
            else
            {
                if (!IS_WIN8OR10)
                {
                    Win32API.SetWindowPos(uBMplayHandleShowing, IntPtr.Zero, -9999, -9999, 587, 256, (Win32API.SetWindowPosFlags)0u);
                    Win32API.SetParent(uBMplayHandleShowing, IntPtr.Zero);
                    Win32API.SetWindowLong(uBMplayHandleShowing, -16, uBMplayHandleWindowStatusOrg);
                }
                flag = true;
            }
        }
        var processStartInfo = new ProcessStartInfo(ExePath);
        if (!IS_WIN8OR10)
        {
            processStartInfo.WindowStyle = ProcessWindowStyle.Minimized;
        }
        processStartInfo.Arguments = "-SP \"" + bmsFilePath + "\"";
        if (uBMplayHandleShowing == IntPtr.Zero)
        {
            uBMplayProcess = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };
            uBMplayProcess.Exited += onExitEventHandlerDefault;
            if (onExitEventHandler != null)
            {
                onExitEventHandlerRegstered = onExitEventHandler.Invoke;
                uBMplayProcess.Exited += onExitEventHandlerRegstered;
            }
            else
            {
                onExitEventHandlerRegstered = null;
            }
            uBMplayProcess.Start();
            var classname = new StringBuilder(4096);
            while (!uBMplayProcess.HasExited)
            {
                bool flag2 = uBMplayHandles().Any(delegate (IntPtr wh)
                {
                    Win32API.GetClassName(wh, classname, classname.Capacity);
                    if (classname.ToString() == "ThunderRT6FormDC")
                    {
                        uBMplayHandleShowing = wh;
                        if (!IS_WIN8OR10)
                        {
                            Win32API.SetWindowPos(uBMplayHandleShowing, IntPtr.Zero, -9999, -9999, 587, 256, (Win32API.SetWindowPosFlags)0u);
                        }
                        return true;
                    }
                    return false;
                });
                if (!(uBMplayHandleShowing == IntPtr.Zero))
                {
                    IntPtr foregroundWindow = Win32API.GetForegroundWindow();
                    NLogWrapper.DebuggerLogger?.Trace("1 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
                    if (uBMplayHandles().Contains(foregroundWindow))
                    {
                        Win32API.SetForegroundWindow((foregroundWindowHandle == IntPtr.Zero) ? ParentHandle : foregroundWindowHandle);
                    }
                    else if (foregroundWindow != IntPtr.Zero && foregroundWindow != uBMplayHandleShowing)
                    {
                        foregroundWindowHandle = foregroundWindow;
                    }
                    else
                    {
                        NLogWrapper.DebuggerLogger?.Trace("1 invalid!");
                    }
                    if (flag2)
                    {
                        break;
                    }
                }
            }
        }
        else
        {
            var process = Process.Start(processStartInfo);
            while (!process.HasExited)
            {
                if (!IS_WIN8OR10)
                {
                    Win32API.SetWindowPos(uBMplayHandleShowing, IntPtr.Zero, -9999, -9999, 587, 256, (Win32API.SetWindowPosFlags)0u);
                }
            }
        }
        return !flag;
    }

    private void waitForLoading(string bmsFilePath)
    {
        var stringBuilder = new StringBuilder(4096);
        var regex = new Regex(Regex.Escape(bmsFilePath));
        while (!uBMplayProcess.HasExited)
        {
            if (!Win32API.IsWindow(uBMplayHandleShowing))
            {
                CloseProcess();
                DispatcherMessageBox.Show("uBMplayを起動できませんでした。" + Environment.NewLine + "uBMplayが正常に動作するか確認してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Hand, MessageBoxResult.OK);
                return;
            }
            Win32API.GetWindowText(uBMplayHandleShowing, stringBuilder, stringBuilder.Capacity);
            if (!IS_WIN8OR10)
            {
                Win32API.SetWindowPos(uBMplayHandleShowing, IntPtr.Zero, -9999, -9999, 587, 256, (Win32API.SetWindowPosFlags)0u);
            }
            IntPtr foregroundWindow = Win32API.GetForegroundWindow();
            NLogWrapper.DebuggerLogger?.Trace("2 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            if (uBMplayHandles().Contains(foregroundWindow))
            {
                Win32API.SetForegroundWindow((foregroundWindowHandle == IntPtr.Zero) ? ParentHandle : foregroundWindowHandle);
            }
            else if (foregroundWindow != IntPtr.Zero && foregroundWindow != uBMplayHandleShowing)
            {
                foregroundWindowHandle = foregroundWindow;
            }
            else
            {
                NLogWrapper.DebuggerLogger?.Trace("2 invalid!");
            }
            if (regex.IsMatch(stringBuilder.ToString()))
            {
                break;
            }
        }
        if (IS_WIN8OR10)
        {
            while (!uBMplayProcess.HasExited && Win32API.GetForegroundWindow() != uBMplayHandleShowing)
            {
                Win32API.SetForegroundWindow(uBMplayHandleShowing);
            }
            while (!uBMplayProcess.HasExited && Win32API.GetForegroundWindow() != ((foregroundWindowHandle == IntPtr.Zero) ? ParentHandle : foregroundWindowHandle))
            {
                Win32API.SetForegroundWindow((foregroundWindowHandle == IntPtr.Zero) ? ParentHandle : foregroundWindowHandle);
            }
        }
    }

    private void setParent()
    {
        if (!(uBMplayHandleShowing != IntPtr.Zero) || !(ParentHandle != IntPtr.Zero))
        {
            return;
        }
        lock (lockThis)
        {
            IntPtr foregroundWindow = Win32API.GetForegroundWindow();
            NLogWrapper.DebuggerLogger?.Trace("3 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            if (uBMplayHandles().Contains(foregroundWindow))
            {
                Win32API.SetForegroundWindow((foregroundWindowHandle == IntPtr.Zero) ? ParentHandle : foregroundWindowHandle);
            }
            else if (foregroundWindow != IntPtr.Zero && foregroundWindow != uBMplayHandleShowing)
            {
                foregroundWindowHandle = foregroundWindow;
            }
            else
            {
                NLogWrapper.DebuggerLogger?.Trace("3 invalid");
            }
            if (!IS_WIN8OR10)
            {
                Win32API.SetWindowLong(uBMplayHandleShowing, -16, 1342177280u);
                Win32API.SetParent(uBMplayHandleShowing, ParentHandle);
                Win32API.SetWindowPos(uBMplayHandleShowing, IntPtr.Zero, 0, 0, 587, 256, (Win32API.SetWindowPosFlags)0u);
            }
            else
            {
                Win32API.SetWindowLong(uBMplayHandleShowing, -16, 281018368u);
            }
            foregroundWindow = Win32API.GetForegroundWindow();
            NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            while (foregroundWindowHandle != foregroundWindow)
            {
                Win32API.SetForegroundWindow((foregroundWindowHandle == IntPtr.Zero) ? ParentHandle : foregroundWindowHandle);
                foregroundWindow = Win32API.GetForegroundWindow();
                NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            }
            Win32API.SetFocus(foregroundWindowHandle);
            NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
        }
    }

    public void RestartPlayingBMSfile()
    {
        SendPressKeyEvent(KeyCode.HOME);
        SendReleaseKeyEvent(KeyCode.HOME);
    }

    public void PausePlayingBMSfileToggle()
    {
        SendPressKeyEvent(KeyCode.KEY0);
        SendReleaseKeyEvent(KeyCode.KEY0);
    }

    public void FastForwardPlayingBMSfileStart()
    {
        SendPressKeyEvent(KeyCode.DOWN);
    }

    public void FastForwardPlayingBMSfileEnd()
    {
        SendReleaseKeyEvent(KeyCode.DOWN);
    }

    public void FastBackwardPlayingBMSfileStart()
    {
        SendPressKeyEvent(KeyCode.UP);
    }

    public void FastBackwardPlayingBMSfileEnd()
    {
        SendReleaseKeyEvent(KeyCode.UP);
    }

    public void ShowInfo()
    {
        SendPressKeyEvent(KeyCode.KEY9);
        SendReleaseKeyEvent(KeyCode.KEY9);
    }

    public void ShowEffect()
    {
        SendPressKeyEvent(KeyCode.KEY6);
        SendReleaseKeyEvent(KeyCode.KEY6);
    }

    public void ChangePlayside()
    {
        SendPressKeyEvent(KeyCode.KEY8);
        SendReleaseKeyEvent(KeyCode.KEY8);
    }

    public void IncreaseHighSpeed()
    {
        SendPressKeyEvent(KeyCode.KEY2);
        SendReleaseKeyEvent(KeyCode.KEY2);
    }

    public void DecreaseHighSpeed()
    {
        SendPressKeyEvent(KeyCode.KEY1);
        SendReleaseKeyEvent(KeyCode.KEY1);
    }

    public void VolumeChanged()
    {
    }

    private void SendPressKeyEvent(KeyCode code)
    {
        if (!(uBMplayHandleShowing != IntPtr.Zero))
        {
            return;
        }
        lock (lockThis)
        {
            if (nowPressed == KeyCode.NONE)
            {
                nowPressed = code;
                CurrentFocusForKeyEvent = Win32API.GetForegroundWindow();
                for (int i = 0; i < 5; i++)
                {
                    Win32API.SetForegroundWindow(uBMplayHandleShowing);
                    Thread.Sleep(50);
                }
                DirectInputSendKey.SendKey((short)code, KeyEventFlags[code] | DirectInputSendKey.KEYEVENTF.KEYEVENTF_KEYDOWN, 0);
                NLogWrapper.DebuggerLogger?.Trace("pushed");
            }
        }
    }

    private void SendReleaseKeyEvent(KeyCode code)
    {
        lock (lockThis)
        {
            if (nowPressed == KeyCode.NONE)
            {
                return;
            }
            code = nowPressed;
            nowPressed = KeyCode.NONE;
            Thread.Sleep(50);
            DirectInputSendKey.SendKey((short)code, KeyEventFlags[code] | DirectInputSendKey.KEYEVENTF.KEYEVENTF_KEYUP, 0);
            if (CurrentFocusForKeyEvent != IntPtr.Zero)
            {
                for (int i = 0; i < 5; i++)
                {
                    Win32API.SetForegroundWindow(CurrentFocusForKeyEvent);
                    Thread.Sleep(50);
                }
                CurrentFocusForKeyEvent = IntPtr.Zero;
            }
            NLogWrapper.DebuggerLogger?.Trace("released");
        }
    }

    private void uBMplayExited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
            uBMplayHandleShowing = IntPtr.Zero;
            uBMplayProcess = null;
        }
    }
}
