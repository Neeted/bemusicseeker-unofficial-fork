using BeMusicSeeker.TestSupport.Components;

namespace BeMusicSeeker.WindowsProcessProbe;

/// <summary>静的初期化と設定互換の旧保証を、一要求一fresh processで最後まで検証します。</summary>
internal static class PlaybackProbe
{
    /// <summary>全表明と設定領域の回収後だけ成功結果を公開します。</summary>
    internal static int Run(string caseName)
    {
        Exception? primary = null;
        try
        {
            switch (caseName)
            {
                case nameof(BassCollectibleLoadContextProbe.BassAudioPlayer_StaticInitializationDoesNotEnumerateOrLoadNativeRuntime):
                    new BassCollectibleLoadContextProbe().BassAudioPlayer_StaticInitializationDoesNotEnumerateOrLoadNativeRuntime();
                    break;
                case nameof(PlayerPanelStateSettingsCompatibilityProbe.PlayerPanelState_PreservesPersistedKeyNamesAndValues):
                    new PlayerPanelStateSettingsCompatibilityProbe().PlayerPanelState_PreservesPersistedKeyNamesAndValues();
                    break;
                case nameof(PlayerPanelStateSettingsCompatibilityProbe.PlayerPanelState_AtomicNormalizationFailurePreservesOriginalAndMaterializesCanonicalValue):
                    new PlayerPanelStateSettingsCompatibilityProbe().PlayerPanelState_AtomicNormalizationFailurePreservesOriginalAndMaterializesCanonicalValue();
                    break;
                default: throw new ArgumentException("未知のplayback caseです: " + caseName);
            }
        }
        catch (Exception exception) { primary = exception; }
        finally
        {
            // Bassだけの実行では設定型やResourcesを初期化しない。
            if (caseName.StartsWith("PlayerPanelState_", StringComparison.Ordinal))
            {
                try { TestSettingsFactory.CleanupIsolatedSettingsDirectories(); }
                catch (Exception cleanup)
                {
                    if (primary is null) { primary = cleanup; }
                    else { primary.Data["SettingsCleanupFailure"] = cleanup; }
                }
            }
        }
        if (primary is not null)
        {
            Console.Error.WriteLine(primary);
            if (primary.Data["SettingsCleanupFailure"] is Exception cleanup) { Console.Error.WriteLine(cleanup); }
            return 2;
        }
        Program.WriteResult(new { Case = caseName, Passed = true });
        return 0;
    }
}
