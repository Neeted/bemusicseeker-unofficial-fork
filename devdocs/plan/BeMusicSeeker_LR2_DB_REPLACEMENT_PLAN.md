# BeMusicSeeker LR2 song.db 完全生成 実装計画

この資料は、BeMusicSeeker 側の作業エージェントへ渡すための実装計画です。
OpenLR2 の調査結果と BeMusicSeeker 側の現状を前提に、LR2 の起動時ファイル走査を
BeMusicSeeker 側の `song.db` 生成で代替する方針をまとめます。

## 参照元

- OpenLR2 ローカルクローン:
  `D:\github-clone\OpenLR2`
- BeMusicSeeker 作業ツリー:
  `D:\work\BeMusicSeeker-decomp`

OpenLR2 はオリジナル LR2 そのものではなく、既に手が入った実装です。そのため、この
資料では「オリジナル LR2 の完全再現」ではなく、「LR2 が起動時に再走査しなくてもよい
DB を BeMusicSeeker が安定して生成する」ことを主目的にします。

## 目標

BeMusicSeeker は譜面管理アプリなので、LR2 で完全に読めない譜面も DB から消さない。
その一方で、LR2 向けの `song` / `folder` テーブルはできるだけ完全に生成し、LR2 側の
起動時スキャンによるエラーや長時間処理を避ける。

目標状態:

- LR2 のデータベース自動更新は「手動のみ」で運用できる。
- BeMusicSeeker が LR2 `song` / `folder` テーブルを更新する。
- LR2 起動時の root folder チェックで `date` が一致し、再帰スキャンに入らない。
- LR2 非対応 BMS は BeMusicSeeker の owned collection と LR2 `song` row には残す。
- LR2 非対応 BMS は `folder` / `parent` を LR2 表示対象として使えない状態にし、
  LR2 起動時 scan に踏ませない。
- 非対応 BMS を LR2 のプレイ画面から開いた場合に失敗することは許容する。
- ただし、LR2 起動時のファイル走査で非対応パスを踏ませないことを優先する。

## 決定済み前提

- 対象は BMS 系 chart のみ。
  - `bmson` は LR2 `song` / `folder` には入れない。
  - `bmson` には LR2 compatibility warning も出さない。
  - `bmson_song` / app-owned maintenance / playlist など BeMusicSeeker 内部の扱いは現行通り維持する。
- BMS owned source は引き続き LR2 `song` table に保存する。
  - CP932 非対応や path 長過多の BMS も `song` row からは削除しない。
  - LR2 互換 path として扱えない場合は `song.folder` / `song.parent` を `NULL` にし、
    compatibility warning で扱う。
  - `song` から除外する方式は、別の BMS owned storage がない限り採用しない。
