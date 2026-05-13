# プレイリストデータ・更新検知・互換出力 現行仕様

この資料は、BeMusicSeeker のプレイリスト関連 DB、外部難易度表の更新検知、LR2 カスタムフォルダ出力、beatoraja `.bmt` 出力の現行仕様をまとめる。実装履歴ではなく、現行動作と守るべき意味を正本として書く。

## 目的

プレイリスト機能は次の 3 つを分けて扱う。

- プレイリスト内容: プレイリスト名、外部同期 URL、folder 構成、entry、header 由来 course など、BeMusicSeeker が管理する正本データ。
- LR2 互換出力: LR2 の custom folder として参照できる `.lr2folder` 出力。
- beatoraja 互換出力: beatoraja の `table` フォルダで読める `.bmt` 難易度表キャッシュ出力。

`last_update` は「前回状態を知っているプレイリストが、外部同期により実際に変化したか」を示す日時として扱う。旧 DB へ hash 情報を初めて埋めるだけの処理は、DB 保存と `.bmt` 出力の対象にはするが、`last_update` の更新理由にはしない。

## DB Schema

プレイリスト関連の app-owned table は `playlist`、`playlist_entry`、`playlist_course` である。

### `playlist`

`playlist` はプレイリストの header と表示・出力設定を保持する。

| Column | 意味 |
| --- | --- |
| `playlist_id` | プレイリスト ID。ローカル表の `.bmt` pseudo URL にも使う。 |
| `name` | 表示名。`.bmt` top-level `name` にも使う。 |
| `symbol` | 難易度表記号。`tag` が無い外部表の `.bmt` folder 名 prefix 候補。 |
| `tag` | header.json 由来の tag。外部表の `.bmt` folder 名 prefix では最優先。 |
| `page_url` / `header_url` / `data_url` | 外部同期元 URL。外部表の `.bmt` URL は `page_url` 優先、無ければ絶対 header URL を使う。 |
| `header_sha256` | 取得した header JSON 全体の SHA-256。header 由来情報の更新検知に使う。 |
| `data_sha256` | 取得した data JSON 全体の SHA-256。raw data JSON の更新検知に使う。 |
| `compat_prefix` | LR2 custom folder 互換 level 名の prefix。外部表で `tag` と `symbol` が無い場合の `.bmt` folder 名 prefix 候補。 |
| `last_update` | 前回状態から既知の内容変化があった時だけ更新する日時。hash 初期化だけでは更新しない。 |
| `output_dir` / `is_root_folder` / `ignore_folder_output` | LR2 custom folder 出力設定。 |
| `folder_order` / `folder_sort_key` / `folder_sort_ascending` | folder 順序と並び替え設定。 |
| `is_external_sync` | 外部同期表かどうか。 |

`course_sha256` は保存しない。course は header JSON 由来なので、course の外部更新は `header_sha256` の変化で検知する。DB 内の `playlist_course` 改ざん検知は現時点の要件に含めない。

### `playlist_entry`

`playlist_entry` はプレイリスト詳細の譜面行を保持する。

| Column | 意味 |
| --- | --- |
| `playlist_id` | 所属 playlist。 |
| `md5` / `sha256` | 譜面識別 hash。`.bmt` song ではどちらか一方以上が必要。 |
| `title` / `artist` | 表示と `.bmt` song 出力に使う。`.bmt` song では `title` が必要。 |
| `folder` / `level` | 所属 folder と level。外部表の `.bmt` folder 名は header tag/symbol と compatible level から組み立てる。 |
| `url` / `url_diff` / `name_diff` / `org_md5` | 入手先・差分・親 hash などの補助情報。`.bmt` song では対応範囲で `url`、`appendurl`、`org_md5` に出力する。 |
| `memo` / `adddate` / `is_removed` | ローカル状態。外部同期時に一致行へ引き継ぐ。 |

外部同期の entry 更新検知は、DB 永続 row と再取得 row を正規化した fingerprint 比較で行う。folder 名や `org_md5` だけの違いなど、ローカル状態・互換出力由来の差分は content change として扱わない。

### `playlist_course`

`playlist_course` は header JSON 由来の course を entry とは分けて保持する。

| Column | 意味 |
| --- | --- |
| `course_id` | row ID。 |
| `playlist_id` | 所属 playlist。 |
| `course_order` | header 内の course 順。 |
| `course_json` | 正規化した course JSON object。 |

header の `course` は `[[{...}]]` のような入れ子配列も平坦化して course object として保存する。ローカルプレイリストの course は初期実装では空でよい。

## 外部同期と更新検知

外部同期では、再取得した header/data JSON から `BMSTable` と `BMSTableEntry` を作り、既存 DB と比較して既存ローカル状態を引き継ぐ。

更新判定は次の 2 種類に分ける。

| 判定 | 内容 | `last_update` | DB 保存 | `.bmt` 出力 |
| --- | --- | --- | --- | --- |
| known content change | entry fingerprint 差分、または既存 non-NULL `header_sha256` / `data_sha256` から別 hash への変化 | 更新する | する | する |
| hash initialization | NULL/空の `header_sha256` / `data_sha256` に初回値が入る | 更新しない | する | する |

