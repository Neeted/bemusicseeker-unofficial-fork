# beatoraja Table URL Import

この文書は、beatoraja `config_sys.json` の `tableURL` を BeMusicSeeker のプレイリストへ取り込む機能の現行仕様である。

## 目的

beatoraja の Resource タブで登録済みの難易度表 URL を、BeMusicSeeker のプレイリスト管理と `.bmt` 出力管理へ移行する。

`BMT SORT` は beatoraja の Resource タブ上の難易度表順序に対応する数値として扱い、Table URL インポート後は `config_sys.json` の `tableURL` 順が BeMusicSeeker の `BMT SORT` 先頭側に反映される。

## 入力

- beatoraja root は設定画面に表示されている `BeatorajaRootPath` を使う。設定保存前でもボタン押下時の表示値で即時実行する。
- `config_sys.json` は `BeatorajaConfigService.ReadTableUrls` で読み、`tableURL` 配列の文字列要素を順序保持で取得する。
- 空文字列は対象外にする。
- 同じ絶対 URL が複数回現れる場合は先頭だけを採用する。
- 絶対 URI として解釈できない値は失敗結果として集計する。

## インポート順序

1. UI 確認後、ステータスバーの playlist sync progress surface に `beatoraja Table URL インポート {completed}/{total}` を表示する。進捗総数には URL 件数だけでなく、登録、ライブラリ参照更新、`BMT SORT` 反映、完了処理の後処理ステップも含める。
2. 対象 URL ごとに、既存 playlist の `Page_url` または絶対 `Header_url` と一致するか確認する。
3. 既存 playlist があれば再読み込みせず、後続の `BMT SORT` 反映対象にだけ入れる。
4. 未登録 URL は通常の外部難易度表インポートと同じ `LoadExternalTableAsync` 経路で読み込む。
5. 通常読み込みに失敗した場合、beatoraja `tablepath` 配下の `.bmt` キャッシュ復元を試みる。
6. 通常読み込みまたは `.bmt` 復元で登録できた playlist は、ライブラリ参照、外部同期 STATUS、プレイリストサマリー更新、`.bmt` 出力予約の既存後処理へ接続する。
7. 全 URL 処理後、成功または既存として扱えた playlist を `tableURL` 順で `BMT SORT` の先頭に並べる。
8. BeMusicSeeker にのみ存在する playlist は、現在の `BMT SORT` 順を維持したまま最後尾へ移動する。

## 性能とバッチ境界

未登録 URL の外部取得は、通常リロードと同じ最大並列数の snapshot load 経路で並列実行する。単体登録 API を URL 数分 await する形にはしない。

DB 登録、表示 collection 反映、重複名の連番解決、`.bmt` 出力予約、URL 補完予約、ライブラリ参照更新は、外部取得完了後に `tableURL` 順へ戻してからバッチ処理する。DB や表示 collection の変更は並列化しない。

外部取得が終わった後も登録や参照更新で時間がかかるため、進捗表示は `プレイリストを登録中`、`ライブラリ参照を更新中`、`BMT SORT を反映中` のようなフェーズをサブラベルに出す。URL 件数が完了しただけで進捗バーを満了させない。

`BMT SORT` 反映は、`tableURL` に対応する playlist を `1..N` に配置し、BeMusicSeeker にのみ存在する playlist のうち衝突・先頭側・無効値のものだけを後ろへ移動する。全 playlist を毎回密に再採番して不要なヘッダ DB 書き込みを増やさない。

既存 playlist の raw URL 補正は、対象 `BMSTable` の writer lock 内で行い、変更分だけをまとめてヘッダ commit する。

## config Table URL の raw 保持

`.bmt` 内の `url` が `config_sys.json` の URL と異なる場合でも、BeMusicSeeker 側で管理する表 URL は必ず `config_sys.json` の `tableURL` 側にする。

Table URL インポート経路では、`Uri.AbsoluteUri` で正規化した文字列ではなく、`config_sys.json` に書かれていた raw 文字列を既存の `page_url` または絶対 `header_url` に保持する。通常の `URLを指定して読み込む` とプレイリストプロパティ編集は従来どおり正規化済み URL を保存する。

これにより、後続の `.bmt` 出力と `config_sys.json` `tableURL` 同期は、ユーザーが beatoraja に登録していた URL 文字列を正本として扱う。

## `.bmt` 復元

