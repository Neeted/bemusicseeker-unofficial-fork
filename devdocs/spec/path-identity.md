# Path Identity Policy

本アプリは Windows / NTFS を主なターゲット環境とするが、永続化された `path` の同一性はファイルシステムの一般的な大文字小文字規則へ暗黙に寄せない。

パス長や実ファイル I/O の長パス対応は [path-length-and-io.md](path-length-and-io.md) を参照する。本資料は、DB 行の同一性として path 文字列をどう比較するかだけを扱う。

## 基本方針

- `song.path`, `folder.path`, `maintenance.path`, `bmson_song.path`, `install.path` など、DB の主キーまたは行同一性として使う `path` は **case-sensitive な exact string** として扱う。
- 現在スキャンで得た path 集合を正本にする。同期処理は、この exact path 集合に一致する行を更新し、一致しない行を stale row として削除対象にする。
- 大文字小文字だけ異なる path は別ファイルとして扱う。片方の `favorite`, `tag`, `adddate`, `maintenance` 情報をもう片方へ自動移行しない。
- `File.Exists` や Windows の通常のパス解決結果を、DB 行の削除判定に使わない。NTFS では case-only に違う path が同じ実体へ解決されることがあるため、削除判定はスキャン結果の exact set で行う。

## `COLLATE NOCASE` の扱い

`COLLATE NOCASE` は path identity には使わない。

使用してよい例:

- `.lr2folder` など拡張子や表示用ソートのように、行同一性ではない比較。
- md5 / sha256 / bundle id など、path ではない識別子の正規化や表示順。
- Windows 向けの探索範囲最適化。ただし、その結果を主キー更新や prune 判定へ直接使う場合は exact path へ戻して扱う。

避ける例:

- `path TEXT PRIMARY KEY COLLATE NOCASE` の一時テーブルで current path を保持する。
- `WHERE existing.path = current.path COLLATE NOCASE` で `song`, `folder`, `maintenance`, `bmson_song`, `install` の更新対象を決める。
- `NOT EXISTS (... COLLATE NOCASE)` で stale row prune を行う。
- case-only に異なる旧 path を現在 path へ単純 `UPDATE` する。

## LR2 `song.db` 同期

OpenLR2 / LR2 互換の `song` / `folder` schema は `path TEXT primary key` であり、`COLLATE NOCASE` ではない。そのため、従来版 BeMusicSeeker や LR2 が作成した DB には case-only path variant が複数行として存在し得る。

LR2 同期では次の順で扱う。

1. 現在スキャン結果から exact current path set を作る。
2. `song` / `maintenance` の upsert は exact path match で行う。
3. current path に exact match しない既存 `song` 行は stale row として削除する。
4. stale `song` 行に対応する `maintenance` と orphan digest を削除する。
5. case-only variant 間で保存値を移行しない。

これにより、Windows の通常環境では実質的に従来どおり動きつつ、case-sensitive filesystem や case-only variant を持つ既存 LR2 DB に対しても、同期処理が制約違反で停止しない。

## 通常スキャン差分

通常スキャンで得た譜面 path も exact set として扱う。

- `ChartScanResult.ChartFilePaths` と `ChartFileEntriesByPath` は case-sensitive な exact key にする。
- `RootFileEnumerationResult` の group 内 path 集合 / entry map と、managed fallback scan の chart file 集約も exact key にする。
- 既存 `song` / `bmson_song` 行との比較は exact path で行う。
- case-only に異なる既存行は、現在スキャンに存在しない stale row として削除対象になる。
- 現在スキャンに存在する case-only variant は、新しい exact path の行として通常の追加 / upsert 対象になる。
- `OwnedChartCollectionState` や `LibraryChartRefIndexSnapshot` など、アプリ内の owned chart path lookup も exact key にする。
- MD5 が同じでも、case-only variant 間では `favorite`, `tag`, `adddate` などの保存列 relink を行わない。

ディレクトリ探索、リソース探索、`.lr2folder` の prefix scope、拡張子判定など、ファイルシステム探索に近い処理は Windows / NTFS 前提の case-insensitive 比較を使うことがある。その結果を DB 行同一性へ使う境界では、必ず exact path に戻して扱う。