- CRC は既存の `LR2CRC32` / `Lr2SongFolderParentNormalizer` 系の実装を活かす。
  - 新規に CRC アルゴリズムを作り直さない。
  - ただし、CRC の入力文字列、末尾 `\`、NUL、`ROOT`、drive root / UNC の扱いは
    golden fixture で固定する。
- 完全生成は LR2 連携モードではデフォルト有効にする。
  - 設定化する主目的は、スタンドアロンモードで LR2 用の重い補助列挙・解析を避けること。
  - LR2 連携モードでも、LR2 自身に厳密な譜面走査を任せたいユーザー向けに無効化できる余地を残す。
- LR2 compatibility warning は完全生成設定が有効なときの評価を正本にする。
  - 既存の `Lr2PathEncodingUnsupported` のように軽く出せる warning は継続する。
  - ただし maintenance 列や scan group が必要な warning は完全生成有効時のみ出す。
  - スタンドアロンモードでは `LR2非対応パス` ツリー自体を非表示にする。
- backfill は通常運用では発生しない想定にする。
  - 既存 LR2 DB に必要列が欠けている、または完全生成を初回有効化した場合だけ backfill が発生する。
  - backfill が必要な場合は警告・進捗・キャンセル可能性を UI / log に出す。
  - 初回 backfill は startup ready / operable を待たせず、初期化完了後の background workflow として開始する。
  - LR2 起動導線では backfill 完了まで起動を止め、警告付き強行起動は提供しない。
- 段階実装中は、Phase 8 / Phase 9 まで完了するまでは完全生成設定を hidden / disabled にする。
  - `song` row だけ新形式になり、対応する `folder` row / manifest / backfill が未整備な中間状態を
    ユーザー環境で有効化しない。

## 設定方針

完全な `song.db` 生成は設定化する。ただし LR2 連携ユーザーにとって望ましい既定動作なので、
LR2 連携モードでは **デフォルト有効** にする。

この計画でいう LR2 連携モードは、設定上 `OperationModeLR2DB == true` であり、LR2 の
database / executable path が有効に解決できる状態を指す。

推奨 UI:

- 設定画面の「動作モード」内、「LR2と連携する」の近くに置く。
- 表示名例:
  - `LR2用song.dbをBeMusicSeekerで完全生成する`
  - または `LR2起動時のDB自動更新をBeMusicSeekerで代替する`
- 初期値:
  - `OperationModeLR2DB == true` の場合は有効。
  - スタンドアロン運用では無効にし、設定項目も非表示にする。

設定別の処理方針:

- 完全生成が無効:
  - 現行に近い軽量スキャンを維持する。
  - chart / audio / image / movie の列挙でよい。
  - `song.txt`、`.lr2folder`、`folderinfo.txt`、LR2 folder hierarchy のための追加列挙は行わない。
- 完全生成が有効:
  - BMS の変更検出に必要な chart mtime / size / hash 判定を行う。
  - BMS chart directory 直下の text group を列挙する。
  - `.lr2folder` と `folderinfo.txt` を LR2 `folder` テーブル生成の入力として扱う。
  - root folder と BMS chart ancestor directory の mtime を取得する。
  - LR2 互換性 warning 用に CP932 変換可否と CP932 byte length を評価する。

## OpenLR2 側で参考にする挙動

主な参照箇所:

- `SearchSongsFromPath`:
  `D:\github-clone\OpenLR2\LR2\LR2_songmanage.cpp:1056`
- `ParseBMSMETA`:
  `D:\github-clone\OpenLR2\LR2\LR2_songmanage.cpp:2595`
- `GetFolderDataFromPath`:
  `D:\github-clone\OpenLR2\LR2\LR2_songmanage.cpp:1650`
- manual-only 起動時の更新対象:
  `D:\github-clone\OpenLR2\LR2\LR2_songmanage.cpp:2526`
- `AssignCRC32`:
  `D:\github-clone\OpenLR2\LR2\En_fileutil.cpp:625`

OpenLR2 の重要な挙動:

- `SearchSongsFromPath` は `FindFirstFileA(root + "*.*")` で走査する。
- `ParseBMSMETA` は narrow `fopen` で読み、行を CP932 として扱う。
- BMS ディレクトリ直下に `*.txt` があれば `song.txt = 1` になる。
- `song.date` は BMS ファイルの最終更新時刻の Unix 秒。
- `song.adddate` は登録・更新時刻の Unix 秒。
- `folder.date` はディレクトリまたは `.lr2folder` ファイルの最終更新時刻の Unix 秒。
- `folder.adddate` は登録・更新時刻の Unix 秒。
- `song.folder` は chart directory path + trailing slash + NUL の LR2 CRC32。
- `song.parent` は parent directory path + trailing slash + NUL の LR2 CRC32。
- root folder の `parent` は `AssignCRC32("ROOT")`。
- 通常フォルダの `folder.type` は `1`。
- `.lr2folder` の `folder.type` は通常 `2`。
- root の特殊 `.lr2folder` は `#COMMAND` / tag によって `3`, `4`, `6` になり得る。

manual-only でも LR2 起動時には以下が選択される:

```sql
SELECT path,date FROM folder WHERE parent = ROOT OR date = 0
```

したがって、BeMusicSeeker が LR2 起動時走査を代替するには、少なくとも root folder の
`date` を実ディレクトリ mtime と一致させる必要がある。`date = NULL` や `date = 0`
は避ける。

## BeMusicSeeker 側の現状

主な参照箇所:

- 起動時チャートスキャン:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:4241`
- Everything scan:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\Utils\EverythingFileScanner.cs:29`
- managed fallback:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\Utils\FastRootFileEnumerator.cs:35`
- `ChartScanResult`:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\Utils\ChartScanResult.cs:6`
- `ChartFileSnapshot`:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\ChartFileSnapshot.cs:5`
- `ChartFileContentReader.ReadSnapshot`:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\ChartFileContentReader.cs:10`
- BMS file diff parser:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryInitializationService.cs:902`
- BMS commit:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryDbGateway.cs:180`
- lightweight parser:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSFile.cs:669`
- detailed `chart_info` parser:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\ChartInfoParser.cs:203`
- `.lr2folder` 出力:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSPlaylist.cs:2614`
- `.lr2folder` 本文生成:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSPlaylist.cs:4374`

現状:

- Everything を優先し、失敗時に managed filesystem scan へ fallback する。
- 現在の `ChartScanResult` は chart path / chart directory / audio/image/movie の
  resource hash を持つ。
