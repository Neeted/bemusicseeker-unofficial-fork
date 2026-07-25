using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Ini;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using BeMusicSeeker.Models.Utils;
using Livet;
using Ribbit.Logging;

namespace BeMusicSeeker.Models;

public class uBMplay : NotificationObject, IBMSPlayer, IExternalWindowPlayer, INotifyPropertyChanged
{
    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IExternalPlayerProcessGateway processGateway;

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

        public temporarilyRewriteSettings(string iniFilePath, int playerVolume)
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
            if ((1u & (setSectionParameterValue("Main", "AlwaysOnTop", "False") ? 1u : 0u) & (setSectionParameterValue("Main", "VSYNC", "False") ? 1u : 0u) & (setSectionParameterValue("Option", "BGA", "3") ? 1u : 0u) & (setSectionParameterValue("Option", "AutoSeparate", "True") ? 1u : 0u) & (setSectionParameterValue("Option", "SkinType", "0") ? 1u : 0u) & (setSectionParameterValue("Option", "Volume", Math.Min(100, Math.Max(0, playerVolume)).ToString()) ? 1u : 0u)) == 0)
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

    private IExternalPlayerProcessSession uBMplayProcess;

    private ExternalWindowHandle uBMplayHandleShowing;

    private ExternalWindowHandle foregroundWindowHandle;

    private EventHandler onExitEventHandlerRegstered;

    private readonly EventHandler onExitEventHandlerDefault;

    private readonly object lockThis = new();

    private temporarilyRewriteSettings iniFile;

    private string _exePath;

    private IExternalPlayerWindowHost windowHost;

