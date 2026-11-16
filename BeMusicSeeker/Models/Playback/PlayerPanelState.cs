using System;

namespace BeMusicSeeker.Models;

/// <summary>
/// メインウィンドウと再生の管理主体が選ぶ、プレイヤーパネルの表示形態を表します。
/// 設定として永続化される値で、Models の設定窓口と画面の両方が使うため、Models に置きます。
/// 設定には列挙子名、または数値の文字列で保存され、旧値の正規化も数値に依存するため、名前・数値・[Flags] を変えません。
/// </summary>
[Flags]
public enum PlayerPanelState
{
    /// <summary>曲名を大きく表示します。値 0 も正規の値です。</summary>
    TITLE_LARGE = 0,

    /// <summary>曲名を小さく表示します。</summary>
    TITLE_SMALL = 1,

    /// <summary>BMS プレイヤーの表示を選ぶフラグです。</summary>
    BMS_PLAYER = 2,
}
