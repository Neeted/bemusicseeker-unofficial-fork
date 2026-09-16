# プレイリストのLR2カスタムフォルダ出力

## 目的と適用範囲

プレイリストから `.lr2folder` と対応するLR2のフォルダ行を生成する契約を定めます。正本のDB保存とは別の処理で、LR2連携モードだけが実ファイルを出力します。

## 用語

[共通用語集](../glossary.md)を参照します。コードの識別子は原表記を使います。

## 仕様

### 出力先と事前検査

既定の通常出力先は `LR2CustomFolderOutputBaseDir`、追加先は `LR2CustomFolderAdditionalOutputBaseDirs`、ルート型は `LR2CustomFolderOutputBaseDirRootType` です。追加先はJSON文字列配列でフルパスを保存し、表示とDBの選択値には末尾のディレクトリ名を使います。

表の `custom_folder_output_base_name` がNULLなら既定先、登録済み名なら追加先を選びます。保存名が設定にない場合の実効先は既定先です。設定画面で追加先を削除した場合は参照をNULLへ、改名した場合は新名へ更新します。ルート型の間も通常先の選択値は保持し、実効出力先だけルート型を優先します。

`output_dir` は入力をtrimしてShift_JIS互換にしたフォルダ名です。空または同じ規則で変換した表名と同じならDBではNULLとし、表名由来の実効値を使います。通常型は選択先の下、ルート型はルート型基点の下へこの名前のディレクトリを作ります。

起動・差分更新・初期化の前には全出力基点を検査し、読取りと自己所有の一時ファイルの作成・書込み・削除を要求します。使えない基点を自動作成して成功扱いにしません。配下の表ディレクトリや出力ファイルがまだないことは失敗ではありません。スタンドアロンではLR2出力先の残存設定で起動を止めません。

