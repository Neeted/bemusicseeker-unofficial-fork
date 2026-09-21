# 譜面情報の保存と最新性

## 目的と適用範囲

`chart_info` の識別条件、読込み、補完解析、保存と公開を定めます。解析の値そのものは[解析互換性](parser-compatibility.md)、入力の読取りは[譜面ファイルの読取り](chart-file-reading.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 識別と保存主体

BMSは `song.hash` のMD5と `chart_digest_map` のSHA-256を結び、`chart_info.sha256` を参照します。BMSONは `bmson_song.sha256` を優先し、`chart_digest_map` を作りません。

`chart_info` は譜面メタデータのキャッシュでもあるため、現在のカタログに所持主体がない行を、読込みや補完解析だけを理由に削除しません。BMSとBMSONの保存主体に実行中の `ChartInfo` オブジェクトを付けず、表示はセッション内の索引と投影の提供元から解決します。

### 最新性の判定

同じ譜面の判定優先順位は次のとおりです。

| 優先順位 | 条件 | 結果 |
| --- | --- | --- |
| 1 | 識別条件が一致し、解析器の版が現在の `chart_info` | 既存情報を再利用する |
| 2 | 解析器の版が現在で、記録時の時間上限が現在の上限以上である解析失敗 | 再解析しない |
| 3 | いずれもない | 解析候補にする |

現在の情報と現在の失敗が併存する場合は情報を優先します。古い解析器の結果や、現在より短い制限時間で記録した失敗は、再解析を省く根拠にしません。

判定の正本は `ChartInfoBuildService.EvaluateSnapshot` です。呼出元が単一の `ChartFileSnapshot`、所持主体の識別条件、事前取得した情報と失敗を渡します。評価処理自身はファイルやDBを読み書きせず、既存情報の適用、失敗による省略、解析の成功・失敗、入力取得不能のいずれかを返します。導入時解析、全件補完、LR2同期で解析器・優先順位・失敗メッセージの整形を分岐しません。

### 導入後の対象限定検索

パスから導入後の情報を生成する場合、実際に読めたスナップショットのMD5だけで解析失敗を検索します。発見時のMD5は、その後に内容が変わっている可能性があるため根拠にしません。対象が空ならDBを開かず、問い合わせもしません。

検索はパラメーター付きのMD5集合を分割して行い、失敗表を全件取得してから絞りません。全所持主体を照合する起動時の読込みとは区別します。現在の情報を再利用する場合も、新しい保存主体に必要な適用と確定後の索引・通知は省きません。

### 保存する列と公開順序

導入時解析と全件補完は、保存対象と譜面情報を `CatalogChartInfoStorageWriteRequest` へまとめ、`CatalogMutationOwner.ApplyChartInfoStorageWrite` から一つのトランザクションへ渡します。

新規・更新ファイルは基本解析と詳細解析を組み合わせた保存行を生成できます。既所持譜面の全件補完は行全体を再生成せず、パスと正規化MD5が一致する既存BMSの次の9列だけを更新します。

`level`、`difficulty`、`maxbpm`、`minbpm`、`bga`、`exlevel`、`longnote`、`random`、`karinotes`

基本列、`mode`、`judge`、`favorite`、`tag`、`adddate` は既存値を維持します。欠落行や識別不一致は件数と上限付きの例を診断し、暗黙に `song` 行を挿入しません。BMSONは情報とセッション索引を更新しますが、LR2の `song` 行やハッシュ対応表を作りません。

ハッシュ不足の候補が既存の現在情報を再利用した場合も保存用の適用結果を作ります。同じMD5を持つ複数BMSは一回の読取り・評価結果を全対象へ反映します。

公開順序は、DB確定、DBで一致した保存主体の更新、ハッシュ依存索引、譜面情報のセッション索引、警告・ハッシュ通知の順です。確定前や確定失敗時に部分公開しません。ハッシュの変更内容は一回作り、索引更新と公開に共用します。

### 起動時の読込みと同一起動内の省略

LR2連携と単独動作は、同じ読み取り専用の処理で実在する情報・失敗を取得し、所持譜面と照合します。この段階でスキーマを作成・修復せず、導入可能になるまでを同期的に待たせません。

`lr2_song_db_sync_status` はLR2の生成行の同期状態です。同期済み、またはファイル差分なしという状態は、`chart_info` の存在と完全性を証明しません。どちらの動作モードでも、現在の情報も失敗もない所持譜面は補完候補になります。

実データの照合で全対象が現在の情報または失敗と判定された場合は、`ChartInfoHydrationAllCurrentSnapshot` を記録できます。所持集合、BMS・BMSONの保存行、解析器の版、時間上限が変わらない同一起動内だけ、候補集計と全件補完を `hydration_all_current` として省略します。LR2の同期状態からこの結果を合成しません。

解析失敗の明示削除は、失敗行と警告を更新しますが、同一起動内の上記記録を無効化せず、即時の再読込みや補完を予約しません。現在の情報がない譜面は次回起動の実データ照合で候補へ戻ります。同一起動中に既に進行中の読込みが削除前後のどちらを観測するかは保証しません。

### 失敗

DBの読込み失敗では「全て最新」を合成しません。解析時間超過、例外、最終的な解析不能には共通の結果分類と長さを制限したメッセージを使います。ハッシュを計算できた失敗は保存候補とし、成功時は同じMD5の失敗を削除する候補とします。保存と削除のトランザクションは呼出元の保存処理が管理します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| スキーマ、メタデータの入出力、起動時取込み | [`CatalogChartInfoOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs)、[`ChartInfoMetadataBundleStartupImporter`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoMetadataBundleStartupImporter.cs) | [`ChartInfoMetadataSchemaExportImportTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoMetadataSchemaExportImportTests.cs) の `ImportChartInfoMetadataBundle_ImportsMissingAndStaleRowsAndClearsFailures`（同一bundle再投入を含む）、`StartupImporter_PrefersDatabaseBundleOverArchive`、`StartupImporter_ImportsArchiveBundleAndMovesImportedArchive`（抽出済みcacheの移動・再配置・一回だけの抽出） |
| 解析結果、時間上限、互換入力 | [`ChartInfoParser`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoParser.cs)、[`ChartInfoBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoBuildService.cs) | [`ChartInfoParserBehaviorTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoParserBehaviorTests.cs) |
| 全件補完の9列更新、同じハッシュの複数主体、確定失敗 | [`CatalogMutationOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogMutationOwner.cs)、[`ChartInfoBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoBuildService.cs) | [`ChartInfoBackfillStorageTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoBackfillStorageTests.cs) の `BackfillChartInfos_ParsesMissingRowsSkipsCurrentRowsAndReparsesStaleRows`、`BackfillChartInfos_UpdatesOnlyChartInfoSongProjectionAndPreservesOtherColumns`、`BackfillChartInfos_ReusedCurrentRowProjectsSongColumnsForMissingDigestCandidate`、`BackfillChartInfos_GroupsDuplicateMissingSha256TargetsByMd5`（同じMD5を一回読取りで反映）、`BackfillChartInfos_TransactionFailureDoesNotPublishCanonicalDigestSongOrIndex`、`BackfillChartInfos_LaterChunkFailureKeepsEarlierPublicationAndDoesNotPublishFailedChunk` |
| 読取り専用の照合、対象MD5の検索、既存情報の適用、公開順序 | [`ChartInfoInlineBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoInlineBuildService.cs)、[`BmsLibraryDbGateway`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDbGateway.cs)、[`CatalogChartInfoOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/CatalogChartInfoOwner.cs) | [`ChartInfoInlineHydrationTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInlineHydrationTests.cs) |
| 導入後の実ハッシュ、警告削除、競合と次回の再評価 | [`ChartInfoInlineBuildService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/ChartInfo/ChartInfoInlineBuildService.cs)、[`ChartInfoParseFailureRemovalWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/ChartInfoParseFailureRemovalWorkflowOwner.cs) | [`ChartInfoInstallFailureRetryTests`](../../../BeMusicSeeker.Tests/ChartInfo/ChartInfoInstallFailureRetryTests.cs) の `InstallChartPackages_UsesInstalledSnapshotMd5ForFailureLookup`、`BackfillChartInfos_ParseFailureStillPersistsDigest`、`BackfillChartInfos_SkipsCurrentPersistedParseFailure`、`BackfillChartInfos_ReparsesStalePersistedParseFailureAndUpdatesRecord`、`BackfillChartInfos_ReparsesShorterTimeoutFailure`、`RemoveChartInfoParseFailuresByMd5_RetriesOnNextStartupInBothModes`、`BackfillChartInfos_CommitsInChunksAndLogsPhaseBoundaries`、`RetryIfLockedOrBusy_RetriesRealDatabaseContention`。実MD5のBMS解析失敗、確定後の警告、次回起動の再評価を確認する。 |

## 関連資料

[譜面ファイルの読取り](chart-file-reading.md)、[変更操作](mutations.md)、[データと索引](../core/data-and-indexes.md)、[テスト実行](../development/testing.md)を参照します。
