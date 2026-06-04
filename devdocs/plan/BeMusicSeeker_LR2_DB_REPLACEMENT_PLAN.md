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
DBを BeMusicSeeker が安定して生成する」ことを主目的にします。

## 目標

BeMusicSeeker は譜面管理アプリなので、LR2 で完全に読めない譜面も DB から消さない。
その一方で、LR2 向けの `song` / `folder` テーブルはできるだけ完全に生成し、LR2 側の
起動時スキャンによるエラーや長時間処理を避ける。

目標状態:

- LR2 のデータベース自動更新は「手動のみ」で運用できる。
- BeMusicSeeker が `song` / `folder` テーブルを更新する。
- LR2 起動時の root folder チェックで `date` が一致し、再帰スキャンに入らない。
- LR2 非対応パスの譜面は BeMusicSeeker の DB には残し、LR2 互換性警告を出す。
- 非対応譜面を LR2 のプレイ画面から開いた場合に失敗することは許容する。
- ただし、LR2 起動時のファイル走査で非対応パスを踏ませないことを優先する。

## 設定方針

完全な `song.db` 生成は設定化する。ただし、LR2連携ユーザーにとって望ましい既定動作
なので、**デフォルト有効**にする。

推奨UI:

- 設定画面の「動作モード」内、「LR2と連携する」の近くに置く。
- 添付画像の UI でいうと、`LR2と連携する` の直下または `詳細` の中に置く。
- 表示名例:
  - `LR2用song.dbをBeMusicSeekerで完全生成する`
  - または `LR2起動時のDB自動更新をBeMusicSeekerで代替する`
- 初期値:
  - `OperationModeLR2DB == true` の場合は有効。
  - スタンドアロン運用では無効、または設定自体を非表示にしてもよい。

この設定はファイル列挙の方法にも影響する。完全生成が有効な場合は、従来の譜面検出に
加えて、LR2 `song` / `folder` に必要な補助情報も列挙・収集する。

設定別の列挙方針:

- 完全生成が無効:
  - 現行に近い軽量スキャンを維持する。
  - chart / audio / image / movie の列挙でよい。
- 完全生成が有効:
  - chart / audio / image / movie に加えて text group を列挙する。
  - `.lr2folder` と `folderinfo.txt` も `folder` テーブル生成の入力として扱う。
  - root folder と chart ancestor directory の mtime を取得する。
  - LR2 互換性警告用に CP932 変換可否と CP932 バイト長を評価する。

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
- BMS ディレクトリに `*.txt` があれば `song.txt = 1` になる。
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

## LR2 互換性警告の方針

LR2 非対応パスの譜面も BeMusicSeeker の DB から削除しない。代わりに
`ChartWarningCategory.Lr2Compatibility` の warning として表示する。

最低限の警告対象:

- chart file path が Windows CP932 に厳密エンコードできない。
- chart file path の CP932 byte length + NUL が `>= 260`。
- chart directory の `folder\*.*` scan path の CP932 byte length + NUL が `>= 260`。
- BMS / bmson 内 resource reference を chart directory と結合した resolved path が
  CP932 非対応。
- resolved resource path の CP932 byte length + NUL が `>= 260`。
- 正規化不能な resource reference がある。

CP932 判定は code page 932 を明示する:

```csharp
Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
```

### 既存実装との統合

現行実装には LR2 非対応パス用の処理がすでにある。中心は
`Lr2SongFolderParentNormalizer`:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\Lr2SongFolderParentNormalizer.cs:10`。

この normalizer は chart path を strict Shift_JIS で encode し、chart directory と
parent directory の LR2 CRC32 を計算して `song.folder` / `song.parent` を補完する。
失敗した場合は `folder` / `parent` を `NULL` にし、
`ChartWarningKind.Lr2PathEncodingUnsupported` を設定する。

呼び出し箇所は複数ある:

- `UpsertSongs` / file diff commit / path replace:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryDbGateway.cs:171`
- 起動時の song table normalize:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryInitializationService.cs:2435`
- 起動時の relative path / CRC normalize:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryInitializationService.cs:2494`
- file rename 時の直 CRC 計算:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryStateApplier.cs:302`
- folder rename 時の直 CRC 計算:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryDbGateway.cs:1287`

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
- `EvaluateResourceReferences(chartPath, ChartResourceSnapshot)`
  - resource reference の CP932 encode 可否、resolved path byte length、正規化不能件数を返す。
