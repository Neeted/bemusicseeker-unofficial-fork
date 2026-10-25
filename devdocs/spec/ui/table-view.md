# 一覧の描画・操作・仮想化

## 目的と適用範囲

譜面、プレイリスト詳細、履歴、プレイリスト一覧で使う `CustomTableView` の責務を定めます。列の値は[列と既定値](table-columns.md)、データの正本と更新は[共通モデル](../library/chart-model.md)に集約します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 表示の構成

`CustomTableView` は可視範囲をコードで描画する表です。メインの `customTableView` は `MainChartList.Rows` と `MainChartList.ColumnsSettings`、プレイリスト一覧の `customTablePlaylistSummary` は `PlaylistSummaryView` と専用の列設定を使います。表示先に応じて二つの表を切り替えます。

行の高さは19、見出しは22、文字サイズは11が既定です。外観設定は両方へ反映します。メイン一覧のスコア表示には `SovjetBox` を使います。クリア、DJ LEVEL、難易度、判定、履歴のクリアとBEST DJは `CustomTableTextStyle.Score`、現在の文字サイズ、縦補正 `1d` を共用します。

並べ替え状態は通常一覧の `SortParameters`、履歴の `PlayHistorySortParameters` を `MainTableSortParameters` へ切り替えて公開します。プレイリスト一覧は独立した `PlaylistSummarySortParameters` を使います。

### プレイリストツリーの選択

