namespace BeMusicSeeker.Models.BmsLibraryInternal;

/// <summary>検査時の事実を表示へ渡す変更不能な結果です。翻訳済み文言を保持しません。</summary>
internal sealed class Lr2PlayHistorySchemaStatusSnapshot
{
    private Lr2PlayHistorySchemaStatusSnapshot(
        bool isReset,
        Lr2PlayHistorySchemaStatus status,
        string scoreDbPath,
        Lr2PlayHistorySchemaReason reason,
        string diagnostic)
    {
        IsReset = isReset;
        Status = status;
        ScoreDbPath = scoreDbPath ?? string.Empty;
        Reason = reason;
        Diagnostic = diagnostic ?? string.Empty;
    }

    internal bool IsReset { get; }

    internal Lr2PlayHistorySchemaStatus Status { get; }

    internal string ScoreDbPath { get; }

    /// <summary>言語に依存しない検査結果の詳細理由です。</summary>
    internal Lr2PlayHistorySchemaReason Reason { get; }

    /// <summary>検査時の診断を保持し、表示のための再検査を避けます。</summary>
    internal string Diagnostic { get; }

    internal static Lr2PlayHistorySchemaStatusSnapshot Reset { get; } = new(
        isReset: true,
        Lr2PlayHistorySchemaStatus.Unreadable,
        scoreDbPath: string.Empty,
        reason: Lr2PlayHistorySchemaReason.None,
        diagnostic: string.Empty);

    /// <summary>検査結果から状態・対象・理由・診断を複写し、後の表示言語変更と分離します。</summary>
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
            result.Reason,
            result.Diagnostic);
    }
}
