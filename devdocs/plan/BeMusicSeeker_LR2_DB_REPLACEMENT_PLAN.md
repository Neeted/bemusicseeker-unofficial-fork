# BeMusicSeeker LR2 song.db 生成・同期 実装計画

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
- LR2 連携モードの LR2 `song` / `folder` は、外部編集を守る表ではなく、
  現在の実ファイル・設定・プレイリスト出力から作る一覧 cache として扱う。
  current generation input から導けない stale / unknown row は温存せず、生成 workflow 内で
  削除または上書きして収束させる。
  維持対象は `favorite` / `adddate` / `tag` など明示した LR2 user columns に限り、
  `level` / `judge` / `date` / `folder` / `parent` など generated columns は既存 DB の
  `NULL` や旧値を尊重して残す対象にしない。
- LR2 song.db 同期 completed 後の steady-state 起動では、`.bmt` 出力 OFF かつ file diff が 0 件から数件の
  ケースで、LR2 song.db 再 sync や heavyweight validation を起動 background tail に混ぜない。
  この状態では従来の path-only 差分確認に近い負荷に戻し、`startup_background_summary` は 50 秒未満を目標にする。
- LR2 起動時の root folder チェックで `date` が一致し、再帰スキャンに入らない。
- LR2 非対応 BMS は BeMusicSeeker の owned collection と LR2 `song` row には残す。
- LR2 非対応 BMS は `folder` / `parent` を LR2 表示対象として使えない状態にし、
  LR2 起動時 scan に踏ませない。非対応 chart の存在だけで sync 完了や stale `folder` cleanup を
  block しない。
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
- `EnableLR2SongDbSync` / `LR2 song.db を同期する専用設定` は公開設定・内部モードの
  どちらとしても廃止する。
  - LR2 連携モードでは、BeMusicSeeker が LR2 `song.db` の `song` / `folder` / LR2 互換性 `maintenance`
    facts を管理する。
  - スタンドアロンモードでは LR2 `folder` row、`.lr2folder` discovery、`folderinfo.txt`、LR2 custom folder
    hierarchy など LR2 専用の重い面を作らない。
  - LR2 に譜面解析を任せるために `song.date` や generated columns を空にするモードは持たない。
- LR2 compatibility warning は LR2 連携モードの評価を正本にする。
  - 既存の `Lr2PathEncodingUnsupported` も LR2 互換性 warning の一部として扱う。
  - maintenance facts や scan group が必要な warning も LR2 連携モードで評価する。
  - 画面名は `LR2互換性警告` とし、path だけでなく LR2 互換性 warning 全体を表示する。
  - スタンドアロンモードでは `LR2互換性警告` ツリー自体を非表示にする。
- sync は通常運用では発生しない想定にする。
  - LR2 連携モードで、LR2 `song.db` 内の song.db 同期 status が current `Completed` + matching signature でない場合だけ sync が発生する。
  - LR2 連携モードへ切り替えた、status signature が変わった、前回 run が incomplete / failed / cancelled の場合は
    sync needed とする。旧 schema / 未移行 row の追加検査は sync needed 判定に含めない。
  - sync が必要な場合は警告・進捗・キャンセル可能性を UI / log に出す。
  - 初回 sync は startup ready / operable を待たせず、`startup_initialization_complete` 後の
    background workflow として開始する。
  - 初回 sync の `song_rows` stage は、起動時 file diff や手動 maintenance rescan と同じ
    bounded producer / consumer 形にする。単純な `500 件読む -> 逐次 parse -> DB commit -> 次 chunk`
    という段階処理や、`song_rows` chunk ごとの DB SELECT / 行単位 upsert は最終形ではない。
  - 空 DB 初期構築では、file diff apply の `ChartFileSnapshot` / parser result から LR2 generated song columns、
    `chart_info` 由来 numeric columns、LR2 compatibility facts、text group flag を同時に作る。
    初期構築後に LR2 sync が同じ BMS chart を全件再読込する設計にはしない。
    2026-06-09 時点の実装では、初回自動 LR2 song.db 同期の `song_rows` が独立 pipeline として再 read / re-parse する余地が残っているため、`devdocs/plan/chart-file-read-pipeline-unification-plan.md` Phase 8 で file diff の fresh 生成物を検証して skip / 縮小する。
  - BeMusicSeeker からの LR2 起動導線で sync 完了待ちや起動 block は行わない。
    LR2 が再走査する可能性は song.db 同期 status の警告として表示する。
- status / sync progress と mutation guard は LR2 連携モードの標準機能として扱う。
  - sync が必要なら background workflow で進捗を表示し、owned collection / LR2 `song.db` mutation 操作は開始前に抑止する。

## 設定方針

この計画でいう LR2 連携モードは、設定上 `OperationModeLR2DB == true` であり、LR2 の
database / executable path が有効に解決できる状態を指す。

`EnableLR2SongDbSync` は設定として廃止する。UI / manual / settings persistence からも削除し、
LR2 連携モードそのものを LR2 `song.db` 生成・同期の入口にする。

2026-06-11 方針変更:

- 「LR2 に譜面解析を任せるために `song.date` や generated columns を埋めない」モードは採用しない。
- `song.date` は OpenLR2 と同じ file mtime Unix seconds として、BeMusicSeeker が管理する。
- LR2 song.db 同期の型名 / log 名 / durable table 名は `Lr2SongDbSync...` / `lr2_song_db_sync_*`
  として実装する。公開仕様としても、これは「専用同期モード」ではなく LR2 連携モードの
  `song.db` 同期である。
- スタンドアロンモードで避ける対象は、LR2 `folder` row、`.lr2folder` discovery、`folderinfo.txt`、
  LR2 custom folder hierarchy など LR2 専用 surface であり、BeMusicSeeker 内部の `song` /
  通常 `maintenance` 作成までは無効化しない。

モード別の処理方針:

- スタンドアロンモード:
  - chart / audio / image / movie の列挙と、BeMusicSeeker 内部に必要な `song` / `maintenance` は維持する。
  - LR2 `folder` row、`.lr2folder`、`folderinfo.txt`、LR2 folder hierarchy のための追加列挙は行わない。
  - `LR2互換性警告` ツリーは非表示にする。
- LR2 連携モード:
  - BMS の変更検出に必要な `song.path` / `song.date` / hash 判定を行い、`song.date` は常に
    OpenLR2 と同じ Unix seconds mtime として埋める。
  - BMS chart file、resource file、BMS chart directory 直下の text group、`.lr2folder`、
    `folderinfo.txt` を metadata-bearing file enumeration surface から列挙する。
  - `.lr2folder`、`folderinfo.txt`、LR2 built-in custom folder source (`LR2files\CustomFolder`) を
    LR2 `folder` テーブル生成の入力として扱う。
  - `folderinfo.txt` は BMS root 配下の normal directory だけでなく、`.lr2folder` parent/category
    directory row の title source としても扱う。
  - root folder と BMS chart ancestor directory の mtime は directory metadata surface として取得する。
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
- `GetUnixtimeFromFiletime` / `GetFileUnixtime`:
  `D:\github-clone\OpenLR2\LR2\En_fileutil.cpp:190`

OpenLR2 の重要な挙動:

- `SearchSongsFromPath` は `FindFirstFileA(root + "*.*")` で走査する。
- `ParseBMSMETA` は narrow `fopen` で読み、行を CP932 として扱う。
- BMS ディレクトリ直下に `*.txt` があれば `song.txt = 1` になる。
- `song.date` は BMS ファイルの最終更新時刻の Unix 秒。
- `song.adddate` は登録・更新時刻の Unix 秒。
- `folder.date` はディレクトリまたは `.lr2folder` ファイルの最終更新時刻の Unix 秒。
- `folder.adddate` は登録・更新時刻の Unix 秒。
- Windows では `FindFirstFile` / `WIN32_FIND_DATA.ftLastWriteTime` の `FILETIME` を
  `(filetime - 116444736000000000) / 10000000` で UTC Unix 秒へ変換する。ローカル時刻として
  再解釈しない。
- BeMusicSeeker の `LastWriteTimeUtc -> Unix 秒` 変換はこの OpenLR2 現行実装と同じ意味にする。
  旧 DB との差分を埋めるために local time 変換や別の fallback date を導入しない。
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
- `RootFileEnumerationResult` は file / directory の metadata entry (`path`, `mtime`) を持つ。
  Everything grouped bridge と managed fallback は同じ entry surface へ materialize する。
- 現在の `ChartScanResult` は chart path / chart directory / chart file entry / directory entry /
  audio/image/movie の resource hash に加え、chart directory 直下の `.txt` presence と
  `folderinfo.txt` 候補を持つ。BMS file mtime は chart file entry から file diff 側の snapshot へ渡す。
- normal folder row の directory mtime は、startup / file diff producer が root 配下 directory group を
  `RootFileEnumerationEntry` surface として作り、sync input / normal folder sync は必要 target だけを
  その surface から resolver 化する。
- `.lr2folder` discovery は LR2 song.db sync 入力として、chart/resource scan surface とは別に
  root / extension / filename を調整した metadata-bearing file surface から作る。
- `ChartFileSnapshot` には file bytes、MD5、SHA256、`LastWriteTimeUtc` がある。
- `BMSFile.CreateBMSFileFromSnapshot` は軽量パーサー。
- 軽量パーサーは `#WAVxx`、`#BMPxx`、主要メタデータ、`#STAGEFILE`、
  `#BANNER`、`#BACKBMP` を読む。
- 軽量パーサーは `maxbpm`、`minbpm`、`longnote`、`bga`、`random`、
  `karinotes`、`exlevel` を十分には埋めない。
- 詳細解析結果は `chart_info` にあるが、素の LR2 `song` 列へ同期されていない。
- `.lr2folder` ファイル出力はあるが、出力と同時に `folder` テーブルを完全に作る層は
  不十分。
- BMS の `path + song.date` 差分検出と `.txt` presence の targeted update は実装済みであり、
  LR2 song.db sync でも同じ contract を使う。

LR2 song.db sync で固定する scan surface の方針:

- `song.date` / `folder.date` / `.lr2folder` freshness を LR2 起動時 scan 抑止の正本にするため、
  native bridge と managed fallback の両方で、列挙時に file / directory の mtime を同じ結果 surface に含める。
- native bridge を拡張せずに列挙後 managed API で mtime を再取得する方針は採らない。
  fallback 側だけ列挙時に mtime を持つ方針も採らない。
- Everything の一時的な利用不可は central enumeration service で managed fallback するが、
  native DLL 欠落、旧 ABI、export missing、contract mismatch は app / DLL 同梱契約の破損として扱い、
  fixed scan / grouped metadata scan のどちらでも互換 fallback しない。
- Everything query は既存の並列 grouped query の考え方に揃え、chart / audio / image / movie に加えて
  `.txt`、`folderinfo.txt`、`.lr2folder`、directory 用の query を並べる。
  `.txt` / `folderinfo.txt` / `.lr2folder` は chart / resource より件数が少ない前提で、独立 query として扱う。
  directory は `folder: path:"D:\BMS"` のように末尾区切りなしの root path を query に入れ、root 自身も
  surface に含める。managed fallback も root entry と配下 directory entry を同じ shape で返す。
- managed fallback も `FastRootFileEnumerator` / `WIN32_FIND_DATA` 由来の列挙時 metadata を
  同じ `RootFileEnumerationResult` 相当へ格納し、後段は backend に依存しない。
- `ChartScanResult` / normal folder metadata / `.lr2folder` discovery は、path-only set ではなく
  metadata-bearing surface から派生させる。
- sync input 作成は、startup / file diff で得た surface を正本として再利用する。
  scan surface capture は producer から渡された `.lr2folder` discovery / entry surface を保存するだけにし、
  欠けている場合に capture 側で別 scan を実行して補完しない。欠けた surface は producer contract の不備として扱い、
  既存 snapshot を再利用不可にしたうえで、startup / file diff producer 側で修正する。
  playlist custom folder 出力も同様に、`.lr2folder` parent directory metadata は
  `Lr2FolderFileDbSyncService` 内部の filesystem fallback ではなく、出力側 producer が scoped metadata snapshot を
  作って resolver として渡す。この scoped producer は対象 directory だけを direct lookup し、
  target 数によって output base/root 全体の directory enumeration へ切り替えない。owned metadata で対象を解決できる場合は
  grouped enumeration 自体を走らせず、欠落 target が残った場合だけ missing target に限定した grouped enumeration を使う。
  `folderinfo.txt` grouped surface の取得失敗は directory name fallback で握り潰さず、input producer の失敗として扱う。
  owned mutation sync は LR2 song.db sync の no-surface 再取得とは別の bounded producer として扱い、
  mutation target directory 直下の `folderinfo.txt` / directory mtime だけを owned metadata surface にする。
  小さい install / library delta で BMS root 全体の `.txt` / directory grouped scan へ広げない。
  playlist materialization などで一部入力が変わる場合も、surface 全体を破棄せず、
  影響を受けた `.lr2folder` group と、同じ producer 境界で得た directory / `folderinfo.txt` / `.txt`
  metadata だけを materialization result または同じ grouped enumeration API で refresh する。
  `.txt` / `folderinfo.txt` / directory metadata まで失効させて、hot path で resource scan や広域再列挙に落とさない。
  playlist materialization 直後の app-owned output directory mtime は出力側 producer が exact target を
  filesystem から取得してよく、後段の LR2 song.db sync input へ再利用する場合は
  `Lr2SongDbSyncPreparedDataSurface` の metadata surface として明示的に渡す。
- surface を再利用できない場合の再取得も Everything grouped query、または同じ contract の
  managed fallback grouped enumeration で行う。200k 件級の chart path に対して
  `Directory.EnumerateFiles` / `File.Exists` / `GetLastWriteTime` を個別に呼ぶ ad hoc fallback は、
  scoped mutation の少数対象以外では採用しない。

## 実装前に固定する必要がある contract

### BMS 変更検出

LR2 song.db sync の正しさは、BMS の変更検出に依存する。現行の「追加 path だけ parse」では、
既存 BMS の内容変更、mtime 変更、`song.txt` 変更、LR2 numeric columns の変更を取り逃がす。

方針:

- BMS / bmson に関係なく、owned chart の path add / delete / move 検出 contract は統一する。
- LR2 `song` row を生成する BMS では、`song.path` と `song.date` を変更検出の正本にする。
- `Startup`、`ReloadFileDiff`、search root 変更後 reload、manual rescan のいずれでも、
  既存 path の `song.date` と実 BMS mtime の mismatch を update target として扱う。
- LR2 song.db sync status は durable `Completed` + contract signature を正本にする。未リリース機能のため、
  旧 DB / 未移行 DB かどうかを maintenance nullable column などから追加検査する移行保護モードは持たない。
  status が `Completed` でない場合、既存 generated columns の収束は後段 LR2 song.db sync の責務にし、
  file diff 側は現在の path / mtime / hash / text group の変更検出 contract だけを扱う。
- DB row の `path` と現 file path、`song.date` と現 file mtime の Unix 秒が一致する場合は更新なし。
- `path` または `song.date` が変わった場合は `ChartFileSnapshot` を読み、MD5 を比較する。
- `path` / `song.date` が変わり MD5 も変わった場合:
  - BMS は再parseし、`song` / `chart_info` / `maintenance` / LR2 compatibility snapshot を更新する。
  - bmson は現行の `bmson_song` 更新経路を維持する。
- `song.date` が変わったが MD5 が同じ場合:
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
- LR2 `song` row に既にある `path` / `date` を差分検出の正本にし、file size や
  high precision mtime の独自永続列は追加しない。
- hash 読みは `path` / `song.date` 変化対象だけに限定する。
- mtime が保持された外部コピーや同秒更新は完全には検出できないため、明示的な全譜面再スキャンを
  force verify として残す。
- text group の freshness は BMS file metadata とは別に扱う。

### `song` row の列所有権

LR2 song.db sync では `InsertOrReplace` で LR2 `song` row を丸ごと置換する前提にしない。
LR2 / ユーザーが触る可能性のある列と、BeMusicSeeker が再生成する列を分ける。
ただし、この分離は「generated columns の既存値を保護する」ためではない。
LR2 連携モードでは、以下の generated columns は current owned chart / `chart_info` /
scan surface から作り直し、既存 DB の `NULL` や古い補完値は保存対象にしない。
維持するのは後述の user columns だけである。

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

- LR2 `song` row の DB 書き込み入口は `Lr2SongDbWriter` に単一化する。
  - 既存の `UpsertSongs`、file diff commit、maintenance update などからの直接 `InsertOrReplace(song)` は
    廃止し、`Lr2SongRowEnricher` / `Lr2SongDbWriter` 経由へ寄せる。
  - 例外的な low-level write helper は `Lr2SongDbWriter` 内部だけに閉じる。
- 新規 row は full insert でよい。
- 既存 row は列単位 merge または targeted `UPDATE` に寄せる。
- やむを得ず writer 内部で `InsertOrReplace` を使う場合は、既存 row から維持列を読んでから書く。
- path move/relink では旧 path row の維持列を新 path row へ引き継ぐ。
- `favorite` / `adddate` / `tag` の維持をテストで固定する。

### `folder` row の生成 scope

LR2 song.db sync では `folder` row の stale pruning が重要になる。古い root row や `date = 0`
row が残ると、manual-only でも LR2 が不要な scan に入る。

方針:

- `folder` は LR2 native table の派生 cache として扱い、app-owned manifest / row ownership は追加しない。
- BeMusicSeeker は現在の入力 source から `expected folder path set` を deterministic に生成し、
  LR2 連携モードでは `folder` table をその expected set へ収束させる。
  row ownership 列は追加しないが、現在の LR2 root / custom folder 出力 / built-in source から
  導けない stale row は削除対象にする。
- generation scope は `path` を正本にする。
  - scope 内で同じ `path` が複数 source から出る場合は、LR2 built-in custom folder、
    `.lr2folder` file、`folderinfo.txt` 付き directory、通常 directory の順で 1 row に正規化する。
  - 既存 row が同じ `path` にあれば `adddate` など維持列を引き継ぐ。
- root / ancestor / normal folder row は、現在の LR2 BMS root と BMS chart directory set から
  deterministic に再生成する。
- LR2 互換 path として扱えない BMS は `song.folder` / `song.parent` を `NULL` にし、その BMS だけを
  根拠にした `folder` expected row は生成しない。同じ directory に LR2 互換 BMS がある場合は、
  その互換 BMS 由来の chart directory set として folder row を生成する。
- LR2 互換 path として扱えない BMS は completion blocker にしない。
  `song` row と compatibility warning は残しつつ、folder generation source からだけ除外する。
  非互換 BMS が 1 件あることを理由に、他の managed normal folder row の prune / upsert を停止しない。
