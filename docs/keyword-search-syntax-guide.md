# Keyword Search Syntax Guide

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](keyword-search-syntax-guide.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](keyword-search-syntax-guide.md)

The BeMusicSeeker search boxes support simple word search as well as multi-word AND, field-qualified search, phrase search, exclusions, OR, regular expressions, and numeric ranges.

This syntax is mainly available in:

- the main chart list
- playlist detail
- playlist list

---

## Input Assistance

Search boxes provide field-name completion and search history.

### Field Completion

When you start typing a field name, available fields are shown as candidates.

```text
tit
```

In this example, `title:` can be selected.

Completion also works for exclusions.

```text
-ar
```

In this example, `-artist:` can be selected.

Candidate controls:

| Operation | Behavior |
| :--- | :--- |
| `↑` / `↓` | Select candidate |
| `Enter` / `Tab` | Confirm selected candidate |
| `Esc` | Close candidates |
| `Ctrl+Space` | Show candidates |
| Mouse click | Confirm candidate |

Field completion only completes field names. If you already typed a search term after `:`, such as `title:alpha`, field completion is not shown.

### Playlist Name Completion for `playlist:` / `ref:` / `table:`

In the main chart list and playlist detail, entering `playlist:`, `ref:`, or `table:` shows installed playlist names as candidates.

```text
playlist:Sat
```

This can select a playlist name such as `Satellite Sub`.
When confirmed, names containing spaces, quotes, backslashes, or `|` are automatically wrapped in double quotes.

```text
playlist:"Satellite Sub"
ref:GENOSIDE
table:GENOSIDE
```

### Search History

When the search box is empty and focused, or when you press `↓`, search history is shown.
Selecting a history item replaces the whole search box text.

History is saved when:

- you press `Enter`
- the search box loses focus
- you select a history candidate

History is stored separately for normal search boxes and the playlist-list search box. Each keeps up to 20 unique entries, with the most recently used entry first.

---

## Basic Search

Typing a word shows rows whose target columns contain that string. Matching is case-insensitive.

```text
alpha
```

Multiple space-separated words are treated as AND search.

```text
alpha artist
```

This shows rows that match both `alpha` and `artist`.

---

## Target Columns

### Main Chart List

Normal search targets:

- TITLE
- ARTIST
- GENRE
- TAG
- PATH
- PLAYLIST column display symbols
- MD5
- SHA256

### Playlist Detail

Playlist detail targets the main-list columns plus:

- memo
- comment

### Playlist List

Playlist-list search targets:

- playlist id
- name
- symbol

---

## Field-Qualified Search

Use `field:keyword` to restrict a search term to a specific column.

```text
title:alpha
artist:xi
sha256:abcdef
```

Tokens starting with `A:` through `Z:` or `a:` through `z:` are treated as Windows drive paths, not fields. Full paths and drive-relative paths can therefore be searched without quotes.

```text
D:\BMS\
D:
```

Multiple field terms are ANDed.

```text
title:alpha artist:xi
```

### Fields for Main Chart List and Playlist Detail