- `ApplyToSong(BMSFile, evaluation)`
  - `song.folder` / `song.parent` を設定し、`ChartWarningKind` を更新する。

`ChartWarningKind.Lr2PathEncodingUnsupported` は chart path の CP932 非対応 warning として
継続利用する。path length と resource reference 互換性には
`ChartWarningKind.Lr2PathTooLong`、`ChartWarningKind.Lr2ResourcePathUnsupported` のような
LR2 互換性カテゴリの kind を追加する。既存の `ChartWarningKind.UnsupportedResourcePath` は
install estimation 用の warning なので、LR2 起動互換性の warning には流用しない。

warning 表示は既存の投影モデルに合わせる。現行の欠落リソース警告は
`ResourceHealthIndexSnapshot` が `maintenance` から `ChartWarning` へ変換され、
`ChartWarningProjectionFormatter` で通常 warning と合成される:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\ResourceHealthWarningProjection.cs:29`、
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\ViewModels\ChartWarningProjectionFormatter.cs:10`。
LR2 互換性警告も同様に `Lr2CompatibilityWarningProjection` を作り、`maintenance` の
fact columns から `ChartWarning` を生成して合成する。DB には localized message を保存せず、
warning text は `ChartWarningDefinition` と resources から生成する。

`LR2非対応パス` の一覧もこの warning projection に合わせる。現行の
`ChartFilesUnregistered` は `parent` が空の BMS を抽出している:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:1383`。
完全生成後は `parent` 空は実装上の副作用にすぎないため、一覧の membership は
`Lr2CompatibilityWarningProjection` または共有 predicate の
`HasLr2CompatibilityIssue(chart)` に寄せる。これにより、CP932 非対応だけでなく
path length や resource reference の LR2 非対応も同じ入口で扱える。

### 起動時 path / CRC 検査の整理

現行の起動時 normalize は、`song.folder` / `song.parent` が CRC らしい値でも
`ApplyIfMissingOrInvalid` を通す。これにより CP932 encode と directory CRC 計算が毎回走る。
完全生成が有効な場合は、起動時の全件再計算を避ける。

方針:

- 新規追加・譜面更新・backfill・path 変更のタイミングで `EvaluateChartPath` を実行し、
  `song.folder` / `song.parent` と `maintenance` の LR2 path warning columns を更新する。
- 起動時 hydrate では、`maintenance.path` と `maintenance.hash` が現在の chart と一致し、
  LR2 path warning columns が評価済みなら保存済み結果を warning 表示に使う。
- 評価済みで `song.folder` / `song.parent` が CRC らしい値なら、起動時の
  `ApplyIfMissingOrInvalid` による CRC 再計算を省略する。
- 評価結果が未保存、または `folder` / `parent` が欠けている row は、既存 normalizer に
  fallback して補完する。
- 完全生成が無効な場合や、外部で直接変更された DB を読む場合は、現行 normalize を維持する。

### maintenance に保存する LR2 互換性 snapshot

LR2 互換性 warning は、起動時に毎回 BMS / bmson を全量読み込んで再判定しない。
譜面ファイルを読む機会に `maintenance` へ同期し、起動時・一覧表示時は保存済み結果を使う。

既存の `maintenance` は `path` が primary key、`hash` が indexed column で、resource
health 用の定義数・存在数を保持している:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\LR2\LR2SongDBExtended.cs:44`。
hydrate 時も `path` が一致し、かつ現在の chart hash と `maintenance.hash` が一致する
row だけを有効な maintenance として接続している:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:5867`。

LR2 互換性 snapshot の identity / freshness は既存の `maintenance.path` と
`maintenance.hash` を source of truth にする。判定ロジックを変えた場合は、既存の
「全譜面を再スキャン」による maintenance 全量再生成で反映する:
`D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BMSLibrary.cs:11007`。

推奨列:

- `lr2_path_warning_flags INTEGER NULL`
  - chart path と chart directory scan path に対する bit flag。
  - `NULL` は未検査、`0` は検査済みで問題なし。
- `lr2_path_cp932_bytes INTEGER NULL`
  - chart file path の CP932 byte length + NUL。
- `lr2_folder_scan_cp932_bytes INTEGER NULL`
  - chart directory の `folder\*.*` scan path の CP932 byte length + NUL。
- `lr2_resource_warning_flags INTEGER NULL`
  - resource reference に対する bit flag。
  - `NULL` は未検査、`0` は検査済みで問題なし。
- `lr2_resource_warning_count INTEGER NULL`
  - LR2 resource path compatibility 上の問題数。
- `lr2_resource_warning_examples TEXT NULL`
  - JSON など。表示用に最大数件だけ保持する。
  - localized message ではなく、kind / raw path / normalized path / byte length などの
    structured facts を保存する。
- `lr2_resource_max_relative_cp932_bytes INTEGER NULL`
  - resource reference 単体の最大 CP932 byte length。
- `lr2_resource_max_resolved_cp932_bytes INTEGER NULL`
  - chart directory と結合した resolved path の最大 CP932 byte length。
- `is_lr2_compatibility_warning_ignored BOOLEAN NOT NULL DEFAULT 0`
  - 欠落リソース警告用の `is_files_warning_ignored` とは分ける。

保存するものは「全 resource reference の完全リスト」ではなく、警告表示と診断に必要な
サマリを基本にする。全参照を JSON で保持すると DB サイズが大きくなりやすい。
フォルダ移動後にファイル再読込なしで厳密再計算したい場合は、別途
`maintenance_resource_reference` のような app-owned table を作る余地を残す。

schema migration:

- 現状の `CreateTable<LR2SongDBExtended.maintenance>()` は、既存 table への列追加を
  保証する migration としては弱い。
- `PRAGMA table_info(maintenance)` で既存列を確認し、足りない列を
  `ALTER TABLE maintenance ADD COLUMN ...` で追加する。
- 既存 row を drop / rebuild しない。`is_files_warning_ignored` などユーザー操作由来の
  状態を失わないため。
- `BMSFileMaintenanceInfo`、`MaintenanceRowsEquivalent`、`CloneMaintenanceInfo`、
  必要なら UI 用 snapshot / PropertyChanged に新列を追加する:
  `D:\work\BeMusicSeeker-decomp\BeMusicSeeker\Models\BmsLibraryInternal\BmsLibraryMaintenanceService.cs:1241`。

同期タイミング:

- 新規追加譜面:
  - `ChartFileSnapshot` を読んで `BMSFile.CreateBMSFileFromSnapshot` するタイミングで
    chart path と resource reference を同時に評価し、`maintenance` へ upsert する。
- 譜面内容変更:
  - hash / mtime の変化を検出して再parseしたタイミングで `maintenance` を更新する。
- フォルダ名変更・移動:
  - chart path と `maintenance.path` を更新し、LR2 path warning columns を再計算する。
  - resource resolved path が変わるため、resource warning columns も再計算する。
  - 移動処理内で再計算できない場合は resource warning columns を `NULL` にし、
    background maintenance で chunked に再評価する。
- LR2 root 設定変更:
  - root path 自体の CP932 / byte length 警告を再評価する。
  - chart path が変わらない限り chart / resource snapshot は stale にしなくてよい。

評価対象:

- chart file path
- chart directory の `folder\*.*` scan path
- `#WAVxx`
- `#BMPxx`
- `#STAGEFILE`
- `#BANNER`
- `#BACKBMP`
- bmson の audio / bga / movie / preview / optional image 相当

