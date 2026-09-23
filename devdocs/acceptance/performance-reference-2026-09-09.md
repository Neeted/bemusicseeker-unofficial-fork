# 2026-09-09 大規模ライブラリの性能参照値

本書は、データ規模と比較時の注意点を残す観測記録である。性能要件の正本は [性能仕様](../spec/core/performance-and-scale.md)。ここにある単発の実測値を応答時間の保証、合格予算、既知の退行の許容値にしない。

## 入力と再現条件

| 項目 | 内容 |
| --- | --- |
| v3.0.0.0 | 対象コミット `b1fe3b83fa48fc02a4c1525f8a73bddc5a742ca0` |
| v2.1.6.0 | `3c000ec2e7a6e619c60d0f8c9e48ad12bd06d4f5` |
| ログ | `install-performance - v2.1.6.0.log` / `install-performance - v3.0.0.0.log` |
| 操作 | 同じファイル・DBの状態から20 ZIPを保留へ投入、既所持警告を無視して導入、新規画面で追加した譜面群を削除。断面一致は提供者の条件説明。 |
| 回数 | 各版1回。ハードウェア、OSキャッシュ、パッケージの全内容・バイト数、DB容量は未取得。 |

入力ログはリポジトリへ同梱しない。以下のSHA-256と計測点名・記録時刻で元資料を識別する。本書には規模と必要な集計だけを残し、利用者の絶対パスや譜面名は転載しない。

```text
install-performance - v2.1.6.0.log
SHA-256: 57caa2255ab2d5571ec774cf0c6703b4d5f1b161f5fc2fb225a6a2c26f77b6ce

install-performance - v3.0.0.0.log
SHA-256: 5aa5c6729cb418416c038ab69f0a46f9f8dc4ebb60feb47467148db404477aaa
```

## 規模の観測値

時刻は元ログの表記をそのまま使う。特記のない計測点はv3ログから取得し、両版で一致する主要件数はv2でも確認した。

| データ | 観測値 | 計測点・時刻・意味 |
| --- | ---: | --- |
| BMS格納行 | 211,522 | `song_tbl_load_projection` 23:27:26.0163 / `rows` |
| BMSON格納行 | 1,065 | 同上 / `bmsonRows` |
| 所持譜面合計 | 212,587 | `everything_scan success` 23:27:39.9981 / `charts`。BMS + BMSONとも一致。 |
| 譜面ディレクトリ | 30,582 | 同上 / `chartDirectoryCount` |
| 通常フォルダの差分対象 | 33,386 | `lr2_normal_folder_mtime_diff` 23:27:41.6939 / `directories` |
| 既存フォルダ行の読込み | 33,747 | `lr2_normal_folder_mtime_snapshot_prefetch` 23:27:26.5252 / `existingRows` |
| 取得したディレクトリ集合 | 35,426 | `everything_scan directory_surface` 23:27:39.9980 / `directories` |
| 逆引きキー合計 | 8,265,303 | `resource_index_build` 23:27:40.8431 / `reverseLookupKeys` |
| 譜面ディレクトリ別のリソースキー登録合計 | 12,432,127 | 同上 / `chartRelativeKeys` |
| 音声キー登録 | 11,390,644 | `song_tbl_file_check_cache_counts` 23:27:41.7288 / `audioResourceKeyEntries` |
| 画像キー登録 | 1,033,291 | 同上 / `imageResourceKeyEntries` |
| 動画キー登録 | 8,192 | 同上 / `movieResourceKeyEntries` |
| 音声・画像・動画の検索結果 | 11,568,473 / 1,046,336 / 9,565 | `everything_scan success` 23:27:39.9981。合計12,624,374は検索結果の件数であり、逆引きキー数とは異なる。 |
| ネイティブ圧縮データ | 266,560,518バイト | `resource_index_build` / `payloadBytes`。最終RAM使用量ではない。 |
| 重複を除いた主ハッシュ | 212,376 | `installed_primary_hash_lookup build` 23:28:12.7409 / `primaryHashes` |
| 完全な導入済み索引 | 424,752キー / 425,170ディレクトリ参照 | `installed_chart_lookup_index build` 23:28:41.4301。二種のハッシュと複数配置の参照を含み、所持行数とは異なる。 |
| ダイジェスト読込み | 211,311行 | `song_tbl_load_projection` / `chartDigestRows`。部分的なキャッシュの観測値。 |
| 譜面情報の補完 | 読込212,376 / 利用可能212,355 / 解析失敗21行 | `chart_info_hydration done` 23:27:51.3476。各読取りモデルの件数であり、DB全体容量を示さない。 |
| 保守情報の補完 | 212,587行・キー | `maintenance_hydration done` 23:27:55.8846 |
| プレイリスト | 505表 / 577,864項目 | `playlist_entries_hydration done` 23:27:50.2772。有効570,541 / 削除済み7,323。 |
| カスタムフォルダ出力 | 物理項目309,450件 | `playlist_custom_folder_output_repair physical_signature_done` 23:28:04.5868 |
| スコア初回読込み・IRキャッシュ読込み | 18,172 / 18,347行 | `score_tbl_load` 23:27:26.2773 / `ranking_cache_refresh done` 23:27:51.6673。全プレイ履歴件数ではない。 |

