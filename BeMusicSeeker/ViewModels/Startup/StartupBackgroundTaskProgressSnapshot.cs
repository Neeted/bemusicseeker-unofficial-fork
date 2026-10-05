using BeMusicSeeker.Models.BmsLibraryInternal;

namespace BeMusicSeeker.ViewModels;

/// <summary>スケジューラーが捕捉した要求の識別、分類と実行境界を表示先へ渡します。</summary>
/// <param name="Name">処理名。</param>
/// <param name="Generation">要求受付時に捕捉した既存世代。</param>
/// <param name="Version">スケジューラー通知では要求版。機能所有者の通知ではその主体の要求版。横断比較しません。</param>
/// <param name="IsPostInitialization">既存スケジューラーの後続処理分類。</param>
/// <param name="Request">受付時の発生元。実行開始後や世代変更後に付け直しません。</param>
/// <param name="IsRunning">実行開始ならtrue、終端ならfalse。</param>
internal sealed record StartupBackgroundTaskProgressSnapshot(
    string Name,
    long Generation,
    long Version,
    bool IsPostInitialization,
    bool IsRunning,
    OperationProgressRequest Request = null);