- `.lr2folder` row は discovery source を分類するが、所有権として永続化しない。
  - `playlist_output_lr2folder`: BeMusicSeeker がプレイリスト出力として生成する `.lr2folder`。
    実ファイルは既存の出力設定・出力 directory convention で管理する。
  - `discovered_lr2folder`: LR2 BMS root、BeMusicSeeker custom folder 出力 base、
    LR2 built-in custom folder source (`LR2files\CustomFolder`) から discovery した `.lr2folder`。
  - DB row はどちらも現在の discovery result から生成する派生 cache とし、消えた file の row は
    generation scope 内の path prune で削除する。実 `.lr2folder` ファイルは discovery では削除しない。
  - `playlist_output_lr2folder` は物理 file discovery の偶然の結果ではなく、playlist 正本からの
    materialization result を正本にする。playlist materialization 後に LR2 song.db sync を実行する場合は、
    materialization 前に捕捉した `.lr2folder` surface をそのまま正本にしない。
    ただし、これは `.lr2folder` group の refresh が必要という意味であり、
    同じ startup scan surface に含まれる `.txt` / `folderinfo.txt` / normal directory metadata まで
    捨てる理由にはしない。
- `LR2files\Rival` は LR2 が ranking server 通信と active rival ID に基づいて動的に反映する領域なので、
  BeMusicSeeker の built-in discovery root には含めない。通常の LR2 BMS search root や custom folder
  出力 base で発見した `__RIVAL__` `.lr2folder` は、他の外部 `.lr2folder` と同じく処理する。
- `.lr2folder` discovery は、LR2 連携モードだけで行う。
- `.lr2folder` discovery root は、LR2 BMS search root、BeMusicSeeker の custom folder 出力 base、
  LR2 built-in custom folder source から作る。
  - 通常出力先 `LR2CustomFolderOutputBaseDir`
  - ルート出力先 `LR2CustomFolderOutputBaseDirRootType`
  - LR2 executable directory 配下の `LR2files\CustomFolder`
- 通常出力先配下の `.lr2folder` 親 directory row も、LR2 root folder hierarchy の一部として
  expected folder scope に含める。通常出力先 `LR2CustomFolderOutputBaseDir` は他の通常 BMS root と
  同じく `parent = ROOT` の `type = 1` directory row として扱い、その配下の playlist/table directory row は
  通常出力先 directory row の子にする。たとえば
  `D:\BMS\#BeMusicSeeker\<Table>\0000.lr2folder` は `D:\BMS\#BeMusicSeeker\` を root 相当境界にし、
  `D:\BMS\#BeMusicSeeker\` を `parent = ROOT`、`D:\BMS\#BeMusicSeeker\<Table>\` を
  `parent = hash(D:\BMS\#BeMusicSeeker\)` の `type = 1` row として作る。
- ルート出力先配下の `.lr2folder` 親 directory row も expected folder scope に含める。
  ルート出力先 `LR2CustomFolderOutputBaseDirRootType` は、それ自体ではなく、指定された playlist/table
  directory それぞれを BMS root 相当として扱う。ルート出力 playlist/table の directory row は
  `parent = ROOT` とし、その directory 配下に生成される numbered `.lr2folder` row は containing directory の
  hash を `parent` にする。
