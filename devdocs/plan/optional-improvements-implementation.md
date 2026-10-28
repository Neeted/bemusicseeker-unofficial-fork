# 採用した任意改善の実装計画

## 目的と再開の入口

採用済み改善の未完了範囲を、作業ワークツリーへ割り当てる単位にまとめる。以下は実装予定の契約・受入条件であり、実装済み保証ではない。既存課題は `dev` の `9b1ae8268`、追加課題は `ff9d74a49` までの関連経路と照合している。静的な到達確認と、実再現・検証の成功を区別し、再開時は対象範囲の後続差分だけを確認する。

本書と参照する仕様で再開できるよう、必要な成立条件・方針・検証境界を本文へ取り込む。追加資料の採用項目と見送りの分類は利用者合意済みだが、資料中の設計案を一括承認とは扱わない。見送りは[既知の課題・未採用候補](release-follow-up-candidates.md)に置く。共有URLのBMT、標準偏差0のT-SCORE、履歴空欄の整列、T/Nのdefined、本人IRのクリア降格、初期化失敗後の更新開始は今回着手しない。

文書だけで確定する現行契約は、[外部変更と整合保証](../spec/library/file-db-consistency.md#保証の前提)、[取得元別の履歴集計](../spec/playlist/play-history.md#期間と集計)、[一括外部同期ON](../spec/ui/playlist-properties.md)、[同内容BMS/PMSの詳細識別](../spec/library/chart-info.md#識別と共通詳細値)を正本とし、未実装課題として再計上しない。

## 共通の確定事項と対象外

- `chartInfoParser` は21を維持する。`lr2_song_db_sync_status.signature` の構成・意味・prefix・順序・参照定数は、利用者の事前明示承認なしに変更しない。旧BMSON対応でも版更新・全件強制再解析を加えず、同hash・版21キャッシュの通常再利用を保つ。
- 基本BMS/PMSの資源指示・資源名はCP932、先頭BOMの自動補正なしを維持する。表示メタデータの復号・再生parserのBOM処理と混同しない。所持再スキャンの確定文字コード保持は現行契約を使い、確定済み保持経路の無変更は無書込み、未確定の再検出は従来どおりとする。
- プレイリスト一覧は同じ表／譜面を選択し続け、主行消失時は残存選択の表示順先頭、全消失時は解除する。単列TSVも空欄の行を保つ。再生行消失時は単曲リピートで同じ譜面を継続し、通常自動送りは終曲で停止、手動前後送りは無作用とする。表示側の選択復元を別曲の再生根拠にしない。
- 構成ファイルフルスキャンのINSTL DSTは手入力・候補選択で指定でき、編集した所持一譜面だけへ既存の一時投影で保持する。同一起動中の画面再表示・後続再インストールへ使い、全リセット・再起動を越える保持やDB保存は追加しない。セル確定だけで再インストールを実行しない。保留パッケージは従来どおり全項目へ適用する。真に編集対象外の一覧には確定パスだけ表示し、▼・候補案内を出さない。
- 詳細セルの保存・必要表示・後処理が終わるまで、次の詳細セルの編集開始だけを止める。行選択・再生は止めず、終端後の明示編集を受け付ける。編集待ち行列や自動再保存は追加しない。
- 保留文字コードは現在の保留項目の寿命だけで保持し、再起動復元を追加しない。実際に導入成功した譜面の指定だけ成功先の所持データへ保存し、資源だけ更新した別の所持譜面や同hash別配置へ適用を広げない。
- 親・子それぞれの譜面が自己領域内に実在する音声・BGA・動画を定義する入れ子は重要警告として行を強調し、自動・手動とも導入しない。未使用定義は数える。譜面を持つ子領域は親から除外し、親音源参照だけの子、バナーだけ、未定義の同梱媒体だけでは禁止しない。全ての子譜面を一律拒否する案は採らない。
- 履歴右クリックのhashコピーを廃止し、MD5列を追加して既存SHA256列を使う。右クリックの「ランキングデータの更新」も廃止する。他用途のhash値・ローカルランキングcacheは残す。
- 空名・空白名の実子フォルダと表rootを区別し、保存済み所属名の前後空白を詳細要求で除かない。外部プレイヤー固有の試聴・再生、資源のみ導入後の保守対象拡張、プロパティ診断への文字コード引継ぎは対象外。
- 競合を許す必要性を先に判断し、既存受付・未開始拒否・明示再操作を優先する。予約、自動再試行、永続修復台帳、全画面共通管理器を増やさず、受理済みTaskの回収とデータ正しさを保つ。
- 対象パスはリポジトリ相対、行番号は調査版の目安。既存テストは保証分担の入口であり、テスト数・内部APIの固定要求ではない。

## 実装単位と着手順

単位責任者は範囲内の調査・設計承認・引継ぎ点検・実装・Quick・独立レビュー・ローカルコミットを管理する。全体ルートは利用者判断、共有契約、ワークツリー割当、Functionalと性能測定の開始調整、dev統合を管理する。複数原因は下表の区切りで独立して検証・レビュー・コミットし、準備が揃った単位から統合する。

| 単位 | 対象・コミットの区切り | 共有先・順序 |
| --- | --- | --- |
| U02 再生による現在セル追従 | 再生による選択変更と現在セルを揃える | U21 → U02 → U03。現行の識別選択を維持 |
| U03 共通表の表示とコピー | TSV空行 → URL幅 → フルスキャンDST編集・非編集案内 → 言語cache | U02後。同担当が能力付与・編集終端・一時投影まで所有。U15のDST検証と順序調整 |
| U04 履歴列とメニュー | hashコピー廃止・MD5列・末尾separator | 共通列・MainWindow。U03後、U05/U08と直列 |
| U05 ツリーのdrop・編集とフォルダ識別 | OS drop・展開 → 空名drop → 所属名空白保持 → 遅延改名失効 | MainWindow・Playlist要求/変更。U04/U08と直列 |
| U07 言語変更と確認文言 | 検索 → プロパティ → ランプ → 確認ボタン | 既存owner別コミット。全言語資源は一担当。U03/U16/U17/U20/U23と共有編集を調整 |
| U08 手動ランキング取得廃止 | UI・専用通信・資料・専用テストを除去 | MainWindow/composition/Ir。U04/U05後、起動時ローカルcache維持 |
| U09 確定文字コードの保持と導入・同期への引継ぎ | LR2全体同期 → 導入成功先への引継ぎ | Maintenance/Catalog/Install/LR2。導入serviceはU15との順序調整後、署名変更なし |
| U10 BMS再生入力 | 明示BASE62 | Ribbit BMSFile。U13のWriter検証と調整 |
| U12 内蔵再生の対象と候補探索 | 現在曲識別・送り位置・FolderSkip・Repeat失敗探索を統合 | 同じPlayback queue/VMで一単位。現行のentry索引を利用し、選択scopeは流用しない |
| U13 音声変換の出力名 | Writerのfile path契約へ統一 | Writer/変換owner。U10と検証の重複を調整 |
| U14 重複統合候補 | 候補とCtrl+Gを物理条件へ揃える | Duplicate owner。U15の物理パス判定と共有する場合だけ直列 |
| U15 導入・推定 | 宛先による範囲拡張廃止 → 入れ子禁止 → 重複保留の登録整理 → 候補根変換・ユニークhash票 → DST表記 | PackageInstall/service/estimation。同一writer。候補根と票数は同じ座標へ揃える。登録整理は発見結果/完全性を共用。U03/U09と順序調整 |
| U16 ランプの対象・取得元 | 実NO SONG所属 → 成績と履歴の同一捕捉 | lamp source/model/viewer。U07ランプ・U17日付と直列 |
| U17 履歴投影・日付境界 | 所属大小文字 → 履歴/ランプの日付共用 | PlayHistory/HistoricalSnapshot、U16後 |
| U20 外部同期の処理済み進捗 | 通知生産・表示・文言 | ExternalSyncOwner/SyncProgress/Hub、U22と直列、U07の言語資源編集と調整 |
| U21 詳細セル保存中の編集開始 | 先行保存終端まで次の編集開始を止める | 現行の識別選択と編集値引渡しを維持し、同じ表担当でU02へ接続 |
| U22 standalone外部表の出力名検査 | LR2専用検査をLR2モードへ限定 | ExternalSyncOwner。U20と同担当で順次 |
| U23 BMSルート確定後の導入先選択表示 | 保存値と選択肢の通知を揃える | Settings VM/page、U07設定通知と編集を調整 |

現在の所有は、`optional-updater` がU21後にU02と共通表、`optional-search-ui` がU07の言語表示、`optional-encoding` がU09、`optional-bms-input` がU12と再生入力、`optional-lr2-output` がU15。空いた作業ツリーを最新devから再利用し、U13/U14/U16/U17/U20/U22/U23は全体ルートが共有先の終了に応じて割り当てる。メイン以外最大5本であり、枠を埋めることを目的にしない。

### 開始・引継ぎと計画の更新

1. 最新dev、差分、作業ツリーと所有を確認し、単位責任者へ確定契約・許可パス・共有依存・完了条件を渡す。利用者判断待ちの範囲は実装せず、独立部分を進める。
2. Quickと必要ビルドは単位責任者が所有する。Functionalは同時1件、性能測定は重い検証と分離する。現在は並列formatでメモリ競合・時間超過が観測されたため、新しいQuick/buildも全体ルートの開始調整で直列化する。恒久的な検証規則の変更ではない。
3. 検証・独立レビュー後、日本語の件名・箇条書き本文でローカルコミットする。全体ルートが基準dev・検証版・レビュー版を照合し、必要な作業ブランチ側rebaseと影響確認後、一件ずつ `merge --ff-only` で統合する。公開履歴/dev/mainはrebaseせず、同じ最終版のFunctionalを統合直後に重複実行しない。
4. 各実装単位の完了に伴う本計画の更新は、その単位の作業ワークツリーで行う。現行仕様・必要な設計判断の正本への移管、完了項目の除去、残る範囲の直接依存の更新を製品変更と同じレビュー・コミット範囲へ含めてdevへ統合する。全体ルートは計画の整合確認と共有作業の順序調整を担い、メイン作業ツリーで完了更新を後追いコミットしない。他単位の未完了範囲を変更せず、計画の同時編集や完了記録の蓄積を行わない。
5. 統合後は最新devから次ブランチへ切り替え、統合済み・未使用の完了ローカルブランチを削除する。送信・公開・タグ・署名・バージョン更新は本書で許可しない。

正本は[エージェント運用](../spec/development/agent-workflow.md)、[検証](../spec/development/testing.md)、[ブランチ運用](../spec/development/release.md)。委譲の完了はイベントで待ち、定期的な状態・差分・ログ読取りで代替しない。

## UI共通整理の適用境界

表示Bindingは正本、編集候補は一回の編集が所有し、確定は開始時の対象へ一度渡す。正当なフォーカス離脱確定・IME Enter/Esc・各設定固有の確定境界を保つ。対象交換では古い候補/遅延callbackを破棄し、言語・テーマ・純再描画では候補・選択・scrollを保つ。PropertyChanged/LostFocus/Explicitの一律化や、通常DP・ローカル値所有設定の全面的SetCurrentValue化はしない。

Loadedは表示同期と一回購読、Unloadedは表示購読・予約だけの解除、Closed/Disposeは新規表示の終端とする。開始済みの実処理は既存機能ownerが回収し、Unloadedを最終終了に読み替えない。新しい共通基底・汎用画面差分・入力管理器・新翻訳cache・全行購読/実体化は不要。変更機能の仕様に表示・候補・選択・保存の所有、入力/取消、内外移動、再利用、純表示変更、遅延表示と実Taskの終端を短く記す。

既存のEditableTextBlockの候補分離/Escape、再生開始時の一回フォーカス、右クリック現在セル、通常一覧の識別復元、保存→出力→必要表示→編集解除→受付解放、三択のCancel/X/Escは維持する境界であり、新規実装対象ではない。設定Bindingや非モーダル終了も、具体的な不足なしに全体を作り直さない。初回重複解析の完了で画面を奪わない保証を、明示Merge/Hash成功後の無条件の次群/存続群選択へ広げない。

## U02 再生による現在セル追従

### 再生による選択行変更と現在セル

**現存。再生による主行変更の改善を一覧単位へ含める。** `PlaybackPanelViewModel.cs` の再生候補採用が一覧の選択indexを更新するが、`CustomTableView.OnSelectedIndexChanged` は `selectionModel.SyncCurrentIndex` と再描画だけで、`currentCellHit` を更新しない。Aの列を操作後、Next/Previous/自然終了でBへ移ると、B選択のままF2・文字入力・単一セルコピーにAの現在セルが残る。

実際に受理した外部主行変更の入口で既存 `EnsureCurrentCellForCurrentRow` を共用し、現存する同列をBへ引き継ぐ。内部選択の逆通知と識別復元の条件を守り、コピー・編集入口ごとの補正は置かない。編集中は `CommitActiveEdit`/`CellEditEnded` でAの旧属性へ入力を一回渡してから現在セルを更新し、Bへdraftを付け替えずBのeditorを自動生成しない。保存TaskをUIで同期的に待たず既存terminalへ接続する。

主行を変えないPause/Resume/Stop/時刻や表示更新はeditor・候補・現在セルを変えない。現在セル更新のためのフォーカス要求、追従タイマー、再生側の現在セル状態、draft保存復元状態は不要。現在列が消えた/0列/0行/解除の古いhitは既存規則へ揃える。

`CustomTableNavigationTests` の「外部SelectedIndex変更で現在セルは作り直さない」という旧期待は新契約へ更新する。`CustomTableEditingTests` でAへの一回終端、`MainWindowPlaybackWpfTests` で実再生・Binding・描画/コピー/編集の一致、主行不変時の編集保持を分担する。入力不正や保存失敗は既存保存terminalの通知に従う。再生開始を保存完了まで止める新仕様は含めず、そのための追加状態・再試行は不要。

現行の `CustomTableView` の識別選択を維持し、編集開始境界を扱うU21の後に同じ担当で実装する。

## U03 共通表の表示とコピー

### 1列TSVコピーの空行

**現存。ユーザー回答で空欄行保持を採用。** `CustomTableDataTransfer.BuildTsv` が `StringBuilder.Length > 0` で行区切りを判断するため、TITLEのみ表示した履歴の空行→SongがSongだけになる。`CustomTableView.CopySelectedRowsToClipboard` も空文字を無条件に不受理とする。

行の存在と文字列長を分け、先頭・中間・末尾・全空行の順序/数を保持する。空行＋SongはCRLF＋Song、空2行はCRLF。0行・0可視列と実在する空セルを区別し、選択した一つの空セルのコピーが古いClipboardを残す扱いも確認する。全件実体化・選択モデル変更は不要で、制御文字正規化と元URLコピーを維持する。

`CustomTableColumnFactoryTests` の既存TSV検証へ空行条件を追加し、実コピーの代表は共通表WPFで接続。複数列、非表示列、null行除外、制御文字の既存保証を維持する。列生成・URL幅とファイルが重なるが原因は独立。TSV単位だけで完了できる。

### URL列幅による表示と操作

**現存する固定ボタンを機能拡張する項目。** `CustomTableColumnFactory.CreateAllMainColumns` のUrl1/Url2はmin/max40、CanResize=false、DownloadIcon、repeat edit=falseで固定。幅を持つ設定自体は既存 `ColumnLayout` にある。

確定済み計画の契約は40 DIPなら現行取得ボタンで通常クリック/再クリックとも取得、F2/文字入力による既存編集と元URLコピーを維持。40より広ければ元URLを表示し、通常セルの選択・再クリック/F2/文字入力編集に揃え、取得actionは出さない。空URLも同じ役割とし、幅だけで編集権限を増やさない。単クリック即編集・行double click変更は含めない。

既存幅から表示種別・値・配置・action・repeat editを同じ列定義で導く。40へ戻せば戻る。新mode保存フラグやDPI物理pixel条件を持たず、列再構築/cache失効・編集終端・幅保存を再利用する。`CustomTableColumn.cs`、`CustomTableView`/interaction、列設定の共有変更になる。

`CustomTableColumnFactoryTests`/`CustomTableEditingTests`/`CustomTableColumnSettingsTests` と実URL列で40→広幅→40、Url1/2、空値、編集許可/拒否、候補編集中の幅変更、保存幅復元、Tooltip/コピーを確認。既定幅の繰返し取得と広幅で取得しない結果を別に確認する。選択/current-cell単位と同じ表担当で順次実装可能。

### INSTL DSTの編集案内とフルスキャンの再インストール先指定

**表示判断と編集判断の不一致、およびフルスキャンの能力付与不足を改善する未実装項目。** `CustomTableColumnFactory.GetInstallDestinationText` はchartの `InstallDestinationSuggestions` が非空なら、画面の操作文脈にかかわらず `▼ SelectCandidate` または `▼ path` を返す。`RegularChartListOwner.CanBeginCellEdit` は `InstallPending`/`FullScanCheck` 区分かつ `UpdateInstallDestination` 能力を要求するが、`GridRowResolver` は同能力を保留パッケージだけへ付与している。`MainWindowViewModel.MainChartListCellEditBeginningRequested` はプレイリスト詳細なら別の `CanBeginDetailEdit` へ振り分け、導入先列はその編集属性に含まれない。列表示だけを編集可否の根拠にせず、通常の編集受付と揃える。

**ユーザー確定:** 構成ファイルフルスキャン画面でも、保留と同じ手入力・候補選択により、所持譜面の再インストール先を指定できるようにする。通常の所持行は編集した一譜面を対象とし、同フォルダや同hashの別配置へ一括適用しない。保留パッケージでは既存どおり全項目へ適用する。真に編集不可の一覧は確定済みパスだけを表示し、▼・候補選択案内を出さない。編集権限の拡張はこのフルスキャンに限り、別のインストール済みパッケージ画面や未所持プレイリスト行へ広げない。

指定先の保持には既存の一時投影を使う。形式・hash・pathが同じ譜面の画面再表示では確定値を保持し、同一起動中の後続再インストールがその値を使う。全リセット・再起動後は保持せず、再スキャンでの識別変更を越える保持も保証しない。新しいDB保存項目を追加せず、セル確定だけで物理再インストールを開始しない。手入力の実在・既知所持先の検査、空編集・取消・明示クリアの区別、候補と確定パスの正本は[導入先推定](../spec/library/install-estimation.md#比較用の基本情報と導入先の代表ペア)と既存編集終端へ揃える。

実装は `MainChartListViewModel` の `FullScanCheck` 文脈、`GridRowResolver` の能力付与、`RegularChartListOwner` の編集開始・確定から既存の `PendingPackageWorkflowOwner.SetPendingAsync`／`BMSLibrary.SetPendingInstallDestination` へ接続する。モデルは既に全件確認の既知所持譜面を受け付け、単独所持行の変更を `MainChartRowProjectionOwner` の一時投影へ公開する。別操作の `FixInstalledLocationsAsync` は表示行の現在導入先を `RepairInstalledLocationRequest` へ捕捉するため、この通常経路まで追って指定先の利用を確認する。列定義へ編集可否を最小限渡し、表示cache失効も同じ文脈の切替へ接続する。候補表示専用の別状態、一覧ごとの独立権限判定、全行購読、別の再インストール経路は追加しない。

既存 `CustomTableColumnFactoryTests.InstallDestinationCell_DecoratesCandidatesWithoutChangingEditPaths` はcontextを渡さず候補があれば▼という内部期待を固定しており、利用者契約の根拠にしない。独立テスト設計には追加契約と保持寿命を渡し、`CustomTableEditingTests`、`CustomTablePhase6CacheTests`、`MainWindowPackageMaintenanceWpfTests`、導入先更新と再インストールの既存テストで保証を分担する。実フルスキャン画面の全件・欠落・欠落無視（`FullScanAllChartsFilterSelected` / `FileMissingFilterSelected` / `FileMissingIgnoredFilterSelected`）の通常入口で編集開始、候補選択・手入力、確定・取消、確定値と一覧表示の一致、画面再表示の保持、後続の明示再インストールによる指定先利用を確認する。セル確定だけでは物理処理しないこと、単譜面と保留パッケージ全項目の範囲、全リセット・再起動を越える保持を追加しないことも確認する。候補を持つ同じ元値で非編集一覧は案内なし・編集不可、保留とフルスキャンは案内と操作が一致することを確認し、保留の既存操作を維持する。

現行の識別選択を維持し、U21とU02の現在セル追従を完結させた同担当が、このU03項目を実装する。導入サービスを共有するU15とは編集・検証の順序を調整する。機能仕様の実装済み記述への反映は製品変更と同時に行う。

### 共通表の保留候補案内

**現存。** 通常の保留行で導入先未指定かつ候補ありのとき、`CustomTableColumn.GetInstallDestinationText`（`Views/CustomTable/CustomTableColumn.cs:527`）は `Resources.InstallDestination_SelectCandidate` を「▼」付きで返す。`CustomTableRowCache` がその整形値と描画を保持する。

`CustomTableViewLoaded` / `Unloaded`（322/329行付近）はThemeChangedと表VMの購読を管理し、`RefreshTheme`（436行付近）はrow/text cacheを消して再描画する。`UpdateDrawingEnvironment`（3048行付近）はthread culture/UI culture/palette変更時に `InvalidateDisplayValues` とtext cache失効を行う。しかし `ResourceService.Resources` の購読はなく、ChangeCultureはthread cultureを変えないため、同じrowの既存文字列が残る。

- 方針: 表のLoaded/Unloadedに沿った一つの資源購読から、既存表示値/描画cacheを失効して `RequestRedraw` へ接続する。既存 `InvalidateDisplayValues` / row cache / text layout cacheの責務を使い、言語ごとの別cacheを足さない。thread culture/テーマ用の既存失効を削除しない。
- 寿命: Loaded中だけ受信し、Unloadedで解除。Dispatcherへ投稿する場合も切離し後の旧更新を表へ適用しない。再Loaded時は現言語で既存cacheを更新する。
- 完了条件: 同じItemsSource・行参照・選択・scroll・編集中候補・生pathを保持して案内文字列が更新。全行実体化/再検索/ItemsSource交換なし。実表示済み行でアプリChangeCulture→再描画を観測し、hidden/unloaded→再表示も分担。
- 既存テスト: `CustomTablePhase6CacheTests.InstallDestinationCell_RendersCandidateMarkerInsideNarrowCell`、`CellValueCache_ReusesValuesUntilRowInvalidated`、`CustomTableView_ShownAfterHiddenChangeUsesLatestValues`、`CustomTableView_MainChartListLifecycleFollowsLoadedDataContext`。`CustomTableTextLayoutCacheTests` のcultureキー確認はアプリ言語通知の代用にしない。
- 共有書込み先: `CustomTableView.cs` と同cache testsは共通UI/編集寿命/URL列整理と直列化する。行モデルへ言語購読を増やす変更は不要。列headerの再構築一般まで本件のために作り直さない。
- 除外: theme、DPI、thread culture変化時の既存cache失効、候補印の狭幅描画そのものは実装済み。

## U04 履歴列とメニュー

### 履歴の右クリックhashコピー廃止とMD5/SHA256列

**現存。ユーザー確定契約に置換する。** 旧資料の「MD5不明ならMD5メニューだけ隠す」は採らず、履歴専用の右クリックhashコピーを廃止し、不要なseparatorを除く。MD5・SHA256を列から通常セル/TSVコピーできるようにする。

`CustomTableColumnFactory.CreateAllPlayHistoryColumns` にはSHA256列とRAW HASH列が既にあり、MD5列がない。`PlayHistoryRow` はLR2で `Md5=rawHash`、beatorajaで `Md5=md5`、`Sha256=sha256` を持つので、種類の正しい既存値を直接使う。MD5へSHA256を補うfallback、新hash計算、DB列、再読取りを追加しない。RAW HASHは生の履歴識別列として別に存在するため、今回の依頼だけで削除しない。

追加・接続先は `Views/CustomTable/CustomTableColumn.cs`、`ViewModels/ChartList/CustomTableColumnSettings.cs`（既存 `Hash` layout利用を優先）、`ViewModels/PlayHistory/PlayHistorySortEngine.cs`、`Views/MainWindow/MainWindow.xaml` の履歴列メニュー。通常列の表示切替・幅/順序保存・初期化に接続する。既存列ID/設定の不要な破壊や重複した新設定は避ける。

削除候補は `MainWindow.xaml` のCopyMd5/CopySha256項目と専用separator、`MainWindow.cs` の表示switch/`playHistoryContextMenuItemCopyHashClick`、`PlayHistoryContextMenuState` のコピー専用定数/CanCopy/GetCopyValue、`PlayHistoryContextMenuActionKind` のコピー値、`PlayHistoryWorkflowOwner.ContextMenu.cs` のコピーaction/helper、コピー専用資源（全repoの利用箇所を確認して削除）。`ForValue`/Valueも他利用がなければ削る。

**残す責務:** 同じメニューstateのMd5/Sha256は設定Web actionとscore viewerへも渡される。`HasVisibleItem` は現在hashコピーがあることを使って未解決行も受理しているので、コピー廃止で未解決行の設定Web actionまで失わせない。入力値解決とメニューの実際の表示項目の判定を既存責務内で整理する。ここで `RawHash` を無条件にMd5へ入れる旧fallbackを新列へ持ち込まない。設定Web actionのSHA256等は維持する。

保証は未解決beatoraja（MD5空、SHA256既存値）、未解決LR2（MD5生値）、解決済み両hash、行交換後のセルコピー、列設定保存/復元、メニューコピーなし・先頭/末尾/連続separatorなし、設定Web actionとローカル操作/日付検索を維持。`CustomTableColumnFactoryTests`/`CustomTableColumnSettingsTests`、履歴read model/sort系列、`MainWindowPlayHistoryWpfTests` に分担し、削除したcopy actionテストを新列コピーへ置換する。

### ライブラリツリーの末尾separator

**現存。正確な対象はrootメニュー。** `BeMusicSeeker/Views/MainWindow/MainWindow.xaml` の `treeViewLibraryFolderRootContextMenu` はReload、Reinitialize_library、末尾Separatorの3項目で、separatorの後に項目がない。ここを削除する。別の `treeViewLibraryFolderContextMenu` はseparator後にExplorer等があり、同名に近いことを理由に削らない。

メニュー定義一箇所の削除で足り、動的separator管理器は不要。`MainWindowTreePresentationWpfTests` の実rootメニュー代表または既存のcompiledメニュー検証で残る2操作の到達と末尾separatorなしを確認する。削除のためだけの多層テストは増やさない。履歴メニュー変更とXAMLが重なるため同担当または順次統合。

## U05 ツリーのdrop・編集とフォルダ識別

### プレイリストツリーのファイルdrop・ホバー展開

**両方現存。既存の合意済み任意改善。** `MainWindow.playlistTableDrop` は形式検査より先に `e.Handled=true` にし、内部行形式でなければreturnする。OSファイルが表見出し/子フォルダへ落ちると、通常の `Window_Drop`→`DroppedInstallDropTerminal`→`PackageInstallWorkflow.AcquireAndTryEnqueueDroppedPaths` に届かない。内部譜面行dropは既存の表/フォルダ、OS FileDropは既存Windowの導入受付へ一回だけ通す。新規の表import扱いへ変えない。

`playlistTableDragEnter` は1秒後のanimation値とCompletedの両方に `!tviTable.IsExpanded` を使い、展開済み見出しも閉じる。未展開の見出しだけを開く。DragLeave/Drop/取消の既存後始末を保持し、新しい展開保存状態・DnD共通管理を加えない。

共有変更先は `MainWindow.cs` のDrop/DragEnter/DragLeave群。`MainWindowPlaylistWorkspaceWpfTests` と既存drop導入系列に、実ヒット対象・一回の受付・追加結果・展開済み維持/未展開→開くを分担する。Handledやanimation値だけのテストでは不十分。アーカイブ・ファイル条件の網羅をこのUI単位へ複製しない。

### 空名プレイリストフォルダへのdrop

**現存。ユーザー確定:** root分類は表rootへdropしたときだけ。実在する空名子ノードは通常フォルダdropであり、その空所属へ追加/移動する。

`PlaylistWorkspaceViewModel.Mutations.cs` の `AddRowsToFolder` は `targetFolder?.FolderName ?? string.Empty` と `string.IsNullOrWhiteSpace(folderName)` でnodeなしのrootと空名nodeを潰す。後続の存在検査、同表entry除去、root分類用 `BuildRootFolderDropMutations` 呼出しがすべてこの区別に依存する。最初だけ直さず、`targetFolder == null` による一つのroot判定を全分岐へ接続する。空文字/空白が名前の実nodeもroot扱いしない。

通常名、真の表見出しdropの既存分類・採番、特殊node/同期ON拒否、MEMO/URL/level保持、物理移動なしを維持する。空名禁止・自動改名・DBスキーマ・新しい分類戦略は不要。`PlaylistWorkspacePersistenceCommandTests` のroot drop系列で所持/同表/別表→空所属、同一所属再drop、DB読戻しと非対象値を確認し、実ヒットnode代表は `MainWindowPlaylistWorkspaceWpfTests` へ置く。モデルdropとツリーUIの合同単位にすれば、二つのroot判定や同じDnDのテスト重複を避けられる。

### 保存済み所属名の前後空白

`BMSTable.EntryCommit` の改名は原名を保存する一方、`PlaylistRequestFactory` が詳細要求のfolderを `Trim()` し、` A ` がAを指す。要求生成と詳細元集合再利用の判定へ保存済み所属キーをそのまま渡し、nullの表rootと空文字・空白だけの実folderを区別する。既存データの一括Trim・同名統合・禁止・自動改名は行わない。

Aと ` A ` が共存する表で、改名、再選択、保存再読込み、詳細元集合再利用を通して所属と表示を確認する。空名dropと同じ識別契約へ揃え、要求側の一箇所修正だけで全経路を保証したと扱わない。

### ツリーの遅延改名開始

**現存。共通UI整理の具体的不足として扱う。** `MainWindow.playlistTableFolderClicked` は選択済み編集可能フォルダをクリックすると `Task.Delay(1000)` の後、DataContext・ヘッダー・選択・編集可否だけ再検査して `IsInEditMode=true` にする。その間に検索へ入力しても選択は残る。`EditableTextBlock.TextBox_Loaded` がFocus/SelectAllし、検索から入力を奪い、その後Enter/離脱で通常改名へ進む。EscapeのBinding修正・再生開始の一回focus化とは別。

そのクリックの短命な開始要求として、後発の明示入力/フォーカス移動、対象交換/切離し、別編集開始/終了で失効させる。既存部品の編集候補と保存入口を使う。現在の無条件な遅延callbackを残して新ガードを重ねず、旧開始要求の一つの所有に整理する。全画面入力管理器、恒久世代台帳、固定待機の再試行は不要。

`MainWindowPlaylistWorkspaceWpfTests` でクリック→1秒以内に検索入力→callback後も検索の全文/フォーカス保持・改名0回、移動しない場合は一回開始を確認。F2、別ノード、切離し、重複クリックで古い要求が再開しない代表を分担する。遅延完了後の全UI状態を保証する一般フレームワークへ拡張しない。DnDと同MainWindowツリー領域のため同担当へまとめられる。

## U07 言語変更と確認文言

### 検索提示（通常検索・プレイリストサマリー）

**editor本体は一部更新済み・外側の警告/ヘルプは両入口で現存。** `KeywordSearchEditor.Loaded/Unloaded` はResourceService購読を管理し、`ResourceServicePropertyChanged` → `RefreshLocalizedSurface`（226–253行付近）でアクセシビリティ名と開いた候補を更新済み。このhandlerを新たに作り直さない。

通常側は `MainWindow.xaml:716/725` が `ChartFilters.KeywordSearchWarningText` / `KeywordSearchHelpText` をBindingする。`ChartListFilterViewModel.UpdateKeywordSearchPresentation`（188行付近）は警告を再生成しhelpを通知できるが、呼出元は入力/文脈変更でResourceServiceの購読なし。Helpはgetterが最新資源を読むだけなので既存Bindingの更新契機が不足する。

サマリー側は `MainWindow.xaml:767/776` が `PlaylistWorkspace.PlaylistSummaryKeywordSearchWarningText` / `PlaylistSummaryKeywordSearchHelpText` をBindingする。`PlaylistWorkspaceViewModel.KeywordSearch.cs.UpdatePlaylistSummaryKeywordSearchPresentation`（47行付近）は入力変更時のwarning生成だけで、help通知も言語購読もない。別ownerのまま同じ改善項目に含める。通常検索だけを直して完了にしない。

- 方針: 通常ownerは既存 `UpdateKeywordSearchPresentation(raiseHelpText:true)`、サマリーownerは既存warning整形とhelp通知を、資源変更へ接続する。入力・文脈・候補版を変えず、保存式の再代入やUpdateContextを言語通知の代用にしない。
- 寿命: editorのLoaded/Unloadedは既に確立しているが、それだけでは外側ownerの通知にならない。新購読は各ownerの実寿命で解除される接続を明示する。ChartFiltersはApplicationCompositionで生成されMainWindowが保持し、ファイル内にDispose overrideはないため、「Disposeに書けば通常終了で必ず呼ばれる」と仮定しない。既存shell終了/表示切離しへの実接続か弱い購読等の既存方式を実装時に選び、永久static購読を残さない。サマリーにも同じ寿命確認が必要。新しい検索統合ownerは作らない。
- 完了条件: 同じraw入力・文脈のまま警告Tooltipと開いているhelpが更新。通常/プレイリストサマリー双方で検索イベント/再取得回数0、入力・caret・候補選択・history/favorite・保存値不変。閉鎖/切離し後の不要通知なし。
- 既存テスト: `ChartListFilterViewModelTests.KeywordFilter_UpdatesWarningAndContextHelpNotifications`、`KeywordFilter_InvalidPlayHistoryDateShowsLocalizedWarning` は入力時の更新を確認。サマリーの既存keyword/presentation testsと代表MainWindow WPFに通常/summary両Bindingを分担。既存editor language/accessibilityの保証は維持する。
- 共有書込み先: `ChartListFilterViewModel.cs`、`PlaylistWorkspaceViewModel.KeywordSearch.cs`（必要時同VM本体の寿命接続）、検索VM/WPF tests。候補文法引用/保存式比較と同ファイル編集は直列化するが、今回の静的確認では文法・履歴比較を再調査しない。
- 除外: editorアクセシビリティ名/開いた候補の言語handler、検索実行/文法、履歴/favorite識別、検索入力編集の状態整理。

### 開いている譜面プロパティ

**現存。** `ChartPropertiesWindowManager.Show`相当の通常窓生成→ `ChartPropertiesViewModel` → `LoadAsync` は一回の背景診断を表示する。`ChartPropertiesViewModel` のconstructorは配置basisを翻訳済み `VerificationBasis` に置換して保持し、`LoadCoreAsync` は診断結果を `Issues` / `UnusedDefinitions` / `UndefinedReferences` に整形して結果本体を保持しない。失敗 `Status` も文字列だけ。`UnusedDescription` / `UndefinedDescription` は資源getterでもPropertyChangedが発火しない。ResourceService購読なし。

- 方針: 捕捉した配置basisと取得済みの診断事実を画面寿命の入力として保ち、既存の `FormatReason` / `JoinOrNone` に再整形を集める。成功/失敗/初期という必要な表示状態から失敗文を再生成し、元例外を翻訳目的で保持しない。basis、issues、なし、BMS/BMSON説明、失敗Statusの通知を揃える。Title/Artist/path/実診断内容はそのまま。
- 寿命: `ChartPropertiesWindow.WindowClosed` → `viewModel.Close()` が既存終端。ここで購読解除し、開始済みLoadAsyncの失敗回収/非反映を保つ。managerは開始済みTaskを終了待機するため、その寿命を新造しない。
- 完了条件: 開いた同じ窓の言語変更で確認基準・不足理由・なし・形式説明・失敗表示が新言語。read回数一回、元path/配置basis/資源factを保持。読込み中の言語変更は完成時の現言語で表示。閉鎖後は言語通知・完了結果の反映なし。
- 既存テスト: `ChartPropertiesViewModelTests.LoadAsync_RunsOnceInBackgroundAndPublishesResult`、`LoadAsync_KeepsPlacementCapturedBeforeDestinationChanges`、`Close_ObservesStartedReadWithoutPublishingAfterClose`、`LoadAsync_ExposesLocalizedFailureWithoutExceptionDetails`、`LoadAsync_FormatsSourceCommandsAndFormatSpecificDescriptions`。実Binding/コピー/窓閉鎖は `ChartPropertiesWindowTests` とmanager系列に分担する。
- 共有書込み先: `ChartPropertiesViewModel.cs`、同VM/Window tests。保存済み文字コードを診断へ渡す別候補と同ファイルを直列化し、言語更新から再解析を呼ばない。
- 除外: 一回読込み、捕捉配置保持、閉鎖後非反映、WindowのResourceService直結ラベルは既存保証。

### ランプの既存区間

**一部更新済み・区間表示は現存。** `PlaylistLampViewerViewModel` はconstructor（184行付近）でResourceServiceへ購読し、`Dispose`（404行付近）で解除済み。`ResourceServicePropertyChanged`（524行付近）は `RefreshStatistics`、WindowTitle、ScoreSourceTextを更新する。`RefreshStatistics` は既存集計結果を再整形するだけで、グラフを再構築しない。

不足は同じ既存segment VMへの通知。`PlaylistLampViewerSegmentViewModel` はconstructor（636行付近）で `Label=PlaylistLampViewerLabels.GetLabel(segment)` を固定。`DetailText` / `AutomationName` / `SelectionStatus` は資源getterだが、言語変更通知が来ない。`PlaylistLampViewerWindow.xaml` の凡例はLabel/DetailText、bar ButtonはToolTip=DetailText、Automation Name=AutomationName、ItemStatus=SelectionStatusをBindingしている。handlerがある事実だけで全ラベル更新済みと扱えない。

- 方針: 既存親handlerから `EnumerateSegments`（589行付近、全体clear/rankと各folderのclear/rankを列挙）で同じsegmentへ表示更新を依頼する。Labelを再整形/再評価し、Label・PercentageText・DetailText・AutomationName・SelectionStatusの必要通知を行う。新たにsegmentごとの資源購読を作らない。再集計/ApplyResult/segment collection再作成は呼ばない。
- 寿命: 既存親購読/Disposeを利用。既に閉じたviewerへ通知しない。所有している同じsegment/Buttonと選択を保つ。
- 完了条件: 捕捉済み同じButton/segmentでToolTip・Automation Name/ItemStatus・凡例Labelが更新。選択segment、表示mode、folder/clear/rank collection参照、集計結果、取得回数を保持。新規取得・再集計なし。小さい正値/算出不可の割合説明も現言語である。
- 既存テスト: `PlaylistLampViewerWindowPresentationTests.Viewer_statisticsUseContentWidthsAndWrapWholeGroupsWhileTrackingLanguageAndTheme` は統計言語更新、`Viewer_datePresentationTracksApplicationCultureWithoutChangingTheQuery` は日付の現文化対応、`Viewer_countModeUsesAllNormalFoldersAndPreservesProjectionUntilDataRefresh` は表示modeの参照保持を保証。既存segmentを捕捉して言語変更後のBinding値を追加確認し、変更後に新しいsegmentを取り直して成功判定しない。
- 共有書込み先: `PlaylistLampViewerViewModel.cs` / presentation tests。過去日取得元の一貫性や日付境界の別修正と同ファイルでも、集計境界は変更しない。
- 除外: 窓title/source、統計ラベル、DatePicker言語、数値/日時の固定書式、count-mode切替のprojection保持は既改善。

### 確認ボタンの翻訳

**現存。** `BeMusicSeeker/Views/ThemedMessageBox.cs:GetButtons` がOK/Yes/No/Cancelを固定英語で生成する。既定資源・`Resources.cs`・`lang/`の6言語へ既存資源を優先して接続する。modal表示中の言語変更購読や翻訳cacheは不要。

`ThemedMessageBoxTests` はdefault/resultの純粋規則、`DialogPresentationTests` は実Content・明示応答・Enter・Escへ分担する。既存のYes=全体/No=譜面のみ/Cancel・X・Esc=全操作取消の契約は変えない。資源を触る他翻訳作業と担当を揃えれば、ファイル衝突を避けて独立実装可能。

## U08 手動ランキング取得廃止

### 手動ランキング更新の廃止と終了寿命

**手動通信の入口・寿命問題は残るが、採用方針は廃止に確定したため新しい終了待機設計は不要。** `MainWindow.xaml:171/244` の「ランキングデータの更新」→ `tableContextMenuItemUpdateRankingDataClick`（7548行付近）→ `MainWindowRankingCacheTerminal` → `RankingCacheDownloadWorkflowOwner.Request` がbackground taskを開始し、`ObserveFault` だけでShell shutdownへjoinしない。

- 採用方針: 両context menuの項目と連続/末尾の不要separatorを除去する。terminal、composition引数/保持、owner/runtime、専用capability、専用UI可否判定を本番参照に従って取り除く。到達しなくなる通信末端も残さない。
- 専用末端の候補: `BMSLibrary.GetIRDataNeedUpdates` / `DownloadIRData` / `IRDataCacheInfo`、`rankingInfoUrl` / `rankingDataUrl`、`BmsLibraryIrClient.GetRankingInfo` / `DownloadRankingData` とinterface、`BmsLibraryIrService.GetIRDataNeedUpdatesFromRankingInfo` / `DownloadRankingCacheFiles` / `PrepareDownloadedRankingCache` / `PromoteDownloadedRankingCache` / `ApplyPreparedDownloadedRankingCache` と専用DTO。共通helperは起動時cache読込みの参照を確認して残す。
- 維持: `UpdateLr2IrRankingCacheOnStartup` 設定、既存Ir XML、`ir_data`、起動時のローカルcache走査/再集計、順位/オフライン推定、本人IR設定・通信・成績、LR2ローカルスコア。ユーザーファイルやDB既存行を削除する移行は作らない。起動時cache読込みは `BMSLibrary:7104` → serviceの既存cache reloadであり、手動Downloadとは別入口。
- 旧計画から除く: 存続/廃止の再判断、手動task取消/join/staging cleanupの実装案、手動通信成功/部分失敗を維持する受入。廃止する経路を終了寿命へ接続する二重作業はしない。サービス終了の提供情報は実疎通確認済みと書かない。
- 完了条件: 所持/保留/未所持等の通常context menuで項目なし・不要separatorなし。手動owner/通信/専用設定や表示資源の残参照なし。起動時cache XMLと保存済み順位の通常反映、オフライン推定、本人IRは従来どおり。設定と既存ファイル/DB値に便乗変更なし。
- 既存保証の整理: `RankingCacheDownloadWorkflowOwnerTests` と手動専用service/Downloadテストは対応する製品経路の削除に合わせて退役。`MainWindowSelectedChartContextMenuWpfTests` とcomposition testは現行項目への期待を更新。`BmsLibraryIrStartupTests.InitializeStartup_RankingCacheDerivedValuesReachNormalSearchAndLampAggregation`、起動時cache parser/service、`StartupRankingRefreshPolicyTests` を残し、共通保証を失わない。`devdocs/spec/integration/lr2-ranking.md` の手動取得・保存・関連保証表とユーザーマニュアルの該当案内も同単位で整理する。Resources本体・6言語で専用文言を揃えて除く。

## U09 確定文字コードの保持と導入・同期への引継ぎ

### 導入成功先への指定引継ぎ

`ChartFileProjection.WithPath` は譜面pathだけを変更し、保守値のpathを変えないため、導入後の一致検査で確定指定が失効する。成功先のPath・同じHash・Encoding・EncodingFixedだけを未評価の保守値へ渡す小さい共通投影を、ライブの `PackageChartEntry.ApplyInstalledPath` とdetachedな `CreateInstalledChartProjection` の両方へ使う。入力元の健康度・警告無視・配置依存診断をコピーしない。

成功先で健康度・警告を評価し、既存の導入終端の保存へまとめる。導入失敗した対象、資源だけ更新した別所持譜面、同hashの別配置へ指定を広げない。現行の所持再スキャンの確定指定保持へ接続し、新しい由来列や保守対象追加を行わない。

通常導入・推定先導入・自動継続・部分失敗の実配置とDB読戻しで成功対象だけのPath/Hash/Encoding/Fixed/タイトル一致、成功先での評価、後続再スキャン・再起動での保持を確認する。U15が扱う導入serviceの編集完了後に同じwriterを渡す。

### LR2全体同期への確定指定適用

`Lr2SongDbSyncService.CreateSyncSongRow` は常に自動検出し、保存済みBig5等の指定と表示メタデータが食い違い得る。`CaptureLr2SynchronizationInputRowSnapshot` が既に捕捉する不変入力の、同じpath・内容hashに対応した具体的な確定指定を使う。新辞書・永続snapshotは不要。

CP932の基本資源解析を保ち、同じ読取りsnapshotへ `ApplyMetadataEncoding` を適用する。資源名まで指定encodingで読む単純置換は行わない。未指定・未確定は従来の検出、別hashには旧指定を移さない。`CommittedBmsPaths` による確定済みsong行の利用では既存の再読取り・再解析省略を維持し、保存同期署名は変えない。

`BmsLibraryLr2SongDbSyncTests`、`Lr2SongDbSyncServiceTests`、保守系列で同期前後の指定・修正印・タイトル・一覧一致、同path別hash、手動/自動確定、未確定、CP932、BOM、BMSONを分担する。仕様は[譜面読取り](../spec/library/chart-file-reading.md)と[LR2 song DB](../spec/integration/lr2-song-db.md)の対応節を実装と同時に更新する。

## U10 BMS再生入力

### 明示BASE62の音源IDとLNOBJ

`BMSFile` は `#BASE` を扱わず、大文字化で定義件数が減るかで基数を推測する。`#BASE 62`、`#WAVAA normal.wav`、`#LNOBJ aa` のように小文字音源が未定義だとAAとaaを潰し、通常音をLN終端として除く。

明示BASE62では定義・ノート・LNOBJを同じ大小文字区別で扱い、BASEなしの既存推測と受理範囲を保つ。WAV AA/aa双方あり、片方だけ、LNOBJだけ大小が異なる入力で通常音が残り、LNOBJ終端自身は発音しないことを実音声イベントで確認する。分岐・BOMの現行契約と同じparserで整合させ、基本情報側の解析へ便乗変更しない。

## U12 内蔵再生の対象と候補探索

### 現在曲の識別と現在一覧の位置

`BeginPlaybackCore` は開始時の行番号を保存するが、queueは現在の一覧を読む。A再生後に `[A,B]` を `[B,A]` へ並べ替えると、単曲リピートでも古い番号のBを開始し得る。再生中の譜面を対象の正本、一覧の識別を現在位置の照合に使い、番号だけで決めない。

通常行は種類・実pathで区別し、同hash別配置を潰さない。プレイリストは開始した生存 `BMSTableEntry` を既存の非実体化索引で探し、重複譜面行を区別する。現行の選択identity契約を利用するが、絞り込み種別を含む選択scopeを再生へ流用しない。新永続ID・全行実体化は不要。

単曲リピートは現在の同譜面を使う。前後送り・自然終了・先読みは、その時点の一覧で現在対象の位置を照合する。行が消えたら単曲リピートは同曲継続、通常自動送りは終曲で停止、手動送りは無作用。単曲の再ロード失敗は停止し、別曲探索へ意味を変えない。

### FolderSkipとロード失敗を含む有限探索

現行は別フォルダ探索と再生可能行探索が分かれ、`A/1 → 未所持 → A/2 → B/1` で未所持が別フォルダ扱いになりA/2へ進む。また `StartAtIndex` はRepeat時の絶対先頭/末尾のロード失敗を一巡終端に読み替え、次の正常曲を試さず止まる。

次・前・自動送り・先読みを一つの有限候補探索へ揃え、再生可能性とフォルダ条件を同時に判定する。未所持・欠落・非対応・ロード失敗を飛ばしても、送り元は最後に成功した曲の実folderのままにする。同期例外・非同期Ready失敗とも同じ試行上限内で継続し、絶対添字で一周を判断しない。明示Startは指定行を優先し、その失敗後は明示初期行のfolderを当該探索の基準にする。

Repeat ONで再生可能行が同一folderしかなければ一巡後に現在行を一度再開する既存動作を保つ。新しい再帰・再試行台帳・古い先読みの強制採用は不要。

`PlaybackPanelViewModelTests` と `MainWindowPlaybackWpfTests` で並べ替え・現在行が残る絞込み・行消失、同hash別path・重複entryを確認する。次/前/先読み、連続未所持、欠落、非対応、折返し、一件、全同folder、全不可、先頭ロード不良→次正常、自然終了折返し、同期/非同期失敗を分担する。queue変更後は現在条件で判断し、全不可でも有限停止する。既存の方向・プレイヤー捕捉・一時停止seek保証を保持する。

## U13 音声変換の出力名

### 音声出力ベース名の解釈

**現存する。** 本番の `SelectedChartAudioConversionWorkflowOwner`（802–819行付近）は `Path.Combine(saveDirectory, fileName)` を拡張子なしの出力ファイル名として渡す。`BMSAutoPlayWriter.Write`（73–87行付近）は空名をアプリdirectoryへ置換し、DirectoryExistsなら `[artist] title` を配下へ足す。出力先に同名directoryがあるだけで意味が変わる。

- 採用方針: 唯一の本番呼出元のfile path契約へ統合し、未使用のdirectory modeと空名の別意味fallbackを除去する。別mode引数や状態を追加しない。既存encoderの拡張子付与/重名連番処理を使う。
- 完了条件: `Out/Song` directoryがあっても `Out/Song.wav` を生成し、directoryと入力を保全。完成fileが既にある場合も連番をOut直下へ生成。BMS/BMSONで同じ契約。録音形式・品質・正規化は既存保証を維持する。
- 既存保証: `BMSAutoPlayWriterTests.Write_RendersInitialNoteOnceAndIgnoresDeviceVolumeAndMute`、`EmptyRenderFailsBeforeEncoderCreationWithoutChangingExistingOutput`、`OfflineNormalizationUsesWholeRenderRmsAndFixedNoneGain`、`SelectedChartAudioConversionWorkflowOwnerTests` の通常変換・CompleteFile系列。実WAVE生成を中心に追加し、各encoder実体を本件だけのため網羅増設しない。

## U14 重複統合候補

### 重複フォルダ統合の候補表示

- 通常入口・原因: `BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs:339` の `CaptureDuplicateFolderMergeDestinations` は同一パスだけを除外する。`CaptureDuplicateFolderKeyboardAction`（353付近）は元グループの2件で一件へ直接統合、3件以上でメニューを開く。親子も候補に残る一方、`BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs:2256` の `EnsureDirectoryMutationDestinationIsDistinct` は相互包含を拒否し、同ファイル1560から統合の物理準備へ接続される。
- 影響: 実行できない宛先をメニュー・Ctrl+Gから選べる。物理拒否は維持する。
- 方針: 既存のパス比較を使って同一・相互包含を候補から除く。候補ゼロでは操作しない。元グループ件数による直接統合／メニューの分岐を維持し、3件以上から有効候補が1件になっても新しく直接統合しない。
- 境界: 利用者向け動作の追加未決なし。物理前提のうち動的な受付・存在確認まで候補計算へ複写しない。
- 検証: `DuplicateMaintenanceWorkflowOwnerTests.DuplicateFolderInteractionQueries_PreserveDestinationAndKeyboardPolicy` は無関係なA/B/Cのみ。親→子、子→親、有効兄弟、候補ゼロ、有効候補1件だが元件数3件のメニュー維持を同系列へ追加。実物理側拒否は既存導入・統合テストで保持する。
- 単位・依存: この候補変更だけの独立単位。U15の残存整理と同じ導入サービスの共有パス判定を触る場合は直列化し、候補表示のために物理操作や独自正規化を増やさない。対応仕様は `devdocs/spec/library/mutations.md`。

## U15 導入・推定

### 入力範囲と資源を持つ入れ子の導入禁止

同じ宛先の単体A/Bを親directoryのpackageへ再編成すると、譜面項目はA/Bのまま、実導入の再帰列挙だけが子Cまで含む。`delete_parent=false` でも成立し、追加残存整理とは別原因である。宛先一致を理由とする親への昇格、専用候補収集・置換・entry対応を削除し、最初の検出で単体となったものは単体のまま同じ変更セッションへ渡す。フォルダpackageは選定根以下の全内容を相対位置ごと扱い、未参照素材・説明書・`.bms_` 等を欠落させない。

**確定した直接入力の扱い:** ファイルを直接指定した場合も、隣接資源によってフォルダ全体とする従来の検出を維持する。常に1ファイルへ限定する案は採らない。検出時のフォルダ化と、その後の宛先一致だけによる再グループ化を区別し、後者だけを除去する。既存の検出規則全体を追加資料の分類表へ置き換えない。

**確定した入れ子条件:** 各有効譜面directory直下の譜面が定義する実在の音声・BGA・動画を、そのdirectoryの自己領域と照合する。未使用定義も対象。資源の所属は最も近い譜面directoryとし、譜面のない `A/sound/` はA、譜面のある `A/B/` はBであり、B領域の資源をAの根拠へ重複計上しない。SelfOwned件数だけでは未定義媒体・バナーまで数えるため不可。親と子の双方が該当すると入力package全体を重要警告付きで保留にし、行を既存警告色で強調する。

親音源を参照するだけの子譜面、音源だけの子directory、`.bms_`、バナーのみ、未定義の同梱媒体のみでは新しい禁止を加えない。既存の一般的な子譜面警告・音源不足・親参照推定見送りの条件を緩和する意味ではない。新しい禁止がなければ従来の手動導入等の可否へ従う。

発見・復元時に未使用資源定義が解放される前の情報で判定し、元package全体の導入条件として維持する。警告文字列・行色や未所持だけに絞った作業packageから可否を決めない。初段自動、推定後自動、強制新規、指定先導入、全既所持資源上書きと清掃だけの分岐を同じ導入前判定へ接続する。DST指定では解除しない。`UpdateInstallDestination` は保留削除・無効拡張子改名にも使われるため、一律に能力を外して解消操作を塞がない。

検証は実取込み→推定→導入でA/B以外のCのbytes・path・保留・install行が不変で後続導入可能なこと、同/別宛先・追加整理ON/OFF・部分失敗/取消/同名衝突を分担する。入れ子禁止は全導入入口と再起動、親行からの操作、警告色を確認し、正常な別packageを継続する。親共有の子譜面・未使用定義・未定義媒体・バナー・多階層の境界を独立設計で確認する。既存の入れ子自動導入成功期待は、新しい限定拒否条件に一致する部分だけ置換する。

### 重複保留の登録整理

**ユーザー確定:** 通常追加と起動復元で、親子の入力範囲が重複する部分だけを現行のdirectory入力検出へ一度通し、非重複の保留集合へ自動で揃える。重複を理由に導入を止め続ける方式や専用の解除操作は追加しない。単一譜面・実在資源・参照共通率による従来のfolder化を維持し、追加資料の検出規則へ全面置換しない。

現在の通常追加は新しい子を既存親の下として除外し、新しい親の下の既存子は登録解除する方向依存がある。起動復元は同一canonical pathだけを除く。これらを、重複群の最上位根から得た現在の検出結果へ統一する。結果が単体A/Bと子Cなら3件、親一式ならCを含む全譜面を捕捉した親1件とする。親を無条件に優先したり、子の登録だけを除いて譜面一覧から漏らしたりしない。重複しない既存保留は読み直さず、単位と指定を保つ。

完全に読めた重複群だけ、既存 `ApplyInstallTableMutation` と状態反映の入口で複数登録の削除・追加を一括確定する。登録整理自体でファイルを動かさず、入力回収を伴う `RemovePendingPackagesAsync` は呼ばない。起動復元では項目公開前に整理し、自動導入・自動推定を開始しない。

列挙失敗を空の成功へ変える検出や、factoryで再帰取得の `IsComplete` を捨てる経路は改める。既存の取得結果・完全性を分類とpackage作成へ共用し、同じ根を重複読取りしない。読取失敗・取消・DB確定失敗では当該群の元登録と入力を保全し、不完全な結果から導入を始めない。独立した正常群は処理できる。読取問題の解消後は、同じ入力の再投入でも既存パス除外の前に整理を行えるようにし、新しい復旧状態・由来台帳・専用UIを増やさない。

単位不変の保留は導入先・候補・項目内文字コードを保つ。分割・統合された単位は導入先・候補を解除し、通常の推定・手指定で扱う。旧親の宛先を子へ一律コピーしない。同一起動中の同path・同内容の譜面は確定文字コードと復号済み情報を引き継ぎ、再起動復元用の保存は追加しない。旧packageの相対配置・配置依存診断をそのまま別単位へコピーしない。`delete_parent` は同じ単位で保持し、変更単位へ旧値を一律コピー・ORして後片付け範囲を広げない。

通常追加の親先・子先・同時投入とDB復元で同じ集合になること、借用/管理展開先の整理中のbytes・path不変、読取/取消/DB失敗で元登録保持、無関係な保留の不変、単位変更時の文字コード保持と宛先非拡散、問題解消後の通常再投入を確認する。整理後は既存の導入操作へつなぎ、入れ子の限定禁止・別保留の残存保護を迂回しない。`BmsLibraryInitializationInstallTests`、`BmsLibraryPendingPackageRegroupTests`、通常取込み・推定・導入系列を分担する。

### 所持位置からパッケージ根への変換

入力根 `Incoming/Pack`、入力譜面 `sub/old.bms` に対し、所持 `Songs/Song/sub/old.bms` のdirectoryをそのまま宛先にすると、導入時に `sub/sub` となる。入力根から譜面への相対directoryを所持directory末尾の構成と厳密に照合し、その部分だけ除いた候補根を使う。再結合して元の所持directoryへ戻ることと許可範囲を確認する。構成不一致では自動適用せず保留にし、別祖先探索・directory名補正・別方式の再探索を足さない。

単体と根直下は空の相対部分として既存directoryを使う。混在packageの所持hash票と全既所持資源上書き先で共用し、後者の一譜面複数所持先による見送りは維持する。下記の票数は変換後の候補根に対して数える。祖先資源索引・Strong条件緩和・親参照推定の自動許可を追加しない。

実導入で子既所持＋未所持、全既所持資源更新、直下＋子、多階層、複数候補、相対構成不一致を確認し、相対位置が一度だけ付くことを保証する。`BmsLibraryInstallEstimationServiceTests` と通常推定・導入系列を使い、文字列変換だけの確認で終えない。

### 導入先推定のユニークhash票

- 原因: `BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryInstallEstimationService.cs:227` の `TryResolveInstalledDestinationFromPackage` は242から入力entryごとに所持先を引き、249で `MatchedHashCount` を加算する。各hashの所持先重複は除くが入力の同hash重複は除かない。`BMSLibrary.PackageInstall.cs` の最上位所持先による自動DST決定へ届く。
- 影響: Aがhash X、BがY/Zを持つ時、Xの入力コピー3件ならA3対B2、コピー1件ならA1対B2。Strong・音源条件を満たす場合は自動DSTも逆転する。
- 方針: 一要求の候補根ごとに異なる有効primary hashを一票にする。同hashの入力が異なる相対位置にある場合は各配置から候補根を求めて集約し、最初の入力だけで変換しない。同hashが複数候補根へ一致すれば各根へ一票。`MatchedHashCount` も一致したユニークhash数へ揃える。所持索引・hash解決のBMS/BMSON方針を保つ。
- 境界: 次着手対象はユニークhash票と明示されているため意味変更の追加選択は不要。同票は現行の複数候補結果を維持する。
- 検証: `BmsLibraryInstallEstimationServiceTests.TryResolveInstalledDestinationFromPackage_PrefersDirectoryWithMostMatchingCharts:678`、MixedBmsAndBmson、TopTieの系列を拡張。コピー数・名・順の違い、hash欠損、両形式、同hash複数所持先を比較し、`PackageInstallWorkflowOwnerEstimationTests` 等の通常推定でDSTまで確認する。Strong・資源条件を弱めない。
- 単位・依存: 採点と自動適用の小単位。同じU15の残存整理と共通の導入入口だが物理入力保護は別保証。`devdocs/spec/library/install-estimation.md` の正本へ反映する。I/O・永続状態・新索引を増やさない。

### 指定先の既知フォルダ表記

`ValidateInstallDestination` は既知directoryへ大小文字無視で一致しても手入力表記を返す。照合成功時は既知集合の登録表記を `ValidatedDestinationDirectory` へ返し、表示・要求・最終path・保存行を同じ値へ揃える。全path小文字化やLR2 CRCの計算式変更は行わない。

大小文字だけ異なるDST手入力から実導入・song DB読戻しまで通し、既所持とfolder/parent識別値が一致することを確認する。引用符・ファイルpath入力・UNC・ドライブルート・空指定の受理契約は維持する。U03のフルスキャン指定・一時保持と同じ検証サービスを使い、同時編集しない。

## U16 ランプの対象・取得元

### 実フォルダ名 `[NO SONG]` のランプ集計

- 通常入口・原因: `BmsLibraryPlaylistLampDataSource.CaptureCore`（`BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistLampDataSource.cs:79`）は通常表の `folder_list` とentryを供給している。ところが `PlaylistLampModels.cs:865` の順序正規化、`PlaylistLampAggregationService.cs:85,369` の集計、`BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs:493` の行、`PlaylistWorkspaceViewModel.PlaylistLampViewer.cs:273` の遷移、`PlaylistWorkspaceViewModel.DetailSource.cs:91` の全通常フォルダ範囲が文字列 `[NO SONG]` だけで除外する。遷移先には既に `PlaylistFolderNode.IsSpecial` があり、その型条件と重複する名前条件が正当な実フォルダを拒否する。
- 影響: 行・件数・全体ランプ・クリック先の通常一覧から同名の実所属が消える。
- 方針: 特殊ノードは型／既存選択種別で除外し、通常表の保存folder名はそのまま集計・表示・遷移させる。供給に本来特殊ノードがないなら、全階層へ新しいboolを運ぶ設計を増やさず、文字列による誤除外を削除して型境界だけ残す。
- 境界: 受理済み名を禁止・改名しないこと以外に利用者動作の未決なし。
- 検証: `PlaylistLampAggregationTests.cs:198` は通常entryにこの名前を付けて特殊ノード代わりにし、除外を期待している。ここを同名実folderの正しい件数・ランプへ改め、本物の特殊node除外は `PlaylistWorkspaceActionWorkflowTests` へ分担。実folderの行・全体・全通常folderへの遷移、特殊node非対象を確認する。
- 単位・依存: ランプモデルからworkspace詳細ソースまで一単位。言語変更後のランプ表示改善と同じviewerを触るので同責任者または直列化。同じU16の取得元捕捉はデータソース、U17の日付境界はランプ履歴系だが原因・判定は別にする。仕様は `devdocs/spec/playlist/lamp-viewer.md` の正本へ反映する。

### ランプ現在成績と履歴取得元の同一捕捉

- 原因: `BmsLibraryPlaylistLampDataSource.cs:137` がcurrent score snapshotを捕捉した後、223で引数なし `historicalSourceContextFactory()` を呼ぶ。`BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs:4244` の `ResolvePlaylistLampHistoricalScoreSourceContext` は最新設定とactive provider、Beatorajaでは再取得したscore snapshotから履歴DBパスとnotesを作る。`PlaylistLampHistoricalScoreSnapshot.cs:98` の照合はprovider種別だけ。
- 影響: Aの現在成績捕捉→同種BeatorajaのB設定・成績公開→Bの履歴文脈生成の順で、A現在値とB履歴の部分巻戻しが混在する。設定Close禁止などを必要条件にしない通常経路。
- 方針: `BMSLibrary.ScoreSnapshot` が既に持つ `ActiveScoreSource`・`ScoreDbPath`・`SourceGeneration`・`ScoresBySha256` を一回の捕捉値として履歴文脈へ渡す。factoryをその引数から構築する形へ置換し、MainWindowの最新設定と別snapshot再読込みを取り除く。既に完成して公開済みの一組があれば使用し、準備できない場合は未表示／取得不可とする。
- 境界: 利用者動作の追加未決なし。LR2のリンク有効条件を既存snapshotの取得元契約へどう写すかは実装時に確認する。旧スコア保持cacheや自動retryで一貫性を作らない。
- 検証: `BmsLibraryPlaylistLampDataSourceTests.CaptureAsync_recapturesCurrentMembershipOwnershipAndApplicationScore:127` とfactory failure系列、`PlaylistLampHistoricalScoreSnapshotTests` を基礎とし、A/Bの実DBでcurrent捕捉後・history構築前にB公開する順序を制御。A一式またはB一式（未準備ならunavailable）を確認し、Bに履歴があるhash／ないhashの混在を検出する。B公開後の新しい日付要求はB成績・履歴・日付範囲を使う。後発日付要求優先と通常Close終端を維持。
- 単位・依存: data source・factory・文脈とそのテストを一単位。NO SONG改善とdata source、U17の日付境界と履歴スナップショットが重なるため所有調整。本人IRcacheの存廃とは分離する。既存snapshot以上の永続状態・長期lock・追加DB読み直しを導入しない。

## U17 履歴投影・日付境界

### 履歴所属の大小文字

- 通常入口・原因: 表単体／プリセット／FOLDER専用プリセットの表示対象を `BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryDisplayTarget.cs` の索引で投影する。`ResolveMatches:367` がRawHash・Md5・Sha256の一致をまとめた後、375で `Identity` 全体を `OrdinalIgnoreCase` で重複排除する。`PlayHistoryDisplayTargetMatch:454` の識別文字列は表・hash・folderを連結しているため、同表同hashの `A` と `a` が同じ所属になる。
- 影響: 保存所属は残るが履歴ラベルから片方が落ちる。
- 方針: hashの既存の大文字小文字同一性を保ち、folderだけはOrdinalとする所属識別へ揃える。単純に全IdentityをOrdinalへ変える案は、hashが既に正規化されていることを実装で確認できる場合に限る。型付きの小さいキーが必要ならこの投影内部に閉じる。
- 境界: 利用者動作の未決なし。MAX内訳、プリセット名、URL、一般の表名比較へ拡張しない。
- 検証: `BeMusicSeeker.Tests/PlayHistory/PlayHistoryReadModelTests.cs` の `PlayHistoryDisplayTargetIndex_PlaylistTargetUsesPlaylistFolder:1377` 等へ、同表同hashのA/aが各一回、RawHash/Md5二重一致でも同一所属一回、元entry不変を追加。プリセット二種への投影も同じ索引を通す。
- 単位・依存: 履歴投影とそのテスト・`devdocs/spec/playlist/play-history.md` の小単位。同じU17の日付境界も同じテストファイルを編集するので所有または直列編集を調整する。

### 午前0時の夏時間移行

- 原因: `BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryPeriodRequest.cs:156` の `ToUnixSeconds` と `BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampHistoricalScoreSnapshot.cs:550` の `ToUnixSecondsAtLocalMidnight` が、未指定Kindのローカル0時を `TimeZoneInfo.ConvertTimeToUtc` へ直接渡す。日別履歴ツリーは各日requestをまとめて作り、ランプは選択日の翌日境界から巻き戻す。
- 影響: 0時欠落の日は境界変換が失敗し、履歴日付ツリー全体またはランプ集計が失敗する。曖昧な0時を日付の遅い始端とすると早い側の記録を落とす。日本の通常日には同じ原因はない。
- 方針: 小さい日付境界変換を履歴とランプで共用。欠落時は指定日以降の最初の有効境界、重複時は早いUTC境界を使い、隣接日は同じ境界を共有する半開区間とする。固定1時間加算、例外を履歴なしに置換、時刻帯設定やDB形式の変更はしない。
- 境界: 既存資料・コードに早いUTC境界採用を妨げる契約矛盾は見つからない。「0時をそのまま変換」の現行テストは固定UTC/UTC+09の通常日だけ。実装は欠落時間を1時間と仮定しないこと。1日丸ごとの欠落等を許容する一般日付境界として実装できるかを確認し、不要な時刻帯データ保存は加えない。
- 検証: `PlayHistoryReadModelTests.PlayHistoryPeriodRequest_DayRangesUseLocalMidnightInclusiveAndNextMidnightExclusive:71`、`UsesProvidedLocalTimeZoneForDayBoundary:83`、日付ツリー系列と `PlaylistLampHistoricalScoreSnapshotTests.Cutoff_includes_local_midnight_and_same_timestamp_uses_source_id_descending:22` を拡張。固定のカスタム時刻帯で0時欠落・重複、移行前日／当日／通常日、境界前後の漏れ・二重所属なし、ランプ翌日最初の記録の巻戻しを確認。ホスト時刻帯の変更を要求しない。必要なTimeZone注入は短命な引数に閉じる。
- 単位・依存: 共通境界と両利用先・両既存テストを一単位。履歴所属大小文字と同テストを共有し、U16の取得元捕捉とはランプ履歴スナップショット周辺を共有する。現行の日付仕様へ反映し、culture改善と混同しない。

## U20 外部同期の処理済み進捗

### 目的

ステータスバーの外部プレイリスト同期で、対象名を「最後に処理を終了したプレイリスト」として表示し、件数を「処理済み件数」と明示する。最大32件の並列処理において、最後に開始された表や任意の進捗通知が表示名を上書きする曖昧さをなくす。

取得・保存・派生出力の処理結果や実行順序は変更しない。通信待ちによる件数の停滞は、実際に終了した対象が増えていない状態として正確に表示する。

### 表示契約

| 状態 | 主表示 | 補足表示 |
| --- | --- | --- |
| 対象確定直後 | `外部プレイリストの同期 0/471件処理済み` | なし |
| 470件が終了し、1件が未終了 | `外部プレイリストの同期 470/471件処理済み` | `直近の処理終了：縦連コレクション` |
| 全471件の表単位処理が終了 | `外部プレイリストの同期 471/471件処理済み` | 最後に終了した対象名。複合処理の終端までは行を維持 |

- 分母は既存の再取得対象表数を使う。無効なURIなど、既存の対象選択から除かれる表を新たに算入しない。
- 分子は成功件数ではなく、**表ごとの再取得・反映試行が終了した件数**とする。取得・適用に失敗した表も、既存の終了時加算に従って数える。「同期成功」「更新済み」とは表現しない。
- 表示する名前・URIは、最後に**処理終了を通知した対象**のものとする。開始イベントでは更新しない。終了した対象がまだなければ補足表示は空にする。名前が空の場合は既存のURIフォールバックを保つ。
- 完了通知の順序が並列実行で前後しても、同一要求の表示件数が小さい値へ戻らないようにする。すべての中間値を描画することまでは要求しない。件数と対象名は同じ採用済みスナップショットから表示する。
- `471/471` は表単位処理の終了であり、LR2カスタムフォルダ・BMT出力等を含む操作全体の成功を意味しない。行を消す条件は従来どおり複合操作の実終端とする。
- 親行 `[1/5] プレイリストの再読み込み` の段階数・完了条件は変更しない。

### 実装方針と対象

#### 1. 表単位の進捗通知を整理する

対象：`BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs` の `ReloadPlaylistTargetsAsync`。

- 対象確定後の初期通知 `0/総数` は維持する。
- 各表の**開始時**に表名・URIを報告する通知は廃止する。開始を別の進捗件数として扱わない。
- 各表の `finally` で行っている終了件数の加算と、終了した表名・URIを伴う通知を残す。成功・失敗・取消の処理契約や結果の格納位置を動かさない。
- 並列通知の逆転による古い表示を、既存の要求識別と進捗集約の範囲で排除する。通知順序を整えるために外部通信・保存の並列度を落としたり、通知先の処理をモデルのロック下で同期的に待ったりしない。

この入口は自動外部同期と選択プレイリスト再同期で共用されるため、**両方の表名を「直近の処理終了」に統一**する。別入口の `LoadExternalTableSnapshotsAsync` は新規取込み等でも利用されるため、今回は変更しない。

#### 2. ステータスバーの表現を明確化する

主な確認箇所：

- `BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistSync.cs`
- `BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Reload.cs`
- `BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.SyncProgress.cs`
- `BeMusicSeeker/ViewModels/MainWindow/OperationProgressHubViewModel.cs`

`external_playlist_sync` と `playlist_manual_reload` の進捗行だけに、処理済み件数と直近処理終了名が分かる表示書式を適用する。ほかのプレイリスト編集・取込み・出力が使う `CurrentTableName` の意味や共通スナップショットの構造は、原則として変更しない。

表示識別（`Source`、`OperationId`、`Request`、世代・操作トークン）と、同じ要求の汎用通知・専用通知の統合、旧要求・終了通知の除外を維持する。表示を増やすための新しい進捗台帳や表ごとの実行状態は追加しない。

#### 3. 文言・仕様を更新する

- 主表示の「件処理済み」と補足表示の「直近の処理終了」を表示リソース化する。対象言語は `Resources.resx` / `Resources.cs` と `lang/` の既存6言語。既存の一般的な処理名を別用途まで書き換えず、対象行の書式として対応する。
- `devdocs/spec/runtime/statusbar-progress.md` の外部同期行・選択プレイリスト再同期行について、件数と対象名の意味を更新する。
- `devdocs/spec/runtime/progress.md` に追記が必要な場合は、通知の採用と複合終端の意味だけを整合させる。関係のない進捗行や仕様は変更しない。

### 対象外

- HTTPタイムアウト・リトライ・同時実行数・同期対象の選別・取得/保存/出力アルゴリズムの変更
- 通信中の表一覧、成功/失敗の別カウンタ、経過時間タイマー、擬似的な進捗増加
- `471/471` 到達後の「後処理中」という新しい段階表示
- 新規取込み、表URL取込み、プロパティ更新、BMT出力など、別の進捗生産元の表示意味の変更

### テスト担当への引継ぎ観点

具体的な判定基準・テスト配置・テスト作成・既存テストの整理は専用担当に委ね、単位責任者が保証の分担を承認する。少なくとも以下の契約を確認する。

- 並列開始と完了順序が異なっても、表示名は開始対象ではなく直近の処理終了対象になり、件数が後戻りしない。
- 取得失敗も処理済みに算入し、成功表示へ読み替えない。開始直後の `0/N` は表名を持たない。
- 自動外部同期・選択再同期の行識別、ほかの進捗行との併存、取消・失敗・派生出力後の行消去が維持される。
- 多言語リソースが整合し、実ステータスバーでも件数と補足の意味が伝わる。

既存の `BmsPlaylistExternalReloadTests`、`OperationProgressHubViewModelTests`、`MainWindowPlaylistWorkspaceWpfTests` とリソース整合テストを先に調べ、重複する新規テストを増やさない。

## U21 詳細セル保存中の編集開始

`CanBeginDetailEdit` は先行保存中を見ず、AのLEVEL保存中にBのMEMOを編集できる。Aの編集状態解除と遅延整列がBを自動確定し、保存がBusy拒否されると入力欄だけ閉じ得る。実経路の成立は確認済みで、再現検証は実装前の独立設計に含める。

一回の保存・必要表示・後処理の実終端まで次の詳細セル編集開始だけを拒否し、既存ownerの短命な保存状態へ判定を揃える。必要表示より先に受付を解放しない。行選択・再生を妨げず、終端後は明示編集を受け付ける。待ち行列・自動再保存・永続編集状態は追加しない。

Aの保存を制御している間、Bのクリック・F2・文字入力では編集が始まらず、Aの並べ替えを含む終端後はBを保存できることを実表→owner→保存→変更配送で確認する。成功、保存前失敗、保存後の必要処理失敗、取消、無変更で解除を確認する。`MainWindowPlaylistWorkspaceWpfTests`、`PlaylistWorkspacePersistenceCommandTests`、詳細更新系列を分担する。現行の識別選択・全置換前の編集値引渡しを維持し、同じ表担当が所有して選択維持と入力消失を別に判定する。

## U22 standalone外部表の出力名検査

standaloneで表名 `한글`・ASCII tagの外部表を取り込むと、CP932向け `Output_dir` が空になり、`PlaylistExternalSyncOwner` の単表・一括登録でモード判定前に拒否される。配置やプロパティ保存には既にモード別検査があるため、検証機構全体を作り替えず、無条件の空出力名拒否を捕捉済み `OperationModeLR2DB` が有効な場合へ限定する。

アプリ内名・Unicode・JSON保存値を保ち、翻字・仮ASCII名・強制改名を行わない。通常URL/JSONインポートの単独・複数登録と再読込み、LR2モードの既存拒否・配置検査・重複URL/名前・必要出力を確認する。`BmsPlaylistMigrationAndRegistrationTests`、`PlaylistWorkspaceExternalSourceTests` 等へ分担する。U20と同じownerの編集を直列化する。

## U23 BMSルート確定後の導入先選択表示

導入設定ページを一度開いた後、旧導入先Aを検索ルートから外し代替Bを確定すると、保存値はBでも選択が空になる。`SettingsDialogViewModel.Confirmation` が `ConfirmedBMSDirectories` だけを通知し `BMSInstallDir` を通知しないためで、編集中の一般的な通知不足とは分ける。

ルート確定後の候補と確定値の通知を揃え、保存値を実ページの選択へ反映する。先頭候補の補完・別保存領域・設定全体の通知再設計は不要。事前生成したページでA→B、取消、保存失敗、ルート追加だけを確認し、保存値・候補・選択が一致することを保証する。U07設定通知との同時編集を避ける。
