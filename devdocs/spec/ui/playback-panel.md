# 再生パネルの表示

## 目的と適用範囲

再生パネルの保存状態、利用可能な表示面、初期同期と通常のアニメーションを定めます。音声処理の寿命は[音声実行基盤](../runtime/audio.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 保存状態と有効な表示

`PlaybackPanelViewModel.PlayerPanelState` が保存された要求状態の正本です。表示は設定を直接読まず、利用可能な面を考慮した `EffectivePlayerPanelState` を使います。`TITLE_SMALL` は小型表示のフラグ、値0の `TITLE_LARGE` は正規の拡大型であり、未初期化ではありません。

表示面は画像と内蔵プレイヤーです。プレイヤーを使えない場合は画像を表示しますが、要求状態は書き換えません。小型指定とプレイヤー指定が併存していても、最初のフレームから画像の小型表示になります。

### 外部プレーヤーのホスト

`ExternalPlayerHwndHost` はWPFの `HwndHost` として、自プロセスのUIスレッド上に標準STATICクラスの黒い子HWNDを一つ所有します。WPFウィンドウへの接続で生成され、初期状態が `Collapsed` でも `ContentRendered` 後には有効です。`PlayerHostHandle` は生成済みハンドルを返し、取得時の遅延生成は行いません。通常の初期化完了通知から `MainWindow` が非ゼロの親HWNDを外部プレーヤーへ接続します。

画像と再生面の切替、Overlay表示、`Unloaded` と切離し・再接続では同じHWNDを保持します。終了は既存のシェル終了管理主体によるプレーヤー終了の実完了を待ち、最終 `MainWindow.Closed` からUIスレッド上でホストを解放します。ネイティブ生成・破棄の失敗は例外として伝えます。

外部プレーヤーは物理587×256ピクセルです。XAMLの予約領域は `MinWidth=587`、`Height=256`、`MaxHeight=256` DIPとし、中央・下端へ配置します。ホストの配置は各軸について、WPFから渡された `finalSize` と物理寸法を実行時DPI倍率でDIPへ換算した値の小さい方を返します。小型でも固定の予約寸法を維持し、親の可用領域とは区別します。空ホストの位置とサイズは `HwndHost` が同期します。通常テストは実行時DPIで確認し、異なるDPIの実モニターにおける描画・入力は[ユーザーに推奨する実機確認](../development/testing.md#ユーザーに推奨する実機確認)へ分けます。

### 共通の再生対象と一覧状態

内蔵再生の選択・キュー・現在曲には共通 `ChartFile` を使います。解析・保存の形式入口から共通モデルへ接続し、次曲・前曲・フォルダ送りは譜面を対象にします。一時配置と終了callbackも、開始時に確定した同じ対象と世代を保持します。外部playerの形式条件は内蔵再生能力から推測しません。

再生方式の設定確定は次回要求だけを更新し、現在のplayer・曲・演奏Taskを維持します。Pause・Resume・Stop・Restartでは方式を置換しません。新規開始は既存の選曲受付を通過してから同じ保存済み設定のfactoryと形式能力を持つ次回要求を一回捕捉し、その能力で候補を選びます。候補の検査と必要な一時配置の受付が成功した後に、捕捉したfactoryで実playerを置換します。同じ開始内の入力不良による候補skipでは、後から保存された要求を捕捉し直しません。繰り返しOFFの手動送りが一覧の先頭・末尾で新しい開始へ進まない場合も、次回要求は消費しません。bmson行の直接起動は次回要求があればその能力で副作用前に判定し、非対応なら現在曲やヘッダーを変えず拒否します。試聴の順次実行を待った後も明示行の条件を確認し、別行へ読み替えません。ヘッダーの更新は受理した再生開始に合わせ、順番待ちの行から先行して変更しません。先読みは現在playerの能力に従います。

seek等の操作能力、外部playerの表示面、一時試聴のLR2専用確認は現在の実playerから求めます。設定保存や画像設定の通知だけで将来の方式へ切り替えず、実置換の成功時に関連Bindingを更新します。置換に失敗した要求は次の明示開始まで保持します。置換中に受け付けた新しい方式の要求は、先行置換の成功・失敗で消しません。Busyで拒否した開始を後予約せず、方式の生成も開始しません。

所持カタログを譜面情報の唯一の正本とし、ライブラリ変更時は再生を停止します。`PlaybackPanelViewModel` が現在対象と再生状態を所有します。通常一覧とプレイリスト一覧は `status` getterで元状態の `~PLAYALL` を保持し、種類と実pathの `OrdinalIgnoreCase` 一致時だけ現在の再生bitを重ねます。保存主体・`ChartFile`・一時投影には再生状態を書き戻しません。同対象の複数行と後から実体化する行も同じ現在値を返します。行登録、個別再生通知、別の状態辞書、一覧全件走査は使いません。

準備中は `LOADING` (2)、開始成功と再開は `PLAY` (1)、一時停止は `PAUSE` (4)、停止・開始失敗・対象未設定は `NONE` (0)です。早送り中の `FORWARD` (8) と巻戻し中の `BACKWARD` (16) は現在bitへ加え、操作終了時に方向bitだけ解除します。`PLAYALL=0x1F` です。先読みだけの譜面には状態を付けません。

自然終了の復元故障を最外の開始・停止要求へ返す場合は、同じ要求の終了引数と専用の不変例外envelopeを対にして通知責務を移譲します。返却例外全体をInnerExceptionへ保持し、独立した原因とpathを失いません。Panelは包装内も含めてこの通知責務を識別し、実Stop/Shutdownで開始観測が失効した後も一通知・一主診断を行います。表示の解除と既観測の重複抑止は従来の世代判定を保ちます。通常の古い外部開始故障を通知する条件には広げません。

準備・開始・一時停止・再開・停止・対象変更では既存の非同期UI dispatchを通して `MainChartList.RequestDisplayRefresh` から表の `RefreshDisplay` へ接続します。行集合・順序・選択・スコア・未確定編集を維持してセルの表示値を再評価します。時刻進行だけでは表を更新せず、sessionGateを保持したままUI完了を同期的に待ちません。

題名・字幕・作者は選択対象の共通表示値を使います。表示対象の `DisplayedChart` が変わると画像とバナーを更新します。素材Aから素材B、素材なしへの選曲でも前曲の素材を残さず、素材なしは既定画像とバナー背景なしへ戻します。

現在曲がない停止中は、表の主行変更を既存の選択通知から受け、題名・字幕・作者と `DisplayedChart` をその行へ更新します。Ctrl+A後のShiftで選択集合が変わらなくても同じです。再生中に同じ操作で主行を変えても現在曲の表示を優先します。通知の要否と一入力一回の配送は[一覧](table-view.md#選択とキー操作)の共通選択モデルで判断します。

画像は再生に必須の入力ではありません。表示対象が変わったら前曲の画像とバナーを先に除去し、現在曲のパネル画像とバナーを独立して読みます。パネル画像の欠落・破損では設定外部画像または同梱既定画像へ進み、バナーの欠落・破損では同曲の正常パネル画像を `UniformToFill` で使います。同曲パネル画像も使えなければバナー背景と枠を消します。正常バナーは `Fill` を維持します。

主画像は譜面の正常な `STAGEFILE`、有効な設定外部画像、現在テーマの同梱既定画像の順で表示します。設定外部画像の読取り・復号失敗はテーマ既定画像へ進み、バナーの代替brush生成も失敗した場合は背景と枠なしを保ちます。任意画像とその元パス・原因の診断の失敗は、実選曲Task・現在曲・`PLAY`状態・開始結果を変えません。画像の復号はストリームを閉じる前に完了させます。画面の `DataContext`契約違反を素材不足として隠しません。

実開始後のBPM・最小値・最大値・total・ノート進行・表示終端は共通再生解析結果を使います。形式固有の表示統計は[bmson再生仕様](../library/bmson-playback.md#共通の再生経路と表示)に従い、音声の発音数や他プレイヤーの採点値とは区別します。

### テーマ画像とフラットな操作表示

各テーマ辞書は `Playback.DefaultImage` を `BitmapImage` として所有します。Lightは既存の `Assets/Images/default_image.jpg`、ほかのテーマは `Assets/Images/Playback/{Theme}.jpg` を使います。既定表示時だけ主画像の `Source` を動的資源へ接続し、同じViewを表示したままテーマ画像を切り替えます。stageと設定外部画像は通常の `Source` に設定し、テーマ変更で画素や参照を変更しません。C#側は外部画像だけをキャッシュし、既定画像の復号・保持はテーマ資源へ任せます。`DataContext` がなければ主画像・バナー背景を空にし、バナー枠を消します。主画像は単一のImageと `UniformToFill` を使い、小型表示のぼかしを維持します。バナーに外部画像やテーマ既定画像を流用しません。

操作帯はテーマごとに独立した `Playback.ControlBackgroundBrush`、`Playback.ControlForegroundBrush`、`Playback.ControlHoverBrush` を動的に参照します。LightとDarkは順に `#FF252526`、`#FFD4D4D4`、`#FF333337` を各辞書で独立定義します。ほかのテーマは現在の `App.SurfaceBrush`、`App.TextBrush`、`App.ControlHoverBrush` と同じ色値をそれぞれ独立定義します。操作アイコン、小型曲名、時刻は操作前景を使い、時刻は局所不透明度で控えめにします。無効アイコンは `App.DisabledTextBrush`、通常ボタン背景は透明、hoverと押下は操作hover背景を使い、押下は局所不透明度でも示します。

画像上の曲名領域は `#CC000000` と白文字、統計は既存の書体・内容に白文字と `#33000000` の背景を使います。グラデーションや影は付けません。バナー枠は `App.BorderBrush` を使います。

seekと音量のSliderは設定と同じ `App.Canonical.CompactSliderStyle` の軌道とつまみを使い、輪郭線とつまみ周囲の余白を設けません。未再生軌道は `App.BorderBrush`、再生済軌道とつまみは `App.AccentBrush` に接続し、状態色は[共通外観](appearance.md#通常版と省スペース版)に従います。seekの時刻は通常非表示で、つまみのhover・drag時だけ背景・枠・影のない白文字で表示します。音量欄は上のボタン群と同じ幅へ広げ、メニューの `App.TextBrush` を使う百分率列に40 DIP、Slider列に残り幅を割り当てます。0〜100%の桁数でSliderの幅・左右位置を変えません。

シークの座標は実 `PART_Track` とつまみの配置に基づく `Track.ValueFromPoint` で時刻へ換算し、上下限へ丸めます。アイコンボタンのhover背景と重なる範囲でも、軌道・つまみを含むseek全体を描画と入力の最前面へ置きます。値を反映しても双方向Bindingを保ちます。再生中のseekで一時停止し、mouse-upまたは押下したままのmouse-leaveで再開する既存操作を維持します。seekだけに `Stylus.IsPressAndHoldEnabled=False` を適用します。音量のpreviewと操作完了・終了時の保存は変更しません。

再生ボタンとパネル所有Sliderは `FocusVisualStyle=null` とし、専用focus枠も持ちません。フォーカス可否、Tab移動、キー操作、コマンドは維持します。アイコン操作のstyle/templateとseekの時刻表示はViewのResourcesが所有し、Sliderの描画と枠抑止は共通辞書が所有します。再生メニューのHS UP／HS DOWN／Info／Effect／1P/2Pは共通の枠付き省スペースButtonを使い、命令・Click・有効条件を維持します。

操作帯の部品間は4 / 8 DIPへ揃え、通常版・省スペース版の共通操作styleを用途に応じて使います。外部HWND、画像、seekの描画位置、高さアニメーションの寸法は操作帯の余白と分けて保持します。

### 初期同期

高さ、画像のぼかし、大きい題名の余白、バナーと小型題名の不透明度を一つの処理で同期します。初回の有効なViewModelの適用はアニメーションを除去し、最終値へ即時反映します。

DataContextが読込み前・読込み中のどちらで設定されても同じです。表示中の差替え、非表示からの再読込みも初期同期とし、以前のViewModelや表示期間の状態から遷移しません。購読の世代を持ち、古い通知を新しい表示へ適用しません。

| 値 | 小型 `TITLE_SMALL` | 拡大型 `TITLE_LARGE` |
| --- | ---: | ---: |
| パネルの高さ | 110 | 286 |
| ぼかし半径 | 20 | 0 |
| 大きい題名の下余白 | -40 | 0 |
| バナーの不透明度 | 1 | 0 |
| 小型題名の不透明度 | 1 | 1。表示可否は既存の変換処理で決める |

初期同期後の対象プロパティにはアニメーションを残しません。一時的な非表示、任意のDispatcher遅延、固定待機、起動だけの不透明度で初期フレームを隠しません。

### 通常の遷移

初期同期後、同じViewModelと購読世代で小型フラグが変わった場合だけ遷移します。表示面や利用可否だけが変わり、小型フラグが同じ場合は再開しません。

高さとぼかしは約1秒、大きい題名は0.7秒です。小型バナーは0.8秒後から0.5秒で表示し、拡大型へ戻るときは0.2秒で消します。小型題名は小型化の0.8秒後から表示します。完了・置換・非表示時にアニメーションを除去し、プロパティの基本値を最終状態として残します。

### 曲の準備・開始と次候補

表の行activationと再生ボタンは、入力時に停止・一時停止なら受付後にプレイヤー面を要求します。再生中からPauseする操作では要求せず、競合拒否・行/形式検査の拒否・終了後は面と保存された表示設定を変更しません。競合と無関係な空選択のStartでも面選択意図を維持し、受理後の起動失敗・取消で巻き戻しません。

面要求は要求内callbackとして、候補検査と必要なL取得後・次回playerの適用試行後・BeginPlayback前に既存Dispatcherへ一度投稿します。置換成功時は、実playerの能力が利用可能になってから配送できる順序にします。適用失敗でも受理済みの一回投稿は維持し、失敗・取消で面選択意図を破棄しません。再開では選曲gate内の拒否確認後・TogglePause前に投稿します。表と再生ボタンの入口は既存background要求のTaskを呼出元までそのまま返し、UIイベント側は既存の故障観測へ接続します。要求Taskは選曲の実終端を表し、面要求のUI配送は待ちません。複数候補をスキップしても一要求につき一度で、既存選曲gateの受付順に配送します。通知の投稿・実行故障は局所診断に止め、再生結果を変えません。Viewは既存の接続期間と閉鎖状態を確認し、切離し・再接続後へ古い面要求を適用しません。PlaybackStarting/Startedのスクロール・フォーカス用途は維持します。

`PlaybackStarting` は選択行のスクロールだけを行います。内蔵playerのReadyを含む `PlaybackStarted` のUI受信時に、MainWindowが終了中でなければ選択済みの表示面を同期復帰し、実シェルHWNDが前面HWNDと一致する場合だけ表へキーボードフォーカスを同期で一度要求します。前面不一致でも表示面の復帰は行います。通知受信後のDispatcher予約や再試行を残さず、後発の検索・セル編集を古い要求で奪いません。受信時点の終了状態、再生ownerの世代、Viewの接続期間で古い通知を拒否します。

一時配置が必要な保留譜面では、候補解決後にLを取得してからsession・選択・表示・開始イベントを更新します。Start/Next/Previous/行activationのいずれも、取得拒否で旧曲・位置・ヘッダー・PLAY・選択と終了callback、面と保存設定、保留中の次回方式を維持し、playerの生成・交換も行いません。拒否した要求の予約・自動再開は行いません。

開始要求は選曲gateの取得後、次回playerのfactoryや置換待ちより前に現在の再生世代を捕捉します。旧playerのClose待ち、採用後のUI配送待ち、開始状態の確定前にStopを受けた要求は、失敗通知を出さず開始を中止します。最後の失効検査と現在曲の確定は同じsession排他内で行い、置換と同じ要求の不正候補送りによる世代更新だけを引き継ぎます。先行Nextが正常に更新した後、選曲gateを取得した後続Nextは通常どおり実行します。

Stopが採用前なら次回player要求を次の明示開始へ保持し、採用後なら採用済みplayerを維持して同じfactoryを再実行しません。待機中に保存された別の要求を消費せず、中止した開始から新しい要求へ乗り換えません。一時配置でも同じ判定を使い、開始確定前の失効ではコピーせずLを解放します。受理済み面要求の一回配送、能力とhostの接続、旧Closeの実終端待ちを維持し、factory・状態取得・Closeの実故障は取消へ読み替えません。

内蔵playerの開始成功は[次曲の先行準備](../runtime/audio.md#次曲一件の先行準備)のReadyで判定し、成功時にLOADINGからPLAYへ移します。演奏終了やUI描画を待たず、開始した曲の手動送り先一件を準備します。通常開始も先読み採用も同じ出力開始確認を通ります。Readyの開始失敗をCompletionから再通知せず、短曲の終了callbackとCompletionも一回だけ扱います。自然終了を受け付けた時点の世代・player・譜面を保持し、選曲入力の待機後に再確認します。先行する手動送りで現在曲が変わった場合、古い自然終了は次曲送りも変更中の停止も行いません。手動送りのFIFOは維持します。

外部playerには先読み能力を要求しません。旧playerの操作終了後、`PlayStart`の同期呼出しを終えた時点を選曲側の開始境界とします。返却された未完了Taskは終了・失敗の観測へつなぎますが、選曲入力や一時コピーをその終了まで保持しません。内蔵playerの一時コピーはReady終端まで保持します。コピーの除去と元pathの復元は開始失敗時にも行います。コピー・公開・開始の主失敗は、元名復元やplayer停止の二次失敗で置き換えません。既に通知済みの開始失敗も局所的に保持し、復元失敗による二重通知を避けます。二次原因は復元の退避名/元名、または停止対象譜面のpathとともに既存診断へ残します。主失敗がない復元失敗は呼出元へ返します。

一時試聴の譜面・音源・画像はパッケージ内の相対配置を維持し、サブフォルダの同名素材も元の位置で参照できます。コピー先にある素材の内容を保持します。譜面名は入力元の兄弟と同相対の配置先のファイル・ディレクトリ占有から決め、衝突がなければ元名、衝突時は末尾へ `_` を加えた最終名へ一回だけ非上書きで移動し、同じ相対フォルダへ配置します。候補探索中は改名せず、移動成功後だけ元名復元の対象とします。詳細は[一時試聴の配置と寿命](../runtime/audio.md#次曲一件の先行準備)に従います。回収は所有ファイルと所有した空ディレクトリだけを対象とし、借用ディレクトリや途中で追加された第三者ファイルを再帰削除しません。停止・再試聴後も元データと既存宛先を保全します。

外部viewerのINI準備と復元は[外部起動](external-launch.md#bmiidxview2015とubmplayのini保全)に従います。自然終了の復元故障は一度通知して自動次曲を止めます。古い世代の故障で新しい曲や表示を消さず、故障通知そのものは維持します。

先読みの基準は現在の再生対象であり、Pause/Resume、Seek、ハイライト変更では候補を変更しません。一覧差替え・並べ替え・再生モード変更への先読み再評価も行いません。次の実対象は通常選択で決め、保持した準備と一致する場合だけ使います。不一致なら通常ロードの待ちを許し、古い先読み候補へは送りません。単曲リピートでは手動送り先を優先し、第二候補は持ちません。一時配置が必要な候補は先読みせず、さらに先へ飛ばしません。

前曲は後方向、次曲と先読みは前方向に同じ候補探索を使い、非対応BMSON・欠落等の除外と開始時の入力不良後もその方向を保ちます。新しい折返しや追加周回は行いません。探索開始時の位置・件数・モードを捕捉し、一巡以内で既存の世代判定へ戻ります。一覧の縮小やStop・終了で失効した要求から新しい再生を開始せず、元要求・後続の明示操作・終了を終端させます。検索や一覧操作を禁止せず、全一覧の複製は行いません。探索中のモード変更は次の要求から有効です。

FolderSkipがONなら後方で最も近い別の実フォルダの行を前曲とします。RepeatがONで全行が同じ実フォルダなら一巡して現在行の同曲を一回開始します。RepeatがOFFの先頭での手動前曲は無作用とし、次回player要求も消費しません。単クリックのRestartと明示行開始の意味は変えません。

行の取得は一覧側の寿命内で済ませ、workerへ渡すのは入力一件だけです。曲開始は共通の非同期処理でReadyと候補登録まで直列化し、演奏全体は待機区間に含めません。モード・行変更の通知から先読みTaskを差し替える処理は持ちません。

譜面・音源の物理変更では現在曲と先読みを全て停止します。既存の共通変更受付中は新しい再生を予約せず断り、自然終了の次曲送りも停止へ変えます。変更後の自動再開・先読み再登録はありません。停止・変更・交換・終了の資源終端は[音声仕様](../runtime/audio.md#次曲一件の先行準備)に従います。

先読みfatalは、準備Taskを引き取った開始・停止、または未消費のTaskを監視する背景処理が報告します。背景処理は停止・cleanupを終えて通知し、パネルは捕捉した曲がまだ現在曲の場合だけ表示状態を解除します。新しい明示再生と重なった遅い故障も原因を隠さず通知しますが、新曲の状態を消しません。通常の入力不良は実採用時の既存の失敗処理へ渡します。自然終了からの停止が先読みTaskを引き取った場合も、その故障を一回通知し、停止表示を維持して次曲へは進みません。実開始へ引き渡した準備Taskのfatalも、停止によって開始時の表示世代が失効していても開始側が通知します。表示解除は現在の開始に限り、旧開始の故障で新しい曲の状態を消しません。通常の取消は故障通知にしません。

## 実装とテストの対応

操作帯の共通styleと4 / 8 DIPの間隔は `PlaybackPanelView.xaml` の静的差分で確認します。表示面・HWND・高さアニメーション・seek・音量の既存保証は以下のケースへ分担し、間隔だけを固定するテストは増やしません。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 次回playerのClose待ち・採用後UI待ちでの停止 | `PlaybackPanelViewModel.RunPlaybackSelectionAsync` / `ReplacePlayerCoreAsync` / `BeginPlaybackCore` | `PlaybackPanelViewModelTests.PendingPlayerReplacement_StopCancelsStartWithoutConsumingUnadoptedOrNewRequest` は通常/一時配置×採用前/後×新保存有無で、通知/開始なし、両Taskの終端、面要求、コピーなしとL解放、次の明示開始での保持/再利用を確認する。`PendingPlayerFactory_StopBeforeReturnKeepsRequestForExplicitStart` はfactory中の失効、`PendingPlayerReplacement_StopDoesNotSuppressOldCloseFailure` は実故障通知を分担する。受理済みNextと不正候補送りは既存の専用ケースを維持する。 |
| 次の明示再生へのplayer方式の採用・失敗時保持 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) の `UsePlayerForNextPlayback` / `ApplyPlayerForNextPlaybackAsync` | `PlaybackPanelViewModelTests.ConfirmedPlayerFactoryIsUsedOnlyAtNextPlaybackStart` は一時停止での現再生維持と次の明示開始での一回採用、`ConfirmedPlayerFactory_ApplyFailureKeepsCandidateForNextExplicitStart` は通常/一時配置の実交換準備失敗後の候補保持・通知/要求終端・自動再試行なし・L解放と次明示開始での再採用を確認する。`TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput` は受付拒否でfactoryを消費せず旧playerを終了しない保証、既存の直接交換失敗ケースは旧session/購読の保全を分担する。 |
| 面要求の入力条件、非待機配送、標準画面への接続 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | `PlaybackPanelViewModelTests.SurfaceRequest_UsesInputStateAndKeepsAcceptedIntent` は停止開始・再開・Pause・受理後故障/取消・空選択、`SurfaceRequest_PendingPlayerReplacementSelectsAvailableSurface` は通常配置の両入口と一時配置の行起動で、旧playerの解放待機中に実Dispatcherを動かしてから実BMIIDXViewへ交換し、実Viewによる一回の面配送・実効面/保存状態・対象pathの一回開始を確認する。`SurfaceRequest_PlayButtonKeepsInputConditionWhileEarlierReadyIsPending` は先行Ready待ち中の入力条件、`SurfaceRequest_QueuedFifoAndOptionalFailureDoNotChangePlayback` はUI未配送での後続完了・FIFO・任意通知故障、既存InternalReadyとReloadRejectsケースはReady/skip/接続期間との独立を確認する。[`MainWindowPlaybackWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaybackWpfTests.cs) の `PlaybackSurface_ChangesOnlyAfterAcceptedExplicitInput` は標準terminal/実Dispatcher/compiled画面を使い、両入口の実Task終端での競合拒否後も面・保存値が不変で、L解放後の次の明示入力だけが切り替えることを確認する。`PlaybackControls_BindPanelStateAndCapabilitiesThroughPlaybackOwner` はcompiled表のキーから同じ行activationへの行とindexの転送を担う。 |
| 一時試聴の受付拒否、自然終了復元故障 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | `PlaybackPanelViewModelTests.TemporaryCopy_AdmissionRacePreservesExistingSessionUntilExplicitInput` は候補解決中の実L先取りによる各入口の状態・面要求保全（行activationはPause、再生ボタンは現在曲なし）、`NaturalExit_UbmplayRestoreFailureNotifiesOnceWithoutAdvancing` は実uBMplayとの自然終了単独・旧main通知未配送での新曲開始・現開始中の同期終了、実復元故障後の返却前Stop/Shutdownによる失効の一通知/一主診断、元原因/path、起動阻止と明示再試行、`NaturalExit_StaleFailurePreservesReplacementAndStillNotifies` は旧世代の表示保全と故障通知を確認する。 |
| 異なるDPIの実モニターでの描画・入力 | [`ExternalPlayerHwndHost`](../../../BeMusicSeeker/Views/Playback/ExternalPlayerHwndHost.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | 下記の自動テストは実行時DPIで配置・寸法・HWND寿命を検証する。異なるDPIのモニターでの描画・入力位置とモニター移動後の操作は[ユーザーに推奨する実機確認](../development/testing.md#ユーザーに推奨する実機確認)へ分ける。 |
| 譜面の表示対象変更、素材A→素材B→素材なし | [`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) の `DisplayedChart` 通知と `RefreshArtwork(ChartFile)` | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelView_ChartSelectionReplacesArtworkAndRestoresDefaultWhenAbsent` は同じcompiled Viewで全10テーマ往復の既定画像をCopyPixelsの内容で比較し、操作色の更新、外部・stage表示の明暗往復での画素・参照保持、実選曲A→B→素材なしとDataContext解除による画像・バナーの除去を確認する。 |
| 同じ選択集合で主行だけ変わる停止時表示、再生中の現在曲優先 | `CustomTableSelectionModel` から標準 `MainWindowPlaybackTerminal`、`PlaybackPanelViewModel.HandleTableSelection` への接続 | `MainWindowPlaybackWpfTests.RangeSelection_UpdatesStoppedHeaderAndKeepsPlayingChartThroughStandardTerminal` はcompiled実表のCtrl+A→Shift下から題名・字幕・作者の実TextBlockと `DisplayedChart` の更新を確認し、標準入口の実開始Taskで先頭曲を再生した後の同じ選択操作では先頭曲表示を保つ。集合/主行/基点の判定と表の一回通知は `CustomTableSelectionModelTests` / `CustomTableNavigationTests` に分担する。 |
| 正常Aから破損パネル画像・破損バナー・両素材の破損/欠落、設定外部画像の欠落/破損 | [`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) の `RefreshArtwork` / `ApplyPanelImage` / `ReadOptionalArtwork` | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelView_ArtworkFailureKeepsCurrentSelectionAndPlayback` は実選曲・コンパイル済み画面・実復号処理から前曲除去、同曲パネル画像または既定画像の画素、背景と伸縮方式、元パス・原因の診断と現在曲・`PLAY`・一回の開始を確認する。バナーbrushの二次故障と診断の独立、`DataContext`検査の位置は静的に確認する。 |
| フラット描画、操作色とfocus装飾なし | [`PlaybackPanelView.xaml`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml)、各テーマ辞書 | `PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader` は実効操作前景、統計・曲名の背景と白文字、影なし・focus装飾なしと入力可能性を確認する。全テーマの独立キー・色値、hover/pressedの接続とfocus枠なしは静的確認する。 |
| 0〜100%の音量表示と固定配置、preview・完了時保存 | 共通の `App.Canonical.CompactSliderStyle` と `PlayerVolume*` 完了イベント | `PlaybackPanelView_VolumeMenuCommitsOnMouseCompletionAndShutdown` は実Popupで各桁数の表示・Slider左右位置/幅・実railとthumbの中央・focus装飾なしを確認し、37のmouse完了保存と44の終了保存を維持する。 |
| 実Trackによる時刻換算、ボタン背景より前面の描画・入力、双方向Bindingとpause/resume | `PlaybackPanelView.xaml` のseek前面化、`PlaybackPanelView.SeekFromPoint`、`sliderPlayerMouseMove` / `sliderPlayerMouseLeftButtonUp` / `sliderPlayerMouseLeave` | `PlaybackPanelView_SeekUsesActualTrackAndPreservesBindingAndResume` は実配置から求めたボタンhover背景との重なりでつまみ以外のInputHitTestと重なるつまみの描画画素を確認する。異なるcontrol幅とthumb幅で最小・中央・最大、player通知から表示・座標から同じplayer時刻、範囲丸め・Binding保持・実mouse-upでの再開と再開始なしを確認する。hover/pressedトリガー接続と押下mouse-leaveは静的に確認する。 |
| 再生方式の次回適用、形式能力、現在の操作能力と表示面 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[設定の局所確定](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.Confirmation.cs) | `PlaybackPanelViewModelTests.ConfirmedPlayerFactoryIsUsedOnlyAtNextPlaybackStart`と非対応bmsonの直接起動ケースが選曲・現在再生の維持を、`PendingPlayerReplacementRetainsFailuresAndNewerRequests`が置換失敗・後続要求保持を、`QueuedRowActivationRechecksPendingCapabilityWithoutChangingCurrentHeader`が実行順を待った後の明示行拒否、現在曲・選択・ヘッダー・面の維持と後続bms開始での要求残存を、`InternalReady_InvalidInputNotifiesAndSkipsOnce`が同じ開始のskip中に次回要求を消費しないことを確認する。[`SettingsDialogBehaviorTests.CompiledPlayerSelection_AutomaticallySavesAndDefersFactoryUntilNextPlayback`](../../../BeMusicSeeker.Tests/Settings/SettingsDialogBehaviorTests.cs)が実選択から実置換時のcontrol/表示面更新を分担する。 |
| 共通譜面キュー、現在曲表示、再生状態投影、外部playerの能力 | `PlaybackPanelViewModel.NowPlayingChart` / `GetPlaybackStatus`、[`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs)、`PlaybackChartQueue` | `PlaybackPanelViewModelTests.PlaybackPanelChartQueueKeepsIdentityStatusAndSingleAdvance` は混在入力を代表として現在対象・ヘッダー・PLAY/PAUSE・一回の送り・旧対象の状態解除と `SCORE_UNSENT` / `SEARCHING` の維持を、`PlaybackPanelDoesNotSendBmsonToAnExternalPlayerWithoutThatCapability` は外部能力の分離を確認する。 |
| 要求状態と実効状態、初期フレーム、差替え・再読込み、遷移 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) |
| 初期Collapsedでの子HWND生成、UIスレッド・プロセス所有 | [`ExternalPlayerHwndHost`](../../../BeMusicSeeker/Views/Playback/ExternalPlayerHwndHost.cs)、`PlaybackPanelView.PlayerHostHandle` | `PlaybackPanelViewModelTests.PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader` は `ContentRendered` 後の継承 `Handle`、親、PID・TIDを確認する。 |
| 面切替・Overlay・切離しと再接続での寿命、物理寸法と予約領域 | `ExternalPlayerHwndHost.ArrangeOverride`、[`PlaybackPanelView.xaml`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml) | `PlaybackPanelViewModelTests.PlaybackPanelView_PlayerSurfaceAndOverlayPreserveHostAndPhysicalLayout` は実再生入口から面切替・Overlay・拡大型の中央下端配置と実行時DPIでの物理寸法、小型の要求・実効状態と再読込み時の即時同期・同じHWNDの生存を確認する。`PlaybackPanelView_SameViewModelStateChangesUseTransitionsAndReloadSynchronizesImmediately` は切離し・再接続の寿命と遷移を確認する。固定寸法より小さい `finalSize` の本番到達は確認しておらず、min式は実装レビューで確認する。 |
| 通常初期化完了からのホスト接続、プレーヤー終了待ちと最終解放 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowViewHostTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs) の `MainWindowRenderedInitializationAttachesCreatedPlaybackHost` は `ContentRendered` を入口とする接続を、`MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose` は終了gate保留中のUI応答・HWND生存と最終Closed後の無効化を確認する。 |
| 再生通知のDispatcher所有threadへの投稿 | [`WpfPlaybackUiDispatcher`](../../../BeMusicSeeker/Views/Playback/WpfPlaybackUiDispatcher.cs) | [`WpfPlaybackUiDispatcherTests`](../../../BeMusicSeeker.Tests/Playback/WpfPlaybackUiDispatcherTests.cs) の `Dispatch_PostsActionToDispatcherOwnerThread` はWindowなしの共有Dispatcherを使い、投稿元Taskと受信側actionの実完了を分けて所有threadへの一回到達を確認する。Dispatcher終了時の挙動は専用STAの既存ケースが分担する。 |
| 内蔵Readyを含む開始通知での表示面復帰と表への一回のフォーカス要求 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) の `RestorePlaybackSurfaceAndFocusTable`、`PlaybackPanelView` の開始通知接続 | `MainWindowPlaybackWpfTests.InternalReady_RestoresTableOnceAndPreservesLaterSearchOrMemoInput` は実表入口・内蔵Ready・実MainWindowのHWND照合を通し、入力なしの初回要求と後発検索・本番受付のMEMO編集への追加要求なし、文字列・候補・選択・未確定状態を確認する。`InternalReady_ForegroundMismatchRestoresSurfaceWithoutRequestingTableFocus` は保存済み題名面の実表示を保ち、前面不一致で要求しないことを確認する。OS前面値とフォーカス要求だけを代替し、物理OSフォーカス取得・LostKeyboardFocusは実機確認へ分担する。外部表示面は既存の `PlaybackSurface_ChangesOnlyAfterAcceptedExplicitInput`、終了後・再接続後の旧通知抑止は `PlaybackPanel_DispatchesTelemetryAndSuppressesStalePlaybackEventsThroughUiBoundary` と `PlaybackPanelView_ReloadRejectsEventsQueuedByThePreviousSubscription` が分担する。MainWindow受信終端に遅延要求が残らないことと終了guardは静的に確認する。 |
| Ready成功時のPLAY・一回の次候補、一時コピー寿命、停止の終端 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackChartQueue`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackChartQueue.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`InternalReady_StartsNextPreparationBeforeCompletionOrUiPublication`、`TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion`、`FileMutation_StopsCurrentSongBeforeWritingAndRejectsPlaybackWhileBusy`で開始・入力・停止の境界を確認する。[`AudioContractsTests`](../../../BeMusicSeeker.Tests/Playback/AudioContractsTests.cs)の`PreloadFatal_CloseJoinsPreparationAndNotifiesOnceWithIndependentCleanupFailure`で実内蔵playerの背景故障通知を確認する。 |
| 停止で表示が失効した後の採用済み準備故障の通知 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`AdoptedPreloadFailure_AfterStopNotifiesOnceWithoutRestarting`でB開始が準備中にStopを受けた後の元原因の一回通知、次へ・停止の終端、自動再開なしと停止表示を確認する。 |
| 手動送り待ちの後に残った旧曲の自然終了 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`NaturalExit_QueuedBehindManualNextDoesNotAdvanceTheReplacementSong`でA候補解決中に手動・自然終了・手動を受け付け、A・B・Cだけを開始することを確認する。 |
| 前曲の除外・開始時入力不良後の方向と、一開始内の次回方式捕捉 | `PlaybackPanelViewModel.NavigatePlayback` / `FindPlayableChart` / `StartAtIndex` | `PlaybackPanelViewModelTests.Navigation_UnavailableCandidatesKeepRequestedDirection` は非対応BMSON・欠落・連続除外・候補なしを実一覧と非BMSON player能力で、`Previous_LegacyInvalidStartContinuesBackward` は外部と同じ開始Task境界、`Previous_InvalidReadyContinuesBackwardWithoutRecapturingPlayer` は同期・非同期Readyと新しい次回方式の保持を実開始列で確認する。前方向のReady通知・面要求は既存の `InternalReady_InvalidInputNotifiesAndSkipsOnce`、単クリックRestart・二重クリック前曲は `PlaybackPreviousButtonGestureTests` が分担する。 |
| 前曲の有限探索とStop・終了、捕捉したモード | `PlaybackPanelViewModel.NavigatePlayback` / `FindAdjacentPlaybackIndex` | `PlaybackPanelViewModelTests.Previous_ListShrinkAndInvalidationTerminateNavigation` は実queueの行取得とrowGate解放後に5→3行の縮小とStopまたは終了を挿入し、元要求・次明示開始・Closeの終端と新曲・開始イベント・通知なしを確認する。`Previous_FolderTraversalUsesCapturedModes` は後方最寄りの別実フォルダ、全行同フォルダの一回開始、探索中のモード変更が次要求へ適用されることを確認する。先頭の無作用・次回方式非消費、Next版の終了・queue寿命は既存ケースで分担する。 |
| 自然終了の停止へ渡った先読み故障の一回通知 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`NaturalSinglePlayStop_NotifiesPreloadCleanupFailureOnceWithoutStartingNextSong`でA開始後のB準備、自然終了callbackとCompletionからの単曲停止、元原因の一回通知、B開始なしと停止表示を確認する。 |
| 外部playerの停止中に受け付けた開始、一時コピー寿命、開始呼出しと返却Taskの失敗 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`LegacyStart_WaitsForPreviousCloseAndRetainsInputUntilInvocation`で通常・一時配置の双方について旧playerの終了前の開始禁止、呼出し時の入力保持、未完了Taskを待たない選曲完了、開始呼出し失敗・開始後の失敗の一回通知を確認する。 |
| 一時改名時の配置先維持、元ファイルの復元、一時コピーの除去 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs)の`PlaybackPanel_StartAtIndexUsesChartInstallDestinationWhenTemporaryRenameChangesPath`で共通Dispatcherを使い、開始Taskの完了後に再生先pathとファイルの復元・除去を確認する。 |
| 相対素材・同名別フォルダ・既存素材の保持、再試聴、外部開始後の読取り | `PlaybackPanelViewModel.StartTemporarilyInstalledChart`、`temporarilyCopyFiles` | `PlaybackPanelViewModelTests.TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion` は通常発見した入れ子パッケージで衝突なし・入力元兄弟・両側複数・ディレクトリ占有からの開始名、元・兄弟・既存宛先のバイト内容、相対素材・別フォルダの同名素材・パッケージ外素材の保全、`Ready` 前の実要求TaskとLの保持、`Completion` 前の回収を確認する。衝突なしの停止後とディレクトリ占有の代表での `Ready` 故障後には明示再試聴を確認し、故障の元原因を一度だけ通知する。`TemporaryCopy_ExternalDelayedReadKeepsNestedChartAndRestoresOriginalName` は外部実開始後のgateで入れ子譜面と相対音源を読み、元名の復元と既存宛先保全を確認する。`TemporaryCopy_ResidueNotifiesOnceAfterCleanupWithoutChangingPlayback` は開始主原因と元名衝突の二次診断、非所有の元名fileと退避元の保全、L解放後の残留通知を確認する。`TemporaryCopy_CopyFailurePreservesPrimaryWhenStopAlsoFails` は実素材の読取り禁止と停止故障が重なってもcopy元原因・対象pathを保持して回収することを確認する。`TemporaryCopyFilesTests.Dispose_PreservesRelativeStructureExistingMaterialsAndUnownedFiles` と `CopyFailure_JoinsCopiesAndRemovesOnlyOwnedDestinations` は所有範囲と失敗回収を分担し、既存256件の並列追跡を維持する。 |
| Pause・シーク・一覧・モード変更では準備を差し替えず、実際の送り先を優先すること | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`SongStart_PreparesOnceAndUsesLiveTargetAfterListAndModeChanges`でAの次にBを準備した後、現在の一覧からCへ進み、Cの開始で次候補を準備することを確認する。 |
| 関連する画面状態と更新 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePresentationStateTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePresentationStateTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) |
| ルート画面への再生管理主体の接続と表示の能力 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | [`MainWindowPlaybackWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaybackWpfTests.cs) |

## 関連資料

[外観](appearance.md)、[音声基盤](../runtime/audio.md)、[WPFテストの実行](../development/testing.md)を参照します。
