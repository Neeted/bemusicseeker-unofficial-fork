# Keyword Search Syntax Guide

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](keyword-search-syntax-guide.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](keyword-search-syntax-guide.md)

BeMusicSeeker search boxes support regular word search, multi-word AND search, field-qualified search, phrase search, exclusion conditions, OR, and regular expressions.

This syntax is mainly available in the following search boxes:

- Main chart list
- Playlist detail
- Playlist list
- Play Log

---

## Input Assistance

Search boxes provide field completion for easier field-qualified search input, and search history for recalling previous searches.

### Field Completion

When you start typing a field name in a search box, available fields are shown as candidates.

```text
tit
```

In the example above, you can select `title:` as a candidate.

Completion also works for exclusion conditions.

```text
-ar
```

In the example above, you can select `-artist:` as a candidate.

Candidate controls are as follows.

| Operation | Behavior |
| :--- | :--- |
| `↑` / `↓` | Select a candidate |
| `Enter` / `Tab` | Confirm the selected candidate |
| `Esc` | Close candidates |
| `Ctrl+Space` | Show candidates |
| Mouse click | Confirm a candidate |

Field completion completes only field names.
If you have already typed a search word after `:`, such as `title:alpha`, field completion is not shown.

### Playlist Name Completion for `playlist:` / `ref:` / `table:`

In the main chart list, playlist detail, and Play Log, entering a search term for `playlist:`, `ref:`, or `table:` shows installed playlist names as candidates.

```text
playlist:Sat
```

In the example above, you can select a playlist name such as `Satellite Sub`.
When a candidate is confirmed, only names that contain spaces, quotes, backslashes, or `|` are automatically wrapped in double quotes.

```text
playlist:"Satellite Sub"
ref:GENOSIDE
table:GENOSIDE
```

### Search History

Search history is shown when the search box is empty and focused, or when you press `↓`.
Selecting a history item replaces the entire search box with that search string.

History is saved at the following times:

- When you press `Enter`
- When focus leaves the search box
- When you select a history candidate

History is stored separately for normal search boxes and the playlist-list search box.
Each history keeps up to 20 entries. Duplicate search strings are not stored; when the same string is used again, it moves to the top.

---

## Basic Search

Typing a search word shows rows where any target column contains that string.
Matching is case-insensitive.

```text
alpha
```

When multiple words are separated by spaces, only rows matching all words are shown.

```text
alpha artist
```

The example above shows rows that match both `alpha` and `artist`.

---

## Target Columns

### Main Chart List

Normal search targets the following columns:

- TITLE
- ARTIST
- GENRE
- TAG
- PATH
- Display symbols in the PLAYLIST column
- MD5
- SHA256

### Playlist Detail

Normal search targets the same columns as the main chart list, plus:

- memo
- comment

### Playlist List

Normal search targets:

- playlist id
- name
- symbol

### Play Log

Normal search targets:

- TITLE
- ARTIST
- PATH
- Display labels in the FOLDER column
- Referenced playlist names
- raw hash
- MD5
- SHA256
- TYPE
- score write type
- CLEAR
- source name
- source path
- play date (`yyyy-MM-dd`)

In the Play Log, not every displayed column is part of normal search. BEST DJ, BEST RATE, BEST EXSCORE, BP, COMBO, OPTION, OP HISTORY, PLAY EXSCORE, and JUDGES are not currently targeted by normal search or field-qualified search.

---

## Field-Qualified Search

Use `field:keyword` to restrict the search target to a specific column.

```text
title:alpha
artist:xi
sha256:abcdef
```

Tokens beginning with `A:` through `Z:` or `a:` through `z:` are treated as Windows paths containing drive letters, not as field-qualified terms.
This means full paths and drive-relative paths can be searched as-is without quotes.

```text
D:\BMS\
D:
```

Multiple field-qualified terms are combined with AND.

```text
title:alpha artist:xi
```

### Fields Available in the Main Chart List and Playlist Detail

