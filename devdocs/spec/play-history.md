# プレイログ現行仕様

この資料は、BeMusicSeeker のプレイログ機能の現行仕様をまとめる。実装履歴ではなく、現在の挙動と守るべき意味を正本として書く。

## 目的

プレイログは BMS player の play history / update history を BeMusicSeeker 上で参照するための read model である。LR2 provider は LR2 linked profile の LR2 `score.db` を source とし、設定画面で導入した trigger が導入後の score 更新を記録する。beatoraja provider は既存 beatoraja 設定の選択 player directory にある `scorelog.db` を update history、`score.db.player` を期間 summary source として read-only で参照する。beatoraja 側へ trigger は導入しない。

UI は現在の score source から単一 provider を選択する。`UseBeatorajaScoreDb` が有効で、選択 player の beatoraja score が実際に読み込まれている場合は beatoraja provider を使う。それ以外では LR2 provider を使う。LR2 と beatoraja の履歴を同じ view で合算しない。

次のものは現行実装の scope 外である。

- 導入前 LR2 score からの backfill。LR2 `score` row には「最後にいつプレイしたか」を復元できる十分な情報が無いため、導入済み score を last play として補完しない。

## LR2 Schema

LR2 provider は LR2 linked profile 専用で動く。stand-alone mode など LR2 linked profile ではない場合は schema status を `SkippedProfile` として扱い、trigger install / read は有効化しない。LR2 linked profile で score DB path が未設定または file 不在の場合は `Unreadable` として扱う。LAST PLAY SORT materialization は schema status に関わらず固定 SQL projection を出力する。

`Lr2PlayHistorySchemaService` は LR2 `score.db` に次の app-owned objects を導入または検査する。

| Object | 用途 |
| --- | --- |
| `bms_lr2_play_history` | 1 play / 1 score write 相当の履歴本体。score 更新前後の best 値、player 集計差分、finalize 状態を保持する。 |
| `bms_lr2_last_play` | chart hash ごとの最終 play 時刻。LAST PLAY SORT custom folder の sort source。 |
| `bms_lr2_play_pending` | `score` trigger と `player` trigger の間で同一 play の pending row をつなぐための一時 state。 |
| `idx_bms_lr2_play_history_hash_time` | hash 別履歴 read 用 index。 |
| `idx_bms_lr2_play_history_time` | 期間 read / archive period index 用 index。 |
| `bms_lr2_score_history_after_insert` / `bms_lr2_score_history_after_update_playcount` | `score` insert / update を履歴 row として記録する。 |
| `bms_lr2_player_history_after_update` / `bms_lr2_player_history_cleanup_stale_pending` | `player` 更新で play 実績差分を finalize し、pending state を掃除する。 |

schema check は missing object、missing column、incompatible column、index / trigger mismatch、reserved object name collision を区別する。status は主に次の意味を持つ。

| Status | 意味 |
| --- | --- |
| `Installed` | 必要 objects が揃っている。read と LR2 / OpenLR2 側での LAST PLAY SORT 利用が可能。 |
| `NotInstalled` | 導入対象 objects がまだ無い。UI は install を提示する。 |
| `Repairable` | repair 可能な missing / mismatched object がある。UI は repair を提示する。 |
| `ManualRepairRequired` | 互換性のない column や object name collision など、自動 repair すべきでない状態。 |
| `Unreadable` | score DB を開けない、または検査できない。 |
| `SkippedProfile` | LR2 linked profile ではない。 |

`InstallOrRepair` は `NotInstalled` と `Repairable` にだけ書き込みを行う。`Installed` / `SkippedProfile` / `Unreadable` / `ManualRepairRequired` は意味を変えずに返す。

## LR2 Read Model

`Lr2PlayHistoryReader` は active LR2 linked profile の score DB path を read-only source として扱う。`ActiveScoreSource` snapshot ではなく、現在の LR2 linked profile が指す score DB path を読む。

read の入力は `Lr2PlayHistoryReadRequest` である。

