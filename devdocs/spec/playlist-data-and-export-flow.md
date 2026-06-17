# プレイリストデータ・更新検知・互換出力 現行仕様

この資料は、BeMusicSeeker のプレイリスト関連 DB、外部難易度表の更新検知、LR2 カスタムフォルダ出力、beatoraja `.bmt` 出力の現行仕様をまとめる。実装履歴ではなく、現行動作と守るべき意味を正本として書く。

## 目的

プレイリスト機能は次の 3 つを分けて扱う。

- プレイリスト内容: プレイリスト名、外部同期 URL、folder 構成、entry、header 由来 course など、BeMusicSeeker が管理する正本データ。
- LR2 互換出力: LR2 の custom folder として参照できる `.lr2folder` 出力。
- beatoraja 互換出力: beatoraja の `table` フォルダで読める `.bmt` 難易度表キャッシュ出力。

`last_update` は「前回状態を知っているプレイリストが、外部同期により実際に変化したか」を示す日時として扱う。旧 DB へ hash 情報を初めて埋めるだけの処理は、DB 保存と `.bmt` 出力の対象にはするが、`last_update` の更新理由にはしない。

## DB Schema

プレイリスト関連の app-owned table は `playlist`、`playlist_entry`、`playlist_course` である。これらは BeMusicSeeker 全体の app-owned schema version `app_schema = 1` の一部であり、プレイリスト専用の schema version row は持たない。

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
| `compat_prefix` | compatible level folder 名の materialized prefix。LR2 custom folder 互換 level 名と、外部表で `tag` と `symbol` が無い場合の `.bmt` folder 名 prefix 候補。 |
| `last_update` | 前回状態から既知の内容変化があった時だけ更新する日時。hash 初期化だけでは更新しない。 |
| `output_dir` / `is_root_folder` / `ignore_folder_output` | LR2 custom folder 出力設定。 |
| `folder_order` / `folder_sort_key` / `folder_sort_ascending` | folder 順序と並び替え設定。 |
| `is_external_sync` | 外部同期表かどうか。 |
| `bmt_sort` | beatoraja `.bmt` URL 登録順。1 始まりの一意な整数として正規化する。 |
| `is_bmt_output` | beatoraja `.bmt` 出力対象かどうか。既定値は true。 |

`course_sha256` は保存しない。course は header JSON 由来なので、course の外部更新は `header_sha256` の変化で検知する。DB 内の `playlist_course` 改ざん検知は現時点の要件に含めない。

既存 DB で `bmt_sort` / `is_bmt_output` が無い場合は column を追加するだけで特殊な移行 table rebuild は行わない。`is_bmt_output` は旧 dump 復元や fresh schema の互換性のため DB 上は NULL を許容し、アプリ正規化で NULL を true へ収束させる。`bmt_sort` は欠損・重複・0 以下の値を含め、既存の有効値を優先しつつ playlist name / `playlist_id` で tie-break して 1 始まりの連番へ正規化する。既存 playlist を外部同期で更新する場合は `bmt_sort` / `is_bmt_output` を維持し、新規 playlist は現在の最大 `bmt_sort` + 1 を割り当て、`is_bmt_output = true` で作る。

### `playlist_entry`

`playlist_entry` はプレイリスト詳細の譜面行を保持する。

| Column | 意味 |
| --- | --- |
| `playlist_id` | 所属 playlist。 |
| `md5` / `sha256` | 譜面識別 hash。手動追加された所持 BMS / bmson entry は、owned chart から分かる両方の hash を保存する。外部同期 row は同期元の値を保存する。lookup は md5 があれば md5、md5 が無ければ sha256 を selected key とする。 |
| `title` / `artist` | 表示と `.bmt` song 出力に使う。`.bmt` song では `title` が必要。 |
| `folder` / `level` | 所属 folder と level。外部表の `.bmt` folder 名は header tag/symbol と compatible level から組み立てる。 |
| `url` / `url_diff` / `name_diff` / `org_md5` | 入手先・差分・親 hash などの補助情報。手動追加の `org_md5` は、ドロップ元 chart と同じディレクトリにある所持 BMS / bmson chart の MD5 集合を保存する。`.bmt` song では対応範囲で `url`、`appendurl`、`org_md5` に出力する。 |
| `memo` / `adddate` / `is_removed` | ローカル状態。外部同期時に一致行へ引き継ぐ。 |

