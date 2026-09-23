using System;
using System.IO;
using System.Linq;
using BeMusicSeeker.Models.Utils;

namespace BeMusicSeeker.Models.BmsLibraryInternal;

internal static class DisplayedExceptionMessage
{
    internal static string Format(Exception exception)
    {
        if (exception is AggregateException aggregateException)
        {
            return string.Join(Environment.NewLine, aggregateException.Flatten().InnerExceptions.Select(Format));
        }

        if (exception is FileMutationException fileMutationException)
        {
            return fileMutationException.InnerException?.Message ?? fileMutationException.Message;
        }

        return exception?.Message ?? string.Empty;
    }
}
