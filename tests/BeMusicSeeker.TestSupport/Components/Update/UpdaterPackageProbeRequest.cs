namespace BeMusicSeeker.TestSupport.Components.Update;

/// <summary>生存アプリと再起動診断の固定probeへ、親caseが所有する領域と残期限を渡します。</summary>
/// <param name="RootDirectory">親caseの専有領域です。HKCUと領域の最終回収は親が行います。</param>
/// <param name="UpdaterPath">catalog Currentから専有領域へコピー済みの実updaterです。</param>
/// <param name="Restarting">元DataRowのjournal中断状態です。結果でも同じ値を照合します。</param>
/// <param name="RemainingTicks">親の単調180秒期限の残時間です。親自身も同じ元期限でprobeを監視します。</param>
internal sealed record UpdaterPackageProbeRequest(string RootDirectory, string UpdaterPath, bool Restarting, long RemainingTicks);
