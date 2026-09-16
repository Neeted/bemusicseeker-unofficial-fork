# 再生パネルの表示

## 目的と適用範囲

再生パネルの保存状態、利用可能な表示面、初期同期と通常のアニメーションを定めます。音声処理の寿命は[音声実行基盤](../runtime/audio.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 保存状態と有効な表示

`PlaybackPanelViewModel.PlayerPanelState` が保存された要求状態の正本です。表示は設定を直接読まず、利用可能な面を考慮した `EffectivePlayerPanelState` を使います。`TITLE_SMALL` は小型表示のフラグ、値0の `TITLE_LARGE` は正規の拡大型であり、未初期化ではありません。

表示面は画像とBMSプレイヤーです。プレイヤーを使えない場合は画像を表示しますが、要求状態は書き換えません。小型指定とプレイヤー指定が併存していても、最初のフレームから画像の小型表示になります。

### 初期同期

高さ、画像のぼかし、大きい題名の余白、バナーと小型題名の不透明度を一つの処理で同期します。初回の有効なViewModelの適用はアニメーションを除去し、最終値へ即時反映します。

DataContextが読込み前・読込み中のどちらで設定されても同じです。表示中の差替え、非表示からの再読込みも初期同期とし、以前のViewModelや表示期間の状態から遷移しません。購読の世代を持ち、古い通知を新しい表示へ適用しません。

| 値 | 小型 `TITLE_SMALL` | 拡大型 `TITLE_LARGE` |
| --- | ---: | ---: |
| パネルの高さ | 110 | 286 |
| ぼかし半径 | 20 | 0 |
| 大きい題名の下余白 | -40 | 0 |
| バナーの不透明度 | 1 | 0 |
| 小型題名の不透明度 | 1 | 1。表示可否は既存の変換処理で決める |

初期同期後の対象プロパティにはアニメーションを残しません。一時的な非表示、任意のDispatcher遅延、固定待機、起動だけの不透明度で初期フレームを隠しません。

### 通常の遷移

初期同期後、同じViewModelと購読世代で小型フラグが変わった場合だけ遷移します。表示面や利用可否だけが変わり、小型フラグが同じ場合は再開しません。

高さとぼかしは約1秒、大きい題名は0.7秒です。小型バナーは0.8秒後から0.5秒で表示し、拡大型へ戻るときは0.2秒で消します。小型題名は小型化の0.8秒後から表示します。完了・置換・非表示時にアニメーションを除去し、プロパティの基本値を最終状態として残します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 要求状態と実効状態、初期フレーム、差替え・再読込み、遷移 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/PlaybackPanelViewModel.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/PlaybackPanelView.xaml.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/PlaybackPanelViewModelTests.cs) |
| 関連する画面状態と更新 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePresentationStateTests`](../../../BeMusicSeeker.Tests/PlaylistWorkspacePresentationStateTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/PlaylistWorkspaceDetailRefreshTests.cs) |
| ルート画面への再生管理主体の接続と表示の能力 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindowViewModel.cs) | [`MainWindowPlaybackWpfTests`](../../../BeMusicSeeker.Tests/MainWindowPlaybackWpfTests.cs) |

## 関連資料

[外観](appearance.md)、[音声基盤](../runtime/audio.md)、[WPFテストの実行](../development/testing.md)を参照します。
