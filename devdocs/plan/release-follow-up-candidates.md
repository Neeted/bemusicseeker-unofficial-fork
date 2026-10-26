# 任意改善

## 目的と扱い

[リリース前の必須修正](release-blocking-fixes.md)と独立して、必要になった時点で採否を判断する未完了事項をまとめる。以下はいずれも今回は実装せず、未実装をリリース阻害要因にしない。着手時に通常入口と既存テストを再確認し、解消済みの課題を再実装しない。

LR2/uBMplay/BMIIDXView2015の試聴・再生固有の問題は、既存の必須項目と今後の新規発見を含め任意改善とする。表出力・BMT・成績連携と内蔵・共通処理は既存分類を維持し、共有修正の効果が外部playerにも及ぶことだけを理由に任意へ移さない。分類は未完了計画の採否を扱い、統合済みコードを戻すものではない。

既存候補に加え、BMTの混在prefixフォルダ名、Updaterの管理fileからdirectoryへの遷移は、利用者が今回は任意へ移すと明示した。推薦表のMEMO引継ぎ、外部変更の通常利用前提、検索提示の言語追従も任意区分を維持する。調査提案の「推奨」を実装承認と扱わない。

追加項目は `dev` の `8c53fc9b` と作業差分を静的に照合した。処理経路・処理量と実再現・性能測定を区別し、本計画の統合で製品テスト・実サービス接続・実アプリ操作を実施したとは扱わない。採否に必要な未決事項は各項目に残す。追加の手動文字コード保持、空名フォルダへのドロップ、履歴ハッシュコピー、再生位置操作、TSV空行、手動順位cache終了寿命、ドラッグ件数表示、早送り/巻戻し終端の8件も利用者が任意に分類した。`5d499a91` まで必要経路を静的に照合し、既存候補と別原因であることを確認した。分類だけで各修正案・受入例を採用契約へ昇格させず、具体的な未決動作は将来の採用条件として残す。

