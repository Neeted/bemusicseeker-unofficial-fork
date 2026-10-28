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
| `Loading` | 最初の確定結果を待つ。 | 明示操作による新しい集計結果を待つ。 |
| `Ready` / `Empty` | 表示する。 | 内容を更新する。 |
| `Deleted` | 一回通知し、表示しない。 | 対象のビューアだけを通知なしで閉じる。 |
| `Failed` | 未取得として表示し、結果に依存する表示・操作を無効にする。 | 現在の要求の取得失敗を表示し、結果に依存する表示・操作を無効にする。 |

取得・集計の失敗だけでビューアを閉じず、正常な空集計・ゼロ件・全件未プレイへ置き換えません。結果領域を未取得へ更新し、旧表示の保存・条件照合・復元は要求しません。失敗後も別の日付や過去日から「最新」への変更で取得できます。他の窓の表示・操作は独立しています。

対象削除は、既存のプレイリスト一覧変更通知を管理主体が受け、安定した `playlist_id` で現在の対象を再解決して判断します。この通知で捕捉・再集計は行いません。一覧の再読込みに伴う同じ対象の一時的な除去・再追加を削除と誤認しません。初回の削除案内に失敗しても対象窓の回収を妨げません。

### 初期配置とDPI

プロセスは埋込みマニフェストでPerMonitorV2を宣言します。ビューアはそれぞれタスクバーへ表示し、初回のShowとアクティブ化の試行が終わったら一時的なOwnerを解除します。

表示直前のOwnerと最大の面積で重なるモニターを選び、その作業領域を使います。通常状態はOwnerの現在の外形矩形、最大化・最小化状態は選んだ作業領域を中央の基準にします。復元用の位置は使いません。希望外形寸法は1290 × 820 DIP、通常の最小外形寸法は820 × 560 DIPです。

希望寸法が収まる場合は基準の上下左右中央へ置き、はみ出す位置だけを作業領域内へ補正します。希望寸法が超過する場合は超過した軸だけを縮め、作業領域の左上へ置きます。位置は負座標も含む物理ピクセルで扱い、DPI倍率は寸法だけに掛けます。整数へ丸めた後も包含を保ちます。通常の最小寸法も収まらない軸は表示先に合わせて緩和しますが、OSの枠等が要求する最小寸法を超える極端に小さい領域まで保証しません。

初回結果と取消・対象削除の確認後、同じUIスレッドで未表示HWNDを作り、一時的に最小寸法を緩和して対象作業領域内へ位置と寸法を同時に移します。移動後のHWNDとWPFの実DPI変換が一致することを確認し、仮配置で変わらない希望寸法から最終外形を適用してShowします。未表示のRootVisualは未接続なので、初期配置のDPIをVisualから取得しません。WPFの同期したDPI更新とWM_SIZEの寸法同期を使い、待機・ポーリングや最終寸法の二重適用は行いません。ネイティブ取得・配置の失敗は既存の表示失敗と同じ回収経路へ返します。

表示後の移動・サイズ変更はWPFとユーザー操作を優先し、初期位置へ戻しません。最小寸法は現在のDPIと作業領域で更新し、広い画面では通常値へ戻します。表示先の大きさを恒久的な最大寸法にしません。同じDPIで作業領域だけが変わる移動では、最小寸法だけを更新します。

DPI変更時はWPFの標準処理を維持し、独自のフォルダ名・件数の列幅を再計測します。区間ラベルは現在のPixelsPerDipをMultiBindingの明示入力とし、論理幅が同じでも収容判定を再評価します。この更新でDB読取り・再集計、行・区間の再生成、表示モード・選択・スクロールの初期化は行いません。ロード前・閉鎖後の列計測は既存の更新予約・解除で抑止します。

### 取得の契機と通常の終了

開くとき、別の基準日へ変更するとき、過去日から「最新」へ戻すときに、その時点の表所属・フォルダ・所持状態・アプリ内スコアと、使用する情報源の履歴を捕捉します。同じ日付の再設定と、既に最新の状態での「最新」は何もしません。

