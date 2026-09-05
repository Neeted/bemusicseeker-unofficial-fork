# アプリケーション終了処理

## 目的

BeMusicSeeker は DB 読み書き、譜面/アーカイブ/メタデータのファイル IO、起動後バックグラウンド処理を並行して実行するため、Window が閉じられたあとに単純な後始末を行うだけでは SQLite statement やファイル更新中の処理が残る可能性がある。

終了処理では「厳密にすべての非同期処理を証明する」ことは目標にせず、現時点で把握している長時間処理と DB/ファイル IO を現実的に止め、整合性境界まで待ってから終了へ進める。

## 終了の入口

通常終了では `MainWindow.OnClosing` が最初の `Closing` を一度キャンセルし、`ShellShutdownWorkflowOwner` の window-close request を開始する。準備が完了すると `MainWindow.ApplyTerminalShutdown` が window state を capture し、player close の実完了を非同期に待ち、UI 上の settings 保存、audio/一時領域の terminal cleanup を完了してから、composition 済みの `IApplicationLifetimePort.RequestShutdown()` を要求する。production adapter はここで WPF application shutdown を開始する。final lifetime boundary は差し替え可能だが、`MainWindow.OnClosing`、終了準備、terminal cleanupを短絡してはならない。

自動アップデートでは、更新パッケージをダウンロードし、updater の起動情報を先に作成する。この時点で updater exe の作業ディレクトリへのコピーも済ませる。その後、通常終了と同じ `ShellShutdownWorkflowOwner` の準備を通し、既知の DB/IO/background worker が idle になってから updater process を起動し、同じ application-lifetime boundary へ shutdown を要求する。updater は現在の PID 終了を待ってから上書きを開始する。

shutdown preparation は不可逆な終了準備として扱う。updater process の起動が終了準備後に失敗した場合、アプリを半終了状態で継続せず、terminal cleanup 後に同じ application-lifetime boundary へ shutdown を要求する。失敗内容はログへ残す。

`CompleteTerminalShutdownAsync` は進行中・完了済みを含め同じ Task を返す。player の同期 close/drain は worker で実行し、その待機中も UI dispatcher は処理を継続する。window state capture → player close 実完了 → UI 上の設定保存（失敗通知を含む）→ audio runtime 解放 → application shutdown の順序を守る。設定保存失敗はログと通知を行い、既存どおり cleanup を継続する。

準備完了の `IsCloseAllowed` だけでは terminal Window close を認可しない。`MainWindow` は terminal Task を await した後にだけ認可し、再入 `OnClosing` と `Closed` は同期 terminal cleanup を再実行しない。terminal 内の regular chart stop と DB process lock 最終確認も非同期に待つ。timeout、detach、強制 native 解放は追加しない。

## 終了準備で行うこと

`PrepareShutdownAsync` は多重実行されず、2 回目以降は同じ Task を返す。

終了準備では以下を行う。

- `BMSLibrary.RequestShutdown` と `BMSPlaylist.RequestShutdown` を先に呼び、モデル側の worker が shutdown を観測できる状態にする
- startup background task queue の未実行項目のうち、queue 時点で model 側 Running を立てる `lr2_song_db_sync`、`chart_info_hydration`、`maintenance_hydration`、`installable_maintenance` は shutdown 中に依存関係待ちを緩めて drain する。各 worker は shutdown を観測すると DB/ファイル IO に入らず、対応する Running/Completed 状態を戻して終了する
- その他の startup background task は終了時に実行せず、scheduler 上で `discarded` として完了扱いにする
- 終了要求後の新規 queue は skip する
- playlist source build、play history filter、playlist library index prewarm をキャンセルする
- play history の keyword/display-target refresh、playlist reference apply、external playlist sync、playlist reload cleanup が idle になるまで待つ
- regular chart folder rename は登録済みの操作 Task と既存 refresh の実完了まで drain する。rename の fault はログで観測したうえで終了 cleanup を短絡せず、残りの tracked worker と lock が idle になるまで終了準備を続ける
- drop install queue と pending install estimate queue をキャンセルする
- `BMSLibrary.RequestShutdown` で LR2 song.db sync と pending install estimate queue をキャンセルし、chart info/maintenance/score/ranking 系の deferred worker を shutdown skip 可能にする
- `BMSPlaylist.RequestShutdown` で playlist entries hydration と beatoraja `.bmt` 出力の保留要求を止め、キュー済み worker が実行された場合も shutdown skip で状態を戻す
- 起動/リロード用 `_semaphore`、startup background task、drop install、playlist build、prewarm、BMSLibrary/BMSPlaylist の終了待ち対象、LR2 DB process lock、SQLite connection tracker が idle になるまで待つ

通常のアプリ内終了では、終了準備が tracked idle になるまで待つ。アプリ自身は idle を待たずに終了へ進む timeout fallback を持たず、強制終了が必要な場合は OS や外部プロセス kill の責務とする。

