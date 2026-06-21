# キーワード検索構文ガイド

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](keyword-search-syntax-guide.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](keyword-search-syntax-guide.md)

BeMusicSeeker の検索ボックスでは、単語検索に加えて、複数語 AND、カラム指定、フレーズ検索、除外条件、OR、正規表現を利用できます。

この構文は主に以下の検索欄で利用できます。

- メインの譜面一覧
- プレイリスト詳細
- プレイリスト一覧
- プレイログ

---

## 入力支援

検索ボックスには、カラム指定検索を入力しやすくする field 補完と、過去の検索を呼び出す検索履歴があります。

### field 補完

検索欄で field 名を入力し始めると、利用できる field が候補として表示されます。

```text
tit
```

上の例では `title:` を候補として選べます。

除外条件でも補完できます。

```text
-ar
```

上の例では `-artist:` を候補として選べます。

候補の操作は以下の通りです。

| 操作 | 挙動 |
| :--- | :--- |
| `↑` / `↓` | 候補を選択 |
| `Enter` / `Tab` | 選択中の候補を確定 |
| `Esc` | 候補を閉じる |
| `Ctrl+Space` | 候補を表示 |
| マウスクリック | 候補を確定 |

field 補完は field 名だけを補完します。
`title:alpha` のように `:` の後に検索語を入力している場合、field 補完は表示されません。

### `playlist:` / `ref:` / `table:` のプレイリスト名補完

メイン譜面一覧、プレイリスト詳細、プレイログでは、`playlist:` / `ref:` / `table:` の検索語を入力すると、導入済みプレイリスト名が候補として表示されます。

```text
playlist:Sat
```

上の例では `Satellite Sub` のようなプレイリスト名を候補として選べます。
候補を確定すると、スペース、引用符、バックスラッシュ、`|` を含む名前だけが自動的にダブルクォートで囲まれます。

```text
playlist:"Satellite Sub"
ref:GENOSIDE
table:GENOSIDE
```

### 検索履歴

検索欄が空の状態でフォーカスしたとき、または `↓` を押したときに検索履歴が表示されます。
履歴を選ぶと、検索欄全体がその検索文字列に置き換わります。

履歴は以下のタイミングで保存されます。

- `Enter` を押したとき
- 検索欄からフォーカスが外れたとき
- 履歴候補を選択したとき

履歴は通常の検索欄とプレイリスト一覧の検索欄で別々に保存されます。
保存件数はそれぞれ最大 20 件で、同じ検索文字列は重複せず、最後に使ったものが先頭に移動します。

---

## 基本検索

検索語を入力すると、対象カラムのどこかにその文字列を含む行が表示されます。
大文字・小文字は区別しません。

```text
alpha
```

複数の語をスペース区切りで入力した場合は、すべての語に一致する行だけが表示されます。

```text
alpha artist
```

上の例は `alpha` と `artist` の両方に一致する行を表示します。

---

## 検索対象カラム

### メイン譜面一覧

通常検索では、以下が検索対象です。

- TITLE
- ARTIST
- GENRE
- TAG
- PATH
- PLAYLIST 列の表示記号
- MD5
- SHA256

### プレイリスト詳細

通常検索では、メイン譜面一覧の対象に加えて、以下も検索対象です。

- memo
- comment

### プレイリスト一覧

通常検索では、以下が検索対象です。

- playlist id
- name
- symbol

### プレイログ

通常検索では、以下が検索対象です。

- TITLE
- ARTIST
- PATH
- FOLDER 列の表示ラベル
- 参照プレイリスト名
- raw hash
- MD5
- SHA256
- TYPE
- score write type
- CLEAR
- source 名
- source path
- プレイ日付 (`yyyy-MM-dd`)

プレイログでは、表示される列すべてが通常検索の対象になるわけではありません。BEST DJ、BEST RATE、BEST EXSCORE、BP、COMBO、OPTION、OP HISTORY、PLAY EXSCORE、JUDGES などは、現在は通常検索・field 指定検索の対象外です。

---

## カラム指定検索

`field:keyword` の形で、検索対象を特定のカラムに限定できます。

```text
title:alpha
artist:xi
sha256:abcdef
```

`A:` から `Z:` / `a:` から `z:` で始まる token は Windows のドライブレターを含むパスとして扱い、field 指定にはしません。
そのため、フルパスや drive-relative path は引用符なしでそのまま検索できます。

```text
D:\BMS\
D:
```

複数指定した場合は AND 検索です。

```text
title:alpha artist:xi
```

### メイン譜面一覧・プレイリスト詳細で使える field

