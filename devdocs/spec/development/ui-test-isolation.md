# 画面テストの分離

## 目的と適用範囲

画面（WPF）を扱うテストの、共有ホスト・Dispatcher・Windowの所有・表示入力・後片付け・外部UIAによる操作の規則を定めます。通常テストの実行基盤は[テストの実行基盤](test-runtime.md)、検証の実行範囲・結果判定は[テストの実行と受入検証](testing.md)に従います。共有ホストの観測記録とケース境界の検査は[共有ホストの観測とケース境界の検査](ui-test-host-observation.md)に従います。

## 用語

[共通用語集](../glossary.md)と[テストの実行と受入検証](testing.md#用語)の用語を使います。コードの識別子は原表記を使います。

## 仕様

保証の選び方は[画面に関わる保証の分担](test-authoring.md#画面に関わる保証の分担)に従います。以下は、必要と判断した境界での実行・資源管理の規則です。

### 共有ホスト

#### 起動と利用

WPFは `BeMusicSeeker.TestSupport.Wpf` の `TestUiDispatcherHost` にある一つの `Application` と専用のSTA Dispatcherをプロセス内で共有します。最初の `Dispatcher` / `Invoke` / `ProcessQueuedPresentation` / `RunWindowTest` の利用時に遅延起動し、テストごとに作りません。TestSupportを参照するだけではWPF・設定を初期化せず、画面を使わないmoduleでは起動しません。ウィンドウなしの操作は `Invoke`、実ウィンドウ・Popupは `RunWindowTest` と `TestWindowPresentationScope` を使います。

テストごとの `Application`・STAスレッド・独自のDispatcher待機ループは作りません。直接の `Dispatcher.PushFrame` や `HwndSource` は、共通基盤か明示された例外に限ります。

#### 要求の受付と終端

共有ホストへの要求は、実際のDispatcherOperationと既存のホスト終端通知を観測します。ホストが既に終了していた場合と、要求の受理後に終了した場合は、所有スレッドを合流させて元のホスト例外を返します。Dispatcher停止の二次失敗でも終端通知・合流を失わず、二次失敗を元の例外へ添付します。健全なホスト上のAction自身の失敗は保持し、後続の要求は受理します。

#### 未処理例外の記録と受取り

共有ホストのDispatcherで `UnhandledException` に達した例外は、ホストを終端させず継続し、例外本体・順序・発生時のケース（ケースの間なら直前に終了したケース）とともに記録します。ハンドラーは共有ホストにだけ付け、専用STAの検査には付けません。ケースが共有Dispatcherの `UnhandledException` を直接購読して `Handled` を設定することはしません。期間中に未処理例外がないことの表明と、期待する未処理例外の受取りは、次のホストの口を使います。

##### 待機・消化APIの呼出し中の例外

待機・消化API（`AwaitTaskOnDispatcher`・`AwaitPresentationOnDispatcher`・`ProcessQueuedPresentation`・`ProcessQueuedBackground`）の呼出し中に記録された例外があれば、そのAPIは通知を待ち続けず、元の例外で失敗し、記録を消費します（ただし、`ProcessQueuedPresentation` の配置の確定自体が例外を投げた場合を除きます。[表示入力と待機](#表示入力と待機)）。2件目以降は最初の例外の `Data` へ添付します。この失敗で記録は報告済み（消費済み）となり、ケースの終了時に再報告しません。呼出し中に起きたものは、ホスト以外の入れ子フレームで起きたものも対象です。入れ子の待機が先に記録を消費して外側の待機のフレームだけが止まった場合、外側は対象のTaskが未完了なら待機を続けます。

##### `AssertNoUnhandledExceptions`

`AssertNoUnhandledExceptions` は、現在のケースの期間に未処理例外がないことを表明します。記録があれば消費し、各例外の型・メッセージ・スタックを含む `AggregateException`（元の例外を内部例外に保持）で失敗します。

##### `TakeUnhandledExceptions`

`TakeUnhandledExceptions` は、現在のケースの期間の記録だけを発生順に受け取って消費します。他ケースの記録とケースの間の記録は見えず、受け取れません。

##### 記録の対象

記録の対象は `Dispatcher.UnhandledException` に達した例外です。`BeginInvoke`・`SynchronizationContext.Post`、`Delegate` を受け取る旧形式の `Dispatcher.Invoke(DispatcherPriority, Delegate)` の呼出し先、Win32メッセージから配送される入力・ウィンドウイベントのハンドラーなどが該当します。旧形式の `Invoke` の呼出し先の例外は、`Handled=true` のため呼出し側へ返りません。その `Invoke` を囲む待機・消化APIの呼出し中なら、そのAPIが元の例外で失敗します。それ以外は `AssertNoUnhandledExceptions`・`TakeUnhandledExceptions` か、ケースの終了時に表れます。後から呼んだ待機・消化APIは、呼出し開始時の位置以降の記録だけを使うため、失敗しません。

##### 呼出し側へ返る例外

`Action`・`Func` を受け取る `Invoke`・`InvokeAsync` の例外は呼出し側（`Task`）へ返り、記録しません。結果を観測しない `InvokeAsync` の失敗は記録されず、`Task` を待つ側で観測します。待機対象のTaskの失敗も呼出し側へ返ります。

##### 残る限界

WPFは一度処理した例外を印付けし、同じ例外オブジェクトが再び `Dispatcher` 上で未処理になると、`UnhandledException` を呼ばず、従来どおり `Dispatcher` の外へ送出します（同じ失敗Taskを複数の `async void` で待つ場合など）。`Dispatcher.Run` まで出ればホストが終端し、`InvokeAsync`（`Invoke`・`RunWindowTest` を含む）の中ならTaskの失敗、待機中なら待機の失敗として表れます。印を外す対処はWPFの内部実装に依存するため行いません。また、待機APIは呼出し前に記録された未報告分では失敗しません。待つTaskがその例外で失われた継続に依存する場合は、全体の期限による停止になります。

#### テーマの退避と終了処理

共有ホストは現在の `AppearanceTheme` を退避し、準備完了前に保存せずLightへ設定します。終了時は同じDispatcherで復元してから `Application` とDispatcherを停止し、通知とスレッドの合流で待ちます。未起動なら何もしません。既存の `Application` との競合、起動失敗、呼出し・終了の失敗は表面化させ、`Application.ResourceAssembly` は変更しません。

### テストのUIスケジューラー

UIスケジューラー（`IUiScheduler`）を必要とするテスト補助は、呼出し側が窓口を新しく作り特定のDispatcherを必要としない場合、既定の `TestUiScheduler.CreateDefault()` を使います。構築スレッドが共有ホストのDispatcherスレッドならそのDispatcherへ投稿し、それ以外は本番アダプターのDispatcherなしのインライン実行に解決します。

- Dispatcherを返す関数は、メッセージポンプを持つDispatcherかnullに解決します。共有ホストの外のスレッド（MSTestのworker・プールのスレッド・専用スレッド）で `Dispatcher.CurrentDispatcher` を捕捉する関数は使いません。ポンプされないDispatcherへの予約は実行されず、別スレッドからの同期実行が戻らないためです。`Dispatcher.CurrentDispatcher` は共有ホストのスレッド上（`RunWindowTest`・`Invoke` の中など）では共有ホストのDispatcherと同じです。
- 共有ホストのDispatcherを必要とする場合は、`TestUiDispatcherHost.Dispatcher` を返す関数を明示して渡します（例：`new TestUiScheduler(() => TestUiDispatcherHost.Dispatcher)`）。
- Dispatcherがないときの本番アダプターのインライン実行は、呼出し元のスレッドでその場で実行するだけで、UIスレッドへの直列化を保証せず、複数のスレッドから同時に実行されえます。UIスレッドでの直列化を前提とする検査は、共有ホストのDispatcherを使います。

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

#### 表示入力の反映

表示入力の反映には共通の `ProcessQueuedPresentation` を使い、実controlの値・選択・配置を確認します。この補助の契約は、次の(a)〜(e)をこの節の正本とします。

##### (a) 手順と順序

戻る前に、次を一度ずつ行います。(1) Loaded優先度以上の予約の消化。(2) 共有ホストのスレッドでの配置の直接の確定。ホストが所有する専用の要素の `UpdateLayout` を一度呼び、共有ホストのDispatcherで、PresentationSourceに接続された木の配置を同期に確定します（ホストの要素自体は木に入れないため測定されません）。(3) 配置が積んだLoaded優先度以上の予約の消化。(2)と(3)は繰り返して収束させません。

##### (b) 保証しないもの

Loadedイベントの発火の時機（配置との前後を含む）と描画の完了は保証しません。Loadedイベントの発火が必要な場合と、Input優先度へ下げた描画の実行が必要な場合は、Background優先度以上（Input優先度へ下げた描画を含む）の積まれている予約を消化する `ProcessQueuedBackground` を使います（Loaded優先度の予約と、Input優先度へ下げた描画の中の発火を含みます。まだ積まれていない予約は対象外です）。描画の完了自体は、実通知と `AwaitPresentationOnDispatcher` で待ちます。機能処理、Background優先度の予約、閉鎖、Dispatcher全体のアイドルも保証しません。

##### (c) 配置を直接確定する理由

WPFは次の描画を即時に予約するとき、未実行のInput優先度の確認用の予約がなければ積み、それが0.5秒を超えて実行されていないと、次の描画をInput優先度へ下げます。描画の外で積まれた配置の要求は降格済みの描画をRender優先度へ戻すことがありますが、WPFは未処理の配置要求が残っている間は後の要求を同じ要求に合流させるため、後の要求で優先度が戻らない場合があります。描画の最中に積まれた要求も戻さず、既に積まれた描画の優先度も描画の外の経路から書き換えられ得ます。そのため、配置の要求を含む描画が、Input優先度へ下げられたまま残ることがあります（例：描画の最中に積まれた要求。経路の網羅は主張しません）。Loaded優先度までの消化では、Input優先度へ下げた描画に含まれる配置が処理されないため、配置を直接確定します。

##### (d) 例外の扱い

(2)の配置の確定が例外を投げた場合は、その例外が元のまま伝わり、(3)は行わず、呼出し中に記録された未処理例外は消費されずケースの境界で報告されます（上の待機・消化APIの一般則の例外です）。それ以外の呼出し中に記録された未処理例外は、(3)の後で元の例外で失敗し、記録を消費します。

##### (e) 確認方法

(2)の効果はWPFの内部の時機に依存して決定的に作れないため恒久テストを持たず、次の二つで確かめます。

静的確認として、経路が `Application.MainWindow`・`Application.Windows`・テストのWindowを参照しないこと、`UpdateLayout` の対象がホストの所有する木に入らない要素だけであること、(2)が(1)の消化の後・(3)の前に呼ばれること、(2)の例外が元のまま伝わり、(3)と記録の消費を行わないことを確認します。

負荷再現として、Releaseビルドの `BeMusicSeeker.Windows.Tests` の実行ファイルを直接使い、`FullyQualifiedName~SearchEditor_ShortSectionsSizeToContentForSavedValuesAndFields` のfilterを4並列で各5回（計20回）、同時に `Name~SearchEditor` のfilterを2並列で繰り返し、対象ケースが失敗しないことを確かめます。この条件は修正前の実装で失敗を再現した条件です。WPF（.NET）の版を上げるときは、同じ確認を繰り返します。

#### 表示入力の待機

表示入力の待機では、次を守ります。

##### 実処理の終端の待機

実処理は、利用側のTaskまたは適用・終端の通知を待ちます。入力側のTaskCompletionSourceの完了だけを利用側の終端としません。

##### 未完了の否定観測

未完了の否定観測は到達を止める待合せを保持して行い、`finally` で解放と開始済みTaskの回収を行います。

##### 成功後に作用を持たないイベントハンドラー

成功後に追加の作用を持たないイベントハンドラーは、コンパイル済みの入口の引数・受理状態と委譲先Taskの回収、委譲後に追加の作用がないことの静的な確認で分担します。

##### 先行する操作の終端を前提にする入力

先行する操作の終端を前提に次の入力（F2による編集開始など）を送る場合は、受付の解放や通知の到着ではなく、その操作の実Taskの終端を待ちます。MainWindowの入口が `PublishScenarioOperation` で公開する操作（セル編集の終了など）の実Taskは、観測用の `MainWindowViewModel.ScenarioOperationStarted` で捕捉できます。製品が先行操作の終端まで次の操作を拒否する契約を、入力の再送や固定時間の待機で迂回しません。

##### compiledの `async void` ハンドラーの終端

実Dispatcherへ配送するcompiledの `async void` ハンドラーの終端は、`MainWindowEventCompletion.CaptureAsyncEventCompletion` で捕捉します。イベントの発生が戻り、捕捉中に現在の文脈を取り込んだ処理の全Post（awaitの継続などを含む）の配送と開始済みの全操作が終端するまで完了せず、同じ発生の中で後から開始したハンドラーを取りこぼしません。ハンドラーが一度も開始しなければ、戻った時点で開始数0（`started=0`）を示す診断で失敗し、無期限には待ちません。

Postのcallbackが投げた例外（`async void` の例外を含む）は元の例外のままCompletionを失敗させ、Dispatcherへの送出も維持するため共有ホストの記録と報告が重複します。一次の失敗はCompletion側です。失敗したCompletionを、記録経路の待機の外（すでに完了した後など）で観測するケースは、`TakeUnhandledExceptions` で記録を受け取ります。

開始数の累計は `Capture` が返す `StartedCount` で参照し、`OperationStarted` の通知（`async void` のほか `AsyncOperation` なども含む）の累計です。例外時に `AsyncVoidMethodBuilder` が「例外のPost、続いて `OperationCompleted`」の順で呼ぶ公開の呼出し順に依存し、逆転すれば例外のPostの前に完了しうるため、例外を投げるハンドラーの検査が検出します。

Completionの完了後に外部のcallbackから開始した操作は対象外です。

#### Background優先度までの予約の消化

注入や表明の前にBackground優先度までの予約を消化する必要がある場合は、`ProcessQueuedBackground` を使います。呼出し前に積まれたBackground以上の予約と、消化中に積まれたBackgroundより高い優先度の予約を、戻る前に実行済みにします。UIスレッド上とテストスレッドの両方から呼べます。ContextIdle・ApplicationIdle・SystemIdleは境界にしません。まだ積まれていないworkerの後続予約や、消化中に積まれたBackgroundの予約は保証せず、それぞれの実通知・Taskで待ちます。その場で空の処理だけを持つ `InvokeAsync` をBackgroundに積んで待つ形は、この補助へ置き換えます。予約した処理がフラグなどの作用を持ち、その実行自体を後で表明する形は、消化ではなくその予約の実行を観測するので置き換えません。

#### 終端の後に予約される表示

終端の後に別途予約される表示（`ObserveFault` で捨てるTask、`async void` の表示など）が存在しないことを、固定時間の経過で判定しません。注入した窓口に対し、生成元の終端と必要な消化（`ProcessQueuedBackground` など）を確定させた後で表明します。[非同期処理の待機](test-runtime.md#非同期処理の待機)の短い否定観測は、この終端と消化を確定した後の補助としてだけ使い、それを不在の根拠にはしません。

#### Dispatcher上のTaskの待機

Dispatcher上の通常の機能Taskの待機は、局所的な制限時間を加えない `TestUiDispatcherHost.AwaitTaskOnDispatcher` を使います。描画・閉鎖・ネイティブ解放の通知の欠落は、同じDispatcher基盤の `AwaitPresentationOnDispatcher` で、既定15秒の制限時間付きで観測します。明示された通知の制限時間と、通常のTaskの外側の制限時間は別に扱います。

#### 表示の観測待機

`ShowAndWaitForContentRendered` は表示前に通知を購読し、制限時間付きのDispatcher処理で読込み・描画・非ゼロの配置・HWNDを確認します。任意の `renderNotificationTimeout` はLoaded到達後のContentRendered通知の待機だけに適用し、Loaded・Dispatcher・後片付けの制限時間へ流用しません。モーダル、即時終了、描画通知を制御する場合は、表示直前に `PrepareForOwnedPresentation` を呼びます。ただし、`B` と閉鎖の検査（未準備のモーダルを表示する共通基盤自体の恒久テスト）は準備を通さないことが目的の例外です（[共有ホストの観測記録](ui-test-host-observation.md#共有ホストの観測記録)）。

表示の観測待機は、実際の描画・配置・ネイティブ条件の成立を完了条件とします。成立後に、無関係な継続処理による一般的な `ApplicationIdle` 待ちを成功条件へ追加しません。描画通知がない場合、即時終了、ネイティブ観測の失敗、期限超過を成功へ置き換えず、表示観測と後片付けの待機を区別します。

### 表示範囲とWindowの所有

#### 表示範囲の排他

実Window・Popupの表示範囲（`TestWindowPresentationScope`）は共有のWindow・HWND一覧を観測するため、準備から処理の終端・後片付けまで非並列にします。同一テストの親子Windowには同じ表示範囲を渡し、独自の入れ子の表示範囲を作りません。Windowを作らず共有状態を変えない `Invoke` は、この理由では直列化しません。実Dispatcherの終了を保証する専用STAと、共通基盤の意図的な入れ子の表示範囲の検査は、それぞれの保証のために維持します。

#### 未準備のWindowの表示

どの表示範囲でも `PrepareForOwnedPresentation` を通していないWindowが共有ホストで表示された場合は、型・Title・モーダルなどを記録し、表示したケース（ケースの間なら次のケースの開始時、module終了後ならCleanup）の失敗にします。Windowが残っていれば、非モーダルは境界が同期的に閉じ、未準備のモーダルは検出器が閉じて本体を戻らせます（[共有ホストの観測記録](ui-test-host-observation.md#共有ホストの観測記録)）。表示範囲の準備を通したWindowはモーダルでも記録しません。

#### MainWindowの寿命

MainWindowの生成前から、終了処理・`Closed`・設定購読の解除・`Resources["vm"]` の復元までは `MainWindowTestLifetime` が所有します。機能の入力と実処理の終端の観測は機能側へ残します。コンパイル済みの内容だけを描画する場合も共通の表示範囲の実Windowを使い、Binding・値・選択・配置の表明を維持します。

#### コンストラクターだけを使うハーネス

コンストラクターだけを使うテスト用ハーネスは、`PrepareConstructorOnlyShell` で既存のシェル起動境界へ完了済みの初期化を渡し、表示だけのfixtureから本番の起動を開始しません。実際の起動・起動取消・終了の接続は、`MainWindowViewHostTests` とPlaylistの `ActualMainWindowFixture` が本番の起動経路で検証します。

### ダイアログ窓口の接続

未宣言のダイアログ要求の記録（項目 `D`）は[項目 `D`](ui-test-host-observation.md#項目-d未宣言のダイアログ要求失敗にします)に従います。

実ダイアログへ届きうる経路には、次のいずれかを接続します。所有者を解決できないこと（`OwnerUnavailable`）や `Application.MainWindow` の有無を、通知を抑止する手段にしません。

#### 記録用の窓口

通知の記録だけを確かめる場合に使います。所有者の解決に依存せず窓口ごと差し替える形を主とします（例：`SettingsDialogHarness.RecordingDialogService`）。`SettingsDialogHarness.RecordingDialogService` は通知・確認だけを数え、ウィンドウ・ピッカー・進捗は `UndeclaredDialogs` へ委ねて `D` にします。

##### 構成への窓口の注入

`ApplicationComposition` の `dialogService` は必須（null は `ArgumentNullException`）で、本番は根の窓口を渡します。テストは、窓口を宣言しないとき `UndeclaredDialogs.Create()` を、通知・確認を宣言するときは記録用の窓口を明示して渡し、構成が暗黙に実の `UiDialogCoordinator` を作る経路を持ちません。

##### 共通の窓口と専用の引数

この窓口は、構成が作るすべての所有者（再生・導入先・メンテナンス・選択譜面の操作などの子構成を含む）の通知・確認を一つの窓口へ集めて観測する入口です。子構成の担当は専用の引数を持たず、構成の `dialogService` を使います。例外として、フォルダ自動リネームは構成のファイル/DB変更の窓口（`fileDbMutationDialogService`。省略時は `dialogService`）を使います。

構成の専用引数（`settingsDialogService`・`fileDbMutationDialogService`・`playlistWorkspaceDialogService`・`restartFailureDialogs`・`libraryDialogService`）を併せて渡した経路は専用の窓口が優先され、共通の窓口へは届きません（[`ApplicationCompositionDialogRoutingTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ApplicationCompositionDialogRoutingTests.cs)）。

##### `MainWindowViewModelTestFactory.Create` の既定の窓口

`MainWindowViewModelTestFactory.Create` は、構成の共通の窓口に未宣言の要求を `D` で失敗にする窓口（`UndeclaredUiDialogService`）を渡します。未注入の通知・確認が出るケースは `D` で失敗になるため、通知を期待するケースは、記録用の窓口を `settingsDialogs`・`playlistWorkspaceDialogs`・`fileDbMutationDialogs` へ明示的に渡して表明します。

##### 構成を直接構成する経路と `MainWindow` 側の経路

Createの専用引数で差し替えられない経路（ライブラリ、再起動失敗、保守の再検査など構成の共通の窓口を使う経路）で通知を宣言するときは、`ApplicationComposition` を直接構成し、`dialogService` か該当する専用引数（`settingsDialogService` など）で記録用の窓口を渡します。これらの専用引数は従来どおり共通の窓口より優先され、`ApplicationComposition`（ViewModel側）へ届く窓口だけを差し替えます。

プロパティ・一括編集・インポート・ランプビューアーなど `MainWindow` 側の経路は、構成の `playlistWorkspaceDialogService` で渡し、`MainWindow` は `MainWindowViewModel` から取得します（`MainWindow` は窓口の引数を持ちません）。

#### `UiDialogCoordinator` の `messagePresenter` だけを差し替える形

`UiDialogCoordinator` は `messagePresenter` の前に所有者を解決し、所有者が null なら `messagePresenter` を呼ばず `OwnerUnavailable` を返します。所有者は、登録中の表示中モーダル、要求の `Owner`、アクティブなWindow、`MainWindow` の順に解決されます。表示範囲の実Windowは非アクティブで表示されるため、アクティブなWindowとしては解決されません。

そのため、要求の `Owner` へ表示範囲の実Windowを渡す経路、または `modalScopeFactory` で登録した表示中のモーダルと組み合わせる場合に限り、記録用に使えます。`MainWindow` への自動設定には頼りません。

差し替えが効くのはメッセージと確認（`ShowMessageAsync`・`ConfirmAsync`）だけで、`ShowWindowAsync`・ピッカー・進捗表示は実ダイアログのままです。

#### 準備を接続した `UiDialogCoordinator`

実ダイアログを表示して確かめる場合に使い、`modalScopeFactory` の中で `TestWindowPresentationScope.PrepareForOwnedPresentation` を呼びます（例：[`PendingPackageRestoreWindowTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/PendingPackageRestoreWindowTests.cs)）。

### 窓口の宣言と既定の窓口

#### 窓口を必須とする型、共有の生成補助と未宣言の窓口

`PlaylistWorkspaceViewModel` と `SettingsDialogViewModel` は、ダイアログ窓口（`playlistWorkspaceDialogService`・`schemaDialogs`）を必須の引数とし、null を受け付けません。窓口を宣言しないテストは `UndeclaredDialogs.Create()` の結果を渡します。観測の境界が有効なmoduleでは `UndeclaredUiDialogService` を返し、境界がないCoreでは、記録が消費されず黙って通ることを避けるため、全メンバーで例外を投げる `TestUiDialogService` を返します。

共有の生成補助のうち、窓口の引数を省略（null）できるものは、省略されたとき同じ補助の結果へ解決します。窓口の引数を持たない補助（`BmsPlaylistTestSupport`・`RegularChartListOwnerTestSupport`・`PlayHistoryReadModelTestFixture`・`PlaylistWorkspaceFixtureFactory` など）は、常に補助の結果を渡します。

#### `SettingsDialogHarness` の既定の窓口

`SettingsDialogHarness` は、窓口を渡さないとき既定で `UndeclaredDialogs.Create()` の窓口を使い、未宣言の要求は `D` になります。通知を期待するケースは `SettingsDialogHarness.RecordingDialogService` を明示的に渡します。`composition` を渡した場合は `dialogs` を使わず、構成側の窓口を使います。同じ設定画面へ渡す `ApplicationDataUninstallWorkflowOwner` と `schemaDialogs` には同じ窓口を渡します。

#### Coreでの窓口の例外と到達の表明

Coreの `TestUiDialogService` は記録ではなく例外を投げるため、製品に窓口の例外を捕捉する経路があるとCoreでは到達が隠れうります。到達を表明したいケースは、記録用の窓口を明示的に注入します。

#### 引数なしの `UiDialogCoordinator` の生成

新しく書くテストで引数なしの `new UiDialogCoordinator()` を使いません。例外は、`UiDialogCoordinator` 自体や、所有者を解決できない経路そのものを検査する場合です。この例外は[ダイアログ窓口の接続](#ダイアログ窓口の接続)の `OwnerUnavailable` の規則にも適用します。

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
| compiledの `async void` ハンドラーの完了判定 | [`MainWindowEventCompletion`](../../../tests/BeMusicSeeker.TestSupport/Wpf/MainWindowEventCompletion.cs) | [`MainWindowEventCompletionTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/MainWindowEventCompletionTests.cs)。購読者なしの開始0件での即時失敗、同期ハンドラーの後にgate待ちのハンドラーがある場合の早期完了の防止と開始数、await後の例外でCompletionが成功にならないこと、単一ハンドラーの終端後の成功を確認する。 |
| 画面の準備、表示、破棄 | [`TestWindowPresentationScope`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestWindowPresentationScope.cs) | [`SettingsControlPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs)、[`WindowPresentationScopeTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WindowPresentationScopeTests.cs)。一般的なアイドルと別に、実際の描画・配置・Win32の結果、入れ子の所有資源、失敗の優先順位、後片付けの消化の失敗の保持を確認する。 |
| 共有ホストのテーマ・意味別リソース | [`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs) | [`WpfThemeResourceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Themes/WpfThemeResourceTests.cs)。Lightと実際のコンストラクターの意味別ブラシへの到達を確認する。 |
| UI moduleの配置と終了の所有 | [`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs)、[`TempDirectoryPublisher`](../../../BeMusicSeeker/TempDirectoryPublisher.cs) | 初期化・例外時を含む回収順序と排他の解放は静的に確認する。[`WpfHostLifecycleTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WpfHostLifecycleTests.cs) と共通probeの [`WpfHostLifecycleProbe`](../../../tests/Fixtures/WindowsProcessProbe/WpfHostLifecycleProbe.cs) は同じホストを使い、子プロセスの生存中にHWND・実スレッド・一時セッションの消失を確認し、解放後に全子プロセスと両パイプを回収する。 |

## 関連資料

[テスト設計](test-authoring.md#画面に関わる保証の分担)、[テストの実行基盤](test-runtime.md)、[テストの実行と受入検証](testing.md)、[共有ホストの観測とケース境界の検査](ui-test-host-observation.md)、[テスト配下の指針](../../../tests/AGENTS.md)。
