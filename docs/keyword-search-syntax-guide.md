# キーワード検索構文ガイド

BeMusicSeeker の検索ボックスでは、単語検索に加えて、複数語 AND、カラム指定、フレーズ検索、除外条件、OR、正規表現を利用できます。

この構文は主に以下の検索欄で利用できます。

- メインの譜面一覧
- プレイリスト詳細
- プレイリスト一覧

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
- 参照プレイリスト情報
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

---

## カラム指定検索

`field:keyword` の形で、検索対象を特定のカラムに限定できます。

```text
title:alpha
artist:xi
sha256:abcdef
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
| `playlist` | 参照プレイリスト情報 |
| `ref` | `playlist` と同じ |
| `md5` | MD5 |
| `hash` | `md5` と同じ |
| `sha256` | SHA256 |
| `memo` | memo。プレイリスト詳細のみ |
| `comment` | comment。プレイリスト詳細のみ |

### プレイリスト一覧で使える field

| field | 対象 |
| :--- | :--- |
| `id` | playlist id |
| `name` | name |
| `symbol` | symbol |

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