| field | 対象 |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `genre` | GENRE |
| `tag` | TAG |
| `path` | PATH |
| `playlist` / `ref` / `table` | 参照プレイリスト名。PLAYLIST 列の短い表示記号ではなく、ツールチップに表示されるフルのプレイリスト名を検索対象にします |
| `md5` / `hash` | MD5 |
| `sha256` | SHA256 |
| `level` | `chart_info.level` |
| `difficulty` | `chart_info.difficulty`。`beginner`, `normal`, `hyper`, `another`, `insane` も指定可 |
| `mainbpm` | `chart_info.mainbpm` |
| `maxbpm` | `chart_info.maxbpm` |
| `minbpm` | `chart_info.minbpm` |
| `duration` / `length` | 演奏時間。秒単位で指定 |
| `judge` / `judge%` / `judgepct` | 判定幅倍率。`judge` は `veryhard`, `hard`, `normal`, `easy`, `veryeasy` も指定可 |
| `feature` | `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll` |
| `notes` | 総ノーツ数 |
| `long` / `ln` | ロングノーツ数 |
| `scratch` | 通常スクラッチ + ロングスクラッチ |
| `total` | TOTAL 有効値 |
| `tn` / `t/n` | `total / notes` |
| `density` | 平均密度 |
| `peak` / `peakdensity` | 1 秒窓の最大密度 |
| `end` / `enddensity` | 終盤密度 |
| `soflan` | 変速回数 |
| `clear` | CLEAR。`NP`, `F`, `AE`, `LAE`, `EC`, `NC`, `HC`, `EXH`, `FC`, `PF`, `MAX` または表示名で指定可 |
| `rank` / `djlevel` / `dj` | DJ LEVEL。`F`, `E`, `D`, `C`, `B`, `A`, `AA`, `AAA`, `MAX` を指定可 |
| `rate` | RATE。`rateDouble` の値で、`0.95` が 95% を表す |
| `score` | SCORE |
| `combo` | COMBO |
| `bp` | BP |
| `memo` | memo。プレイリスト詳細のみ |
| `comment` | comment。プレイリスト詳細のみ |

数値 field は範囲指定と比較演算を利用できます。

```text
level:10..12
notes:>=2000
duration:<120
tn:2.0..
rate:0.95..
bp:0..10
```

`defined` / `undefined` も利用できます。`undefined` は `undef` / `null` とも書けます。`chart_info` が未構築の場合や、`level` が NULL の場合、`difficulty_defined=false` / `total_defined=false` の場合は `undefined` に一致します。数値のスコア系 field ではスコア未取得などで値がない場合に `undefined` に一致します。`rank` は DJ LEVEL が空の場合に `undefined` に一致します。`clear` は `NO SONG` / `NO PLAY` も CLEAR 種別として扱うため、常に `defined` に一致します。

```text
total:undefined
total:undef
total:null
difficulty:defined
-feature:random
score:defined
rank:undefined
```

`clear` は CLEAR 種別の完全一致です。表示名に加えて、以下の短縮表現を利用できます。

| 入力 | 対象 |
| :--- | :--- |
| `NP` | NO PLAY |
| `F` | FAILED |
| `AE` | ASSIST |
| `LAE` | L-ASSIST |
| `EC` | EASY CLEAR |
| `NC` | CLEAR |
| `HC` | HARD CLEAR |
| `EXH` | EX HARD |
| `FC` | FULL COMBO |
| `PF` | PERFECT |
| `MAX` | MAX |

### プレイリスト一覧で使える field

| field | 対象 |
| :--- | :--- |
| `id` | playlist id |
| `name` | name |
| `symbol` | symbol |

### プレイログで使える field

プレイログでは、譜面一覧・プレイリスト詳細とは別の field が使われます。

| field | 対象 |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `path` | PATH |
| `folder` | FOLDER 列の表示ラベル |
| `playlist` / `ref` / `table` | 参照プレイリスト名。FOLDER 列の短い表示ラベルではなく、解決されたプレイリスト名を検索対象にします |
| `md5` | MD5。beatoraja 履歴では、所持譜面などから MD5 を解決できた場合だけ値があります |
| `hash` | raw hash。LR2 では記録元の MD5、beatoraja では記録元の SHA256 を検索対象にします |
| `sha256` | SHA256 |
| `date` | プレイ日付。`yyyy-MM-dd`, `yyyy/M/d`, `yyyy/MM/dd`, `yyyyMMdd` を指定可 |
| `year` | プレイ年。例: `2026` |
| `month` | プレイ月。`yyyy-MM`, `yyyy/MM`, または `1` から `12` を指定可 |
| `type` / `kind` | TYPE と score write type。`type` が主名で、`kind` は互換エイリアスです。例: `score`, `bp`, `clear`, `combo`, `play` |
| `clear` | 更新前または更新後の CLEAR。通常の `clear:` と同じ短縮表現を指定可 |
| `oldclear` | 更新前の CLEAR。通常の `clear:` と同じ短縮表現を指定可 |
| `newclear` | 更新後の CLEAR。通常の `clear:` と同じ短縮表現を指定可 |
| `finalized` | 確定済みかどうか。`true`, `1`, `yes`, `y`, `finalized` / `false`, `0`, `no`, `n`, `unfinalized`, `pending` を指定可 |
| `source` | source 名と source path |

