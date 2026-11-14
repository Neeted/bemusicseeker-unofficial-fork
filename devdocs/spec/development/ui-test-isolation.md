# 画面テストの分離

## 目的と適用範囲

画面（WPF）を扱うテストの、共有ホスト・Dispatcher・Windowの所有・表示入力・後片付け・外部UIAによる操作の規則を定めます。通常テストの実行基盤は[テストの実行基盤](test-runtime.md)、検証の実行範囲・結果判定は[テストの実行と受入検証](testing.md)に従います。

## 用語

[共通用語集](../glossary.md)と[テストの実行と受入検証](testing.md#用語)の用語を使います。コードの識別子は原表記を使います。

## 仕様

保証の選び方は[画面に関わる保証の分担](test-authoring.md#画面に関わる保証の分担)に従います。以下は、必要と判断した境界での実行・資源管理の規則です。

### 共有ホスト

WPFは `BeMusicSeeker.TestSupport.Wpf` の `TestUiDispatcherHost` にある一つの `Application` と専用のSTA Dispatcherをプロセス内で共有します。最初の `Dispatcher` / `Invoke` / `ProcessQueuedPresentation` / `RunWindowTest` の利用時に遅延起動し、テストごとに作りません。TestSupportを参照するだけではWPF・設定を初期化せず、画面を使わないmoduleでは起動しません。ウィンドウなしの操作は `Invoke`、実ウィンドウ・Popupは `RunWindowTest` と `TestWindowPresentationScope` を使います。

テストごとの `Application`・STAスレッド・独自のDispatcher待機ループは作りません。直接の `Dispatcher.PushFrame` や `HwndSource` は、共通基盤か明示された例外に限ります。

共有ホストへの要求は、実際のDispatcherOperationと既存のホスト終端通知を観測します。ホストが既に終了していた場合と、要求の受理後に終了した場合は、所有スレッドを合流させて元のホスト例外を返します。Dispatcher停止の二次失敗でも終端通知・合流を失わず、二次失敗を元の例外へ添付します。健全なホスト上のAction自身の失敗は保持し、後続の要求は受理します。

共有ホストのDispatcherで `UnhandledException` に達した例外は、ホストを終端させず継続し、例外本体・順序・発生時のケース（ケースの間なら直前に終了したケース）とともに記録します。ハンドラーは共有ホストにだけ付け、専用STAの検査には付けません。ケースが共有Dispatcherの `UnhandledException` を直接購読して `Handled` を設定することはしません。期間中に未処理例外がないことの表明と、期待する未処理例外の受取りは、次のホストの口を使います。

- 待機・消化API（`AwaitTaskOnDispatcher`・`AwaitPresentationOnDispatcher`・`ProcessQueuedPresentation`・`ProcessQueuedBackground`）の呼出し中に記録された例外があれば、そのAPIは通知を待ち続けず、元の例外で失敗し、記録を消費します（ただし、`ProcessQueuedPresentation` の配置の確定自体が例外を投げた場合を除きます。[表示入力と待機](#表示入力と待機)）。2件目以降は最初の例外の `Data` へ添付します。この失敗で記録は報告済み（消費済み）となり、ケースの終了時に再報告しません。呼出し中に起きたものは、ホスト以外の入れ子フレームで起きたものも対象です。入れ子の待機が先に記録を消費して外側の待機のフレームだけが止まった場合、外側は対象のTaskが未完了なら待機を続けます。
- `AssertNoUnhandledExceptions` は、現在のケースの期間に未処理例外がないことを表明します。記録があれば消費し、各例外の型・メッセージ・スタックを含む `AggregateException`（元の例外を内部例外に保持）で失敗します。
- `TakeUnhandledExceptions` は、現在のケースの期間の記録だけを発生順に受け取って消費します。他ケースの記録とケースの間の記録は見えず、受け取れません。
- 記録の対象は `Dispatcher.UnhandledException` に達した例外です。`BeginInvoke`・`SynchronizationContext.Post`、`Delegate` を受け取る旧形式の `Dispatcher.Invoke(DispatcherPriority, Delegate)` の呼出し先、Win32メッセージから配送される入力・ウィンドウイベントのハンドラーなどが該当します。旧形式の `Invoke` の呼出し先の例外は、`Handled=true` のため呼出し側へ返りません。その `Invoke` を囲む待機・消化APIの呼出し中なら、そのAPIが元の例外で失敗します。それ以外は `AssertNoUnhandledExceptions`・`TakeUnhandledExceptions` か、ケースの終了時に表れます。後から呼んだ待機・消化APIは、呼出し開始時の位置以降の記録だけを使うため、失敗しません。
- `Action`・`Func` を受け取る `Invoke`・`InvokeAsync` の例外は呼出し側（`Task`）へ返り、記録しません。結果を観測しない `InvokeAsync` の失敗は記録されず、`Task` を待つ側で観測します。待機対象のTaskの失敗も呼出し側へ返ります。
- 残る限界：WPFは一度処理した例外を印付けし、同じ例外オブジェクトが再び `Dispatcher` 上で未処理になると、`UnhandledException` を呼ばず、従来どおり `Dispatcher` の外へ送出します（同じ失敗Taskを複数の `async void` で待つ場合など）。`Dispatcher.Run` まで出ればホストが終端し、`InvokeAsync`（`Invoke`・`RunWindowTest` を含む）の中ならTaskの失敗、待機中なら待機の失敗として表れます。印を外す対処はWPFの内部実装に依存するため行いません。また、待機APIは呼出し前に記録された未報告分では失敗しません。待つTaskがその例外で失われた継続に依存する場合は、全体の期限による停止になります。

共有ホストは現在の `AppearanceTheme` を退避し、準備完了前に保存せずLightへ設定します。終了時は同じDispatcherで復元してから `Application` とDispatcherを停止し、通知とスレッドの合流で待ちます。未起動なら何もしません。既存の `Application` との競合、起動失敗、呼出し・終了の失敗は表面化させ、`Application.ResourceAssembly` は変更しません。

### テストのUIスケジューラー

UIスケジューラー（`IUiScheduler`）を必要とするテスト補助は、呼出し側が窓口を新しく作り特定のDispatcherを必要としない場合、既定の `TestUiScheduler.CreateDefault()` を使います。構築スレッドが共有ホストのDispatcherスレッドならそのDispatcherへ投稿し、それ以外は本番アダプターのDispatcherなしのインライン実行に解決します。

- Dispatcherを返す関数は、メッセージポンプを持つDispatcherかnullに解決します。共有ホストの外のスレッド（MSTestのworker・プールのスレッド・専用スレッド）で `Dispatcher.CurrentDispatcher` を捕捉する関数は使いません。ポンプされないDispatcherへの予約は実行されず、別スレッドからの同期実行が戻らないためです。`Dispatcher.CurrentDispatcher` は共有ホストのスレッド上（`RunWindowTest`・`Invoke` の中など）では共有ホストのDispatcherと同じです。
- 共有ホストのDispatcherを必要とする場合は、`TestUiDispatcherHost.Dispatcher` を返す関数を明示して渡します（例：`new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher)`）。
- Dispatcherがないときの本番アダプターのインライン実行は、呼出し元のスレッドでその場で実行するだけで、UIスレッドへの直列化を保証せず、複数のスレッドから同時に実行されえます。UIスレッドでの直列化を前提とする検査は、共有ホストのDispatcherを使います。

### ケース境界の検査

UI moduleの `UiModuleLifetime` の `GlobalTestInitialize`・`GlobalTestCleanup` が、共有ホストの未処理例外の記録と、失敗にする観測の記録（[共有ホストの観測記録](#共有ホストの観測記録)）をケースの境界で報告します。ケースの帰属は、Windowsが1 workerで直列に実行されることを前提にします。ホストが起動していない間は、ホスト由来の記録（未処理例外・`B`・`E-a`〜`E-g`）が空で、境界の処理はホスト・`Settings.Default`・`Application` を起動も読取りもしません。未宣言のダイアログ窓口の記録（`D`）はホストの起動と関係なく、呼んだスレッドで記録され、境界はホストを起動せずに出力・判定します。先行ケースがホストを起動した後は、ホストを使わないケースでも、ホスト由来の残留の報告と期間中の記録の帰属を受けます。

- ケースの終了時：そのケースの期間に記録され、ケース内で受け取られなかった例外と、失敗にする観測の記録（項目 `B`、`E-a`〜`E-f`、`D`）および復元の失敗を、そのケースの一つの失敗として報告します。観測と復元は記録の出力より先に、報告は未処理例外の収集（`EndCase`）の後に行い、観測や復元が失敗しても `EndCase` は必ず通します。同じ境界の未処理例外とは一つの失敗にまとめ、元の例外を内部例外として参照のまま持ちます。報告したものは消費し、再報告しません。テスト本体が既に失敗している場合は、本体の失敗を主として残し、境界の記録は `TestContext` の出力へ書いて消費します（この場合、置換は起きません）。クラスの `[TestCleanup]` の失敗は `GlobalTestCleanup` から観測できず、本体が成功していて境界の記録があるとき、TRX上は境界の失敗に置き換わります（失敗の優先順位は本体、表示の観測、後片付け、ケース境界の記録の順ですが、後片付けとの順位はこの限界の範囲で満たされません）。境界の記録を直せば、後片付けの失敗は次の実行で表面化します。境界の失敗のメッセージにも、この置換の可能性を記します。
- ケースの間に起きた例外・未準備のWindowの表示（`B`）・未宣言のダイアログ要求（`D`）：次のケースの開始時に「直前のケースの残留」として、発生元のケース名を付けてその次のケースを失敗させます。無関係な次のケースが失敗しても、帰属を明示して受け入れます。ケースの間に `B` の記録があるときだけ、開始時にもSend優先度の `Invoke` を一回使って終了時と同じ観測と復元を行い、見つけた残留は直前のケースの間へ帰属させます（観測と復元は `BeginCase` より前です）。`B` が無ければ開始時に何も読まず、成功経路の `Invoke` はケースごとに一回のままです。`B` を伴わないケース間の `E` は、従来どおり次のケースの終了時に観測します。
- moduleの終了時：未報告の未処理例外の記録と、ケースの間に残った失敗にする観測の記録（主に `B`）が残っていれば、最後のケースの残留として `UiModuleLifetime.Cleanup` を失敗させます。ホストは終端済みのため、観測も復元もせず、記録だけを判定します。ホストの終了中に追加された記録も対象です。
- ケースの重なり：前のケースの終了処理を経ずに次のケースが始まった場合は、記録がなくても、新しく始まったケースの失敗にします。前のケースの終了処理が呼ばれないこと自体が境界処理の前提崩れで、以後の帰属を信頼できないためです。その時点で消費されていない記録は「帰属不能」として失敗に添えて消費し、誤ったケースへ帰属しません。

- 開始時に失敗したケース：そのケースは本体も終了処理も実行されません。MSTest 4.5.2では、`GlobalTestInitialize` が例外を投げたケースの `GlobalTestCleanup` は呼ばれず、次のケースは通常どおり実行されます（版を上げるときは同じ確認を繰り返します）。そのため境界は、開始に失敗させたケースを記録側でも開始前の状態（ケースの間、直前のケースはそのケース）へ戻し、次のケースの開始で重なりと誤判定しません。開始時に `TestContext` へ書いた行はTRXの標準出力に残り、失敗のメッセージにも記録の行を含めます。

判定は一つの定義に集約し、ケースの開始・終了・moduleの終了の三つの境界が同じ定義を使います。ケース間の帰属・次のケースの開始時の報告・開始時の状態の戻し・観測や本体の失敗時の出力・moduleの終了時の失敗は、ケースの順序や本体の失敗に依存するため恒久テストにせず、制御フローで静的に確認し、局所MTPで一度だけ一時的に確かめます（その確認の改変は取り除きます）。判定の定義（`D` を含む八項目）は `DispatcherHostTests`、復元は `DispatcherHostTests` と `WindowPresentationScopeTests`、未宣言のダイアログ窓口の記録と結果は `UndeclaredUiDialogServiceTests` で確認します。ホストの記録・受取り・待機APIの即時失敗は `DispatcherHostTests` で確認します。

#### 共有ホストの観測記録

未処理例外とは別に、共有ホストの想定外の状態を観測し、記録します。項目 `B`・`E-a`〜`E-f`・`D` は、発生元の修正後はどのケースでも0件であるべきため、ケースの失敗にします（`D` 以外は基準へ戻します）。`E-g`・`E-a-hwnd`・`X-detector` は、正当な残りと区別できない、または検出器自身の欠陥であるため、記録だけです。

- 失敗にする項目と記録だけの項目：失敗にするのは `B`、`E-a`、`E-b`、`E-c`、`E-d`、`E-e`、`E-f`、`D` の完全一致の八項目で、定義は一つです（前方一致にしません。`E-a-hwnd` は昇格しません）。`E-g`（未消化のDispatcher操作）は、後続の通常の処理が正当に積む操作と区別できないため、`E-a-hwnd` は、WPFが遅延生成する内部のHWNDやIMEなど分類しきれない残りを含むため、`X-detector` は検出器の欠陥が全ケースへ波及しないよう、記録だけです。受入の確認では `X-detector` が0件であることを条件にします。
- 失敗の扱い：未処理例外と同じ扱いです。本体が成功していればケースの失敗にし、本体が既に失敗していれば本体の失敗を主として残し、境界の失敗は `TestContext` の出力へ書いて消費します。同じ境界の未処理例外とは一つの失敗にまとめ、元の例外（未処理例外と復元の例外）を内部例外として参照のまま持ちます。失敗のメッセージは、失敗にした各記録の行、帰属、復元できなかった残留、未処理例外の内容と、クラスの `[TestCleanup]` の失敗を置き換えうることを含みます。
- 記録の出力：記録は `[BMS-OBS] <項目> | <帰属> | <内容>` の一行で、全項目を従来どおり、ケースの終了時（`GlobalTestCleanup`）に `TestContext` へ書き、検証1回分のTRXのそのケースの出力から辿れます。ケースの間に記録された分は、次のケースの開始時に「ケース間（直前：ケース名）」の帰属で、そのケースの出力へ書きます。module終了時に残った分は、Consoleと、実行の結果フォルダー（`TestContext.TestRunResultsDirectory`）内の `host-observation-residue-<module名>.txt` へ出力します。結果フォルダーが取得できなければConsoleだけです。出力の失敗は判定を変えません。出力と判定は、境界ごとに同じ記録の一覧を使います。検出器自身の失敗は項目 `X-detector` として同じ経路へ書きます。`TestContext` への書込み自体が失敗した記録は、失敗とともにConsoleへ出力し、失われません。いずれも表示・判定を妨げません。記録の行には、帰属したケース名（ケースの間は直前のケース名）を含めます。例外として、閉鎖の失敗（`close-failed`）の記録は、追加した時点で同じ行を、プロセスの標準エラーの生のストリーム（`Console.OpenStandardError()`、UTF-8の一行を一回の書込みで出して `Flush`、同時の書込みは排他）へ直接出します（MSTestは `Console.Error` をテストごとのメモリ上のバッファへ振り分けるため、`ShowDialog` から戻らず停止してhangdumpで強制終了されると、その行が失われるためです）。出力の失敗は判定を変えず、`X-detector` として扱います。境界・TRXへの出力は従来どおりで、行が二重に見えることは許容します。標準エラーへ直接出る行が強制終了後も残ることは、恒久テストでは確認していません（強制終了する検査を持ちません）。強制終了後に残るという点は、同期の書込みと `Flush`（`HostObservationLog.WriteRawStandardError`）からの推論です。確認できるのは、行が標準エラーへ出ることで、`WindowPresentationScopeTests` の `UnpreparedModalWindow_CancelledClose_IsRecordedAsFailure` を局所MTPで直接実行し、そのプロセスの標準エラーをシェルのリダイレクト（`2>"$env:TEMP\bms-close-failed-stderr.txt"` はPowerShellの例で、出力先はリポジトリの外の一時領域です）でファイルへ取り、`action=close-failed` を含む行があることを一度だけ一時的に確かめます。MSTest.Sdk（現在 `global.json` の4.5.2）またはMTPの版を上げるときは、この確認を繰り返します。
- 項目 `B`（未準備のWindowの表示。失敗にします）：共有ホストのスレッドで表示（初回のレイアウト）されたWindowのうち、どの表示範囲でも `PrepareForOwnedPresentation` を通していないものを、型・Title・モーダル・所有者・HWND・帰属とともに記録します。`Window` の `SizeChanged` へのクラスハンドラーでWindowごとに一度観測し（表示後の最初のレイアウトで観測します）、準備済みのWindow（モーダルを含む）は記録しません。表示されないWindowは通常記録されません。表示したままの非モーダルのWindowは、同じ境界の `E-a` の観測で復元が閉じます。未準備のモーダルは、テスト本体が入れ子のループから戻らず境界へ到達できないため、検出器が閉じます。`ShowDialog` の最初のレイアウトはスレッドがモーダルになる前に起きるため、検出器は表示の観測の直後に `Send` の操作を一つ予約し、その時点で公開APIの `ComponentDispatcher.IsThreadModal`（スレッド全体の状態）が真で、Windowが `Application.Windows` に残っていれば、記録の内容へ `action=close-scheduled` を追記して閉じます（`SizeChanged` の中では閉じず、Dispatcherを進めません）。閉じる入口は復元と同じ一つの関数（`HostResidueRestoration.CloseWindow`、入口の選択は `SelectCloseEntry`）です。閉じたことで記録は消さず、判定は `B` のままケースの失敗です。閉鎖に成功した場合は追加の記録をせず（`ShowDialog` が戻ることで観測します）、閉鎖が取り消された場合と、閉じる入口の呼出しが `WM_CLOSE` を送る前に同期に例外を投げた場合だけ、同じ項目 `B` で `action=close-failed` と理由（`canceled`、または例外の型とメッセージ）・Windowの型・Titleを持つ記録を加えます。同期に受け取った例外はDispatcherの未処理例外へ漏らしません。同期の例外は、上記の未処理例外の残る限界（一度処理した例外の再送出）を除けば、`Close` 自身の前提の検査（閉鎖の途中の再度の `Close` など）に限られます（所有者の終了の入口も `Close` の前に投げる処理を持ちません）。この捕捉は、通常の到達経路のない防御です。表示済みのWindowのClosingの処理（`OnClosing`・`Closing` のハンドラー）の例外は、`Close` が同期に送る `WM_CLOSE` の配送の中で起きてDispatcherの `UnhandledException` に達するため、検出器の呼出しへは返らず、そのケースのDispatcherの未処理例外として記録されます（報告と受取りは上記の未処理例外の規則に従います）。WPFはこの場合もモーダルの枠を終えてWindowを閉じるため、`close-failed` は記録されず、`ShowDialog` は戻ります。`close-failed` の行はプロセスの標準エラーへも直接出ます（上記「記録の出力」）。非モーダルの未準備のWindowは、境界の `E-a` と復元が扱います。表示時の記録の `thread-modal` は最初のレイアウトの時点の値です。ホスト以外のDispatcher（専用STAの検査）は対象外です。
- 項目 `D`（未宣言のダイアログ要求。失敗にします）：画面テストの構成の既定のダイアログ窓口（`UndeclaredUiDialogService`）、および観測の境界が有効なmoduleで、窓口を宣言しないテストが `UndeclaredDialogs.Create()` から受け取る窓口が、記録用の窓口を明示的に注入していない通知・確認・窓口表示・ファイル／フォルダー／保存先の選択・進捗の要求を受けた時点で記録します。製品の通知経路には窓口の例外を捕捉してログだけにする所があるため、例外ではなく記録で失敗にします。内容は `method=<メソッド名> | request=<要求の型名> | title=<題名> | text=<本文の要約> | owner=<要求のOwnerの型名、なければnull>` の一行で、窓口表示だけ `result=<結果の型引数の型名>` を加えます。型名は `FullName` です。題名は通知・確認が `Caption`、ピッカーと進捗が `Title`、本文は通知・確認が `MessageBoxText`、進捗が `Label` で、持たない要求は `-`、値がnullでも `-` です。題名・本文はCRLF・CR・LFをそれぞれ空白一つにし（連続は畳みません）、先頭200文字で切ります（切ったときだけ末尾に `…` を付けます）。200文字目が高位サロゲートなら、ペアを分断せず199文字で切ります。呼出しが戻る前（返したTaskを待つ前）に一件記録し、`B` と同じく呼出し時点のケース（ケースの間なら直前のケース名付き）へ帰属させます。ケースの間の `D` は次のケースの開始時に、直前のケースの残留として失敗にします（ただし開始時の観測と復元は `B` がある場合だけ動き、`D` では動きません）。窓口は状態を変えないため、復元の対象はありません。結果は表示失敗（`Failed`、原因の例外つき）で、`OwnerUnavailable` は返さず、`ShowWindowAsync` のWindow生成と `RunWithProgressAsync` の処理は呼びません（実画面を作りません）。観測の境界を有効にしていないmodule（Core）では記録が消費されず黙って通るため、窓口の生成時に `InvalidOperationException` を投げます。通知を期待するテストは、記録用の窓口を専用の引数へ明示的に渡して表明します。
- 項目 `E-a`〜`E-g`（ケース終了時の残留）：各ケースの終了時に、Send優先度の `Invoke` を一回だけ使って、共有ホストの起動時点を基準に比べます。失敗にする残留を見つけたときだけ、同じ `Invoke` の中で復元します。ホストが未起動・終端済みなら何も起動せず何も読みません。各残留は最初に観測したケースへ一度だけ帰属させ、以後のケースで繰り返しません。値が基準へ戻れば報告済みの印を消し、後で再びずれたら再び報告します。
  - `E-a`：ホストのスレッドの `Application.Windows` に基準の後から残るWindow。基準はホストの起動時点です（表示範囲の開始時点ではありません）。`MainWindow` が残る場合は同じWindowを一つの記録にし、`Application.Windows` に含まれない `MainWindow` だけが `E-b` です。`E-a-hwnd`（記録だけ）は、ホストのスレッドのトップレベルHWNDのうち `Application.Windows` のどれにも（WPFが作るnative ownerを含め）対応しないものです。WPFが遅延生成する内部のHWND（`HwndWrapper[...]` など）も含まれるため、native window classと分類（`kind=`）を記録に添えます。分類は、`wpf-internal`（クラス名が序数比較で `HwndWrapper[` で始まり、`HwndSource` が無く、非表示）、`ime`（クラス名が `IME` または `MSCTFIME UI` に完全一致し、非表示）、それ以外の `other` です。入力は、ホストのスレッドで、観測の時点のHWNDから収集します。限界：Windowの隠し所有者の漏れは `wpf-internal` と同じ特徴を持つため区別できません。
  - `E-c`：`Application.Resources["vm"]`。参照で判定し、別のオブジェクトへ替えるたびにそのオブジェクトごとに一度報告します。観測は、キーが無いことと値がnullであることを区別しません。
  - `E-d`：`Properties.Resources.Culture`（基準は `ja-JP`）。
  - `E-e`：保存値 `Settings.Default.AppearanceTheme`（基準はホストが設定するLight）。`AppThemeService` は適用中のテーマを公開で返さないため、判定するのは保存値だけで、読取りはホストの起動後に限ります。
  - `E-f`：`LogManager.Configuration` の参照の同一性。基準は最初に観測した非nullの構成で、nullから非nullへの変化（`NLogWrapper` の遅延初期化）は基準として採用します。
  - `E-g`：Background以上で未消化のDispatcher操作。公開の `Dispatcher.Hooks`（`OperationPosted`・`OperationCompleted`・`OperationAborted`）で、ホストの起動時点から投稿された操作を追跡し、境界で `Pending` かつ優先度がBackground以上のものを、操作ごとに一度報告します。優先度の変更は購読せず、数える時点の優先度で判定します。ContextIdle以下は数えません。
- 復元：失敗にした残留（その観測で新しく報告した `B`・`E-a`〜`E-f` に対応するもの。以前に報告済みの残留へは手を付けません）を、ホストのスレッドで同期的に基準へ戻します。`Dispatcher` を進めません（`PushFrame`・`ProcessQueued`・`AwaitPresentation` を使いません）。本体が失敗していても復元します。
  - 順序：Window（所有の深い順、同じ深さでは後に作ったものから。表示範囲の後片付けと同じ順序）、`MainWindow`、値（`Resources["vm"]`、文化圏、テーマ、NLog構成）の順です。
  - 閉じる入口：`SettingsWindow`・`PlaylistPropertyDialog`・`PlaylistSummaryBulkEditDialog` は所有者の終了の入口（`CloseForOwnerShutdown`）、それ以外は `Close` です（通常の `Close` はVMを持つこれらの型で閉鎖が取り消され、処理が開始されます）。表示範囲の後片付けの入口は変えません。閉じる前にWindowが `Application.Windows` に含まれなければ閉じた扱いにし、閉じた後は `Application.Windows` から外れたことを確かめます。HWNDの破棄は成功条件に含めません（残れば `E-a-hwnd` の記録に表れます。表示済みの未準備のWindowの `Close` が同じスレッドで同期にHWNDを破棄することは、`WindowPresentationScopeTests.Restore_ClosesShownUnpreparedWindow` が確認します）。`MainWindow`・`ProgressDialog` には特別な入口がなく、閉鎖が取り消されれば復元の失敗です。
  - 復元の対象にしたWindowが `MainWindow` ならば、閉鎖の成否によらず基準（共有ホストでは `null`）へ戻します。復元に成功した項目（閉じたWindowと値）は報告済みの印を消し、同じ値へ再びずれたら再び報告して復元します。`Resources["vm"]` は基準にキーが無ければ `null` を代入せずキーを削除します。
  - 文化圏は `ResourceService.ChangeCulture` の経路で基準（`ja-JP`）へ戻し、`Resources.Culture` と表示の通知（`DisplayCulture`）を戻します。適用されなければ復元の失敗にし、通知の無い直接代入へは後退しません。`ChangeCulture` は `NLogWrapper` を通してログを書き、未初期化なら静的コンストラクターが構成を差し替えるため、NLogの復元を最後にします。
  - テーマは保存値を基準（Light）にし、ホストの起動時と同じ経路で適用します（保存はしません）。成否は保存値で判定します。NLog構成は、復元の時点の基準のオブジェクトを参照のまま `LogManager.Configuration` へ再代入します。基準は最初に観測した非nullの構成のため、テストが置いた構成が基準になりうる限界があります。また、同じ観測で `E-f` が報告されていなくても、文化圏の復元が `NLogWrapper` の初期化で構成を差し替えた場合は、次の境界で `E-f` として表れえます。
  - 項目ごとに捕捉して残りの復元を続け、再試行しません。復元の例外は復元の失敗として元の例外を参照のまま保持します。`Dispatcher` の `UnhandledException` に達した例外は、同じ境界の未処理例外として報告します。復元後に起きる例外は、ケース間の例外として次のケースの開始時に報告します。
  - 復元できなかった残留は、以後のケースへ残りうるため、報告済みの印が残り、同じ残留は再報告しません。基準へ戻れば印が消えます。最初の報告が汚染の起点で、無関係な後続のケースが毎回失敗しないようにするためです。
- 帰属の規則の例外：`E-a`〜`E-g` は発生の時刻を観測しないため、観測したケース（`GlobalTestCleanup` のケース）へ帰属させ、記録に「このケース、またはその直前のケース間で生じた」旨を書きます。`B` と `D` は発生の時点のケース（ケースの間なら直前のケース名付き）へ帰属させます。
- 境界の順序：観測と復元、記録の出力、未処理例外の収集（`EndCase`）、判定の順です。観測処理自体（読取り・比較）が例外を投げた場合は `X-detector` として出力し、判定を変えません。境界が各ケースで明示的に呼ぶ観測で、`UiModuleLifetime` が `AssemblyInitialize` で有効にしたUI moduleだけで動きます。有効にしないmodule（Core）では、共有ホストが起動しても未準備のWindowの検出器・Dispatcher Hooks・基準の読取りを取り付けません。有効化を共有ホストの起動後に呼んだ場合、その起動には取り付けません。
- 対象外・限界：モーダルの閉鎖が取り消された場合と、閉じる入口が `WM_CLOSE` を送る前に同期に例外を投げて閉じられないままの場合は、`ShowDialog` から戻らず境界へ到達できないため、停止が残ります（`close-failed` の記録はプロセスの標準エラーへ直接出るため、強制終了されても実行ログに残ります）。閉鎖の途中の再度の `Close` による同期の例外では、先に始まった閉鎖が後で完了すれば `ShowDialog` は戻ります。表示済みのWindowのClosingの処理の例外では、WPFがWindowを閉じて `ShowDialog` が戻るため停止は残らず、その例外はケースの未処理例外として報告されます。閉鎖の判定はWindowごとに最初のレイアウトの後の一度だけで、最初のレイアウトがモーダルの外で起きた場合（例：`Show`→`Hide`→`ShowDialog`）は閉じず、停止が残ります。スレッドのモーダル状態はスレッド全体の状態のため、準備済みのモーダルが開いている間に表示された未準備の非モーダルWindowも、閉鎖の対象になります（未準備である時点で `B` の失敗の対象です）。Win32のコモンダイアログとネイティブのMessageBox（`EmergencyDialog`）はWPFのWindowではないため `B` の対象外で、ケース終了時にホストのスレッドへ残っていた場合だけ `E-a-hwnd` に現れえます。`UiDialogOwnerResolver` のactive modal・`MainWindowViewModel` のセマフォは、公開の観測点がなくリフレクションを使わないため観測しません。`LogManager.Configuration` の読取りは、NLogが構成の自動読込みを未実施だった場合にその試行を起こしうる限界があります。

観測の比較・失敗の判定・復元は、基準と報告済みの印などの状態を引数に取る関数で、恒久テストは独立の状態（テスト開始時点の現在値を基準）で確認し、境界の記録を作りません。観測の恒久テストと、表示範囲の後片付けの消化の失敗の保持を確かめる恒久テスト（`WindowPresentationScopeTests` の `Cleanup_ContinuesAndKeepsOriginalFailuresWhenProcessQueuedPresentationThrows`）が使う非表示（`WS_VISIBLE` なし）かつ非アクティブ（`WS_EX_NOACTIVATE`）の `HwndSource`（位置の指定は補助で、画面外の保証には使いません。後者は残留HWNDと、その破棄が2回目の消化へ予約を積む契機に使います）、未表示の `new Window`（`D` を作る恒久テストが要求のOwnerに使う未表示のWindowを含みます）、復元の恒久テストが一時的に変える値（`Resources.Culture`、`Resources["vm"]`、保存値 `AppearanceTheme` とそのテーマの適用、`LogManager.Configuration`、`Application.MainWindow`、復元が閉じるWindow）に加え、`B` を作る恒久テストが準備なしで表示するWindow（未準備のモーダルを含む）と、閉鎖を取り消すテスト用Window、閉鎖の処理で例外を投げるテスト用Windowは、この共通基盤自体の検査として明示された例外で、`finally` で元へ戻します。準備なしで表示するWindowには、`TestWindowPresentationScope.PlaceUnpreparedNonActivating` で画面外・タスクバー非表示・`ShowActivated=false`・`WS_EX_NOACTIVATE` を適用しますが、準備扱いにしないため（`B` が記録されます）、前面・矩形の確認と破棄の管理は持たず、モーダルは見張り（`StallTimeout`）で必ず戻します。表明は対象の項目だけに絞り、他の由来で混ざりうる項目（`E-g` など）に依存させません。Windowの表示の記録（`B`）と未宣言のダイアログ要求の記録（`D`）を作る恒久テストは、自分の記録だけを消費し、同じケースの別の由来の `B`・`D` は境界に残して失敗にします。境界が無効なmoduleで `UndeclaredUiDialogService` を生成すると失敗することは、Coreの `UndeclaredUiDialogServiceBoundaryTests` で確認します。ケース終了時の記録・TRXへの出力・module終了時の出力は、制御フローで静的に確認し、TRXへの出力は局所MTPで一度確かめます。

### UI moduleの寿命

UI moduleの `UiModuleLifetime` は、次の順に初期化します。

1. 実行領域の準備
2. 必要な利用者セッションの排他の取得
3. 実行領域内のmodule専有のprofileによる `ApplicationPathSnapshot.FromLayout` / `ApplicationPathPolicy.Configure`
4. 日本語リソースの基準

配置の確定は `Settings.Default` の最初の読取りより前とし、実行ファイル・Assetsは同じビルド出力を指定します。Windows moduleだけは、ResourceServiceとホストの最初の読取りより前に、同じビルドの表示言語ファイルを専有profileの `lang` へコピーし、その場所を言語ディレクトリとして渡します。他のUI moduleの表示言語ファイルの配置は維持します。必要な素材の欠落を、通常の利用者領域への代替で補いません。素のホストではAppの文化圏一覧を複製しません。初期化と終了処理は既存のmoduleの寿命へ集約します。

各ケースが `OwnedProcess` を回収した後、moduleは次の順に回収します。

1. 開始済みホストのWindow・Dispatcherの終端とスレッドの合流
2. 共有設定fixtureの専有領域
3. 生成済みの管理一時セッションの実Taskの終端
4. 利用者セッションの排他の解放

共有設定はホストの終了を確認できた場合だけ回収し、ホストの終了に失敗したときは保持します。設定の回収が失敗しても管理一時セッションと排他の回収を続け、各失敗を集約します。初期化に失敗したときも取得済みの排他を解放します。未使用の管理一時セッションを後片付けのために生成しません。共通の `WindowsProcessProbe` は排他を取得せず、親ケースの `OwnedProcess` が子プロセスと両方のパイプを終端まで所有します。module全体の子プロセス台帳は持ちません。

### 表示入力と待機

表示入力の反映には共通の `ProcessQueuedPresentation` を使い、実controlの値・選択・配置を確認します。この補助の契約は、次の(a)〜(e)をこの節の正本とします。

(a) 手順と順序：戻る前に、次を一度ずつ行います。(1) Loaded優先度以上の予約の消化。(2) 共有ホストのスレッドでの配置の直接の確定。ホストが所有する専用の要素の `UpdateLayout` を一度呼び、共有ホストのDispatcherで、PresentationSourceに接続された木の配置を同期に確定します（ホストの要素自体は木に入れないため測定されません）。(3) 配置が積んだLoaded優先度以上の予約の消化。(2)と(3)は繰り返して収束させません。

(b) 保証しないもの：Loadedイベントの発火の時機（配置との前後を含む）と描画の完了は保証しません。Loadedイベントの発火が必要な場合と、Input優先度へ下げた描画の実行が必要な場合は、Background優先度以上（Input優先度へ下げた描画を含む）の積まれている予約を消化する `ProcessQueuedBackground` を使います（Loaded優先度の予約と、Input優先度へ下げた描画の中の発火を含みます。まだ積まれていない予約は対象外です）。描画の完了自体は、実通知と `AwaitPresentationOnDispatcher` で待ちます。機能処理、Background優先度の予約、閉鎖、Dispatcher全体のアイドルも保証しません。

(c) 配置を直接確定する理由：WPFは次の描画を即時に予約するとき、未実行のInput優先度の確認用の予約がなければ積み、それが0.5秒を超えて実行されていないと、次の描画をInput優先度へ下げます。描画の外で積まれた配置の要求は降格済みの描画をRender優先度へ戻すことがありますが、WPFは未処理の配置要求が残っている間は後の要求を同じ要求に合流させるため、後の要求で優先度が戻らない場合があります。描画の最中に積まれた要求も戻さず、既に積まれた描画の優先度も描画の外の経路から書き換えられ得ます。そのため、配置の要求を含む描画が、Input優先度へ下げられたまま残ることがあります（例：描画の最中に積まれた要求。経路の網羅は主張しません）。Loaded優先度までの消化では、Input優先度へ下げた描画に含まれる配置が処理されないため、配置を直接確定します。

(d) 例外の扱い：(2)の配置の確定が例外を投げた場合は、その例外が元のまま伝わり、(3)は行わず、呼出し中に記録された未処理例外は消費されずケースの境界で報告されます（上の待機・消化APIの一般則の例外です）。それ以外の呼出し中に記録された未処理例外は、(3)の後で元の例外で失敗し、記録を消費します。

(e) 確認方法：(2)の効果はWPFの内部の時機に依存して決定的に作れないため恒久テストを持たず、次の二つで確かめます。静的確認として、経路が `Application.MainWindow`・`Application.Windows`・テストのWindowを参照しないこと、`UpdateLayout` の対象がホストの所有する木に入らない要素だけであること、(2)が(1)の消化の後・(3)の前に呼ばれること、(2)の例外が元のまま伝わり、(3)と記録の消費を行わないことを確認します。負荷再現として、Releaseビルドの `BeMusicSeeker.Windows.Tests` の実行ファイルを直接使い、`FullyQualifiedName~SearchEditor_ShortSectionsSizeToContentForSavedValuesAndFields` のfilterを4並列で各5回（計20回）、同時に `Name~SearchEditor` のfilterを2並列で繰り返し、対象ケースが失敗しないことを確かめます。この条件は修正前の実装で失敗を再現した条件です。WPF（.NET）の版を上げるときは、同じ確認を繰り返します。

表示入力の待機では、次を守ります。

- 実処理は、利用側のTaskまたは適用・終端の通知を待ちます。入力側のTaskCompletionSourceの完了だけを利用側の終端としません。
- 未完了の否定観測は到達を止める待合せを保持して行い、`finally` で解放と開始済みTaskの回収を行います。
- 成功後に追加の作用を持たないイベントハンドラーは、コンパイル済みの入口の引数・受理状態と委譲先Taskの回収、委譲後に追加の作用がないことの静的な確認で分担します。
- 先行する操作の終端を前提に次の入力（F2による編集開始など）を送る場合は、受付の解放や通知の到着ではなく、その操作の実Taskの終端を待ちます。MainWindowの入口が `PublishScenarioOperation` で公開する操作（セル編集の終了など）の実Taskは、観測用の `MainWindowViewModel.ScenarioOperationStarted` で捕捉できます。製品が先行操作の終端まで次の操作を拒否する契約を、入力の再送や固定時間の待機で迂回しません。
- 実Dispatcherへ配送するcompiledの `async void` ハンドラーの終端は、`MainWindowEventCompletion.CaptureAsyncEventCompletion` で捕捉します。イベントの発生が戻り、捕捉中に現在の文脈を取り込んだ処理の全Post（awaitの継続などを含む）の配送と開始済みの全操作が終端するまで完了せず、同じ発生の中で後から開始したハンドラーを取りこぼしません。ハンドラーが一度も開始しなければ、戻った時点で開始数0（`started=0`）を示す診断で失敗し、無期限には待ちません。Postのcallbackが投げた例外（`async void` の例外を含む）は元の例外のままCompletionを失敗させ、Dispatcherへの送出も維持するため共有ホストの記録と報告が重複します。一次の失敗はCompletion側です。失敗したCompletionを、記録経路の待機の外（すでに完了した後など）で観測するケースは、`TakeUnhandledExceptions` で記録を受け取ります。開始数の累計は `Capture` が返す `StartedCount` で参照し、`OperationStarted` の通知（`async void` のほか `AsyncOperation` なども含む）の累計です。例外時に `AsyncVoidMethodBuilder` が「例外のPost、続いて `OperationCompleted`」の順で呼ぶ公開の呼出し順に依存し、逆転すれば例外のPostの前に完了しうるため、例外を投げるハンドラーの検査が検出します。Completionの完了後に外部のcallbackから開始した操作は対象外です。

注入や表明の前にBackground優先度までの予約を消化する必要がある場合は、`ProcessQueuedBackground` を使います。呼出し前に積まれたBackground以上の予約と、消化中に積まれたBackgroundより高い優先度の予約を、戻る前に実行済みにします。UIスレッド上とテストスレッドの両方から呼べます。ContextIdle・ApplicationIdle・SystemIdleは境界にしません。まだ積まれていないworkerの後続予約や、消化中に積まれたBackgroundの予約は保証せず、それぞれの実通知・Taskで待ちます。その場で空の処理だけを持つ `InvokeAsync` をBackgroundに積んで待つ形は、この補助へ置き換えます。予約した処理がフラグなどの作用を持ち、その実行自体を後で表明する形は、消化ではなくその予約の実行を観測するので置き換えません。

終端の後に別途予約される表示（`ObserveFault` で捨てるTask、`async void` の表示など）が存在しないことを、固定時間の経過で判定しません。注入した窓口に対し、生成元の終端と必要な消化（`ProcessQueuedBackground` など）を確定させた後で表明します。[非同期処理の待機](test-runtime.md#非同期処理の待機)の短い否定観測は、この終端と消化を確定した後の補助としてだけ使い、それを不在の根拠にはしません。

Dispatcher上の通常の機能Taskの待機は、局所的な制限時間を加えない `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使います。描画・閉鎖・ネイティブ解放の通知の欠落は、同じDispatcher基盤の `AwaitPresentationOnDispatcher` で、既定15秒の制限時間付きで観測します。明示された通知の制限時間と、通常のTaskの外側の制限時間は別に扱います。

`ShowAndWaitForContentRendered` は表示前に通知を購読し、制限時間付きのDispatcher処理で読込み・描画・非ゼロの配置・HWNDを確認します。任意の `renderNotificationTimeout` はLoaded到達後のContentRendered通知の待機だけに適用し、Loaded・Dispatcher・後片付けの制限時間へ流用しません。モーダル、即時終了、描画通知を制御する場合は、表示直前に `PrepareForOwnedPresentation` を呼びます。ただし、`B` と閉鎖の検査（未準備のモーダルを表示する共通基盤自体の恒久テスト）は準備を通さないことが目的の例外です（[共有ホストの観測記録](#共有ホストの観測記録)）。

表示の観測待機は、実際の描画・配置・ネイティブ条件の成立を完了条件とします。成立後に、無関係な継続処理による一般的な `ApplicationIdle` 待ちを成功条件へ追加しません。描画通知がない場合、即時終了、ネイティブ観測の失敗、期限超過を成功へ置き換えず、表示観測と後片付けの待機を区別します。

### 表示範囲とWindowの所有

実Window・Popupの表示範囲（`TestWindowPresentationScope`）は共有のWindow・HWND一覧を観測するため、準備から処理の終端・後片付けまで非並列にします。同一テストの親子Windowには同じ表示範囲を渡し、独自の入れ子の表示範囲を作りません。Windowを作らず共有状態を変えない `Invoke` は、この理由では直列化しません。実Dispatcherの終了を保証する専用STAと、共通基盤の意図的な入れ子の表示範囲の検査は、それぞれの保証のために維持します。

どの表示範囲でも `PrepareForOwnedPresentation` を通していないWindowが共有ホストで表示された場合は、型・Title・モーダルなどを記録し、表示したケース（ケースの間なら次のケースの開始時、module終了後ならCleanup）の失敗にします。Windowが残っていれば、非モーダルは境界が同期的に閉じ、未準備のモーダルは検出器が閉じて本体を戻らせます（[共有ホストの観測記録](#共有ホストの観測記録)）。表示範囲の準備を通したWindowはモーダルでも記録しません。

実ダイアログへ届きうる経路には、次のいずれかを接続します。所有者を解決できないこと（`OwnerUnavailable`）や `Application.MainWindow` の有無を、通知を抑止する手段にしません。

- 記録用の窓口：通知の記録だけを確かめる場合に使います。所有者の解決に依存せず窓口ごと差し替える形を主とします（例：`SettingsDialogHarness.RecordingDialogService`）。`SettingsDialogHarness.RecordingDialogService` は通知・確認だけを数え、ウィンドウ・ピッカー・進捗は `UndeclaredDialogs` へ委ねて `D` にします。`ApplicationComposition` の `dialogService` は必須（null は `ArgumentNullException`）で、本番は根の窓口を渡します。テストは、窓口を宣言しないとき `UndeclaredDialogs.Create()` を、通知・確認を宣言するときは記録用の窓口を明示して渡し、構成が暗黙に実の `UiDialogCoordinator` を作る経路を持ちません。この窓口は、構成が作るすべての所有者（再生・導入先・メンテナンス・選択譜面の操作などの子構成を含む）の通知・確認を一つの窓口へ集めて観測する入口です。子構成の担当は専用の引数を持たず、構成の `dialogService` を使います。例外として、フォルダ自動リネームは構成のファイル/DB変更の窓口（`fileDbMutationDialogService`。省略時は `dialogService`）を使います。構成の専用引数（`settingsDialogService`・`fileDbMutationDialogService`・`playlistWorkspaceDialogService`・`restartFailureDialogs`・`libraryDialogService`）を併せて渡した経路は専用の窓口が優先され、共通の窓口へは届きません（[`ApplicationCompositionDialogRoutingTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ApplicationCompositionDialogRoutingTests.cs)）。`MainWindowViewModelTestFactory.Create` は、構成の共通の窓口に未宣言の要求を `D` で失敗にする窓口（`UndeclaredUiDialogService`）を渡します。未注入の通知・確認が出るケースは `D` で失敗になるため、通知を期待するケースは、記録用の窓口を `settingsDialogs`・`playlistWorkspaceDialogs`・`fileDbMutationDialogs` へ明示的に渡して表明します。Createの専用引数で差し替えられない経路（ライブラリ、再起動失敗、保守の再検査など構成の共通の窓口を使う経路）で通知を宣言するときは、`ApplicationComposition` を直接構成し、`dialogService` か該当する専用引数（`settingsDialogService` など）で記録用の窓口を渡します。これらの専用引数は従来どおり共通の窓口より優先され、`ApplicationComposition`（ViewModel側）へ届く窓口だけを差し替えます。プロパティ・一括編集・インポート・ランプビューアーなど `MainWindow` 側の経路は、構成の `playlistWorkspaceDialogService` で渡し、`MainWindow` は `MainWindowViewModel` から取得します（`MainWindow` は窓口の引数を持ちません）。
- `UiDialogCoordinator` の `messagePresenter` だけを差し替える形：`UiDialogCoordinator` は `messagePresenter` の前に所有者を解決し、所有者が null なら `messagePresenter` を呼ばず `OwnerUnavailable` を返します。所有者は、登録中の表示中モーダル、要求の `Owner`、アクティブなWindow、`MainWindow` の順に解決されます。表示範囲の実Windowは非アクティブで表示されるため、アクティブなWindowとしては解決されません。そのため、要求の `Owner` へ表示範囲の実Windowを渡す経路、または `modalScopeFactory` で登録した表示中のモーダルと組み合わせる場合に限り、記録用に使えます。`MainWindow` への自動設定には頼りません。差し替えが効くのはメッセージと確認（`ShowMessageAsync`・`ConfirmAsync`）だけで、`ShowWindowAsync`・ピッカー・進捗表示は実ダイアログのままです。
- 準備を接続した `UiDialogCoordinator`：実ダイアログを表示して確かめる場合に使い、`modalScopeFactory` の中で `TestWindowPresentationScope.PrepareForOwnedPresentation` を呼びます（例：[`PendingPackageRestoreWindowTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/PendingPackageRestoreWindowTests.cs)）。

`PlaylistWorkspaceViewModel` と `SettingsDialogViewModel` は、ダイアログ窓口（`playlistWorkspaceDialogService`・`schemaDialogs`）を必須の引数とし、null を受け付けません。窓口を宣言しないテストは `UndeclaredDialogs.Create()` の結果を渡します。観測の境界が有効なmoduleでは `UndeclaredUiDialogService` を返し、境界がないCoreでは、記録が消費されず黙って通ることを避けるため、全メンバーで例外を投げる `TestUiDialogService` を返します。共有の生成補助のうち、窓口の引数を省略（null）できるものは、省略されたとき同じ補助の結果へ解決します。窓口の引数を持たない補助（`BmsPlaylistTestSupport`・`RegularChartListOwnerTestSupport`・`PlayHistoryReadModelTestFixture`・`PlaylistWorkspaceFixtureFactory` など）は、常に補助の結果を渡します。`SettingsDialogHarness` は、窓口を渡さないとき既定で `UndeclaredDialogs.Create()` の窓口を使い、未宣言の要求は `D` になります。通知を期待するケースは `SettingsDialogHarness.RecordingDialogService` を明示的に渡します。`composition` を渡した場合は `dialogs` を使わず、構成側の窓口を使います。同じ設定画面へ渡す `ApplicationDataUninstallWorkflowOwner` と `schemaDialogs` には同じ窓口を渡します。Coreの `TestUiDialogService` は記録ではなく例外を投げるため、製品に窓口の例外を捕捉する経路があるとCoreでは到達が隠れうります。到達を表明したいケースは、記録用の窓口を明示的に注入します。

新しく書くテストで引数なしの `new UiDialogCoordinator()` を使いません。例外は、`UiDialogCoordinator` 自体や、所有者を解決できない経路そのものを検査する場合です。この例外は上の `OwnerUnavailable` の規則にも適用します。

MainWindowの生成前から、終了処理・`Closed`・設定購読の解除・`Resources["vm"]` の復元までは `MainWindowTestLifetime` が所有します。機能の入力と実処理の終端の観測は機能側へ残します。コンパイル済みの内容だけを描画する場合も共通の表示範囲の実Windowを使い、Binding・値・選択・配置の表明を維持します。

コンストラクターだけを使うテスト用ハーネスは、`PrepareConstructorOnlyShell` で既存のシェル起動境界へ完了済みの初期化を渡し、表示だけのfixtureから本番の起動を開始しません。実際の起動・起動取消・終了の接続は、`MainWindowViewHostTests` とPlaylistの `ActualMainWindowFixture` が本番の起動経路で検証します。

### 非アクティブ表示と外部入力

共通基盤は非アクティブ表示専用です。表示直前に手動配置、タスクバー非表示、非アクティブ表示を適用し、全モニターの外へ置きます。HWND生成時には既存の拡張スタイルを保って `WS_EX_NOACTIVATE` を設定・再読取りします。前面でないことと矩形も確認し、Popupにも開く境界で同じ規則を適用します。ネイティブAPIの失敗や表示観測の失敗は、テスト本体とは独立して保持します。

通常テストに前面操作の例外は設けません。次を合否条件にしません。

- 実OSカーソルの取得・移動や `Mouse.GetPosition` による物理位置
- 実フォーカスの取得成功、物理修飾キー、入力キャプチャ
- Popupが外部入力で閉じないこと

イベントを明示しても呼出し先がOSの状態を参照する場合は分離できていないため、本番の処理へ明示入力を渡す境界で検証します。Popupの内容と接続は、開閉イベント・操作入力、閉じたテンプレートのBinding、明示配置などで確認します。

非公開のWPFメソッドによるフォーカスの偽装、テスト専用の本番分岐、外部入力を無視する製品仕様の変更は行いません。実際のフォーカス移動・IME等のOS接続は[実機確認](testing.md#利用者に推奨する実機確認)と分担します。共有Dispatcherの待機中には別テストが再入し得るため、Dispatcherの利用だけで排他できると考えず、[資源の分離と共有](test-runtime.md#資源の分離と共有)に従います。

### 後片付けと失敗の優先順位

開始時の共有スレッドのウィンドウ・HWNDを基準に、追跡したPopup、深い所有関係から順にウィンドウ、購読、Dispatcherに残った描画処理、残留HWNDを片付けます。

- 開始時に存在し、明示的に追跡していない別の所有者のWindowは、後からHWNDを持っても、その表示範囲の残留破棄に含めません。そのWindowに属する実際のネイティブの所有者も、訪問済みのハンドルで停止する所有チェーンから導出して保護します。
- 閉鎖前に `Closed` を購読し、実際の閉鎖を待ってから購読を解除します。未表示・閉鎖済みの対象は区別し、閉鎖の取消やWindow・HWNDの残留を成功にしません。
- 機能処理の開始済みTaskは呼出し側が回収します。設定画面には `CloseForOwnerShutdown` を使います。
- 後片付けの2回の消化（`ProcessQueuedPresentation`。購読の解除の後と、残留HWNDの破棄の後）が例外を投げても、その例外を元のオブジェクトのまま、それ以前の失敗に続けて発生順に後片付けの失敗へ保持し、残留HWND・Windowの検査と登録済みの失敗の報告を続けます。失敗を成功にせず、消化が消費した記録は後片付けの失敗としてだけ報告します。確認は `WindowPresentationScopeTests` の `Cleanup_ContinuesAndKeepsOriginalFailuresWhenProcessQueuedPresentationThrows` です（配置の確定の例外は、後片付けが発生元で絞らないため同じ契約で扱い、静的に確認します）。

失敗の優先順位はテスト本体、表示観測、後片付けの順です。

### 外部UIAによる操作

外部のUI Automation（UIA）クライアントからの操作は、Desktopで確認します。局所的なWPF検証のプロバイダー呼出しの成功だけで、外部クライアントからの操作、通知の受信、本番構成の保存・正常終了・再起動を確認済みにはしません。

- 専有データで起動した本番の実行ファイルへ接続し、実入力、表のパターン、実際の編集欄、ドラッグ、確認画面を操作します。
- 操作対象は、所有するプロセスと親要素の範囲、既存の意味を持つキー（`AutomationId` 等）で一意に選びます。同名要素の先頭一致で代用しません。
- 編集などの非同期操作は、入力前に対象を限定して観測を登録し、登録の応答を確認してから入力します。成功は、既存の実Taskの終端と、独立して読み取った保存値で確認します。UIA呼出しの復帰や通知の到着だけでは保存成功と判定しません。

機能固有の観測条件は対象機能の仕様（例: [一覧表示](../ui/table-view.md)）に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| テストのUIスケジューラーの既定の捕捉先 | [`TestUiScheduler`](../../../tests/BeMusicSeeker.TestSupport/Components/TestUiScheduler.cs)（`CreateDefault`）、本書の[テストのUIスケジューラー](#テストのuiスケジューラー) | Coreの [`TestUiSchedulerDefaultTests`](../../../tests/BeMusicSeeker.Core.Tests/Verification/TestUiSchedulerDefaultTests.cs) が、ポンプされないスレッドで構築した既定の窓口が予約をその場で実行し、別スレッドからの同期実行が戻ることを確認する。Windowsの [`TestUiSchedulerHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/TestUiSchedulerHostTests.cs) が、ホストのスレッドで構築した既定の窓口がホストのDispatcherへ投稿して消化後に実行すること、workerではnullに解決することを確認する。 |
| Dispatcherの受付・終端・通常Task待機 | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`DispatcherHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/DispatcherHostTests.cs)。終了済み・受理後の終了と停止の二次失敗、Action失敗後の継続、合流、利用側の成功・失敗・取消・通知欠落、単一STA、Background優先度までの消化（UIスレッド上・テストスレッド、idle優先度を含めない）を確認する。`ProcessQueuedPresentation` の配置の直接の確定は恒久テストを持たず、[表示入力と待機](#表示入力と待機)の(e)の静的確認と、Releaseの実行ファイルを直接使う並列負荷の再現（対象ケース4並列で各5回（計20回）と `Name~SearchEditor` 2並列）で一度確かめ、WPF（.NET）の版を上げるときは同じ確認を繰り返す。 |
| 共有Dispatcherの未処理例外の記録・帰属・ケース境界の報告 | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs)、[`HostUnhandledExceptionLog`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostUnhandledExceptionLog.cs)、[`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs) | [`DispatcherHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/DispatcherHostTests.cs)。ホストを終端させず記録し後続の要求を受理すること、複数の記録の順序と一度だけの受取り、待機・消化APIの元の例外での即時失敗と消費、呼出し側へ返る失敗を記録しないことを確認する。ケース終了時・ケースの間・module終了時・重なりの報告と、ホストを使わないmoduleで何も起動しないことは、制御フローで静的に確認し、開始時の失敗の状態の戻し・本体の失敗時の出力・観測が失敗したときの `EndCase`・module終了時の失敗は、局所MTPで一度だけ一時的に確かめる（改変は取り除く）。失敗注入による恒久の実施とは扱わない。 |
| 共有ホストの観測の記録・失敗化・復元（未準備のWindowの表示、ケース終了時の残留 `E-a`〜`E-g`、失敗にする `B`・`E-a`〜`E-f`・`D`） | [`HostObservationLog`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostObservationLog.cs)、[`HostResidueComparison`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostResidueComparison.cs)、[`HostResidueRestoration`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostResidueRestoration.cs)（判定 `HostObservationVerdict` と復元）、[`UndeclaredUiDialogService`](../../../tests/BeMusicSeeker.TestSupport/Wpf/UndeclaredUiDialogService.cs)（`D`）、[`UndeclaredDialogs`](../../../tests/BeMusicSeeker.TestSupport/Wpf/UndeclaredDialogs.cs)（境界の有無による窓口の選び分け）、[`TestWindowPresentationScope`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestWindowPresentationScope.cs)、[`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs)、[`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs) | [`WindowPresentationScopeTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WindowPresentationScopeTests.cs) が準備済みのWindowを記録しないこと、未準備の表示が閉じた後も記録に残ること、記録が表示・Close・Dispatcherの進行を変えないこと、表示済みの未準備のWindowが復元で閉じること、未準備のモーダルが検出器の閉鎖で戻り記録（`close-scheduled`）が残ること、準備済みのモーダルとモーダルでない未準備のWindowを検出器が閉じないこと、閉じる入口の選択（三つの型は `CloseForOwnerShutdown`、それ以外は `Close`）、閉鎖の取消が `close-failed`（`canceled`）として記録されること（自分のタイトルの記録だけを消費する）、Closingの処理の例外が `close-failed` にならず、ケースの未処理例外として一件だけ元の参照のまま表れ、WPFがWindowを閉じて `ShowDialog` が戻ることを確認する。[`UndeclaredUiDialogServiceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/UndeclaredUiDialogServiceTests.cs) が、七つのメンバーの `D` の記録（呼出し時点・行の形・要求のOwner・要約の規則）、表示失敗の結果、Window生成と処理を呼ばないこと、`MainWindowViewModelTestFactory.Create` の既定の接続を確認する。`UndeclaredDialogs` の選び分けは `HostResidueBoundary.IsEnabled` による一行の分岐で、制御フローで静的に確認する。Coreで誤って `UndeclaredUiDialogService` を選ぶと、生成時の例外で全利用ケースが失敗する。[`DispatcherHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/DispatcherHostTests.cs) が、独立の状態で残留のWindow・MainWindow・HWNDのみ・`Resources["vm"]`・文化圏・NLog構成（観測値）・未消化操作を一度だけ報告し、報告済みの印が基準へ戻ると消えて再びずれると再び報告されること（`Resources["vm"]`・文化圏・基準のWindowを再びMainWindowにする場合）を確認する。操作・HWNDの印の整理は同じ参照・ハンドルが戻らない限り挙動に現れないため、挙動としては確認しない。同じテストクラスが、失敗にする八項目だけが失敗になり記録だけの項目は失敗にならないこと、未処理例外・復元の失敗との統合で元の例外を参照のまま持つこと（判定）、Window・MainWindow・`Resources["vm"]`（キーの削除と同じ参照への復帰）・文化圏と表示の通知・テーマの保存値と適用・NLog構成の復元、復元が `Dispatcher` を進めないこと、閉鎖の取消・例外があっても残りを復元し元の例外を保持して再報告しないこと（取り消すWindowがMainWindowでも基準へ戻ること）、復元に成功した値の報告済みの印が消え、復元の直後の同じ値へのずれが再び報告されること、`SettingsWindow` が所有者の終了の入口で閉じること、HWNDのみの残留の分類を確認する。`ThemedMessageBox` など実ダイアログの閉鎖、同期の例外の `close-failed` の記録、ケース境界の出力・ケース間・開始時・module終了時の出力と失敗、ホスト未起動で何も起動しないことは、制御フローで静的に確認し、境界の出力が検証1回分のTRXに載ることは局所MTPで確かめる。 |
| compiledの `async void` ハンドラーの完了判定 | [`MainWindowEventCompletion`](../../../tests/BeMusicSeeker.TestSupport/Wpf/MainWindowEventCompletion.cs) | [`MainWindowEventCompletionTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/MainWindowEventCompletionTests.cs)。購読者なしの開始0件での即時失敗、同期ハンドラーの後にgate待ちのハンドラーがある場合の早期完了の防止と開始数、await後の例外でCompletionが成功にならないこと、単一ハンドラーの終端後の成功を確認する。 |
| 画面の準備、表示、破棄 | [`TestWindowPresentationScope`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestWindowPresentationScope.cs) | [`SettingsControlPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs)、[`WindowPresentationScopeTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WindowPresentationScopeTests.cs)。一般的なアイドルと別に、実際の描画・配置・Win32の結果、入れ子の所有資源、失敗の優先順位、後片付けの消化の失敗の保持を確認する。 |
| 共有ホストのテーマ・意味別リソース | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`WpfThemeResourceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Themes/WpfThemeResourceTests.cs)。Lightと実際のコンストラクターの意味別ブラシへの到達を確認する。 |
| UI moduleの配置と終了の所有 | [`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、[`TempDirectoryPublisher`](../../../BeMusicSeeker/TempDirectoryPublisher.cs) | 初期化・例外時を含む回収順序と排他の解放は静的に確認する。[`WpfHostLifecycleTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WpfHostLifecycleTests.cs) と共通probeの [`WpfHostLifecycleProbe`](../../../tests/Fixtures/WindowsProcessProbe/WpfHostLifecycleProbe.cs) は同じホストを使い、子プロセスの生存中にHWND・実スレッド・一時セッションの消失を確認し、解放後に全子プロセスと両パイプを回収する。 |

## 関連資料

[テスト設計](test-authoring.md#画面に関わる保証の分担)、[テストの実行基盤](test-runtime.md)、[テストの実行と受入検証](testing.md)、[テスト配下の指針](../../../tests/AGENTS.md)。
