using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal enum ExternalPlaylistImportOutcomeKind
{
    Imported,
    SkippedDuplicateName,
    Failed
}

internal sealed class ExternalPlaylistImportOutcome
{
    private ExternalPlaylistImportOutcome(ExternalPlaylistImportOutcomeKind kind, Uri uri, string tableName, Exception exception)
    {
        Kind = kind;
        Uri = uri;
        TableName = tableName ?? string.Empty;
        Exception = exception;
    }

    internal ExternalPlaylistImportOutcomeKind Kind { get; }

    internal Uri Uri { get; }

    internal string TableName { get; }

    internal Exception Exception { get; }

    internal static ExternalPlaylistImportOutcome Imported(Uri uri, string tableName)
    {
        return new ExternalPlaylistImportOutcome(ExternalPlaylistImportOutcomeKind.Imported, uri, tableName, null);
    }

    internal static ExternalPlaylistImportOutcome SkippedDuplicateName(Uri uri, string tableName, Exception exception)
    {
        return new ExternalPlaylistImportOutcome(ExternalPlaylistImportOutcomeKind.SkippedDuplicateName, uri, tableName, exception);
    }

    internal static ExternalPlaylistImportOutcome Failed(Uri uri, Exception exception)
    {
        return new ExternalPlaylistImportOutcome(ExternalPlaylistImportOutcomeKind.Failed, uri, string.Empty, exception);
    }
}

internal sealed class ExternalPlaylistImportQueueSummary
{
    internal ExternalPlaylistImportQueueSummary(IEnumerable<ExternalPlaylistImportOutcome> outcomes)
    {
        Outcomes = [.. (outcomes ?? []).Where(outcome => outcome != null)];
    }

    internal IReadOnlyList<ExternalPlaylistImportOutcome> Outcomes { get; }

    internal int ImportedCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Imported);

    internal int SkippedDuplicateNameCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.SkippedDuplicateName);

    internal int FailedCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed);

    internal bool HasNotifiableItems => SkippedDuplicateNameCount > 0 || FailedCount > 0;

    internal IReadOnlyList<ExternalPlaylistImportOutcome> SkippedDuplicateNameOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.SkippedDuplicateName)];

    internal IReadOnlyList<ExternalPlaylistImportOutcome> FailedOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed)];
}

internal sealed class ExternalPlaylistImportQueue
{
    private readonly object syncRoot = new();

    private readonly Queue<Uri> pendingUris = new();

    private bool isDraining;

    internal bool Enqueue(Uri uri)
    {
        return EnqueueRange([uri]);
    }

    internal bool EnqueueRange(IEnumerable<Uri> uris)
    {
        List<Uri> validUris = [.. (uris ?? []).Where(uri => uri != null)];
        if (validUris.Count == 0)
        {
            return false;
        }
        lock (syncRoot)
        {
            foreach (Uri uri in validUris)
            {
                pendingUris.Enqueue(uri);
            }
            if (isDraining)
            {
                return false;
            }
            isDraining = true;
            return true;
        }
    }

    internal int PendingCount
    {
        get
        {
            lock (syncRoot)
            {
                return pendingUris.Count;
            }
        }
    }

    internal bool TryDequeue(out Uri uri)
    {
        lock (syncRoot)
        {
            if (pendingUris.Count > 0)
            {
                uri = pendingUris.Dequeue();
                return true;
            }
            isDraining = false;
            uri = null;
            return false;
        }
    }

    internal IReadOnlyList<Uri> DequeueBatch()
    {
        lock (syncRoot)
        {
            if (pendingUris.Count == 0)
            {
                isDraining = false;
                return [];
            }
            List<Uri> batch = [.. pendingUris];
            pendingUris.Clear();
            return batch;
        }
    }
}
