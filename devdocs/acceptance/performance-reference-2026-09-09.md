# 2026-09-09 大規模ライブラリの性能参照記録

本書は、データ規模と比較時の注意点を残す観測記録である。性能要件の正本は [performance-and-scale.md](../spec/performance-and-scale.md)。ここにある単発の実測値を秒数SLO、合格budget、既知の退行の許容値にしない。

## 入力と再現条件

| 項目 | 内容 |
| --- | --- |
| v3.0.0.0 | 添付Git bundleのHEAD `b1fe3b83fa48fc02a4c1525f8a73bddc5a742ca0` |
| v2.1.6.0 | `3c000ec2e7a6e619c60d0f8c9e48ad12bd06d4f5` |
| ログ | `install-performance - v2.1.6.0.log` / `install-performance - v3.0.0.0.log` |
| 操作 | 同一filesystem / DB断面から20 ZIPを保留へ投入、既所持警告を無視してinstall、新規画面で追加した譜面群を削除。断面一致は提供者の条件説明。 |
| 回数 | 各版1回。hardware、OS cache、packageの全内容・bytes、DBファイルbytesは未取得。 |

入力ログはリポジトリへ同梱しない。以下のSHA-256とmarker / 記録時刻で元資料を識別する。本書には規模と必要な集計だけを残し、利用者の絶対pathや譜面名は転載しない。

```text
install-performance - v2.1.6.0.log
SHA-256: 57caa2255ab2d5571ec774cf0c6703b4d5f1b161f5fc2fb225a6a2c26f77b6ce

install-performance - v3.0.0.0.log
SHA-256: 5aa5c6729cb418416c038ab69f0a46f9f8dc4ebb60feb47467148db404477aaa
```

この文書追加ではアプリ実行・benchmarkを再実施していない。出典は提供ログの集計と対象HEADの文書・計測経路の確認である。

## 規模の観測値

時刻は元ログの表記をそのまま使う。特記のないmarkerはv3ログから取得し、両版で一致する主要件数はv2でも確認した。

| データ | 観測値 | 出典marker / 時刻 / 意味 |
| --- | ---: | --- |
| BMS storage rows | 211,522 | `song_tbl_load_projection` 23:27:26.0163 / `rows` |
| BMSON storage rows | 1,065 | 同上 / `bmsonRows` |
| owned chart合計 | 212,587 | `everything_scan success` 23:27:39.9981 / `charts`。BMS + BMSONとも一致。 |
| 譜面directory | 30,582 | 同上 / `chartDirectoryCount` |
| normal folder差分対象 | 33,386 | `lr2_normal_folder_mtime_diff` 23:27:41.6939 / `directories` |
| folder既存row読込 | 33,747 | `lr2_normal_folder_mtime_snapshot_prefetch` 23:27:26.5252 / `existingRows` |
| filesystem directory surface | 35,426 | `everything_scan directory_surface` 23:27:39.9980 / `directories` |
| reverse lookupキー合計 | 8,265,303 | `resource_index_build` 23:27:40.8431 / `reverseLookupKeys` |
| chart-directory別resource key登録合計 | 12,432,127 | 同上 / `chartRelativeKeys` |
| Audio key登録 | 11,390,644 | `song_tbl_file_check_cache_counts` 23:27:41.7288 / `audioResourceKeyEntries` |
| Image key登録 | 1,033,291 | 同上 / `imageResourceKeyEntries` |
| Movie key登録 | 8,192 | 同上 / `movieResourceKeyEntries` |
| Audio / Image / Movie query hits | 11,568,473 / 1,046,336 / 9,565 | `everything_scan success` 23:27:39.9981。合計12,624,374は列挙hitでありreverse key数とは別。 |
| native packed payload | 266,560,518 bytes | `resource_index_build` / `payloadBytes`。最終RAM使用量ではない。 |
| distinct primary hash | 212,376 | `installed_primary_hash_lookup build` 23:28:12.7409 / `primaryHashes` |
| full installed lookup | 424,752 keys / 425,170 dirRefs | `installed_chart_lookup_index build` 23:28:41.4301。2種のhashや複数配置の参照を含み、owned row数ではない。 |
| digest読込 | 211,311 rows | `song_tbl_load_projection` / `chartDigestRows`。partial cacheの観測。 |
| chart-info hydration | raw 212,376 / usable 212,355 / parse failure 21 rows | `chart_info_hydration done` 23:27:51.3476。各projectionの件数であり、DB全体容量を示さない。 |
| maintenance hydration | 212,587 rows / keys | `maintenance_hydration done` 23:27:55.8846 |
| playlist | 505表 / 577,864 entries | `playlist_entries_hydration done` 23:27:50.2772。active 570,541 / removed 7,323。 |
| custom-folder output | 309,450 physical entries | `playlist_custom_folder_output_repair physical_signature_done` 23:28:04.5868 |
| score初回読込 / IR cache読込 | 18,172 / 18,347 rows | `score_tbl_load` 23:27:26.2773 / `ranking_cache_refresh done` 23:27:51.6673。全プレイ履歴件数ではない。 |

