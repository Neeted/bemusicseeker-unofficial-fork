# BMS / bmson 譜面抽象化 移行計画

## 目的

`bmson` 対応は段階導入の過程で、通常一覧では `PendingChartEntry : BMSFile`、プレイリスト詳細では `ResolvedBmson`、DB では `bmson_song` として扱われている。  
このため「譜面ファイルとしては共通に扱いたい処理」と「BMS / LR2 専用処理」が混ざり、不整合が出始めている。

この資料では、`BMS` と `bmson` をどちらも「所持譜面」として扱うための共通抽象を整理し、既存機能を壊さず段階的に移行する計画を残す。

## 現在見えている問題

### 1. bmson の移動 / 再インストール先更新が反映されない場合がある

フォルダ移動や再インストール先への移動では、BMS は `song` / `folder` / `maintenance` が更新される。一方 `bmson` は `bmson_song` 側の path 更新・登録解除が一部経路で漏れる可能性がある。

特に unregister を伴うフォルダ移動では、BMSFile だけを対象にして早期 return する経路があり、`bmson_song` が旧 path のまま残る候補がある。

### 2. bmson 行で WAV などのヘルス更新走査を実行すると表示値が壊れる場合がある

通常一覧の bmson は `PendingChartEntry : BMSFile` として見せているため、BMS 専用の maintenance / encoding / reload 処理へ混ざれる。  
その結果、bmson に対して BMS parser / encoding reload が走り、`TITLE`, `ARTIST` などが空欄化する可能性がある。

### 3. 通常一覧 bmson と playlist 詳細 bmson の表現が違う

- 通常一覧: `PendingChartEntry : BMSFile`
- playlist 詳細: `PlaylistDetailRow.ResolvedBmson`
- DB: `LR2SongDBExtended.bmson_song`

同じ bmson 所持譜面でも、右クリックメニュー、file open、preview、score / LR2IR 系の表示条件が経路ごとにずれる。

### 4. BMS 専用操作の誤適用 / 除外条件の散在

次の機能は BMS / LR2 前提だが、現在は `BMSFile` 型に乗っているかどうかで判定されやすい。

- LR2 DB 未登録
- LR2 IR / score viewer / ranking 更新
- ゼロノート検索
- 文字化けチェック / 文字化け修正
- `song`, `folder`, `chart_digest_map` 更新

bmson はこれらを受け付けない方が自然だが、条件が UI / ViewModel / service に分散している。

## 基本方針

### BMS と bmson は「譜面ファイル」として共通化する

共通で扱う情報:

- path
- md5
- sha256
- title / artist / level / mode
- `chart_info`
- resource references
- resource health
- open file / open folder
- Mocha / MinIR
- install / move / remove / unregister
- duplicate / installed directory lookup

### LR2 / BMS 専用処理は capability として明示する

bmson では不可にするもの:

- LR2 `song` / `folder` table 更新
- `chart_digest_map`
- LR2 IR / score viewer / ranking 更新
- BMS parser reload
- BMS encoding detection / fix
- zero-note notation check for BMS charts whose `chart_info.notes == 0`
- BMS playback preview

### bmson 専用処理も BMS 互換 API に押し込めない

bmson では別処理にするもの:

- `bmson_song` table 更新
- bmson JSON parser
- bmson resource references
- bmson metadata / chart_info parser
- 将来の bmson playback

## 目標設計

### 永続モデル

永続モデルは既存方針を維持する。

| Kind | DB table | 主な責務 |
| :--- | :--- | :--- |
| BMS | `song`, `folder`, `maintenance`, `chart_digest_map` | LR2 互換の所持譜面管理 |
| bmson | `bmson_song` | アプリ独自の bmson 所持管理 |
| BMS / bmson 共通 | `chart_info` | 譜面メタデータ |

`song` table は LR2 互換のため bmson を入れない。  
`bmson_song` は path を主キーにし、md5 / sha256 / title / artist / resource references / chart_info relation を持つ。

### 共通 read model

永続モデルとは別に、アプリ内で扱う共通 read model を作る。

```text
OwnedChartRef
  Kind: Bms | Bmson
  Path
  Directory
  Md5
  Sha256
  Title
  Artist
  Level
  Mode
  ChartInfo
  ResourceReferences
  BmsFile?       // Kind=Bms の実体
  BmsonSong?     // Kind=Bmson の実体
```

