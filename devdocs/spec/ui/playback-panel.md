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

所持カタログを譜面情報の唯一の正本とし、ライブラリ変更時は再生を停止します。`PlaybackPanelViewModel` が現在対象と再生状態を所有します。通常一覧とプレイリスト一覧は `status` getterで元状態の `~PLAYALL` を保持し、種類と実pathの `OrdinalIgnoreCase` 一致時だけ現在の再生bitを重ねます。保存主体・`ChartFile`・一時投影には再生状態を書き戻しません。同対象の複数行と後から実体化する行も同じ現在値を返します。行登録、個別再生通知、別の状態辞書、一覧全件走査は使いません。

準備中は `LOADING` (2)、開始成功と再開は `PLAY` (1)、一時停止は `PAUSE` (4)、停止・開始失敗・対象未設定は `NONE` (0)です。早送り中の `FORWARD` (8) と巻戻し中の `BACKWARD` (16) は現在bitへ加え、操作終了時に方向bitだけ解除します。`PLAYALL=0x1F` です。先読みだけの譜面には状態を付けません。

準備・開始・一時停止・再開・停止・対象変更では既存の非同期UI dispatchを通して `MainChartList.RequestDisplayRefresh` から表の `RefreshDisplay` へ接続します。行集合・順序・選択・スコア・未確定編集を維持してセルの表示値を再評価します。時刻進行だけでは表を更新せず、sessionGateを保持したままUI完了を同期的に待ちません。