外部同期の entry 更新検知は `data_sha256` で行う。entry fingerprint 相当の比較は、再取得 row と既存 row の対応付けを補助し、`memo`、`adddate`、`is_removed` などのローカル状態を引き継ぐために使うが、`last_update` や `playlist_entry` 更新のトリガーにはしない。

`playlist_entry` に md5 と sha256 の両方がある場合、解決・参照・summary の正本は md5 である。md5 が保存されている row では、md5 miss 後に同じ row の sha256 へ探索を広げない。sha256-only row は、外部同期元が sha256 しか持たない場合や既存互換 row のための入力として維持する。

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

更新判定は header JSON と data JSON の hash を分けて扱う。

| 判定 | 内容 | `last_update` | DB 保存 | `.bmt` 出力 |
| --- | --- | --- | --- | --- |
| header known change | 既存 non-NULL `header_sha256` から別 hash への変化 | 更新する | `playlist` / `playlist_course` を保存する。`playlist_entry` は更新しない | する |
| header hash initialization | NULL/空の `header_sha256` に初回値が入る | 更新しない | `playlist` / `playlist_course` を保存する。`playlist_entry` は更新しない | する |
| data known change | 既存 non-NULL `data_sha256` から別 hash への変化 | 更新する | `playlist` / `playlist_course` / `playlist_entry` を保存する | する |
| data hash initialization | NULL/空の `data_sha256` に初回値が入る | 更新しない | `.bmt` 対応以前の DB 修復として `playlist` / `playlist_course` / `playlist_entry` を保存する | する |

`header_sha256` は header JSON 全体を対象にするため、`tag`、`course`、`level_order`、`symbol`、`compat_prefix` などの header 由来変化を含む。

`data_sha256` は data JSON 全体を対象にする。raw data JSON が前回既知の値から変わった場合、`playlist_entry` を再取得結果で全置換する。

## `last_update`

`last_update` は「前回の正本状態を知っている状態から、外部同期で内容が変化した」ことを表す。

- `header_sha256` / `data_sha256` が non-NULL 既知値から別値へ変わった場合は更新する。
- `header_sha256` / `data_sha256` が NULL/空から初回値へ埋まっただけの場合は更新しない。
- entry fingerprint 差分だけでは更新しない。entry fingerprint 相当の比較はローカル状態引き継ぎの対応付け補助として扱う。
- `tag` と `course` は header に含まれるため、単独では `last_update` 判定に使わない。
- 外部表を初めて登録する場合は、取得した header の `last_update` があればそれを使い、無ければ登録時刻を初期 `last_update` として保存する。Walkure 系リコメンド表のように取得処理が更新日時を持つ場合は、その取得元日時を設定する。
- ローカルの空プレイリストを新規作成する場合は、`CreateBMSTable()` 時点で作成時刻を初期 `last_update` として持つ。DB には後続のプロパティ保存など、通常のプレイリスト保存処理で反映される。
- ローカル表の folder / entry 構成を手動編集した場合は、`RenameFolder`、`CreateNewFolder`、`AddBMSTableEntriesToFolder`、`RemoveBMSTableEntries` が `last_update` を編集時刻へ更新する。
- プレイリスト名、symbol、URL、custom folder 出力先、folder sort/order、`compat_prefix` などのプロパティ保存だけでは `last_update` を更新しない。`compat_prefix` 変更で compatible folder 名を再 materialize する場合も、譜面 membership 変更ではなく表示・出力 projection の更新として扱う。

この分離により、機能追加後の旧 DB 初回補完で `last_update` が現在時刻へ塗り替わることを避ける。

## プレイリスト正本の保存

プレイリスト名、symbol、外部同期 URL、folder 順序、custom folder 出力設定、course などの正本は DB の `playlist` / `playlist_course` / `playlist_entry` に保存する。DB 保存は LR2 linked profile と standalone profile のどちらでも行う。

プレイリストプロパティ保存では、`playlist` 本体と `playlist_course` を mode 非依存で保存する。`compat_prefix` 変更により compatible folder 名の再 materialize が必要な場合だけ、`playlist_entry.folder` も同じ transaction 系で保存する。それ以外のプロパティ変更では `playlist_entry` を保存し直さない。

