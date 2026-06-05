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
  - 完全生成が有効で、LR2 `song.db` 内の完全生成 status が completed でない場合だけ backfill が発生する。
  - 既存 LR2 DB に必要列が欠けている、完全生成を初回有効化した、status signature が変わった、
    前回 run が incomplete / failed / cancelled の場合は backfill needed とする。
  - backfill が必要な場合は警告・進捗・キャンセル可能性を UI / log に出す。
  - 初回 backfill は startup ready / operable を待たせず、`startup_initialization_complete` 後の
    background workflow として開始する。
  - 完全生成設定を OFF から ON に変更した場合も、保存後に同じ background workflow を queue する。
  - BeMusicSeeker からの LR2 起動導線で backfill 完了待ちや起動 block は行わない。
    LR2 が再走査する可能性は完全生成 status の警告として表示する。
- 段階実装中は、Phase 8 / Phase 9 まで完了するまでは完全生成設定を hidden / disabled にする。
  - `song` row だけ新形式になり、対応する `folder` row 生成 scope / backfill が未整備な中間状態を
    ユーザー環境で有効化しない。

## 設定方針

完全な `song.db` 生成は設定化する。ただし LR2 連携ユーザーにとって望ましい既定動作なので、
最終仕様では LR2 連携モードで **デフォルト有効** にする。
段階実装中は Phase 8 / Phase 9 の status / backfill UI が入るまで hidden setting として扱い、
実装上の既定値は `false` のままにする。

この計画でいう LR2 連携モードは、設定上 `OperationModeLR2DB == true` であり、LR2 の
database / executable path が有効に解決できる状態を指す。

推奨 UI:

- 設定画面の「動作モード」内、「LR2と連携する」の近くに置く。
- 表示名例:
  - `LR2用song.dbをBeMusicSeekerで完全生成する`
  - または `LR2起動時のDB自動更新をBeMusicSeekerで代替する`
- 初期値:
  - 最終仕様では `OperationModeLR2DB == true` の場合は有効。
  - Phase 8 / Phase 9 までの段階実装では hidden / disabled で、`Settings` / `app.config` の既定値は `false`。
  - スタンドアロン運用では無効にし、設定項目も非表示にする。

設定別の処理方針:

- 完全生成が無効:
  - 現行に近い軽量スキャンを維持する。
  - chart / audio / image / movie の列挙でよい。
  - `song.txt`、`.lr2folder`、`folderinfo.txt`、LR2 folder hierarchy のための追加列挙は行わない。
- 完全生成が有効:
  - BMS の変更検出に必要な `song.path` / `song.date` / hash 判定を行う。
  - BMS chart directory 直下の text group を列挙する。
  - `.lr2folder`、`folderinfo.txt`、LR2 built-in custom folder source を
    LR2 `folder` テーブル生成の入力として扱う。
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
- 現在の chart/resource scan surface は `.lr2folder` discovery を正本として持たない。
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
  その generation scope 内だけを upsert / prune する。
- generation scope は `path` を正本にする。
  - scope 内で同じ `path` が複数 source から出る場合は、LR2 built-in custom folder、
    `.lr2folder` file、`folderinfo.txt` 付き directory、通常 directory の順で 1 row に正規化する。
  - 既存 row が同じ `path` にあれば `adddate` など維持列を引き継ぐ。
- root / ancestor / normal folder row は、現在の LR2 BMS root と BMS chart directory set から
  deterministic に再生成する。
- LR2 互換 path として扱えない BMS は `song.folder` / `song.parent` を `NULL` にし、その BMS だけを
  根拠にした `folder` expected row は生成しない。同じ directory に LR2 互換 BMS がある場合は、
  その互換 BMS 由来の chart directory set として folder row を生成する。
- `.lr2folder` row は discovery source を分類するが、所有権として永続化しない。
  - `playlist_output_lr2folder`: BeMusicSeeker がプレイリスト出力として生成する `.lr2folder`。
    実ファイルは既存の出力設定・出力 directory convention で管理する。
  - `discovered_lr2folder`: LR2 BMS root、BeMusicSeeker custom folder 出力 base、
    LR2 built-in custom folder source から discovery した `.lr2folder`。
  - DB row はどちらも現在の discovery result から生成する派生 cache とし、消えた file の row は
    generation scope 内の path prune で削除する。実 `.lr2folder` ファイルは discovery では削除しない。
