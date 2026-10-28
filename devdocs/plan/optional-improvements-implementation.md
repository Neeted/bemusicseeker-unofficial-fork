# 採用した任意改善の実装計画

## 目的と再開の入口

採用済み改善の未完了範囲を、作業ワークツリーへ割り当てる単位にまとめる。以下は実装予定の契約・受入条件であり、実装済み保証ではない。既存課題は `dev` の `9b1ae8268`、追加課題は `ff9d74a49` までの関連経路と照合している。静的な到達確認と、実再現・検証の成功を区別し、再開時は対象範囲の後続差分だけを確認する。

本書と参照する仕様で再開できるよう、必要な成立条件・方針・検証境界を本文へ取り込む。追加資料の採用項目と見送りの分類は利用者合意済みだが、資料中の設計案を一括承認とは扱わない。見送りは[既知の課題・未採用候補](release-follow-up-candidates.md)に置く。共有URLのBMT、標準偏差0のT-SCORE、履歴空欄の整列、T/Nのdefined、本人IRのクリア降格、初期化失敗後の更新開始は今回着手しない。

文書だけで確定する現行契約は、[外部変更と整合保証](../spec/library/file-db-consistency.md#保証の前提)、[取得元別の履歴集計](../spec/playlist/play-history.md#期間と集計)、[一括外部同期ON](../spec/ui/playlist-properties.md)、[同内容BMS/PMSの詳細識別](../spec/library/chart-info.md#識別と共通詳細値)を正本とし、未実装課題として再計上しない。

## 共通の確定事項と対象外

- `chartInfoParser` は21を維持する。`lr2_song_db_sync_status.signature` の構成・意味・prefix・順序・参照定数は、利用者の事前明示承認なしに変更しない。旧BMSON対応でも版更新・全件強制再解析を加えず、同hash・版21キャッシュの通常再利用を保つ。
- 基本BMS/PMSの資源指示・資源名はCP932、先頭BOMの自動補正なしを維持する。表示メタデータの復号・再生parserのBOM処理と混同しない。所持再スキャンの確定文字コード保持は現行契約を使い、確定済み保持経路の無変更は無書込み、未確定の再検出は従来どおりとする。
- プレイリスト一覧は同じ表／譜面を選択し続け、主行消失時は残存選択の表示順先頭、全消失時は解除する。単列TSVも空欄の行を保つ。再生行消失時は単曲リピートで同じ譜面を継続し、通常自動送りは終曲で停止、手動前後送りは無作用とする。表示側の選択復元を別曲の再生根拠にしない。
- 導入先の編集能力と表示案内、一譜面/保留の出自と一時保持は[一覧](../spec/ui/table-view.md#行のドラッグと編集)と[導入先推定](../spec/library/install-estimation.md#比較用の基本情報と導入先の代表ペア)を正本とする。
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
| U03 共通表の表示とコピー | TSV空行 → URL幅 → 言語cache | 同担当が共通表の列・編集寿命を所有 |
| U04 履歴列とメニュー | hashコピー廃止・MD5列・末尾separator | 共通列・MainWindow。U03後、U05/U08と直列 |
| U05 ツリーのdrop・編集とフォルダ識別 | OS drop・展開 → 空名drop → 所属名空白保持 → 遅延改名失効 | MainWindow・Playlist要求/変更。U04/U08と直列 |
| U08 手動ランキング取得廃止 | UI・専用通信・資料・専用テストを除去 | MainWindow/composition/Ir。U04/U05後、起動時ローカルcache維持 |
| U09 確定文字コードの導入への引継ぎ | 導入成功先への引継ぎ | Maintenance/Catalog/Install。導入serviceはU15との順序調整後 |
| U10 BMS再生入力 | 明示BASE62 | Ribbit BMSFile |
| U15 導入・推定 | 宛先による範囲拡張廃止 → 入れ子禁止 → 重複保留の登録整理 → 候補根変換・ユニークhash票 → DST表記 | PackageInstall/service/estimation。同一writer。候補根と票数は同じ座標へ揃える。登録整理は発見結果/完全性を共用。U09と順序調整 |
| U16 ランプの対象・取得元 | 成績と履歴の同一捕捉 | lamp source/model/viewer |

現在の所有は、`optional-updater` が共通表、`optional-encoding` がU09、`optional-bms-input` が再生入力、`optional-lr2-output` がU15。空いた作業ツリーを最新devから再利用し、U16は全体ルートが共有先の終了に応じて割り当てる。メイン以外最大5本であり、枠を埋めることを目的にしない。

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

## U08 手動ランキング取得廃止

### 手動ランキング更新の廃止と終了寿命

**手動通信の入口・寿命問題は残るが、採用方針は廃止に確定したため新しい終了待機設計は不要。** `MainWindow.xaml:171/244` の「ランキングデータの更新」→ `tableContextMenuItemUpdateRankingDataClick`（7548行付近）→ `MainWindowRankingCacheTerminal` → `RankingCacheDownloadWorkflowOwner.Request` がbackground taskを開始し、`ObserveFault` だけでShell shutdownへjoinしない。

- 採用方針: 両context menuの項目と連続/末尾の不要separatorを除去する。terminal、composition引数/保持、owner/runtime、専用capability、専用UI可否判定を本番参照に従って取り除く。到達しなくなる通信末端も残さない。
- 専用末端の候補: `BMSLibrary.GetIRDataNeedUpdates` / `DownloadIRData` / `IRDataCacheInfo`、`rankingInfoUrl` / `rankingDataUrl`、`BmsLibraryIrClient.GetRankingInfo` / `DownloadRankingData` とinterface、`BmsLibraryIrService.GetIRDataNeedUpdatesFromRankingInfo` / `DownloadRankingCacheFiles` / `PrepareDownloadedRankingCache` / `PromoteDownloadedRankingCache` / `ApplyPreparedDownloadedRankingCache` と専用DTO。共通helperは起動時cache読込みの参照を確認して残す。
- 維持: `UpdateLr2IrRankingCacheOnStartup` 設定、既存Ir XML、`ir_data`、起動時のローカルcache走査/再集計、順位/オフライン推定、本人IR設定・通信・成績、LR2ローカルスコア。ユーザーファイルやDB既存行を削除する移行は作らない。起動時cache読込みは `BMSLibrary:7104` → serviceの既存cache reloadであり、手動Downloadとは別入口。
- 旧計画から除く: 存続/廃止の再判断、手動task取消/join/staging cleanupの実装案、手動通信成功/部分失敗を維持する受入。廃止する経路を終了寿命へ接続する二重作業はしない。サービス終了の提供情報は実疎通確認済みと書かない。
- 完了条件: 所持/保留/未所持等の通常context menuで項目なし・不要separatorなし。手動owner/通信/専用設定や表示資源の残参照なし。起動時cache XMLと保存済み順位の通常反映、オフライン推定、本人IRは従来どおり。設定と既存ファイル/DB値に便乗変更なし。
- 既存保証の整理: `RankingCacheDownloadWorkflowOwnerTests` と手動専用service/Downloadテストは対応する製品経路の削除に合わせて退役。`MainWindowSelectedChartContextMenuWpfTests` とcomposition testは現行項目への期待を更新。`BmsLibraryIrStartupTests.InitializeStartup_RankingCacheDerivedValuesReachNormalSearchAndLampAggregation`、起動時cache parser/service、`StartupRankingRefreshPolicyTests` を残し、共通保証を失わない。`devdocs/spec/integration/lr2-ranking.md` の手動取得・保存・関連保証表とユーザーマニュアルの該当案内も同単位で整理する。Resources本体・6言語で専用文言を揃えて除く。

## U09 確定文字コードの導入への引継ぎ

### 導入成功先への指定引継ぎ

`ChartFileProjection.WithPath` は譜面pathだけを変更し、保守値のpathを変えないため、導入後の一致検査で確定指定が失効する。成功先のPath・同じHash・Encoding・EncodingFixedだけを未評価の保守値へ渡す小さい共通投影を、ライブの `PackageChartEntry.ApplyInstalledPath` とdetachedな `CreateInstalledChartProjection` の両方へ使う。入力元の健康度・警告無視・配置依存診断をコピーしない。

成功先で健康度・警告を評価し、既存の導入終端の保存へまとめる。導入失敗した対象、資源だけ更新した別所持譜面、同hashの別配置へ指定を広げない。現行の所持再スキャンの確定指定保持へ接続し、新しい由来列や保守対象追加を行わない。

通常導入・推定先導入・自動継続・部分失敗の実配置とDB読戻しで成功対象だけのPath/Hash/Encoding/Fixed/タイトル一致、成功先での評価、後続再スキャン・再起動での保持を確認する。U15が扱う導入serviceの編集完了後に同じwriterを渡す。

## U10 BMS再生入力

### 明示BASE62の音源IDとLNOBJ

`BMSFile` は `#BASE` を扱わず、大文字化で定義件数が減るかで基数を推測する。`#BASE 62`、`#WAVAA normal.wav`、`#LNOBJ aa` のように小文字音源が未定義だとAAとaaを潰し、通常音をLN終端として除く。

明示BASE62では定義・ノート・LNOBJを同じ大小文字区別で扱い、BASEなしの既存推測と受理範囲を保つ。WAV AA/aa双方あり、片方だけ、LNOBJだけ大小が異なる入力で通常音が残り、LNOBJ終端自身は発音しないことを実音声イベントで確認する。分岐・BOMの現行契約と同じparserで整合させ、基本情報側の解析へ便乗変更しない。

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

### ランプ現在成績と履歴取得元の同一捕捉

- 原因: `BmsLibraryPlaylistLampDataSource.cs:137` がcurrent score snapshotを捕捉した後、223で引数なし `historicalSourceContextFactory()` を呼ぶ。`BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs:4244` の `ResolvePlaylistLampHistoricalScoreSourceContext` は最新設定とactive provider、Beatorajaでは再取得したscore snapshotから履歴DBパスとnotesを作る。`PlaylistLampHistoricalScoreSnapshot.cs:98` の照合はprovider種別だけ。
- 影響: Aの現在成績捕捉→同種BeatorajaのB設定・成績公開→Bの履歴文脈生成の順で、A現在値とB履歴の部分巻戻しが混在する。設定Close禁止などを必要条件にしない通常経路。
- 方針: `BMSLibrary.ScoreSnapshot` が既に持つ `ActiveScoreSource`・`ScoreDbPath`・`SourceGeneration`・`ScoresBySha256` を一回の捕捉値として履歴文脈へ渡す。factoryをその引数から構築する形へ置換し、MainWindowの最新設定と別snapshot再読込みを取り除く。既に完成して公開済みの一組があれば使用し、準備できない場合は未表示／取得不可とする。
- 境界: 利用者動作の追加未決なし。LR2のリンク有効条件を既存snapshotの取得元契約へどう写すかは実装時に確認する。旧スコア保持cacheや自動retryで一貫性を作らない。
- 検証: `BmsLibraryPlaylistLampDataSourceTests.CaptureAsync_recapturesCurrentMembershipOwnershipAndApplicationScore:127` とfactory failure系列、`PlaylistLampHistoricalScoreSnapshotTests` を基礎とし、A/Bの実DBでcurrent捕捉後・history構築前にB公開する順序を制御。A一式またはB一式（未準備ならunavailable）を確認し、Bに履歴があるhash／ないhashの混在を検出する。B公開後の新しい日付要求はB成績・履歴・日付範囲を使う。後発日付要求優先と通常Close終端を維持。
- 単位・依存: data source・factory・文脈とそのテストを一単位。本人IRcacheの存廃とは分離する。既存snapshot以上の永続状態・長期lock・追加DB読み直しを導入しない。