`header_sha256` は header JSON 全体を対象にするため、`tag`、`course`、`level_order`、`symbol`、`compat_prefix` などの header 由来変化を含む。

`data_sha256` は data JSON 全体を対象にする。現時点では entry fingerprint 比較が主判定だが、raw data JSON が前回既知の値から変わった場合も known content change とする。将来、playlist_entry 更新検知を raw hash ベースへ寄せるかは別途検討する。

## `last_update`

`last_update` は「前回の正本状態を知っている状態から、外部同期で内容が変化した」ことを表す。

- entry fingerprint が変わった場合は更新する。
- `header_sha256` / `data_sha256` が non-NULL 既知値から別値へ変わった場合は更新する。
- `header_sha256` / `data_sha256` が NULL/空から初回値へ埋まっただけの場合は更新しない。
- `tag` と `course` は header に含まれるため、単独では `last_update` 判定に使わない。

この分離により、機能追加後の旧 DB 初回補完で `last_update` が現在時刻へ塗り替わることを避ける。

## LR2 Custom Folder 出力

LR2 custom folder 出力は LR2 linked profile の機能であり、standalone profile では実ファイル出力しない。

主な契機:

- 起動・`ReloadTables` 後の playlist entries hydration callback。
- 外部同期で known content change があった場合。
- ローカル編集で `ReOutputCustomFolderAndCommitToDB` を通る場合。
- プレイリストプロパティ変更で custom folder 出力先や root 設定が変わった場合。

出力先は `LR2CustomFolderOutputBaseDir` または root 用の `LR2CustomFolderOutputBaseDirRootType` と、playlist の `output_dir` から決まる。`ignore_folder_output` により level/user/alphabet/clear などの folder 種別を除外できる。

## beatoraja `.bmt` 出力

beatoraja `.bmt` 出力は LR2 linked profile に依存しない。設定 `EnableBeatorajaBmtOutput` が ON で `BeatorajaBmtTablePath` が非空の場合だけ有効になる。非空 path が存在しない場合は設定 validation error とし、空欄は未有効として保存可能にする。

### 出力形式

`.bmt` は gzip 圧縮した UTF-8 JSON で、beatoraja の `TableData` 相当を出力する。

top-level:

- `url`: 外部表は `page_url` 優先、無ければ絶対 header URL。ローカル表は `bemusicseeker://playlist/{playlist_id}`。
- `name`: playlist name。
- `tag`: header tag、無ければ symbol、最後に compat prefix。
- `folder`: songs を持つ folder だけ出力。
- `course`: header 由来 course を beatoraja course 形状へ変換して出力。

出力ファイル名は `SHA-256(TableData.url) + ".bmt"`。

song は `title` と `md5` / `sha256` のどちらかを持つ行だけ出力する。対応範囲で `artist`、`url`、`appendurl`、`ipfs`、`appendipfs`、`org_md5` を出力する。空 folder は出力しない。folder と course が両方空の table は出力しない。

外部表の `folder[].name` は header `tag` 優先、無ければ `symbol`、最後に `compat_prefix` を使い、`tag + compatibleLevel` 形式にする。ローカル表は既存 folder 名をそのまま使う。

course constraints は header source の `grade_mirror` などから beatoraja enum 名 `MIRROR`、`GAUGE_LR2`、`LN` などへ変換する。beatoraja validator に合わない trophy は出力しない。

### 出力契機

- 起動時と `ReloadTables` 時は、playlist entries hydration 後に全 playlist を playlist 単位 queue へ入れる。現時点では前回から変化がなくても再出力する。
- 外部同期では known content change と hash initialization のどちらでも DB 保存対象になり、callback により `.bmt` 出力対象になる。
- ローカル編集で `ReOutputCustomFolderAndCommitToDB` または `CommitBMSTableEntry` を通る場合は対象 playlist を再出力する。
- プレイリストプロパティ保存後は対象 playlist を再出力する。
- `.bmt` 設定の有効状態または table path が変わった場合は全 playlist を再出力する。旧 path がある場合は manifest cleanup する。
- プレイリスト削除と backup restore 後は全出力系を使い、manifest cleanup により不要な managed `.bmt` を削除する。

### Managed Cleanup

出力先直下に `.bemusicseeker-bmt-manifest` を置く。拡張子 `.json` は付けない。

manifest は BeMusicSeeker が管理した `.bmt` と playlist ID から最後に出力した `.bmt` file への対応を記録する。cleanup は manifest に記録された `.bmt` だけを削除対象にし、管理外の `.bmt` は削除しない。

同じ playlist ID の `.bmt` URL が変わり、ファイル名が変わった場合は、manifest に残る旧ファイルを削除してから新ファイルを管理対象にする。

同名 `.bmt` が既に存在する場合は上書きする。管理外ファイルであっても、出力対象 URL の SHA-256 と同名なら BeMusicSeeker 出力が優先され、以後 manifest 管理対象になる。
