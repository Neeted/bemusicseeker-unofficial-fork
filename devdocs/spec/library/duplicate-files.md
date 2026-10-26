# 重複譜面の確認と整理

## 目的と適用範囲

所持しているBMS・BMSONの重複検索、グループ表示、削除、フォルダ統合を定めます。保留入力、導入先の一時状態、プレイリストだけの情報は重複検索の所持主体へ含めません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

「グループ」は同じハッシュを共有するフォルダを連結した集合です。重複ハッシュのない兄弟譜面を含むことと、その譜面へ重複警告を付けることは別です。

## 仕様

### 識別と検索結果

重複はBMSの `hash` とBMSONの `md5` を共通の主ハッシュとして判定します。SHA-256へ切り替えて一致させません。パスまたはMD5のない保存行は所持集合に入らず、検索や警告解除の対象にもなりません。同じ正本パスに複数行がある状態は正常系としません。

`DuplicateChartGroups` は現在の検索結果です。必要時に `SearchDuplicateChartGroups` が全結果を置換し、有効なキャッシュがあれば再検索しません。`OwnedDuplicateChartRowSnapshot` は所持token、捕捉時の共通値・パス・ディレクトリ・MD5と重複ハッシュの索引を保持します。捕捉した値は後の現在値適用で変わらず、操作前の対象解決と確認後の固定対象検査は[共通譜面モデル](chart-model.md#所持譜面の識別条件)に従います。

### グループと警告

同じMD5を持つ複数パスを検出し、そのディレクトリをUnion-Findで接続します。AとBがハッシュX、BとCがハッシュYを共有すれば、A・B・Cは同じグループです。

グループには接続したフォルダの兄弟譜面も含め、整理時に同梱内容を確認できるようにします。重複警告を付けるのは重複ハッシュの譜面だけです。見出しは先頭の題名、子ノードはフォルダの一覧で、見出し順に並べます。

BMSの警告は同じ所持tokenの共通現在値へ反映し、BMSONは重複表示用の `ChartFile` に投影します。解除は前回実際に警告を付けたBMSのtoken集合だけを対象とし、所持集合の全置換後だけ未追跡の警告を次回検索で全体解除します。警告だけの反映は集合版を進めません。

### 一覧と操作

| ツリーの選択 | 一覧 |
| --- | --- |
| 重複ルート | 全グループの譜面 |
| グループ | そのグループの譜面 |
| フォルダ | そのパス配下の譜面 |

グループは現在の検索結果内で一意な所属フォルダを値として捕捉し、手動選択と整理後の自動選択で共用します。見出しは表示用であり、同名・空名の別グループを同一視しません。捕捉した所属が現在の結果にない場合は空一覧とし、別グループや全件へ広げません。全件表示は明示的な重複ルート選択だけです。フォルダ配下の選択はグループ全体の選択と区別します。

一覧は `DuplicateFilterSelected` による仮想的な部分集合で、列設定は `DuplicateCustomTableColumnSettings` を使います。フォルダをExplorerで開く操作は、存在しない場合は何もしません。

検索結果の `ChartFile` は操作用の捕捉値として保持します。一覧の構築・再選択では、そのtoken集合の共通現在値を同じカタログ版で一回捕捉し、検索結果が持つ重複固有警告だけを重ねます。退役tokenを別項目へ読み替えません。表示中も、確定したカタログ差分の実対象だけを交換し、表示済み・未表示の入力、検索適否、並び順、健康度を揃えます。BMS・BMSONの重複警告と、固有の兄弟譜面の非警告を維持します。

再選択で既存の並べ替え順を使う前に、捕捉済みの全入力で列・副キーTitle・同値時の元順序を線形検査します。値が変わっても整列済みなら同じ順序を再利用し、崩れた場合だけ再整列します。カタログ確定後・通知配送前の再選択にも同じ判定を使い、新しい値へ古い添字順を組み合わせません。通知の配送順に依存する重複専用のcache失効処理は設けず、新しい世代・表示行の全具体化も追加しません。

共通現在値の更新だけでは全重複解析や全行の具体化を行いません。グループ所属・フォルダ一覧・`Header` は検索結果のまま維持し、グループを再検索したときに置換します。共通値の差分反映で、ツリー所属や自動選択キーまで更新する保証は持ちません。捕捉済みの操作snapshotは変更しません。

統合先メニューには同じグループから入力元を除いたフォルダを表示します。`ShowDuplicateFileCheckConfirmMsg` が有効なら確認してから実行します。再生停止、画面更新抑制、重複表示優先の範囲を設定し、[変更セッションの統合契約](mutations.md#フォルダ統合と確定後の保守)へ渡します。DB失敗時の全体補償や、独自の通知経路は持ちません。

停止後のモデル準備では、元フォルダが確定済み利用者BMSルート本体または空の登録子を含む祖先なら統合を拒否します。元先の実体・DB・登録を保持し、登録の解除・付替えを行いません。登録を含まない通常作品は、登録配下や同prefixの兄弟でも通常の統合条件で処理できます。

入力元を除く所持集合に同じMD5が残る譜面は移動対象から除きます。譜面名だけの衝突は採番し、リソースの衝突は既存の上書き設定に従います。残存物の削除には[確認済みコピーの条件](file-db-consistency.md#入力元の後片付け)を適用します。

### Ctrl+Gと同一フォルダ内の整理

フォルダが一つなら同一フォルダ内の重複削除、二つなら選択元からもう一方への統合、三つ以上なら統合先メニューの展開を行います。

同一フォルダ内ではMD5ごとに一件を残します。更新日時が古いもの、同日時ならファイル名が短いものを優先します。設定に応じて確認し、`RemoveLibraryCharts` からごみ箱へ送ります。削除数は計画件数ではなく、削除APIの成功で確認した対象数です。個別の物理削除失敗後の独立対象は処理できますが、カタログ・必須反映の失敗では次グループへの成功時選択を行いません。

### 更新の集約と後続保守

結果のプロパティと無効化の `DuplicateChartGroupsInvalidationVersion` は分離します。`EnsureDuplicateChartGroupsReady` は同時要求を一つの処理へ集約し、後続は同じ結果を待ちます。

構築済みのプレイリスト候補索引は、所持項目の確定差分で維持します。無関係な現在値更新から先行読込みを開始せず、初回の必要な準備とライブラリ交換時の取消は索引の所有者が管理します。通常一覧、フォルダ、導入、重複の更新抑制は、解除時に保留した通知を流します。

統合後のリソース保守は `DeferOnUpdates` を使います。更新中は索引の差分反映・全体構築を行わず、次の `GetResourceHealthIndexSnapshotForView` が現在の所持対象から一度構築し、続く読取りは同じ結果を再利用します。統合と保守の成否は同じ操作結果に残し、確定後の必須反映失敗を通常成功にしません。

### 自動選択

二フォルダの統合と同一フォルダ内の削除では、操作前に現在の順序から次のグループの所属フォルダを捕捉します。三フォルダ以上の統合では移動先フォルダを捕捉し、元フォルダの消失後も存続グループを解決します。次のグループがなければ自動選択しません。見出しや処理後の隣接位置へ読み替えません。

`ApplyDuplicateMaintenanceSelectionAsync` は結果の変更通知、既に完成した状態、既存の待機上限を使い、捕捉した所属から現在のグループを解決して仮想化された項目を必要に応じて生成・選択します。対象が現在結果から消失した場合は左の重複選択と旧行選択を解除して空一覧にし、同名の別グループやルートへ切り替えません。この表示解決のために重複全解析を追加しません。識別は当該検索結果・保守操作内だけで使い、恒久IDや選択永続化を持ちません。

### 起動と性能

重複解析は明示的な要求で行い、起動の必須経路へ入れません。起動中のツリー・一覧表示は表示準備まで遅延します。

分析は所持集合が持つ重複MD5の索引から開始し、全行のハッシュ辞書を毎回作りません。兄弟を含めるためのディレクトリ索引は接続対象だけを扱います。削除、パス変更、追加更新、MD5変更と同じ捕捉結果の置換に索引を同期します。検索結果のグループ自体は全置換であり、グループの差分更新まで行う仕様ではありません。

警告対象は捕捉した重複bucketのlookup hashへ所属する譜面で判断します。増分snapshotから別々に列挙した行の参照同一性を要求せず、接続先の固有な兄弟譜面には重複警告を付けません。

`SearchDuplicateChartGroups` の重複ハッシュ数・対象行数・接続フォルダ数、`duplicate_refresh_coalesce` の検索・合流、`ui_suppress`、`duplicate_refresh_priority`、`duplicate_group_autoselect` を診断に使います。確定結果と異常報告の回数は共通の変更仕様に従います。

## 実装とテストの対応

`BmsLibraryDuplicateServiceTests.MergeChartDirectory_TwoWarmOperationsKeepIndexesCurrentWithoutFullRebuild` は実検索・再利用と2回の実統合で、フォルダ集合4→3→2、グループ譜面6→5→4、重複警告4→3→2、所持・DB総数22→21→20を確認する。統合後も残る1グループ、移動先の固有hash参照、移動元消失、既存重複実体のbytesと背景16行を保つ。旧snapshotと温めた索引を全再構築しない保証を同じケースで維持する。

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 登録本体・空の登録子を含む元の準備拒否と通常作品の統合 | [`LibraryMutationOwner.MergeChartDirectory`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.Merge.cs)、`BmsLibraryDuplicateMaintenanceStore` | [`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) の `MergeChartDirectory_RejectsRegisteredSourceScope` と `MergeChartDirectory_AllowsOrdinaryChildAndSimilarSibling` が元先のFS・DB・登録・新library読込みを確認する。[`DuplicateMaintenanceWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) の `RunFolderMergeAsync_RegisteredScopeRefusalReleasesAdmissionForNextMerge` は本番storeを通る停止後拒否と次要求の受付を確認する。改名の保護条件と保証の分担は[変更仕様](mutations.md#実装とテストの対応)を参照する。 |
| MD5識別、接続グループ、兄弟譜面、警告 | [`BmsLibraryDuplicateService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Maintenance/BmsLibraryDuplicateService.cs)、[`OwnedDuplicateChartRowSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Catalog/OwnedChartCollectionState.cs) | [`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) |
| 同版の共通現在値捕捉、確定差分、固有警告・未表示行・検索・順序・健康度・再選択 | [`DuplicateGroup.ProjectCurrentChart`](../../../BeMusicSeeker/Models/Library/DuplicateGroup.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs)、[`ChartSubsetReferenceRows`](../../../BeMusicSeeker/ViewModels/ChartList/ChartSubsetReferenceRows.cs)、[`ChartListOrder.IsSortedFor`](../../../BeMusicSeeker/ViewModels/ChartList/ChartListOrder.cs) | [`RegularChartNormalLibraryRefreshTests.Duplicates`](../../../BeMusicSeeker.Tests/ChartList/RegularChartNormalLibraryRefreshTests.Duplicates.cs) の `DuplicateCurrentValues_RealEncodingAndRescanPatchOnlyAffectedRows` は、実重複検索から一件の文字コード変更・BMS/BMSONの再調査・通知配送・再選択を通し、配送前の再選択を含め検索加入/離脱・Title/健康度順・旧snapshot不変と対象だけの仕事量を確認する。`ChartListVirtualViewTests.CapturedOrder_CurrentValuesRespectDirectionTitleAndStableTies` は昇降順の副キー・元順序・空入力と整列済み値変更を確認する。解析条件とwarm索引は `BmsLibraryDuplicateServiceTests` が分担する。 |
| 同名・空名、明示root、folder配下、未解決、行選択の識別 | [`DuplicateViewContext`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateViewContext.cs)、[`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/ChartList/RegularChartListOwner.cs) | [`RegularChartViewBuildAndOrderingTests`](../../../BeMusicSeeker.Tests/ChartList/RegularChartViewBuildAndOrderingTests.cs) の `RegularEntry_SelectsDuplicateSubsetFromLibraryOwner` と `RegularEntry_DuplicateFolderContextSelectsOnlyMatchingCharts` が実ownerからのpath集合、選択scope、子孫と同prefix兄弟、捕捉後の元list変更を確認する。 |
| 操作前の次対象・三以上の存続宛先、実左Treeまでの接続と消失時解除 | [`MaintenanceTreeViewModel`](../../../BeMusicSeeker/ViewModels/Maintenance/MaintenanceTreeViewModel.cs)、`DuplicateMaintenanceWorkflowOwner`、`MainWindow.ApplyDuplicateMaintenanceSelectionAsync` | [`MaintenanceTreeViewModelTests`](../../../BeMusicSeeker.Tests/Maintenance/MaintenanceTreeViewModelTests.cs) と [`DuplicateMaintenanceWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs) が捕捉値と現在結果の所属を確認する。[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `DuplicateSelections_ActualMergeSelectsCapturedNextGroupThroughCompiledTree` は実検索・標準composition・本番store・SQLite/FSからauto左選択までを通し、`DuplicateSelections_DisappearedTargetClearsTreeAndRowsWithoutHeaderFallback` は対象消失時の左無選択・空一覧・旧行選択解除を分担する。 |
| 統合、後続保守、受付解放、失敗と通知 | [`DuplicateMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Maintenance/DuplicateMaintenanceWorkflowOwner.cs) | [`DuplicateMaintenanceWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Maintenance/DuplicateMaintenanceWorkflowOwnerTests.cs)、[`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryDuplicateServiceTests.cs) |
| 削除の確認件数とカタログ反映 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/Library/BMSLibrary.LibraryMutationOwner.cs) | [`OwnedChartCollectionLibraryMutationTests`](../../../BeMusicSeeker.Tests/Catalog/OwnedChartCollectionLibraryMutationTests.cs)、[`BmsLibraryFolderRenameRefreshTests`](../../../BeMusicSeeker.Tests/Maintenance/BmsLibraryFolderRenameRefreshTests.cs) |
| 終端の分類と多言語表示 | [`FileDbMutationReport`](../../../BeMusicSeeker/ViewModels/ChartOperations/FileDbMutationReport.cs) | [`FileDbMutationReportTests`](../../../BeMusicSeeker.Tests/ChartOperations/FileDbMutationReportTests.cs)、[`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/Localization/LocalizationResourceParityTests.cs) |

## 関連資料

[共通譜面モデル](chart-model.md)、[警告](warnings.md)、[一覧表示](../ui/table-view.md)、[利用者向け説明](../../../docs/manual.ja.md)を参照します。
