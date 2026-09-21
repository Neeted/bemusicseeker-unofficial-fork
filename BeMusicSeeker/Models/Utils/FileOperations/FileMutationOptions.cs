using System;

namespace BeMusicSeeker.Models.Utils;

/// <summary>
/// 変更系ファイル操作の補正範囲と再試行条件をまとめます。
/// </summary>
internal sealed class FileMutationOptions
{
    /// <summary>
    /// 変更系ファイル操作の補正範囲と再試行条件を初期化します。
    /// </summary>
    /// <param name="readOnlyNormalizationScope">ReadOnly 属性を解除する範囲です。</param>
    /// <param name="retryOnSharingViolation">共有違反時に短時間リトライする場合は true です。</param>
    /// <param name="retryOnAccessDenied">アクセス拒否時に ReadOnly 補正後の短時間リトライを行う場合は true です。</param>
    /// <param name="retryDelayMs">リトライ間隔（ミリ秒）です。</param>
    /// <param name="maxRetryCountOnSharingViolation">共有違反時の最大再試行回数です。</param>
    /// <param name="maxRetryCountOnAccessDenied">アクセス拒否時の最大再試行回数です。</param>
    /// <exception cref="ArgumentOutOfRangeException">リトライ回数または待機時間が負の場合に送出されます。</exception>
    public FileMutationOptions(
        ReadOnlyNormalizationScope readOnlyNormalizationScope = ReadOnlyNormalizationScope.None,
        bool retryOnSharingViolation = true,
        bool retryOnAccessDenied = true,
        int retryDelayMs = 150,
        int maxRetryCountOnSharingViolation = 4,
        int maxRetryCountOnAccessDenied = 3)
    {
        if (retryDelayMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelayMs));
        }
        if (maxRetryCountOnSharingViolation < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryCountOnSharingViolation));
        }
        if (maxRetryCountOnAccessDenied < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetryCountOnAccessDenied));
        }

        ReadOnlyNormalizationScope = readOnlyNormalizationScope;
        RetryOnSharingViolation = retryOnSharingViolation;
        RetryOnAccessDenied = retryOnAccessDenied;
        RetryDelayMs = retryDelayMs;
        MaxRetryCountOnSharingViolation = maxRetryCountOnSharingViolation;
        MaxRetryCountOnAccessDenied = maxRetryCountOnAccessDenied;
    }

    /// <summary>
    /// ReadOnly 属性を解除する範囲です。
    /// </summary>
    public ReadOnlyNormalizationScope ReadOnlyNormalizationScope { get; }

    /// <summary>
    /// 共有違反時に短時間リトライするかを表します。
    /// </summary>
    public bool RetryOnSharingViolation { get; }

    /// <summary>
    /// アクセス拒否時に ReadOnly 補正後の短時間リトライを行うかを表します。
    /// </summary>
    public bool RetryOnAccessDenied { get; }

    /// <summary>
    /// リトライ間隔（ミリ秒）です。
    /// </summary>
    public int RetryDelayMs { get; }

    /// <summary>
    /// 共有違反時の最大再試行回数です。
    /// </summary>
    public int MaxRetryCountOnSharingViolation { get; }

    /// <summary>
    /// アクセス拒否時の最大再試行回数です。
    /// </summary>
    public int MaxRetryCountOnAccessDenied { get; }
}
