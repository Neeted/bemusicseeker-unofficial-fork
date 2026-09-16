# 重複譜面の確認と整理

## 目的と適用範囲

所持しているBMS・BMSONの重複検索、グループ表示、削除、フォルダ統合を定めます。保留入力、導入先の一時状態、プレイリストだけの情報は重複検索の所持主体へ含めません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

「グループ」は同じハッシュを共有するフォルダを連結した集合です。重複ハッシュのない兄弟譜面を含むことと、その譜面へ重複警告を付けることは別です。

## 仕様

### 識別と検索結果

重複はBMSの `hash` とBMSONの `md5` を共通の主ハッシュとして判定します。SHA-256へ切り替えて一致させません。パスまたはMD5のない保存行は所持集合に入らず、検索や警告解除の対象にもなりません。同じ正本パスに複数行がある状態は正常系としません。

`DuplicateChartGroups` は現在の検索結果です。必要時に `SearchDuplicateChartGroups` が全結果を置換し、有効なキャッシュがあれば再検索しません。`OwnedDuplicateChartRowSnapshot` は保存主体の識別、捕捉時のパス・ディレクトリ・MD5と重複ハッシュの索引を保持します。表示用の譜面は必要時に保存主体から生成し、捕捉行に固定しません。

### グループと警告

同じMD5を持つ複数パスを検出し、そのディレクトリをUnion-Findで接続します。AとBがハッシュX、BとCがハッシュYを共有すれば、A・B・Cは同じグループです。

グループには接続したフォルダの兄弟譜面も含め、整理時に同梱内容を確認できるようにします。重複警告を付けるのは重複ハッシュの譜面だけです。見出しは先頭の題名、子ノードはフォルダの一覧で、見出し順に並べます。

BMSの警告は保存主体へ反映し、BMSONは重複表示用の `ChartFile` に投影します。解除は前回実際に警告を付けたBMS集合だけを対象とし、保存集合の全置換後だけ未追跡の警告を次回検索で全体解除します。

### 一覧と操作

| ツリーの選択 | 一覧 |
| --- | --- |
| 重複ルート | 全グループの譜面 |
| グループ | そのグループの譜面 |
| フォルダ | そのパス配下の譜面 |

一覧は `DuplicateFilterSelected` による仮想的な部分集合で、列設定は `DuplicateCustomTableColumnSettings` を使います。フォルダをExplorerで開く操作は、存在しない場合は何もしません。

統合先メニューには同じグループから入力元を除いたフォルダを表示します。`ShowDuplicateFileCheckConfirmMsg` が有効なら確認してから実行します。再生停止、画面更新抑制、重複表示優先の範囲を設定し、[変更セッションの統合契約](mutations.md#フォルダ統合と確定後の保守)へ渡します。DB失敗時の全体補償や、独自の通知経路は持ちません。

入力元を除く所持集合に同じMD5が残る譜面は移動対象から除きます。譜面名だけの衝突は採番し、リソースの衝突は既存の上書き設定に従います。残存物の削除には[確認済みコピーの条件](file-db-consistency.md#入力元の後片付け)を適用します。

### Ctrl+Gと同一フォルダ内の整理

フォルダが一つなら同一フォルダ内の重複削除、二つなら選択元からもう一方への統合、三つ以上なら統合先メニューの展開を行います。

同一フォルダ内ではMD5ごとに一件を残します。更新日時が古いもの、同日時ならファイル名が短いものを優先します。設定に応じて確認し、`RemoveLibraryCharts` からごみ箱へ送ります。削除数は計画件数ではなく、削除APIの成功で確認した対象数です。個別の物理削除失敗後の独立対象は処理できますが、カタログ・必須反映の失敗では次グループへの成功時選択を行いません。

### 更新の集約と後続保守

結果のプロパティと無効化の `DuplicateChartGroupsInvalidationVersion` は分離します。`EnsureDuplicateChartGroupsReady` は同時要求を一つの処理へ集約し、後続は同じ結果を待ちます。

統合中は `BeginDuplicateRefreshPriorityWindow("merge_folder")` により重複表示を優先し、重複画面の表示中に所持集合変更から始まるプレイリスト索引の先行読込みを後回しにします。画面更新後に再予約します。通常一覧、フォルダ、導入、重複の更新抑制は、解除時に保留した通知を流します。

統合後のリソース保守は `DeferOnUpdates` を使います。更新中は索引の差分反映・全体構築を行わず、次の `GetResourceHealthIndexSnapshotForView` が現在の所持対象から一度構築し、続く読取りは同じ結果を再利用します。統合と保守の成否は同じ操作結果に残し、確定後の必須反映失敗を通常成功にしません。

### 自動選択

二フォルダの統合と同一フォルダ内の削除では次のグループ、三フォルダ以上の統合では同じグループの見出しを保存して選択します。次のグループがなければ選びません。

`WaitForDuplicateListUpdateAndSelect` は結果の変更通知、既に完成した状態、待機上限の経路を使い、仮想化された項目を必要に応じて生成して選択します。キーは `DuplicateGroup.Header` であり、同じ見出しが複数あれば最初の一致を選ぶ補助的な動作です。衝突しない恒久IDを持つとは保証しません。

### 起動と性能

重複解析は明示的な要求で行い、起動の必須経路へ入れません。起動中のツリー・一覧表示は表示準備まで遅延します。

分析は所持集合が持つ重複MD5の索引から開始し、全行のハッシュ辞書を毎回作りません。兄弟を含めるためのディレクトリ索引は接続対象だけを扱います。削除、パス変更、追加更新、MD5変更と同じ捕捉結果の置換に索引を同期します。検索結果のグループ自体は全置換であり、グループの差分更新まで行う仕様ではありません。

`SearchDuplicateChartGroups` の重複ハッシュ数・対象行数・接続フォルダ数、`duplicate_refresh_coalesce` の検索・合流、`ui_suppress`、`duplicate_refresh_priority`、`duplicate_group_autoselect` を診断に使います。確定結果と異常報告の回数は共通の変更仕様に従います。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| MD5識別、接続グループ、兄弟譜面、警告 | [`BmsLibraryDuplicateService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryDuplicateService.cs)、[`OwnedDuplicateChartRowSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/OwnedChartCollectionState.cs) | [`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryDuplicateServiceTests.cs) |
| 統合、後続保守、受付解放、失敗と通知 | [`DuplicateMaintenanceWorkflowOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/DuplicateMaintenanceWorkflowOwner.cs) | [`DuplicateMaintenanceWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/DuplicateMaintenanceWorkflowOwnerTests.cs)、[`BmsLibraryDuplicateServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryDuplicateServiceTests.cs) |
| 削除の確認件数とカタログ反映 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.cs) | [`OwnedChartCollectionLibraryMutationTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionLibraryMutationTests.cs)、[`BmsLibraryFolderRenameRefreshTests`](../../../BeMusicSeeker.Tests/BmsLibraryFolderRenameRefreshTests.cs) |
| 終端の分類と多言語表示 | [`FileDbMutationReport`](../../../BeMusicSeeker/ViewModels/MainWindow/FileDbMutationReport.cs) | [`FileDbMutationReportTests`](../../../BeMusicSeeker.Tests/FileDbMutationReportTests.cs)、[`LocalizationResourceParityTests`](../../../BeMusicSeeker.Tests/LocalizationResourceParityTests.cs) |

## 関連資料

[共通譜面モデル](chart-model.md)、[警告](warnings.md)、[一覧表示](../ui/table-view.md)、[利用者向け説明](../../../docs/manual.ja.md)を参照します。