- `.lr2folder` discovery は、完全生成有効時だけ行う。
- `.lr2folder` discovery root は、LR2 BMS search root、BeMusicSeeker の custom folder 出力 base、
  LR2 built-in custom folder source から作る。
  - 通常出力先 `LR2CustomFolderOutputBaseDir`
  - ルート出力先 `LR2CustomFolderOutputBaseDirRootType`
  - LR2 executable directory 配下の `LR2files\CustomFolder`
  - LR2 executable directory 配下の `LR2files\Rival` など、OpenLR2 が起動時に明示処理する
    built-in folder source
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
- generation scope 外の既存 `folder` row は原則触らない。
- unknown root row または `date = 0` row が残り、LR2 startup scan 抑止に影響する場合は
  silent retention しない。初期実装では自動削除せず warning / diagnostic の対象にし、
  cleanup は明示操作として別導線で実行する。
  - cleanup 導線は Phase 8 の初期実装に含める。
  - cleanup は全 `folder` row 削除ではなく、diagnostic で列挙した startup-scan blocker row だけを
    確認後に transaction で削除する。
  - cleanup できない、または cleanup 後も blocker が残る場合は完全生成 status を warning にし、
    LR2 起動時に再走査が起き得る状態として表示する。

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
- `Completed` は generation scope の反映完了に加え、startup-scan blocker diagnostic が clean であることを
  条件にする。unknown root row や `date = 0` row が残る場合は `Incomplete` とし、status warning に出す。
- `Needed` / `Running` / `Incomplete` / `Failed` / `Cancelled` は完全生成 status に warning を出す。
- 完全生成設定値が欠落または不正な場合は、他の設定値と同じく既定値へ正規化し、設定値 warning は出さない。
- LR2 側の DB 自動更新設定が手動のみでない可能性がある場合は、設定画面または status info として注意を出す。
  - 現行 `LR2Config` wrapper には autoreload 判定 API がないため、実装時に config 要素名と値を確認する。
  - BeMusicSeeker 側から LR2 config を自動変更しない。
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

- `song.txt` と LR2 resource compatibility warning を、後追い全件 filesystem check なしで計算する。

text group:

- BMS chart directory 直下の任意 `.txt` 有無だけを扱い、特定ファイル名 `song.txt` には限定しない。
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
- directory mtime
- `folderinfo.txt`
- existing folder rows

性能条件:

- chart ごとに `Directory.GetLastWriteTime` / `folderinfo.txt` check を行わない。
- root / ancestor / BMS chart directory を unique directory set に dedupe してから metadata を取得する。
- directory metadata と `folderinfo.txt` existence は Everything / managed fallback で
  意味が揃うよう parity test を置く。
- `LR2CustomFolderOutputBaseDir` / `LR2CustomFolderOutputBaseDirRootType` は chart / resource scan の
  explicit root にはしない。ただし親 BMS root に内包される場合は subtree 除外しない。
- Everything query に exclude DSL は追加せず、root / extension / filename の組み合わせで
  chart / resource surface と directory metadata surface を分ける。

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
- generation scope から消えた row が prune される。
- generation scope 外の unknown row は削除されない。
- Phase 7 の `.lr2folder` source を追加しても prune 基盤を流用できる。

### Phase 7: `.lr2folder` DB 同期を追加する

目的:

- BeMusicSeeker が出力した `.lr2folder` と、LR2 BMS root 配下の任意 `.lr2folder` を
  LR2 scan に任せず `folder` table へ同期する。

対象:

- `playlist_output_lr2folder`: BeMusicSeeker が出力する `.lr2folder`。
- `discovered_lr2folder`: LR2 BMS root / custom folder 出力 base / LR2 built-in custom folder source
  から discovery した `.lr2folder`。
