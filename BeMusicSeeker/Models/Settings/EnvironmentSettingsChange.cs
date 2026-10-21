using BeMusicSeeker.Properties;

namespace BeMusicSeeker.Models;

/// <summary>環境の確定と再起動受付へ渡す、画面候補を参照しない最小の変更です。</summary>
internal sealed record EnvironmentSettingsChange(bool OperationModeLR2DB, string LR2RootPath, string LR2SongDBPath,
    string LR2ConfigXmlPath, string BeatorajaRootPath, string BeatorajaPlayerId, string BeatorajaScoreDbPath, string BeatorajaBmtTablePath)
{
    /// <summary>確定値から環境の必要項目だけを捕捉します。</summary>
    internal static EnvironmentSettingsChange Capture(Settings settings) => new(settings.OperationModeLR2DB, settings.LR2RootPath,
        settings.LR2SongDBPath, settings.LR2ConfigXmlPath, settings.BeatorajaRootPath, settings.BeatorajaPlayerId,
        settings.BeatorajaScoreDbPath, settings.BeatorajaBmtTablePath);

    /// <summary>環境だけを選択保存し、成功後に確定値へ公開します。</summary>
    internal void Commit(Settings settings) => settings.Commit(
        (nameof(Settings.OperationModeLR2DB), OperationModeLR2DB),
        (nameof(Settings.LR2RootPath), LR2RootPath), (nameof(Settings.LR2SongDBPath), LR2SongDBPath),
        (nameof(Settings.LR2ConfigXmlPath), LR2ConfigXmlPath), (nameof(Settings.BeatorajaRootPath), BeatorajaRootPath),
        (nameof(Settings.BeatorajaPlayerId), BeatorajaPlayerId), (nameof(Settings.BeatorajaScoreDbPath), BeatorajaScoreDbPath),
        (nameof(Settings.BeatorajaBmtTablePath), BeatorajaBmtTablePath));
}
