# BMS・BMSONの共通モデル

## 目的と適用範囲

BMSとBMSONを同じ画面・操作から扱うためのモデルと、形式ごとに維持する保存・解析の境界を定めます。共通化を理由に、既存のLR2データベース、プレイリストの保存形式、設定キー、利用者向けの文言は変更しません。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

| 用語 | 意味 |
| --- | --- |
| 保存主体 | DB行に対応する現在のオブジェクト。BMSは `BMSFile`、BMSONは `bmson_song` です。 |
| 投影 | 保存主体や項目から、その用途に必要な読取りモデルを作ること。 |
| 所属のない譜面 | 保留パッケージ項目として解決されない操作対象。コードでは `LooseEntries` などで表します。 |

## 仕様

### 保存するデータとアプリ内の譜面

| 対象 | 正本と責務 |
| --- | --- |
| BMSの保存行 | `BMSFile : LR2SongDB.song`。LR2の `song` / `folder`、BMS解析結果、スコア行との接続、文字コード検査など形式固有の処理を持ちます。 |
| BMSONの保存行 | `LR2SongDBExtended.bmson_song`。アプリ独自の `bmson_song` に保存します。LR2の `song` へ互換行を作りません。 |
| 譜面の共通表現 | `ChartFile`。種類、実パス、MD5・SHA-256、表示値、保存主体を持つ読取りモデルです。DB行そのものではありません。 |
| 所持譜面集合 | `OwnedChartCollectionState`。種類・パス・ハッシュ・保存主体の対応と用途別の索引を管理します。形式共通の検索・変更はこの集合を入口にします。 |
| パッケージ内の譜面 | `PackageChartEntry`。`ChartFile` と、導入先・候補・警告・検索状態を持ちます。 |
| 画面で一時的に重ねる値 | `ChartFileTransientState`。導入先、字幕、警告、リソース健全性、文字コードなどを保持します。永続化の正本にはしません。 |

`ChartFile` の生成は `ChartFileProjection` に集約します。保存主体を必要とする処理だけが `GetBmsStorageOwner()` / `GetBmsonStorageOwner()` を呼びます。BMSONを `BMSFile` の派生型へ変換して共通処理に渡す経路は設けません。

`BMSFiles` / `BmsonSongs` は保存行の読取り専用ビューです。DB読込み、明示的な全置換、形式固有の解析・保存では直接扱えますが、共通処理のために両一覧を繰り返し結合しません。内部の追加・削除・移転は変更窓口を通し、外部からの全置換だけを全体の無効化境界とします。格納順・世代・差分反映の詳細は[データと索引](../core/data-and-indexes.md)を参照します。

### 所持譜面の識別条件

所持譜面は実パスとMD5を必須とし、正規化した絶対パスはBMS・BMSONを通じて一意です。同じMD5を持つ別配置は別の所持主体として数えます。

DB読込みや外部からの全置換では、パスまたはMD5のない行を所持集合から除外します。同一パスの重複は、BMS、BMSONの順に入力を読み、先の行を採用して除外件数を記録します。内部の追加・移転・ハッシュ更新がこの条件を破った場合は、単なる再構築で隠さず異常として検出します。

プレイリストの未所持項目や表示専用の投影は、パスなしでも作成できます。ただし、その値を導入済みの判定、フォルダ操作、リソース保守、所持譜面のハッシュ更新通知へ入れません。SHA-256は補助識別子であり、MD5を欠く所持譜面を許容する代替条件ではありません。

### 表示行と現在値の取得

`LibraryChartRow` は保存行またはパッケージ項目を包み、`Chart` で共通の譜面を公開します。`ChartListSourceRow` は仮想一覧の絞込み・並べ替えに使う軽量な読取りモデルです。実パス・作者だけで絞り込める場合に、全件の詳細な `ChartFile` やリソース一覧を作りません。

