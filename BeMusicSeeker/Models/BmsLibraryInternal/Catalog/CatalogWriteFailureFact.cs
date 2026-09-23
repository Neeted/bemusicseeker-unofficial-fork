using System;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>
/// Immutable evidence of a catalog write failure captured before the original
/// exception is rethrown. The consumer publishes it after catalog write guards
/// have been released.
/// </summary>
internal sealed class CatalogWriteFailureFact(
    string runId,
    string stage,
    string logReason,
    Exception exception) : EventArgs
{
    public string RunId { get; } = string.IsNullOrWhiteSpace(runId) ? "song_db_write" : runId;

    public string Stage { get; } = string.IsNullOrWhiteSpace(stage) ? "lr2_song_db_write_failed" : stage;

    public string LogReason { get; } = string.IsNullOrWhiteSpace(logReason) ? "lr2_song_db_write_failed" : logReason;

    public Exception Exception { get; } = exception;

    public string ExceptionTypeName { get; } = exception?.GetType().Name ?? "unknown";

    public string DisplayedMessage { get; } = GetDisplayedExceptionMessage(exception)
        .Replace(Environment.NewLine, " | ");

    public string Detail => Stage + ": " + DisplayedMessage;

    private static string GetDisplayedExceptionMessage(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            return string.Join(
                Environment.NewLine,
                aggregateException.Flatten().InnerExceptions.Select(GetDisplayedExceptionMessage));
        }

        if (exception is FileMutationException fileMutationException)
        {
            return fileMutationException.InnerException?.Message ?? fileMutationException.Message;
        }

        return exception?.Message ?? string.Empty;
    }
}