「800万リソース」は本プロファイルではreverse lookupキーの規模として使う。extension除去、カテゴリ、chart-directoryからの相対key、所有関係があるため、実ファイル・directory別key登録・reverse key・DB rowは相互に同数ではない。

## Packageと差分の観測

両版の `auto_install_prepare` は `discovered=20 pendingAdd=20 autoInstall=0`。その後の `install_chart_packages` は20回で、`addedFiles` の分布は1譜面のpackageが17、2譜面が2、3譜面が1、合計24譜面。インストール失敗は0。

`delete_library_result` は両版とも `input=24 canonical=24 removed=24 failures=0 folderDeletes=20 fileDeletes=0`。v2は23:26:26.3593、v3は23:30:35.0887。

`reverse_lookup_incremental_update reason=install_package` は両版20回、`updatedHashes=0` が18回、残りは32と1。これは逆引きmappingの変更件数であり、実際に展開・検査したresource数ではない。

`maintenance_rescan_chunk` の `fileExistsFallback` 合計は両版15,994回。1譜面のchunkに667回・1,275回などの存在確認があるが、同梱されたdistinctファイル数・package別最大resource数はこのmarkerから確定しない。「複数譜面・数百resourceを含み得る」は提供者が示した設計前提として採用し、合成検証ケースと実測値を区別する。

## 操作時間の参考値

同じ操作に対応する最外層 `ui_suppress begin depth=1` から `end depth=0` の時刻差。確認回答待ちは含めないが、一部の表示反映・推定結果・後続索引処理も外側にあるため、厳密なoperation terminal指標ではない。

| 操作 | v2開始〜終了 | v3開始〜終了 | v2秒 | v3秒 |
| --- | --- | --- | ---: | ---: |
| ZIP投入 | 23:24:44.2241〜23:24:57.4087 | 23:28:40.2013〜23:28:47.4304 | 13.1846 | 7.2291 |
| install | 23:25:07.6457〜23:25:36.5921 | 23:29:08.3678〜23:29:29.2239 | 28.9464 | 20.8561 |
| delete | 23:26:19.3334〜23:26:27.0800 | 23:30:18.3165〜23:30:35.0887 | 7.7466 | 16.7722 |

この対ではdeleteの増加を観測した。合計の短縮でdeleteの悪化を相殺しない。一方、各版1回かつ以下の条件差があるため、他環境へ適用する性能保証や実装ごとの寄与時間は導かない。

- v2の `virtual_order_prewarm` は23:25:13.5261まで継続し、投入とinstall前半に重なる。v3は23:28:28.6154にpost work完了後、投入した。
- 保留画面のvisible columnはv2が10、v3が13。新規画面は両版12。
- v3の `install_chart_packages.moveMs` はreceipt経路でDB / maintenance / state callback等も囲み、個別fieldの0は仕事が消えたことを意味しない。`InstallPackagesWithFileMutationReceipts` と [package install owner](../../BeMusicSeeker/Models/BMSLibrary.PackageInstall.cs) を含めて範囲を確認する。inline chart-info処理はpackageログの後にもある。
- `startup_initialization_complete` のrequired / post分類は版で異なる。marker名が同じでも同じ仕事量とは限らない。

## この記録が保証しないこと

特定CPU / RAM構成への推奨、DB file bytes、packageの全展開bytes、最大resource数、bucket fan-outの分布、反復時の分位点、最新HEADでの性能passは未確定。今後の測定はこの記録を書き換えて結果を上書きせず、対象commitと条件付きの別記録として残す。