通常の所持譜面では、保存主体の現在値とViewModelの一時状態から投影します。パッケージ行では元の `PackageChartEntry` を保持し、`ProjectionVersion` と変更通知に応じて投影を更新します。導入先の表示だけで警告一覧を構築するなど、不要な値の具体化は避けます。

BMSONを通常一覧に含める範囲は、通常のルート・フォルダ・キーワード・モード絞込みと全譜面の検査一覧です。プレイリスト、保守、導入の専用一覧は、それぞれの情報源からBMSONを扱います。BMS専用の文字化け・未登録・ゼロノート一覧も、画面へ渡す時点では共通の譜面行に投影します。

表示値の正本を保存行へ重複して持たせません。

| 表示する値 | 取得元と更新 |
| --- | --- |
| 譜面情報 | `BMSLibrary.ResolveChartInfo(...)` とセッションの `ChartInfoIndex`。保存主体に一時的な `ChartInfo` を付けて代用しません。 |
| スコア・ランク・達成率 | `ChartScoreSnapshot` を介して譜面・表示行で解決します。BMSの保存行はスコア行の接続と購読の管理を担当します。 |
| 参照するプレイリスト | `PlaylistReferenceIndex`。`RefTablesSymbols` / `RefTablesNames` は表示列名であり、BMS保存行の可変キャッシュではありません。 |
| 警告 | `ChartWarningCollection` と `ChartWarningProjectionFormatter`。行側で表示文字列・ハッシュ・ツールチップを作ります。 |
| リソース健全性・文字コード | 有効な `maintenanceInfo` / `MaintenanceInfo` と一時投影。未計算の状態を空の計算結果として具体化しません。BMS側の内部更新は `NotifyMaintenanceInfoChanged(...)` で通知します。 |
| 導入先 | `PackageChartEntry`、`ChartFileTransientState`、モデル内の一時状態。BMS保存行へ書き戻しません。明示的に空へ変更した状態も区別します。 |

通常一覧の並べ替えは `LibraryChartRowSortEngine` と `ChartListOrder` の列定義を正本にします。公開する並べ替え列と仮想一覧の対応を揃え、未対応列を全件の通常行生成で補いません。画面側の採用・破棄・通知は[一覧表示](../ui/table-view.md)を参照します。

### 操作対象と権限

UIからは `ChartOperationTarget` に譜面、元の項目、所持・保留・未所持の状態、操作権限をまとめて渡します。`SourceScope` は `Library`、`PendingPackage`、`NewlyInstalledPackage`、`PlaylistOwned`、`PlaylistMissing` を区別します。メニュー表示と実行側の両方で権限を確認します。

| 権限 | BMS | BMSON | 条件 |
| --- | --- | --- | --- |
| `OpenFile` / `OpenFolder` | 可 | 可 | 実パスがあり、欠落していないこと。 |
| `OpenRepositoryBySha256` | 可 | 可 | 譜面または譜面情報に有効なSHA-256があること。 |
| `OpenPlaylistUrls` | 可 | 可 | プレイリスト項目にURLまたは差分URLがあること。 |
| `RunResourceHealthCheck` | 可 | 可 | 実パスがあり、欠落していないこと。 |
| `UseLr2Ir` / `UseScoreViewer` / `UpdateRanking` | 可 | 不可 | BMSで、有効なMD5があること。BMS-IRリンクでLR2BMSIDを代用しません。 |
| `RunBmsEncodingCheck` / `RunBmsEncodingFix` / `RunZeroNoteCheck` | 可 | 不可 | 欠落していないBMSであること。 |
| `RenameInvalidExtension` / `ConvertToAudio` | 可 | 不可 | 欠落していないBMSであること。 |
| `RepairInstalledLocation` / `MoveInLibrary` / `RemoveFromLibrary` | 可 | 可 | 実パスがあり、欠落・保留状態ではないこと。 |
| `UpdateInstallDestination` | 可 | 可 | プレイリスト項目ではない保留パッケージ行であること。 |