- LR2 built-in `LR2files\CustomFolder` のカテゴリ directory row
  (`RANDOM\`, `PLAYLEVEL\`, `CLEAR\`, `RANK\`, `INSANE01\`, `INSANE02\` など) も、
  対応 bitmask が有効な場合は expected scope に含める。
  category directory row は `.lr2folder` file sync 側で生成されるが、title/date は normal folder row と同じ
  directory metadata / `folderinfo.txt` contract に揃える。`LR2files\CustomFolder\RANDOM\folderinfo.txt`
  のような候補がある場合は `#TITLE` を title に反映し、無ければ directory name を使う。
  `LR2files\CustomFolder` 自体は scope boundary であり、root row を無理に生成しない。
- `LR2files\CustomFolder` は LR2 setup の `<customfolder>` bitmask を正本にして対象を決める。
  - `1`: `RANDOM/`
  - `2`: `favorite.lr2folder`
  - `4`: `TOP10.lr2folder`
  - `8`: `PLAYLEVEL/`
  - `0x10`: `CLEAR/`
  - `0x20`: `RANK/`
  - `0x40`: `ignore.lr2folder`
  - `0x80`: `INSANE01/` と `INSANE02/`
  - `course1.lr2folder` / `course2.lr2folder` / `course3.lr2folder` は bitmask と独立して常時対象にする。
  - `newsong.lr2folder` は `titleflash` と `song.adddate` に依存する dynamic row として扱い、
    対象曲が無い場合は生成しない。
- chart / resource scan root は LR2 BMS search root から作る。ただし custom folder 出力 base が
  LR2 BMS search root に explicit root として含まれている場合は、その explicit root だけ除外する。
  - 親の BMS root 配下に出力 base が内包される場合は subtree 除外しない。
  - 出力 base 配下に BMS / bmson / resource が存在する場合も、親 root の通常探索結果として扱う。
- Everything query には exclude query 概念を追加しない。root / extension / filename を調整して
  chart / resource surface と `.lr2folder` file surface を別々に取得する。
  Everything 結果は query contract 通りに扱い、アプリ固有かどうかは結果分類で判定する。

削除ルール:

- generation scope 内で期待されなくなった `folder.path` row は削除する。
- BeMusicSeeker が出力した `.lr2folder` を削除した場合も、次の generation でその file path が
  expected set から消え、対応する `folder` row が削除される。
- discovery で見つからなくなった `.lr2folder` row は削除する。実 `.lr2folder` ファイルは
  BeMusicSeeker から削除しない。
- startup file diff の `.lr2folder` sync は外部 discovery 分だけを対象にする。playlist header と
  `playlist_entry` から導ける numbered `.lr2folder` exact path は、物理 file が scan surface に
  含まれていても current 候補から外し、親 BMS root / output base の prune scope に含まれても exact path で
  削除対象から保護する。通常 custom folder 出力 base / root custom folder 出力 base そのものは外部 discovery
  scope として残すため、base 直下や管理 playlist 由来ではない同 directory 配下の `.lr2folder` は外部ファイル
  として扱う。外部 `.lr2folder` は譜面 file diff と同じく、mtime が変われば読み直し、scan surface から
  消えた DB row は discovery complete かつ read failure なしの scoped sync で prune する。
- アプリ管理 playlist 出力の `.lr2folder` / `folder` row は playlist materialization が正本であり、
  起動時の外部 `.lr2folder` discovery sync では検査・修復しない。物理欠損、`folder` 投影欠落、
  `folder.date` と実 `.lr2folder` mtime の不一致は playlist entries hydration 後の batch materialization /
  single DB sync、明示手動再同期は playlist materialization stage で収束させる。
  アプリ管理 `.lr2folder` は既存 file 本文を読み直して差分判定せず、`playlist` / `playlist_entry` 正本から
  expected file を出力し、実 file mtime を `folder.date` へ反映する。本文読み取りが必要なのは管理外
  `.lr2folder` discovery だけに限定する。
- LR2 song.db sync では、current generation input から導けない既存 `folder` row は prune 対象にする。
  `unknown root` / `date = 0` / expected set 外 row / 列挙 metadata から解決できる mtime 不一致は、
  完了を妨げる永続状態として温存せず、生成 workflow 内で削除または上書きして収束させる。
  完了直前に live filesystem へ戻って missing target や mtime を再検証することはしない。
- LR2 song.db sync では、current owned BMS path set から導けない既存 `song` row も prune 対象にする。
  `song` は実ファイル由来の一覧 cache であり、既知 root 外や旧 root の stale row を保護して
  `Incomplete` に残さない。対応する `maintenance` row と orphaned `chart_digest_map` も同じ
  cleanup transaction で整理する。
- cleanup 導線は、workflow で修復できない既存 DB の残骸を手動で消すための補助に留める。
  定常的な startup-scan diagnostic の残件解消は workflow 自身の upsert / prune によって行う。

### 通常 mutation 時の LR2 DB writer contract

LR2 song.db sync の完了後は、初回 sync に頼らず、所持譜面ライブラリの mutation と同じ operation 内で
LR2 `song` / `folder` を最新状態へ保つ。

対象 mutation:

- file diff / startup scan による BMS add / delete / move / update。
- 手動 install / reinstall / uninstall。
- duplicate merge や実ファイル削除などの owner-backed unregister。
- BMS root set 変更、root folder 設定変更。
- manual rescan で LR2 生成列に差分が出た場合。

方針:

- LR2 `song` row への保存は、すべて `Lr2SongDbWriter` / `Lr2SongRowEnricher` を通す。
- mutation producer は、owned collection delta と同じ単位で LR2 song/folder delta を作る。
- BMS add/update は `ChartFileSnapshot`、text group snapshot、chart_info/detailed parser result、
  existing song row を入力にし、`Lr2SongRowEnricher` と `Lr2SongDbWriter` を通して generated columns を
  changed-only に保存する。
- BMS delete は LR2 `song.path` を正本にして該当 row を削除する。delete だけで再帰 folder prune を行わず、
  affected folder scope を dirty にし、同 operation の末尾で不要 folder row を prune する。
- BMS move/relink は旧 `song` row の維持列 `favorite` / `adddate` / `tag` を新 path row へ引き継ぐ。
- text group、directory mtime、`folderinfo.txt`、`.lr2folder` source に影響する mutation は
  folder generation scope を dirty にし、同 operation の末尾で affected scope を再生成して
  scope 内 upsert / prune まで完了させる。
- root set 変更や custom folder output base 変更のように影響範囲が広い場合は、scope 全体を再生成する。
- LR2 DB write が busy / lock / unexpected failure で失敗した場合、owned collection mutation は成功させてよいが、
  LR2 song.db sync status を `Incomplete` にし、次回 sync / diff run で再同期できる状態にする。
- スタンドアロンモードでは、通常 mutation で LR2 補助列挙・folder generation は行わない。

テスト:

- BMS add/update/delete/move で LR2 `song` row と維持列が期待通りになる。
- install / uninstall / duplicate merge 後に `song` row が stale にならない。
- text group 変更で同 directory の `song.txt` が更新される。
- directory rename / root set 変更で affected folder rows が再生成される。
- LR2 DB write failure は LR2 song.db sync status を `Incomplete` にし、次回 sync で復旧できる。

### LR2 compatibility warning の scope

LR2 compatibility warning は BMS のみを対象にする。bmson は対象外。

評価対象:

- BMS chart file path
- BMS chart directory の `folder\*.*` scan path
- BMS resource reference の resolved path
- BMS root folder / ancestor folder / playlist output `.lr2folder` / discovered `.lr2folder`
- shared `maintenance` table の LR2 columns は bmson row では `NULL` / ignored とし、
  projection 側でも chart kind gate を置く。

表示方針:

- LR2 連携モードでは maintenance fact から warning を投影する。
- スタンドアロンモードでは LR2 互換性 warning を投影せず、`LR2互換性警告` ツリーも出さない。
- LR2 compatibility warning の ignore 永続化はこの計画では追加しない。
  - warning digest / tooltip / `LR2互換性警告` membership は `warning-model.md` の structured warning
    projection に従って組み立てる。
  - membership は `song.parent IS NULL` ではなく、`ChartWarningCategory.Lr2Compatibility` の structured warning
    を持つ chart とする。`Lr2PathEncodingUnsupported` だけでなく、resource raw/resolved path や length の
    LR2 互換性 warning も同じ画面で扱う。
  - 将来 ignore UI / ignore list を追加する場合は、別計画で永続 state を追加する。

### LR2 song.db 同期 status / sync workflow

LR2 連携モードでは、BeMusicSeeker は LR2 `song.db` の生成状態を durable に管理し、必要な sync を
background workflow として進める。この workflow は DB 生成状態の可視化と復旧を目的にし、
LR2 起動導線の block は行わない。

- sync が必要な場合は、警告・進捗・キャンセル可能性を表示する。
- status は `NotNeeded` / `Needed` / `Running` / `Completed` / `Failed` / `Cancelled` /
  `Incomplete` を持つ。
- `Completed` の signature が schema / generator / parser / LR2 song.db generation contract と一致する場合は
  full sync 不要とする。BMS root、`.lr2folder` discovery root、LR2 built-in special folder 設定のような
  runtime input は file diff / scoped folder sync の責務で収束させ、signature mismatch だけで `song_rows`
  全量再同期へ倒さない。
- `Completed` は current generation input から導いた `song` / `folder` expected set へ DB を収束させた
  run の完了状態である。旧 row や unknown root row を温存したまま `Incomplete` にするのではなく、
  workflow 内で削除または上書きできるものは修復してから完了する。
- 保存済み `Completed` の判定では、`song` / `folder` / `maintenance` row を再走査して未移行状態を推測しない。
  `HasIncompleteLr2CompatibilityMaintenanceFacts` のような nullable fact の穴埋め検査は LR2 song.db 同期の status 判定に使わない。
  CP932 byte count 系の `NULL` は、CP932 非対応または測定対象なしを表す正当な fact であり、未評価の marker ではない。
- startup-scan diagnostic は、実装確認や同一 run 内での修復候補のログとして扱う。保存済み `Completed` を覆す
  追加の移行判定や、次回起動時の full sync 要否判定には使わない。
- `Needed` / `Running` / `Incomplete` / `Failed` / `Cancelled` は LR2 song.db 同期 status に warning を出す。
- 廃止済みの `EnableLR2SongDbSync` 設定値は、新規 warning や移行判定に使わない。
- LR2 側の DB 自動更新設定はこの計画の実装対象にしない。
  - BeMusicSeeker 側から LR2 config を自動変更しない。
  - manual-only 運用はユーザー向け docs の推奨として扱い、status / schema / sync の判定条件には含めない。
- LR2 が起動中の場合の DB write 競合を検出する。
  - SQLite busy timeout / file lock / transaction 失敗時の扱いをログに出す。
- LR2 song.db 同期 status は durable に保存する。
  - 保存先は LR2 `song.db` 内の BeMusicSeeker-owned metadata table とする。
  - schema version
  - generator version
  - `chart_info` parser version
  を含め、いずれかが変わった場合は completed を無効化する。
- LR2 BMS root set、`.lr2folder` discovery root set、LR2 setup の `<customfolder>` bitmask /
  `titleflash` / `newsong` 対象有無は durable completed signature に含めない。
  これらは現在の実ファイル・設定から `song` / `folder` row を差分更新できる runtime input であり、
  変更時は file diff / `.lr2folder` diff / built-in special folder scoped sync を queue する。
- generator version は `song` / `folder` 生成列、folder generation scope、warning projection の意味が変わる時に bump する。
- `chart_info` parser version は detailed parser / `chart_info` schema contract の意味が変わる時に bump する。
  LR2 `song` row 用 lightweight parser は別 version を持たない。lightweight parser の OpenLR2 寄せや
  default 補正の改善は、手動の LR2 song.db 再同期で最新化できる前提にする。
  自動 invalidation が必要なほど generated row contract が変わる場合だけ generator version を bump する。
- sync は startup ready / operable をブロックしない。初期化完了後に background workflow として開始し、
  LR2 起動要求が来ても同じ workflow へ join して待つことはしない。
- sync 中は read-only 操作を許可する。
  - 一覧閲覧、検索、ソート、プレイリスト表示、設定画面表示は許可する。
  - owned collection / LR2 `song.db` に mutation を起こす操作は開始前に抑止する。
    例: 譜面追加、削除、移動、install/reinstall、全譜面再スキャン、BMS root 設定変更。
- sync 完了前に owned collection / storage row generation と LR2 root / `.lr2folder` discovery root /
  built-in custom folder 設定の snapshot が変わっていないかを確認する。これは実行中に入力が変わった
  run を `Completed` として publish しないための running-run guard であり、次回起動の durable signature
  invalidation ではない。
  - 完了直前に `.txt` / `folderinfo.txt` / `.lr2folder` / directory mtime を広く再列挙しない。
    sync 中に外部ファイルが変わった場合は、次回 scan / signature 再評価で新しい input として扱う。
  - built-in `newsong.lr2folder` の有効/無効は sync input 作成時の `titleflash` 判定に固定する。
    長時間 run が titleflash 境界を跨いでも、それだけで current 判定を stale にしない。
  - アプリ内の mutation や設定変更で source snapshot が変わっていなければ `Completed` を記録し、
    以後は通常 mutation 時の LR2 DB writer contract で差分維持する。

## LR2 互換性評価

### 既存実装との統合

現行実装には LR2 互換性警告用の処理がすでにある。中心は
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

`maintenance` の freshness は既存の `path` / `hash` を基本にする。LR2 互換性評価のために
file size / high precision mtime の独自 freshness 列は追加しない。

推奨列:

- `lr2_path_warning_flags INTEGER NULL`
- `lr2_chart_path_cp932_bytes INTEGER NULL`
- `lr2_folder_scan_cp932_bytes INTEGER NULL`
- `lr2_resource_warning_flags INTEGER NULL`
- `lr2_resource_max_raw_cp932_bytes INTEGER NULL`
- `lr2_resource_max_resolved_cp932_bytes INTEGER NULL`
- `lr2_resource_unsupported_count INTEGER NULL`

schema migration:

- 既存 table を drop / rebuild しない。
- `PRAGMA table_info(maintenance)` で既存列を確認し、足りない列を
  `ALTER TABLE maintenance ADD COLUMN ...` で追加する。
- app schema version / preflight に組み込む。
- maintenance hydrate / write は `EnsureMaintenanceSchemaVNext` 相当の schema ensure より前に実行しない。
- `BMSFileMaintenanceInfo`、`MaintenanceRowsEquivalent`、`CloneMaintenanceInfo` に新列を追加する。

保存するものは警告テキストや全 resource reference の完全リストではなく、警告 kind を判定する
最小 fact に限定する。表示文言、digest、tooltip は `warning-model.md` の structured warning
projection に従って実行時に組み立てる。詳細な具体例を tooltip に出す機能はこの計画では追加しない。

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

### Phase 1: BMS 変更検出を `song.path` / `song.date` / hash ベースに統一する

目的:

- LR2 song.db sync の前提として、既存 BMS の変更を検出できるようにする。
- スタンドアロンモードでも同じ変更検出 contract を使えるようにする。

実装:

- DB row の `song.path` と実 path、`song.date` と実 BMS mtime の Unix 秒が一致する場合は unchanged。
- 通常状態では、`song.path` または `song.date` が変わった場合だけ `ChartFileSnapshot` を読み、MD5 を比較する。
- 通常状態の `Startup` / `ReloadFileDiff` / search root 変更後 reload では、既存 path の mtime mismatch を
  update target set に含める。
- LR2 song.db sync status が `Completed` でない場合も、旧 DB / 未移行 DB 専用の検査や保護モードは持たない。
  既存 generated metadata の収束は後段 LR2 song.db sync へ任せ、file diff 側は現在の変更検出 contract
  に基づく差分だけを扱う。
- MD5 が同じなら `song.date` のみ更新する。
- MD5 が違うなら再parseする。
- deleted path と added path の MD5 が同じ場合は move/relink として維持列を引き継ぐ。

テスト:

- `song.path` + `song.date` 一致は parse しない。
- 通常状態では、`song.date` changed + MD5 same は `song.date` のみ更新する。
- 通常状態では、`song.date` changed + MD5 changed は BMS row / chart_info / maintenance を更新する。
- `Completed` でない状態では、既存 BMS path の全量 generated metadata refresh を file diff へ持ち込まず、
  LR2 song.db sync に収束させる。maintenance nullable fact の `NULL` はこの判定に使わない。
- `Startup` / `ReloadFileDiff` / search root 変更後 reload の全経路で既存 path の mtime mismatch を検出する。
- path move + MD5 same で `favorite` / `adddate` / `tag` が維持される。
- bmson の現行更新検出と矛盾しない。

### Phase 2: `song` row merge / ownership を導入する

目的:

- `InsertOrReplace` による既存 LR2/user-owned column の破壊を避ける。

実装:

- `Lr2SongRowMerger` または `Lr2SongDbWriter` を追加する。
- LR2 `song` row への直接 `InsertOrReplace(song)` / direct update sites を洗い出し、
  `Lr2SongDbWriter` 経由へ移行する。
- 既存 row がある場合は維持列を読んでから generated columns を更新する。
- generated column ごとの targeted `UPDATE` を基本にし、full row replace は使わない。
- `favorite` / `adddate` / `tag` は維持する。

テスト:

- existing `favorite` が維持される。
- existing `adddate` が維持される。
- existing `tag` が維持される。
- generated columns は更新される。
- 新規 row は必要列が null/0 不正にならない。
- file diff、manual rescan、maintenance update などの既存 song write 経路が `Lr2SongDbWriter` を通る。

### Phase 3: text group と raw resource reference snapshot を scan/parse contract に追加する

目的:

- `song.date` / `song.txt` / directory metadata / `.lr2folder` freshness と
  LR2 resource compatibility warning を、後追い全件 filesystem check なしで計算する。

file enumeration surface:

- `RootFileEnumerationResult` は path-only set ではなく、group ごとの file entry surface を持つ。
  file entry は少なくとも full path、last write time UTC、LR2 `date` 用 Unix 秒、必要なら file size を持つ。
  file size は update 判定の正本にはしないが、diagnostic / bridge parity の補助値として持ってよい。
- native bridge ABI を拡張し、grouped Everything query result から各 file の mtime を一緒に返す。
  layout version / result version はログ分析用に出してよいが、アプリ本体と native bridge DLL は同一配布物として扱い、
  runtime で旧 ABI を許容する分岐は増やさない。
- managed fallback は列挙時に `WIN32_FIND_DATA` / file metadata から同じ file entry を作る。
  fallback 後に全 path へ `File.GetLastWriteTimeUtc` を再実行しない。
- chart / audio / image / movie / `.txt` / `folderinfo.txt` / `.lr2folder` は同じ grouped enumeration API で扱う。
  query は root / extension / filename の組み合わせで分け、exclude DSL は導入しない。
- directory metadata surface は file entry surface と同じ generation の directory set に対して作る。
  directory mtime も native bridge / fallback で同じ shape にし、normal folder row と startup-scan diagnostic の正本にする。

text group:

- BMS chart directory 直下の任意 `.txt` 有無だけを扱い、特定ファイル名 `song.txt` には限定しない。
- descendant / ancestor の `.txt` を拾わない。
- スタンドアロンモードでは走らせない。

raw resource reference:

- BMS parse pass で raw directive と resolved path を保持する。
- `ChartResourceSnapshot` の lookup key とは別投影にする。

性能条件:

- 10M resource で managed materialize が増えすぎないよう、native bridge は path string と metadata を
  compact に pack し、C# 側の materialize は後段が必要とする group / directory index へ直結させる。
- `.txt` / `folderinfo.txt` / `.lr2folder` は chart / resource query と同じ grouped request に並べる。
  これらの件数は少ない想定なので、別 pass の managed stat を発生させない。
- native bridge と managed fallback の parity test を先に置き、どちらか一方だけが mtime / existence を
  取れる状態を許可しない。

テスト:

- Everything native bridge / managed fallback の file metadata parity。
- chart directory 直下の `.txt` のみ `song.txt=1`。
- BMS file mtime が `song.date` に入り、mtime mismatch が update target になる。
- `.lr2folder` file mtime と directory mtime が folder row / startup-scan diagnostic に反映される。
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
- resource raw path / resolved path の CP932 非対応と byte length boundary が warning になる。
- resource path の親ディレクトリ参照は LR2 非対応理由にせず、OpenLR2 が受け取る相対 path として byte length / CP932 判定に含める。
- bmson は warning 対象にならない。
- maintenance の最小 fact から `warning-model.md` 準拠の digest / tooltip が組み立てられる。

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

解析方針:

- LR2 song.db sync / 手動再同期の `song_rows` stage では、対象 BMS の bytes を読み、
  lightweight parser を再実行して LR2 `song` row の基本メタデータを作る。
  既存 `song` row の generated columns を正本としてコピーする経路は、read / parse 失敗時の
  non-destructive preserve に限る。
- lightweight parser は `title` / `subtitle` / `genre` / `artist` / `subartist` /
  `stagefile` / `banner` / `backbmp` に加え、LR2 実行時の選曲 / プレイ挙動に近い
  `mode` / `judge` を担当する。ここで OpenLR2 に寄せた directive parse と未定義 default を行う。
  例として `#RANK` 未定義は `judge=2` に正規化し、`mode` も lightweight parser 側の
  OpenLR2 互換 contract として固定する。
- `level` / `difficulty` はプレイ挙動ではなく譜面メタデータとして扱う。LR2 song.db sync / 再同期 /
  file diff / アプリ内導入のすべてで同じ値になるよう、current `chart_info` / detailed parser
  由来の単体譜面決定値を優先する。`difficulty` は `#DIFFICULTY` raw 値を第一入力にし、
  `difficulty_defined=false` の推定値も deterministic chart metadata として採用する。
  `chart_info` が欠落・parse failure・schema mismatch で使えない場合だけ lightweight parser の
  valid value を fallback とし、それも無ければ `difficulty=2` を固定 default とする。
  `#DIFFICULTY` が BMS 文化圏では選曲リストの分類・フィルタ用メタ情報であり、
  `1=Easy/Beginner`, `2=Normal/Standard`, `3=Hyper/Hard`, `4=Another/Extra`,
  `5=Insane/発狂` という意味合いで使われることを前提にする。
  追加の文化的キーワード推定が必要になった場合は lightweight parser へ独自アルゴリズムを増やさず、
  `chart_info` parser の inference contract を拡張する。
- OpenLR2 `SetUndefinedDifficulty` 相当の `folder, mode, karinotes` 順 DB-wide 補完は、
  初回 LR2 song.db sync では再現可能でも file diff / アプリ内導入では周辺譜面依存になり、
  差分同期の設計を複雑化させるため採用しない。既存実装に残る post-write
  `NormalizeUndefinedSongDifficulties(songDb)` は撤去済みで、`difficulty` は書き込み前に
  row-local / chart_info-local な値へ確定する。
- `maxbpm` / `minbpm` / `longnote` / `bga` / `random` / `karinotes` / `exlevel` など、
  lightweight parser だけでは足りない列も detailed parser / `chart_info` から補う。
  `chart_info` に列や意味が足りないことが分かった場合は、既存 `song.db` の値を守る fallback で埋めず、
  `chart_info` / parser contract の拡張を検討する。
- `chart_info.judge` は判定幅 percent であり、LR2 `song.judge` の `#RANK` raw 値ではないため流用しない。
- `chart_info` と lightweight parser の両方が値を持つ列は、列ごとに precedence を明示する。
  原則として LR2 `song` row の基本メタデータは lightweight parser、詳細解析でしか安定しない列は
  `chart_info` を正本にする。
- LR2 の細かいバグ再現は目標にしない。

テスト:

- 新規 BMS 行の `date` が snapshot mtime になる。
- `txt` が text group から正しく反映される。
- BMS 本体未変更でも `.txt` 追加・削除で `song.txt` だけ更新される。
- detailed parser 由来 column が埋まる。
- CP932 非対応 path でもクラッシュしない。

### Phase 6: `folder` row generator と generation scope を追加する

目的:

- root / ancestor / normal folder rows を deterministic に生成する。
- generation scope 内の stale rows を安全に prune する。
- `.lr2folder` source は Phase 7 で追加できるよう、source 種別を拡張可能にする。

入力:

- LR2 BMS root directories
- BMS chart directories
- directory metadata surface
- `folderinfo.txt`
- `.lr2folder` parent/category directory metadata
- existing folder rows

性能条件:

- chart ごとに `Directory.GetLastWriteTime` / `folderinfo.txt` check を行わない。
- root / ancestor / BMS chart directory を unique directory set に dedupe してから metadata を取得する。
- directory metadata と `folderinfo.txt` existence は Phase 3 の metadata-bearing enumeration surface から取得し、
  native bridge と managed fallback の意味を揃える。
- `LR2CustomFolderOutputBaseDir` / `LR2CustomFolderOutputBaseDirRootType` は chart / resource scan の
  explicit root にはしない。ただし親 BMS root に内包される場合は subtree 除外しない。
- Everything query に exclude DSL は追加せず、root / extension / filename の組み合わせで
  chart / resource surface と directory metadata surface を分ける。

生成ルール:

- directory の `path` は trailing separator 付き。
- root folder の `parent` は `AssignCRC32("ROOT")`。
- non-root folder の `parent` は parent folder path + trailing slash + NUL の LR2 CRC。
- `date = directory metadata surface の mtime を Unix 秒化した値`。
- `adddate` は existing row があれば維持し、新規だけ現在時刻。
- `folderinfo.txt` があれば LR2 風に parse する。対象は normal BMS directory だけでなく、
  `.lr2folder` parent/category directory row、特に `LR2files\CustomFolder` 配下の category directory row を含む。
- `folderinfo.txt` がなければ directory name を `title` にする。
- `date = NULL` / `date = 0` は生成しない。

テスト:

- root folder row の `parent` が ROOT CRC。
- root folder row の `date` が directory mtime。
- nested folder row の `parent` が親 directory CRC。
- generation scope から消えた row が prune される。
- current LR2 root / custom folder output / built-in CustomFolder source から導けない stale row は prune される。
- Phase 7 の `.lr2folder` source を追加しても prune 基盤を流用できる。

### Phase 7: `.lr2folder` DB 同期を追加する

目的:

- BeMusicSeeker が出力した `.lr2folder` と、LR2 BMS root 配下の任意 `.lr2folder` を
  LR2 scan に任せず `folder` table へ同期する。

対象:

- `playlist_output_lr2folder`: BeMusicSeeker が出力する `.lr2folder`。
- `discovered_lr2folder`: LR2 BMS root / custom folder 出力 base / LR2 built-in custom folder source
  (`LR2files\CustomFolder`) から discovery した `.lr2folder`。
- discovery root には LR2 BMS search root、通常出力先 `LR2CustomFolderOutputBaseDir`、
  ルート出力先 `LR2CustomFolderOutputBaseDirRootType`、LR2 executable directory 配下の
  `LR2files\CustomFolder` を含める。
  - `LR2files\CustomFolder` は LR2 setup の `<customfolder>` bitmask に従って、
    `RANDOM/`, `favorite.lr2folder`, `TOP10.lr2folder`, `PLAYLEVEL/`, `CLEAR/`,
    `RANK/`, `ignore.lr2folder`, `INSANE01/`, `INSANE02/` を対象化する。
  - `course1.lr2folder`, `course2.lr2folder`, `course3.lr2folder` は bitmask と独立して常時対象にし、
    `folder.type = 6` として同期する。
  - `newsong.lr2folder` は `titleflash` と `song.adddate` から対象曲有無を判定し、対象曲がある場合だけ
    `folder.type = 3` として同期する。
  - `LR2files\Rival` は built-in discovery root に含めない。LR2 が ranking server 通信と active rival ID に基づいて
    `folder` row を更新する領域であり、BeMusicSeeker から全件 discovery / prune しない。
  - 通常の LR2 BMS search root や custom folder 出力 base の中で見つかる `__RIVAL__` `.lr2folder` は、
    built-in Rival ではなく外部 `discovered_lr2folder` として処理する。
- playlist output / discovered / built-in は query では絞らず、discovery result と current output path set の
  照合で分類する。
- BeMusicSeeker 管理の `playlist_output_lr2folder` では、`.lr2folder` 実ファイルを正本にしない。
  - `playlist` / `playlist_entry` / `playlist_course` とプレイリスト出力設定を正本にする。
  - 同一の `Lr2PlaylistCustomFolderProjection` から `.lr2folder` 本文と LR2 `folder` row を生成する。
  - ルートフォルダ出力では、playlist/table directory row を LR2 root 直下に生成し、配下の
    numbered `.lr2folder` row はその directory row の子として生成する。配下 row をすべて
    `ROOT` 親へ flatten しない。
  - `folder.command` は生成した `#COMMAND` と一致させる。既存の numbered `.lr2folder` が
    `playlist_entry` table を参照する SQL command を出す場合、entry 内容の正本は
    `playlist_entry` table であり、command はその table を参照する projection として同期する。
  - `folder.date` は出力後の `.lr2folder` file mtime にする。`date = NULL` / `date = 0` は禁止する。
  - `adddate` は既存 row があれば維持し、新規 row だけ現在時刻にする。
- 外部由来の `discovered_lr2folder` と LR2 built-in custom folder source だけが `.lr2folder` parse 結果を
  `folder` row 生成の正本にする。
- `.lr2folder` 実ファイルは既存の出力設定または外部ツールの管理に任せる。
- Everything query に exclude DSL は追加せず、root / extension / filename の組み合わせで
  `.lr2folder` file surface を取得する。アプリ生成物か外部生成物かは query ではなく結果分類で判定する。
- 既存の custom folder 出力し直し、出力先移動、削除、`ignore_folder_output` 変更、
  `is_root_folder` 変更、playlist entry 更新後の再出力では、`.lr2folder` file 出力だけで終わらせず、
  同じ workflow で generation scope 内の `folder` row を upsert / prune する。

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
- playlist 由来 `.lr2folder` 出力で、`.lr2folder` 本文と `folder.command` が同一 projection から生成される。
- `.lr2folder` 再出力だけだった操作でも `folder.command` / `folder.date` / `folder.max` が更新される。
- `.lr2folder` 削除で generation scope から row が消える。
- custom folder 出力先移動で旧 numbered `.lr2folder` の `folder` row が prune され、新 row の
  `date` / `adddate` が `NULL` にならない。
- `ignore_folder_output` / `is_root_folder` / playlist entry 更新で affected `folder` row が upsert / prune される。
- discovered `.lr2folder` で `folder` row が入る。
- discovered `.lr2folder` が消えた場合は派生 `folder` row だけが消え、実ファイル削除は行わない。
- 通常出力先 / ルート出力先配下の外部生成 `.lr2folder` は discovery result として扱われる。
- BeMusicSeeker 生成 `.lr2folder` は同じ discovery result から playlist output として分類される。
- `LR2files\CustomFolder` の built-in source は `<customfolder>` bitmask に従って同期される。
- `LR2files\CustomFolder` 配下の category directory row は、同 directory の `folderinfo.txt #TITLE`
  があれば title に反映し、無ければ directory name を使う。
- `newsong.lr2folder` は対象曲がある場合だけ `type = 3` で同期され、対象曲がない場合は prune される。
- `course1.lr2folder` / `course2.lr2folder` / `course3.lr2folder` は `type = 6` で同期される。
- `LR2files\Rival` は built-in discovery root に含めず、既存 row を全件 prune しない。
- 通常 discovery 範囲で見つかった `__RIVAL__` `.lr2folder` は外部 `.lr2folder` として同期される。
- current discovery scope から導けなくなった unknown / stale `.lr2folder` row は prune される。
- ルート出力先配下の `.lr2folder` 親 directory row と built-in `LR2files\CustomFolder` のカテゴリ directory row は
  expected scope として扱われ、unknown root blocker にならない。
- Shift_JIS 出力した `.lr2folder` を同じ解釈で parse できる。

### Phase 8: LR2 song.db sync status / sync UI を追加する

目的:

- LR2 song.db sync が未完了の状態を UI / log で追跡できるようにする。
- sync が発生する場合にユーザーへ見える形にする。

実装:

- LR2 song.db sync status を保持する。
- status は `NotNeeded` / `Needed` / `Running` / `Completed` / `Failed` / `Cancelled` /
  `Incomplete` とする。
- status と signature を LR2 `song.db` 内の BeMusicSeeker-owned metadata table に保存する。
- status が存在しない、contract signature が変わった、または前回 status が `Failed` / `Cancelled` /
  `Incomplete` の場合は `Needed` とする。root set や built-in folder 設定の変化だけでは `Needed` にしない。
- LR2 連携モードで `Needed` の場合、`startup_initialization_complete` 後に background workflow を queue する。
- queued workflow は File Enumeration / Everything Surface Contract の consumer であり、
  sync input 作成のための独自広域再スキャン入口ではない。直前の startup / file diff scan surface を
  再利用し、surface が stale / missing の場合は通常 scan cycle で surface を作り直すか、
  `source_stale` / `Incomplete` として次回へ回す。
- sync needed / running / completed / failed / cancelled / incomplete を log と UI に出す。
- sync progress は全体合算 total と stage 別 processed count の両方を表示する。
- `Needed` / `Running` / `Incomplete` / `Failed` / `Cancelled` は LR2 song.db sync status warning として表示する。
- startup-scan diagnostic は、workflow が upsert / prune した後の実装確認ログと同一 run 内の修復候補に限定する。
  保存済み `Completed` の再評価や次回起動時の full sync 要否判定には使わない。
- 設定値が欠落または不正な場合は既定値へ正規化する。設定値 warning は出さない。
- LR2 config の auto update 設定検出や変更は行わない。manual-only 運用の推奨は docs に記載し、
  LR2 song.db sync status には含めない。
- sync のためだけの自動 backup は作らない。
- write は per-chunk transaction とし、chunk commit 成功後に processed cursor / run id / status を durable に更新する。
- chunk 失敗時はその chunk の transaction を rollback し、status を `Failed` にする。
- cancel 後の partial write は incomplete として扱い、status warning に出す。
- 次回 run は最後に成功した cursor から再開する。再開前に必要なら current status / signature を再検証する。
- sync 中は read-only 操作を許可し、owned collection / LR2 `song.db` mutation 操作は開始前に抑止する。
- sync の完了直前に owned / storage generation と LR2 root / `.lr2folder` discovery root /
  built-in custom folder 設定 snapshot を再確認し、同じ run の開始時入力から変わっていれば `Completed` にしない。
  この確認は running-run guard であり、保存済み `Completed` signature に runtime input を含めるものではない。
  startup-scan diagnostic の残件はログに残すが、保存済み `Completed` を覆す追加検査にはしない。
  `.txt` / `folderinfo.txt` / `.lr2folder` / directory metadata の広域再列挙は
  完了直前には行わず、次回 scan / signature 再評価へ委ねる。

テスト:

- sync 不要時は警告なし。
- sync 必要時は警告と進捗が出る。
- status 欠落、signature 変更、前回 `Failed` / `Cancelled` / `Incomplete` で `Needed` になる。
- 不正な設定値は既定値へ正規化され、設定値 warning は出ない。
- failed 状態で LR2 song.db sync status warning が出る。
- cancel 後に incomplete status が残り、次回再開できる。
- sync 中に read-only 操作は許可され、library mutation 操作は抑止される。
- workflow 修復後も owned / storage / root 設定 snapshot の stale など、再試行で実際に解消すべき
  source 不整合が残る場合は `Completed` にならない。startup-scan diagnostic の残件だけでは
  `Completed` を妨げず、ログで次の改善対象として追う。
- 完了直前に owned / storage generation や LR2 root / built-in custom folder 設定 snapshot が
  同じ run の開始時入力から変わった場合は、その run を `Completed` にしない。
  次回起動時は schema / generator / parser contract signature で full sync 要否を判定し、runtime input の変化は
  file diff / scoped folder sync で収束させる。

### Phase 9: resumable sync

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
- LR2 song.db sync run では実ファイルと scan surface を正本にし、generated columns は bulk upsert / prune で収束させる。
  `favorite` / `adddate` / `tag` など LR2 user columns は維持するが、generated columns を守るための
  1 row 1 SELECT / 1 row 1 orphan check は使わない。
  既存 generated columns の `NULL` / 旧値は、LR2 連携モードの current projection より優先しない。
  LR2 song.db sync は「既存 DB の足りない値を保護しながら埋める」処理ではなく、
  current owned chart set と scan surface 由来の cache へ収束させる処理とする。
- preflight backup / restore point は作らない。
- run id / durable status / processed cursor を持つ。
- `song_rows` は軽量 target list から開始する。`BMSFile.CreateSongRowPersistenceCopy()` を全件分作って
  sync input に保持しない。
- `song_rows` は reader が `ChartFileSnapshot` を bounded queue に流し、parallel workers が
  BMS row build / `chart_info` memory resolver apply / LR2 compatibility facts を同じ parse result から作り、
  single writer が chunk transaction と durable cursor 更新を行う。
  LR2 compatibility facts は BOM 自動判定なしの CP932 decode 由来 parsed BMS row の raw resource references から直接評価し、
  `ChartFileProjection` / `ChartResourceSnapshot` への再投影を hot path に置かない。
  commit 成功後だけ cursor を進め、cancel / failure は chunk 境界で再開できるようにする。
- `chart_info` は run 開始時に current row index / session index として一括準備する。
  `song_rows` chunk ごとに `chart_info` table を SELECT しない。
- writer は full sync 用 bulk writer を使う。chunk 開始時に必要な既存 user columns / `adddate` /
  generated row identity をまとめて読み、memory 上で preservation / changed decision を行う。
  existing row の generated columns が一致する場合は `song` row を更新しない。
  `chart_digest_map` update と orphan cleanup も chunk / run 単位でまとめ、行単位の `FindSongByPath` /
  `UpsertChartDigest` / `DeleteChartDigestIfOrphaned` を hot path に置かない。
  LR2 compatibility facts の `maintenance` update / insert も chunk temp table 経由でまとめ、
  row ごとの update-miss-insert を hot path に置かない。
  final song prune の current path temp table も multi-value insert で作り、path ごとの SQL round-trip を避ける。
- chunk 成功後だけ cursor を進め、失敗 chunk は rollback して次回再処理する。
- 完了直前の owned / storage / root 設定 snapshot check が current であり、startup-scan diagnostic に対する
  prune / update / resync を同じ run 内で試みたら `Completed` を記録する。generated DB row は実ファイル由来の一覧 cache なので、
  expected set 外 row / unknown root row / stale generated row は守らず、同じ run 内で prune または update して
  current surface へ収束させる。残った diagnostic はログ化し、LR2 compatibility warning は blocker にせず
  maintenance warning として保持する。
- `favorite` / `adddate` / `tag` を維持。
- CP932 非対応 BMS row は BeMusicSeeker DB から削除しない。
- sync 再実行で追加差分が出ない。

テスト:

- `date = null` の既存 row が mtime で sync される。
- 維持列が維持される。
- CP932 非対応 BMS row が削除されない。
- 2 回目 sync が no-op になる。
- partial run 後に resume できる。
- failed chunk が rollback され、次回同じ target から再開できる。
- owned / storage / root 設定 snapshot の staleness が検出された run は `Completed` にならず、
  次回再実行対象になる。

## データマッピング早見表

### `song`

- `hash`: snapshot MD5。
- `path`: full BMS path。
- `folder`: LR2 互換 path の場合、chart directory path の LR2 CRC。
- `parent`: LR2 互換 path の場合、parent directory path の LR2 CRC。
- `title`, `subtitle`, `genre`, `artist`, `subartist`, `stagefile`, `banner`, `backbmp`:
  lightweight parser の値。保存値は既存 contract を維持し、`difficulty` 推定のためだけに
  OpenLR2 風の title/subtitle 分割を保存値へ反映しない。
- `mode`, `judge`:
  lightweight parser の値。`mode` は LR2 での key mode 表示 / プレイ対象に関わるため OpenLR2 寄せを維持する。
  `judge` は LR2 `#RANK` raw 値で、判定幅 percent の `chart_info.judge` は使わない。
- `level`, `difficulty`:
  current `chart_info` / detailed parser の値を優先する。`difficulty` は `#DIFFICULTY` explicit value と
  detailed parser の deterministic inference を採用し、`chart_info` が使えない場合だけ lightweight parser の
  valid value、さらに無ければ fixed default `2` を使う。DB 全体の `folder, mode, karinotes` 順に依存する
  OpenLR2 `SetUndefinedDifficulty` 相当の post-write 補完は採用しない。
- `maxbpm`, `minbpm`, `longnote`, `bga`, `random`, `karinotes`, `exlevel`:
  detailed parser / `chart_info` から補う。`chart_info` で足りない列がある場合は
  `chart_info` contract の拡張を検討し、既存 `song` row の generated value を保存して穴埋めしない。
- `date`: snapshot last write time の Unix 秒。
- `adddate`: 新規 row は現在時刻。既存 row は維持。
- `favorite`: 既存値を維持、なければ `0`。
- `tag`: 既存値を維持。
- `type`: `0`。
- `txt`: text group で BMS chart directory 直下に任意 `.txt` があれば `1`、なければ `0`。

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

`folder` row の generation source:

- `normal_folder`: LR2 root / ancestor / chart directory から生成した row。
- `playlist_output_lr2folder`: BeMusicSeeker が出力した `.lr2folder` から生成した row。
- `discovered_lr2folder`: LR2 BMS root / custom folder 出力 base / LR2 built-in custom folder source で
  discovery した `.lr2folder` から生成した row。
  実ファイルは管理せず、DB row だけを current discovery result の派生 cache として生成する。
- `max`: `#MAXTRACKS` または `#PLAYLEVEL`。なければ `0`。
- `adddate`: 新規 row は現在時刻。既存 row は維持。

## リスクと注意点

- BMS 変更検出は LR2 song.db sync の前提条件。path added/deleted だけでは不十分。
- 通常差分検出は LR2 `song.path` / `song.date` を正本にする。mtime preserved copy や
  同秒更新は完全には検出できないため、force rescan を残す。
- LR2 song.db 同期は未リリース機能であり、旧 LR2 DB / 未移行 maintenance を特別扱いする互換移行モードは持たない。
  生成済みかどうかは durable `Completed` + signature で判断し、row nullable column の NULL や旧 schema 由来の
  欠落から追加の未移行状態を推測しない。
- `maintenance` の LR2 byte count fact は nullable である。CP932 非対応、resource reference なし、測定対象なしを
  `NULL` で表すため、`NULL` を未評価 marker として扱う検査を file diff / status 判定へ入れない。
- Everything と managed fallback の結果は意味的に揃える。片方だけ text group や metadata
  を返す状態にしない。
- text group だけでなく、directory mtime、`folderinfo.txt`、`.lr2folder` の existence /
  mtime についても native / fallback parity test を置く。
- LR2 連携モードだけで重い LR2 専用列挙を増やす。スタンドアロンモードの既存動作を重くしない。
- LR2 は legacy narrow path を多用する。BeMusicSeeker 内では unsupported path を保持して
  よいが、LR2 起動時 scan に踏ませる row は慎重に扱う。
- CP932 byte length の警告と Unicode string length の警告は分ける。
- `ChartResourceSnapshot` の normalized resource key を LR2 warning 判定の raw path として
  流用しない。
- folder row の削除は generation scope 内に限定する。scope 外の row は原則触らない。
- LR2 が起動中の `song.db` 書き込みは競合する可能性があるため、busy/lock 失敗を明示ログにする。

## フェーズ進捗

この表は、この計画書を実装タスクとして渡す際の現在地を示す。上の「実装フェーズ」は
ゴール仕様、ここは実装済み範囲と残作業の管理を目的にする。

| Phase | 状態 | 実装済み / 現行決定 | 残作業 / 注意 |
| --- | --- | --- | --- |
| Phase 0: Golden fixture と contract 固定 | 主要実装済み | `LR2CRC32` / ROOT sentinel / CP932 boundary の contract、OpenLR2 source classifier の推測抑止、`exlevel` contract、manual-only scan blocker、copied `song.db` の current completed no-op はテスト化済み。`D:\LR2beta3\LR2files\CustomFolder` 由来の built-in CustomFolder 物理 fixture は追加済み。 | app 生成ではない LR2 / OpenLR2 生成 `song.db` が無いため、実 DB 期待 row との golden 比較は保留。外部 `.lr2folder` / 通常 BMS directory `folderinfo.txt` は正本性を断定できないため、必須 fixture ではなく regression 補強として扱う。 |
| Phase 1: BMS 変更検出 | 主要実装済み | `song.path` / `song.date` / hash を使う変更検出、same MD5 の targeted update、runtime reload の再評価 queue は接続済み。 | 大規模 root 変更・mtime preserved copy の手動検証を残す。 |
| Phase 2: `song` row merge / ownership | 主要実装済み | `Lr2SongDbWriter`、generated/user column 分離、runtime write failure の status marking、merge 時 user column preservation、copied `song.db` sync 時の user column preservation は自動テスト済み。 | 実 DB copy での総合確認を残す。 |
| Phase 3: metadata-bearing scan surface / raw resource reference | 主要実装済み | BMS parser / snapshot 側の raw resource reference、text group の targeted `song.txt` 更新、`RootFileEnumerationResult` の file / directory mtime entry、Everything fixed scan + grouped directory query / managed fallback の metadata surface は実装済み。chart file mtime は fixed native scan / managed fallback の両方から `ChartScanResult` に保持し、通常 file diff の `song.date` / `bmson_song.updated_at` 判定へ使う。`.txt` / `folderinfo.txt` / `.lr2folder` / directory metadata surface は startup / file diff scan surface から sync input へ保持する。scan surface の gate は `OperationModeLR2DB` へ整理済み。 | 実機ログで、スタンドアロンでは LR2 専用 surface を作らないこと、LR2 連携では `.txt` / `.lr2folder` / `folderinfo.txt` / directory mtime が必要範囲で保持されることを確認する。 |
| Phase 4: LR2 compatibility warning | 主要実装済み | `Lr2CompatibilityEvaluator`、maintenance 最小 fact、standalone mode での compatibility tree 非表示は接続済み。表示名は `LR2互換性警告` に整理済み。廃止済み `EnableLR2SongDbSync` 依存は撤去済み。 | LR2 連携モードで path / resource 両方の LR2 compatibility warning が `ChartWarningCategory.Lr2Compatibility` として表示されることを実機確認する。 |
| Phase 5: `song` row enricher | 主要実装済み | `song_rows` stage と file diff は `ChartFileSnapshot` から lightweight parser を使い、`Lr2SongRowEnricher` で `date` / `txt` / folder-parent CRC / user column preservation を適用する。missing / stale `chart_info` は同じ snapshot から worker が生成し、`chart_info.judge` を `song.judge` へ流用しない判断は維持する。`mode` / `judge` は LR2 実行時挙動へ関わるため lightweight parser の OpenLR2 寄せを維持する。`level` / `difficulty` は譜面メタデータとして `chart_info` / detailed parser 由来を優先し、`difficulty` 未定義は fixed default `2` に収束させる。post-write `NormalizeUndefinedSongDifficulties(songDb)` / DB-wide finalization は撤去済みで、LR2 song.db 同期・file diff・アプリ内導入で同じ row-local / chart_info-local precedence に揃える。 | `song.date` を抜いて LR2 に再解析させる分岐は作らない。実 DB fixture では構造列 (`folder` / `parent` / `.lr2folder`) と `mode` / `judge` を重点比較し、`level` / `difficulty` は chart_info contract との差分を確認する。 |
| Phase 6: normal `folder` row generator | 主要実装済み | normal folder generator / scope planner / DB sync、mutation・file diff failure の incomplete marking、directory metadata surface 由来の `folder.date` resolver、`folderinfo.txt` entry metadata surface は接続済み。通常 file diff では current scan surface の directory mtime と既存 `folder.date` の exact path 比較を同期トリガーにし、差分がある directory だけを upsert 対象にする。削除譜面の親 directory は mtime 差分がある場合だけ prune scope にし、root 直下削除を root 全体 prune へ広げない。実行可否は `OperationModeLR2DB` へ整理済み。 | スタンドアロンでは `folder` row を生成しないこと、LR2 連携では directory mtime / folder row freshness が維持されることを実機確認する。 |
| Phase 7: `.lr2folder` DB sync | 主要実装済み | playlist projection と `.lr2folder` / `folder` row sync の同一化、通常 discovery、built-in source の相対 path 化、root custom output / built-in category parent row 生成、`LR2files\Rival` の built-in discovery 非対象化、`LR2files\CustomFolder` の `<customfolder>` bitmask、built-in category parent row の `folderinfo.txt #TITLE` 反映、`newsong` dynamic row、`course1-3` の `type=6` は接続済み。 | 実 LR2 setup / built-in folder fixture で最終確認する。 |
| Phase 8: status / sync UI | 主要実装済み | durable status、runtime progress、cancel、mutation guard、queued 後 preflight stage (`chart_info_hydration` / `input_surface` / `compatibility_projection_index` / `chart_info_resolver_snapshot`) の runtime 表示は実装済み。UI から専用 enable 設定は削除済みで、明示操作は `LR2 song.db データを再同期` に集約済み。status / signature から公開設定値も撤去済み。 | 長時間 sync の UI 手動確認と failure/cancel 再起動確認を残す。 |
| Phase 9: resumable sync | 主要実装済み | durable cursor、stage / chunk resume、cancel boundary、completed status current 時の 2 回目 no-op queue、copied `song.db` の current completed no-op、failed song-row chunk rollback/retry、cancel 後 restart resume、`song_rows` の bounded reader / worker / writer pipeline、worker-side `chart_info` apply / LR2 compatibility fact build、run-scoped `chart_info` resolver、sync input の全件 `BMSFile` persistence copy 廃止、generated song temp-table writer、chunk digest / maintenance facts writer、final prune temp path bulk insert、`Lr2SongDbSync...` / `lr2_song_db_sync_*` へのリネームは自動テスト対象とともに反映済み。 | 実機ログで `song_rows` が単純 file read benchmark に近づいているか、chunk `readMs` / `parseMs` / `commitMs` / queue wait を確認する。 |

### フェーズ別進捗メモ

以下はフェーズごとの設計責務と実装時の注意を残す詳細メモである。完了 / 未完了の判定は
上の進捗表を正とし、残作業は後続の「残作業の推奨順」で管理する。

- Phase 0: golden fixture は一部実装済みで、残りは該当 service の実装・検証 cycle に合わせて追加する。
  - まず既存 `Lr2SongFolderParentNormalizer` の CRC / CP932 encode contract を固定する。
  - byte length boundary と manual-only scan blocker は自動テスト済みである。
  - `folderinfo.txt` / `.lr2folder` は synthetic fixture で主要 contract を固定済みで、残りは実 DB 由来 fixture として追加する。
  - LR2 root sentinel は `LR2CRC32("ROOT")` ではなく `LR2CRC32("ROOT\0") = e2977170` として扱う。
- Phase 1: BMS 変更検出は主要経路へ接続済み。残りの検証では以下を固定する。
  - 通常状態では、file diff で既存 BMS の `song.date` / mtime mismatch を parse target に入れる。
    ただし `Completed` でない状態の既存 generated metadata 全量 refresh は後段 LR2 song.db sync へ任せ、
    maintenance nullable fact などの追加検査で file diff target を広げない。
  - MD5 が同じ場合は `song.date` の targeted update だけ行い、chart_info / maintenance は再生成しない。
  - MD5 が変わる場合は parsed row へ差し替え、`favorite` / `adddate` / `tag` は既存 row から維持する。
  - 削除された path と新規 path が同じ MD5 で一意に対応する場合だけ、LR2 user columns を新規 row へ継承する。
    source または destination が同一 MD5 で複数ある場合は `lr2_song_relink_ambiguous` として記録し、誤継承を避ける。
- Phase 2: `Lr2SongDbWriter` と generated/user column 分離は実装済み。
  - まず `Lr2SongDbWriter` を導入し、既存 `song.path` row がある場合は `favorite` / `adddate` / `tag` を
    DB 上に残したまま generated columns だけを更新する。
  - `txt` / text group は Phase 3 で正本を設計してから扱うため、この段階では従来どおり generated row 側の値を保存する。
  - `song.hash` が `NULL` の既存 row も existing row として扱い、hash 取得結果だけで new row 判定しない。
- Phase 3: raw resource reference と targeted `song.txt` 更新の基盤は実装済み。
  `RootFileEnumerationResult` と native grouped bridge decode result は file / directory metadata entry surface になっている。
  `ChartScanResult` は `.txt` / `folderinfo.txt` entry metadata を保持し、merged scan result と normal folder sync /
  sync request へ渡す。
  - fixed native resource scan は、chart file mtime と、chart / audio / image / movie に加えて `.txt` / `folderinfo.txt`
    query の file mtime を同じ bridge layout で返す。LR2 song.db sync の generated row に使う
    `song.date` / `folder.date` / `.lr2folder.date` / `folderinfo.txt` / `.txt` 判定や
    startup-scan diagnostic では、metadata 欠落時に受け取り側で live lookup に戻らない。
    欠けた metadata は scan surface / scoped producer 側の契約不足として扱う。
  - `.lr2folder` は grouped enumeration surface で列挙し、同じ `RootFileEnumerationEntry` shape で sync へ渡す。
  - managed fallback は列挙時に同じ metadata を持つ。native / fallback のどちらでも後追い全件 stat を行わない。
  - `song.date` が一致していて BMS 本体 MD5 が同じ場合、`.txt` 増減は targeted `song.txt` update だけ行い、
    chart_info / maintenance は再生成しない。
  - raw resource reference は runtime-only の `ChartResourceReference` として BMS parser で保持する。
    `WAVfiles` / `BGAfiles` の normalized lookup set は既存 resource health / install estimation の正本として残し、
    LR2 warning 用 raw path は `ChartResourceSnapshot.ResourceReference.RawPath` へ別投影する。
  - `ChartResourceSnapshot.AudioReferences` / `VisualReferences` / `MovieReferences` は既存どおり unique lookup key の list とし、
    LR2 warning 用には `ResourceReferences` で valid raw directive を全件保持する。同一 lookup key に複数 raw directive があっても、
    resource health の count は増やさず、LR2 raw path evaluation では全 raw directive を見る。
  - optional image (`stagefile` / `banner` / `backbmp`) は既存 snapshot field から扱い、`#WAV` / `#BMP` raw reference collection へは混ぜない。
- Phase 4: compatibility evaluator と maintenance projection は実装済み。
  - まず `Lr2CompatibilityEvaluator` を fact-only service として導入し、schema / warning projection には接続しない。
  - path CRC は `Lr2SongFolderParentNormalizer` の既存 contract を再利用し、CP932 byte length と resource raw/resolved path fact だけを追加する。
  - legacy path length boundary は NUL 終端を除いた CP932 259 bytes を上限として扱う。
  - warning projection は `ResourceHealthWarningProjection` へ混ぜず、maintenance facts が評価済みの BMS row だけ
    `BMSFile.Warnings` の `Lr2Compatibility` category として差し替える。未評価 row は placeholder attach だけで既存 warning を消さない。
- Phase 5: `Lr2SongRowEnricher` と lightweight parser / chart_info 由来 column reflection は方針変更あり。
  - まず `Lr2SongRowEnricher` を導入し、既存の `date` / `txt` / user columns preservation / folder-parent CRC 正規化を
    file diff parser と DB writer から同じ入口へ寄せる。
  - LR2 song.db sync / 再同期では `ChartFileSnapshot` から `BMSFile.CreateBMSFileFromSnapshot` を通し、
    lightweight parser の結果を `song` row の基本メタデータにする。
  - `mode` / `judge` は lightweight parser の値を使う。
    OpenLR2 寄せの default 補正も lightweight parser 側で行い、LR2 song.db sync / 再同期では既存 DB の
    `NULL` や `chart_info` の overlapping value で上書きしない。
  - `level` / `difficulty` は譜面メタデータとして扱い、current `chart_info` / detailed parser の
    単体譜面決定値を優先する。`difficulty` は `#DIFFICULTY` raw 値と detailed parser の deterministic
    inference を採用し、`chart_info` が使えない場合だけ lightweight parser の valid value、
    さらに無ければ fixed default `2` を入れる。
    `folder, mode, karinotes` 順の DB-wide finalization は LR2 song.db sync では再現可能でも
    file diff / アプリ内導入では周辺譜面依存になり、差分同期の設計を複雑化させるため撤去済み。
  - `chart_info` は `level` / `difficulty` / `maxbpm` / `minbpm` / `longnote` / `bga` /
    `random` / `karinotes` / `exlevel` など、詳細 parser が正本を持つ列の補完に使う。
  - `song.judge` は LR2 の raw `#RANK` 値で、`chart_info.judge` は判定幅 percent なので写さない。
  - `bga` は BMS/BMSON timeline 上の BGA event 有無、`exlevel` は BMS `#EXLEVEL` の raw 値を正本にする。
    `#DEFEXRANK` は判定幅計算だけに使い、`exlevel` 未定義時は `0` を入れる。
  - lightweight parser の version 管理は追加しない。改善後の値は手動 LR2 song.db 再同期で最新化できる。
- Phase 6: normal folder generator / scope planner / DB sync は主要経路へ接続済み。
  - まず DB 接続前の pure `Lr2FolderRowGenerator` を追加し、LR2 root / ancestor / chart directory から
    normal `folder` row と generation scope path set を作る contract を固定する。
  - `folderinfo.txt` は directory metadata snapshot の候補 surface から読み、`#TITLE` を normal folder row title へ反映する。
  - 既存 `folder` row の `adddate` は path match で維持し、`date` は directory metadata mtime を正本にする。
  - directory metadata mtime が取れない row は `date = NULL` で生成せず、metadata surface 側の欠落として扱う。
  - CP932 非対応の directory row は生成せず、通常 BMS の `song.folder` / `song.parent` と同じく LR2 に踏ませない前提にする。
  - DB writer 接続前に `Lr2FolderGenerationScopePlanner` を追加し、生成 row と既存 row から upsert/delete plan を作る。
    prune 対象は current LR2 root scope 内の normal `folder.type = 1` row に限定し、`.lr2folder` / custom folder row と
    scope 外 row は触らない。
  - 既存 non-normal row と同じ normalized key に normal row が生成される場合、normal row upsert は抑止する。
    同一 path 複数 source の優先順位は Phase 7 の `.lr2folder` source 統合で扱い、normal generator が custom row を
    `InsertOrReplace` で上書きしないようにする。
  - metadata mtime 欠落がある partial generation では prune と exact-key drift repair を抑止し、完全な metadata surface で
    再生成できた時点で delete + upsert を行う。
  - `Lr2FolderDbWriter` は scope plan を exact path の delete と generated row の upsert として単一 transaction で適用する
    薄い層にする。安全判定は planner の責務にし、writer 側で独自 prune 判断を増やさない。
  - generation scope plan は initialization / file diff / owned mutation workflow へ接続する。
    file diff の full scan 失敗時は normal folder sync を skip し、normal folder sync 自体が失敗した場合は
    `lr2_song_db_sync_status` を `Incomplete(lr2_normal_folder_file_diff_sync_failed)` に落として次回 sync で修復する。
    owned mutation 側も sync 失敗時は `Incomplete(lr2_normal_folder_mutation_sync_failed)` に落とす。
  - directory metadata surface は `Lr2FolderDirectoryMetadataSnapshot` として分離する。入力は unique directory set と
    metadata-bearing enumeration surface の directory mtime / `folderinfo.txt` candidate file entry で、
    `folderinfo.txt #TITLE` parse・欠落/読み取り失敗 count をここで集約する。
    native bridge ABI と managed fallback の両方が directory mtime を同じ shape で返し、
    initialization / mutation 側は owned chart から dedupe した directory set と metadata surface をこの snapshot に渡す。
  - `Lr2NormalFolderDbSyncService` は normal directory folder row の production-shaped compose 層にする。
    既存 row 読み込み、metadata snapshot、normal row 生成、scope plan、DB writer 適用をまとめるが、initialization / mutation の
    呼び出し判断や feature gate は持たない。chunk-local な file diff commit へ folder prune を混ぜず、full scan 完了後に
    complete chart path set を渡す呼び出し側から使う。`AllowPrune` は complete scan と source generation の整合を確認した
    caller だけが立て、既定では upsert のみ行って stale row delete はしない。
  - initialization への production 接続は `OperationModeLR2DB` で gate する。
    LR2 linked mode の場合、full file scan 完了後に current scan surface の directory mtime と
    既存 `folder.date` を exact path で比較し、差分がある directory だけ normal folder sync へ渡す。
    `Completed` steady-state でも `folder` table 全件検証は行わず、scan surface が持つ directory metadata と
    exact lookup だけで同期範囲を決める。
  - full scan 後の normal folder sync は file diff DB commit を flush した後に別 workflow として実行し、
    owned collection の `HasDbDiff` や UI refresh 判定には混ぜない。folder sync の生成 / upsert / delete / metadata 欠落は
    `song_tbl_file_check_breakdown` の LR2 normal folder metrics として追跡する。
  - 通常 file diff の追加・更新は BMS path 差分そのものを normal folder sync の起動条件にしない。
    新規 directory row や `folderinfo.txt` title 反映は、scan surface の directory mtime と DB `folder.date` の
    mismatch から検出する。
  - 通常 file diff の削除 scope は、削除された譜面の親 directory が directory mtime 差分対象だった場合だけ prune scope にする。
    root 直下の譜面削除は root 全体 prune に広げない。少数削除で sibling subtree 全体を再生成・prune しないため、
    scope 内に残る current BMS path だけを追加して、その exact directory 内の stale normal folder row を収束させる。
  - full scan 境界では complete chart path set と `folderinfo.txt` scan surface を渡せるため `AllowPrune=true` とする。
    ただし compose service 側は CP932 非対応 chart path や metadata 欠落がある場合に stale normal row deletion を抑止する。
    `ChartScanExecutionResult.Success != true` の partial surface では normal folder sync 自体を skip し、`folderinfo.txt`
    surface 欠落や chart path 不完全性を stale row prune / title overwrite に使わない。
  - `folderinfo.txt` は単独の previous/current file surface diff を持たない。
    親 directory の mtime が DB `folder.date` と異なる場合にだけ、同じ scan surface の候補から
    `folderinfo.txt` を読み、存在すれば `#TITLE` を反映し、存在しなければ directory name に戻す。
    `folderinfo.txt` の上書き編集で親 directory mtime が変わらないケースは LR2 互換寄りの割り切りとして取り逃がしを許容する。
  - normal folder sync が失敗した場合、directory metadata 欠落で row 生成が未適用だった場合、
    または `folderinfo.txt` read failure で title source を反映できなかった場合は、
    LR2 song.db sync status を incomplete に落とし、次回の manual / startup sync で current scan surface から再評価する。
- Phase 7: `.lr2folder` projection / DB sync / playlist output sync / 通常 discovery / built-in CustomFolder 連動は主要経路へ接続済み。
  - まず `.lr2folder` 本文を `LR2SongDB.folder` row へ投影する pure `Lr2FolderFileProjection` を追加する。
    この層は `#TITLE` / `#SUBTITLE` / `#CATEGORY` / `#INFORMATION_A` / `#INFORMATION_B` /
    `#COMMAND` / `#TAG` / `#MAXTRACKS` / `#BANNER` / `#CUSTOMFOLDER` を parse する。
    `#TAG` と `#COMMAND` は LR2 互換の command alias として後勝ちにし、通常の `.lr2folder` row は `type = 2` を既定にする。
  - `.lr2folder` row の `path` はファイル path そのもの、normal directory row の `path` は末尾 separator 付き directory path として分離する。
    `Lr2FolderFileProjection` は existing row の `adddate` を維持し、新規 row だけ生成時刻を入れる。
    ただし LR2 executable directory 配下の built-in source (`LR2files\CustomFolder`) は、
    既存 startup normalization と同じく LR2 root 相対 path (`LR2files\...`) を DB path として保持する。
    実ファイル読み取り用の file path と DB に保存する path は `Lr2FolderFileSyncItem.FilePath` /
    `DatabasePath` として分離する。
  - `#CUSTOMFOLDER` は parse fact として保持するが、BeMusicSeeker 生成 `.lr2folder` の必須条件にはしない。
    OpenLR2 root 特殊 type は fixture で確定した source だけが explicit type を渡す。
    固定 source は `newsong.lr2folder` の `type = 3` と、
    `course1.lr2folder` / `course2.lr2folder` / `course3.lr2folder` の `type = 6` とする。
  - source 分類は `Lr2FolderFileSourceClassifier` に閉じる。通常 BMS root / 通常 custom folder 出力 base は
    `type = 2` と directory parent hashを使う。root custom folder 出力 base では、base 直下の standalone
    `.lr2folder` だけ root parent hash を使い、playlist/table directory 配下の `.lr2folder` は containing
    directory hash を使う。
    LR2 built-in CustomFolder source は LR2 root 相対 path に変換し、source directory 直下の `.lr2folder` だけ
    root parent hash、入れ子の `.lr2folder` は相対 path の containing directory hash を使う。
    `LR2files\Rival` は LR2 が server 通信と active rival ID から動的に反映するため、built-in source classifier では扱わない。
    既定の built-in discovery root は `LR2files\CustomFolder` のみで、通常 scan root / custom folder 出力 base で見つかった
    `__RIVAL__` 系 `.lr2folder` は外部 `discovered_lr2folder` として扱う。
  - built-in `LR2files\CustomFolder` は LR2 `config/system/customfolder` bitmask を正本にする。
    `RANDOM/` は `0x1`、`favorite.lr2folder` は `0x2`、`TOP10.lr2folder` は `0x4`、
    `PLAYLEVEL/` は `0x8`、`CLEAR/` は `0x10`、`RANK/` は `0x20`、
    `ignore.lr2folder` は `0x40`、`INSANE01/` / `INSANE02/` は `0x80` で対象化する。
    `course1-3.lr2folder` は bitmask に関係なく対象化する。
    `newsong.lr2folder` は `config/system/titleflash` と `song.adddate` から対象曲がある場合だけ対象化する。
    `song.adddate` 未設定の current row は、LR2 song.db sync で新規 `song` row として `adddate = now` が入る可能性があるため
    `newsong` 対象として扱う。空ライブラリや titleflash 無効時は対象にしない。
    `customfolder` / `titleflash` / `newsong` 対象有無は sync signature に含めず、LR2 setup 変更や
    `newsong` 期限切れは built-in special folder scoped sync で反映する。
    disabled になった built-in row は `LR2files\CustomFolder` DB-relative prune scope で削除対象にする。
  - normal folder sync は `type = 1` の scope だけを prune / upsert 対象にし、`type = 2` の `.lr2folder` row を
    上書き・削除しない境界を維持する。
  - `.lr2folder` DB sync は `Lr2FolderFileDbSyncService` に分離する。
    入力 item は current `.lr2folder` file projection、`ScopeDirectories` / `ScopePaths` は stale row prune の境界として扱う。
    通常 / root custom folder 出力先と built-in `LR2files\CustomFolder` については、同じ sync request の
    directory row generation scope から親 / カテゴリ directory row も生成する。prune 用の
    `DirectoryRowScopeDirectories` とは分け、playlist 単位の再出力で sibling playlist の directory row を
    削除しない。通常出力先では `LR2CustomFolderOutputBaseDir` を root 相当境界にし、通常出力先 row を
    `parent = ROOT`、playlist/table directory row をその子にする。root 出力では playlist/table directory
    それぞれを root 相当境界にし、playlist/table directory row を `parent = ROOT` にする。
    prune 用 scope は引き続き出力 directory に限定し、sibling playlist や custom folder 出力 base 全体へ広げない。
    同一親に複数 `.lr2folder` がある場合、親 row は一度だけ upsert 候補にし、generated path set で
    stale parent row pruning と重複 write を抑止する。
    親 / カテゴリ directory row の `title` と `date` は `Lr2FolderDirectoryMetadataSnapshot` を正本にし、
    `.lr2folder` sync service 側では `folderinfo.txt` を直接読まない。built-in `LR2files\CustomFolder`
    については、`.lr2folder` parent directory targets を grouped text / directory metadata surface で解決し、
    その場の sync request と後続用 prepared surface の両方へ載せる。既存の normal folder 用 `DirectoryEntries` と merge して同じ
    resolver contract へ揃える。
    startup / file diff の `.lr2folder` scoped sync では、BMS scan root で既に捕捉した `folderinfo.txt` surface を再列挙せず、
    chart/resource scan root 外の discovery root だけを scoped text metadata producer に通して request と scan capture surface に合成する。
    `AllowPrune=false` では upsert のみ行い、`AllowPrune=true` でも scope 内の `.lr2folder` file path row だけを削除対象にする。
    normal directory row (`type = 1`) と scope 外 `.lr2folder` row は削除しない。
  - `.lr2folder` file path は CP932 非対応なら row 生成しない。mtime 欠落 row は missing metadata として skip し、
    `date = NULL` の `.lr2folder` row は生成しない。
    prune 判断は input path ではなく「実際に生成できた row path」を current set にするため、非対応 / metadata 欠落 row は
    scope 内 stale row として削除対象にできる。
  - `.lr2folder` row の exact path casing が変わった場合は、case-insensitive key で既存 `adddate` を引き継ぎつつ、
    古い exact path row を削除して新しい exact path row を upsert する。
  - `.lr2folder` sync は reserved normal directory type (`type = 1`) を生成しない。既存 `type = 1` row と同じ path が
    input に来た場合も、その row を `.lr2folder` row で置き換えない。
  - 外部 `.lr2folder` 互換用に `#GENRE` は `#CATEGORY`、`#PLAYLEVEL` は `#MAXTRACKS` の alias として扱う。
  - BeMusicSeeker の playlist custom folder output は、`.lr2folder` file を書いた直後に同じ本文から
    `Lr2FolderFileDefinition` を作り、同じ output directory を `ScopeDirectories` として `folder` row を sync する。
    これにより、従来 `.lr2folder` 再出力だけだった操作でも `folder.command` / `folder.date` / `folder.max` が
    同じ workflow で更新される。file 出力に成功した path だけを sync item にし、file mtime を `folder.date` の正本にする。
    parent directory row の date は、出力 producer が materialization 後に作る owned directory entry surface を
    正本にし、Everything index の反映待ちを receiver 側 `DirectoryInfo` fallback で埋めない。owned surface で
    必要 target が揃う場合は grouped directory query を追加実行しない。
- Phase 8 は `lr2_song_db_sync_status` table の単一 row (`name = "default"`) から開始する。
  `status` / `signature` / `run_id` / `processed_cursor` / `total_count` / `stage` / `last_error` を durable に保持し、
  `Completed` かつ contract signature 一致のときだけ full sync 不要と判定する。`Failed` / `Cancelled` / `Incomplete` /
  contract signature mismatch / missing row は `Needed` として再開可能にする。
  初期接続では `startup_initialization_complete` 後の best-effort warmup から status を評価し、必要なら
  `lr2_song_db_sync` startup background task を queue する。この task は通常 startup readiness を待たせず、
  実装済み stage を進めたうえで残 stage がある場合は `Incomplete` として再開可能にする。
  runner は normal folder stage を実行し、current owned BMS path snapshot と LR2 BMS root から
  root / ancestor / chart directory `folder` row を sync する。`folderinfo.txt` は file enumeration の
  metadata surface から渡された候補を使う。続く `song_rows` stage は current owned BMS path / hash /
  mtime / owner identity だけを軽量 target として持ち、current owned `BMSFile` の persistence 用 copy を
  全件 materialize しない。少数の bounded reader が `ChartFileSnapshot` を bounded queue に流し、parallel workers が
  snapshot から BMS row、LR2 generated columns、LR2 compatibility facts を一度で作る。LR2 compatibility facts は
  BOM 自動判定なしの CP932 decode 由来 parsed BMS row の raw resource references から直接評価し、BMS row を `ChartFile` / `ChartResourceSnapshot`
  へ再投影しない。parse/read 失敗時は既存 generated row を破壊しないための
  non-destructive skip / preserve に留め、`chart_info` numeric、mtime、`folder.date`、prune 成功の
  代替正本にはしない。skip / preserve 件数を log / status に残す。
  current `chart_info` parser version の `chart_info` row は run 開始時に一括 resolver として準備し、
  lightweight parser では足りない BPM / `longnote` / `random` / `karinotes` / `exlevel` などを
  memory lookup で反映する。overlapping column は列ごとに正本を分ける。
  `mode` / `judge` は LR2 実行時挙動に関わるため lightweight parser を正本にし、
  `level` / `difficulty` は譜面メタデータとして current `chart_info` / detailed parser を正本にする。
  `song_rows` chunk 内で `chart_info` table へ SELECT しない。
  必要な `chart_info` が missing / stale の場合も、LR2 song.db sync 開始前に別の synchronous full sync を走らせない。
  `song_rows` worker が同じ `ChartFileSnapshot` から current `chart_info` parser version の `chart_info` row / parse failure
  を作り、song row / LR2 compatibility facts と同じ chunk transaction で保存する。commit 成功後だけ
  durable cursor を進めるため、rollback / retry 時は chart_info と song row が同じ chunk boundary で再試行される。
  current `chart_info_parse_failure` の MD5 は run 開始時に memory set として渡し、既知失敗は再 parse しない。
  chart_info row / parse failure の presentation 更新は chunk ごとに dispatch せず、run 後に一回だけ coalesce する。
  空 DB 初期構築では file diff apply 側の snapshot / parser result から同じ LR2 projection を作り、
  sync が同じ chart をもう一度全件読む形にはしない。
  current owned chart directory 直下の `.txt` presence も `TextFileDirectories` scan surface として渡し、
  `song.txt` を同じ sync write で再生成する。
  LR2 compatibility facts は `maintenance` の LR2 列だけを bulk update し、既存 resource health /
  encoding columns は置換しない。`maintenance` row が無い場合だけ path/hash と LR2 列の最小 row を作る。
  同じ sync run で計算した LR2 compatibility facts は live BMS row の materialized `maintenanceInfo` に
  LR2 列だけ overlay し、`Lr2CompatibilityWarningProjection` を通して warning 表示を更新する。
  resource health / encoding state は live row 側でも上書きしないため、この refresh のためだけに full
  `maintenance_hydration` や resource health index rebuild は走らせない。
  persistence 用 copy では live row の `folder` / `parent` が形式上 valid でも path と一致する保証がないため、
  これらを空にして `Lr2SongRowEnricher` に再生成させる。
  さらに LR2 BMS root、通常 custom folder 出力 base、root custom folder 出力 base 配下の外部 `.lr2folder` は
  `RootFileEnumerationService` の Everything/fallback path で discovery し、`Lr2FolderFileDbSyncService` へ渡して
  source classification 済みの `folder.type = 2` row を sync する。app-managed playlist 出力は materialization が
  `Lr2SongDbSyncPreparedDataSurface` として producer-owned 出力 directory scope / file entry と
  parent directory metadata を返す。built-in scoped sync は同じ prepared surface に directory mtime に加えて
  `folderinfo.txt` / `.txt` directory metadata も載せる。
  LR2 song.db sync input は既存 scan surface からその scope 配下の古い候補を取り除いて生成済み file entry と
  producer-owned metadata を合成する。
  output base 直下など producer-owned scope 外の `.lr2folder` は外部 discovery の対象として残す。stale prune scope は
  LR2 BMS root に限定し、custom folder 出力 base は discovery-only とする。playlist 出力先の exact prune は
  既存の playlist output workflow が担当する。
  LR2 built-in custom folder source (`LR2files\CustomFolder`) も discovery root に含める。
  `LR2files\CustomFolder` は LR2 setup の `<customfolder>` bitmask に従い、`course1-3` は常時対象、
  `newsong` は `titleflash` と `song.adddate` に基づく dynamic row として扱う。
  `LR2files\Rival` は built-in discovery root に含めず、LR2 側の ranking server 連動に任せる。
  通常出力先 / BMS root 由来の `.lr2folder` は絶対 path と directory parent hash のまま扱い、
  そこに `__RIVAL__` `.lr2folder` があれば外部 `discovered_lr2folder` として処理する。
  `.lr2folder` prune は discovery surface が complete で、かつ発見した各 file を読み取れた場合だけ許可する。
  enumeration failure や一時的な file read failure がある場合、その回は upsert のみにして既存 row を消さない。
  file read / parse に失敗した `.lr2folder` row も non-destructive preserve に留める。
  既存 row 由来の folder/parent 再生成を、mtime や command / title の代替正本として使わない。
  current owned chart directory 直下の `.txt` presence は file diff の text group scan surface と同じ意味で
  sync 入力に渡し、`song.txt` へ反映する。通常 file diff でも BMS 本体 mtime が変わらず `.txt`
  presence だけ変わる場合は targeted `song.date` / `song.txt` update へ落とすため、text group freshness は
  current scan contract に含まれる。
  file diff を通らない direct install / path replacement では、DB helper ではなく mutation application 層で
  `Lr2TextGroupResolver` を呼び、導入後 / 移動後 directory の direct `.txt` presence を `BMSFile.txt` に反映してから
  `Lr2SongDbWriter` / storage row update へ渡す。
  path replacement では `song.folder` / `song.parent` を一度空にして `Lr2SongRowEnricher` に再生成させる。
  これにより通常 mutation でも sync / file diff と同じ CP932 非対応判定と LR2 CRC contract を使う。
  direct install / path replacement / remove 後は、同じ owned mutation 成功 path で normal `folder.type = 1` row も
  `Lr2NormalFolderDbSyncService` に渡して sync する。追加だけの mutation は upsert-only とし、削除または移動を含む
  mutation は old / new chart directory を dirty prune scope として渡す。dirty scope は exact chart directory であり、
  root 子 directory へ広げない。sync input にはその scope 内に残る current BMS path も含め、scope 内で期待されなくなった
  normal folder row だけを削除する。bmson は `song` / `folder` の対象にしない。
  root set 変更や scan surface が不完全な場合の広域 prune は引き続き full scan / sync に任せる。
  この best-effort sync が失敗しても owned mutation は成功扱いにし、`lr2_song_db_sync_status` を
  `Incomplete(lr2_normal_folder_mutation_sync_failed)` にして次回 sync で修復できるようにする。
  `folder` row の path replacement / startup normalization でも parent CRC は `Lr2SongFolderParentNormalizer.ComputeDirectoryHash`
  を使い、`LR2CRC32` の直接呼び出しを通常 mutation / initialization surface に増やさない。
  完了直前の snapshot staleness check は、service に caller-provided predicate を渡す形にし、`BMSLibrary` 側で
  owned collection / storage row version と LR2 root / `.lr2folder` discovery root /
  built-in custom folder 設定 snapshot を開始時入力と再比較する。これは実行中に入力が変わった run を
  completed として publish しないための running-run guard であり、次回起動の full sync signature ではない。
  ここでは `.lr2folder` /
  `folderinfo.txt` / text group / directory metadata を再列挙しない。stale の場合は `Completed` にせず
  `Incomplete(source_stale_detected)` とする。
  sync 後の diagnostic は、current song row 欠落、`song.date` 欠落 / `0`、expected normal folder row 欠落、
  expected `.lr2folder` row 欠落などをログに残すための確認に限定する。root set が空の場合は生成対象なしとして扱う。
  LR2 song.db sync 本体は current path set から導けない既存 `song` row や expected set 外の `folder` row を
  同一 run 内の通常 prune / update として処理し、diagnostic を別の status gate にしない。
  status signature は schema / generator / parser contract のみを current 判定に使う。
  normalized / deduplicated / case-insensitive な LR2 BMS root set、`.lr2folder` discovery root set、
  LR2 setup の `<customfolder>` bitmask、`titleflash` は signature へ含めない。
  root set や built-in CustomFolder の対象条件が変わった場合は既存 `Completed` を無効化せず、
  対応する file diff / scoped folder sync で `folder` row を更新する。`LR2files\Rival` は
  BeMusicSeeker の built-in discovery root ではないため、通常 discovery で見つかる external `.lr2folder`
  だけを処理する。
  signature には app schema / chart_info schema / chart_info parser / song-folder generator /
  `.lr2folder` parser / LR2 compatibility fact の version も含める。各 component の生成意味が変わった場合は
  対応 version を bump し、既存 `Completed` を再評価対象にする。

## File Enumeration / Everything Surface Contract

Everything native bridge と managed fallback は、LR2 song.db sync status の `Completed` 判定に影響する
入力 surface として扱う。特に `.txt`、directory mtime、`folderinfo.txt`、`.lr2folder`
existence / mtime は意味的に揃える。
queued された LR2 song.db sync sync は、この surface の consumer であり、独自に
Everything / filesystem 広域再スキャンを始める入口ではない。surface が古い場合は、影響 group を
同じ grouped enumeration contract で refresh するか、current run を `source_stale` として完了させない。

現行計画の整理:

- chart / resource search roots と `.lr2folder` discovery roots は別 surface として扱う。
  chart / resource roots は、BeMusicSeeker 内で「BMS root folder」として扱う root だけを正本にする。
  BeMusicSeeker が明示的に管理する通常 custom folder 出力先と root custom folder 出力先は、
  chart / resource query root として扱わない。
  - 通常出力先は 1 つの base root として扱い、その配下の numbered `.lr2folder` / directory は
    chart / resource root list に並べない。
  - root custom folder 出力先も同じく 1 つの base root として扱う。LR2 config や既存 DB 由来で
    `D:\BMS\ROOT\...` のような子 directory が search root として列挙されていても、
    chart / resource root list には入れない。
  - これは query root の正規化であり、`D:\BMS\` という BMS root の配下に
    `D:\BMS\#BeMusicSeeker\` が自然に存在する場合まで Everything exclude DSL で subtree 除外する
    という意味ではない。重要なのは、アプリ管理の出力先を追加 root として増やさないこと。
- `.lr2folder` discovery roots は BMS search directories、通常 custom folder 出力先、root custom folder 出力先、
  LR2 built-in `LR2files\CustomFolder` を含める。通常出力先や root 出力先に外部 tool が
  `.lr2folder` を生成する運用があるため、`.lr2folder` discovery は chart/resource search より広い。
  app-managed output / external discovered source / built-in source の判定は query ではなく result classifier で行う。
- `.lr2folder` discovery は sync 専用の後追い列挙にしない。
  LR2 song.db sync completed 後の steady-state でも、file diff / startup scan surface で `.lr2folder` の
  existence / mtime 変化を検出し、`folder` row へ反映できる必要がある。
  ただし `.lr2folder` discovery roots は chart / resource roots より広いため、同一 query root set に
  無理に混ぜない。startup / file-diff cycle 内で同じ grouped enumeration API を使い、
  chart/resource surface と `.lr2folder` surface を別 root set の entry snapshot として保持する。
- native bridge ABI は拡張する前提にする。chart / resource / `.txt` / `folderinfo.txt` / `.lr2folder`
  の grouped query result は、path だけでなく file mtime を含む file entry surface を返す。
- managed fallback も同じ file entry surface を返す。通常の生成 workflow では、fallback scan 後に
  同じ path へ mtime を再問い合わせする二段構えにはしない。
- `.lr2folder` sync item も enumeration entry の mtime を正本にする。path だけが渡された `.lr2folder` は
  missing metadata として扱い、生成 row の `folder.date` は後段で `File.GetLastWriteTimeUtc` を呼んで補完しない。
- startup-scan diagnostic / cleanup helper も、渡された enumeration entry と DB row を入力にする。
  完了直前や cleanup helper 内で live filesystem mtime へ fallback しない。
- directory mtime は normal folder row と startup-scan diagnostic の正本であるため、
  native bridge / fallback の metadata surface に含める。
- surface が不完全な場合、該当 surface を使った destructive prune は抑止する。stale row prune や
  startup-scan diagnostic へ使う surface は complete flag とセットで扱い、残件はログに残す。
- Everything query に exclude DSL は追加しない。root / extension / filename の組み合わせで surface を分け、
  アプリ管理物か外部由来かは結果分類で判定する。query の戻り値は Everything / native bridge contract を
  信用し、期待外拡張子や filename を後段で再フィルタする通常処理は増やさない。
- `everything_scan` log は chart / audio / image / movie だけでなく、LR2 連携モードの metadata surface も
  観測できる粒度にする。最低限 `textQuery` / `textQueryHits` / `folderInfoHits` /
  `lr2FolderQueryHits` / `directoryQueryHits` / metadata bridge ms / fallback reason を出し、
  `.lr2folder` discovery が sync input で突然始まるように見えないようにする。
- root normalization log を追加する。
  chart / resource roots には BMS root folder だけが残り、通常 custom folder 出力先、root custom folder 出力先、
  およびそれらの子 directory が explicit root として混ざらないことを確認できるようにする。
  `.lr2folder` roots には BMS root folder 群、通常 custom folder 出力先、root custom folder 出力先、
  LR2 built-in `LR2files\CustomFolder` が入ることを別 counts で出す。
- bridge layout version / result version はログ分析用に出してよい。アプリ本体と native bridge DLL は
  同一配布物なので、旧 ABI DLL を runtime fallback する互換分岐は計画に含めない。

## 残作業の推奨順

現時点では、cursor / status / cancellation などの基盤は実装済みだが、hot path は初期化同型に
収束していない。以下は実装サイクルの推奨順であり、実機確認・手動確認・LR2 起動確認は
最後にまとめて行う。

各実装サイクルの開始前に、設計レビューでは機能面だけでなく性能面も必ず確認する。
特に以下を禁止事項として扱う。

- sync input 作成中に、startup / file diff scan surface で既に得た情報を広域再列挙する。
- chart / resource / `.lr2folder` / directory metadata の root contract を別々の ad hoc helper で
  再解釈する。
- completed steady-state の通常起動へ、初回 sync 用の full validation / full table scan /
  full hydration を混ぜる。
- 200k 件級の hot path で、chunk ごとの DB SELECT、行単位 upsert、既存 DB 保護のための
  defensive merge を増やす。
- 「こうなるかもしれない」だけで incomplete blocker を増やし、実ファイル由来の一覧 cache へ
  収束させる方針を弱める。

直近の通常起動短縮ターゲット:

- LR2 song.db sync completed / file diff 0 件に近い通常起動では、`everything_scan success` 後の
  file diff / LR2 folder 周辺処理を数秒以内に収束させ、`startup_ready_operable elapsedMs` を
  scan 完了から大きく離さない。まずはこの区間で 5 秒以上の短縮を狙う。
  ここでいう operable は既存 startup readiness tier の境界であり、playlist entries hydration や
  chart_info / maintenance hydration まで完了した全機能収束状態を意味しない。
- resource index / reverse lookup surface は導入可能 readiness の正本なので、未完成のまま lazy 化して
  `startup_ready_operable` だけを短く見せる方針は採らない。
- これは background task を単に後ろへ隠す方針ではない。`startup_install_estimation_ready` /
  `startup_ready_operable` と同時に `startup_initialization_complete` も観測し、後段 hydration の
  no-op 性能は別途扱う。
- DB 由来の現在状態は、実装単位ごとにばらばらに SELECT して準備しない。起動時 catalog load
  または file diff 準備段階で、`song` / `folder` の hot path 用 compact snapshot を作り、
  normal folder mtime diff、外部 `.lr2folder` diff、startup surface capture が共有する。
  ただし「各 table 1 回の広い SELECT」が常に最速とは限らないため、実 DB では SQL projection /
  predicate で絞る案と、アプリ内 map 共有案をログと `EXPLAIN QUERY PLAN` で比較する。
- 共有 snapshot は correctness の正本ではなく、同一 startup generation の read-through view として扱う。
  DB write が発生した場合は mutation delta で snapshot を更新するか、後続処理に stale として渡さない。

1. 通常 file diff と LR2 song.db sync の重い folder sync を切り離す。主要実装済み、hot path 残あり。
   - `ReloadFileDiff` / startup file diff の「差分あり」は owned BMS / bmson の実ファイル差分だけを正本にする。
   - LR2 `folder` table の incomplete / stale / expected row 欠落を通常 file diff の差分扱いへ混ぜない。
   - startup file diff で LR2 normal folder sync を行う場合も、DB `folder.date` と current directory mtime の
     mismatch で作った directory scope と prune scope を正本にした scoped sync にする。
   - scoped sync は既存 `folder` row も生成対象と prune scope だけを読み、少量差分で `folder` table 全件 read に戻さない。
   - LR2 song.db sync / cleanup 用の全件 normal folder sync は sync stage 専用に残す。
   - LR2 song.db sync 未完了 status の評価は軽量に保ち、未完了であること自体が通常差分確認を秒単位で遅くしない。
   - file diff は `Lr2NormalFolderSyncScopeBuilder` に directory mtime diff の `DirectoryPaths` と
     削除譜面の親 directory 由来の `PruneScopeDirectories` だけを渡す。
     BMS path diff は normal folder sync の直接トリガーにしない。
     date-only / text-only / bmson-only の DB 差分だけでは LR2 normal folder sync を起動しない。
2. scan surface / root contract を実装へ反映し、LR2 song.db sync input の再列挙をなくす。
   - 契約: `song.db` sync / 再同期の chart target は、アプリ内 mutation 完了後の current catalog を
     最新の所持一覧として扱う。アプリ外で譜面ファイルが変わらない前提では、LR2 song.db sync のためだけに
     chart / audio / image / movie resource scan を再実行しない。
   - 契約: LR2 song.db sync / 再同期が最新性を要求する対象は directory mtime / `.txt` directory /
     `folderinfo.txt` / `.lr2folder` metadata surface。現在の scan surface が使える場合はそれを正本にし、
     使えない場合だけ grouped Everything / managed fallback の metadata-only surface を producer として
     再取得する。受け取り側で target ごとの live filesystem fallback や resource scan を再実装しない。
   - 完了: 通常 file diff の `everything_scan` surface から `.txt` / `folderinfo.txt` を
     `RootFileEnumerationEntry` として保持し、`CreateLr2SongDbSyncInput()` はその surface を
     再利用する。sync input 作成中に `folderinfo.txt` を root 配下から広域再列挙しない。
   - 完了: scan surface が使えない場合も、`folderinfo.txt` と `.txt` directory は同じ grouped Everything /
      managed fallback の text metadata surface から取得し、generation target directory だけに絞る。
      target ごとの `FileInfo(target\folderinfo.txt)` lookup や chart ごとの `Directory.EnumerateFiles("*.txt")`
      には戻さない。
   - 完了: `CreateDirectoryMetadataTargets(roots, chartPaths)` は input 作成中に一度だけ作り、
     folderinfo 候補、directory entry lookup、normal folder sync で共有する。
   - 完了: startup / file diff producer は root 配下 directory group を
     `RootFileEnumerationEntry` surface として作り、LR2 song.db sync input と file diff normal folder sync は
     必要 target だけをその surface から読む。欠けた entry は input 側で live filesystem lookup して補完せず、
     missing metadata として後段へ渡す。scan surface が無い manual LR2 song.db sync input では、
     grouped Everything / managed fallback で directory surface を再取得する。
   - 完了: package install / library delta の owned mutation normal folder sync は、full root grouped scan を使わず、
     mutation target directory set に限定した producer-owned metadata surface を作る。小さい mutation で
     BMS root 全体の `.txt` / directory fallback scan を走らせない。
   - 完了: input 作成ログに `reusedScanSurface` / `scanSurfaceMissReason` / `directoryTargets` /
     `directoryEntries` / `folderInfoCandidates` / `textFileDirs` と主要 step の elapsed ms を出し、
     scan surface が使われているか、使えない場合にどの step が queued 後の空白を作ったかを確認できるようにする。
     `.lr2folder` candidates と `.txt` directory surface は `lr2FolderCandidatesSource` /
     `textFileDirsSource` で `scan_surface_direct` / `scan_surface_prepared_merge` / `enumeration` 系の
     source を明示し、`reusedScanSurface=true` の遅延が再検索か prepared merge かを判別できる。
   - 完了: sync input は使用した scan surface generation を保持し、後続 file diff scan で `.txt` /
     `folderinfo.txt` surface が更新された場合は完了直前の source current 判定で `source_stale` にする。
   - 完了: playlist materialization は、生成した `.lr2folder` paths / entries と producer-owned
     `DirectoryEntries` を `Lr2SongDbSyncPreparedDataSurface` として返す。built-in scoped sync は、
     読み直した `.lr2folder` paths / entries と `DirectoryEntries` に加えて `FolderInfoFileEntries` /
     `TextFileDirectories` も同じ prepared surface として返す。LR2 song.db sync input はこの producer surface を
     既存 scan surface に合成し、producer-owned output directory scope を受け取り側で再 discovery しない。
     prepared surface が無い場合は scan surface の `.lr2folder` / `.txt` list を direct use し、prepared がある場合も
     scope matcher は drive root / prefix sibling を保ったまま path ごとの親 directory climb を避ける。
   - 完了: chart/resource search roots を「BMS root folder として扱う root」だけに正規化する。
     通常 custom folder 出力先、root custom folder 出力先、およびそれらの配下の explicit root は
     chart / resource scan root から除外する。ログで `D:\BMS\ROOT\...` のような root 出力先子 directory が
     query に大量に並ぶ状態は設計不一致として扱う。`GetBmsDirectories_ExcludesCustomFolderOutputSearchRootsAndExplicitChildren`
     で、親 BMS root 配下に自然に含まれる output-like subtree は除外せず、explicit output root / child root だけを
     除外する contract を固定済み。
   - 完了: `.lr2folder` discovery roots は BMS roots + 通常出力先 + root 出力先 + `LR2files\CustomFolder`
     にする。chart / resource roots とは root contract が異なるため、`ChartScanResult` の default groups へ
     混ぜず、startup / file diff producer が同じ grouped enumeration API で別 surface として作り、
     `SongTableFileCheckResult.Lr2ScanLr2Folder*` に保持する。
   - 完了: grouped scan fallback は requested extension union だけを列挙し、`.lr2folder` / `folderinfo.txt`
     discovery の fallback が root 配下全ファイルの metadata surface を作らないようにする。
   - 完了: `bms_search_root_normalization` log で root 正規化と `.lr2folder` discovery root の counts を
     起動 scan / reload の入口で確認できるようにする。
   - 完了: native fixed scan + grouped directory query と managed fallback の directory metadata surface は
     root entry を含む同じ `RootFileEnumerationEntry` contract に揃える。ログには directory query hit /
      elapsed と LR2 song.db sync input の `directoryEntries` / `missingDirectoryEntries` を出す。
    - 完了: no-surface LR2 song.db sync input producer は grouped Everything / managed fallback の directory
     surface を再取得し、target ごとの `DirectoryInfo` lookup へ落とさない。built-in `LR2files\CustomFolder` の
     `folderinfo.txt` も同じ grouped text metadata surface から target に絞る。
    - 完了: `.lr2folder` parent/category directory metadata は、LR2 song.db sync input / scoped sync producer が
     request の `DirectoryEntries` に載せる。file diff / startup producer が持っている directory surface を
     `.lr2folder` scoped sync の親 folder 行生成でも再利用し、sync service 側では mtime 欠落 target を
     grouped scan / live filesystem lookup で補完しない。
   - 完了: `.lr2folder` discovery は startup / file diff scan surface の producer contract へ寄せる。
     `BMSLibrary` 側の後付け attach は廃止し、scan completion 前に producer-owned surface を作る。
     directory mtime は scan surface 再利用と no-surface 時の grouped再取得まで接続済みであり、
     native bridge / managed fallback の実機 parity 確認を残す。
   - 完了: startup / file diff 後の `.lr2folder` scoped sync は、playlist header と `playlist_entry`
     集計から導ける numbered `.lr2folder` exact path だけを current 候補から除外し、DB 上の
     `folder.path` 表現も exact path で prune 保護する。同じ出力 directory 配下でも正本に存在しない
     `.lr2folder` は外部 discovery 候補として残す。外部 `.lr2folder` は candidate exact path と
     discovery prune scope だけを読み、`initialize` でも discovery complete /
     read failure なしなら stale row を prune する。ログは `lr2folder_file_diff_filter` の `candidates` /
     `externalCandidates` / `appManagedFiltered` / `appManagedExactFiles` と `lr2folder_file_diff_sync` の
     `pruneExcludedPaths` で候補削減と保護範囲を確認できる。
     起動時外部 `.lr2folder` diff は、汎用 sync service の既定意味論は残したまま軽量 option を使い、
     prune prefix read を `.lr2folder` row に限定し、mtime 一致で preserved の child row は parent
     directory row exact read / metadata build / upsert を行わない。app 管理 playlist output と built-in
     special folder は、それぞれ playlist materialization / built-in scoped sync の責務に残す。
     2026-06-08 の実機 Release 起動では、`lr2folder_file_diff_sync` が `externalCandidates=1265`、
     `existingRows=12094`、`preserved=1265`、`elapsedMs=871` まで下がった。旧ログでは
     app-managed 除外後でも `existingRows=45854`、`elapsedMs=2176` 程度だったため、normal folder row を
     prune scan で広く読む問題は解消済み。
   - 完了: normal folder mtime diff は `folder` row 全列 materialize から `path` / `type` / `date`
     projection へ縮小し、さらに startup phase1 の catalog load 完了後に `type = 1` の prefix snapshot を
     Everything scan 裏で前倒し取得する。`ApplyFileScanDiff` は snapshot task を file diff 開始時に待たず、
     normal folder mtime 判定の直前でだけ resolver を呼ぶため、resource index build、current row indexing、
     BMS / bmson target selection、parse / no-op commit 判定と DB snapshot read が重なる。
     ログは `lr2_normal_folder_mtime_snapshot_prefetch`、`lr2_normal_folder_mtime_snapshot_prefetch_wait`、
     `lr2_normal_folder_mtime_diff prefetched=true` で確認する。
     2026-06-08 の実機 Release 起動では、snapshot prefetch は `existingRows=33250` / `elapsedMs=414`、
     consume wait は `waitMs=0`、mtime diff は `directories=33205` / `prefetched=true` /
     `candidateBuildMs=82` / `existingMapMs=45` / `compareMs=23` / `elapsedMs=167` まで短縮した。
     旧実装では同条件で `elapsedMs=1455` 前後だったため、scan surface 由来の normalized directory set を
     信用し、mtime diff 内の `Path.GetFullPath` 反復と重い root 判定を避ける効果が大きい。
     続けて normal folder directory metadata target 作成も normalized scan surface 用 overload へ切り替え、
     ancestor walk 内の `Path.GetFullPath` 反復を避けた。2026-06-08 の実機 Release 起動では
     `everything_scan success` から `startup_install_estimation_ready` までが約 4.5 秒、
     `startup_ready_operable elapsedMs=26451`、`startup_initialization_complete elapsedMs=50123` になった。
     残りの主候補は `diff_bms_target_ms` など file diff map/target loop と、外部 `.lr2folder` filter 開始前の
     app-managed scope 準備 / `.lr2folder` sync 直列区間である。
   - 次候補: file diff の current BMS / bmson row list、deleted row md5 queue、bmson `path` / `updated_at`
     lookup は既に catalog load の結果を使っているが、処理単位ごとに map を再構築している。
     scan 結果待ち不要な compact current-row snapshot を startup generation で共有し、Everything scan 完了後の
     `diff_current_index_ms` / `diff_bms_target_ms` / `diff_bmson_target_ms` をさらに短くできるか実ログで確認する。
   - 完了: LR2 song.db sync completed 後の通常 file diff でも `.txt` surface が有効な場合だけ
      `song.txt` flag を比較し、surface が無いスタンドアロンモードでは既存 `txt` を保持する。
      `Completed` でない状態の既存 BMS path も、text-only refresh は file diff へ広げず、
       LR2 song.db sync の `song_rows` stage へ任せる。
       manual LR2 song.db sync input で scan surface が無い場合は grouped text metadata surface から
      `TextFileDirectories` を作り、譜面数比例の per-directory `.txt` lookup を行わない。
   - 完了: `folderinfo.txt` は directory mtime 差分に従って scoped normal folder sync で読み、単独 file mtime diff では
      normal folder sync を起動しない。`.lr2folder` は scan surface の current file entry と既存 row の `date` を
      scoped sync 側で比較する。
3. `chart_info` run-scoped resolver と inline chart_info 生成を導入する。完了。
   - LR2 song.db sync が必要な run の開始時に current `chart_info` index を一括で準備する。
   - `song_rows` chunk ごとの `chart_info` DB SELECT を撤廃する。
   - missing / stale `chart_info` は、LR2 song.db sync 開始前の同期 full sync ではなく、`song_rows` worker が
     同じ `ChartFileSnapshot` から作り、song row と同じ chunk transaction で保存する。
   - current `chart_info_parse_failure` は run-scoped MD5 set として渡し、既知失敗を timeout まで再 parse しない。
   - 空 DB 初期構築では file diff の inline `chart_info` result をそのまま LR2 projection へ渡す。
4. `level` / `difficulty` の precedence を chart_info-local に整理する。
   - `mode` / `judge` は lightweight parser の LR2 / OpenLR2 寄せを維持する。
   - `level` / `difficulty` は current `chart_info` / detailed parser の値を優先し、
      LR2 song.db sync / file diff / アプリ内導入で同じ row-local precedence にする。
   - `difficulty` は `chart_info.difficulty` があれば `difficulty_defined=false` の推定値も採用する。
     `chart_info` が欠損・stale・parse failure の場合だけ lightweight parser の valid value を使い、
     それも無ければ fixed default `2` を入れる。
   - `Lr2SongDbWriter.NormalizeUndefinedSongDifficulties(songDb)` と
     `song_difficulty_normalized` finalization log は撤去済み。
     DB-wide `folder, mode, karinotes` 順補完を file diff / mutation で再現しようとしない。
   - `ChartInfoParser` の `difficulty` inference が BMS 文化圏の分類として不十分な場合は、
     lightweight parser へ別アルゴリズムを足さず、`chart_info` parser の inference contract を拡張する。
   - 既存テストは「stale DB row を fallback anchor にしない」「chart_info が無い場合だけ default `2`」
     を確認する形へ置き換える。
5. sync input の全件 `BMSFile` copy を廃止する。完了。
   - input は lightweight target list、scan metadata surface、version snapshot だけを保持する。
   - `songRows=...` は persistence copy count ではなく target count として扱う。
   - memory usage が current catalog + bounded queue + current chunk の範囲に収まることを log で確認する。
6. `song_rows` pipeline を writer が詰まらない構造へ寄せる。主要実装済み。
   - 完了: reader は bounded producer として譜面 bytes / mtime の供給を主責務にし、
      LR2 song.db sync では worker 飢餓を避けるため最大 2 本まで使う。read queue capacity を超えて全件 bytes を保持しない。
   - 完了: workers が parse / chart_info apply / LR2 compatibility facts を同じ parse result から作り、
     `BMSFile` と `BMSFileMaintenanceInfo` を含む「DB に書ける completed item」を出力する。
   - 完了: LR2 compatibility facts は BOM 自動判定なしの CP932 decode 由来 parsed BMS row の raw resource references から直接評価し、
     `ChartFileProjection` / `ChartResourceSnapshot` を hot path から外す。
   - 完了: writer は順序制御、bulk song write、bulk compatibility facts write、durable cursor update に専念する。
   - 完了: LR2 song.db sync 用 bulk song writer は既存 row の generated columns が unchanged の場合、
     `song` row を UPDATE しない。
   - 完了: LR2 compatibility facts は chunk commit 後に live warning projection へ渡し、全件分の
     `BMSFileMaintenanceInfo` を sync result に保持しない。
   - 最優先残作業: `song_rows` の DB commit が reader / parser より支配的になっている場合は、
     読み込み側の微調整ではなく writer contract を先に直す。
     1000 件 chunk ごとに `song` temp table upsert、`chart_digest_map`、LR2 compatibility facts、
     status cursor update を同一 hot transaction で繰り返す形は最終形ではない。
   - 現ログでは resource scan は従来から重い支配項目であり、今回の悪化原因としては切り分ける。
     `song_rows` では chunk wall time のうち `commitMs`、特に `songStageMs` がまだ無視できないため、
     file reader / parser pipeline の微調整より先に DB writer の SQL 形状、index、changed-only 判定、
     transaction 粒度を確認する。
   - writer 改善 cycle では `commitMs` とは別に `songStageMs` / `chartInfoStageMs` /
     `compatibilityStageMs` / `sqliteCommitMs` / post-commit `statusCursorMs` を出す。性能レビューでは
     「どこが重いか」だけでなく、その処理自体が 200k 件級 hot path に入るべきかを確認する。
   - 完了: `songStageMs` はさらに `songChanged` / `songUpdated` / `songInserted` と
     `songEnrichMs` / `songTempMs` / `songPreviousHashMs` / `songUpdateMs` / `songInsertMs` /
     `songDigestUpsertMs` / `songDigestCleanupMs` / `songTempCleanupMs` に分解して出す。
   - 完了: LR2 song.db sync song writer は `UpsertSongRows` の schema ensure を contract とし、
     chunk ごとの `chart_digest_map` table creation / `song` / `bmson_song` existence probe を行わない。
     単発 mutation API では schema probe を維持する。
   - 完了: `chart_digest_map` は missing row を補修しつつ、同一 `md5` / `sha256` row には
     `INSERT OR REPLACE` を行わない。sha256 が異なる row だけ update する。
   - `maintenance` の LR2 compatibility facts は、chunk temp table から全 `maintenance` row へ
     correlated subquery を繰り返す形にしない。`path` indexed lookup と changed-only update /
     missing-row insert に寄せ、同一値 row は update しない。完了。
   - full sync の `song` generated column 更新も、実ファイル由来の一覧 cache へ収束させることを
     主目的にする。LR2 / user-owned column の維持は明示した列だけを snapshot して戻し、
     generated columns については defensive merge を増やさない。完了。
   - 完了: `maintenance.path` / `song.path` の NOCASE lookup は schema ensure の index を contract にし、
     LR2 song.db sync hot path は chunk temp table 起点の indexed lookup へ寄せる。writer 内で
     producer が持つべき値を再生成する fallback は増やさない。
   - 残作業: full sync では実ファイル由来の一覧へ収束させることを優先し、既存 `song.db` を守るための
     defensive merge を hot path に増やさない。保存する user columns / `adddate` / `favorite` / `tag`
     だけを明示的に snapshot し、generated columns は staging table から set-based に反映する。
   - 完了: chunk log は `readMs` / `parseMs` / `chartInfoApplyMs` / `compatibilityBuildMs` /
     `commitMs` と writer sub-step time (`songStageMs`, `chartInfoStageMs`, `compatibilityStageMs`,
     `sqliteCommitMs`, `statusCursorMs`) を分けて出す。
   - 残作業: stage-level の reader wait、worker wait、queue high watermark は `pipeline_done` に出ているため、
     必要なら chunk-level の worker aggregate time を追加し、DB writer が支配的か、
     producer/consumer の詰まりが残っているかを同じ run で判断できるようにする。
   - 残作業: encoding detection / BMS metadata parse 内部の二重走査をなくせるかは、
     writer commit を潰した後に判断する。現ログで commit が支配的な場合、parser 側の最適化は
     次順位とする。
   - `ChartFileContentReader` は read-only snapshot と hash付き snapshot の責務を分け、hash 計算を
     worker 側へ逃がせる形にする。
7. full sync 用 bulk DB writer を導入する。
   - chunk 単位で existing user columns / adddate / generated identity をまとめて読む。完了。
   - generated column の insert は multi-value insert、既存 row update は temp table update へ寄せる。完了。
   - `chart_digest_map` update / orphan cleanup は chunk 単位へ寄せる。完了。
   - LR2 compatibility facts の `maintenance` update / insert は chunk temp table へ寄せる。完了。
   - LR2 song.db sync 用 bulk song writer は `song_idx_path_nocase` を使い、chunk ごとの `song` 全表 scan を避ける。完了。
   - final song prune の current path temp table insert は multi-value chunk へ寄せる。完了。
   - 行単位 `UpsertChartDigest` / `DeleteChartDigestIfOrphaned` は full sync hot path から外し、
     単発 mutation API 専用に残す。完了。
   - 残作業: 現ログで 1000 件あたり `songStageMs` が `commitMs` の大きな部分を占めているため、
     「完了」扱いの bulk writer も再レビューする。設計レビューでは SQL の set-based 化だけでなく、
     temp table clear / index maintenance / digest map update / transaction granularity / status update 頻度が
     200k 件で妥当かを必ず確認する。
   - 次の確認 cycle の具体順:
     1. 実機ログで `songStageMs` / `compatibilityStageMs` / `chartInfoStageMs` / `sqliteCommitMs` /
        `statusCursorMs` を再確認し、支配的な stage が移ったかを見る。
     2. まだ `songStageMs` が支配的なら、分解済みログで generated row staging / existing row update /
        missing insert / digest map upsert / digest orphan cleanup のどこが支配的かを確認する。
        ログだけで不十分な場合に `EXPLAIN QUERY PLAN` と実 DB copy を使う。
     3. まだ `compatibilityStageMs` が支配的なら、chunk match table population と changed-only predicate の
        実 DB plan を確認する。
     4. `sqliteCommitMs` / `statusCursorMs` が支配的なら、transaction 粒度と durable cursor 更新頻度を
        別 cycle で見直す。
8. final diagnostics / blocker 判定を prune-first に整理する。主要実装済み。
   - `song` / `folder` / `maintenance` は実ファイル由来の一覧 cache として current surface へ収束させる。
   - expected set 外 row / unknown root row は守らず prune する。
   - compatibility warning は blocker にせず warning projection として扱う。
9. completed steady-state の no-op 性能を確認する。
   - `.bmt` 出力 OFF、LR2 song.db sync completed、file diff 0 件から数件の起動で、
     LR2 song.db sync task が queue されず、`startup_background_summary` が 50 秒未満に戻ることを確認する。
   - completed status と signature current 判定に、full validation や全件 DB scan を混ぜない。
   - 完了: file diff が DB diff なしの場合は storage row replacement / storage row version increment を行わず、
     resource index / health presentation だけを必要範囲で更新する。
   - 残作業: chart_info backfill / song.db LR2 song.db sync 系は初回または signature 変更時だけ走る。
     completed status で signature current の場合、通常起動の file diff は実ファイル由来の変更検出だけを行い、
     chart_info full hydration や LR2 folder full validation を「念のため」混ぜない。
   - 残作業: `.bmt` 出力 OFF、LR2 song.db sync completed、file diff 0 件から数件の条件で、
     `startup_background_summary < 50s` を acceptance とする。50 秒を超える場合は、
     sync ではなく startup background task の常時 hydration / prewarm を疑う。
10. `.lr2folder` / `folderinfo.txt` / `.txt` の steady-state diff sync を実機確認する。
   - `.lr2folder` discovery は初回 sync 用の入力ではなく、通常 file diff surface の一部として扱う。
     LR2 song.db sync completed 後も、アプリ管理外の `.lr2folder` 作成 / 更新 / 削除を startup file diff で検出し、
     対応する `folder` row を scoped sync する。
   - `.lr2folder` roots は BMS root folder 群、通常 custom folder 出力先、root custom folder 出力先、
     LR2 built-in `LR2files\CustomFolder` であり、chart/resource roots とは別 root set で同じ grouped
     enumeration API へ渡す。
    - `folderinfo.txt` と directory metadata は normal folder row と `.lr2folder` parent/category row の
      title / date source として扱い、LR2 song.db sync completed 後は DB `folder.date` と current directory mtime の
      mismatch で作った affected directory scope だけを再同期する。`folderinfo.txt` 削除時も旧 title を温存せず、
      親 directory mtime が変化したタイミングで現 surface 由来の normal directory row へ戻す。
   - `.txt` は `song.txt` 生成列の source なので、BMS 本体 mtime が変わらない text-only 変更でも
     scoped song row update の対象にする。
   - sync 完了後の steady-state では、これらの変更検出のために full sync や全件 folder validation を
     queue しない。file diff surface の差分から scoped DB writer を起動する。
    - 残確認: Everything native / managed fallback を切り替えても directory mtime surface の意味が揃い、
      clean scan が毎回 normal folder sync へ落ちないことを実機ログで確認する。
10. native bridge metadata parity を統合確認する。
   - fixed scan の `.txt` / `folderinfo.txt` entry、grouped enumeration の `.lr2folder` entry、directory mtime が同じ `RootFileEnumerationEntry` contract になることを実機 Everything 環境で確認する。
   - bridge layout / result version log を必要に応じて追加する。
11. Phase 9 の統合確認を固める。
   - copied `song.db` で、初回 sync、2 回目 no-op、partial resume、failed chunk rollback、
     cancel/restart、startup-scan diagnostic の残件ログを確認する。
   - completed status と signature が current な場合に 2 回目 sync queue が発生しないことは unit/integration-shaped test で固定済み。
   - copied `song.db` でも completed status と signature が current な場合に sync queue が発生しないことは unit/integration-shaped test で固定済み。
   - song row chunk failure では chunk transaction が rollback され、durable `Failed` cursor から再実行できることは unit/integration-shaped test で固定済み。
   - 実機ログで `processed_cursor` / `stage` / `Completed` / `Incomplete` の遷移が想定どおりか確認する。
12. Phase 0 の残 fixture を整理する。
   - 完了: `D:\LR2beta3\LR2files\CustomFolder` の実物 file tree を
     `BeMusicSeeker.Tests\TestData\lr2_builtin_custom_folder_real` に fixture 化し、mtime manifest から file /
     directory timestamp を再適用する regression test を追加した。これは built-in `.lr2folder` / `folderinfo.txt`
     の物理入力 contract を固定するもので、app 生成 `song.db` を期待 DB として使う自己検証ではない。
   - 外部 `.lr2folder` / 通常 BMS directory `folderinfo.txt` は LR2 ユーザーや外部 tool が任意に作れる入力であり、
     実物 file だけでは「正しい LR2 fixture」と断定しない。現行 synthetic fixture と既存 unit /
     integration-shaped test が production 実装の前提を固定している。
   - app 生成ではない LR2 / OpenLR2 生成 `song.db` が入手できるまでは、期待 `folder` row DB/CSV との比較は行わない。
     現状の app 生成 `song.db` を golden として使うと自己検証になるため、実 DB row 比較は保留する。
   - app 以外が生成した LR2 / OpenLR2 `song.db` を入手できた場合だけ、物理 file tree、mtime manifest、
     期待 `folder` row DB/CSV をセットにした任意補強 fixture を追加する。比較対象列は少なくとも
     `folder.path` / `type` / `title` / `subtitle` / `category` / `info_a` / `info_b` / `command` /
     `banner` / `parent` / `date` / `max` / `adddate` とする。
   - 外部 `.lr2folder` / `folderinfo.txt` は、LR2 / OpenLR2 との差分やユーザー報告で具体的な regression が
     見つかった時点で fixture 化する。正本性のない実物ファイルを収集するだけの作業は残タスクにしない。
   - 完了: built-in `LR2files\CustomFolder\<category>\` は、実物 file tree の nested `.lr2folder` と
     `folderinfo.txt` から category directory row を生成し、`folder.date` は `folderinfo.txt` file mtime ではなく
     category directory mtime 由来になることを固定した。
   - `LR2files\Rival\*.lr2folder` の type 4 row は、built-in discovery 対象外として「生成しない」確認用に切り出す。
     Rival 以外の type 4 root special が実 DB にある場合は、別ケースとして `path` / `type` / `title` / `parent` /
     `command` / `max` を固定する。
   - 同じ fixture に対応する BMS の `song.path` / `date` / `folder` / `parent` / `mode` / `judge` / `level` /
     `difficulty` / `txt` / `exlevel` / `favorite` / `adddate` / `tag` を 3-5 件同梱し、
     LR2 起動時 scan 抑止に効く `song` / `folder` の実 DB 断面として比較する。
13. LR2 manual-only 起動での最終確認を行う。
   - LR2 song.db sync 後、未変更 root で LR2 / OpenLR2 が再帰 scan に入らないことを確認する。
   - LR2 起動そのもののブロッキングや排他はこの計画の対象外として扱う。

## 横断的な実装済みメモ

この節は、個別 Phase へ閉じにくい実装済みの判断と runtime 境界をまとめる。新しい残作業は
上の「残作業の推奨順」へ追加し、この節には完了済み contract だけを残す。

- `Lr2SongDbWriter.UpsertGeneratedSong(...)` は direct install / path replacement など単発 runtime mutation の
  persistence 境界として扱う。full sync / 空 DB 初期構築の hot path では、同じ preservation rule を持つ
  bulk writer を使う。
  writer は DB-oriented な境界であり、`date` 欠落時に実ファイル mtime へ fallback しない。
  BMS mtime は caller が `ChartFileSnapshot` / enumeration metadata から `BMSFile.date` に反映してから渡す。
  writer は新規 `song` row で `adddate` が無い場合だけ現在時刻を入れ、既存 row の `adddate` は
  update では変更しない。
- install package inline `chart_info` build は、parse / current row skip で得た `chart_info` row を BMS storage owner
  に反映してから `Lr2SongDbWriter` へ渡す。これにより direct install 直後の `song` row も、sync を待たずに
  `level` / `difficulty` / BPM / `longnote` / `random` / `karinotes` を持つ。
  `mode` / `judge` は同じ parse result の lightweight parser projection を正本にする。
- playlist custom folder output workflow は `.lr2folder` file を出力した同じ操作内で `folder` row も sync する。
  通常フォルダ出力では、出力 directory が LR2 BMS search root 配下にある場合に BMS search root からの
  normal directory row chain を生成する。
  ルートフォルダ出力 (`is_root_folder`) では playlist workflow 側でも `Lr2FolderFileSourceClassifier` を通し、
  playlist/table directory row を `ROOT` 親に置き、generated `.lr2folder` row の parent はその containing
  directory hash に揃える。ルート出力 base 直下の standalone `.lr2folder` だけは `ROOT` 親にする。
  playlist entry の行単位編集保存も、LR2 連携モードで出力先が設定されている場合は owning table の `.lr2folder` projection を再出力し、
  `folder` row を同じ scope で pruning する。`is_root_folder` の一括変更は旧出力先 directory を変更前に捕捉し、
  commit と同じ operation 内で旧 directory row を prune してから新出力先を生成する。
- playlist entry level の LR2 `song.level` writeback は、complete row writer ではなく targeted `UPDATE song SET level`
  を使う。既存 `song` row が無い path には不完全 row を作らず、既存 row の user / generated columns も触らない。
- LR2 song.db sync の進捗は、durable status table と log に加えて `BMSLibrary` の bindable property
  (`Running` / requested-completed version / total / processed / stage) にも投影する。service 側は progress callback を
  観測専用として扱い、callback 失敗で durable sync を失敗させない。
- startup progress では LR2 song.db sync を startup / full reinitialize の background phase として扱う。
  request が来た場合だけ `[processed/total] LR2 song.db 同期 <stage>` を表示し、request 前に skip された場合は
  post-startup warmup 由来の遅い request で進捗を巻き戻さない。
- in-process で `Lr2SongDbSyncRunning` の間に同じ workflow が再要求された場合は、durable status が
  `Needed` のままでも追加 queue せず、現在の bindable progress を返す。アプリ再起動後の persisted `Running` は
  incomplete run として再評価し、通常の sync request へ戻す。
- sync runner の `Incomplete` / `Failed` は startup progress の完了版数を進めない。
  `Lr2SongDbSyncFailedVersion` と failure message で ViewModel へ通知し、進捗バーは失敗状態として残す。
  `CompletedVersion` は durable status が `Completed` になった run だけで進める。
- LR2 song.db sync status は durable table の評価結果と runtime progress を `BMSLibrary` の internal snapshot +
  public version に投影する。`MainWindowViewModel` はこれを status bar 用の runtime status に変換し、
  `Needed` / `Failed` / `Incomplete` / `Cancelled` を startup progress 外でも persistent warning として表示する。
  `Running` は startup progress が表示中なら既存 startup progress を正本にし、startup progress 外では設定保存後の
  background sync などを見失わないよう status bar に表示する。`Completed` / `NotNeeded` は status bar では非表示にする。
- 設定画面の明示導線は `LR2 song.db データを再同期` とする。
  この操作は current owned BMS と app-managed playlist custom folder projection から LR2 `song` / `folder`
  派生 cache を再同期するためのもので、`maintenance` 全譜面再スキャンや `chart_info` sync とは別の機能として表示する。
  アプリ管理 playlist 出力は `.lr2folder` 実ファイルの有無だけを見ず、`playlist` / `playlist_entry` 正本から
  `.lr2folder` file と `folder` row を同じ projection で再 materialize する。
  手動再同期は playlist materialization stage を先に進め、その stage では全対象 playlist の期待 projection を
  batch 化し、物理 `.lr2folder` は本文を読み返さず、欠損または DB 投影/mtime 不一致の対象だけを書き換え、
  LR2 `folder` row は単発 batch sync にまとめる。
  table ごとに既存出力を削除して LR2 `folder` table を全読みする処理を繰り返さない。開始、table 単位 projection、
  batch materialization / sync 完了を performance log と status bar に出す。設定画面全体を同期的に無効化して
  queue 前の長時間処理を隠さない。
  materialization 後は `.lr2folder` group だけを materialization result / grouped scan で更新し、
  `.txt` / `folderinfo.txt` / directory metadata surface の再利用を壊さない。
  起動時の playlist entries hydration 後に検出した物理 `.lr2folder` 欠損も、同じ batch materialization /
  単発 LR2 `folder` row sync で修復する。`playlist_update` の per-table callback から DB sync を繰り返す経路は使わない。
  既存 DB が壊れている場合も、通常 startup diff を重くするのではなく、この明示再同期または LR2 song.db sync で正常化する。
- `EnableLR2SongDbSync` は設定ダイアログから削除する。設定保存後は、LR2 連携 mode / LR2 root /
  通常 custom folder 出力先 / ルート custom folder 出力先のいずれかが変更された場合に scoped folder sync を実行する。
  具体的には、アプリ管理 playlist は `playlist` / `playlist_entry` 正本から `.lr2folder` と `folder` row を再
  materialize し、LR2 built-in special folder は `LR2files\CustomFolder` に限定した `.lr2folder` diff sync を行う。
  ただし LR2 root / custom folder 出力先 / built-in special folder 設定の変更は full sync signature を
  変えない。保存後の処理は status 再評価と scoped folder sync の入口であり、`song_rows` 全量再同期を
  自動で開始する入口ではない。
- LR2 song.db sync 実行中は、install / merge / rename / root move / extension rename / delete など
  owned collection と LR2 `song.db` を同時に変える操作を入口で警告して中止する。加えて
  `ApplyInstalledChartStorageTargets` / `ApplyLibraryMutationDelta` に low-level guard を置き、将来の追加経路や
  テスト用 reflection 経路が入口 guard を迂回しても DB / owned collection を書き換えないようにする。
  file diff reload / full reinitialize / pending install apply / install destination repair / playlist level writeback /
  encoding commit / mode commit / direct song commit も同じ sync priority window で止める。
  pending-only 操作は current owned source mutation ではないため、この guard の対象外とする。
- resumable sync は folder 系を stage 境界、`song_rows` を chunk 境界で再開する。durable status の
  `processed_cursor` が `normal_folders` / `.lr2folder` の完了境界に達している場合、その完了済み stage は
  再実行しない。`song_rows` は chunk commit 成功後だけ cursor を進め、次回 run では cursor 以前の
  song target をスキップする。失敗時に outer catch が durable failed status を上書きしても、同一 run の
  既存 cursor / total は維持する。
- sync cancellation は request token を service へ渡し、stage 境界と `song_rows` chunk 境界で
  `Cancelled` status と current cursor を durable に記録する。runtime request state も `Cancelled` として終端し、
  次回 evaluate では通常の resumable sync request に戻す。`Running` status は status bar にキャンセル操作を表示し、
  UI は model の cancellation token request を発火するだけで、durable status の確定は sync runner の境界処理に任せる。
- startup-scan diagnostic は、resume や実装不整合を確認するためのログ / 同一 run 内の修復候補として扱う。
  保存済み `Completed` を覆したり、次回起動時の sync 要否を決める追加検査にはしない。
- `folder.date` の mismatch は directory / `.lr2folder` file の列挙 metadata から mtime を解決できる場合に
  同じ run 内で UPDATE する。完了直前に `File.Exists` / `Directory.Exists` / live mtime 取得へ fallback しない。
  expected set 外 row や unknown root row は cleanup する。
- LR2 song.db sync 本体は current input から導けない `song` / `folder` row を同一 run 内で prune / update し、
  修復できない残件は diagnostic log として残す。別途「古い row 検査」を保存済み status 判定へ横展開しない。
- `LR2互換性警告` tree は LR2 連携モード専用の compatibility surface として扱い、standalone mode では表示しない。
- library root scan の `.txt` surface は LR2 連携モードのときだけ列挙する。
  pending package / install estimation の局所 scan は package 表示・導入時 projection のため既存どおり text group を扱う。
- `.lr2folder` projection は、通常 custom folder の `type = 2` と OpenLR2 root special 用の
  `type = 3/4/6` だけを受け付ける。`LR2files\CustomFolder` の固定 special は
  `newsong.lr2folder` を `type = 3`、`course1-3.lr2folder` を `type = 6` として扱う。
  それ以外の source は fixture で確定するまで特殊 type を推測しない。未知 type や reserved normal directory `type = 1`
  は `folder` row にしない。
- root custom folder 出力先が `LR2files\CustomFolder` 配下と重なる場合は、
  BeMusicSeeker 管理出力を優先し、absolute path と app-managed output hierarchy として扱う。これは出力 workflow の
  scope prune を absolute output directory で完結させるためであり、外部由来の built-in CustomFolder source は
  引き続き LR2 root 相対 path (`LR2files\CustomFolder\...`) として扱う。
  built-in special folder の scoped sync は DB-relative `LR2files\CustomFolder` row だけを prune scope にし、
  物理 `LR2files\CustomFolder` 配下にある app-managed output の absolute row を巻き込まない。
- `song.exlevel` は BMS `#EXLEVEL` の raw integer を正本にし、`#DEFEXRANK` は判定幅計算にだけ使う。
  非 null の `chart_info` 値を優先し、OpenLR2 互換の `0` 補完は `chart_info` 欠損 / 未設定時だけ使う。
  同じ方針で、OpenLR2 補完値は決定的 default に限定し、詳細解析値を上書きしない。
- manual `ReloadFileDiff` / search root 変更後は file diff 適用完了後に
  `QueueLr2SongDbSync("ReloadFileDiff")` で status を再評価する。
  これにより schema / generator contract mismatch は次回起動待ちにせず検出するが、
  root set / built-in custom folder 設定の変更だけで durable completed status を invalid にしない。
- startup-scan diagnostic は実装確認ログとして legacy `folder.type = 0 / NULL` row も扱えるが、
  保存済み `Completed` の追加検査には使わない。
  `folder.type` だけで normal directory / `.lr2folder` を判定せず、normalized path が `.lr2folder`
  で終わるかを target 種別の正本にする。legacy normal directory row は、expected normal folder path に
  含まれなければ cleanup 対象、含まれる場合は normal folder resync で現行 projection へ収束させる。
- direct owned mutation 中の normal folder sync failure は、file diff normal folder sync failure と同じく
  durable LR2 song.db sync status を `Incomplete` にする。mutation は user operation の正しさを優先して進め、
  folder row の再同期は次回 sync / retry で復旧できる状態にする。
- duplicate merge の BMS source unregister は LR2 `song` row を一度削除してから destination path を再登録するため、
  unregister 前に source path の LR2 user columns (`favorite` / `adddate` / `tag`) を snapshot し、
  moved BMS owner へ再適用してから destination `song` row を upsert する。これにより merge は
  direct path replacement と同じく DB 側 user state を保持する。
- direct runtime の LR2 `song` write failure (`song` upsert / level update / mutation delete / path replacement)
  は normal folder sync failure と同じく durable LR2 song.db sync status を `Incomplete` にする。
  この段階では既存の user operation 失敗 semantics は変えず、例外は再 throw する。次回 sync / retry が
  復旧経路になるよう、失敗 stage と message を status に残す。
  owned mutation apply は state applier 全体ではなく、BMS `song` row を実際に触る unregister / path cleanup /
  path replacement の callback で marking する。bmson row や installed package state の失敗を LR2 song.db sync
  status に誤分類しないためである。
- file diff reload と maintenance workflow は下位 service 内で `Lr2SongDbWriter` を直接呼ぶため、workflow 境界で
  fatal failure を durable LR2 song.db sync status `Incomplete` にする。下位 writer をすべて status-aware にするより、
  UI/operation semantics を変えず、sync/retry の入口を失わないことを優先する。
- direct runtime の `dbGateway.UpsertSongs(...)` / `UpdateSongLevels(...)` call site は、
  `BMSLibrary.ExecuteLr2SongDbWrite(...)` を status boundary として通す。下位 `BmsLibraryDbGateway` はテストや
  internal service の raw DB gateway として残るため、production `BMSLibrary` 側の call site guard test で回帰を防ぐ。
- playlist custom folder output は `.lr2folder` file と LR2 `folder` row を同じ projection から更新するため、
  LR2 song.db sync sync 実行中は playlist 側の `.lr2folder` sync 境界でも mutation guard を通す。
  `folder` row sync が失敗した場合は、既存の playlist warning 表示 semantics は維持しつつ、durable LR2 song.db sync
  status を `Incomplete(lr2_playlist_lr2folder_sync_failed)` にする。
- `.lr2folder` DB sync は explicit `FolderType = 3/4/6` を保存できる。
  Source classifier は `newsong.lr2folder` と `course1-3.lr2folder` の固定 mapping 以外では special type を推測しない。
