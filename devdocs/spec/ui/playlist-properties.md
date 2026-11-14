# プレイリストのプロパティと一括編集

## 目的と適用範囲

プレイリストの設定画面、サマリーからの一括編集、BMT出力順の操作を定めます。正本の保存・失敗時の復元はプレイリスト保存仕様に従います。

## 用語

[共通用語集](../glossary.md)を参照します。

| 用語 | 意味 |
| --- | --- |
| P | プレイリストの正本と、必要な管理出力の変更の受付。[操作の競合ポリシー](../core/operation-concurrency-policy.md)の区分。 |
| 編集中の値 | プロパティ画面・一括編集画面が保持する、保存前の入力。正本を先に変更しない。 |

画面操作の自動化は、Windows UI Automationの `Selection`（選択領域）と `SelectionItem`（選択項目）の操作契約に対応します。

## 仕様

### 単一プレイリストの編集

#### 表名の入力条件

手動の新規作成とプロパティ保存では、LR2連携・スタンドアロンとも、空またはUnicode空白だけの表名を通常の入力検査で拒否します。外部同期表の手動編集も同じ条件です。非空白を含む表名はこの条件で拒否せず、前後の空白を新しく除去しません。拒否時はDB・正本・別項目・出力を保ち、同じ編集セッションで訂正して保存できます。外部同期とインポートのモデル読込みには、この制限を広げません。

手動の新規作成と、名前を変更するプロパティ保存（プロパティ画面、概要のセル編集）では、LR2連携・スタンドアロンとも、他の表の名前と `Ordinal` で一致する表名を入力検査で拒否します。比較には保存する値を使い、大文字小文字だけが異なる名前は別の名前として受理します。拒否時の保証と訂正は空の表名と同じです。

- 名前を変更しない保存は、保存済みの同名を理由に拒否しません。同名の表の一方を別の名前へ変更する保存は受理します。
- DBの読込み、SQL復元、外部同期の再取得、内蔵表の名前の追従には、この検査を広げません。保存済みの同名の表はそのまま読み込みます。取込みでの同名の扱いは[インポート](../playlist/import.md)と[Table URL取込み](../playlist/table-url-import.md)に従います。
- 同名の検査は、LR2連携の配置の検査より先に行います。名前由来の出力名が重なる場合も、同名が原因の拒否は同名として案内します。

入力不備の案内は、拒否の理由ごとに次の文言を使い、プロパティ画面と概要のセル編集で揃えます。

| 理由 | プロパティ画面 | 概要のセル編集 |
| --- | --- | --- |
| 同名 | 保存時に `Error_PlaylistAlreadyExists` を一回。名前欄の入力時は案内せず、LR2連携の出力先の案内も出さない。 | `Error_PlaylistAlreadyExists` |
| LR2連携の配置（名前の変更による） | 名前欄の入力時に `Error_OutputFolderNameEmptyOrDuplicateChangePlaylist`、保存時に `Error_PlaylistPropertiesInvalid`。 | `Error_OutputFolderNameEmptyOrDuplicateChangePlaylist` |
| LR2連携の配置（名前を変更しない） | 出力名欄の入力時に `Error_OutputFolderNameEmptyOrDuplicateCheckInput`、保存時に `Error_PlaylistPropertiesInvalid`。 | `Error_OutputFolderNameEmptyOrDuplicateCheckInput` |
| パスを含む出力名 | `Error_OutputFolderNameMustBeSingleName` | `Error_OutputFolderNameMustBeSingleName` |
| 上記以外（空の表名、接頭辞の衝突など） | `Error_PlaylistPropertiesInvalid` | `Msg_invalid_setting` |

保存サービスは、新規表または出力名の生値を変更した要求だけを入力検査し、禁止入力は未完了出力の継続より前に理由付きで拒否します。既存値を変更せずに引き継ぐ別列の編集は許可します。

