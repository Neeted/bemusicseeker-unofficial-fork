# 共有ホストの観測とケース境界の検査

## 目的と適用範囲

画面テストの共有ホストで想定外の状態を観測して記録する検出器と、ケースの境界での報告・失敗化・復元を定めます。検出器と境界処理を保守するときと、テストの出力に表れる `[BMS-OBS]` の失敗を診断するときに読みます。画面テストを書くときの共有ホストの使い方は[画面テストの分離](ui-test-isolation.md)に従います。

## 用語

用語は[画面テストの分離](ui-test-isolation.md#用語)に従います。

## 仕様

共有ホストの未処理例外の記録と受取りは[画面テストの分離](ui-test-isolation.md#未処理例外の記録と受取り)に従います。

### ケース境界の検査

UI moduleの `UiModuleLifetime` の `GlobalTestInitialize`・`GlobalTestCleanup` が、共有ホストの未処理例外の記録と、失敗にする観測の記録（[共有ホストの観測記録](#共有ホストの観測記録)）をケースの境界で報告します。ケースの帰属は、Windowsが1 workerで直列に実行されることを前提にします。

ホストが起動していない間は、ホスト由来の記録（未処理例外・`B`・`E-a`〜`E-g`）が空で、境界の処理はホスト・`Settings.Default`・`Application` を起動も読取りもしません。未宣言のダイアログ窓口の記録（`D`）はホストの起動と関係なく、呼んだスレッドで記録され、境界はホストを起動せずに出力・判定します。

先行ケースがホストを起動した後は、ホストを使わないケースでも、ホスト由来の残留の報告と期間中の記録の帰属を受けます。

#### ケースの終了時

そのケースの期間に記録され、ケース内で受け取られなかった例外と、失敗にする観測の記録（項目 `B`、`E-a`〜`E-f`、`D`）および復元の失敗を、そのケースの一つの失敗として報告します。観測と復元は記録の出力より先に、報告は未処理例外の収集（`EndCase`）の後に行い、観測や復元が失敗しても `EndCase` は必ず通します。同じ境界の未処理例外とは一つの失敗にまとめ、元の例外を内部例外として参照のまま持ちます。報告したものは消費し、再報告しません。

テスト本体が既に失敗している場合は、本体の失敗を主として残し、境界の記録は `TestContext` の出力へ書いて消費します（この場合、置換は起きません）。

クラスの `[TestCleanup]` の失敗は `GlobalTestCleanup` から観測できず、本体が成功していて境界の記録があるとき、TRX上は境界の失敗に置き換わります（失敗の優先順位は本体、表示の観測、後片付け、ケース境界の記録の順ですが、後片付けとの順位はこの限界の範囲で満たされません）。境界の記録を直せば、後片付けの失敗は次の実行で表面化します。境界の失敗のメッセージにも、この置換の可能性を記します。

#### ケースの間に起きた例外・未準備のWindowの表示（`B`）・未宣言のダイアログ要求（`D`）

次のケースの開始時に「直前のケースの残留」として、発生元のケース名を付けてその次のケースを失敗させます。無関係な次のケースが失敗しても、帰属を明示して受け入れます。

ケースの間に `B` の記録があるときだけ、開始時にもSend優先度の `Invoke` を一回使って終了時と同じ観測と復元を行い、見つけた残留は直前のケースの間へ帰属させます（観測と復元は `BeginCase` より前です）。`B` が無ければ開始時に何も読まず、成功経路の `Invoke` はケースごとに一回のままです。`B` を伴わないケース間の `E` は、従来どおり次のケースの終了時に観測します。

#### moduleの終了時

未報告の未処理例外の記録と、ケースの間に残った失敗にする観測の記録（主に `B`）が残っていれば、最後のケースの残留として `UiModuleLifetime.Cleanup` を失敗させます。ホストは終端済みのため、観測も復元もせず、記録だけを判定します。ホストの終了中に追加された記録も対象です。

#### ケースの重なり

前のケースの終了処理を経ずに次のケースが始まった場合は、記録がなくても、新しく始まったケースの失敗にします。前のケースの終了処理が呼ばれないこと自体が境界処理の前提崩れで、以後の帰属を信頼できないためです。その時点で消費されていない記録は「帰属不能」として失敗に添えて消費し、誤ったケースへ帰属しません。

#### 開始時に失敗したケース

そのケースは本体も終了処理も実行されません。MSTest 4.5.2では、`GlobalTestInitialize` が例外を投げたケースの `GlobalTestCleanup` は呼ばれず、次のケースは通常どおり実行されます（版を上げるときは同じ確認を繰り返します）。

そのため境界は、開始に失敗させたケースを記録側でも開始前の状態（ケースの間、直前のケースはそのケース）へ戻し、次のケースの開始で重なりと誤判定しません。

開始時に `TestContext` へ書いた行はTRXの標準出力に残り、失敗のメッセージにも記録の行を含めます。

#### 判定の集約と確認方法

判定は一つの定義に集約し、ケースの開始・終了・moduleの終了の三つの境界が同じ定義を使います。

ケース間の帰属・次のケースの開始時の報告・開始時の状態の戻し・観測や本体の失敗時の出力・moduleの終了時の失敗は、ケースの順序や本体の失敗に依存するため恒久テストにせず、制御フローで静的に確認し、局所MTPで一度だけ一時的に確かめます（その確認の改変は取り除きます）。

判定の定義（`D` を含む八項目）は `DispatcherHostTests`、復元は `DispatcherHostTests` と `WindowPresentationScopeTests`、未宣言のダイアログ窓口の記録と結果は `UndeclaredUiDialogServiceTests` で確認します。ホストの記録・受取り・待機APIの即時失敗は `DispatcherHostTests` で確認します。

### 共有ホストの観測記録

未処理例外とは別に、共有ホストの想定外の状態を観測し、記録します。項目 `B`・`E-a`〜`E-f`・`D` は、発生元の修正後はどのケースでも0件であるべきため、ケースの失敗にします（`D` 以外は基準へ戻します）。`E-g`・`E-a-hwnd`・`X-detector` は、正当な残りと区別できない、または検出器自身の欠陥であるため、記録だけです。

#### 失敗にする項目と記録だけの項目

失敗にするのは `B`、`E-a`、`E-b`、`E-c`、`E-d`、`E-e`、`E-f`、`D` の完全一致の八項目で、定義は一つです（前方一致にしません。`E-a-hwnd` は昇格しません）。

`E-g`（未消化のDispatcher操作）は、後続の通常の処理が正当に積む操作と区別できないため、`E-a-hwnd` は、WPFが遅延生成する内部のHWNDやIMEなど分類しきれない残りを含むため、`X-detector` は検出器の欠陥が全ケースへ波及しないよう、記録だけです。

受入の確認では `X-detector` が0件であることを条件にします。

#### 失敗の扱い

未処理例外と同じ扱いです。本体が成功していればケースの失敗にし、本体が既に失敗していれば本体の失敗を主として残し、境界の失敗は `TestContext` の出力へ書いて消費します。同じ境界の未処理例外とは一つの失敗にまとめ、元の例外（未処理例外と復元の例外）を内部例外として参照のまま持ちます。失敗のメッセージは、失敗にした各記録の行、帰属、復元できなかった残留、未処理例外の内容と、クラスの `[TestCleanup]` の失敗を置き換えうることを含みます。

#### 記録の出力

##### 出力先

記録は `[BMS-OBS] <項目> | <帰属> | <内容>` の一行で、全項目を従来どおり、ケースの終了時（`GlobalTestCleanup`）に `TestContext` へ書き、検証1回分のTRXのそのケースの出力から辿れます。ケースの間に記録された分は、次のケースの開始時に「ケース間（直前：ケース名）」の帰属で、そのケースの出力へ書きます。module終了時に残った分は、Consoleと、実行の結果フォルダー（`TestContext.TestRunResultsDirectory`）内の `host-observation-residue-<module名>.txt` へ出力します。結果フォルダーが取得できなければConsoleだけです。出力の失敗は判定を変えません。出力と判定は、境界ごとに同じ記録の一覧を使います。

検出器自身の失敗は項目 `X-detector` として同じ経路へ書きます。`TestContext` への書込み自体が失敗した記録は、失敗とともにConsoleへ出力し、失われません。いずれも表示・判定を妨げません。記録の行には、帰属したケース名（ケースの間は直前のケース名）を含めます。

##### 閉鎖の失敗の標準エラー出力

例外として、閉鎖の失敗（`close-failed`）の記録は、追加した時点で同じ行を、プロセスの標準エラーの生のストリーム（`Console.OpenStandardError()`、UTF-8の一行を一回の書込みで出して `Flush`、同時の書込みは排他）へ直接出します（MSTestは `Console.Error` をテストごとのメモリ上のバッファへ振り分けるため、`ShowDialog` から戻らず停止してhangdumpで強制終了されると、その行が失われるためです）。出力の失敗は判定を変えず、`X-detector` として扱います。境界・TRXへの出力は従来どおりで、行が二重に見えることは許容します。

##### 標準エラー出力の確認方法

標準エラーへ直接出る行が強制終了後も残ることは、恒久テストでは確認していません（強制終了する検査を持ちません）。強制終了後に残るという点は、同期の書込みと `Flush`（`HostObservationLog.WriteRawStandardError`）からの推論です。

確認できるのは、行が標準エラーへ出ることで、`WindowPresentationScopeTests` の `UnpreparedModalWindow_CancelledClose_IsRecordedAsFailure` を局所MTPで直接実行し、そのプロセスの標準エラーをシェルのリダイレクト（`2>"$env:TEMP\bms-close-failed-stderr.txt"` はPowerShellの例で、出力先はリポジトリの外の一時領域です）でファイルへ取り、`action=close-failed` を含む行があることを一度だけ一時的に確かめます。

MSTest.Sdk（現在 `global.json` の4.5.2）またはMTPの版を上げるときは、この確認を繰り返します。

#### 項目 `B`（未準備のWindowの表示。失敗にします）

##### 観測の対象

共有ホストのスレッドで表示（初回のレイアウト）されたWindowのうち、どの表示範囲でも `PrepareForOwnedPresentation` を通していないものを、型・Title・モーダル・所有者・HWND・帰属とともに記録します。`Window` の `SizeChanged` へのクラスハンドラーでWindowごとに一度観測し（表示後の最初のレイアウトで観測します）、準備済みのWindow（モーダルを含む）は記録しません。表示されないWindowは通常記録されません。

##### 未準備のWindowの閉鎖

表示したままの非モーダルのWindowは、同じ境界の `E-a` の観測で復元が閉じます。未準備のモーダルは、テスト本体が入れ子のループから戻らず境界へ到達できないため、検出器が閉じます。

`ShowDialog` の最初のレイアウトはスレッドがモーダルになる前に起きるため、検出器は表示の観測の直後に `Send` の操作を一つ予約し、その時点で公開APIの `ComponentDispatcher.IsThreadModal`（スレッド全体の状態）が真で、Windowが `Application.Windows` に残っていれば、記録の内容へ `action=close-scheduled` を追記して閉じます（`SizeChanged` の中では閉じず、Dispatcherを進めません）。閉じる入口は復元と同じ一つの関数（`HostResidueRestoration.CloseWindow`、入口の選択は `SelectCloseEntry`）です。閉じたことで記録は消さず、判定は `B` のままケースの失敗です。

##### 閉鎖の失敗の記録

閉鎖に成功した場合は追加の記録をせず（`ShowDialog` が戻ることで観測します）、閉鎖が取り消された場合と、閉じる入口の呼出しが `WM_CLOSE` を送る前に同期に例外を投げた場合だけ、同じ項目 `B` で `action=close-failed` と理由（`canceled`、または例外の型とメッセージ）・Windowの型・Titleを持つ記録を加えます。

同期に受け取った例外はDispatcherの未処理例外へ漏らしません。同期の例外は、[未処理例外の残る限界](ui-test-isolation.md#残る限界)（一度処理した例外の再送出）を除けば、`Close` 自身の前提の検査（閉鎖の途中の再度の `Close` など）に限られます（所有者の終了の入口も `Close` の前に投げる処理を持ちません）。この捕捉は、通常の到達経路のない防御です。

表示済みのWindowのClosingの処理（`OnClosing`・`Closing` のハンドラー）の例外は、`Close` が同期に送る `WM_CLOSE` の配送の中で起きてDispatcherの `UnhandledException` に達するため、検出器の呼出しへは返らず、そのケースのDispatcherの未処理例外として記録されます（報告と受取りは[ケース境界の検査](#ケース境界の検査)と[未処理例外の記録と受取り](ui-test-isolation.md#未処理例外の記録と受取り)の規則に従います）。WPFはこの場合もモーダルの枠を終えてWindowを閉じるため、`close-failed` は記録されず、`ShowDialog` は戻ります。

`close-failed` の行はプロセスの標準エラーへも直接出ます（[記録の出力](#記録の出力)）。

##### 非モーダルのWindowと対象外

非モーダルの未準備のWindowは、境界の `E-a` と復元が扱います。表示時の記録の `thread-modal` は最初のレイアウトの時点の値です。ホスト以外のDispatcher（専用STAの検査）は対象外です。

#### 項目 `D`（未宣言のダイアログ要求。失敗にします）

##### 記録の対象

画面テストの構成の既定のダイアログ窓口（`UndeclaredUiDialogService`）、および観測の境界が有効なmoduleで、窓口を宣言しないテストが `UndeclaredDialogs.Create()` から受け取る窓口が、記録用の窓口を明示的に注入していない通知・確認・窓口表示・ファイル／フォルダー／保存先の選択・進捗の要求を受けた時点で記録します。製品の通知経路には窓口の例外を捕捉してログだけにする所があるため、例外ではなく記録で失敗にします。

##### 記録の内容

内容は `method=<メソッド名> | request=<要求の型名> | title=<題名> | text=<本文の要約> | owner=<要求のOwnerの型名、なければnull>` の一行で、窓口表示だけ `result=<結果の型引数の型名>` を加えます。型名は `FullName` です。題名は通知・確認が `Caption`、ピッカーと進捗が `Title`、本文は通知・確認が `MessageBoxText`、進捗が `Label` で、持たない要求は `-`、値がnullでも `-` です。

題名・本文はCRLF・CR・LFをそれぞれ空白一つにし（連続は畳みません）、先頭200文字で切ります（切ったときだけ末尾に `…` を付けます）。200文字目が高位サロゲートなら、ペアを分断せず199文字で切ります。

##### 帰属と結果

呼出しが戻る前（返したTaskを待つ前）に一件記録し、`B` と同じく呼出し時点のケース（ケースの間なら直前のケース名付き）へ帰属させます。ケースの間の `D` は次のケースの開始時に、直前のケースの残留として失敗にします（ただし開始時の観測と復元は `B` がある場合だけ動き、`D` では動きません）。

窓口は状態を変えないため、復元の対象はありません。結果は表示失敗（`Failed`、原因の例外つき）で、`OwnerUnavailable` は返さず、`ShowWindowAsync` のWindow生成と `RunWithProgressAsync` の処理は呼びません（実画面を作りません）。

##### 境界が無効なmoduleと通知の表明

観測の境界を有効にしていないmodule（Core）では記録が消費されず黙って通るため、窓口の生成時に `InvalidOperationException` を投げます。通知を期待するテストは、記録用の窓口を専用の引数へ明示的に渡して表明します。

#### 項目 `E-a`〜`E-g`（ケース終了時の残留）

各ケースの終了時に、Send優先度の `Invoke` を一回だけ使って、共有ホストの起動時点を基準に比べます。失敗にする残留を見つけたときだけ、同じ `Invoke` の中で復元します。ホストが未起動・終端済みなら何も起動せず何も読みません。

各残留は最初に観測したケースへ一度だけ帰属させ、以後のケースで繰り返しません。値が基準へ戻れば報告済みの印を消し、後で再びずれたら再び報告します。

##### `E-a`・`E-b`（残留のWindowと `MainWindow`）

ホストのスレッドの `Application.Windows` に基準の後から残るWindow。基準はホストの起動時点です（表示範囲の開始時点ではありません）。`MainWindow` が残る場合は同じWindowを一つの記録にし、`Application.Windows` に含まれない `MainWindow` だけが `E-b` です。

##### `E-a-hwnd`（記録だけ。残留のHWND）

`E-a-hwnd`（記録だけ）は、ホストのスレッドのトップレベルHWNDのうち `Application.Windows` のどれにも（WPFが作るnative ownerを含め）対応しないものです。WPFが遅延生成する内部のHWND（`HwndWrapper[...]` など）も含まれるため、native window classと分類（`kind=`）を記録に添えます。分類は、`wpf-internal`（クラス名が序数比較で `HwndWrapper[` で始まり、`HwndSource` が無く、非表示）、`ime`（クラス名が `IME` または `MSCTFIME UI` に完全一致し、非表示）、それ以外の `other` です。

入力は、ホストのスレッドで、観測の時点のHWNDから収集します。限界：Windowの隠し所有者の漏れは `wpf-internal` と同じ特徴を持つため区別できません。

##### `E-c`（`Application.Resources["vm"]`）

`Application.Resources["vm"]`。参照で判定し、別のオブジェクトへ替えるたびにそのオブジェクトごとに一度報告します。観測は、キーが無いことと値がnullであることを区別しません。

##### `E-d`（文化圏）

`Properties.Resources.Culture`（基準は `ja-JP`）。

##### `E-e`（保存値のテーマ）

保存値 `Settings.Default.AppearanceTheme`（基準はホストが設定するLight）。`AppThemeService` は適用中のテーマを公開で返さないため、判定するのは保存値だけで、読取りはホストの起動後に限ります。

##### `E-f`（NLogの構成）

`LogManager.Configuration` の参照の同一性。基準は最初に観測した非nullの構成で、nullから非nullへの変化（`NLogWrapper` の遅延初期化）は基準として採用します。

##### `E-g`（記録だけ。未消化のDispatcher操作）

Background以上で未消化のDispatcher操作。公開の `Dispatcher.Hooks`（`OperationPosted`・`OperationCompleted`・`OperationAborted`）で、ホストの起動時点から投稿された操作を追跡し、境界で `Pending` かつ優先度がBackground以上のものを、操作ごとに一度報告します。優先度の変更は購読せず、数える時点の優先度で判定します。ContextIdle以下は数えません。

#### 復元

失敗にした残留（その観測で新しく報告した `B`・`E-a`〜`E-f` に対応するもの。以前に報告済みの残留へは手を付けません）を、ホストのスレッドで同期的に基準へ戻します。`Dispatcher` を進めません（`PushFrame`・`ProcessQueued`・`AwaitPresentation` を使いません）。本体が失敗していても復元します。

##### 順序

Window（所有の深い順、同じ深さでは後に作ったものから。表示範囲の後片付けと同じ順序）、`MainWindow`、値（`Resources["vm"]`、文化圏、テーマ、NLog構成）の順です。

##### 閉じる入口

`SettingsWindow`・`PlaylistPropertyDialog`・`PlaylistSummaryBulkEditDialog` は所有者の終了の入口（`CloseForOwnerShutdown`）、それ以外は `Close` です（通常の `Close` はVMを持つこれらの型で閉鎖が取り消され、処理が開始されます）。表示範囲の後片付けの入口は変えません。閉じる前にWindowが `Application.Windows` に含まれなければ閉じた扱いにし、閉じた後は `Application.Windows` から外れたことを確かめます。

HWNDの破棄は成功条件に含めません（残れば `E-a-hwnd` の記録に表れます。表示済みの未準備のWindowの `Close` が同じスレッドで同期にHWNDを破棄することは、`WindowPresentationScopeTests.Restore_ClosesShownUnpreparedWindow` が確認します）。`MainWindow`・`ProgressDialog` には特別な入口がなく、閉鎖が取り消されれば復元の失敗です。

##### `MainWindow` と値の基準への復帰

復元の対象にしたWindowが `MainWindow` ならば、閉鎖の成否によらず基準（共有ホストでは `null`）へ戻します。復元に成功した項目（閉じたWindowと値）は報告済みの印を消し、同じ値へ再びずれたら再び報告して復元します。`Resources["vm"]` は基準にキーが無ければ `null` を代入せずキーを削除します。

##### 文化圏の復元

文化圏は `ResourceService.ChangeCulture` の経路で基準（`ja-JP`）へ戻し、`Resources.Culture` と表示の通知（`DisplayCulture`）を戻します。適用されなければ復元の失敗にし、通知の無い直接代入へは後退しません。`ChangeCulture` は `NLogWrapper` を通してログを書き、未初期化なら静的コンストラクターが構成を差し替えるため、NLogの復元を最後にします。

##### テーマの復元

テーマは保存値を基準（Light）にし、ホストの起動時と同じ経路で適用します（保存はしません）。成否は保存値で判定します。

##### NLog構成の復元

NLog構成は、復元の時点の基準のオブジェクトを参照のまま `LogManager.Configuration` へ再代入します。基準は最初に観測した非nullの構成のため、テストが置いた構成が基準になりうる限界があります。また、同じ観測で `E-f` が報告されていなくても、文化圏の復元が `NLogWrapper` の初期化で構成を差し替えた場合は、次の境界で `E-f` として表れえます。

##### 復元の失敗の扱い

項目ごとに捕捉して残りの復元を続け、再試行しません。復元の例外は復元の失敗として元の例外を参照のまま保持します。`Dispatcher` の `UnhandledException` に達した例外は、同じ境界の未処理例外として報告します。復元後に起きる例外は、ケース間の例外として次のケースの開始時に報告します。

##### 復元できなかった残留

復元できなかった残留は、以後のケースへ残りうるため、報告済みの印が残り、同じ残留は再報告しません。基準へ戻れば印が消えます。最初の報告が汚染の起点で、無関係な後続のケースが毎回失敗しないようにするためです。

#### 帰属の規則の例外

`E-a`〜`E-g` は発生の時刻を観測しないため、観測したケース（`GlobalTestCleanup` のケース）へ帰属させ、記録に「このケース、またはその直前のケース間で生じた」旨を書きます。`B` と `D` は発生の時点のケース（ケースの間なら直前のケース名付き）へ帰属させます。

#### 境界の順序

観測と復元、記録の出力、未処理例外の収集（`EndCase`）、判定の順です。観測処理自体（読取り・比較）が例外を投げた場合は `X-detector` として出力し、判定を変えません。

#### 境界の有効化

境界が各ケースで明示的に呼ぶ観測で、`UiModuleLifetime` が `AssemblyInitialize` で有効にしたUI moduleだけで動きます。有効にしないmodule（Core）では、共有ホストが起動しても未準備のWindowの検出器・Dispatcher Hooks・基準の読取りを取り付けません。有効化を共有ホストの起動後に呼んだ場合、その起動には取り付けません。

#### 対象外・限界

##### 閉鎖できない場合の停止

モーダルの閉鎖が取り消された場合と、閉じる入口が `WM_CLOSE` を送る前に同期に例外を投げて閉じられないままの場合は、`ShowDialog` から戻らず境界へ到達できないため、停止が残ります（`close-failed` の記録はプロセスの標準エラーへ直接出るため、強制終了されても実行ログに残ります）。

閉鎖の途中の再度の `Close` による同期の例外では、先に始まった閉鎖が後で完了すれば `ShowDialog` は戻ります。表示済みのWindowのClosingの処理の例外では、WPFがWindowを閉じて `ShowDialog` が戻るため停止は残らず、その例外はケースの未処理例外として報告されます。

閉鎖の判定はWindowごとに最初のレイアウトの後の一度だけで、最初のレイアウトがモーダルの外で起きた場合（例：`Show`→`Hide`→`ShowDialog`）は閉じず、停止が残ります。

##### 閉鎖の対象の範囲

スレッドのモーダル状態はスレッド全体の状態のため、準備済みのモーダルが開いている間に表示された未準備の非モーダルWindowも、閉鎖の対象になります（未準備である時点で `B` の失敗の対象です）。

##### 観測しない対象

Win32のコモンダイアログとネイティブのMessageBox（`EmergencyDialog`）はWPFのWindowではないため `B` の対象外で、ケース終了時にホストのスレッドへ残っていた場合だけ `E-a-hwnd` に現れえます。`UiDialogOwnerResolver` のactive modal・`MainWindowViewModel` のセマフォは、公開の観測点がなくリフレクションを使わないため観測しません。

##### 構成の読取りの限界

`LogManager.Configuration` の読取りは、NLogが構成の自動読込みを未実施だった場合にその試行を起こしうる限界があります。

#### 確認方法

観測の比較・失敗の判定・復元は、基準と報告済みの印などの状態を引数に取る関数で、恒久テストは独立の状態（テスト開始時点の現在値を基準）で確認し、境界の記録を作りません。

##### 共通基盤自体の検査での例外

観測の恒久テストと、表示範囲の後片付けの消化の失敗の保持を確かめる恒久テスト（`WindowPresentationScopeTests` の `Cleanup_ContinuesAndKeepsOriginalFailuresWhenProcessQueuedPresentationThrows`）が使う非表示（`WS_VISIBLE` なし）かつ非アクティブ（`WS_EX_NOACTIVATE`）の `HwndSource`（位置の指定は補助で、画面外の保証には使いません。後者は残留HWNDと、その破棄が2回目の消化へ予約を積む契機に使います）、未表示の `new Window`（`D` を作る恒久テストが要求のOwnerに使う未表示のWindowを含みます）、復元の恒久テストが一時的に変える値（`Resources.Culture`、`Resources["vm"]`、保存値 `AppearanceTheme` とそのテーマの適用、`LogManager.Configuration`、`Application.MainWindow`、復元が閉じるWindow）に加え、`B` を作る恒久テストが準備なしで表示するWindow（未準備のモーダルを含む）と、閉鎖を取り消すテスト用Window、閉鎖の処理で例外を投げるテスト用Windowは、この共通基盤自体の検査として明示された例外で、`finally` で元へ戻します。

準備なしで表示するWindowには、`TestWindowPresentationScope.PlaceUnpreparedNonActivating` で画面外・タスクバー非表示・`ShowActivated=false`・`WS_EX_NOACTIVATE` を適用しますが、準備扱いにしないため（`B` が記録されます）、前面・矩形の確認と破棄の管理は持たず、モーダルは見張り（`StallTimeout`）で必ず戻します。

##### 表明の範囲

表明は対象の項目だけに絞り、他の由来で混ざりうる項目（`E-g` など）に依存させません。Windowの表示の記録（`B`）と未宣言のダイアログ要求の記録（`D`）を作る恒久テストは、自分の記録だけを消費し、同じケースの別の由来の `B`・`D` は境界に残して失敗にします。

##### 境界が無効なmoduleでの窓口の生成

境界が無効なmoduleで `UndeclaredUiDialogService` を生成すると失敗することは、Coreの `UndeclaredUiDialogServiceBoundaryTests` で確認します。

##### 境界の出力の確認

ケース終了時の記録・TRXへの出力・module終了時の出力は、制御フローで静的に確認し、TRXへの出力は局所MTPで一度確かめます。

## 実装とテストの対応

共有Dispatcherの未処理例外の記録・帰属・ケース境界の報告の対応は、[画面テストの分離](ui-test-isolation.md#実装とテストの対応)の対応表の行にまとめています。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 共有ホストの観測の記録・失敗化・復元（未準備のWindowの表示、ケース終了時の残留 `E-a`〜`E-g`、失敗にする `B`・`E-a`〜`E-f`・`D`） | [`HostObservationLog`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostObservationLog.cs)、[`HostResidueComparison`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostResidueComparison.cs)、[`HostResidueRestoration`](../../../tests/BeMusicSeeker.TestSupport/Wpf/HostResidueRestoration.cs)（判定 `HostObservationVerdict` と復元）、[`UndeclaredUiDialogService`](../../../tests/BeMusicSeeker.TestSupport/Wpf/UndeclaredUiDialogService.cs)（`D`）、[`UndeclaredDialogs`](../../../tests/BeMusicSeeker.TestSupport/Wpf/UndeclaredDialogs.cs)（境界の有無による窓口の選び分け）、[`TestWindowPresentationScope`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestWindowPresentationScope.cs)、[`TestUiDispatcherHost`](../../../tests/BeMusicSeeker.TestSupport/Wpf/TestUiDispatcherHost.cs)、[`UiModuleLifetime`](../../../tests/ModuleInitialization/UiModuleLifetime.cs) | [`WindowPresentationScopeTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/WindowPresentationScopeTests.cs) が準備済みのWindowを記録しないこと、未準備の表示が閉じた後も記録に残ること、記録が表示・Close・Dispatcherの進行を変えないこと、表示済みの未準備のWindowが復元で閉じること、未準備のモーダルが検出器の閉鎖で戻り記録（`close-scheduled`）が残ること、準備済みのモーダルとモーダルでない未準備のWindowを検出器が閉じないこと、閉じる入口の選択（三つの型は `CloseForOwnerShutdown`、それ以外は `Close`）、閉鎖の取消が `close-failed`（`canceled`）として記録されること（自分のタイトルの記録だけを消費する）、Closingの処理の例外が `close-failed` にならず、ケースの未処理例外として一件だけ元の参照のまま表れ、WPFがWindowを閉じて `ShowDialog` が戻ることを確認する。[`UndeclaredUiDialogServiceTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/UndeclaredUiDialogServiceTests.cs) が、七つのメンバーの `D` の記録（呼出し時点・行の形・要求のOwner・要約の規則）、表示失敗の結果、Window生成と処理を呼ばないこと、`MainWindowViewModelTestFactory.Create` の既定の接続を確認する。`UndeclaredDialogs` の選び分けは `HostResidueBoundary.IsEnabled` による一行の分岐で、制御フローで静的に確認する。Coreで誤って `UndeclaredUiDialogService` を選ぶと、生成時の例外で全利用ケースが失敗する。[`DispatcherHostTests`](../../../tests/BeMusicSeeker.Windows.Tests/Infrastructure/DispatcherHostTests.cs) が、独立の状態で残留のWindow・MainWindow・HWNDのみ・`Resources["vm"]`・文化圏・NLog構成（観測値）・未消化操作を一度だけ報告し、報告済みの印が基準へ戻ると消えて再びずれると再び報告されること（`Resources["vm"]`・文化圏・基準のWindowを再びMainWindowにする場合）を確認する。操作・HWNDの印の整理は同じ参照・ハンドルが戻らない限り挙動に現れないため、挙動としては確認しない。同じテストクラスが、失敗にする八項目だけが失敗になり記録だけの項目は失敗にならないこと、未処理例外・復元の失敗との統合で元の例外を参照のまま持つこと（判定）、Window・MainWindow・`Resources["vm"]`（キーの削除と同じ参照への復帰）・文化圏と表示の通知・テーマの保存値と適用・NLog構成の復元、復元が `Dispatcher` を進めないこと、閉鎖の取消・例外があっても残りを復元し元の例外を保持して再報告しないこと（取り消すWindowがMainWindowでも基準へ戻ること）、復元に成功した値の報告済みの印が消え、復元の直後の同じ値へのずれが再び報告されること、`SettingsWindow` が所有者の終了の入口で閉じること、HWNDのみの残留の分類を確認する。`ThemedMessageBox` など実ダイアログの閉鎖、同期の例外の `close-failed` の記録、ケース境界の出力・ケース間・開始時・module終了時の出力と失敗、ホスト未起動で何も起動しないことは、制御フローで静的に確認し、境界の出力が検証1回分のTRXに載ることは局所MTPで確かめる。 |

## 関連資料

[画面テストの分離](ui-test-isolation.md)、[テスト設計](test-authoring.md#画面に関わる保証の分担)、[テストの実行基盤](test-runtime.md)、[テストの実行と受入検証](testing.md)、[テスト配下の指針](../../../tests/AGENTS.md)。