- discovery root には LR2 BMS search root、通常出力先 `LR2CustomFolderOutputBaseDir`、
  ルート出力先 `LR2CustomFolderOutputBaseDirRootType`、LR2 executable directory 配下の
  `LR2files\CustomFolder` / OpenLR2 が明示処理する built-in folder source を含める。
  - OpenLR2 で確認できる built-in custom folder source は `RANDOM/`, `favorite.lr2folder`,
    `TOP10.lr2folder`, `PLAYLEVEL/`, `CLEAR/`, `RANK/`, `ignore.lr2folder`,
    `INSANE01/`, `INSANE02/`, `course1.lr2folder`, `course2.lr2folder`, `course3.lr2folder`
    を初期対象にする。
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
- generation scope 外の unknown `.lr2folder` row は誤削除しない。
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
- startup-scan blocker diagnostic が clean でない場合は `Completed` にせず `Incomplete` にする。
- 設定値が欠落または不正な場合は既定値へ正規化する。設定値 warning は出さない。
- LR2 config の auto update 設定は config 要素名を確認して検出する。検出できない場合は
  「自動更新設定を確認できない」status info を出し、BeMusicSeeker 側からは config を自動変更しない。
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
- blocker diagnostic が残る場合は `Completed` にならない。
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

## 実装メモ

- Phase 0 の golden fixture は段階的に追加する。
  - まず既存 `Lr2SongFolderParentNormalizer` の CRC / CP932 encode contract を固定する。
  - byte length boundary、`folderinfo.txt`、`.lr2folder`、manual-only scan 対象の fixture は、
    `Lr2CompatibilityEvaluator` / `Lr2FolderRowGenerator` の導入 cycle で追加する。
  - LR2 root sentinel は `LR2CRC32("ROOT")` ではなく `LR2CRC32("ROOT\0") = e2977170` として扱う。
- Phase 1 は段階的に追加する。
  - まず file diff で既存 BMS の `song.date` / mtime mismatch を parse target に入れる。
  - MD5 が同じ場合は `song.date` の targeted update だけ行い、chart_info / maintenance は再生成しない。
  - MD5 が変わる場合は parsed row へ差し替え、`favorite` / `adddate` / `tag` は既存 row から維持する。
- Phase 2 は段階的に追加する。
  - まず `Lr2SongDbWriter` を導入し、既存 `song.path` row がある場合は `favorite` / `adddate` / `tag` を
    DB 上に残したまま generated columns だけを更新する。
  - `txt` / text group は Phase 3 で正本を設計してから扱うため、この段階では従来どおり generated row 側の値を保存する。
  - `song.hash` が `NULL` の既存 row も existing row として扱い、hash 取得結果だけで new row 判定しない。
- Phase 3 は段階的に追加する。
  - まず scan surface に BMS chart directory 直下の `.txt` 有無を追加し、`song.txt` へ反映する。
  - fixed native resource scan は維持し、`.txt` だけ Everything grouped query で補完する。
    grouped query が使えない場合に managed 全列挙へ落とすと起動コストが跳ねるため、この段階では text surface を空扱いにする。
  - `song.date` が一致していて BMS 本体 MD5 が同じ場合、`.txt` 増減は targeted `song.txt` update だけ行い、
    chart_info / maintenance は再生成しない。
  - raw resource reference は runtime-only の `ChartResourceReference` として BMS parser で保持する。
    `WAVfiles` / `BGAfiles` の normalized lookup set は既存 resource health / install estimation の正本として残し、
    LR2 warning 用 raw path は `ChartResourceSnapshot.ResourceReference.RawPath` へ別投影する。
  - `ChartResourceSnapshot.AudioReferences` / `VisualReferences` / `MovieReferences` は既存どおり unique lookup key の list とし、
    LR2 warning 用には `ResourceReferences` で valid raw directive を全件保持する。同一 lookup key に複数 raw directive があっても、
    resource health の count は増やさず、LR2 raw path evaluation では全 raw directive を見る。
  - optional image (`stagefile` / `banner` / `backbmp`) は既存 snapshot field から扱い、`#WAV` / `#BMP` raw reference collection へは混ぜない。
