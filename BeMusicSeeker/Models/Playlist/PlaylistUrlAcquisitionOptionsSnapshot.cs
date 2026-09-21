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

    internal static PlaylistUrlAcquisitionOptionsSnapshot CreateCurrent(Settings settings)
    {
        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        return new PlaylistUrlAcquisitionOptionsSnapshot
        {
            ScanBmsFilesOnStartup = settings.ScanBmsFilesOnStartup,
            AutoInstall = settings.AutoInstall
        };
    }
}
