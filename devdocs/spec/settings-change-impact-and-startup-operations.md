# Settings Change Impact and Startup Operations

## Purpose

設定画面の OK は、入力値の保存だけでなくライブラリ初期化・ファイル差分更新・スコア再読み込みを起動する場合がある。
起動直後の初期化中にこれらを再入させると、DB 構築やバックグラウンド更新とは独立した設定変更でも 2 回目の初期化が予約され、進捗表示のフェーズも混線しやすい。

この資料では、設定変更の影響範囲と起動・リロード operation の扱いを固定する。

## Current Settings Boundary

設定ダイアログは、保存済み設定を表す `temp*` snapshot と、現在の `Settings.Default` / LR2 config 値との差分で dirty 判定を行う。
編集バッファは `Settings.Default` が兼ねる。dirty 判定と保存判定は getter の副作用や filesystem の現在状態ではなく、保存時点で明示的に保持した snapshot と現在値の比較を基準にする。

### Settings Window Presentation

設定UIは `MainWindow` 内の overlay ではなく、`MainWindow` を owner とする独立した modal `SettingsWindow` として表示する。
表示には共通 dialog coordinator の owner 解決と `ShowDialog()` 経路を使用し、設定ウィンドウが開いている間は `MainWindow` を操作できない状態にする。owner を持たない表示や、失敗を無視して modeless 表示へ切り替える fallback は行わない。

変更操作が進行中であることだけを理由に、設定画面の表示・編集・既存のCancel／closeを禁止したり、全面read-onlyへ変更したりしない。既存のdialog重複、edit completion、audio test、reload retry pending等の個別制限は維持する。画面を開けることはSave、schema操作、再初期化等の実行許可ではなく、下記のOK契約とStartup Apply Gate、各ownerのavailabilityを使う。