LR2連携では、保存の事前検査を[共通配置条件](../playlist/lr2-custom-folders.md#出力先と事前検査)の一つの判定で行います。公開済みの配置と候補の配置を比べ、配置が変わる表または新規の表が、後の集合で他の表と同一・親子になる場合だけ、名前由来の出力名、明示した出力名、基点の選択、ルート型の変更、新規の編集中の値の登録を拒否します。拒否した編集はDB・正本へ適用しません。他の表同士の保存済みの衝突は問わないため、衝突を解消する保存は受理し、画面の入力の可否も保存と同じ判定です。配置が変わらない保存（配置以外の項目の保存）で、保存済みの衝突に残る表は、DBへ保存して出力を止め、止めた表を一件の警告で案内します。

#### 受付と準備

保存・一括編集・順序変更の受付と編集中の値の現行性は、[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。新規作成の取消は未登録の編集中の値を閉じるだけで、正本の仮登録・削除を行いません。

既存プロパティ・新規プロパティ・一括編集の準備は、Pを待たずに取得できた場合だけ開始し、準備の間だけ保持します。

利用者が明示した操作がPの競合（Pの使用中、再読込み・初期化の実行中、自分の管理主体の実行中）で受け付けられない場合は、[操作の競合ポリシー](../core/operation-concurrency-policy.md)の「明示要求の競合の案内」に従い、副作用なしで `Warn_LibraryOperationBusy` を警告として一回案内し、汎用の失敗や入力不備の案内と重ねません。対象は、プロパティ画面と一括編集を開く操作、それらの保存と一括の各適用、概要のセル編集の保存、BMT保存順の移動、概要のフラグのセル、表・概要の行・フォルダの削除です。画面の入力は保持して閉じず、セル編集は保存済みの表示へ戻します。拒否した要求は自動で再実行しません。外部同期の有効化と削除のように確認を先に出す操作は、確認の前に受付の可否を取得せずに読み取って検査し、使用中なら確認を出さずに案内します。確認の後の取得で競合した場合も一回案内します。同じ画面を開く処理が進行中のための二重起動の抑止（新規作成と既存表のプロパティは同じ画面として扱います）は案内しません。終了の要求中、受付の閉鎖、ライブラリの未接続は案内しません。

- 既存の選択は、そのP内で同IDの一意な現在の正本へ解決し、その正本から初期値を読みます。
- 要求集合は一度だけ捕捉します。空の選択や解決できない選択を含む場合は全体を拒否し、同じ現在の正本への重複はまとめます。
- 編集セッションと画面用ViewModelの準備中は、awaitを含めてPを保持します。成功・失効・準備時の例外で解放してから画面を表示します。

一括編集の開始・適用では、一要求ごとに正本集合を一度だけ捕捉し、その要求内のID索引と参照集合で全対象を解決・検証します。選択対象ごとに正本全件を再走査せず、初出の選択順を保って重複を排除します。同IDが複数登録されている場合は、同一参照の二重登録でも開始を拒否します。索引は画面の寿命や次の要求へ保存しません。

#### 画面の構成

所有者付きのモーダル画面を開き、「全般」「フォルダ」「カスタムフォルダ」を表示します。毎回「全般」から始め、前回のカテゴリは保存しません。共通の確定・取消ボタンはカテゴリの外側に置きます。取消ボタンは `Resources.Cancel` へBindingし、新しく開いた画面で現在の言語のリソースを表示します。取消・Esc・通常の閉鎖による編集中の値の破棄と復元は、言語によらず同じです。

| カテゴリ | 編集対象 |
| --- | --- |
| 全般 | 名前、シンボル、互換用接頭辞、エントリ種別、外部同期の有効状態と3種類のURL。 |
| フォルダ | ソートキー・昇降順、拡張したフォルダ順、自動整列と手動の上下移動・自然順。 |
| カスタムフォルダ | LR2出力先、13種類のフォルダの出力設定と説明。 |

カテゴリは、UI Automationの選択操作に対応します。選択を下線で示し、フォーカスと無効状態を区別します。左右キー・Home・Endで移動し、無効な項目は飛ばします。本文は独立したスクロール領域とし、カテゴリを変更すると先頭へ戻しますが、編集値は保持します。

上部の選択、更新日時、確定操作は、本文のスクロールから独立して固定します。本文のScrollViewerはペイン端まで配置し、本文Paddingと固定領域の左右を16 DIPに揃えます。上部ナビゲーションの内容スタイルには追加の余白を持たせず、本文の余白を一回だけ適用します。採用する最小寸法でも、末尾の入力・確定・取消へ到達でき、横スクロールバーをフッターへ延ばしません。画面右下の更新表示は `Update: yyyy/MM/dd` とします。

フォルダ一覧は利用可能な高さに伸ばし、収まらなければその一覧内をスクロールします。空文字だけを既存の「名前なし」ラベルで表示し、保存済みの非空の空白名は原値で表示します。上下移動・自然順・名前を変えない保存では、表示ラベルをキーに使わず原値を保持します。手動改名の入力は[保存仕様](../playlist/storage-and-export.md#手動編集と失敗)に従います。説明を含む他の内容も、画面を縮小したときに到達可能にします。出力先は幅いっぱいに表示し、出力項目は上寄せの等幅2列とします。説明文は折り返し、横スクロールを要求しません。

カスタムフォルダはLR2用です。エントリがフォルダ単位なら、レベル別の出力を無効にし、フォルダ内のソート候補からレベル順と追加日時順を除きます。外部同期の設定に応じてURLとフォルダの編集可否を切り替え、自動整列中は手動整列を無効にします。編集欄は双方向、管理主体から受け取る一覧は一方向に接続します。

#### URLと出力名の入力

Page・Header・DataのURL欄は、空欄をnullによる明示解除として扱います。Pageは空白だけの入力も解除として扱い、それ以外の指定は絶対URIを要求します。Header・Dataは相対URIも受理します。外部同期の必須URLと解決基点は、[保存時のURL条件](../playlist/storage-and-export.md#プロパティのurl)に従い、同期中の編集可否は維持します。

OKでは、画面を無効化する前にURL3欄と出力フォルダ名のBindingを反映します。変換失敗、解除でない相対Pageなど、入力エラーが一つでもあれば、保存サービスを呼ばず、既存の入力不備通知を表示して画面を再び操作可能にします。入力文字列と他項目の編集候補を保持し、DB・正本を変更せず、同じ画面で訂正して再保存できます。不正な入力を空や旧値へ読み替えて成功として保存しません。

Pageと出力フォルダ名の画面検証は、型変換後・setterの前に行い、Header・Dataへ別の文字列検証を追加しません。出力名が[単一名の入力条件](../playlist/lr2-custom-folders.md#出力先と事前検査)を満たさない場合は、専用の理由を通知し、不正な文字列と別項目の候補を保持します。取消と通常の閉鎖は、URLと出力名のどちらの入力エラーでも妨げず、通常の復元と閉鎖を行います。

#### 保存・取消・閉鎖

通常の閉じる操作は、一回の復元と完了通知を通し、閉じる要求を再発行しません。次の場合は画面を閉じず、親画面と同じ調整処理で所有者を解決して通知します。

- 保存時の入力不備
- 実際の復元・表示・閉鎖の失敗

保存操作のTaskは、保存・復元、通知の依頼（受付の解放後の、終了の判定と最初の組の依頼まで）、実際の閉鎖、再有効化まで待ちます。通知の表示の完了は待たず、通知の失敗は診断だけに残し、保存結果へ合成したり、次の操作までTaskへ保持したりしません。実際の閉鎖や再有効化の失敗は画面寿命の失敗として返し、再有効化に失敗しても、操作中の受付フラグは解除します。保存前の確認が失敗した場合は、承認にも利用者の取消にも置き換えず、対象の変更を止めます。

外部同期の有効・無効の切替えは、利用者の確認を経てから確定します。確認は、画面の管理主体が、注入されたプレイリストの窓口へ依頼します（Ownerは窓口の解決に任せます）。確認中は値を確定せず、旧値の変更通知で表示を戻し、承認された場合だけ、編集の準備が有効であることを確かめてから値を設定して通知します。取消では値を変えません。確認中のさらなる切替えは、表示を戻すだけで、新しい確認を重ねません。保存と初期化は、確認の終端を待ってから、確定した値で行い、確認を待つ間は受付を取得しません。確認の表示が失敗した場合は、値を変えずに診断へ残し、画面の入口からErrorの通知を一件だけ試み（通知の失敗は診断だけ）、確認中の状態を解除して、次の切替えでは新しい確認を出します。

取消・Esc・通常閉鎖は、最新の保存値の復元と表示が成功すれば閉じます。手動保存用の名前検査は取消へ適用せず、外部取込みで受理した空名も、変更せずに取り消せます。DB確定後の出力失敗からの取消は、最新の確定値へ戻し、再保存や自動の出力再試行を行いません。

保存サービスは、一つの保存結果として、確定した参照、最新の保存値、未完了の必要出力、今回の実処理の失敗を返します。画面はそれらを採用してから失敗を通知します。同時編集の判定は、最新の保存値、現行の参照、所属と解決キーの版（`EntryLookupRevision`）に基づき、同じ保存の確定を古い編集スナップショットとの差として拒否しません。URLの補完のように、利用者操作でなく所属・解決キーを変えない項目値の更新は、編集の競合とみなしません。補完した実行時の値は保存で失わず、DBへも保存しません。所属・解決キーの変更は引き続き競合として拒否します。プロパティ画面の保存と概要のセル編集は、同じ比較を使います。新規作成中の表は作成経路と実際の登録結果で判定し、旧参照が集合から外れたことを新規作成と読み替えません。

#### 外部同期の再取得

外部同期がONの状態へ保存するとき、次のいずれかがあれば、ヘッダー保存後に既存の経路で再取得します。

- 保存済みのOFFからONへの切替
- 保存前後の実効同期元 `PageUrl ?? HeaderUrl` の変更（Pageの追加・解除、PageなしのHeader変更を含む）
- 保存済みHeader/Dataの、今回新たに行う空欄化

次の場合は再取得を増やしません。

- 非空Dataだけの変更
- Pageが選ばれているときの、非空Headerだけの変更
- 同じ実効同期元の保存
- 同期がOFFの保存

取得に失敗しても、保存済みの属性と既存項目を維持し、必要な出力を終えてから同期の未完了を通知します。同じURLの再保存だけでは自動で再取得せず、手動同期など既存の再操作を使います。

#### 新規作成と出力の継続

新規作成では、名前を入力して保存すると、一件のIDへ登録し、必要な初回出力まで待ちます。登録前の編集中の値に旧出力先はなく、通常・追加・ROOT先への初回生成を既存表の移行と区別します。LR2出力やROOT検索ルートのディスク保存に失敗しても、画面と登録済みのIDを保ち、明示的な再保存で未完了の部分を継続します。必要な出力と表示の完了後に画面を閉じます。

外部の再取得でDB・正本が確定した場合も、その保存で必要になった旧出力先の整理とBMT出力を保持します。同じ属性を重ねてDB保存せず、後続の実出力処理が失敗しても次の明示保存へ渡します。参照に依存するフォルダ選択の変換と項目変更通知は、新しい参照を採用してから行います。

A→BのDB保存後に出力だけが失敗した場合、同じ画面の次の明示保存は、未完了のA→Bを先に完了します。さらにCへ編集していても、その成功後にだけCをDBへ保存します。継続中に失敗した場合は、DBはBのまま、画面のCを保持します。復元・取消の基準はBです。

未完了は編集セッション内で一つに限り、成功した出力と公開済みのスナップショットを解放します。継続に必要な保存値・参照の結果記録・出力ごとのフラグを保持し、過去の失敗例外は次の失敗へ加えません。索引や現行参照の必要な公開が失敗した場合は、その未完了を残します。画面の終了や再起動を跨ぐ台帳、自動の再試行、旧保存全体の再実行は行いません。

### サマリーからの一括編集

選択したプレイリストを捕捉して対象を固定し、各操作のボタンで反映します。画面全体のOKでまとめて確定する方式ではありません。URLとフォルダ内の順序を一律に上書きする機能は設けません。

BMT、外部同期、ルート型、出力種別、出力先、外部属性の初期化は、適用のPを取得・借用した後、差分抽出や属性変更・通信より先に、開始時と同じ保存先の同一の正本の参照が、全対象で生存していることを検証します。一部が失効していても全体を拒否し、変更不要だった失効対象も省きません。編集内容を同IDの別の正本へ移さず、DB・管理出力・生存している正本・画面入力を保持します。拒否後は画面を開き直し、新しい初期値から明示的に編集します。外部取得の失敗、空の属性、出力競合による対象別の除外は、全対象の現行性を確認した後に適用します。

| 操作 | 反映規則 |
| --- | --- |
| 出力フラグ | チェックは有効、未チェックは無効、中間状態は変更なし。フォルダ単位エントリへのレベル出力は禁止。 |
| 出力先変更 | ルート出力中でも保存された出力先名を更新する。有効な出力先が変わるかどうかとは区別する。 |
| 外部同期を有効にする | `EnableExternalSync` の実際の成否を用いる。URL不足などで有効化できない表はOFFのまま無通知で除外し、既にONの表は変更せず、新たに有効化した表だけを保存・出力する。全件が有効化不能でも通常の適用として終了し、入力は「変更なし」へ戻す。除外件数・表名の通知は追加しない。 |
| 外部情報で初期化 | 指定した名前・シンボル・互換用接頭辞・出力先を、外部同期の有効状態にかかわらず取得情報から反映する。 |

外部情報の取得は、上限付きの並列処理で先に行い、読めない対象は変更せず飛ばします。初期化では既存の接頭辞を保存する規則を使わず、選択された項目を外部の値から作ります。出力先がNULLなら名前から作り、Shift_JISで扱えない文字とファイル名の不正文字を除きます。接頭辞の衝突と、LR2出力先の空値・同一・包含は、対象ごとに除外します。名前を変更する対象は、新しい名前が、いずれかの表の現在の名前、または同じ操作で先に受理した対象の新しい名前と `Ordinal` で一致する場合に、対象ごとに除外します。同じ操作で名前が変わる別の対象の旧名も現在の名前に含めるため、対象同士の名前の入替えは受理しません。除外は他の除外と同じく診断ログだけに残します。除外した表の旧配置も残した最終集合を副作用の前に検査し、安全な表だけを更新します。

エントリの投影が変わる複数の対象は、一回のDBトランザクションで保存します。出力先が変わる場合は、移転元の不要な出力も処理し、同じ出力先の内容変更もまとめて出力します。取得・DB反映・出力・画面更新を一つの進捗範囲とし、最後にボタンを再び使用可能にします。BMTの内容に影響する操作は、有限の変更表をまとめて一回の出力Taskにし、その完了を直接待ちます。LR2専用設定だけの変更では、BMTを再生成しません。

各反映ボタンは、保存と必要な出力の実際のTaskを所有し、受付の拒否・保存・出力の失敗を画面へ通知します。実際のTaskの終端より前に、編集中の値をリセットしたり、反映完了としてボタンを再有効化したりしません。失敗時は編集入力を保持し、成功時だけ反映後の値へ更新します。保存前の失敗は、その保存対象の正本を変更前へ戻し、同じ編集中の値を再び反映して保存できます。保存済みの対象は、後段の出力や別の保存対象の失敗で戻しません。

### BMT出力順の操作

サマリーのドラッグによる並べ替えは、BMT出力順の昇順表示でのみ受け付けます。絞込みで隠れた項目の相対順は維持します。先頭・末尾への移動と、現在の表示順の採用は、選択対象と現在の並びを明示して適用し、保存とBMT出力の必要性を判定します。

表一覧のSYNC・ROOT・BMT OUTPUTは、現在の保存済み行のboolを `True` / `False` として、セルコピーと選択行のTSVへ渡します。コピー自体は値を反転も保存もせず、可視列の順、選択行の順、空欄とURL生値の既存規則を保ちます。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 本文余白と固定領域、最小寸法での末尾到達 | `PlaylistPropertyDialog.xaml`、共通本文ScrollViewerと上部ナビゲーション | `PlaylistSummaryBulkEditTests.PlaylistPropertyDialog_ResizeKeepsGeneralEditorsReachable` は末尾editor到達と上部選択・更新日時・操作の固定を確認する。既存の `PlaylistPropertyDialog_FolderOrderScrollsIndependentlyAndGrowsWithWindow` と `PlaylistPropertyDialog_TopNavigationKeepsSingleSelectionAndDraftsAcrossCategories` は独立一覧と選択・draft保持を分担する。 |
| 三つの編集準備の短期P、現正本の初期値、空・失効・準備例外 | `PlaylistWorkspaceViewModel.PropertyEditing`、`PlaylistSummaryBulkEdit` | [`PlaylistWorkspaceActionWorkflowTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)の`PlaylistEditPreparation_*`、`PlaylistPropertyPreparation_ResolvesCurrentAndHoldsAdmissionThroughHydration`。実hydrationの通知gateと新規準備の設定gateでP保持を観測し、準備Task終端の解放、未登録draftと取消を確認する。 |
| 一括対象の一要求一回の正本捕捉、重複・IDなし・曖昧ID | `PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit` | [`PlaylistWorkspaceActionWorkflowTests.PlaylistBulkPreparationAndApply_CaptureCurrentCollectionOncePerRequest`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)。小さい実対象集合のコピー・列挙回数で開始と差分なし適用を確認し、同ID別参照・同参照二重登録の拒否を分担する。順序保持は初出順のListと参照集合による実装を静的確認する。 |
| 六操作の全対象現行性、同ID置換・別storeの拒否と開き直し | `PlaylistWorkspaceViewModel.PlaylistSummaryBulkEdit`、`PlaylistSummaryBuild` | [`PlaylistSummaryBulkEditTests.BulkApply_RejectsEntireStaleSelectionBeforeChanges`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryBulkEditTests.cs)は六操作の全件退役・無変更対象を含む一部退役で実DB・BMT bytes・正本・入力保持を確認し、別storeはBMTで代表確認する。`BulkBmtApply_ExternalReplacementRejectsOldDraftAndReopenedDraftSaves`は実外部再同期による同ID別参照から旧draft拒否、旧選択の開き直しと保存までを確認する。実WPFでの通知と入力保持は既存modal cleanupケースが分担する。 |
| 単一画面の編集、取消、保存後の更新 | [`PlaylistPropertyDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs)、`PlaylistWorkspaceViewModel.PropertyEditing` | [`PlaylistWorkspaceActionWorkflowTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) / [`PlaylistWorkspaceActionWorkflowTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) は保存結果・prefixと選択・詳細更新を確認する。[`MainWindowPlaylistWorkspaceWpfTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) / [`MainWindowPlaylistWorkspaceWpfTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の所有モーダル・通常閉鎖系列は実ボタン・取消・復元・画面寿命を分担する。 |
| URLの明示解除・相対Header/Dataの保存と再表示、取消 | [`PlaylistPropertyDialogViewModel.Page_url`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs)、[`PlaylistPropertyDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.cs)、[URL欄のBinding](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.xaml) | [`MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyDialog_ClearPageAndRelativeSourcesPersistAndReopenWithoutSavingCancel`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) は実メニュー・所有画面・実OKから新readonly DBの格納値と新モデル、再表示を確認する。Page空欄だけの限定Binding反映で保存保証を代用せず、Header/DataはOKで反映する。入力エラーと別候補の取消も同じ経路で確認する。 |
| URL3欄の変換失敗・相対Pageの保存拒否と同じ画面での訂正 | `PlaylistPropertyDialog.UpdateAndValidateUrlInputs`、`PlaylistPageUrlValidationRule` | 同WPF系列の `PlaylistPropertyDialog_UrlErrorsKeepDraftAndSavedValuesUntilCorrectedInSameDialog` は3欄の変換失敗と相対Pageの4編集で、入力文字列・別候補・DB・正本、所有通知と実操作Task終端、訂正後の保存を確認する。既存の `PlaylistSummaryBulkEditTests.PlaylistPropertyDialog_PreservesInventoryBindingsAndAvailabilityGates` は全項目のBinding方向と編集可否を分担する。 |
| URLの受理条件、同期の必須URLと絶対基点 | [`PlaylistPropertySaveService`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs) | [`PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_UrlAcceptancePreservesClearRelativeSourcesAndSyncRequirements`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) は実編集sessionの条件表、相対Pageの保存拒否とDB不変、Pageなし・絶対Header＋相対Dataの同期設定保存を担う。画面からの変換失敗は上のWPF系列へ分担する。 |
| 実folder編集controlから保存・LR2出力順への接続 | `PlaylistPropertyDialog`、`PlaylistPropertyDialogViewModel`、`PlaylistPropertySaveService` | [`MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyFolderControls_SaveDraftOrderToDatabaseAndRealLr2Output`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) は実Up・Down・NaturalSortのdraftを独立順と比較する。共通Save後は新readonly DBの手動保存列、またはheaderと実entryから再構成した自然順、実LR2採番順・folder行を確認し、新propertyのAutoオンと上下移動無効化も観測する。未保存正本・未編集属性・別表の値と出力を保ち、所有モーダル・保存Task・native閉鎖を待つ。出力種別の条件表は既存custom-folder系列へ分担する。 |
| 保存後の未完了出力先行、Cの保持、最新保存基準と外部取得・接頭辞非再実行 | `PlaylistPropertySaveService`、`PlaylistPropertyDialogViewModel` | [`BmsPlaylistCustomFolderOutputTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) / [`BmsPlaylistCustomFolderOutputTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)の`PlaylistPropertyDialog_*`。実DB・FS・config・BMTとP/Task終端を下位から確認する。 |
| 外部再取得確定後の追加DB失敗とA→B継続、参照採用後の接頭辞選択・詳細更新 | `PlaylistPropertySaveService`、`PlaylistWorkspaceViewModel.PropertyEditing` | [`BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialog_ExternalReloadCommitRetainsOutputMigrationAfterLaterDatabaseFailure`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs)で実置換後の移行DB故障と、元JSONを外した明示再保存を確認する。[`PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_ExternalReplacementRemapsSelectedFolderAndRefreshesDetail`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)で実workspaceの通知時の新参照・選択・ツリー・実詳細Folder列・DBを確認する。 |
| 同じ所有画面の実保存ボタン、外部置換後BMT失敗の通知・維持・再保存・`Close` | [`PlaylistPropertyDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.cs)、[`PlaylistWorkspaceViewModel.PropertyEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PropertyEditing.cs) | [`MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyDialog_ExternalReplacementBmtFailureKeepsOwnedDialogAndResavesSameId`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)。実データ連結、通知の所有画面、通知二次失敗後も終端するTaskと画面維持、次の明示保存成功後の旧失敗非混入、同IDの保存行と実出力を確認する。 |
| 選択集合、三状態フラグ、外部初期化、出力の集約 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryBulkEditTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) / [`PlaylistSummaryBulkEditTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistSummaryBulkEditTests.cs) |
| 表示と更新日時、件数 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryCountAndPresentationTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryCountAndPresentationTests.cs) |
| カテゴリ操作と所有者付き画面 | [プロパティ画面](../../../BeMusicSeeker/Views/Playlist/PlaylistPropertyDialog.xaml) | [`MainWindowPlaylistWorkspaceWpfTests`（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) / [`MainWindowPlaylistWorkspaceWpfTests`（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) |
| 一括変更の実Task所有、Busy・DB失敗・BMT出力待ちと出力失敗 | [`PlaylistSummaryBulkEditDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistSummaryBulkEditDialog.cs) | [`MainWindowPlaylistWorkspaceWpfTests.MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs)のcompiled反映入口・入力保持・終端後の再有効化 |
| 一括変更の保存前復元と同じ入力の再保存 | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.cs) | [`PlaylistSummaryBulkEditTests.BulkHeaderSaveFailure_RestoresCurrentTableAndSameDraftCanSave`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistSummaryBulkEditTests.cs)の各入口・実DB失敗・同一正本保持と再保存 |
| 出力名の実Binding入力・訂正・取消 | `PlaylistPropertyDialog` | `MainWindowPlaylistWorkspaceWpfTests.PlaylistPropertyOutputName_InvalidBindingKeepsDraftAndAllowsCorrectionOrCancel` が未反映rawの実OK、専用通知、DB不変、訂正保存または取消を確認する。 |
| 実効同期元と再取得条件 | `PlaylistPropertySaveService` | `PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_EffectiveSourceChangeReloadsOnlyWhenRequired` がPage追加・解除、Header変更、同一実効元と同期OFFを実sessionとSQLiteで確認する。保存後取得失敗と必要BMT・Reset基準は `BmsPlaylistCustomFolderOutputTests.PlaylistPropertyDialog_ExternalSourceFailureAdoptsSavedValuesAndCompletesRequiredOutput` が担う。 |
| 表名の入力検査と訂正 | `PlaylistPropertySaveService` | [`PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_BlankNamesRejectWithoutMutationAndAllowCorrection`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) は、実sessionの検査・保存と既存のViewModel経路で、空名の拒否、無変更、訂正後の保存を確認する。 |
| 同名の表名の拒否と訂正、保存済みの同名の受理 | `PlaylistPropertySaveService`、[`PlaylistPropertyDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs) | 同クラスの `PlaylistPropertySave_DuplicateNamesRejectWithoutMutationAndAllowCorrection` は、新規・既存とLR2連携・スタンドアロンの組合せで、保存サービスと画面からの保存の拒否、DB・正本・出力の不変、同名の入力時に案内がなく保存時の案内が一件であること、別名で出力先だけが重なる入力との区別、大文字小文字だけが異なる名前への訂正を確認する。`PlaylistPropertySave_StoredDuplicateNamesAcceptUnchangedNameAndRenameAway` は、保存済みの同名の表で、名前を変えない保存と別名への変更の受理、元の名前へ戻す入力を案内しないことをスタンドアロンで確認する。保存時の案内から所有画面の表示までは、既存の通知の経路が分担する。読込み・復元・再取得・内蔵表へ検査を広げないことは、検査の位置が保存サービスと一括初期化だけであることを静的に確認する。 |
| 概要のセル編集の理由ごとの文言 | [`PlaylistWorkspaceViewModel.PropertyEditing`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.PropertyEditing.cs) | 同クラスの `PlaylistSummaryPropertyEdit_RejectionMessageFollowsReason` は、同名（LR2連携で出力先も重なる場合を含む）、名前の変更による配置、出力名の変更による配置、空の表名の各行で、文言が一件で理由に一致すること、再表示の要求、DBの不変、同名の拒否後の訂正を確認する。パスを含む出力名は `SummaryOutputName_RejectsRawBeforeEquivalentValueCheck` が担う。 |
| LR2連携の最終配置の重なり拒否と、保存済みの衝突を問わない保存 | `PlaylistPropertySaveService` | [`ManagedPlaylistOutputPlacementTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/ManagedPlaylistOutputPlacementTests.cs) が条件の網羅（`EditPrecheck_RejectsOnlyChangedOrNewTablesOverlappingAfterSet`）を、同クラスの `PropertySave_RejectsOverlappingFinalPlacementBeforeMutation` が実編集sessionへの接続を、[`BmsPlaylistCustomFolderOutputTests.SavedCollidingOutput_ExplicitOperationsFreezeOnlyCollidingTables`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistCustomFolderOutputTests.cs) が衝突を解消する保存の受理と、配置以外の保存の警告を確認する。 |
| 外部情報での初期化の除外と旧配置 | `PlaylistWorkspaceViewModel` | `BmsPlaylistPersistenceLifecycleTests` の外部初期化ケースが、除外後の旧配置への再衝突と、安全な表の保存を確認する。同クラスの `ApplyPlaylistSummaryExternalPropertyInitializationAsync_ExcludesTargetsWhoseNewNameDuplicates` は、スタンドアロンで、未選択の表の現在の名前、先に受理した対象の新しい名前、名前の入替え、別の対象の旧名による除外と、大文字小文字だけの違い・自分の現在の名前の受理を、正本とDBで確認する。 |
| 外部同期の切替えの確認（確定は承認後、確認中の保存と初期化は終端を待つ、表示失敗） | [`PlaylistPropertyDialogViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertyDialogViewModel.cs) | [`PlaylistWorkspaceActionWorkflowTests`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) の `PlaylistPropertyDialog_PendingExternalSyncConfirmation_*` は、確認中に値を確定せず再切替えで確認を重ねないこと、保存と初期化が確認の終端を待ち受付を保持しないこと、承認・取消ごとの確定値を確認する。`PlaylistPropertyDialog_ExternalSyncConfirmationDisplayFailure_*` は、表示の失敗（失敗結果・失敗したTask・同期の例外）で例外を出さず値を変えず、Errorの通知が一件で、次の切替えが新しい確認を出すことを確認する。編集セッションの終了後の承認は値を適用しないことは、到達できる入口がテストの窓口だけのため、静的に確認する。 |
| 保存の現行性は所属と解決キーの版で判定し、URL補完の反映を競合にしない | [`PlaylistPropertySaveService`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistPropertySaveService.cs)（`PlaylistPropertyValues`）、[`BMSTable.EntryLookupRevision`](../../../BeMusicSeeker/Models/Playlist/BMSTable.EntryCommit.cs) | [`PlaylistUrlCompletionTests.PropertySave_AfterUrlCompletionCommitSucceedsAndKeepsRuntimeUrls`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistUrlCompletionTests.cs): 編集セッションの作成後に実際の補完を反映しても、名前だけの保存が成功してDBの名前を更新し、補完した実行時のURLを保持してDBへ保存しない。`PlaylistWorkspaceActionWorkflowTests.PlaylistPropertySave_RejectsAStaleDetachedSessionWithoutOverwritingConcurrentChanges` が、所属・解決キーの変更（entries・reference）と属性競合を引き続き拒否する。 |
| 並び順欄を編集していない保存は保存済みの明示順を変更対象にしない | `PlaylistPropertySaveService.CreateHeaderEdit` | [`PlaylistWorkspacePersistenceCommandTests.PlaylistPropertySave_NameOnlyKeepsStoredFolderOrder`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspacePersistenceCommandTests.cs): 手動 `[B]`・旧 `[""]`・`["", "A"]` の表で名前だけを保存し、保存済みの順序とメモリ・DBの自動判定を保つ。 |
| 明示要求の競合の案内（プロパティ・一括編集・セル編集・削除・BMT順・フラグ） | [`PlaylistWorkspaceViewModel`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.Mutations.cs)、[`PlaylistOperationNotices.Busy`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistOperationNoticePresenter.cs)、[`PlaylistRemovalWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistRemovalWorkflowOwner.cs)、[`PlaylistSummaryBulkEditDialog`](../../../BeMusicSeeker/Views/Playlist/PlaylistSummaryBulkEditDialog.cs) | `PlaylistWorkspaceActionWorkflowTests` の `PlaylistEditPreparation_*`・`PlaylistPropertySave_PlaylistBusyWarnsOnceAndKeepsDraft`・`PlaylistSummaryPropertyEdit_PlaylistBusyWarnsOnceWithoutValidationMessage`・`PlaylistSummaryCellAction_PlaylistBusyWarnsOnceWithoutGenericFailure`、`PlaylistSummaryBulkEditTests.BulkApply_PlaylistBusyRejectsBeforeChangesAndWarnsOnce`、`PlaylistWorkspacePersistenceCommandTests.BmtOrderCommand_PlaylistBusyWarnsOnceWithoutChanges`と削除の確認前検査、`MainWindowPlaylistWorkspaceWpfTests.MainWindowPlaylistDialogs_UseOwnedNativeModalLifetimeAndCleanup` が、副作用なし・警告一回・汎用の失敗なし・確認の前の検査・下書き保持を確認する。 |
| 取消ボタンの翻訳 | `PlaylistPropertyDialog.xaml` | [DialogPresentationTests](../../../tests/BeMusicSeeker.Windows.Tests/Dialogs/DialogPresentationTests.cs) の `CancelButtons_NewDialogsUseCurrentLanguageAndKeepCancellation` が6言語のContentを確認する。取消・Esc・draftは既存ケースが担当する。 |

## 関連資料

[保存と正本](../playlist/storage-and-export.md)、[LR2出力](../playlist/lr2-custom-folders.md)、[BMT出力](../playlist/bmt-export.md)、[共通外観](appearance.md)。