| Field | 意味 |
| --- | --- |
| `ScoreDbPath` | LR2 score DB path。 |
| `IsLr2LinkedProfile` | LR2 linked profile として扱うか。false の場合は skipped。 |
| `PlayedAtFromInclusive` / `PlayedAtToExclusive` | `played_at` の half-open range。 |
| `FinalizationFilter` | LR2 row の確定状態 filter。通常期間は `FinalizedOnly`、Diagnostics node は `UnfinalizedOnly`。 |
| `Limit` | 読み込み上限。UI の通常一覧は default limit を使い、archive period index には使わない。 |
| `DisableLimit` | UI の read cache 構築など、source 全体を snapshot する内部用途で既定上限を無効化する。 |

reader は schema check を先に行う。`Installed` 以外の read 可能でない status では rows を空にし、diagnostics に原因を入れる。`Repairable` は index 以外の repairable mismatch なら warning diagnostic を付けて read を試みるが、missing / mismatched index がある場合は performance boundary を守るため read / period index を止め、error diagnostic を返す。read result は source profile、raw rows、diagnostics、schema status を保持する。

`ReadPeriodIndex` は `bms_lr2_play_history.played_at` から archive tree 用の日別代表 timestamp を読む。これは通常一覧の row limit に巻き込まれない。reader 単体の period index は SQL で日単位に group 化できるが、UI では下記の read cache から finalized row の `played_at` を取り出し、local date ごとの最大 timestamp を in-memory で作る。

## beatoraja Read Model

`BeatorajaPlayHistoryReader` は beatoraja player directory の `scorelog.db` を read-only source として扱う。`scorelog` は best 更新時だけ追記される update log であり、全プレイ履歴ではない。通常 Chart 更新履歴として扱うのは `mode = 0` の row だけであり、course / grade などの aggregate row を通常 Chart 履歴に混ぜない。

beatoraja の `scoredatalog.db` は `sha256 + mode` を primary key とする最新プレイ詳細の保存先であり、プレイごとの append log ではない。現行 play history / update history projection では `scoredatalog.db` を source にしない。beatoraja row は SCORE / CLEAR / BP / COMBO などの best delta を表示する。BEST DJ / BEST RATE は `scorelog` の old / new score と、beatoraja score load 時に `score.db.score.notes` から作られた score snapshot の notes から計算する。PlayHistory reader は `score.db.score` を再読み込みせず、現在の best score / clear / combo / BP を履歴 row に混ぜない。今回プレイの actual result、option、単曲 playtime は持たない。

beatoraja には LR2 `finalized = 0` に相当する未確定 play history row がない。Diagnostics node の `UnfinalizedOnly` 要求では通常の update history row を代替表示せず、空 rows として扱う。

beatoraja provider の日時 index は `scorelog.date` を source とする。reader 単体では `MAX(scorelog.date)` を日別に集約できるが、UI では read cache から finalized 相当の通常 row の `played_at` を取り出し、local date ごとの最大 timestamp を in-memory で作る。`scorelog.db` が無い、または読めない場合、beatoraja provider の update history / archive index は未提供になり、`score.db` の `score.date` や `scoredatalog.date` へ意味を変えて fallback しない。

beatoraja の `score.db.player` は日別の累計 snapshot として読み、画面期間の summary にだけ使う。単曲 row へ play count / judge count / playtime を結び付けられる source ではないため、beatoraja row の actual result 系列は空欄のままにする。期間 summary の play count / judge count / playtime は、期間終了境界より前の最新 snapshot から期間開始境界より前の最新 snapshot を引いた値であり、`すべて` では最新 snapshot の累計値を使う。`未確定 / 診断` は LR2 専用診断のため beatoraja period summary は未対応として `-` を表示する。`score.db` または `player` table が読めない場合も、別の値へ意味を変えて fallback せず `-` を表示する。

## Read Cache

Play History view は画面遷移または期間選択で最初に provider / source path ごとの play history row を全件ロードし、アプリ起動中は `PlayHistoryReadCache` に保持する。以後の期間切り替え、Diagnostics node、archive tree、row limit、keyword search、sort、表示対象切り替えは原則として同じ in-memory snapshot から処理し、同一 source への DB select を繰り返さない。

