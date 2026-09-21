# 導入先の推定

## 目的と適用範囲

保留パッケージや選択譜面について、要求するリソースを最もよく満たす既存フォルダを推定する仕様です。入力、候補抽出、順位、確信度、画面への適用を定めます。実際の導入の受付・保存・失敗は[変更操作](mutations.md)に従います。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

本資料の「候補」は既存の譜面フォルダです。「充足率」は要求リソースのうち導入後に利用できる割合、「自身の所有分」は子孫から集約された分を除いたフォルダ自身のリソースを意味します。

## 仕様

### 入力とリソースキー

対象はパッケージまたは単独の譜面です。複数譜面のパッケージでは `ChartResourceSnapshot` の参照を和集合にし、音声・画像・動画・任意画像を分けます。

キーは拡張子を除いた譜面相対のパスです。`foo.wav` と `./foo.wav` は `foo`、`sound/./foo.wav` は `sound/foo`、`sound/foo.v2.wav` は `sound/foo.v2` になります。`foo` と `sound/foo` は別であり、ファイル名だけに戻して一致させません。既に正規化した参照をパッケージ集約時に再正規化しません。

親参照 `..` は未対応の参照として保持し、索引のキーへ入れません。対象に一件でも含まれる場合は推定せず、`UnsupportedResourcePath` を表示します。

### 入力元に同梱されたリソース

通常の導入推定では、候補のリソースとパッケージが持ち込む `BundledResources` の和集合を評価します。統合先と再導入先の修正では候補のリソースだけを評価します。

ディレクトリ単位のパッケージは、そのルートだけを再帰的に調べます。一ルートあたりの訪問上限は50000件です。上限を越えた時点で止め、部分結果を推定に使わず `SourceSurfaceScanLimitExceeded` を付けます。複数パッケージの合計件数ではありません。全件を先に取得する方式ではなく、訪問中に上限を確認します。

単一BMS・BMSONと単独譜面では、親をパッケージ範囲として列挙しません。`SourceDirectory` は入力元の候補除外や診断に使いますが、同梱リソースと入力元候補の集合は別の空集合です。走査時間、件数、ハッシュ生成時間、訪問件数は0、走査方式は空、上限超過はfalseとします。相対参照があることを親走査の理由にしません。

### ライブラリ側の索引

候補は `DirectoryResourceLookupCache.Keys` に含まれる譜面フォルダです。リソースだけの下位フォルダは候補にせず、入力元自身も除きます。

照合の正本は音声・画像・動画別の相対キーと逆引きです。ファイル名だけの集合や全種類を混ぜた集合を、候補抽出・最終照合・音声条件の代わりに使いません。索引がない場合は推定不能とし、全ライブラリの走査に切り替えません。起動時走査を無効にした設定では、推定できない場合があります。

### 入口ごとの候補

| 入口 | 候補と選び方 |
| --- | --- |
| 通常推定 | 未所持譜面を含むパッケージについて外部の譜面フォルダを探す。既所持だけなら通常推定しない |
| 既所持・未所持が混在 | 既所持ハッシュの実配置先を数え、単独最多ならそのフォルダを使う。同点ならその候補集合だけをリソース・基本情報で評価する |
| 統合先 | 全対象の既所持ハッシュ一致数が単独最多なら採用する。同点または一致なしなら、候補自身のリソースだけで評価する |
| 再導入先修正 | 現在の配置先を除いた既存フォルダを評価する。現在配置の状態は改善判定の基準だけに使う |

混在パッケージでは、配置先候補が解決できない場合、または候補限定の評価に必要な索引がない場合、通常推定へ切り替えず `InstalledDestinationResolveFailed` とします。候補の基本情報まで同等なら、フォルダ内の一意な主ハッシュ数を補助条件とし、単独最多なら通常の確定候補にできます。

それでも複数候補が残る場合、`AutoApplyAmbiguousInstallDestination` が有効で選択候補の題名・アーティストの根拠が `Strong` なら、自動設定と候補表示を行い `InstalledDestinationAutoAppliedAmbiguous` を残します。設定無効または根拠が弱い場合は導入先を空にし、`InstalledDestinationAmbiguous` と候補を表示します。