## 実装フェーズ

### Phase 1: LR2 `song` 行の補完サービスを追加する

サービス例:

- `Lr2SongRowEnricher`
- `Lr2SongDbCompatibilityEnricher`

入力:

- `BMSFile song`
- `ChartFileSnapshot snapshot`
- 現在時刻の Unix 秒
- 可能なら同じ snapshot から作った `chart_info`
- chart directory context
- text group の列挙結果

`InsertOrReplace` 前に埋める値:

- `date = snapshot.LastWriteTimeUtc` の Unix 秒。
- `adddate = now`。既存行更新時は既存の非 null `adddate` をできれば維持する。
- `favorite = 0`。既存値がある場合は維持する。
- `type = 0`。
- `txt = chart directory に *.txt があれば 1、なければ 0`。
- `maxbpm`, `minbpm`, `longnote`, `bga`, `random`, `karinotes`,
  `exlevel`, `difficulty` は BeMusicSeeker の詳細解析結果から埋める。
- 詳細解析で取れない値は LR2 に渡して安全な non-null default を入れる。

候補となる呼び出し位置:

- `ParseFileDiffCandidate` で `BMSFile.CreateBMSFileFromSnapshot(candidate.Snapshot)`
  の直後。
- または `FlushFileDiffParsedBatch`。`InlineBmsParseCandidate` が `File` と
  `Snapshot` の両方を持つため、ここでも可能。