`.bmt` キャッシュは beatoraja と同じく `SHA-256(config_sys.json の raw Table URL) + ".bmt"` をファイル名として探す。

復元対象:

- playlist name: `.bmt` root `name`
- symbol / tag: `.bmt` root `tag`
- page URI: `config_sys.json` の raw Table URL
- folders / songs: `.bmt` root `folder[].songs`
- courses: `.bmt` root `course`
- song fields: `title`, `artist`, `md5`, `sha256`, `url`, `appendurl`, `org_md5`

復元は `.bmt` JSON を直接 DB に書かず、合成 header/data JSON を作って `BMSTable.LoadHeaderJSON` / `LoadDataJSON` に戻す。

folder 名が `tag` で始まる場合は `compat_prefix = tag` として復元し、再出力時に `tag + level` が二重にならないようにする。folder 名が tag で統一されていない場合は `compat_prefix = ""` とし、復元元の folder 名をそのまま entry folder として保持する。

`.bmt` 復元に成功した場合、Table URL インポート結果としては `.bmtから復元` の成功扱いにする。ただしプレイリストサマリーの `STATUS` は外部 URL 同期の最新結果を表示する列なので、元 URL の外部読み込みで発生した 404 / 403 / HTTP / NETWORK などの失敗を表示する。

## 重複 playlist 名

通常の URL 指定インポートは同名 playlist を拒否する既存仕様を維持する。

beatoraja Table URL インポート経路だけは、beatoraja 側が同名難易度表を許容している可能性に合わせ、同名 playlist を `name(1)`, `name(2)` のように連番リネームして登録する。

## 失敗時の扱い

通常読み込みにも `.bmt` 復元にも失敗した URL は playlist として登録しない。

この URL は BeMusicSeeker 管理 `.bmt` manifest に入らないため、`.bmtを出力する` と `config_sys.json` `tableURL` 同期では管理外 URL として扱われる。既存 `tableURL` 同期仕様により、管理外 URL は先頭側に既存順で残る。

通常読み込みが成功したものの entry と course がどちらも 0 件の table は、後続の外部同期で復旧する可能性があるため、Table URL インポートとしては成功扱いで playlist 登録する。この状態では `.bmt` ファイル本体は出力できないが、BeMusicSeeker 管理 Table URL として manifest に URL 所有権を残す。`config_sys.json` `tableURL` 同期では管理外 URL ではなく BMT SORT 対象の管理 URL として扱い、既存 `tableURL` の先頭側に固定されないようにする。

空表が後で外部同期や手動操作により entry または有効 course を持つ状態へ復旧した場合、同じ playlist / Table URL の管理枠で `.bmt` ファイルを出力する。逆に playlist 削除や `.bmt` 出力対象外化を行った場合は、空表由来の URL 所有権も manifest から外し、`config_sys.json` `tableURL` 同期の管理対象から外す。

## `.bmtを出力する` 有効化時の案内

`.bmtを出力する` を OFF から ON にする際、beatoraja root が有効で、`tableURL` に BeMusicSeeker playlist として未取り込みの URL が存在する場合は確認を出す。

案内内容:

- beatoraja の Table URL 登録済み難易度表を先に BeMusicSeeker のプレイリストツリーへ取り込むと、beatoraja 選曲画面の難易度表順を維持しやすい。
- beatoraja 側で難易度表をリロードする必要が少なくなり、管理を BeMusicSeeker に一元化できる。
- ユーザーが続行した場合は `.bmt` 出力を有効化する。

## Verification map

beatoraja `tableURL` import と `.bmt` output の owner coverage は Functional の専用 fixtureへ分離する。

| behavior | canonical fixture | Functional route |
| --- | --- | --- |
| Table URL の読み取り、既存一致、外部 load / `.bmt` fallback、raw URL保持、失敗集計、登録後処理 | `BmsPlaylistMigrationAndRegistrationTests` | `playlist-migration-registration`, 1 worker / `ClassLevel` |
| manifest、managed `.bmt` file、`config_sys.json` `tableURL`同期、custom-folder outputとの連携 | `BmsPlaylistCustomFolderOutputTests` | `playlist-custom-folder-output`, 1 worker / `ClassLevel` |

各 fixtureは process-local な設定とGUID付き temporary root / databaseを所有し、既存の class-wide `DoNotParallelize` と completion / cleanup signalを維持する。旧 `BmsPlaylistUpdateTests` routeは使用しない。