- Phase 4 は段階的に追加する。
  - まず `Lr2CompatibilityEvaluator` を fact-only service として導入し、schema / warning projection には接続しない。
  - path CRC は `Lr2SongFolderParentNormalizer` の既存 contract を再利用し、CP932 byte length と resource raw/resolved path fact だけを追加する。
  - legacy path length boundary は NUL 終端を除いた CP932 259 bytes を上限として扱う。
  - warning projection は `ResourceHealthWarningProjection` へ混ぜず、maintenance facts が評価済みの BMS row だけ
    `BMSFile.Warnings` の `Lr2Compatibility` category として差し替える。未評価 row は placeholder attach だけで既存 warning を消さない。
- Phase 5 は段階的に追加する。
  - まず `Lr2SongRowEnricher` を導入し、既存の `date` / `txt` / user columns preservation / folder-parent CRC 正規化を
    file diff parser と DB writer から同じ入口へ寄せる。
  - detailed parser / `chart_info` 由来 numeric columns は、同じ enricher に後続 cycle で接続する。
  - 初回接続では `chart_info` に正本がある `level` / `difficulty` / `maxbpm` / `minbpm` / `mode` /
    `longnote` / `random` / `karinotes` を反映する。
  - `song.judge` は LR2 の raw `#RANK` 値で、`chart_info.judge` は判定幅 percent なので写さない。
    `bga` / `exlevel` も現行 `chart_info` に直接の正本がないため、推測で埋めず、対応する parser fact を追加する cycle まで残す。
- Phase 6 は段階的に追加する。
  - まず DB 接続前の pure `Lr2FolderRowGenerator` を追加し、LR2 root / ancestor / chart directory から
    normal `folder` row と generation scope path set を作る contract を固定する。
  - `folderinfo.txt` は、この段階では caller が渡す metadata の `#TITLE` として扱い、file discovery / read は後続の
    scan surface cycle に残す。
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
  - generation scope plan の initialization / mutation workflow への接続は後続 cycle で行う。
  - directory metadata surface は `Lr2FolderDirectoryMetadataSnapshot` として分離する。入力は unique directory set と
    `folderinfo.txt` candidate path set で、mtime 取得・`folderinfo.txt #TITLE` parse・欠落/読み取り失敗 count をここで集約する。
    native bridge ABI へ directory mtime を急いで追加せず、initialization / mutation 側は Everything grouped query または managed fallback で
    得た `folderinfo.txt` file surface と、owned chart から dedupe した directory set をこの snapshot に渡す。
  - `Lr2NormalFolderDbSyncService` は normal directory folder row の production-shaped compose 層にする。
    既存 row 読み込み、metadata snapshot、normal row 生成、scope plan、DB writer 適用をまとめるが、initialization / mutation の
    呼び出し判断や feature gate は持たない。chunk-local な file diff commit へ folder prune を混ぜず、full scan 完了後に
    complete chart path set を渡す呼び出し側から使う。`AllowPrune` は complete scan と source generation の整合を確認した
    caller だけが立て、既定では upsert のみ行って stale row delete はしない。
  - initialization への最初の production 接続は hidden setting `EnableLR2SongDbFullGeneration=false` を既定にし、
    LR2 linked mode かつ設定が明示的に有効な場合だけ full file scan 完了後に normal folder sync を実行する。
    Phase 8 / Phase 9 の status / backfill UI が入るまで通常ユーザー経路からは有効化しない。
  - full scan 後の normal folder sync は file diff DB commit を flush した後に別 workflow として実行し、
    owned collection の `HasDbDiff` や UI refresh 判定には混ぜない。folder sync の生成 / upsert / delete / metadata 欠落は
    `song_tbl_file_check_breakdown` の LR2 normal folder metrics として追跡する。
  - full scan 境界では complete chart path set と `folderinfo.txt` scan surface を渡せるため `AllowPrune=true` とする。
    ただし compose service 側は CP932 非対応 chart path や metadata 欠落がある場合に stale normal row deletion を抑止する。
    `ChartScanExecutionResult.Success != true` の partial surface では normal folder sync 自体を skip し、`folderinfo.txt`
    surface 欠落や chart path 不完全性を stale row prune / title overwrite に使わない。