これは「所持譜面」を表すための read model であり、DB 永続化の source of truth ではない。

### UI 操作用 target

右クリックや command は `BMSFile` ではなく `ChartOperationTarget` を受け取る。

```text
ChartOperationTarget
  OwnedChartRef Chart
  PlaylistEntry?
  IsOwned
  IsPending
  IsPlaylistMissing
  Capabilities
```

`Capabilities` には以下を含める。

```text
OpenFile
OpenFolder
OpenRepositoryBySha256
UseLr2Ir
UseScoreViewer
UpdateRanking
RunBmsEncodingCheck
RunBmsEncodingFix
RunZeroNoteCheck
RunResourceHealthCheck
MoveInLibrary
RemoveFromLibrary
UpdateInstallDestination
```

UI は `Kind` 直接判定ではなく capability を見る。これにより「BMS だけ」「bmson だけ」「共通」の条件が散らばらない。

## 操作 capability 一覧

| 操作 | BMS | bmson | 備考 |
| :--- | :---: | :---: | :--- |
| ファイルを開く | Yes | Yes | path があれば共通 |
| フォルダを開く | Yes | Yes | path があれば共通 |
| Mocha / MinIR | Yes | Yes | sha256 があれば共通 |
| LR2 IR | Yes | No | md5 / LR2 `song` 前提 |
| Score Viewer | Yes | No | 現状 BMS / LR2 score 前提 |
| ranking 更新 | Yes | No | 現状 BMS / LR2 IR 前提 |
| 導入先推定 | Yes | Yes | resource reference は format 別 parser |
| 導入 / 移動 / 削除 | Yes | Yes | 永続化先は別 |
| resource health | Yes | Yes | 計算元は format 別 |
| maintenance table 反映 | Yes | Yes | テーブルは共有し、workflow は分離する |
| BMS encoding check / fix | Yes | No | bmson JSON には適用しない |
| zero-note check | Yes | No | BMS の `chart_info.notes == 0` と本文正規表現確認 |
| `song` / `folder` 更新 | Yes | No | LR2 互換領域 |
| `bmson_song` 更新 | No | Yes | アプリ独自領域 |
| `chart_digest_map` | Yes | No | BMS 用 SHA-256 map |
| `chart_info` | Yes | Yes | 共通 metadata |

## 移行フェーズ

### Phase A: 破壊的な誤適用を止める

最初に、現在報告されている実害を抑える。

- bmson 行を BMS encoding / reload / zero-note check に流さない
- bmson の resource health check は BMS parser reload なしで行う
- folder move / unregister / re-install move で `bmson_song` path を確実に更新する
- bmson 削除時に installed directory index / duplicate cache / parent folder cache を無効化する
- `TryGetInstalledDirectoryByHash` を BMSFiles だけでなく installed directory index 経由に寄せる

この phase は設計移行の前でも入れられる安全修正とする。

### Phase B: `OwnedChartRef` と `ChartOperationTarget` を追加する

まだ既存 row model は大きく変えず、resolver を先に作る。

- `BMSFile` から `OwnedChartRef(Bms)` を作る
- `bmson_song` から `OwnedChartRef(Bmson)` を作る
- `PendingChartEntry` からも `OwnedChartRef` を作る
- `PlaylistDetailRow` / `PlaylistDetailSourceRow` から `ChartOperationTarget` を作る
- context menu の表示条件を capability へ移す

この段階で通常一覧 bmson と playlist bmson の右クリック差分を減らす。

### Phase C: ライブラリ mutation を chart 単位に寄せる

library mutation の内部を `ChartOperationTarget` / `OwnedChartRef` に対応させる。旧 `MoveBMSFile` wrapper は production 参照がないため削除済みで、library chart 削除入口は `RemoveChartFiles` へ移行済みである。`MoveBMSRootFolder` は root folder 操作の BMS 語彙として残っている。

- BMS は従来通り `song`, `folder`, `maintenance` を更新
- bmson は `bmson_song` を更新
- 共通で installed directory index / duplicate cache / parent folder cache を invalidation
- folder move / unregister は BMS と bmson の両方を delta に含める

外部 API 名はすぐ変えなくてもよいが、内部実装は `BMSFile` 前提から脱却する。

### Phase D: maintenance table を共有し、workflow を format 別に分離する

`maintenance` table は分けず、BMS / bmson の resource health 保存先として共有する。  
ただし、BMS の encoding / 文字化け / zero-note workflow と、BMS / bmson 共通の resource health workflow は明確に分離する。

