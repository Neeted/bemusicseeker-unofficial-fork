using System;
using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>
/// URL 取得・自動導入 workflow が一回の操作で参照する設定値です。
/// </summary>
internal sealed class PlaylistUrlAcquisitionOptionsSnapshot
{
    internal bool ScanBmsFilesOnStartup { get; init; }

    internal bool AutoInstall { get; init; }

    internal bool ShouldAutoInstall => ScanBmsFilesOnStartup && AutoInstall;

    /// <summary>譜面・アーカイブ1ファイルの上限（バイト）です。宣言長と実受信量を、それぞれこの値と比べます。</summary>
    internal long MaxFileSizeBytes { get; init; } = Settings.UrlDownloadMaxFileSizeMiBRange.Default * BytesPerMiB;

    /// <summary>ヘッダー待ちと、本文の一回の読取りごとの無受信を切る期間です。</summary>
    internal TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(Settings.UrlDownloadIdleTimeoutSecondsRange.Default);

    private const long BytesPerMiB = 1024L * 1024L;

    internal static PlaylistUrlAcquisitionOptionsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new PlaylistUrlAcquisitionOptionsSnapshot
        {
            ScanBmsFilesOnStartup = settings.ScanBmsFilesOnStartup,
            AutoInstall = settings.AutoInstall,
            MaxFileSizeBytes = settings.UrlDownloadMaxFileSizeMiB * BytesPerMiB,
            IdleTimeout = TimeSpan.FromSeconds(settings.UrlDownloadIdleTimeoutSeconds)
        };
    }
}
