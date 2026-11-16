using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.TestSupport.Components;


internal sealed class RecordingPlayerSettingsGateway : IPlayerSettingsGateway
{
    private readonly PlayerSettingsSnapshot snapshot;

    internal RecordingPlayerSettingsGateway(PlayerSettingsSnapshot? snapshot = null)
    {
        this.snapshot = snapshot ?? new PlayerSettingsSnapshot(
            AudioDriver.Invalid,
            string.Empty,
            string.Empty,
            SampleRate.AUTO,
            SampleFormat.AUTO,
            0,
            false,
            100,
            new PlayerResolution(800, 600),
            true,
            new WindowPlacement(0, 1, 0, 0, 0, 0, 0, 0, 800, 600));
    }

    internal bool PlacementUpdated { get; private set; }

    internal WindowPlacement? Placement { get; private set; }

    public PlayerSettingsSnapshot CaptureSnapshot() => snapshot;

    public void UpdateWindowPlacement(WindowPlacement windowPlacement)
    {
        PlacementUpdated = true;
        Placement = windowPlacement;
    }
}

internal sealed class RecordingExternalPlayerProcessGateway : IExternalPlayerProcessGateway
{
    private readonly Queue<IExternalPlayerProcessSession> preparedSessions = new();

    internal ExternalPlayerProcessDiscoveryRequest DiscoveryRequest { get; private set; } = null!;

    internal ExternalPlayerProcessLaunchRequest LaunchRequest { get; private set; } = null!;

    internal RecordingExternalPlayerProcessSession Session { get; } = new();

    internal IReadOnlyList<IExternalPlayerProcessSession> ExistingProcesses { get; set; } = Array.Empty<IExternalPlayerProcessSession>();

    internal void EnqueuePreparedSession(IExternalPlayerProcessSession session)
    {
        preparedSessions.Enqueue(session);
    }

    public IReadOnlyList<IExternalPlayerProcessSession> FindExisting(ExternalPlayerProcessDiscoveryRequest request)
    {
        DiscoveryRequest = request;
        return ExistingProcesses;
    }

    internal int PrepareCount { get; private set; }

    public IExternalPlayerProcessSession Prepare(ExternalPlayerProcessLaunchRequest request)
    {
        PrepareCount++;
        LaunchRequest = request;
        return preparedSessions.Count > 0 ? preparedSessions.Dequeue() : Session;
    }
}

internal sealed class RecordingExternalPlayerProcessSession : IExternalPlayerProcessSession
{
    private EventHandler? exitHandlers;

    internal bool Started { get; private set; }

    internal bool KeepRunning { get; set; }

    internal Exception? StartException { get; set; }

    internal Action? BeforeStart { get; set; }

    internal int HasExitedReadCount { get; private set; }

    /// <summary>HasExitedが同じthreadから終了通知する.NETの再入を一回だけ再現します。</summary>
    internal Action? OnNextHasExited { get; set; }

    internal int CloseMainWindowCount { get; private set; }

    internal Exception? KillException { get; set; }

    internal int KillCount { get; private set; }

    public event EventHandler Exited
    {
        add => exitHandlers += value;
        remove => exitHandlers -= value;
    }

    public bool HasExited
    {
        get
        {
            HasExitedReadCount++;
            Action? notification = OnNextHasExited;
            OnNextHasExited = null;
            notification?.Invoke();
            return Started && !KeepRunning;
        }
    }

    public ExternalWindowHandle MainWindowHandle { get; set; }

    public IReadOnlyList<int> ThreadIds => Array.Empty<int>();

    public void Start()
    {
        BeforeStart?.Invoke();
        if (StartException != null)
        {
            throw StartException;
        }
        Started = true;
    }

    internal void RaiseExited()
    {
        exitHandlers?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>購読解除前にOS側で取得済みとなった通知先を捕捉します。配送と実終了は別々に制御します。</summary>
    internal Action CaptureExitedNotification()
    {
        EventHandler? snapshot = exitHandlers;
        return () => snapshot?.Invoke(this, EventArgs.Empty);
    }

    public void CloseMainWindow()
    {
        CloseMainWindowCount++;
    }

    public void Kill()
    {
        KillCount++;
        if (KillException != null)
        {
            throw KillException;
        }
        KeepRunning = false;
        Started = true;
    }
}

internal sealed class RecordingExternalPlayerWindowHost : IExternalPlayerWindowHost
{
    private ExternalWindowHandle foregroundWindow;

    internal RecordingExternalPlayerWindowHost(ExternalWindowHandle parentHandle)
    {
        ParentHandle = parentHandle;
    }

