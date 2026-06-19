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
- OK は user.config への保存と即時反映が必要な UI 状態だけを扱い、LR2 `config.xml` / custom folder 出力先 / BMS root の整合処理を起動しない。
- Cancel は保存済み snapshot と現在値の差分が無い場合は閉じるだけにし、全設定の restore、theme / culture 再適用、`LR2Config` 再読み込みを行わない。
- 起動・リロード進捗が active の間は、設定保存自体を受け付けない。これは UI-only だけの変更でも同じで、設定画面 OK の副作用を単純化するため。

### Score-only

対象例:

- beatoraja score.db の利用有無
- beatoraja score.db パス
- LR2 linked profile の player score DB path に影響する LR2 root / player config 変更

扱い:

- 既存プロファイルが有効な通常状態では `ReloadScoresOnly()` を起動する。
- LR2 play history schema check は score DB 読み込み境界で行い、設定画面を開くだけでは行わない。
- score reload 後は Play History read cache を破棄する。Play History view は次回利用時に現在の score source から履歴 row を全件ロードし直す。
- 起動・リロード進捗が active の間は適用不可。

### Play History Display

対象例:

- `プレイログ FOLDER 表示プリセット`

扱い:

- user.config の JSON を保存し、Play History の表示対象 dropdown を再構築する。
- playlist 正本、playlist entries、LR2 `song.db`、custom folder 出力は変更しない。

### Startup-only / Next-use

対象例:

- 起動時の file check skip
- pending install source scan policy
- encoder / player の詳細設定のうち、次回実行時または該当機能利用時にだけ参照されるもの

扱い:

- user.config へ保存するだけで、現在の library / score / custom folder / LR2 config に即時整合処理をかけない。
- Cancel 時は snapshot 差分がある設定だけを戻し、無変更なら閉じるだけにする。

### Folder/File Diff

対象例:

- LR2 config.xml パス
- LR2 config.xml 由来の BMS 検索ルート変更
- standalone mode の BMS 検索ルート変更
- LR2 custom folder の通常 / 追加 / root 出力先

扱い:

- 既存プロファイルが有効な通常状態では `ReloadFileDiff()` を起動する。
- LR2 custom folder 出力先の LR2 `config.xml` search root 同期、出力先移行、管理外 `.lr2folder` 同期は、custom folder 出力先設定が変わった場合だけ実行する。
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

設定画面の dirty 判定は `Settings.Default.PropertyChanged` の発火有無ではなく、保存済み snapshot と現在の draft / settings 値の明示差分で行う。getter の防御的正規化、表示更新、schema status の presentation 更新だけで Cancel が full restore に入ってはならない。

LR2 play history schema check は設定画面表示時の自動処理にしない。schema status は、アプリ起動時または Play History read など score DB を読むタイミングで得た結果を表示に利用し、未確認の場合は「未確認」表示のままにする。Play History read で得た `Lr2PlayHistorySchemaCheckResult` が現在の LR2 linked profile の score DB と一致する場合、設定画面の表示状態へ共有してよい。導入 / 修復 / 削除の明示操作では、操作直前に read-only check を行って対象 score DB と状態を確認する。操作成功後は Play History read cache を破棄する。

初回設定の案内は、設定画面で言語と動作モードを選ぶことを先に示す。`スタンドアローン(LR2と連携しない)` では一般タブの BMS ディレクトリとインストールタブの新規インストール先が必須で、`LR2と連携する` では一般タブの LR2 ディレクトリ、プレイリストタブのカスタムフォルダ出力先、インストールタブの新規インストール先が必須になる。必須項目が揃って `OK` が押されるまで、BMS ファイルの初回スキャンは開始しない。

設定保存直後のメッセージは、初回スキャンをこれから開始することを示す。初回完了メッセージは `startup_initialization_complete` 後に表示し、次回以降は差分更新中心になることを伝える。

## Settings File Migration

設定は実行ファイル横の `config/user.config` を正とする。従来版の AppData 配下 `user.config` が見つかった場合は、`LegacyUserConfigMigrator` が初回起動時にコピーする。

このコピー時に、従来版 user.config だけを対象にした互換補正を行う。

- 旧難易度表 URL `http://www.ribbit.xyz/bms/tables/table_info.json` は現行既定 URL に置き換える。
- LR2 連携設定で `LR2RootPath` が空の場合、`LR2ConfigXmlPath` と `LR2SongDBPath` が同じ LR2 ルート配下を指し、`LR2body.exe` または `LRHbody.exe` が存在する場合だけ root path を補完する。
- 旧版で言語設定を OS culture から初期化していた範囲の `AssemblyVersion` では、コピー時に同じ判定で `Lang` を補正する。現在利用できない culture の場合は従来どおり `en-US` にする。
- 旧 DataGrid 由来の列設定や migration version など、現行実装が読まない既知の廃止 user setting はコピー時および保存時に削除する。

通常起動時の `AssemblyVersion` 更新は、現行バージョンを保存するだけで、バージョン番号を条件にした設定補正を行わない。読み込み後の null 補完やテーマ名正規化など、現行設定値として常に成立させるべき防御的補正は `SettingsLoaded` に残す。

旧版の `AssemblyVersion` 閾値だけを根拠に LR2 カスタムフォルダを強制再生成する処理は廃止する。現在はテーブル更新、出力先欠落、または `.lr2folder` 不在を通常の再生成条件にする。

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
