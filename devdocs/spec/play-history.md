# プレイログ現行仕様

この資料は、BeMusicSeeker のプレイログ機能の現行仕様をまとめる。実装履歴ではなく、現在の挙動と守るべき意味を正本として書く。

## 目的

プレイログは BMS player の play history を BeMusicSeeker 上で参照するための read model である。LR2 provider は LR2 linked profile の LR2 `score.db` を source とし、設定画面で導入した trigger が導入後の score 更新を記録する。beatoraja provider は既存 beatoraja 設定の選択 player directory にある `scoredatalog.db` / `scorelog.db` を read-only で参照する。

UI は現在の score source から単一 provider を選択する。`UseBeatorajaScoreDb` が有効で、選択 player の beatoraja score が実際に読み込まれている場合は beatoraja provider を使う。それ以外では LR2 provider を使う。LR2 と beatoraja の履歴を同じ view で合算しない。

次のものは現行実装の scope 外である。

- beatoraja の provider 固有 aggregate。単曲 row の playtime は空欄であり、player aggregate から期間 summary へ足す処理は未実装。
- 導入前 LR2 score からの backfill。LR2 `score` row には「最後にいつプレイしたか」を復元できる十分な情報が無いため、導入済み score を last play として補完しない。

## LR2 Schema

LR2 provider は LR2 linked profile 専用で動く。stand-alone mode など LR2 linked profile ではない場合は schema status を `SkippedProfile` として扱い、trigger install / read / LAST PLAY SORT materialization は有効化しない。LR2 linked profile で score DB path が未設定または file 不在の場合は `Unreadable` として扱う。

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
| `Installed` | 必要 objects が揃っている。read と LAST PLAY SORT materialization を許可する。 |
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

reader は schema check を先に行う。`Installed` 以外の read 可能でない status では rows を空にし、diagnostics に原因を入れる。`Repairable` は index 以外の repairable mismatch なら warning diagnostic を付けて read を試みるが、missing / mismatched index がある場合は performance boundary を守るため read / period index を止め、error diagnostic を返す。read result は source profile、raw rows、diagnostics、schema status を保持する。

`ReadPeriodIndex` は `bms_lr2_play_history.played_at` から日単位の代表 timestamp を読む。これは archive tree 用であり、通常一覧の row limit に巻き込まれない。現行 SQL は `strftime(..., 'localtime')` で finalized 全履歴を日単位に group 化するため、真の index-only lookup ではなく O(履歴行数) の集計になり得る。ただし row 本体や projection index を作らないため、通常一覧の全 row projection より軽い境界として扱う。

## beatoraja Read Model

`BeatorajaPlayHistoryReader` は beatoraja player directory の `scoredatalog.db` を read-only source として扱う。通常 Chart 履歴として扱うのは `mode = 0` の row だけであり、course / grade などの aggregate row を通常 Chart 履歴に混ぜない。

`scorelog.db` が同じ player directory にある場合は、`sha256 + mode + date` が一致する `scorelog` row から best delta を補う。対応する `scorelog` row が無い場合、actual result は表示し、best delta 欄は空欄のままにする。

beatoraja には LR2 `finalized = 0` に相当する未確定 play history row がない。Diagnostics node の `UnfinalizedOnly` 要求では `scoredatalog.db` の通常履歴を代替表示せず、空 rows として扱う。

beatoraja provider の日時 index は `MAX(scoredatalog.date)` を日別に集約する。`scoredatalog.db` が無い、または読めない場合、beatoraja provider の last play / archive index は未提供になり、`score.db` の `score.date` へ意味を変えて fallback しない。

## Projection

`PlayHistoryRow` は provider 非依存の表示 row である。LR2 provider では `Lr2PlayHistoryRecord`、beatoraja provider では `BeatorajaPlayHistoryRecord` を `PlayHistoryProjectionIndex` で app-owned library / playlist 情報へ解決して作る。

UI refresh では raw rows がある場合に projection index を作る。projection index の作成が stale request / cancellation で中断された場合は表示更新を中止する。raw rows が無い場合や projection source が無い場合は `PlayHistoryProjectionIndex.Empty` で unresolved row と diagnostics を作れる。これは chart 解決を諦めて raw history を表示するための projection fallback であり、schema / table 未導入を成功扱いに変える fallback ではない。

主な列の意味は次の通り。

