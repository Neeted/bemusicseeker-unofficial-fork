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
- 完全生成が有効な LR2 `song` / `folder` は、外部編集を守る表ではなく、
  現在の実ファイル・設定・プレイリスト出力から作る一覧 cache として扱う。
  current generation input から導けない stale / unknown row は温存せず、生成 workflow 内で
  削除または上書きして収束させる。
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
  - 完全生成が有効で、LR2 `song.db` 内の完全生成 status が completed でない場合だけ backfill が発生する。
  - 既存 LR2 DB に必要列が欠けている、完全生成を初回有効化した、status signature が変わった、
    前回 run が incomplete / failed / cancelled の場合は backfill needed とする。
  - backfill が必要な場合は警告・進捗・キャンセル可能性を UI / log に出す。
  - 初回 backfill は startup ready / operable を待たせず、`startup_initialization_complete` 後の
    background workflow として開始する。
  - 完全生成設定を OFF から ON に変更した場合も、保存後に同じ background workflow を queue する。
  - BeMusicSeeker からの LR2 起動導線で backfill 完了待ちや起動 block は行わない。
    LR2 が再走査する可能性は完全生成 status の警告として表示する。
- 完全生成設定は、status / backfill progress と mutation guard が揃った段階から設定 UI に表示する。
  - 設定 ON 時に backfill が必要なら background workflow で進捗を表示し、owned collection / LR2 `song.db`
    mutation 操作は開始前に抑止する。

## 設定方針

完全な `song.db` 生成は設定化する。ただし LR2 連携ユーザーにとって望ましい既定動作なので、
LR2 連携モードで **デフォルト有効** にする。スタンドアロンモードでは、設定値が true でも
完全生成 workflow は走らない。

この計画でいう LR2 連携モードは、設定上 `OperationModeLR2DB == true` であり、LR2 の
database / executable path が有効に解決できる状態を指す。

推奨 UI:

- 設定画面の「動作モード」内、「LR2と連携する」の近くに置く。
- 表示名例:
  - `LR2用song.dbをBeMusicSeekerで完全生成する`
  - または `LR2起動時のDB自動更新をBeMusicSeekerで代替する`
- 初期値:
  - `OperationModeLR2DB == true` の場合は有効。
  - `Settings` の既定値は `true`。
  - スタンドアロン運用では無効にし、設定項目も非表示にする。

設定別の処理方針:

- 完全生成が無効:
  - 現行に近い軽量スキャンを維持する。
  - chart / audio / image / movie の列挙でよい。
  - `song.txt`、`.lr2folder`、`folderinfo.txt`、LR2 folder hierarchy のための追加列挙は行わない。
- 完全生成が有効:
  - BMS の変更検出に必要な `song.path` / `song.date` / hash 判定を行う。
  - BMS chart file、resource file、BMS chart directory 直下の text group、`.lr2folder`、
    `folderinfo.txt` を同じ metadata-bearing file enumeration surface から列挙する。
  - `.lr2folder`、`folderinfo.txt`、LR2 built-in custom folder source
    (`LR2files\CustomFolder`) を LR2 `folder` テーブル生成の入力として扱う。
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
- `RootFileEnumerationResult` は file / directory の metadata entry (`path`, `mtime`) を持つ。
  Everything grouped bridge と managed fallback は同じ entry surface へ materialize する。
- 現在の `ChartScanResult` は chart path / chart directory / audio/image/movie の
  resource hash に加え、chart directory 直下の `.txt` presence と `folderinfo.txt` 候補を持つ。
- 現在の `ChartScanResult` は file mtime / directory mtime そのものはまだ持たない。BMS file mtime は
  file diff 側で snapshot から扱う。normal folder row の directory mtime は、backfill / file diff /
  mutation sync の入口で directory metadata surface から resolver として渡す。
- `.lr2folder` discovery は LR2 full generation 入力として、chart/resource scan surface とは別に
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
  完全生成でも同じ contract を使う。

完全生成で固定する scan surface の方針:

- `song.date` / `folder.date` / `.lr2folder` freshness を LR2 起動時 scan 抑止の正本にするため、
  native bridge と managed fallback の両方で、列挙時に file / directory の mtime を同じ結果 surface に含める。
- native bridge を拡張せずに列挙後 managed API で mtime を再取得する方針は採らない。
  fallback 側だけ列挙時に mtime を持つ方針も採らない。
- Everything query は既存の並列 grouped query の考え方に揃え、chart / audio / image / movie に加えて
  `.txt`、`folderinfo.txt`、`.lr2folder`、directory 用の query を並べる。
  `.txt` / `folderinfo.txt` / `.lr2folder` は chart / resource より件数が少ない前提で、独立 query として扱う。
- managed fallback も `FastRootFileEnumerator` / `WIN32_FIND_DATA` 由来の列挙時 metadata を
  同じ `RootFileEnumerationResult` 相当へ格納し、後段は backend に依存しない。
- `ChartScanResult` / normal folder metadata / `.lr2folder` discovery は、path-only set ではなく
  metadata-bearing surface から派生させる。

## 実装前に固定する必要がある contract

### BMS 変更検出

完全生成の正しさは、BMS の変更検出に依存する。現行の「追加 path だけ parse」では、
既存 BMS の内容変更、mtime 変更、`song.txt` 変更、LR2 numeric columns の変更を取り逃がす。

方針:

- BMS / bmson に関係なく、owned chart の path add / delete / move 検出 contract は統一する。
- LR2 `song` row を生成する BMS では、`song.path` と `song.date` を変更検出の正本にする。
- `Startup`、`ReloadFileDiff`、search root 変更後 reload、manual rescan のいずれでも、
  既存 path の `song.date` と実 BMS mtime の mismatch を update target として扱う。
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

完全生成では `folder` row の stale pruning が重要になる。古い root row や `date = 0`
row が残ると、manual-only でも LR2 が不要な scan に入る。

方針:

- `folder` は LR2 native table の派生 cache として扱い、app-owned manifest / row ownership は追加しない。
- BeMusicSeeker は現在の入力 source から `expected folder path set` を deterministic に生成し、
  完全生成 ON では `folder` table をその expected set へ収束させる。
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
- `LR2files\Rival` は LR2 が ranking server 通信と active rival ID に基づいて動的に反映する領域なので、
  BeMusicSeeker の built-in discovery root には含めない。通常の LR2 BMS search root や custom folder
  出力 base で発見した `__RIVAL__` `.lr2folder` は、他の外部 `.lr2folder` と同じく処理する。
