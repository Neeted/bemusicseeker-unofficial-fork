# リリース前の必須修正

## 目的と採用範囲

通常の入力・操作で到達する保存、導入、一覧、再生、出力の不整合を解消する。本書は採用済みの修正対象と、既存の採用契約・未完了の実装・検証を扱う。対象は22件、実装の単位は下表の21単位とする。URL補完の取得元比較と候補比較は一単位にまとめ、二つの不具合の判定は独立して残す。[任意改善](release-follow-up-candidates.md)は今回実装せず、リリース完了条件に含めない。

追加指摘は `dev` の `8c53fc9b` とLR2出力簡素化の作業差分を照合した静的調査を根拠とする。既存対象も含め、通常経路の確認を実再現・テスト成功と読み替えない。着手時に最新のコードと既存差分を確認し、解消済みの課題を再実装しない。LR2出力設定の保存・生成の契約は[設定の保存](../spec/runtime/settings-persistence.md)と[LR2カスタムフォルダ](../spec/playlist/lr2-custom-folders.md)を参照する。

今回追加した保留導入採番、BMT親MD5、数値・日時、ツリー編集、更新の課題は、`6e443a87` の必要経路を静的照合した。実再現・製品テストは未実施であり、原因が残る範囲を着手時に確認する。追加資料の識別子は既存計画の識別子と共通でないため、以下では機能名を主に扱う。フォルダ削除確認は[現行の三択契約](../spec/library/mutations.md#確認と共通受付)を維持し、未完了課題へ再登録しない。

以下の代表判定は独立テスト設計の代替ではなく、実装・テストが完了した保証でもない。

## 共通の変更境界

- LR2/uBMplay/BMIIDXView2015の試聴・再生固有の未完了事項は、既存の必須項目と今後の新規発見を含め[任意改善の分類方針](release-follow-up-candidates.md#目的と扱い)へ分離する。表出力・成績連携、内蔵・共通処理は既存分類を保ち、共有修正の効果が外部playerへ及ぶだけで対象外にしない。
- 現行の入力受理、取消、受付、成功対象の一変更セッション、管理外ファイル・URLの保全を維持する。入力拒否の追加や意味の変わる代替は、合意した範囲を超えて導入しない。
- 設定は自身の候補と他項目の最新確定値を使う。受理済み入力の固定、候補排他、設定実操作の同時一件、必要なL/P受付、終了時の開始済みTask回収を維持する。通常閉鎖は候補だけを捨て、再表示は公開済み確定値から作る。
- 過去の失敗要求・成功段階を保持して後続操作を制限する方式を、最新入力の保存と保存済み正本からの再生成へ置換する。予約、自動再試行、自動巻戻し、永続修復台帳、専用修復管理、差分ごとの常駐完了辞書は追加しない。
- 少数変更の全件走査・全行実体化・全ソートを常用しない。既存の所有主体と通知・Taskを再利用し、旧処理を並存させない。同じファイルを扱うだけの理由で別機能を一括コミットしない。
- 新しい利用者向け文言は `Resources.resx`、`Resources.cs` と6言語辞書を同じ単位で揃える。現行仕様と[操作の競合ポリシー](../spec/core/operation-concurrency-policy.md)の変更も該当単位に含める。

## ライブラリ操作で採用する契約

### 重複グループの選択と移動要求

手動選択と整理後の自動選択は、現在の検索結果内で一意な所属フォルダによる短命な識別へ揃える。二フォルダ統合・同一フォルダ整理は操作前に捕捉した次のグループ、三フォルダ以上の統合は存続グループを選ぶ。同名・空名でも取り違えず、対象消失は空結果とする。明示した重複ルートだけが全件を表示し、フォルダ単位選択との区別を保つ。見出しの最初の一致という旧経路、永続ID・選択永続化・追加の全件再検索は残さない。

初回重複解析中も許可される後発の画面移動を優先する。入力受付から表示採用まで同じ短命な要求識別を使い、古い画面切替と解析途中のsummary解除等の表示副作用を採用しない。必要なcache構築・catalog通知は完了させる。既存の一覧要求識別・取消を利用できる境界を確認し、画面移動をBusyで新規禁止する案は採らない。

## 着手順と実装単位

先行統合された設定テスト・仕様の成果を前提にする。数値・日時は[保存と出力](../spec/playlist/storage-and-export.md)、[一覧](../spec/ui/table-view.md)、[設定の保存](../spec/runtime/settings-persistence.md)の型・形式・入力条件を前提にする。次の実装割当は残る必須項目から選び、内蔵・共通処理の既存作業は継続できる。任意の外部player固有修正とUI共通整理の採用・完了を必須修正の前提にしない。

表の番号は実装単位の識別であり、全件の直列順ではない。上記優先と次の依存境界で着手を決める。devへの統合は、依存関係・検証・独立レビューが揃った単位から一件ずつ進め、着手順やLR2関連作業を理由に独立した完成単位を保留しない。新しい実装・テスト・仕様・計画は各作業WTの別ブランチで完結させ、並列開発中のメイン作業ツリーはdevの統合専用として原則クリーンに保つ。独立した単位の調査・設計・編集は並行できる。全体ルートは利用者への質問、全体優先、単位間の依存と共有契約、WT割当、Functionalと性能測定の開始調整、dev統合と全体報告を担う。各単位責任者は範囲内の調査・テスト必要性と独立設計承認・引継ぎ点検・実装・Quick・仕様計画・レビュー・差戻し・branch内commitを完結させ、工程別の全体ルート再承認を求めない。小単位では責任者が実装を兼ねられる。未確定の利用者挙動・保証、範囲拡張、他単位や共有契約への影響は全体ルートへ戻す。各単位は必要な翻訳も含み、表中のクラス名は探索起点でありAPIや配置を固定しない。

並列化は次の依存境界で判断する。ファイルが別というだけで独立とせず、共有API、DBスキーマ、テスト補助、生成物、仕様の変更範囲を着手時に確認する。単に同じDB型を読むだけの理由で全件を直列化せず、共有境界を変更する必要が出た場合に再判断する。

| 作業群 | 直列に保つ順序・合流 | 並行できる範囲 |
| --- | --- | --- |
| 導入先推定 | 保護ファイル採番では、共有する物理計画helperの書込みを直列にし、実導入の相対配置契約を維持する。 | 登録readerの先行単位は取り込む。後から統合する側は新版で実導入・保留・cleanup・索引反映を確認する。 |
| 重複一覧・選択 | 重複の所属識別→初回解析後の古い画面切替は同じ通知・選択契約で順次扱う。範囲選択主行は現行のマウス入力・現在セル契約を保って扱う。 | 重複保守の共有境界を取り込んだ後、独立した表取得等と並行できる。 |
| URL・表の値と出力 | 取得の大小文字→補完元/候補の大小文字→転送アーカイブ保存名は同じ取得経路へ順次適用する。転送後相対参照、JSON出力、推薦prefix、名前なしsort、プロパティURL解除は、共通の表/URI正本やhelperの変更有無を確認して分ける。保存済み所属の追加・移動を先行し、親MD5の遅延解決とBMTコース往復は同じentry/exporterを直列調整する。生成意味を変える単位では既存exporterVersionを必要に応じ更新し、最終版で旧生成物を一度再生成した後は未変更skipを維持する。 | 別の責務へ閉じるものは並行できる。同じ表の正本・保存APIまで広げる場合は同じ作業群へ戻す。 |
| 再生・成績・設定 | PlaybackPanelViewModelの共有gate・停止・世代契約と書込みを直列にする。管理入力回収で使う停止・joinは[ライブラリ変更](../spec/library/mutations.md#保留目録の解除と管理入力の回収)の現行契約を維持する。設定の録音形式・履歴プリセット・追加先改名は同じSettings責務で順次扱う。 | IR派生値は共通score表現・起動/終了基盤等を変更しない範囲で独立候補。外部固有の再開処理は[任意候補](release-follow-up-candidates.md#外部プレイヤー固有の試聴再生)として別に採否を判断し、同じownerへ書き込む場合だけ直列にする。 |
| ツリー編集 | EditableTextBlockの候補/終端とMainWindowの確定値取得を一単位にする。 | 他の表・検索部品と同名の概念だけでは結合しない。任意の更新通知翻訳を採る場合もMainWindowの一writerへ合流する。 |
| 更新 | ドライブルート正規化→完成後ready公開→同期lock保持。Program/gateway、既存のmarker公開helper、テスト補助、配布生成物・更新仕様を直列確認する。 | 更新以外の共有境界を変更しない単位とは並行可能。実プロセス/drive検証は負荷と所有を別途調整する。管理file→directory遷移は任意の別原因。 |

作業WTの暫定最大5本・他チャット分を含む所有、原則一writer、子担当への委譲と責任者への返却、仕事の配分とイベント待機、安全な切れ目での移管と成功版の引継ぎは[並列作業](../spec/development/agent-workflow.md#並列作業)に従う。検証・レビュー待ちだけを増やさず、完了・統合・再利用や回収を進める。ignoredの資料・bin・検証結果が新WTへ自動到達したとは扱わない。

Functionalは全WT同時一件とし、別WTのQuickと必要ビルドは並行できる。Quickは単位責任者が開始・結果・回収まで自主管理し、Functionalと性能測定の開始調整は全体ルートが行う。同WTの編集・整形・ビルド・レビュー、共有出力と資源の衝突を避け、対象と必要依存を凍結する。実測負荷や資源問題に応じて調整し、開始済み処理を都合で中断しない。詳細は[検証仕様](../spec/development/testing.md#複数ワークツリーの検証)に従う。

必要な検証・独立レビューとbranch内commitを終えた単位から、全体ルートへ版・変更と他単位影響・検証根拠・独立レビュー結果・未確認と残引継ぎ・後片付けを返す。全体ルートは最新devとの差と単位間の接続を確認し、一件ずつFFする。必要な作業branch側rebaseと新版の検証・影響レビュー、同版FF直後のFunctional重複回避、最新devから次branchへ切り替えるWT再利用、統合済みかつ全WT未使用の完了localbranch削除は[統合手順](../spec/development/release.md#通常の作業ブランチの統合)に従う。

| 単位 | 修正対象・根拠 | 採用する境界・対象外 | 完了を判定する代表ケース・既存検証の起点 |
| --- | --- | --- | --- |
| 1 | 重複グループの選択識別。見出しは一意でなく、`NormalizeDuplicateViewParameter` と `TryResolveDuplicateSource` が最初の一致・全件へ広げる。 | 上記の手動・自動選択契約へ統一。物理削除先まで誤ることは未確認。 | 同名・空名、未解決、明示root、二フォルダ/同一フォルダ整理後の次対象、三以上の存続対象を通常重複操作で判定。 |
| 2 | 初回重複解析後の古い画面切替。`RegularChartListOwner` のTask完了が後発移動後も元modeを通知する。 | 上記の最終受付優先。重複グループの識別と同じ通知経路を直列で扱い、F01の選択維持へ広げない。 | `RegularChartNavigationTests` 等で受付→別移動→解析完了を順序制御。通常/保守/導入/履歴/playlistの最終mode・parameter・行・左選択を保ち、cacheと必須通知は完了。時間待ちだけで再現しない。 |
| 3 | URL取得の大小文字同一視。成功/失敗集合等がHTTPパス・queryやDrive/MediaFireの不透明IDをIgnoreCaseで潰す。 | scheme/host正規化、fragment除去、同じ共有ファイルの重複抑止を保ち、有意なパス/query/IDは区別。成功/失敗/循環/非CORE除外のキー意味を揃える。MD5・Windowsパスの比較を変えない。 | `PlaylistUrlAcquisitionOwnershipTests`、`PlaylistExternalPackageLookupServiceTests` で別内容が両方保存・一つの導入引渡しへ進む。同一実体抑止、先行失敗後の別キー取得。追加通信・再試行は作らない。 |
| 4a | URL補完元の大小文字同一視。設定は新URLを保存するが、`BMSPlaylist.UrlCompletion` の取得前cache比較が旧データを返す。 | URL補完候補の比較と共通の小さなURI比較へ統一し、二つのprivate比較を置換。HTTPの有意差、scheme/host同値、Windows file URIの既存同一性を保つ。強制毎回取得・新cacheは不要。 | `PlaylistUrlCompletionTests` で大小文字だけ違う二つの固定取得元を実取得し、新実効値まで確認。同一元のcache、失敗時旧候補、空解除、TSV優先を保持。 |
| 4b | URL補完候補の大小文字同一視。`BMSTableEntry.UrlCompletion` が取得済み候補の代入前に同値として戻り、実効値・差分が旧値のままになる。 | URL補完の取得元比較と一コミットだが別判定。URL1/2の有意差をruntime・正規差分へ反映。通常補完はDBへ材料化せず、永続URL優先/上書き設定と明示保存時の材料化を保持。 | 取得元を別名にして取得元比較を回避し、候補の `Song.zip`→`song.zip` とquery差でURL1/2・実効値・receipt・表示行を判定。host/scheme同値で不要な変更なし。 |
| 5 | 転送アーカイブの保存名。`ResolveDownloadedArchiveFileName` は元URLにqueryがある場合しか最終応答URIを使わない。 | Content-Disposition優先で有効な最終名を採用。最終名が無効で元名が有効な既存ケースを保つ。拡張子推測・追加通信・HTML再帰探索は不要。 | `/download/123`→`/song.zip`、ヘッダー優先、最終名無効を通常URL取得の保存・導入引渡しまで判定。URL補完と同じworkflowを直列編集。 |
| 6 | 外部表の転送後相対参照。`PlaylistExternalSyncOwner.GetExternalContentAsync` が最終応答URIを捨てる。 | 取得した各文書の実URIへ相対header/data/entryを解決し、返却表・保存後・公開data基準を揃える。利用者の同期元は一時転送先へ置換しない。回数・制限・取消・解放・失敗時既存表保持を維持。 | slash補完転送・header転送から実dataとentry URLへ到達し、DB再読込み後も同じ意味。リダイレクト後の取得名とHTTP基盤を共有しても別単位。 |
| 7 | JSON出力のdata参照。header/data別ディレクトリを受理するが `PlaylistExport` がbasenameへ落とす。 | 出力時だけheaderから実dataへの正しいURIを作る。既存非空 `Data_url` と正本を保つ。別ディレクトリを新規拒否しない。 | `PlaylistJsonExportDialogTests` 等で同別ディレクトリ、予約文字、異ドライブのfile URIを実出力→参照解決。参照成立と公開時の可搬性を混同しない。 |
| 8 | BMTコース制約往復。`BmtTableExportService` が短縮gauge名だけを認識し、自身の正規出力名を次回に省く。 | GAUGE_5KEYS/7KEYS/9KEYS/24KEYSと既存短縮名を同じ値へ変換。順序・重複除去・未知制約省略を維持。trophy・公開規則は変えない。 | 実gzip→保存course→再出力で4種類、短縮名、他制約・重複を確認。既存exporterVersionで必要再生成へ接続。既存の復元・出力変換境界を使う。 |
| 9 | 内蔵推薦表の保存済みprefix。生成内容が変わるリロードで属性だけを戻し、実folder・明示順は無修飾のまま保存する。 | 未修飾の生成hashを保ち、保存済みprefixを完成folder・順序へ一度投影。共通変換を使い、setter全域副作用・fingerprint拡張・推薦専用MEMO引継ぎは作らない。 | `PlaylistRecommendedTableOwnerTests` で異なる既存A/Bスコア→設定切替→生成hash差のあるリロード→実所属・順序・DB・必要出力。同一入力時の不要保存抑制。 |
| 10 | LR2名前なし所属のsort範囲。抽出は空文字を所属指定とし、sortは空白判定でALLへ広げる。 | ALL=null、名前なし=空文字をsortでも揃える。受理済み空白名を改名・拒否しない。ALLの代表値・スキーマ・索引・ランダム規則は変えない。 | `BmsPlaylistCustomFolderOutputTests` で同hashの名前なし/別所属へ異なるlevel/adddate。実出力#COMMANDをSQLiteへ適用しLEVEL/ADDDATE両方向と別所属除外。設定保存とは独立したSQL出力の修正。 |
| 11 | 順位cache採用後の成績派生値。`BmsLibraryIrService` の高得点更新と `BMSScore.Overwrite` がrank/rateを更新せず、DJ LEVEL表示・検索・内訳が古い。 | 採用境界で得点/notesから既存 `ScoreValueCalculator` に揃え、新規・既存行を統一。低得点・同点、clear/BP/combo、順位・未送信判定を保つ。RATE列の不具合とは扱わず、全score走査・追加通信・本人IR存廃へ広げない。 | `BmsLibraryIrServiceTests` と起動系列で高得点既存/新規/低得点/同点、1000notes・1800点→AAA/90%を独立値で判定。snapshotから検索・集計へ代表接続。 |
| 12 | プロパティPage URL空欄。WPFの空文字→nullがsetterで失敗し、表示だけ空・旧値残存となる。 | nullによる明示解除と非null検証を区別し、無効入力を空へ読み替えない。一般・フォルダ・出力設定の全面作り直しはしない。 | 実ダイアログ→保存→再開で解除と不正入力、header/dataを含む保存経路を確認。限定Binding probeを通常編集session・DB保存の保証にしない。 |
| 13 | Updaterドライブルート正規化。`Program.cs` が絶対化前に末尾slashを落としてD:とし、作業場所基準で解決してready前に失敗する。 | 絶対化後にrootを保つ末尾整形、包含判定・URI基点を整合。書込み可能な既存root配置を拒否せず、ready/proceed・管理領域・長パス・UNC・reparse拒否・rollbackを維持。種類変更修正は任意。 | `UpdaterPackageSyncTests`、`UpdaterProcessGatewayTests` の実プロセス境界で隔離した仮想ドライブ等のroot配置→ready→適用/rollback。通常実ドライブ直下へ試験物を置かず、正規化単体だけで完了にしない。 |
| 14 | 録音形式の偽表示。`EncoderIndex` getterが実行ファイル欠損時に保存値を変えずWAVEを返す。 | 常に保存済み形式を表示し、明示WAVE選択は通常保存。利用不能な別形式の警告・保存拒否、実変換時の既存代替規則は維持。 | 保存済みMP3・encoder欠損でもMP3表示。明示WAVEの保存/再表示/変換snapshot一致、別の利用不能形式拒否後も保存・表示一致。実設定ページの接続を確認。 |
| 15 | 履歴プリセット確定名。ストアはTrimするがeditorへ未正規化名を戻し、次の編集・削除の照合が外れる。 | 確定する一件を正規化し、保存とeditorで同じ内容を使う。新ID・保存形式は不要。 | ` 練習 ` 保存後に再表示なしで対象変更・改名・削除、JSONと表示が同じ一件。取消・空名・重複名を維持。 |
| 16 | 追加出力先の改名入力。setterの毎回TrimがPropertyChanged Bindingへ戻り、途中の空白を消す。 | 入力文字列は保持し、正規化は確定時だけ。保存経路整理とは独立したBinding修正。 | 実Bindingで `My`→`My `→`My Charts`、前後空白の確定時除去、空白名拒否、改名後の出力先・保存値・表参照一致。 |
| 17 | 保護ファイルの計画内採番衝突。採番helperは実FSだけを見て後続通常宛先を奪い、後段の重複拒否で正常な導入が止まる。 | 短命な一物理計画で全通常宛先・譜面宛先・生成済み採番先を考慮する。既存suffix、同一内容省略と通常上書きを維持。処理済み名だけの予約や永続台帳は作らない。 | `BmsLibraryPackageInstallServiceTests` の実FS/DBで異内容readme.txt/readme(1).txtと既存readme.txt、両列挙順、複数suffix/採番名を確認。txt/bmx/pmx、同一内容省略、導入・重複統合の保持、所持追加・保留解除まで判定。現状の準備失敗を破損と扱わない。 |
| 18 | 再起動後のBMT親MD5欠落。hydrateが生値を遅延保持する一方、Org_md5 getterとBMT projectionが未解決リストを直読する。 | 親MD5の公開読取りを一つの遅延解決へ揃え、backing fieldで再入を避ける。clone/JSONも同値にし、起動時全件展開・出力ごとのDB再読込みは追加しない。旧誤出力は既存exporterVersion/skip境界で一度再生成する。 | `BmtTableExportServiceTests` と実SQLite hydrateで保存→再読込み→事前getterなし→実gzipの親MD5を判定。SHA256のみ/MD5あり、単複数親/親なし、詳細表示有無、元DB保持、旧manifestから再生成→次回省略を確認。コース制約往復と版更新を直列調整。 |
| 19 | フォルダ編集取消で表示Binding消失。EditableTextBlockのText SetValue/Escape/内部TextBox書戻しがOneWay DisplayNameを外し、recycling後に旧名を残す。 | 表示Bindingと短命な編集候補を分け、確定時だけ現在対象・候補を既存改名入口へ一度渡す。取消/対象交換は候補を捨てる。EditModeChangedとMainWindowの確定値取得、DPの開始/終端を同じ契約へ揃える。Escape一行だけの補正、DisplayName TwoWay化、仮想化無効化はしない。 | `MainWindowPlaylistWorkspaceWpfTests` の実ツリー/compiled部品で無変更Escape・変更Escape・正常確定。Aの選択/focusを外して実scrollで同一部品がCへ再利用されたこと、Binding・新DataContext・表示名・操作対象を一組で確認。取消は保存なし、確定は一回。 |
| 20 | ready書込み中の公開。最終名へ直接書くwriterと存在直後のReadAllTextが共有違反になり正常な更新開始を失敗させる。 | 同currentの一時名へmarkerをflush/closeし、完成後に最終ready名へ公開する。既存decisionの公開契約へ揃え、内容・期限・proceed/cancel・失敗時アプリ存続を維持。 | `UpdaterProcessGatewayTests`、`UpdaterPackageSyncTests` で公開準備中は最終名なし、出現時はclose済みを実writer/readerの順序制御で確認。receipt後だけ終了準備し、未公開期限切れ・取消・本来の起動失敗時回収を維持。writer終了後から読むfixtureだけにしない。 |
| 21 | 更新cleanupが所有中の同期lockを削除。journalなし・新版起動直後にUpdater/watchdogの排他保持と重なり誤Failed通知になる。 | update-transaction.lockを一時物回収から除外し、OpenOrCreateで再利用する既存寿命を維持。例外全体抑制・待機再試行は追加しない。 | `UpdateDownloadServiceTests.CleanupPreviousWorkDirectoryPreservesActiveUpdaterPayload` でjournalなし/実lock保持中もlock/current/receiptを保ち、古いdownloadを回収。ready受理→新版起動後cleanupの系列を接続し、破損とは断定しない。 |

## 責務別の参照と依存

| 領域 | 正本・共有境界 |
| --- | --- |
| 設定・保存・セル | [設定](../spec/runtime/settings.md)、[設定の保存](../spec/runtime/settings-persistence.md)、[一覧](../spec/ui/table-view.md)、[保存と出力](../spec/playlist/storage-and-export.md)。設定ViewModel/Windowへの書込みは直列。数値・日時は各正本の固定形式へ揃え、外部ラベル抽出と数値セル全体解析を分ける。score・BMTの失敗契約はそれぞれ独立。 |
| 物理変更・資源・推定 | [ライブラリ変更](../spec/library/mutations.md)、[整合性](../spec/library/file-db-consistency.md)、[重複](../spec/library/duplicate-files.md)、[導入](../spec/library/drop-install.md)、[推定](../spec/library/install-estimation.md)、[性能](../spec/core/performance-and-scale.md)。発見済み子の推定は導入経路で扱う。 |
| 表・URL・履歴 | [取得](../spec/playlist/downloads.md)、[Table URL取込み](../spec/playlist/table-url-import.md)、[BMT](../spec/playlist/bmt-export.md)、[LR2出力](../spec/playlist/lr2-custom-folders.md)、[履歴](../spec/playlist/play-history.md)。URL比較の共有とcache・取得所有者の結合は別判断。BMT混在名、推薦MEMO、本人IR存廃は任意。 |
| 再生・画面・外部起動 | [再生パネル](../spec/ui/playback-panel.md)、[一覧](../spec/ui/table-view.md)、[外部起動](../spec/ui/external-launch.md)。共通の一時配置・回収、範囲選択と汎用起動を扱う。外部固有のPause・再開始は任意側に分け、共通修正の前提にしない。右クリックと範囲選択の原因を区別。 |
| 更新 | [ポータブル更新](../spec/integration/portable-update.md)。root正規化、ready完成後公開、同期lock保持を別原因の必須単位とし、管理file→directory遷移の実装は混ぜない。 |

Linux上の抽出索引コードの計測は、Windowsの製品bridge・DB/UIの完了時間の非退行を保証しない。通常入力と正規キーが増える入力の仕事量・完了時間を分け、未測定の条件は制約として明記する。恣意的な許容率や無関係な全体ベンチマークを追加しない。

共有表コンテキストメニューの対象解決は、PlacementTargetとMenuItem.DataContextの参照元が異なるという指摘だけでは別表操作を実証できず、必須改修に数えない。限定WPF probeのA→B→Aでは一致した。確認する場合は実MainWindowで複数表を開閉し、現在対象と処理先を判定する。明示DataContext代入だけのテストで保証せず、乖離を再現した場合だけ対象解決の修正を再判断する。新cacheやメニュー全体の書換えを先行しない。

## 実装開始・検証・完了条件

各単位の着手前に、[テスト設計](../spec/development/test-authoring.md)で不足する恒久保証だけを独立設計し、単位責任者の承認と[引継ぎ点検](../spec/development/agent-workflow.md#引継ぎの点検)を経て着手する。既存の保存段階省略、旧要求固定、旧スコア保持、候補完了引継ぎを正解の根拠にしない。追加対象も修正案・受入例からそのままテスト数や新保証を確定せず、必要な独立設計と引継ぎ点検を経る。置換・削除するテストは保証の移管先または不要な理由を明確にする。

判定条件は既存サービス・実SQLite・実FS・native等へ置き、WPFは実Binding・イベント接続の代表へ分担する。新規テスト不要の判断と既存回帰の実行範囲を区別する。最新入力での必要媒体再保存、入力修正後の成功、未知設定・管理登録・無関係候補の保持、部分保存の失敗通知、通常閉鎖・再表示・終了joinも既存保証として残す。

各単位の反復は `scripts/verify-refactor.ps1` の対象を絞った `Quick`、通常開発のコミット・統合前は単位責任者が全体ルートと開始調整して `Functional`、公開候補は `Full` を選ぶ。[検証仕様](../spec/development/testing.md)の環境・実行回数・失敗分類に従う。native変更は生成物と配布整合も確認する。

保存・削除・互換性、受付・Task寿命、索引・走査完全性、主体の責務を実質変更する単位は重大変更として扱う。各最終差分で分類し、凍結した新規コンテキストの予備レビュー後、独立した通常または重大最終レビューへ進む。重要度ラベルだけでレビュー種別を選ばない。

通常入口の必要な再現と修正後の結果、維持する取消・失敗・保存・表示・性能を確認し、必須検証と最終レビューを通過して完了とする。必要な契約・実装・テスト対応を機能仕様へ移し、完了項目は本書から削除する。全項目が完了したら本書と不要な参照を削除し、[計画一覧](README.md)を更新する。