cache 構築は `Lr2PlayHistoryReader` / `BeatorajaPlayHistoryReader` に `FinalizationFilter = All` と `DisableLimit = true` を渡して行う。SQL や row conversion は reader 側の既存実装を使い、cache は読み込まれた raw row に対して期間・確定状態・limit の in-memory filter だけを担当する。独自 SQL や別の row mapping は持たない。

cache key は LR2 では score DB path と LR2 linked profile 判定、beatoraja では score DB path、`scorelog.db` path、score snapshot version である。score reload、play history schema の導入 / 修復 / 削除、source path または provider の切り替えでは cache を破棄し、次回 Play History view 利用時に再ロードする。

LR2 schema status は reader が source を読むときに得た `Lr2PlayHistorySchemaCheckResult` を含む。設定ダイアログは表示時に DB check を自動実行しないが、Play History read で得た結果が現在の LR2 linked profile と一致する場合は表示状態へ共有する。未読の場合は未確認表示のままであり、導入 / 修復 / 削除ボタンの明示操作だけが操作直前の check を行う。

## Projection

`PlayHistoryRow` は provider 非依存の表示 row である。LR2 provider では `Lr2PlayHistoryRecord`、beatoraja provider では `BeatorajaPlayHistoryRecord` を `PlayHistoryProjectionIndex` で app-owned library / playlist 情報へ解決して作る。

UI refresh では raw rows がある場合に projection index を作る。projection index の作成が stale request / cancellation で中断された場合は表示更新を中止する。raw rows が無い場合や projection source が無い場合は `PlayHistoryProjectionIndex.Empty` で unresolved row と diagnostics を作れる。これは chart 解決を諦めて raw history を表示するための projection fallback であり、schema / table 未導入を成功扱いに変える fallback ではない。

主な列の意味は次の通り。

| Field | 意味 |
| --- | --- |
| `Provider` / `Source` / `SourcePath` | source provider と DB path。 |
| `HistoryId` / `SourceKey` | source 内の履歴 row identity。LR2 では `history_id`、beatoraja では `scorelog.rowid`。 |
| `PlayedAtUnix` / `PlayedAt` | play time。UI では local time として表示する。 |
| `Finalized` | player aggregate delta まで確定した row か。通常一覧では finalized row を扱う。 |
| `RawHash` / `Md5` / `Sha256` | source hash、resolved MD5、resolved SHA-256。LR2 source hash は MD5、beatoraja source hash は SHA-256。 |
| `ResolvedChart` / `ResolvedChartRef` | app library で解決できた chart identity。 |
| `HashKind` | chart / course / unknown の分類。現行 LR2 projection は chart 解決できなければ unknown。 |
| `Title` / `Artist` / `Path` | resolved chart から得た表示情報。未解決なら空。 |
| `FolderLabels` / `PlaylistNames` | playlist reference resolver と display target filter / FOLDER projection で得た所属表示。display target が `すべて` の場合は projection 時点の playlist symbol、playlist 選択時は playlist entry folder、target set 選択時は `org_symbol + level` を表示する。 |
| `Kind` | play history row の分類。score / bp / clear / combo は複合表示できる。`play` は他の分類がない場合だけ表示する。 |
| `BestClear` / `BestDjLevelText` / `BestRateText` / `BestExscore` / `BestBp` / `BestCombo` | best 更新があった場合の before / after 表示。初回 BP は値だけを表示する。 |
| `PlayExscore` / `Judges` / `PlaytimeSeconds` | finalized actual play delta から作る実プレイ結果。beatoraja update history row では空欄。 |
| `Option` / `OpHistory` | LR2 option snapshot / option history。beatoraja update history row では空欄。 |

空 hash の raw row や chart 解決できない row は失敗として捨てず、diagnostic または unresolved row として扱う。Play history view の context menu は chart row 用 menu を広く出さず、resolved MD5 がある row は BMS-IR、resolved SHA-256 がある row は Mocha / MinIR と hash copy、所持 chart に解決できる row は Explorer / 譜面ビューアを出す。unresolved row には chart 操作 menu を出さない。

LR2 `OP HISTORY` は `new_op_history & ~old_op_history` で新規に立った bit を名前表示する。`old_op_history & ~new_op_history` がある場合は `ASSIST off` のように消えた bit も遷移として表示する。