### 候補の絞込み

まずカテゴリ別の逆引きで参照キーに合う候補を集めます。候補が0件でも全ライブラリへ戻らず、`no_viable_destination_below_threshold` とします。

音声参照が二件以上なら候補自身で二件以上、一件なら一件以上の一致を要求します。音声参照がなければこの条件はありません。さらに、実際に使える音声の充足率が `innerWavHealthThreshold` を超える見込みがない候補を除きます。通常導入は同梱分を含め、統合・再導入は候補だけで判断します。

### 評価値と順位

カテゴリごとに一致数 `Matched`、充足率 `Health`、候補側件数 `CandidateCount`、適合率 `Precision`、和集合に対する一致率 `Jaccard` を求めます。`Health` は一致数を要求数で割った値です。

有効候補の主な充足率は、音声参照があれば音声、なければ画像・動画・任意画像の最大値です。この値が `innerWavHealthThreshold` を超えることを要求します。

順位は音声、画像、動画、任意画像の順で比較し、各カテゴリ内は充足率、一致数、Jaccard、適合率の順です。最後にディレクトリのパスで順序を安定させます。比率は表示用に丸めず、元の比で比較します。

### 題名・アーティストによる同順位の解決

リソースの評価値が先頭候補と等しい有効候補だけを、基本情報で比較します。通常は最大3候補です。混在パッケージの候補限定評価で一意ハッシュ数の取得処理が渡された場合だけ、同等候補全体を対象にします。画面の候補表示はいずれも上位3件までです。

比較順は、題名とアーティストの組の完全一致、題名一致、アーティスト一致、題名の類似度、組の一致を裏付ける数、題名を裏付ける数、アーティストを裏付ける数です。混在パッケージに限り、それらが全て同等のときフォルダ内の一意な主ハッシュ数を使い、最後は元のリソース順位とパス順に戻します。譜面数で基本情報の明確な優劣を覆しません。

基本情報で一意に分かれた場合は `metadata_tiebreak_distinct`、一意ハッシュ数で分かれた場合は `directory_hash_count_tiebreak_distinct` として確信度を高くできます。

### 祖先候補の抑制

子孫のリソースが祖先の譜面フォルダからも見える場合、同等以上の評価を持つ子孫があり、祖先自身の一致数が0、子孫自身の一致数が1以上なら祖先を抑制します。親子関係のある候補だけを調べ、自身の所有分の照合も必要なときだけ行います。

### 確信度と画面への適用

| 結果 | 適用 |
| --- | --- |
| 有効候補なし | `Confidence=High`、導入先null、自動適用false。候補がないという判定の確信度であり、導入成功を意味しない |
| 一意な有効候補で基本情報も妥当 | 高い確信度で自動設定する |
| リソース同等だが基本情報または許可された一意ハッシュ数で差が付く | 高い確信度にできる |
| 解消しない複数候補、基本情報の不一致 | 低い確信度と候補を返す |
| 再導入で現在配置より改善しない | 低い確信度と候補を返す |

`INSTL DST` は、導入先があり `ShouldAutoApplyDestination=true` の場合だけ自動設定します。題名・アーティスト列、候補、警告も同じ結果から更新します。保留のリソース状態は代表譜面だけでなく全 `PackageChartEntry` に投影し、既所持・単一ファイル・入れ子の警告と両立させます。

保留の推定・手動変更・クリアと、導入済み譜面の再導入先推定・クリアは、操作結果から一覧へ反映する共通経路を使います。変更した譜面と一覧への影響を一つの操作完了通知で渡し、画面切替なしで導入先・代表情報・候補・推定警告を更新します。クリアは明示的な空値として反映し、次に表示行から作る要求にも古い導入先を残しません。