`maintenance` table で bmson が正式利用するフィールド:

- `path`
- `hash`
- `wav_files_existing` / `wav_files_defined`
- `bga_files_existing` / `bga_files_defined`
- `movie_files_existing` / `movie_files_defined`
- `is_stagefile_existing` / `is_stagefile_defined`
- `is_banner_existing` / `is_banner_defined`
- `is_backbmp_existing` / `is_backbmp_defined`
- `is_files_warning_ignored`

bmson では使用しない、または BMS 専用として扱うフィールド:

- `encoding`
- `is_encoding_fixed`

bmson は UTF-8 JSON 前提なので、BMS の文字化けチェック / 修正 / encoding reload には入れない。  
`encoding` に `utf-8` を保存する場合でも、それは情報表示用の仕様値であり、文字化け修正対象にするものではない。

Phase D の実装方針:

- `CleanupMaintenanceTable()` は BMS path と bmson path の両方を生存対象にする
- 起動時 maintenance hydration は BMS だけでなく bmson row にも path で適用する
- `UpdateMaintenanceInfo()` は BMS 用 encoding workflow と resource health workflow を内部で分ける
- bmson の health 更新では BMS parser reload / encoding detection / zero-note check を呼ばない
- bmson の resource reference は `bmson_song` / bmson parser 由来の `wav_files`, `bga_files`, optional image fields を使う
- full scan view では BMS / bmson の health columns を同じ表示仕様で扱う

この phase では table 追加は行わない。  
将来 bmson 固有の JSON diagnostic や parser warning を永続化したくなった場合のみ、別 table 追加を再検討する。

### Phase E: 通常一覧の bmson から `PendingChartEntry : BMSFile` を外す

`PendingChartEntry` は本来 pending install adapter としての性格が強い。  
通常一覧の所持 bmson 表示には専用 row を作る。

候補:

```text
LibraryChartRow
  OwnedChartRef Chart
  Display properties
  ChartInfo display properties
```

通常 DataGrid は `BMSFile` と `PendingChartEntry` の混在ではなく、`LibraryChartRow` を表示する。  
既存 `BMSFile` 型 API が必要なときだけ `row.Chart.CompatibilityChartFile` を取り出す。

実装後の境界:

- 通常一覧の所持 BMS row は `LibraryChartRow.FromBmsFile(BMSFile)`
- 通常一覧の所持 bmson row は `LibraryChartRow.FromBmsonSong(bmson_song)`
- `LibraryChartRow` は表示・検索・ソート・右クリック resolver のための read model であり、DB 更新の source of truth ではない
- pending install / package parse / maintenance 内部 adapter としての `PendingChartEntry : BMSFile` は残す
- 保留 / 新規インストール画面で `PendingChartEntry` を `LibraryChartRow` が包む場合でも、元の `PendingChartEntry` を `Chart.CompatibilityChartFile` / `GridRowResolver.GetOperationChartFile()` に残す。`warning`, `instl_dst`, resource health などの pending state は adapter 側の値を正とする
- `ChartOperationTarget` には `SourceScope` を持たせ、同じ `LibraryChartRow` でも `PendingPackage`, `NewlyInstalledPackage`, `Library`, `PlaylistOwned`, `PlaylistMissing` を区別する。保留行は local install destination 更新対象、新規導入後行と通常所持行は library mutation 対象として扱う
- bmson install 後は、登録された `bmson_song` を `PendingChartEntry.BmsonSong` に差し替え、`path` / `folder` / `md5` / `sha256` / `MaintenanceInfo` を同期する。これにより、新規画面の PATH 表示、Explorer、削除/移動操作が同じ実体を指す
- pending / newly-installed bmson の操作 target は `bmson_song.path` ではなく、現在の `PendingChartEntry.path` を優先する。`bmson_song` は保存済み実体の参照として使い、表示・操作のカレント path は adapter 側を正とする
- DataGrid cell getter では DB lookup や `ChartInfoIndex` lookup を行わず、`BMSFile`, `bmson_song`, `ChartInfo`, `MaintenanceInfo` に materialize 済みの値だけを見る
- BMS 専用操作は `ChartOperationTarget.Capabilities` と chart kind / adapter kind で guard し、compatibility bmson を流さない

Phase E 完了後も domain/storage model は `BMSLibrary.BMSFiles` と `BMSLibrary.BmsonSongs` の二本立てのまま維持する。

