# 既知の課題・未採用の任意改善

## 扱い

本書は、着手しない候補と既知の課題を保持する。以下の修正案は採用済み仕様ではなく、着手時に必要性・通常入口の到達・利用者向け保証を確認する。外部プレイヤー固有の試聴・再生は任意改善とし、LR2表出力・成績連携やbeatorajaのBMT出力へこの分類を広げない。

共有URLのBMTと標準偏差0のT-SCOREは、利用者指定により次着手対象から除く。手動ランキング取得を廃止してもローカルキャッシュとランキング算出は残すため、T-SCOREの課題は消滅しない。

## 譜面プロパティの文字コード解釈

[ChartPropertiesViewModel](../../BeMusicSeeker/ViewModels/ChartOperations/ChartPropertiesViewModel.cs) は実ファイル診断へ保存済み指定を渡さず、[ChartResourceDiagnosticsService](../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceDiagnosticsService.cs) はBMSを自動判定で読み直す。BOMなしUTF-8の `#TITLE 漢` は、UTF-8へ修正した一覧では「漢」、プロパティ再読取りではCP932解釈になる場合がある。DB・一覧の修正値自体を失う問題ではない。

採用する場合の方針は次のとおり。

- 開く時点のパス・種類・配置と、所持では保存済み、保留では現在の項目内の文字コード・確定状態を捕捉する。確定した具体的指定は表示メタデータに使い、それ以外は既存の自動判定を維持する。
- 実ファイル読取りは既存の一回を使い、採用した復号方式と由来を診断結果として返す。表示のための再読取り・再解析、新しいDB列は追加しない。
- 表示メタデータと、CP932で扱うBMS/PMS資源名・定義番号の解釈を分ける。BMSONは既存のUTF-8を維持する。判定アルゴリズムや資源の確認先は変更しない。
- コピー可能な読取り専用欄に「UTF-8（保存済み指定）」「CP932（自動判定）」「CP932（判定未確定）」等を表示する。BOMが指定に優先した場合は実際の採用方式と由来を示す。
- `EncodingFixed` だけで手動指定と断定せず、未保存の操作履歴を推測・永続化しない。`EncodingName` の `?`・`unknown` をそのまま実使用方式と表示しない。
- 読取り完了時にタイトル等と一緒に確定し、閉鎖後の非反映を維持する。表示文言は既定リソースと6言語を揃える。

完了条件は、所持の保存済み指定と保留の現在項目内の指定、指定なし、未確定判定、BOM、BMSONで表示と実際の解釈が一致し、従来の資源診断を保つこと。保留指定のDB保存・再起動復元は追加しない。`ChartPropertiesViewModelTests`、`ChartResourceDiagnosticsTests`、`ChartPropertiesWindowTests` を実解析と代表Bindingに分担する。採用時に [プロパティ仕様](../spec/ui/chart-properties.md)を更新する。

## 行ドラッグの件数表示

[共通表のghost生成](../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs)は `1 row` / ` rows` を直接組み立て、日本語等で開始する通常の行ドラッグにも英語を表示する。転送・選択・保存結果の不整合ではない。

採用案は件数文字列だけを既存Resourcesへ接続し、既定資源と6言語の単数/複数・書式引数を揃えるものとする。drag形式、DropEffects、閾値、選択、Adorner寿命・themeは変更せず、ドラッグ中の言語追従用の購読やcacheを追加しない。TSVコピーと同じ共通表でも別原因・別単位として扱う。

判定案は `LocalizationResourceParityTests` と共通表の代表に分担し、1/2/多件、英語単複、言語変更後に開始したghostの実表示を確認する。辞書整合だけで表示の保証を代替しない。

## 資源のみ導入後の保守値の共用

[資源のみ導入の新規表示](../../BeMusicSeeker/Models/Library/BMSLibrary.PackageInstall.cs) は、入力パッケージと同じハッシュで導入先直下にある所持譜面を取得し、表示用の健康度を再評価する。追加譜面が0件のとき、保存済み保守値は旧値のままになり得る。[通常保守](../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Common.cs) は `forceUpdate: true` を使い、単に対象を増やすと再読取り・MD5/SHA256・再解析・文字コード再判定・警告除外解除・詳細生成まで波及する。

採用を検討する範囲は、資源のみの推定先導入と既所持資源上書きで、現在すでに表示用再評価を行う所持譜面集合に限る。共有資源を参照する全所持譜面への即時反映は含めない。

1. 成功対象の所持token・パスを集め、同じ配置の重複を除く。同じハッシュでも別配置は別の対象とする。
2. 一操作の物理処理と資源索引更新を終えてから最終配置で一度評価する。同じ導入先へ後続パッケージが資源を足す場合、先行時点の値を保存しない。
3. 同じ操作の資源索引と既存の資源保守評価を使い、ディレクトリ再走査、表示用と保存用の二重読取り・解析を避ける。
4. 資源存在数と対応状態だけを既存のmaintenance DB・共通現在値へ反映し、同じ評価結果で新規表示を作る。この範囲の表示専用再計算は統合して削除する。
5. 保存済み文字コード、警告除外、タイトル、内容識別、所持token、譜面数を保つ。通常保守・詳細生成へ既存対象を丸ごと追加しない。
6. 実際の変更だけを一セッションで保存・索引反映・通知する。件数による黙った省略、新しい永続キャッシュ、全ライブラリ依存索引は追加しない。

