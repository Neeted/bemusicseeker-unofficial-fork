using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
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

    // 固定Exitedハンドラーの同期再入から、最外要求のロック解放後へ結果を渡す間だけ参照します。
    private ExitNotificationScope activeExitScope;

    private sealed class ExitNotificationScope
    {
        internal readonly List<(Action<object, EventArgs> Callback, object Sender, EventArgs Args, Exception Failure)> Notifications = [];
        internal readonly Dictionary<TemporarilyRewriteSettings, Exception> RestorationResults = [];
    }

    /// <summary>最外要求だけが結果scopeを所有し、同期再入は借用します。例外終了でもロック解放後に確定順で通知し、同じ返却故障の通知は開始・停止の呼出元へ渡します。</summary>
    /// <param name="action">lock内で実行する既存要求。</param>
    private void ExecuteWithExitNotifications(Action action)
    {
        ExitNotificationScope ownedScope = null;
        Exception propagatedFailure = null;
        try
        {
            lock (lockThis)
            {
                if (activeExitScope != null) { action(); return; }
                ownedScope = new ExitNotificationScope();
                activeExitScope = ownedScope;
                try { action(); }
                finally { activeExitScope = null; }
            }
        }
        catch (Exception failure)
        {
            propagatedFailure = failure;
            if (ownedScope != null && ownedScope.Notifications.Any(result => result.Failure != null && ContainsFailure(failure, result.Failure)))
            {
                propagatedFailure = new PlaybackExitFailureException(failure);
                throw propagatedFailure;
            }
            throw;
        }
        finally
        {
            if (ownedScope != null)
            {
                foreach ((Action<object, EventArgs> Callback, object Sender, EventArgs Args, Exception Failure) result in ownedScope.Notifications)
                {
                    bool handledByCaller = result.Failure != null && ContainsFailure(propagatedFailure, result.Failure);
                    NotifyExitSafely(result.Callback, result.Sender, result.Args, result.Failure, handledByCaller);
                }
            }
        }

        // この要求が返す原因だけを照合し、他の終了故障や後続要求の通知所有は変えません。
        static bool ContainsFailure(Exception propagated, Exception failure) => propagated != null
            && (ReferenceEquals(propagated, failure)
                || (propagated is AggregateException aggregate
                    ? aggregate.InnerExceptions.Any(inner => ContainsFailure(inner, failure))
                    : ContainsFailure(propagated.InnerException, failure)));
    }

    /// <summary>HasExitedの同期通知が確定した復元失敗も呼出元へ返し、同じbackupを同じ要求内で再試行しません。</summary>
    private bool HasExitedLocked(IExternalPlayerProcessSession process)
    {
        bool exited = process.HasExited;
        ThrowKnownRestorationFailureLocked();
        return exited;
    }

    private void ThrowKnownRestorationFailureLocked()
    {
        if (iniFile != null && activeExitScope.RestorationResults.TryGetValue(iniFile, out Exception failure)
            && failure != null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private void CaptureExitNotificationLocked(object sender, EventArgs args, Exception failure)
    {
        Action<object, EventArgs> callback = onExitCallback;
        onExitCallback = null;
        if (callback != null) { activeExitScope.Notifications.Add((callback, sender, args, failure)); }
    }

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

    /// <summary>一時値の原子的公開と、補完保存を除く元バイト列の復元を所有します。</summary>
    internal sealed class TemporarilyRewriteSettings
    {
        private readonly string iniFilePath;

        private readonly byte[] backupContents;

        /// <summary>保持した元のバイト列を原子的に復元します。失敗は呼出元へ返し、所有権を解除しません。</summary>
        internal void RevertSettings()
        {
            if (backupContents != null)
            {
                AtomicFileWriter.Write(iniFilePath, stream => stream.Write(backupContents));
            }
        }

        /// <summary>既存INIを読み、一時値を原子的に公開します。不在の場合だけ不足節を新規生成します。</summary>
        internal TemporarilyRewriteSettings(string iniFilePath, int playerVolume)
        {
            this.iniFilePath = iniFilePath;
            byte[] originalContents = null;
            try { originalContents = File.ReadAllBytes(iniFilePath); }
            catch (FileNotFoundException) { }
            string input = string.Empty;
            if (originalContents != null)
            {
                // File.ReadAllTextが行っていたBOM検出も、取得した同じbytesから維持します。
                using var reader = new StreamReader(new MemoryStream(originalContents), Encoding.GetEncoding("shift_jis"));
                input = reader.ReadToEnd();
            }
            string newline = originalContents == null ? Environment.NewLine
                : input.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            bool trailingNewline = originalContents == null || input.EndsWith("\n", StringComparison.Ordinal);

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

            string output = string.Join(newline, lines);
            if (trailingNewline) { output += newline; }
            byte[] rewritten = Encoding.GetEncoding("shift_jis").GetBytes(output);
            AtomicFileWriter.Write(iniFilePath, stream => stream.Write(rewritten));
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

    private Action<object, EventArgs> onExitCallback;

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
                    throw new FileNotFoundException(string.Format(BeMusicSeeker.Properties.Resources.Error_InvalidPlayerExecutableFormat, "uBMplay.exe"), value);
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
    }

    void IExternalWindowPlayer.AttachWindowHost(IExternalPlayerWindowHost windowHost)
    {
        this.windowHost = windowHost ?? throw new ArgumentNullException(nameof(windowHost));
    }

    /// <summary>所有processの終了を確認して設定を復元します。実復元失敗はbackupを保持して返し、次の明示操作で再試行します。</summary>
    public void CloseProcess() => ExecuteWithExitNotifications(CloseProcessCoreLocked);

    private void CloseProcessCoreLocked()
    {
        ThrowKnownRestorationFailureLocked();
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
            if (ReferenceEquals(requestFailure, playerFailure)) { ExceptionDispatchInfo.Capture(requestFailure).Throw(); }
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
            ExceptionDispatchInfo.Capture(playerFailure).Throw();
        }
        // 終了に失敗したprocessの遅い自然終了は、同じ要求のcallbackで観測し続けます。
        onExitCallback = null;
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
        uBMplayProcess.Exited -= uBMplayExited;
        Exception closeFailure = null;
        try
        {
            uBMplayProcess.CloseMainWindow();
            waitPolicy.WaitForProcessExit(
                () => HasExitedLocked(uBMplayProcess),
                uBMplayProcess.Kill,
                "uBMplay did not terminate after graceful close and kill.");
        }
        catch (Exception exception)
        {
            closeFailure = exception;
        }
        if (closeFailure != null)
        {
            uBMplayProcess.Exited += uBMplayExited;
            throw new InvalidOperationException("uBMplay could not be terminated.", closeFailure);
        }
        uBMplayHandleShowing = default;
        uBMplayProcess = null;
        if (restoreSettings)
        {
            TryRevertSettingsWhenNoOwnedProcessLocked();
        }
    }

    /// <summary>INIの原子的準備後に起動・再利用します。自然終了の復元結果は現在要求のcallbackへロック外で一度渡します。</summary>
    /// <remarks>準備・復元の実失敗では新規起動しません。同じ復元故障を開始側へ返す場合は終了引数で通知所有を開始側へ渡します。返却Taskは開始境界で完了します。</remarks>
    public Task PlayStart(string bmsFilePath, Action<object, EventArgs> onExitEventHandler = null)
    {
        if (!File.Exists(bmsFilePath))
        {
            throw new FileNotFoundException(BeMusicSeeker.Properties.Resources.Error_BmsFileNotFound, bmsFilePath);
        }
        if (string.IsNullOrEmpty(ExePath))
        {
            throw new FileNotFoundException(BeMusicSeeker.Properties.Resources.Error_ExecutableNotFound, "");
        }
        string iniFilePath = Path.Combine(DirectoryExt.GetDirectoryNameSimple(ExePath), "ubm.ini");
        ExecuteWithExitNotifications(() =>
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
                onExitCallback = onExitEventHandler;
                bool startedNewProcess = createProcess(bmsFilePath);
                if (startedNewProcess && (uBMplayProcess == null || HasExitedLocked(uBMplayProcess) || !RequireWindowHost().IsWindow(uBMplayHandleShowing)))
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
                    CloseProcessCoreLocked();
                }
                catch (Exception cleanupFailure)
                {
                    if (ReferenceEquals(startupFailure, cleanupFailure)) { throw; }
                    throw new AggregateException(
                        "uBMplay startup failed and its process cleanup could not be completed.",
                        startupFailure,
                        cleanupFailure);
                }
                throw;
            }
        });
        return Task.CompletedTask;
    }

    private bool TryRevertSettingsWhenNoOwnedProcessLocked()
    {
        TemporarilyRewriteSettings backup = iniFile;
        ThrowKnownRestorationFailureLocked();
        CloseExitedRequestProcessLocked();
        IExternalPlayerProcessSession process = uBMplayProcess;
        if (process != null)
        {
            if (!HasExitedLocked(process)) { return false; }
            CompletePlayerProcessExitLocked(process);
        }
        if (uBMplayRequestProcess != null) { return false; }
        if (backup == null) { return true; }
        // 再入が同じbackupの結果を確定済みなら、それを利用します。新backupには適用しません。
        if (activeExitScope.RestorationResults.TryGetValue(backup, out Exception previousFailure))
        {
            if (previousFailure != null) { ExceptionDispatchInfo.Capture(previousFailure).Throw(); }
            return true;
        }
        try
        {
            backup.RevertSettings();
            activeExitScope.RestorationResults.Add(backup, null);
            if (ReferenceEquals(iniFile, backup)) { iniFile = null; }
            return true;
        }
        catch (Exception failure)
        {
            activeExitScope.RestorationResults.Add(backup, failure);
            throw;
        }
    }

    private void CloseExitedRequestProcessLocked()
    {
        IExternalPlayerProcessSession process = uBMplayRequestProcess;
        if (process != null && HasExitedLocked(process))
        {
            CompleteRequestProcessExitLocked(process);
        }
    }

    private void CloseRequestProcessLocked()
    {
        IExternalPlayerProcessSession process = uBMplayRequestProcess;
        if (process == null)
        {
            return;
        }
        if (!HasExitedLocked(process))
        {
            process.CloseMainWindow();
            waitPolicy.WaitForProcessExit(
                () => HasExitedLocked(process),
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
    }

    private void uBMplayRequestExited(object sender, EventArgs e) => ExecuteWithExitNotifications(() =>
    {
        if (sender is not IExternalPlayerProcessSession process
            || !ReferenceEquals(uBMplayRequestProcess, process)) { return; }
        CompleteRequestProcessExitLocked(process);
        if (uBMplayProcess != null) { return; }
        Exception failure = null;
        try { TryRevertSettingsWhenNoOwnedProcessLocked(); }
        catch (Exception exception) { failure = exception; }
        CaptureExitNotificationLocked(sender, e, failure);
    });

    private static void ThrowStartupFailed()
    {
        throw new InvalidOperationException(BeMusicSeeker.Properties.Resources.Error_UbmplayStartupFailed);
    }

    private bool createProcess(string bmsFilePath)
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
        var launchRequest = ExternalPlayerProcessLaunchRequest.Create(
            ExePath,
            "-SP \"" + bmsFilePath + "\"",
            !RequireWindowHost().UsesLegacyWindowEmbedding
                ? System.Diagnostics.ProcessWindowStyle.Normal
                : System.Diagnostics.ProcessWindowStyle.Minimized);
        if (uBMplayHandleShowing.IsEmpty)
        {
            uBMplayProcess = processGateway.Prepare(launchRequest);
            uBMplayProcess.Exited += uBMplayExited;
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
                () => HasExitedLocked(uBMplayProcess),
                () => { },
                string.Format(BeMusicSeeker.Properties.Resources.Error_PlayerMainWindowTimeoutFormat, "uBMplay"),
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
                    () => HasExitedLocked(process),
                    aborted: null,
                    () =>
                    {
                        if (RequireWindowHost().UsesLegacyWindowEmbedding)
                        {
                            RequireWindowHost().MoveExternalWindowOffscreen(uBMplayHandleShowing);
                        }
                    },
                    BeMusicSeeker.Properties.Resources.Error_UbmplayPlaybackRequestTimeout,
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
        if (uBMplayProcess == null || HasExitedLocked(uBMplayProcess))
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
                    CloseProcessCoreLocked();
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
            () => HasExitedLocked(uBMplayProcess),
            () => { },
            BeMusicSeeker.Properties.Resources.Error_UbmplayChartLoadTimeout,
            pollMilliseconds: 0);
        if (!loaded)
        {
            ThrowStartupFailed();
        }
        if (!RequireWindowHost().UsesLegacyWindowEmbedding)
        {
            TrySetForegroundWindow(uBMplayHandleShowing);
            TrySetForegroundWindow(foregroundWindowHandle.IsEmpty ? RequireWindowHost().ParentHandle : foregroundWindowHandle);
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
            TrySetForegroundWindow(restoreWindow);
            foregroundWindow = RequireWindowHost().GetForegroundWindow();
            RequireWindowHost().SetFocus(foregroundWindowHandle);
            NLogWrapper.DebuggerLogger?.Trace("3.5 " + uBMplayHandleShowing + " " + foregroundWindow + " " + foregroundWindowHandle);
        }
    }

    private IExternalPlayerWindowHost RequireWindowHost()
    {
        return windowHost ?? throw new InvalidOperationException("uBMplayの再生ホストが接続されていません。");
    }

    private void TrySetForegroundWindow(ExternalWindowHandle window)
    {
        if (window.IsEmpty || !RequireWindowHost().IsWindow(window))
        {
            return;
        }
        RequireWindowHost().SetForegroundWindow(window);
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

    /// <summary>外部プレイヤーの実行中音量は変更しません。次の起動では保存済みの音量を使います。</summary>
    public void VolumeChanged(int volume)
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

    private void CompletePlayerProcessExitLocked(IExternalPlayerProcessSession process)
    {
        if (!ReferenceEquals(uBMplayProcess, process)) { return; }
        process.Exited -= uBMplayExited;
        uBMplayProcess = null;
        uBMplayHandleShowing = default;
    }

    private void uBMplayExited(object sender, EventArgs e) => ExecuteWithExitNotifications(() =>
    {
        if (sender is not IExternalPlayerProcessSession process
            || !ReferenceEquals(uBMplayProcess, process)) { return; }
        CompletePlayerProcessExitLocked(process);
        Exception failure = null;
        try
        {
            if (!TryRevertSettingsWhenNoOwnedProcessLocked()) { return; }
        }
        catch (Exception exception) { failure = exception; }
        CaptureExitNotificationLocked(sender, e, failure);
    });

    private void NotifyExitSafely(Action<object, EventArgs> callback, object sender, EventArgs args, Exception failure, bool failureNotificationOwnedByCaller)
    {
        if (callback == null) { return; }
        try { callback.Invoke(sender, failure == null ? args : new PlaybackExitEventArgs(failure, failureNotificationOwnedByCaller)); }
        catch (Exception notificationFailure)
        {
            try { NLogWrapper.FileLogger?.Warn(notificationFailure, "ubmplay_exit_notification_failed"); }
            catch { /* 通知故障はイベントの呼出元へ返しません。 */ }
        }
    }
}