今回の追加資料からは18項目（UI共通整理を含む）を任意候補へ統合する。既存候補は維持し、名称を主に区別する。追加の必要経路は`6e443a87`で静的照合したもので、今回の資料整理による実再現・テスト成功を意味しない。数値・日時の地域非依存化は[必須計画](release-blocking-fixes.md#数値日時の地域非依存契約)の独立した採用契約であり、UI共通整理の採用を待たない。

## 候補と進める条件

| 候補 | 現状の評価 | 次に判断すること | 採用した場合の単位 |
| --- | --- | --- | --- |
| 外部変更後の操作案内 | 再スキャンは取得済み資源索引を使うため、手動配置変更後の手順には補足の価値がある。外部変更への自動追従を増やす根拠にはしない。 | 日英マニュアルへ「リロード完了後に対象譜面を再スキャン」を追記するか。既存UIへの補足は必要性を別に判断する。 | 案内だけの独立した文書コミット。 |
| プロパティの文字コード解釈表示 | 実ファイルの再診断が保存済み文字コードを使わず、一覧と別のタイトルを表示する経路は存在する。診断画面としての現行挙動から、必須不具合とは分ける。 | 保存済み確定指定の適用と、実際の復号方式・由来の表示を一緒に採用するか。 | 診断入力・短命な結果・画面・翻訳・回帰テストを一コミット。 |
| 一覧更新時の選択維持（F01） | 現在は選択位置が残り、別の表・entryへ選択が移り得る。ただし、元資料の例は表示選択と実操作対象の不一致を証明していない。 | サマリー・詳細で識別への追従を採用するか。採用時に主行・基点・消失・明示選択の扱いも確定する。 | 一貫した選択仕様と共通表・更新側の接続を一コミット。 |
| 資源のみ導入後の保守値共用 | 資源配置・索引反映は成功する一方、表示用再評価と保存済み保守値が分かれ、画面間の不足表示が違う経路は存在する。即時更新は必須修正にしない。 | 既存の表示用評価対象に限定した共用で、完了時間の退行を残さず実現できるか。 | 評価・保存・現在値・表示用計算の統合と性能比較を一単位。大きな拡張が必要なら見送る。 |

必須修正の後、UI共通整理を採る場合は筆頭として判断する。既存候補では必要なら案内、文字コード表示、選択仕様の順に採否を判断する。資源保守値の共用は性能評価を伴う別作業として扱う。改善を採用するかどうかと、その実装方法の検討を混同しない。

## UIの状態・入力・寿命を揃える共通整理

任意改善の筆頭とする。個別不具合の追加計上ではなく、既存部品の表示・候補・選択・フォーカス・終端の責務を揃える作業である。共通表の候補編集、設定部品のBinding所有、古い選択通知の失効、非モーダル画面の開始済み処理の終端を生かす。必須の再生フォーカス/ツリー編集は各単位で先に直し、この整理を前提にしない。フォルダ削除は[現行の三択と取消](../spec/library/mutations.md)を維持する。

| 境界 | 採用時に揃える契約 |
| --- | --- |
| 表示・編集・確定 | 表示Bindingは正本、編集候補は一回の編集が所有する。開始時に対象/初期値を捕捉、入力/IMEは候補だけを更新し、確定時は同じ対象/候補を既存入口へ一度渡す。Escapeは候補を破棄し、旧文字列を表示へ再代入しない。対象交換/再利用は旧候補・callbackを捨て、言語/テーマ/純再描画は候補・選択・scrollを保つ。 |
| 部品の確定境界 | 正当なフォーカス離脱確定、一般セルの確定順序、設定の通常closeを保持する。PropertyChanged/LostFocus/Explicitへ一律化しない。Binding維持が必要な内部同期は既存SetCurrentValueの使い方へ揃え、通常DPやローカル値所有設定を一括置換しない。表示へ戻せない候補をAPI置換だけで解決せず、DPラッパーにXAMLから必ず呼ばれる副作用を要求しない。 |
| 選択・識別 | 論理選択を各ListBoxのSelectedItem/SelectedIndexへ投影し、未実体化の候補も生成時に強調/Enter対象を一致させる。入力・caret・文脈・候補版・置換範囲は適用可否、区画/候補種別/保存式または挿入値は候補行、文法比較は保存式の識別と分ける。候補消失は解除し、新永続ID/スキーマや全件実体化を増やさない。 |
| 複合UIの内外 | TextBox、その検索欄所有のPopupと行内ボタンを一操作範囲とする。内側移動は候補・選択を保ち、同Windowの表/別検索欄へ出た時だけ履歴確定・Blurを一度行う。Popupの別ツリーと論理/キーボードfocusを区別し、同じ型/hover/TextBoxだけで所有を判定しない。Esc/非活性は既存終端へ接続する。 |
| 入力と遅延focus | 現在のModifiersを判定元とし、部品内だけでKeyUpを回収するフラグを正本にしない。IME Enter/Escを維持する。遅延復帰は短命要求が有効な間だけ、初回成功・後発明示入力・切離し/所有交換で終える。新しい全画面入力管理器、固定待機の再試行・永続台帳を増やさない。 |
| 表示再適用 | 同じ内容の再通知で候補・行内ボタン・選択・scroll/focusを失わず、owner/入力版/適用可能候補の更新は取りこぼさない。既存表示等価性と列再構築/cache失効を使い、汎用画面差分処理を追加しない。URL幅切替は表示とaction/編集可否を同じ列定義から決める。 |
| 確認結果 | 既定ボタン/Enter先と閉鎖応答を分け、明示Yes/OK（当該ボタンのEnterを含む）のみ承認、Noはその拒否、Cancelのある確認のcloseは取消、表示失敗は未承認とする。情報通知のclose後の通常継続を保つ。選択削除/重複整理はYes=全体、No=譜面のみ、Cancel/X/Esc=先行Yesの有無によらず全操作取消。同期/非同期で意味を揃え、旧close→No続行へ戻さない。 |
| 表示資源 | ResourceService.Resourcesの通知を既存表示ownerで受け、事実/配置/集計/キーから文言・ToolTip・Automationを再生成する。表は既存cache、プロパティは取得済み診断、ランプは同じ区間へ接続する。言語/テーマで解析・検索・集計・保存を再実行せず、新翻訳cache/行ごと購読を追加しない。数値文字列は必須の固定規則、型付きDatePickerは既存DisplayCultureとSelectedDateを保つ。 |
| 表示と処理の寿命 | Loadedは現在表示同期と一回購読、Unloadedは表示購読/予約だけ解除、対象交換は旧候補/callbackを破棄、Closed/Disposeは新規表示/購読を終える。開始済み実処理は既存機能ownerが終端・回収し、Unloadedを最終終了にしない。再接続で入力を二重処理せず旧callbackを新対象へ反映しない。 |

採る場合は同じ検索部品のShift・仮想化選択・部品外離脱・候補行識別を一writerへまとめ、候補引用/保存式比較、表示言語、URL幅、確認翻訳を各責務で合流する。共有API・テスト補助・生成物・仕様に書き込む箇所だけ直列にし、個別の原因/判定は残す。新しい基底クラスを全画面へ配る作業、巨大な共通管理主体、少数変更の全件反映は含めない。

新規/変更UIの機能仕様には表示正本・候補・選択・保存のowner、クリック/キー/IME/確定/取消、Popup/メニュー/子窓を含む内外、対象交換/再利用/同表示/言語変更の保持と破棄、遅延表示と実処理の終端を短く記載する。反映先は[検索](../spec/ui/keyword-search.md)、[一覧](../spec/ui/table-view.md)、[表示](../spec/ui/appearance.md)、[ダイアログ](../spec/ui/dialogs.md)と各機能仕様とし、コード形式だけを検査する新規則・全機能共通テストの複製は設けない。

受入は既存MainWindowChartPresentationWpfTests/MainWindowPlaylistWorkspaceWpfTests/MainWindowPlaybackWpfTests、ThemedMessageBoxTests/DialogPresentationTests、ChartPropertiesWindowTests/PlaylistLampViewerWindowPresentationTestsへ分担する。実Bindingで無変更/変更Escape→同部品再利用→一回確定、未実体化候補のキー選択/Enter、候補B→行内ボタン→入力欄→外部移動、Shift外部解放/IME、後発入力後callback、同表示再通知、三択の明示Yes/No/Cancel/X/Esc、表示変更、URL40→広幅→40、切離し/再接続/処理中閉鎖を通す。静止表示や内部関数だけで保証を代替しない。

## 外部変更の前提と操作案内

根拠は [リロード入口](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、[走査後の資源反映](../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Common.cs)、[再スキャン](../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMaintenanceOwner.cs)。リロードで資源索引を更新しても、譜面本体が不変なら保存済み保守値の全再評価までは行わない。

採用時は[日本語マニュアル](../../docs/manual.ja.md)と[英語マニュアル](../../docs/manual.md)の不足資源追加後の案内を揃え、次の順序を説明する。

1. エクスプローラー等で音源・画像・動画を追加・削除したら、ライブラリの「リロード」を実行する。
2. リロード完了後、対象譜面の「ファイルスキャン」→「再スキャン」で不足状態を更新する。

リロードだけで不足表示も必ず解消するとは説明しない。ファイル監視、自動リロード、再スキャン前の強制全走査、毎回の確認ダイアログは含めない。[利用者向け文書の編集方針](../README.md#利用者向け文書の編集方針)に従い、文書だけなら構文・リンク・UTF-8・LFと差分を検査する。新たな外部変更追従機能のテストは不要である。


通常利用の前提を仕様へ明文化することも任意の文書改善とする。[ファイルとDBの整合性](../spec/library/file-db-consistency.md)は外部変更への包括保護・完全回復を要求しないが、起動中は外部変更を行わないという前提を直接には記していない。採用する場合は保証前提の正本を一箇所更新し、必要な参照を張る。これは上記のリロード・再スキャンという事後の操作案内と同義ではない。

既存の軽量な直前観測、アプリ自身の正常操作による整合性、起動前の変更を読む責務は維持する。既存の非保証を超える利用制限を文言だけで追加しない。新しい監視、強制再走査、mtime修復、hash再計算、再試行は含めず、製品不具合件数として扱わない。文書の構文・参照・UTF-8・LF・差分を確認し、製品テストは追加しない。

## 譜面プロパティの文字コード解釈

[ChartPropertiesViewModel](../../BeMusicSeeker/ViewModels/ChartOperations/ChartPropertiesViewModel.cs) は実ファイル診断へ保存済み指定を渡さず、[ChartResourceDiagnosticsService](../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceDiagnosticsService.cs) はBMSを自動判定で読み直す。BOMなしUTF-8の `#TITLE 漢` は、UTF-8へ修正した一覧では「漢」、プロパティ再読取りではCP932解釈になる場合がある。DB・一覧の修正値自体を失う問題ではない。

採用する場合の方針は次のとおり。

- 開く時点のパス・種類・配置と、保存済み文字コード・確定状態を捕捉する。確定した具体的指定は表示メタデータに使い、それ以外は既存の自動判定を維持する。
- 実ファイル読取りは既存の一回を使い、採用した復号方式と由来を診断結果として返す。表示のための再読取り・再解析、新しいDB列は追加しない。
- 表示メタデータと、CP932で扱うBMS/PMS資源名・定義番号の解釈を分ける。BMSONは既存のUTF-8を維持する。判定アルゴリズムや資源の確認先は変更しない。
- コピー可能な読取り専用欄に「UTF-8（保存済み指定）」「CP932（自動判定）」「CP932（判定未確定）」等を表示する。BOMが指定に優先した場合は実際の採用方式と由来を示す。
- `EncodingFixed` だけで手動指定と断定せず、未保存の操作履歴を推測・永続化しない。`EncodingName` の `?`・`unknown` をそのまま実使用方式と表示しない。
- 読取り完了時にタイトル等と一緒に確定し、閉鎖後の非反映を維持する。表示文言は既定リソースと6言語を揃える。

完了条件は、所持・保留双方の保存済み指定、指定なし、未確定判定、BOM、BMSONで表示と実際の解釈が一致し、従来の資源診断を保つこと。`ChartPropertiesViewModelTests`、`ChartResourceDiagnosticsTests`、`ChartPropertiesWindowTests` を実解析と代表Bindingに分担する。採用時に [プロパティ仕様](../spec/ui/chart-properties.md)を更新する。

## 所持再スキャンでの手動文字コード保持

[保守評価](../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryMaintenanceService.cs)は手動変更を固定指定として保存する一方、選択譜面/全所持の再スキャンでは `forceUpdate` が固定値も再判定へ通す。BOMなしUTF-8を手動指定した後でもEncodingだけが上書きされてタイトルと不整合になり、確定非CP932の判定ではメタデータも再復号され得る。プロパティの再解釈とは異なり、保存値の境界である。

元資料では手動指定保持を方針合意済みとする。具体的な保持対象の識別は採用時に確認する。

採用時は、既存のmaintenance評価で指定の優先と対応メタデータ更新を一緒に整え、資源の再評価を維持する案とする。`EncodingFixed` は自動確定にも使うため、既存データから保持対象をどう識別できるかを確認する。失われた操作履歴を推測せず、新DB列・全件再保存を先行しない。判定器全体・資源名のCP932解釈・実装済みの軽量/詳細ライブラリ側BOM処理や、下記の再生/音声側BOM候補へ広げない。

判定案は `BmsLibraryMaintenanceServiceTests` と現在値/再スキャン系列へ分担し、選択/全所持再スキャン後のDB読戻しで指定とタイトル、資源評価、非対象列の保持を確認する。手動指定と自動確定・未確定/未指定を区別し、無変更時の余分な書込みを増やさない。

## プレイリスト一覧の選択維持方式

[CustomTableView](../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs) の選択は添字を使い、並べ替え後の対象へ自動対応付けしない。右クリックは現在の行をヒットテストし、選択外ならその行へ切り替え、複数選択内なら集合を保って現在行を移す。更新完了後から実行までに追加更新がない例では、クリック行と実操作対象が食い違うとはいえない。

所持・保留一覧の識別復元と、プレイリストのサマリー・詳細の選択位置保持は別の契約である。F01の選択の不意な移動を改善するかは独立して判断する。必須の右クリック現在セル、範囲選択の主行通知、セル編集の完了境界で共通表を変更しても、未採用の識別追従を一律に適用しない。

識別追従を採用する場合は、次を一組で設計する。

- サマリーは `PlaylistId`、詳細は同じ生存 `BMSTableEntry` を使う。変更前に選択集合・現在行・Shiftの基点を捕捉し、最終集合へ一度対応付ける。
- サマリーの内部リスト置換前に捕捉する。詳細の複数変更は、途中の件数で選択を切り詰めず、最終結果へ復元する。
- BMTドラッグの既存のID復元と操作固有の選択意図を同じ反映先へ接続する。view置換でも選択維持だけの全行実体化を行わない。
- 削除・絞込みで消える対象、主行・基点の消失、新しい明示選択の優先を利用者と確定する。所持・保留一覧の消失ルールを、F01への採用なしに流用しない。

完了条件は、並べ替え・ソートキー編集・複数追加削除・view置換後の選択集合と後続操作対象が採用した仕様に一致し、右クリックの選択切替・複数選択内の操作を保つこと。`CustomTableSelectionModelTests`、プレイリスト表示更新テスト、既存MainWindowの実表接続で分担する。[一覧仕様](../spec/ui/table-view.md)と必要なプレイリスト仕様へ契約を反映する。

## 1列TSVコピーの空行

[TSV生成](../../BeMusicSeeker/Views/CustomTable/CustomTableDataTransfer.cs)は出力行数の代わりに `StringBuilder.Length` で改行を決める。TITLE一列だけの実履歴表で空TITLE行→`Song`行を複数コピーすると、先頭空行が消える。空行だけの選択では空文字となり、コピーされない。選択維持や現在セルとは別原因である。

行数・順序を保持する動作は将来の採用時に確定する案とする。採る場合は最初の出力行の管理を直し、0行/0可視列と実在空セルを区別する。選択モデル、列保存、全件実体化、セル値の制御文字正規化・URL取得方式は変更しない。

判定案は `CustomTableColumnFactoryTests` の既存TSV検証と実コピー代表へ分担する。単列の先頭/末尾/複数/全空行、複数列・非表示列・null行除外・制御文字を確認し、例えば空行＋`Song`なら `\r\nSong`、空行2行なら `\r\n` を保つ。採用前にこれらをテスト期待値として固定しない。

## 行ドラッグの件数表示

[共通表のghost生成](../../BeMusicSeeker/Views/CustomTable/CustomTableView.cs)は `1 row` / ` rows` を直接組み立て、日本語等で開始する通常の行ドラッグにも英語を表示する。転送・選択・保存結果の不整合ではない。

採用案は件数文字列だけを既存Resourcesへ接続し、既定資源と6言語の単数/複数・書式引数を揃えるものとする。drag形式、DropEffects、閾値、選択、Adorner寿命・themeは変更せず、ドラッグ中の言語追従用の購読やcacheを追加しない。TSVコピーと同じ共通表でも別原因・別単位として扱う。

判定案は `LocalizationResourceParityTests` と共通表の代表に分担し、1/2/多件、英語単複、言語変更後に開始したghostの実表示を確認する。辞書整合だけで表示の保証を代替しない。

## 未解決履歴行のハッシュコピー

[履歴メニュー状態](../../BeMusicSeeker/ViewModels/PlayHistory/PlayHistoryContextMenuState.cs)はMD5がない場合に、種類を区別せず `RawHash` を使う。未解決beatoraja行では生値がSHA256なので、通常の「MD5をコピー」が64桁のSHA256をClipboardへ渡す。履歴の内訳・選択問題とは別原因である。

MD5不明時はMD5操作を非表示にしSHA256操作を保つ方針は、将来の採用時に確定する案とする。採る場合は既存値のハッシュ種類解決を履歴メニュー内で揃え、LR2の生MD5を使う条件も明示する。新hash計算、ファイル再読込み、DB列・`RawHash` 仕様、SHA256 Web操作は変更しない。

判定案はメニュー状態/actionと `MainWindowPlayHistoryWpfTests` の実表示・実行代表へ分担する。未解決beatorajaのMD5操作とSHA256正値、未解決LR2のMD5、解決済み両hash、行交換後の値を確認する。既存テスト名だけで種類・Clipboardへの値が保証済みとは扱わない。

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

先頭戻し・再開/再起動と試聴専用の数値境界を必須修正から分離し、停止完了後Restartの候補も本節にまとめる。通常入口の静的到達、将来採用時の方針と検証案を保持するもので、製品実装・自動テスト成功・実機再現を意味しない。共通の入力元名衝突と内蔵にも届く反復フォーカスは[必須計画](release-blocking-fixes.md)に残す。必須側の着手に本節の完了を要求しない。

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

[必須の数値・日時統一](release-blocking-fixes.md#数値日時の地域非依存契約)から、外部3playerの試聴専用INI/XMLの数値入口・往復だけを分離する。改善・調査候補であり、未実装・実症状未検証の箇所を一律に不具合と断定しない。

| 専用境界 | 将来採用時の確認起点 |
| --- | --- |
| uBMplayの一時INI Volume | `uBMplay`の試聴用音量書出しと`UbmplaySettingsTests`。既存の値域、無関係値・bytes保全、補完/復元の契約を維持して実INIを観測する。 |
| BMIIDXView2015の一時INI BMSVOLUME | `BMIIDXView2015`の試聴用音量書出しと`ExternalPlayerProcessGatewayTests`の実INI。準備失敗時の旧process/bytes保全も既存保証へ分担する。 |
| LR2 preview XMLの専用数値 | `LR2Config`のwindow size/screenmode/master volume/volumeflagのpreview数値入口とwindow/master往復。`LR2ConfigTests`と`Lr2PreviewOwnerTests`等の既存境界を起点に、試聴五項目と通常値・復元を区別する。 |

LR2Configの共通整数読取りはcustomfolder/titleflash/autoreloadから表・環境設定へ届くため必須側に残す。BMSAutoPlayWriterの通常音声変換BPM、EncoderQualityの精度と録音設定、汎用Web/プログラム名の日時風文字列、BMT/BeatorajaConfig/IRも必須分類を維持する。共有成果を戻さず、専用部分の採否と既存受入の追加範囲だけを別に判断する。

### 停止完了後のLR2先頭戻し

通常の `Start→Pause→Stop(closeProcess:true)` 完了後、[前曲ボタンの単クリック](../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs)は遅延して `RestartCommand` を呼ぶ。保持された旧pathをLR2が再起動する一方、panel表示はNONEのままになる経路を静的に確認した。実機再現は未実施であり、利用者は今回は変更せず別課題として残すと回答した。

将来採用時に停止完了後の先頭戻しを無作用にするか判断する。現在曲のない停止完了と、開始済み読込み・Busy・終了受付を区別し、全操作の新しい禁止や修復状態へ広げない。実LR2 adapterとprocess代替による上記操作順で実開始と停止表示を対にして判定する。

## 一時停止中の再生位置操作

[再生位置sliderのUp/Leave](../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs)は、操作前の状態にかかわらず現在 `IsPaused` なら `StartCommand` のtoggleを追加する。内蔵音声を明示Pauseしてからクリック/ドラッグするとPLAYへ戻る。[音声仕様](../spec/runtime/audio.md)のSeek自体の状態保持だけでは、Viewの再開意図を確定できない。[外部playerの一時停止中Restart](#一時停止中の先頭戻し)とは別操作である。

現行再開を仕様化するか、操作前Pauseを維持するかは未決とし、将来採用時に判断する。Pause保持を採る場合だけ、一回の短命なgestureが自ら開始したPauseの再開を所有する案とする。background commandの完了順と、取消・曲/画面切替後の古いgestureも同じ寿命で扱う。音声engine、外部Restart、永続状態、全再生共通の新状態機械は対象外。

保持案の判定は `PlaybackPanelViewModelTests` の実View slider系列を起点に、明示Pause→クリック/ドラッグ/LeaveのPause保持とPLAY→ドラッグの必要なPause/Resumeを分担する。実Track座標・clamp・Bindingを保ち、旧gestureを次曲へ持ち越さない。採る動作が決まる前に期待結果を確定しない。

## 早送り・巻戻しの終端

[Playback View](../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs)でDown→押したままLeave→戻ってUpを行うと、LeaveとUpがそれぞれEndを送る。`PlaybackPanelViewModel` も方向bit解除済みでadapter Endを呼び、`BMIIDXView2015` のEndはEnterによるPause toggleなので、実playerが再停止しPLAY表示と食い違う。位置sliderの再開意図とは別原因である。

将来の採用案は短命なgestureまたは既存方向状態へ開始/終了の所有を揃え、一押下の終端を一回にする。停止・曲/player交換後の遅いUpが新playerへ作用しないことも同じ境界で扱う。Leaveで終端する既存操作は無断変更せず、BMIIDX Enter無効化・表示だけの補正・全音声queueや巨大共通状態機械は追加しない。位置sliderと同じView/VMを編集する場合は直列とし、原因と判定を分ける。

判定案は `PlaybackPanelViewModelTests` と `ExternalPlayerProcessGatewayTests` へ分担し、両方向のDown→Up、Down→Leave→Enter→Up、出入り反復でStart/End各一回を確認する。実BMIIDX adapterと代替window hostでEnter送信一組を観測し、fake命令列だけで代替しない。内蔵flags/uBMplayキー解放と停止/曲交換後の旧Up無作用を維持する。

## 言語変更後の音声詳細選択肢の表示

[音声設定の表示名](../../BeMusicSeeker/ViewModels/Settings/AudioDeviceTestWorkflowOwner.Presentation.cs)のサンプルレート変換品質・ミキサースレッド数は、構築時の翻訳済み辞書を保持する。言語変更後も再生成・通知されず、初めてページを開く時点でも旧言語が残り得る。設定値と実行結果は変わらないため、今回の必須修正には含めない。

採用時は表示名だけを現文化へ追従させ、キー・保存値・音声処理を変えない。新しい表示cacheは増やさず、既存の資源変更通知へ接続する。既存の音声設定/compiledページ系列で、ページ作成前と表示中の言語変更を分担し、選択キー保持を確認する。

## 重複フォルダ統合の候補表示

重複フォルダ候補表示（D3）は利用者合意済みの任意改善とし、今回は実装しない。[重複操作](../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs)の統合先候補・Ctrl+Gは同じグループの親子フォルダを提示するが、物理処理は包含パスを拒否する。物理側の拒否は維持する。

将来採用する場合は、候補とショートカットを同じ実行可能条件へ揃える。無効な相手を除くためだけに新しい物理操作や独自パス正規化を追加しない。親子の相互拒否、無関係な宛先、候補ゼロを既存の重複操作・物理事前検査へ分担して確認する。

## プレイリストツリーのファイルドロップと展開

ファイルドロップ・遅延ホバー（PL01・PL03）は利用者合意済みの任意改善とし、今回は実装しない。[ツリーのDrop/DragEnter](../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)には次の改善余地がある。

- ツリーのDropはデータ形式の検査前にHandledとし、ファイルを受け付けるWindowへバブルしない。採用時は内部譜面行DnDとOSファイルの受付先を保ったまま、一回だけ適切な入口へ通す。新しい汎用DnD管理を設けない。
- 表見出しへの遅延ホバーは展開状態を反転するため、展開済みの表も閉じる。採用時は未展開の表だけを開き、DragLeave・Drop・取消の既存後始末を保つ。展開状態を新たに永続化しない。

代表WPFは実際の対象と追加結果・開閉を確認し、アニメーション値やHandledだけで合格にしない。ファイル取り込みの条件網羅は既存導入テストへ委ねる。

## 空名プレイリストフォルダへのドロップ

[プレイリストの変更要求生成](../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)は、nodeなしの表ルートと実在する空名nodeを同じ空文字へ縮約する。同期OFF表の「(フォルダ名なし)」子ノードへ譜面をドロップしてもroot分類へ入り、別の一致所属や新規タイトル所属に保存され得る。必須のLR2空名sortとは別の入力・保存経路である。

明示した空所属への直接追加・移動を尊重する動作は未承認の案とし、将来採用時に確定する。採る場合はnode有無と所属文字列を分け、存在検査・移動元除去・追加先を同じroot判定へ揃える。空名拒否・自動改名、DBスキーマ、物理移動、表ルートの分類規則を変更しない。

判定案は `PlaylistWorkspacePersistenceCommandTests` のroot drop系列とMainWindowの実ヒットnode代表へ分担する。所持/同表/別表→空所属、空所属内の再ドロップをDB所属とMEMO/URL/levelで確認し、本当の表見出しdropの分類・採番、通常名、特殊node/同期ON拒否を維持する。

## LR2本人IRキャッシュの存廃とプレイヤー復帰

利用者合意により本人IR処理は廃止候補として扱い、A→B→Aの混入は今回の必須修正へ昇格させない。存廃の決定前に修正も撤去も実装しない。

正常取得応答が得られる前提では、[IR更新](../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)がプレイヤー別metadataのdigest一致を、現在の共通ir_score集合の一致と読み替える。Bの成績へ置換してもAのmetadataは残るため、Aへ戻るとBを返す経路がある。実サービスへの接続成功は確認していない。

存続して修正する場合は、現成績の内容・所有と取得結果を一致させる。新しいプレイヤー別DBや履歴の無条件削除を先行せず、正常空応答、同内容時の無書込み、成績とmetadataの同一トランザクション、失敗時の旧状態保持を維持する。廃止する場合は本人IRの設定・取得・表示・保存資料の責務をまとめて整理し、別の順位キャッシュやLR2ローカルスコアの扱いを便乗変更しない。

## 手動順位キャッシュの存廃と終了寿命

利用者は取得先サービス終了という提供情報を踏まえ任意の廃止候補に分類した。実サービスの疎通は未確認で、現在の成功通信や一時物残留を実証したとは扱わない。本人IRキャッシュの存廃とは別機能であり、一括撤去しない。存続/廃止と既存cacheの保持・削除・利用は将来の採用時に決め、廃止候補への分類を削除承認としない。

LR2スコア元・本人ID、更新対象と既存Irフォルダがあり、取得先の応答が得られる前提では、通常の順位キャッシュ更新から [RankingCacheDownloadWorkflowOwner](../../BeMusicSeeker/ViewModels/Startup/RankingCacheDownloadWorkflowOwner.cs) が起動するTaskはfault観測だけで、取消/待機契約と [Shell終了](../../BeMusicSeeker/ViewModels/MainWindow/ShellShutdownWorkflowOwner.cs)のjoin対象に接続されない。XML取得はDB/IRロック前なのでロック待機でも代替できず、終了先行時には未反映cacheと `%TEMP%/BeMusicSeeker_RankingCache_<GUID>` が残り得る。

存続採用時の案は、既存ownerが実通信Taskの受付・取消・終端とstaging cleanupを所有し、本文取得まで取消を伝えてShell終了へ接続するものとする。終了後の適用/新規通知を防ぎ、時間切れでTaskを捨てる方式、L/P占有だけの代替、恒久queue・再取得台帳は追加しない。temp管理への接続だけで通信寿命を解決したと扱わない。DB破損・既存score消失は本件影響と断定しない。

存続案の判定は `RankingCacheDownloadWorkflowOwnerTests`、`ShellShutdownWorkflowOwnerTests`、`TempDirectoryPublisherTests` へ分担する。制御可能な未完了通信/本文/cleanupを使い、一覧待ち・承認直後・XML途中・適用境界の終了で受理済みTaskの収束とstaging削除、終了開始後の新規受付なし、取消後の適用/通知なしを確認し、成功・部分失敗・確認Cancelを維持する。廃止採用時は当該UI・取得・関連設定/資料と既存cacheの扱いをまとめて整理し、本人IRやLR2ローカルスコアへ便乗変更しない。

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

## 言語変更後の検索提示

警告が出る検索式を表示したまま言語を変えると、`ChartListFilterViewModel` の保存済み警告とヘルプBindingが旧言語のまま残る。資源側は変更通知を出すが、検索ownerは入力・文脈変更時にしか提示を更新しない。検索結果や保存値の破損ではないため、今回の必須修正には含めない。

採用時は既存の提示再生成・再通知を言語変更へ接続し、購読と解除の所有を明確にする。検索の再実行、式の書換え、履歴・選択の変更、翻訳cache追加は不要。音声詳細選択肢と似た症状でも所有者が異なるため一括で作り直さない。`ChartListFilterViewModelTests` と代表WPFで入力・文脈固定のまま警告とヘルプが新言語になり、検索実行イベントが増えないことを確認する。

## BMS条件分岐の未選択枝の混入

RibbitのParseMainは再帰/読み飛ばし後にも古いローカルtextで終端を判定し、固定乱数1のIF 1/ELSEやIF 2/ELSEIF 3/ELSEで未選択イベントも処理する。内蔵再生・先読み・音声変換は共通PlaybackChart.Loadから同結果を受ける。

採用時は再帰後の実命令を基準に継続/読み飛ばしを決め、古いtextとの二重管理を整理する。全parserを作り直さず、必須の数値規則と同parserの書込みを直列にする。RibbitBmsFileTimingTestsで真偽IF/ELSEIF、ELSE、入れ子を固定乱数で判定し、選択枝のイベント一回・未選択の資源定義非混入を確認する。共通再生データへの投影を代表一例で接続する。

## 旧BMSONのライブラリ情報解析

再生側が受ける0.21系initBPM/soundChannelをライブラリBmsonJsonParserは拾わず、BPM0・音源0件として健康度/導入へ渡し得る。[再生の対応範囲](../spec/library/bmson-playback.md)を情報解析へ広げる任意対応であり、現行仕様違反と一括断定しない。

採用時はJSON境界で旧キーを正規化し、旧新キー共存時の優先を明示する。再生timingの無条件流用はせず、詳細結果の変更は既存parserVersionによる通常再解析へ接続する。同リリースの解析変更と版更新をまとめ、新移行台帳/別強制再解析は追加しない。現行/旧同義入力のBPM・資源・詳細、通常folderの音源あり導入/なし保留、旧chart_info保存済み→通常更新→新DB接続を判定し、LegacyAndVersionlessAdaptersShareModernTimingAndModernKeysWinの再生保証は維持する。

## 古い外部プレイヤー終了通知の所有者照合

BMIIDX/LR2でAの捕捉済み終了通知がB開始後に届くと、Bのprocess/handle/pathやLR2 preview scopeまで消す経路がある。Panelの世代照合とadapter内部の解放は別で、購読解除だけでは捕捉済みdelegateを止められない。Pause/Restartとは別原因の任意候補である。

採用時は通知対象processを捕捉し、現在の本人と一致する状態だけ解放する。null senderを使う明示Closeも実processを渡す同じ小さい終端へ揃え、LR2の終了後preview復元の明示再試行を保つ。必須再生群と同adapterの書込みを直列にする。ExternalPlayerProcessGatewayTestsの実adapter/CaptureExitedNotificationでA通知捕捉→A終了→B開始→A通知配送→B Stopを制御し、BへのClose、LR2 scope保持、自然終了/明示停止/復元再試行を確認する。時間待ちだけに依存しない。

## 再生・音声変換のUTF-8 BOM

軽量/詳細ライブラリ側のBOM対応だけでは、Ribbit共有decoderの先頭U+FEFFが残り、先頭BPM/TITLE/WAV命令を読み落とす。先頭BPM120のBOMなしで001小節BGMが2秒、BOMありで既定130の約1.8461538秒となる原因は独立して残る。

採用時は共有decodeで先頭UTF-8 BOMを除き、本番load/準備済み入力へ同じ本文を渡す。BOMなし判別順、CP932の資源名bytes、元bytesのhashを保ち、UTF16/32やparserVersion拡張へ広げない。必須数値/分岐と同parserを直列にする。RibbitBmsFileTimingTestsで先頭BPM/TITLE/WAVのBOM有無一致と2秒、各元bytesのhashを別判定し、BMSAutoPlayWriterTestsで音声変換への代表接続を確認する。

## 音声出力ベース名の解釈

変換ownerは拡張子なしfile pathを渡すがWriterは既存directoryなら意味を変え、Out/SongフォルダがあるとOut/Song.wavの代わりにその配下へ出力する。BMS/BMSON・各形式に共通する任意候補である。

採用時は一つの本番呼出元SelectedChartAudioConversionWorkflowOwnerのfile path契約へWriterを統合し、未使用directory modeのための引数を増やさない。録音形式/必須数値の共有箇所へ合流する。BMSAutoPlayWriterTestsとowner代表で実WAVEがOut直下、子folder/入力元保全、既存完成fileがある場合もencoderの連番でOut直下になることを確認する。

## 検索候補の予約構文の引用

候補のQuoteValueIfNeededはre:始まりを引用せず、表re:mixがregex mix、re:[が不正regexになる。採用時は大小文字を問わず予約構文始まりも引用し、通常名の意味を保つ。KeywordSearchPresentationTests/GridKeywordSearchQueryTestsで候補を実parserへ通し、re:mix/RE:mix/re:[、引用途中、escape、後続句、caretを判定する。保存式比較とは別原因として、同文法helper/検索仕様の書込みを調整する。

## 保存検索式の正規表現の同一性

履歴/お気に入りは全体の大小文字を無視し、title:re:\dとtitle:re:\D等の別パターンを潰す。採用時は通常部分をOrdinalIgnoreCase、regex本体をOrdinalとする小さい文法比較へ統合する。保存文字列は既存の空白/改行整形だけを維持し、大小変換・意味的regex同値・式順/別名の新正規化を追加しない。

tokenizer/引用/否定/項目/OR/Windows drive等の区別を共用し、re:部分文字列だけで判定しない。Regex生成・条件整列を行うGridKeywordSearchQuery.Parse全体は比較へ呼ばず、不正regexも本体原文で比較する。履歴/お気に入りの追加・削除・重複表示・保存復元へ一契約を使う。KeywordSearchSavedQueryStoreTestsと履歴系列でsongtitle/SONGTITLE・title:songtitle/TITLE:SONGTITLEは一件、\d/\D・\w/\Wは別件とし、混在句・引用re:・旧保存値の復元/片方削除と既存上限/形式を確認する。候補行識別とは分ける。

## 言語変更後の表・プロパティ・ランプ表示

一項目内の三つの表示ownerを区別する。既存の音声詳細選択肢と検索提示の言語候補と合わせて五つの所有者であり、同じ症状だけで一括再生成しない。数値・日時は必須の固定規則へ合流する。

| 表示owner | 採用時の変更境界と代表判定 |
| --- | --- |
| 共通表cache | 保留候補案内の整形/描画cacheがアプリ言語通知を受けず旧言語を残す。表寿命の既存資源通知から既存cacheを失効する。CustomTablePhase6CacheTestsと実表で行・選択・scroll・編集候補・生pathを保ち、再検索/全行実体化/ItemsSource交換なしで同じ行の文言が更新する。 |
| 開いたプロパティ | ChartPropertiesViewModelの確認基準・診断・なし/BMS/BMSON説明を取得済み診断と捕捉配置から再整形し、close時に購読解除する。ChartPropertiesViewModelTests/ChartPropertiesWindowTestsで同じ窓の言語追従、読取り一回、path/基準/事実保持を確認し、譜面再解析はしない。 |
| ランプ既存区間 | 言語handlerから同じ区間VMのToolTip/Automation Name/ItemStatusへ通知する。PlaylistLampViewerWindowPresentationTestsで捕捉済み同じButtonと区間参照、選択/表示mode/取得回数を保って文言だけ更新し、再集計/再取得/区間再生成はしない。 |

## URL列幅による表示と操作

任意の機能追加として、既定40 DIPの取得ボタンは通常クリック/再クリックとも現在の取得を維持し、F2/文字入力による既存編集と元URLコピーを保つ。40 DIPより広い場合は元URLを表示し、通常セルの選択・再クリック/F2/文字入力編集へ揃え、取得actionを出さない。編集不可文脈へ幅だけで権限を与えず、空URLも同じ役割にする。単クリック即編集や行double clickの変更は加えない。

採用時は既存ColumnLayout.Widthから表示種別/値/配置/action/再クリック編集可否を一緒に導き、40へ戻せば元へ戻す。最小/既定幅、既存の編集終端・列再構築・cache失効と幅保存を利用し、DPI物理pixel・新mode保存フラグは不要。現行資料の再クリック編集記述は現在の取得ボタン動作に合わせる。CustomTableColumnFactoryTests/EditingTests/ColumnSettingsTestsと実URL列で40→広幅→40、URL1/2・空値・編集可否・幅変更時候補・保存幅復元・ToolTip/コピーを判定し、広幅で取得なし/既定幅の繰返し取得を別確認する。共通表cache/必須フォーカス編集の共有書込みへ合流する。

## 検索欄外で解放したShiftの残留

部品内KeyUpだけで消すshiftKeyDownが、Shiftを保持して一覧へ移動→一覧で解放→検索へ戻ると残り、通常TabがShift+Tabになる。採用時は現在のKeyboardDevice.Modifiers等を判定元にし補助フラグ依存を整理する。MainWindowChartPresentationWpfTestsの実focusで通常Tab適用・実Shift+Tab・行内ボタン巡回・IME確定抑止を確認する。保存式比較とは別の判定とし、下記の検索選択/離脱群と同部品一writerで統合する。

## 確認ボタンの翻訳

ThemedMessageBox.GetButtonsは本文が選択言語でもYes/No/Cancelラベルを固定英語で作る。採用時は既存資源と必要な肯定/否定ラベルを既定資源・6言語へ揃える。生成時取得で足り、modal中の言語購読は増やさない。ThemedMessageBoxTests/DialogPresentationTestsで実Contentと明示結果・Enter既定・Escを確認し、現行三択の取消契約を保つ。確認の応答意味を再変更しない。

## 画面外の検索候補の選択強調

キー移動は論理indexを変えて既存containerだけIsSelectedを設定してからscrollするため、後から実体化した末尾はEnter対象でも強調されない。採用時は区画をまたぐ論理選択をListBox.SelectedItem/SelectedIndexへ投影し、生成後も同じ選択へ揃える。仮想化無効化・全候補実体化・無条件遅延描画は加えない。

MainWindowChartPresentationWpfTestsのLargeFavoritesVirtualizeOffViewportRows等で未実体化を確認→キーで末尾/画面外→生成→強調/Enter一致を判定する。hover色と選択色、先頭末尾折返し、区画間、container再利用、全件実体化しない性質を確認する。同検索部品のShift/離脱/行識別と直列統合し、独立に合否を出す。

## 検索候補の行内ボタンから部品外への離脱

TextBoxからPopup行内ボタンへ進むとBlurを抑止する一方、そのボタンから同Window一覧へ移る終端がなく、StaysOpen=Trueの候補が残る。恒久的操作不能とは扱わない。採用時はTextBox/所有Popup/行内ボタンを含む範囲で、内部移動は保持、外部移動は履歴確定/Blur一回へ揃える。Popupの別ツリー、保留中focusと終了後論理focusも同じ寿命で扱う。

実MainWindow検索/PopupでTextBox→ボタン・ボタン間・ボタン→一覧/別検索を通し、元候補だけの閉鎖と二重実行/履歴保存なしを確認する。TextBox通常外側クリック・Esc・非活性の終端を保つ。Shift/選択/行識別と同検索部品一writerにする。

## 検索候補の行本人と入力状態の識別

候補B→Shift+Tabで行内ボタン→実行せずTabで戻ると先頭Aへ移る。再表示が使うIdentityは入力/caret/版/置換範囲で保存候補全行が共通のため、候補行を区別できない。採用時は適用可否の入力照合と、区画/種別/保存式または挿入内容の行識別を分け、行消失時は別行を本人扱いしない。保存式文法比較とも分け、新保存ID/スキーマを導入しない。

MainWindowChartPresentationWpfTestsで二件以上からBを選び実行なしのボタン往復→同候補の選択/強調/Enterを確認する。通常/playlist両検索、履歴/お気に入り混在、同入力再適用、選択候補削除を分担する。同表示の再構築/重複反映は既存等価性で整理するがowner/版更新は保ち、Shift/画面外選択/部品外離脱とは独立判定する。

## 実フォルダ名NO SONGのランプ集計

通常所属`[NO SONG]`を文字列だけで特殊扱いし、ランプの行/合計/全通常folder対象/遷移から落とす。採用時は特殊nodeの型境界と実folder名を分け、供給・PlaylistLampModelsの順序・集計・行・Workspace遷移/DetailSourceを一単位へ揃える。受理済み名の禁止/自動改名はしない。

PlaylistLampAggregationTests/PlaylistWorkspaceActionWorkflowTestsで同名実folderと本物の特殊nodeを別fixtureにし、実folderの件数・ランプ・行・遷移と特殊node除外を判定する。通常folderを特殊node代わりに使う既存testも意味を分ける。言語追従の同viewer書込みへ合流する。

## 履歴所属の大小文字

PlayHistoryDisplayTargetはfolder正本が区別するA/aを所属Identity全体のOrdinalIgnoreCaseで潰し、表単体/プリセット/FOLDER専用プリセットの表示で片方を落とす。保存所属自体は両方残る。採用時は正規化済みIdentityの完全一致またはhash/folder別比較でfolderをOrdinalにする。

PlayHistoryReadModelTestsで同表同hashのA/aラベルが各一回、RawHash/Md5から同一所属を引いても二重表示なし、元データ不変を確認する。MAX内訳/履歴preset名/URL等の比較方針まで拡張せず、共有する履歴投影・helperだけ直列調整する。

## 更新失敗通知の翻訳

更新の確認/取得/開始失敗でMainWindowの本文・見出しが固定英語になる。採用時は既存通知を資源へ接続し、処理失敗を表す文言へ揃える。元例外は開発診断として保持し、新しい失敗分類を設けない。StartupUpdateWorkflowOwnerTestsの通知/アプリ継続、実表示と資源整合を代表確認する。MainWindowの必須focus/ツリー編集と一writerへ合流し、ready/lockの必須修正とは原因・判定を分ける。

## 採用時と完了時の運用

採用する項目だけ、利用者の意図に依存する未決事項を確定し、[独立テスト設計・引継ぎ・レビュー](../spec/development/agent-workflow.md)と[変更に応じた検証](../spec/development/testing.md)へ進む。本書を採用済みの機能仕様として扱わない。

完了・不採用が決まった項目は、必要な仕様・現行設計の根拠を正本へ移して本書から除く。全項目の扱いが確定したら本書を削除し、[計画一覧](README.md)を更新する。未採用の項目を完了記録や重複台帳として温存しない。