| field | Target |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `genre` | GENRE |
| `tag` | TAG |
| `path` | PATH |
| `playlist` / `ref` / `table` | Referenced playlist name. Searches the full tooltip name, not the short PLAYLIST column symbol |
| `md5` / `hash` | MD5 |
| `sha256` | SHA256 |
| `level` | `chart_info.level` |
| `difficulty` | `chart_info.difficulty`. Also accepts `beginner`, `normal`, `hyper`, `another`, `insane` |
| `mainbpm` | `chart_info.mainbpm` |
| `maxbpm` | `chart_info.maxbpm` |
| `minbpm` | `chart_info.minbpm` |
| `duration` / `length` | Play length in seconds |
| `judge` / `judge%` / `judgepct` | Judge width multiplier. `judge` also accepts `veryhard`, `hard`, `normal`, `easy`, `veryeasy` |
| `feature` | `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll` |
| `notes` | Total notes |
| `long` / `ln` | Long notes |
| `scratch` | Normal scratch + long scratch |
| `total` | Effective TOTAL |
| `tn` / `t/n` | `total / notes` |
| `density` | Average density |
| `peak` / `peakdensity` | Maximum 1-second-window density |
| `end` / `enddensity` | Ending density |
| `soflan` | Speed-change count |
| `clear` | CLEAR. Accepts `NP`, `F`, `AE`, `LAE`, `EC`, `NC`, `HC`, `EXH`, `FC`, `PF`, `MAX`, or display names |
| `rank` / `djlevel` / `dj` | DJ LEVEL. Accepts `F`, `E`, `D`, `C`, `B`, `A`, `AA`, `AAA`, `MAX` |
| `rate` | RATE. Uses `rateDouble`; `0.95` means 95% |
| `score` | SCORE |
| `combo` | COMBO |
| `bp` | BP |
| `memo` | memo. Playlist detail only |
| `comment` | comment. Playlist detail only |

Numeric fields support ranges and comparisons.

```text
level:10..12
notes:>=2000
duration:<120
tn:2.0..
rate:0.95..
bp:0..10
```

`defined` / `undefined` are also available. `undefined` can be written as `undef` or `null`.

```text
total:undefined
total:undef
total:null
difficulty:defined
-feature:random
score:defined
rank:undefined
```

`clear` is an exact match for clear type. Short forms are available.

| Input | Target |
| :--- | :--- |
| `np` | NO PLAY |
| `f` | FAILED |
| `ae` | ASSIST EASY |
| `lae` | LIGHT ASSIST EASY |
| `ec` | EASY CLEAR |
| `nc` | NORMAL CLEAR |
| `hc` | HARD CLEAR |
| `exh` | EX HARD CLEAR |
| `fc` | FULL COMBO |
| `pf` | PERFECT |
| `max` | MAX |

### Fields for Playlist List

| field | Target |
| :--- | :--- |
| `id` / `playlistid` | playlist id |
| `name` | playlist name |
| `symbol` | playlist symbol |

---

## Phrase Search

Use double quotes when a term contains spaces.

```text
title:"Blue Sky"
playlist:"Satellite Sub"
"long phrase"
```

Quoted phrases are treated as a single token.

### Escapes in Quotes

Inside quotes, use backslash escapes.

| Input | Meaning |
| :--- | :--- |
| `\"` | double quote |
| `\\` | backslash |

---

## Exclusion Search

Prefix a token with `-` to exclude matches.

```text
alpha -beta
-artist:xi
-feature:random
```

This is useful for removing a title, artist, feature, playlist, or score condition from the result.

---

## OR Search

Use `|` between tokens to express OR.

```text
title:alpha|title:beta
artist:xi|artist:削除
clear:fc|clear:pf
```

OR can be combined with other AND terms.

```text
level:10..12 clear:fc|clear:pf -feature:random
```

### OR Cautions

OR has lower priority than token parsing. Quote values that contain spaces or `|`.

```text
playlist:"A | B"
```

---

## Regular Expression Search

Use regex syntax when a field supports it.

```text
title:/^alpha/
artist:/xi|削除/
path:/\\BMS\\/
```

Regular expressions are case-insensitive unless the implementation explicitly treats them otherwise.

### Regex Timeout

Regular expressions have a timeout. If a pattern is too expensive, the app treats it as a failed match rather than freezing the UI.

---

## Invalid Syntax

Invalid tokens are treated conservatively. The app tries not to crash or freeze on malformed input.

Examples of invalid or ambiguous input:

```text
title:
level:abc
notes:10..abc
title:"unterminated
```

If a search does not behave as expected, simplify it first and then add conditions back one by one.

---

## Common Examples

```text
title:alpha
artist:xi
level:10..12
notes:>=2000
duration:<120
feature:ln
-feature:random
clear:fc|clear:pf
rate:0.95..
bp:0..10
playlist:"Satellite Sub"
sha256:abcdef
total:undefined
rank:undefined
```