設計判断:

- `maxbpm` / `minbpm` / `longnote` / `bga` / `random` / `karinotes` /
  `exlevel` / `difficulty` は LR2 のバグや癖を厳密再現しなくてよい。
- BeMusicSeeker 側の beatoraja 風詳細パーサー、または `chart_info` の解析結果を
  正として埋める。
- LR2 が通常 insert する `song` カラムは、意図的に unsupported とする場合を除き
  null にしない。

テスト:

- 新規 BMS 行の `date` が snapshot mtime になる。
- 新規 BMS 行の `adddate` が non-null になる。
- 既存行更新時に `favorite` が維持される。
- 既存行更新時に可能なら `adddate` が維持される。
- `txt` が text group から正しく反映される。
- CP932 非対応パスでもクラッシュせず、警告が残る。

### Phase 2: `*.txt` スキャンを text group として正式実装する

`*.txt` 有無の取得は、最初から scan contract に含める方針にする。

理由:

- 対象ディレクトリ数が 30,000 程度になり得る。
- 譜面数は 210,000 程度、resource file は 10,000,000 以上になり得る。
- chart directory ごとの `Directory.EnumerateFiles("*.txt")` 後追い確認は、
  実装は簡単だが最終的に性能上のボトルネックになりやすい。
- Everything native bridge を使う場合、chart/audio/image/movie と同じタイミングで
  text file を拾う方がスケールしやすい。

正式方針:

- `ChartDirectoryScanBuilder.CreateEnumerationGroups` に text group を追加する。
- text group は少なくとも `.txt` を対象にする。
- `ChartScanResult` に text 情報を追加する。
  - 推奨は `HasTextFileByChartDirectory`。
  - 将来 resource health に使うなら `TextRelativePathHashesByChartDirectory` でもよい。
- Everything scan でも text query を追加する。
- managed fallback でも同じ group を返す。
- 完全 `song.db` 生成設定が無効の場合は、text group を省略できるようにする。

候補設計:

```csharp
public Dictionary<string, bool> HasTextFileByChartDirectory { get; set; }
```

または、既存 resource hash 形式に寄せる:

```csharp
public Dictionary<string, uint[]> TextRelativePathHashesByChartDirectory { get; set; }
```

`song.txt` だけが目的なら boolean で十分。ただし Everything native bridge 側の pack
形式と既存 audio/image/movie の作りに合わせるなら hash array 形式の方が拡張しやすい。

Everything native bridge を変更する場合:

- DLL はアプリとセットで配布する前提。
- したがって ABI 互換維持は不要。
- managed 側と native 側を同時に更新してよい。
- 旧DLLとの混在実行を前提にした version negotiation は必須ではない。
- ただし、診断しやすいように bridge version / result layout version をログに出すのは
  有益。

