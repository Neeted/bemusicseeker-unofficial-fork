# 再生パネルの表示

## 目的と適用範囲

再生パネルの保存状態、利用可能な表示面、初期同期と通常のアニメーションを定めます。あわせて、パネルが受け付ける再生の開始・停止・送りと、一覧の再生状態の表示を扱います。

音声処理の寿命と次曲の先行準備は[音声実行基盤](../runtime/audio.md)、外部プレイヤーのプロセスと設定ファイルの保全は[外部起動](external-launch.md#一覧再生の外部プレイヤー所有範囲)、一覧の選択と現在セルは[一覧の選択とキー操作](table-view.md#選択とキー操作)を正本とします。

## 用語

[共通用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| プレイヤー | 再生を担う実装。内蔵プレイヤーと、外部プレイヤー（BMIIDXView2015、uBMplay、LR2）を区別する。 |
| 表示面 | パネルに表示する画像面と、内蔵・外部プレイヤーの再生面。 |
| 要求状態・実効状態 | 保存された表示の要求（`PlayerPanelState`）と、利用可能な表示面を考慮した実際の表示（`EffectivePlayerPanelState`）。 |
| 再生世代 | 開始・停止・送りの要求が現在のものかを判定するための、`PlaybackPanelViewModel` が進める番号。 |
| 曲開始の直列化 | 開始・送り・行の起動の要求を受付順に一件ずつ処理する仕組み。演奏全体は含まない。 |
| 次回の再生方式 | 設定で確定し、次の新規開始で使うプレイヤーの生成手段と形式能力。 |
| 一時試聴 | 保留中のパッケージの譜面を、一時的に配置先へコピーして試聴すること。ライブラリ変更の受付（L）を必要とする。 |

LとPは[操作の競合ポリシー](../core/operation-concurrency-policy.md)の受付の区分です。

## 仕様

### 保存状態と有効な表示

`PlaybackPanelViewModel.PlayerPanelState` が、保存された要求状態の正本です。表示は設定を直接読まず、実効状態 `EffectivePlayerPanelState` を使います。`TITLE_SMALL` は小型表示のフラグです。値0の `TITLE_LARGE` は正規の拡大型であり、未初期化を表しません。

表示面は画像と内蔵プレイヤーです。プレイヤーを使えない場合は画像を表示しますが、要求状態は書き換えません。小型の指定とプレイヤーの指定が併存していても、最初のフレームから画像の小型表示になります。

表示切替ボタンのツールチップは、実効状態と現在の言語のリソースから作ります。言語を変更しても表示面・再生・プレイヤーは変えず、文言だけを更新します。

### 外部プレイヤーのホスト

`ExternalPlayerHwndHost` は、WPFの `HwndHost` として、自プロセスのUIスレッド上に標準STATICクラスの黒い子ウィンドウを一つ所有します。

| 時点 | 契約 |
| --- | --- |
| 生成 | WPFウィンドウへの接続で生成する。初期状態が `Collapsed` でも、`ContentRendered` の後には有効である。`PlayerHostHandle` は生成済みのハンドルを返し、取得時に遅延生成しない。 |
| 接続 | 通常の初期化完了の通知から、`MainWindow` が非ゼロの親ハンドルを外部プレイヤーへ接続する。 |
| 表示の変化 | 画像と再生面の切替、重ね合わせ表示、`Unloaded`、切離しと再接続を通して同じハンドルを保つ。 |
| 終了 | 画面の枠組みの終了管理によるプレイヤー終了の実際の完了を待ち、最後の `MainWindow.Closed` からUIスレッド上でホストを解放する。 |
| 失敗 | ネイティブの生成・破棄の失敗は例外として伝える。 |

外部プレイヤーの大きさは物理587×256ピクセルです。XAMLの予約領域は `MinWidth=587`、`Height=256`、`MaxHeight=256` DIPとし、中央・下端へ配置します。ホストは、各軸について、WPFから渡された配置寸法と、物理寸法を実行時のDPI倍率でDIPへ換算した値の小さい方を使います。小型表示でも固定の予約寸法を保ち、親の可用領域とは区別します。空のホストの位置とサイズは `HwndHost` が同期します。

### 共通の再生対象と一覧状態

#### 再生対象

内蔵再生の選択・キュー・現在曲には、共通の `ChartFile` を使います。次曲・前曲・フォルダ送りは譜面を対象にし、一時配置と終了コールバックも、開始時に確定した同じ対象と再生世代を保持します。外部プレイヤーの形式条件は、内蔵再生の能力から推測しません。

現在曲は `NowPlayingChart` を正本とします。曲は、譜面形式と実パスの `OrdinalIgnoreCase` による一致で区別し、同じハッシュの別パスを同じ曲にまとめません。一覧上の位置は次のように求めます。

- 開始候補の譜面と、その行の識別情報（所持項目の識別、パッケージ項目、生存している `BMSTableEntry` など）を、同じ行取得の境界で捕捉します。
- 送りと先読みは、現在の一覧表示が持つ識別の索引で位置を探し、取得した行の識別情報と、形式・実パスも確認します。並べ替えで別のエントリへ移った位置は、現在曲の位置として採用しません。
- 同じ譜面を指す複数のプレイリストエントリは、開始したエントリで区別し、並べ替えと絞込みの後も生存している行の現在位置を使います。
- 選択範囲と旧い一覧表示は保持せず、存在しない識別を新しい所持項目の識別やハッシュへ結び直しません。

#### 再生方式の次回適用

再生方式の設定を確定しても、次回の再生方式だけを更新し、現在のプレイヤー・曲・演奏Taskは維持します。一時停止・再開・停止・再始動では方式を置換しません。

- 新規開始は、曲開始の直列化を通過してから、保存済みの次回の再生方式を一回だけ捕捉し、その形式能力で候補を選びます。同じ開始の中で入力不良の候補を飛ばしても、後から保存された方式を捕捉し直しません。
- 候補の検査と、必要な一時配置の受付が成功した後に、捕捉した方式で実際のプレイヤーを置換します。
- 繰り返しがOFFの手動送りが一覧の端で新しい開始へ進まない場合は、次回の再生方式を消費しません。
- bmson行の直接起動は、次回の再生方式があればその形式能力で副作用の前に判定し、非対応なら現在曲とヘッダーを変えずに拒否します。直列化の順番を待った後も、明示した行の条件を確認し、別の行へ読み替えません。ヘッダーは受理した開始に合わせて更新し、順番待ちの行で先に変えません。
- 先読みは、現在のプレイヤーの能力に従います。

シークなどの操作能力、外部プレイヤーの表示面、一時試聴のLR2専用の確認は、現在の実際のプレイヤーから求めます。設定の保存や画像設定の通知だけで将来の方式へ切り替えず、実際の置換に成功した時点で関連する表示を更新します。

| 状況 | 次回の再生方式 |
| --- | --- |
| 置換に失敗した | 次の明示的な開始まで保持する。 |
| 置換中に新しい方式が保存された | 先行する置換の成功・失敗で消さない。 |
| 開始が競合で拒否された | 後から予約せず、方式の生成も開始しない。 |

#### 一覧の再生状態

所持カタログを譜面情報の唯一の正本とし、ライブラリの変更時は再生を停止します。`PlaybackPanelViewModel` が現在の対象と再生状態を所有します。

通常一覧とプレイリスト詳細の行は、`status` の読取り時に、行が持つ状態の `~PLAYALL` の部分を保ち、形式と実パスの `OrdinalIgnoreCase` が一致する場合だけ現在の再生ビットを重ねます。保存の管理主体・`ChartFile`・一時的な投影へ再生状態を書き戻しません。同じ対象の複数の行と、後から実体化する行も同じ現在値を返します。行の登録、行ごとの再生通知、別の状態辞書、一覧の全件走査は使いません。

| 状態 | 値 | 条件 |
| --- | ---: | --- |
| `LOADING` | 2 | 準備中 |
| `PLAY` | 1 | 開始に成功した場合と再開 |
| `PAUSE` | 4 | 一時停止 |
| `NONE` | 0 | 停止、開始失敗、対象未設定 |
| `FORWARD` | 8 | 早送り中。現在のビットへ加え、操作の終了時にこのビットだけを解除する |
| `BACKWARD` | 16 | 巻戻し中。`FORWARD` と同じ扱い |

`PLAYALL` は `0x1F` です。先読みだけの譜面には状態を付けません。

準備・開始・一時停止・再開・停止・対象の変更では、既存の非同期のUI配送を通して `MainChartList.RequestDisplayRefresh` から表の表示更新へ接続し、セルの表示値だけを再評価します。行集合・順序・選択・スコア・未確定の編集は保ちます。時刻の進行だけでは表を更新せず、再生状態の排他を保持したままUIの完了を同期的に待ちません。表側の扱いは[一覧の可視行だけの実体化](table-view.md#可視行だけの実体化)に従います。

#### 表示する内容

題名・字幕・作者は、表示対象の共通の表示値を使います。

現在曲がない停止中は、表の主行の変更を既存の選択通知から受け、題名・字幕・作者と `DisplayedChart` をその行へ更新します。Ctrl+Aの後のShift操作のように選択集合が変わらなくても同じです。再生中に同じ操作で主行が変わっても、現在曲の表示を優先します。通知の要否と、一回の入力につき一回の配送は、[一覧の選択とキー操作](table-view.md#選択とキー操作)の共通の選択モデルで判断します。

採用した次曲・前曲・自然終了の候補が一覧の主行を変える場合と、主行が変わらない一時停止・再開・停止・時刻・表示の更新の場合の、現在セルと編集中の入力の扱いも、[一覧の選択とキー操作](table-view.md#選択とキー操作)を正本とします。

#### 画像とバナー

画像は再生に必須の入力ではありません。`DisplayedChart` が変わったら、前の曲の画像とバナーを先に除去し、現在の対象のパネル画像とバナーを独立して読みます。素材A、素材B、素材なしと選曲を切り替えても前の曲の素材を残さず、素材なしでは既定画像に戻し、バナーの背景を消します。

主画像は、譜面の正常な `STAGEFILE`、有効な設定の外部画像、現在のテーマの同梱既定画像の順で表示します。

| 状況 | 表示 |
| --- | --- |
| パネル画像の欠落・破損 | 設定の外部画像、それも読めなければテーマの既定画像へ進む。 |
| バナーの欠落・破損 | 同じ曲の正常なパネル画像を `UniformToFill` で使う。同じ曲のパネル画像も使えなければ、バナーの背景と枠を消す。代替の描画に失敗した場合も背景と枠なしを保つ。 |
| 正常なバナー | `Fill` で表示する。 |

任意の画像の読取りや、その元のパス・原因の診断が失敗しても、実際の選曲Task・現在曲・`PLAY` 状態・開始結果は変えません。画像の復号はストリームを閉じる前に完了させます。画面の `DataContext` の契約違反は、素材の不足として扱いません。

開始後のBPM・最小値・最大値・TOTAL・ノートの進行・表示の終端は、共通の再生解析結果を使います。形式ごとの表示統計は[bmson再生の共通経路](../library/bmson-playback.md#共通の再生経路と表示)に従い、音声の発音数や他のプレイヤーの採点値とは区別します。

### テーマ画像とフラットな操作表示

#### 既定画像

各テーマの辞書は、`Playback.DefaultImage` を `BitmapImage` として持ちます。Lightは `Assets/Images/default_image.jpg`、ほかのテーマは `Assets/Images/Playback/{Theme}.jpg` を使います。

- 既定画像を表示するときだけ、主画像を動的リソースへ接続し、同じ画面を表示したままテーマの画像を切り替えます。
- `STAGEFILE` と設定の外部画像は通常の値として設定し、テーマを変更しても画素や参照を変えません。
- コード側は外部画像だけをキャッシュし、既定画像の復号と保持はテーマのリソースに任せます。
- `DataContext` がなければ、主画像とバナーの背景を空にし、バナーの枠を消します。
- 主画像は一つのImageと `UniformToFill` を使い、小型表示のぼかしを保ちます。バナーに外部画像やテーマの既定画像を流用しません。

#### 操作帯の配色

操作帯は、テーマごとに独立した `Playback.ControlBackgroundBrush`、`Playback.ControlForegroundBrush`、`Playback.ControlHoverBrush` を動的に参照します。

| テーマ | 背景 / 前景 / ホバー |
| --- | --- |
| LightとDark | 順に `#FF252526`、`#FFD4D4D4`、`#FF333337` を各辞書で独立に定義する。 |
| ほかのテーマ | 現在の `App.SurfaceBrush`、`App.TextBrush`、`App.ControlHoverBrush` と同じ色値を、それぞれ独立に定義する。 |

- 操作アイコン、小型の曲名、時刻は操作の前景色を使い、時刻は不透明度を下げて控えめにします。
- 無効なアイコンは `App.DisabledTextBrush`、通常のボタン背景は透明です。ホバーと押下は操作のホバー背景を使い、押下は不透明度でも示します。
- 画像上の曲名の領域は `#CC000000` と白文字、統計は既存の書体・内容に白文字と `#33000000` の背景を使います。グラデーションや影は付けません。バナーの枠は `App.BorderBrush` を使います。
- 再生メニューのHS UP／HS DOWN／Info／Effect／1P/2Pは、共通の枠付き省スペースButtonを使い、命令・クリック・有効条件を保ちます。
- 操作帯の部品間は4 / 8 DIPに揃え、用途に応じて通常版・省スペース版の共通スタイルを使います。外部プレイヤーの領域、画像、シークの描画位置、高さアニメーションの寸法は、操作帯の余白と分けて保ちます。

#### シークと音量

シークと音量のSliderは、設定と同じ `App.Canonical.CompactSliderStyle` の軌道とつまみを使い、輪郭線とつまみ周囲の余白を設けません。未再生の軌道は `App.BorderBrush`、再生済みの軌道とつまみは `App.AccentBrush` を使い、状態色は[共通外観の通常版と省スペース版](appearance.md#通常版と省スペース版)に従います。

シークの時刻は通常は表示せず、つまみのホバー中とドラッグ中だけ、背景・枠・影のない白文字で表示します。シークの操作は次のとおりです。

- 座標は、実際の軌道とつまみの配置に基づいて時刻へ換算し、上下限に丸めます。値を反映しても双方向のデータ連結を保ちます。
- アイコンボタンのホバー背景と重なる範囲でも、軌道とつまみを含むシーク全体を描画と入力の最前面に置きます。
- 明示的に一時停止した状態でのクリックとドラッグは位置だけを変え、ボタンを離したときや、押したまま領域を出たときも一時停止を保ちます。再生中の連続したシークも再生を保ちます。シークの開始・終了から再生の切替を予約しません。
- シークのSliderだけ、ペンの長押しによる右クリック相当の操作を無効にします（`Stylus.IsPressAndHoldEnabled=False`）。

音量欄は上のボタン群と同じ幅に広げ、メニューの `App.TextBrush` を使う百分率の列に40 DIP、Sliderの列に残りの幅を割り当てます。0～100%のどの桁数でも、Sliderの幅と左右の位置を変えません。音量の即時反映と、操作完了時・終了時の保存は[設定の項目の保存](../runtime/settings.md#項目の保存)に従い、この表示で変えません。

再生ボタンとパネルが所有するSliderには、フォーカス枠を表示しません（`FocusVisualStyle=null`）。フォーカスの可否、Tab移動、キー操作、命令は保ちます。アイコン操作のスタイルとシークの時刻表示は画面のリソースが、Sliderの描画とフォーカス枠の抑止は共通の辞書が所有します。

### 初期同期

高さ、画像のぼかし、大きい題名の余白、バナーと小型題名の不透明度を、一つの処理で同期します。有効なViewModelを初めて適用するときは、アニメーションを除去し、最終値へ即時に反映します。

`DataContext` が読込みの前・途中のどちらで設定されても同じです。表示中の差替えと、非表示からの再読込みも初期同期とし、以前のViewModelや表示期間の状態から遷移しません。購読には世代を持たせ、古い通知を新しい表示へ適用しません。

| 値 | 小型 `TITLE_SMALL` | 拡大型 `TITLE_LARGE` |
| --- | ---: | ---: |
| パネルの高さ | 110 | 286 |
| ぼかし半径 | 20 | 0 |
| 大きい題名の下余白 | -40 | 0 |
| バナーの不透明度 | 1 | 0 |
| 小型題名の不透明度 | 1 | 1。表示の可否は既存の変換処理で決める |

初期同期の後の対象プロパティにアニメーションを残しません。一時的な非表示、任意のDispatcherの遅延、固定の待機、起動時だけの不透明度で初期フレームを隠しません。

### 通常の遷移

初期同期の後、同じViewModelと購読の世代で小型フラグが変わった場合だけ遷移します。表示面や利用可否だけが変わり、小型フラグが同じ場合は遷移しません。

| 対象 | 時間 |
| --- | --- |
| 高さとぼかし | 約1秒 |
| 大きい題名 | 0.7秒 |
| 小型バナーの表示 | 0.8秒後から0.5秒 |
| 小型バナーの消去（拡大型へ戻るとき） | 0.2秒 |
| 小型題名の表示 | 小型化の0.8秒後から |

完了・置換・非表示の時点でアニメーションを除去し、プロパティの基本値を最終状態として残します。

### 前曲ボタンの遅延判定

単クリックは500ms後に現在曲を最初から再生し直し、ダブルクリックは前曲へ移ります。判定の時計は、画面と同じDispatcherの `Background` 優先度で動きます。

画面が時計の開始・停止と購読を所有します。`DataContext` の交換と `Unloaded` では、時計を止めて購読を解除し、判定中のジェスチャーを無効にします。交換後や再接続後は新しいクリックから判定し、以前の表示期間の遅延通知を持ち越しません。

### 曲の準備・開始と次候補

#### 面の要求とフォーカス

表の行の起動と再生ボタンは、入力時に停止または一時停止であれば、受理後にプレイヤーの表示面を要求します。

| 入力・結果 | 面の要求 |
| --- | --- |
| 再生中からの一時停止 | 要求しない。 |
| 競合による拒否、行や形式の検査による拒否、終了後 | 要求せず、面と保存された表示設定を変えない。 |
| 空選択の開始（競合以外） | 面の選択の意図を保ち、受理後の起動失敗や取消で巻き戻さない。 |

面の要求は、要求の中で一度だけ既存のDispatcherへ投稿します。

- 新規開始では、候補の検査と必要なLの取得の後、次回の再生方式の適用を試みた後、再生の開始より前に投稿します。置換に成功した場合は、実際のプレイヤーの能力が利用可能になってから配送される順序にします。適用に失敗しても、受理済みの一回の投稿は保ちます。
- 再開では、拒否の確認の後、一時停止の切替の前に投稿します。
- 複数の候補を飛ばしても、一要求につき一度だけ、曲開始の直列化の受付順に配送します。
- 表と再生ボタンの入口は、要求のTaskを呼出元へそのまま返し、UIイベント側は既存の故障の観測へ接続します。要求のTaskは選曲の終端を表し、面要求のUI配送は待ちません。
- 投稿・実行の故障は局所診断で止め、再生結果を変えません。画面は接続期間と閉鎖状態を確認し、切離しと再接続の後に古い面要求を適用しません。

`PlaybackStarting` は、選択行へのスクロールだけを行います。同じ主行の明示的な再生にも使い、後に受理した主行の可視化を最終結果にします。

内蔵プレイヤーの準備完了（Ready）を含む `PlaybackStarted` をUIで受信した時点で、`MainWindow` が終了中でなければ、選択済みの表示面を同期的に戻します。実際のメイン画面のウィンドウが前面のウィンドウと一致する場合だけ、表へのキーボードフォーカスを同期的に一度要求します。前面が一致しなくても表示面は戻します。受信後にDispatcherの予約や再試行を残さず、後から始めた検索やセル編集を古い要求で奪いません。受信時点の終了状態、再生の管理主体の世代、画面の接続期間で古い通知を拒否します。

#### 一時配置と受付の拒否

一時配置が必要な保留譜面では、候補を解決した後にLを待たずに取得してから、再生状態・選択・表示・開始イベントを更新します。開始・次へ・前へ・行の起動のいずれでも、Lの取得が拒否された場合は、プレイヤーの生成も交換も行わず、次を保ちます。拒否した要求の予約や自動再開は行いません。

- 旧曲、位置、ヘッダー、`PLAY`、選択、終了コールバック
- 面と保存された表示設定
- 保留中の次回の再生方式

一時試聴の配置先、譜面名の決定、コピーの公開と回収、Lの保持期間は、[音声の一時導入試聴](../runtime/audio.md#一時導入試聴)を正本とします。

#### 開始・停止の世代

開始要求は、曲開始の直列化に入った後、次回の再生方式の生成や置換の待機より前に、現在の再生世代を捕捉します。次の待機中に停止を受けた要求は、失敗を通知せずに開始を中止します。

- 旧プレイヤーの終了の待機
- 採用後のUI配送の待機
- 開始状態の確定前

中止の判定は次のとおりです。

- 最後の失効の検査と現在曲の確定は、同じ再生状態の排他の中で行います。開始中に引き継ぐ世代の更新は、置換と同じ要求が不正な候補を飛ばした場合だけです。
- 先行する次へが正常に世代を更新した後に直列化へ入った次の次へは、通常どおり実行します。
- 停止がプレイヤーの採用前なら、次回の再生方式を次の明示的な開始まで保持します。採用後なら採用済みのプレイヤーを保ち、同じ生成手段を再実行しません。
- 待機中に保存された別の方式を消費せず、中止した開始から新しい要求へ乗り換えません。
- 一時配置でも同じ判定を使い、開始の確定前に失効した場合はコピーせずにLを解放します。
- 受理済みの面要求の一回の配送、能力とホストの接続、旧プレイヤーの終了の待機は保ちます。生成・状態取得・終了の実際の故障は、取消に読み替えません。

明示的な停止は、UIで受け付けた時点で、現在曲と、停止前に受理した未開始の入力を失効させます。

- 停止の境界は通常の再生世代とは分けて捕捉し、ワーカーでの開始順では受付の前後を判定しません。曲開始の直列化に入った後に停止の境界を照合してから世代を捕捉するため、停止を挟まない連続した次へ・前へ・行の起動は順に実行します。
- 失効した未開始の要求は、候補の探索、次回の再生方式の生成と採用、一時コピーを始めず、別の候補へも進みません。
- 停止の後の明示的な入力は、通常どおり開始できます。

停止処理は、受付時のプレイヤーと停止直後の世代だけを保持し、プレイヤー操作の排他を取得した後も両方が一致する場合だけ終了させます。

- 新しい開始が先に採用された場合、遅れて届いた旧い停止は、その再生を止めず表示も更新しません。
- 空選択の開始は、取得済みの世代が一致して現在曲が残る場合だけ状態を解除し、停止が捕捉した終了処理を後から失効させません。停止後の空選択の入力が、曲を開始せずに世代だけを更新することはありません。
- 旧プレイヤーの終了処理が先に排他を取得した場合は、その終端まで新しい開始が待ちます。
- 実際に始めた準備・終了・コピーの回収の故障は保持し、通常の取消では再生失敗を通知しません。

#### 譜面の変更に伴う停止

譜面や音源の物理変更では、現在曲と先読みをすべて停止します。変更に伴う停止も受付時に失効させ、曲開始の直列化の中で、一時コピーの回収と変更受付の解放へ合流します。

- 変更側が共通の受付を保持している間は新規の開始を拒否し、自然終了による次曲送りも停止に変えます。変更後の自動再開と先読みの再登録はありません。
- プレイヤー操作の排他を取得した後は、変更側が現在のプレイヤーを終了の終端まで所有します。その間のUIの停止による世代の更新を解放済みの根拠にせず、終了の故障や終了受付で、変更前の停止を成功として返しません。
- 通常のUIの停止による後続曲の保護と、この実資源の停止の責務は分けます。自然終曲・通常の置換・候補探索での表示の解除は停止の境界を進めず、アプリの終了受付は入力を閉じて回収します。

行の取得は一覧側の寿命の中で済ませ、ワーカーへ渡すのは入力一件だけです。曲開始は準備完了と次候補の登録までを直列化し、演奏全体は待機に含めません。一覧のモードや行の変更の通知から、先読みのTaskを差し替える処理は持ちません。停止・変更・交換・終了での資源の終端は、[音声の次曲一件の先行準備](../runtime/audio.md#次曲一件の先行準備)に従います。

#### 開始成功と自然終了

内蔵プレイヤーの開始成功は、[次曲の先行準備](../runtime/audio.md#次曲一件の先行準備)の準備完了（Ready）で判定し、成功した時点で `LOADING` から `PLAY` へ移します。

- 演奏の終了やUIの描画を待たず、開始した曲の手動送り先一件を準備します。通常の開始も先読みの採用も、同じ出力開始の確認を通ります。
- 準備完了での開始失敗を完了（Completion）から再通知しません。短い曲でも、終了コールバックと完了をそれぞれ一回だけ扱います。
- 自然終了を受け付けた時点の世代・プレイヤー・譜面を保持し、曲開始の直列化を待った後に再確認します。先行する手動送りで現在曲が変わっていれば、古い自然終了は次曲送りも、変更中の停止も行いません。手動送りの受付順は保ちます。

外部プレイヤーには先読みの能力を要求しません。旧プレイヤーの操作が終わった後、`PlayStart` の同期呼出しを終えた時点を選曲側の開始の境界とします。返された未完了のTaskは終了・失敗の観測へつなぎますが、選曲の入力や一時コピーをその終了まで保持しません。内蔵プレイヤーの一時コピーは、準備完了の終端まで保持します。

開始に失敗した場合も、コピーの除去と元パスの復元を行います。

- コピー・公開・開始の主な失敗を、元の名前の復元やプレイヤーの停止の二次的な失敗で置き換えません。
- 開始の失敗は、コピーの回収・元の名前の復元・変更受付の解放まで保持します。独立した回収・復元の失敗があれば、元の開始原因と `AggregateException` にまとめて一回通知します。
- 入力不良だけが残る場合は、同じ候補列の探索へ戻します。独立した回収・復元の失敗を含む場合は、次の候補へ進みません。
- 停止の二次的な失敗は、停止対象の譜面のパスとともに既存の診断へ残します。主な失敗がない復元の失敗は、呼出元へ返します。

外部プレイヤーの設定ファイルの準備と復元は、[外部起動のINI保全](external-launch.md#bmiidxview2015とubmplayのini保全)に従います。自然終了での復元の故障は一度通知して、自動の次曲送りを止めます。古い世代の故障で新しい曲や表示を消さず、故障の通知そのものは行います。

自然終了での復元の故障を最外の開始・停止要求へ返す場合は、同じ要求の終了引数と、専用の変更不能な例外の包装を対にして、呼出元が通知を所有することを示します（`FailureNotificationOwnedByCaller`）。

- 返却する例外全体を `InnerException` に保持し、独立した原因とパスを失いません。
- パネルは、包装の内側も含めてこの通知の責務を識別し、実際の停止・終了で開始の観測が失効した後も、一回の通知と一回の主な診断を行います。
- 表示の解除と、観測済みの故障の重複抑止は、通常の世代の判定に従います。この扱いを、通常の古い外部開始の故障の通知条件には広げません。

#### 先読み

先読みの基準は現在の再生対象です。一時停止・再開、シーク、ハイライトの変更では候補を変えず、一覧の差替え・並べ替え・再生モードの変更でも先読みを再評価しません。

- 次の実際の対象は通常の選択で決め、保持した準備と一致する場合だけ使います。一致しなければ通常のロードを待ち、古い先読みの候補へ送りません。
- 単曲リピートでは手動送りの先を優先し、第二の候補は持ちません。
- 一時配置が必要な候補は先読みせず、さらに先も探しません。

先読みの致命的な故障は、準備のTaskを引き取った開始・停止、または未消費のTaskを監視する背景処理が報告します。背景処理は停止と後片付けを終えてから通知し、パネルは、捕捉した曲がまだ現在曲である場合だけ表示状態を解除します。

| 状況 | 扱い |
| --- | --- |
| 新しい明示的な再生と重なった遅い故障 | 原因を隠さず通知するが、新しい曲の状態は消さない。 |
| 通常の入力不良 | 実際に採用するときの既存の失敗処理へ渡す。 |
| 自然終了からの停止が準備のTaskを引き取った | 故障を一回通知し、停止表示を保ち、次の曲へは進まない。 |
| 実際の開始へ引き渡した準備の致命的な故障 | 停止で開始時の表示世代が失効していても、開始側が通知する。表示の解除は現在の開始に限る。 |
| 通常の取消 | 故障として通知しない。 |

#### 送り先の探索

前へは後方向、次へと先読みは前方向に候補を探します。

- 行を調べる時点で一巡の残数を消費し、未所持・存在しないパス・対象のプレイヤーが非対応の形式を除いてから、フォルダを比較します。
- 開始時の同期的な例外と、完了済み・遅延した準備完了の入力不良は、先読みと同じ `NextSongPreloadOwner.IsInputFailure` で判定します。`InvalidBmsonFileException`、入力に由来する `InvalidBmsFileException`、既存の入力I/Oなどだけを一回通知し、同じ探索位置から続けます。分類外のBMS・ネイティブ・メモリ不足・独立した後片付けを含む集合例外は入力不良に読み替えず、停止します。
- 探索は、絶対添字0や末尾を一巡の終端に読み替えません。
- 単発の試聴には新しい探索列を作りません。繰り返しがOFFなら折り返しません。
- 一覧の縮小や停止・終了で失効した要求からは新しい再生を始めず、元の要求・後続の操作・終了を終端させます。探索中も検索や一覧の操作は禁止せず、一覧全体を複製しません。探索中のモード変更は次の要求から有効です。

候補が見つからない場合の扱いは次のとおりです。

| 状況 | 扱い |
| --- | --- |
| 現在の行が消えた手動の次へ・前へ | 現在の再生を保ち、何もしない。 |
| 現在の行が消えた通常の自然終了 | 終曲として停止する。 |
| 単曲かつリピートがON | 一覧に位置がなくても現在の譜面を一回直接再開する。その再ロードで入力不良なら停止し、別の曲へ進まない。 |
| 手動の次へ・前への初回探索で候補がない | フォルダ送りのON/OFFや不備の種類によらず何もせず、現在曲・`PLAY`/`PAUSE`・選択・次回の再生方式を保つ。 |
| 自然終曲・明示的な開始で候補が尽きた、実際のロード後の入力不良で候補が尽きた | 停止する。 |
| 通常の先読みで候補がない・入力不良 | 現在曲を停止しない。 |
| 一時配置へ進んだ後、回収・復元・変更受付の解放を終えて入力不良だけが残った | 元の探索位置から続ける。回収・復元に独立した失敗があれば、元の入力原因と合わせて保持して停止する。停止で失効した要求からは、通知も次の候補も再開しない。 |

単曲の設定は、自然終了時の停止と反復だけに適用し、手動送りは制限しません。

フォルダ送りがONなら、最後に成功した送り元の曲のフォルダを要求の間は固定します。フォルダは `Path.GetDirectoryName` の結果を `OrdinalIgnoreCase` で比較し、新たなパスの正規化は加えません。

- A現在→B不良→A別→Cの並びではCへ進みます。A現在→B不良→B別でB別が正常なら、B別へ進みます。
- 明示的な開始と行の起動は、初期対象をフォルダによらず優先し、その失敗後は初期フォルダを固定します。
- リピートの一巡の終端にある元の行だけはフォルダによる除外を免除し、全行が同じフォルダでも現在曲を一回候補にできます。
- 繰り返しがOFFのとき、一覧の端での手動送りは何もせず、次回の再生方式も消費しません。単クリックの再始動は変わりません。

### 入力単位の再生観測

明示的な検証起動では、通常の入力から呼出しごとの受取先を渡し、通知の包装の内側にある実際の再生Taskと、開始直後の準備完了・完了・世代・変更不能な元パスと再生パスを捕捉します。自然終了のコールバックへ持ち越さず、共有のイベント登録や経路の文字列で後続のTaskを対応付けません。外部プレイヤーには疑似的な準備完了を作りません。

- 拒否・取消・処理済みの故障・結果記録の不在を、開始の成功にしません。入力不良から同じ探索位置で別の候補へ進んでも、最初の不成功を保持します。
- 検証の操作は、直前の開始と準備完了の後に捕捉した実際の再生一件だけを、明示的な停止で消費します。自然な完了の後のパネルの世代の変化を、停止を拒否する条件にしません。
- 通信の終了は観測の参照を破棄するだけで、再生の停止は通常の終了処理が所有します。
- 一時試聴では、準備完了の後に所有したコピーが回収されることを許容します。

## 実装とテストの対応

操作帯の共通スタイルと4 / 8 DIPの間隔は、`PlaybackPanelView.xaml` の静的確認で扱います。表示面、ホスト、高さアニメーション、シーク、音量の保証は次の表のテストが分担します。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 現在曲identity・現表示位置、行消失、FolderSkipの固定基準、Repeatの一巡と入力不良 | [`MainChartListPlaybackQueue`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackChartQueue.cs)、`PlaybackPanelViewModel.FindNextPlaybackCandidate` / `StartAtIndex` | `Navigation_CurrentIdentitySurvivesSortAndFilterWithoutMergingDuplicateCharts` は実通常/playlist viewで同hash別path・同chart別entryと並替・絞込後の送りを確認する。`Navigation_RemovedCurrentRowKeepsManualPlaybackAndSingleRepeatTarget` は行消失時の手動・自然終了・単曲反復/不良停止を、`Navigation_FolderSkipPreloadAndNextIgnoreUnownedRowsBeforeFolderComparison` は先読みと実Nextを確認する。`Navigation_FolderSkipKeepsSuccessfulOriginAcrossInputFailures` は同期例外/遅延Readyとfolder大小文字差、`Navigation_ExplicitStartPrioritizesInitialRowAndKeepsItsFolderAfterFailure` は明示初期優先、`Navigation_RepeatWrapContinuesAfterFirstInputFailureAndStopsAfterOneFailedCycle` は手動/自然折返しと全不良一巡を分担する。`MainChartListPlaybackQueue_RejectsAnotherEntryMovedToResolvedPosition` と `MainChartListPlaybackQueue_CapturesChartAndLocatorFromReturnedRowDuringReorder` は同chart別entryの並替え中の位置照合と同じ行からの捕捉を、`MainChartListPlaybackQueue_ResolvesTokenlessSubsetItemAtItsCurrentPosition` はtoken/packageなし実subsetの既存索引への接続を確認する。既存の候補長列・方向・次回player捕捉・queue交換寿命・自然終了callback・WPF activationケースは維持する。 |
| UI受付時の停止境界、待機入力の取消、遅いCloseと新規入力 | `PlaybackPanelViewModel.AcceptTableRowActivation` / `AcceptStartPlayback` / `AcceptNextPlayback` / `AcceptPreviousPlayback` / `AcceptStopPlayback` / `RunPlaybackSelectionAsync` | `QueuedRowActivation_StopCancelsAcceptedRowsAndPreservesNextPlayer` は実HandleのReady待ちB/C→Stopと全Task終端・無通知・次回factoryの保全を確認する。`AcceptedStop_DoesNotCloseLaterStart` は同じ本番受付でD→旧Stop→B/Cの実行順と、旧Close先行時のBeginStart待機をClose呼出しと開始列で区別する。`EmptySelectionStart_AfterStopDoesNotInvalidateCapturedClose` は通常開始Aの停止受付後に空選択Playを制御し、再Stopなし・ありの両条件でPlay終端後もAのClose到達・実終端と受理済み面要求を維持する。`AcceptedRowActivations_KeepEachStartWithoutStop` と既存 `ReceivedNextRequests_KeepEachStartWhileEarlierReadyIsPending` は通常の逐次開始を保つ。既存 `TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion` のReady途中Stop代表はコピー・元名・非所有物・変更受付の回収と明示再試聴を確認する。compiled UIからHandleへの転送は既存 `PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner` が分担する。 |
| 次回playerのClose待ち・採用後UI待ちでの停止 | `PlaybackPanelViewModel.RunPlaybackSelectionAsync` / `ReplacePlayerCoreAsync` / `BeginPlaybackCore` | `PlaybackPanelViewModelTests.PendingPlayerReplacement_StopCancelsStartWithoutConsumingUnadoptedOrNewRequest` は通常/一時配置×採用前/後×新保存有無で、通知/開始なし、両Taskの終端、面要求、コピーなしとL解放、次の明示開始での保持/再利用を確認する。`PendingPlayerFactory_StopBeforeReturnKeepsRequestForExplicitStart` はfactory中の失効、`PendingPlayerReplacement_StopDoesNotSuppressOldCloseFailure` は実故障通知を分担する。受理済みNextと不正候補送りは既存の専用ケースを維持する。 |
| 次の明示再生へのplayer方式の採用・失敗時保持 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) の `UsePlayerForNextPlayback` / `ApplyPlayerForNextPlaybackAsync` | `PlaybackPanelViewModelTests.ConfirmedPlayerFactoryIsUsedOnlyAtNextPlaybackStart` は一時停止での現再生維持と次の明示開始での一回採用、`ConfirmedPlayerFactory_ApplyFailureKeepsCandidateForNextExplicitStart` は通常/一時配置の実交換準備失敗後の候補保持・通知/要求終端・自動再試行なし・L解放と次明示開始での再採用を確認する。`TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput` は受付拒否でfactoryを消費せず旧playerを終了しない保証、既存の直接交換失敗ケースは旧session/購読の保全を分担する。 |
| 面要求の入力条件、非待機配送、標準画面への接続 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | `PlaybackPanelViewModelTests.SurfaceRequest_UsesInputStateAndKeepsAcceptedIntent` は停止開始・再開・Pause・受理後故障/取消・空選択、`SurfaceRequest_PendingPlayerReplacementSelectsAvailableSurface` は通常配置の両入口と一時配置の行起動で、旧playerの解放待機中に実Dispatcherを動かしてから実BMIIDXViewへ交換し、実Viewによる一回の面配送・実効面/保存状態・対象pathの一回開始を確認する。`SurfaceRequest_PlayButtonKeepsInputConditionWhileEarlierReadyIsPending` は先行Ready待ち中の入力条件、`SurfaceRequest_QueuedFifoAndOptionalFailureDoNotChangePlayback` はUI未配送での後続完了・FIFO・任意通知故障、既存InternalReadyとReloadRejectsケースはReady/skip/接続期間との独立を確認する。[`MainWindowPlaybackWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaybackWpfTests.cs) の `PlaybackSurface_ChangesOnlyAfterAcceptedExplicitInput` は標準terminal/実Dispatcher/compiled画面を使い、両入口の実Task終端での競合拒否後も面・保存値が不変で、L解放後の次の明示入力だけが切り替えることを確認する。`PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner` はcompiled表のキーから同じ行activationへの行とindexの転送を担う。 |
| 一時試聴の受付拒否、自然終了復元故障 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | `PlaybackPanelViewModelTests.TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput` は候補解決中の実L先取りによる各入口の状態・面要求保全（行activationはPause、再生ボタンは現在曲なし）、`NaturalExit_UbmplayRestoreFailureNotifiesOnceWithoutAdvancing` は実uBMplayとの自然終了単独・旧main通知未配送での新曲開始・現開始中の同期終了、実復元故障後の返却前Stop/Shutdownによる失効の一通知/一主診断、元原因/path、起動阻止と明示再試行、`NaturalExit_StaleFailurePreservesReplacementAndStillNotifies` は旧世代の表示保全と故障通知を確認する。 |
| 異なるDPIの実モニターでの描画・入力 | [`ExternalPlayerHwndHost`](../../../BeMusicSeeker/Views/Playback/ExternalPlayerHwndHost.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | 下記の自動テストは実行時DPIで配置・寸法・HWND寿命を検証する。異なるDPIのモニターでの描画・入力位置とモニター移動後の操作は[利用者に推奨する実機確認](../development/testing.md#利用者に推奨する実機確認)へ分ける。 |
| 譜面の表示対象変更、素材A→素材B→素材なし | [`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) の `DisplayedChart` 通知と `RefreshArtwork(ChartFile)` | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelView_ChartSelectionReplacesArtworkAndRestoresDefaultWhenAbsent` は同じcompiled Viewで全10テーマ往復の既定画像をCopyPixelsの内容で比較し、操作色の更新、外部・stage表示の明暗往復での画素・参照保持、実選曲A→B→素材なしとDataContext解除による画像・バナーの除去を確認する。 |
| 同じ選択集合で主行だけ変わる停止時表示、再生中の現在曲優先 | `CustomTableSelectionModel` から標準 `MainWindowPlaybackTerminal`、`PlaybackPanelViewModel.HandleTableSelection` への接続 | `MainWindowPlaybackWpfTests.RangeSelection_UpdatesStoppedHeaderAndKeepsPlayingChartThroughStandardTerminal` はcompiled実表のCtrl+A→Shift下から題名・字幕・作者の実TextBlockと `DisplayedChart` の更新を確認し、標準入口の実開始Taskで先頭曲を再生した後の同じ選択操作では先頭曲表示を保つ。集合/主行/基点の判定と表の一回通知は `CustomTableSelectionModelTests` / `CustomTableNavigationTests` に分担する。 |
| 再生候補採用による現在セルと編集・コピーの対象追従 | `PlaybackPanelViewModel` の候補採用から `MainChartList.SelectedIndex` / `CustomTableView.OnSelectedIndexChanged` への既存Binding | `MainWindowPlaybackWpfTests.NextPlayback_FollowsCurrentCellThroughBindingForDisplayEditAndCopy` は標準terminalとfake backendの実Next Taskとcompiled BindingのB主行到達、描画済み現在列と表示値、実F2とCtrl+Cから同じB列への接続を分けて確認する。旧A入力の一回終端、同期詳細整列と最新主行の保持、画面外可視化と同値保持は `CustomTableEditingTests` / `CustomTableNavigationTests`、Next/Previous/自然終了の候補採用条件は既存 `PlaybackPanelViewModelTests` に分担する。主行不変時の現在セル・未確定editor・確定0回は `PlaybackStateChangesRefreshLoadedTableWithoutReplacingRowsOrCommittingEditor` が確認する。 |
| 正常Aから破損パネル画像・破損バナー・両素材の破損/欠落、設定外部画像の欠落/破損 | [`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) の `RefreshArtwork` / `ApplyPanelImage` / `ReadOptionalArtwork` | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelView_ArtworkFailureKeepsCurrentSelectionAndPlayback` は実選曲・コンパイル済み画面・実復号処理から前曲除去、同曲パネル画像または既定画像の画素、背景と伸縮方式、元パス・原因の診断と現在曲・`PLAY`・一回の開始を確認する。バナーbrushの二次故障と診断の独立、`DataContext`検査の位置は静的に確認する。 |
| フラット描画、操作色とfocus装飾なし | [`PlaybackPanelView.xaml`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml)、各テーマ辞書 | `PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader` は実効操作前景、統計・曲名の背景と白文字、影なし・focus装飾なしと入力可能性を確認する。全テーマの独立キー・色値、hover/pressedの接続とfocus枠なしは静的確認する。 |
| 0〜100%の音量表示と固定配置、preview・完了時保存 | 共通の `App.Canonical.CompactSliderStyle` と `PlayerVolume*` 完了イベント | `PlaybackPanelView_VolumeMenuCommitsOnMouseCompletionAndShutdown` は実Popupで各桁数の表示・Slider左右位置/幅・実railとthumbの中央・focus装飾なしを確認し、マウス操作の完了時の保存と終了時の保存を確認する。 |
| 実Trackによる時刻換算、前面の描画・入力、双方向Binding、seek前のPLAY/PAUSE保持 | `PlaybackPanelView.xaml` のseek前面化、`PlaybackPanelView.SeekFromPoint` / `sliderPlayerMouseMove` | `PlaybackPanelView_SeekPreservesTrackBindingAndPlaybackState` は実配置でのつまみ以外のInputHitTest・重なり画素、幅とthumb幅の差・端点・範囲丸め、player通知と双方向Bindingを確認する。PLAY中の連続seek、明示Pause後のclick・連続seek・反復mouse-upで位置と状態を確認する。seekからの非同期toggle、mouse-up・押下mouse-leaveのtoggle接続の不在は静的に確認する。既存の `PlaybackPanelView_ReloadRejectsEventsQueuedByThePreviousSubscription` と `PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart` が購読と捕捉済み入力の寿命を、[`BmsRealtimeAudioSchedulerTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/BmsRealtimeAudioSchedulerTests.cs) の `PauseSeekAndRateChangesKeepTheRealtimeGraphFrozenUntilResume` と `RealtimeTickAndControlsAreSerializedThroughChartStateApplication` がengineの状態・時計保持を分担する。 |
| 再生方式の次回適用、形式能力、現在の操作能力と表示面 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[設定の局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) | `PlaybackPanelViewModelTests.ConfirmedPlayerFactoryIsUsedOnlyAtNextPlaybackStart`と非対応bmsonの直接起動ケースが選曲・現在再生の維持を、`PendingPlayerReplacementRetainsFailuresAndNewerRequests`が置換失敗・後続要求保持を、`QueuedRowActivationRechecksPendingCapabilityWithoutChangingCurrentHeader`が実行順を待った後の明示行拒否、現在曲・選択・ヘッダー・面の維持と後続bms開始での要求残存を、`InternalReady_InvalidInputNotifiesAndSkipsOnce`が同じ開始のskip中に次回要求を消費しないことを確認する。[`SettingsDialogBehaviorTests.CompiledPlayerSelection_AutomaticallySavesAndDefersFactoryUntilNextPlayback`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsDialogBehaviorTests.cs)が実選択から実置換時のcontrol/表示面更新を分担する。 |
| 共通譜面キュー、現在曲表示、再生状態投影、外部playerの能力 | `PlaybackPanelViewModel.NowPlayingChart` / `GetPlaybackStatus`、[`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs)、`PlaybackChartQueue` | `PlaybackPanelViewModelTests.PlaybackPanelChartQueueKeepsIdentityStatusAndSingleAdvance` は混在入力を代表として現在対象・ヘッダー・PLAY/PAUSE・一回の送り・旧対象の状態解除と `SCORE_UNSENT` / `SEARCHING` の維持を、`PlaybackPanelDoesNotSendBmsonToAnExternalPlayerWithoutThatCapability` は外部能力の分離を確認する。 |
| 要求状態と実効状態、初期フレーム、差替え・再読込み、遷移 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | [`PlaybackPanelViewModelTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)、[`PlaybackPanelViewModelTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playback/PlaybackPanelViewModelTests.cs) |
| 初期Collapsedでの子HWND生成、UIスレッド・プロセス所有 | [`ExternalPlayerHwndHost`](../../../BeMusicSeeker/Views/Playback/ExternalPlayerHwndHost.cs)、`PlaybackPanelView.PlayerHostHandle` | `PlaybackPanelViewModelTests.PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader` は `ContentRendered` 後の継承 `Handle`、親、PID・TIDを確認する。 |
| 面切替・Overlay・切離しと再接続での寿命、物理寸法と予約領域 | `ExternalPlayerHwndHost.ArrangeOverride`、[`PlaybackPanelView.xaml`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml) | `PlaybackPanelViewModelTests.PlaybackPanelView_PlayerSurfaceAndOverlayPreserveHostAndPhysicalLayout` は実再生入口から面切替・Overlay・拡大型の中央下端配置と実行時DPIでの物理寸法、小型の要求・実効状態と再読込み時の即時同期・同じHWNDの生存を確認する。`PlaybackPanelView_SameViewModelStateChangesUseTransitionsAndReloadSynchronizesImmediately` は切離し・再接続の寿命と遷移を確認する。固定寸法より小さい `finalSize` の本番到達は確認しておらず、min式は実装レビューで確認する。 |
| 通常初期化完了からのホスト接続、プレイヤー終了待ちと最終解放 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowViewHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowViewHostTests.cs) の `MainWindowRenderedInitializationAttachesCreatedPlaybackHost` は `ContentRendered` を入口とする接続を、`MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose` は終了gate保留中のUI応答・HWND生存と最終Closed後の無効化を確認する。 |
| 再生通知のDispatcher所有threadへの投稿 | [`WpfPlaybackUiDispatcher`](../../../BeMusicSeeker/Views/Playback/WpfPlaybackUiDispatcher.cs) | [`WpfPlaybackUiDispatcherTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/WpfPlaybackUiDispatcherTests.cs) の `Dispatch_PostsActionToDispatcherOwnerThread` はWindowなしの共有Dispatcherを使い、投稿元Taskと受信側actionの実完了を分けて所有threadへの一回到達を確認する。Dispatcher終了時の挙動は専用STAの既存ケースが分担する。 |
| 内蔵Readyを含む開始通知での表示面復帰と表への一回のフォーカス要求 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `RestorePlaybackSurfaceAndFocusTable`、`PlaybackPanelView` の開始通知接続 | `MainWindowPlaybackWpfTests.InternalReady_RestoresTableOnceAndPreservesLaterSearchOrMemoInput` は実表入口・内蔵Ready・実MainWindowのHWND照合を通し、入力なしの初回要求と後発検索・本番受付のMEMO編集への追加要求なし、文字列・候補・選択・未確定状態を確認する。`InternalReady_ForegroundMismatchRestoresSurfaceWithoutRequestingTableFocus` は保存済み題名面の実表示を保ち、前面不一致で要求しないことを確認する。OS前面値とフォーカス要求だけを代替し、物理OSフォーカス取得・LostKeyboardFocusは実機確認へ分担する。外部表示面は既存の `PlaybackSurface_ChangesOnlyAfterAcceptedExplicitInput`、終了後・再接続後の旧通知抑止は `PlaybackPanel_DispatchesTelemetryAndSuppressesStalePlaybackEventsThroughUiBoundary` と `PlaybackPanelView_ReloadRejectsEventsQueuedByThePreviousSubscription` が分担する。MainWindow受信終端に遅延要求が残らないことと終了guardは静的に確認する。 |
| Ready成功時のPLAY・一回の次候補、一時コピー寿命、停止の終端 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`MainChartListPlaybackQueue`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackChartQueue.cs) | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)の`InternalReady_StartsNextPreparationBeforeCompletionOrUiPublication`、`TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion`、`FileMutation_StopsCurrentSongBeforeWritingAndRejectsPlaybackWhileBusy`で開始・入力・停止の境界を確認する。変更前停止は成功・Close故障に加え、Ready待ち中の停止受付後にUI Stopを遅延実行する代表で、Closeの実終端前に物理変更へ進まないことを確認する。L・入力gate・player操作gate内の停止所有とshutdown時の成功返却阻止は静的確認へ分担する。[`AudioContractsTests`](../../../tests/BeMusicSeeker.Windows.Tests/Playback/AudioContractsTests.cs)の`PreloadFatal_CloseJoinsPreparationAndNotifiesOnceWithIndependentCleanupFailure`で実内蔵playerの背景故障通知を確認する。 |
| 停止で表示が失効した後の採用済み準備故障の通知 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)の`AdoptedPreloadFailure_AfterStopNotifiesOnceWithoutRestarting`でB開始が準備中にStopを受けた後の元原因の一回通知、次へ・停止の終端、自動再開なしと停止表示を確認する。 |
| 手動送り待ちの後に残った旧曲の自然終了 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)の`NaturalExit_QueuedBehindManualNextDoesNotAdvanceTheReplacementSong`でA候補解決中に手動・自然終了・手動を受け付け、A・B・Cだけを開始することを確認する。 |
| 前曲の除外・開始時入力不良後の方向と、一開始内の次回方式捕捉 | `PlaybackPanelViewModel.NavigatePlayback` / `FindNextPlaybackCandidate` / `StartAtIndex` | `PlaybackPanelViewModelTests.Navigation_UnavailableCandidatesKeepRequestedDirection` は未所持・非対応BMSON・欠落・連続除外・候補なしを実一覧と非BMSON player能力で、`Navigation_InitialEmptyKeepsManualPlaybackAndPendingPlayerButNaturalExitStops` は同folder正常曲中央からの前後送りと自然終了、未所持・別folder欠落・非対応形式の手動候補ゼロ、混在の自然終了を確認し、手動ではPLAY/PAUSEと追加Start/Closeなし・次回factory未実行を保ち、次の正常な明示Startで要求を一回採用する。`PlaybackPanel_StopsAfterAllRepeatCandidatesAreUnavailable` は正常再生後の明示Startで一万件全利用不能の有限停止と現在曲解除を、`Previous_LegacyInvalidStartContinuesBackward` は外部と同じ開始Task境界と入力不良後に後続が欠落だけとなる停止を、`Previous_InvalidReadyContinuesBackwardWithoutRecapturingPlayer` は同期・非同期Readyと新しい次回方式の保持を実開始列で確認する。前方向のReady通知・面要求は既存の `InternalReady_InvalidInputNotifiesAndSkipsOnce`、単クリックRestart・二重クリック前曲は `PlaybackPreviousButtonGestureTests` が分担する。 |
| 前曲ボタンの時計とViewの接続期間 | `PlaybackPanelView.HandlePreviousButtonClick` / `CancelPreviousButtonGesture` | Windowsの `PlaybackPanelView_UnloadedCancelsPendingPreviousButtonRestart` は実WindowのLoaded・DataContext交換・Unloaded・再接続を通し、明示時計の停止と購読解除、旧時計からの配送受信ゼロ、新旧VMと再接続後の正のRestart、旧通知の排除、バナー除去、背景通知のUI配送を確認する。停止だけで購読を消す代替時計や固定時間の負の観測は使わない。`PlaybackPanelView_DefaultPreviousButtonTimerRestartsPlayback` は公開既定構成の実時計からRestart通知への接続を確認し、500ms・優先度・Dispatcherの指定は静的に確認する。クリック回数と遅延経過の判定はCoreの `PlaybackPreviousButtonGestureTests` が分担する。 |
| 前曲の有限探索とStop・終了、捕捉したモード | `PlaybackPanelViewModel.NavigatePlayback` / `FindNextPlaybackCandidate` | `PlaybackPanelViewModelTests.Previous_ListShrinkAndInvalidationTerminateNavigation` は実queueの行取得とrowGate解放後に5→3行の縮小とStopまたは終了を挿入し、元要求・次明示開始・Closeの終端と新曲・開始イベント・通知なしを確認する。`Previous_FolderTraversalUsesCapturedModes` は後方最寄りの別実フォルダ、前後方向の全行同フォルダの一回開始、探索中のモード変更が次要求へ適用されることを確認する。先頭の無作用・次回方式非消費、Next版の終了・queue寿命は既存ケースで分担する。 |
| 自然終了の停止へ渡った先読み故障の一回通知 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)の`NaturalSinglePlayStop_NotifiesPreloadCleanupFailureOnceWithoutStartingNextSong`でA開始後のB準備、自然終了callbackとCompletionからの単曲停止、元原因の一回通知、B開始なしと停止表示を確認する。 |
| 外部playerの停止中に受け付けた開始、一時コピー寿命、開始呼出しと返却Taskの失敗 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)の`LegacyStart_WaitsForPreviousCloseAndRetainsInputUntilInvocation`で通常・一時配置の双方について旧playerの終了前の開始禁止、呼出し時の入力保持、未完了Taskを待たない選曲完了、開始呼出し失敗・開始後の失敗の一回通知を確認する。 |
| 一時改名時の配置先維持、元ファイルの復元、一時コピーの除去 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`ChartListVirtualViewTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartList/ChartListVirtualViewTests.cs)、[`ChartListVirtualViewTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartList/ChartListVirtualViewTests.cs)の`PlaybackPanel_StartAtIndexUsesChartInstallDestinationWhenTemporaryRenameChangesPath`で共通Dispatcherを使い、開始Taskの完了後に再生先pathとファイルの復元・除去を確認する。 |
| 相対素材・同名別フォルダ・既存素材の保持、再試聴、外部開始後の読取り | `PlaybackPanelViewModel.StartTemporarilyInstalledChart`、`temporarilyCopyFiles` | `PlaybackPanelViewModelTests.TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion` は通常発見した入れ子パッケージで衝突なし・入力元兄弟・両側複数・ディレクトリ占有からの開始名、元・兄弟・既存宛先のバイト内容、相対素材・別フォルダの同名素材・パッケージ外素材の保全、`Ready` 前の実要求TaskとLの保持、`Completion` 前の回収を確認する。衝突なしの停止後とディレクトリ占有の代表での `Ready` 故障後には明示再試聴を確認し、故障の元原因を一度だけ通知する。`TemporaryCopy_ExternalDelayedReadKeepsNestedChartAndRestoresOriginalName` は外部実開始後のgateで入れ子譜面と相対音源を読み、元名の復元と既存宛先保全を確認する。`TemporaryCopy_ResidueNotifiesOnceAfterCleanupWithoutChangingPlayback` は開始主原因と元名衝突の二次診断、非所有の元名fileと退避元の保全、L解放後の残留通知を確認する。`TemporaryCopy_CopyFailurePreservesPrimaryWhenStopAlsoFails` は実素材の読取り禁止と停止故障が重なってもcopy元原因・対象pathを保持して回収することを確認する。`TemporaryCopyFilesTests.Dispose_PreservesRelativeStructureExistingMaterialsAndUnownedFiles` と `CopyFailure_JoinsCopiesAndRemovesOnlyOwnedDestinations` は所有範囲と失敗回収を分担し、多数のファイルの並列追跡を確認する。 |
| Pause・シーク・一覧・モード変更では準備を差し替えず、実際の送り先を優先すること | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../tests/BeMusicSeeker.Core.Tests/Playback/PlaybackPanelViewModelTests.cs)の`SongStart_PreparesOnceAndUsesLiveTargetAfterListAndModeChanges`でAの次にBを準備した後、現在の一覧からCへ進み、Cの開始で次候補を準備することを確認する。 |
| 関連する画面状態と更新 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePresentationStateTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspacePresentationStateTests.cs) / [`PlaylistWorkspacePresentationStateTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspacePresentationStateTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) |
| ルート画面への再生管理主体の接続と表示の能力 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | [`MainWindowPlaybackWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaybackWpfTests.cs) |
| 表示切替ボタンのツールチップの言語追従 | `PlaybackPanelView.xaml`、`panelStateToTooltipConverter` | [PlaybackPanelViewModelTests](../../../tests/BeMusicSeeker.Windows.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelView_ViewModeTooltipFollowsLanguageWithoutChangingSurfaceOrPlayback` は画像／内蔵再生と再生中／停止中の4条件で、言語往復後のtooltipと、表示面・再生・gateway起動回数の不変を確認する。 |
| 入力単位の再生観測、一時試聴の入れ子の素材と元データの保全、停止後の再開始 | `PlaybackPanelViewModel` の検証起動の観測 | [`PlaybackScenarioTests`](../../../tests/BeMusicSeeker.Scenarios.Tests/Playback/PlaybackScenarioTests.cs)（`full` の検証区分）: 入れ子の相対音源、元の名前・元のバイト列・配置先にある他の素材の保全と、停止の後の再開始を確認する。 |

## 関連資料

[外観](appearance.md)、[一覧の描画・操作](table-view.md)、[外部起動](external-launch.md)、[音声基盤](../runtime/audio.md)、[WPFテストの実行](../development/testing.md)。