性能の採用条件は、同じ入力・設定で現行版と変更版を比較し、受付から必要な後処理・一覧反映までの完了時間に繰り返し退行が残らないこと。読取り・解析数、DB更新行数、走査・索引反映回数も観測し、コード上の再利用だけで性能不変とは判定しない。

| 比較条件 | 判定すること |
| --- | --- |
| 資源のみ1パッケージ、同じ先へ複数パッケージ | 評価の二重化とパッケージごとの同一譜面再評価を除き、最終配置に一致する。 |
| 複数導入先・BMS/BMSON混在 | 今回の対象数に応じた処理で、文字コード・詳細生成へ波及しない。 |
| 健康度が変わらない上書き | 不要な同値保存・通知を増やさない。 |
| 対象固定で背景ライブラリだけ増加 | 索引初回構築と再利用を分け、再利用時の全所持走査・複製を増やさない。 |
| 通常の新規・差分導入 | 改善対象外への余計な仕事を増やさない。 |

固定秒数・許容退行率を根拠なく設定しない。性能悪化が残る場合や大きな設計拡張が必要な場合は見送る。検証は `BmsLibraryPackageInstallServiceTests` の資源のみ・連続導入と `InstalledOnlyResourceOverwriteValidationTests` を起点に、実配置・DB読戻し・現在値・各一覧の一致と、変更しない値の保持を確認する。単体新規導入の必須修正へ混ぜない。

## 終了取消テストの予定外通知の調査