基点同士の同一・親子配置は設定入口で拒否します。既定通常先と管理外BMS検索ルートの重複も拒否します。追加先・ルート型先については既存jukeboxを管理領域として採用する再設定のため、保存時に警告して利用者が続行した場合だけ許可する経路があります。採用後は通常譜面の検索対象ではありません。検索対象の区別は[設定](../runtime/settings.md#カスタムフォルダと検索対象)を参照します。

### jukeboxとの同期

通常・追加先は利用者設定を正本としてjukeboxに不足分を補います。ルート型は基点そのものではなく、各表の実効出力ディレクトリをルートとして扱います。子ディレクトリが未生成なら必要に応じて作成します。保存時と起動時は、基点や不要な旧子ルートを残さず現在の表ディレクトリへ揃えます。起動時は表一覧を読み込んだ後に行います。`ReloadTables` は不足する表ルートを補いますが、不要ルートの完全な整理は保存時・起動時が担当します。

### 出力除外と種類

`ignore_folder_output` は出力しない種類を表すビットです。既存値に新しい種類のビットがなければ、その種類は出力します。旧版の全除外相当を現在の全除外値へ拡張しません。`entry_type=Folder` では保存値に関係なくレベル別出力を無効にします。

新しい通常表は `PlaylistDefaultIgnoreFolderOutput`、推定表とおすすめ表は専用の固定値を使います。既存表の外部同期では保存済みの値を保持します。

| 種別 | bit | `PlaylistDefaultIgnoreFolderOutput` の既定 | 推定表 プレイリスト の既定 | おすすめ表 プレイリスト の既定 |
| --- | ---: | --- | --- | --- |
| 利用者フォルダ | `0x001` | 出力 | 出力 | 出力 |
| レベル | `0x002` | 出力しない | 出力 | 出力しない |
| 頭文字 | `0x004` | 出力しない | 出力しない | 出力しない |
| クリア | `0x008` | 出力 | 出力しない | 出力しない |
| DJレベル | `0x010` | 出力 | 出力しない | 出力しない |
| 譜面特性 | `0x020` | 出力しない | 出力しない | 出力しない |
| その他 | `0x040` | 出力しない | 出力しない | 出力しない |
| ランダム | `0x080` | 出力 | 出力しない | 出力しない |
| BPM順 | `0x100` | 出力 | 出力しない | 出力しない |
| BP順 | `0x200` | 出力 | 出力しない | 出力しない |
| プレイ回数順 | `0x400` | 出力 | 出力しない | 出力しない |
| 最終プレイ順 | `0x800` | 出力 | 出力しない | 出力しない |
| 全曲 | `0x1000` | 出力 | 出力 | 出力 |

通常の既定除外は `Level | Alphabet | CategoryAll | Other` です。`AllFolders=0x1FFF` は全出力を禁止する値です。

全曲と利用者フォルダが対象範囲を選び、クリア、DJレベル、BPM順、BP順、プレイ回数順、最終プレイ順はその範囲ごとに出力します。レベル、頭文字、譜面特性、その他は独立した全体分類です。

| 実装上の種類 | 生成内容・条件 |
| --- | --- |
| `AllSongsFolder` / `UserFolder` | 全体の「表名 ALL」と各フォルダ。削除済み譜面を除き、空のフォルダ名は表名を表示する。 |
| `LevelFolder` | レベルを切り捨てた整数ごとの `LEVEL n` とNULL用の `LEVEL ???`。 |
| `AlphabetFolder` | `A.B.C.D.` から `U.V.W.X.Y.Z.` までの6区間と `OTHERS`。 |
| `ClearFolder` | `CLEAR FOLDER` 配下に番号付きのNO PLAY、FAILED、ASSIST、EASY、CLEAR、HARD、FC、P.A。タイトルには番号を付けない。LR2のclear=2をEASYビットでASSIST/EASYに分け、FCはP.Aビットを持たないものだけとする。 |
| `DJLevelFolder` | `DJ LEVEL` 配下のAAA、AA、A、UNDER A。A未満とスコアなしを最後の区分へまとめる。 |
| `CategoryAllFolder` | `ALL LONG NOTES` と判定種類別。 |
| `OtherFolder` | `MY BEST`、`NEW SONGS`、`REMOVED SONGS`。IR取得・未送信検知が有効なときは `UNSENT SONGS`。 |
| `RandomFolder` | 対象範囲・レベル・クリア・DJレベルの各出力にランダム版を追加する。`ORDER BY random()` と `#MAXTRACKS 1` の両方が必要。 |
| `BpmSortFolder` | `chart_info.mainbpm` の昇順、欠損は末尾。パーサー版を条件にしない。 |
| `BpSortFolder` | `score.minbp` の昇順、未プレイ・欠損は末尾。 |
| `PlayCountSortFolder` | `score.playcount` の降順、未プレイ・欠損は末尾。 |
| `LastPlaySortFolder` | `bms_lr2_last_play.last_play_at` の降順、欠損は末尾。他の日時へ代用しない。 |

最終プレイ順は履歴スキーマ未導入でもファイルを生成しますが、LR2側で開くには対象表が必要です。SQLで現在の履歴を参照するため、プレイごとの再出力は不要です。

### ファイルとSQL

各相対ディレクトリで `0000.lr2folder` から採番し、Shift_JISで出力します。通常版を先に、ランダム版をその後に並べます。本文は `#COMMAND`、`#MAXTRACKS`、`#CATEGORY`、`#TITLE`、`#INFORMATION_A`、`#INFORMATION_B` を持ちます。カテゴリは表名、タイトルは表示フォルダ名です。

コマンドはLR2/OpenLR2が `song LEFT JOIN score ON song.hash=score.hash` に適用できるWHERE句断片です。ファイル単位の表は所属MD5へ絞り、フォルダ単位の表は所属MD5から得た `song.folder` へ広げます。`playlist_entry`、`chart_info`、必要な `ir_score` と履歴表も参照します。

最終プレイ順は次の固定SQLを使います。

```sql
ORDER BY (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) IS NULL ASC,
         (SELECT last_play_at FROM bms_lr2_last_play WHERE hash = song.hash) DESC
```

同じ生成内容から `folder` 行を同期します。`date` は実際の更新時刻、既存 `adddate` は保持し、新規だけ生成時刻を使います。通常先→表ディレクトリ→連番ファイルという階層を保ち、ルート型は各表ディレクトリだけをLR2ルート直下へ置きます。階層を平坦化しません。CRC32や表の列は[楽曲DB同期](../integration/lr2-song-db.md)を参照します。

### 管理領域の整理

表の出力ディレクトリ内は管理領域です。同じ先への再出力では期待集合にない `.lr2folder` を削除します。出力先変更、ルート切替、表削除では旧表ディレクトリ全体と対応範囲のDB行を整理します。基点直下の他の項目は変更しません。表の出力先同士の包含は通常経路では想定しません。

### 修復と一括反映

出力状態がない、入力が変わった、物理ファイルが欠落した、ファイル・出力ディレクトリ・祖先の更新時刻が変わった場合は再確認対象にします。その場合だけ正本から軽量な期待集合を作り、ファイル行・ディレクトリ行の欠落、日時・親の不一致、余分な行を調べます。列挙基盤が使えない場合は再作成対象にし、全ファイルの個別存在確認を通常経路にしません。

状態が現行ならDB行だけの外部編集を常時監査しません。ただしjukeboxへルート型表を補完した直後は、表ディレクトリ自身の行を完全一致パスで確認します。末尾区切り付きパス、`type=1`、`parent=e2977170` に限り、不一致ならその表を再確認対象へ戻します。

欠落表や手動再同期の全対象はまとめて生成し、物理ファイルの差分だけを書き、DB同期は一回で行います。表ごとに旧出力を消して同期を繰り返しません。生成開始、表の処理、物理反映、DB同期、状態保存まで進捗と診断を出します。

管理外の `.lr2folder` 探索は、管理表ディレクトリを列挙時の除外範囲として渡します。全件を候補化してから捨てません。保持した走査結果と現在の除外範囲が変わった場合は現在の範囲で探索し直し、出力前の古いファイル集合を出力後の同期に使いません。

全体生成の準備は一つの明示的受付で外側の変更権を取得し、内部の具体的な連携処理だけに同じ権限を渡します。使用中なら待機・内部再試行・自動再開をせずfalseで終端します。進捗は既存の最新状態通知キューを使い、権限保持中に購読先を同期実行しません。購読先の例外は診断し、物理ファイル・DB・保存状態の結果を変えません。

## 実装とテストの対応

| 仕様項目・主な条件 | 実装箇所 | テスト箇所・確認内容 |
| --- | --- | --- |
| 種類・SQL・階層と保存設定 | [`BMSTable`](../../../BeMusicSeeker/Models/BMSTable.cs) | [`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/BmsPlaylistCustomFolderOutputTests.cs) |
| 一括生成・旧出力先・状態・受付 | [`PlaylistCustomFolderOutputOwner`](../../../BeMusicSeeker/Models/BmsLibraryInternal/PlaylistCustomFolderOutputOwner.cs) | [`PlaylistCustomFolderOutputOwnerTests`](../../../BeMusicSeeker.Tests/PlaylistCustomFolderOutputOwnerTests.cs)、[`BmsPlaylistCustomFolderOutputTests`](../../../BeMusicSeeker.Tests/BmsPlaylistCustomFolderOutputTests.cs) |
| スキーマの互換性・既定値 | [`BMSPlaylist`](../../../BeMusicSeeker/Models/BMSPlaylist.cs) | [`PlaylistSchemaMigrationTests`](../../../BeMusicSeeker.Tests/PlaylistSchemaMigrationTests.cs) |

## 関連資料

[プレイリスト保存](storage-and-export.md)、[プロパティ画面](../ui/playlist-properties.md)、[楽曲DB同期](../integration/lr2-song-db.md)を参照します。