| Field | 意味 |
| --- | --- |
| `Provider` / `Source` / `SourcePath` | source provider と DB path。 |
| `HistoryId` / `SourceKey` | source 内の履歴 row identity。LR2 では `history_id`、beatoraja では `scoredatalog.rowid`。 |
| `PlayedAtUnix` / `PlayedAt` | play time。UI では local time として表示する。 |
| `Finalized` | player aggregate delta まで確定した row か。通常一覧では finalized row を扱う。 |
| `RawHash` / `Md5` / `Sha256` | source hash、resolved MD5、resolved SHA-256。LR2 source hash は MD5、beatoraja source hash は SHA-256。 |
| `ResolvedChart` / `ResolvedChartRef` | app library で解決できた chart identity。 |
| `HashKind` | chart / course / unknown の分類。現行 LR2 projection は chart 解決できなければ unknown。 |
| `Title` / `Artist` / `Path` | resolved chart から得た表示情報。未解決なら空。 |
| `FolderLabels` / `PlaylistNames` | playlist reference resolver と display target filter で得た所属表示。display target が `すべて` の場合は projection 時点の playlist symbol、playlist 選択時は playlist entry folder、target set 選択時は `org_symbol + level` を表示する。 |
| `Kind` | play history row の分類。score / bp / clear / combo は複合表示できる。`play` は他の分類がない場合だけ表示する。 |
| `BestClear` / `BestDjLevelText` / `BestRateText` / `BestExscore` / `BestBp` / `BestCombo` | best 更新があった場合の before / after 表示。初回 BP は値だけを表示する。 |
| `PlayExscore` / `Judges` / `PlaytimeSeconds` | finalized actual play delta から作る実プレイ結果。 |
| `Option` / `OpHistory` | LR2 option snapshot / option history、または beatoraja option の表示。 |

空 hash の raw row や chart 解決できない row は失敗として捨てず、diagnostic または unresolved row として扱う。Play history view の context menu は chart row 用 menu を広く出さず、resolved MD5 がある row は BMS-IR、repository SHA-256 がある row は Mocha / MinIR と hash copy、unresolved row は raw hash copy を中心にする。

LR2 `OP HISTORY` は `new_op_history & ~old_op_history` で新規に立った bit を名前表示する。`old_op_history & ~new_op_history` がある場合は `ASSIST off` のように消えた bit も遷移として表示する。

beatoraja row では raw `option` / `random` / `seed` を保存値として扱い、LR2 `op_best` と同じ意味へ丸めない。UI projection では `option = 1P + 2P * 10 + DP * 100` として RANDOM / MIRROR / FLIP / BATTLE AS などの短い文字列へ変換する。`random` / `seed` は通常の OPTION 表示には混ぜない。beatoraja clear は表示用に既存 clear text へ投影するが、raw record は provider 専用 record に保持する。beatoraja の `oldminbp = int.MaxValue` は未プレイ sentinel として null に正規化し、`2147483647 -> value` とは表示しない。

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

一覧ラベル・検索欄の下には Play History 専用 summary row を表示する。summary row は table row ではなく UI band であり、判定数、プレイ数、演奏時間、score / BP / combo / clear 更新数、ASSIST / EASY / NORMAL / HARD / FC の clear 更新内訳をカード風に並べる。beatoraja provider のときだけ EXH 内訳も表示する。`PROVIDER` / `SOURCE` は内部診断用プロパティとして保持するが、ユーザー表示列にはしない。

`CLEAR` と `BEST DJ` は更新元、矢印、更新先を同一セル内で別色表示する。選択行や current cell では読みやすさを優先して選択用の単色前景にする。Play History の `CLEAR` は遷移表示を短く保つため、通常一覧の長い表記ではなく `NO PLAY`、`ASSIST`、`EASY`、`NORMAL`、`HARD`、`EXH`、`FC`、`PA` などの短縮形を使う。

## Keyword Search

Play History view は通常検索欄に `GridKeywordSearchContext.PlayHistory` を使う。keyword search は LR2 DB read と projection が終わり、display target filter を適用した後、sort / view 適用の前に in-memory で `PlayHistoryRow` を絞り込む。`KeywordFilterUpdated` / display target 変更 / `SortUpdated` は同じ期間 request の read / projection result を再利用し、DB read と projection index build を繰り返さない。

Play History context の field は次の通り。

| Field | 意味 |
| --- | --- |
| global token | title、artist、path、folder labels、playlist names、raw hash、SHA-256、kind、source、play date を横断検索する。 |
| `title:` / `artist:` / `path:` | resolved chart の表示情報。未解決 row では空。 |
| `folder:` | display target 適用後の `FolderLabels`。`すべて` では projection 時点の playlist symbol、playlist 選択時は playlist entry folder、target set では一致する難易度表 entry の `org_symbol + level`。 |
| `playlist:` / `ref:` / `table:` | playlist reference の name 表示。 |
| `md5:` | resolved MD5。 |
| `hash:` | source raw hash。LR2 では MD5、beatoraja では SHA-256。 |
| `sha256:` | resolved SHA-256。未解決 row では空。 |
| `date:` | local play date。`yyyy-MM-dd` / `yyyy/MM/dd` / `yyyyMMdd` を exact match する。 |
| `year:` | local play year を exact match する。 |
| `month:` | local play month を exact match する。`M` / `MM` / `yyyy-M` / `yyyy-MM` / `yyyy/M` / `yyyy/MM` を受け付ける。 |
| `kind:` | `Kind` と LR2 `score_write_type`。 |
| `clear:` | LR2 best clear の before / after 表示。`HC` など既存 clear alias を使える。 |
| `finalized:` | `true` / `false`、`1` / `0`、`finalized` / `unfinalized` / `pending` を boolean として扱う。 |
| `source:` | provider display name と source path。 |

