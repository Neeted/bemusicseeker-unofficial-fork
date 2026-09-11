# LR2 song.db Generation

この資料は、LR2 連携モードで BeMusicSeeker が生成・同期する LR2 `song.db` の現行仕様です。
実装計画、調査履歴、完了済み作業の記録は含めません。

## 目的

LR2 連携モードでは、BeMusicSeeker が LR2 `song.db` 内の `song` / `folder` と LR2 互換性情報を管理します。
LR2 側の起動時自動更新に依存せず、BeMusicSeeker のライブラリ、LR2 設定、プレイリスト出力、実ファイル状態から LR2 が選曲できる DB を生成することを目的とします。

LR2 の `song` / `folder` は外部編集を保存する正本ではなく、現在の入力から再生成できる cache として扱います。
full reconciliation では `folder` table 全体を完全な入力から投影し、アプリ生成 row を一度の transaction で置き換えます。
一方、`song` table の membership（追加・削除・stale prune）は file-diff の所有であり、full reconciliation は membership を変更しません。
LR2 ユーザー操作に属する `favorite` / `adddate` / `tag` などの値は明示的な維持対象です。

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
BeMusicSeeker が管理する現在の BMS chart path は file-diff が管理する membership です。
full reconciliation は既存 `song` row の membership を追加・削除せず、file-diff が確定した row に対して生成列だけを更新します。
stale row の追加・削除・prune と app-owned maintenance / chart digest の整理は、既存の file-diff / scoped incremental route が担当します。

