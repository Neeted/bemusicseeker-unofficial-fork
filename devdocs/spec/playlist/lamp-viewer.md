# プレイリストのランプ集計

## 目的と適用範囲

一つのプレイリストについて、所持数、クリア状態、DJレベルをローカルの別ウィンドウで表示する仕様です。現在の所属・所持状態と、選択日のスコアを区別します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

**区間（セグメント）**は積上げバーの一カテゴリです。**基準日**は、その日の終了時点のスコアを再現するための日付です。

## 仕様

### 開く操作と状態

プレイリストツリーとサマリー行の「ページを開く」の直下に「ランプビューアを開く」を表示します。型付きの画面要求で処理し、ブラウザや外部URLは使用しません。

呼出しごとに新しい `ThemedWindow` を作ります。同じプレイリストも複数開けます。メインウィンドウを一時的な所有者として `CenterOwner` の配置を決め、モードレス表示と最初のアクティブ化を一回試みた直後に `Owner` を解除します。以後の重なり順をメインウィンドウに拘束しません。通知ダイアログの所有者はメインウィンドウのままです。

| 状態 | 表示前 | 表示後 |
| --- | --- | --- |
| `Loading` | 最初の確定結果を待つ。 | 新しい集計結果を待つ。 |
| `Ready` / `Empty` | 表示する。 | 内容を更新する。 |
| `Deleted` | 一回通知し、表示しない。 | 対象のビューアだけを通知なしで閉じる。 |
| `Failed` | 一回通知し、表示しない。 | 一回通知し、対象のビューアだけを閉じる。 |

購読は開始前に行います。通知の表示失敗は呼出元へ返します。終端通知の重複や手動で閉じる操作があっても、データ源、セッション、DataContextの破棄は一回です。メインウィンドウ終了時は、開く途中の処理を取り消して全ビューアを閉じ、後からウィンドウや通知を表示しません。位置・大きさ・選択を保存せず、ウィンドウ間キャッシュも設けません。

### 統計とカテゴリ

有効な実エントリだけを集計し、未所持のエントリも総数に含めます。全体の割合の分母は総数、フォルダの割合はそのフォルダの実エントリ数です。分母が0なら「利用不可」とし、0%にはしません。

カードの順序は、総数、所持数、未所持数、所持率、使用中のスコア情報源、プレイ済み数、未プレイ数、プレイ率、EXスコア率の算術平均、全体クリア率、プレイリスト最終更新です。クリア率は `ASSIST` 以上の数を総数で割り、`FAILED` と `NP` を含めません。

スコアを取得できない場合は、プレイ済み数からクリア率までの5枚をまとめて省略し、グラフとその操作も表示しないか無効にします。所持状態のカードは維持します。空の集計や全件未プレイに置き換えず、この状態だけでビューアを終端にしません。

| 情報源・区分 | 表示規則 |
| --- | --- |
| beatorajaのクリア | `MAX`、`PERFECT`、`FC`、`EXHARD`、`HARD`、`NORMAL`、`EASY`、`ASSIST`、`FAILED`、`NP` の順。`ASSIST` は `INVALID` と `L_ASSIST` を含む。 |
| DJレベル | `AAA`、`AA`、`A`、`B`、`C`、`D`、`E`、`F`、`NP` の順。値 `MAX` は `AAA` に含める。 |
| DJレベルF | プレイ済みの `F` と値 `INVALID` を含む。`NO_PLAY` / `NO_SONG` は含めない。 |
| NP | 未プレイまたはスコアなし。専用のNSカテゴリは作らない。 |
| LR2 | カード、凡例、全体・フォルダのバー、ツールチップ、Automationのすべてで `MAX` と `EXHARD` を除く。 |

`playlist.last_update` はローカルの壁時計値です。タイムゾーンを割り当てたり変換したりせず、`ToString("g", CurrentCulture)` で末尾のカードに表示します。NULLは「利用不可」です。スコア情報源の `LastUpdatedUtc` / `SourceLastUpdatedUtc` を代わりに使いません。

### 基準日のスコア

初期値は「最新」で、現在のスコアを使用します。基準日はカレンダーだけで選択し、テキスト欄は読取り専用です。使用中の情報源全体で対象になる最古の履歴行のローカル日付から今日までを許可します。履歴がない場合は日付選択だけを無効にし、「最新」は使用できます。選択はウィンドウごとに独立し、保存しません。

ローカル日付 `D` を選ぶと、その翌日の午前0時以降の履歴を現在のスコアから巻き戻します。境界時刻と同じ行も含め、時刻の降順、同時刻では情報源の行IDの降順に処理します。

