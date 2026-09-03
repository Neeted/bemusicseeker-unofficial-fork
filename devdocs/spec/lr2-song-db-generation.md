# LR2 song.db Generation

この資料は、LR2 連携モードで BeMusicSeeker が生成・同期する LR2 `song.db` の現行仕様です。
実装計画、調査履歴、完了済み作業の記録は含めません。

## 目的

LR2 連携モードでは、BeMusicSeeker が LR2 `song.db` 内の `song` / `folder` と LR2 互換性情報を管理します。
LR2 側の起動時自動更新に依存せず、BeMusicSeeker のライブラリ、LR2 設定、プレイリスト出力、実ファイル状態から LR2 が選曲できる DB を生成することを目的とします。

LR2 の `song` / `folder` は外部編集を保存する正本ではなく、現在の入力から再生成できる cache として扱います。
現在の入力から導けない stale / unknown row は、同期 workflow 内で削除または上書きして収束させます。
ただし、LR2 ユーザー操作に属する `favorite` / `adddate` / `tag` などの値は明示的な維持対象です。

## 適用範囲

- 対象は BMS 系 chart です。
- `bmson` は LR2 `song` / `folder` に出力しません。
- `bmson` の永続化は `bmson_song` など BeMusicSeeker 側の table が担当します。
- `bmson` には LR2 互換性警告を出しません。
- スタンドアロンモードでは LR2 `folder` row、`.lr2folder` discovery、`folderinfo.txt`、LR2 built-in custom folder hierarchy など LR2 専用の生成面を作りません。

## LR2 設定

LR2 連携モードでは、BeMusicSeeker が LR2 `config.xml` の `<autoreload>` を `0` に設定します。
これは LR2 SETUP の「データベース自動更新: 手動のみ」に対応します。

この設定は次のタイミングで保証します。

- 初回起動または設定選択で LR2 連携モードを使うとき。
- 設定ダイアログで LR2 連携モードの設定を保存するとき。
- LR2 連携モードで起動設定を読み込むとき。

BMS 検索 root は BeMusicSeeker の共通 root UI で管理します。
LR2 SETUP の JUKEBOX タブから root を直接追加する運用は推奨しません。
BeMusicSeeker 側で root を追加するときは、追加操作そのものでは `Directory.EnumerateFiles(..., AllDirectories)` のような再帰事前探索を行いません。
追加された root を含む通常のライブラリ差分検出・再同期 workflow に処理を寄せます。

LR2 `config.xml` の JUKEBOX root は、存在するディレクトリかつ Shift_JIS で表現できるパスだけを有効な入力として扱います。
同一 root、親子関係にある root、相対 path は正規化・拒否・保存更新の対象です。

## 入力

LR2 `song.db` 生成は、主に次の入力から期待 row を作ります。

- LR2 の BMS 検索 root。
- BeMusicSeeker の現在の BMS chart path。
- chart directory、親 directory、root 周辺の directory metadata。
- `folderinfo.txt`。
- BMS root 探索で発見されたアプリ管理外 `.lr2folder`。
- BeMusicSeeker がプレイリスト出力として生成する `.lr2folder`。
- LR2 executable directory 配下の `LR2files\CustomFolder`。
- LR2 custom folder 設定、title flash 時間など `config.xml` の system 設定。
- chart 解析結果、LR2 互換性警告、resource scan facts。

同期 request は、file diff や root enumeration の結果を受け取ります。
同期処理内で意味の変わる広範囲 fallback 探索は行わず、入力が不完全な場合は不完全として扱います。

LR2 linked mode では、LR2 の `<jukebox>` は LR2 に見せる root set として扱い、通常の BMS directory、通常 custom folder 出力先、追加通常出力先、root custom folder 出力 playlist directory を登録対象にします。一方、本アプリの chart / resource scan root は `<jukebox>` を入力にしつつ用途で絞り込みます。従来版で通常 custom folder 出力先に楽曲が同居していた可能性があるため、通常 custom folder 出力先は chart / resource scan root から除外しません。ただし通常 custom folder 出力先は楽曲インストール先としては扱わず、BMS directory 一覧、install destination 候補、library folder tree node からは除外します。追加通常出力先と root custom folder 出力先は BeMusicSeeker 管理の custom folder 出力領域として chart / resource scan root から除外し、`.lr2folder` discovery root としてだけ扱います。最終的な `<jukebox>` の BMS 検索 root 同士に同一・親子関係の重なりを作らないことを前提にし、親 BMS root 配下の一部 directory だけを chart / resource scan から除外する通常仕様は持ちません。手動編集などで重なりがある場合は設定保存時の validation で是正を促します。

## song Table

`song` table は BMS chart の LR2 互換 row を保持します。
BeMusicSeeker が管理する現在の BMS chart path が期待集合です。
期待集合に含まれない stale `song` row は同期中に削除され、関連する app-owned maintenance / chart digest 情報も整理されます。

