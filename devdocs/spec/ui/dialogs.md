# ダイアログの表示境界

## 目的と適用範囲

通知、確認、ファイル選択、進捗、子ウィンドウ、重ね合わせ表示を、共通の表示窓口へ接続する境界を定めます。各要求について、親ウィンドウ、UIスレッド、完了と失敗の表し方を明示し、機能側が直接表示や隠れた代替経路を持たないようにします。

選択画面の対象・既定名・拡張子は[ファイルとフォルダの選択](file-dialogs.md)、表示部品の外観は[外観](appearance.md)に従います。

## 用語

共通語は[用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| 親ウィンドウ | WPFの `Owner`。モーダル表示の所有元。 |
| 表示窓口 | `IUiDialogService` と、その実装 `UiDialogCoordinator`。 |
| 緊急表示 | 通常の表示基盤を使えない場合に `App` だけが使う `EmergencyDialog`。 |
| 所有する入口 | 確認や通知を伴う操作を受け付け、その結果と失敗の案内に責任を持つ画面側の入口。 |

## 仕様

### 共通の表示経路

機能側は、通知・確認・選択・進捗・子ウィンドウの意図を型付きの要求として表示窓口へ渡します。文字列のメッセージキーから処理内容を選ぶ経路は設けません。

| 呼出元 | 経路 |
| --- | --- |
| `MainWindowViewModel` の通常の通知・確認 | 表示窓口。画面固有の非通知操作は、型付きのイベントで `MainWindow` へ渡す。 |
| `MainWindow` の進捗 | `RunWithProgressAsync` |
| `MainWindow` のファイル・フォルダ選択 | 表示窓口の選択要求 |
| モーダルの子ウィンドウ | `ShowWindowAsync` |
| 重ね合わせ表示 | `ShowOverlayDialog` / `HideOverlayDialog`。初期言語の画面から設定画面への遷移も、この経路で明示的に扱う。 |

ライブラリ操作の表示は、モデルが持つ窓口の型 `IBmsLibraryDialogService` と、構成が注入する実装 [BmsLibraryDialogService](../../../BeMusicSeeker/Views/Dialogs/BmsLibraryDialogService.cs)、[変更結果の表示](../library/mutations.md)の境界に従います。モデルは表示の実装を持たず、既定の実装も作りません。実装は注入された `IUiDialogService` へ表示を委譲し、構成がライブラリへ渡します。表示経路を統一するために、モデルの排他権を保持したまま画面の応答を待つことはしません。

この窓口は、同期の `Confirm` と、OK だけの通知を待たずに依頼する `Post` だけを持ちます。同期の `Confirm` を使うのは、受付の前に行う閏年の確認だけで、OK だけの通知は同期に表示しません。変更の確認は所有者が利用者へ確認して判断を渡し、判断が欠けた要求はモデルが確認を表示せず失敗させます。`Post` は、モデルが根の背景処理（走査の警告・起動後の保守の読込み失敗）と、受付の外の経路（自有の公開入口の警告・自有の入口のスコアの取得失敗）が使います。実装は構成の UI の予約先へ表示の開始を予約して戻り、呼出し元のスレッドで表示を始めず、UI スレッドから呼ばれても同期のモーダルにしません。予約した後は、見出しと重要度が同じ通知を一つへまとめ、組を最初に現れた順に、先行の表示の完了を待って表示します。表示の失敗・予約の拒否・予約中の例外は、呼出し元へ伝えず診断へ残し、別の表示手段へ切り替えません。終了の要求の後は、依頼の直前と表示の開始の時点の両方で判定して表示せず、診断へ残します。ライブラリ変更の操作（自動導入・保留導入・導入先の修正・導入先の編集・一括の削除・改名・上書き・選択譜面の変更・重複の整理・フォルダ自動リネーム・通常の譜面一覧のフォルダ名の変更）の通知は、所有者が作る操作ごとの収集先（`LibraryOperationNotices`）へ集め、所有者が受付（借用した外側の受付と対象別の受付を含む）を解放した後に、所有者に注入された窓口でまとめて表示します。収集先は重複を除き、どのスレッドの追加も同じ列へ入れます。所有者は、表示するかどうかの判定（終了の要求・世代の失効）と最初の依頼を、UI の文脈の外で、操作の Task の完了（フォルダ自動リネームでは終端の通知）より前に行い、表示の完了は待ちません。表示の失敗は操作の結果を変えず診断だけに残します。フォルダ自動リネームは、完了・失敗の通知を受付を保持したまま発行した後に受付を解放し、案内の依頼を済ませてから終端を通知します。通常の譜面一覧のフォルダ名の変更は、案内と終端の報告（`FileDbMutationReport.ShowAsync`）をどちらも受付の解放後に依頼します。スコアの取得失敗は、借用した起動・再初期化では結果へ載せて ViewModel が受付の解放後に表示します。

本番の表示窓口は、構成が接続する ViewModel 側の所有者について、`App` が一つだけ作り、`ApplicationComposition` の `dialogService` 引数で構成へ渡します。構成は、再生・音声変換・保守・選択譜面の操作など、構成が作る ViewModel 側のすべての所有者へこの窓口を渡し、所有者ごとに窓口を作りません。本番で `UiDialogCoordinator` を生成するのは `App` と `ApplicationScenarioDialogs`（と `UiDialogCoordinator` 自身のコンストラクターの連鎖）だけで、Views は窓口を生成しません。`MainWindow` は窓口を引数で受けず、構成から `MainWindowViewModel` の `DialogService`（共通の窓口）と `PlaylistWorkspaceDialogService`（プレイリスト作業領域の窓口。専用の指定がなければ共通の窓口と同じ）を取得します。現行の振分けは次のとおりです。共通の窓口へ届くのは、更新案内（表示と内部の通知）、更新失敗、`CustomTableView` の通知に加えて、昇格の警告、ドロップの警告、外部操作の失敗、列の初期化の確認です。プレイリストの窓口へ届くのは、プロパティ・一括編集・取込み・ランプビューア・beatoraja（確認・通知・要約）・プレイリストの操作通知とプロパティ画面の外部同期の確認に加えて、ライブラリ・導入・保守・右クリックメニューなどシェル全般の操作失敗の通知（`NotifyMainWindowOperationFailureAsync`）です。設定画面の初期化中の適用拒否とプレイ履歴の表示プリセット編集の検証エラーは、設定画面の窓口へ届きます（プリセット編集のダイアログは、設定画面が自分の窓口をコンストラクターで渡します）。すべての表示経路は構成の窓口かシナリオの窓口を通り、固定の窓口を持つ経路は残りません。したがって Views は、窓口を必須の引数で受け取り既定の生成を持ちません。ドロップ・外部操作の失敗・列の初期化の確認は、イベントハンドラーが窓口の完了を `await` し、表示の時機は窓口に従います。昇格の警告は、ハンドラーが同期のまま戻り、非同期の表示が完了または失敗を要求へ一度だけ返します。任意の通知（ドロップ、外部操作の失敗、プレイ履歴の検証エラー）の表示が失敗した場合は、診断へ残すだけで、操作の成否を変えず、`async void` のハンドラーから例外を出しません。設定画面の適用拒否の通知の失敗は、`UiDialogRoute.ThrowIfNotShown` で例外にして呼出し元へ伝えます。列の初期化は明示的な OK のときだけ実行し、表示の失敗を承認とも取消とも読み替えず、所有する入口が一度だけ失敗を案内します。`MainWindow` が別に使う窓口として、`SettingsDialogService`（設定画面とリリースノート）と `FileDbMutationDialogs`（保留中のパッケージの変更の終端）があります。XAML の要素として引数なしで生成される `CustomTableView` だけは、`MainWindow` が生成後に設定メソッドで共通の窓口を渡し、未設定のまま通知しようとした場合は通知の失敗として診断へ残します。個々の ViewModel 側の所有者のコンストラクターは、窓口の既定の生成を持ちません（窓口を省略できる所有者は、省略を通知なしとして扱います）。`ApplicationComposition` の `dialogService` は必須で（null は `ArgumentNullException`）、構成も窓口の既定を作りません。子構成（再生・導入先・保守・選択譜面の操作など）の担当は専用の引数を持たず、構成の内部で同じ `dialogService` を使います。例外として、フォルダ自動リネームは構成のファイル/DB変更の窓口（`fileDbMutationDialogService`。省略時は `dialogService`）を使います。設定・ファイル/DB変更・プレイリスト作業領域・再起動失敗・ライブラリの専用引数は、経路ごとの差し替えです。省略した経路は共通の窓口を使い、指定した経路は専用の窓口が優先されます。シナリオ実行では、`App` が [ApplicationScenarioDialogs](../../../BeMusicSeeker/Runtime/ApplicationScenarioDialogs.cs)（事前に宣言した入力だけを照合して応答する検証用の窓口）を共通の窓口として構成へ渡し、`MainWindow` は同じ窓口を構成から取得します。宣言した入力のない通知は従来どおり内部の表示窓口へ委譲されます。手動導入の失敗報告の入力を宣言している間は、宣言どおりの報告以外の通知を失敗にし、UIスレッド以外から届いた通知も照合せず失敗にします。

### プレイリスト側の操作通知

プレイリスト側（P）の操作が表示する通知（操作の通知・操作を実行しなかった理由の案内・出力先の不正・プロパティの入力不備・外部同期の失敗・取込み要約の更新失敗・取込みの集計）は、`MainWindow` のイベントでも同期の待ちでも表示せず、ViewModel 側の表示役（`PlaylistOperationNoticePresenter`）が、プレイリスト作業領域の窓口へ待たずに依頼します。契約は次のとおりです。

- 通知は、操作ごとの収集先（`PlaylistOperationNotificationOwner.OperationNotificationSession`）へ事実として集めます。内側の操作の収集先は、外側の収集先があれば通知を引き継ぎ、最も外側の収集先だけが依頼します。最も外側の収集先は受付（P。取得していれば L を含む）より外側で作り、受付を解放した後に依頼します。例外・取消で終わる操作も、集めた通知を解放後に依頼します。
- 表示役は、見出しと重要度が同じ通知を一つへまとめます。組は最初に現れた順で、先行の表示の完了を待って順に表示します。一件だけの組は本文のままで、二件以上の組は件数と最大5件の標本にします（ライブラリ側の `FileDbMutationReport.ShowOperationMessagesAsync` と同じ規則です）。Owner は指定せず、窓口の解決に任せます。表示は発生元の画面（モーダル）の終了後になり得て、そのときは表示時点の窓口の解決（表示中のモーダル、なければアクティブなウィンドウ、なければ `MainWindow`、いずれもなければ親なし）が親を決めます。発生元の画面を親にする保証はありません。
- 表示するかどうかの判定（終了の要求）と最初の組の依頼は、UI の文脈の外で、操作の Task の完了より前に行い、表示の完了は待ちません。UI スレッドは表示の完了を同期で待たず、窓口の同期のモーダルにもなりません。受付を持たない同期の入口（Busy による拒否など）は、呼出し元へ戻った後に待たずに依頼します。
- 終了の要求の後は依頼の直前に判定して表示せず、診断へ残します。表示の失敗（失敗の結果・失敗した Task・同期の例外）は診断へ残すだけで、操作の結果・受付の解放・後続の操作の受付を変えず、別の表示手段へ切り替えません。後続の組と、取込みの集計は表示を続けます。
- 本文と見出しは、依頼の時点の選択言語で整形します（収集した時点の言語に固定しません）。例外の本文は作成時の文字列のままです。
- 操作を実行しなかった理由（Busy・対象消失・外部同期表）の案内も、同じ収集先へ積み、解放後に一件として依頼します。拒否の結果（DB・ファイル・表を変えず、後で実行しない）は変えません。
- BMT 出力の失敗は、受付（P）の内側で確定するため、現在の操作の収集先があれば同じ操作の通知として積み、その操作が受付を解放した後に依頼します。収集先がない呼出し（サマリーの `IsBmtOutput` セルの操作が出す BMT 出力など。`PlaylistWorkspaceViewModel.SummaryActions.cs`）は、受付が解放されて空いた後に、UI の文脈の外から依頼します。順序の変更は操作の収集先の内側で実行するため、前者です。いずれも受付を保持したまま依頼せず、表示の完了は待ちません。
- 収集先を閉じるときに、受け取られないまま残った通知は、失われたことを診断へ残します。外側の収集先がすでに閉じられている場合は、内側の通知を外側へ移さず、内側に残します。
- ドロップと復元は、主な失敗があり、BMT 出力の失敗の通知を除いて警告もエラーもないときに、操作の失敗の通知（ドロップは `Msg_error_unexpected`、復元は `Msg_failed_playlist_restore`）を補い、集めた他の通知と同じ操作の通知として表示します。BMT 出力の失敗は操作自身の失敗とは別の事実なので、補う判定には数えず、BMT の警告と操作の失敗の通知の両方を表示します（通知に BMT 由来の印を持たせて区別します）。ドロップは取消のとき通知を捨てます。
- 終了と受付の完了待ちは、P の実際の終端を待ち、未完了の表示は待ちません。P の実際の終端までは、競合する P の操作を Busy で拒否します。

したがって `MainWindow` には、プレイリストの窓口の Task を `GetAwaiter().GetResult()` などで同期的に待つ経路がなく、窓口は未完了の Task を返してかまいません。

### 窓口の Task を待つ表示経路

次の経路は、同期の待ちを持たず、窓口の Task を `await` します。窓口が返す Task は未完了でもかまいません。

| 経路 | 契約 |
| --- | --- |
| beatoraja の表URLの取込み | ViewModel（`PlaylistWorkspaceViewModel`）が、構成のプレイリストの窓口へ直接依頼します（Owner は窓口の解決に任せ、null です）。確認は受付（P）を取得する前に待ち、承認だけが取込みを開始します。確認の表示の失敗は、承認にも取消にも読み替えず、開始せずにログへ残して Error の通知を一件だけ出します。受付を取得した後に決まる通知（最終のルート検証の失敗・URLなし・有効なURLなし）、取込み中に確定した操作通知、要約は、受付を解放した後に、見出し・重要度ごとにまとめた通知、要約の順で依頼します（[プレイリスト側の操作通知](#プレイリスト側の操作通知)と同じ契約です）。要約の本文は ViewModels 側の整形関数が組み立てます。返す Task は、終了の判定と最初の依頼までを含んで完了し、表示の完了は待ちません。結果は、取込みを開始した場合の要約です（確認の取消・開始前の拒否・受付後の早い終了では null）。通知と要約の表示は任意であり、失敗しても診断へ残すだけで、結果と残りの表示を変えず、終了の要求後は表示しません。 |
| 更新失敗の通知 | 所有者（`StartupUpdateWorkflowOwner`）の `FailurePresentationRequested` は `Func<Exception, Task>` で、`MainWindow` が共通の窓口へ依頼します。表示の要求は、最初の `await` より前に窓口へ渡します。永続した失敗の記録の既読化は、表示の Task が成功した後にだけ行い、表示が失敗（同期の例外・失敗した Task・取消）した場合はログへ残して既読にしません。終端の公開とアプリ終了の要求は、表示の完了を待ちません。 |
| プロパティ画面の外部同期の切替え | 画面の ViewModel（`PlaylistPropertyDialogViewModel`）が、注入されたプレイリストの窓口へ確認を依頼します（Owner は null）。確認中は値を確定せず、旧値の変更通知で表示を戻します。承認された場合だけ、編集の準備が有効であることを確かめてから値を設定して通知し、取消では値を変えません。確認中のさらなる切替えは表示を戻すだけで、新しい確認を出しません。保存と初期化は、確認の終端を待ってから、確定した値で行います（確認を待つ間は受付を取得しません）。確認の表示の失敗は、値を変えずにログへ残し、この入口から Error の通知を一件だけ試み（通知の失敗はログだけ）、確認中の状態を解除します。 |
| 設定画面の表示 | `MainWindow` は、設定画面の `ShowWindowAsync` の終端を `await` で待ち、UI スレッドを同期で止めません。設定画面の後片付け（参照の解除と重ね合わせの復元）は表示の終端の後に行い、その後に `MainWindow` の表示の Task が完了します。失敗（取消を含む）は呼出し元へ同期の例外として出さず、Dispatcher の未処理例外へ一度だけ出し、Dispatcher の終了の開始後に届けられない場合はログへ残します。 |

### 直接表示を許可する実装

次の表は直接表示を許可する範囲です。一般の機能コードは、`ShowDialog`、標準のメッセージボックス、選択画面、進捗画面を直接呼びません。構造検証は、本番コードで直接呼出しを含むファイルの集合を、この表と比較します。

| ファイル | 責務 |
| --- | --- |
| `BeMusicSeeker/Views/ThemedMessageBox.cs` | テーマに従うメッセージ画面の表示部品。 |
| `BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs` | 通常の表示基盤を使えない場合の標準メッセージボックス。 |
| `BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs` | 進捗、選択、子ウィンドウなどの実際の表示。 |

### 要求と失敗の扱い

表示要求ごとに、意図、親ウィンドウ、スレッド境界、結果と失敗の表し方を定めます。表示の失敗を成功や取消に読み替えず、別の表示手段へ暗黙に切り替えません。`UiDialogRoute` は窓口が返した表示結果を検査する補助で、`ThrowIfNotShown` だけを持ちます。表示できなかった結果（失敗、親なし、結果なし）を例外にし、表示の失敗を隠しません。表示そのものは、必ず構成かシナリオの窓口へ依頼します。

#### 確認の結果

| 条件 | 扱い |
| --- | --- |
| 確認の表示に失敗した | 承認にも利用者の取消にも置き換えず、その操作を開始しない。 |
| 列の初期化 | 明示的なOKだけで実行する。 |
| フォルダー全体の確認 | Yesは全体、Noは譜面のみ、Cancel・閉じる・Escは全操作の取消。 |

#### 失敗の案内

確認または本体の失敗は、所有する入口で一度だけ案内します。この案内は任意通知であり、案内自体が失敗しても、元の結果、確定済みの変更、編集値の意味を変えません。

所有する入口は、確認や表示の呼出しより前から、開始した実際のTaskの終端までを保護します。通知の監視で開始済みのTaskの終端を代用しません。この規則は次の入口にも適用します。

- 検索ルートの選択
- 子の編集画面
- ランプビューアの初回表示
- サマリーの一括編集で行う外部同期の確認。確認、実際の適用（`applyTask`）、必要な回収、再有効化までを画面の閉鎖抑止の範囲に含める。

一時試聴のコピーが残った場合の通知と局所診断は、[音声の一時導入試聴のコピーと回収](../runtime/audio.md#コピーと回収)に従います。

#### 緊急表示

`App` の緊急表示は `EmergencyDialog` に限ります。通常画面から緊急経路を流用しません。

### 独立した非モーダルウィンドウ

ランプビューアと[譜面プロパティ](chart-properties.md)は、画面側の専用の管理主体（`PlaylistLampViewerWindowManager`、`ChartPropertiesWindowManager`）が、`Show` の呼出し、窓の寿命、開始済みの処理の寿命を所有します。共通のモーダル表示APIに非モーダルの意味を混ぜません。

| 画面 | 管理主体が所有するもの |
| --- | --- |
| ランプビューア | 集計と、その取得処理の回収。 |
| 譜面プロパティ | 要求ごとの新しい窓と一回の読取り。初期配置のときだけ一時的な親ウィンドウを使い、表示後は親を外してタスクバーに個別表示する。 |

譜面プロパティの表示に失敗した場合は、作成済みのWindowを閉じて回収し、終了の受付前に限り、画面の枠組みへ一度だけ通知します。終了後の遅れた通知を抑止しても、元の原因のログと開始済みの処理の回収は省きません。取得・解析の失敗は、プロパティ画面の中で扱います。

### 確認ボタンの言語と応答

通常の確認画面（`ThemedMessageBox`）のOK・Yes・No・Cancelは、表示時点の言語リソースを実際のボタンの文言に使います。

- OK・Yes・Noは確認ボタン専用のリソース、Cancelは共通の取消のリソースを使います。
- 言語を変更すると、次に表示する確認画面から新しい文言になります。表示中の確認画面は言語変更を購読せず、翻訳のキャッシュも持ちません。
- 翻訳は、既定の応答、明示的な応答、Enter・Esc・閉じる操作の意味を変えません。

表プロパティと進捗ダイアログの取消ボタンは `Resources.Cancel` を参照し、新しく開いた画面で現在の言語を表示します。取消の動作、`AutomationId`、Esc、通常の閉鎖は言語に依存しません。

### 保留復元の失敗時の選択画面

保留の復元で未確定の失敗が起きた場合は、専用の `PendingPackageRestoreWindow` を `ShowWindowAsync` で表示し、「再試行」「保留一覧を初期化して続行」「終了」を明示的な値として区別します。

- 本文で、すべての保留登録を消去することと、元のファイルおよび導入済みの楽曲をこの操作では削除しないことを示します。
- 一覧の初期化は、`IsAccepted` と明示的なResetの両方を要求します。×とEscには承認の値を与えません。
- 復元と一覧初期化の元の原因は、画面と既存のログへ渡します。DB確定後の障害や表示不能を、この選択へ再分類しません。
- 選択の範囲と終了の引渡しは、[起動の保留復元](../runtime/startup.md#保留復元の失敗時選択)に従います。

### 操作中に確定した通知の言語

プレイリスト操作の途中で確定し、操作の終端で初めて表示する通知は、確定時点ではなく表示直前の言語で本文と見出しを整形します。対象は、リコメンドの実力値の更新、カスタムフォルダ出力の警告、JSON出力・バックアップ・復元・BMT出力・ドロップ・Level入力の結果などです。

通知は次の値を変更不能な事実として保持し、表示窓口が一件ごとに表示直前に整形します。

- 重要度
- 見出しと本文のリソース名
- 翻訳の対象でない書式引数（パス、表名、Invariant書式の数値）
- 例外の本文などの詳細

例外の本文などの診断は翻訳せず、作成時点の文字列のまま保持します。そのため、失敗から表示までの間に言語を変えると、外側の文は表示時の言語、例外の本文は作成時の言語になることがあります。表示中のモーダルは言語の変更に追従せず、通知のために元の処理を再実行しません。

### 設定画面と子ウィンドウ

設定画面は、親を必須とする `ShowWindowAsync` のモーダル表示です。設定画面内の選択・通知・子ウィンドウは、表示中の `SettingsWindow` を親とし、`MainWindow` を親に戻しません。

| 表示 | 契約 |
| --- | --- |
| 入力検証の同期通知 | 確認や非同期の通知と同じ、注入済みの表示窓口を使い、通知のたびに調停処理を作り直さない。表示に失敗した場合は、拒否した入力を採用せず、失敗を呼出元へ伝える。 |
| LR2の `song.db` と設定XMLの個別参照 | 一般ページで行う。標準の配置を使うよう促す確認を同じ注入済みの表示窓口から表示し、同意された場合だけファイル選択を開く。確認の取消と閉じる操作では選択へ進まず、編集値を変えない。 |
| バージョン情報からの `ReleaseNotesWindow` | 注入された `IUiDialogService.ShowWindowAsync` だけで開き、親は `SettingsWindow`。親なし・非モーダルの代替はない。閉じても、設定の編集値の適用・初期化や設定画面の閉鎖を行わない。 |

LR2の個別参照で確認や選択の表示に失敗した場合は、元の例外と内部の経路・状態をログへ残し、同じ表示窓口と設定ウィンドウから、翻訳済みの一般向けの通知を一度だけ行います。内部の診断文字列を利用者向けの文言に付加しません。この通知も失敗した場合はログだけに留め、再帰的な通知や代替経路は使いません。一般ページのクリック処理は非同期で待機し、イベントハンドラー以外へ `async void` を広げません。

初回設定と修復の流れは、[設定の初回と修復](../runtime/settings.md#初回と修復)と[起動の完了境界](../runtime/startup.md#起動の流れと完了境界)に従います。

## 実装とテストの対応

| 仕様項目 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 直接表示を行うファイルの限定 | [UiDialogCoordinator](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs)、上記の表示部品 | [DialogRouteConsolidationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogRouteConsolidationTests.cs) の `DirectDialogRouteFiles_MatchDocumentedBoundaries`: 直接呼出しを含むファイルの集合が表と一致すること。検出規則は実装側を参照する。 |
| ライブラリ操作の窓口の実装と注入、モデルの Views 非依存 | [BmsLibraryDialogService](../../../BeMusicSeeker/Views/Dialogs/BmsLibraryDialogService.cs)、`BMSLibrary` のコンストラクター、`ApplicationComposition.CreateBmsLibrary` | [BmsLibraryDialogServiceTests](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/BmsLibraryDialogServiceTests.cs): 注入した表示窓口への通知・確認の振分けと要求の内容、結果の変換、窓口なしのライブラリの拒否に加えて、投函が予約だけで戻ること、見出し・重要度ごとのまとめと表示の順、表示の失敗・予約の拒否・予約中の例外を投函元へ出さず後続を続けること、表示の開始の時点の抑止。[BmsLibraryDialogServiceLogTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/BmsLibraryDialogServiceLogTests.cs): 表示できなかった通知が、元の例外の型と本文または拒否の事実として診断に残ること。[ModelLayerArchitectureTests](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/ModelLayerArchitectureTests.cs): `Models` 配下のソースが `Views` 名前空間を参照しないこと。 |
| 構成の共通の窓口と専用引数の優先 | `App`、[ApplicationComposition](../../../BeMusicSeeker/ViewModels/MainWindow/ApplicationComposition.cs) の `dialogService` と専用引数、`MainWindowChildComposition` が構成の `dialogService` を使う経路 | [ApplicationCompositionDialogRoutingTests](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ApplicationCompositionDialogRoutingTests.cs): 共通の窓口だけを渡したライブラリの背景の警告（投函）が共通の窓口へ届くこと、終了の要求の後と、投函の後・表示の前に始まった終了の後は表示しないこと、専用引数が共通より優先されること、子構成の保守の再検査の確認が共通の窓口へ届き専用引数の窓口へ届かないこと。同クラスの `MainWindow_UpdateFailureNotification_UsesCommonDialogService`: 共通の窓口とプレイリストの窓口を別に構成した `MainWindow` で、更新失敗の通知が共通の窓口へちょうど1件（表示時の文化圏の見出し・本文、OK、Error、Owner なし）届き、プレイリストの窓口へ届かないこと。表示の要求は、終端の通知より前に積んだ UI の操作の中で、最初の `await` より前に窓口へ渡すことを待ち合わせの根拠にする。子構成の経路（導入先、重複保守、選択譜面の変更・資源の健全性、譜面情報の解析失敗の削除、音声変換、保守の再検査）と再生・起動失敗の通知が構成の共通の窓口を使い、`MainWindowViewModel` と `ApplicationComposition` に窓口の個別生成が残らないことは、静的確認へ分担する（恒久テストにしない）。 |
| 本番の窓口の生成元の限定 | `App`、[ApplicationScenarioDialogs](../../../BeMusicSeeker/Runtime/ApplicationScenarioDialogs.cs) | [DialogRouteConsolidationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogRouteConsolidationTests.cs) の `UiDialogCoordinator_IsConstructedOnlyByAllowedProductionOwners`: コンパイル後の呼出し関係で、`UiDialogCoordinator` のコンストラクターの呼出し元が `App`・`ApplicationScenarioDialogs`・`UiDialogCoordinator` 自身の3つに限られ、`App` からの呼出しがあること。固定生成があると注入した窓口（シナリオの宣言の照合、テストの未宣言要求 `D`）が黙って無効になるため、生成元を限る。 |
| 同期だった表示経路の宛先・Owner・失敗の扱い | `MainWindow`、[MainWindowColumnResetTerminal](../../../BeMusicSeeker/Views/MainWindow/MainWindowFeatureTerminals.cs)、[SettingsWindow](../../../BeMusicSeeker/Views/Settings/SettingsWindow.cs)、[PlayHistoryFolderDisplayPresetEditDialog](../../../BeMusicSeeker/Views/PlayHistory/PlayHistoryFolderDisplayPresetEditDialog.cs) | [MainWindowChartPresentationWpfTests](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowChartPresentationWpfTests.cs) の `MainColumnResetClickConfirmsThroughInjectedDialogAndResetsOnlyOnExplicitOk`: 列の初期化の確認が構成の共通の窓口へ1件届き、明示的な OK のときだけ初期化し、表示の失敗では初期化せずプレイリストの窓口へ失敗を一度だけ案内すること。[ApplicationCompositionDialogRoutingTests](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ApplicationCompositionDialogRoutingTests.cs) の `MainWindow_DropWarning_UsesCommonDialogServiceAndKeepsDropOutcome`: ドロップの警告の宛先・内容・Owner、通知の前に確定した `Effects` と `Handled`、表示失敗でも例外を出さないこと。[SettingsWindowPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs) の `SettingsWindow_ApplyBlockedNotification_UsesWindowDialogsAndPropagatesPresentationFailure`: 適用拒否の通知が設定の窓口へ届き、表示失敗が呼出し元の Task の失敗になり、確認と再同期が始まらないこと。同クラスの `PlayHistoryPresetDialog_ValidationError_UsesSettingsDialogsAndKeepsStateOnPresentationFailure`: 検証エラーが設定の窓口へ届き、表示失敗でも閉じず、プリセットと保存値が変わらないこと。外部操作の失敗（`IsLoaded` の条件がある）と昇格の警告（実際の昇格の判定がある）は恒久テストにせず、静的確認へ分担する。昇格の警告の受け手は `ElevatedProcessWarningWorkflowOwnerTests` が確認する。 |
| beatoraja の確認・受付の解放後の表示・順序・実終端・失敗・終了中・要約の形 | [PlaylistWorkspaceViewModel.BeatorajaTableUrlImport](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.BeatorajaTableUrlImport.cs)、[BeatorajaTableUrlImportOutcome](../../../BeMusicSeeker/ViewModels/Playlist/BeatorajaTableUrlImportOutcome.cs) | [BeatorajaTableUrlImportPresentationTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BeatorajaTableUrlImportPresentationTests.cs): 確認が受付の前に一件届き未完了の間は受付が空いていること、取消・表示の失敗（失敗結果・失敗した Task・同期の例外）では開始せず Error の通知を出すこと、受付の後に決まった通知と操作通知・要約が受付の解放後に届き、返す Task が表示の完了を待たずに最初の依頼で完了すること、通知から要約の順、表示の失敗がログだけで取込みの結果と残りの表示を変えないこと、終了中は表示しないこと。[BeatorajaTableUrlImportSummaryFormatTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BeatorajaTableUrlImportSummaryFormatTests.cs): 要約の本文（件数の段落、警告・失敗の節の順、標本5件と「- ...」、名前の代替）。[SettingDialogEditCompletionTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs) の `BeatorajaTableUrlButton_DeliversConfirmedRootToWorkspace`: 設定画面からの取込みで、確認・通知・要約が設定の窓口とは別のプレイリストの窓口へ届き、要約の表示を保持したまま、実終端が完了すること（表示の完了を待たない）。 |
| 更新失敗の既読化 | [StartupUpdateWorkflowOwner](../../../BeMusicSeeker/ViewModels/Startup/StartupUpdateWorkflowOwner.cs) | [StartupUpdateWorkflowOwnerTests](../../../tests/BeMusicSeeker.Core.Tests/Startup/StartupUpdateWorkflowOwnerTests.cs) の `FailurePresentation_*`: 既読化が表示の Task の成功後にだけ一度行われること、表示の失敗（同期の例外・失敗した Task・取消）では既読にせず警告の診断へ残すこと、未完了の表示が終端の公開とアプリ終了の要求を待たせないこと。 |
| プロパティ画面の外部同期の確認 | [PlaylistPropertyDialogViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs) | [PlaylistWorkspaceActionWorkflowTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) の `PlaylistPropertyDialog_PendingExternalSyncConfirmation_*`: 確認中は値を確定せず再切替えで確認を重ねないこと、保存と初期化が確認の終端を待ち受付を保持しないこと、承認なら確定値・取消なら変更前の値を保存すること。同クラスの `PlaylistPropertyDialog_ExternalSyncConfirmationDisplayFailure_*`: 確認の表示の失敗で setter が例外を出さず、値を変えず、Error の通知が一件で、次の切替えが新しい確認を出すこと。 |
| 受付の解放後に表示するOKだけの通知の見出し・重要度ごとのまとめ | [FileDbMutationReport](../../../BeMusicSeeker/ViewModels/ChartOperations/FileDbMutationReport.cs)、[OperationNoticeGrouping](../../../BeMusicSeeker/Models/BmsLibraryInternal/Dialogs/OperationNoticeGrouping.cs) | [FileDbMutationReportTests](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/FileDbMutationReportTests.cs) の `OperationMessages_*` と `MaintenanceReadFailures_*`: 同じ見出し・重要度を最初に現れた順に一件へまとめ、1・2・5・6・7件の件数と標本、標本の一行化、表示失敗後の後続の組の表示、読込み失敗の事実を表示時の言語で整形することを確認する。 |
| ライブラリ変更の操作ごとの通知の収集先（重複除去・別スレッドの追加・取得後の写し・受付の解放後の依頼） | [LibraryOperationNotices](../../../BeMusicSeeker/Models/BmsLibraryInternal/Dialogs/LibraryOperationNotices.cs)、[PendingPackageWorkflowOwner](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs) | [LibraryOperationNoticesTests](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/LibraryOperationNoticesTests.cs): 一致する通知を最初の一件へまとめ、別のスレッドの追加を残し、取得後の追加で取得済みの写しが変わらないこと。Core の `PendingPackageWorkflowOwnerTests` の `InstallPackagesAsync_*`: 外側の受付の解放後に一度だけ、UI スレッドの同期のモーダルを避けて依頼すること。譜面変更系の所有者（選択譜面の変更・重複の整理・フォルダ自動リネーム・フォルダ名の変更・導入先の編集）の同じ依頼は、[ライブラリ変更](../library/mutations.md)の対応表の試験が、論理Lの解放後・UIの外・操作の完了前・表示の完了を待たないこと・終了中と失効した世代で依頼しないことを確認する。 |
| 保守の再検査の読込み失敗の受付の解放後の表示 | [SelectedChartResourceHealthWorkflowOwner](../../../BeMusicSeeker/ViewModels/ChartOperations/SelectedChartResourceHealthWorkflowOwner.cs)、[MaintenanceRescanWorkflowOwner](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceRescanWorkflowOwner.cs) | [SelectedChartResourceHealthWorkflowOwnerTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/ChartOperations/SelectedChartResourceHealthWorkflowOwnerTests.cs) の `RescanAsync_*`（処理中の表示0件・表示の時点の受付の取得・表示中の別の実保守の完了・確定失敗でも表示・表示失敗・終了後と差替え後の非表示。取消の警告の表示の時点の受付の解放は `RescanAsync_WhenLibraryBlocksMutation_ShowsWarningWithoutRefresh`）、[MaintenanceRescanWorkflowOwnerTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/Maintenance/MaintenanceRescanWorkflowOwnerTests.cs) の `RealRescan_ShowsGroupedReadFailuresAfterAdmissionOnlyWhenCurrent`（完了・取消・失敗・世代の失効・終了）、[SelectedChartResourceHealthWorkflowOwnerTests（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/ChartOperations/SelectedChartResourceHealthWorkflowOwnerTests.cs) の `RescanAsync_LogsEveryReadFailureInOneEventEvenWhenNoticeIsSkippedForShutdown`（表示しない場合の全件のログ）。 |
| 設定画面の表示の非同期化 | [MainWindow](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [SettingsWindowPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs) の `MainWindow_SettingsPresentationAwaitsPendingWindowTaskAndCleansUpAfterIt`: 未完了の表示の Task を同期で待たずに戻り表示中は重ね合わせを保つこと、後片付けが表示の終端の後で次の要求が新しく表示を始めること。同クラスの `MainWindow_SettingsPresentationFailureRestoresPlaybackOverlayVisibility`: 失敗が OpenCommand から同期で出ず、元の例外を内部に持つ失敗として Dispatcher の未処理例外へ一度だけ出ること。 |
| 緊急表示と通常表示の分離 | [EmergencyDialog](../../../BeMusicSeeker/Views/Dialogs/EmergencyDialog.cs) | 同クラスの `StandardMessageBox_IsLimitedToEmergencyDialog`: コンパイル後の呼出し関係で、標準メッセージボックスの呼出しが `EmergencyDialog` だけにあり、その呼出元が `App` だけであること。 |
| 確認ボタンの翻訳と応答の意味 | [ThemedMessageBox](../../../BeMusicSeeker/Views/ThemedMessageBox.cs) | [DialogPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs): 6言語の実際のボタン文言、言語変更後の次の表示、4構成の明示応答・Enter・Esc。[ThemedMessageBoxTests](../../../tests/BeMusicSeeker.Core.Tests/Dialogs/ThemedMessageBoxTests.cs): 既定値と結果の変換。[UiDialogCoordinatorWpfTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/UiDialogCoordinatorWpfTests.cs) の `WholeFolderConfirmation_RealMessageProducesDistinctNoCancelAndClose`: フォルダー全体の確認でNo・Cancel・閉じる・Escを区別すること。[LocalizationResourceParityTests](../../../tests/BeMusicSeeker.Core.Tests/Localization/LocalizationResourceParityTests.cs): 辞書・getter・6言語の整合。 |
| 設定画面内の選択・子画面と親子関係 | [SettingsWindow](../../../BeMusicSeeker/Views/Settings/SettingsWindow.xaml)、[UiDialogCoordinator](../../../BeMusicSeeker/Views/Dialogs/UiDialogCoordinator.cs) | [SettingDialogEditCompletionTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs)、[SettingDialogCategoryPersistenceTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogCategoryPersistenceTests.cs)、[SettingsWindowPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsWindowPresentationTests.cs)、[SettingsControlPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingsControlPresentationTests.cs): 取消、確認、親ウィンドウ、公開Automationによる選択。実際のフォーカスは[利用者に推奨する実機確認](../development/testing.md#利用者に推奨する実機確認)へ分ける。 |
| 設定の入力検証通知、表示失敗と入力保持 | [SettingsDialogViewModel](../../../BeMusicSeeker/ViewModels/Settings/SettingsDialogViewModel.cs) の `ShowUiMessage` | [SettingDialogEditCompletionTests](../../../tests/BeMusicSeeker.Windows.Tests/Settings/SettingDialogEditCompletionTests.cs) の `CustomFolderOutputValidation_UsesInjectedDialogAndPreservesRejectedValue`: 注入済み窓口への一度の通知、表示成功・失敗・親なしの結果、拒否値と保存済みXMLの不変。 |
| 一括編集の確認の寿命、任意通知の失敗、更新リンクの失敗 | [`PlaylistSummaryBulkEditDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistSummaryBulkEditDialog.cs)、[`UpdateAvailableDialog`](../../../BeMusicSeeker/Views/Dialogs/UpdateAvailableDialog.xaml.cs) | [`MainWindowPlaylistWorkspaceWpfTests`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup`: 確認中の `applyTask` と閉鎖抑止、確認失敗時の未適用と編集値保持、出力確定後の通知失敗と後片付け。[`DialogPresentationTests`](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `UpdateLinkLaunchFailureKeepsWindowAndDrainsCompiledEvent`: 起動失敗の通知Taskが終わっても成功として閉じないこと。 |
| 進捗ダイアログと表プロパティの取消ボタンの翻訳 | `ProgressDialog.xaml`、`PlaylistPropertyDialog.xaml` | [DialogPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `CancelButtons_NewDialogsUseCurrentLanguageAndKeepCancellation`: 6言語で新しく開いた画面の文言が `Resources.Cancel` と一致し、進捗の取消が処理側へ届いて `CancelledByUser` で閉じること。 |
| プレイリスト側の操作通知の依頼（受付の解放後・まとめ・順序・待たない・UIの外・失敗・終了） | [PlaylistOperationNoticePresenter](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistOperationNoticePresenter.cs)、[PlaylistOperationNotificationOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistOperationNotificationOwner.cs) | [PlaylistOperationNoticePresenterTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistOperationNoticePresenterTests.cs): 同じ見出し・重要度を最初に現れた順に一件へまとめること、前の組の表示の完了後に次の組を依頼すること、表示の失敗（失敗結果・失敗した Task・同期の例外）で後続を続けて診断を一件だけ残し未観測の例外を出さないこと、依頼時の言語で整形すること、終了の要求の後は依頼せずスキップを診断へ残すこと、UI の文脈から呼ばれても窓口を UI の文脈の外から呼び表示の完了を待たないこと、収集先の閉鎖で受け取られない通知を診断へ残し、閉じた外側へ移さないこと。各経路の依頼の時点（受付の解放後・UIの外）と順序は、[プレイリストの保存と出力](../playlist/storage-and-export.md)の対応表の試験が確認する。[ShellShutdownWorkflowOwnerTests](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/ShellShutdownWorkflowOwnerTests.cs): 表示が未完了でも終了が受付の実終端だけを待つこと、終了の要求の後の操作は表示を依頼せずログへ残すこと。 収集先がない呼出しの BMT 出力の失敗が、受付の解放後に（依頼の時点で受付を取得できる状態で）UI の外から依頼されること: [PlaylistWorkspacePersistenceCommandTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs) の `BmtOutputFailure_WithoutOperationCollector_IsRequestedAfterThePlaylistIsIdle`。順序変更の BMT 出力失敗が、受付の保持中は依頼されず、解放後に一件だけ依頼されること（`BmtOrderOutputFailure_WaitsForSavedHeaderDelivery`）。BMT 出力の失敗を補う判定に数えず、復元の失敗の通知と BMT の警告の両方が出ること（`PlaylistRestore_BmtOutputFailureDoesNotSuppressTheRestoreFailureNotice`）、ドロップで Information・BMT の警告・補充の Error が組ごとに出ること（`PlaylistDropAdmission_AwaitsRequiredOutputsAndPreservesCommittedFactsAcrossFailures`）。表示役が未接続のときに破棄を診断へ残すこと（`PlaylistOperationNoticePresenterTests` の `Notify_WithoutPresenter_LogsTheDiscard`） |
| 操作中に確定した通知の依頼時の言語 | [PlaylistOperationNotificationOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistOperationNotificationOwner.cs)、[PlaylistOperationNoticePresenter](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistOperationNoticePresenter.cs) | [PlaylistOperationNotificationLanguageTests](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistOperationNotificationLanguageTests.cs): 各経路の通知を言語Aで操作を始め、通知の依頼の直前に言語Bへ切り替えて、窓口へ届いた本文・見出し・アイコンが言語Bの内容で言語Aの文字列でないことを確認する（出力保守の警告は、収集した通知を直接整形して同じ規則を確認する）。[MainWindowPlaylistWorkspaceWpfTests](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の `RecommendationReload_LanguageChangeBeforeFirstDisplayUsesDisplayLanguage`: 再読込みの途中で言語を変えると、終端で表示するリコメンドの通知が表示時の言語になり、読取りを再実行しないこと。同クラスの `PlaylistBackupFailure_ActualMainWindowShowsErrorWithHandIcon`: 重要度とアイコンの対応。[PlaylistRecommendedTableOwnerTests](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs) の `AppliedSkillNotification_UsesOrgNameNumbersAndSettingWithoutDate`: 数値のInvariant書式。 |

## 関連資料

[ファイル選択](file-dialogs.md)、[外観](appearance.md)、[設定](../runtime/settings.md)、[ライブラリ変更](../library/mutations.md)。