modal表示はbackground処理の停止を意味せず、`Settings.Default`は独立draftではない。新規・改修する処理で必要な設定の取得時点と所有を扱う際は、[共通並行性契約 section 6.1](workflow-concurrency-and-complexity.md#61-例外を拡大せずに適用する)に従う。全設定のdraft再設計、既存UI previewの撤廃、Saveを予約して後で自動適用する変更は行わない。

- 設定ウィンドウは表示ごとに生成し、同時に複数表示しない。既存ウィンドウの表示中に open request を受けた場合は、そのウィンドウを前面へ戻す。
- 設定ウィンドウは標準の WPF title bar を持ち、リサイズ可能とする。titleは設定画面自身を表すlocalized resourceを使い、Advanced categoryのlocalized labelと共有しない。位置、サイズ、選択カテゴリ、scroll位置は永続化しない。
- title bar は独自chromeへ置換しない。source initialization 後に current theme の semantic brushからnative caption属性を更新し、表示中のtheme変更へ追従する。DWM非対応時やnative失敗時はsystem fallbackを維持し、設定画面の表示やclose lifecycleを失敗させない。theme購読はclose時に解除する。
- 現行の11カテゴリと順序（Advanced → Right-click settings → About）を維持し、各カテゴリ本文を parameterless `UserControl` が1つずつ所有する。すべてのカテゴリは同じ `SettingsDialogViewModel` を inherited `DataContext` として共有し、カテゴリ固有の ViewModel や category state persistence は追加しない。
- `SettingsWindow` は navigation、選択カテゴリheader、単一の本文 `ScrollViewer`、下部action、close lifecycle と operation gate を所有する。これらは固定し、選択されたカテゴリ本文だけを縦scrollする。カテゴリ切替時は本文scrollを先頭へ戻す。
- Playlist / Install / Backup / Advanced / About は、`SettingsSection`、`SettingsField`、`SettingsOptionRow`、`SettingsPathPicker`、`SettingsListEditor`、`SettingsStatusBanner` の平坦なpresentationで構成し、`GroupBox` / `Expander` や入れ子cardを使用しない。collection editorは横scrollを無効にし、操作可能な最小list高とaccessible nameを持つ。
- General の LR2 integration は、LR2 path/status、song.db data resync、play-history schema status/install-repair をそれぞれ独立した `SettingsSection` として表示する。各 action の既存 click handler、LR2 mode gate、resync 時の close / forced resync、schema の availability・status・button 契約は変えず、section heading だけを localized resource から与える。
- Audio は output、advanced、test / latency measurement、volume の semantic section に分ける。latency field と test playback / measurement action は同じ dedicated test section に置き、`Device_setting_latency` を field label として使う。advanced section は advanced controls、volume section は volume control のみを所有し、既存の test availability、status、成功時の latency 更新契約を変えない。
- Playlist の MD5 URL mapping と play-history folder display preset は別々の `SettingsSection` とする。preset の既存 localized heading / description は section が一度だけ所有し、内側の collection editor で重複表示しない。
- Backup はplaylist backup / restoreとLR2 scheduled backupだけを所有する。LR2 play-history schema uninstallとapplication-data uninstallはAdvancedのDanger zoneだけに表示し、既存owner、確認順序、operation gate、failure presentation、cache reload、terminal close契約を変更しない。
- About はapp identity、version/build、license/credits、links、Release Notes actionだけを表示する。version/build値はpage lifetime中不変だが、その2つのformat済み表示はpageがloadedの間だけculture通知を購読して同じ表示中に更新し、unload時に購読を解除する。release historyの正本は `ReleaseNotesWindow.xaml` に置き、Settingsのinject済みdialog coordinatorからowner=`SettingsWindow`のmodal windowとして開く。Release Notesは`CenterOwner`、`ShowInTaskbar=false`、標準native title barとし、settings draftやpresentation lifecycleを開始・終了しない。
- About から開く Release Notes は、selection-enabled かつ read-only の `FlowDocumentScrollViewer` が framework-owned default context menu を使う。application-local `ContextMenu`、専用 menu 文言、clipboard / cursor の横取りは行わない。現行 .NET 10 WPF の公開 menu role は `ApplicationCommands.Copy` と `ApplicationCommands.SelectAll` であり、caption と popup position は framework variation とする。Cut/Copy/Paste 固定、selection 外の右クリックでは開かないこと、Select All を除外することを要求する legacy right-click 契約は採用しない。
- 初期サイズは `820x760`、最小サイズは `820x600`、左navigation幅は `216` とする。本文と全カテゴリpageは利用可能な横幅へ連続的にstretchし、幅上限、breakpoint、実測幅converterを設けない。横scrollは無効にし、最小サイズでも本文、section、field、card、input、listを横方向へclipしない。位置、サイズ、選択カテゴリ、scroll位置は再表示へ引き継がず、毎回 General から開始する。
- label / value / action を並べる設定行は、固定位置へ詰め込まず伸縮可能な Grid を使用する。長い翻訳labelとcheckbox contentは折り返し、最小サイズでも横scrollを必要としないことを presentation contract とする。
- 下部actionでは Save and close を accent action、Cancel を quiet action として区別する。controlの通常、hover、focus、disabled状態は既存theme resourceと標準control behaviorを維持し、設定画面専用の固定paletteを追加しない。
- カテゴリ検索、カテゴリ再分類、設定値の即時保存化、独立draftへの移行はこのpresentation変更の対象外とする。
- light / dark theme と表示中のculture変更は、同じ設定ウィンドウへ反映する。新しいユーザー向け文言は全言語resourceで管理する。
- 設定ウィンドウから開くpicker、確認dialog、子Windowは、active modal ownerとして設定ウィンドウを所有者にする。
- picker、drop、focus、selection などカテゴリ固有の terminal behavior は対応する category control が所有する。manual resync、schema、backup / restore、uninstall など shell gate が必要な操作だけを、狭い Window method へ接続する。
- title barのclose、Alt+F4、Escは通常のCancelと同じrollback契約へ接続する。`IsEditCancellationEnabled == false` の間はuser closeを拒否し、reload retryを迂回させない。
- Apply成功、変更なしApply、manual LR2 resyncなどViewModelからのclose requestは、Cancel rollbackを再実行しない。shell shutdownは設定ウィンドウのcancel guardで妨げない。
- 設定ウィンドウの表示中は従来どおりplayback surfaceを抑止し、accepted、cancelled、failed、shutdownを含むすべての終了経路で復元する。
- 表示開始時は保持済みのappearance selectionとLR2 play-history schema statusだけをpresentationへ反映し、表示を理由に新しいschema checkを起動しない。
- operation mode の radio は ViewModel から表示へだけ同期し、初期binding、group内の自動check/uncheck、再表示、Cancelでは選択要求を発生させない。実際のradio clickだけが選択意図を1回送信し、active library profileでmodeが変わる場合は既存の確認と mode restart request を各1回だけ実行する。request は既存の UI dispatcher で Close/update と先着判定を共有し、受理後は通常の Close/terminal 経路へ接続する。
- audio device test 中は既存の edit completion / cancellation gate を維持し、test workflow 完了前の Save、Cancel、native closeを許可しない。status は progress / result text と icon を併記する。
- `SettingsStatusBanner` はnull、空、空白だけのstring statusを表示せず、messageがbinding経由で非blankへ戻った場合は自動で再表示する。non-string contentおよびcaller指定のCollapsedは上書きしない。path status の message resource は状態文だけを持ち、glyph は banner の `Icon` が一つだけ描画する。表示中のbannerはmessageをAutomation Name、semantic statusをItemStatusとして公開し、Polite live updateを使用する。decorative iconはAutomation treeへ独立表示しない。GeneralとAdvancedのLR2 schema statusは、local valueではなく同じStyleのdefault setterとrepair可能時triggerで `Information/i` と `Warning/!` を切り替え、repair不可へ戻った時にdefaultへ復帰する。
- 閉じた設定ウィンドウは presentation を解除して Window lifecycle を完了した後、共有 `SettingsDialogViewModel` への `DataContext` と binding graph を切り離す。
- close後に遅延した `ContentRendered` callback が到着しても、presentation を再有効化せず終了する。
- table-list URI とplaylist metadata URIのinline validation messageはpresentation transientとする。不正入力はraw URIとdirty状態を変更せず、同じWindow内のカテゴリ切替、再Activate、validation failureではmessageを維持する。Cancel、変更なしSave、native closeを含む終了後、共有ViewModelを使う次のfresh SettingsWindow activationで両messageをnotification付きで消去する。`ResetSettings()` はこのtransient cleanupのownerにしない。

- 表示や dirty 判定は LR2 `config.xml` を保存しない。
- PlaybackPanel の background 起動待ち中も Settings の表示・編集は停止しない。LR2body の試聴 scope が active な間に作成する `LR2Config` は、最新 disk XML の非所有値と保存済みの `system/windowsize_x`、`windowsize_y`、`screenmode`、`sound/volumemaster`、`volumeflag` を draft へ読み込む。試聴の一時値を draft の初期値へ混入させない。
- 試聴中の通常 Save / BMS 検索 root Save は同じ短い保存排他へ参加し、active scope の保存済み5項目（元の欠落を含む）を保存文書へ適用してから draft の非所有変更を公開する。公開に成功した場合だけ、scope の保存値をその保存文書から同期する。試聴 publish は復元値を更新しない。試聴終了・起動失敗時は最新 XML の非所有変更を保持したまま、5値を開始時の値または欠落へ戻す。設定 Save で作成した draft は同じ試聴の後続復元へ共有しない。
- UI 表示や差分比較に使う LR2 BMS 検索ルートは `GetBMSSearchDirectoriesForChangeTracking()` で読み、存在しないディレクトリを勝手に除外しない。
- ランタイム検索対象や保存時の必須検証で実在ディレクトリだけが必要な場合は `GetBMSSearchDirectoriesReadOnly()` を使う。この読み取りも `config.xml` は保存しない。
- LR2 `config.xml` の保存は、BMS 検索ルート変更、custom folder 出力先同期、または autoreload 設定の明示的な正規化が必要な場合だけ行う。
- `Settings.Default` の getter は、表示時に無効 path を `null` へ戻すなどの永続値変更を行わない。値の補正が必要な場合は保存処理、cancel rollback、または設定読み込み時の防御的補正に閉じ込める。

### LR2 path draft contract

- `LR2RootPath`、`LR2SongDBPath`、`LR2ConfigXmlPath` は互換性のため独立した raw setting として維持し、startup consumer は保存済み child path を再導出しない。
- raw `LR2ConfigXmlPath` と、読み込み済みの `LR2Config` object / validation status は別状態とする。dialog open、`ResetSettings()`、またはpickerでconfigがmissing / unreadable / malformedだった場合もraw pathを`null`や空へ正規化しない。無関係な設定をSaveしてreopenした場合も同じraw valueを保持する。
- 標準配置は root 配下の `LR2files\Database\song.db` と `LR2files\Config\config.xml|config.xmh` とする。path比較はWindowsのcase-insensitive full-path比較を使い、xmlとxmhはいずれも標準配置とみなす。
- root picker は候補root、標準child path、解析済みconfigを先に解決し、有効なtupleだけを3 raw draftへ一括反映する。現在と同じ有効rootの再選択でもfreshな解析済みconfigを採用する。成功時の通知は root、song、config のraw tupleを先に送り、その後にstatusとselection errorを含むdependent presentationを各1回送って、設定画面表示中に外部更新されたXMLを表示へ反映する。後の保存でもstale documentから上書きしない。root / configが無効なら3値と現在の解析済みobjectをすべて変更せずfailureを即時表示する。期待される song.db が未作成でも新root側の標準pathをdraftへ設定し、以前のrootのsong.dbをfallbackとして残さない。linked modeの保存validationがmissingを明示する。
- rootが空でchild pathがある場合、またはchild pathが標準配置と異なる場合だけcustom configurationとする。custom stateはcomputed presentation stateであり、永続flagを追加しない。
- advanced LR2 path dialogはsong/configの初期値をdialog-local draftへcopyし、直接入力とpickerはこのlocal draftだけを更新する。pickerは直前のtyped valueをinitial directoryに使い、accepted candidateをbindingを壊さない `SetCurrentValue` で同じeditorへ反映する。missing / unreadable / malformed candidateはlocal valueと親draftを変えずfailureを表示する。Done / Enterは両editorの現在textを単一tupleとして検証し、両方が有効な場合だけ親draftへ一括反映する。Cancel / Esc / native closeはlocal draftを捨てるだけで親draftを変更しない。Doneでは永続化せず、親Settings Saveが3 raw valueを保存する。
- legacy root inferenceはportable config copy時の互換migrationに限定し、通常startupや設定画面表示を理由にuser.configを書き換えない。

Cancel は次の契約に従う。

- 変更なしの場合は設定ダイアログを閉じるだけにし、全設定 restore、LR2 config 再読み込み、ファイル差分更新、スコア再読み込みを行わない。
- 変更がある場合は `ResetSettings()` で snapshot の値へ戻し、テーマなど preview 適用済みの UI 状態も保存済み値へ戻す。
- score reload または file diff reload が失敗して retry pending の場合は、保存済み設定とライブ状態の不一致を隠さないため Cancel を無効にし、OK から同じ operation を再試行できる状態を保つ。
- retry pending 中は LR2 手動再同期など設定画面を閉じる別の操作も無効にし、失敗した reload の retry を迂回できないようにする。
- Cancel は保存済み snapshot へ戻した後、必要な preview 状態を shell へ通知して設定ダイアログを閉じる。Cancel から library 初期化や reload operation は起動しない。

OK は次の契約に従う。

- 既存プロファイルで `IsLibraryOperationInProgress == true` の場合、保存せず警告を表示して snapshot の値へ戻す。
- 変更なしの場合は、active library profile が有効なら保存・検証・post-save 処理を行わず閉じる。初期化失敗後など active profile が無い場合は、変更がなくても初期設定 apply と初期化 retry の経路へ進む。
- 初回設定では full validation を行い、保存後に awaitable な `InitializeAsync()` を起動する。`MainWindow.ContentRendered` (`async void` event boundary) と設定 apply の両 caller が同じ owner を await する。
- 既存プロファイルでは `CheckValidationBeforeSave()` を使う。validation に関係する設定が変わった場合は full validation を行い、変わっていない場合は現在の必須設定が外部要因で壊れていないかだけを確認する。
- `Settings.Default.Save()` は user.config 対象の変更がある場合だけ呼ぶ。
- `lr2config.Save()` は LR2 BMS 検索ルートまたは autoreload 設定を保存する必要がある場合だけ呼ぶ。
- post-save 処理は `SettingsPostSaveImpact` で分類し、必要な impact flag がある場合だけ `necessaryStepsAfterSaved()` を実行する。
- custom folder search root 同期は `CustomFolderSearchRootSync` impact がある場合だけ実行する。
- player 再生成、LR2 backup 有効化通知、playlist URL completion refresh、LR2 generated-data sync、beatoraja BMT export は、それぞれ対応する impact flag がある場合だけ実行する。
- 保存完了後は `SettingsSnapshotRefreshScope` で snapshot 更新範囲を分類し、変更された設定範囲に必要な snapshot / 表示状態だけを現在値へ更新する。
- `ReloadScoresOnlyAsync()` と `ReloadFileDiffAsync()` は設定ダイアログから使う awaitable reload capability とし、成功時に対応する retry pending を解除し、失敗時は pending を保持して次の OK を同じ reload へ接続する。schema 導入 / 修復 / 削除後の score reload も score capability を使う。

`SettingsSnapshotRefreshScope` は保存後 snapshot 更新の範囲を表す。`backupSavedSettings()` は初期化や reset 後の full snapshot 更新に使い、通常の OK 保存後は `backupSavedSettingsCore(scope)` で部分更新する。

- `StandaloneSearchRoots`: standalone BMS root list を再読込し、standalone root snapshot を更新する。
- `Lr2SearchRoots`: LR2 config 由来の BMS search root snapshot を更新する。
- `CustomFolderOutputBase`: custom folder 追加出力先 list を再読込する。通常出力先と追加通常出力先は LR2 `<jukebox>` 同期にも影響するため `Lr2SearchRoots` と併用する。通常出力先は従来版互換の chart scan root として残すが、BMS directory 一覧 / install destination 候補 / library folder tree node からは除外する。追加通常出力先と root 出力先は LR2 `<jukebox>` には登録するが、chart scan root とこれらのユーザー向け候補から除外する。
- `PlayHistoryDisplayPreset`: Play History 表示プリセット list を再読込し、プリセット snapshot を更新する。
- `OperationMode`: operation mode 表示状態を更新する。
- `ValidationState`: 保存可否や dirty 判定に関係する notification を更新する。
- `Full`: 初期 snapshot、reset 後 snapshot、operation mode 変更など、境界全体を取り直す必要がある場合に使う。

LR2 root / LR2 config path / operation mode / LR2 BMS search root / custom folder 出力先など LR2 config 境界が変わる場合は、post-save の custom folder search root sync が LR2 config の search root を補完する可能性がある。そのため、直接 BMS search root を編集していない場合でも `Lr2SearchRoots` を含め、保存後の LR2 search root snapshot を同期後の値へ更新する。新しく通常出力先を選択する場合、user.config で既に管理されていない LR2 `<jukebox>` 登録済み root と同一または親子関係になる path は validation error とし、保存時確認による採用にはしない。最終的な `<jukebox>` の BMS search root 同士に同一・親子関係の重なりがある状態は異常として扱い、手動編集などで既に存在する場合は保存時 validation で是正を促す。親 root 配下の一部 directory だけを scan 除外する通常仕様は持たない。

保存後 snapshot 更新では、外観テーマ、言語、通常 UI 設定など search root / custom folder / play history preset に影響しない変更で root list や custom folder list の再読込を行わない。これらの list 再読込は `ObservableCollection` 更新と property notification を伴い、設定ダイアログ表示中は binding 経由で search root / custom folder / LR2 directory 候補の再評価へ連鎖しやすいため、対応する scope がある場合だけ行う。

root list / custom folder list の再読込は、保存値と現在の `ObservableCollection` / selected item が一致している場合、collection clear / add と notification を行わない。同じ値を再代入して dirty 判定や候補リストを再評価させない。

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
- `ReloadScoresOnlyAsync()` 後も保持済み結果を設定ダイアログへ publish する。
- Play History read で得た `Lr2PlayHistorySchemaCheckResult` が現在の LR2 linked profile の score DB と一致する場合、同じ表示状態へ反映する。
- 設定ダイアログ表示時は保持済み状態を presentation へ反映するだけにする。保持結果がない場合は「状態未確認」を表示する。
- library 側の保持結果が `null` になった場合は、同一セッション内の古い `Installed` / `Repairable` 表示を残さず未確認表示へ戻す。
- 導入 / 修復 / 削除の明示操作では、操作直前に read-only check を行って対象 score DB と状態を確認する。操作成功後は操作後の結果で表示状態を更新し、Play History read cache を破棄する。

## Performance Logging

設定ダイアログ周辺の性能ログは、挙動に影響しない best-effort ログとして扱う。
ログ出力の例外は保存結果、schema check 結果、UI cleanup に影響させない。

- `settings_dialog_open`: ダイアログ表示時の theme selection 同期、schema status presentation 更新、`ContextIdle` 到達までの時間を記録する。`handlerMs` には visible changed handler 自体の時間を記録する。
- `settings_cancel`: Cancel 操作の時間と reset 有無を記録する。
- `settings_validation`: OK 時 validation の時間と結果を記録する。
- `settings_change_classification`: 保存時に算出した変更種別と `SettingsPostSaveImpact` を記録する。
- `settings_save`: `SaveSettingsCore()` 全体の時間、validation 結果、変更種別、user.config / LR2 config 保存有無と保存時間を記録する。
- `settings_apply`: OK command 全体の時間、結果、validation 時間、保存時間、restart mode を記録する。ユーザーが MessageBox を閉じるまでの待ち時間は含めない。
- `settings_post_save`: post-save impact ごとの処理時間を記録する。
- `settings_backup_snapshot`: snapshot 更新時の scope、standalone roots、custom folder bases、LR2 roots snapshot、play history preset refresh / snapshot の時間を記録する。
- `settings_schema_status_score_load_check`: score DB 読み込み境界の read-only schema check 時間と結果を記録する。
- `settings_schema_status_publish`: library 側に保持した schema check 結果を設定ダイアログへ publish する時間と結果を記録する。

`virtual_order_prewarm` は startup task として既知の重い処理であり、設定ダイアログ操作の性能評価からは分離して扱う。

## Known Improvement Candidates

設定変更後処理は、post-save impact と snapshot refresh scope の分解により、UI-only 設定では保存後の root / custom folder / play history preset 再読込を避けられる状態になっている。
現行ログでは、外観テーマや言語などの軽微な変更は `postSaveImpact=None`、`scope=None`、`backupSnapshotMs=0` で完了し、保存本体も数 ms から数十 ms 程度に収まる。

一方で、OK 押下後の総時間は `settings_validation` が支配的になる場合がある。これは現行仕様の許容範囲内だが、将来さらに詰める場合の改善候補として扱う。

- 軽微な UI-only 変更でも `CheckValidationBeforeSave()` は現在の必須設定の外部状態を確認するため、path / file existence / player / backup / beatoraja / install dir などの検証が固定費になる。
- Play History 表示プリセットの変更では、プリセット JSON の整合性だけで済むケースでも full validation 相当の経路へ入り、他カテゴリの filesystem validation が同時に走る可能性がある。
- 今後改善する場合は、特定の設定名だけを特別扱いするのではなく、変更範囲から `ValidationImpact` を分類し、必須設定再確認、Play History preset 検証、外部ファイル存在確認、full validation を分ける。
- その前段として、`settings_validation_detail` のような詳細ログで validation 内訳を記録する。候補は LR2 core path、standalone roots、custom folder output、beatoraja、player executable、install dir、LR2 backup、Play History preset JSON など。
- 詳細ログも挙動に影響しない best-effort とし、validation の成否や保存結果を変えない。

## Impact Classes

### UI-only

対象例:

- 外観テーマ
- CustomTableView のフォントサイズ、行高、ヘッダー高
- 言語
- 表示・確認ダイアログ・詳細設定のうちライブラリ内容を再構築しないもの
- プレイヤー表示や通常 UI の選択状態

扱い:

- ライブラリ初期化、ファイル差分更新、スコア再読み込みは起動しない。
- OK は user.config への保存と即時反映が必要な UI 状態だけを扱い、LR2 `config.xml` / custom folder 出力先 / BMS root の整合処理を起動しない。
- Cancel は保存済み snapshot と現在値の差分が無い場合は閉じるだけにし、全設定の restore、theme / culture 再適用、`LR2Config` 再読み込みを行わない。
- CustomTableView の外観3値は編集中もメイン一覧へ即時反映し、Cancel では保存済み snapshot へ戻して同じ一覧へ通知する。設定画面内に独立した静的一覧 preview は持たない。
- 失敗していない起動・リロード進捗が active の間は、設定保存自体を受け付けない。score-only / file-diff operation または設定画面から起動した初期化の失敗後は、cleanup 済みの failed 表示を保持しつつ同じ設定反映を retry できる。

### Score-only

対象例:

- beatoraja score.db の利用有無
- beatoraja score.db パス
- LR2 linked profile の player score DB path に影響する LR2 root / player config 変更

扱い:

- 既存プロファイルが有効な通常状態では `ReloadScoresOnlyAsync()` を起動する。
- LR2 play history schema check は score DB 読み込み境界で read-only に行い、共有 cache へ結果を publish する。設定画面を開くだけでは check しない。
- score reload 後は Play History read cache を破棄する。Play History view は次回利用時に現在の score source から履歴 row を全件ロードし直す。
- 失敗していない起動・リロード進捗が active の間は適用不可。score-only / file-diff operation の失敗後は cleanup 済みなら retry できる。reload pending 中に full initialize へ遷移して失敗した場合も、cleanup 後は初期設定 apply の retry を受け付ける。

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

#### `RightClickActionsJson`

`RightClickActionsJson` は Startup-only / Next-use の設定である。設定画面の `右クリック設定` カテゴリを開くだけでは保存済み値を変更せず、`Webページを開く` / `プログラムから開く` の draft はページ内に保持する。`Save` は全アクションを検証して一度にシリアライズし、既存の settings edit session へ原子的に引き渡す。`Cancel` または native close は draft を破棄し、保存済み raw 値を変更しない。

保存後は次に通常一覧、未所持プレイリスト行、または Play History のコンテキストメニューを開いた時点で現在の保存値を読み取る。保存を理由に library reload、score reload、file diff、Play History の再構築、または外部プログラムの起動は行わない。hash-only の行は `Webページを開く` だけ、local chart に解決できる行は `プログラムから開く` も対象となる。

保存済み raw 値が invalid の場合、runtime は action を公開せず、設定画面は診断と常時使用できる `既定値へ戻す` / `Restore defaults` を recovery として提示する。復元は draft だけを置き換え、Save するまで invalid 値を自動補正したり有効部分だけを採用したりしない。明示的な empty aggregate は empty のまま保存され、default actions を再投入しない。

### Folder/File Diff

対象例:

- LR2 config.xml パス
- LR2 config.xml 由来の BMS 検索ルート変更
- standalone mode の BMS 検索ルート変更
- LR2 custom folder の通常 / 追加 / root 出力先

扱い:

- 既存プロファイルが有効な通常状態では awaitable な `ReloadFileDiffAsync()` を起動する。
- LR2 custom folder 出力先の LR2 `config.xml` search root 同期、出力先移行、管理外 `.lr2folder` 同期は、custom folder 出力先設定が変わった場合だけ実行する。
- 失敗していない起動・リロード進捗が active の間は適用不可。file-diff operation の失敗後は cleanup 済みなら retry できる。

### Full Reinitialize

対象例:

- LR2 song.db パス
- `Score-only` と `Folder/File Diff` の同時変更

扱い:

- 既存プロファイルが有効な通常状態では、LR2 song.db 境界または score/folder の同時変更に対して `InitializeAsync()` を起動する。LR2 linked / standalone の mode 切替は process restart として扱い、この operation では処理しない。
- 失敗していない起動・リロード進捗が active の間は適用不可。

## Startup Apply Gate

設定画面 OK は、保存前に現在のライブラリ operation を確認する。

- `IsLibraryOperationInProgress == true`: 保存せず、設定を適用できない旨を表示する。failed score-only / file-diff operation または設定画面から起動した初期化は cleanup 後にこの busy 判定から外れるため、保存済み設定を再保存せず同じ反映を retry できる。
- `IsLibraryOperationInProgress == false`: 通常の保存・反映判定に進む。

初回設定ダイアログは、まだ進捗 operation が active ではないため保存可能とする。
初期化が始まった後に設定画面を開いた場合、`startup_ready_operable`までのUI block、activeなrequired startup / reload progress、またはchart mutationがある間は保存不可とする。`startup_initialization_complete`後も続くpost-initialization maintenanceだけでは保存をblockしない。設定変更と競合する個別owner operationがある場合は、そのownerのavailabilityで別途禁止する。

設定画面の dirty 判定は `Settings.Default.PropertyChanged` の発火有無ではなく、保存済み snapshot と現在値の明示差分で行う。getter の防御的正規化、表示更新、schema status の presentation 更新だけで Cancel が full restore に入ってはならない。dirty 判定は filesystem validation や LR2 XML 保存を含めない。

LR2 play history schema check は設定画面表示時の自動処理にしない。schema status は、アプリ起動時、`ReloadScoresOnlyAsync()`、Play History read など score DB を読むタイミングで得た結果を共有 cache から表示に利用し、未確認の場合は「未確認」表示のままにする。Play History read で得た `Lr2PlayHistorySchemaCheckResult` が現在の LR2 linked profile の score DB と一致する場合、同じ cache へ publish する。導入 / 修復 / 削除の明示操作では、操作直前に read-only check を行って対象 score DB と状態を確認する。操作成功後は schema status cache を操作後の結果で更新し、Play History read cache を破棄する。

初回設定の案内は、設定画面で言語と動作モードを選ぶことを先に示す。`スタンドアローン(LR2と連携しない)` では一般タブの BMS ディレクトリとインストールタブの新規インストール先が必須で、`LR2と連携する` では一般タブの LR2 ディレクトリ、プレイリストタブのカスタムフォルダ出力先、インストールタブの新規インストール先が必須になる。必須項目が揃って `OK` が押されるまで、BMS ファイルの初回スキャンは開始しない。

設定保存直後のメッセージは、初回スキャンをこれから開始することを示す。初回完了メッセージは required local initialization を表す `startup_initialization_complete` 後に表示し、次回以降は差分更新中心になることを伝える。external sync、physical audit、export、prewarmは後続してよい。

## Settings File Migration

設定は実行ファイル横の `config/user.config` を正とする。従来版の AppData 配下 `user.config` が見つかった場合は、`LegacyUserConfigMigrator` が初回起動時にコピーする。既存の portable config とコピーした legacy config は、最初の `Settings` getter / read より前に同じ正規化 owner を通す。

この正規化は idempotent で、保存時だけに遅延させない。旧 `PlayerPanelState` の MOVIE bit (4) を BMS bit (2) へ移し、compact bit と未知の bit を保持する。`UseExternalWebBrowser` を含む obsolete setting key は同じ正規化処理で削除する。

正規化結果の永続化に失敗した場合は、既存の portable config を変更せず parse 可能な状態で保持する。永続化の成否にかかわらず、同一起動中の read path は読み込んだ設定をメモリ上で正規化し、旧値に対応する現行の値を返す。

コピー時には、従来版 user.config だけを対象にした残りの互換補正も行う。

- 旧難易度表 URL `http://www.ribbit.xyz/bms/tables/table_info.json` は現行既定 URL に置き換える。
- LR2 連携設定で `LR2RootPath` が空の場合、`LR2ConfigXmlPath` と `LR2SongDBPath` が同じ LR2 ルート配下を指し、`LR2body.exe` または `LRHbody.exe` が存在する場合だけ root path を補完する。
- 旧版で言語設定を OS culture から初期化していた範囲の `AssemblyVersion` では、コピー時に同じ判定で `Lang` を補正する。現在利用できない culture の場合は `en-US` にする。
- 旧 DataGrid 由来の列設定や migration version など、現在使用しない user setting はコピー時および保存時に削除する。

通常起動時の `AssemblyVersion` 更新は、現行バージョンを保存するだけで、バージョン番号を条件にした設定補正を行わない。読み込み後の null 補完やテーマ名正規化など、現行設定値として常に成立させるべき防御的補正は `SettingsLoaded` に残す。

通常起動時は、旧版の `AssemblyVersion` 閾値だけを根拠に LR2 カスタムフォルダを強制再生成しない。LR2 カスタムフォルダの再生成条件は、テーブル更新、出力先欠落、または `.lr2folder` 不在とする。

## Portable settings failure boundary

`ApplicationStartupCompositionOwner` は App の single-instance mutex 取得、設定準備、main-window composition の順に実行する。所有権が取れない起動は設定の read / legacy migration / recovery / write を行わない。App の constructor は path と logging の準備だけを行い、設定 getter は呼ばない。

`PortableSettingsProvider` の正常な Save return は永続化成功を意味する。読み取り専用、アクセス・共有違反、serialization、構造不正、書込み失敗は対象 path と元の cause を保持した `PortableSettingsException` で伝播する。provider は UI を表示しない。同じ directory の一時ファイルを atomic replace / move で公開し、失敗時は既存バイトを保持する。一時ファイル cleanup の失敗は元の保存失敗を置き換えない。

欠落ファイルは provider の通常 read では既定値を意味する。既存ファイルの unreadable / XML syntax error は空設定ではない。`configuration/userSettings/BeMusicSeeker.Properties.Settings` の必須構造欠落も明示的な失敗とし、個別 setting key の欠落だけは既定値にする。

startup file owner は最初の getter より前に入力を検証する。XML syntax corruption の場合だけ元ファイルを同 directory の一意な `user.config.broken-*` へ上書きせず移動し、backup path と既定値への reset を警告してから最小の正常 XML を新規作成する。first-run 判定は新しい設定の AssemblyVersion 欠落から行う。部分 salvage と同一起動での legacy fallback は行わない。警告前に新規作成したり、破損設定から言語を読んだりしない。usable settings がない段階の通知は OS / default resource culture を使う。

退避成功後の新規作成が失敗した場合も fatal とする。次回起動で target がなく、同 directory に `user.config.broken-*` の実ファイルが残っていれば legacy 自動移行を抑止して新規作成する。既存の正常な target がある場合はその設定を優先する。追加の marker、journal、メモリ上の復旧 flag は持たない。

read、構造、退避、新規作成、legacy import の失敗は error を表示して起動を中止する。以後の getter、theme、main-window composition は行わず、terminal cleanup が mutex を解放する。一方、正常に読み込めた設定の正規化または version 更新の保存失敗は warning を表示し、読込済みの互換値を使って起動を続ける。通常 Save 時の XML 破損は保存失敗であり、startup recovery を実行しない。 version 更新の Save が内部でファイルを再読込する際の Read failure も fatal とし、初回 materialization が成功していても warning 継続へ変換しない。

## Operation Serialization

`InitializeAsync()`, `ReloadFileDiffAsync()`, `ReloadScoresOnlyAsync()`, `ReloadTables()`, `ReinitializeLibraryAsync()` は `_semaphore` で直列化される。`MainWindow.ContentRendered` はこの awaitable operation を起動して初期選択を適用する presentation boundary である。
ただし `_semaphore` は同時実行を防ぐだけで、ユーザー操作から 2 回目の operation を予約することまでは防がない。

そのため設定画面 OK の時点で active operation を拒否し、意図しない予約を作らない。score-only / file-diff operation または設定画面から起動した初期化が失敗した場合は、失敗表示を保持しつつ `IsLibraryOperationInProgress` の busy 判定を解除し、semaphore と UI suppression の cleanup 後に設定画面 OK から同じ反映を再試行できるようにする。他の active / failed operation は従来どおり busy として扱う。

## Progress Ownership

起動・リロード進捗は operation token で所有者を区別する。

- 新しい progress operation は `_semaphore` 取得後、実際にその operation を開始する直前に作成する。
- 初回 `InitializeAsync()` は新しい `BMSLibrary` / `BMSPlaylist` を作成してから progress baseline を取る。
- UI suppress の遅延 flush、ライブラリフォルダツリーの遅延更新、外部 playlist sync、playlist reference apply は、スケジュール時の operation token と現在の token が一致する場合だけ進捗フェーズを完了させる。

これにより、古い operation の遅延イベントが新しい operation の `StartupReadyUi`, `StartupReadyOperable`, playlist reference, external sync などを誤って進めることを防ぐ。

## Current Non-goals

- 起動中の library-affecting 設定変更をキューして、初期化完了後に自動適用すること。
- UI-only 設定だけを起動中に部分保存すること。
- `_semaphore` を廃止して operation coordinator へ全面移行すること。

これらはこの仕様の範囲外とし、進捗整合性と二重初期化防止を優先する。

## Verification map

設定画面の Functional ownership は、現在の `serial-state-a`（1 worker / `ClassLevel`）で process-local な WPF `Application` / STA dispatcher / settings state を直列化する。foreground interaction と non-activating presentation は同じ serial host を共有するが、`TestWindowPresentationScope` は既定の non-activating HWND に `WS_EX_NOACTIVATE` を永続設定して readback するため、foreground を必要とする exact 7 method 以外は foreground を取得しない。旧 `settings-edit-foreground-classwide`、`settings-window-nonactivating-classwide`、`settings-state-classwide` の named host route は退役済みである。

| behavior | canonical fixture | Functional route |
| --- | --- | --- |
| foreground keyboard、focus、hit-testing、nested modal activation と LR2 advanced path commit の7 method | `SettingsForegroundInteractionTests`（`SettingsWindow_NavigationSupportsKeyboardAutomationAndResetsPageScroll`、`SettingsComboBox_HitTestingPreservesWholeSurfaceAndEditableTextRoutes`、`SettingsControlDictionary_OverridesOuterImplicitStylesAndMaterializesClosedRoutes`、`SettingsWindow_ManualResyncClosesAndQueuesForcedWorkflow`、`Lr2AdvancedPathsDialog_EnterCommitsFocusedEditorBeforeAccepting`、`Lr2AdvancedPathsDialog_EnterKeepsDialogOpenWhenFocusedCandidateIsRejected`、`Lr2AdvancedPathsDialog_InitialInvalidTupleStaysOpenAndFocusesRejectedEditor`） | `serial-state-a`, 1 worker / `ClassLevel`; この exact 7 methodだけが `TestWindowActivation.ForegroundInteraction` を要求 |
| non-foreground SettingsWindow presentation | `SettingsWindowPresentationTests`（foreground 7 methodは含めない） | `serial-state-a`, 1 worker / `ClassLevel`; 共有 host の既定 `NonActivating` policy が offscreen、非foreground、native `WS_EX_NOACTIVATE` を検証 |
| process-local settings/application state | existing exact 14 classes | `serial-state-a`, 1 worker / `ClassLevel` |

runner は上記 route の実際の class selector、worker、scope、remaining exclusion、foreground allowlistを起動前に検証する。反復時の対象は `FullyQualifiedName~VerificationRunnerContractTests` の filtered Quick とする。

Portable settings failure packet `PORTABLE-SETTINGS-FAILURE-20260905` revision 5 の検証対応:

| IDs | Behavior / fixture | Lane / ownership |
| --- | --- | --- |
| P01-P04 | strict read/save、atomic bytes、syntax quarantine / `PortableSettingsPersistenceTests`（新設: filesystem persistence の fault matrix を既存 global fixture から分離） | remaining Functional、instance-owned provider path / unique temp directory、同期 return・throw・readback |
| P03/P04b/P06 | fatal read、valid save warning、generated canonical read、create failure後の新 owner 起動と実 legacy移行抑止 / `ApplicationSettingsLifecycleTests` | existing serial-state-a、Resources.Culture 復元、追加 physical cases は unique temp path |
| P05 | ownership denial / preparation fatal / successful composition gate / `ApplicationStartupCompositionOwnerTests` | existing serial-state-b、owner task completion、既存 WPF host |
| P03/P06 | migration compatibility / `PortableSettingsMigrationTests`、`PlayerPanelStateSettingsCompatibilityTests` | existing routes、旧 normalizer byte preservation assertion を維持 |
| P10 | resx/accessor/six-language parity、backup/path/cause placeholder / `LocalizationResourceParityTests` | read-only resources、exact translated copy assertion なし |

## Runtime settings save failures

設定ダイアログの保存失敗はエラーを表示し、画面、下書き、保存前snapshotを保持する。後処理・reload・closeへ進まず、保存のため一時適用したaudio出力選択を復元する。Cancelは以前の設定編集snapshotへ戻し、再保存はユーザーの次のSave操作で行う。user.config成功後にLR2 config.xml保存が失敗した場合は部分保存を明示し、既に保存したuser.configを巻き戻さない。

動作モード変更では、確認したmode、現在の履歴表示identity、runtimeのLR2位置だけを同じproviderのatomic保存境界で保存する。他の設定下書きは保存しない。保存が成功し、request が first-wins で受理された場合だけ Reload と通常の Close/terminal へ進む。保存失敗またはrequest拒否時はactive modeのraw値と表示を復元し、他の下書きを保持して再起動・終了しない。terminal末尾の後継 process start failure は、MainWindow の既存 shutdown authorization を維持したまま、failure notification の完了を待って観測する。

MainWindowの検索ルート追加・削除は下位ownerの保存失敗伝播を保持し、View terminalでエラーを表示する。

LR2停止時のwindow placement取得は既存Settingsのメモリ値だけを更新する。停止・次曲・player切替では設定Saveを呼ばない。通常の設定SaveはUI編集がなくても未保存のruntime位置を保存する。既存PropertyValuesのdirty状態で判断し、別の永続flagは持たない。Cancelはruntime位置を戻さない。強制終了では最後の未保存位置が失われる場合がある。

LR2 の試聴開始後に設定画面が開いた場合、試聴用の一時5値は Settings の同一 draft へ引き継がれない。起動後にその draft で BMS 検索 root を編集して Save しても、保存文書には root の変更と非所有値だけを反映し、試聴5値は active scope の保存済み値へ戻す。process Start 前の失敗では process API の状態参照や強制終了を行わず、temporary publish が成功した場合にだけ5値を復元する。Start 後の失敗では process cleanup と復元を独立して試み、終了不能 process の所有権を保持する。元の失敗、復元失敗、対象 config path は既存の失敗通知へ渡す。

terminal終了はplayer closeによる最後の位置取得後に一度だけ設定をSaveする。失敗はUI警告とログへ通知し、audio・一時fileなどのcleanupと終了要求を続ける。再入で保存・終了要求を重複させない。 terminal警告のnative dialog呼出しはApp最上位に閉じ、ApplicationCompositionの専用callbackを既存終了ownerへ渡す。

P07/P08c/P08d-SはSettingsDialogBehaviorTestsのcommand/sessionとisolated provider readback、P08dはPlayerSettingsGatewayTestsとExternalPlayerProcessGatewayTestsのcapture経路、P09はShellShutdownWorkflowOwnerTestsのpublic player attachment・final save・通知/終了callbackで検証する。P10のkey/placeholder契約は全6言語に適用する。物理fault matrixはP01/P02のfixtureに集約する。

LR2 試聴中の draft 境界と root 保存の実到達経路は `ExternalPlayerProcessGatewayTests` と `SettingsDialogBehaviorTests` の既存 fixtureで検証し、player生成後・PlayStart前の設定画面 main-window root Save、同じ draft を再生成しない起動中の root Save、起動直後・終了後の5値の意味的disk readbackを行う。設定画面の既存 Save / Cancel / root operation coverageは維持し、設定画面表示を理由に playback を停止しない契約を共有する。