`compat_prefix` の再 materialize は、既存 prefix 付き folder を新 prefix 付き folder へ写像する。既存 prefix が空の場合、外部同期表だけ未 prefix folder を新 prefix 付き folder として扱い、ローカル任意 folder 名は一括 rename しない。folder 名衝突が起きる場合は保存を失敗させ、既存 folder 構成を保持する。

entry folder projection を更新した場合は、DB 保存だけで終わらせず、プレイリスト詳細 source revision を進める。表示中の playlist detail は source を再構築し、ツリーの folder node と一覧の `FOLDER` 列は同じ materialized folder 名を表示する。変更前の folder を選択中だった場合は、同じ rewrite mapping で選択中 folder key も変更後 folder へ追従させる。

DB 保存後、LR2 linked profile でのみ `.lr2folder` の移動・再生成や `config.xml` の BMS search directory 更新を行う。standalone profile では `.lr2folder` 実出力は行わないが、プレイリスト header / entry の DB 保存と beatoraja `.bmt` 再出力要求は行う。

entry の追加・削除・folder 編集など、`playlist_entry` の全置換が必要なローカル編集では、DB 全体保存を行った後に LR2 linked profile でのみ custom folder を再出力する。custom folder 出力処理は DB 保存の副作用を持たず、DB 保存の有無は呼び出し元の正本更新処理で決める。

## LR2 Custom Folder 出力

LR2 custom folder 出力は LR2 linked profile の機能であり、standalone profile では実ファイル出力しない。

主な契機:

- 起動・`ReloadTables` 後の playlist entries hydration callback。
- 外部同期で header/data hash の known change があった場合。
- ローカル編集で DB 保存後に custom folder 再出力が必要になった場合。
- プレイリストプロパティ変更で custom folder 出力先や root 設定が変わった場合。

出力先は `LR2CustomFolderOutputBaseDir` または root 用の `LR2CustomFolderOutputBaseDirRootType` と、playlist の `output_dir` から決まる。`ignore_folder_output` により level/user/alphabet/clear などの folder 種別を除外できる。

プレイリストプロパティのカスタムフォルダタブにある「フォルダ名」は、実効値として `BMSTable.Output_dir` を操作する。入力値は trim 後に Shift-JIS 互換文字列へ丸める。空、または playlist name を同じ規則で丸めた値と同じ場合、DB の `playlist.output_dir` は NULL にし、実効出力フォルダ名は playlist name 由来の値に戻す。`is_root_folder = false` の場合は `LR2CustomFolderOutputBaseDir\Output_dir`、`is_root_folder = true` の場合は `LR2CustomFolderOutputBaseDirRootType\Output_dir` を出力 directory にする。UI の「ルートフォルダにする」は `is_root_folder`、出力フォルダ種別のチェック群は `ignore_folder_output` の反転として扱う。DB には「出力する種別」ではなく「出力しない種別」の bit flag が保存される。旧形式の「既存全種別を出力しない」値 `0x7F` は、追加種別も含めた全無効として解釈する。

出力 directory には `0000.lr2folder` からの連番ファイルを Shift-JIS で作る。階層を持つ種別では、各相対 directory の中で `0000.lr2folder` から採番する。各ファイルは少なくとも `#COMMAND`、`#MAXTRACKS`、`#CATEGORY`、`#TITLE`、`#INFORMATION_A`、`#INFORMATION_B` を持つ。BeMusicSeeker 生成ファイルでは `#CATEGORY` は playlist name、`#TITLE` はフォルダ表示名である。生成順は root 直下の `UserFolder`、`LevelFolder`、`AlphabetFolder`、`CategoryAllFolder`、`OtherFolder` と、階層付きの `ClearFolder`、`DJLevelFolder`、`BpmSortFolder`、`BpSortFolder`、`PlayCountSortFolder` で構成され、`ignore_folder_output` に含まれる種別は生成しない。`RandomFolder` は単独の抽出条件ではなく、User / Level / Clear / DJ level の各出力に `RANDOM` 版を追加する modifier として扱う。同一 directory 内では通常フォルダを先に連番化し、その後ろに `RANDOM` 版をまとめて配置する。各 playlist の出力 directory 配下は BeMusicSeeker の管理領域であり、再出力時は期待 projection に含まれない `.lr2folder` を削除対象にする。通常出力先 / root 出力先の直下にある別フォルダや別ファイルは、対応する playlist の `Output_dir` でない限り触らない。管理 playlist の出力先同士が包含関係になっている場合は、内側の playlist 出力 directory を外側 playlist の stale cleanup / DB prune から除外する。

