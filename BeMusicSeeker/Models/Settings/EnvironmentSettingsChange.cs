using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>モードとLR2パスだけを環境確定・再起動受付へ渡します。beatorajaの組は局所反映が所有します。</summary>
internal sealed record EnvironmentSettingsChange(bool OperationModeLR2DB, string LR2RootPath, string LR2SongDBPath,
    string LR2ConfigXmlPath)
{
    /// <summary>確定値から環境の必要項目だけを捕捉します。</summary>
    internal static EnvironmentSettingsChange Capture(Settings settings) => new(settings.OperationModeLR2DB, settings.LR2RootPath,
        settings.LR2SongDBPath, settings.LR2ConfigXmlPath);

    /// <summary>環境だけを選択保存し、成功後に確定値へ公開します。</summary>
    internal void Commit(Settings settings) => settings.Commit(
        (nameof(Settings.OperationModeLR2DB), OperationModeLR2DB),
        (nameof(Settings.LR2RootPath), LR2RootPath), (nameof(Settings.LR2SongDBPath), LR2SongDBPath),
        (nameof(Settings.LR2ConfigXmlPath), LR2ConfigXmlPath));
}
