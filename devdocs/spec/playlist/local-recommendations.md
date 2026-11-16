# 内蔵難度推定表とリコメンド

## 目的と適用範囲

同梱のWalkureモデルから難度推定表を生成し、選択中のローカルスコアからリコメンド表を計算します。生成結果は既存の登録、保存、再読込み、表示、LR2カスタムフォルダとBMT出力へ渡します。計算と表の生成では、Walkure、LR2IR、参照難度表への通信を使いません。任意URLの補完と利用者のダウンロードは[別機能](downloads.md)です。

## 用語

[共通用語集](../glossary.md)に従います。**原観測**は、選択したスコアDBを既存の読取り処理で読み、モデル対象のキーとランプへ投影した変更不能な入力です。モデルには通常譜面と、LR2の複合キーを持つ段位が含まれます。

## 仕様

実力更新通知の実力値と差分は、表名・一覧と同じく、文化圏に依存しない小数2桁で表示します。例えば `10.75` と ` (+0.75)`、`10.00` と ` (-0.75)` とし、数値の保存・計算や通知条件を変えません。

### モデルと難度推定表

取込み元は [walkure-offline の固定コミット](https://github.com/naktazdim/walkure-offline/tree/3836d501cd05da8fd8fd4fdc1881b3115399c55b)です。`assets/data/recommendation-model-entries.json` と `recommendation-star-rating-mapping.json` を未改変のままアセンブリへ埋め込みます。モデルの自動取得、更新、学習は行いません。`Copyright (c) 2026 walkure.net` と[MIT全文](../../../third_party/licenses/21-Walkure-MIT.txt)をモデル・固定テスト入力に添付します。モデルが欠けた場合も、通信や代替値で補いません。

EASY、NORMAL、HARD、FC の推定表には各 `clearDifficultyStarRatings` の保存値を使います。正常な null は未定義のまま保持し、再換算や 0 への補完をしません。所持の有無で対象を絞らず、通常譜面すべてを対象にします。発狂表とOverjoyの両方に属する譜面は、所属ごとに分類行を作ります。段位は表の候補へ表示しません。モデルにない作者名や配布URLは、外部表や通信から必須補完せず、所持譜面の表示は既存カタログの解決へ渡します。難度推定表はスコアを必要としません。

### 選択スコアと方針

登録・再読込みの取得開始時に選択設定を一度だけ捕捉し、UIスレッド外でスコアDBを一回読みます。DB接続を閉じた後で原観測へ投影します。全譜面の表示用スコア、グローバルなスコア状態、IRの先読みは更新しません。一回の複数表操作では同じ読取り結果・失敗を共有し、方針だけを各表へ適用します。原観測は操作の外へ保持せず、次の操作で読み直します。スコアの変更やソースの切替による、リコメンドの自動更新は行いません。

LR2はモデルのキーへ照合し、モデル収録のLR2段位の複合キー12件も実力の観測に使います。beatorajaは既存の読取り処理の通常のmode=0に限り、既存カタログ索引と保存済み譜面情報でMD5とSHA-256を照合できる範囲を使います。songdata.db、全mode、beatorajaの段位は追加で読みません。両方に属する譜面も、一つのキーとして一度だけ観測します。ランク0も除外しません。

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

未設定、読込み失敗、該当する観測なし、推定不能は、推薦操作の失敗です。正常な空のDB（Loaded）と、NotConfigured / Failedを区別します。古いスコア、全NO PLAY、架空の実力、途中の表へは置き換えません。

対象を自動選択する一括同期（起動時の同期と、全体の再読込みに続く同期）では、スコアDBを利用できない（NotConfigured）ときだけ、そのおすすめ表だけを見送ります。見送った表のDB・項目行・公開中の参照を変えず、更新の確定情報を作らず、LR2・BMTの出力対象にしません。同じ同期の他の表の取得・保存・確定は妨げません。見送りは隠さず、その表の同期STATUSは「その他のエラー」の失敗のまま原因と取得元を詳細へ残し、対象単位の診断を記録します。見送りを理由に、操作全体や起動時の同期Taskを失敗にはしません。ただし対象の失敗は残るため、完了を全件成功とは通知しません。判別は例外の型で行い、文言には依存しません。

取込み、選択した表の再同期、プロパティ保存のように表を名指しする操作では、NotConfiguredも操作の失敗です。読込み失敗（Failed）、該当する観測なし、推定不能は、一括同期でも見送らず、操作全体の失敗に含めます。

### 数学と推薦候補

上流のGRMの対数尤度微分を移植し、θの探索範囲は[-20,20]、二分法の停止幅は1e-6、返却値は上側の境界です。隣接ランプの累積ロジスティック確率の差をカテゴリ確率とします。換算は固定25点の区分線形補間と端の区間での外挿で、★と確率は上流と同じ二桁の数値です。平均難度や二値成功率で置き換えません。

探索途中のカテゴリ確率による非有限の微分も、上流と同じ比較・境界更新で扱います。最終的な範囲外・非有限のθや★を成功として返しません。

推薦は現在ランプより上位かつ未丸めの達成確率20%以上の通常譜面を対象にします。★難度 null の目標は確率0です。同じ譜面の複数目標は別行とし、EASY / NORMAL / HARD / FC の分類で確率降順に表示・出力します。同率の二次順序は固定しません。推薦表の記号は `R★` です。

### URI、UI、取消と保存

推薦計算と難度推定の行生成はそれぞれ固有の処理とし、完成した集合をJSON解析と同じ `BMSTable.entries` の設定経路へ一度渡してから、生成ハッシュを計算します。親・所属ID、正規化、フォルダの派生状態、読込状態とリビジョンは[共通取込み](storage-and-export.md#正本と識別)に揃え、共通の登録・保存・再読込みへ渡します。保存時の所属推測、推定専用の保存、NOT NULL制約の緩和は行いません。

保存用の識別子は `bmseeker:table.estimation?type=easy/normal/hard/fc` と `bmseeker:table.recommended` です。推薦の三方針は前節のパラメーターで識別します。`mode=readonly/update`、mode 未指定、従来受理した未知 mode は同じローカル計算です。`id`、`filter`、`name`、その他未知パラメーターは無視し、未知値という理由だけで新規拒否しません。非 bmseeker、未対応経路、未対応推定種類の失敗を維持します。

メニューでは内蔵推定表4種類とリコメンド3方針を選択します。LR2 IDと送信確認は不要です。リコメンドは三方針とも、再読込みのたびに方針名と小数2桁の★実力値から `name` と `org_name` を生成します。手動で付けた名前も次回の再読込みで自動名へ戻り、プレイヤーIDやURIの `name` に依存しません。難度推定表と通常の外部表は、手動名を保持します。内蔵表の「ページを開く」は利用できません。プレイリストツリーの表と同じく、プレイリスト一覧（サマリー）の行の右クリックメニューの「ページを開く」も、開ける外部ページがない行（`LinkUri` がnull・相対URI・`bmseeker` スキームの内蔵表）では無効にします。判定は `OpenPlaylistSummaryUriAsync` が実際に開く条件と同じで、`bmseeker` 以外の絶対URIは有効です。内蔵表の取込みも[競合ポリシー](../core/operation-concurrency-policy.md#プレイリストと必要出力)に従います。必須の準備が未完了なら開始せず、受理した一要求は登録・必要な出力・公開の終端まで待ちます。

計算専用の読取りは、ライブラリ変更の操作権限を新たに取りません。既存の表更新・編集の許可条件を保ち、新しい受付制限、永続世代、再試行は加えません。DBやモデルのロックを保持したまま、UIや別の管理主体を待ちません。取消は読取り・計算・生成・反映へ伝えます。反映前の失敗と取消は元の表とDBを保持し、更新中の状態を解除します。対象ごとの既存の確定境界を維持し、別の表で成功した反映の全体巻戻しは保証しません。

生成内容のヘッダーと行から、時刻や利用者設定を含まないハッシュを求め、[既存の再読込み統合](storage-and-export.md#更新検知と更新日時)で比較します。ヘッダーのハッシュには、表示言語のリソースから作る名称（`org_name`）を含めず、推定の種類・推薦の方針の識別子・★の数値（Invariant書式）など、言語に依存しない値を使います。表示言語の切替えだけでは更新と判定しません。保存済みの旧ハッシュとは、導入後の最初の再読込みで一度だけ食い違い、その一回に限り日時が進むことを許容します。表示名または取得元名だけの差もヘッダーへ保存して画面へ公開しますが、名称差自体では `last_update` を進めません。既知ハッシュ差による保存・出力・日時更新は維持し、名称も含め同一なら不要な保存・通知・出力を繰り返しません。表設定、所属 ID、出力設定、メモは既存統合で引き継ぎます。

実力更新通知は、DB保存とUI反映に成功した後、旧新の `org_name` から解釈した★数値を比較します。両方解釈可能で数値が異なり、`ShowRecommUpdatedMsg` が有効なら一回通知します。`Updated`、ハッシュ初期化、`last_update` は通知の追加条件にしません。旧名・両ハッシュ未設定も同じ規則です。同値の表記差、比較不能な旧名、新規登録、設定OFF、読取り・計算・保存・反映の失敗や反映前の取消では実力通知しません。既存の数値文法・文化圏契約を維持し、手動の `name` や名称全体の差を比較に使いません。通知本文は新★と符号付き増減だけで、更新日時行は含めません。

全体の再読込み、個別の再読込み、プロパティで同期をOFFからONへ変更して保存する経路は、いずれも操作内の通知セッションから、メイン画面に注入された同じ表示窓口へ実力更新通知を渡して表示します。同期ONの既存の確認も同じ窓口を使い、元の本文・ボタン・肯定判定と、表示不能時の例外を維持します。全体の再読込みでは、入口の処理とは別に遅延同期の終端があります。取得しただけでは成功通知にしません。表示の失敗は既存の窓口で観測し、保存の巻戻しや再送は加えません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 地域に依存しない実力更新通知 | `PlaylistRecommendedTableOwner.NotifyAppliedSkillChange` | `PlaylistRecommendedTableOwnerTests.AppliedSkillNotification_UsesOrgNameNumbersAndSettingWithoutDate` はfr-FRで増減の固定表記と既存の通知・無通知条件を確認する。 |
| サマリー行の「ページを開く」の可否が実際に開く条件と一致する | [`PlaylistWorkspaceViewModel.CanOpenPlaylistSummaryUri`](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalLinks.cs)、[`MainWindow.customTablePlaylistSummary_RowContextMenuRequested`](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs) | [`PlaylistWorkspaceActionWorkflowTests.PlaylistSummaryPageAvailability_MatchesWhatSummaryOpenActuallyOpens`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) がURIなし・相対・内蔵表・https・httpの可否と実際の起動の一致を、[`MainWindowPlaylistWorkspaceWpfTests.PlaylistSummaryOpenPageMenu_IsEnabledOnlyForExternalPages`](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) が右クリックの入口から開いたメニューの項目の可否を確認する。 |
| 実埋込みモデル、GRM、換算、確率、三方針 | [WalkureRecommendationModel](../../../BeMusicSeeker/Models/Playlist/WalkureRecommendationModel.cs) | [WalkureRecommendationMathTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/WalkureRecommendationMathTests.cs)。独立上流の θ差≤1e-9、二桁★、通常譜面649候補、混合ランプ、補充・除外、20%境界、null、外挿、推定不能。 |
| 四種類の元★・両所属・段位非表示・URI互換 | [PlaylistRecommendedTableOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistRecommendedTableOwner.cs) | [PlaylistRecommendedTableOwnerTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs) / [PlaylistRecommendedTableOwnerTests（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs)。スコア不要、保存値/null、未対応の拒否、未知mode等の受理、三方針の自動名・推定表の手動名と設定保持。 |
| 完成集合の共通取込みと実保存・新主体のhydration | [BMSTable](../../../BeMusicSeeker/Models/Playlist/BMSTable.cs)、共通登録・読込み主体 | 同テスト群の四推定・三推薦の生成時の親、採番後ID、実SQLite往復、非空行・名称・フォルダ・レベル、推定の手動名と保存設定保持。JSONの互換folderと所属は [BmsPlaylistExternalLoadTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistExternalLoadTests.cs)、同じ行参照の補償と局所編集は既存Lifecycle・Workspace群が分担する。 |
| freshな選択DB、接続解放、正規化、rank0、mode0 | [BMSLibrary.ReadRecommendationScoresAsync](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[bindings](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/BmsPlaylistLibraryBindings.cs)、既存 reader | 同 owner 群の実合成 LR2 / beatoraja DB とソース切替、[BmsLibraryIrServiceTests](../../../tests/BeMusicSeeker.Core.Tests/Ir/BmsLibraryIrServiceTests.cs) の reader・状態検査。 |
| 操作内だけの一回共有、失敗・取消・解除 | [PlaylistExternalSyncOwner](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs) | 同 owner 群の複数表取得、原入力 gate、DB保持・更新中解除と次回取得。 |
| 一括同期でのスコア未設定の見送りと、名指し・他の失敗の維持 | [`PlaylistRecommendedTableOwner.LoadWalkureTableAsync`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistRecommendedTableOwner.cs)（専用例外 `RecommendationScoreNotConfiguredException`）、[`PlaylistExternalSyncOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistExternalSyncOwner.cs)（`UpdateBMSTablesInternalAsync` の見送り指定と `PlaylistReloadTargetResult.IsSkippedRecommendationScoreNotConfigured`） | [`PlaylistRecommendedTableOwnerTests.LoadWalkureTable_MissingFailedOrEmptyScoresFail`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistRecommendedTableOwnerTests.cs) が未設定だけ専用型で、他は `InvalidOperationException` そのものであること、同クラスの `UpdateBmsTablesInternalAsync_ScoreNotConfiguredSkipsOnlyRecommendedInAutomaticBatch` が、一括・未設定だけの見送り（集約失敗なし・対象の失敗と診断は保持）、名指し・読込み失敗・観測なしの集約失敗、おすすめ表のDB・行・参照・出力の不変、他の表の反映、読取り1回を実SQLiteで確認する。[`PlaylistExternalFailureBoundaryTests.ExternalSync_StartupWithoutScoreDatabaseCompletesAndKeepsRecommendedTable`](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistExternalFailureBoundaryTests.cs) が、起動時の予約した処理のTaskの正常終了、STATUSの失敗と原因、完了通知が全件成功でないこと、受付の解放を確認する。 |
| 保存・再読込み・同一入力・出力・通知 | [保存管理主体](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistAggregatePersistenceOwner.cs)、既存 LR2/BMT 出力 | [BmsPlaylistExternalReloadTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/BmsPlaylistExternalReloadTests.cs) / [BmsPlaylistExternalReloadTests（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/BmsPlaylistExternalReloadTests.cs)、[PlaylistReloadMergeTests](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistReloadMergeTests.cs) の名称差だけの実ヘッダー保存・公開・日時維持、完全同一時の不要保存なし、ハッシュ初期化時の `Updated == false` と実力通知、通常外部表の手動名、設定・ID・メモとLR2/BMT出力の保持。 |
| 数値通知・反映失敗と取消 | [推薦管理主体](../../../BeMusicSeeker/Models/BmsLibraryInternal/Playlist/PlaylistRecommendedTableOwner.cs)、外部同期・保存管理主体 | 同 owner 群の10.00↔10.75・同値・比較不能・OFF・推薦以外・日時なし、実DB群の旧★と設定ONを持つ保存失敗・UI未完了・拒否・中止・例外。 |
| 全体・個別・同期ON保存からの表示 | [MainWindow](../../../BeMusicSeeker/Views/MainWindow/MainWindow.cs)、既存 workspace とUiDialogCoordinator | [MainWindowPlaylistWorkspaceWpfTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) / [MainWindowPlaylistWorkspaceWpfTests（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の三入口×数値差/同値。独立★3.87→6.60/+2.73、両ハッシュ未設定、実DB両名称・固定日時・実ツリー自動名とfake presenter入力を照合。PlaylistOperationNotificationOwnerTests と UiDialogCoordinatorWpfTests の一回取出し・表示失敗検査は維持。 |
| ID・確認不要の取込み、準備待ち、ページ不可 | [PlaylistWorkspaceViewModel](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalPlaylistImport.cs)、[ExternalLinks](../../../BeMusicSeeker/ViewModels/Playlist/PlaylistWorkspaceViewModel.ExternalLinks.cs) | [PlaylistWorkspaceActionWorkflowTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs) / [PlaylistWorkspaceActionWorkflowTests（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/Playlist/PlaylistWorkspaceActionWorkflowTests.cs)。三方針の実登録、準備前非変更・完了後結果、ページ解決。 |
| 内蔵4種類・3方針のメニューと資源 | [MainWindow.xaml](../../../BeMusicSeeker/Views/MainWindow/MainWindow.xaml)、Resources と lang 全6言語 | [MainWindowPlaylistWorkspaceWpfTests（Core）](../../../tests/BeMusicSeeker.Core.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) / [MainWindowPlaylistWorkspaceWpfTests（Windows）](../../../tests/BeMusicSeeker.Windows.Tests/MainWindow/MainWindowPlaylistWorkspaceWpfTests.cs) の実メニュー接続と URI、LocalizationResourceParityTests の全件共通検査。 |

数学の固定入力・採取手順・ランプ意味の分離は[TestData/Walkure](../../../BeMusicSeeker.Tests/TestData/Walkure/README.md)に示します。実DBを使うテストは、小さな独自の合成入力を使います。WPFを使うテストは、全体の再読込みでは対象の `PlaylistExternalSyncCompleted`、個別の再読込みと同期ON保存では対象Taskの終端を待ってから表示件数を判定します。表示の受付結果だけを、画面に到達した根拠にはしません。配布物での実モデルの読込みは、標準の `full` の確認範囲です。

## 関連資料

[保存と更新](storage-and-export.md)、[LR2 出力](lr2-custom-folders.md)、[BMT 出力](bmt-export.md)、[取得と導入](downloads.md)、[テスト作成](../development/test-authoring.md)。