生成するフォルダ種別は次の通り。

| 種別 | 生成内容 | 主な command 条件 |
| --- | --- | --- |
| UserFolder | playlist 全体の `playlist name ALL` と、`folder_list` の各フォルダ。空 folder 名は playlist name を title にする。 | `playlist_entry.is_removed = 0`、folder 別では `playlist_entry.folder = {folder}`。 |
| LevelFolder | `playlist_entry.level` を floor した整数別の `LEVEL n` と、NULL 用の `LEVEL ???`。 | `playlist_entry.level` / `is_removed`。 |
| AlphabetFolder | `A.B.C.D.` から `U.V.W.X.Y.Z.` までの 6 区間と `OTHERS`。 | `UPPER(playlist_entry.title)` の範囲。 |
| ClearFolder | `CLEAR FOLDER\0 NO PLAY`、`1 FAILED`、`2 ASSIST`、`3 EASY`、`4 CLEAR`、`5 HARD`、`6 FC`、`7 P.A` の各 directory に、playlist 全体の ALL と folder 別ファイルを出す。LR2 の TITLE 順表示でもクリア状態順になるよう directory 名に番号を付ける。`.lr2folder` の `#TITLE` は番号なしの状態名を使う。`FC` と `P.A` は分離し、`FC` は P.A bit を持たない full combo だけにする。 | `score.clear`、`score.rank`、`score.op_history`。 |
| DJLevelFolder | `DJ LEVEL\AAA`、`AA`、`A`、`UNDER A` の各 directory に、playlist 全体の ALL と folder 別ファイルを出す。`UNDER A` は A 未満と no score をまとめる。 | `score.rank`。 |
| CategoryAllFolder | `ALL LONG NOTES` と judge 種別別 folder。 | `song.longnote`、`song.judge`。 |
| OtherFolder | `MY BEST`、`NEW SONGS`、`REMOVED SONGS`。LR2IR score 取得と未送信検知が有効な場合は `UNSENT SONGS` も出す。 | `score.playcount`、`playlist_entry.adddate`、`playlist_entry.is_removed`、必要に応じて `ir_score` と `score` の一致条件。 |
| RandomFolder | User / Level / Clear / DJ level の各出力に `RANDOM` 版を追加する。 | 元の条件に `ORDER BY random()` と `#MAXTRACKS 1` を付ける。OpenLR2 は `#MAXTRACKS 1` だけでは random folder として扱わないため、`ORDER BY random()` を必ず含める。 |
| BpmSortFolder | `BPM SORT` directory に、playlist 全体の ALL と folder 別ファイルを出す。 | `chart_info.mainbpm` の昇順。parser version は filter 条件にせず、`mainbpm` 欠損は末尾。 |
| BpSortFolder | `BP SORT` directory に、playlist 全体の ALL と folder 別ファイルを出す。 | `score.minbp` の昇順。未プレイ / 欠損は末尾。 |
| PlayCountSortFolder | `PLAY COUNT SORT` directory に、playlist 全体の ALL と folder 別ファイルを出す。 | `score.playcount` の降順。未プレイ / 欠損は末尾。 |

`playlist.entry_type = File` の playlist では、基本条件は `song.hash in (SELECT md5 FROM playlist_entry WHERE playlist_id = ... AND is_removed = 0 ...)` になる。`entry_type = Folder` の playlist では、基本条件は `song.folder in (SELECT folder FROM song WHERE hash in (...))` になり、playlist entry の譜面 hash から LR2 `song.folder` 単位に広げる。どちらの場合も command は LR2 / OpenLR2 が `SELECT * FROM song LEFT JOIN score ON song.hash = score.hash WHERE {folder.command}` として評価できる WHERE 句断片である。OpenLR2 の実装では、`.lr2folder` の `#COMMAND` または `#TAG` が `folder.command` に入り、選曲画面でカスタムフォルダを開いたときにこの command が song/score join の WHERE 条件として使われる。

現在の command が主に参照する SQL table は次の通り。