- Phase 7 は段階的に追加する。
  - まず `.lr2folder` 本文を `LR2SongDB.folder` row へ投影する pure `Lr2FolderFileProjection` を追加する。
    この層は `#TITLE` / `#SUBTITLE` / `#CATEGORY` / `#INFORMATION_A` / `#INFORMATION_B` /
    `#COMMAND` / `#TAG` / `#MAXTRACKS` / `#BANNER` / `#CUSTOMFOLDER` を parse する。
    `#TAG` と `#COMMAND` は LR2 互換の command alias として後勝ちにし、通常の `.lr2folder` row は `type = 2` を既定にする。
  - `.lr2folder` row の `path` はファイル path そのもの、normal directory row の `path` は末尾 separator 付き directory path として分離する。
    `Lr2FolderFileProjection` は existing row の `adddate` を維持し、新規 row だけ生成時刻を入れる。
    ただし LR2 executable directory 配下の built-in source (`LR2files\CustomFolder` / `LR2files\Rival`) は、
    既存 startup normalization と同じく LR2 root 相対 path (`LR2files\...`) を DB path として保持する。
    実ファイル読み取り用の file path と DB に保存する path は `Lr2FolderFileSyncItem.FilePath` /
    `DatabasePath` として分離する。
  - `#CUSTOMFOLDER` は parse fact として保持するが、BeMusicSeeker 生成 `.lr2folder` の必須条件にはしない。
    OpenLR2 の built-in / root 特殊 type は後続の discovered source 統合で explicit type を渡す。
  - source 分類は `Lr2FolderFileSourceClassifier` に閉じる。通常 BMS root / 通常 custom folder 出力 base は
    `type = 2` と directory parent hash、root custom folder 出力 base は `type = 2` と root parent hash を使う。
    LR2 built-in source は LR2 root 相対 path に変換し、source directory 直下の `.lr2folder` だけ root parent hash、
    入れ子の `.lr2folder` は相対 path の containing directory hash を使う。OpenLR2 の root 特殊 `type = 3/4/6` は、
    type の確定条件を fixture で固定する cycle まで推測で振らない。
  - normal folder sync は `type = 1` の scope だけを prune / upsert 対象にし、`type = 2` の `.lr2folder` row を
    上書き・削除しない境界を維持する。
  - `.lr2folder` DB sync は `Lr2FolderFileDbSyncService` に分離する。
    入力 item は current `.lr2folder` file projection、`ScopeDirectories` / `ScopePaths` は stale row prune の境界として扱う。
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
  LR2 built-in custom folder source (`LR2files\CustomFolder` / `LR2files\Rival`) も discovery root に含める。
  built-in source は LR2 root 相対 path で sync し、source 直下は root parent hash、入れ子は relative containing
  directory hash を使う。通常出力先 / BMS root 由来の `.lr2folder` は絶対 path と directory parent hash のまま扱う。
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
  direct install / path replacement 後は、同じ owned mutation 成功 path で normal `folder.type = 1` row も
  `Lr2NormalFolderDbSyncService` に渡して upsert-only sync する。対象は追加/移動された BMS の新 path とし、
  bmson は `song` / `folder` の対象にしない。mutation 中は complete chart path surface ではないため
  `AllowPrune=false` とし、stale normal folder row の削除は full scan / backfill に任せる。
  この best-effort sync が失敗しても owned mutation は成功扱いにし、`lr2_full_generation_status` を
  `Incomplete(lr2_normal_folder_mutation_sync_failed)` にして次回 backfill で修復できるようにする。
  `folder` row の path replacement / startup normalization でも parent CRC は `Lr2SongFolderParentNormalizer.ComputeDirectoryHash`
  を使い、`LR2CRC32` の直接呼び出しを通常 mutation / initialization surface に増やさない。
  完了直前の source staleness check は、service に caller-provided predicate を渡す形にし、`BMSLibrary` 側で
  owned collection / storage row version と root / `.lr2folder` / `folderinfo.txt` / text group surface を開始時入力と
  再比較する。stale の場合は `Completed` にせず `Incomplete(source_stale_detected)` とする。
  sync 後は startup-scan blocker diagnostic を実行し、root set 欠落、current song row 欠落、
  `song.date` 欠落 / `0`、known root 外 song row が無い場合だけ `Completed` を記録する。
  blocker が残る場合は `Incomplete` (`startup_scan_blockers_detected`) とし、diagnostic count を log / status
  detail に残す。初期実装では blocker row の自動削除は行わず、cleanup 導線は後続 cycle に残す。
  status signature は normalized / deduplicated / case-insensitive な LR2 BMS root set を含める。
  加えて `.lr2folder` discovery root set も含める。root set が変わった場合は既存 `Completed` を信用せず、
  backfill needed として再評価する。
  signature には app schema / chart_info schema / chart_info parser / song-folder generator /
  `.lr2folder` parser / LR2 compatibility fact の version も含める。各 component の生成意味が変わった場合は
  対応 version を bump し、既存 `Completed` を再評価対象にする。

