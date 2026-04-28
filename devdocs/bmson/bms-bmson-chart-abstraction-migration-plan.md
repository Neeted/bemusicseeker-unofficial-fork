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
- zero-note check based on LR2 `song.notes`
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
| maintenance table 反映 | Yes | TBD | bmson は仕様決定後に追加 |
| BMS encoding check / fix | Yes | No | bmson JSON には適用しない |
| zero-note check | Yes | No | `chart_info.notes` ではなく BMS/LR2 警告 |
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

`MoveBMSFile`, `RemoveBMSFiles`, `MoveBMSRootFolder` の内部を `ChartOperationTarget` / `OwnedChartRef` に対応させる。

- BMS は従来通り `song`, `folder`, `maintenance` を更新
- bmson は `bmson_song` を更新
- 共通で installed directory index / duplicate cache / parent folder cache を invalidation
- folder move / unregister は BMS と bmson の両方を delta に含める

外部 API 名はすぐ変えなくてもよいが、内部実装は `BMSFile` 前提から脱却する。

### Phase D: maintenance を format 別に分離する

現在の maintenance は BMS 前提の情報が混在している。

分離案:

- BMS encoding maintenance
  - BMS の文字化けチェック / 修正
  - `maintenance.encoding`
  - BMS parser reload
- Chart resource health
  - WAV / BGA / Movie / optional image
  - BMS / bmson 共通
  - 計算元は `OwnedChartRef.ResourceReferences`

bmson の resource health を `maintenance` table に保存するかは仕様決定が必要。  
保存する場合は `maintenance.path` を共通 path-keyed record として扱えるが、encoding 系は BMS のみに限定する。

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
BMS 専用操作が必要なときだけ `row.Chart.BmsFile` を取り出す。

この phase は影響が大きいため、Phase A-D で安全性を上げてから行う。

### Phase F: 命名と API を整理する

長期的には、ユーザー向け文言以外の内部 API も整理する。

- `MoveBMSFile` -> `MoveChartFile`
- `RemoveBMSFiles` -> `RemoveChartFiles`
- `BMSFiles` -> `BmsFiles` / `OwnedCharts`
- `BMSFilesPendingInstall` -> `PendingCharts`
- `ForceFileScanCheckBMSFiles` -> `ForceResourceHealthCheckCharts`

ただし一度に rename すると diff が大きくなるため、実装が安定してから行う。

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

## 推奨する次の着手順

1. Phase A の安全修正
   - bmson を BMS encoding / zero-note / BMS reload から除外
   - bmson folder move / unregister の `bmson_song` 更新漏れ修正
2. Phase B の `ChartOperationTarget` 導入
   - 右クリック menu と handler を capability ベースに整理
   - 通常一覧 bmson と playlist bmson の差を減らす
3. Phase C の mutation 統合
   - install / move / remove / unregister の BMS / bmson 共通化
4. Phase D の maintenance 分離
   - resource health を format-independent にする
5. Phase E で `PendingChartEntry` の用途縮小
   - 通常一覧の bmson 表示を専用 row / common row へ移行

## ゴール

最終状態では、アプリ内の大半の処理は「BMS か bmson か」ではなく「その譜面 target がその操作 capability を持つか」で分岐する。  
これにより、bmson に BMS 専用処理が誤適用される問題と、通常一覧 / playlist 詳細で bmson の挙動がずれる問題を同時に減らす。
