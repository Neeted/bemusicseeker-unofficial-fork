using System;
using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>
/// Immutable facts used to choose the play-history database without touching the file system.
/// </summary>
internal sealed class PlayHistoryReadSourceSelectionInput
{
    /// <summary>
    /// Initializes source-selection facts captured by the shell owner.
    /// </summary>
    /// <param name="lr2ScoreDbPath">The resolved LR2 score database path.</param>
    /// <param name="isLr2LinkedProfile">Whether the current profile is linked to LR2.</param>
    /// <param name="useBeatorajaScoreDb">The configured beatoraja score database switch.</param>
    /// <param name="beatorajaScoreDbPath">The resolved beatoraja score database path.</param>
    /// <param name="activeScoreSource">The score source currently active in the library.</param>
    /// <param name="beatorajaScoreDbFileExists">The already-observed score database existence fact.</param>
    /// <param name="beatorajaScoreContext">The immutable score snapshot for beatoraja projection.</param>
    internal PlayHistoryReadSourceSelectionInput(
        string lr2ScoreDbPath,
        bool isLr2LinkedProfile,
        bool useBeatorajaScoreDb,
        string beatorajaScoreDbPath,
        ActiveScoreSource activeScoreSource,
        bool beatorajaScoreDbFileExists,
        BeatorajaPlayHistoryScoreContext beatorajaScoreContext)
    {
        Lr2ScoreDbPath = lr2ScoreDbPath ?? string.Empty;
        IsLr2LinkedProfile = isLr2LinkedProfile;
        UseBeatorajaScoreDb = useBeatorajaScoreDb;
        BeatorajaScoreDbPath = beatorajaScoreDbPath ?? string.Empty;
        ActiveScoreSource = activeScoreSource;
        BeatorajaScoreDbFileExists = beatorajaScoreDbFileExists;
        BeatorajaScoreContext = beatorajaScoreContext ?? BeatorajaPlayHistoryScoreContext.Empty;
    }

    /// <summary>
    /// Gets the resolved LR2 score database path.
    /// </summary>
    internal string Lr2ScoreDbPath { get; }

    /// <summary>
    /// Gets whether the current profile is linked to LR2.
    /// </summary>
    internal bool IsLr2LinkedProfile { get; }

    /// <summary>
    /// Gets the configured beatoraja score database switch.
    /// </summary>
    internal bool UseBeatorajaScoreDb { get; }

    /// <summary>
    /// Gets the resolved beatoraja score database path.
    /// </summary>
    internal string BeatorajaScoreDbPath { get; }

    /// <summary>
    /// Gets the active score source captured by the shell owner.
    /// </summary>
    internal ActiveScoreSource ActiveScoreSource { get; }

    /// <summary>
    /// Gets the already-observed beatoraja score database existence fact.
    /// </summary>
    internal bool BeatorajaScoreDbFileExists { get; }

    /// <summary>
    /// Gets the immutable beatoraja score snapshot used by projection.
    /// </summary>
    internal BeatorajaPlayHistoryScoreContext BeatorajaScoreContext { get; }
}

/// <summary>
/// Selects the play-history reader source from immutable runtime facts.
/// </summary>
internal static class PlayHistoryReadSourceSelector
{
    /// <summary>
    /// Applies the existing provider-selection contract without reading files or changing fallback meaning.
    /// </summary>
    /// <param name="input">The captured settings, score-source, and file-existence facts.</param>
    /// <returns>The source context consumed by the play-history owner.</returns>
    internal static PlayHistoryReadSourceContext Select(PlayHistoryReadSourceSelectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        bool useBeatorajaProvider = input.UseBeatorajaScoreDb
            && input.ActiveScoreSource == ActiveScoreSource.Beatoraja
            && !string.IsNullOrWhiteSpace(input.BeatorajaScoreDbPath)
            && input.BeatorajaScoreDbFileExists;
        return useBeatorajaProvider
            ? PlayHistoryReadSourceContext.Beatoraja(
                input.BeatorajaScoreDbPath,
                input.BeatorajaScoreContext)
            : PlayHistoryReadSourceContext.Lr2(
                input.Lr2ScoreDbPath,
                input.IsLr2LinkedProfile);
    }
}