| Table | 用途 |
| --- | --- |
| `song` | LR2 が認識した譜面本体。`hash`、`folder`、`title`、`artist`、`level`、`judge`、`longnote`、`adddate` などを参照できる。 |
| `score` | LR2 score DB 由来の score row。`hash` で `song` と LEFT JOIN され、`clear`、`rank`、`rate`、`minbp`、`playcount`、`op_history` などを参照できる。 |
| `playlist_entry` | BeMusicSeeker app-owned table。playlist の所属譜面、user folder、難易度、追加日、削除状態を command 側で参照する。 |
| `chart_info` | BeMusicSeeker app-owned chart metadata。BPM sort で `mainbpm` を参照する。 |
| `ir_score` | BeMusicSeeker が LR2IR score を取得している場合の app-owned table。`UNSENT SONGS` 判定で `score` と照合する。 |

出力後は `.lr2folder` file と同じ projection から LR2 `folder` table row も同期する。LR2 `folder` table は `title`、`subtitle`、`category`、`info_a`、`info_b`、`command`、`path`、`type`、`banner`、`parent`、`date`、`max`、`adddate` を持つ。BeMusicSeeker 生成 `.lr2folder` row は通常 `type = 2`、通常 directory row は `type = 1` である。`path` は file path または directory path、`parent` は親 directory path の LR2 CRC32 hash で、LR2 root 直下に置く row は固定 root parent hash `e2977170` を使う。`date` は対象 file / directory の mtime、`adddate` は既存 row があれば維持し、無ければ生成時刻を使う。

LR2 linked profile では、custom folder 出力は `.lr2folder` 実ファイルだけでなく LR2 `folder` table row も同じ projection から同期する。通常出力では `LR2CustomFolderOutputBaseDir` を通常の BMS root 相当として扱い、この出力先 directory row を LR2 root 直下に置く。各 playlist/table directory row は通常出力先 directory row の子にし、numbered `.lr2folder` row は playlist/table directory row の子にする。root 出力では、指定された playlist/table directory それぞれを BMS root 相当として扱い、その directory row を LR2 root 直下に置く。配下の level/user/alphabet などの `.lr2folder` row は playlist/table directory row の子にする。通常出力先全体を飛ばして playlist/table directory row を LR2 root 直下へ置いたり、配下 row を LR2 root 直下へ flatten したりしない。この root 出力 semantics は BeMusicSeeker 管理 playlist の projection に限定する。LR2 BMS search root 配下で自然 discovery した外部 `.lr2folder` は、search root 自身だけを LR2 root 直下に置き、配下 directory row は親 directory hash を `parent` にする。

アプリ管理 playlist の custom folder は `playlist` / `playlist_entry` / `playlist_course` と出力設定が正本であり、LR2/OpenLR2 の選曲画面で実際に使われる内容として保証すべき対象は LR2 `folder` table row である。出力済み `.lr2folder` 実ファイルは、LR2 が期待するファイルを出力先に置いておくことで不要な変更検知やスキャンを走らせないための配置物という意味が強く、実ファイル本文を正本として扱わない。

出力先変更、root 出力切替、entry/folder 編集、明示的な LR2 song.db 同期データ再同期では、playlist 正本から `.lr2folder` file と LR2 `folder` row を再 materialize し、この経路で `folder` row の command / title / category / information などの内容を保証する。起動時 repair は軽量な欠損・mtime 収束用の保険であり、期待 file path、起動時 scan surface 上の物理 file 存在と mtime、LR2 `folder` row の存在 / date / parent、期待外 row の有無で rewrite 対象を決める。起動時 repair では `.lr2folder` 本文を全量読み直さず、既存 `folder` row の command / title なども全件監査しない。期待される階層 directory や file が欠落している場合や、管理 playlist directory 配下に期待 projection 外の LR2 `folder` row だけが残っている場合は構造不一致として rewrite 対象にするが、構造・mtime・DB row が一致しているのに file 本文だけが異なるケースは no-op として扱う。

