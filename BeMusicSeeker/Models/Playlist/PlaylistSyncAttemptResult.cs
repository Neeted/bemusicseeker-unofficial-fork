using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BeMusicSeeker.Models;

internal sealed class PlaylistSyncAttemptResult
{
    internal BMSTable SourceTable { get; set; }

    internal BMSTable ResultTable { get; set; }

    internal Uri PageUri { get; set; }

    internal bool Succeeded { get; set; }

    internal bool Updated { get; set; }

    internal Exception Exception { get; set; }

    internal PlaylistSyncStatusKind FailureKind { get; set; }

    internal string Detail { get; set; } = string.Empty;

    internal static PlaylistSyncAttemptResult CreateSuccess(BMSTable sourceTable, BMSTable resultTable, Uri pageUri, bool updated)
    {
        return new PlaylistSyncAttemptResult
        {
            SourceTable = sourceTable,
            ResultTable = resultTable,
            PageUri = pageUri,
            Succeeded = true,
            Updated = updated,
            FailureKind = updated ? PlaylistSyncStatusKind.Updated : PlaylistSyncStatusKind.Ok
        };
    }

    internal static PlaylistSyncAttemptResult CreateFailure(BMSTable sourceTable, Uri pageUri, Exception exception)
    {
        return CreateFailure(sourceTable, sourceTable, pageUri, exception);
    }

    internal static PlaylistSyncAttemptResult CreateFailure(BMSTable sourceTable, BMSTable resultTable, Uri pageUri, Exception exception)
    {
        PlaylistSyncStatusKind playlistSyncStatusKind = ClassifyFailureKind(exception);
        return new PlaylistSyncAttemptResult
        {
            SourceTable = sourceTable,
            ResultTable = resultTable ?? sourceTable,
            PageUri = pageUri,
            Succeeded = false,
            Updated = false,
            Exception = exception,
            FailureKind = playlistSyncStatusKind,
            Detail = BuildFailureDetail(exception, pageUri)
        };
    }

    private static PlaylistSyncStatusKind ClassifyFailureKind(Exception exception)
    {
        if (exception is PlaylistHeaderUriNotFoundException)
        {
            return PlaylistSyncStatusKind.HeaderNotFound;
        }
        if (exception is PlaylistHeaderParseException)
        {
            return PlaylistSyncStatusKind.HeaderParseError;
        }
        if (exception is PlaylistDataParseException)
        {
            return PlaylistSyncStatusKind.DataParseError;
        }
        if (exception is ArgumentException argumentException)
        {
            if (string.Equals(argumentException.ParamName, "_header_json", StringComparison.Ordinal))
            {
                return PlaylistSyncStatusKind.HeaderParseError;
            }
            if (string.Equals(argumentException.ParamName, "_data_json", StringComparison.Ordinal))
            {
                return PlaylistSyncStatusKind.DataParseError;
            }
        }
        if (exception is InvalidOperationException invalidOperationException && !string.IsNullOrWhiteSpace(invalidOperationException.Message) && invalidOperationException.Message.StartsWith("Failed to resolve playlist data_url", StringComparison.Ordinal))
        {
            return PlaylistSyncStatusKind.InvalidDataUrl;
        }
        if (TryGetHttpStatusCode(exception, out int statusCode))
        {
            return statusCode switch
            {
                404 => PlaylistSyncStatusKind.Http404,
                403 => PlaylistSyncStatusKind.Http403,
                _ => PlaylistSyncStatusKind.HttpError,
            };
        }
        if (exception is TaskCanceledException || exception?.InnerException is TaskCanceledException || exception?.InnerException is SocketException)
        {
            return PlaylistSyncStatusKind.NetworkError;
        }
        if (exception is HttpRequestException httpRequestException && httpRequestException.InnerException is SocketException)
        {
            return PlaylistSyncStatusKind.NetworkError;
        }
        return PlaylistSyncStatusKind.UnknownError;
    }

    private static bool TryGetHttpStatusCode(Exception exception, out int statusCode)
    {
        statusCode = 0;
        string text = exception?.Message;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        Match match = Regex.Match(text, "statusCode=(\\d+)", RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }
        return int.TryParse(match.Groups[1].Value, out statusCode);
    }

    private static string BuildFailureDetail(Exception exception, Uri pageUri)
    {
        string message = BuildFailureMessage(exception);
        string text = pageUri?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message))
        {
            return text;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            return message;
        }
        return message + Environment.NewLine + text;
    }

    private static string BuildFailureMessage(Exception exception)
    {
        if (exception == null)
        {
            return string.Empty;
        }
        string message = exception.Message ?? string.Empty;
        string innerMessage = exception.InnerException?.Message ?? string.Empty;
        if (string.IsNullOrWhiteSpace(innerMessage) || string.Equals(message, innerMessage, StringComparison.Ordinal))
        {
            return message;
        }
        if (string.IsNullOrWhiteSpace(message))
        {
            return innerMessage;
        }
        return message + Environment.NewLine + innerMessage;
    }
}