- 現在の `ChartScanResult` は file mtime、directory mtime、`.txt` 有無を持たない。
- `ChartFileSnapshot` には file bytes、MD5、SHA256、`LastWriteTimeUtc` がある。
- `BMSFile.CreateBMSFileFromSnapshot` は軽量パーサー。
- 軽量パーサーは `#WAVxx`、`#BMPxx`、主要メタデータ、`#STAGEFILE`、
  `#BANNER`、`#BACKBMP` を読む。
- 軽量パーサーは `maxbpm`、`minbpm`、`longnote`、`bga`、`random`、
  `karinotes`、`exlevel` を十分には埋めない。
- 詳細解析結果は `chart_info` にあるが、素の LR2 `song` 列へ同期されていない。
- `.lr2folder` ファイル出力はあるが、出力と同時に `folder` テーブルを完全に作る層は
  不十分。
- 既存 BMS の変更検出は、完全生成に必要な `path + mtime` 差分検出としては不足している。

## 実装前に固定する必要がある contract

### BMS 変更検出

完全生成の正しさは、BMS の変更検出に依存する。現行の「追加 path だけ parse」では、
既存 BMS の内容変更、mtime 変更、`song.txt` 変更、LR2 numeric columns の変更を取り逃がす。

方針:

- BMS / bmson に関係なく、owned chart の変更検出 contract は統一する。
- DB row と現 file の `path` / high precision mtime / size が一致する場合は更新なし。
- high precision mtime または size が変わった場合は `ChartFileSnapshot` を読み、MD5 を比較する。
- mtime / size が変わり MD5 も変わった場合:
  - BMS は再parseし、`song` / `chart_info` / `maintenance` / LR2 compatibility snapshot を更新する。
  - bmson は現行の `bmson_song` 更新経路を維持する。
- mtime / size が変わったが MD5 が同じ場合:
  - BMS は `song.date` だけ更新する。
  - hash identity は変えない。
  - `folder.date` / directory mtime は folder pipeline の metadata freshness に従って更新する。
- BMS 本体が未変更でも、chart directory 直下の `.txt` 追加・削除は `song.txt` 更新対象にする。
  - text group snapshot / directory metadata freshness を持ち、差分があれば該当 directory の BMS row を
    `txt` 更新対象にする。
- path が消え、同じ MD5 の新 path が同時に追加された場合は move/relink として扱う。
  - LR2 `song.path` は primary key なので、delete + insert ではなく維持列を旧 row から新 row へ引き継ぐ。
  - `favorite` / `adddate` / `tag` を維持する。
- 同一 MD5 の deleted / added が複数ある場合は自動 relink しない。
  - 移動先 path に既存 row がある場合も自動 relink しない。
  - relink 不成立時は `lr2_song_relink_ambiguous` warning log を出し、削除対象は通常 delete、
    追加対象は既存 row merge または新規 add として処理する。維持列の引き継ぎは行わない。
- path が消えた場合は owned collection から unregister し、LR2 `song` から削除する。
- path が追加された場合は通常の追加 parse を行う。

性能上の条件:

- 200k 件級で毎回全 BMS bytes を読む設計にはしない。
- high precision mtime / size / path の軽量 metadata を scan contract に入れるか、DB 側に
  metadata snapshot を持つ。
- high precision mtime は Windows FILETIME ticks 相当で保持する。
- hash 読みは mtime / size 変化対象だけに限定する。
- mtime が保持された外部コピーや同秒更新は完全には検出できないため、明示的な全譜面再スキャンを
  force verify として残す。
- text group の freshness は BMS file metadata とは別に扱う。

### `song` row の列所有権

完全生成では `InsertOrReplace` で LR2 `song` row を丸ごと置換する前提にしない。
LR2 / ユーザーが触る可能性のある列と、BeMusicSeeker が再生成する列を分ける。

BeMusicSeeker が生成・更新する列:

- `hash`
- `title`
- `subtitle`
- `genre`
- `artist`
- `subartist`
- `path`
- `folder`
- `parent`
- `stagefile`
- `banner`
- `backbmp`
- `level`
- `difficulty`
- `maxbpm`
- `minbpm`
- `mode`
- `judge`
- `longnote`
- `bga`
- `random`
- `date`
- `txt`
- `karinotes`
- `type`
- `exlevel`

既存値を維持する列:

- `favorite`
- `adddate`。既存 row があれば維持し、新規 row だけ現在時刻を入れる。
- `tag`。BeMusicSeeker が明示的にタグ編集を扱うまで LR2 / user owned とする。

DB write 方針:

