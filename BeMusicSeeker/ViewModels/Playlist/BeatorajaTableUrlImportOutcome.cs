using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

internal enum BeatorajaTableUrlImportOutcomeKind
{
    Existing,
    Imported,
    RestoredFromBmt,
    Failed,
    Warning
}

internal sealed class BeatorajaTableUrlImportOutcome
{
    private BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind kind, Uri uri, string rawUrl, string tableName, Exception exception)
    {
        Kind = kind;
        Uri = uri;
        RawUrl = rawUrl ?? uri?.ToString() ?? string.Empty;
        TableName = tableName ?? string.Empty;
        Exception = exception;
    }

    internal BeatorajaTableUrlImportOutcomeKind Kind { get; }

    internal Uri Uri { get; }

    internal string RawUrl { get; }

    internal string TableName { get; }

    internal Exception Exception { get; }

    internal static BeatorajaTableUrlImportOutcome Existing(Uri uri, string tableName)
    {
        return new BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind.Existing, uri, null, tableName, null);
    }

    internal static BeatorajaTableUrlImportOutcome Imported(Uri uri, string tableName)
    {
        return new BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind.Imported, uri, null, tableName, null);
    }

    internal static BeatorajaTableUrlImportOutcome RestoredFromBmt(Uri uri, string tableName)
    {
        return new BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind.RestoredFromBmt, uri, null, tableName, null);
    }

    internal static BeatorajaTableUrlImportOutcome Failed(Uri uri, Exception exception)
    {
        return new BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind.Failed, uri, null, string.Empty, exception);
    }

    internal static BeatorajaTableUrlImportOutcome Failed(string rawUrl, Exception exception)
    {
        return new BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind.Failed, null, rawUrl, string.Empty, exception);
    }

    internal static BeatorajaTableUrlImportOutcome Warning(Uri uri, string tableName, Exception exception)
    {
        return new BeatorajaTableUrlImportOutcome(BeatorajaTableUrlImportOutcomeKind.Warning, uri, null, tableName, exception);
    }
}

internal sealed class BeatorajaTableUrlImportSummary
{
    internal BeatorajaTableUrlImportSummary(IEnumerable<BeatorajaTableUrlImportOutcome> outcomes)
    {
        Outcomes = [.. (outcomes ?? []).Where(outcome => outcome != null)];
    }

    internal IReadOnlyList<BeatorajaTableUrlImportOutcome> Outcomes { get; }

    internal int ExistingCount => Outcomes.Count(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Existing);

    internal int ImportedCount => Outcomes.Count(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Imported);

    internal int RestoredFromBmtCount => Outcomes.Count(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.RestoredFromBmt);

    internal int FailedCount => Outcomes.Count(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Failed);

    internal IReadOnlyList<BeatorajaTableUrlImportOutcome> FailedOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Failed)];

    internal int WarningCount => Outcomes.Count(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Warning);

    internal IReadOnlyList<BeatorajaTableUrlImportOutcome> WarningOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == BeatorajaTableUrlImportOutcomeKind.Warning)];
}