「800万リソース」は、この入力条件では逆引きキーの規模を指す。拡張子の除去、カテゴリ、譜面ディレクトリからの相対キー、所有関係を扱うため、実ファイル、ディレクトリ別のキー登録、逆引きキー、DB行は相互に同数ではない。

## パッケージと差分の観測

両版の `auto_install_prepare` は `discovered=20 pendingAdd=20 autoInstall=0`。その後の `install_chart_packages` は20回で、`addedFiles` の分布は1譜面のパッケージが17、2譜面が2、3譜面が1、合計24譜面。インストール失敗は0。

`delete_library_result` は両版とも `input=24 canonical=24 removed=24 failures=0 folderDeletes=20 fileDeletes=0`。v2は23:26:26.3593、v3は23:30:35.0887。

`reverse_lookup_incremental_update reason=install_package` は両版20回、`updatedHashes=0` が18回、残りは32と1。これは逆引きの対応関係の変更件数であり、実際に展開・検査したリソース数ではない。

`maintenance_rescan_chunk` の `fileExistsFallback` 合計は両版15,994回。1譜面の処理単位に667回・1,275回などの存在確認があるが、同梱されたファイルの異なり数と、パッケージ別の最大リソース数は、この計測点から確定しない。「複数譜面・数百リソースを含み得る」は提供者が示した設計前提として採用し、合成検証ケースと実測値を区別する。

## 操作時間の参考値

同じ操作に対応する最外層 `ui_suppress begin depth=1` から `end depth=0` の時刻差。確認回答待ちは含めないが、一部の表示反映・推定結果・後続索引処理も外側にあるため、厳密な操作終端の指標ではない。

| 操作 | v2開始〜終了 | v3開始〜終了 | v2秒 | v3秒 |
| --- | --- | --- | ---: | ---: |
| ZIP投入 | 23:24:44.2241〜23:24:57.4087 | 23:28:40.2013〜23:28:47.4304 | 13.1846 | 7.2291 |
| 導入 | 23:25:07.6457〜23:25:36.5921 | 23:29:08.3678〜23:29:29.2239 | 28.9464 | 20.8561 |
| 削除 | 23:26:19.3334〜23:26:27.0800 | 23:30:18.3165〜23:30:35.0887 | 7.7466 | 16.7722 |

この比較では削除時間が増えています。他の操作の短縮で相殺しません。一方、各版1回かつ以下の条件差があるため、他環境へ適用する性能保証や実装ごとの寄与時間は導かない。

- v2の `virtual_order_prewarm` は23:25:13.5261まで継続し、投入と導入前半に重なる。v3は23:28:28.6154に後処理の完了後、投入した。
- 保留画面の表示列数はv2が10、v3が13。新規画面は両版12。
- v3の `install_chart_packages.moveMs` はDB・保守・状態反映も囲む区間で、個別値0を仕事がない意味には扱いません。譜面情報の処理がパッケージのログより後にある点も比較範囲に含めます。
- `startup_initialization_complete` の必須処理と後処理の分類は版で異なる。計測点名が同じでも同じ仕事量とは限らない。

## この記録が保証しないこと

この測定だけでは、特定のCPU・メモリ構成への推奨、DB容量、パッケージの展開後容量、最大リソース数、キーごとの参照先数の分布、反復時の分位点、現行版の性能受入結果は示せない。今後も比較に必要な測定だけを、対象コミット・入力・測定条件を識別できる形で残します。適用できなくなった記録は退役し、履歴はGitで確認します。