    internal List<string> Operations { get; } = new();

    internal List<string> KeyEvents { get; } = new();

    internal IReadOnlyList<ExternalWindowHandle> EnumeratedWindows { get; set; } = Array.Empty<ExternalWindowHandle>();

    internal bool LegacyWindowEmbedding { get; set; }

    internal string WindowClassName { get; set; } = string.Empty;

    internal string WindowTitle { get; set; } = string.Empty;

    internal ExternalWindowHandle ForegroundWindow
    {
        get => foregroundWindow;
        set => foregroundWindow = value;
    }

    internal bool SetForegroundWindowSucceeds { get; set; } = true;

    internal bool Lr2WindowStyleApplied { get; set; }

    internal bool CompleteLr2WindowStyleApply { get; set; } = true;

    internal Action? Lr2WindowStyleApplyAttempt { get; set; }

    internal WindowPlacement? CapturedPlacement { get; private set; }

    internal WindowPlacement? AppliedPlacement { get; private set; }

    public ExternalWindowHandle ParentHandle { get; }

    public bool UsesLegacyWindowEmbedding => LegacyWindowEmbedding;

    public ExternalWindowHandle GetForegroundWindow()
    {
        Operations.Add("GetForegroundWindow");
        return foregroundWindow;
    }

    public bool IsWindow(ExternalWindowHandle window) => true;

    public bool SetForegroundWindow(ExternalWindowHandle window)
    {
        Operations.Add("SetForegroundWindow");
        if (SetForegroundWindowSucceeds)
        {
            foregroundWindow = window;
        }
        return SetForegroundWindowSucceeds;
    }

    public void SetFocus(ExternalWindowHandle window)
    {
        Operations.Add("SetFocus");
    }

    public IReadOnlyList<ExternalWindowHandle> EnumerateThreadWindows(IReadOnlyList<int> threadIds)
        => EnumeratedWindows;

    public string GetClassName(ExternalWindowHandle window) => WindowClassName;

    public string GetWindowText(ExternalWindowHandle window) => WindowTitle;

    public void SendKey(short scanCode, bool extended, bool keyDown)
    {
        Operations.Add("SendKey");
        KeyEvents.Add($"{scanCode}:{extended}:{keyDown}");
    }

    public void AttachBmiIdxWindow(ExternalWindowHandle childWindow)
    {
        Operations.Add("AttachBmiIdxWindow");
    }

    /// <summary>旧終了snapshotの配送後も、新mainのwindowが採用されたままか観測します。</summary>
    internal ExternalWindowHandle LastAttachedUbmplayWindow { get; private set; }

    public void AttachUbmplayWindow(ExternalWindowHandle childWindow, bool legacyWindowStyle)
    {
        Operations.Add("AttachUbmplayWindow");
        LastAttachedUbmplayWindow = childWindow;
    }

    public void MoveExternalWindowOffscreen(ExternalWindowHandle childWindow)
    {
        Operations.Add("MoveExternalWindowOffscreen");
    }

    public void DetachUbmplayWindow(ExternalWindowHandle childWindow)
    {
        Operations.Add("DetachUbmplayWindow");
    }

    public bool IsLr2WindowStyleApplied(ExternalWindowHandle childWindow)
        => Lr2WindowStyleApplied;

    public void ApplyLr2WindowStyle(ExternalWindowHandle childWindow)
    {
        Operations.Add("ApplyLr2WindowStyle");
        Lr2WindowStyleApplyAttempt?.Invoke();
        if (CompleteLr2WindowStyleApply)
        {
            Lr2WindowStyleApplied = true;
        }
    }

    public void ApplyLr2ExtendedWindowStyle(ExternalWindowHandle childWindow)
    {
        Operations.Add("ApplyLr2ExtendedWindowStyle");
    }

    public void NotifyBmiIdxPlaybackStarted(ExternalWindowHandle childWindow)
    {
        Operations.Add("NotifyBmiIdxPlaybackStarted");
    }

    public WindowPlacement CaptureWindowPlacement(ExternalWindowHandle childWindow)
    {
        Operations.Add("CaptureWindowPlacement");
        CapturedPlacement = new WindowPlacement(7, 2, 0, 0, 0, 0, 10, 20, 810, 620);
        return CapturedPlacement;
    }

    public void ApplyWindowPlacement(ExternalWindowHandle childWindow, WindowPlacement placement, int width, int height)
    {
        Operations.Add("ApplyWindowPlacement");
        AppliedPlacement = placement;
    }
}