- 新規 row は full insert でよい。
- 既存 row は列単位 merge または targeted `UPDATE` に寄せる。
- やむを得ず `InsertOrReplace` を使う場合は、既存 row から維持列を読んでから書く。
- path move/relink では旧 path row の維持列を新 path row へ引き継ぐ。
- `favorite` / `adddate` / `tag` の維持をテストで固定する。

### `folder` row の所有権

完全生成では `folder` row の stale pruning が重要になる。古い root row や `date = 0`
row が残ると、manual-only でも LR2 が不要な scan に入る。

方針:

- BeMusicSeeker が生成する normal folder row と BeMusicSeeker が出力した `.lr2folder` row を
  app-owned として扱う。
- app-owned row の管理には app-owned manifest table を必ず追加する。
  - 例: `lr2_generated_folder_manifest(path TEXT PRIMARY KEY, kind TEXT, source_key TEXT, updated_at INTEGER)`
  - 未リリース前提でも、stale pruning、resume、rollback のために所有権を明示する。
- manifest には generation run id / source root signature / generator version を持たせる。
- manifest がない既存 row を不用意に削除しない。
- 既存 row のうち、現在の LR2 root / BMS chart directory set から deterministic に一致する
  root / ancestor row は初回 backfill 時に app-owned として採用する。
- root / ancestor / normal folder row は、現在の LR2 BMS root と BMS chart directory set から
  deterministic に再生成する。
- `.lr2folder` row は、BeMusicSeeker が出力・管理する custom folder を中心に扱う。
- LR2 root 配下の任意 `.lr2folder` を広域探索しない。
- `.lr2folder` の parse 対象は BeMusicSeeker-owned manifest と既知 playlist 出力に限定する。
- 任意 `.lr2folder` の広域探索はこの計画では実装しない。必要になった場合は、source ownership
  と明示設定を持つ別機能として計画する。

削除ルール:

- 生成対象から消えた app-owned normal folder row は削除する。
- BeMusicSeeker が出力した `.lr2folder` を削除した場合、対応する app-owned `folder` row も削除する。
- ユーザー管理かどうか不明な `.lr2folder` row は削除しない。
- unknown root row または `date = 0` row が残り、LR2 startup scan 抑止に影響する場合は
  silent retention しない。初期実装では自動削除せず warning と起動前 block の対象にし、
  cleanup は明示操作として別導線で実行する。
  - cleanup 導線は Phase 8 の初期実装に含める。
  - cleanup は全 `folder` row 削除ではなく、diagnostic で列挙した startup-scan blocker row だけを
    backup 作成後に削除する。
  - cleanup できない、または cleanup 後も blocker が残る場合は LR2 起動を継続して block する。

### LR2 compatibility warning の scope

LR2 compatibility warning は BMS のみを対象にする。bmson は対象外。

評価対象:

- BMS chart file path
- BMS chart directory の `folder\*.*` scan path
- BMS resource reference の resolved path
- BMS root folder / ancestor folder / BeMusicSeeker-owned `.lr2folder`
- shared `maintenance` table の LR2 columns は bmson row では `NULL` / ignored とし、
  projection 側でも chart kind gate を置く。

表示方針:

- 完全生成有効時は maintenance fact から warning を投影する。
- 完全生成無効時は、既存の `Lr2PathEncodingUnsupported` など軽い path warning だけ維持する。
- スタンドアロンモードでは `LR2非対応パス` ツリーを出さない。
- warning の ignore は resource health と独立させる。
  - `is_lr2_compatibility_warning_ignored` が true の BMS は、warning digest / tooltip / `LR2非対応パス`
    membership から除外する。
  - raw maintenance facts は保持し、ignore 解除時に再評価なしで表示へ戻せるようにする。

### LR2 起動前 workflow

完全生成が有効な場合、BeMusicSeeker から LR2 を起動する前に以下を満たす。

- 完全生成が未完了なら、起動前 workflow で backfill を開始または再開し、完了するまで LR2 起動を止める。
- backfill が必要な場合は、警告・進捗・キャンセル可能性を表示する。
- LR2 側の DB 自動更新設定が手動のみでない可能性がある場合は注意を出す。
  - 現行 `LR2Config` wrapper には autoreload 判定 API がないため、実装時に config 要素名と値を確認する。
- LR2 が起動中の場合の DB write 競合を検出する。
  - SQLite busy timeout / file lock / transaction 失敗時の扱いをログに出す。
- 完全生成 status は durable に保存する。
  - 保存先は LR2 `song.db` 内の BeMusicSeeker-owned metadata table とする。
  - schema version
  - generator version
  - parser version
  - LR2 BMS root set signature
  - 完全生成設定値
  - folder manifest generation id
  を含め、いずれかが変わった場合は completed を無効化する。