`song.path` は原則として実 chart path です。
既存 row と file system 探索結果が case-insensitive には一致するが ordinal では一致しない場合は、探索結果の path 表記を正とし、`song.path`、`bmson_song.path`、関連 `maintenance.path` をその表記へ更新します。
この更新は case-only rename や旧 DB 由来の path 表記差を収束させるための key 更新であり、NOCASE 一致だけを current とは見なしません。
`song.folder` / `song.parent` は path 表記に依存する LR2 CRC32 なので、BMS の path 表記を更新するときは同じ入力から再計算した値へ更新します。
LR2 互換 path として扱えない BMS も、BeMusicSeeker の管理対象 BMS である限り `song` row からは削除しません。
その場合は `song.folder` / `song.parent` を `NULL` にし、LR2 互換性警告として扱います。

`song.date` は chart file の mtime を Unix seconds で保存します。
新規 row の `adddate` が未設定の場合は、同期時点の LR2 形式時刻を初期値にします。
`level` / `judge` / `date` / `folder` / `parent` など生成列は現在の生成結果で更新します。

`song.folder` / `song.parent` は LR2 CRC32 互換の hash です。
path 全体が Shift_JIS で表現できる場合だけ計算します。

- `song.folder`: chart directory path に末尾 `\` と NUL を付けた Shift_JIS byte sequence の CRC32。
- `song.parent`: chart directory の親 directory path に末尾 `\` と NUL を付けた Shift_JIS byte sequence の CRC32。
- Shift_JIS で表現できない path、または Windows path として解釈できない path は LR2 path encoding unsupported として扱います。

hash 計算は既存の `LR2CRC32` と `Lr2SongFolderParentNormalizer` を正本にします。

## folder Table

`folder` table は LR2 選曲画面の階層を表す cache です。
row ownership column は持たないため、BeMusicSeeker は現在の生成入力から期待集合を作り、同期 scope 内の stale row を削除します。

通常 directory row は `type = 1` です。
`folder` table には directory 自身の hash column はなく、`folder.path` が row key です。
directory path に末尾 `\` と NUL を付けた Shift_JIS byte sequence の CRC32 は、他 row から参照される hash として使います。
LR2 root 直下として扱う row の `folder.parent` は `ROOT` + NUL の CRC32 です。
root 配下の directory の `folder.parent` は実際の親 directory hash です。

`folder.date` は directory または `.lr2folder` file の mtime を Unix seconds で保存します。
LR2 の起動時 root folder check が再帰スキャンへ進まないよう、root row の date も実 directory mtime と一致させます。

通常 directory row の期待集合は、少なくとも次の directory から作ります。

- LR2 BMS 検索 root。
- BMS chart directory と、その root までの親 directory。
- `folderinfo.txt` を持つ directory。
- text group `.txt` の directory。
- `.lr2folder` の親階層として必要な directory。

同一 path が複数 source から生成候補になる場合は、LR2 表示上の意味が強い source を優先します。
優先度は built-in custom folder、`.lr2folder`、`folderinfo.txt` directory、通常 directory の順です。

## .lr2folder

`.lr2folder` row は通常 `type = 2` です。
現行実装で source 分類により特殊 type を付けるのは built-in custom folder です。
`newsong.lr2folder` は `type = 3`、`course1.lr2folder` / `course2.lr2folder` / `course3.lr2folder` は `type = 6` です。
それ以外の `.lr2folder` は、本文に `#COMMAND` / `#TAG` が含まれていても通常 `type = 2` として投影します。

`.lr2folder` の source は次の 3 種類です。

- BeMusicSeeker がプレイリスト出力として生成するアプリ管理 `.lr2folder`。
- LR2 BMS root 探索で自然に発見されたアプリ管理外 `.lr2folder`。
- `LR2files\CustomFolder` 配下の built-in custom folder。

アプリ管理外 `.lr2folder` は、実ファイルが置かれている directory の LR2 BMS root 階層に従って parent を決めます。
探索 root 自身だけが `ROOT` parent になり、配下の directory や `.lr2folder` を root 直下へ平坦化しません。

アプリ管理プレイリスト `.lr2folder` には通常出力先とルート出力先があります。
通常出力先は、指定された出力 base directory を LR2 BMS root のように扱います。
ルート出力先は、指定された root 出力 base directory 配下に playlist directory を作り、その directory を LR2 BMS root として扱います。
通常の numbered `.lr2folder` は playlist directory を parent に持ち、root 出力 base 直下の standalone `.lr2folder` だけが `ROOT` parent になります。
プレイリスト保存時の materialization は、`0000.lr2folder` からの連番 file を Shift_JIS で出力し、同じ projection から `folder` row も同期します。
ルート出力先を使う playlist は、root flag や出力先変更に合わせて LR2 `config.xml` の BMS search root 追加・削除対象にもなります。

## LR2 Built-in Custom Folder

LR2 executable directory 配下の `LR2files\CustomFolder` は built-in custom folder source です。
LR2 `config.xml` の `<customfolder>` bitmask に従って有効な folder を選びます。
bitmask は `RANDOM`、`favorite.lr2folder`、`TOP10.lr2folder`、`PLAYLEVEL`、`CLEAR`、`RANK`、`ignore.lr2folder`、`INSANE01`、`INSANE02` の有効化に使います。
course 系 folder は LR2 互換上常に扱います。
`newsong.lr2folder` は `titleflash` と `song.adddate` を入力として生成対象を決めます。
有効な built-in `.lr2folder` は LR2 root relative path として `folder.path` に保存します。

