# Settings Change Impact and Startup Operations

## Purpose

設定画面の OK は、入力値の保存だけでなくライブラリ初期化・ファイル差分更新・スコア再読み込みを起動する場合がある。
起動直後の初期化中にこれらを再入させると、DB 構築やバックグラウンド更新とは独立した設定変更でも 2 回目の初期化が予約され、進捗表示のフェーズも混線しやすい。

この資料では、設定変更の影響範囲と起動・リロード operation の扱いを固定する。

## Impact Classes

### UI-only

対象例:

- 外観テーマ
- 言語
- 表示・確認ダイアログ・詳細設定のうちライブラリ内容を再構築しないもの
- プレイヤー表示や通常 UI の選択状態

扱い:

- ライブラリ初期化、ファイル差分更新、スコア再読み込みは起動しない。
- 起動・リロード進捗が active の間は、設定保存自体を受け付けない。これは UI-only だけの変更でも同じで、設定画面 OK の副作用を単純化するため。

### Score-only

対象例:

- beatoraja score.db の利用有無
- beatoraja score.db パス

扱い:

- 既存プロファイルが有効な通常状態では `ReloadScoresOnly()` を起動する。
- 起動・リロード進捗が active の間は適用不可。

### Folder/File Diff

対象例:

- LR2 config.xml パス
- LR2 config.xml 由来の BMS 検索ルート変更
- standalone mode の BMS 検索ルート変更

扱い:

- 既存プロファイルが有効な通常状態では `ReloadFileDiff()` を起動する。
- 起動・リロード進捗が active の間は適用不可。

### Full Reinitialize

対象例:

- LR2 連携モードの切り替え
- LR2 song.db パス
- `Score-only` と `Folder/File Diff` の同時変更

扱い:

- 既存プロファイルが有効な通常状態では `Initialize()` を起動する。
- 起動・リロード進捗が active の間は適用不可。

## Startup Apply Gate

設定画面 OK は、保存前に現在のライブラリ operation を確認する。

- `IsLibraryOperationInProgress == true`: 保存せず、設定を適用できない旨を表示する。
- `IsLibraryOperationInProgress == false`: 通常の保存・反映判定に進む。

初回設定ダイアログは、まだ進捗 operation が active ではないため保存可能とする。
初期化が始まった後に設定画面を開いた場合は、起動・バックグラウンド更新が完全に終わるまで保存不可とする。

## Operation Serialization

`Initialize()`, `ReloadFileDiff()`, `ReloadScoresOnly()`, `ReloadTables()`, `ReinitializeLibrary()` は `_semaphore` で直列化される。
ただし `_semaphore` は同時実行を防ぐだけで、ユーザー操作から 2 回目の operation を予約することまでは防がない。

そのため設定画面 OK の時点で active operation を拒否し、意図しない予約を作らない。

## Progress Ownership

起動・リロード進捗は operation token で所有者を区別する。

- 新しい progress operation は `_semaphore` 取得後、実際にその operation を開始する直前に作成する。
- 初回 `Initialize()` は新しい `BMSLibrary` / `BMSPlaylist` を作成してから progress baseline を取る。
- UI suppress の遅延 flush、ライブラリフォルダツリーの遅延更新、外部 playlist sync、playlist reference apply は、スケジュール時の operation token と現在の token が一致する場合だけ進捗フェーズを完了させる。

これにより、古い operation の遅延イベントが新しい operation の `StartupReadyUi`, `StartupReadyOperable`, playlist reference, external sync などを誤って進めることを防ぐ。

## Current Non-goals

- 起動中の library-affecting 設定変更をキューして、初期化完了後に自動適用すること。
- UI-only 設定だけを起動中に部分保存すること。
- `_semaphore` を廃止して operation coordinator へ全面移行すること。

これらは将来の改善候補だが、現時点では進捗整合性と二重初期化防止を優先する。