- generator version は `song` / `folder` 生成列、LR2 folder manifest、warning projection の意味が変わる時に bump する。
- parser version は BMS metadata parse、text group parse、raw resource reference parse、CRC 入力正規化の意味が
  変わる時に bump する。
- backfill は startup ready / operable をブロックしない。初期化完了後に background workflow として開始し、
  LR2 起動要求が来た時点で未完了なら同じ workflow に join して完了まで待つ。

## LR2 互換性評価

### 既存実装との統合

現行実装には LR2 非対応パス用の処理がすでにある。中心は
`Lr2SongFolderParentNormalizer`:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\Lr2SongFolderParentNormalizer.cs:10`。

完全 `song.db` 生成では、LR2 path 判定と CRC 計算を点在させず、
`Lr2CompatibilityEvaluator` のようなサービスに集約する。既存の
`Lr2SongFolderParentNormalizer` はその evaluator の結果を `BMSFile` に適用する adapter
として残す。

統合後の役割:

- `EvaluateChartPath(path)`
  - CP932 encode 可否、chart path byte length、`folder\*.*` scan path byte length、
    `folder` CRC、`parent` CRC を返す。
- `EvaluateFolderPath(path)`
  - root / ancestor / `.lr2folder` path の CP932 encode 可否と scan path byte length を返す。
- `EvaluateResourceReferences(chartPath, rawResourceReferenceSnapshot)`
  - resource reference の CP932 encode 可否、resolved path byte length、正規化不能件数を返す。
- `ApplyToSong(BMSFile, evaluation)`
  - `song.folder` / `song.parent` を設定し、`ChartWarningKind` を更新する。

`ChartResourceSnapshot` は lookup 用に正規化された情報が中心であり、LR2 byte length warning
には不足する。LR2 compatibility 用には同じ parse pass で、少なくとも次を保持する。

- raw directive kind
- raw path
- normalized path with extension
- resolved absolute path
- normalization status
- parent traversal / unsupported state

### maintenance に保存する LR2 互換性 snapshot

LR2 互換性 warning は、起動時に毎回 BMS を全量読み込んで再判定しない。
BMS ファイルを読む機会に `maintenance` へ同期し、起動時・一覧表示時は保存済み結果を使う。

`maintenance` の freshness は既存の `path` / `hash` を基本にし、BMS metadata snapshot を
追加する場合は `path + hash + high precision mtime + size` で判断する。

推奨列:

- `lr2_path_warning_flags INTEGER NULL`
- `lr2_path_cp932_bytes INTEGER NULL`
- `lr2_folder_scan_cp932_bytes INTEGER NULL`
- `lr2_resource_warning_flags INTEGER NULL`
- `lr2_resource_warning_count INTEGER NULL`
- `lr2_resource_warning_examples TEXT NULL`
- `lr2_resource_max_relative_cp932_bytes INTEGER NULL`
- `lr2_resource_max_resolved_cp932_bytes INTEGER NULL`
- `is_lr2_compatibility_warning_ignored BOOLEAN NOT NULL DEFAULT 0`
- `lr2_evaluated_mtime INTEGER NULL`
- `lr2_evaluated_file_size INTEGER NULL`

schema migration:

- 既存 table を drop / rebuild しない。
- `PRAGMA table_info(maintenance)` で既存列を確認し、足りない列を
  `ALTER TABLE maintenance ADD COLUMN ...` で追加する。
- app schema version / preflight に組み込む。
- maintenance hydrate / write は `EnsureMaintenanceSchemaVNext` 相当の schema ensure より前に実行しない。
- `BMSFileMaintenanceInfo`、`MaintenanceRowsEquivalent`、`CloneMaintenanceInfo` に新列を追加する。

保存するものは「全 resource reference の完全リスト」ではなく、警告表示と診断に必要な
サマリを基本にする。全参照を JSON で保持すると DB サイズが大きくなりやすい。

## 実装フェーズ

### Phase 0: Golden fixture と contract 固定

実装を始める前に、LR2 互換性の入力・出力を fixture で固定する。

対象:

- `AssignCRC32("ROOT")`
- chart directory + trailing slash + NUL の CRC
- parent directory + trailing slash + NUL の CRC
- drive root
- UNC path
- CP932 path
- CP932 非対応 path
- byte length boundary
- `GetUnixtimeFromFiletime` 相当の Unix 秒
- `folderinfo.txt`
- `.lr2folder`
- manual-only で `parent = ROOT OR date = 0` の row が scan 対象になること

成果物:

- `Lr2CompatibilityEvaluator` 用 golden tests
- `Lr2FolderRowGenerator` 用 golden tests
- コピーした `song.db` での dry-run 検証手順

### Phase 1: BMS 変更検出を mtime/size/hash ベースに統一する

目的:

- 完全生成の前提として、既存 BMS の変更を検出できるようにする。
- スタンドアロンモードでも同じ変更検出 contract を使えるようにする。

実装:

- scan result または DB-side snapshot に BMS high precision mtime / size を持たせる。
- DB row の path / high precision mtime / size が一致する場合は unchanged。
- mtime または size が変わった場合だけ `ChartFileSnapshot` を読み、MD5 を比較する。
- MD5 が同じなら `song.date` / mtime / size snapshot のみ更新する。
- MD5 が違うなら再parseする。
- deleted path と added path の MD5 が同じ場合は move/relink として維持列を引き継ぐ。

テスト:

- path + high precision mtime + size 一致は parse しない。
- mtime/size changed + MD5 same は `song.date` のみ更新する。
- mtime/size changed + MD5 changed は BMS row / chart_info / maintenance を更新する。
- path move + MD5 same で `favorite` / `adddate` / `tag` が維持される。
- bmson の現行更新検出と矛盾しない。

### Phase 2: `song` row merge / ownership を導入する

目的:

- `InsertOrReplace` による既存 LR2/user-owned column の破壊を避ける。

実装:

- `Lr2SongRowMerger` または `Lr2SongDbWriter` を追加する。
- 既存 row がある場合は維持列を読んでから generated columns を更新する。
- generated column ごとの targeted `UPDATE` を基本にし、full row replace は使わない。
- `favorite` / `adddate` / `tag` は維持する。

テスト:

- existing `favorite` が維持される。
- existing `adddate` が維持される。
- existing `tag` が維持される。
- generated columns は更新される。
- 新規 row は必要列が null/0 不正にならない。

### Phase 3: text group と raw resource reference snapshot を scan/parse contract に追加する

目的:

- `song.txt` と LR2 resource compatibility warning を、後追い全件 filesystem check なしで計算する。

text group:

- BMS chart directory 直下の `.txt` 有無だけを扱う。
- descendant / ancestor の `.txt` を拾わない。
- 完全生成無効時は走らせない。

raw resource reference:

- BMS parse pass で raw directive と resolved path を保持する。
- `ChartResourceSnapshot` の lookup key とは別投影にする。

性能条件:

- 10M resource で managed materialize が増えすぎないよう、Everything native bridge と
  fallback の意味を揃える。
- native bridge の layout version / result version を log に出す。

テスト:

- Everything / managed fallback の text group parity。
- chart directory 直下の `.txt` のみ `song.txt=1`。
- raw resource path の CP932 length が拡張子付きで評価される。

### Phase 4: LR2 compatibility evaluator と maintenance projection を追加する

目的:

- path / byte length / resource reference の LR2 compatibility を一箇所で評価する。

実装:

- `Lr2CompatibilityEvaluator`
- `Lr2CompatibilityWarningProjection`
- `ChartWarningKind.Lr2PathTooLong`
- `ChartWarningKind.Lr2ResourcePathUnsupported`
- `ChartWarningKind.Lr2ResourcePathTooLong`

scope:

- BMS only。
- bmson は対象外。

テスト:

- CP932 非対応 BMS path が warning になる。
- BMS path byte length boundary が warning になる。
- resource resolved path の CP932 非対応 / byte length boundary が warning になる。
- bmson は warning 対象にならない。
- ignore flag の表示・一覧 membership の挙動が固定される。

### Phase 5: `song` row enricher を追加する

目的:

- LR2 起動時 scan に頼らず、LR2 `song` columns を埋める。

入力:

- `BMSFile`
- `ChartFileSnapshot`
- text group result
- chart_info / detailed parser result
- existing song row

生成列:

- `date`
- `folder`
- `parent`
- `type`
- `txt`
- LR2 numeric columns

詳細解析:

- `maxbpm` / `minbpm` / `longnote` / `bga` / `random` / `karinotes` /
  `exlevel` / `difficulty` は BeMusicSeeker の detailed parser / `chart_info` を正とする。
- LR2 の細かいバグ再現は目標にしない。

テスト:

- 新規 BMS 行の `date` が snapshot mtime になる。
- `txt` が text group から正しく反映される。
- BMS 本体未変更でも `.txt` 追加・削除で `song.txt` だけ更新される。
- detailed parser 由来 column が埋まる。
- CP932 非対応 path でもクラッシュしない。

### Phase 6: `folder` row generator と ownership manifest を追加する

目的:

- root / ancestor / normal folder rows を deterministic に生成する。
- stale app-owned rows を安全に prune する。

入力:

- LR2 BMS root directories
- BMS chart directories
- directory mtime
- `folderinfo.txt`
- existing folder rows
- app-owned folder manifest

性能条件:

- chart ごとに `Directory.GetLastWriteTime` / `folderinfo.txt` check を行わない。
- root / ancestor / BMS chart directory を unique directory set に dedupe してから metadata を取得する。
- directory metadata と `folderinfo.txt` / `.lr2folder` existence は Everything / managed fallback で
  意味が揃うよう parity test を置く。

生成ルール:

- directory の `path` は trailing separator 付き。
- root folder の `parent` は `AssignCRC32("ROOT")`。
- non-root folder の `parent` は parent folder path + trailing slash + NUL の LR2 CRC。
- `date = Directory.GetLastWriteTime(...).ToUnixtime()`。
- `adddate` は existing row があれば維持し、新規だけ現在時刻。
- `folderinfo.txt` があれば LR2 風に parse する。
- `folderinfo.txt` がなければ directory name を `title` にする。
- `date = NULL` / `date = 0` は生成しない。

テスト:

- root folder row の `parent` が ROOT CRC。
- root folder row の `date` が directory mtime。
- nested folder row の `parent` が親 directory CRC。
- stale app-owned row が prune される。
- user-owned / unknown row は削除されない。

### Phase 7: `.lr2folder` DB 同期を追加する

目的:

- BeMusicSeeker が出力した `.lr2folder` を LR2 scan に任せず `folder` table へ同期する。

対象:

- BeMusicSeeker が出力・管理する `.lr2folder`。
- LR2 root 配下の任意 `.lr2folder` の全探索は初期対象にしない。
- parse 対象は BeMusicSeeker-owned manifest と既知 playlist 出力に限定する。
- 任意 `.lr2folder` の広域探索はこの計画では実装しない。

parse directive:

- `#TITLE`
- `#SUBTITLE`
- `#CATEGORY`
- `#INFORMATION_A`
- `#INFORMATION_B`
- `#COMMAND`
- `#MAXTRACKS`
- `#BANNER`
- `#CUSTOMFOLDER`