## 同期状態

LR2 `song.db` 同期状態は BeMusicSeeker 管理 metadata として保持します。
主な状態は `NotNeeded`、`Needed`、`Running`、`Completed`、`Failed`、`Cancelled`、`Incomplete` です。
metadata は `lr2_song_db_sync_status` の `name=default` row に保持し、status、signature、run id、processed cursor、total count、stage、error、updated/completed time を記録します。

`Completed` は、現在の生成入力と signature に対して期待 `song` / `folder` 集合へ収束したことを表します。
steady-state 起動では、`Completed` かつ signature が一致していれば full sync を行いません。
旧 schema や過去バージョン由来 row の追加検査を startup tail に混ぜて status を再評価しません。

この status は `chart_info` row の現存・完全性・currentnessを表しません。chart-info hydrationはLR2 linkedでもstandaloneでも実在する`chart_info`、current parse failure、owned chartを照合し、`Completed` statusをskip条件に使いません。statusのscopeとchart-info lifecycleの境界は [chart-info-lifecycle.md](chart-info-lifecycle.md) を正本とします。

root 変更、custom folder 出力設定変更、playlist 出力変更、file diff、前回 incomplete / failed / cancelled などは同期必要判定または scoped sync の入力です。
自動同期は、主にこの同期をまだ完了していない `song.db` を現在の生成入力へ収束させる一度限りの処理です。steady-state では上記の `Completed` + signature 判定で省略できるため、日常的な起動進捗には含めず、起動完了後の専用 status として扱います。同じ理由から通常のユーザーキャンセル経路は設けず、失敗・未完了は既存の retry へ、アプリ終了は内部 shutdown cancellation へ分けます。
通常起動の自動同期は、正常な `startup_initialization_complete` が記録された後にだけ既存 scheduler へ一度 queue します。初回完了ダイアログが pending の場合も、同期を queue してから従来の表示処理へ進み、ダイアログの終了を同期開始の条件にしません。起動失敗・中断では自動 queue を作りません。同じ Startup generation では in-memory の scheduling guard により重複 queue を作らず、既存の `Completed` + signature gate が不要な full sync を抑止します。
LR2 同期の実行中にユーザーが status bar からキャンセルする操作と workflow route は提供しません。アプリ終了時の内部 shutdown drain は既存の cancellation token と `BMSLibrary` の終了処理を使い、durable な `Cancelled` status を従来どおり保持します。
自動同期の `Running` / 進捗 / `Incomplete` / `Failed` は `OperationProgressHub` の LR2 専用 status として UI / log に出し、startup progress の phase、分母、値、成功・失敗には参加させません。同期中は read-only 操作を許容し、DB mutation を伴う操作は制限します。
full sync の主要 stage は、normal folder、`.lr2folder` file、song row、stale prune、diagnostic、completed の順です。

`song_rows` stage は開始時に current parser version の `chart_info` resolver と timeout-aware current parse-failure MD5 set を取得します。各 worker は、song generated columns に使う既存の `ChartFileSnapshot` とこの事前取得 facts を route-neutral chart-info evaluator へ渡します。worker 内で譜面を追加読取したり、`chart_info` / parse failure を DB query したりしません。missing / stale `chart_info` は LR2 専用 parser ではなく inline / full backfill と同じ evaluator で生成します。

## Startup Scan Diagnostic

startup scan diagnostic は、LR2 が起動時に再帰スキャンへ進みそうな `folder` row の欠落や stale date を検出・修復するための補助処理です。
これは `Completed` status の正否を毎回再評価する heavyweight validation ではありません。

diagnostic は期待される normal folder row、`.lr2folder` row、親 directory row、cleanup 対象 row、date update 対象 row を比較し、必要な場合だけ修復します。

## 性能方針

- root 追加 UI では再帰 file 探索を事前実行しません。
- file system 探索は通常の file diff / root enumeration 基盤に集約します。
- `song` upsert と stale prune は chunk、temporary table、index を使って処理します。
- directory hash は cache して同一 directory の再計算を避けます。
- 既存 `.lr2folder` row の date、type、parent が期待値と一致する場合は、本文 parse を省略して preserve できます。
- `.lr2folder` discovery が不完全な場合は、不完全な入力を隠して broad prune しません。
- sync 失敗時は `Incomplete` / `Failed` などとして表面化し、成功扱いにしません。

## 主な実装

- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbWriter.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongFolderParentNormalizer.cs`
- `BeMusicSeeker/Models/LR2/LR2Config.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`

## 関連仕様

- [data-and-indexes.md](data-and-indexes.md)
- [startup-initialization-flow.md](startup-initialization-flow.md)
- [settings-change-impact-and-startup-operations.md](settings-change-impact-and-startup-operations.md)
- [playlist-data-and-export-flow.md](playlist-data-and-export-flow.md)
- [warning-model.md](warning-model.md)