beatoraja clear は表示用に既存 clear text へ投影するが、raw record は provider 専用 record に保持する。beatoraja の `oldminbp = int.MaxValue` は未プレイ sentinel として null に正規化し、`2147483647 -> value` とは表示しない。

## Period Selection And UI

左 tree には `プレイログ` root があり、固定期間 node と archive node を持つ。固定期間は次の通り。

| Node | Range |
| --- | --- |
| `すべて` | range なし。 |
| `今日` | local midnight inclusive から翌 local midnight exclusive。 |
| `昨日` | 前日 local midnight inclusive から当日 local midnight exclusive。 |
| `最近 7 日` | 今日を含む 7 日間。 |
| `最近 30 日` | 今日を含む 30 日間。 |
| `未確定 / 診断` | LR2 `finalized = 0` の未確定 row と read / projection diagnostics を確認する diagnostics 用 node。確定済み row は一覧対象にしない。beatoraja provider では通常履歴を未確定 row として代替表示しない。 |

archive node は `年 > 月 > 日` の階層で、`ReadPeriodIndex` の結果から local date を作って降順に並べる。年 / 月 / 日 node の range も local time zone で計算した half-open range である。

view request には request id があり、古い非同期 refresh の結果が新しい selection を上書きしないよう stale request guard を持つ。Play History view では in-memory sort と column settings foundation を使い、row source は `PlayHistoryRow` として custom table view に渡す。

一覧ラベル・検索欄の下には Play History 専用 summary row を表示する。summary row は table row ではなく UI band であり、判定数、プレイ数、演奏時間、score / BP / combo / clear 更新数、ASSIST / EASY / NORMAL / HARD / FC の clear 更新内訳をカード風に並べる。beatoraja provider のときだけ EXH 内訳も表示する。更新種別と clear 更新内訳のカードはクリックで一覧フィルターとして選択できる。複数カード選択時はカード間を OR、keyword search とは AND で合成する。カードフィルターは keyword text / history へ書き込まず、選択状態はカード表示で示す。演奏時間は画面期間に対する値であり、keyword search、summary card filter、表示対象 / FOLDER 投影で一覧行数が変わっても追従しない。未対応または読み取り不可の場合は `-` を表示する。`PROVIDER` / `SOURCE` は内部診断用プロパティとして保持するが、ユーザー表示列にはしない。

`CLEAR` と `BEST DJ` は更新元、矢印、更新先を同一セル内で別色表示する。選択行や current cell では読みやすさを優先して選択用の単色前景にする。Play History の `CLEAR` は遷移表示を短く保つため、通常一覧の長い表記ではなく `NP`、`ASSIST`、`EASY`、`NORMAL`、`HARD`、`EXH`、`FC`、`PA` などの短縮形を使う。

LR2 native の `clear = 2` は `op_history` の EASY bit で表示用 clear を解決する。EASY bit が立っている場合は `EASY`、EASY bit がない場合は `ASSIST` として扱う。OpenLR2 では CONSTANT / H-RAN / SCATTER / autoscratch などで `clear = 2` へ降格される場合があるため、ASSIST bit 自体は clear type 判定には使わない。この基準は通常一覧、keyword search、Play History、clear type custom folder で同じ基準に揃える。

## Keyword Search

Play History view は通常検索欄に `GridKeywordSearchContext.PlayHistory` を使う。keyword search は LR2 DB read と projection が終わり、display target filter を適用した後、sort / view 適用の前に in-memory で `PlayHistoryRow` を絞り込む。`KeywordFilterUpdated` / display target 変更 / `SortUpdated` は同じ期間 request の read / projection result を再利用し、DB read と projection index build を繰り返さない。

## Sort

Play History view の sort state は通常一覧の `SortParameters` と共有しない。通常一覧で選択した列が Play History に持ち込まれることはなく、Play History のヘッダー操作は `PlayHistorySortParameters` だけを更新する。未指定時は `PlayedAt` 降順を既定とし、同時刻の行は `HistoryId` 降順で安定化する。ユーザー操作で sort できるのは Play History の列定義が持つ `SortMemberPath` だけであり、通常一覧にしか存在しない列は Play History の sort request として発生させない。