`playlist_custom_folder_output_status` は、本アプリが最後に custom folder materialization と LR2 `folder` row sync を完了した playlist の durable no-op 判定である。`output_directory`、root flag、出力 bit、entry type、folder sort、未送信出力設定、header/data hash、`last_update`、期待 file 数、LR2 `folder` table 行数、generator version が current で、scan surface 上でも期待 file が存在する場合、その playlist は起動時 repair の DB row lookup / materialization 対象にしない。status が current でない場合は、scan surface または対象出力 directory の grouped enumeration から得た `.lr2folder` mtime を使って repair target を選ぶ。列挙基盤が使えない場合は対象 file を再 materialize する方向へ倒し、個別 file ごとの `File.Exists` / `GetLastWriteTimeUtc` を repair target selection の通常経路にはしない。

起動時に物理 `.lr2folder` が欠落している場合も、欠落 table をまとめて batch materialization に流し、LR2 `folder` row は単発 sync で収束させる。手動再同期の playlist materialization は、全対象 playlist の期待 `.lr2folder` projection を作り、物理ファイルは差分だけ書き換え、期待 projection に含まれない管理 playlist directory 配下の `.lr2folder` は削除し、LR2 `folder` row は batch sync として 1 回で収束させる。table ごとに既存出力を削除して DB sync を繰り返さない。stage 開始、table 単位 projection、batch materialization / LR2 `folder` table sync / status 保存完了を performance log と status bar に出し、設定画面全体を同期的に無効化して隠れた長時間処理にしない。カスタムフォルダ操作内の LR2 `folder` table sync は汎用の LR2 `song.db` 同期とは別の責務として扱い、UI 文言も分離する。外部ツールが作った `.lr2folder` は、アプリ管理 playlist directory の外にある場合だけ LR2 song.db 同期側の discovery result として扱う。LR2 song.db 同期が playlist materialization 後に実行される場合は、materialization 前に捕捉した startup scan surface を再利用せず、新しい `.lr2folder` file surface を使う。

## beatoraja `.bmt` 出力

beatoraja `.bmt` 出力は LR2 linked profile に依存しない。設定 `EnableBeatorajaBmtOutput` が ON で、`BeatorajaRootPath` が有効な beatoraja ディレクトリを指す場合だけ有効になる。beatoraja ディレクトリは直下に `config_sys.json` と `beatoraja.jar` または `beatoraja.exe` があることを条件にする。

出力先は `config_sys.json` の `tablepath` から解決する。`tablepath` が相対 path の場合は beatoraja ディレクトリ基準、絶対 path の場合はそのまま使う。`BeatorajaBmtTablePath` は派生値として保持されるが、UI では直接編集しない。

beatoraja score 読み込みも同じ beatoraja ディレクトリを起点にする。プレイヤー一覧は `config_sys.json` の `playerpath` 配下のフォルダから作り、選択した `BeatorajaPlayerId` の `score.db` を `BeatorajaScoreDbPath` の派生値として使う。

### 出力対象と順序

`.bmt` 出力対象は `playlist.is_bmt_output = true` の playlist に限定する。`is_bmt_output = false` の playlist は全出力時の active set に含めず、manifest cleanup により既存の managed `.bmt` と BeMusicSeeker 管理 `tableURL` から削除される。これは個別 playlist の出力対象制御であり、`KeepBeatorajaBmtFilesWhenOutputDisabled` の「全体設定を OFF にした時に既存出力を残す」挙動とは混ぜない。

全出力時の projection 順と `config_sys.json` へ登録する BeMusicSeeker 管理 URL の順序は `bmt_sort` 昇順を正本にする。同値や欠損が残っている場合は name / `playlist_id` で tie-break するが、DB 読み込み時に連番へ正規化されることを前提にする。

プレイリストサマリーでは `BMT SORT` と `BMT OUTPUT` を表示する。`BMT SORT` 昇順表示中のみ、サマリー行の drag & drop で順序を変更できる。降順表示や他列 sort 中の drag reorder は受け付けない。フィルター中の drag reorder は非表示行を現在の相対位置に保持し、可視行のアンカーに対して選択行だけを挿入する。画面外への大きな移動は、行 context menu の「現在の並びをBMT SORTに反映」「BMT SORTの先頭へ」「BMT SORTの末尾へ」で補完する。

`BMT SORT` だけを変更した場合は `.bmt` 本体を書き直さず、専用の URL 同期経路で `config_sys.json` の `tableURL` のみを更新する。`BMT OUTPUT` を変更した場合は、変更された playlist だけを個別出力または個別削除の対象にする。

