using System;
using System.IO;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 変更系ファイル操作が最終的に失敗したときに、診断情報を保持して送出されます。
/// </summary>
internal sealed class FileMutationException : IOException
{
    /// <summary>
    /// 変更系ファイル操作の失敗を表す例外を初期化します。
    /// </summary>
    /// <param name="kind">失敗した操作種別です。</param>
    /// <param name="primaryPath">主対象パスです。</param>
    /// <param name="secondaryPath">副対象パスです。</param>
    /// <param name="attemptCount">実行試行回数です。</param>
    /// <param name="normalizedReadOnlyCount">ReadOnly 属性を解除した件数です。</param>
    /// <param name="win32ErrorCode">抽出できた Win32 エラーコードです。取得できない場合は -1 です。</param>
    /// <param name="wasRetried">再試行が行われた場合は true です。</param>
    /// <param name="rootCause">根本原因となった元例外です。</param>
    /// <exception cref="ArgumentNullException">主対象パスまたは元例外が null の場合に送出されます。</exception>
    public FileMutationException(
        FileMutationKind kind,
        string primaryPath,
        string secondaryPath,
        int attemptCount,
        int normalizedReadOnlyCount,
        int win32ErrorCode,
        bool wasRetried,
        Exception rootCause)
        : base(BuildMessage(kind, primaryPath, secondaryPath, attemptCount, normalizedReadOnlyCount, win32ErrorCode, wasRetried, rootCause), rootCause)
    {
        Kind = kind;
        PrimaryPath = primaryPath ?? throw new ArgumentNullException(nameof(primaryPath));
        SecondaryPath = secondaryPath;
        AttemptCount = attemptCount;
        NormalizedReadOnlyCount = normalizedReadOnlyCount;
        Win32ErrorCode = win32ErrorCode;
        WasRetried = wasRetried;
        RootCause = rootCause ?? throw new ArgumentNullException(nameof(rootCause));
    }

    public FileMutationException() : base()
    {
    }

    public FileMutationException(string message) : base(message)
    {
    }

    public FileMutationException(string message, int hresult) : base(message, hresult)
    {
    }

    public FileMutationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// 失敗した操作種別です。
    /// </summary>
    public FileMutationKind Kind { get; }

    /// <summary>
    /// 主対象パスです。
    /// </summary>
    public string PrimaryPath { get; }

    /// <summary>
    /// 副対象パスです。
    /// </summary>
    public string SecondaryPath { get; }

    /// <summary>
    /// 実行試行回数です。
    /// </summary>
    public int AttemptCount { get; }

    /// <summary>
    /// ReadOnly 属性を解除した件数です。
    /// </summary>
    public int NormalizedReadOnlyCount { get; }

    /// <summary>
    /// 抽出できた Win32 エラーコードです。取得できない場合は -1 です。
    /// </summary>
    public int Win32ErrorCode { get; }

    /// <summary>
    /// 再試行が行われたかを表します。
    /// </summary>
    public bool WasRetried { get; }

    /// <summary>
    /// 根本原因となった元例外です。
    /// </summary>
    public Exception RootCause { get; }

    private static string BuildMessage(
        FileMutationKind kind,
        string primaryPath,
        string secondaryPath,
        int attemptCount,
        int normalizedReadOnlyCount,
        int win32ErrorCode,
        bool wasRetried,
        Exception rootCause)
    {
        string rootCauseTypeName = rootCause?.GetType().FullName ?? "(null)";
        string rootCauseMessage = rootCause?.Message ?? string.Empty;
        return string.Format(
            "file_mutation_failed kind={0} path={1} secondaryPath={2} attempts={3} normalizedReadOnly={4} win32={5} retried={6} rootType={7} error={8}",
            kind,
            primaryPath ?? "(null)",
            string.IsNullOrWhiteSpace(secondaryPath) ? "(none)" : secondaryPath,
            attemptCount,
            normalizedReadOnlyCount,
            win32ErrorCode,
            wasRetried,
            rootCauseTypeName,
            rootCauseMessage);
    }
}