| field | Target |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `genre` | GENRE |
| `tag` | TAG |
| `path` | PATH |
| `playlist` / `ref` / `table` | Referenced playlist name. Searches the full playlist name shown in the tooltip, not the short display symbol in the PLAYLIST column |
| `md5` / `hash` | MD5 |
| `sha256` | SHA256 |
| `level` | `chart_info.level` |
| `difficulty` | `chart_info.difficulty`. `beginner`, `normal`, `hyper`, `another`, and `insane` can also be specified |
| `mainbpm` | `chart_info.mainbpm` |
| `maxbpm` | `chart_info.maxbpm` |
| `minbpm` | `chart_info.minbpm` |
| `duration` / `length` | Play duration, specified in seconds |
| `judge` / `judge%` / `judgepct` | Judge width multiplier. `judge` also accepts `veryhard`, `hard`, `normal`, `easy`, and `veryeasy` |
| `feature` | `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll` |
| `notes` | Total notes |
| `long` / `ln` | Long-note count |
| `scratch` | Normal scratch + long scratch |
| `total` | Effective TOTAL value |
| `tn` / `t/n` | `total / notes` |
| `density` | Average density |
| `peak` / `peakdensity` | Maximum density in a 1-second window |
| `end` / `enddensity` | Ending density |
| `soflan` | Number of speed changes |
| `clear` | CLEAR. Accepts `NP`, `F`, `AE`, `LAE`, `EC`, `NC`, `HC`, `EXH`, `FC`, `PF`, `MAX`, or display names |
| `rank` / `djlevel` / `dj` | DJ LEVEL. Accepts `F`, `E`, `D`, `C`, `B`, `A`, `AA`, `AAA`, `MAX` |
| `rate` | RATE. This is the `rateDouble` value, so `0.95` means 95% |
| `score` | SCORE |
| `combo` | COMBO |
| `bp` | BP |
| `memo` | memo. Playlist detail only |
| `comment` | comment. Playlist detail only |

Numeric fields support ranges and comparison operators.

```text
level:10..12
notes:>=2000
duration:<120
tn:2.0..
rate:0.95..
bp:0..10
```

`defined` / `undefined` are also available. `undefined` can also be written as `undef` or `null`.
When `chart_info` has not been built, when `level` is NULL, or when `difficulty_defined=false` / `total_defined=false`, the value matches `undefined`.
For numeric score-related fields, missing values such as scores that have not been acquired match `undefined`.
`rank` matches `undefined` when DJ LEVEL is empty.
`clear` always matches `defined` because `NO SONG` and `NO PLAY` are also treated as CLEAR types.

```text
total:undefined
total:undef
total:null
difficulty:defined
-feature:random
score:defined
rank:undefined
```

`clear` is an exact match against the CLEAR type.
In addition to display names, the following abbreviations can be used.

| Input | Target |
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

### Fields Available in the Playlist List

| field | Target |
| :--- | :--- |
| `id` | playlist id |
| `name` | name |
| `symbol` | symbol |

### Fields Available in the Play Log

The Play Log uses a field set separate from the main chart list and playlist detail.

| field | Target |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `path` | PATH |
| `folder` | Display labels in the FOLDER column |
| `playlist` / `ref` / `table` | Referenced playlist name. Searches the resolved playlist name, not the short display label in the FOLDER column |
| `md5` | MD5. In beatoraja history, this has a value only when MD5 can be resolved from an owned chart or similar source |
| `hash` | raw hash. LR2 targets the recorded MD5, and beatoraja targets the recorded SHA256 |
| `sha256` | SHA256 |
| `date` | Play date. Accepts `yyyy-MM-dd`, `yyyy/M/d`, `yyyy/MM/dd`, and `yyyyMMdd` |
| `year` | Play year. Example: `2026` |
| `month` | Play month. Accepts `yyyy-MM`, `yyyy/MM`, or `1` through `12` |
| `type` / `kind` | TYPE and score write type. `type` is the primary name; `kind` is a compatibility alias. Examples: `score`, `bp`, `clear`, `combo`, `play` |
| `clear` | CLEAR before or after the update. Accepts the same abbreviations as normal `clear:` |
| `oldclear` | CLEAR before the update. Accepts the same abbreviations as normal `clear:` |
| `newclear` | CLEAR after the update. Accepts the same abbreviations as normal `clear:` |
| `finalized` | Whether the row is finalized. Accepts `true`, `1`, `yes`, `y`, `finalized` / `false`, `0`, `no`, `n`, `unfinalized`, `pending` |
| `source` | source name and source path |