### Phase F: 命名と API を整理する

長期的には、ユーザー向け文言以外の内部 API も整理する。
ただし `BMSFile`, `BMSLibrary.BMSFiles`, `BMSFilesView` は XAML binding、設定、pending install、LR2 互換の語彙に広く残っているため、全面 rename は挙動が安定してから行う。`BMSPackage` については `ChartFiles` を primary API とし、production 参照のなくなった旧 `BMSFiles` alias は残さない。

Phase F-1 では「新規コードの入口を chart 抽象に揃える」ことを優先する。

- `GridRowResolver.TryGetChartRef` / `TryGetOperationChartTarget` を primary API とし、BMS-only 互換 shim は `GetRealBmsFile` に限定する
- UI handler は `ChartOperationTarget.Capabilities` で対象を絞り、BMS 専用操作だけ実体 BMS `BMSFile` へ戻す
- 構成ファイルフルスキャンは `RunResourceHealthCheck` capability を使い、BMS / bmson の両方を chart resource health 対象にする
- `ForceResourceHealthCheckCharts` など chart 名 API を主入口にし、production 参照のない旧名 wrapper は残さない
- `BMSPackage` 経由の package 内 chart 参照は `ChartFiles` を primary API とし、旧 `BMSFiles` alias は production 参照がなくなった段階で削除する

Phase F-2 以降で検討する広範囲 rename 候補:

- `MoveBMSFile` -> `MoveChartFile` は、production 参照のない旧 wrapper を削除済み
- `RemoveBMSFiles` -> `RemoveChartFiles` は、library chart 削除入口として移行済み
- `InstallBMSFilesAuto` / `InstallBMSPackagesForce` / `InstallBMSPackagesToEstimatedDir` は `InstallChartPackagesAuto` / `ForceInstallPendingPackages` / `InstallPendingPackagesToEstimatedDestinations` へ移行済み
- `BMSFiles` -> `BmsFiles` / `OwnedCharts`
- pending install の lock/status 表示名は chart 名へ寄せる。

BMS という名前を残す箇所は、LR2 / BMS 仕様 / 既存 UI / DB 互換の意味を持つものとして扱う。BMS と bmson の両方を対象にする新規内部処理では chart 名を使う。

Phase F-2 では、広範囲 rename ではなく chart 共通操作の入口を整理する。

- resource health は `ForceResourceHealthCheckCharts` / `SetChartResourceWarningsIgnored` を主 API とし、BMS / bmson 両方を対象にする
- pending install destination は `UpdateInstallDestination`、所持 BMS の再インストール先修復は `RepairInstalledLocation` として capability を分離する
- `SearchInstallDestinationForPendingPackages` / `SearchInstallDestinationForPendingCharts`, `ClearInstallDestinationForPendingPackages` / `ClearInstallDestinationForPendingCharts`, `RemovePendingPackages`, `RemovePendingCharts` を追加し、pending/package 互換処理の入口を chart 名へ寄せる
- `BMSLibrary` では `GetChartsNeedResourceFix`, `SetChartResourceWarningsIgnored`, `RemoveChartFiles`, `InstallChartPackagesAuto`, `ForceInstallPendingPackages`, `InstallPendingPackagesToEstimatedDestinations` など chart / pending package 共通名の入口へ寄せる。production 参照のなくなった旧 BMS 名 API / wrapper は残さない
- `BMSFilesView`, `BMSLibrary.BMSFiles` の rename は Phase F-3 以降に回す。`BMSPackage` の package 内 chart 参照は `ChartFiles` に一本化し、互換 alias は残さない

Phase F-3 に進む前に、DataGrid sort engine を整理する。

- 通常一覧は Phase E 以降 `LibraryChartRowSortEngine` が正本なので、`UseFastSortInDataGridExperimental` は `LibraryChartRowSortEngine` 側で解釈する
- 挙動は旧 BMSFile 一覧 sort と互換にし、fast sort 有効時は文字列列を `StringComparer.OrdinalIgnoreCase` ベース、無効時は legacy natural sort ベースにする
- typed sort が必要な列、例えば `Chart*` の数値キー、`rateDouble`、date / bool / numeric property、LEVEL mixed double は設定に関係なく typed sort を使う
- `Folder` など、従来 playlist detail で legacy natural 固定だった列は互換性を優先し、通常一覧 / playlist detail それぞれの既存 profile を明示する
- `BMSFileSortEngine` は test-only helper 化していたため削除済み。互換・性能確認は `BmsSortCompatibilityTests` から直接 `LibraryChartRowSortEngine` を検証する
- sort log の `sortEngine=fast|legacy` と実際の `sortProfile` が矛盾しないよう、`library_chart_*` profile 名も fast / legacy / typed の区別が分かる名前に揃える