| 情報源 | 復元規則 |
| --- | --- |
| LR2 | 確定・未確定の両方を読み、`old_clear`、`old_op_history`、`old_exscore`、`old_totalnotes` を復元する。 |
| LR2の旧スコアなし | `old_playcount = NULL` なら、他の旧値があってもNPにする。 |
| LR2の不完全な旧値 | `old_playcount` が非NULLで、前記4フィールドがすべてNULLなら履歴スコアを利用不可にする。現在値やNPで補わない。 |
| beatoraja | モード0だけを読み、`oldclear`、`oldscore` と現在のノーツ数を使う。 |
| 巻戻し対象の行がないハッシュ | 現在のスコアを維持する。旧スコアなしとは区別する。 |

読取りは使用中の情報源だけで行い、失敗しても別の情報源へ切り替えません。LR2では必要な履歴トリガーがすべて一致することを要求します。修復可能な索引だけの不具合は警告付きで読めますが、この読取りでは修復・書込みをしません。

履歴の欠落、不正、読取り不能、日付範囲外はスコア情報だけの利用不可とし、「最新」や有効な日付への変更で復帰できます。取消に応じない処理でも、要求と情報源の世代番号で古い結果の公開を抑えます。

過去日に変わるのはスコア依存の数・割合・グラフだけです。プレイリスト所属、フォルダ順、所持・欠落状態、最終更新は常に現在の情報を使います。

### グラフと操作性

左をクリア、右をDJレベルとし、同じ幅の2領域に見出し・凡例・積上げバーを表示します。全体のバーの下には一つの縦スクロールと仮想化された行集合を置き、通常フォルダをその順序で表示します。0件の通常フォルダも残し、特殊な `[NO SONG]` 行は表示と共通尺度から除外します。

右上の「割合」「曲数」は排他的な2択で、初期値は割合です。窓ごとに独立し保存しません。スコア利用不可では操作を無効にし、基準日・最新への変更、データ更新、利用不可からの復帰でも選択モードを保持します。右上は「基準日」ラベル、DatePicker、「最新」ボタン、間隔、隣接した「割合」「曲数」トグルを同じ水平行に配置し、日付の右側に状態ラベルは置きません。トグルはフォーカス時の装飾枠を表示せず、キーボード操作・フォーカス可能性・選択色・通常枠・ホバー・押下・Automationを維持します。ヘッダー左のプレイリスト名は18 DIP・SemiBoldとし、最小幅820 DIPと各言語・明暗テーマでも省略記号と全文のツールチップで長い名前を扱い、操作部との重なりを防ぎます。

| モード・対象 | 幅の尺度と区間ラベル |
| --- | --- |
| 割合の通常行 | 各行自身の譜面エントリ数 `N` で100%へ自己正規化し、区間の割合を表示する。 |
| 曲数の通常行 | 通常フォルダ全件の最大譜面エントリ数 `M` を共通尺度とし、利用可能幅 `W` に対して区間件数 `c` の幅を `W*c/M`、行の占有幅を `W*N/M` とする。左右で同じ尺度を使い、区間件数を `CurrentCulture` の `N0` で表示する。 |
| 全体バー | 両モードとも全幅へ自己正規化し、区間ラベルだけを割合／曲数へ切り替える。 |

曲数は既存の譜面エントリ数です。同じタイトルやハッシュによる追加の集約はしません。最大値は未実体化行も含む通常行全件から結果更新時に再計算します。0件通常行は保持し、全0件・フォルダなしでは区間を作らずゼロ除算しません。最後の区間は行の占有幅の終端で端数を調整し、短い行の余白を埋めません。

幅の尺度と詳細の割合分母は分離します。ツールチップとAutomation名は各フォルダ自身の件数を分母とし、`M=100` でも50件中20件は40%です。モード切替だけではDB読取り、再集計、結果適用、行・区間・コレクションの再構築、選択やスクロールの初期化、一覧要求を起こしません。幅不変の最大行と全体バーもラベルを即時更新します。

各行は「ラベル、6 DIPの余白、バー、6 DIPの余白、件数」です。書体と現在の内容から幅を測り、両領域で共有します。ラベルは170 DIP、件数はローカライズした `N0(9999)` の幅を上限とします。件数は単位なしの `N0` 表記です。行更新後に再計算し、長いラベルには省略記号と全文のツールチップを使います。区切り線は設けず、バーが重ならない行間を確保します。

正の件数だけを重み付きの標準 `Button` として配置し、最後の区間に占有幅内の端数のピクセルを割り当て、利用可能幅を超えないようにします。0件は正の幅やボタンを持ちません。既定幅1290 DIPではカードと `MAX` から `NP` の凡例をそれぞれ一行に収め、狭い場合は折り返せます。

凡例にはラベルと件数を表示します。バー内の割合・曲数は完全に収まるときだけ表示し、切れた文字や省略記号にしません。正の件数のツールチップとAutomation名には、完全なラベル・件数・0ではない割合を残します。表示精度より小さい正の割合は、0ではなくローカライズした下限表現にします。

色は動的テーマから取得します。左右の通常フォルダと全体バーのホスト背景は透明にして実際の窓背景を透過させ、曲数表示の未使用領域を既存パレットのNP区間から判別できるようにします。文字色は実際の単色背景のWCAG sRGB相対輝度から選んだ不透明な黒または白とし、選択後も適用します。ホストと未選択ボタンに枠線を付けず、選択した区間だけを枠線・太字・Automation状態で示します。Enter、Space、AutomationのInvokeは同じ要求を発行します。

