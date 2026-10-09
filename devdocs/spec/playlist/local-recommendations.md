# 内蔵難度推定表とリコメンド

## 目的と適用範囲

同梱 Walkure モデルから難度推定表を生成し、選択中のローカルスコアからリコメンド表を計算します。生成結果は既存の登録、保存、再読込み、表示、LR2 カスタムフォルダと BMT 出力へ渡します。計算・表生成に Walkure、LR2IR、参照難度表の通信を使いません。任意 URL の補完と利用者のダウンロードは [別機能](downloads.md)です。

## 用語

[共通用語集](../glossary.md)を参照します。原観測は、選択 DB の既存 reader を経てモデル対象のキーとランプへ射影した変更不能な入力です。モデルには通常譜面と LR2 の複合キーを持つ段位が含まれます。

## 仕様

### モデルと難度推定表

取込み元は [walkure-offline の固定コミット](https://github.com/naktazdim/walkure-offline/tree/3836d501cd05da8fd8fd4fdc1881b3115399c55b)です。`assets/data/recommendation-model-entries.json` と `recommendation-star-rating-mapping.json` を未改変のままアセンブリへ埋め込みます。モデルの自動取得、更新、学習は行いません。`Copyright (c) 2026 walkure.net` と [MIT 全文](../../../third_party/licenses/21-Walkure-MIT.txt)をモデル・固定テスト入力に添付します。モデル欠落を通信や代替値で補いません。

EASY、NORMAL、HARD、FC の推定表には各 `clearDifficultyStarRatings` の保存値を使います。正常な null は未定義のまま保持し、再換算や 0 への補完をしません。所持の有無で対象を絞らず、通常譜面すべてを対象にします。発狂表と Overjoy の両所属は所属ごとの分類行を作ります。段位は表の候補へ表示しません。モデルにない作者名や配布 URL を外部表や通信から必須補完せず、所持譜面の表示は既存カタログの解決へ渡します。難度推定表にはスコアを要求しません。

### 選択スコアと方針

登録・再読込みの取得開始時に選択設定を一度捕捉し、UI 外で既存 `BmsLibraryInitializationService.LoadScoreTable` を一回呼びます。接続が閉じた後で原観測を射影します。全譜面の表示用スコア、グローバルスコア状態、IR 先読みを更新しません。一回の複数表操作では同じ読取り結果・失敗を共有し、方針だけを各表へ適用します。原観測は操作外へ保持せず、次の操作で読み直します。スコア変更・ソース切替による自動リコメンド更新は追加しません。

LR2 はモデルキーへ照合し、モデル収録の LR2 段位複合キー12件も実力の観測に使います。beatoraja は既存 reader の通常 mode=0 に限定し、既存カタログ索引・保存譜面情報で MD5 と SHA-256 を照合できる範囲を使います。songdata.db、全 mode、beatoraja 段位を追加で読みません。両所属の譜面も、一つのキーとして一度だけ観測します。ランク0も除外しません。

| 既存ランプ | 原観測 |
| --- | --- |
| NO PLAY | 観測なし。 |
| FAILED、INVALID、ASSIST | FAILED。 |
| EASY | EASY。 |
| CLEAR | NORMAL。 |
| HARD、EX-HARD | HARD。 |
| FC、PA、MAX | FC。 |

| 方針 | 採用結果 |
| --- | --- |
| 標準 | 実観測をそのまま使用し、FAILED を含める。 |
| `base=failed` | モデル対象の未プレイを FAILED で補充し、実ランプを上書きしない。所持だけに限定しない。 |
| `failed=noplay` | FAILED を観測から除外し、EASY 以上を維持する。 |

後二つは排他的で、同時指定はエラーです。同じモデルと他ランプを固定したとき、この二方針では新規 FAILED でθが変わりません。新規 EASY による低下は許容します。原スコアを方針で書き換えません。

未設定、読込み失敗、該当観測なし、推定不能は推薦操作の失敗です。正常な空 DB の Loaded と NotConfigured / Failed を区別します。古いスコア、全 NO PLAY、架空の実力、途中の表へ置き換えません。

### 数学と推薦候補

上流の GRM 対数尤度微分を移植し、θの探索範囲は [-20,20]、二分法の停止幅は 1e-6、返却値は上側の境界です。隣接ランプの累積ロジスティック確率の差をカテゴリ確率とします。換算は固定25点の区分線形補間と端の区間での外挿で、★と確率は上流と同じ二桁の数値です。平均難度や二値成功率で置き換えません。

探索途中のカテゴリ確率による非有限の微分も、上流と同じ比較・境界更新で扱います。最終的な範囲外・非有限のθや★を成功として返しません。

推薦は現在ランプより上位かつ未丸めの達成確率20%以上の通常譜面を対象にします。★難度 null の目標は確率0です。同じ譜面の複数目標は別行とし、EASY / NORMAL / HARD / FC の分類で確率降順に表示・出力します。同率の二次順序は固定しません。推薦表の記号は `R★` です。

### URI、UI、取消と保存

保存用の識別子は `bmseeker:table.estimation?type=easy/normal/hard/fc` と `bmseeker:table.recommended` です。推薦の三方針は前節のパラメーターで識別します。`mode=readonly/update`、mode 未指定、従来受理した未知 mode は同じローカル計算です。`id`、`filter`、`name`、その他未知パラメーターは無視し、未知値という理由だけで新規拒否しません。非 bmseeker、未対応経路、未対応推定種類の失敗を維持します。

メニューでは内蔵推定表4種類とリコメンド3方針を選択します。LR2ID と送信確認は不要です。リコメンドは三方針すべて、再読込みのたびに方針名と二桁の★実力値から `name` と `org_name` を生成します。手動名も次回再読込みで自動名へ戻り、プレイヤー ID・URI の `name` に依存しません。難度推定表と通常外部表の手動名保持は維持します。内蔵表の「ページを開く」は利用不可です。内蔵表の取込みも[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。必須準備が未完了なら開始せず、受理した一要求は登録・必要出力・公開の実終端まで待ちます。

計算専用読取りはライブラリ変更操作権を新たに取りません。既存の表更新・編集の許可条件を保ち、新しい受付制限、永続世代、再試行を追加しません。DB やモデルのロックを保持して UI・別管理主体を待ちません。読取り・計算・生成・反映へ取消を伝え、反映前の失敗と取消は元表・DB を保持して更新中状態を解除します。対象ごとの既存確定境界を維持し、別表の成功済み反映の全体巻戻しは保証しません。

生成内容のヘッダー・行を時刻や利用者設定を含まないハッシュへ渡し、[既存の再読込み統合](storage-and-export.md#更新検知と更新日時)で比較します。表示名または取得元名だけの差もヘッダーへ保存して画面へ公開しますが、名称差自体では `last_update` を進めません。既知ハッシュ差による保存・出力・日時更新は維持し、名称も含め同一なら不要な保存・通知・出力を繰り返しません。表設定、所属 ID、出力設定、メモは既存統合で引き継ぎます。

実力更新通知は、DB保存とUI反映に成功した後、旧新の `org_name` から解釈した★数値を比較します。両方解釈可能で数値が異なり、`ShowRecommUpdatedMsg` が有効なら一回通知します。`Updated`、ハッシュ初期化、`last_update` は通知の追加条件にしません。旧名・両ハッシュ未設定も同じ規則です。同値の表記差、比較不能な旧名、新規登録、設定OFF、読取り・計算・保存・反映の失敗や反映前の取消では実力通知しません。既存の数値文法・文化圏契約を維持し、手動の `name` や名称全体の差を比較に使いません。通知本文は新★と符号付き増減だけで、更新日時行は含めません。

全体リロード、個別リロード、プロパティで同期OFFからONへ変更して保存する経路は、操作内の通知セッションから実MainWindowの注入済み表示窓口へ渡します。同期ONの既存確認も同じ注入済み窓口へ接続し、元の本文・ボタン・肯定判定と表示不能の例外を維持します。全体リロードの入口Taskとは別に遅延同期の終端があり、取得だけで成功通知にはしません。表示失敗は既存窓口で観測し、新しい保存巻戻しや再送は追加しません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 実埋込みモデル、GRM、換算、確率、三方針 | [WalkureRecommendationModel](../../../BeMusicSeeker/Models/Playlist/WalkureRecommendationModel.cs) | [WalkureRecommendationMathTests](../../../BeMusicSeeker.Tests/Playlist/WalkureRecommendationMathTests.cs)。独立上流の θ差≤1e-9、二桁★、通常譜面649候補、混合ランプ、補充・除外、20%境界、null、外挿、推定不能。 |
| 四種類の元★・両所属・段位非表示・URI互換 | [PlaylistRecommendedTableOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistRecommendedTableOwner.cs) | [PlaylistRecommendedTableOwnerTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs)。スコア不要、保存値/null、未対応の拒否、未知mode等の受理、三方針の自動名・推定表の手動名と設定保持。 |
| freshな選択DB、接続解放、正規化、rank0、mode0 | [BMSLibrary.ReadRecommendationScoresAsync](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[bindings](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsPlaylistLibraryBindings.cs)、既存 reader | 同 owner 群の実合成 LR2 / beatoraja DB とソース切替、[BmsLibraryIrServiceTests](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) の reader・状態検査。 |
| 操作内だけの一回共有、失敗・取消・解除 | [PlaylistExternalSyncOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | 同 owner 群の複数表取得、原入力 gate、DB保持・更新中解除と次回取得。 |
| 保存・再読込み・同一入力・出力・通知 | [保存管理主体](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs)、既存 LR2/BMT 出力 | [BmsPlaylistExternalReloadTests](../../../BeMusicSeeker.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[PlaylistReloadMergeTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistReloadMergeTests.cs) の名称差だけの実ヘッダー保存・公開・日時維持、完全同一時の不要保存なし、ハッシュ初期化時の `Updated == false` と実力通知、通常外部表の手動名、設定・ID・メモとLR2/BMT出力の保持。 |
| 数値通知・反映失敗と取消 | [推薦管理主体](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistRecommendedTableOwner.cs)、外部同期・保存管理主体 | 同 owner 群の10.00↔10.75・同値・比較不能・OFF・推薦以外・日時なし、実DB群の旧★と設定ONを持つ保存失敗・UI未完了・拒否・中止・例外。 |
| 全体・個別・同期ON保存からの表示 | [MainWindow](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、既存 workspace とUiDialogCoordinator | [MainWindowPlaylistWorkspaceWpfTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の三入口×数値差/同値。独立★3.87→6.60/+2.73、両ハッシュ未設定、実DB両名称・固定日時・実ツリー自動名とfake presenter入力を照合。PlaylistOperationNotificationOwnerTests と UiDialogCoordinatorWpfTests の一回取出し・表示失敗検査は維持。 |
| ID・確認不要の取込み、準備待ち、ページ不可 | [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)、[ExternalLinks](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalLinks.cs) | [PlaylistWorkspaceActionWorkflowTests](../../../BeMusicSeeker.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)。三方針の実登録、準備前非変更・完了後結果、ページ解決。 |
| 内蔵4種類・3方針のメニューと資源 | [MainWindow.xaml](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)、Resources と lang 全6言語 | [MainWindowPlaylistWorkspaceWpfTests](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の実メニュー接続と URI、LocalizationResourceParityTests の全件共通検査。 |

数学の固定入力・採取手順・ランプ意味の分離は [TestData/Walkure](../../../BeMusicSeeker.Tests/TestData/Walkure/README.md)に示します。実 DB は小さい独自合成入力です。通常の並列テストとケース固有の資源を使い、gate は finally で解放して本体・遅延同期・出力の終結後に資源を破棄します。WPFは既存共有Dispatcherと非アクティブ表示fixtureを使い、全体リロードは対象の `PlaylistExternalSyncCompleted`、個別と同期ON保存は対象Taskの終端を待って表示件数を判定します。表示受付のreceiptだけを画面到達の成功根拠にはしません。共有設定・WPF を所有する既存群の直列属性は維持します。配布物での実モデル読込みは標準 Full の確認範囲です。

推薦専用の HTTP 境界・DTO・再試行・旧段位辞書・参照表取得とそのテストは退役しています。旧推定表の可変キャッシュ、LR2ID・送信確認、readonly/update の UI 分岐も除去しています。推薦管理主体の非公開名を source-string で検査した旧テストは削除し、実生成・保存・メニュー接続で責務を検査します。一般 HTTP の期限・取消、外部表取込み、再読込み統合、LR2/BMT 出力の既存テストは維持します。推薦の手動名を引き継ぐ旧期待だけを自動名へ置換し、名称差のある初回から全件未保存とする期待を、名称保存後の同一再読込みへ移しています。

## 関連資料

[保存と更新](storage-and-export.md)、[LR2 出力](lr2-custom-folders.md)、[BMT 出力](bmt-export.md)、[取得と導入](downloads.md)、[テスト作成](../development/test-authoring.md)。