テスト:

- Everything scan で `.txt` がある chart directory に text 情報が入る。
- managed fallback でも同じ結果になる。
- 完全生成無効時は text group を走らせない、または結果を使わない。
- 複数譜面が同じ directory にある場合、同じ text 情報から `song.txt` が決まる。

### Phase 3: 通常 `folder` テーブル行を生成する

サービス例:

- `Lr2FolderTableBuilder`
- `Lr2FolderRowGenerator`

入力:

- LR2連携設定の BMS root directories
- `ChartScanResult.ChartDirectories`
- text/custom folder scan result
- 現在時刻の Unix 秒
- 既存 `folder` rows

出力:

- 通常ディレクトリ階層に対応する `LR2SongDB.folder` rows。

生成ルール:

- 設定された BMS root directory ごとに root `folder` row を作る。
- chart directory だけでなく、root から chart directory までの ancestor directory も
  すべて row 化する。
- directory の `path` は trailing separator 付きにする。
- root folder の `parent` は `AssignCRC32("ROOT")`。
- non-root folder の `parent` は parent folder path + trailing slash + NUL の LR2 CRC。
- `date = Directory.GetLastWriteTime(...).ToUnixtime()`。
- `adddate = now`。既存 row がある場合は既存の非 null `adddate` をできれば維持する。
- `folderinfo.txt` があれば LR2 風に parse する。
- `folderinfo.txt` がなければ directory name を `title` にする。

`folderinfo.txt` で読む directive:

- `#TITLE` -> `title`
- `#SUBTITLE` -> `subtitle`
- `#CATEGORY` / `#GENRE` -> `category`
- `#INFORMATION_A` -> `info_a`
- `#INFORMATION_B` -> `info_b`
- `#COMMAND` / `#TAG` -> `command`
- `#MAXTRACKS` / `#PLAYLEVEL` -> `max`
- `#BANNER` -> `banner`
- `#CUSTOMFOLDER` -> custom marker

通常 folder の default:

- `title = directory name`
- `subtitle = ""`
- `category = ""`
- `info_a = ""`
- `info_b = ""`
- `command = ""`
- `type = 1`
- `banner = ""`
- `max = 0`

重要:

- `folder.date` を null にしない。
- `folder.date` を 0 にしない。
- root folder の `date` が実ディレクトリ mtime と一致しないと、manual-only でも LR2 が
  更新チェックから再帰走査に入る可能性がある。

テスト:

- root folder row の `parent` が ROOT CRC になる。
- root folder row の `date` が directory mtime になる。
- nested folder row の `parent` が親 directory CRC になる。
- `folderinfo.txt` の値が `category`, `info_a`, `info_b`, `command`, `max`,
  `banner` に入る。
- 再生成が deterministic / idempotent である。

### Phase 4: `.lr2folder` を parse して `folder` テーブルに upsert する

BeMusicSeeker は既に playlist から `.lr2folder` を出力している。しかし、完全生成モード
では LR2 がその `.lr2folder` を scan することに依存しない。

対象:

- BeMusicSeeker が出力した custom folder directory。
- LR2連携設定に含まれる BMS root 配下の任意の `.lr2folder`。
- 必要なら LR2 標準 custom folder directory。

parse する directive:

- `#TITLE` -> `folder.title`
- `#SUBTITLE` -> `folder.subtitle`
- `#CATEGORY` -> `folder.category`
- `#INFORMATION_A` -> `folder.info_a`
- `#INFORMATION_B` -> `folder.info_b`
- `#COMMAND` -> `folder.command`
- `#MAXTRACKS` -> `folder.max`
- `#BANNER` -> `folder.banner`
- `#CUSTOMFOLDER` -> custom folder marker

type rules:

- 通常 `.lr2folder` は `type = 2`。
- root `.lr2folder` で command/tag が `__NEWSONG__` なら `type = 3`。
- root `.lr2folder` で command/tag が `__RIVAL__` なら `type = 4`。
- root `.lr2folder` で command/tag が `__EXPERT__`, `__NONSTOP__`, `__GRADE__`
  なら `type = 6`。

