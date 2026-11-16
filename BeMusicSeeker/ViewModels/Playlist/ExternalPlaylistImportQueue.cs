using System;
using System.Collections.Generic;
using System.Linq;

namespace BeMusicSeeker.ViewModels;

/// <summary>一要求の対象ごとの登録、通信前のURL除外、取得後の同名除外、失敗を区別します。</summary>
internal enum ExternalPlaylistImportOutcomeKind
{
    Imported,
    SkippedDuplicateUrl,
    SkippedDuplicateName,
    Failed
}

/// <summary>取得元URIを維持した一対象のインポート結果です。</summary>
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

    /// <summary>既存表とページまたはヘッダーURLが一致し、取得せず除いた結果を作ります。</summary>
    /// <param name="uri">今回選択されたURI。</param>
    /// <param name="tableName">一致した既存表の名前。</param>
    internal static ExternalPlaylistImportOutcome SkippedDuplicateUrl(Uri uri, string tableName)
    {
        return new ExternalPlaylistImportOutcome(ExternalPlaylistImportOutcomeKind.SkippedDuplicateUrl, uri, tableName, null);
    }

    internal static ExternalPlaylistImportOutcome Failed(Uri uri, Exception exception)
    {
        return new ExternalPlaylistImportOutcome(ExternalPlaylistImportOutcomeKind.Failed, uri, string.Empty, exception);
    }
}

/// <summary>一要求内の結果を集計します。後続要求の予約や永続キューは保持しません。</summary>
internal sealed class ExternalPlaylistImportQueueSummary
{
    internal ExternalPlaylistImportQueueSummary(IEnumerable<ExternalPlaylistImportOutcome> outcomes)
    {
        Outcomes = [.. (outcomes ?? []).Where(outcome => outcome != null)];
    }

    internal IReadOnlyList<ExternalPlaylistImportOutcome> Outcomes { get; }

    internal int ImportedCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Imported);

    internal int SkippedDuplicateNameCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.SkippedDuplicateName);

    /// <summary>通信前に登録済みURLとの一致で除いた対象数を返します。</summary>
    internal int SkippedDuplicateUrlCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.SkippedDuplicateUrl);

    internal int FailedCount => Outcomes.Count(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed);

    /// <summary>除外や失敗があり、完了時の結果通知が必要かを返します。</summary>
    internal bool HasNotifiableItems => SkippedDuplicateUrlCount > 0 || SkippedDuplicateNameCount > 0 || FailedCount > 0;

    /// <summary>同URLで取得を省いた対象の名前とURIを、通知用に返します。</summary>
    internal IReadOnlyList<ExternalPlaylistImportOutcome> SkippedDuplicateUrlOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.SkippedDuplicateUrl)];

    internal IReadOnlyList<ExternalPlaylistImportOutcome> SkippedDuplicateNameOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.SkippedDuplicateName)];

    internal IReadOnlyList<ExternalPlaylistImportOutcome> FailedOutcomes => [.. Outcomes.Where(outcome => outcome.Kind == ExternalPlaylistImportOutcomeKind.Failed)];
}