[`PlaylistRootReload_UsesOneTypedTerminalAndHandlesBeforeAsyncCompletion`](../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `shutdown-cancellation` で、終了に伴う取消の通知0件という期待に対して1件を観測した。通知の本文・生産元が分からず、原因と本番影響は未特定である。対象単体・同クラス群と診断追加後の通常検証では再現していない。URLセル編集代表とは別のテストプロセスで実行されるため、その共有Application・Dispatcher・Taskから直接混入する経路はない。

再現時には同ケースの通知本文・見出し・生産stack・発生段階・shutdown/CloseCompletion診断を使い、取消通知と別の終了通知を区別して生産経路を特定する。期待値、終了取消の無通知契約、受付・終了処理を根拠なく変えない。これは再現条件を調べる項目であり、解決済みとは扱わない。

## 外部プレイヤー固有の試聴・再生

先頭戻し・再開/再起動と試聴専用の数値境界を必須修正から分離し、停止完了後Restartの候補も本節にまとめる。通常入口の静的到達、将来採用時の方針と検証案を保持するもので、製品実装・自動テスト成功・実機再現を意味しない。共通の入力元名衝突と内蔵にも届く反復フォーカスの契約は[音声](../spec/runtime/audio.md)、[再生パネル](../spec/ui/playback-panel.md)、[一覧](../spec/ui/table-view.md)を参照する。共通処理の修正に本節の完了を要求しない。

### 一時停止中の先頭戻し

[LR2body.RestartPlayingBMSfile](../../BeMusicSeeker/Models/LR2/LR2body.cs)はprocessを開始する一方、[PlaybackPanelViewModel](../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)の表示はPAUSEのままで、次の操作と実再生が逆転する。BMIIDXはPauseをENTERキーで送るため、同じ利用者動作への整合を別に確認する。内蔵のPause/Restartは既存engineの停止状態と再生位置を操作し、このpath再起動とは区別する。

将来採用する場合は、利用者が回答した次の方針を使う。Pause中の先頭戻しはPauseを維持し、次の明示再生で現在曲の先頭から開始する。LR2のpaused Restartではprocessを開始しない。再生中Restart、現在曲・実player・既存callback/終了順と次回方式予約の非消費を保ち、別の選択行から再解決しない。

| BMIIDXの先頭再開で失敗する段階 | 将来採用時の結果と次操作 |
| --- | --- |
| INI準備失敗 | 旧processの一時停止、現在曲、PAUSEと先頭再開位置を保ち、旧processの再開・終了や新process開始をせず、元原因を既存経路で一回通知する。原因解消だけでは自動再試行せず、次の明示Resumeで同曲先頭の起動を試みる。 |
| 旧process終了後の起動失敗 | 既存startup契約で今回の開始途中processを回収し、NONE・現在曲解除・停止へ収束する。元原因を一回通知し、自動再開・次曲送りを行わない。次の明示Playは通常の停止状態から現在の選択対象を使う。 |

実装候補は、現在曲のPauseと先頭再開位置を既存adapterへ統合し、限定した先頭Resumeの成功後にpanel表示を採用する。準備前にその位置を捨てず、旧processを失ったかは同一呼出しの実採用点で扱う。一般Pause、共有Stop・世代・Task寿命の保証へ広げず、大きな状態機械・全曲履歴・修復要求・独自予約・自動巻戻しを追加しない。BMIIDXの早送り/巻戻しEndで使う既存ENTER送信と、明示Pauseの状態更新は区別し、送信列を保つ。

判定案は実LR2/BMIIDX adapter＋process/window代替とpanelを接続した `Start→Pause→Restart→Resume→Pause` とする。表示・現在曲と実Start/終了を対で確認し、playing Restart、別選択と次回方式非消費も代表フローへ分担する。BMIIDXは実INI公開失敗→PAUSE保持→次の明示Resume成功、および旧process終了→起動失敗→停止→別行の通常Play成功で、元原因の一通知と自動次曲なしを確認する。`PlaybackPanelViewModelTests` を上位接続、`ExternalPlayerProcessGatewayTests` をINI保全・startup cleanupへ分担し、fake playerの命令列だけで代用しない。FF/BW End後にplaying Restart→Pauseを行う代表で、Pause情報の誤反転も識別する。

### 一時試聴の再開・再起動

初回一時配置の回収後、LR2のResume/RestartとBMIIDXのRestartが保持した回収済みpathを直接再投入する。[LR2body](../../BeMusicSeeker/Models/LR2/LR2body.cs)と[BMIIDXView2015](../../BeMusicSeeker/Models/Playback/BMIIDXView2015.cs)の再読込み経路の問題であり、内蔵Pauseは既存engineのPause、Restartは既存CurrentTimeの先頭移動でpathを読み直さない。uBMplayのHOME/KEY0に同じ原因があることは未確認であり、一括断定しない。

将来採用する場合は、上記の一時停止と共通の入力元名衝突の確定版へ合流し、初回と再読込みを同じ一時配置所有処理へ統合する。現在曲・実playerの必要文脈だけを保持し、選択や次回factoryから再解決しない。副作用前に必要Lを非待機取得し、Busyでは状態/processを保つ。開始・cleanup Taskを既存寿命へ接続し、曲全体で一時物・Lを保持しない。

判定案は実保留→導入先編集→実adapter＋process代替とし、各起動時のファイル存在/内容と再回収を確認する。別選択、次回方式予約、Busy、停止/終了も同じ `PlaybackPanelViewModelTests` 系列へ分担し、命令列だけのfake playerで代用しない。

### 試聴専用設定の数値文字列

数値・日時の[共通契約](../spec/playlist/storage-and-export.md)に対し、外部3playerの試聴専用INI/XMLの数値入口・往復だけを任意で扱う。改善・調査候補であり、未実装・実症状未検証の箇所を一律に不具合と断定しない。

| 専用境界 | 将来採用時の確認起点 |
| --- | --- |
| uBMplayの一時INI Volume | `uBMplay`の試聴用音量書出しと`UbmplaySettingsTests`。既存の値域、無関係値・bytes保全、補完/復元の契約を維持して実INIを観測する。 |
| BMIIDXView2015の一時INI BMSVOLUME | `BMIIDXView2015`の試聴用音量書出しと`ExternalPlayerProcessGatewayTests`の実INI。準備失敗時の旧process/bytes保全も既存保証へ分担する。 |
| LR2 preview XMLの専用数値 | `LR2Config`のwindow size/screenmode/master volume/volumeflagのpreview数値入口とwindow/master往復。`LR2ConfigTests`と`Lr2PreviewOwnerTests`等の既存境界を起点に、試聴五項目と通常値・復元を区別する。 |

LR2Configの共通整数読取りはcustomfolder/titleflash/autoreloadから表・環境設定へ届くため必須側に残す。BMSAutoPlayWriterの通常音声変換BPM、EncoderQualityの精度と録音設定、汎用Web/プログラム名の日時風文字列、BMT/BeatorajaConfig/IRも必須分類を維持する。共有成果を戻さず、専用部分の採否と既存受入の追加範囲だけを別に判断する。

### 停止完了後のLR2先頭戻し

通常の `Start→Pause→Stop(closeProcess:true)` 完了後、[前曲ボタンの単クリック](../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs)は遅延して `RestartCommand` を呼ぶ。保持された旧pathをLR2が再起動する一方、panel表示はNONEのままになる経路を静的に確認した。実機再現は未実施であり、利用者は今回は変更せず別課題として残すと回答した。

将来採用時に停止完了後の先頭戻しを無作用にするか判断する。現在曲のない停止完了と、開始済み読込み・Busy・終了受付を区別し、全操作の新しい禁止や修復状態へ広げない。実LR2 adapterとprocess代替による上記操作順で実開始と停止表示を対にして判定する。

## 早送り・巻戻しの終端

[Playback View](../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs)でDown→押したままLeave→戻ってUpを行うと、LeaveとUpがそれぞれEndを送る。`PlaybackPanelViewModel` も方向bit解除済みでadapter Endを呼び、`BMIIDXView2015` のEndはEnterによるPause toggleなので、実playerが再停止しPLAY表示と食い違う。位置sliderの再開意図とは別原因である。

将来の採用案は短命なgestureまたは既存方向状態へ開始/終了の所有を揃え、一押下の終端を一回にする。停止・曲/player交換後の遅いUpが新playerへ作用しないことも同じ境界で扱う。Leaveで終端する既存操作は無断変更せず、BMIIDX Enter無効化・表示だけの補正・全音声queueや巨大共通状態機械は追加しない。位置sliderと同じView/VMを編集する場合は直列とし、原因と判定を分ける。

判定案は `PlaybackPanelViewModelTests` と `ExternalPlayerProcessGatewayTests` へ分担し、両方向のDown→Up、Down→Leave→Enter→Up、出入り反復でStart/End各一回を確認する。実BMIIDX adapterと代替window hostでEnter送信一組を観測し、fake命令列だけで代替しない。内蔵flags/uBMplayキー解放と停止/曲交換後の旧Up無作用を維持する。

## LR2本人IRキャッシュの存廃とプレイヤー復帰

利用者合意により本人IR処理は廃止候補として扱い、A→B→Aの混入は今回の必須修正へ昇格させない。存廃の決定前に修正も撤去も実装しない。

### プレイヤーを戻したときのキャッシュ

正常取得応答が得られる前提では、[IR更新](../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)がプレイヤー別metadataのdigest一致を、現在の共通ir_score集合の一致と読み替える。Bの成績へ置換してもAのmetadataは残るため、Aへ戻るとBを返す経路がある。実サービスへの接続成功は確認していない。

存続して修正する場合は、現成績の内容・所有と取得結果を一致させる。新しいプレイヤー別DBや履歴の無条件削除を先行せず、正常空応答、同内容時の無書込み、成績とmetadataの同一トランザクション、失敗時の旧状態保持を維持する。廃止する場合は本人IRの設定・取得・表示・保存資料の責務をまとめて整理し、別の順位キャッシュやLR2ローカルスコアの扱いを便乗変更しない。

### 高得点の本人IRによるクリア降格

ローカルHARD・1000点、本人IREASY・1800点では、[IR更新](../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)の得点優先の採用から [BMSScore.Overwrite](../../BeMusicSeeker/Models/Chart/BMSScore.cs) がクリアと達成時optionも上書きし、アプリ内でHARDがEASYへ下がり得る。統合後の値で未送信を判定するため表示も消え得る。影響は採用成績・一覧・検索・ランプ・未送信表示で、ローカルのスコアDBを書き換える問題ではない。

本人IRを存続する場合は、クリアとその達成optionを得点・判定数等の採用から分ける案を検討する。プレイヤー復帰とは別原因だが、存廃前に別の保存機構や修正を先行しない。ローカル/IRのクリア優劣と得点優劣、同点、IRだけの成績についてランプ・option・派生得点・未送信を確認する。手動ランキング取得の廃止を本人IRの存廃・本修正の採用と読み替えず、順位cacheへ便乗変更しない。

## ランキング情報だけがある未プレイ曲の個人成績

本人のローカル成績がなく、他者だけを含む正常な順位XMLがある所持譜面では、起動時の順位キャッシュ反映によって個人成績が空欄からDJ LEVEL F・SCORE 0・RATE 0・BP 0・COMBO 0へ変わる。CLEARのNO PLAYとランキング統計は保持されるが、個人成績の `undefined` 検索に一致しなくなる。新規通信は不要で、LR2のローカル成績DBを書き換える問題ではない。

[順位解析](../../BeMusicSeeker/Models/Ir/Lr2IrRankingCacheParser.cs)は本人不在でも他者由来のノーツ数と順位-1を返し、[順位の適用](../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)がローカル成績のない場合に [BMSScore](../../BeMusicSeeker/Models/Chart/BMSScore.cs)を作る。[共通投影](../../BeMusicSeeker/Models/Chart/ChartScoreSnapshot.cs)まで個人成績の既定値0が伝わる。

将来採用時は、本人の実績がなくランキング情報だけを持つ場合に、個人のDJをINVALID、SCORE・RATE・BP・COMBOをnullとして投影する案を検討する。順位人数・更新日時等は保持し、キャッシュ削除や `NoScore` 全体への置換はしない。得点0・BP 0だけを欠損判定の根拠にせず、本人不在と実際の0点成績を区別する。

`BmsLibraryIrServiceTests` と `BmsLibraryIrStartupTests` を起点に、本人不在＋ローカル成績なしの空欄と統計保持、ローカル成績ありの保全、本人あり・FAILED 0点・有効なBP/COMBO 0を対照にする。初回XML解析と保存済み行の再適用から、共通snapshot・通常/プレイリスト一覧・検索までを確認する。

## 本人の0点成績に対する同点順位の採用

本人とローカルの成績がともに0点、他者が100点の正常な順位XMLを起動時に反映すると、オフライン順位推定が既定のOFFでは、本人の2人中2位を解析してもRANKINGが空欄に残る。RANKINGは更新されず、人数と更新日時は反映される。

[順位解析](../../BeMusicSeeker/Models/Ir/Lr2IrRankingCacheParser.cs)は0点も有効な本人行として順位を返すが、[適用処理](../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)の同点順位採用が `data.score > 0` の内側にある。[順位仕様](../spec/integration/lr2-ranking.md)の同点採用を0点にも揃える案を、将来採用時に検討する。

本人の有効な正の順位がありローカルと同点なら、得点が0でも順位を採用する。本人不在の順位-1は別扱いとし、ローカル高得点時の推定ON/OFFや、他の成績項目の採用条件は維持する。`BmsLibraryIrServiceTests` の実解析から共通snapshotの `RankingString` が `2/2` になることを確認し、保存済み行の再適用、正数同点、本人不在、ローカル高得点・推定OFFを対照にする。不要なXML再読込みは増やさない。

## 正常に空のLR2スコアとプレイヤーID

LR2のscore表が正常に空でplayer行に非ゼロのiridがある場合、[DB読取り](../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs)はIDを返すが、[スコア採用](../../BeMusicSeeker/Models/Library/BMSLibrary.cs)が成績0件を理由にLR2IDを0へ変える。起動時の順位更新がID 0で終了し、既存の正常な順位XMLも反映されない。本人IR通信を有効にした場合はその取得も行われない。

将来採用時は、選択したLR2スコアDBを正常に読めた場合に、行数によらず取得したIDを採用する案を検討する。正常空・未設定・読取り失敗を既存結果型で区別し、実値0の補完、旧取得元のID再利用、ダミー成績の生成はしない。

空score表＋player.irid 123の実DBからLoaded・空成績・ID 123を公開し、本人IR通信OFF・順位キャッシュONの実起動を通して同IDのXMLと通常snapshotへ接続することを確認する。`BmsLibraryIrServiceTests.LoadScoreTable_EmptySelectedDatabaseIsLoaded` と `BmsLibraryIrStartupTests` を起点に、保存済み順位行の利用と、実値ID 0・未設定・読取り失敗も確認する。

## オフライン順位推定のXML先読み範囲

オフライン順位推定を有効にすると、[更新準備](../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)は保存済み順位行の全ハッシュについて不足するXMLを先読みする。XMLが未更新でローカル成績がIR以下の場合や、ローカル成績がない場合にも読込みが起こる。推定は既定で無効であり、利用者のキャッシュ規模での処理時間・一時保持量は未計測である。

将来の改善に必要な条件は次のとおり。

- 同じ入力でXML読込み件数・処理時間・一時保持量を測り、通常更新と推定が必要な更新を区別する。
- 捕捉済みのスコアから必要なハッシュを絞り、今回取得済みのlookupを再利用する。永続キャッシュを追加しない。
- 準備後にスコアが変わっても、現在の要求・スコア文脈に対する結果を欠落させない。
- 適用側の共有ロック内へXML I/Oを戻さず、取消・停止・鮮度・保存の既存責務を保つ。
- スコアなし・同点/低得点・高得点と、更新済み/更新不要XMLを使い、必要な読込みだけで順位結果を維持する。

## BMT混在フォルダ名と通常外部表の出力名

BMT cache復元では、全folderが同じtagで始まらない場合に空prefixを保存する。`BeatorajaBmtTableImportService` から通常表へ登録した後、`BmtTableExportService.ResolveFolderName` が外部表のtagを付け直すため、tag=st、元folder=st0/sl1ならstst0/stsl1となり得る。数字付き所属でも成立し、必須の非数値所属保持とは別の原因である。

ただし、実際に取り込む `.bmt` に全フォルダが同一tagで始まらない例が存在するかは未調査であり、利用者回答により後回しにする。まず実際の入力で必要性を確認する。[Table URL取込み](../spec/playlist/table-url-import.md)の元名保持と[BMT出力](../spec/playlist/bmt-export.md)の「tag＋互換level」の整合を採用前に決める。

全BMTの出力folderを保存済みfolderへ統一する案は未採用である。通常外部JSONにも次の互換変更があるため、混在名修正だけのつもりで実装しない。

| 通常JSON等の入力 | 保存folder | 現行BMT名 | 保存名へ統一した場合 |
| --- | --- | --- | --- |
| tag=st、prefix省略、level=1 | st1 | st1 | st1 |
| tag=st、compat_prefix=`LEVEL `、level=1 | `LEVEL 1` | st1 | `LEVEL 1` |
| tag=st、compat_prefix空、level=1 | 1 | st1 | 1 |
| tag=stの表で保存prefixをXへ変更 | X1 | st1 | X1 |
| CP932で表せないtag=😀、既定互換prefix | `LEVEL 1` | 😀1 | `LEVEL 1` |

空prefixは通常JSONでも明示可能なため、空prefix・URL形状からcache由来を推測して特別扱いしない。全表の保存名統一を採るか、通常外部表の従来名を維持する明示的な区別を設計するかは将来の判断である。後者を採る場合も、出自・保存方法・元URL再取得後の扱い・既存表移行を根拠付きで決め、未確認の出自をDBへ推測保存しない。ゲーム側の名称依存の選択・保存状態への影響も未確認である。

採用時はURL SHA-256の管理ファイル名、playlist_id、hash、DB所属、表URL、管理URL登録順、管理外ファイルを維持する。変換の変更を既存exporterVersionによる再生成へ接続し、新しい移行台帳・旧版出力退避は追加しない。実gzip復元→DB→再出力と上表、空tag・ローカル表・prefix編集、旧版台帳からの再生成と次回省略を採用契約から検証する。必須のBMT準備・採用・整理やコース制約往復の実装待ちにはしない。

## Updaterの管理fileからdirectoryへの遷移

旧manifestの管理ファイル `a` を新ZIPの `a/b/c.dll` へ置き換える通常の構成変更は受理されるが、`Updater/Program.cs` の旧祖先除去と旧file集合差分の削除が、新しく必要になったdirectoryと子を削除する経路がある。不具合自体は妥当である。

利用者回答により今回は任意とする。ローカルmain=`d1562754` と調査時HEAD=`8c53fc9b` のpublishスクリプト・package layout・pubxml・Updater projectに差はなく、追跡fileと新版の祖先directoryの交差も0件だった。生成ZIP全体は未確認であり、今回のmain→dev配布で発火するとは確認できていない。将来この種類変更を含む配布を行う前には、生成ZIPとmanifestで到達を確認して解消する。

採用する場合は、祖先除去を今も配置を妨げる旧管理fileだけへ限定し、旧対象整理でも新版に必要なdirectoryを消さないよう、二つの削除判定を同じ新file・必要directoryの意味へ揃える。種類変更の拒否、事後失敗/rollbackへの置換、永続台帳追加は行わない。管理外・reparse・逆遷移・補償保証を維持する。[ポータブル更新](../spec/integration/portable-update.md)と実装を同じ単位で更新する。

`UpdaterPackageSyncTests` の同構成に対する再起動失敗後の復元は成功時の子の生存を保証しない。採用時は実FS/ZIPで単一子・兄弟・多階層の成功、実manifest、管理外兄弟、逆遷移、rollbackを判定する。必須のドライブルート正規化と同じProgramを触る場合は直列にするが、別の原因・コミットとして扱う。

## 共通再読込みでの複数候補と利用者情報の対応

通常推薦表には同hashが複数目標ランプに現れる。別scoreを選び生成hashが変わる明示リロードでは、`PlaylistAggregatePersistenceOwner` のMEMO引継ぎがhash候補からlevel差だけで対応を選び、目標folderを見ず候補を消費しないため、複数旧行が同じ新行を上書きし得る。同じ生成結果の再読込みだけで必ず消えるとは扱わない。

今回は任意を維持する。将来、通常外部表の難度移動・folder改名も含めた共通の引継ぎとして、同hash複数行の対応と衝突時の保全を決める。推薦専用の安定ID、MEMO禁止、候補消費方式、DB列、既存MEMOの自動修復を先に必須化しない。必須の推薦prefix投影と独立して採否を判断する。

採用時の検証は `PlaylistRecommendedTableOwnerTests` と共通再読込み系列を起点に、異なるMEMO・追加日時、候補消失/追加、多対一、mixed/standardの生成差をDBまで確認する。期待する引継ぎ規則は将来の合意から決め、現行の対応方法をそのまま正解にしない。

## 古い外部プレイヤー終了通知の所有者照合

BMIIDX/LR2でAの捕捉済み終了通知がB開始後に届くと、Bのprocess/handle/pathやLR2 preview scopeまで消す経路がある。Panelの世代照合とadapter内部の解放は別で、購読解除だけでは捕捉済みdelegateを止められない。Pause/Restartとは別原因の任意候補である。

採用時は通知対象processを捕捉し、現在の本人と一致する状態だけ解放する。null senderを使う明示Closeも実processを渡す同じ小さい終端へ揃え、LR2の終了後preview復元の明示再試行を保つ。必須再生群と同adapterの書込みを直列にする。ExternalPlayerProcessGatewayTestsの実adapter/CaptureExitedNotificationでA通知捕捉→A終了→B開始→A通知配送→B Stopを制御し、BへのClose、LR2 scope保持、自然終了/明示停止/復元再試行を確認する。時間待ちだけに依存しない。

## 更新失敗通知の翻訳

更新の確認/取得/開始失敗でMainWindowの本文・見出しが固定英語になる。採用時は既存通知を資源へ接続し、処理失敗を表す文言へ揃える。元例外は開発診断として保持し、新しい失敗分類を設けない。StartupUpdateWorkflowOwnerTestsの通知/アプリ継続、実表示と資源整合を代表確認する。MainWindowの必須focus/ツリー編集と一writerへ合流し、ready/lockの必須修正とは原因・判定を分ける。

## 共有URLのBMTを物理ファイル単位で確定する

今回の次着手対象には含めない。以下は将来採用する際の検討案である。

**任意改善。プロパティから意図的に同じURLを複数表へ保存する利用は稀なため、今回のリリース完了条件へ含めない。出力元方針は将来採用時に決定する。**

### 成立する通常操作と影響

1. BMT出力を有効にする。
2. 外部同期OFFのローカル表AとBを作り、異なる名前・曲集合を持たせる。Aは1曲、Bは複数曲など、生成gzipの長さも異なる構成にする。
3. 各表のプロパティで、同じ絶対Page URLを保存する。
4. 入力を変えずにアプリの通常終了・起動を繰り返す。

ローカル表もPage URLが優先され、同じURLのSHA-256から決まる一つのBMTファイルへ出力される。ID別の未変更判定により、内容が起動ごとに交替する。

| 時点 | 共有ファイルの内容 | 次回の判定 |
| --- | --- | --- |
| A、Bの順に出力済み | B | Aの記録だけが実ファイルと不一致 |
| 次の通常起動 | A | Bの記録だけが実ファイルと不一致 |
| さらに次の通常起動 | B | Aの記録だけが実ファイルと不一致 |

交替するのは表名だけでなく、folderと曲集合も含む。同じ原因で、最後に書いたBを削除してAの共有参照だけを残した時、その時点ではBの内容が残る枝もある。

**成立入口は通常起動のforce=false全出力。** 表だけのUIリロードはBMT出力を要求せず、force=trueの明示再生成もこの交替を示す手順には使わない。同じURLの再importは既存の重複チェックがあるため、成立入口へ含めない。

### 原因と確定前の判断

プロパティ保存はURLの形式等を検査するが、URL一意性は要求しない。出力はURL単位のファイルを共有する一方、未変更判定と長さ・mtimeは表IDごとに保存する。順次書込みの途中で得た各FileStateが台帳へ残り、次回は前の書込みが後の書込みを「未反映」と判断する。

同じ入力で出力が交替することは不具合である。一方、**同じURLに異なる内容の複数表がある場合の出力元**は、将来採用時に決める必要がある。

主要根拠：

- [Page URLの編集条件](../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.xaml)
- [保存時検査](../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)
- [Page URLを優先するURL解決](../../BeMusicSeeker/Models/Playlist/BmtTableExportService.cs)
- [ID単位の投影省略](../../BeMusicSeeker/Models/Playlist/BmtTableExportService.cs)
- [同名ファイルへの順次書込み](../../BeMusicSeeker/Models/Playlist/BmtTableExportService.cs)
- [実ファイルと各IDの長さ・mtimeを比較](../../BeMusicSeeker/Models/Playlist/BmtTableExportService.cs)
- [通常起動で引き継ぐforce=false](../../BeMusicSeeker/ViewModels/Startup/StartupLibraryInitializationWorkflowOwner.cs)

### 推奨する修正

出力元方針の決定後、投影対象選択、出力元選択、未変更判定を**共有物理ファイル単位**に揃える。既存台帳の表ID・URL・ファイルの関係を用い、影響するURLグループの採用元を解決して一度だけ出力する。

推奨する出力元は、現行のforce=true全量出力との互換性を保つ「現在のBMT順で最後の非空表」。これは採用案であり、採用前に期待値として確定しない。URL重複を新たに拒否する案は、現在保存できる入力の互換性を変える。

全IDのmtimeを最後の値へ揃えるだけでは、共有ファイルがどの表の内容であるべきかを解決できず、部分更新・削除で再発する。全表の毎回再生成や、新しい所有履歴・永続台帳ではなく、既存の必要出力と台帳更新へ統合する。旧台帳から正しい共有出力へ移る必要生成も、既存の版・未変更判定の仕組みで扱う。

### 受入条件

- 同URL・別ID・異なる曲集合で、force=false全出力を連続しても内容が安定し、以後の無変更時は不要なgzip書込みを省略する。
- 共有URLの片側だけを編集する部分出力も、採用した出力元規則に一致する。
- 出力元・非出力元それぞれの削除／出力対象外化、空表を含む共有、最後の所有者の削除を区別し、内容・ファイル・URL所有を一致させる。
- BMT順が出力元を決めるなら、順序変更時にも必要な共有ファイルを更新する。URL登録順だけ変えて終えない。
- 実gzip、台帳、beatoraja configを照合する。異URLや完全同一内容の共有、管理外ファイル・URLの保全を維持する。
- BmtTableExportServiceTestsとPlaylistBmtOutputOwnerの実接続へ分担する。既存の共有ファイル削除テストは同じJSONを2 IDへ与えており、異なる内容の競合条件を追加する。

既知のBMT混在prefixフォルダ名、親MD5遅延解決、コース制約往復とは別原因である。

## 標準偏差0のランキングのT-SCOREを算出不可として扱う

今回の次着手対象には含めない。以下は将来採用する際の検討案である。

**任意改善。標準偏差0で定義できないT-SCOREを、既存の算出不可表示へ揃える。**

参加者が1人、または全員が同じ非ゼロ得点の正常なLR2ランキングキャッシュでは、平均はその得点、標準偏差は0になる。例えば本人1800・平均1800なら0/0でNaN、ローカル1900・平均1800なら正の無限大になり、T-SCOREへ流れる。

既存のローカルXMLから、起動時のランキングキャッシュ更新を通して到達する。新しい通信や壊れたキャッシュは不要である。rank/rateの更新漏れやキャッシュの所有・寿命とは別で、今回の偏差値計算そのものが原因である。

根拠：[正常なランキングからsigma=0を生成](../../BeMusicSeeker/Models/Ir/Lr2IrRankingCacheParser.cs)、[起動後のキャッシュ反映](../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[標準偏差0を除外しない偏差値計算](../../BeMusicSeeker/Models/Chart/BMSScore.cs)、[非有限値のままF2整形する表示](../../BeMusicSeeker/Views/CustomTable/CustomTableColumn.cs)。

採用時はSetStdDevValで、標準偏差0のため定義できない値を既存のnullable値へ揃え、空欄等の既存の算出不可表示に接続する。50点や0点という別の意味の数値へ置き換えない。

実parserから参加者1人・複数人同点・通常の分散ありを渡し、平均と同点／ローカル高得点で非有限値がsnapshotと一覧へ流れないこと、分散ありの従来値が変わらないことを確認する。

## 履歴更新列の空欄と並べ替え

[PlayHistoryRow](../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryRow.cs) のCLEAR・BP・COMBOはベスト更新なしを空欄にする一方、[PlayHistorySortEngine](../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistorySortEngine.cs) は非表示の新ベスト値で整列する。BP表示が `10 → 空欄 → 20` のように混在し得る。

将来採用時は更新flagがfalseなら整列キーをnullとし、空欄位置は既存nullable列の規則へ揃える案を検討する。「常に末尾」と新規に決めず、履歴保存値・検索の意味は変えない。3列の更新あり/なし/欠損、昇降順・同値を確認する。今回の履歴投影・日付境界修正には含めない。

## T/Nの計算可能性とdefined検索

[GridKeywordSearchQuery](../../BeMusicSeeker/ViewModels/Search/GridKeywordSearchQuery.cs) の `tn` / `t/n` のdefinedはTOTAL定義だけを見ているが、[ChartInfoDisplayFormatter](../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoDisplayFormatter.cs) の比率はノーツ数0以下でnullになる。TOTAL明示済みのゼロノート譜面は空欄でも `tn:defined` に一致する。

将来採用時はTOTALのdefinedを維持し、T/NだけTOTAL明示ありかつ計算可能へ揃える案を検討する。TOTAL未定義の補完値を新たにdefinedとしない。ノーツ0/正数、TOTAL明示あり/なし、解析情報なしと `tn:defined` / `tn:undefined` / `t/n:` / `total:defined` を比較する。保存式比較や検索候補の修正には含めない。

## 初期化失敗後の更新開始

外付けドライブ不在等で初期化が失敗すると、失敗表示の `StartupProgress.IsActive` が維持され、後処理完了後も `CanStartUpdate` がfalseとなる。実操作の終端と失敗表示の寿命の混同が原因である。

将来採用時は実操作・終了準備の既存状態へ開始可否を接続する。現在の `IsOperationActive` も同じ `IsActive` を含むため、参照を置き換えるだけでは解消しない。通常初期化中・ファイル変更中・失敗後処理中は抑止し、後処理完了後に開始可能なことを確認する。失敗表示を強制的に隠す回避や新しい永続状態は追加しない。Updaterのファイル整理修正とは別の未採用課題である。

## 更新ZIP取得中の通常終了の応答性

更新ZIPを正常に取得している間にアプリを閉じると、要求の世代は失効するがHTTP取得は続き、通常終了はその実完了を待つ。HTTPの期限は300秒である。[終了契約](../spec/runtime/shutdown.md)は取消完了または実完了待ちを許容しており、今回は現在の完了待ちを維持する。

再検討する場合は、正常な低速取得中に終了を早める必要性を確認する。採用時は、今回の取得が所有する取消をHTTPとコピーへ渡し、ストリーム・一時ファイルを含む実Taskの終端まで待つ。更新プログラムが適用を始めた後の強制停止へ広げない。

根拠は [StartupUpdateWorkflowOwner](../../BeMusicSeeker/ViewModels/Startup/StartupUpdateWorkflowOwner.cs) の `NotifyClosing` と `ExecuteAsync`、[UpdateDownloadService](../../BeMusicSeeker/Models/Update/UpdateDownloadService.cs) の `DownloadAndVerifyAsync`、[ShellShutdownWorkflowOwner](../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs) の `CompleteWindowCloseAsync`。採用時には、長い正常本文の取得中に閉じたときの取消・解放・通常終了と、通常取得成功を対にして確認する。

## アプリ全体のキーボード操作の要求整理

画面は、対象範囲を選ぶツリー、検索と候補、情報・操作・編集項目が混在する一覧、再生パネル、メニュー・ダイアログから構成される。表計算ソフトのようなセル間の連続編集を前提にしない。今回はMEMO等をTabで確定できる現在の操作を維持し、Shift+Tabだけを逆方向へ変える修正は行わない。

編集中のTabは入力確定後にWPFのフォーカス移動を要求し、次の現在セル・選択行を選ぶ処理ではない。検索欄の候補ポップアップ表示中はEnter/Tabが候補適用、Shift+Tabが保存検索の操作ボタンへの入口になる。メイン一覧のEnterは再生、表の概要一覧のEnterはツリー項目への移動につながるため、個々のキーの向きだけではアプリ全体の操作を決められない。

将来改善する場合は、キーボードで支援する利用手順の必要性を先に決める。候補は「対象範囲を選ぶ→検索する→結果を選ぶ→行操作をする→閲覧を続ける」「一行のMEMOを入力・確定して閲覧へ戻る」「メニューやダイアログを閉じて同じ作業を続ける」などとし、全操作のキーボード対応を既定の目標にはしない。

採用する利用手順について、次を要求として確定してからキー操作を設計する。

- キーボードだけで完結させる範囲と、マウスを併用する範囲。
- ツリー・検索・一覧・再生・ダイアログ間の入口、終了時の出口、取消時の復帰先。
- 選択行・選択集合、現在セル、編集用入力欄、キーボードフォーカスの役割と、操作が参照する対象。
- 通常表示、編集中、候補表示中、IME変換中、保存中における確定・取消の役割。
- 入力先と操作対象を利用者が認識する方法。

その後に既存のキー操作を利用手順へ対応付け、維持・統合・追加・削除の必要性を判断する。局所的なショートカット追加を先行させず、既存操作も一律には削除しない。受入条件は採用した利用手順の入口から目的達成・復帰までを対象とし、キー分岐や単独のフォーカス移動だけで完了としない。

根拠は [CustomTableView](../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs) の `HandleKeyDown`、[KeywordSearchEditor](../../BeMusicSeeker/Views/Search/KeywordSearchEditor.xaml.cs) の `HandleInputKey`、[MainWindow](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の行操作接続と再生後のフォーカス復帰、および[一覧](../spec/ui/table-view.md)、[検索](../spec/ui/keyword-search.md)、[再生パネル](../spec/ui/playback-panel.md)。

## 採用・完了時

採用する場合は未確定の利用者挙動を質問し、実装計画へ必要な入口・契約・検証条件を移す。完了した項目は契約・採用理由・検証対応を仕様へ移して計画から除き、完了記録だけを残さない。[計画の運用](README.md#運用)に従う。
