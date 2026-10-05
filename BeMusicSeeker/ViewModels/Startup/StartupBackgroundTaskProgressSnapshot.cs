namespace BeMusicSeeker.ViewModels;

/// <summary>スケジューラーが捕捉した要求の識別、分類と実行境界を表示先へ渡します。</summary>
/// <param name="Name">処理名。</param>
/// <param name="Generation">要求受付時に捕捉した既存世代。</param>
/// <param name="Version">通常はスケジューラー要求版。スコア適用と順位更新はモデル要求版。</param>
/// <param name="IsPostInitialization">既存スケジューラーの後続処理分類。</param>
/// <param name="IsRunning">実行開始ならtrue、終端ならfalse。</param>
internal sealed record StartupBackgroundTaskProgressSnapshot(
    string Name,
    long Generation,
    long Version,
    bool IsPostInitialization,
    bool IsRunning);
