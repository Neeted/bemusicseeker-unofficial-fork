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

/// <summary>現起動の論理仕事と実終端を投影する変更不能な集計です。成功件数ではありません。</summary>
/// <param name="OperationToken">登録元の起動操作。</param>
/// <param name="Generation">登録元のscheduler世代。</param>
/// <param name="Revision">同世代の通知順序。</param>
/// <param name="Total">受理済み論理仕事数。warmupは一連で一件です。</param>
/// <param name="Terminated">正常・失敗・開始前破棄で実終端した仕事数。</param>
/// <param name="IsComplete">登録閉鎖・開始と対象仕事の実終端がすべて成立したか。</param>
internal sealed record StartupAdditionalProgressSnapshot(long OperationToken, long Generation,
    long Revision, int Total, int Terminated, bool IsComplete);