`song.path` は原則として実 chart path です。DB 行の同一性と収束の正本は [path-identity.md](path-identity.md) です。
file-diff は対象範囲の信頼できる scan 入力を exact path で照合し、現在の path の行を維持・追加・更新し、含まれない旧行をその旧 exact key で削除します。case-only の差分も、それ以外の path 差分も同じ規則です。NOCASE 一致や同じ実ファイルへの解決だけで current と判定したり、旧行の key を単純に書き換えて既存の現在行と統合したりしません。`bmson_song` と関連 `maintenance` にも同じ行 identity を使いますが、BMSON を LR2 出力へ含める意味ではありません。
保存値の relink は行集合の収束とは別です。BMS の一対一・同一 MD5 relink を case-only にも統一する方針と、現行実装に残る除外は [path identity の relink 規則](path-identity.md#relink-policy) に従います。採用済みの統一化は未実装であり、既存の現在行の保存値保護や maintenance の再評価を外しません。
`song.folder` / `song.parent` は path 表記に依存する LR2 CRC32 なので、現在の BMS path から再計算した値へ更新します。
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
full reconciliation では BeMusicSeeker が現在の生成入力から期待集合を作り、全 table を置き換えます。
playlist/settings/catalog の incremental action では、既存の owner が明示した同期 scope 内だけ stale row を削除します。

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
full reconciliation はすべての source と必要な親/root row を先に投影し、完全な preflight が成功した後でだけ既存 table を一度の transaction で delete/upsert します。
入力 discovery、metadata、parse のいずれかが不完全、または同じ優先度の異なる projection が衝突した場合は、folder table を変更せず `Incomplete` / `Failed` として表面化します。
同一 path・同一 tier の同一 projection は順序によらず deduplicate します。

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
full reconciliation では `.lr2folder` の mtime が既存 row と一致していても本文を再parseします。mtime 一致を理由に full projection を preserve する shortcut はありません。
playlist/settings の通常 incremental action は、既存の scoped folder DB synchronization を引き続き使用します。full preparation の playlist/built-in materialization は physical file の準備・検証だけを行います。

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

`Completed` は、完全な full input の projection、folder table の atomic apply、file-diff が所有する song membership に対する生成列更新、source-current check、status commit がすべて成功したことを表します。
steady-state 起動では、`Completed` かつ signature が一致していれば full sync を行いません。
旧 schema や過去バージョン由来 row の追加検査を startup tail に混ぜて status を再評価しません。

この status は `chart_info` row の現存・完全性・currentnessを表しません。chart-info hydrationはLR2 linkedでもstandaloneでも実在する`chart_info`、current parse failure、owned chartを照合し、`Completed` statusをskip条件に使いません。statusのscopeとchart-info lifecycleの境界は [chart-info-lifecycle.md](chart-info-lifecycle.md) を正本とします。

root 変更、custom folder 出力設定変更、playlist 出力変更、file diff、前回 incomplete / failed / cancelled などは同期必要判定または scoped sync の入力です。
自動同期は、主にこの同期をまだ完了していない `song.db` を現在の生成入力へ収束させる一度限りの処理です。steady-state では上記の `Completed` + signature 判定で省略できるため、日常的な起動進捗には含めず、起動完了後の専用 status として扱います。すべての非 `Completed` state と manual force は item zero から開始し、durable cursor から resume しません。同じ理由から通常のユーザーキャンセル経路は設けず、失敗・未完了は既存の retry へ、アプリ終了は内部 shutdown cancellation へ分けます。
通常起動の自動同期は、正常な `startup_initialization_complete` が記録された後にだけ既存 scheduler へ一度 queue します。初回完了ダイアログが pending の場合も、同期を queue してから従来の表示処理へ進み、ダイアログの終了を同期開始の条件にしません。起動失敗・中断では自動 queue を作りません。同じ Startup generation では in-memory の scheduling guard により重複 queue を作らず、既存の `Completed` + signature gate が不要な full sync を抑止します。
LR2 同期の実行中にユーザーが status bar からキャンセルする操作と workflow route は提供しません。アプリ終了時の内部 shutdown drain は既存の cancellation token と `BMSLibrary` の終了処理を使い、安全に status を保存できる場合は `Incomplete` / `shutdown_interrupted` を記録します。過去 DB の `Cancelled` 値は読み取り・表示・retry 判定の互換性のため保持しますが、新しい production run は `Cancelled` row を作りません。shutdown 以外の cancellation は failure として表面化します。
自動同期の `Running` / 進捗 / `Incomplete` / `Failed` は `OperationProgressHub` の LR2 専用 status として UI / log に出し、startup progress の phase、分母、値、成功・失敗には参加させません。同期中は read-only 操作を許容し、DB mutation を伴う操作は制限します。
full sync の主要 stage は、normal folder、`.lr2folder` file、folder preflight/apply、song generated-column update、source-current check、completed の順です。diagnostic/repair/cleanup stage は存在しません。

folder table の full reconciliation は、完全な projection を作り、既存 row の表示用列を一行ずつ merge/preparation しながら、whole-table transaction を開始する前に runtime-only の stage progress を公開できます。この stage の分母は反復開始前に確定した projection row 数であり、各値は当該 row の pre-transaction preparation 後に公開され、その分母を超えず単調に進みます。atomic apply が戻るまで durable `processed_cursor` は進めません。projection row が空の場合は正の分母を公開せず、入力の全体数を事前に確定できない場合も determinate な値を作りません。progress observer の例外は同期結果、DB commit、failure/cancellation の表面化を変更しません。UI scheduler が status 通知を遅延させる場合も、runtime-only owner は最初の strict な folder-reconciliation frame だけを一時保持し、最初の status 通知でその一枚だけを公開した後、後続の UI turn で最新 status を一度だけ通知します。失敗・中断 status は保持 frame を置き換え、古い進捗を端末表示へ漏らしません。

`song_rows` stage は開始時に current parser version の `chart_info` resolver と timeout-aware current parse-failure MD5 set を取得します。各 worker は、song generated columns に使う既存の `ChartFileSnapshot` とこの事前取得 facts を route-neutral chart-info evaluator へ渡します。worker 内で譜面を追加読取したり、`chart_info` / parse failure を DB query したりしません。missing / stale `chart_info` は LR2 専用 parser ではなく inline / full backfill と同じ evaluator で生成します。

## 失敗と退役した startup route

full preparation が不完全、projection が衝突、folder apply または song generated-column update が失敗した場合は、部分的な成功や broad fallback を作らず `Incomplete` / `Failed` として表面化します。入力が完全になるまで folder table は変更しません。

startup scan diagnostic、missing/unknown-root inference、date sentinel 判定、startup repair、startup cleanup/retry action は現行 route ではありません。`folder.date`、`song.date` の zero および負値は有効な Unix 秒値としてそのまま保存します。

file-diff の直後に実行される startup/reload full sync には、直前の file-diff pipeline が全体として commit した chart path だけを対象にした in-memory committed-path receipt を一度だけ渡せます。receipt は `BmsRowsVersion` を transitional な narrow guard として一致確認し、`OwnedChartCollectionVersion` には依存しません。消費・失敗・retry・manual/settings run・shutdown・dispose のいずれでも保持・再利用・永続化しません。eligible path は BMS reader 呼び出しと DB currentness/verifier query を省略します。
scan surface の選択・currentness は、`ScanSurfaceGeneration`、BMS/BMSON storage-row version、BMS root、LR2 folder discovery root を guard とします。`OwnedChartCollectionVersion` は scan surface や receipt の validity dependency ではありませんが、runtime-wide な input currentness 判定では引き続き stale input を拒否するために使います。

## 性能方針

- root 追加 UI では再帰 file 探索を事前実行しません。
- file system 探索は通常の file diff / root enumeration 基盤に集約します。
- file-diff が所有する `song` membership の upsert と stale prune は chunk、temporary table、index を使って処理します。full reconciliation の song stage は membership を変更しません。
- directory hash は cache して同一 directory の再計算を避けます。
- full mode では既存 `.lr2folder` row の date、type、parent が一致しても本文を再parseします。mtime は full input の変化検出や parse の省略根拠ではありません。
- `.lr2folder` discovery が不完全な場合は、不完全な入力を隠して broad prune しません。
- full folder preparation は物理 file の materialization/verification と projection 構築を分離します。playlist/settings/catalog の scoped incremental folder DB synchronization は引き続き有効です。
- full reconciliation は full folder projection を一度だけ whole-table transaction へ渡し、song membership を追加・削除・stale prune しません。
- sync 失敗時は `Incomplete` / `Failed` などとして表面化し、成功扱いにしません。

## 主な実装

- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbSyncService.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongDbWriter.cs`
- `BeMusicSeeker/Models/BmsLibraryInternal/Lr2SongFolderParentNormalizer.cs`
- `BeMusicSeeker/Models/LR2/LR2Config.cs`
- `BeMusicSeeker/ViewModels/MainWindowViewModel.cs`

## Verification map

- `LR2-FDR-06-02` no-scan candidate composition: `Lr2SongDbSyncInputBuilderTests`
- `LR2-FDR-06-01` durable prepared-row/status behavior: `BmsLibraryLr2SongDbSyncTests`

## 関連仕様

- [data-and-indexes.md](data-and-indexes.md)
- [startup-initialization-flow.md](startup-initialization-flow.md)
- [settings-change-impact-and-startup-operations.md](settings-change-impact-and-startup-operations.md)
- [playlist-data-and-export-flow.md](playlist-data-and-export-flow.md)
- [warning-model.md](warning-model.md)