    private ExternalWindowHandle CurrentFocusForKeyEvent;

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
            KeyCode.SPACE,
            false
        },
        {
            KeyCode.HOME,
            true
        },
        {
            KeyCode.END,
            true
        },
        {
            KeyCode.KEY0,
            false
        },
        {
            KeyCode.KEY1,
            false
        },
        {
            KeyCode.KEY2,
            false
        },
        {
            KeyCode.KEY3,
            false
        },
        {
            KeyCode.KEY4,
            false
        },
        {
            KeyCode.KEY5,
            false
        },
        {
            KeyCode.KEY6,
            false
        },
        {
            KeyCode.KEY7,
            false
        },
        {
            KeyCode.KEY8,
            false
        },
        {
            KeyCode.KEY9,
            false
        },
        {
            KeyCode.F1,
            false
        },
        {
            KeyCode.F2,
            false
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

    private IReadOnlyList<ExternalWindowHandle> uBMplayHandles()
    {
        return RequireWindowHost().EnumerateThreadWindows(uBMplayProcess.ThreadIds);
    }

    internal uBMplay(
        string exePath,
        IPlayerSettingsGateway playerSettingsGateway,
        IExternalPlayerProcessGateway processGateway)
    {
        ExePath = exePath;
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.processGateway = processGateway ?? throw new ArgumentNullException(nameof(processGateway));
        onExitEventHandlerDefault = uBMplayExited;
    }

    void IExternalWindowPlayer.AttachWindowHost(IExternalPlayerWindowHost windowHost)
    {
        this.windowHost = windowHost ?? throw new ArgumentNullException(nameof(windowHost));
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
            iniFile = new temporarilyRewriteSettings(iniFilePath, playerSettingsGateway.CaptureSnapshot().PlayerVolume);
            bool startedNewProcess = createProcess(bmsFilePath, onExitEventHandler);
            if (startedNewProcess && (uBMplayProcess == null || uBMplayProcess.HasExited || !RequireWindowHost().IsWindow(uBMplayHandleShowing)))
            {
                ThrowStartupFailed();
            }
            waitForLoading(bmsFilePath);
            setParent();
        }
    }

    private static void ThrowStartupFailed()
    {
        throw new InvalidOperationException("uBMplayを起動できませんでした。" + Environment.NewLine + "uBMplayが正常に動作するか確認してください。");
    }

    private bool createProcess(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        bool flag = false;
        foregroundWindowHandle = RequireWindowHost().GetForegroundWindow();
        IReadOnlyList<IExternalPlayerProcessSession> existingProcesses = processGateway.FindExisting(
            ExternalPlayerProcessDiscoveryRequest.Create("uBMplay"));
        if (existingProcesses.Count != 0)
        {
            if (uBMplayHandleShowing.IsEmpty)
            {
                uBMplayProcess = existingProcesses[0];
                CloseProcess();
            }
            else
            {
                if (RequireWindowHost().UsesLegacyWindowEmbedding)
                {
                    RequireWindowHost().DetachUbmplayWindow(uBMplayHandleShowing);
                }
                flag = true;
            }
        }
        ExternalPlayerProcessLaunchRequest launchRequest = ExternalPlayerProcessLaunchRequest.Create(
            ExePath,
            "-SP \"" + bmsFilePath + "\"",
            !RequireWindowHost().UsesLegacyWindowEmbedding
                ? System.Diagnostics.ProcessWindowStyle.Normal
                : System.Diagnostics.ProcessWindowStyle.Minimized);
        if (uBMplayHandleShowing.IsEmpty)
        {
            uBMplayProcess = processGateway.Prepare(launchRequest);
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
            while (!uBMplayProcess.HasExited)
            {
                bool flag2 = uBMplayHandles().Any(delegate (ExternalWindowHandle wh)
                {
                    if (RequireWindowHost().GetClassName(wh) == "ThunderRT6FormDC")
                    {
                        uBMplayHandleShowing = wh;
                        if (RequireWindowHost().UsesLegacyWindowEmbedding)
                        {
                            RequireWindowHost().MoveExternalWindowOffscreen(uBMplayHandleShowing);
                        }
                        return true;
                    }
                    return false;
                });
                if (!uBMplayHandleShowing.IsEmpty)
                {
                    ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
                    NLogWrapper.DebuggerLogger?.Trace("1 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
                    if (uBMplayHandles().Contains(foregroundWindow))
                    {
                        RequireWindowHost().SetForegroundWindow((foregroundWindowHandle.IsEmpty) ? RequireWindowHost().ParentHandle : foregroundWindowHandle);
                    }
                    else if (!foregroundWindow.IsEmpty && foregroundWindow != uBMplayHandleShowing)
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
            IExternalPlayerProcessSession process = processGateway.Prepare(launchRequest);
            process.Start();
            while (!process.HasExited)
            {
                if (RequireWindowHost().UsesLegacyWindowEmbedding)
                {
                    RequireWindowHost().MoveExternalWindowOffscreen(uBMplayHandleShowing);
                }
            }
        }
        return !flag;
    }

    private void waitForLoading(string bmsFilePath)
    {
        if (uBMplayProcess == null || uBMplayProcess.HasExited)
        {
            ThrowStartupFailed();
        }
        var stringBuilder = new StringBuilder(4096);
        var regex = new Regex(Regex.Escape(bmsFilePath));
        bool loaded = false;
        while (!uBMplayProcess.HasExited)
        {
            if (!RequireWindowHost().IsWindow(uBMplayHandleShowing))
            {
                CloseProcess();
                ThrowStartupFailed();
            }
            stringBuilder.Clear();
            stringBuilder.Append(RequireWindowHost().GetWindowText(uBMplayHandleShowing));
            if (RequireWindowHost().UsesLegacyWindowEmbedding)
            {
                RequireWindowHost().MoveExternalWindowOffscreen(uBMplayHandleShowing);
            }
            ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
            NLogWrapper.DebuggerLogger?.Trace("2 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            if (uBMplayHandles().Contains(foregroundWindow))
            {
                RequireWindowHost().SetForegroundWindow((foregroundWindowHandle.IsEmpty) ? RequireWindowHost().ParentHandle : foregroundWindowHandle);
            }
            else if (!foregroundWindow.IsEmpty && foregroundWindow != uBMplayHandleShowing)
            {
                foregroundWindowHandle = foregroundWindow;
            }
            else
            {
                NLogWrapper.DebuggerLogger?.Trace("2 invalid!");
            }
            if (regex.IsMatch(stringBuilder.ToString()))
            {
                loaded = true;
                break;
            }
        }
        if (!loaded)
        {
            ThrowStartupFailed();
        }
        if (!RequireWindowHost().UsesLegacyWindowEmbedding)
        {
            while (!uBMplayProcess.HasExited && RequireWindowHost().GetForegroundWindow() != uBMplayHandleShowing)
            {
                RequireWindowHost().SetForegroundWindow(uBMplayHandleShowing);
            }
            while (!uBMplayProcess.HasExited && RequireWindowHost().GetForegroundWindow() != ((foregroundWindowHandle.IsEmpty) ? RequireWindowHost().ParentHandle : foregroundWindowHandle))
            {
                RequireWindowHost().SetForegroundWindow((foregroundWindowHandle.IsEmpty) ? RequireWindowHost().ParentHandle : foregroundWindowHandle);
            }
        }
    }

    private void setParent()
    {
        if (uBMplayHandleShowing.IsEmpty || RequireWindowHost().ParentHandle.IsEmpty)
        {
            return;
        }
        lock (lockThis)
        {
            ExternalWindowHandle foregroundWindow = RequireWindowHost().GetForegroundWindow();
            NLogWrapper.DebuggerLogger?.Trace("3 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            if (uBMplayHandles().Contains(foregroundWindow))
            {
                RequireWindowHost().SetForegroundWindow((foregroundWindowHandle.IsEmpty) ? RequireWindowHost().ParentHandle : foregroundWindowHandle);
            }
            else if (!foregroundWindow.IsEmpty && foregroundWindow != uBMplayHandleShowing)
            {
                foregroundWindowHandle = foregroundWindow;
            }
            else
            {
                NLogWrapper.DebuggerLogger?.Trace("3 invalid");
            }
            if (RequireWindowHost().UsesLegacyWindowEmbedding)
            {
                RequireWindowHost().AttachUbmplayWindow(uBMplayHandleShowing, legacyWindowStyle: true);
            }
            else
            {
                RequireWindowHost().AttachUbmplayWindow(uBMplayHandleShowing, legacyWindowStyle: false);
            }
            foregroundWindow = RequireWindowHost().GetForegroundWindow();
            NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            while (foregroundWindowHandle != foregroundWindow)
            {
                RequireWindowHost().SetForegroundWindow((foregroundWindowHandle.IsEmpty) ? RequireWindowHost().ParentHandle : foregroundWindowHandle);
                foregroundWindow = RequireWindowHost().GetForegroundWindow();
                NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
            }
            RequireWindowHost().SetFocus(foregroundWindowHandle);
            NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
        }
    }

    private IExternalPlayerWindowHost RequireWindowHost()
    {
        return windowHost ?? throw new InvalidOperationException("uBMplayの再生ホストが接続されていません。");
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
        if (uBMplayHandleShowing.IsEmpty)
        {
            return;
        }
        lock (lockThis)
        {
            if (nowPressed == KeyCode.NONE)
            {
                nowPressed = code;
                CurrentFocusForKeyEvent = RequireWindowHost().GetForegroundWindow();
                for (int i = 0; i < 5; i++)
                {
                    RequireWindowHost().SetForegroundWindow(uBMplayHandleShowing);
                    Thread.Sleep(50);
                }
                RequireWindowHost().SendKey((short)code, KeyEventFlags[code], keyDown: true);
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
            RequireWindowHost().SendKey((short)code, KeyEventFlags[code], keyDown: false);
            if (!CurrentFocusForKeyEvent.IsEmpty)
            {
                for (int i = 0; i < 5; i++)
                {
                    RequireWindowHost().SetForegroundWindow(CurrentFocusForKeyEvent);
                    Thread.Sleep(50);
                }
                CurrentFocusForKeyEvent = default;
            }
            NLogWrapper.DebuggerLogger?.Trace("released");
        }
    }

    private void uBMplayExited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
            uBMplayHandleShowing = default;
            uBMplayProcess = null;
        }
    }
}