parent rules:

- 通常 folder 配下の `.lr2folder` は、含まれる folder path の CRC を `parent` にする。
- root custom folder として LR2 jukebox に追加されるものは `AssignCRC32("ROOT")` を
  `parent` にする。

DB write rule:

- `.lr2folder` を出力・再出力したら、同じ処理単位で `folder` row を upsert する。
- `.lr2folder` を削除したら、対応する `folder` row も削除する。
- 移動時に `date/adddate` を null にしない。新しい path の mtime / adddate を再計算する。

テスト:

- playlist 由来の `.lr2folder` 出力で `folder.command` が DB に入る。
- `.lr2folder` 削除で対応 row が消える。
- `.lr2folder` 移動で `date` / `adddate` が null にならない。
- Shift_JIS 出力した `.lr2folder` を同じ解釈で parse できる。

### Phase 5: LR2起動時スキャン代替ワークフローを作る

完全生成設定が有効な場合、LR2 起動前またはライブラリ更新時に以下を保証する。

- `song` table が最新。
- `folder` table が最新。
- root folder row の `date` が実 directory mtime と一致。
- `date = 0` の生成 row がない。
- LR2 非対応 root path には警告を出す。
- CP932 非対応または長すぎる path は LR2 起動時に scan 対象へ入りにくいように扱う。

LR2 設定との関係:

- BeMusicSeeker 側で完全生成を有効にする場合、LR2 側の DB 自動更新は「手動のみ」を
  推奨する。
- BeMusicSeeker から LR2 を起動する導線があるなら、必要に応じて注意文を出す。
- LR2 側が `autoreload == 2` のままだと、BeMusicSeeker が完全生成しても LR2 が全件
  走査し得るため、警告対象にする。

テスト:

- 完全生成有効時、root folder row に mtime が入る。
- 完全生成有効時、通常生成 row に `date = null` / `date = 0` がない。
- 完全生成無効時、従来のスキャン結果・処理時間が大きく悪化しない。

### Phase 6: 既存 row の migration / backfill

既存の `song.db` に対して、段階的な backfill を行う。

対象:

- `song.date`
- `song.adddate`
- `song.txt`
- `song.maxbpm`
- `song.minbpm`
- `song.longnote`
- `song.bga`
- `song.random`
- `song.karinotes`
- `song.exlevel`
- 通常 folder hierarchy
- `.lr2folder` 由来 folder rows

方針:

- chunked / idempotent にする。
- `favorite` などユーザーが LR2 側で触り得る値は維持する。
- playlist / tag / score 系に影響を出さない。
- CP932 非対応 path row は削除しない。
- unsupported warning は残す。

テスト:

- `date = null` の既存行が mtime で backfill される。
- `favorite` が維持される。
- CP932 非対応 path row が削除されない。
- backfill を再実行しても追加差分が出ない。

## データマッピング早見表

### `song`

- `hash`: snapshot MD5。
- `path`: full chart path。
- `folder`: LR2互換 path の場合、chart directory path の LR2 CRC。
- `parent`: LR2互換 path の場合、parent directory path の LR2 CRC。
- `title`, `subtitle`, `genre`, `artist`, `subartist`, `level`, `difficulty`,
  `judge`, `mode`, `stagefile`, `banner`, `backbmp`:
  lightweight parser の値でよい。
- `maxbpm`, `minbpm`, `longnote`, `bga`, `random`, `karinotes`, `exlevel`:
  detailed parser / `chart_info` から埋める。
- `date`: snapshot last write time の Unix 秒。
- `adddate`: insert time の Unix 秒。既存行更新時は可能なら維持。
- `favorite`: 既存値を維持、なければ `0`。
- `type`: `0`。
- `txt`: text group で chart directory に `.txt` があれば `1`、なければ `0`。

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
- `adddate`: insert time の Unix 秒。既存行更新時は可能なら維持。

## リスクと注意点

