# LR2IRスコアと順位の更新

## 目的と適用範囲

LR2IRの本人スコア、ローカル順位キャッシュ、未送信判定と表示用集計を定めます。楽曲DBの生成やプレイ履歴とは独立した連携です。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 二つの情報源

本人スコアXMLと `ir_score` / `ir_score_refresh_metadata` は未送信判定とUNSENT SONGS出力に使います。順位キャッシュXMLと `ir_data` は順位表示とオフラインスコアの順位推定に使います。有効設定も別々です。

本人スコア取得URLのプレイヤーIDと同一性判定用digestの整数はInvariantで表します。digestはhash順の小文字hash、rawクリア値、既存の成績整数をタブ区切り・LF終端で連結し、OSの地域によってハッシュ入力を変えません。成績の値・並び順・`lastupdate`を除く既存の対象列は維持します。

刷新履歴の構造は、明示的な起動・モデル構築のDB準備で初回取得前に揃えます。新規DBでは未記録の履歴を正常に読め、既存DBと再準備では刷新履歴・本人スコア・楽曲行を保持します。Gatewayの通常履歴読取りは構造・保存行を変更せず、未記録の`null`と表未準備等の読取り失敗を区別します。

本人スコアの確定前に刷新履歴または保存済み本人スコアを読み取れなければ、その更新を失敗で終え、DBの置換・履歴保存・メモリ採用を進めません。正常な未記録・空一覧・digest一致へ変換せず、現在のスコアと未送信判定を保持し、既存の結果と背景仕事の報告で失敗を伝えます。独立した順位キャッシュ更新と次の通常要求は続けられ、その場の再試行や永続的な無効化は行いません。

本契約は起動時のプレイヤーによる更新を対象とし、起動後のプレイヤー切替は扱いません。取得内容と履歴のdigestが一致すれば、正常に読み取った保存成績を使って置換を省きます。過去の部分保存を検出するための追加照合は行いません。履歴が未記録・不一致の場合は、既存の保存成績との比較を使います。

成績が変わる場合は、成績と対象プレイヤーの履歴を同じDB接続・既存トランザクションで一体に保存します。実成績が一致する場合は成績を置換せず、履歴が未記録・不一致の場合だけ履歴を保存し、成績の`lastupdate`を含む既存値を保持します。成績と履歴がともに一致する場合は保存しません。トランザクション内の通常の保存・確定失敗は更新失敗として終え、旧成績・全プレイヤーの履歴・時刻を保持し、新成績をメモリへ採用しません。確定を再呼出しせず、診断の失敗を元結果へ加えません。

本人スコア取得を無効にすると、取得、保存、その情報による未送信判定を行いません。起動時順位キャッシュ更新を無効にすると、ローカルXMLの走査・読込み・保存を行いません。両方無効なら起動時の順位更新を予約しません。

### 本人IR応答の認識

既存の行抽出で有効な成績を一件以上取得できれば、外枠のXML形式を新たな拒否条件にしません。不正行の省略、部分応答、同じhashの先頭行優先を維持します。

抽出結果が0件の場合は、応答全体から正常な空一覧と確認できるときだけ空として採用します。認識対象は、内部が空白だけの `<scorelist></scorelist>` または自己終了する `<scorelist/>` です。前後の空白・BOM、既知の先頭 `#`、XML宣言、末尾の `rivalname` 要素は許容します。HTML・未知の外枠・未知の本文へ空一覧が埋め込まれているだけの場合、途中切断、null・空本文、全ての成績行が読めない場合は正常0件にしません。

判別不能な応答は既存の解析失敗として終え、本人成績・刷新履歴・共有スコアと未送信判定を保持します。独立した順位cacheと次の通常要求は続けられます。別形式への推測による読替えや自動再取得は行いません。

