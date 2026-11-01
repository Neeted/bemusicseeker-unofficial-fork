using System.Collections.Concurrent;
using System.ComponentModel;
using BeMusicSeeker.Models;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.TestSupport.Components;


internal class FakeBmsPlayer : IBMSPlayer, IExternalWindowPlayer
{
    public bool SupportsBmson { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string ExePath { get; set; } = string.Empty;

    public IExternalPlayerWindowHost? WindowHost { get; private set; }

    public void AttachWindowHost(IExternalPlayerWindowHost windowHost)
    {
        WindowHost = windowHost;
    }

    private TimeSpan duration;

    public TimeSpan Duration
    {
        get
        {
            if (ThrowOnTelemetryRead)
            {
                throw new InvalidOperationException("telemetry read failure");
            }
            return duration;
        }
        set => duration = value;
    }

    public bool ThrowOnTelemetryRead { get; set; }

    public TimeSpan CurrentTime { get; set; }

    public TimeSpan StopTime { get; set; }

    public TimeSpan BmsDuration { get; set; }

    public TimeSpan MusicDuration { get; set; }

    public int CurrentVoices { get; set; }

    public int MaxVoices { get; set; }

    public int NoteDensity { get; set; }

    public int NoteDensityMax { get; set; }

    public int Bpm { get; set; }

    public int MinBpm { get; set; }

    public int MaxBpm { get; set; }

    public double Total { get; set; }

    public int Combo { get; set; }

    public int Notes { get; set; }

    public int Measure { get; set; }

    public int LastMeasure { get; set; }

    public int CloseProcessCount { get; private set; }

    public Exception? PlayStartException { get; set; }

    public Task? PlayStartTask { get; set; }

    public Action? BeforePlayStart { get; set; }

    public Action? BeforeClose { get; set; }

    public Action<object, EventArgs>? ExitHandler { get; private set; }

    public ConcurrentQueue<string> Commands { get; } = new();

    private readonly ConcurrentDictionary<string, int> commandCounts = new(
        StringComparer.Ordinal);
    private readonly ConcurrentDictionary<
        (string Command, int Occurrence),
        TaskCompletionSource<object?>> commandSignals = new();

    internal Task WaitForCommandAsync(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        int expectedOccurrence = commandCounts.TryGetValue(command, out int currentOccurrence)
            ? currentOccurrence + 1
            : 1;
        (string command, int expectedOccurrence) key = (command, expectedOccurrence);
        if (commandCounts.TryGetValue(command, out currentOccurrence)
            && currentOccurrence >= expectedOccurrence)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource<object?> signal = commandSignals.GetOrAdd(
            key,
            static _ => new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously));
        if (commandCounts.TryGetValue(command, out currentOccurrence)
            && currentOccurrence >= expectedOccurrence)
        {
            signal.TrySetResult(null);
        }
        return signal.Task;
    }

    public void Raise(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void CloseProcess()
    {
        CloseProcessCount++;
        RecordCommand("Close");
        BeforeClose?.Invoke();
    }

    public Task PlayStart(string bmsFilePath, Action<object, EventArgs>? onExitEventHandler = null)
    {
        RecordCommand("PlayStart:" + bmsFilePath);
        ExitHandler = onExitEventHandler;
        BeforePlayStart?.Invoke();
        if (PlayStartException != null)
        {
            throw PlayStartException;
        }
        return PlayStartTask ?? Task.CompletedTask;
    }

    public void RestartPlayingBMSfile() => RecordCommand("Restart");

    public void PausePlayingBMSfileToggle() => RecordCommand("Pause");

    public void FastForwardPlayingBMSfileStart() => RecordCommand("FastForwardStart");

    public void FastForwardPlayingBMSfileEnd() => RecordCommand("FastForwardEnd");

    public void FastBackwardPlayingBMSfileStart() => RecordCommand("FastBackwardStart");

    public void FastBackwardPlayingBMSfileEnd() => RecordCommand("FastBackwardEnd");

    public void ShowInfo() => RecordCommand("ShowInfo");

    public void ShowEffect() => RecordCommand("ShowEffect");

    public void ChangePlayside() => RecordCommand("ChangePlayside");

    public void IncreaseHighSpeed() => RecordCommand("IncreaseHighSpeed");

    public void DecreaseHighSpeed() => RecordCommand("DecreaseHighSpeed");

    internal int LastVolume { get; private set; }
    public void VolumeChanged(int volume) { LastVolume = volume; RecordCommand("VolumeChanged"); }

    private void RecordCommand(string command)
    {
        Commands.Enqueue(command);
        int occurrence = commandCounts.AddOrUpdate(command, 1, static (_, count) => count + 1);
        if (commandSignals.TryGetValue((command, occurrence), out TaskCompletionSource<object?>? signal))
        {
            signal!.TrySetResult(null);
        }
    }
}

internal sealed class PreloadBmsPlayer : FakeBmsPlayer, INextSongPreloadPlayer
{
    internal Task Ready { get; set; } = Task.CompletedTask;
    internal Task Completion { get; set; } = Task.CompletedTask;
    internal bool LastAllowPreload { get; private set; }
    internal ConcurrentQueue<NextSongPreloadInput> Preloads { get; } = new();
    internal ConcurrentQueue<string> Starts { get; } = new();
    internal Func<Task>? CloseCompletion { get; set; }
    internal Func<string, PlaybackStartOperation>? BeginOperation { get; set; }
    internal TaskCompletionSource<string> StartObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal string? NextSongPath { get; private set; }

    public PlaybackStartOperation BeginStart(string path, Action<object, EventArgs>? onExit, bool allowPreload,
        Action<Exception>? onPlaybackFailure = null)
    {
        _ = PlayStart(path, onExit);
        NextSongPath = null;
        LastAllowPreload = allowPreload;
        Starts.Enqueue(path);
        StartObserved.TrySetResult(path);
        return BeginOperation?.Invoke(path) ?? new PlaybackStartOperation(Ready, Completion);
    }

    public Task PrepareNextAsync(NextSongPreloadInput input, Action<Exception> onFailure)
    {
        NextSongPath = input.Path;
        Preloads.Enqueue(input);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        NextSongPath = null;
        CloseProcess();
        return CloseCompletion?.Invoke() ?? Task.CompletedTask;
    }
}