Play Log `date` / `year` / `month` / `clear` / `oldclear` / `newclear` / `finalized` use dedicated matching. Range and comparison operators such as `level:10..12` are not used for them.
In the Play Log, `clear:defined` matches rows that have a CLEAR value after the update. This differs from the main chart list and playlist detail, where `clear` always matches `defined` because `NO SONG` and `NO PLAY` are also treated as CLEAR types.
Click filtering from the top summary cards also uses these search fields internally. For example, the SCORE update card is equivalent to `type:score`, and the EASY card is equivalent to `type:clear newclear:EC`. Card filters are applied with AND against the search-box filter. When multiple cards are selected, the cards are combined with OR.
The Play Log also does not support main-chart-list / playlist-detail fields such as `level`, `difficulty`, `notes`, `rank`, `rate`, `score`, and `bp`. They are treated as unknown fields.

---

## Phrase Search

To treat a string containing spaces as a single search word, wrap it in double quotes.

```text
"alpha title"
title:"alpha title"
```

An unclosed quote is treated as a phrase through the end of the input.

```text
"alpha title
```

The example above is almost equivalent to `"alpha title"`.

### Escapes in Quotes

Inside quotes, only the following sequences are specially escaped.

| Input | Meaning |
| :--- | :--- |
| `\"` | `"` |
| `\\` | `\` |

Example:

```text
title:"alpha \"quoted\""
```

---

## Exclusion Search

Prefix a search word with `-` to exclude rows that match that condition.

```text
alpha -artist:beta
```

The example above shows rows that match `alpha` and do not contain `beta` in ARTIST.

Exclusion can be combined with field-qualified search, phrase search, and regular expressions.

```text
-title:"old version"
-path:backup
-title:re:^test
```

A `-` that appears anywhere other than the start, such as in `foo-bar`, is treated as a normal character.

---

## OR Search

Use `|` inside a single search word for OR search.

```text
alpha|beta
```

The example above shows rows that match either `alpha` or `beta`.

When combined with a field qualifier, OR is evaluated only within that field.

```text
title:alpha|beta
title:"alpha title"|beta
```

The examples above show rows whose TITLE contains `alpha` or `beta`.

### OR Cautions

Cross-column OR such as `title:alpha|artist:beta` is not supported.
In that case, `artist:beta` is treated as a string to search for inside TITLE.

Empty alternatives are ignored.

```text
alpha|
|alpha
alpha||beta
```

If all alternatives are empty, the condition is treated as invalid and matches nothing.

---

## Regular Expression Search

Use `re:pattern` for regular expression search.

```text
re:^alpha
```

When combining it with a field qualifier, write `field:re:pattern`.

```text
title:re:^alpha
path:re:\\BMS\\.*\\.bms$
```

It can also be used as an exclusion condition.

```text
-title:re:^test
```

Regular expressions are evaluated case-insensitively and culture-independently.

### Regular Expression Timeout

Regular expressions have a 100 ms timeout per condition as protection against runaway patterns.
Very expensive regular expressions are stopped partway through and treated as not matching that condition.

---

## Invalid Syntax Handling

Invalid conditions do not show an error. They are treated as "conditions that do not match".
Therefore, if an AND search contains an invalid condition, the result may become 0 rows.

Examples treated as invalid:

```text
unknown:alpha
title:
-
|
title:re:[
```

| Example | Reason |
| :--- | :--- |
| `unknown:alpha` | Unknown field |
| `title:` | The search word for the field qualifier is empty |
| `-` | The exclusion condition body is empty |
| `\|` | All OR alternatives are empty |
| `title:re:[` | Invalid as a regular expression |

If the field name is empty, as in `:alpha`, it is treated as a normal search word rather than a field qualifier.
In other words, it searches for rows containing the string `:alpha`.

---

## Common Examples

Filter by both TITLE and ARTIST:

```text
title:alpha artist:xi
```

Search by part of an MD5 or SHA256:

```text
md5:abcdef
sha256:123456
```

Search memo in playlist detail:

```text
memo:"favorite chart"
```

Exclude backup folders:

```text
-path:backup
```

Search for charts whose TITLE starts with `alpha`:

```text
title:re:^alpha
```

Search for charts whose TITLE contains `alpha` or `beta`, and whose ARTIST does not contain `test`:

```text
title:alpha|beta -artist:test
```