テスト:

- playlist 由来 `.lr2folder` 出力で `folder` row が入る。
- `.lr2folder` 削除で app-owned row が消える。
- unknown `.lr2folder` row は誤削除しない。
- Shift_JIS 出力した `.lr2folder` を同じ解釈で parse できる。

### Phase 8: LR2 起動前 workflow と backfill UI を追加する

目的:

- 完全生成が未完了の状態で LR2 を起動しない。
- backfill が発生する場合にユーザーへ見える形にする。

実装:

- 完全生成 status を保持する。
- backfill needed / running / completed / failed を log と UI に出す。
- backfill progress は全体合算 total と stage 別 processed count の両方を表示する。
- LR2 起動前に incomplete / failed なら警告する。
- LR2 config の auto update 設定は config 要素名を確認して検出する。検出できない場合は
  「自動更新設定を確認できない」warning を出し、BeMusicSeeker 側からは config を自動変更しない。
- backfill 開始前に LR2 `song.db` 全体の backup を作る。
  - backup は LR2 `song.db` と同じ directory の `bemusicseeker-backup` subdirectory に
    `song.db.<yyyyMMddHHmmss>.bak` として作る。
  - backup 世代は最新 2 件を保持し、それより古い BeMusicSeeker 作成 backup は成功後に削除する。
  - backup 作成に失敗した場合は DB write を開始せず、backfill を failed にする。