IR player score の prefetch は ranking worker の開始前から走るため、prefetch Task 自体も終了待ち対象に含める。`BMSLibrary.RequestShutdown` は新規 prefetch の受付を止め、HTTP 本文受信へ cancellation を伝播する。受信中に終了要求を受けた結果は DB / score に適用せず、通信と ranking の実完了まで drain する。consumer の待機だけを解除して未完了 HTTP を切り離すことはしない。

ただし待機が長引いた場合に原因を追えるよう、主処理系は 60 秒、queue/prewarm 系は 20 秒を warning threshold として扱う。threshold を超えても idle にならない場合は `shutdown wait_slow ...` を warn ログへ 1 回記録し、その後も idle になるまで待機を続ける。

## DB と SQLite statement

`LR2SongDBExtended.SQLiteCommandExtended.GetRawValuesAsString` は例外時にも prepared statement を finalize する。これにより、SQLite connection close 時の `unable to close due to unfinalized statements or unfinished backups` の直接原因になり得る statement 漏れを避ける。

`SQLiteConnectionEx` は `ShutdownOperationTracker` で接続 lifetime を tracking する。終了準備では tracker の active connection count が 0 になるまで待つ。shutdown 準備中に SQLite close 失敗が発生した場合は result に失敗件数を残し、終了ログに記録する。

terminal cleanup の LR2 song/score DB lock 確認は取得後に必ず unlock する。これは最終確認であり、通常は `PrepareShutdownAsync` 側で idle まで待機済みである。最終確認でも lock を取得できない場合は、warning threshold を超えた時点で warn ログを出し、lock を取得できるまで待つ。

coordinated shutdown 中に限り、SQLite close の `unable to close due to unfinalized statements or unfinished backups` 例外は非常用エラーダイアログを出さずログに残す。通常動作中の同じ例外は抑止しない。これは終了直前の表示ノイズを避けるための最後の保険であり、tracked idle まで待つ終了準備で安全な終了タイミングへ寄せることを主対策とする。

## 残るリスク

すべての `Task.Run` を台帳管理しているわけではない。今回の対象は、起動後 background task、LR2 song.db sync、chart info/maintenance/score/ranking 系、playlist hydration、drop install、pending install estimate、playlist build、prewarm など、既知の DB/ファイル IO に寄せている。

SQLite connection lifetime は tracking しているが、すべての DB 操作の意味的な整合性境界を証明しているわけではない。将来新しい long-running DB/IO worker を追加する場合は、shutdown request を観測して停止できること、または `PrepareShutdownAsync` の待機対象に登録することを確認する。

`App.RestartApplication()` は現在も新プロセス起動と mutex 解放を先に行う構造が残っている。`Application.Current.Shutdown()` 自体は `MainWindow.OnClosing` に流れるが、新インスタンスの起動タイミングまで厳密には制御していない。再起動専用の外部 launcher、または MainWindow の coordinated shutdown 完了後に再起動する設計へ寄せる余地がある。

未処理例外時の `Environment.Exit(1)` は安全終了 coordinator を通らない。致命的例外ではプロセス終了を優先しているため、通常終了/自動アップデートと同等の整合性待ちは保証しない。

進捗ダイアログ付きの手動ファイル操作など、一部の UI 起点処理は個別の cancellation token と progress dialog に依存している。終了要求時に UI 側でキャンセル可能なものは止めるが、完全な一覧化は今後の改善対象。

## Verification map: terminal player drain

- `MainWindowViewHostTests.MainWindowPlayerDrainKeepsDispatcherResponsiveAndDefersTerminalClose`: 実非表示 Window の通常終了・更新準備済み終了と再入 Close、player 待機中の UI marker、共有 owner Task、capture / save / audio / lifetime の順序。既存 shared WPF Application と native runtime を使うため class の DNP を維持し、native bootstrap のみを行い device は開かない。finally で gate、player、owner Task、Window、runtime を回収する。
- `ShellShutdownWorkflowOwnerTests`: 一度だけの保存、player が最後に取得した placement の永続化、更新準備との合流、保存失敗時の UI 通知と cleanup 継続。既存 Settings / UI の DNP fixture を使う。
- `ApplicationCompositionTests.CompositionSettingsLifecycleSharesSessionAcrossOpenEditReloadRedisplayAndShutdown`: 既存 settings session の共有契約を async terminal completion まで確認する。

### terminal 中の再生操作

terminal owner は最初の非同期待機より前に PlaybackPanel の再生受付を閉じる。UI command、直接の再生・操作入口、古い player exit による自動送りは以後 player を再起動・操作しない。受付閉鎖は UI 上で player/session lock を待たず、worker の解放処理が既存の曲選択 workflow、player operation の順に合流する。先行する曲解決が復帰しても新たな再生を開始しない。通常の停止・次曲・player 交換ではこの終端受付を閉じない。終了済み player の replay や切離しは行わない。

検証: MainWindowViewHostTests の通常/更新済 Close、PlaybackPanelViewModelTests の終了受付・先行 navigation・通常Stop後再開を既存 session/交換 coverageと併用する。