外枠の互換条件は[取得ツール作者の応答例](https://note.com/3935/n/nddf67d55830a)による観測を参考にします。公式サービスの実際の正常0件本文は未確認であり、上記の空一覧認識は観測済み外枠からの限定的な判断です。異なる形式への対応は実例を得て判断します。

### 予約と要求の鮮度

予約には、現在の初期化がスコア更新を要求すること、取得元がLR2であること、スコアDBパスがnullでないこと、二系統のいずれかが有効であることを要求します。`OperationModeLR2DB`、プレイヤーID、オフライン順位推定の設定を予約条件へ追加しません。

予約時の設定を使って受付を判断し、実行時に最新設定から二系統の作業を再評価します。プレイヤーID0は実行時に処理不要とする条件であり、予約の有無は変えません。

本人スコアの通信・XML解析・正規化ハッシュの計算と順位cache読取りは、必須UI・操作解禁後に一つのscheduler work内でLなしで行います。DB置換・更新とメモリ確定はL取得後にモデル停止と既存score contextを確認して行い、旧組の結果を新組へ保存しません。cache準備内のUpsertも確定側へ集約し、通信から確定の実終端までschedulerで追跡します。XMLの譜面側の更新時刻 `lastupdate` はスコア内容の同一性判定から除きます。通信の取消・終了待ち・取得結果の再利用はデータ仕様と終了仕様に従います。

### ランキング情報JSONの部分応答

`BmsLibraryIrClient.ParseRankingInfoResponse` は有効な行のMD5・サイズ・時刻を保持します。JSONオブジェクト以外の値、必須項目欠落、不正MD5、数値・日付形式や範囲の不正は行単位で省略します。内部故障・資源不足は不正行として隠しません。省略時は応答ごとの件数と最大5件の行番号・理由を一度だけ診断し、診断先の例外は返す一覧を変更しません。

サイズの数値tokenは型付きで読み、文字列の整数解析は既存の前後空白・符号の文法をInvariantで保ちます。JSONの更新日時と順位XMLの固定日時 `yyyy-MM-dd HH:mm:ss` はGregorianで解釈し、OSの暦へ読み替えません。

空・全不正・一部不正の応答はいずれも部分一覧です。返らないMD5を既存キャッシュの削除根拠にせず、有効行だけから取得・更新の候補を選びます。文書全体の不正JSONや後続JSONは応答解析失敗とします。

### 順位キャッシュの解析

`ir_data.lastcacheupdate` とXML末尾の更新時刻で再読込み対象を判断します。既定の並列数は `max(1, ProcessorCount - 1)` で、処理数を制限します。起動時、手動取得、`LR2IRCache` は共通の解析を使います。

順位cache更新のための保存済み順位DBの読取りに失敗した場合は、その更新を失敗として既存の背景仕事の報告へ渡します。正常空、更新不要、正常完了に置き換えず、失敗した順位cacheによる保存・共有スコアへの採用を進めません。独立した本人IRが正常に確定する変更は保ちます。正常に空を読めた場合の通常処理は維持し、その場の再試行や恒久的な無効化は行いません。独立した仕事と次の通常要求は続けられます。

cacheファイルの昇格、再読込み判定、XML読取りに失敗した場合は、対象パス・処理段階・元例外を既存の局所診断へ渡します。失敗した対象だけを非採用とし、健全な対象は処理します。純診断の失敗は操作結果へ加えません。

更新用の解析は全順位の行集合を作らず、スコア要素を一回読み、人数、平均、標本標準偏差、本人のスコア、順位を集計します。ID、クリア、ノート数、コンボ、PG、GR、最小BPが0以上の整数である行だけを使い、不正・負数を含む行は除きます。

順位は本人より高いスコアの件数+1です。本人がいなければ未プレイかつ順位-1の行を作ります。XMLの更新時刻が空・不正・NUL終端ならファイル更新時刻を使います。解析自体の失敗はスキップし、旧式の全件解析へ切り替えません。`xmlFallbackLoads` は診断互換の項目であり、切替経路の存在を意味しません。

### 成績の採用と派生値

本人IRと順位cacheから成績を採用するときは、採用後の得点・ノート数から共通の `ScoreValueCalculator` でDJレベルと整数のスコア率を揃えます。新規行と既存行で同じ計算を使い、既存行の高得点採用ではIRのノート数も採用します。1000ノート・1800点はAAA・90%です。ノート数が0以下なら別の値で補完せず、DJレベルはINVALID、既存の非nullable整数率は0で表します。

低得点・同点では旧得点・ノート数・DJレベル・整数率を保ちます。clear・option履歴・BP・comboの既存採用条件、同点での順位採用、低得点時の順位推定、人数・更新日時・未送信判定はそれぞれ従来の判断を続けます。採用元の成績値を通常snapshotへ公開し、通常一覧のDJレベル検索とランプ内訳集計も同じ採用値を使います。全スコアの再走査や追加通信は行いません。

### オフライン順位推定と保存

推定が有効でローカルスコアがIRの値より高い場合だけ、順位計算用の小さな情報を要求時に読みます。同じ更新中に読み込んだハッシュは再利用します。起動時キャッシュ更新が無効なら、その経路から推定結果を自動更新しません。

対象IDの `ir_data` が空の場合は、重複排除した行を一つのトランザクションで一括追加します。既存行がある場合や一括追加の条件を満たさない場合はハッシュごとに更新します。互換性のため一意制約は追加せず、`ir_data_idx_lr2id_hash(lr2id, hash)` の非一意索引を使います。

手動取得した順位cacheは、採用対象を既存のトランザクションで保存してから共有スコアへ反映します。保存・確定が失敗した場合は旧DB値と共有スコア、表示snapshotを保持し、未保存の更新や新ハッシュを後続の公開へ混入させません。XMLファイルの昇格とDB保存を跨ぐ原子性は追加せず、その場の再試行や新しい状態は持ちません。

`ranking_cache_refresh done` は読込みと保存時間、並列数、解析行数、解析失敗、互換項目、一括追加の利用、追加の推定読込みを記録します。過去の測定値を現行の時間上限にはしません。

スコアの購読はライブラリがスコア実体ごとに一つ保持し、差替え・再読込み・終了で解除します。譜面ごとのscore購読は持ちません。IR・順位・未送信状態の一括反映は、現在lookupのsnapshotと購読・版・取得状態を採用してから、既存snapshotの変更キーを通知します。個別scoreへの重複する一括通知は行わず、通常setterの通知は維持します。通常一覧は通知されたキーの行を現在lookupから投影します。

本人IR・順位cacheの公開Actionは既存の操作受付とモデルロックの解放後に実行します。PropertyChanged、snapshot変更、性能診断の通常失敗はそれぞれの純通知境界で閉じ、必要なsnapshot・購読反映をそのcatchへ含めません。通知の故障で保存・現在lookup・版を戻したり、取得や保存を再実行したりせず、独立cache・要求の実終端・次要求を保ちます。ダウンロード結果には実際に失敗した対象だけを残し、通知例外を失敗対象や再送出する集合へ加えません。取得状態と版のgetterは採用済みsnapshotから読み、通知用の別状態を持ちません。保存済みスコアの初期化も必要な採用と純通知を分け、借用した実親の解放後に通知します。成功・後段失敗と表示への接続は[起動の完了境界](../runtime/startup.md#起動の流れと完了境界)に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 本人IR・順位cacheの採用notesとDJレベル/整数率、低得点・同点の保持 | [`BMSScore`](../../../BeMusicSeeker/Models/Chart/BMSScore.cs) の両Overwrite、[`BmsLibraryIrService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs) の高得点採用 | [`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) の `ApplyIrDataToScores_AdoptsDerivedValuesOnlyWithHigherScore` が新規/高得点/旧notes0・別値/同点/低得点、`BmsScoreOverwrite_ZeroNotesClearsStaleDerivedValues` が両入口の無効notes、`UpdateBmsScores_NewIrScoreInitializesDerivedValues` と既存未送信・clear履歴のケースが通常本人IRの接続を分担する。 |
| 採用成績の通常snapshot・DJレベル検索・内訳集計への接続 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) のscore公開と通常一覧/ランプsnapshot | [`BmsLibraryIrStartupTests.InitializeStartup_RankingCacheDerivedValuesReachNormalSearchAndLampAggregation`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) が実起動・score DB/cache XMLの既存/新規成績からAAA/整数率90を直接確認し、通常ownerの配送終端後の検索加入と同snapshotの実集計件数を確認する。 |
| 刷新履歴の初回準備、既存行保持、純読取りと未準備時の失敗 | [`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の起動・Ensure/Repair準備と履歴読取り | [`AppSchemaPreflightServiceTests`](../../../BeMusicSeeker.Tests/Startup/AppSchemaPreflightServiceTests.cs) の `EnsureLibraryStartupSchema_ContainsLibraryOwnedTables` と `EnsureAppOwnedSchema_FreshLr2DatabaseConvergesPreflightWithoutDigestMigration` が初回利用、`PrepareSchema_ExistingIrRefreshMetadataPreservesRowsAndReadDoesNotMutateDatabase` が既存行・再準備・無変更、`LoadIrScoreRefreshMetadata_UnpreparedDatabaseFailsWithoutCreatingTableOrChangingRows` が失敗と無変更を分担する。 |
| 本人スコア確定前の読取り失敗、旧状態保持と次要求 | [`BmsLibraryIrService.CommitPreparedIrScoreTableWithMetrics`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)、[`BMSLibrary.RunDeferredRankingRefresh`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) の `UpdateIrScoreTableWithMetrics_RecognizedEmptyResponseCommitsEmptyScores`、`CommitPreparedIrScoreTable_MetadataReadFailurePreservesDatabase`、`CommitPreparedIrScoreTable_SavedScoreReadFailurePreservesDatabase` が正常空と履歴・保存スコアの実読取り失敗を分担し、[`BmsLibraryIrStartupTests.InitializeStartup_SavedIrScoreReadFailurePreservesScoresAndAllowsNextRequest`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) が実起動から失敗報告・メモリ保持・別仕事と次要求を確認する。 |
| 本人IRの正常空と判別不能応答、非空の互換性 | [本人IRサービス](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs) | [サービス試験](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs)で正常0件・識別不能・全不正行と既存非空を分担する。[起動試験](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs)で代表応答の非採用、独立cacheと次要求を確認する。保存失敗の条件表は既存の一体保存試験へ分担する。 |
| 一体保存、履歴だけの保存と失敗時の非採用 | [`BmsLibraryIrService.CommitPreparedIrScoreTableWithMetrics`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)、[`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs) の `ReplaceIrScoreTableAndRefreshMetadata`、`UpsertIrScoreRefreshMetadata` | [`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) の `UpdateIrScoreTableWithMetrics_SameScoreDigestSkipsReplaceForSameXml` が初回保存と再保存省略、`CommitPreparedIrScoreTable_MatchingScoresUpdatesOnlyMissingOrDifferentMetadata` が成績を保つ履歴保存、`CommitPreparedIrScoreTable_MetadataStatementFailureRollsBackScoreAndAllMetadata` と `CommitPreparedIrScoreTable_MetadataOnlyCommitBusyPreservesDatabase` が実SQLite保存失敗を分担する。[`BmsLibraryIrStartupTests.InitializeStartup_IrMetadataSaveFailurePreservesScoresAndAllowsNextRequest`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) はメモリ非採用・別仕事と次要求の接続、一般の確定Busyと巻戻し・次transactionは [`SQLiteProviderRuntimeTests.SQLiteConnectionEx_CommitBusyRemainsFailureAndRollsBackUncommittedChanges`](../../../BeMusicSeeker.Tests/Runtime/SQLiteProviderRuntimeTests.cs) へ分担する。 |
| 予約条件、UI後の本人スコア取得、通信中のL受付と退役後の非保存 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [`StartupRankingRefreshPolicyTests`](../../../BeMusicSeeker.Tests/Startup/StartupRankingRefreshPolicyTests.cs)、[`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) |
| 情報JSONの型維持、不正行の上限付き診断、部分応答とキャッシュ保持 | [`BmsLibraryIrClient.ParseRankingInfoResponse`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrClient.cs)、[`BmsLibraryIrService.GetIRDataNeedUpdatesFromRankingInfo`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs) | [`BmsLibraryIrServiceTests.ParseRankingInfoResponse_PreservesTypedFieldsAndSkipsInvalidRows`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs)、`ParseRankingInfoResponse_BoundsSkippedRowDiagnostics`、`ParseRankingInfoResponse_PartialListAndDiagnosticFailurePreserveCache` |
| 起動時順位DBの読取り失敗と正常空、ファイル失敗の局所診断 | [順位サービス](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)、[背景仕事](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | [サービス試験](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs)で正常空からの採用を、[起動試験](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs)で実DB読取り失敗の非成功・旧状態保持・次要求を分担する。ファイル診断の対象・段階・元原因の接続は静的に確認し、対象不良と健全cacheの混在は既存解析回帰で確認する。 |
| XML集計、順位と未送信、保存 | [`LR2IRCache`](../../../BeMusicSeeker/Models/Ir/LR2IRCache.cs) | [`BmsLibraryIrServiceTests`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) |
| 機械整数と固定Gregorian日時の地域非依存 | `BmsLibraryIrService.ComputeScoreDigest`、`Lr2IrRankingCacheParser`、`IRDataCacheInfo.ParseSize` | `BmsLibraryIrServiceTests.ComputeScoreDigestKeepsRawLr2Clear2DistinctFromFailed` は固定tab・LF入力の独立SHA-256とrawクリアの区別、`RankingCacheSummaryParser_TargetPlayerUsesUnifiedExpectedValues` は同一XMLの日時・集計、`ParseRankingInfoResponse_PreservesTypedFieldsAndSkipsInvalidRows` は数値tokenと整数文字列を異文化で確認する。実DBのmetadata・同値skipは `UpdateIrScoreTableWithMetrics_SameScoreDigestSkipsReplaceForSameXml` に分担する。 |
| 手動順位cacheの保存失敗と共有スコアの非採用 | [`BmsLibraryIrService.ApplyPreparedDownloadedRankingCache`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Ir/BmsLibraryIrService.cs)、[`BMSLibrary.DownloadIRData`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) | `DownloadIRData_DatabaseSaveFailurePreservesSharedScoresAndSnapshotAndAllowsNextRequest` は実SQLiteの保存失敗で旧DB・借用したスコア・現在lookupを保持し、既存ハッシュだけを次に正常更新しても失敗した新ハッシュが混入しないことを確認する。一般の確定Busy・巻戻しはSQLiteの既存境界試験に分担する。 |
| 一括反映の実lookup・購読と通知非干渉 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs) のsnapshot・本人IR・順位cache公開、[`ScoreOwnership`](../../../BeMusicSeeker/Models/Library/BMSLibrary.ScoreOwnership.cs) の通常setter通知 | [`BmsLibraryIrStartupTests`](../../../BeMusicSeeker.Tests/Startup/BmsLibraryIrStartupTests.cs) の `InitializeStartup_NotificationFailurePreservesCommittedSnapshotAndIndependentCache` が実起動・新hash・通知故障・別仕事と次要求、`ScoreSubscriptions_ReloadAndShutdownDetachPreviousScore` が通常setter・購読解除、`GetBMSScores_UnconfiguredSourcePublishesOneEmptySnapshotDespiteNotificationFailure` が取得元未設定のon-demandを分担する。[`BmsLibraryIrServiceTests.DownloadIRData_NotificationFailurePreservesSuccessfulSnapshotAndReturnsOnlyMalformedTarget`](../../../BeMusicSeeker.Tests/Ir/BmsLibraryIrServiceTests.cs) は実Downloadの失敗対象、[`RegularChartNormalLibraryRefreshTests.AttachedScoreSnapshotSource_RefreshesOnlyMatchingRowFromRealScoreSetter`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.cs) は生産された変更キーから一覧行への接続を確認する。 |
| キャッシュの取得要求、確認、取消、成功・失敗件数の通知 | [`RankingCacheDownloadWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Startup/RankingCacheDownloadWorkflowOwner.cs) | [`RankingCacheDownloadWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Startup/RankingCacheDownloadWorkflowOwnerTests.cs) |

## 関連資料

[データと索引](../core/data-and-indexes.md)、[起動](../runtime/startup.md)、[終了](../runtime/shutdown.md)、[LR2楽曲DB](lr2-song-db.md)を参照します。