### 一覧への移動

正の区間を選ぶと `PlaylistLampSegmentInvocationRequest` を渡します。安定した `playlist_id` で現在のプレイリストを再解決し、対象フォルダとキーワードを組にした一件の要求で詳細表示へ反映します。古い・削除済み・0件・利用不可の要求は何もしません。

通常フォルダでは同じフォルダを確認して選択します。全体の `Overall` ではテーブルルートを選び、現在の通常フォルダの和集合を対象にします。特殊な `[NO SONG]` フォルダ自体は選びませんが、通常フォルダ内の `NO_PLAY` / `NO_SONG` はNPの対象です。

クリアの別名 `pf`、`fc`、`exh`、`hc`、`nc`、`ec`、`ae`、`lae`、`f` と、DJレベルの `aaa`、`aa`、`a`～`f` を実際の一覧パーサーに渡します。`ASSIST` は `INVALID | L_ASSIST`、`AAA` は `AAA | MAX` に対応します。文字列の厳密な順序ではなく、最終的な所属と一回の表示反映を契約にします。

過去日のグラフからも現在のカテゴリ・範囲へ移り、基準日時を一覧へ渡しません。ツリー選択が成功した後だけ、メインウィンドウの最小化解除・アクティブ化・フォーカスを一回試みます。フォーカスに失敗しても、適用した移動を取り消しません。ビューアと他のウィンドウの選択状態は維持します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 集計の分母・カテゴリ・更新日時 | [`BmsLibraryPlaylistLampDataSource`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistLampDataSource.cs)、[`PlaylistLampAggregationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampAggregationService.cs) | [`BmsLibraryPlaylistLampDataSourceTests`](../../../BeMusicSeeker.Tests/Playlist/BmsLibraryPlaylistLampDataSourceTests.cs)、[`PlaylistLampAggregationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampAggregationTests.cs) |
| 過去日の復元、旧値欠落、情報源の障害 | [`PlaylistLampHistoricalScoreSnapshotReader`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampHistoricalScoreSnapshot.cs)、[`PlaylistLampHistoricalScoreSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampHistoricalScoreSnapshot.cs) | [`PlaylistLampHistoricalScoreSnapshotTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampHistoricalScoreSnapshotTests.cs) |
| 世代番号・状態の更新・破棄 | [`PlaylistLampViewerSession`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampViewerSession.cs) | [`PlaylistLampViewerSessionTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerSessionTests.cs) |
| 表示前の待機、複数窓、終端通知、終了 | [`PlaylistLampViewerWindowManager`](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | [`PlaylistLampViewerWindowManagerTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowManagerTests.cs) |
| 列幅、選択、キーボード・Automation、一覧移動 | [`PlaylistLampViewerWindow`](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs)、[`PlaylistLampViewerViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs) | [`PlaylistLampViewerWindowPresentationTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs) |
| トグルのフォーカス装飾なし、NPと未使用領域の判別 | `PlaylistLampViewerWindow.xaml` の局所トグルスタイルと左右4箇所のバー背景 | 既存表示テストでモード選択・グラフ操作の維持を確認し、一時WPF描画でLight/Dark・820/1290 DIPの曲数表示を確認する。実機入力・DPIは自動検査の対象外。 |
| 表示モード、共通尺度、幅不変の即時ラベル、切替の副作用なし、更新 | `PlaylistLampViewerViewModel`、`PlaylistLampWeightedStackPanel`、`PlaylistLampSegmentLabelConverter` | `Viewer_countModeUsesAllNormalFoldersAndPreservesProjectionUntilDataRefresh` は100/50/0件と特殊行、未実体化最大行、左右、参照・通知・捕捉／集計回数・一覧要求・選択・スクロール不変、最大100→200を確認する。 |
| 複数窓の独立、初期割合、全0件・フォルダなし | `PlaylistLampViewerViewModel`、`PlaylistLampViewerWindow` | `Viewer_displayModeIsIndependentAcrossWindowsAndNewWindowsStartWithPercentage`、`Viewer_countModeRetainsZeroRowsAndHandlesNoFoldersAfterRefresh`。 |
| 両モードのラベル収容、基準日・最新・LR2・利用不可からの復帰 | `PlaylistLampSegmentLabelConverter`、`PlaylistLampViewerViewModel` | `Viewer_widthAwareLabelsShowSelectedModeOnlyWhenTheSegmentCanContainIt`、既存の基準日・LR2表示テストでモード保持を確認する。翻訳対応は `LocalizationResourceParityTests` の共通検査を使う。 |

## 関連資料

[プレイ履歴](play-history.md)、[LR2履歴スキーマ](lr2-history-schema.md)、[共通外観](../ui/appearance.md)。