`GridRowResolver` は通常一覧とプレイリストの行が公開する `Chart` をそのまま使います。生の `BMSFile` を共通操作の入力にせず、必要な場合は明示的に投影します。BMSプレイヤー専用の解決だけは `TryGetBmsPlayerFile(...)` に分けます。

フォルダ名の編集は `TryGetFolderEditChartOperationTarget(...)` で権限を確認し、UIスレッドで `RenameChartFolderRequest` を確定してから実行します。`BMSFile.Level` / `Folder` は読取り専用であり、LEVELの編集はプレイリスト項目の編集として扱います。

右クリックの外部操作は、メニューを開いた正確な行から `RightClickActionResolutionInput` を作り、クリック時に同じ行と現在の設定を再解決します。選択集合を代用しません。未所持のプレイリスト・履歴行も有効なハッシュでWeb操作を行えますが、外部プログラムは解決済みの実ファイルだけを対象にします。詳細は[外部起動](../ui/external-launch.md)を参照します。

### モデルへ渡す参照と要求

ファイルの削除・移転には `LibraryChartRef` を使います。保存主体を優先し、次に種類と正規化した実パスで現行の所持譜面へ解決します。`FromPath(...)` はパスを持つ参照であって、保存主体があることまでは保証しません。ハッシュだけで削除対象を広げません。

UIスレッド上で選択を固定し、機能別の要求へ変換してからバックグラウンド処理へ渡します。

| 操作 | 要求・受渡し |
| --- | --- |
| リソース健全性・無視設定 | `ChartResourceHealthRequest` に `ChartFile` を保持します。 |
| フォルダの自動命名 | `ChartFolderAutoRenameRequest` に `ChartFile` を保持します。 |
| 保留導入先の検索・解除・編集 | `PendingInstallDestinationSearchRequest` / `PendingInstallDestinationClearRequest` / `PendingInstallDestinationEditRequest`。パッケージ項目の識別関係を保持します。 |
| 導入先の修復 | `RepairInstalledLocationTargetSnapshot`。検索・解除は `RepairEntries`、修復は現在の導入先状態を重ねた `RepairCharts` を使います。 |
| 保留譜面の削除 | `RemovePendingCharts(IEnumerable<ChartFile>)`。物理削除に成功したパスを `ChartPathsToRemove` として項目削除へ渡します。 |

パッケージに属する対象は、項目の識別関係から現行パッケージへ解決します。所属しない対象は要求の `LooseEntries` に固定し、後からパスだけで保留パッケージへ吸収しません。検索やメニューの可否判定のために、共有の互換オブジェクトを遅延生成して状態を書き戻すことはしません。

導入先修復はBMS・BMSONを共通の変更事実として返し、保存時だけ形式別に分けます。成功した修復後の保守対象は、確定後の現在の保存主体から取り直し、同じ排他権内でまとめて検査します。操作全体の結果は[変更セッション](mutations.md)に保持します。

### パッケージと保留状態

`ChartPackage.ChartEntries` をパッケージ内の譜面の正本にします。遅延探索の実行時期を保ち、代表譜面・定義リソース・推定入力・保留一覧を項目から作ります。BMSの保存主体が必要な処理だけが対象項目から取り出します。

導入先、候補、低信頼の警告、`SEARCHING` は項目の投影状態です。項目の変更後に表示行が古い投影を固定しないようにします。再構成したパッケージでは、代表だけでなく全項目のリソース健全性と警告を復元します。BMS・BMSONとも導入済み、単一BMSON、入れ子譜面などの項目固有の条件を維持します。

`PackageInstallEstimationSnapshot` は項目から代表譜面、定義リソース、メタデータ、探索範囲を作ります。ディレクトリ型パッケージだけがルート単位の上限付き探索を共有します。単一ファイル型と所属のない譜面では、親ディレクトリを列挙せず、同梱・候補リソースを別々の空集合、探索件数を0として保持します。定義リソースと譜面自身の情報は保持します。探索・候補評価の詳細は[導入先推定](install-estimation.md)を参照します。

