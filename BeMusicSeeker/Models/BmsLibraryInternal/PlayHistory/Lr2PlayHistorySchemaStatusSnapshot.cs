namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal sealed class Lr2PlayHistorySchemaStatusSnapshot
{
    private Lr2PlayHistorySchemaStatusSnapshot(
        bool isReset,
        Lr2PlayHistorySchemaStatus status,
        string scoreDbPath,
        string message)
    {
        IsReset = isReset;
        Status = status;
        ScoreDbPath = scoreDbPath ?? string.Empty;
        Message = message ?? string.Empty;
    }

    internal bool IsReset { get; }

    internal Lr2PlayHistorySchemaStatus Status { get; }

    internal string ScoreDbPath { get; }

    internal string Message { get; }

    internal static Lr2PlayHistorySchemaStatusSnapshot Reset { get; } = new(
        isReset: true,
        Lr2PlayHistorySchemaStatus.Unreadable,
        scoreDbPath: string.Empty,
        message: string.Empty);

    internal static Lr2PlayHistorySchemaStatusSnapshot FromResult(Lr2PlayHistorySchemaCheckResult result)
    {
        if (result == null)
        {
            return Reset;
        }

        return new Lr2PlayHistorySchemaStatusSnapshot(
            isReset: false,
            result.Status,
            result.ScoreDbPath,
            result.Message);
    }
}