- `.lr2folder` discovery は、完全生成有効時だけ行う。
- `.lr2folder` discovery root は、LR2 BMS search root、BeMusicSeeker の custom folder 出力 base、
  LR2 built-in custom folder source から作る。
  - 通常出力先 `LR2CustomFolderOutputBaseDir`
  - ルート出力先 `LR2CustomFolderOutputBaseDirRootType`
  - LR2 executable directory 配下の `LR2files\CustomFolder`
- ルート出力先配下の `.lr2folder` 親 directory row も、LR2 root folder hierarchy の一部として
  expected folder scope に含める。たとえば `D:\BMS\ROOT\...` に出力された playlist / table folder は、
  `.lr2folder` file row だけでなく親 directory row も unknown root 扱いにしない。
- LR2 built-in `LR2files\CustomFolder` のカテゴリ directory row
  (`RANDOM\`, `PLAYLEVEL\`, `CLEAR\`, `RANK\`, `INSANE01\`, `INSANE02\` など) も、
  対応 bitmask が有効な場合は expected scope に含める。
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
- 完全生成 ON では、current generation input から導けない既存 `folder` row は prune 対象にする。
  `unknown root` / `date = 0` / 実 directory または `.lr2folder` file の消失 / mtime 不一致は、
  完了を妨げる永続状態として温存せず、生成 workflow 内で削除または上書きして収束させる。
- cleanup 導線は、workflow で修復できない既存 DB の残骸を手動で消すための補助に留める。
  定常的な startup-scan blocker 解消は workflow 自身の upsert / prune によって行う。

### 通常 mutation 時の LR2 DB writer contract

完全生成の完了後は、初回 backfill に頼らず、所持譜面ライブラリの mutation と同じ operation 内で
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
  完全生成 status を `Incomplete` にし、次回 backfill / diff run で再同期できる状態にする。
- 完全生成設定が OFF の場合、通常 mutation で LR2 補助列挙・folder generation は行わない。

テスト:

- BMS add/update/delete/move で LR2 `song` row と維持列が期待通りになる。
- install / uninstall / duplicate merge 後に `song` row が stale にならない。
- text group 変更で同 directory の `song.txt` が更新される。
- directory rename / root set 変更で affected folder rows が再生成される。
- LR2 DB write failure は完全生成 status を `Incomplete` にし、次回 backfill で復旧できる。

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

- 完全生成有効時は maintenance fact から warning を投影する。
- 完全生成無効時は、既存の `Lr2PathEncodingUnsupported` など軽い path warning だけ維持する。
- スタンドアロンモードでは `LR2非対応パス` ツリーを出さない。
- LR2 compatibility warning の ignore 永続化はこの計画では追加しない。
  - warning digest / tooltip / `LR2非対応パス` membership は `warning-model.md` の structured warning
    projection に従って組み立てる。
  - 将来 ignore UI / ignore list を追加する場合は、別計画で永続 state を追加する。

### 完全生成 status / backfill workflow

完全生成が有効な場合、BeMusicSeeker は生成状態を durable に管理し、必要な backfill を
background workflow として進める。この workflow は DB 生成状態の可視化と復旧を目的にし、
LR2 起動導線の block は行わない。

- backfill が必要な場合は、警告・進捗・キャンセル可能性を表示する。
- status は `NotNeeded` / `Needed` / `Running` / `Completed` / `Failed` / `Cancelled` /
  `Incomplete` を持つ。
- `Completed` の signature が現設定・schema・generator・parser・root set・folder source と一致する場合は
  backfill 不要とする。
- `Completed` は current generation input から導いた `song` / `folder` expected set に DB が収束していることを
  条件にする。旧 row や unknown root row を温存したまま `Incomplete` にするのではなく、
  workflow 内で削除または上書きできるものは修復してから完了判定する。
- startup-scan blocker diagnostic は、workflow が修復したはずの in-scope 不整合が残っていないことを確認する
  最終検査として使う。generation scope 外の古い row を保護するための検査にはしない。
- `Needed` / `Running` / `Incomplete` / `Failed` / `Cancelled` は完全生成 status に warning を出す。
- 完全生成設定値が欠落または不正な場合は、他の設定値と同じく既定値へ正規化し、設定値 warning は出さない。
- LR2 側の DB 自動更新設定はこの計画の実装対象にしない。
  - BeMusicSeeker 側から LR2 config を自動変更しない。
  - manual-only 運用はユーザー向け docs の推奨として扱い、status / schema / backfill の判定条件には含めない。
- LR2 が起動中の場合の DB write 競合を検出する。
  - SQLite busy timeout / file lock / transaction 失敗時の扱いをログに出す。
- 完全生成 status は durable に保存する。
  - 保存先は LR2 `song.db` 内の BeMusicSeeker-owned metadata table とする。
  - schema version
  - generator version
  - parser version
  - LR2 BMS root set signature
  - folder generation source signature
  - 完全生成設定値
  を含め、いずれかが変わった場合は completed を無効化する。
- generator version は `song` / `folder` 生成列、folder generation scope、warning projection の意味が変わる時に bump する。
- parser version は BMS metadata parse、text group parse、raw resource reference parse、CRC 入力正規化の意味が
  変わる時に bump する。
- backfill は startup ready / operable をブロックしない。初期化完了後に background workflow として開始し、
  LR2 起動要求が来ても同じ workflow へ join して待つことはしない。
- backfill 中は read-only 操作を許可する。
  - 一覧閲覧、検索、ソート、プレイリスト表示、設定画面表示は許可する。
  - owned collection / LR2 `song.db` に mutation を起こす操作は開始前に抑止する。
    例: 譜面追加、削除、移動、install/reinstall、全譜面再スキャン、BMS root 設定変更、
    完全生成設定の切替。
- backfill 完了前に current scan / owned source generation / folder source signature を再確認する。
  - backfill 開始後に外部ファイル変更などで入力が stale になっていれば `Completed` にせず `Needed` に戻す。
  - stale でなければ `Completed` を記録し、以後は通常 mutation 時の LR2 DB writer contract で差分維持する。

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

- 完全生成の前提として、既存 BMS の変更を検出できるようにする。
- スタンドアロンモードでも同じ変更検出 contract を使えるようにする。

実装:

- DB row の `song.path` と実 path、`song.date` と実 BMS mtime の Unix 秒が一致する場合は unchanged。
- `song.path` または `song.date` が変わった場合だけ `ChartFileSnapshot` を読み、MD5 を比較する。
- `Startup` / `ReloadFileDiff` / search root 変更後 reload では、既存 path の mtime mismatch を
  update target set に含める。
- MD5 が同じなら `song.date` のみ更新する。
- MD5 が違うなら再parseする。
- deleted path と added path の MD5 が同じ場合は move/relink として維持列を引き継ぐ。

テスト:

- `song.path` + `song.date` 一致は parse しない。
- `song.date` changed + MD5 same は `song.date` のみ更新する。
- `song.date` changed + MD5 changed は BMS row / chart_info / maintenance を更新する。
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
  directory mtime も native bridge / fallback で同じ shape にし、normal folder row と startup blocker diagnostic の正本にする。

text group:

- BMS chart directory 直下の任意 `.txt` 有無だけを扱い、特定ファイル名 `song.txt` には限定しない。
- descendant / ancestor の `.txt` を拾わない。
- 完全生成無効時は走らせない。

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
- `.lr2folder` file mtime と directory mtime が folder row / startup blocker diagnostic に反映される。
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
- `folderinfo.txt` があれば LR2 風に parse する。
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
- `newsong.lr2folder` は対象曲がある場合だけ `type = 3` で同期され、対象曲がない場合は prune される。
- `course1.lr2folder` / `course2.lr2folder` / `course3.lr2folder` は `type = 6` で同期される。
- `LR2files\Rival` は built-in discovery root に含めず、既存 row を全件 prune しない。
- 通常 discovery 範囲で見つかった `__RIVAL__` `.lr2folder` は外部 `.lr2folder` として同期される。
- current discovery scope から導けなくなった unknown / stale `.lr2folder` row は prune される。
- ルート出力先配下の `.lr2folder` 親 directory row と built-in `LR2files\CustomFolder` のカテゴリ directory row は
  expected scope として扱われ、unknown root blocker にならない。
- Shift_JIS 出力した `.lr2folder` を同じ解釈で parse できる。

### Phase 8: 完全生成 status / backfill UI を追加する

目的:

- 完全生成が未完了の状態を UI / log で追跡できるようにする。
- backfill が発生する場合にユーザーへ見える形にする。

実装:

- 完全生成 status を保持する。
- status は `NotNeeded` / `Needed` / `Running` / `Completed` / `Failed` / `Cancelled` /
  `Incomplete` とする。
- status と signature を LR2 `song.db` 内の BeMusicSeeker-owned metadata table に保存する。
- status が存在しない、signature が変わった、または前回 status が `Failed` / `Cancelled` /
  `Incomplete` の場合は `Needed` とする。
- 完全生成が有効で `Needed` の場合、`startup_initialization_complete` 後に background workflow を queue する。
- 完全生成設定を OFF から ON に変更した場合も、設定保存後に同じ workflow を queue する。
- backfill needed / running / completed / failed / cancelled / incomplete を log と UI に出す。
- backfill progress は全体合算 total と stage 別 processed count の両方を表示する。
- `Needed` / `Running` / `Incomplete` / `Failed` / `Cancelled` は完全生成 status warning として表示する。
- startup-scan blocker diagnostic は、workflow が upsert / prune した後に in-scope 不整合が残っていないかを
  確認する最終検査にする。修復可能な stale / unknown row は workflow 内で削除または上書きし、
  温存したまま `Incomplete` にしない。
- 設定値が欠落または不正な場合は既定値へ正規化する。設定値 warning は出さない。
- LR2 config の auto update 設定検出や変更は行わない。manual-only 運用の推奨は docs に記載し、
  full generation status には含めない。
- backfill のためだけの自動 backup は作らない。
- write は per-chunk transaction とし、chunk commit 成功後に processed cursor / run id / status を durable に更新する。
- chunk 失敗時はその chunk の transaction を rollback し、status を `Failed` にする。
- cancel 後の partial write は incomplete として扱い、status warning に出す。
- 次回 run は最後に成功した cursor から再開する。再開前に必要なら current status / signature を再検証する。
- backfill 中は read-only 操作を許可し、owned collection / LR2 `song.db` mutation 操作は開始前に抑止する。
- backfill の完了直前に source generation / folder source signature / startup-scan blocker diagnostic を再確認し、
  stale または blocker 残存なら `Completed` にしない。

テスト:

- backfill 不要時は警告なし。
- backfill 必要時は警告と進捗が出る。
- status 欠落、signature 変更、前回 `Failed` / `Cancelled` / `Incomplete` で `Needed` になる。
- 完全生成設定 OFF では queue されず、ON へ変更すると queue される。
- 不正な設定値は既定値へ正規化され、設定値 warning は出ない。
- failed 状態で完全生成 status warning が出る。
- cancel 後に incomplete status が残り、次回再開できる。
- backfill 中に read-only 操作は許可され、library mutation 操作は抑止される。
- workflow 修復後も in-scope blocker diagnostic が残る場合は `Completed` にならない。
- 完了直前に source signature が変わった場合は `Needed` に戻る。

### Phase 9: resumable backfill

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
- preflight backup / restore point は作らない。
- run id / durable status / processed cursor を持つ。
- chunk 成功後だけ cursor を進め、失敗 chunk は rollback して次回再処理する。
- 完了直前の source staleness check と startup-scan blocker diagnostic が clean な場合だけ `Completed` を記録する。
- `favorite` / `adddate` / `tag` を維持。
- CP932 非対応 BMS row は BeMusicSeeker DB から削除しない。
- backfill 再実行で追加差分が出ない。

テスト:

- `date = null` の既存 row が mtime で backfill される。
- 維持列が維持される。
- CP932 非対応 BMS row が削除されない。
- 2 回目 backfill が no-op になる。
- partial run 後に resume できる。
- failed chunk が rollback され、次回同じ target から再開できる。
- source staleness が検出された run は `Completed` にならず、次回再実行対象になる。

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

- BMS 変更検出は完全生成の前提条件。path added/deleted だけでは不十分。
- 通常差分検出は LR2 `song.path` / `song.date` を正本にする。mtime preserved copy や
  同秒更新は完全には検出できないため、force rescan を残す。
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
- folder row の削除は generation scope 内に限定する。scope 外の row は原則触らない。
- LR2 が起動中の `song.db` 書き込みは競合する可能性があるため、busy/lock 失敗を明示ログにする。

## フェーズ進捗

この表は、この計画書を実装タスクとして渡す際の現在地を示す。上の「実装フェーズ」は
ゴール仕様、ここは実装済み範囲と残作業の管理を目的にする。

| Phase | 状態 | 実装済み / 現行決定 | 残作業 / 注意 |
| --- | --- | --- | --- |
| Phase 0: Golden fixture と contract 固定 | 一部完了 | `LR2CRC32` / ROOT sentinel / CP932 boundary の contract、OpenLR2 source classifier の推測抑止、`exlevel` contract、manual-only scan blocker、copied `song.db` の current completed no-op はテスト化済み。 | `folderinfo.txt` / `.lr2folder` など、実 DB 由来の golden fixture を追加する。 |
| Phase 1: BMS 変更検出 | 主要実装済み | `song.path` / `song.date` / hash を使う変更検出、same MD5 の targeted update、runtime reload の再評価 queue は接続済み。 | 大規模 root 変更・mtime preserved copy の手動検証を残す。 |
| Phase 2: `song` row merge / ownership | 主要実装済み | `Lr2SongDbWriter`、generated/user column 分離、runtime write failure の status marking、merge 時 user column preservation、copied `song.db` backfill 時の user column preservation は自動テスト済み。 | 実 DB copy での総合確認を残す。 |
| Phase 3: metadata-bearing scan surface / raw resource reference | 一部完了 | BMS parser / snapshot 側の raw resource reference、text group の targeted `song.txt` 更新、完全生成 ON 時だけの scan 条件、`RootFileEnumerationResult` の file / directory mtime entry、Everything fixed scan / grouped bridge ABI / managed fallback の metadata surface は実装済み。`.txt` / `folderinfo.txt` / `.lr2folder` / normal folder directory mtime は backfill / sync に接続済み。fallback metadata fixture は追加済み。 | native bridge 実機 parity を統合確認する。 |
| Phase 4: LR2 compatibility warning | 主要実装済み | `Lr2CompatibilityEvaluator`、maintenance 最小 fact、standalone mode での LR2 非対応パス tree 非表示は接続済み。 | warning 表示の実機確認と、copied DB での backfill 表示確認を残す。 |
| Phase 5: `song` row enricher | 主要実装済み | `Lr2SongRowEnricher`、`chart_info` 由来 numeric columns、`exlevel = #EXLEVEL raw int / 未設定 0` は実装済み。 | LR2IR / tag.db 由来の exlevel 上書きは対象外として維持する。 |
| Phase 6: normal `folder` row generator | 主要実装済み | normal folder generator / scope planner / DB sync、mutation・file diff failure の incomplete marking、directory metadata surface 由来の `folder.date` resolver、`folderinfo.txt` entry metadata surface は接続済み。 | 実機で directory mtime / folder row freshness を確認する。 |
| Phase 7: `.lr2folder` DB sync | 主要実装済み | playlist projection と `.lr2folder` / `folder` row sync の同一化、通常 discovery、built-in source の相対 path 化、root custom output / built-in category parent row 生成、`LR2files\Rival` の built-in discovery 非対象化、`LR2files\CustomFolder` の `<customfolder>` bitmask、`newsong` dynamic row、`course1-3` の `type=6` は接続済み。 | 実 LR2 setup / built-in folder fixture での最終確認を残す。 |
| Phase 8: status / backfill UI | 主要実装済み | durable status、runtime progress、setting queue、cancel、mutation guard、startup blocker diagnostic / cleanup は実装済み。 | 長時間 backfill の UI 手動確認と failure/cancel 再起動確認を残す。 |
| Phase 9: resumable backfill | 一部完了 | durable cursor、stage / chunk resume、changed-only song row backfill、cancel boundary の基盤、completed status current 時の 2 回目 no-op queue、copied `song.db` の current completed no-op、failed song-row chunk rollback/retry、cancel 後 restart resume は自動テスト済み。 | 実 DB での partial resume / cancel-restart を統合確認する。 |

### フェーズ別進捗メモ

以下はフェーズごとの設計責務と実装時の注意を残す詳細メモである。完了 / 未完了の判定は
上の進捗表を正とし、残作業は後続の「残作業の推奨順」で管理する。

- Phase 0: golden fixture は一部実装済みで、残りは該当 service の実装・検証 cycle に合わせて追加する。
  - まず既存 `Lr2SongFolderParentNormalizer` の CRC / CP932 encode contract を固定する。
  - byte length boundary と manual-only scan blocker は自動テスト済みである。
  - `folderinfo.txt` / `.lr2folder` は synthetic fixture で主要 contract を固定済みで、残りは実 DB 由来 fixture として追加する。
  - LR2 root sentinel は `LR2CRC32("ROOT")` ではなく `LR2CRC32("ROOT\0") = e2977170` として扱う。
- Phase 1: BMS 変更検出は主要経路へ接続済み。残りの検証では以下を固定する。
  - まず file diff で既存 BMS の `song.date` / mtime mismatch を parse target に入れる。
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
  backfill request へ渡す。
  - fixed native resource scan は、chart / audio / image / movie に加えて `.txt` / `folderinfo.txt`
    query と file mtime を同じ bridge layout で返す。
  - `.lr2folder` は grouped enumeration surface で列挙し、同じ `RootFileEnumerationEntry` shape で backfill へ渡す。
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
- Phase 5: `Lr2SongRowEnricher` と chart_info 由来 column reflection は実装済み。
  - まず `Lr2SongRowEnricher` を導入し、既存の `date` / `txt` / user columns preservation / folder-parent CRC 正規化を
    file diff parser と DB writer から同じ入口へ寄せる。
  - detailed parser / `chart_info` 由来 numeric columns は同じ enricher から反映する。
  - 現行接続では `chart_info` に正本がある `level` / `difficulty` / `maxbpm` / `minbpm` / `mode` /
    `longnote` / `bga` / `random` / `karinotes` / `exlevel` を反映する。
  - `song.judge` は LR2 の raw `#RANK` 値で、`chart_info.judge` は判定幅 percent なので写さない。
  - `bga` は BMS/BMSON timeline 上の BGA event 有無、`exlevel` は BMS `#EXLEVEL` の raw 値を正本にする。
    `#DEFEXRANK` は判定幅計算だけに使い、`exlevel` 未定義時は `0` を入れる。
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
    `lr2_full_generation_status` を `Incomplete(lr2_normal_folder_file_diff_sync_failed)` に落として次回 backfill で修復する。
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
  - initialization への production 接続は `EnableLR2SongDbFullGeneration` で gate し、
    LR2 linked mode かつ設定が有効な場合だけ full file scan 完了後に normal folder sync を実行する。
  - full scan 後の normal folder sync は file diff DB commit を flush した後に別 workflow として実行し、
    owned collection の `HasDbDiff` や UI refresh 判定には混ぜない。folder sync の生成 / upsert / delete / metadata 欠落は
    `song_tbl_file_check_breakdown` の LR2 normal folder metrics として追跡する。
  - full scan 境界では complete chart path set と `folderinfo.txt` scan surface を渡せるため `AllowPrune=true` とする。
    ただし compose service 側は CP932 非対応 chart path や metadata 欠落がある場合に stale normal row deletion を抑止する。
    `ChartScanExecutionResult.Success != true` の partial surface では normal folder sync 自体を skip し、`folderinfo.txt`
    surface 欠落や chart path 不完全性を stale row prune / title overwrite に使わない。
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
    `type = 2` と directory parent hash、root custom folder 出力 base は `type = 2` と root parent hash を使う。
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
    `song.adddate` 未設定の current row は、完全生成で新規 `song` row として `adddate = now` が入る可能性があるため
    `newsong` 対象として扱う。空ライブラリや titleflash 無効時は対象にしない。
    `customfolder` / `titleflash` / `newsong` 対象有無は backfill signature に含めるため、LR2 setup 変更や
    `newsong` 期限切れは次回評価で backfill needed になる。
    disabled になった built-in row は `LR2files\CustomFolder` DB-relative prune scope で削除対象にする。
  - normal folder sync は `type = 1` の scope だけを prune / upsert 対象にし、`type = 2` の `.lr2folder` row を
    上書き・削除しない境界を維持する。
  - `.lr2folder` DB sync は `Lr2FolderFileDbSyncService` に分離する。
    入力 item は current `.lr2folder` file projection、`ScopeDirectories` / `ScopePaths` は stale row prune の境界として扱う。
    root custom folder 出力先と built-in `LR2files\CustomFolder` については、同じ sync request の
    `DirectoryRowScopeDirectories` から親 / カテゴリ directory row も生成する。通常 BMS root は
    `DirectoryRowScopeDirectories` に入れず、normal `folder.type = 1` 生成の責務を混ぜない。
    同一親に複数 `.lr2folder` がある場合、親 row は一度だけ upsert 候補にし、generated path set で
    stale parent row pruning と重複 write を抑止する。
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
- Phase 8 は `lr2_full_generation_status` table の単一 row (`name = "default"`) から開始する。
  `status` / `signature` / `run_id` / `processed_cursor` / `total_count` / `stage` / `last_error` を durable に保持し、
  `Completed` かつ signature 一致のときだけ backfill 不要と判定する。`Failed` / `Cancelled` / `Incomplete` /
  signature mismatch / missing row は `Needed` として再開可能にする。
  初期接続では `startup_initialization_complete` 後の best-effort warmup から status を評価し、必要なら
  `lr2_full_generation_backfill` startup background task を queue する。この task は通常 startup readiness を待たせず、
  実装済み stage を進めたうえで残 stage がある場合は `Incomplete` として再開可能にする。
  最初の実 runner は normal folder stage を実行し、current owned BMS path snapshot と LR2 BMS root から
  root / ancestor / chart directory `folder` row を sync する。`folderinfo.txt` は normal folder generator と同じ
  metadata target から候補を作る。続いて current owned `BMSFile` から persistence 用 copy を作り、可能なら
  `ChartFileSnapshot` から BMS を再 parse して、live UI row を変更せずに `song` generated columns と
  `chart_digest_map` を backfill する。再 parse は snapshot の encoding detection 結果を使い、encoding が
  `unknown` / unsupported の場合は既存 row copy に fallback して文字化けした metadata を永続化しない。
  current parser version の `chart_info` row が既に存在する場合は、`level` / `difficulty` / BPM / `mode` /
  `longnote` / `random` / `karinotes` を同じ backfill write で `song` に反映する。
  LR2 full generation runner は、この song row write の直前に既存の `ChartInfoBuildService` full backfill を
  synchronous に完了させる。これにより missing / stale `chart_info` も既存の reader -> parser workers ->
  DB commit writer pipeline で生成され、LR2 backfill service 側に別 parser / 別 queue を増やさない。
  current owned chart directory 直下の `.txt` presence も `TextFileDirectories` scan surface として渡し、
  `song.txt` を同じ backfill write で再生成する。
  LR2 compatibility facts は `maintenance` の LR2 列だけを targeted update し、既存 resource health /
  encoding columns は置換しない。`maintenance` row が無い場合だけ path/hash と LR2 列の最小 row を作る。
  同じ backfill run で計算した LR2 compatibility facts は live BMS row の materialized `maintenanceInfo` に
  LR2 列だけ overlay し、`Lr2CompatibilityWarningProjection` を通して warning 表示を更新する。
  resource health / encoding state は live row 側でも上書きしないため、この refresh のためだけに full
  `maintenance_hydration` や resource health index rebuild は走らせない。
  persistence 用 copy では live row の `folder` / `parent` が形式上 valid でも path と一致する保証がないため、
  これらを空にして `Lr2SongRowEnricher` に再生成させる。
  さらに LR2 BMS root、通常 custom folder 出力 base、root custom folder 出力 base 配下の `.lr2folder` は
  `RootFileEnumerationService` の Everything/fallback path で discovery し、`Lr2FolderFileDbSyncService` へ渡して
  source classification 済みの `folder.type = 2` row を sync する。stale prune scope は LR2 BMS root に限定し、
  custom folder 出力 base は discovery-only とする。playlist 出力先の exact prune は既存の playlist output workflow が担当する。
  LR2 built-in custom folder source (`LR2files\CustomFolder`) も discovery root に含める。
  `LR2files\CustomFolder` は LR2 setup の `<customfolder>` bitmask に従い、`course1-3` は常時対象、
  `newsong` は `titleflash` と `song.adddate` に基づく dynamic row として扱う。
  `LR2files\Rival` は built-in discovery root に含めず、LR2 側の ranking server 連動に任せる。
  通常出力先 / BMS root 由来の `.lr2folder` は絶対 path と directory parent hash のまま扱い、
  そこに `__RIVAL__` `.lr2folder` があれば外部 `discovered_lr2folder` として処理する。
  `.lr2folder` prune は discovery surface が complete で、かつ発見した各 file を読み取れた場合だけ許可する。
  enumeration failure や一時的な file read failure がある場合、その回は upsert のみにして既存 row を消さない。
  file read / parse に失敗した row は persistence copy の folder/parent 再生成に fallback し、失敗で既存 DB row を消さない。
  current owned chart directory 直下の `.txt` presence は file diff の text group scan surface と同じ意味で
  backfill 入力に渡し、`song.txt` へ反映する。通常 file diff でも BMS 本体 mtime が変わらず `.txt`
  presence だけ変わる場合は targeted `song.date` / `song.txt` update へ落とすため、text group freshness は
  current scan contract に含まれる。
  file diff を通らない direct install / path replacement では、DB helper ではなく mutation application 層で
  `Lr2TextGroupResolver` を呼び、導入後 / 移動後 directory の direct `.txt` presence を `BMSFile.txt` に反映してから
  `Lr2SongDbWriter` / storage row update へ渡す。
  path replacement では `song.folder` / `song.parent` を一度空にして `Lr2SongRowEnricher` に再生成させる。
  これにより通常 mutation でも backfill / file diff と同じ CP932 非対応判定と LR2 CRC contract を使う。
  direct install / path replacement / remove 後は、同じ owned mutation 成功 path で normal `folder.type = 1` row も
  `Lr2NormalFolderDbSyncService` に渡して sync する。追加だけの mutation は upsert-only とし、削除または移動を含む
  mutation は old / new chart directory を dirty prune scope として渡す。sync input にはその scope 内に残る current BMS path も
  含め、scope 内で期待されなくなった normal folder row だけを削除する。bmson は `song` / `folder` の対象にしない。
  root set 変更や scan surface が不完全な場合の広域 prune は引き続き full scan / backfill に任せる。
  この best-effort sync が失敗しても owned mutation は成功扱いにし、`lr2_full_generation_status` を
  `Incomplete(lr2_normal_folder_mutation_sync_failed)` にして次回 backfill で修復できるようにする。
  `folder` row の path replacement / startup normalization でも parent CRC は `Lr2SongFolderParentNormalizer.ComputeDirectoryHash`
  を使い、`LR2CRC32` の直接呼び出しを通常 mutation / initialization surface に増やさない。
  完了直前の source staleness check は、service に caller-provided predicate を渡す形にし、`BMSLibrary` 側で
  owned collection / storage row version と root / `.lr2folder` / `folderinfo.txt` / text group surface を開始時入力と
  再比較する。stale の場合は `Completed` にせず `Incomplete(source_stale_detected)` とする。
  sync 後は startup-scan blocker diagnostic を実行し、root set 欠落、current song row 欠落、
  `song.date` 欠落 / `0`、generation scope 内の `folder.date` 欠落 / `0`、missing target、
  実 directory / `.lr2folder` file の mtime と一致しない `folder.date` が無い場合だけ `Completed` を記録する。
  診断で unknown root / date missing / missing target の `folder` row が見つかった場合は、同じ full generation run 内で
  cleanup delete を適用してから再診断する。mtime mismatch は列挙 metadata と live filesystem の差分や resume 境界の
  可能性があるため即削除せず、残る場合は `Incomplete(startup_scan_blockers_detected)` として
  diagnostic count を log / status detail に残す。
  status signature は normalized / deduplicated / case-insensitive な LR2 BMS root set を含める。
  加えて `.lr2folder` discovery root set、LR2 setup の `<customfolder>` bitmask、`titleflash` を含める。
  root set や built-in CustomFolder の対象条件が変わった場合は既存 `Completed` を信用せず、
  backfill needed として再評価する。`LR2files\Rival` は BeMusicSeeker の built-in discovery root ではないため、
  signature へ含めない。
  signature には app schema / chart_info schema / chart_info parser / song-folder generator /
  `.lr2folder` parser / LR2 compatibility fact の version も含める。各 component の生成意味が変わった場合は
  対応 version を bump し、既存 `Completed` を再評価対象にする。

## File Enumeration / Everything Surface Contract

Everything native bridge と managed fallback は、完全生成 status の `Completed` 判定に影響する
入力 surface として扱う。特に `.txt`、directory mtime、`folderinfo.txt`、`.lr2folder`
existence / mtime は意味的に揃える。

現行計画の整理:

- native bridge ABI は拡張する前提にする。chart / resource / `.txt` / `folderinfo.txt` / `.lr2folder`
  の grouped query result は、path だけでなく file mtime を含む file entry surface を返す。
- managed fallback も同じ file entry surface を返す。fallback scan 後に同じ path へ mtime を再問い合わせする
  二段構えにはしない。
- `.lr2folder` sync item も enumeration entry の mtime を正本にする。path だけが渡された `.lr2folder` は
  missing metadata として扱い、後段で `File.GetLastWriteTimeUtc` を呼んで補完しない。
- directory mtime は normal folder row と startup-scan blocker diagnostic の正本であるため、
  native bridge / fallback の metadata surface に含める。
- surface が不完全な場合は「存在しない」と見なして `Completed` にしない。stale row prune や
  startup-scan blocker diagnostic へ使う surface は complete flag とセットで扱う。
- Everything query に exclude DSL は追加しない。root / extension / filename の組み合わせで surface を分け、
  アプリ管理物か外部由来かは結果分類で判定する。
- bridge layout version / result version はログ分析用に出してよい。アプリ本体と native bridge DLL は
  同一配布物なので、旧 ABI DLL を runtime fallback する互換分岐は計画に含めない。

## 残作業の推奨順

現時点で計画本体の production 接続と自動テストで固定できる主要 contract は実装済みである。
この節に残す作業は、実機 Everything / 実 LR2 DB / 実 LR2 起動ログが必要な統合確認、または
実 DB 由来 fixture を入手した後に追加する contract 補強である。実機確認・手動確認・LR2 での確認を
後回しにする作業サイクルでは、新しい実装ブロッカーとしては扱わない。

1. native bridge metadata parity を統合確認する。
   - fixed scan の `.txt` / `folderinfo.txt` entry、grouped enumeration の `.lr2folder` entry、directory mtime が同じ `RootFileEnumerationEntry` contract になることを実機 Everything 環境で確認する。
   - bridge layout / result version log を必要に応じて追加する。
2. Phase 9 の統合確認を固める。
   - copied `song.db` で、初回 backfill、2 回目 no-op、partial resume、failed chunk rollback、
     cancel/restart、startup-scan blocker cleanup を確認する。
   - completed status と signature が current な場合に 2 回目 backfill queue が発生しないことは unit/integration-shaped test で固定済み。
   - copied `song.db` でも completed status と signature が current な場合に backfill queue が発生しないことは unit/integration-shaped test で固定済み。
   - song row chunk failure では chunk transaction が rollback され、durable `Failed` cursor から再実行できることは unit/integration-shaped test で固定済み。
   - 実機ログで `processed_cursor` / `stage` / `Completed` / `Incomplete` の遷移が想定どおりか確認する。
3. Phase 0 の残 fixture を追加する。
   - `folderinfo.txt`、`.lr2folder` の実 DB 由来 fixture を追加する。これは fixture 入手後の contract 補強であり、
     現行 synthetic fixture と既存 unit / integration-shaped test が production 実装の前提を固定している。
4. LR2 manual-only 起動での最終確認を行う。
   - 完全生成後、未変更 root で LR2 / OpenLR2 が再帰 scan に入らないことを確認する。
   - LR2 起動そのもののブロッキングや排他はこの計画の対象外として扱う。

## 横断的な実装済みメモ

この節は、個別 Phase へ閉じにくい実装済みの判断と runtime 境界をまとめる。新しい残作業は
上の「残作業の推奨順」へ追加し、この節には完了済み contract だけを残す。

- `Lr2SongDbWriter.UpsertGeneratedSong(...)` は direct install / path replacement / backfill の共通 persistence 境界として扱う。
  呼び出し元の `BMSFile` に `date` が無い場合は実ファイル mtime から補完し、新規 `song` row で `adddate`
  が無い場合だけ現在時刻を入れる。既存 row の `adddate` は writer update では変更しない。
- install package inline `chart_info` build は、parse / current row skip で得た `chart_info` row を BMS storage owner
  に反映してから `Lr2SongDbWriter` へ渡す。これにより direct install 直後の `song` row も、backfill を待たずに
  `level` / `difficulty` / BPM / `mode` / `longnote` / `random` / `karinotes` を持つ。
- playlist custom folder output workflow は `.lr2folder` file を出力した同じ操作内で `folder` row も sync する。
  ルートフォルダ出力 (`is_root_folder`) では playlist workflow 側でも `Lr2FolderFileSourceClassifier` を通し、
  generated `.lr2folder` row の parent を `ROOT` に揃える。
  playlist entry の行単位編集保存も、LR2 連携モードで出力先が設定されている場合は owning table の `.lr2folder` projection を再出力し、
  `folder` row を同じ scope で pruning する。`is_root_folder` の一括変更は旧出力先 directory を変更前に捕捉し、
  commit と同じ operation 内で旧 directory row を prune してから新出力先を生成する。
- playlist entry level の LR2 `song.level` writeback は、complete row writer ではなく targeted `UPDATE song SET level`
  を使う。既存 `song` row が無い path には不完全 row を作らず、既存 row の user / generated columns も触らない。
- LR2 full generation backfill の進捗は、durable status table と log に加えて `BMSLibrary` の bindable property
  (`Running` / requested-completed version / total / processed / stage) にも投影する。service 側は progress callback を
  観測専用として扱い、callback 失敗で durable backfill を失敗させない。
- startup progress では LR2 full generation backfill を startup / full reinitialize の background phase として扱う。
  request が来た場合だけ `[processed/total] LR2 song.db 完全生成 <stage>` を表示し、request 前に skip された場合は
  post-startup warmup 由来の遅い request で進捗を巻き戻さない。
- in-process で `Lr2FullGenerationBackfillRunning` の間に同じ workflow が再要求された場合は、durable status が
  `Needed` のままでも追加 queue せず、現在の bindable progress を返す。アプリ再起動後の persisted `Running` は
  incomplete run として再評価し、通常の backfill request へ戻す。
- backfill runner の `Incomplete` / `Failed` は startup progress の完了版数を進めない。
  `Lr2FullGenerationBackfillFailedVersion` と failure message で ViewModel へ通知し、進捗バーは失敗状態として残す。
  `CompletedVersion` は durable status が `Completed` になった run だけで進める。
- LR2 full generation status は durable table の評価結果と runtime progress を `BMSLibrary` の internal snapshot +
  public version に投影する。`MainWindowViewModel` はこれを status bar 用の runtime status に変換し、
  `Needed` / `Failed` / `Incomplete` / `Cancelled` を startup progress 外でも persistent warning として表示する。
  `Running` は startup progress が表示中なら既存 startup progress を正本にし、startup progress 外では設定保存後の
  background backfill などを見失わないよう status bar に表示する。`Completed` / `NotNeeded` は status bar では非表示にする。
- 完全生成設定は設定ダイアログの LR2 連携項目として表示する。既定値は LR2 連携モードの標準挙動に合わせて
  `true` とし、OFF から ON に変更して保存した場合は
  `QueueLr2FullGenerationBackfillIfNeeded("SettingDialog.SaveSettings")` を呼んで同じ background workflow に流す。
  LR2 連携 mode / 完全生成設定 / LR2 root / 通常 custom folder 出力先 /
  ルート custom folder 出力先のいずれかが変更され、保存後に完全生成が有効な場合も同じ queue に流す。
  これらは signature と `.lr2folder` discovery surface を変えるためである。
- LR2 full generation backfill 実行中は、install / merge / rename / root move / extension rename / delete など
  owned collection と LR2 `song.db` を同時に変える操作を入口で警告して中止する。加えて
  `ApplyInstalledChartStorageTargets` / `ApplyLibraryMutationDelta` に low-level guard を置き、将来の追加経路や
  テスト用 reflection 経路が入口 guard を迂回しても DB / owned collection を書き換えないようにする。
  file diff reload / full reinitialize / pending install apply / install destination repair / playlist level writeback /
  encoding commit / mode commit / direct song commit も同じ backfill priority window で止める。
  pending-only 操作は current owned source mutation ではないため、この guard の対象外とする。
- resumable backfill は folder 系を stage 境界、`song_rows` を chunk 境界で再開する。durable status の
  `processed_cursor` が `normal_folders` / `.lr2folder` の完了境界に達している場合、その完了済み stage は
  再実行しない。`song_rows` は chunk commit 成功後だけ cursor を進め、次回 run では cursor 以前の
  song target をスキップする。失敗時に outer catch が durable failed status を上書きしても、同一 run の
  既存 cursor / total は維持する。
- backfill cancellation は request token を service へ渡し、stage 境界と `song_rows` chunk 境界で
  `Cancelled` status と current cursor を durable に記録する。runtime request state も `Cancelled` として終端し、
  次回 evaluate では通常の resumable backfill request に戻す。`Running` status は status bar にキャンセル操作を表示し、
  UI は model の cancellation token request を発火するだけで、durable status の確定は backfill runner の境界処理に任せる。
- startup-scan blocker diagnostic は、resume により folder stage をスキップした場合でも、current generation scope の
  `folder` row が存在し、`folder.date` が directory / `.lr2folder` file の Unix 秒 mtime と一致するかを
  最後に検証する。missing target または不一致が残る run は `Completed` にせず
  `Incomplete(startup_scan_blockers_detected)` とする。
- 同 diagnostic は current root / chart path から導出される通常 folder target が `folder` table に存在することも検証する。
  `normal_folders_completed` 以降の durable resume では normal folder stage を再実行しないため、期待される root /
  ancestor / chart directory row が欠けている場合は `missingExpectedFolderRows` blocker として `Completed` にしない。
- 同 diagnostic は `.lr2folder` stage を resume でスキップした場合も、読込・projection 可能な `.lr2folder`
  source に対応する `folder` row が存在することを検証する。読めない `.lr2folder` は既存の prune 抑止と同じく
  expected row から外し、欠落検出は `missingExpectedLr2FolderRows` として log / status detail に残す。
- startup-scan blocker cleanup は、通常 workflow で修復できなかった既存 DB 残骸を削除する補助 helper
  (`CleanupStartupScanBlockerFolderRows`) として残す。full generation 本体も unknown root / date missing /
  missing target の `folder` row は同一 run 内で cleanup し、mtime mismatch は cleanup せず incomplete diagnostic として残す。
  `song` row 欠落、root 未設定、known root 外 `song` row は削除で直せる問題ではないため cleanup 対象にしない。
- `LR2非対応パス` tree は LR2 連携モード専用の compatibility surface として扱い、standalone mode では表示しない。
- library root scan の `.txt` surface は LR2 連携モードかつ完全生成設定 ON のときだけ列挙する。
  pending package / install estimation の局所 scan は package 表示・導入時 projection のため既存どおり text group を扱う。
- `.lr2folder` projection は、通常 custom folder の `type = 2` と OpenLR2 root special 用の
  `type = 3/4/6` だけを受け付ける。`LR2files\CustomFolder` の固定 special は
  `newsong.lr2folder` を `type = 3`、`course1-3.lr2folder` を `type = 6` として扱う。
  それ以外の source は fixture で確定するまで特殊 type を推測しない。未知 type や reserved normal directory `type = 1`
  は `folder` row にしない。
- root custom folder 出力先が `LR2files\CustomFolder` 配下と重なる場合は、
  BeMusicSeeker 管理出力を優先し、absolute path + root parent として扱う。これは出力 workflow の
  scope prune を absolute output directory で完結させるためであり、外部由来の built-in CustomFolder source は
  引き続き LR2 root 相対 path (`LR2files\CustomFolder\...`) として扱う。
- `song.exlevel` は BMS `#EXLEVEL` の raw integer を正本にし、`#DEFEXRANK` は判定幅計算にだけ使う。
  この修正では parser / schema version を上げないため、既存 `chart_info.exlevel` の非 null 値はそのまま扱い、
  `chart_info.exlevel IS NULL` または generated song row の未設定値だけを LR2 と同じ `0` に正規化する。
- manual `ReloadFileDiff` / search root 変更後は file diff 適用完了後に
  `QueueLr2FullGenerationBackfillIfNeeded("ReloadFileDiff")` で status を再評価する。
  これにより root set / folder source signature mismatch は次回起動待ちにせず検出するが、
  durable status が clean な場合は backfill を開始しない。
- startup-scan blocker diagnostic は legacy `folder.type = 0 / NULL` row も扱う。
  `folder.type` だけで normal directory / `.lr2folder` を判定せず、normalized path が `.lr2folder`
  で終わるかを target 種別の正本にする。これにより旧 DB の directory row でも
  directory mtime / existence を検証し、stale / missing target を cleanup 対象にできる。
- direct owned mutation 中の normal folder sync failure は、file diff normal folder sync failure と同じく
  durable full generation status を `Incomplete` にする。mutation は user operation の正しさを優先して進め、
  folder row の再同期は次回 backfill / retry で復旧できる状態にする。
- duplicate merge の BMS source unregister は LR2 `song` row を一度削除してから destination path を再登録するため、
  unregister 前に source path の LR2 user columns (`favorite` / `adddate` / `tag`) を snapshot し、
  moved BMS owner へ再適用してから destination `song` row を upsert する。これにより merge は
  direct path replacement と同じく DB 側 user state を保持する。
- direct runtime の LR2 `song` write failure (`song` upsert / level update / mutation delete / path replacement)
  は normal folder sync failure と同じく durable full generation status を `Incomplete` にする。
  この段階では既存の user operation 失敗 semantics は変えず、例外は再 throw する。次回 backfill / retry が
  復旧経路になるよう、失敗 stage と message を status に残す。
  owned mutation apply は state applier 全体ではなく、BMS `song` row を実際に触る unregister / path cleanup /
  path replacement の callback で marking する。bmson row や installed package state の失敗を LR2 full generation
  status に誤分類しないためである。
- file diff reload と maintenance workflow は下位 service 内で `Lr2SongDbWriter` を直接呼ぶため、workflow 境界で
  fatal failure を durable full generation status `Incomplete` にする。下位 writer をすべて status-aware にするより、
  UI/operation semantics を変えず、backfill/retry の入口を失わないことを優先する。
- direct runtime の `dbGateway.UpsertSongs(...)` / `UpdateSongLevels(...)` call site は、
  `BMSLibrary.ExecuteLr2SongDbWrite(...)` を status boundary として通す。下位 `BmsLibraryDbGateway` はテストや
  internal service の raw DB gateway として残るため、production `BMSLibrary` 側の call site guard test で回帰を防ぐ。
- playlist custom folder output は `.lr2folder` file と LR2 `folder` row を同じ projection から更新するため、
  LR2 full generation backfill 実行中は playlist 側の `.lr2folder` sync 境界でも mutation guard を通す。
  `folder` row sync が失敗した場合は、既存の playlist warning 表示 semantics は維持しつつ、durable full generation
  status を `Incomplete(lr2_playlist_lr2folder_sync_failed)` にする。
- `.lr2folder` DB sync は explicit `FolderType = 3/4/6` を保存できる。
  Source classifier は `newsong.lr2folder` と `course1-3.lr2folder` の固定 mapping 以外では special type を推測しない。
