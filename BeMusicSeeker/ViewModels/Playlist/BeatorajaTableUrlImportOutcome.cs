using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

    /// <summary>失敗または警告が一件以上あり、要約の表示を注意の種類にするかを返します。</summary>
    internal bool HasFailureOrWarning => FailedCount > 0 || WarningCount > 0;

    /// <summary>
    /// 要約の本文を現在の文化圏で整形します。一段落目に各件数を示し、警告・失敗の節を順に続けます。
    /// 各節は空行の後に見出しを置き、標本を最大5件だけ並べて、超えた分は「- ...」で示します。標本の数は利用者に見える既存の表示の形です。
    /// </summary>
    internal string FormatMessage()
    {
        var message = new StringBuilder();
        message.AppendFormat(
            BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_summary_format,
            ExistingCount,
            ImportedCount,
            RestoredFromBmtCount,
            FailedCount,
            WarningCount);
        AppendOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_warning_header, WarningOutcomes);
        AppendOutcomeSamples(message, BeMusicSeeker.Properties.Resources.Beatoraja_table_url_import_result_failed_header, FailedOutcomes);
        return message.ToString();
    }

    private static void AppendOutcomeSamples(StringBuilder message, string header, IReadOnlyList<BeatorajaTableUrlImportOutcome> outcomes)
    {
        const int maxSamples = 5;
        if (outcomes.Count == 0)
        {
            return;
        }
        message.AppendLine();
        message.AppendLine();
        message.AppendLine(header);
        foreach (BeatorajaTableUrlImportOutcome outcome in outcomes.Take(maxSamples))
        {
            string nameOrUri = !string.IsNullOrWhiteSpace(outcome.TableName) ? outcome.TableName : (outcome.Uri?.ToString() ?? outcome.RawUrl ?? string.Empty);
            message.AppendLine(outcome.Exception != null && !string.IsNullOrWhiteSpace(outcome.Exception.Message)
                ? "- " + nameOrUri + " (" + outcome.Exception.Message + ")"
                : "- " + nameOrUri);
        }
        if (outcomes.Count > maxSamples)
        {
            message.AppendLine("- ...");
        }
    }
}
