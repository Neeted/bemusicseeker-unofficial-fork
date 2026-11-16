#nullable enable
using System;
using System.Threading.Tasks;
using BeMusicSeeker.Models;
using BeMusicSeeker.ViewModels;
using BeMusicSeeker.Views;
using Ribbit.Media.Audio;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    // 明示test sessionで直前の開始一件だけを保持し、停止時に同じsessionの解放を照合します。
    private RetainedPlayback? playback;
    private sealed record RetainedPlayback(PlaybackStartReceipt Start, BassAudioSession Session);

    private async Task<ScenarioMessage> ExecutePlaybackAsync(MainWindowViewModel model, ScenarioPlaybackCommand command)
    {
        MainWindow shell = window ?? throw new InvalidOperationException("The main window is unavailable.");
        var input = new PlaybackInputReceipt();
        RetainedPlayback? stoppingPlayback = command.Action == ScenarioPlaybackAction.Stop ? playback : null;
        Task routed;
        switch (command.Action)
        {
            case ScenarioPlaybackAction.Activate:
                int index = -1;
                object? row = null;
                for (int candidate = 0; candidate < model.MainChartList.Rows.Count; candidate++)
                {
                    object? current = model.MainChartList.Rows[candidate];
                    if (GridRowResolver.TryGetPlaybackChart(current, out ChartFile chart)
                        && string.Equals(chart.Path, command.ChartPath, StringComparison.OrdinalIgnoreCase))
                    { index = candidate; row = current; break; }
                }
                if (row is null) { throw new InvalidOperationException("The playback row is not visible."); }
                routed = shell.RequestPlaybackFromTable(index, row, input);
                break;
            case ScenarioPlaybackAction.Start:
                routed = shell.RequestStartPlayback(input);
                break;
            case ScenarioPlaybackAction.Next:
                routed = shell.RequestNextPlayback(input);
                break;
            case ScenarioPlaybackAction.Stop:
                if (stoppingPlayback is null) { throw new InvalidOperationException("Stop has no captured start."); }
                playback = null;
                routed = shell.RequestStopPlayback(input);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(command));
        }
        await routed.WaitAsync(stopping.Token);
        await (input.ActionTask ?? throw new InvalidOperationException("The playback input was rejected."))
            .WaitAsync(stopping.Token);
        if (input.Failure is Exception failure) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); }
        if (stoppingPlayback is not null)
        {
            PlaybackStartReceipt stopped = stoppingPlayback.Start;
            if (!string.Equals(stopped.ChartPath, command.ChartPath, StringComparison.OrdinalIgnoreCase))
            { throw new InvalidOperationException("Stop targeted a different chart."); }
            try { await stopped.Completion.WaitAsync(stopping.Token); }
            catch (OperationCanceledException) when (stopped.Completion.IsCanceled && !stopping.IsCancellationRequested) { }
            if (!stoppingPlayback.Session.IsReleased) { throw new InvalidOperationException("Stop did not release the captured session."); }
            return new ScenarioPlaybackCompleted(command.RequestId, Snapshot(stopped, stoppingPlayback.Session, true, null));
        }
        PlaybackStartReceipt started = input.Start ?? throw new InvalidOperationException("The input did not begin internal playback.");
        if (!string.Equals(started.ChartPath, command.ChartPath, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("A different chart started."); }
        await started.Ready.WaitAsync(stopping.Token);
        IPlaybackNativeObservation native = started.NativeObservation ?? throw new InvalidOperationException("Native observation is unavailable.");
        BassAudioSession session = native.Session ?? throw new InvalidOperationException("The start has no native session.");
        playback = new(started, session);
        using IDisposable registration = native.ObservePcm(session, out Task<PlaybackPcmSnapshot> capture);
        PlaybackPcmSnapshot pcm = await capture.WaitAsync(stopping.Token);
        if (command.WaitForCompletion) { await started.Completion.WaitAsync(stopping.Token); }
        return new ScenarioPlaybackCompleted(command.RequestId, Snapshot(started, session, false, pcm));
    }

    private static ScenarioPlaybackReceipt Snapshot(PlaybackStartReceipt start, BassAudioSession session,
        bool stopped, PlaybackPcmSnapshot? pcm) => new(start.Generation, start.ChartPath, start.PlaybackPath,
            start.Ready.IsCompletedSuccessfully, start.Completion.Status.ToString(), stopped, session.IsReleased, pcm);
}