再生停止は変更範囲で決めます。フォルダ全体の移動・削除・名前変更では、譜面形式にかかわらず再生中BMSのディレクトリとの重なりを確認します。ファイル単位の削除・修復ではBMSファイルへの影響を確認し、同じフォルダのBMSONだけを変更するためにBMS再生を停止しません。

### プレイリストの識別と行

所持譜面から追加する項目は、判明しているMD5・SHA-256を両方保存します。一方、検索・参照・集計は `PlaylistEntryLookupKey` が選ぶ一つのキーを使います。MD5があればMD5だけを使い、見つからなくてもSHA-256へ切り替えません。MD5のない項目はSHA-256を使います。既存行を読んだだけで不足ハッシュを全量補完しません。

`PlaylistDetailSourceRow` / `PlaylistDetailRow` は解決済みの `ChartFile` を持ちます。未所持項目は項目メタデータから投影し、現行の形式表現は `ChartFileKind.Bms` とします。参照プレイリスト名とキーワード検索も同じ参照索引を使います。

通常フォルダへの追加では、元がプレイリスト行なら `BMSTableEntry.Duplicate()` で項目メタデータを保持します。ルートへの自動振分けでは、解決できた所持譜面から新しい項目を作り、同じ実ディレクトリにあるBMS・BMSONのMD5集合を `org_md5` とします。解決できない行は既存メタデータを複製します。参照の更新はBMSだけの一覧へ戻さず、共通の譜面と対象ハッシュを渡します。

### 譜面情報とリソース保守

`ChartFileSnapshot` はファイル内容・長さ・更新時刻・ハッシュを持つ短命な読取り結果です。アプリ内の譜面を表す `ChartFile` と混同しません。読取り済みの内容は[ファイル読取り](chart-file-reading.md)の契約で再利用します。

BMSは `maintenanceInfo`、BMSONは `MaintenanceInfo` と解析済みのリソース参照を使います。定義・存在件数の計算は `ChartResourceSnapshot.Create(ChartFile)` に集約し、BMSの文字コード・修正済み状態をリソース計算で上書きしません。保守はファイル内容のハッシュ更新通知の生成元ではありません。

リソース参照の `.` は譜面相対のキーへ正規化し、`..` は未対応の親ディレクトリ参照として区別します。通常変更は対象譜面だけの保守を行い、全件対象は明示的な全件検査・索引再構築に限定します。同じ操作で必要な全件対象を二重生成しません。

譜面情報の解析と保存は[譜面情報](chart-info.md)に従います。BMSの全件補完では同MD5を一度評価し、既存 `song` の対応する派生列だけを更新します。BMSONはSHA-256を優先してまとめ、LR2行や `chart_digest_map` を生成しません。永続化の成功後だけ保存主体・索引・通知へ反映します。

### 重複表示

重複検索は、所持集合に隣接する `DuplicateChartRow` の変更不能な集合を使います。行には種類・実パス・MD5・保存主体を持たせ、重複グループに入る対象だけを詳細な譜面へ投影します。導入先の一時状態とSHA-256のみの投影を重複判定へ混ぜません。

構築済みの行集合は追加・削除・移転・MD5変更で差分更新し、取得のたびに全行を複製しません。グループの結果は `DuplicateChartGroups`、結果を無効化した通知は `DuplicateChartGroupsInvalidationVersion` と分けます。