## 作業エージェント向け実装順

1. Phase 0 の golden fixture を追加する。
2. BMS 変更検出を `song.path` / `song.date` / hash ベースに統一する。
3. `song` row merge / ownership writer を追加する。
4. text group と raw resource reference snapshot を scan/parse contract に追加する。
5. `Lr2CompatibilityEvaluator` を追加する。
6. `maintenance` に LR2 compatibility warning 用の列を追加し、schema preflight に組み込む。
7. `Lr2CompatibilityWarningProjection` を追加する。
8. `Lr2SongRowEnricher` を追加し、BMS file diff path に組み込む。
9. `Lr2FolderRowGenerator` と folder generation scope を追加する。
10. `.lr2folder` parser、discovery、generation scope 内の `folder` row sync を追加する。
11. 完全生成 status と backfill progress / warning を追加する。
12. migration / backfill を追加する。
13. コピーした `song.db` で統合確認する。
14. LR2 を manual-only で起動し、未変更 root で再帰スキャンに入らないことを確認する。

## 実装メモ

- `Lr2SongDbWriter.UpsertGeneratedSong(...)` は direct install / path replacement / backfill の共通 persistence 境界として扱う。
  呼び出し元の `BMSFile` に `date` が無い場合は実ファイル mtime から補完し、新規 `song` row で `adddate`
  が無い場合だけ現在時刻を入れる。既存 row の `adddate` は writer update では変更しない。
- install package inline `chart_info` build は、parse / current row skip で得た `chart_info` row を BMS storage owner
  に反映してから `Lr2SongDbWriter` へ渡す。これにより direct install 直後の `song` row も、backfill を待たずに
  `level` / `difficulty` / BPM / `mode` / `longnote` / `random` / `karinotes` を持つ。
- playlist custom folder output workflow は `.lr2folder` file を出力した同じ操作内で `folder` row も sync する。
  ルートフォルダ出力 (`is_root_folder`) では playlist workflow 側でも `Lr2FolderFileSourceClassifier` を通し、
  generated `.lr2folder` row の parent を `ROOT` に揃える。
- playlist entry level の LR2 `song.level` writeback は、complete row writer ではなく targeted `UPDATE song SET level`
  を使う。既存 `song` row が無い path には不完全 row を作らず、既存 row の user / generated columns も触らない。
- LR2 full generation backfill の進捗は、durable status table と log に加えて `BMSLibrary` の bindable property
  (`Running` / requested-completed version / total / processed / stage) にも投影する。service 側は progress callback を
  観測専用として扱い、callback 失敗で durable backfill を失敗させない。
- startup progress では LR2 full generation backfill を startup / full reinitialize の background phase として扱う。
  request が来た場合だけ `[processed/total] LR2 song.db 完全生成 <stage>` を表示し、request 前に skip された場合は
  post-startup warmup 由来の遅い request で進捗を巻き戻さない。
- 完全生成設定は設定ダイアログの LR2 連携項目として binding / resource / 保存後 queue だけ先に配線し、
  mutation blocking と status warning 表示が入るまでは UI 上 `Collapsed` の hidden setting とする。
  既定値は段階実装中の安全側として `false` のままにし、OFF から ON に変更して保存した場合は
  `QueueLr2FullGenerationBackfillIfNeeded("SettingDialog.SaveSettings")` を呼んで同じ background workflow に流す。
