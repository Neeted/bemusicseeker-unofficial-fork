using System;
using System.Threading;
using System.Threading.Tasks;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Describes the startup-library initialization failure that the shell must present.
/// </summary>
internal sealed class StartupLibraryInitializationFailurePresentation
{
    /// <summary>
    /// Initializes an immutable failure presentation request.
    /// </summary>
    /// <param name="exception">The original initialization failure.</param>
    internal StartupLibraryInitializationFailurePresentation(Exception exception)
    {
        Exception = exception ?? throw new ArgumentNullException(nameof(exception));
    }

    /// <summary>
    /// Gets the original initialization failure.
    /// </summary>
    internal Exception Exception { get; }
}

/// <summary>
/// Presents a startup-library initialization failure at the shell boundary.
/// </summary>
internal interface IStartupLibraryInitializationFailurePresenter
{
    /// <summary>
    /// Presents the immutable startup-library failure request.
    /// </summary>
    /// <param name="presentation">The failure presentation request.</param>
    void Present(StartupLibraryInitializationFailurePresentation presentation);
}

/// <summary>
/// Owns the shared operation gate and the file-backed library initialization call for startup.
/// </summary>
internal sealed class StartupLibraryInitializationWorkflowOwner
{
    private readonly SemaphoreSlim operationGate;

    /// <summary>
    /// Initializes the startup library initialization owner.
    /// </summary>
    /// <param name="operationGate">The shared gate that serializes startup and reload operations.</param>
    internal StartupLibraryInitializationWorkflowOwner(SemaphoreSlim operationGate)
    {
        this.operationGate = operationGate
            ?? throw new ArgumentNullException(nameof(operationGate));
    }

    /// <summary>
    /// Acquires the shared operation gate and returns the lease that releases it exactly once.
    /// </summary>
    /// <returns>A typed lease for the acquired operation gate.</returns>
    internal async Task<StartupLibraryInitializationGateLease> AcquireGateAsync()
    {
        await operationGate.WaitAsync();
        return new StartupLibraryInitializationGateLease(operationGate);
    }

    /// <summary>
    /// Runs the file-backed startup initialization on the thread pool and preserves its exception.
    /// </summary>
    /// <param name="initializeStartup">The narrow file-initialization operation.</param>
    /// <returns>A task that completes when file initialization succeeds.</returns>
    internal async Task InitializeAsync(Action initializeStartup)
    {
        if (initializeStartup == null)
        {
            throw new ArgumentNullException(nameof(initializeStartup));
        }

        await Task.Run(initializeStartup).LoggingAndPropagate("Initialize");
    }
}

/// <summary>
/// Owns one acquired startup operation gate and releases it at most once.
/// </summary>
internal sealed class StartupLibraryInitializationGateLease : IDisposable
{
    private SemaphoreSlim operationGate;

    /// <summary>
    /// Initializes a lease for an already acquired operation gate.
    /// </summary>
    /// <param name="operationGate">The gate held by this lease.</param>
    internal StartupLibraryInitializationGateLease(SemaphoreSlim operationGate)
    {
        this.operationGate = operationGate
            ?? throw new ArgumentNullException(nameof(operationGate));
    }

    /// <summary>
    /// Releases the acquired operation gate exactly once.
    /// </summary>
    public void Dispose()
    {
        SemaphoreSlim gate = Interlocked.Exchange(ref operationGate, null);
        gate?.Release();
    }
}