保留パッケージでは項目の状態を正本とし、後続の項目変更を古い一時状態で覆いません。通知・投影の経路が異なる通常のライブラリ行と対象集合行も同じ表示契約を満たします。表示行のキャッシュと並べ替え・絞込みの更新は[一覧表示](../ui/table-view.md#絞込みと画面更新)に従います。

### 自動推定と並列処理

保留に入ったパッケージは一括処理の単位で自動推定します。入力元のリソースが十分なディレクトリパッケージは保留に残して自動推定を省略できますが、手動推定にはこの抑制を適用しません。

一件の手動推定では候補評価を並列化し、`asParallel=true` の並列数は `max(1, CPU数 - 1)`、falseなら1です。複数パッケージの一括推定は外側を並列化し、各候補評価は1にして二重並列を避けます。自動推定の外側並列数は `PendingInstallEstimateMaxParallelPackages`、0ならCPU数から決めます。手動の複数パッケージも同じ一括経路を使います。

全選択譜面が保留パッケージに属する場合はパッケージ単位へまとめられます。単独譜面が混じる場合と再導入先修正は外側を逐次実行し、内側の候補を並列評価します。入力元の基準判定で作った参照集合は同じ対象の評価へ再利用します。

手動と自動は `RunPendingEstimateExclusive` を通り、別の推定処理が警告・導入先・進捗を同時に更新しません。追加の自動推定受付と手動操作の競合には[安全性改善の未完了事項](../../plan/safety-improvements-plan.md)が残ります。受理済みの処理を失っても手動でやり直せばよい、という契約には変更しません。

### 評価結果の最新性

`PendingInstallEstimateCurrentnessStamp` は、リソース索引の世代、所持集合の版、導入済み配置索引の世代、ハッシュ変更の世代をまとめた照合値です。入力の捕捉と世代の公開、適用直前の照合と項目への反映は、同じ短い同期境界で守ります。古い所持・未所持の分割を新しい世代番号だけで正当化しません。

準備済みの一括入力は照合値が一致する場合だけ再利用します。評価から反映までに変化した場合は、同じ段階で一回だけ、現在の分割と評価入力を一緒に捕捉して再評価します。再度変化した対象は反映せずスキップします。元の対象に設定した検索中状態は、成功・スキップ・取消・例外のいずれでも解除します。

### 表示用の再グループ化

発見時に `RegroupEligibleSourceDirectories` へ記録した入力元だけを、一括推定の末尾で再グループ化できます。同じ入力元の保留が二件以上あり、既にディレクトリパッケージがなく、遅延推定の対象が混在しないことを条件にします。

既所持譜面はハッシュから実配置先、未所持譜面は設定済みの推定先を使い、全対象が一つの導入先へ揃う場合だけまとめます。分割・解決不能なら変更しません。再グループ化後の導入先と題名・アーティストは未所持の項目だけへ設定し、全て既所持なら導入先は空です。警告とリソース状態は新しいパッケージ単位で初期化します。

### 推定先への実導入

短いモデルの保護範囲で現在の保留と照合し、入力順に重複を除き、有効な項目の導入先が全て空の候補を除きます。保護範囲を出てから適格な候補だけを捕捉し、除外した入力・行・導入先・警告は変更しません。

分類と物理処理は入力順です。所持判定には開始時の主ハッシュと `SessionSuccessOwnershipOverlay` の先行成功を使います。同一パッケージ内の同じハッシュを重複処理せず、既所持を除いた新規対象の導入先が空または複数なら保留を維持します。

実導入は一つの変更セッションへ集約します。全譜面が既所持でも、リソースだけ・後片付けだけの必要な変更は残します。追加BMS・BMSONと、リソースの移動先にある既所持譜面を末尾の保守対象にします。何も移動しない後片付けだけの成功では保守しません。

既存のリソース状態索引が有効なら対象だけを差分更新し、索引が未構築・無効なら、この後処理だけを理由に全体再構築しません。入力元の保全、確認済み残存物の削除、終端の報告は[ファイルとDBの整合](file-db-consistency.md)に従います。

### 診断

`estimate_install start` の評価方式、候補数、絞込み後件数、照合時間、祖先抑制、確信度と理由を確認します。同順位がある場合は `metadata_frontier` と `metadata_tiebreak`、一括推定は `packageDegree` を併せて確認します。

実導入の対象別記録は物理処理の単位であり、DB確定回数ではありません。`reverse_lookup_incremental_update`、`maintenance_update`、`resource_health_index_delta`、`chart_info_inline_install` で操作末尾の集約を確認します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 相対キー、カテゴリ、候補抽出、順位と確信度 | [`BmsLibraryInstallEstimationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryInstallEstimationService.cs)、[`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) |
| 入力元の走査上限、単一ファイルとディレクトリの区別 | [`PackageInstallEstimationSnapshotBuilder`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PackageInstallEstimationSnapshot.cs) | [`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs)、[`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs) |
| 自動推定の受付、並列評価、結果の最新性 | [`PendingInstallEstimateQueueProcessor`](../../../BeMusicSeeker/Models/Install/PendingInstallEstimateQueueProcessor.cs)、[`BmsLibraryInstallEstimationService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryInstallEstimationService.cs) | [`PendingInstallEstimateQueueProcessorTests`](../../../BeMusicSeeker.Tests/Install/PendingInstallEstimateQueueProcessorTests.cs)、[`BmsLibraryInstallEstimationServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryInstallEstimationServiceTests.cs) |
| 現在の保留との照合、先行成功、保存と必須反映の集約 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPackageInstallServiceTests.cs)、[`BmsLibraryLr2SongDbSyncTests`](../../../BeMusicSeeker.Tests/Lr2/BmsLibraryLr2SongDbSyncTests.cs) |
| 画面の終端、異常報告と後続の失敗 | [`PendingPackageWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、[`MainWindowPendingPackageMutationViewTerminal`](../../../BeMusicSeeker/Views/MainWindow/MainWindowFeatureTerminals.cs) | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs)、[`MainWindowPendingPackageMutationViewTerminalTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPendingPackageMutationViewTerminalTests.cs)、[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) |
| 導入先変更の共通完了通知、即時反映と明示的クリア | [`PendingPackageWorkflowOwner`](../../../BeMusicSeeker/ViewModels/Install/PendingPackageWorkflowOwner.cs)、[`MainChartRowProjectionOwner`](../../../BeMusicSeeker/ViewModels/ChartList/MainChartRowProjectionOwner.cs) | [`PendingPackageWorkflowOwnerTests`](../../../BeMusicSeeker.Tests/Install/PendingPackageWorkflowOwnerTests.cs) の `SearchPendingAsync_LooseTargetPublishesTransientProjectionAfterGateRelease` は排他解放後の単一通知を確認する。[`MainWindowPackageMaintenanceWpfTests`](../../../BeMusicSeeker.Tests/MainWindow/MainWindowPackageMaintenanceWpfTests.cs) の `CorrectInstallDestinationSearchAndClearPreserveFullScanPresentation` は全件確認の実操作から候補・警告の更新とクリア、Rows・選択保持、次要求の現在値を確認する。 |
| 世代変化に伴う分割の作り直し、検索中状態の解除、再グループ化 | [`BMSLibrary`](../../../BeMusicSeeker/Models/Library/BMSLibrary.cs)、[`PendingEstimateSourceBatchSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/PendingEstimateSourceBatchSnapshot.cs) | [`BmsLibraryPendingPackageRegroupTests`](../../../BeMusicSeeker.Tests/Install/BmsLibraryPendingPackageRegroupTests.cs) |
| 導入済み対象だけのリソース上書きに必要な実配置の一致 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Install/BmsLibraryPackageInstallService.cs) | [`InstalledOnlyResourceOverwriteValidationTests`](../../../BeMusicSeeker.Tests/Install/InstalledOnlyResourceOverwriteValidationTests.cs) |
| 評価入力の変更不能性とリソース相対キー | [`ChartResourceSnapshot`](../../../BeMusicSeeker/Models/BmsLibraryInternal/Resources/ChartResourceSnapshot.cs) | [`ChartResourceSnapshotTests`](../../../BeMusicSeeker.Tests/Resources/ChartResourceSnapshotTests.cs) |

## 関連資料

[データと索引](../core/data-and-indexes.md)、[警告](warnings.md)、[変更操作](mutations.md)、[共通並行処理](../core/workflow-concurrency.md)を参照します。