整理後の境界:

- 通常一覧の実行経路は `LibraryChartRowSortEngine.SortForMainView(..., useLegacySortForDataGrid, ...)` のみ
- 旧 `BMSFileSortEngine.UseLegacySortForDataGrid` は廃止し、通常一覧の切り替えは `LibraryChartRowSortEngine.SortForMainView(..., useLegacySortForDataGrid, ...)` の引数で表す
- 旧 `BMSFile` sort 互換検証は、production helper を残さず test-local legacy baseline と `LibraryChartRowSortEngine` の比較に寄せる

Phase F-3 は「広範囲 rename」ではなく、低リスクな内部境界の chart 名化を進める。

F-3 で進める候補:

- `BMSFilesFolderView` / `BMSFilesKeywordFilterView` / `BMSFilesModeFilterView` は、型がすでに `LibraryChartRow` なので `ChartRowsFolderView` / `ChartRowsKeywordFilterView` / `ChartRowsModeFilterView` へ寄せる
- `SetBMSFilesView()` は private helper なので、`SetChartRowsView()` を主 API にし、旧名は必要なら shim にする
- `GetSelectedGridRealFiles` / `GetSelectedGridOperationFiles` / `GetSelectedGridOperationChartFiles` の残存用途を整理し、新規 handler は `GetSelectedChartTargets` / `GetSelectedBmsChartFiles` / `GetSelectedPendingChartFiles` へ統一する
- `BMSFileSortEngine` は通常一覧の実行経路から外れており、production 参照がなくなったため削除済み。`BmsSortCompatibilityTests` は `LibraryChartRowSortEngine` ベースへ移植済み
- phase 名・ログ名・コメントは「通常一覧の表示 row は chart row、storage source は BMS/bmson 二本立て」という境界が分かるようにする

F-3 の実装境界:

- 通常一覧の内部 cache / setter は chart row 名へ寄せるが、public binding の `BMSFilesView` は XAML / settings 互換のため維持する
- BMS 専用 handler は `GetSelectedBmsChartFiles`、pending/package 互換 handler は `GetSelectedPendingChartFiles` を使い、汎用的な legacy selection shim は使用箇所がなくなったら削除する
- `BMSFileSortEngine` は残さず、通常一覧 sort の正本を `LibraryChartRowSortEngine` に一本化する

F-3 では後回しにするもの:

- `BMSFilesView`, `SelectedIndexBMSFilesView`, `ColumnsSettingsBMSFilesView`, `UseAsyncBMSFilesViewBinding` は XAML binding / settings / column state の public UI 契約なので rename しない
- `BMSPackage.ChartFiles` は pending install / package chart discovery の中核であり、参照時に lazy discovery が走る意味も維持する。旧 `BMSPackage.BMSFiles` alias は残さず、「package 内 chart の BMSFile 互換 adapter list」は `ChartFiles` として扱う
- `BMSLibrary.BMSFiles` は LR2 `song` table 側の source of truth として残す。BMS / bmson 共通表示は `LibraryChartRow` / `OwnedChartRef` で扱い、storage model は `BMSFiles` と `BmsonSongs` の二本立てを維持する
- `BMSFilesGarbled`, `BMSFilesZeroNote`, encoding / zero-note / LR2IR / ScoreViewer などは BMS 専用意味を持つため chart 名へ広げない
- install package 操作は ViewModel / UI の入口を `ForceInstallPendingPackages` / `ForceInstallPendingCharts`, `ManualInstallPendingPackages` / `ManualInstallPendingCharts` へ寄せる。DB / model 内部名は pending/package 層への影響を見ながら段階移行する

F-3 の確認観点:

- 通常一覧の folder / keyword / mode filter で、rename 前後の row count と sort/filter 結果が変わらない
- pending / newly-installed / library / playlist owned / playlist missing の選択 helper が、それぞれ正しい `ChartOperationTarget` を返す
- BMS 専用操作は `GetSelectedBmsChartFiles` へ集約され、bmson が encoding / zero-note / LR2IR / ScoreViewer に入らない
- package discovery cache (`BMSPackage.ChartFiles`) は参照による heavy source scan 回避の既存挙動を維持する

