#nullable enable
using System;
using System.Threading.Tasks;
using BeMusicSeeker.ViewModels;

namespace BeMusicSeeker.Runtime;

internal sealed partial class ApplicationScenarioControl
{
    private readonly object initialSyncGate = new();
    private readonly TaskCompletionSource<ScenarioInitialPlaylistSyncCompleted> initialSyncCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PlaylistExternalSyncRequestEventArgs? initialSyncRequest;
    private PlaylistExternalSyncCompletionEventArgs? initialSyncReceipt;
    private bool initialSyncStarted;
    private bool initialSyncConsumed;
    private Task? initialSyncObservation;

    private void AttachInitialPlaylistSync(MainWindowViewModel model)
    {
        model.PlaylistWorkspace.PlaylistExternalSyncQueued += InitialSyncQueued;
        model.PlaylistWorkspace.PlaylistExternalSyncCompleted += InitialSyncCompleted;
        model.PlaylistWorkspace.ExternalPlaylistSyncStarted += InitialSyncStarted;
    }

    private void DetachInitialPlaylistSync()
    {
        if (viewModel is not { } model) { return; }
        model.PlaylistWorkspace.PlaylistExternalSyncQueued -= InitialSyncQueued;
        model.PlaylistWorkspace.PlaylistExternalSyncCompleted -= InitialSyncCompleted;
        model.PlaylistWorkspace.ExternalPlaylistSyncStarted -= InitialSyncStarted;
    }

    private void ClearInitialPlaylistSync()
    {
        DetachInitialPlaylistSync();
        lock (initialSyncGate)
        {
            initialSyncCompletion.TrySetCanceled();
            initialSyncRequest = null;
            initialSyncReceipt = null;
        }
    }

    private void InitialSyncQueued(object? sender, PlaylistExternalSyncRequestEventArgs request)
    {
        if (request.Reason != "Initialize") { return; }
        lock (initialSyncGate)
        {
            if (!initialSyncCompletion.Task.IsCompleted && initialSyncRequest is null && !initialSyncStarted) { initialSyncRequest = request; }
        }
    }

    private void InitialSyncCompleted(object? sender, PlaylistExternalSyncCompletionEventArgs receipt)
    {
        if (receipt.Reason != "Initialize") { return; }
        lock (initialSyncGate)
        {
            if (initialSyncCompletion.Task.IsCompleted) { return; }
            if (initialSyncReceipt is null && (initialSyncRequest?.Version == receipt.Version || initialSyncRequest is null && receipt.WasSkipped))
            { initialSyncReceipt = receipt; }
            if (receipt.WasSkipped && receipt.Version == 0 && initialSyncRequest is null && !initialSyncStarted)
            { initialSyncCompletion.TrySetException(new InvalidOperationException("The initial playlist sync was skipped.")); }
        }
    }

    private void InitialSyncStarted(string reason, Task task)
    {
        if (reason != "Initialize") { return; }
        lock (initialSyncGate)
        {
            if (initialSyncStarted || initialSyncCompletion.Task.IsCompleted) { return; }
            initialSyncStarted = true;
            initialSyncObservation = ObserveInitialSyncAsync(task);
        }
    }

    private async Task ObserveInitialSyncAsync(Task task)
    {
        ScenarioFailure? failure = null;
        try { await task.WaitAsync(stopping.Token).ConfigureAwait(false); }
        catch (Exception exception) { failure = ScenarioFailure.FromException(exception); }
        lock (initialSyncGate)
        {
            if (initialSyncReceipt is not { } receipt)
            { initialSyncCompletion.TrySetException(new InvalidOperationException("The initial playlist task ended without its receipt.")); }
            else
            { initialSyncCompletion.TrySetResult(new(0, receipt.Reason, receipt.Version, receipt.Succeeded, receipt.WasSkipped, failure)); }
        }
        DetachInitialPlaylistSync();
    }

    private async Task<ScenarioInitialPlaylistSyncCompleted> ConsumeInitialSyncAsync(long requestId)
    {
        if (initialSyncConsumed) { throw new InvalidOperationException("The initial playlist sync was already consumed."); }
        initialSyncConsumed = true;
        try
        {
            ScenarioInitialPlaylistSyncCompleted result = await initialSyncCompletion.Task.WaitAsync(stopping.Token);
            return result with { RequestId = requestId };
        }
        finally
        {
            DetachInitialPlaylistSync();
            lock (initialSyncGate) { initialSyncRequest = null; initialSyncReceipt = null; }
        }
    }
}
