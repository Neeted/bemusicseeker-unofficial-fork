# Settings Change Impact and Startup Operations

## Purpose

設定画面の OK は、入力値の保存だけでなくライブラリ初期化・ファイル差分更新・スコア再読み込みを起動する場合がある。
起動直後の初期化中にこれらを再入させると、DB 構築やバックグラウンド更新とは独立した設定変更でも 2 回目の初期化が予約され、進捗表示のフェーズも混線しやすい。

この資料では、設定変更の影響範囲と起動・リロード operation の扱いを固定する。

## Current Settings Boundary

設定ダイアログは、保存済み設定を表す `temp*` snapshot と、現在の `Settings.Default` / LR2 config 値との差分で dirty 判定を行う。
編集バッファは `Settings.Default` が兼ねる。dirty 判定と保存判定は getter の副作用や filesystem の現在状態ではなく、保存時点で明示的に保持した snapshot と現在値の比較を基準にする。

- 表示や dirty 判定は LR2 `config.xml` を保存しない。
- UI 表示や差分比較に使う LR2 BMS 検索ルートは `GetBMSSearchDirectoriesForChangeTracking()` で読み、存在しないディレクトリを勝手に除外しない。
- ランタイム検索対象や保存時の必須検証で実在ディレクトリだけが必要な場合は `GetBMSSearchDirectoriesReadOnly()` を使う。この読み取りも `config.xml` は保存しない。
- LR2 `config.xml` の保存は、BMS 検索ルート変更、custom folder 出力先同期、または autoreload 設定の明示的な正規化が必要な場合だけ行う。
- `Settings.Default` の getter は、表示時に無効 path を `null` へ戻すなどの永続値変更を行わない。値の補正が必要な場合は保存処理、cancel rollback、または設定読み込み時の防御的補正に閉じ込める。

Cancel は次の契約に従う。

- 変更なしの場合は設定ダイアログを閉じるだけにし、全設定 restore、LR2 config 再読み込み、ファイル差分更新、スコア再読み込みを行わない。
- 変更がある場合は `ResetSettings()` で snapshot の値へ戻し、テーマなど preview 適用済みの UI 状態も保存済み値へ戻す。
- Cancel 後に再読み込みが必要な変更が残っていた場合だけ、`IsNeedRestartForSaveOrCancel()` の結果に従って `Initialize()`、`ReloadFileDiff()`、または `ReloadScoresOnly()` を起動する。

OK は次の契約に従う。

- 既存プロファイルで `IsLibraryOperationInProgress == true` の場合、保存せず警告を表示して snapshot の値へ戻す。
- 変更なしの場合は保存・検証・post-save 処理を行わず閉じる。
- 初回設定では full validation を行い、保存後に `Initialize()` を起動する。
- 既存プロファイルでは `CheckValidationBeforeSave()` を使う。validation に関係する設定が変わった場合は full validation を行い、変わっていない場合は現在の必須設定が外部要因で壊れていないかだけを確認する。
- `Settings.Default.Save()` は user.config 対象の変更がある場合だけ呼ぶ。
- `lr2config.Save()` は LR2 BMS 検索ルートまたは autoreload 設定を保存する必要がある場合だけ呼ぶ。
- post-save 処理は `SettingsPostSaveImpact` で分類し、必要な impact flag がある場合だけ `necessaryStepsAfterSaved()` を実行する。
- custom folder search root 同期は `CustomFolderSearchRootSync` impact がある場合だけ実行する。
- player 再生成、LR2 backup 有効化通知、playlist URL completion refresh、LR2 generated-data sync、beatoraja BMT export は、それぞれ対応する impact flag がある場合だけ実行する。
- 保存完了後は `backupSavedSettings()` で snapshot を現在値へ更新する。

`SettingsPostSaveImpact` は post-save 処理の実行条件を表す。

- `CustomFolderSearchRootSync`: LR2 config 境界または custom folder 出力先が変わった場合。
- `PlayerRuntime`: 外部プレイヤー選択、または standalone mode への変更により内部プレイヤーへ戻す必要がある場合。
- `Lr2BackupEnabledNotice`: LR2 mode で LR2 backup を有効化した場合。
- `PlaylistUrlCompletion`: playlist URL completion の有効化、上書き、Stella 対応、または TSV URI が変わった場合。
- `Lr2CoreSync`: LR2 mode で operation mode または LR2 root が変わった場合。
- `ExternalLr2FolderRowsSync`: LR2 mode で custom folder の通常 / root / 追加出力先が変わった場合。ただし `Lr2CoreSync` がある場合は core sync を優先する。
- `BeatorajaBmtExport`: beatoraja BMT 出力、保持、URL 登録、hash mode、root path、または BMT table path が変わった場合。

## LR2 Play History Schema Status

LR2 play history schema status は、設定ダイアログを開いた瞬間に隠れて check しない。
score DB を読む既存の境界で read-only schema check を実行し、その結果を設定ダイアログの表示状態へ publish する。

- startup の score DB 読み込み境界では `CheckLr2PlayHistorySchemaForScoreLoad()` を実行し、結果を保持する。
- `ReloadScoresOnly()` 後も保持済み結果を設定ダイアログへ publish する。
- Play History read で得た `Lr2PlayHistorySchemaCheckResult` が現在の LR2 linked profile の score DB と一致する場合、同じ表示状態へ反映する。
- 設定ダイアログ表示時は保持済み状態を presentation へ反映するだけにする。保持結果がない場合は「状態未確認」を表示する。
- library 側の保持結果が `null` になった場合は、同一セッション内の古い `Installed` / `Repairable` 表示を残さず未確認表示へ戻す。
- 導入 / 修復 / 削除の明示操作では、操作直前に read-only check を行って対象 score DB と状態を確認する。操作成功後は操作後の結果で表示状態を更新し、Play History read cache を破棄する。