プレイリストツリーのフォルダ構成更新では、workspaceの選択を正本にします。ItemsSourceの置換中にWPFが選択する親を新しい利用者選択として採用せず、コンテナ生成後に同じ選択revisionの表示だけを同期します。改名・接頭辞変更は確定した対応へ追従し、別フォルダ更新と特殊NO SONGノードの選択を保ちます。同期配送前の新しい明示選択を古い復元で上書きしません。通常の初回選択、再クリックとキーボード選択は従来の要求経路を使います。選択フォルダの削除先は[保存仕様](../playlist/storage-and-export.md#手動編集と失敗)に従います。

通常Selectedと再クリックは、実際に表示された親TreeViewItemのItemsSourceを選択の出自として渡します。モデル確定後も旧表示が残る間のクリックは旧構成の対応へ追従し、新表示からの同名キーの選択へ旧対応を再適用しません。要求の複製・表参照差替えでも出自を保持し、現在のモデル構成へ暗黙に読み替えません。LampViewerも存在確認と同じ公開構成を使います。

### スクロール領域

`Surface` とセル編集面は表の全面を共有し、共通スクロールバーを前面へ重ねます。専用の行・列、固定ガター、ダミー列、不透明な交点は設けません。バーの表示切替で描画領域やセル座標の幅・高さを変えません。見た目と固定16 DIPの入力領域は[外観仕様](appearance.md#スクロールとフォーカス)に従います。

全面の実幅をW、表示列の合計幅をEとします。縦バーが必要なら有効表示幅は `max(0, W − 16)`、不要ならWです。横の最大値は `max(0, E − 有効表示幅)`、横バーの表示条件は `E > 有効表示幅 + 0.5` とし、ViewportSizeとセル露出判定にも同じ有効表示幅を使います。非表示でも算式上の小さい最大値を0へ置き換えません。途中位置で右端のセルが縦バーの背後へ入ることは許容し、最大位置では最右列の右端をバーの左端まで出せます。描画と列境界の座標系は全面の実幅を使います。

縦方向は行単位です。完全表示行容量は本体高（全面の実高−見出し高）から、横バーが必要な場合だけ16 DIPを引き、行高で切り捨てて求めます。極小時も最小一行を保ち、縦最大値は `max(0, 行数 − 完全表示行容量)` とします。縦ViewportSize・最終行への露出判定も同じ容量を使います。描画容量は全面の本体高を行高で切り上げ、部分表示行と既存の購読overscanを維持します。

縦横の要否は毎回バーなしの実寸法から始め、有効寸法に応じて必要な軸を単調に追加し、変化がなくなったところで確定します。前回の表示状態は判定に使いません。行列・行高・寸法の変更で範囲を再計算し、既存位置を範囲内へ収めます。たとえば見出し/行高20、幅100、高さ120では、E90/5行はバーなし、E80/6行は縦のみ、E120/4行は横のみ、E90/6行は両軸（横最大6、縦最大2）、E101/5行は両軸（横最大17、縦最大1）です。

### 保留譜面の操作バー

右ペインの検索・件数表示の下、譜面一覧ヘッダーの上に、保留ルートと個別パッケージで共通の操作バーを表示します。表示区分は `MainChartList.CurrentOperationContext` の `InstallPending` から導出し、プレイリスト概要・プレイログとの重複表示を避けます。対象外では `Collapsed` とし、空白行を残しません。Light／Dark の意味リソースと実行時の言語切替に追随します。

「推定」「インストール」「クリア」は中央一覧の現在選択だけを対象とし、Ctrl／Shiftの複数選択やボタンへのフォーカス移動でも対象を維持します。未選択時は3ボタンを無効にし、差替え・空化・操作後の選択解除にも追随します。実行時にも区分・選択・変更活動を再確認し、左ツリーの選択パッケージへ補完しません。ツールチップとアクセシブル名には各操作の正式名称を使います。

メニュー固有の配置元検査とイベント処理済みの時点を保ったまま、選択対象取得後の実行処理を操作バーと共有します。操作内容は[導入先推定](../library/install-estimation.md)、受付とBusyは[操作の競合ポリシー](../core/operation-concurrency-policy.md)に従います。

### 可視行だけの実体化

`MainChartList.Rows` は `IChartListViewMetadata` を実装する仮想 `IList` を受け取れます。表は全行を列挙せず、件数を `Count`、描画・選択・説明・右クリックの対象を添字で取得します。旧表示の破棄もメタデータを優先し、処理のためだけに全行を作りません。

| 表示先 | 入力と並べ替え | 実体化 |
| --- | --- | --- |
| 通常ライブラリ・絞込み・全件確認 | `ChartListSourceRow` と `ChartListOrder` | `ChartListVirtualView` が対象の `LibraryChartRow` だけを作る |
| 保守・重複・導入済み・導入保留の対象集合 | 対象の `ChartListSourceRow` と共通の並べ替え | 可視行だけを作る |
| プレイリスト詳細 | `PlaylistDetailSourceRow` と専用の `PlaylistDetailSortEngine` | `PlaylistDetailVirtualView` が対象の行だけを作る |
| プレイ履歴 | 履歴専用の入力・並べ替え・表示 | 履歴仕様に従う |
| プレイリスト一覧 | 件数の小さい既存の行集合 | 通常の行集合を保持する。譜面全件の仮想化とは別扱い |

通常一覧とプレイリスト詳細の所持 BMS・bmson 行は、`status` getterだけで再生管理主体の現在値を読取り、元状態の `~PLAYALL` に再生bitを重ねます。種類と実pathの `OrdinalIgnoreCase` 一致時だけ対象となり、保存主体・`ChartFile`・一時投影へ再生状態を載せません。同対象の重複行と後から実体化する行も最新値を返します。再生状態と対象の変化を `MainChartList.RequestDisplayRefresh` へ渡し、Loaded表の表示値キャッシュを再評価します。行の登録・個別再生通知・全件走査・未訪問行の実体化は行わず、Rows・並び順・選択・score・未確定編集・`SCORE_UNSENT`・`SEARCHING` を維持します。時刻進行だけでは表を更新しません。

選択行のTSVコピーや明示的な全行操作は、その対象の実体化を許します。初回表示の代替として常時全件を作ることは許しません。

通常一覧はカタログから所属・現在値・`CatalogRevision` を一組で捕捉し、その読取り入力から絞込み・順序・件数を導出します。所属の正本はカタログに置き、表示は捕捉した不変入力とtokenの順序を参照します。通常BMSONの同値順もカタログ保持順に従い、初回入力だけをpath順へ整列しません。BMS専用表示ではBMSONの入力を準備しません。

通常一覧の行キャッシュは所持tokenをキーとし、共通基本現在値の参照、捕捉した不変参照入力と、詳細・スコア・パッケージの既存版から表示を再評価します。tokenは項目の識別、版は表示キャッシュの有効性であり、相互の代用品ではありません。基本値・path・hashの変更範囲は必要な行更新・依存判定に使い、詳細通知は変更hash、スコア通知は変更キー、保守・警告通知は変更対象を渡します。局所更新のために全sourceをResetし、BMSON全行を同期し、未訪問行を実体化しません。

捕捉入力と、その所属・基準順序に対応する順序を一緒に採用します。件数一致だけで旧添字を再利用しません。部分更新では、writerが確定した `CatalogCommitReceipt.Snapshot` と旧新値を使い、通知の配送時に後続の現在値を取り直しません。token、lookup key、entryから必要な行を得て、実際に依存する検索適否・並び位置・件数だけを更新します。変更していない不変入力と順序は構造共有し、全件長の投影配列やpatch配列を毎回作りません。後から取得する未訪問行も同じ採用入力を使います。

基準順序だけが変わるtokenも、値変更の対象と合わせて旧snapshotの比較規則で順序木から外し、新snapshotへ切り替えてから再挿入します。順序だけで生値やfilterを再投影せず、旧捕捉値と未変更行を保持します。初回BMSON正規化後も、同値列の比較・選択復元と次の局所更新を同じ規則で処理します。

カタログ、独立score、参照プレイリストの変更結果は `RegularChartListOwner` が既存キューから一度取得し、同じbatchを `MainWindowViewModel` の接続からplaylist workspaceの `ApplyLibraryChanges` にも渡します。通常一覧の表示抑止中やプレイリスト表示中でも、必要な構築中要求へ差分を渡します。配送中の状態はこの受渡しの終端まで保持します。正本の確定・版・差分の契約は[データと索引](../core/data-and-indexes.md)、参照索引は[保存仕様](../playlist/storage-and-export.md#参照プレイリストの索引と表示)に従います。

導入済み項目の変更は、現在値適用時に捕捉した旧新基本値とentry・packageの所属参照を通常通知へ渡します。同tokenの複数通知は先行変更も保持し、最後の現在値だけで依存を判定しません。`RegularChartListOwner` と既存の表示更新判断でbatchの全effectsを一度合成します。同じ所属のTitle不変の純移転を、keyword空・mode全て・Title順で表示している場合はpathだけを更新し、Rows・実体行・選択・編集中入力・順序を保持します。path順、依存する検索・モード・Title変更、Reset、所属変更、必要な旧新情報がない場合は正規の最終順序と集合を更新します。

パッケージpathだけの変更で所属集合通知を発行しません。pathと代表TitleのDisplayTitleは既存のObservableObjectから、受付権と排他の解放後に明示公開します。ツリーヘッダーと選択済みpackageは同じ参照の最新値を使います。加入・離脱・剪定・Resetの集合通知、未所属の保留項目の導入状態・警告通知は維持します。後続の警告・保守表示更新でも、現在の並べ替え・絞込みに依存しない導入済み表示を再構築しません。必要な健全性索引の準備は省略しません。

### 並べ替えとキャッシュの有効性

通常一覧の列定義は、正規化した列名、別名、キーの型と取得方法、比較方針、事前計算の優先度、表示データへの依存を持ちます。`STANDARD` の初期可視列で並べ替え可能なものは全て対応し、全列を可視にした場合も詳細専用の `EntryLevelSortKey` 以外は対応します。

文字列は `OrdinalIgnoreCase`、数値は型付きの未定義を許す比較を使います。降順は昇順配列の反転ではなく、対象キーの降順と題名の昇順を組み合わせます。`rank` は `rateDouble` の別名です。同じキーの別名から重複したキャッシュを作りません。

有効性は捕捉入力、要求・表示条件、依存データ、列、方向で判定します。要求の寿命とデータの版を区別し、全件の内容指紋を再計算しません。`CatalogRevision` や表のentry版の変化だけを、全入力・全順序の再構築条件にしません。

重複一覧の再選択では、同版で捕捉した共通現在値を使い、既存の完全順序が列・副キーTitle・同値時の元添字順を満たすことを線形検査してから再利用します。通知配送前の確定値もこの入力から判断し、配送済みであることをcacheの妥当性の代用にしません。正しい順序は共有し、表示中の実差分による局所更新は継続します。

| 依存するデータ | 無効化の根拠 |
| --- | --- |
| 題名・パス等の現在値 | 確定差分のtoken・旧新値と、現在の列・検索・所属条件 |
| スコア | `ScoreSnapshotChange` の版範囲と変更MD5・SHA-256キー、sourceごとの反映済み位置 |
| 譜面情報 | `ChartInfoIndexVersion` と変更MD5・SHA-256キー |
| 保守結果 | 確定した対象currentと、その列・検索への実依存 |
| 警告 | 対象currentと専門警告の事実、警告・導入先条件への実依存 |
| 導入先 | 表示専用の世代 |
| 参照プレイリスト | 参照ownerの確定版範囲・変更MD5/SHA-256 keyと、sourceの処理済み位置 |

スコア・譜面情報・保守結果・警告の部分更新では、変更キーへ依存する入力と必要な並び位置だけを更新します。対象集合ではツリーの種別、集合名と並びの識別情報も使います。初回、明示的なsort/filter変更、実際の全置換では、その範囲に必要な全体評価を行います。

score lookupは所属と索引を共有し、可変な `BMSScore` の全値を版ごとに固定しません。表示の `ScoreSnapshotVersion` は、その位置までの変更キーを全て反映したことを表します。一件の更新で最新lookupを読めても、別キーの未反映分を飛び越えてsource・検索結果・順序を最新版として採用しません。catalogや譜面情報だけの更新でもscoreの位置を進めません。

初回と必要な全評価では、最初のscore投影前に同じlookupと基準版を捕捉します。その基準が覆うのは、通常一覧の選択形式、専用対象集合、詳細の選択表・実folderに対応する元集合全体です。表示filter前の非表示・未訪問入力も同じlookupへ接続し、別のsourceや範囲のcacheまで更新済みと扱いません。通常一覧の未準備scoreは従来どおりNoScoreとし、表示の捕捉だけで構築を開始しません。

参照表示は要求に必要なMD5/SHA-256両keyと参照版を一回の読取りで捕捉し、不変入力として共有します。MD5が現在一致していてもSHAを捕捉し、MD5 bucket消失後のfallbackも同じ入力と差分から求めます。別表のentry変更、name/symbol、参照選択、遅いhydrationも独立した参照差分として適用し、依存する非表示・未訪問入力、検索集合、必要な並び位置だけを更新します。

詳細が保持する参照入力は、同じ確定結果に属する全変更を適用した後の依存集合で整理します。同表移動や依存の引継ぎで途中の依存数が0になっても、最終的に必要なkeyの値を保持します。変更前の影響行が持つMD5/SHA-256だけを最終索引と照合し、本当に最後の依存が消えたkeyを終端で解放します。保持済みの旧不変入力、捕捉済みbucket不在の扱い、MD5の存在する空表示の優先規則は変えず、整理のための再捕捉・版加算・全source走査は行いません。

構築要求は捕捉前から参照差分を受け取り、依存登録前の変更も保持します。新hash依存だけをR版で捕捉した場合は、既存入力へRまでの結果を適用した境界で新依存を追加し、その後の結果も順に適用してから公開します。参照commit境界を潰さず、古い差分で新捕捉値を巻き戻した状態を公開しません。UI内では既存の採用待ち候補と配送から再開し、同じdrainを自分で待ちません。捕捉版と全依存の処理済み位置を分け、最新版への付替え、行ごとの可変索引読取り、欠落を隠す全再構築は行いません。

### 絞込みと画面更新

通常一覧とリソース状態を投影する新規・不足・無視・重複一覧は、入力行・並べ替え・キャッシュ採用より前に `GetResourceHealthIndexSnapshotForView` で索引を準備します。入力行を再利用する場合も準備を省略しません。保留は導入前の一時警告を表示するため、この準備の対象に含めません。警告の状態と表示条件は[警告仕様](../library/warnings.md)を正本とします。

警告・保守の変更を全件通常一覧の再描画だけで反映する場合も、通知前に索引を準備します。並べ替えや絞込みへ影響しない更新では `Rows` を保持します。行の getter やログへ構築処理を移さず、構築済みで同じ入力の索引は再利用します。

通常一覧のフォルダ・文字列・モードの絞込みは、初回と明示条件変更では現在の順序と入力行から求め、以後の部分更新では影響する項目だけを除去・挿入・置換します。同じ順序が有効なら部分集合を再び並べ替えません。全件確認はBMSONを含む通常ルートと同じ集合を使い、フォルダ条件を適用せず、列だけを `FULLSCAN` にします。

現在値、参照、スコア、譜面情報の通常検索と正規表現は入力行から直接評価します。絞込みのための全行実体化は持ちません。件数も同じ採用入力と条件に従わせます。

保守、重複の全体・群・フォルダ・一覧、導入済み・保留の全体・パッケージは対象集合の仮想表示を使います。BMS専用機能はBMSだけ、重複・導入対象はBMSとBMSONを扱います。重複の所属更新はモデルが群全体を置き換えた後に表示を再構築します。共通現在値だけの更新は、検索時の所属と固有警告を保ってカタログ確定差分の対象だけを置換します。再選択時も同版の現在値を捕捉し、検索時の古い表示値を戻しません。詳細は[重複一覧](../library/duplicate-files.md#一覧と操作)に従います。

導入済み・保留の一覧要求は、UIで現在の経路・検索・並べ替え・列とパッケージ所属参照を捕捉し、派生cacheの破棄・要求leaseの取得・詳細source退役の準備までを済ませます。workerは同じleaseを使い、各パッケージの `ChartEntries` が返す固定列を一回取得してから必要なリソース健全性索引の準備、入力投影、既存の順序構築、検索・mode絞込み、folder集計を行います。未発見のパッケージは本来の発見処理を実行し、空の所属や一部成功へ置き換えません。UI所有の集合をworkerから列挙せず、表示準備のために全所持カタログを追加複製しません。初回に必要な健全性索引は全所持規模の準備を許し、同じ入力の索引と順序cacheは既存の条件で再利用します。

採用は同じleaseの既存終端をUIで実行し、古いworkerが後から起動・完了しても新しい表示要求を取り消し・上書きしません。非UI呼出しによるUI入力捕捉の予約にも既存要求IDを持たせ、要求失効ではleaseの発行がない画面切替も識別します。予約後に選択が変わった場合は、新しいleaseの発行・派生cache破棄・詳細source退役を始めず不採用にします。返すTaskは必要な既存配送と実採用までを表し、未待機の呼出しでもfaultを観測します。停止は取消だけで終わらず、開始済みの実workerを回収します。固定待機による再試行とパッケージ単位の例外握り潰しは行いません。

この境界は [`RegularChartListOwner.PackageViews`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.PackageViews.cs) と既存のsubset構築・採用が担当します。[`RegularChartViewBuildAndOrderingTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs) の `PackageViewAsync_PreservesWarningsFiltersOrderAndSubsetCounts` は新規・保留の全体/単一、BMS/BMSON、警告順と検索・mode・folder件数、健全性のcold準備とwarm再利用を確認し、背景所持16/128件で同じpackage投影数を保ちます。`PackageViewAsync_BlockedWorkerKeepsDispatcherResponsiveAndCannotOverwriteNewerView` は実Dispatcherと入力投影gateを使い、worker待機中のUI応答、新要求採用後の旧worker拒否、停止時の実Task回収を確認します。`PackageViewAsync_QueuedUiEntryDoesNotBeginAfterNewSelectionInvalidation` はUI入口の予約自体を保留してから新選択の失効だけを通し、旧予約がlease・投影・詳細退役を始めずRowsと派生cacheを保持することを実Dispatcherで確認します。全カタログの追加複製がなく健全性のcold準備もworker内であることは同じ入口から静的に確認します。

現在の並べ替え・絞込みへ影響しないデータ更新では `Rows`・順序・選択を保ち、影響する実体化済み行を更新してから表示更新を送ります。表の `RefreshDisplay` はセル値のキャッシュを捨てて再描画します。検索適否や並び位置が変わる場合も、影響行の除去・挿入・置換に留め、無関係な行を作り直しません。

導入先の操作結果は、変更状態の反映、実体化済み行のキャッシュ破棄、表の表示更新の順に適用します。パッケージ項目の変更通知の有無に依存して、この手順を省略しません。導入先と推定警告の変更が現在の並べ替え・絞込み・所属へ影響しない場合は、全件確認や対象集合でも `Rows` と選択を保ちます。更新のためだけに未訪問行を実体化せず、既存の入力行や全ライブラリを無条件に作り直しません。

非対応の通常一覧の並べ替え指定には `main_view_virtual_sort_reset` を記録して既定の題名順を使います。仮想表示の構築失敗は `main_view_virtual_required_failed` として扱い、全件実体化へ切り替えて失敗を隠しません。

入力行の準備前に必要なリソース健全性索引を用意します。初回の必要な投影・順序構築と、確定差分からの局所更新を分け、可視・操作対象だけを表示行にします。明示的な全行操作では、上記のとおり対象全体の実体化を許します。

### 画面状態の一括確定

`MainChartList.ApplyRows` は行、列設定、選択、集計文字列の内部状態を一括して確定した後に通知します。別の行集合へ差し替える前に `RowsReplacing` を子の所有者から直接受け、編集と表示キャッシュを準備します。

履歴の差替え準備は鮮度判定のロック外で行い、最後に要求・順序・検索条件・表示先を検証して、機能側の状態と表の状態を同じ区間で確定します。プレイリスト詳細も準備後に `PlaylistDetailBuildState` のロック内で要求版を確認し、入力・表示の識別と世代、表の内部状態を一緒に採用します。ロック内で公開通知を行いません。

プレイリスト詳細は、表ownerが同時捕捉したentry値・entry版と、カタログが同時捕捉した候補・現在値・`CatalogRevision` を入力にします。`PlaylistDetailRow` は共通の `PlaylistDetailSourceRow` を参照し、列値を再コピーして相互同期しません。所持値は生存tokenの現在値、level・folder・URL・comment・memo等の表固有値はentryの捕捉値を使います。未所持・固有値の優先規則は[保存仕様](../playlist/storage-and-export.md)と[共通モデル](../library/chart-model.md)に従います。専門リソース警告は通常一覧と同じ投影を使います。

サマリーは表とentryの所属・読込み状態を保ち、所持hashの存在集合・entry lookup等の実依存だけを更新します。Title・health・memo・URLだけで全表を再集計せず、coldな任意索引を無関係な更新判定のために構築しません。非表示中の更新は次の表示要求へ渡し、全表の先行読込みを行いません。

サマリー要求はshellがDispatcherのBackground優先度へ一回予約し、workspaceの既存延期状態でデータ更新を表示更新より優先します。予約済みの間に届いた要求はまとめ、世代の取消・古い結果の非採用はworkspaceが判断します。起動・全再初期化で `PlaylistTree` を抑制する間だけ延期し、通常一覧や導入ツリーの抑制・再描画だけではサマリーを止めたり全表を再構築したりしません。予約と抑制の判定だけをshellのロック内で行い、行の構築・公開はロック外で実行します。終了時の拒否・予約取消でも予約状態を解放し、新しい構築を開始しません。

表一覧の再読込みは通常一覧だけを抑制し、対象ごとの保存・必須参照反映後にサマリーの現在参照・更新日時・今回のSTATUSを公開します。取得失敗対象は保存済み内容と更新日時を保ち、今回の失敗STATUSへ更新します。他対象の待機中も表示を進めますが、全件の処理・必要出力・通知session・必須UI終端は既存の実Taskで待ち、部分表示で操作全体の完了やBusy解除を前倒ししません。

詳細の局所更新では、変更したentryの入力と順序木の経路だけを置換します。末尾までの位置索引書換や、既実体化行全体の移動用bufferを作りません。編集中の順序キーを保ちながら最新の表示入力とdraftを使い、編集終了後に必要な行だけを再配置します。行・集合の通知は、sourceと表示状態の確定・ロック解放後に公開します。フォルダ編集・削除・dropの正規確定差分で更新済みなら、操作後の全詳細再表示を重ねません。選択folderの改名等で実選択が変わる場合は、追従先を評価します。

詳細の最新スコア適用では、正常に読み込んだ取得元に記録がないことを明示的な空値として採用します。所持行は `NO_PLAY`、未所持行は `NO_SONG` とし、旧クリア・ランク・スコア・達成率・ノート数・BP・コンボ・順位・順位更新日時・偏差値・スコア難易度を消去します。コピー元の `Chart.Score` で旧値を補いません。検索と並べ替えにも同じ空値を使い、編集中はdraft・行・位置を保持して編集終了後に新しい条件へ収束させます。取得失敗による旧値保持は[保存成績の再読込みと旧結果](../library/chart-model.md#保存成績の再読込みと旧結果)に従い、正常な記録なしと区別します。

集計表示の正本は `MainChartList.SummaryText` です。通常一覧とプレイリスト詳細は、初回の順序登録と同時にfolder別件数を求め、以後は対象folderだけを加減算します。空白だけのfolderは除外し、OrdinalIgnoreCaseで数えます。詳細の差分公開と編集終了のflushでも、採用中viewのRowCount/DistinctFolderCountから集計を更新します。編集中に所属変更をdeferしている間は採用中の集合を数えます。件数のためだけの全source再走査と非同期の再集計cacheは持ちません。フォルダ数が1以下なら曲数だけを表示する既存規則を維持します。専用の対象集合は、その表示に必要な範囲を集計します。

#### 古い表示結果を採用しない境界

通常一覧とプレイリスト詳細の構築中要求は、捕捉以降の差分を受け取れる状態にしてから作業を始めます。既知の変更は対象だけを候補へ適用し、採用直前に要求・選択・条件・対象の生存と捕捉版を確認します。カタログが進んでいて根拠の差分がまだ配送されていなければ、候補を採用待ちとして保持し、配送signalで再開します。無関係な版差で全構築をやり直さず、値の版付替え、busy loop、定期照合で補いません。

scoreの未反映範囲を必要とする要求は、その時点で必要な確定位置を捕捉し、`DeliverPendingLibraryChangesAsync` による既存キューの一回の配送完了を待ちます。workspaceには `SetLibraryChangeDelivery` でこのTaskを接続します。正常完了は通常一覧からworkspaceまでの適用完了を表し、必要位置を覆えない成功、拒否、適用失敗、必要な接続の欠如は候補を採用せず終端します。同じ位置で配送を繰り返さず、後続の実確定で必要位置が進んだ場合だけ新たな登録分を待てます。要求の取消・選択交換・終了は待機を終えますが、別要求が使う配送を取り消しません。元score操作の確定と純通知失敗の分類は変えません。

選択や条件が変わった古い要求は不採用にし、採用する候補の入力・順序・画面内部状態を短い区間で揃えます。公開通知はロック解放後です。履歴の鮮度判定は[履歴仕様](../playlist/play-history.md)に従います。

これは破棄可能な表示結果の契約であり、受理済みの保存・通知・必須処理を古い要求として捨てる規則ではありません。

### 事前計算と診断

並べ替えの事前計算は 必須の実UI Task・成功公開・操作解禁の後に明示登録して行う任意の処理です。優先度1は題名・フォルダ・パス・作者、2は主要なスコア・判定・ノート数・BPM・変速・TOTAL・長さ・密度、3は最大コンボを含む残りです。警告と保守の直接参照列は優先度0で起動時には計算しませんが、要求時は仮想並べ替えへ対応します。起動時の対象上限は[ソートキャッシュ準備範囲](../runtime/settings.md#起動時のソートキャッシュ準備範囲)の0～3から選び、列定義の `PrewarmPriority` だけを使います。優先度0/1/2/3はそれぞれ6/4/17/18列で、基本4列の順は `Title`・`Folder`・`path`・`Artist`、その他の優先度内順と各昇降順も維持します。別名列による事前生成対象の重複を作りません。比較規則、キャッシュキー、更新による失効と要求時の生成は準備範囲によって変えません。

可視範囲の描画では[保持範囲とセル描画の再利用](#保持範囲とセル描画の再利用)に従い、保持範囲内の行参照ごとの値と位置別のセル描画を再利用します。`custom_table_render` は初回、遅い描画などを間引いて記録し、全描画の回数ではありません。`table_first_visible` はメイン表の初回可視描画を示します。

`main_view_build` では通常の仮想表示が `sortEngine=virtual`、詳細の専用経路が `isPlaylistDetailView=True` / `sortEngine=fast` です。後者を仮想化の失敗と解釈しません。同じ集合・条件の再表示では入力と順序の再利用、`orderBuildMs=0` を確認できます。`columnMs` は差替え準備・列適用・表示設定の合計であり、列設定だけの時間ではありません。

### 選択とキー操作

通常クリックは単一選択、Ctrlは選択の切替、Shiftは範囲選択です。右クリックは対象行を選択へ含めてメニューを要求します。空白やスクロールバーの操作で選択と現在セルを変えません。`CustomTableSelectionModel` と双方向の `SelectedIndex` で画面状態を同期します。

通常・専用の譜面一覧では、カタログ・score・参照・譜面詳細の確定変更前に、選択集合・主行・Shift基点を譜面識別で捕捉し、最終順序へ復元します。主行の添字が不変でも副選択を旧添字で転用しません。主行だけが消えた場合は残る選択の表示順先頭を主行とし、基点が消えた場合は主行へ寄せ、選択が全件消えた場合は解除します。未選択譜面を代わりに選びません。後続の削除・移動は、この復元済みの実表の選択集合から要求を作ります。

同じ譜面元集合の再評価・ソート・絞込みで `SelectionPolicy.Preserve` を指定した行差替えも同じ規則を使います。明示的な `Reset` は選択を解除します。別区分・重複群・folder・packageなど異なる元集合への差替えでも旧選択と主行を解除し、旧添字を新集合の選択へ転用しません。プレイリストのサマリー・詳細にはこの識別復元を適用しません。通常一覧は所持token、専用一覧はpackage項目・所持token・未所持譜面の形式とpathで識別し、専用一覧の逆引きは既存の初回依存登録と同時に作ります。更新時は選択と基点だけを索引で照合し、非選択行の実体化や選択保持のための全件走査を増やしません。並びに依存しない変更では同じeditorと未確定文字列を保ちます。

上下キーで行を移り、Enterで現在行の操作、F2または文字入力で編集、Ctrl+Aで全選択、Ctrl+Cで現在セル、Ctrl+Shift+Cで選択行のTSVをコピーします。メニューキーでも行メニューを開けます。コピーは `GetEditText` を使い、URLやリンクのアイコン・表示用文字列ではなくURL自体を取得します。

未編集中の修飾なし左右キーは、現在行の表示列順で直前・直後のセルへ移ります。非表示列を飛ばし、現在行に対応する現存列の現在セルがなければ、右は先頭列、左は末尾列から始めます。古い行のセルは基点にせず、列・データの差替えを越えた現在列の復元状態は持ちません。端から外向きの左右は受理しますが、折り返しやスクロールを行いません。端セルが手動スクロールで画面外でも再表示しません。未選択・0行・0表示列は不受理で、現在セルとスクロールを変更しません。

左右では行の選択集合・現在行・`SelectedIndex`・範囲基点を保ち、`SelectionChanged` を送りません。上下・Shift上下は既存の行選択を行い、移動前の列が現存すればその列を引き継ぎ、なければ先頭の表示列へ戻ります。0表示列では現在セルを設定しません。手動スクロールでは現在セルの識別を保ち、次の左右の基点にします。実際に別の列へ移る場合は現在行も可視範囲へ戻します。描画・単一セルコピー・F2/文字入力編集は同じ現在セルを参照します。編集中の左右はTextBoxへ渡し、表の移動・スクロール・編集確定を行いません。スクロールバー由来の左右を奪わず、Ctrl/Shift/Alt付き左右には表の新操作を割り当てません。

列の横可視化は左右と上下で共有します。表示幅以下の列は全面可視なら横位置を保ち、隠れていれば必要最小量で全面表示します。表示幅を超える列へ右方向に進むと左端、左方向に進むと右端を表示します。上下では広い列の一部が見えていれば横位置を保ち、完全に画面右へ隠れていれば左端、画面左へ隠れていれば右端を表示します。横位置は既存スクロールバーの範囲内に収め、未配置の0幅でも非有限値や無効なセルを作りません。可視化後の配置からセルのヒットを作り直し、以前の矩形を流用しません。

コピー時だけ、各セルのUnicode制御文字（U+0000–001F、U+007F–009F）を1文字につきASCII空白1個へ置換します。NUL以降の文字と後続の列・行、通常文字・既存空白・制御文字以外のUnicodeは保持し、null・空のセルは空文字にします。単一セルと選択行のコピーは同じ正規化を使い、TSVの列区切りTAB・行区切りCRLFは保持します。モデル値・一覧表示・DB・元ファイルは変更しません。

見出しのクリックは `SortRequested` を送り、同じ列なら昇順と降順を切り替えます。`SortMemberPath` のない列と状態列は対象外です。対応する見出し上部中央に方向を描画します。見出しの境界で幅、見出しのドラッグで可視列の順序、右クリックで表示列を変更します。状態列は幅と位置を固定します。

### 行のドラッグと編集

セル編集の非同期処理は、保存、必要出力、現在表示への確定値の必要反映、論理編集状態の解除、受付解放の順にします。TextBox等の入力欄を閉じることと保存Taskの完了は区別します。対象はプレイリスト詳細、表一覧の属性、譜面フォルダ名、保留一覧の導入先です。既存の変更配送Taskと局所投影を使い、少数編集の全再構築・全ソート・全行実体化を行いません。表示先の交換後は旧表示への反映を要求せず、開始済み処理とcleanupを回収します。後発のソートや一覧構築、構築候補の後継への完了引継ぎを新しい完了条件にしません。保存後の出力失敗でも確定値の必要表示と編集解除を実行し、先行する元の失敗を通知します。この契約への実装・回帰置換は[リリース前の必須修正](../../plan/release-blocking-fixes.md)で管理します。

セルeditorの文字余白はTextBox.Paddingから内部ホストへ一回だけ反映し、テンプレートのBorder.Paddingへ重ねません。editorと描画Surfaceは全面を維持し、バーのための固定ガターや追加列は作りません。

選択行は `SelectedRowsDataFormat`、表示先は `RowDragKindDataFormat`、開始行は `PrimaryRowDataFormat` へ格納します。通常・詳細・履歴の表は `PlaylistDropCandidateRows`、プレイリスト一覧は `PlaylistSummaryRows` を使います。

プレイリストツリーは種類と全行を再検証します。履歴は `ResolvedChart` を持つ行だけを譜面へ変換でき、未解決や一覧の行が一つでも混じれば全体を拒否します。見かけの行型だけで受け付けません。

プレイリストのセル編集は短命なdraftを保持し、正本entryと表示用入力を先行変更しません。保存時は同じ生存entry・編集属性・入力値をownerへ渡し、確定差分から対象表示を更新します。保存前失敗と確定後の出力失敗の扱いは[保存仕様](../playlist/storage-and-export.md)に従います。編集中の譜面・score変更でdraftを消さず、編集終了後に最新入力へ収束させます。

プレイリスト一覧内の並べ替えは `BMT SORT` 昇順時だけ許可します。画面は可視行と挿入位置を渡し、ViewModelが全プレイリストの保存順を使って非表示行も保持します。挿入位置を線で示し、更新後は添字でなくプレイリストIDで開始行を再選択します。

主な編集項目は `EntryLevel`、`Folder`、`Url1`、`Url2`、`Comment`、`Memo`、`InstallDst` です。URLの通常クリックは取得操作、再クリック・F2・文字入力はURL文字列の編集です。導入先候補は `ChartFile.InstallDestinationSuggestions` から取得し、保留中のBMSも `PackageChartEntry.Chart` を使います。古い保存用モデルの一時値へ代替しません。確定時は `CellEditEnded` から対象のモデル・DBの更新へ接続します。

ローカルプレイリストのURL1/URL2は空文字・空白だけで確定すると、当該欄の保存値と補完値を解除します。非空の無効URIは確定せず、Escapeは入力を保存しません。保存と別属性編集後の補完保持・再適用は[保存仕様](../playlist/storage-and-export.md)に従います。

`INSTL DST` の編集開始時は、候補が1件でも複数でも未選択です。上下キーで候補を選んでEnterを押すと、その候補の生パスを確定します。候補クリックもクリックした生パスを確定します。文字入力・削除は候補選択を解除し、Enterは入力文字列を確定します。Tabも入力文字列を確定し、既存のフォーカス移動を行います。Escapeは1回目で候補一覧を閉じ、2回目で編集を取り消します。候補一覧を閉じてもモデルの候補は消去しません。

空文字をEnterで確定するか、未設定・候補未選択の空の編集欄から外へ移ると、導入先と代表TITLE/ARTISTを空にし、候補・推定WARNINGを保持して「▼ 候補を選択…」へ戻します。設定済みの導入先を空へ編集する場合も同じです。設定済み文字列を変更せず外へ移れば現値を維持します。候補外の有効な手動入力は、候補なしの行と同じパス検証で受け付け、対象群と候補を共通分類器で照合した[実在ペア](../library/install-estimation.md#比較用の基本情報と導入先の代表ペア)のTITLE/ARTISTへ更新し（Relatedは根拠ペア）、候補・推定WARNINGを保持します。検証拒否時は元の状態を保持します。「インストール先をクリア」は導入先・代表情報・候補・推定WARNINGを消去し、保留登録時の元配置健康度とリソース警告を復元します。その他の分類のWARNINGは保持します。[保留の配置評価](../library/warnings.md#リソース状態の評価)に従い、編集・選択では評価し、明示クリアでは再検査せず計算済み結果を使います。設定先ありでも不足の要約を表示し、推定時の音源見送り理由は現在の健康度と独立して保持します。

操作セルは `CellActionRequested` を送ります。URL1/URL2の取得は各段階でHTTP(S)を検証し、不適切な形式なら通信、ファイル作成、ブラウザへの代替、導入へ進みません。詳細は[取得と導入](../playlist/downloads.md)に従います。プレイリストのヘッダー・データの外部同期は別契約です。

### 保持範囲とセル描画の再利用

入力一覧のnull行はその位置に残し、列の値取得処理を呼ばず空セルと通常の行背景・罫線を描きます。

全面の描画行容量（部分行を含む可視行）と上下各5行を、行参照・変更通知購読・表示値・警告・セル描画の共通の保持範囲にします。通知非対応の行も参照を保持し、同一参照が複数位置に現れる場合は最後の参照が範囲から離れるまで値を維持します。範囲の移動では残る行を全失効せず、完全に離れた行の値と描画を破棄します。画面外の行や列の値・描画は先行生成しません。

行参照ごとのエントリが、要求された列の表示値、警告値、表示位置ごとの `DrawingGroup` を所有します。セル単位のFIFO、別の警告辞書、全セルキーの走査による行失効は使いません。文字レイアウトは従来の8192件FIFOを使います。行描画の再利用時は文字レイアウトを再検索しません。

セル群は行ローカルのY=0と通常の行高で記録し、セル背景・現在セル背景・文字と区間色・セル内クリップ・アイコン・チェック・縦罫線を保持します。縦スクロールで値・選択・現在セル・描画環境が変わらなければ、Y方向の配置と `DrawDrawing` だけでセル群を再利用します。同じ行参照が複数位置にある場合も、位置ごとの選択・現在セル状態を混同しません。保持範囲を離れた位置の描画は、同じ行参照が別位置に残っていても破棄します。

行背景・横罫線・挿入線はセル群の外側で毎回配置します。行背景は選択、警告、交互色の順で決め、セル固有背景は非選択行だけに描き、現在セル背景をその上に描きます。通常文字は現在セル、選択、セル通常色の順で決め、色付き区間は選択・現在セルでも区間の色を維持します。最下部の部分行はSurfaceでクリップし、横罫線の完全可視判定は画面座標で行います。部分行として作ったセル群を通常行として再利用しても、文字・背景・縦罫線を欠かしません。

| 更新条件 | 値・警告 | セル描画 |
| --- | --- | --- |
| 縦スクロールで保持範囲に残る行 | 維持する | 同じ表示状態なら配置だけを変える |
| 行の変更通知・編集終了 | 対象行だけを破棄する | 同じ行の全表示位置を作り直す |
| 選択・現在セルの変更 | 維持する | 表示状態が変わった位置だけ作り直す |
| ソース交換・コレクション変更・`RefreshDisplay`・列定義の交換や再構築 | 全行を破棄する | 全行を作り直す。同じ列IDでも旧定義の値を使わない |
| 横位置・幅・列配置・行高の変更 | 維持する。列定義を交換した場合は上記に従う | 現在の可視列だけを作り直す |
| 高さ・見出し高だけの変更 | 継続する保持行は維持する | 行の配置と保持範囲を再計算し、幅・行高が同じならセル群を再利用する |
| 文字サイズ・スコアフォント・DPIの変更 | 維持する | 文字レイアウトとセル群を作り直す |
| `CurrentCulture`・`CurrentUICulture`・テーマの変更 | 整形済みの数値・日付・ブラシも更新する | 文字レイアウトとセル群を作り直す |
| 非表示・購読終了 | 保持行と通知待ちを破棄する | 破棄し、再表示で最新値から作る |

変更通知は既存の通知待ちキューで重複を除き、Dispatcherへ集約します。通常のスクロールで通知待ちを消しません。通知に対応しない行の任意の書換え監視は行わず、コレクション通知・表示更新・保持範囲からの完全離脱・非表示の既存境界で再取得します。

DPIは変更コールバックと描画開始時の環境確認で追従します。パレット・DPI・フォント・文化・文字サイズ適用済みスタイル・横方向の条件は描画回単位で解決します。詳細性能記録が無効なら、セルごとの時刻取得を行いません。初回表示通知の行数・セル数は可視範囲を表し、今回作り直した件数へ置き換えません。文字レイアウトのヒット数は実際の検索だけを数えます。

差替え待ちと空一覧では本文を描かず、旧行のセル群を表示へ戻しません。行単位のスクロール、tooltip・編集・ドラッグなどの入力契約は維持します。横スクロールでは可視行のセル群を作り直し、全列の先行描画や専用のアイコン・Geometryキャッシュは追加しません。

### 外観の更新

コード描画には `CustomTablePalette` がテーマのリソースを描画用の色と線へ変換します。テーマ変更時は描画色とキャッシュを更新します。内蔵スクロールバーも共通の `ScrollBar.*` リソースへ従います。

一覧ヘッダーの上端には `Table.HeaderGridLineBrush` による1 DIPの線を表示幅全体へ描画します。線の中心は上端から0.5 DIPに置き、端で半分が欠けることを避けます。通常一覧・プレイリスト概要とも、保留・プレイログの追加ツールバーや表示列・データ行の有無によらず上枠を表示し、追加ツールバー側には下枠を重ねません。データ行の罫線・行高・スクロール計算は維持します。

## 実装とテストの対応

[`MainWindowPlaylistWorkspaceWpfTests.PlaylistFolderStructure_ActualTreeKeepsWorkspaceSelectionAndNewExplicitSelection`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) は実TreeViewの構造置換・親Selected・改名・接頭辞保存・別フォルダ更新と、復元配送前の明示選択を通し、ツリー・workspace・詳細hashの一致を確認します。選択の判断と削除先の条件差は [`PlaylistWorkspaceActionWorkflowTests.PlaylistFolderMutation_PreservesOrRemapsSelectionAndDetail`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)、新構成の同キー明示選択と旧prefix対応の衝突は同クラスの `PlaylistPropertySave_NewExplicitSelectionDoesNotApplyOldPrefixMap` が担います。表置換と古い要求の拒否は既存の詳細更新・外部表置換系列へ維持します。

同じ実ツリー代表で、prefix確定後・構成通知前の旧ItemsSourceから選択する区間と、新Bindingを適用して同名の別譜面を選択する区間を制御します。通常Selectedはキー操作後も使う確定経路として確認し、再クリックは実PreviewMouseDownから配送します。物理キーとOSフォーカス移動は自動検証の保証に含めません。構成Bindingのtarget転送と、その後の遅延選択同期の終端を待ち、workerの完了や一般的なidleから表示成功を推測しません。

同WPFクラスの `PlaylistPropertyNewDraft_CompiledNameAndSaveCompleteFirstLr2Output` は新規作成のcompiled入力・初回保存・閉鎖から、同じ表への譜面追加、通常詳細編集、フォルダ構成更新までを接続し、選択と編集済み値の維持を確認します。分類・必要出力・保存失敗の条件網羅はworkspaceの既存系列へ分担します。

セルeditorの余白は `CreateCellEditorTemplate` の静的差分と `MainWindowChartPresentationWpfTests` の共通入力位置の検査で確認します。実編集の開始・入力所有・編集中操作は既存 `CustomTableEditingTests` が担当し、Surfaceと有効viewportの保証は以下の既存viewport・navigationケースへ残します。

[`ApplicationCompositionTests.MainWindowCompositionRoutesPlaylistSummaryRefreshThroughShellArbiter`](../../../BeMusicSeeker.Tests/MainWindow/ApplicationCompositionTests.cs) は実Dispatcherで同時要求のデータ優先・一回適用を確認します。実導入記録削除の抑制中にもサマリーを公開し、別一覧の抑制終了でサマリーを再構築しないことを確認します。

[`MainWindowPlaylistWorkspaceWpfTests.ReloadTables_ActualMainWindowPublishesCompletedSummariesWhileAnotherHttpBodyIsPending`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) は、実全体再読込み・DB・HTTP・shell予約・summary buildからcompiled Bindingと可視セルまでを通し、B本文待機中のA成功・C取得失敗とBの未採用、全体Task・P・Busy・進捗・通知終端の保持、解放後の収束と次の明示操作を確認します。実メニュー配送は同クラスの `PlaylistRootReload_UsesOneTypedTerminalAndHandlesBeforeAsyncCompletion`、必須公開失敗後の停止は `MainWindowViewModelStartupProgressTests.ReloadTables_RequiredPublicationFailureRetainsExistingSafetyStop` へ分担します。

[`MainWindowCatalogProjectionTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowCatalogProjectionTests.cs) の `ManualReload_StandardCompositionConvergesNormalDetailAndSummary` は、標準構成の実初期化・走査保存・通知から表示までを通し、二配置の削除、最後の配置の削除、再加入後の通常所属・詳細の再解決・未所持絞込み・サマリーとエントリー保存値を確認します。外部のファイル発見だけを完全な捕捉入力へ置き換えます。

[`RegularChartViewBuildAndOrderingTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs) の `CatalogInput_ExplicitRefreshUsesSameCountReplacementAndCurrentFilters` は同件数の別所属と現在の検索・モード条件、`CatalogInput_BmsonTiesKeepCapturedOrderAndStaleCandidateCannotOverwriteRows` はBMSON同値順と古い要求の不採用を確認します。捕捉後に確定した変更の配送待ちと局所的な採用は、下表の通常一覧・プレイリストの差分テストへ分担します。

`ChartListVirtualViewTests.AdditionalSupportedOrders_MatchExistingDefaultLibraryChartRowSort` は欠損notesを残した全識別子と定義値の数値順・同値タイトル昇順を独立期待で確認する。NULL位置は固定しない。`PlaylistViewPipelineTests.PlaylistDetailPresentationService_MissingChartInfoParticipatesInKeywordAndNumericSort` は欠損を含む4行の数値順・同値集合と数値検索の2件を確認する。通常cacheと保守更新の保証は既存の再利用・metadata変更系列へ分担する。

`PlaylistWorkspaceDetailRefreshTests.RequestDetailRefresh_OwnsRequestThroughRebuildAndTerminalApply` はproducer gateと要求終端を保ち、同条件再利用と情報更新後の最終検索集合を確認する。`PlaylistWorkspacePresentationStateTests.PlaylistWorkspaceSummarySortRequestDrainsOwnerPresentationState` は名称と未完備所持率を併用し、件数順・cache再利用・件数更新後の集合を確認する。`MainWindowChartPresentationWpfTests.MainWindowPendingCellEditUsesDefaultTerminalAndMainChartOwnerRoute` は実列factoryの導入先・playlist Memo・URL空欄をF2/Enterから共通端末へ接続し、URLのEscape取消と詳細編集の開始・終了を確認する。詳細の5属性DB保存、URL1/2の空文字・空白・無効URI・有効置換と補完の条件は[保存仕様](../playlist/storage-and-export.md)へ分担する。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 保持範囲・値と警告の寿命・位置別描画・表示抑止 | `CustomTableRowCache`、`CustomTableRowChangeTracker`、`CustomTableSurface` | [`CustomTablePhase6CacheTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTablePhase6CacheTests.cs) は範囲の重なり・最後の参照の離脱・再進入、通知対応/非対応/重複参照、Reset・ソース/同一ID列交換・非表示復帰、行通知の局所更新、選択/現在セルの区間色と背景、部分行の通常行化と候補マーカーの実字形、null行の空セルと前後行の位置を確認する。保持範囲は実Windowの可視表、色・境界は画面外の実描画で確認する。購読・通知集約は既存RowChangeTracker/Scheduler/Queue系列、編集確定はEditing系列、文字条件とFIFOはTextLayoutCache系列へ分担する。セルループを通らない再利用、描画環境の無効化接続、旧状態の撤去、差替え抑止・初回通知は静的レビューで確認する。 |
| オーバーレイの有効寸法・両軸の誘発・許容差・再計算とクランプ | `CustomTableView.UpdateScrollBars`、容量計算 | [`CustomTableViewportTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableViewportTests.cs) はfloor/ceil/最小一行と、Windowなしの実表で幅100・高さ120の数値例、算定入口でのE100.5/100.5001の境界、両軸からバーなしへの復帰、空・未配置・極小、行列・行高・寸法変更後の範囲を確認する。高さ126では横バー前の完全表示容量4と全面描画容量6を分ける。描画と購読の実接続・overscan維持は静的確認と既存RowChangeTracker系列へ分担する。 |
| オーバーレイのバー上入力と背後の表操作の分離 | `CustomTableView` のPreview右押下・MouseMove、`IsDescendantOfScrollChrome` | バー自身と子孫を同じ除外へ接続し、Preview右押下では背後の`HitTestTable`へ到達せず、表が入力を消費しないことを静的に確認する。hoverは進行中のresize/reorder/rowdrag継続を先に扱い、その後のバー除外でCursorとtooltipを解除して背後のHitTestへ進まないことを確認する。`InputHitTest`がバー自身・子孫を選ぶ入力領域とSource/`OriginalSource`からPreview入口へ至る接続、バー外の既存経路、capture中の操作継続、`HitTestTable`全体へ除外を混ぜないことも静的確認に含める。自動回帰は既存のNavigation・SelectionModel・Editing、Phase6のtooltip内容、MainWindowPlayHistoryのAppsキーメニュー接続、共通バーの標準操作へ分担する。通常のマウス入力から通知・tooltip表示への接続と実capture操作は自動検証の保証に含めない。 |
| バーの手前への最終セルの露出 | `CustomTableView.EnsureColumnVisible` / `EnsureRowVisible` | [`CustomTableNavigationTests.Navigation_RevealsLastColumnAndRowBeforeOverlayBars`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableNavigationTests.cs) は幅100・高さ120・見出し/行高20・二列各60・六行で実キー受付から最後の列右端x84と最終行の完全表示容量4を確認する。選択・編集・購読は既存Navigation/Editing/SelectionModel/RowChangeTracker系列を維持する。 |
| 保留操作バーの表示・現在選択・共通入口・言語とテーマ | [`MainChartListViewModel.IsPendingInstallViewActive`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartListViewModel.cs)、[`MainWindow.xaml`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)、[`MainWindow.cs`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPendingInstallBarWpfTests.cs) の `PendingInstallBar_TracksPresentedSectionAndCollapsesOutsidePending` は実区分確定から配置・排他表示、`PendingInstallBar_UsesCurrentBoundSelectionAndPreservesMultipleRowsAfterFocusRequest` は実Rows Binding・複数選択・クリックから要求と導入完了後の選択plan、`PendingInstallBar_UpdatesLocalizedNamesAndThemeBrushes` は6言語の実切替とLight/Darkの意味brushを確認する。辞書整合は `LocalizationResourceParityTests` に分担する。 |
| 準備優先度の対象列・順序と非実体化 | [`ChartListOrder`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListOrder.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) の `VirtualOrderPrewarmDescriptors_SelectExplicitPriorityColumnsAndRejectInvalidBounds` が明示列集合・優先度内順・0/8/42/78件・各昇降順・別名の重複なし・-1/4拒否、`AllVirtualOrderPrewarmDescriptors_DoNotRequireRowRealization` が全78件の行生成処理の呼出し0を確認する。 |
| 対象外列の要求時ソート・キャッシュ再利用・更新失効 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`RegularChartViewBuildAndOrderingTests.VirtualOrderPrewarm_UnpreparedColumnsRemainDemandSortedAndInvalidated`](../../../BeMusicSeeker.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs) は実際の通常一覧管理主体と小DBを使い、0/1/2/3で対象外の `Title`/`ChartNotes`/`mode`/`level`を昇降順へ要求し、結果・要求列方向のキャッシュ登録と再利用とカタログ更新失効を確認する。0はソート入力行も順序も準備せず最初の通常要求で新規生成になる。比較規則の細部と他一覧のsortは既存系列へ分担する。 |
| 初回BMSON正規化後の表示順・選択と次の局所更新 | [`NormalLibraryViewOrder`](../../../BeMusicSeeker/ViewModels/ChartList/NormalLibraryViewOrder.cs)、[`RegularChartListOwner.CatalogChanges`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.CatalogChanges.cs) | [`RegularChartNormalLibraryRefreshTests.FirstBmsInstall_NormalizesExistingBmsonViewOrderAndKeepsSelection`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.cs) は実BMS導入・DB・配送から順序と選択、旧snapshotと構築中候補、次更新を確認する。参照変更併存時は新hashの捕捉境界後の変更も含める。 |
| 通常・専用一覧の参照捕捉、局所反映、空MD5 bucketとSHA依存 | [`ChartListReferenceInput`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListReferenceInput.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`RegularChartNormalLibraryRefreshTests.References`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.References.cs) は背景16/128件で参照headerの対象投影・選択・未訪問行、初回capture中の確定、MD5存在中にも保持するSHA依存を確認する。 |
| 重複一覧の共通現在値、検索加入/離脱・並び順・健康度・再選択 | [`ChartSubsetReferenceRows`](../../../BeMusicSeeker/ViewModels/ChartList/ChartSubsetReferenceRows.cs)、[`ChartSubsetViewOrder`](../../../BeMusicSeeker/ViewModels/ChartList/ChartSubsetViewOrder.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、[`ChartListOrder.IsSortedFor`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListOrder.cs) | [`RegularChartNormalLibraryRefreshTests.Duplicates`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.Duplicates.cs) は実重複検索から文字コード確定・再調査・配送・再選択を通し、配送前の再選択も含む順序、対象だけの仕事量、両形式の固有警告と未訪問行を確認する。`ChartListVirtualViewTests.CapturedOrder_CurrentValuesRespectDirectionTitleAndStableTies` は昇降順・副キー・同値順・空入力と整列済み値変更を確認する。 |
| 別表の参照更新、hydration、構築中・同条件要求と実配送終端 | [`PlaylistWorkspaceViewModel.DetailChanges`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailChanges.cs)、[`PlaylistDetailSourceRow`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailSourceRow.cs) | [`PlaylistCatalogViewChangeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) の `OtherTableEntryAndHeaderCommits_UpdateReferenceSearchAndSortLocally`、`ReferenceCommit_DuringInitialProjectionIsBufferedBeforeDependencyRegistration`、`ReferenceCommit_SameViewWaitsForActualDeliveryAndCancellationKeepsSharedDrain`、`OtherTableHydration_UpdatesExistingDetailAfterDelayedReferenceCapture`、`NewEntryReferenceCapture_InsertsDependencyAtExactBoundaryBeforeFollowingCommit` は実modelからの参照結果、検索・sort、draft、capture/依存登録の隙間、取消と共有drain、遅いDB hydrationを分担する。通常ownerからworkspaceへの実接続とREF列・SummaryTextのBindingは [`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の既存代表が確認する。 |
| 詳細件数と空白・大小文字、編集終了後の採用集合 | [`PlaylistDetailVirtualView`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailVirtualView.cs)、[`PlaylistWorkspaceViewModel.DetailChanges`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailChanges.cs) | [`PlaylistViewPipelineTests.PlaylistDetailFolders_ExcludeBlankIgnoreCaseAndPublishAdoptedCounts`](../../../BeMusicSeeker.Tests/Playlist/PlaylistViewPipelineTests.cs) はfolder数の境界を確認し、[`PlaylistCatalogViewChangeTests.EntryRemoval_ChangesOnlyAffectedSourcePositions`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) と編集・filter系列は実差分公開後のSummaryTextまで確認する。 |
| 通常一覧の確定差分、依存する順序・絞込み・件数だけの更新 | [`RegularChartListOwner.CatalogChanges`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.CatalogChanges.cs)、[`NormalLibrarySourceRows`](../../../BeMusicSeeker/ViewModels/ChartList/NormalLibrarySourceRows.cs)、[`NormalLibraryViewOrder`](../../../BeMusicSeeker/ViewModels/ChartList/NormalLibraryViewOrder.cs) | [`RegularChartNormalLibraryRefreshTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.cs) の `MaintenanceCommit_ChangesOnlyTargetProjectionAndKeepsUnvisitedRows` は実BMS・SQLiteから保守と警告の無視・解除を通し、背景16/128件で対象投影・確保・実体化の仕事量を確認する。`CatalogMembershipCommits_UpdateCountsAndDistinctFoldersLocally` はBMS/BMSONの所属と絞込み・folder件数、`CatalogCandidate_WaitsForUndeliveredCommitAndResumesOnlyAffectedRows` は実確定後の配送前後で全再構築せずに収束することを確認する。 |
| 詳細の共通入力、逆引きからの局所更新、構築中要求への差分 | [`PlaylistDetailDependencies`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailDependencies.cs)、[`PlaylistWorkspaceViewModel.DetailChanges`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailChanges.cs)、[`PlaylistDetailSourceRow`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailSourceRow.cs) | [`PlaylistCatalogViewChangeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) の `MaintenanceCommit_UpdatesOnlyDependentPlaylistInputs` は実sourceの専門警告・旧入力不変・未訪問行と背景16/128件の仕事量、`MetadataCommit_ReevaluatesHiddenDependenciesAndKeepsEntryValues` は非表示entryの再評価、`BuildingCandidate_WaitsForCommitDeliveryAndResumesWithoutRebuild` は採用前後の配送と同じ構築要求の継続を確認する。entry保存・URL補完との交錯は[保存仕様](../playlist/storage-and-export.md)へ分担する。 |
| 同じ確定結果での参照入力保持・最終依存の解放 | [`PlaylistDetailDependencies`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailDependencies.cs)、[`PlaylistWorkspaceViewModel.DetailReferences`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.DetailReferences.cs) | [`PlaylistCatalogViewChangeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) の `WorkspaceDeleteOrDrop_DoesNotRequestDuplicateDetailRefresh` は同表移動後の新entryでMEMO保存し、参照表示と参照記号だけの検索、DB・件数・未変更行と背景16/128件での局所仕事量を確認する。`EntryDeletion_ReleasesOnlyLastReferenceDependencyAndPreservesOldInput` は別表のowner値を残した2→1→0で保持・解放・旧入力不変を区別し、`MemoSave_WithCapturedAbsentReferenceBucketDoesNotRecapture` はbucket不在を再捕捉しない。 |
| 詳細の局所順序・独立score/詳細・draft・実操作からの二重更新防止 | [`PlaylistDetailVirtualView`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailVirtualView.cs)、[`PlaylistWorkspaceViewModel.Mutations`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)、`PlaylistWorkspaceViewModel.DetailChanges` | `PlaylistCatalogViewChangeTests` の `MetadataSortMove_PreservesRealizedEntriesWithLogarithmicOrderWork` は背景16/128件と全行実体化で実順序訪問・buffer・参照保持・ロック外通知を確認する。`ScoreChanges_PatchDependentEntriesAndDeferPositionUntilDraftEnds`、`ChartInfoChange_PatchesOnlyUnownedDependentEntry` は実score/解析DBからの独立変更とdraft終端、`EntryEdits_UpdateLevelOrderAndMemoFilterLocally` は保存後の順序と検索を確認する。`TitleRegex_EntryMemoCommitsPreserveMembershipAndMetadataChangesReevaluate` は実MEMO保存でTITLE正規表現の集合を保ち、実譜面TITLE確定で同じ式から離脱・再加入することを確認する。同クラスの実Workspace改名・削除・dropは正規receiptからの更新と全再表示の不発、選択folderの改名追従を分担する。 |
| 詳細の正常な記録なしへの更新、全成績消去・検索・編集終端 | [`PlaylistDetailSourceRow.WithEntryChartInfoAndScore`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailSourceRow.cs)、`PlaylistWorkspaceViewModel.PatchDetailKeys` | [`PlaylistCatalogViewChangeTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistCatalogViewChangeTests.cs) の `ScoreReload_MissingRecordClearsScoresAndDefersOrderUntilDraftEnds` と `ScoreReload_MissingRecordUpdatesFilterMembership` は、実SQLiteの記録削除・`InitializeScoresOnly`・通常owner配送から所持/未所持行の全成績消去、draft・行・位置保持と編集終了後のscore順、検索加入・離脱を確認する。通知生成は `ScoreSnapshotPublicationTests`、取得失敗の旧値保持は `BmsLibrarySavedScoreAvailabilityTests`、設定確定から再読込みへの接続は `SettingDialogEditCompletionTests` が分担する。 |
| scoreの連続した反映済み位置、filter前の範囲、実配送の終端 | `RegularChartListOwner`、`PlaylistWorkspaceViewModel.DetailChanges`、`PlaylistWorkspaceViewModel.DetailRequests` | [`RegularChartNormalLibraryRefreshTests.ScoreChanges`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.ScoreChanges.cs) の未準備score・異キーの片起動/逆順・構築途中の確定・実配送失敗系列が通常一覧を検証する。`PlaylistCatalogViewChangeTests` の `ScoreRanges_DelayedDistinctWakeAndSameConditionRequestCoverAllKeys`、`ScoreRanges_UpdateHiddenInputsBeforeFilteringAndCoverActualFolder`、`ScoreRanges_CommittedDuringInitialProjectionAreAppliedBeforeAdoption` は実setterと通常ownerの単一drainを通し、異キー・逆順起動・非表示入力・実folder・構築中確定を確認する。`ScoreRanges_DeliveryTerminalFailureDoesNotAdoptUncoveredSource` と取消・交換系列は実配送Taskと要求終端を区別し、`ScoreRange_OneKeyKeepsBackgroundWorkIndependent` は16/128件で局所仕事量を確認する。専用対象集合の初回基準は `RegularChartViewBuildAndOrderingTests.VirtualSubset_ScoreBaselineCoversOriginalRangeBeforeFilter` に分担する。 |
| 標準構成でのカタログ確定・独立scoreから通常一覧・開いている詳細・Bindingまでの反映 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs)、`RegularChartListOwner`、`PlaylistWorkspaceViewModel` | [`MainWindowPlaylistWorkspaceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `CatalogMaintenance_ActualMainWindowRefreshesOpenPlaylistAndBoundCells` は実Window・BMS/BMSON・SQLiteと正規保守操作を使い、共通配送、実セルの読取り・通知と未訪問行を確認する。同じ構成で実score DBから所有scoreの一件変更を通し、Rows・行参照・選択・未訪問数と対象一件の投影を保ち、版通知や完了通知からの全再表示を重ねないことを確認する。 |
| 左右の現在セル移動、選択維持、不受理、横可視化と上下の列引継ぎ | [`CustomTableView`](../../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs) の `HandleKeyDown`、現在セルと列可視化 | [`CustomTableNavigationTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableNavigationTests.cs) はWindowなしの実表で表示順/非表示・選択通知と範囲基点・端の画面外・方向別開始・古い行・差替え・空入力・修飾入力・0幅・狭列の最小移動・広列の方向別端寄せ/部分可視/完全画面外を確認する。スクロールバー由来の入力は実Preview経路で確認する。行選択の条件詳細と縦容量は既存SelectionModel/Viewport系列へ分担する。 |
| 移動先の編集・コピー・描画への接続 | `CustomTableView` の `currentCellHit`、`TryBeginEditCurrentCell`、`CopyCurrentCellToClipboard`、`IsCurrentCell` | [`CustomTableEditingTests.HorizontalNavigation_EditStartsAtDestinationAndEditingKeepsArrowKeys`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableEditingTests.cs) は実Windowで移動先へのF2/文字入力と編集中左右の不受理・文字列/横位置/確定件数の維持を確認する。現在セルの行・列はNavigationTestsが自動検証し、同じ`currentCellHit`の行/列IDを`BuildCellText`へ渡す接続と描画の`IsCurrentCell`接続は静的確認する。コピー内容は既存ColumnFactory系列へ分担し、OS Clipboardテストは追加しない。MainWindowの両表が同じCustomTableViewを使うBindingも静的確認する。 |
| コピー時のセル制御文字の空白置換、通常文字の保持、TSV区切りと入力値の保持 | [`CustomTableDataTransfer`](../../../BeMusicSeeker/Views/CustomTable/CustomTableDataTransfer.cs) の `BuildCellText`、`NormalizeCellText`、`BuildTsv` | [`CustomTableColumnFactoryTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableColumnFactoryTests.cs) の `CustomTableDataTransfer_BuildsSingleCellTextFromEditTextAndNormalizes` は編集URL、C0・C1の65文字の1対1置換、NUL前後、通常文字・空白・Unicodeと欠損セルを確認する。`CustomTableDataTransfer_BuildsVisibleColumnTsvAndNormalizesCellText` は実列生成の可視列順、NUL以降・後続列・後続行、TAB・CRLFと入力値の保持を確認する。両コピー操作から共通正規化への接続、制御文字がない場合の文字列生成回避、DB等への非影響は静的に確認する。 |
| 導入先候補の未選択開始、明示選択と入力変更、空入力・離脱・取消・Tab | [`CustomTableView`](../../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs) の `HandleKeyDown`、`HandleEditSuggestionClick`、`HandleEditFocusDeparture` | [`CustomTableEditingTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableEditingTests.cs): 候補1・2件の空Enter、非先頭選択・上下キー、選択後の入力・削除、候補クリック、空・無変更離脱、Escape2回とTabの `CellEditEnded` を明示入力で確認する。 |
| 保留行の導入先編集結果の投影と通知、次要求の現在値 | [`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs)、[`LibraryChartRow`](../../../BeMusicSeeker/ViewModels/ChartList/LibraryChartRow.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) の `PackageChartSourceRows_InstallDestinationEditsNotifyCurrentProjectionAndPreserveEstimation`: 既に読み取ったadapterless BMSONの入力行・表示行について、候補Bの確定結果と空編集結果を同期通知で確認し、候補・推定WARNINGと次の保留検索要求への現在値を確認する。編集受付・受渡しは [`RegularChartNavigationTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNavigationTests.cs) の `InstlDstCellEdit_UsesPendingOwnerWithExactChartTargetAndText` に分担する。 |
| 可視行、順序の再利用、依存世代、対象集合 | [`ChartListVirtualView`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListVirtualView.cs)、[`ChartListOrder`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListOrder.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs)、[`ChartListFilterViewModelTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListFilterViewModelTests.cs) |
| 所持 BMS・bmson のstatus読取りと表示キャッシュ再評価 | [`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs)、[`LibraryChartRow`](../../../BeMusicSeeker/ViewModels/ChartList/LibraryChartRow.cs)、[`PlaylistDetailSourceRow`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailSourceRow.cs)、[`PlaylistDetailRow`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailRow.cs)、[`PlaylistDetailVirtualView`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailVirtualView.cs) | [`PlaylistViewPipelineTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistViewPipelineTests.cs) の `PlaylistDetailOwnedRowsReadCurrentPlaybackStatusAndPreserveEdits` は両形式の重複行、状態遷移、スコア・編集値と未所持行を確認し、`PlaylistDetailVirtualView_UnrealizedRowUsesLatestPlaybackStatusWhenCreated` は未実体化中の状態変更後に現在値で行を作ることを確認する。[`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelChartQueueKeepsIdentityStatusAndSingleAdvance` は共通Chartの準備・再生・一時停止・再開・停止、種類/pathの照合と再生以外の状態保持を代表入力で確認する。[`MainWindowPlaybackWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaybackWpfTests.cs) の `PlaybackStateChangesRefreshLoadedTableWithoutReplacingRowsOrCommittingEditor` は実Loaded表のキャッシュ再評価、Rows・順序・選択・未確定編集の維持と時刻のみで更新しないことを確認する。 |
| リソース警告の初回表示・並べ替え・表示のみ更新 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、[`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | [`RegularChartViewBuildAndOrderingTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs) の `ApplyMainLibraryView_PreparesColdResourceHealthBeforeVirtualRows`、`ApplyMainLibraryView_PreparesResourceHealthBeforeWarningSortAndReusesRows`。[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `ResourceHealthMaintenanceNotificationRefreshesRealizedNormalRowBeforeDisplay` は実通知からRowsを維持して要約・詳細を更新する。 |
| 導入先変更時の現在値・行キャッシュと依存する並べ替え | [`LibraryChartRow`](../../../BeMusicSeeker/ViewModels/ChartList/LibraryChartRow.cs)、[`MainViewRefreshDecisionService`](../../../BeMusicSeeker/ViewModels/MainWindow/MainViewRefreshDecisionService.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs) の `VirtualChartSubsetRow_RefreshesRealizedRowAfterTransientStateChanges` は実体化後の明示的クリア、`PackageChartSourceRows_ReadLiveEntryProjectionForPendingInstall` はパッケージ正本の後続変更を確認する。[`LibraryChartRowSortEngineTests`](../../../BeMusicSeeker.Tests/ChartList/LibraryChartRowSortEngineTests.cs) の `MainViewRefreshDecision_RefreshesWhenInstallDestinationUpdateCanAffectCurrentView` は全件確認でも導入先・警告順への依存を区別する。 |
| 一括確定、差替えと選択・集計の保持 | [`MainChartListViewModel`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartListViewModel.cs)、[`PlaylistDetailVirtualView`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistDetailVirtualView.cs) | [`MainWindowChartPresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs)、[`PlaylistWorkspacePresentationStateTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePresentationStateTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) |
| 選択、スクロール、セル値と文字列のキャッシュ | [`CustomTableView`](../../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs)、[`CustomTableSelectionModel`](../../../BeMusicSeeker/Views/CustomTable/CustomTableSelectionModel.cs) | [`CustomTableSelectionModelTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableSelectionModelTests.cs)、[`CustomTableViewportTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableViewportTests.cs)、[`CustomTableRowChangeTrackerTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableRowChangeTrackerTests.cs)、[`CustomTableTextLayoutCacheTests`](../../../BeMusicSeeker.Tests/CustomTable/CustomTableTextLayoutCacheTests.cs) |
| 譜面一覧更新後の選択集合・主行・Shift基点、消失、後続操作対象 | [`ChartListVirtualView`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListVirtualView.cs)、`RegularChartListOwner.CatalogChanges`、`MainChartListViewModel`、`CustomTableView` | [`RegularChartNormalLibraryRefreshTests.Selection`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.Selection.cs) は通常/専用の実catalog・score・参照・解析確定から実表へ配送し、副選択移動、離脱と16/128件の未訪問行を確認します。`CustomTableSelectionModelTests.RestoreIndices_PreservesIndependentAnchorAndClearsMissingSelection` は主行と基点の独立・消失規則を分担します。`MainWindowSelectedChartContextMenuWpfTests.ChartReorderAndRowsReplacement_CompiledMutationRequestsKeepSelectedCharts` はcompiled Binding・行差替え・削除/移動要求・次のShift操作・別区分/元集合の選択解除・明示Resetを確認します。物理削除・移動の成否は既存mutation系列へ分担し、`MainWindowChartPresentationWpfTests.ChartScoreValueChange_KeepsActiveEditorAndUncommittedText` は非依存更新時の実editorを確認します。 |
| 行の受渡しと詳細・履歴の操作 | [`CustomTableDataTransfer`](../../../BeMusicSeeker/Views/CustomTable/CustomTableDataTransfer.cs) | [`MainWindowPlayHistoryWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlayHistoryWpfTests.cs)、[`MainWindowChartPresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) |
| 履歴表示モード別の列設定、見出し、ドラッグ種別 | [`ChartListRefreshCoordinator`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListRefreshCoordinator.cs) | [`MainColumnSettingModeTests`](../../../BeMusicSeeker.Tests/ChartList/MainColumnSettingModeTests.cs) |

導入済み現在値の局所通知は [`PackageLifecycleOwner.CurrentCharts`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageLifecycleOwner.CurrentCharts.cs)、[`NormalLibraryRefreshPublisher`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/NormalLibraryRefreshPublisher.cs)、`RegularChartListOwner` と `MainViewRefreshDecisionService` が担当します。[`OwnedChartCollectionRefreshTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionRefreshTests.cs) は正式適用結果からの先行依存保持、[`RegularChartNormalLibraryRefreshTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.cs) は条件表・一表示要求・Title順の最終表示を確認します。[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の混在純移転は実DB・current・索引・entryから行・選択・編集の保持まで、[`MainWindowTreePresentationWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowTreePresentationWpfTests.cs) はcompiled headerの解放後更新を確認します。

## 関連資料

[列と既定値](table-columns.md)、[検索支援](keyword-search.md)、[外観](appearance.md)、[プレイ履歴](../playlist/play-history.md)、[性能](../core/performance-and-scale.md)を参照します。
