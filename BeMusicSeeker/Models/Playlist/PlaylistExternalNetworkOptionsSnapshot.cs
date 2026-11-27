using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// 外部難度表の取得が一回の操作で参照する通信設定です。
/// 操作の開始時に一度だけ捕捉し、進行中の操作は設定の変更にかかわらず開始時の値を使い続けます。
/// </summary>
internal sealed class PlaylistExternalNetworkOptionsSnapshot
{
    private const int BytesPerMiB = 1024 * 1024;

    /// <summary>設定が未保存の既定値で構成した値です。</summary>
    internal static PlaylistExternalNetworkOptionsSnapshot Default { get; } = new();

    /// <summary>同時に取得する表の最大数です。</summary>
    internal int MaxConcurrency { get; init; } = Settings.ExternalTableSyncMaxConcurrencyRange.Default;

    /// <summary>HTML・header JSON・data JSON の各HTTP要求が、送信開始から本文完了までに使える期限です。</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(Settings.ExternalTableRequestTimeoutSecondsRange.Default);

    /// <summary>HTML本文の展開後の上限（バイト）です。</summary>
    internal int HtmlLimitBytes { get; init; } = Settings.ExternalTableHtmlLimitMiBRange.Default * BytesPerMiB;

    /// <summary>JSON本文の展開後の上限（バイト）です。HTML上限との大小関係は制約しません。</summary>
    internal int JsonLimitBytes { get; init; } = Settings.ExternalTableJsonLimitMiBRange.Default * BytesPerMiB;

    /// <summary>現在の確定値から、一回の操作が使う値を捕捉します。</summary>
    internal static PlaylistExternalNetworkOptionsSnapshot CreateCurrent(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new PlaylistExternalNetworkOptionsSnapshot
        {
            MaxConcurrency = settings.ExternalTableSyncMaxConcurrency,
            RequestTimeout = TimeSpan.FromSeconds(settings.ExternalTableRequestTimeoutSeconds),
            HtmlLimitBytes = settings.ExternalTableHtmlLimitMiB * BytesPerMiB,
            JsonLimitBytes = settings.ExternalTableJsonLimitMiB * BytesPerMiB
        };
    }
}