重複グループの正本は `DuplicateGroup.ChartFiles` です。BMSの重複警告は保存主体へ反映します。BMSONの重複警告はグループ内の投影に保持し、通常一覧のBMSON保存行へ永続化した警告とはみなしません。詳細は[重複ファイル](duplicate-files.md)を参照します。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 保存行からの投影・識別条件・所持集合 | [`ChartFile`](../../../BeMusicSeeker/Models/ChartFile.cs)、[`ChartFileProjection`](../../../BeMusicSeeker/Models/ChartFileProjection.cs)、[`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/OwnedChartCollectionState.cs) | [`OwnedChartCollectionProjectionTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionProjectionTests.cs)、[`OwnedChartCollectionLookupMembershipTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionLookupMembershipTests.cs) |
| 実パス参照・導入先状態の分離 | [`LibraryChartRef`](../../../BeMusicSeeker/Models/BmsLibraryInternal/LibraryChartRef.cs)、[`OwnedChartCollectionState`](../../../BeMusicSeeker/Models/BmsLibraryInternal/OwnedChartCollectionState.cs) | [`OwnedChartCollectionReferenceIndexTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionReferenceIndexTests.cs)、[`OwnedChartCollectionInstalledOverlayTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionInstalledOverlayTests.cs) |
| 変更・ハッシュ更新・通知 | [`LibraryMutationOwner`](../../../BeMusicSeeker/Models/BMSLibrary.LibraryMutationOwner.cs) | [`OwnedChartCollectionLibraryMutationTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionLibraryMutationTests.cs)、[`OwnedChartCollectionInlineDigestTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionInlineDigestTests.cs)、[`OwnedChartCollectionRefreshTests`](../../../BeMusicSeeker.Tests/OwnedChartCollectionRefreshTests.cs) |
| パッケージ投影の変更通知 | [`PackageChartEntry`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PackageChartEntry.cs) | [`PackageChartEntryNotificationTests`](../../../BeMusicSeeker.Tests/PackageChartEntryNotificationTests.cs) |
| パッケージ導入・修復・再生との同期 | [`BmsLibraryPackageInstallService`](../../../BeMusicSeeker/Models/BmsLibraryInternal/BmsLibraryPackageInstallService.cs) | [`BmsLibraryPackageInstallServiceTests`](../../../BeMusicSeeker.Tests/BmsLibraryPackageInstallServiceTests.cs)、[`ChartFileOperationSynchronizerTests`](../../../BeMusicSeeker.Tests/ChartFileOperationSynchronizerTests.cs) |
| 通常一覧の操作・更新・確定と破棄 | [`RegularChartListOwner`](../../../BeMusicSeeker/ViewModels/MainWindow/RegularChartListOwner.cs) | [`RegularChartNavigationTests`](../../../BeMusicSeeker.Tests/RegularChartNavigationTests.cs)、[`RegularChartNormalLibraryRefreshTests`](../../../BeMusicSeeker.Tests/RegularChartNormalLibraryRefreshTests.cs)、[`RegularChartFolderRenameTests`](../../../BeMusicSeeker.Tests/RegularChartFolderRenameTests.cs)、[`RegularChartCommitAndLifecycleTests`](../../../BeMusicSeeker.Tests/RegularChartCommitAndLifecycleTests.cs) |
| 仮想一覧・表示値・並べ替え | [`LibraryChartRowSortEngine`](../../../BeMusicSeeker/ViewModels/LibraryChartRowSortEngine.cs) | [`RegularChartViewBuildAndOrderingTests`](../../../BeMusicSeeker.Tests/RegularChartViewBuildAndOrderingTests.cs)、[`LibraryChartRowSortEngineTests`](../../../BeMusicSeeker.Tests/LibraryChartRowSortEngineTests.cs) |
| プレイリストの所持数・参照解決・変更 | [`CatalogOwnedCollectionOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/CatalogOwnedCollectionOwner.cs) | [`PlaylistSummaryCountAndPresentationTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryCountAndPresentationTests.cs)、[`PlaylistSummaryOwnedHashTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryOwnedHashTests.cs)、[`PlaylistSummaryMutationAndWarmTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryMutationAndWarmTests.cs)、[`PlaylistSummaryResolveIndexTests`](../../../BeMusicSeeker.Tests/PlaylistSummaryResolveIndexTests.cs)、[`PlaylistViewPipelineTests`](../../../BeMusicSeeker.Tests/PlaylistViewPipelineTests.cs) |

## 関連資料

[データと索引](../core/data-and-indexes.md)、[変更セッション](mutations.md)、[一覧表示](../ui/table-view.md)、[ライブラリの案内](README.md)。