- write は per-chunk transaction とし、run id/status を durable に更新する。
- cancel 後の partial write は incomplete として扱い、LR2 起動前に警告する。
- app-owned folder rows は manifest generation id で resume / rollback できるようにする。
- backup / restore の対象は LR2 `song.db` 全体とする。
  - manifest / durable status / maintenance schema 変更は同じ DB 内に置き、backup から戻せば一貫して戻る。
  - 外部設定ファイルや `.lr2folder` 実ファイルは backup 対象外なので、必要な場合は別操作で扱う。

テスト:

- backfill 不要時は警告なし。
- backfill 必要時は警告と進捗が出る。
- failed 状態で LR2 起動導線が警告する。
- cancel 後に incomplete status が残り、再開または rollback できる。

### Phase 9: migration / backfill

対象:

- `song.date`
- `song.adddate`
- `song.txt`
- LR2 numeric columns
- `folder` hierarchy
- `.lr2folder` 由来 folder rows
- LR2 compatibility maintenance columns

方針:

- chunked / idempotent。
- changed-only write。
- preflight backup / restore point を作る。
- run id / durable status を持つ。
- `favorite` / `adddate` / `tag` を維持。
- CP932 非対応 BMS row は BeMusicSeeker DB から削除しない。
- backfill 再実行で追加差分が出ない。