## Performance Logging

設定ダイアログ周辺の性能ログは、挙動に影響しない best-effort ログとして扱う。
ログ出力の例外は保存結果、schema check 結果、UI cleanup に影響させない。

- `settings_dialog_open`: ダイアログ表示時の theme selection 同期、schema status presentation 更新、`ContextIdle` 到達までの時間を記録する。`handlerMs` には visible changed handler 自体の時間を記録する。
- `settings_cancel`: Cancel 操作の時間、reset 有無、restart mode を記録する。
- `settings_validation`: OK 時 validation の時間と結果を記録する。
- `settings_change_classification`: 保存時に算出した変更種別と `SettingsPostSaveImpact` を記録する。
- `settings_save`: `SaveSettingsCore()` 全体の時間、validation 結果、変更種別、user.config / LR2 config 保存有無と保存時間を記録する。
- `settings_save_and_close`: OK button handler 全体の時間、結果、validation 時間、保存時間、restart mode を記録する。ユーザーが MessageBox を閉じるまでの待ち時間は含めない。
- `settings_post_save`: post-save impact ごとの処理時間を記録する。
- `settings_backup_snapshot`: snapshot 更新時の standalone roots、custom folder bases、LR2 roots snapshot、play history preset refresh / snapshot の時間を記録する。
- `settings_schema_status_score_load_check`: score DB 読み込み境界の read-only schema check 時間と結果を記録する。
- `settings_schema_status_publish`: library 側に保持した schema check 結果を設定ダイアログへ publish する時間と結果を記録する。

`virtual_order_prewarm` は startup task として既知の重い処理であり、設定ダイアログ操作の性能評価からは分離して扱う。

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
- LR2 play history schema check は score DB 読み込み境界で read-only に行い、共有 cache へ結果を publish する。設定画面を開くだけでは check しない。
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

設定画面の dirty 判定は `Settings.Default.PropertyChanged` の発火有無ではなく、保存済み snapshot と現在値の明示差分で行う。getter の防御的正規化、表示更新、schema status の presentation 更新だけで Cancel が full restore に入ってはならない。dirty 判定は filesystem validation や LR2 XML 保存を含めない。

LR2 play history schema check は設定画面表示時の自動処理にしない。schema status は、アプリ起動時、`ReloadScoresOnly()`、Play History read など score DB を読むタイミングで得た結果を共有 cache から表示に利用し、未確認の場合は「未確認」表示のままにする。Play History read で得た `Lr2PlayHistorySchemaCheckResult` が現在の LR2 linked profile の score DB と一致する場合、同じ cache へ publish する。導入 / 修復 / 削除の明示操作では、操作直前に read-only check を行って対象 score DB と状態を確認する。操作成功後は schema status cache を操作後の結果で更新し、Play History read cache を破棄する。

初回設定の案内は、設定画面で言語と動作モードを選ぶことを先に示す。`スタンドアローン(LR2と連携しない)` では一般タブの BMS ディレクトリとインストールタブの新規インストール先が必須で、`LR2と連携する` では一般タブの LR2 ディレクトリ、プレイリストタブのカスタムフォルダ出力先、インストールタブの新規インストール先が必須になる。必須項目が揃って `OK` が押されるまで、BMS ファイルの初回スキャンは開始しない。

設定保存直後のメッセージは、初回スキャンをこれから開始することを示す。初回完了メッセージは `startup_initialization_complete` 後に表示し、次回以降は差分更新中心になることを伝える。

## Settings File Migration

設定は実行ファイル横の `config/user.config` を正とする。従来版の AppData 配下 `user.config` が見つかった場合は、`LegacyUserConfigMigrator` が初回起動時にコピーする。

このコピー時に、従来版 user.config だけを対象にした互換補正を行う。

- 旧難易度表 URL `http://www.ribbit.xyz/bms/tables/table_info.json` は現行既定 URL に置き換える。
- LR2 連携設定で `LR2RootPath` が空の場合、`LR2ConfigXmlPath` と `LR2SongDBPath` が同じ LR2 ルート配下を指し、`LR2body.exe` または `LRHbody.exe` が存在する場合だけ root path を補完する。
- 旧版で言語設定を OS culture から初期化していた範囲の `AssemblyVersion` では、コピー時に同じ判定で `Lang` を補正する。現在利用できない culture の場合は `en-US` にする。
- 旧 DataGrid 由来の列設定や migration version など、現在使用しない user setting はコピー時および保存時に削除する。

通常起動時の `AssemblyVersion` 更新は、現行バージョンを保存するだけで、バージョン番号を条件にした設定補正を行わない。読み込み後の null 補完やテーマ名正規化など、現行設定値として常に成立させるべき防御的補正は `SettingsLoaded` に残す。

通常起動時は、旧版の `AssemblyVersion` 閾値だけを根拠に LR2 カスタムフォルダを強制再生成しない。LR2 カスタムフォルダの再生成条件は、テーブル更新、出力先欠落、または `.lr2folder` 不在とする。

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

これらはこの仕様の範囲外とし、進捗整合性と二重初期化防止を優先する。