`date:` / `year:` / `month:` は通常検索では substring ではなく exact match で扱う。regex (`field:re:...`) を指定した場合だけ、表示用文字列表現に対する regex として扱う。

## Display Target

Play History view の右上 dropdown は表示対象を次の単位で切り替える。

| Target | 意味 |
| --- | --- |
| `すべて` | 期間内の play history row を全件対象にする。FOLDER は投影時に解決した playlist symbol の列挙を使い、表示補正のために playlist entries を同期ロードまたは全件走査しない。 |
| playlist | 選択した playlist の entries と hash が一致する row だけを対象にする。FOLDER はその playlist entry の folder。 |
| target set | settings JSON に保存された playlist / folder reference 集合に一致する row だけを対象にする。FOLDER は `org_symbol + level` の列挙。 |

target set は `Settings.Default.PlayHistoryDisplayTargetSetsJson` に JSON として保存する。初期実装では app-owned DB table と編集 UI は追加しない。JSON は target set name と、`PlaylistId` / `PlaylistName` / `PlaylistSymbol`、任意の `FolderLabel` を持つ reference 配列で構成する。playlist 正本、playlist entries、`playlist.last_update` は変更しない。

display target filter は projection 後、keyword search 前に適用する。target 変更では、同じ期間 request の projection result を再利用して target filter / keyword filter / sort だけを再適用する。対象 playlist entries の読み込みが必要な場合は playlist entries hydration を使うが、playlist reload / external sync / `.bmt` 再出力は起動しない。

## Diagnostics

play history diagnostics は provider / stage / severity / code / message / source path を持つ。現行 UI は summary diagnostic text と log に出す最小実装であり、専用 maintenance view への詳細表示は未実装である。

主要 code は次の分類で扱う。

| Code prefix | 意味 |
| --- | --- |
| `play_history_lr2_schema_*` | LR2 schema check / read boundary の問題。未導入、manual repair required、unreadable など。 |
| `play_history_projection_*` | raw history を app chart / playlist 表示へ投影するときの問題。 |

diagnostics は schema / table 未導入を成功扱いに変える fallback ではない。schema が未導入または壊れている場合は、存在しない table を参照する command や row を生成しない。一方で projection 段階では、chart / playlist 解決に失敗しても raw history row を unresolved row として表示し、diagnostic を添えることがある。

## LAST PLAY SORT

LR2 provider では playlist custom folder 出力に `LastPlaySortFolder` を追加している。これは `.bmt` ではなく LR2 `.lr2folder` / LR2 `folder` row の projection である。

`LastPlaySortFolder` は `bms_lr2_last_play.last_play_at` を降順に見る。command は次の ORDER BY を使う。

```sql
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC,
         (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
```

`bms_lr2_last_play` に row がない chart は `NULL` sort として末尾へ回る。これは「履歴機能導入後にまだ観測していない」という意味であり、score / playcount / playlist update time へ意味を変えて fallback しない。

active LR2 linked profile の play history schema status が `Installed` ではない場合、playlist property / bulk edit の LastPlaySortFolder checkbox は disabled になる。保存済み output bit が ON の場合でも、materialization は diagnostics 付きで失敗し、存在しない `bms_lr2_last_play` を参照する `.lr2folder` / `folder` row を新規生成しない。

LAST PLAY SORT の `.lr2folder` 本文と LR2 `folder` row は固定 SQL projection である。LR2 で曲をプレイして `bms_lr2_last_play` が更新された後は、BeMusicSeeker が `.lr2folder` を再出力しなくても、LR2 / OpenLR2 が custom folder を開く時点の DB 内容で並び順が決まる。

## Settings And Manual Boundary

設定画面の `LR2と連携する` 配下に LR2 play history schema status、refresh、install、repair の操作がある。install / repair は LR2 score DB へ table / index / trigger を追加するため、実行前に対象 DB を確認する必要がある。

manual は日本語 `docs/manual.ja.md` と英語 `docs/manual.md` を同時更新する。現行 manual は Play History view の最小説明、archive period tree、LAST PLAY SORT の概略を持つ。実画面 screenshot 更新は未実施である。

## Out Of Scope / Planned

次は計画上の未実装範囲であり、この spec の現行仕様には含めない。

- maintenance / settings での detailed status view と schema missing / locked / read-only の表示確認。
- beatoraja provider 固有 aggregate: player aggregate から期間 summary へ playtime などを足す処理。
