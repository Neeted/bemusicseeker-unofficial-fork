# chart_info DataGrid 表示・検索拡張メモ

## 目的

`chart_info` に保存した beatoraja / jbms-parser 互換メタデータを、通常の譜面一覧とプレイリスト詳細 DataGrid で利用できるようにする。

今回の v1 は表示、ソート、未定義警告、keyword search field 追加までを範囲にする。`chart_info` schema / parser version は変更しない。

## 追加カラム

カラム選択メニューでは `PLAYLIST` と `CLEAR` の間に区切りを置き、以下の順で追加する。

| Header | Source | 表示 |
| :--- | :--- | :--- |
| `LEVEL` | `chart_info.level` | NULL は空表示、数値 sort key は 0、薄い警告色 |
| `DIFFICULTY` | `chart_info.difficulty` | `BEGINNER/NORMAL/HYPER/ANOTHER/INSANE` は色付き表示。`difficulty_defined=false` は警告色 |
| `MAINBPM` | `mainbpm` | 整数相当なら整数、その他は最大 2 桁 |
| `MAXBPM` | `maxbpm` | 同上 |
| `MINBPM` | `minbpm` | 同上 |
| `DURATION` | `length` | 秒単位で `123.13 s` |
| `JUDGE` | `judge` | `VERYHARD/HARD/NORMAL/EASY/VERYEASY` を色付き表示 |
| `JUDGE%` | `judge` | 数値表示 |
| `FEATURE` | `feature` | `LN MINE RANDOM LN(#LNMODE) CN HCN STOP SCROLL` |
| `NOTES` | `notes` | 総ノーツ。旧 LR2 score / karinotes 系ではなく chart_info 由来 |
| `LONG` | `ln` | ロングノーツ |
| `SCRATCH` | `s + ls` | スクラッチ合算 |
| `TOTAL` | `total` | 小数があれば最大 2 桁。`total_defined=false` は警告色 |
| `T/N` | `total / notes` | 小数 2 桁 |
| `DENSITY` | `density` | 最大 2 桁 |
| `PEAK` | `peakdensity` | 最大 2 桁 |
| `END` | `enddensity` | 最大 2 桁 |
| `SOFLAN` | `speedchange_count` | 変速回数 |

プレイリスト詳細では既存の編集可能な `LEVEL` は `ENTRY LEVEL` に改名して維持する。これは playlist entry のレベルであり、`chart_info.level` とは別物として扱う。`ENTRY LEVEL` はプレイリスト詳細でデフォルト表示、`chart_info.level` の `LEVEL` はプレイリスト詳細ではデフォルト非表示にする。

## 表示 helper

表示文字列と sort key は `ChartInfoDisplayFormatter` と行モデル側の `Chart*` property に集約する。

- `BMSFile` は通常一覧向けに `ChartInfo` から直接算出する。
- 通常一覧の BMSON 行は `PendingChartEntry` で表示されるため、`bmson_song.ChartInfo` を `PendingChartEntry.ChartInfo` として参照できるようにする。これを忘れると BMSON だけ表示列と keyword search field が未解析扱いになる。
- `PlaylistDetailSourceRow` / `PlaylistDetailRow` は `RealFile.ChartInfo` または `ResolvedBmson.ChartInfo` を利用する。
- `ChartInfo == null` は未解析扱いで、表示は空、検索では `undefined` に一致する。

## Keyword Search

通常一覧とプレイリスト詳細で以下の field を追加する。

```text
level, difficulty, mainbpm, maxbpm, minbpm, duration, length,
judge, judge%, judgepct, feature, notes, long, ln, scratch,
total, tn, t/n, density, peak, peakdensity, end, enddensity, soflan
```

数値 field は以下の形式を受ける。

```text
field:10
field:10..12
field:10..
field:..12
field:>=10
field:>10
field:<=12
field:<12
```

`defined` / `undefined` は `ChartInfo == null`、nullable 値なし、または `difficulty_defined=false` / `total_defined=false` の確認に使う。

`difficulty` は `beginner`, `normal`, `hyper`, `another`, `insane` を受ける。`feature` は `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll` を受ける。否定は既存の `-field:value` を使う。

## 残課題

- DataGrid の初期表示セットは控えめにしている。実利用でよく見る列が固まったら preset を見直す。
- `NOTES` を chart_info 由来に置き換えたため、LR2 score 側の `totalnotes` を確認したい場面があれば別名列を追加する。
- `FEATURE` は半角スペース区切り表示のみ。将来は chip / filter UI 化すると視認性が上がる。
- `distribution` と `lanenotes` は v1 では直接表示しない。密度グラフやレーン分布 UI を作る場合に別途検討する。