Play History context の field は次の通り。

| Field | 意味 |
| --- | --- |
| global token | title、artist、path、folder labels、playlist names、raw hash、SHA-256、type / kind、source、play date を横断検索する。 |
| `title:` / `artist:` / `path:` | resolved chart の表示情報。未解決 row では空。 |
| `folder:` | display target 適用後の `FolderLabels`。`すべて` では projection 時点の playlist symbol、playlist 選択時は playlist entry folder、target set では一致する難易度表 entry の `org_symbol + level`。`FOLDER: preset` では preset 外の row は空文字列として扱う。 |
| `playlist:` / `ref:` / `table:` | playlist reference の name 表示。 |
| `md5:` | resolved MD5。 |
| `hash:` | source raw hash。LR2 では MD5、beatoraja では SHA-256。 |
| `sha256:` | resolved SHA-256。未解決 row では空。 |
| `date:` | local play date。`yyyy-MM-dd` / `yyyy/MM/dd` / `yyyyMMdd` を exact match する。 |
| `year:` | local play year を exact match する。 |
| `month:` | local play month を exact match する。`M` / `MM` / `yyyy-M` / `yyyy-MM` / `yyyy/M` / `yyyy/MM` を受け付ける。 |
| `type:` / `kind:` | `Kind` と LR2 `score_write_type`。`type:` を主名、`kind:` を互換 alias とする。 |
| `clear:` | LR2 best clear の before / after 表示。`HC` など既存 clear alias を使える。 |
| `oldclear:` | LR2 best clear の before 表示。`HC` など既存 clear alias を使える。 |
| `newclear:` | LR2 best clear の after 表示。`HC` など既存 clear alias を使える。 |
| `finalized:` | `true` / `false`、`1` / `0`、`finalized` / `unfinalized` / `pending` を boolean として扱う。 |
| `source:` | provider display name と source path。 |

`date:` / `year:` / `month:` は通常検索では substring ではなく exact match で扱う。regex (`field:re:...`) を指定した場合だけ、表示用文字列表現に対する regex として扱う。

## Display Target

Play History view の右上 dropdown は表示対象を次の単位で切り替える。

| Target | 意味 |
| --- | --- |
| `すべて` | 期間内の play history row を全件対象にする。FOLDER は投影時に解決した playlist symbol の列挙を使い、表示補正のために playlist entries を同期ロードまたは全件走査しない。 |
| preset | 設定ダイアログの `プレイログ FOLDER 表示プリセット` で選んだ複数 playlist の entries と hash が一致する row だけを対象にする。FOLDER は各 playlist の `org_symbol + level` の列挙。 |
| `FOLDER: preset` | 期間選択で得た play history row は除外せず、FOLDER だけを指定 preset の `org_symbol + level` で投影する。preset に一致しない row の FOLDER は空欄にする。keyword search はこの FOLDER 投影後に適用する。 |
| playlist | 選択した playlist の entries と hash が一致する row だけを対象にする。FOLDER はその playlist entry の folder。 |

dropdown の表示順は `すべて`、preset、`FOLDER: preset`、playlist 単体である。preset は `Settings.Default.PlayHistoryDisplayTargetSetsJson` に JSON として保存し、設定ダイアログの Playlist tab で追加 / 編集 / 削除する。JSON は target set name と `PlaylistId` の reference 配列だけで構成する。存在しない `PlaylistId` は読み取り時に一致せず、次回編集・保存時に自然に除外される。playlist 正本、playlist entries、`playlist.last_update` は変更しない。

display target filter / FOLDER projection は projection 後、keyword search 前に適用する。target 変更では、同じ期間 request の projection result を再利用して target filter / FOLDER projection / keyword filter / sort だけを再適用する。対象 playlist entries の読み込みが必要な場合は playlist entries hydration を使うが、playlist reload / external sync / `.bmt` 再出力は起動しない。

