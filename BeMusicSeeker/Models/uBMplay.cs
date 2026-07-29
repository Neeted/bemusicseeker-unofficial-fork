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

public class uBMplay : ObservableObject, IBMSPlayer, IExternalWindowPlayer, INotifyPropertyChanged
{
    private readonly IPlayerSettingsGateway playerSettingsGateway;

    private readonly IExternalPlayerProcessGateway processGateway;

    private readonly ExternalPlayerWaitPolicy waitPolicy;

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

    internal sealed class TemporarilyRewriteSettings
    {
        private readonly string iniFilePath;

        private readonly byte[] backupContents;

        internal bool RevertSettings()
        {
            if (backupContents == null)
            {
                return true;
            }
            try
            {
                File.WriteAllBytes(iniFilePath, backupContents);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal TemporarilyRewriteSettings(string iniFilePath, int playerVolume)
        {
            this.iniFilePath = iniFilePath;
            byte[] originalContents = null;
            string input = string.Empty;
            string newline = Environment.NewLine;
            bool trailingNewline = true;
            try
            {
                originalContents = File.ReadAllBytes(iniFilePath);
                input = File.ReadAllText(iniFilePath, Encoding.GetEncoding("shift_jis"));
                newline = input.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                trailingNewline = input.EndsWith("\n", StringComparison.Ordinal);
            }
            catch
            {
                originalContents = null;
            }

            List<string> lines = input.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
            if (trailingNewline && lines.Count > 0 && lines[^1].Length == 0)
            {
                lines.RemoveAt(lines.Count - 1);
            }
            bool allParametersExisted = true;
            allParametersExisted &= SetSectionParameterValue(lines, "Main", "AlwaysOnTop", "False");
            allParametersExisted &= SetSectionParameterValue(lines, "Main", "VSYNC", "False");
            allParametersExisted &= SetSectionParameterValue(lines, "Option", "BGA", "3");
            allParametersExisted &= SetSectionParameterValue(lines, "Option", "AutoSeparate", "True");
            allParametersExisted &= SetSectionParameterValue(lines, "Option", "SkinType", "0");
            allParametersExisted &= SetSectionParameterValue(
                lines,
                "Option",
                "Volume",
                Math.Min(100, Math.Max(0, playerVolume)).ToString());

            try
            {
                string output = string.Join(newline, lines);
                if (trailingNewline)
                {
                    output += newline;
                }
                File.WriteAllText(iniFilePath, output, Encoding.GetEncoding("shift_jis"));
            }
            catch
            {
                originalContents = null;
            }
            backupContents = allParametersExisted ? originalContents : null;
        }

        private static bool SetSectionParameterValue(
            List<string> lines,
            string sectionName,
            string parameterName,
            string value)
        {
            int sectionStart = FindSectionStart(lines, sectionName);
            if (sectionStart < 0)
            {
                if (lines.Count > 0 && lines[^1].Length != 0)
                {
                    lines.Add(string.Empty);
                }
                lines.Add("[" + sectionName + "]");
                lines.Add(parameterName + "=" + value);
                return false;
            }

            int sectionEnd = FindNextSectionStart(lines, sectionStart + 1);
            for (int index = sectionStart + 1; index < sectionEnd; index++)
            {
                string line = lines[index];
                int delimiter = line.IndexOf('=');
                if (delimiter < 0 || !string.Equals(line[..delimiter].Trim(), parameterName, StringComparison.Ordinal))
                {
                    continue;
                }
                string suffix = line[(delimiter + 1)..];
                int comment = suffix.IndexOf(';');
                if (comment >= 0)
                {
                    int commentStart = comment;
                    while (commentStart > 0 && char.IsWhiteSpace(suffix[commentStart - 1]))
                    {
                        commentStart--;
                    }
                    suffix = suffix[commentStart..];
                }
                else
                {
                    suffix = string.Empty;
                }
                lines[index] = line[..(delimiter + 1)] + value + suffix;
                return true;
            }

            lines.Insert(sectionEnd, parameterName + "=" + value);
            return false;
        }

        private static int FindSectionStart(List<string> lines, string sectionName)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                string line = lines[index].Trim();
                if (line.Length > 2
                    && line[0] == '['
                    && line[^1] == ']'
                    && string.Equals(line[1..^1].Trim(), sectionName, StringComparison.Ordinal))
                {
                    return index;
                }
            }
            return -1;
        }

        private static int FindNextSectionStart(List<string> lines, int start)
        {
            for (int index = start; index < lines.Count; index++)
            {
                string line = lines[index].Trim();
                if (line.Length > 2 && line[0] == '[' && line[^1] == ']')
                {
                    return index;
                }
            }
            return lines.Count;
        }
    }

    private IExternalPlayerProcessSession uBMplayProcess;