題名・字幕・作者は選択対象の共通表示値を使います。表示対象の `DisplayedChart` が変わると画像とバナーを更新します。素材Aから素材B、素材なしへの選曲でも前曲の素材を残さず、素材なしは既定画像とバナー背景なしへ戻します。実開始後のBPM・min/max・total・ノート進行・表示終端は共通再生解析結果を使います。形式固有の表示統計は[bmson再生仕様](../library/bmson-playback.md#共通の再生経路と表示)に従い、音声voice数や他プレイヤーの採点値とは区別します。

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

内蔵playerの開始成功は[次曲の先行準備](../runtime/audio.md#次曲一件の先行準備)のReadyで判定し、成功時にLOADINGからPLAYへ移します。演奏終了やUI描画を待たず、開始した曲の手動送り先一件を準備します。通常開始も先読み採用も同じ出力開始確認を通ります。Readyの開始失敗をCompletionから再通知せず、短曲の終了callbackとCompletionも一回だけ扱います。自然終了を受け付けた時点の世代・player・譜面を保持し、選曲入力の待機後に再確認します。先行する手動送りで現在曲が変わった場合、古い自然終了は次曲送りも変更中の停止も行いません。手動送りのFIFOは維持します。

外部playerには先読み能力を要求しません。旧playerの操作終了後、`PlayStart`の同期呼出しを終えた時点を選曲側の開始境界とします。返却された未完了Taskは終了・失敗の観測へつなぎますが、選曲入力や一時コピーをその終了まで保持しません。内蔵playerの一時コピーはReady終端まで保持します。コピーの除去と元pathの復元は開始失敗時にも行います。

先読みの基準は現在の再生対象であり、Pause/Resume、Seek、ハイライト変更では候補を変更しません。一覧差替え・並べ替え・再生モード変更への先読み再評価も行いません。次の実対象は通常選択で決め、保持した準備と一致する場合だけ使います。不一致なら通常ロードの待ちを許し、古い先読み候補へは送りません。単曲リピートでは手動送り先を優先し、第二候補は持ちません。一時配置が必要な候補は先読みせず、さらに先へ飛ばしません。

実選曲と候補計算は同じ前方向移動・再生可能譜面の探索を使います。行の取得は一覧側の寿命内で済ませ、workerへ渡すのは入力一件だけです。曲開始は共通の非同期処理でReadyと候補登録まで直列化し、演奏全体は待機区間に含めません。モード・行変更の通知から先読みTaskを差し替える処理は持ちません。

譜面・音源の物理変更では現在曲と先読みを全て停止します。既存の共通変更受付中は新しい再生を予約せず断り、自然終了の次曲送りも停止へ変えます。変更後の自動再開・先読み再登録はありません。停止・変更・交換・終了の資源終端は[音声仕様](../runtime/audio.md#次曲一件の先行準備)に従います。

先読みfatalは、準備Taskを引き取った開始・停止、または未消費のTaskを監視する背景処理が報告します。背景処理は停止・cleanupを終えて通知し、パネルは捕捉した曲がまだ現在曲の場合だけ表示状態を解除します。新しい明示再生と重なった遅い故障も原因を隠さず通知しますが、新曲の状態を消しません。通常の入力不良は実採用時の既存の失敗処理へ渡します。自然終了からの停止が先読みTaskを引き取った場合も、その故障を一回通知し、停止表示を維持して次曲へは進みません。実開始へ引き渡した準備Taskのfatalも、停止によって開始時の表示世代が失効していても開始側が通知します。表示解除は現在の開始に限り、旧開始の故障で新しい曲の状態を消しません。通常の取消は故障通知にしません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 異なるDPIの実モニターでの描画・入力 | [`ExternalPlayerHwndHost`](../../../BeMusicSeeker/Views/Playback/ExternalPlayerHwndHost.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | 下記の自動テストは実行時DPIで配置・寸法・HWND寿命を検証する。異なるDPIのモニターでの描画・入力位置とモニター移動後の操作は[ユーザーに推奨する実機確認](../development/testing.md#ユーザーに推奨する実機確認)へ分ける。 |
| 譜面の表示対象変更、素材A→素材B→素材なし | [`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) の `DisplayedChart` 通知と `RefreshArtwork(ChartFile)` | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) の `PlaybackPanelView_ChartSelectionReplacesArtworkAndRestoresDefaultWhenAbsent` は実選曲からcompiled Viewの画像・バナー画素更新と既定画像・背景なしへの復帰を確認する。 |
| 共通譜面キュー、現在曲表示、再生状態投影、外部playerの能力 | `PlaybackPanelViewModel.NowPlayingChart` / `GetPlaybackStatus`、[`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs)、`PlaybackChartQueue` | `PlaybackPanelViewModelTests.PlaybackPanelChartQueueKeepsIdentityStatusAndSingleAdvance` は混在入力を代表として現在対象・ヘッダー・PLAY/PAUSE・一回の送り・旧対象の状態解除と `SCORE_UNSENT` / `SEARCHING` の維持を、`PlaybackPanelDoesNotSendBmsonToAnExternalPlayerWithoutThatCapability` は外部能力の分離を確認する。 |
| 要求状態と実効状態、初期フレーム、差替え・再読込み、遷移 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackPanelView`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs) |
| 初期Collapsedでの子HWND生成、UIスレッド・プロセス所有 | [`ExternalPlayerHwndHost`](../../../BeMusicSeeker/Views/Playback/ExternalPlayerHwndHost.cs)、`PlaybackPanelView.PlayerHostHandle` | `PlaybackPanelViewModelTests.PlaybackPanelView_CompiledTreeMaterializesCurrentSurfaceAndHeader` は `ContentRendered` 後の継承 `Handle`、親、PID・TIDを確認する。 |
| 面切替・Overlay・切離しと再接続での寿命、物理寸法と予約領域 | `ExternalPlayerHwndHost.ArrangeOverride`、[`PlaybackPanelView.xaml`](../../../BeMusicSeeker/Views/Playback/PlaybackPanelView.xaml) | `PlaybackPanelViewModelTests.PlaybackPanelView_PlayerSurfaceAndOverlayPreserveHostAndPhysicalLayout` は実再生入口から面切替・Overlay・拡大型の中央下端配置と実行時DPIでの物理寸法、小型の要求・実効状態と再読込み時の即時同期・同じHWNDの生存を確認する。`PlaybackPanelView_SameViewModelStateChangesUseTransitionsAndReloadSynchronizesImmediately` は切離し・再接続の寿命と遷移を確認する。固定寸法より小さい `finalSize` の本番到達は確認しておらず、min式は実装レビューで確認する。 |
| 通常初期化完了からのホスト接続、プレーヤー終了待ちと最終解放 | [`MainWindow`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`MainWindowViewHostTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowViewHostTests.cs) の `MainWindowRenderedInitializationAttachesCreatedPlaybackHost` は `ContentRendered` を入口とする接続を、`MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose` は終了gate保留中のUI応答・HWND生存と最終Closed後の無効化を確認する。 |
| Ready成功時のPLAY・一回の次候補、一時コピー寿命、停止の終端 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs)、[`PlaybackChartQueue`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackChartQueue.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`InternalReady_StartsNextPreparationBeforeCompletionOrUiPublication`、`TemporaryCopy_IsRetainedUntilReadyAndRemovedBeforeCompletion`、`FileMutation_StopsCurrentSongBeforeWritingAndRejectsPlaybackWhileBusy`で開始・入力・停止の境界を確認する。[`AudioContractsTests`](../../../BeMusicSeeker.Tests/Playback/AudioContractsTests.cs)の`PreloadFatal_CloseJoinsPreparationAndNotifiesOnceWithIndependentCleanupFailure`で実内蔵playerの背景故障通知を確認する。 |
| 停止で表示が失効した後の採用済み準備故障の通知 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`AdoptedPreloadFailure_AfterStopNotifiesOnceWithoutRestarting`でB開始が準備中にStopを受けた後の元原因の一回通知、次へ・停止の終端、自動再開なしと停止表示を確認する。 |
| 手動送り待ちの後に残った旧曲の自然終了 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`NaturalExit_QueuedBehindManualNextDoesNotAdvanceTheReplacementSong`でA候補解決中に手動・自然終了・手動を受け付け、A・B・Cだけを開始することを確認する。 |
| 自然終了の停止へ渡った先読み故障の一回通知 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`NaturalSinglePlayStop_NotifiesPreloadCleanupFailureOnceWithoutStartingNextSong`でA開始後のB準備、自然終了callbackとCompletionからの単曲停止、元原因の一回通知、B開始なしと停止表示を確認する。 |
| 外部playerの停止中に受け付けた開始、一時コピー寿命、開始呼出しと返却Taskの失敗 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`LegacyStart_WaitsForPreviousCloseAndRetainsInputUntilInvocation`で通常・一時配置の双方について旧playerの終了前の開始禁止、呼出し時の入力保持、未完了Taskを待たない選曲完了、開始呼出し失敗・開始後の失敗の一回通知を確認する。 |
| 一時改名時の配置先維持、元ファイルの復元、一時コピーの除去 | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`ChartListVirtualViewTests`](../../../BeMusicSeeker.Tests/ChartList/ChartListVirtualViewTests.cs)の`PlaybackPanel_StartAtIndexUsesChartInstallDestinationWhenTemporaryRenameChangesPath`で共通Dispatcherを使い、開始Taskの完了後に再生先pathとファイルの復元・除去を確認する。 |
| Pause・シーク・一覧・モード変更では準備を差し替えず、実際の送り先を優先すること | [`PlaybackPanelViewModel`](../../../BeMusicSeeker/ViewModels/Playback/PlaybackPanelViewModel.cs) | [`PlaybackPanelViewModelTests`](../../../BeMusicSeeker.Tests/Playback/PlaybackPanelViewModelTests.cs)の`SongStart_PreparesOnceAndUsesLiveTargetAfterListAndModeChanges`でAの次にBを準備した後、現在の一覧からCへ進み、Cの開始で次候補を準備することを確認する。 |
| 関連する画面状態と更新 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistWorkspacePresentationStateTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspacePresentationStateTests.cs)、[`PlaylistWorkspaceDetailRefreshTests`](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceDetailRefreshTests.cs) |
| ルート画面への再生管理主体の接続と表示の能力 | [`MainWindowViewModel`](../../../BeMusicSeeker/ViewModels/MainWindow/MainWindowViewModel.cs) | [`MainWindowPlaybackWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaybackWpfTests.cs) |

## 関連資料

[外観](appearance.md)、[音声基盤](../runtime/audio.md)、[WPFテストの実行](../development/testing.md)を参照します。