テスト:

- `date = null` の既存 row が mtime で backfill される。
- 維持列が維持される。
- CP932 非対応 BMS row が削除されない。
- 2 回目 backfill が no-op になる。
- partial run 後に resume できる。
- backup から restore できる。

## データマッピング早見表

### `song`

- `hash`: snapshot MD5。
- `path`: full BMS path。
- `folder`: LR2 互換 path の場合、chart directory path の LR2 CRC。
- `parent`: LR2 互換 path の場合、parent directory path の LR2 CRC。
- `title`, `subtitle`, `genre`, `artist`, `subartist`, `level`, `difficulty`,
  `judge`, `mode`, `stagefile`, `banner`, `backbmp`:
  lightweight parser の値でよい。
- `maxbpm`, `minbpm`, `longnote`, `bga`, `random`, `karinotes`, `exlevel`:
  detailed parser / `chart_info` から埋める。
- `date`: snapshot last write time の Unix 秒。
- `adddate`: 新規 row は現在時刻。既存 row は維持。
- `favorite`: 既存値を維持、なければ `0`。
- `tag`: 既存値を維持。
- `type`: `0`。
- `txt`: text group で BMS chart directory 直下に `.txt` があれば `1`、なければ `0`。

### `folder`

- `path`: trailing separator 付き directory path、または `.lr2folder` file path。
- `title`: directory name、`folderinfo.txt #TITLE`、または `.lr2folder #TITLE`。
- `subtitle`: directive があればその値、なければ empty。
- `category`: `#CATEGORY` または `#GENRE`。
- `info_a`: `#INFORMATION_A`。
- `info_b`: `#INFORMATION_B`。
- `command`: `#COMMAND` または `#TAG`。
- `type`: normal folder は `1`、custom folder は `2`、特殊 root custom folder は
  `3` / `4` / `6`。
- `banner`: `#BANNER`。
- `parent`: root は ROOT CRC、それ以外は containing folder CRC。
- `date`: directory または `.lr2folder` file mtime の Unix 秒。
- `max`: `#MAXTRACKS` または `#PLAYLEVEL`。なければ `0`。
- `adddate`: 新規 row は現在時刻。既存 row は維持。

## リスクと注意点

- BMS 変更検出は完全生成の前提条件。path added/deleted だけでは不十分。
- mtime だけではなく high precision mtime + size を扱う。これでも mtime preserved copy は
  完全には検出できないため、force rescan を残す。
- Everything と managed fallback の結果は意味的に揃える。片方だけ text group や metadata
  を返す状態にしない。
- text group だけでなく、directory mtime、`folderinfo.txt`、`.lr2folder` の existence /
  mtime についても native / fallback parity test を置く。
- 完全生成設定が有効な場合だけ重い列挙を増やす。無効時の既存動作を重くしない。
- LR2 は legacy narrow path を多用する。BeMusicSeeker 内では unsupported path を保持して
  よいが、LR2 起動時 scan に踏ませる row は慎重に扱う。
- CP932 byte length の警告と Unicode string length の警告は分ける。
- `ChartResourceSnapshot` の normalized resource key を LR2 warning 判定の raw path として
  流用しない。
- folder row は ownership manifest なしで削除しない。
- LR2 が起動中の `song.db` 書き込みは競合する可能性があるため、busy/lock 失敗を明示ログにする。

## 作業エージェント向け実装順

1. Phase 0 の golden fixture を追加する。
2. BMS 変更検出を mtime/size/hash ベースに統一する。
3. `song` row merge / ownership writer を追加する。
4. text group と raw resource reference snapshot を scan/parse contract に追加する。
5. `Lr2CompatibilityEvaluator` を追加する。
6. `maintenance` に LR2 compatibility warning 用の列を追加し、schema preflight に組み込む。
7. `Lr2CompatibilityWarningProjection` を追加する。
8. `Lr2SongRowEnricher` を追加し、BMS file diff path に組み込む。
9. `Lr2FolderRowGenerator` と folder ownership manifest を追加する。
10. `.lr2folder` parser と app-owned `folder` row sync を追加する。
11. LR2 起動前 workflow と backfill progress / warning を追加する。
12. migration / backfill を追加する。
13. コピーした `song.db` で統合確認する。
14. LR2 を manual-only で起動し、未変更 root で再帰スキャンに入らないことを確認する。