    private IExternalPlayerProcessSession uBMplayRequestProcess;

    private ExternalWindowHandle uBMplayHandleShowing;

    private ExternalWindowHandle foregroundWindowHandle;

    private EventHandler onExitEventHandlerRegstered;

    private readonly EventHandler onExitEventHandlerDefault;

    private readonly object lockThis = new();

    private TemporarilyRewriteSettings iniFile;

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
        IExternalPlayerProcessGateway processGateway,
        ExternalPlayerWaitPolicy waitPolicy = null)
    {
        ExePath = exePath;
        this.playerSettingsGateway = playerSettingsGateway
            ?? throw new ArgumentNullException(nameof(playerSettingsGateway));
        this.processGateway = processGateway ?? throw new ArgumentNullException(nameof(processGateway));
        this.waitPolicy = waitPolicy ?? ExternalPlayerWaitPolicy.Default;
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
            Exception requestFailure = null;
            Exception playerFailure = null;
            try
            {
                CloseRequestProcessLocked();
            }
            catch (Exception exception)
            {
                requestFailure = exception;
            }
            try
            {
                CloseProcessLocked(restoreSettings: true);
            }
            catch (Exception exception)
            {
                playerFailure = exception;
            }
            if (requestFailure != null && playerFailure != null)
            {
                throw new AggregateException(
                    "uBMplay player and request processes could not be terminated.",
                    requestFailure,
                    playerFailure);
            }
            if (requestFailure != null)
            {
                throw new InvalidOperationException(
                    "uBMplay request process could not be terminated.",
                    requestFailure);
            }
            if (playerFailure != null)
            {
                throw playerFailure;
            }
        }
    }

    private void CloseProcessLocked(bool restoreSettings)
    {
        if (uBMplayProcess == null)
        {
            if (restoreSettings)
            {
                TryRevertSettingsWhenNoOwnedProcessLocked();
            }
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
        Exception closeFailure = null;
        try
        {
            uBMplayProcess.CloseMainWindow();
            waitPolicy.WaitForProcessExit(
                () => uBMplayProcess.HasExited,
                uBMplayProcess.Kill,
                "uBMplay did not terminate after graceful close and kill.");
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }
        if (closeFailure != null)
        {
            if (onExitEventHandlerDefault != null)
            {
                uBMplayProcess.Exited += onExitEventHandlerDefault;
            }
            if (onExitEventHandlerRegstered != null)
            {
                uBMplayProcess.Exited += onExitEventHandlerRegstered;
            }
            throw new InvalidOperationException("uBMplay could not be terminated.", closeFailure);
        }
        uBMplayHandleShowing = default;
        uBMplayProcess = null;
        if (restoreSettings)
        {
            TryRevertSettingsWhenNoOwnedProcessLocked();
        }
    }

    public Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
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
            CloseExitedRequestProcessLocked();
            if (uBMplayRequestProcess != null)
            {
                CloseRequestProcessLocked();
            }
            if (TryRevertSettingsWhenNoOwnedProcessLocked())
            {
                iniFile = new TemporarilyRewriteSettings(iniFilePath, playerSettingsGateway.CaptureSnapshot().PlayerVolume);
            }
            try
            {
                bool startedNewProcess = createProcess(bmsFilePath, onExitEventHandler);
                if (startedNewProcess && (uBMplayProcess == null || uBMplayProcess.HasExited || !RequireWindowHost().IsWindow(uBMplayHandleShowing)))
                {
                    ThrowStartupFailed();
                }
                waitForLoading(bmsFilePath);
                setParent();
            }
            catch (Exception startupFailure)
            {
                try
                {
                    CloseProcess();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "uBMplay startup failed and its process cleanup could not be completed.",
                        startupFailure,
                        cleanupFailure);
                }
                throw;
            }
        }
        return Task.CompletedTask;
    }

    private bool RevertSettingsLocked()
    {
        if (iniFile == null)
        {
            return true;
        }
        if (!iniFile.RevertSettings())
        {
            return false;
        }
        iniFile = null;
        return true;
    }

    private bool TryRevertSettingsWhenNoOwnedProcessLocked()
    {
        CloseExitedRequestProcessLocked();
        if (uBMplayProcess != null && !uBMplayProcess.HasExited)
        {
            return false;
        }
        if (uBMplayRequestProcess != null)
        {
            return false;
        }
        return RevertSettingsLocked();
    }

    private void CloseExitedRequestProcessLocked()
    {
        if (uBMplayRequestProcess?.HasExited == true)
        {
            CompleteRequestProcessExitLocked(uBMplayRequestProcess);
        }
    }

    private void CloseRequestProcessLocked()
    {
        IExternalPlayerProcessSession process = uBMplayRequestProcess;
        if (process == null)
        {
            return;
        }
        if (!process.HasExited)
        {
            process.CloseMainWindow();
            waitPolicy.WaitForProcessExit(
                () => process.HasExited,
                process.Kill,
                "uBMplay request process did not terminate after graceful close and kill.");
        }
        CompleteRequestProcessExitLocked(process);
    }

    private void CompleteRequestProcessExitLocked(IExternalPlayerProcessSession process)
    {
        if (!ReferenceEquals(uBMplayRequestProcess, process))
        {
            return;
        }
        process.Exited -= uBMplayRequestExited;
        uBMplayRequestProcess = null;
        TryRevertSettingsWhenNoOwnedProcessLocked();
    }

    private void uBMplayRequestExited(object sender, EventArgs e)
    {
        lock (lockThis)
        {
            if (sender is IExternalPlayerProcessSession process)
            {
                CompleteRequestProcessExitLocked(process);
            }
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
                CloseProcessLocked(restoreSettings: false);
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
            waitPolicy.WaitUntil(
                () =>
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
                            return true;
                        }
                    }
                    return false;
                },
                () => uBMplayProcess.HasExited,
                () => { },
                "uBMplayのメインウィンドウ待機がタイムアウトしました。",
                pollMilliseconds: 0);
        }
        else
        {
            IExternalPlayerProcessSession process = processGateway.Prepare(launchRequest);
            uBMplayRequestProcess = process;
            process.Exited += uBMplayRequestExited;
            try
            {
                process.Start();
                waitPolicy.WaitUntil(
                    () => process.HasExited,
                    aborted: null,
                    () =>
                    {
                        if (RequireWindowHost().UsesLegacyWindowEmbedding)
                        {
                            RequireWindowHost().MoveExternalWindowOffscreen(uBMplayHandleShowing);
                        }
                    },
                    "uBMplayの再生request受付待機がタイムアウトしました。",
                    pollMilliseconds: 0);
                CompleteRequestProcessExitLocked(process);
            }
            catch (Exception requestFailure)
            {
                try
                {
                    CloseRequestProcessLocked();
                }
                catch (Exception cleanupFailure)
                {
                    throw new AggregateException(
                        "uBMplay playback request failed and its process cleanup could not be completed.",
                        requestFailure,
                        cleanupFailure);
                }
                throw;
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
        waitPolicy.WaitUntil(
            () =>
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
                    return true;
                }
                return false;
            },
            () => uBMplayProcess.HasExited,
            () => { },
            "uBMplayの譜面読み込み待機がタイムアウトしました。",
            pollMilliseconds: 0);
        if (!loaded)
        {
            ThrowStartupFailed();
        }
        if (!RequireWindowHost().UsesLegacyWindowEmbedding)
        {
            FocusWindow(uBMplayHandleShowing, "uBMplayのforeground待機がタイムアウトしました。");
            FocusWindow(
                foregroundWindowHandle.IsEmpty ? RequireWindowHost().ParentHandle : foregroundWindowHandle,
                "uBMplay起動後のforeground復元がタイムアウトしました。");
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
            ExternalWindowHandle restoreWindow = foregroundWindowHandle.IsEmpty
                ? RequireWindowHost().ParentHandle
                : foregroundWindowHandle;
            FocusWindow(restoreWindow, "uBMplay attach後のforeground復元がタイムアウトしました。");
            foregroundWindow = RequireWindowHost().GetForegroundWindow();
            RequireWindowHost().SetFocus(foregroundWindowHandle);
            NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
        }
    }

    private IExternalPlayerWindowHost RequireWindowHost()
    {
        return windowHost ?? throw new InvalidOperationException("uBMplayの再生ホストが接続されていません。");
    }

    private void FocusWindow(ExternalWindowHandle window, string timeoutMessage)
    {
        if (window.IsEmpty || !RequireWindowHost().IsWindow(window))
        {
            return;
        }
        waitPolicy.WaitUntil(
            () => RequireWindowHost().GetForegroundWindow() == window,
            () => uBMplayProcess == null || uBMplayProcess.HasExited || !RequireWindowHost().IsWindow(window),
            () => RequireWindowHost().SetForegroundWindow(window),
            timeoutMessage,
            pollMilliseconds: 0);
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
            if (sender is IExternalPlayerProcessSession process
                && !ReferenceEquals(uBMplayProcess, process))
            {
                return;
            }
            uBMplayHandleShowing = default;
            if (uBMplayProcess != null)
            {
                uBMplayProcess.Exited -= onExitEventHandlerDefault;
                if (onExitEventHandlerRegstered != null)
                {
                    uBMplayProcess.Exited -= onExitEventHandlerRegstered;
                }
            }
            uBMplayProcess = null;
            TryRevertSettingsWhenNoOwnedProcessLocked();
        }
    }
}