アプリ内スコアの再読込みが失敗した後の明示捕捉は、その情報源の失敗状態を使い、以前の成績を今回の利用可能な結果へ置き換えません。次の通常読込みが成功すれば、新しい明示捕捉にその成績を使います。既に捕捉した不変入力は後の読込みで変えません。[保存成績の再読込み](../library/chart-model.md#保存成績の再読込みと旧結果)に従います。

開いているだけの窓は、スコア・表所属・所持状況の変更へ自動追随しません。これらの変更用の購読、通知ワーカー、更新予約は持ちません。外部スコアDBの自動再読込みや監視、更新ボタン、古さの警告、日付別の結果キャッシュも設けません。表エントリが初回準備中の場合だけ既存の準備完了を非同期で待ち、対象を再解決して捕捉します。窓の取消しは待機を終え、共有の準備処理そのものは停止しません。

セッションは初回取得と、明示操作で受理した全ての捕捉・集計を終端まで所有します。後の要求で置き換えられた処理も含みます。要求の世代番号で古い結果を拒否し、Dispatcherへ投入した後も、画面へ適用する時点で世代と閉鎖状態を確認します。取消しを無視した古い捕捉が後から戻っても後続集計を開始しません。

手動閉鎖、対象削除、初回取得の取消し、表示準備失敗、所有者の終了は、各窓の一つの回収Taskへ合流します。未表示のままの生成済み `Window` も実際に閉じ、`Application.Windows` と `Owner.OwnedWindows` から除去します。`Closed` からの再入は同じ回収へ合流し、初回表示・通知自身を逆方向に待ちません。

同期 `Dispose` は受付・結果公開を止め、実ウィンドウの閉鎖を先に要求します。取消コールバックや捕捉・集計をUIスレッドで同期的に待ちません。メイン画面の終了経路は `DisposeAsync` で開始済み処理と通常回収の終端を待ち、閉鎖後の窓や通知を表示しません。初回表示用トークンは、その処理と取消コールバックが終端してから解放します。資源を持たない捕捉データ源に、通知活動や専用の破棄契約は設けません。

生成・表示準備・閉鎖・必要な回収の失敗は、任意案内の失敗へ読み替えません。既存の回収経路で残りを試み、所有者へ失敗を返します。所有する失敗の通知は既存の翻訳済み一般エラー文言を使い、元例外の詳細は既存のログへ記録します。解放を確認できなかった資源は所有者の管理下に残し、同じ終了待機から確認できるようにします。自動再試行や通知失敗の履歴保存は行いません。位置・大きさ・選択は保存しません。

### 統計とカテゴリ

有効な実エントリだけを集計し、未所持のエントリも総数に含めます。全体の割合の分母は総数、フォルダの割合はそのフォルダの実エントリ数です。分母が0なら「利用不可」とし、0%にはしません。

統計は、総数・所持数・未所持数・所持率、プレイ済み数・未プレイ数・プレイ率、EXスコア率の算術平均・全体クリア率の3群で表示します。各項目は上に控えめな色のラベル、下にやや大きく太い値を左寄せで並べ、群内の項目間だけに控えめな縦線を置きます。群間は広く空け、見出しを置きません。背景・囲み枠・角丸・固定項目幅を設けず内容幅で配置し、ラベルと値の途中では改行しません。外周は16 DIPを維持し、既定1290×820 DIPの日本語9項目は一段に収めます。最小820×560 DIPなど狭い場合は必要に応じて群単位で折り返し、行端に区切り線を残しません。

使用中のスコア情報源とプレイリスト最終更新は、統計の下に小さな通常文字の補助行として表示します。補助行のラベル・値の連結と統計の読み上げ名は、言語ごとの書式を使います。統計と補助行の文言は言語変更へ追従し、テーマ変更はラベル・値・区切り線の意味色へ反映します。これらの表示更新だけでは集計・取得要求やグラフの投影を変更しません。クリア率は `ASSIST` 以上の数を総数で割り、`FAILED` と `NP` を含めません。

スコアを取得できない場合は、プレイ済み数からクリア率までの5項目をまとめて省略し、グラフとその操作も表示しないか無効にします。所持状態の統計と補助行は維持します。空の集計や全件未プレイに置き換えず、この状態だけでビューアを終端にしません。

| 情報源・区分 | 表示規則 |
| --- | --- |
| beatorajaのクリア | `MAX`、`PERFECT`、`FC`、`EXHARD`、`HARD`、`NORMAL`、`EASY`、`ASSIST`、`FAILED`、`NP` の順。`ASSIST` は `INVALID` と `L_ASSIST` を含む。 |
| DJレベル | `AAA`、`AA`、`A`、`B`、`C`、`D`、`E`、`F`、`NP` の順。値 `MAX` は `AAA` に含める。 |
| DJレベルF | プレイ済みの `F` と値 `INVALID` を含む。`NO_PLAY` / `NO_SONG` は含めない。 |
| NP | 未プレイまたはスコアなし。専用のNSカテゴリは作らない。 |
| LR2 | 凡例、全体・フォルダのバー、ツールチップ、Automationのすべてで `MAX` と `EXHARD` を除く。 |

`playlist.last_update` はローカルの壁時計値です。タイムゾーンを割り当てたり変換したりせず、固定文化圏のGregorian `yyyy-MM-dd HH:mm` で、従来の分粒度のまま補助行に表示します。NULLは「利用不可」です。スコア情報源の `LastUpdatedUtc` / `SourceLastUpdatedUtc` を代わりに使いません。

### 基準日のスコア

初期値は「最新」で、現在のスコアを使用します。基準日はカレンダーだけで選択し、テキスト欄は読取り専用です。使用中の情報源全体で対象になる最古の履歴行のローカル日付から今日までを許可します。履歴がない場合は日付選択だけを無効にし、「最新」は使用できます。選択はウィンドウごとに独立し、保存しません。

日付入力面・Calendarは[共通の日付部品](../ui/appearance.md#日付部品の表示)を使います。未選択の案内、短日付、カレンダーの月・曜日・週開始と操作説明はアプリの選択言語に追従します。非Gregorianの追加言語でもWPF標準のGregorian書式を使い、フォーカス喪失やカレンダーを開く操作で選択日を変えません。履歴の取得や再有効化後も読取り専用を維持します。親がLoadedのままテンプレート内のテキスト部品を再生成した場合も、新しいPARTへ表示文化圏と読取り専用を再接続し、選択日・表示月・Calendarモード・取得要求を変更しません。表示中の同じ部品を更新し、言語・テーマ変更で選択日、表示月、割合／曲数モード、取得要求を変更しません。`null` は現在スコアを意味し、「最新」は日付指定を解除します。同じ日付の指定と、既に最新での「最新」は再取得しません。

ローカル日付 `D` を選ぶと、その翌日の午前0時以降の履歴を現在のスコアから巻き戻します。境界時刻と同じ行も含め、時刻の降順、同時刻では情報源の行IDの降順に処理します。

| 情報源 | 復元規則 |
| --- | --- |
| LR2 | 確定・未確定の両方を読み、`old_clear`、`old_op_history`、`old_exscore`、`old_totalnotes` を復元する。 |
| LR2の旧スコアなし | `old_playcount = NULL` なら、他の旧値があってもNPにする。 |
| LR2の不完全な旧値 | `old_playcount` が非NULLで、前記4フィールドがすべてNULLなら履歴スコアを利用不可にする。現在値やNPで補わない。 |
| beatoraja | モード0だけを読み、`oldclear`、`oldscore` と現在のノーツ数を使う。 |
| 巻戻し対象の行がないハッシュ | 現在のスコアを維持する。旧スコアなしとは区別する。 |

読取りは使用中の情報源だけで行い、失敗しても別の情報源へ切り替えません。LR2では必要な履歴トリガーがすべて一致することを要求します。修復可能な索引だけの不具合は警告付きで読めますが、この読取りでは修復・書込みをしません。

履歴の欠落、不正、読取り不能、日付範囲外はスコア情報だけの利用不可とし、「最新」や有効な日付への変更で復帰できます。取消に応じない処理でも、明示要求の世代番号で古い結果の公開を抑えます。

履歴DBの元例外は[共通の読取り境界](play-history.md#外部操作と診断)で診断します。ランプ固有の履歴変換に失敗した場合も、その場所で元例外・取得元・段階・対象パスを記録し、利用不可という結果へ変換します。診断のために例外を保持・再送出しません。

過去日に変わるのはスコア依存の数・割合・グラフだけです。プレイリスト所属、フォルダ順、所持・欠落状態、最終更新には、その明示操作時に捕捉した現在の情報を使います。

### グラフと操作性

左をクリア、右をDJレベルとし、同じ幅の2領域に見出し・凡例・積上げバーを表示します。全体のバーの下には一つの縦スクロールと仮想化された行集合を置き、通常フォルダをその順序で表示します。0件の通常フォルダも残し、特殊な `[NO SONG]` 行は表示と共通尺度から除外します。

右上の「割合」「曲数」は排他的な2択で、初期値は割合です。窓ごとに独立し保存しません。スコア利用不可では操作を無効にし、基準日・最新への変更、データ更新、利用不可からの復帰でも選択モードを保持します。右上は「基準日」ラベル、DatePicker、「最新」ボタン、間隔、隣接した「割合」「曲数」トグルを同じ水平行に配置し、日付の右側に状態ラベルは置きません。トグルはフォーカス時の装飾枠を表示せず、キーボード操作・フォーカス可能性・選択色・通常枠・ホバー・押下・Automationを維持します。ヘッダー左のプレイリスト名は18 DIP・SemiBoldとし、通常最小幅820 DIPと各言語・明暗テーマでも省略記号と全文のツールチップで長い名前を扱い、操作部との重なりを防ぎます。

| モード・対象 | 幅の尺度と区間ラベル |
| --- | --- |
| 割合の通常行 | 各行自身の譜面エントリ数 `N` で100%へ自己正規化し、区間の割合を表示する。 |
| 曲数の通常行 | 通常フォルダ全件の最大譜面エントリ数 `M` を共通尺度とし、利用可能幅 `W` に対して区間件数 `c` の幅を `W*c/M`、行の占有幅を `W*N/M` とする。左右で同じ尺度を使い、区間件数を固定文化圏の桁区切りなしの整数で表示する。 |
| 全体バー | 両モードとも全幅へ自己正規化し、区間ラベルだけを割合／曲数へ切り替える。 |

曲数は既存の譜面エントリ数です。同じタイトルやハッシュによる追加の集約はしません。最大値は未実体化行も含む通常行全件から結果更新時に再計算します。0件通常行は保持し、全0件・フォルダなしでは区間を作らずゼロ除算しません。最後の区間は行の占有幅の終端で端数を調整し、短い行の余白を埋めません。

幅の尺度と詳細の割合分母は分離します。ツールチップとAutomation名は各フォルダ自身の件数を分母とし、`M=100` でも50件中20件は40%です。モード切替だけではDB読取り、再集計、結果適用、行・区間・コレクションの再構築、選択やスクロールの初期化、一覧要求を起こしません。幅不変の最大行と全体バーもラベルを即時更新します。

各行は「ラベル、6 DIPの余白、バー、6 DIPの余白、件数」です。書体と現在の内容から幅を測り、両領域で共有します。ラベルは170 DIP、件数は桁区切りなしの `9999` の幅を上限とします。件数は単位なし・桁区切りなしの整数表記です。行更新後に再計算し、長いラベルには省略記号と全文のツールチップを使います。区切り線は設けず、バーが重ならない行間を確保します。

正の件数だけを重み付きの標準 `Button` として配置し、最後の区間に占有幅内の端数のピクセルを割り当て、利用可能幅を超えないようにします。0件は正の幅やボタンを持ちません。既定幅1290 DIPでは日本語の統計9項目と `MAX` から `NP` の凡例をそれぞれ一行に収め、狭い場合は折り返せます。

凡例にはラベルと件数を表示します。バー内の割合・曲数は完全に収まるときだけ表示し、切れた文字や省略記号にしません。正の件数のツールチップとAutomation名には、完全なラベル・件数・0ではない割合を残します。割合は小数点を `.`、小数2桁・桁区切りなしとして表示します。表示精度より小さい正の割合は、0ではなく翻訳済みの下限表現にし、その数値も同じ固定書式を使います。

表示中の言語変更は、同じ全体・フォルダのクリア／DJレベル区間へ反映します。凡例ラベル、割合、ツールチップ、Automation名・選択状態の説明を更新し、小さい正値の下限表現と算出不可の説明も現在の言語を使います。区間・ボタン・フォルダ行と各コレクション、選択、割合／曲数モード、集計結果を維持し、再取得・再集計・結果の再適用・一覧要求を起こしません。言語変更の購読は親ViewModelの寿命で管理し、窓の閉鎖と破棄の終端後は親と区間へ通知しません。

色は動的テーマから取得します。左右の通常フォルダと全体バーのホスト背景は透明にして実際の窓背景を透過させ、曲数表示の未使用領域を既存パレットのNP区間から判別できるようにします。文字色は実際の単色背景のWCAG sRGB相対輝度から選んだ不透明な黒または白とし、選択後も適用します。ホストと未選択ボタンに枠線を付けず、選択した区間だけを枠線・太字・Automation状態で示します。Enter、Space、AutomationのInvokeは同じ要求を発行します。

### 一覧への移動

正の区間を選ぶと `PlaylistLampSegmentInvocationRequest` を渡します。安定した `playlist_id` で現在のプレイリストを再解決し、対象フォルダとキーワードを組にした一件の要求で詳細表示へ反映します。古い・削除済み・0件・利用不可の要求は何もしません。

通常フォルダでは同じフォルダを確認して選択します。全体の `Overall` ではテーブルルートを選び、現在の通常フォルダの和集合を対象にします。特殊な `[NO SONG]` フォルダ自体は選びませんが、通常フォルダ内の `NO_PLAY` / `NO_SONG` はNPの対象です。

クリアの別名 `pf`、`fc`、`exh`、`hc`、`nc`、`ec`、`ae`、`lae`、`f` と、DJレベルの `aaa`、`aa`、`a`～`f` を実際の一覧パーサーに渡します。`ASSIST` は `INVALID | L_ASSIST`、`AAA` は `AAA | MAX` に対応します。文字列の厳密な順序ではなく、最終的な所属と一回の表示反映を契約にします。

過去日のグラフからも現在のカテゴリ・範囲へ移り、基準日時を一覧へ渡しません。ツリー選択が成功した後だけ、メインウィンドウの最小化解除・アクティブ化・フォーカスを一回試みます。フォーカスに失敗しても、適用した移動を取り消しません。ビューアと他のウィンドウの選択状態は維持します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 物理座標の中央・位置補正・片軸縮小、負原点、非整数DPI、最小寸法の緩和と復帰 | [配置計算](../../../BeMusicSeeker/Views/Playlist/WindowPlacementCalculator.cs) | [配置計算テスト](../../../BeMusicSeeker.Tests/Playlist/WindowPlacementCalculatorTests.cs)で代表条件、平行移動、丸め後の包含を確認する。 |
| 未表示HWND、実DPIとWPF寸法、表示直前のOwnerと状態別基準、タスクバー | [配置](../../../BeMusicSeeker/Views/Playlist/WindowPlacementCalculator.cs)、[窓の管理](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | [表示テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs)の `Viewer_initialPlacementUsesCurrentOwnerAndSynchronizesHiddenNativeWindow` でShow前の外形・不可視・実DPIと、scope適用前のタスクバー設定を確認する。集計後の配置からShowへの同期接続は静的確認する。実モニター間の異DPI移動とタスクバーの見た目は自動検証の保証に含めない。 |
| DPI変更の独自計測、幅不変のラベル再評価、既存状態維持 | [画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs) | [表示テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs)の `Viewer_countModeUsesAllNormalFoldersAndPreservesProjectionUntilDataRefresh` で非整数DPI通知と実Binding、行・区間参照、選択・スクロール・モード、捕捉・集計件数の保持を確認する。 |
| 配布実体のPerMonitorV2 | [マニフェスト](../../../BeMusicSeeker/app.manifest) | [出力ポリシーテスト](../../../BeMusicSeeker.Tests/Verification/ManagedDependencyOutputPolicyTests.cs)の `ApplicationExecutableEmbedsPerMonitorV2Manifest` で構築済みexeの埋込みXMLを確認する。 |
| 明示操作時の表・所持・スコアの捕捉、初回準備待ち | [データ源](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsLibraryPlaylistLampDataSource.cs) | [データ源テスト](../../../BeMusicSeeker.Tests/Playlist/BmsLibraryPlaylistLampDataSourceTests.cs)で当時点の入力と準備完了・取消を確認する。 |
| 集計の分母・カテゴリ・更新日時 | [集計](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampAggregationService.cs) | [集計テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampAggregationTests.cs)で正常空、未所持、情報源別の計算を確認する。 |
| 過去日の復元、旧値欠落、情報源の障害 | [履歴スコア](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampHistoricalScoreSnapshot.cs) | [履歴スコアテスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampHistoricalScoreSnapshotTests.cs)で日付境界・巻戻し・利用不可を確認する。 |
| 明示要求の競合、失敗結果の非採用、全受理処理の終端 | [セッション](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistLampViewerSession.cs) | [セッションテスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerSessionTests.cs)で新旧日付の完了逆転、取消しを無視する捕捉と後続集計の抑止、停止後の受付・公開拒否を確認する。 |
| Dispatcher配送後の古い結果・閉鎖後結果の非採用 | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs) | [ViewModelテスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerViewModelTests.cs)で実Dispatcherを使い、投入済み旧結果による日付・表示・操作の巻戻りを防ぐことを確認する。 |
| 初回・表示中の取得失敗、複数窓、対象削除、再読込み | [窓の管理](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | [管理主体テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowManagerTests.cs)で未取得表示と別窓の独立、初回削除案内、表示中の対象だけの閉鎖、同じ対象の再読込みで閉じないことを確認する。 |
| 実閉鎖の先行、初回取消し、開始済み日付取得の終端 | [窓の管理](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindowManager.cs) | [管理主体テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowManagerTests.cs)で実 `Closed`、所有集合からの除去、UI応答、受理済み処理の回収、遅い完了でも再表示しないことを確認する。 |
| 統計3群と補助2件、内容幅・群単位の折返し、言語・テーマ・Automation | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml) | [ViewModelテスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerViewModelTests.cs)の `Viewer_statisticsKeepOwnershipPlayAndScoreGroupsSeparateFromSupplementaryDetails` で異なる値の所属と書式を、[表示テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs)の `Viewer_statisticsUseContentWidthsAndWrapWholeGroupsWhileTrackingLanguageAndTheme` で既定日本語・最小幅・大数値・長い翻訳・明暗テーマの実配置と、ja/en/fr/zh-CNの言語切替後の画面・Automationの翻訳書式を確認する。利用不可時の4項目と補助2件は既存LR2表示ケース、失敗時の存在数と利用不可はViewModelケースが担う。 |
| 既存区間の言語追従、参照・選択・モードと集計結果の維持、破棄後の通知停止 | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)の親言語ハンドラーと区間表示通知 | [表示テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs)の `Viewer_statisticsUseContentWidthsAndWrapWholeGroupsWhileTrackingLanguageAndTheme` で、変更前から捕捉した全体・フォルダのクリア／DJレベルButtonのToolTip・Automation名／選択状態と同じ凡例TextBlockを確認する。割合モードのまま言語だけを切り替え、十分幅のある同じButtonのContentも現在の割合文字列へ更新し、曲数モードの件数と区別する。代表カテゴリを異なる追加JSON翻訳へ切り替え、小正値・空フォルダ、非表示区間の値と通知、各参照と結果・取得／集計／一覧要求回数・コレクション通知の維持、実CloseとDisposeAsync終端後の親／区間通知0を確認する。WindowClosedから管理主体の破棄への接続は既存管理主体テストと静的確認、日付・表示モードと割合の一般境界は各既存ケース、同梱辞書の全件整合は `LocalizationResourceParityTests` が担う。 |
| 表示モード、尺度、幅不変の即時ラベル、切替の副作用なし | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs) | [表示テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs)で100/50/0件・特殊行・未実体化最大行、左右の尺度、参照・選択・スクロールの保持、モード切替時の捕捉・集計・一覧要求がないことを確認する。 |
| 日付の言語・テーマ追従、初期化順と表示更新の副作用なし | [共通日付表示](../../../BeMusicSeeker/Views/DatePickerPresentation.cs)、[言語サービス](../../../BeMusicSeeker/ViewModels/ResourceService.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml) | `PlaylistLampViewerWindowPresentationTests.Viewer_datePresentationTracksApplicationCultureWithoutChangingTheQuery` でnull／固定の月日、en/frの月日交換、非Gregorianの追加JSON言語と標準のLostFocus／開く操作、週開始が同じ言語間、開いた同じPopupとCalendar、月年表示維持、標準ボタンの一回接続と取得回数不変を確認する。同日・最新の再取得抑止は `PlaylistLampViewerSessionTests.Session_queryUpdateKeepsTheLatestImmutableQuery` が担う。 |
| 日付変更・最新復帰・LR2・利用不可、複数窓とモードの独立 | [ViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistLampViewerViewModel.cs)、[画面](../../../BeMusicSeeker/Views/Playlist/PlaylistLampViewerWindow.xaml.cs) | [表示テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistLampViewerWindowPresentationTests.cs)で操作による再取得と表示モード保持、全0件・フォルダなし、ラベル収容を確認する。翻訳は共通の `LocalizationResourceParityTests` で確認する。 |
| 区間からの移動、現在の対象の再解決 | [ワークスペース](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PlaylistLampViewer.cs) | [ワークスペース操作テスト](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)でカテゴリと対象範囲を、[MainWindow接続テスト](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)の `PlaylistLampNavigation` で実ツリーへの移動と完了を確認する。 |

## 関連資料

[プレイ履歴](play-history.md)、[LR2履歴スキーマ](lr2-history-schema.md)、[共通外観](../ui/appearance.md)。