プレイリストサマリー行の context menu には一括変更 UI がある。対象は選択行 snapshot で、`output_dir`、playlist 名、URI、`compat_prefix`、folder 順序は扱わない。ダイアログ全体の OK は持たず、custom folder 出力種別、root folder、外部同期、`.bmt` 出力それぞれの「選択行へ一括適用」だけが変更を発生させる。custom folder 出力種別は tri-state で、checked は出力、unchecked は非出力、indeterminate は変更なしを表す。`entry_type = Folder` の playlist では `LevelFolder` は単体プロパティと同じく出力不可として扱う。外部同期 ON は `BMSTable.EnableExternalSync()` の実結果を採用し、URL 不備の playlist は警告なしで OFF のまま残す。custom folder 出力種別の一括変更は `playlist.ignore_folder_output` だけを header 保存し、LR2 linked profile では対象 playlist の custom folder projection を batch materialization / batch DB sync に流す。`.bmt` 投影には影響しないため `.bmt` 出力予約は行わず、処理中はステータスバーに custom folder 出力の件数進捗と処理中 playlist 名を表示する。root folder だけの変更も `.bmt` 出力予約は行わない。外部同期と `.bmt` 出力の一括変更は `.bmt` 投影または出力対象に影響するため、既存の `.bmt` 同期経路へ流す。

### 出力形式

`.bmt` は gzip 圧縮した UTF-8 JSON で、beatoraja の `TableData` 相当を出力する。

top-level:

- `url`: 外部表は `page_url` 優先、無ければ絶対 header URL。ローカル表は `bemusicseeker://playlist/{playlist_id}`。
- `name`: playlist name。
- `tag`: header tag、無ければ symbol、最後に compat prefix。
- `folder`: songs を持つ folder だけ出力。
- `course`: header 由来 course を beatoraja course 形状へ変換して出力。

出力ファイル名は `SHA-256(TableData.url) + ".bmt"`。

song は `title` と `md5` / `sha256` のどちらかを持つ行だけ出力する。folder song の hash 出力は `BeatorajaBmtHashOutputMode` で切り替える。`Original` は DB row に保存された hash だけをそのまま出力する。`FillMissingMd5Sha256` は DB row に保存された hash を第一候補にし、selected-key で所持 chart または chart_info を解決できる場合だけ、欠けている counterpart hash を `.bmt` 出力 projection 上で補完する。md5 と sha256 が別 chart を指す場合は md5 を正本にし、危険な counterpart 補完は行わない。`PreferSha256Only` は sha256 を出せる song では md5 を省略して sha256 のみを出力し、sha256 を解決できない md5 row は譜面を落とさず md5 のまま出力する。既定値は `Original`。course は header source JSON の hash だけを出力し、全 mode で補完対象にしない。対応範囲で `artist`、`url`、`appendurl`、`ipfs`、`appendipfs`、`org_md5` を出力する。空 folder は出力しない。folder と course が両方空の table は出力しない。

外部表の `folder[].name` は header `tag` 優先、無ければ `symbol`、最後に `compat_prefix` を使い、`tag + compatibleLevel` 形式にする。ローカル表は既存 folder 名をそのまま使う。

course constraints は header source の `grade_mirror` などから beatoraja enum 名 `MIRROR`、`GAUGE_LR2`、`LN` などへ変換する。beatoraja validator に合わない trophy は出力しない。

### 出力契機

