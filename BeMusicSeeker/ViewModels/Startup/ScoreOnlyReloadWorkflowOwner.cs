using System;
using System.Threading.Tasks;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Owns the score-only storage reload boundary without acquiring shell-wide resources.
/// </summary>
internal sealed class ScoreOnlyReloadWorkflowOwner
{
    private readonly Func<Task> reloadScoresAsync;

    /// <summary>
    /// Initializes the owner with the concrete score-storage reload operation.
    /// </summary>
    /// <param name="reloadScoresAsync">The operation that reloads score data.</param>
    internal ScoreOnlyReloadWorkflowOwner(Func<Task> reloadScoresAsync)
    {
        this.reloadScoresAsync = reloadScoresAsync
            ?? throw new ArgumentNullException(nameof(reloadScoresAsync));
    }

    /// <summary>
    /// Runs the score-only storage reload and preserves its completion, failure, and cancellation.
    /// </summary>
    /// <returns>The task returned by the configured score-storage reload operation.</returns>
    internal Task ReloadAsync()
    {
        return reloadScoresAsync();
    }
}