プレイログの `date` / `year` / `month` / `clear` / `oldclear` / `newclear` / `finalized` は専用判定です。`level:10..12` のような範囲指定や比較演算は使いません。
プレイログの `clear:defined` は、更新後 CLEAR がある行に一致します。譜面一覧・プレイリスト詳細の `clear` は `NO SONG` / `NO PLAY` も CLEAR 種別として扱うため常に `defined` ですが、プレイログでは意味が異なります。
上部サマリーカードのクリック絞り込みも、この検索 field を内部的に使います。たとえば SCORE 更新カードは `type:score`、EASY カードは `type:clear newclear:EC` 相当の条件として、検索欄の条件と AND で適用されます。複数カードを選んだ場合、カード同士は OR で扱われます。
また、プレイログでは `level`、`difficulty`、`notes`、`rank`、`rate`、`score`、`bp` など、譜面一覧・プレイリスト詳細用の field は使えません。未知の field として扱われます。

---

## フレーズ検索

スペースを含む文字列を 1 つの検索語として扱いたい場合は、ダブルクォートで囲みます。

```text
"alpha title"
title:"alpha title"
```

未閉じのクォートは、入力末尾までをフレーズとして扱います。

```text
"alpha title
```

上の例は `"alpha title"` とほぼ同じ意味になります。

### クォート内のエスケープ

クォート内では、以下だけが特別にエスケープされます。

| 入力 | 意味 |
| :--- | :--- |
| `\"` | `"` |
| `\\` | `\` |

例:

```text
title:"alpha \"quoted\""
```

---

## 除外検索

検索語の先頭に `-` を付けると、その条件に一致する行を除外します。

```text
alpha -artist:beta
```

上の例は、`alpha` に一致し、かつ ARTIST に `beta` を含まない行を表示します。

field 指定やフレーズ検索、正規表現とも組み合わせられます。

```text
-title:"old version"
-path:backup
-title:re:^test
```

`foo-bar` のように先頭以外にある `-` は通常の文字として扱います。

---

## OR 検索

1 つの検索語の中で `|` を使うと OR 検索になります。

```text
alpha|beta
```

上の例は、`alpha` または `beta` に一致する行を表示します。

field 指定と組み合わせた場合、OR はその field の中だけで評価されます。

```text
title:alpha|beta
title:"alpha title"|beta
```

上の例は、TITLE に `alpha` または `beta` が含まれる行を表示します。

### OR の注意点

`title:alpha|artist:beta` のような「カラムをまたいだ OR」は扱いません。
この場合、`artist:beta` は TITLE 内で探す文字列として扱われます。

空の候補は無視されます。

```text
alpha|
|alpha
alpha||beta
```

すべての候補が空の場合は不正な条件として扱われ、何にも一致しません。

---

## 正規表現検索

`re:pattern` で正規表現検索ができます。

```text
re:^alpha
```

field 指定と組み合わせる場合は、`field:re:pattern` と書きます。

```text
title:re:^alpha
path:re:\\BMS\\.*\\.bms$
```

除外条件としても使えます。

```text
-title:re:^test
```

正規表現は大文字・小文字を区別せず、カルチャ非依存で評価されます。

### 正規表現のタイムアウト

正規表現には暴走対策として、1 条件あたり 100ms のタイムアウトがあります。
非常に重い正規表現は途中で打ち切られ、その条件は一致しなかったものとして扱われます。

---

## 不正な構文の扱い

不正な条件は、エラー表示を出さずに「一致しない条件」として扱われます。
そのため、AND 検索の中に不正な条件が含まれると、結果が 0 件になることがあります。

不正扱いになる例:

```text
unknown:alpha
title:
-
|
title:re:[
```

| 例 | 理由 |
| :--- | :--- |
| `unknown:alpha` | 未知の field |
| `title:` | field 指定の検索語が空 |
| `-` | 除外条件の中身が空 |
| `\|` | OR の候補がすべて空 |
| `title:re:[` | 正規表現として不正 |

`:alpha` のように field 名が空の場合は、field 指定ではなく通常の検索語として扱われます。
つまり、文字列 `:alpha` を含む行を探します。

---

## よく使う例

TITLE と ARTIST の両方で絞り込む:

```text
title:alpha artist:xi
```

MD5 または SHA256 の一部で探す:

```text
md5:abcdef
sha256:123456
```

プレイリスト詳細で memo を検索する:

```text
memo:"favorite chart"
```

バックアップフォルダを除外する:

```text
-path:backup
```

TITLE が `alpha` で始まる譜面を探す:

```text
title:re:^alpha
```

TITLE に `alpha` または `beta` を含み、ARTIST に `test` を含まない譜面を探す:

```text
title:alpha|beta -artist:test
```