Play History の表示対象 dropdown は playlist 正本から作る read model であり、playlist の追加・削除・リロードに対する `BMSTables` 変更通知から更新される。`BMSTables` 変更通知は playlist 正本の writer lock 中に発生し得るため、通知 handler 内で同期的に playlist reader lock を取り直してはならない。表示対象の再構築は UI Dispatcher へ遅延し、writer lock が解放された後の snapshot として行う。遅延中または再構築中に追加通知が来た場合は revision を進め、最新 revision を反映するまで再実行する。Play History 側の read model 更新は playlist 正本、playlist entries、LR2 custom folder、beatoraja `.bmt` 出力を変更しない。

## Diagnostics

play history diagnostics は provider / stage / severity / code / message / source path を持つ。現行 UI は summary diagnostic text と log に出す最小実装であり、専用 maintenance view への詳細表示は未実装である。

主要 code は次の分類で扱う。

| Code prefix | 意味 |
| --- | --- |
| `play_history_lr2_schema_*` | LR2 schema check / read boundary の問題。未導入、manual repair required、unreadable など。 |
| `play_history_projection_*` | raw history を app chart / playlist 表示へ投影するときの問題。 |

diagnostics は schema / table 未導入を成功扱いに変える fallback ではない。play history read / projection では schema が未導入または壊れている状態を diagnostic として扱い、履歴 table の意味を score / playcount などへ置き換えない。一方で LAST PLAY SORT custom folder materialization は schema status を検査せず、固定 SQL projection を出力する。projection 段階では、chart / playlist 解決に失敗しても raw history row を unresolved row として表示し、diagnostic を添えることがある。

## LAST PLAY SORT

LR2 provider では playlist custom folder 出力に `LastPlaySortFolder` を追加している。これは `.bmt` ではなく LR2 `.lr2folder` / LR2 `folder` row の projection である。

`LastPlaySortFolder` は `bms_lr2_last_play.last_play_at` を降順に見る。command は次の ORDER BY を使う。

```sql
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC,
         (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
```

`bms_lr2_last_play` に row がない chart は `NULL` sort として末尾へ回る。これは「履歴機能導入後にまだ観測していない」という意味であり、score / playcount / playlist update time へ意味を変えて fallback しない。

active LR2 linked profile の play history schema status に関わらず、playlist property / bulk edit の LastPlaySortFolder checkbox は通常の出力種別として操作できる。materialization は固定 SQL projection を出力し、schema 未導入時に `bms_lr2_last_play` へ fallback したり出力を抑止したりしない。schema 未導入環境では、LR2 / OpenLR2 側で `LAST PLAY SORT` が機能しない。

LAST PLAY SORT の `.lr2folder` 本文と LR2 `folder` row は固定 SQL projection である。LR2 で曲をプレイして `bms_lr2_last_play` が更新された後は、BeMusicSeeker が `.lr2folder` を再出力しなくても、LR2 / OpenLR2 が custom folder を開く時点の DB 内容で並び順が決まる。

## Settings And Manual Boundary

設定画面の `LR2と連携する` 配下に LR2 play history schema status と、状態別文言を持つ導入 / 修復の統合ボタンがある。install / repair は LR2 score DB へ table / index / trigger を追加するため、実行前に対象 DB を確認する必要がある。手動の状態確認ボタンは置かず、設定画面表示時の自動 schema check も行わない。schema status は startup / Play History read など score DB を読むタイミングで得た結果を表示に使い、未確認の場合は未確認表示とする。導入 / 修復 / 削除の明示操作では、操作直前の read-only check で状態を更新する。

LR2 play history schema の削除は `バックアップ > データのアンインストール` に置く。確認 dialog で対象 player score DB path を表示し、trigger のみ削除（今後の記録を停止し、既存履歴 table は保持）と、table も含めた削除（既存履歴も削除）を選択できる。

manual は日本語 `docs/manual.ja.md` と英語 `docs/manual.md` を同時更新する。現行 manual は Play History view の最小説明、archive period tree、LAST PLAY SORT の概略を持つ。実画面 screenshot 更新は未実施である。

## Out Of Scope / Planned

次は計画上の未実装範囲であり、この spec の現行仕様には含めない。

- maintenance / settings での detailed status view と schema missing / locked / read-only の表示確認。