- 起動時と `ReloadTables` 時は、playlist entries hydration と deferred external sync が収束した後に `is_bmt_output = true` の playlist を active profile / DB の投影として全出力し、manifest 管理下で active set に含まれない `.bmt` を削除する。外部同期を行わない初期読み込みでは hydration 後に同じ全出力を行う。playlist が 0 件、または出力対象 playlist が 0 件の場合も managed `.bmt` は空集合へ収束させる。
- 外部同期では header/data hash の known change と initialization のどちらでも `.bmt` 再出力対象になる。手動再同期後は、対象 playlist が非出力状態になった場合の旧 managed `.bmt` も削除できるように全体投影を出力する。
- ローカル編集で `playlist` / `playlist_entry` を保存した場合、または `CommitBMSTableEntry` を通る場合は対象 playlist を再出力する。
- プレイリストプロパティ保存後は、playlist 名、symbol、外部同期 URL、`compat_prefix`、folder 順序、entry folder projection など `.bmt` 投影に影響する変更があった場合だけ対象 playlist を再出力する。custom folder 出力先、root folder、custom folder 出力種別だけの変更では `.bmt` 再出力を予約しない。
- `.bmt` 設定の有効状態または table path が変わった場合は全 playlist を再出力する。旧 path がある場合は manifest cleanup する。ただし `KeepBeatorajaBmtFilesWhenOutputDisabled` が ON の状態で `.bmt` 出力を無効化した場合は、managed `.bmt` / manifest / `tableURL` を削除せず、最後に出力した状態をそのまま残す。
- `.bmt hash output` の mode だけを変更した場合は、元 playlist の変化ではないため即時の全再出力 trigger にしない。次に元 playlist が再出力対象になった時点で、その時の mode を使って projection する。
- プレイリスト削除時は対象 playlist の managed `.bmt` と BeMusicSeeker 管理 `tableURL` だけを削除する。backup restore 後は全出力系を使い、manifest cleanup により不要な managed `.bmt` を削除する。
- `RegisterBeatorajaBmtUrls` が ON の場合、`.bmt` 出力後に `config_sys.json` の `tableURL` も同期する。

全 playlist の `.bmt` 出力中は、ステータスバーに `.bmt` 出力の件数進捗と処理中 playlist 名を表示する。進捗は manifest 判定で未変更と判断されなかった playlist の table data projection と gzip 書き込みを含む。

### Managed Cleanup

出力先直下に `.bemusicseeker-bmt-manifest` を置く。拡張子 `.json` は付けない。

manifest は BeMusicSeeker が管理した `.bmt` と playlist ID から、最後に出力した `.bmt` file / URL / playlist name / `header_sha256` / `data_sha256` / `last_update` ticks / `.bmt` 投影入力 fingerprint / `.bmt` file mtime / `.bmt` file size への対応を記録する。cleanup は manifest に記録された `.bmt` だけを削除対象にし、管理外の `.bmt` は削除しない。起動・`ReloadTables`・restore・設定変更の全出力では、manifest を現在の active playlist set の投影として扱い、別 DB / 別 profile 由来で現在存在しない managed `.bmt` も削除する。

manifest schema v2 では、出力対象 playlist の URL / file name / playlist name / `header_sha256` / `data_sha256` / `last_update` ticks / 投影入力 fingerprint と、実 `.bmt` file の mtime / size が manifest と一致する場合、table data projection と gzip ファイル書き込みを省略する。投影入力 fingerprint には、`.bmt` 出力に効く tag 解決結果、外部同期扱い、compatible prefix、folder order、course JSON を含める。`.bmt hash output` の resolver 結果だけが変わった場合は、元 playlist の変化ではないため、この no-op 判定の invalidation 要因にしない。manifest は active set に合わせて更新し、cleanup と `tableURL` 同期の正本として使い続ける。

旧 manifest に含まれる `contentHash` は no-op 判定には使わない。旧 manifest の `files` と playlist URL は cleanup / `tableURL` 差し替えの所有情報としてだけ読み、新形式での出力後に schema v2 manifest へ自然に置き換える。

同じ playlist ID の `.bmt` URL が変わり、ファイル名が変わった場合は、manifest に残る旧ファイルを削除してから新ファイルを管理対象にする。

同名 `.bmt` が既に存在する場合は上書きする。管理外ファイルであっても、出力対象 URL の SHA-256 と同名なら BeMusicSeeker 出力が優先され、以後 manifest 管理対象になる。

### config_sys.json tableURL 同期

beatoraja 選曲画面の難易度表表示順は `config_sys.json` の `tableURL` 配列順が優先される。配列にない `.bmt` は beatoraja の `tablepath` ディレクトリ列挙順に依存するため、BeMusicSeeker 管理 `.bmt` の順序安定化には `tableURL` 同期を使う。

同期時は、既存 `tableURL` のうち BeMusicSeeker 管理外の URL を既存順のまま先頭側に残す。manifest に記録された前回 BeMusicSeeker 管理 URL は削除し、今回出力できた managed URL を playlist の `bmt_sort` 昇順で末尾に追加する。manifest entry が現在の playlist snapshot に見つからない場合は、既知 playlist の後ろへ name / playlist identity 順で並べる。`RegisterBeatorajaBmtUrls` が OFF の場合は、前回管理 URL を `tableURL` から外す。