- BMS diff は現状、BMS の変更検出を mtime ベースで十分には行っていない。完全生成で
  既存 BMS の値も正しく維持するなら、path added/deleted だけでなく mtime/hash による
  再解析が必要。
- Everything と managed fallback の結果は意味的に揃える。片方だけ text group や metadata
  を返す状態にしない。
- 完全生成設定が有効な場合だけ重い列挙を増やす。無効時の既存動作を重くしない。
- LR2 は legacy narrow path を多用する。BeMusicSeeker 内では unsupported path を保持して
  よいが、LR2 起動時 scan に踏ませる row は慎重に扱う。
- BeMusicSeeker 側の DB write は parameterized SQL を使う。OpenLR2 の固定長 SQL buffer
  問題は、LR2 自身が row を再生成するときのリスクとして扱う。
- CP932 byte length の警告と Unicode string length の警告は分ける。
- Everything native bridge の ABI 互換維持は不要。DLL をアプリとセットで配布するため、
  managed/native を同時更新する前提でよい。

## 作業エージェント向け実装順

1. 完全 `song.db` 生成設定を追加する。
   - LR2連携設定の近くに配置する。
   - デフォルト有効。
   - 設定値で scan group を切り替えられるようにする。
2. text group を scan contract に追加する。
   - `ChartDirectoryScanBuilder`
   - `ChartScanResult`
   - `EverythingFileScanner`
   - `FastDirectoryFileScanner` / `FastRootFileEnumerator`
   - Everything native bridge
3. text group の Everything / fallback parity test を追加する。
4. `Lr2CompatibilityEvaluator` を追加し、chart path / folder path / resource reference の
   LR2 互換性判定を集約する。
   - 既存の `Lr2SongFolderParentNormalizer` は evaluator の結果を `BMSFile` へ反映する
     adapter にする。
   - 既存の直 `LR2CRC32` 計算箇所を段階的に evaluator 経由へ寄せる。
5. `maintenance` に LR2 compatibility warning 用の列を追加する。
   - fresh / stale 判定は既存の `maintenance.path` / `maintenance.hash` と、
     LR2 warning 列の `NULL` / non-`NULL` で行う。
   - 既存 `maintenance` table には `ALTER TABLE ADD COLUMN` で nullable columns を追加する。
   - 起動時に未検査 row の譜面ファイルを全量読み込まないようにする。
6. `Lr2CompatibilityWarningProjection` を追加する。
   - `maintenance` の fact columns から `ChartWarning` を生成する。
   - `ChartWarningProjectionFormatter` で通常 warning / resource health warning と合成する。
   - `LR2非対応パス` の一覧 membership を `parent` 空判定から LR2 compatibility predicate へ寄せる。
7. 新規追加譜面・譜面更新時に chart path / resource reference warning を
   `maintenance` へ同期する。
8. フォルダ名変更・移動時に対象 `maintenance` row の LR2 compatibility snapshot を
   即時再計算し、resource reference 再計算を後回しにする場合は対象列を `NULL` にする。
9. 起動時 hydrate / normalize で、評価済み `maintenance` と有効な `song.folder` /
   `song.parent` がある row の CRC 再計算を省略する。
10. `Lr2SongRowEnricher` を追加し、BMS file diff path に組み込む。
11. `song.date`, `song.adddate`, `song.txt`, LR2 numeric columns の test を追加する。
12. `chart_info` または詳細パーサーの値を LR2 `song` columns へ反映する。
13. `Lr2FolderRowGenerator` を追加し、root / ancestor directory rows を生成する。
14. `folderinfo.txt` parser を追加する。
15. `.lr2folder` parser と `folder` table upsert を追加する。
16. `.lr2folder` 出力・削除・移動時の DB 同期を行う。
17. 既存 row の migration / backfill を追加する。
18. CP932 非対応 path、CP932 byte length、resource reference length の警告を追加する。
19. コピーした `song.db` で統合確認する。
20. LR2 を manual-only で起動し、未変更 root で再帰スキャンに入らないことを確認する。