F-3 後のテスト補強で、今回の BMS / bmson chart 抽象化はいったん完了扱いにする。

完了条件:

- 通常一覧 / pending / newly installed / playlist owned / playlist missing の各 scope で `ChartOperationTarget` と capability が固定されている
- BMS 専用処理、つまり encoding / zero-note / LR2IR / ScoreViewer / ranking / audio convert / invalid extension rename に bmson が入らない
- BMS / bmson 共通処理、つまり open / repository link / resource health / move / remove は capability 境界で両方を扱える
- 通常一覧の所持 bmson は `PendingChartEntry` ではなく `LibraryChartRow` として表示され、pending/package adapter とは境界が分かれている
- sort / search / maintenance / mutation / pending install の回帰テストが、共通処理と専用処理の境界を守っている

完了後に残す次候補:

- `BmsSortCompatibilityTests` を `LibraryChartRowSortEngine` ベースへ移植し、test-only だった `BMSFileSortEngine` は削除済み
- converter / private helper など、XAML binding に影響しない内部 UI 名を小さく chart 名へ寄せる
- `BMSPackage.ChartFiles` の呼び出し側移行と `PendingChartEntry : BMSFile` の本格抽象化を検討する。ただし package discovery / pending install への影響が大きいため、実害が出た箇所から段階的に進める
- public `BMSFilesView` / settings / column state 名の rename は互換リスクが高いため当面保留する

## テスト方針

### 共通 target / capability tests

- BMS row は `UseLr2Ir=true`, `RunBmsEncodingFix=true`
- bmson row は `OpenRepositoryBySha256=true`, `UseLr2Ir=false`, `RunBmsEncodingFix=false`
- playlist 所持 bmson は missing ではなく owned target になる
- playlist 未所持 row は `IsOwned=false` のまま、chart_info があれば repository link は出せる

### mutation tests

- bmson file move で `bmson_song.path` / `folder` が更新される
- bmson folder move で配下の全 `bmson_song` が更新される
- unregister 付き folder move で bmson が `bmson_song` から削除される
- BMS と bmson 混在 folder で両方の DB が正しく更新される
- 削除後に installed directory index / duplicate cache が stale にならない

### maintenance tests

- bmson resource health scan で title / artist / level が空欄化しない
- bmson に対して BMS encoding reload が呼ばれない
- bmson maintenance row が cleanup で削除されない
- 起動後に bmson row へ persisted maintenance health が適用される
- BMS encoding fix は BMS のみ対象
- BMS zero-note check は bmson を対象にしない

### UI tests

- 通常一覧 bmson と playlist 所持 bmson で repository link / file open の可否が一致する
- LR2IR / Score Viewer / ranking は bmson で表示されない
- Open Video / Search Link を LR2IR cache 由来にする場合は bmson で非表示、汎用検索にする場合は capability 名を分ける
- DataGrid scroll 時に DB lookup / ChartInfoIndex lookup が走らない

## 実装時の注意点

- `song` / `folder` は LR2 互換領域なので、bmson を入れない
- `chart_digest_map` は BMS 用の補助 table として維持する
- `chart_info` は BMS / bmson 共通 metadata として維持する
- `maintenance.encoding` は BMS 表示補正用であり、bmson JSON decode や chart_info decode に使わない
- `PendingChartEntry` は段階移行中の adapter として残してよいが、永続所持 bmson の標準 row にしない
- capability は UI 表示だけでなく、handler 側でも再チェックする

## 完了後の運用方針

Phase A から F-3 までで、通常利用上の BMS / bmson chart 抽象化は完了とする。

今後は大きな rename を先行せず、実運用で不整合が出た箇所を `ChartOperationTarget` / `OwnedChartRef` / `LibraryChartRow` の境界へ寄せる。  
BMS 名が残っていても、それが LR2 / `song` table / BMS parser / package adapter の互換境界を表している場合は許容する。

## ゴール

最終状態では、アプリ内の大半の処理は「BMS か bmson か」ではなく「その譜面 target がその操作 capability を持つか」で分岐する。  
これにより、bmson に BMS 専用処理が誤適用される問題と、通常一覧 / playlist 詳細で bmson の挙動がずれる問題を同時に減らす。
