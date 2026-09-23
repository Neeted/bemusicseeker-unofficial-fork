# Keyword Search Guide

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](keyword-search-syntax-guide.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](keyword-search-syntax-guide.md)

The BeMusicSeeker search box can quickly narrow a list by title, artist, and other information. You can also choose where to search and combine multiple conditions.

The searches in this guide are mainly available in:

- Main chart list
- Playlist detail
- Playlist list
- Play Log

Available search fields vary by screen. You can always check the candidates shown in the search box to see what is available.

---

## Searches to Try First

| What you want to find | Example |
| :--- | :--- |
| Charts containing `alpha` | `alpha` |
| A specific TITLE and ARTIST | `title:alpha artist:xi` |
| Charts from level 10 through 12 | `level:10..12` |
| Anything outside a backup folder | `-path:backup` |
| Charts referenced by a playlist | `playlist:"Satellite Sub"` |
| Play Log entries from a date | `date:2026-08-27` |

Separate conditions with spaces. Only rows matching every condition are shown.

---

## Suggestions, Favorites, and Search History

Select a search box to see suggestions that match the current input.

### Before You Type

When the search box is empty, available groups appear in this order:

1. Favorites
2. Search history
3. Search fields

Favorites and Search history each show up to five rows at once; scroll to see additional rows. Search fields show up to ten rows at once.

### As You Type

Start typing a search field to narrow the suggestions to fields available on the current screen.

```text
t
```

In the main chart list and playlist detail, this can suggest `table:`, `tag:`, and `title:`. Suggestions also work when you begin an exclusion with `-`.

```text
-ar
```

This suggests `-artist:`.

Fields with a fixed set of choices show Search values after `:`.

```text
clear:
rank:
feature:
```

After you choose a value, a space is added so you can immediately enter another condition. Free-text fields such as `title:` do not show value suggestions.

For `playlist:`, `ref:`, and `table:`, you can choose from installed playlist names.

```text
playlist:Sat
```

Playlist names are automatically placed in double quotes when needed.

```text
playlist:"Satellite Sub"
```

Search fields and Search values show up to ten rows at once; scroll to see additional rows.

### Using Favorites

Use **Add to Favorites** at the end of a Search history row to save that search. Use **Remove from Favorites** at the end of a Favorite row to remove it.

- There is no limit to the number of Favorites.
- Adding or removing a Favorite does not change what you are currently typing.
- When the same search exists in both places, it is shown only in Favorites. Removing it from Favorites makes its history entry visible again if that entry still exists.

The main chart list, playlist detail, and Play Log share the same Favorites. The Playlist list keeps a separate set. A Favorite or history entry that uses a field unavailable on the current screen is hidden there but is not deleted.

### Using Search History

Pressing `Enter` after typing a search, or moving away from the search box, saves it to Search history. Choosing a Favorite or history row replaces the full contents of the search box and moves that search to the top of Search history.

- Up to 20 searches are kept, most recent first.
- Reusing the same search moves it to the top instead of creating a duplicate.
- Use **Delete from search history** to delete an entry you no longer need.

Search history is shared by the main chart list, playlist detail, and Play Log. The Playlist list keeps a separate history.

### Keyboard Controls

| Key | Result |
| :--- | :--- |
| `↑` / `↓` | Select a suggestion |
| `Enter` / `Tab` | Insert the selected suggestion |
| `Esc` | Close suggestions |
| `Ctrl+Space` | Open suggestions again |
| `Shift+Tab` | Move to the action buttons for the selected Favorite or history row |

After moving to an action button, use `Tab` / `Shift+Tab` to move between buttons and `Enter` or `Space` to activate one. Pressing `Enter` to confirm IME text does not choose a suggestion.

---

## Basic Search

### Search for Text

Enter a word to show rows containing that text in any default search column. Matching is case-insensitive.

```text
alpha
```

Separate words with spaces to require every word.

```text
alpha artist
```

### Choose Where to Search

Use `field:value` to limit a condition to one kind of information.

```text
title:alpha
artist:xi
sha256:abcdef
```

Multiple conditions are combined with AND.

```text
title:alpha artist:xi
```

Text containing a Windows drive letter, such as `D:\BMS\`, can be searched as a path.

### Search for Text Containing Spaces

Place the text in double quotes.

```text
"alpha title"
title:"alpha title"
```

### Exclude Matching Rows

Place `-` before a condition.

```text
alpha -artist:beta
-path:backup
```

### Match Either Value

Use `|` within one condition.

```text
alpha|beta
title:alpha|beta
```

`|` cannot join different fields, so cross-field OR searches such as `title:alpha|artist:beta` are not supported. Entering separate conditions combines them with AND instead.

---

## Search Fields by Screen

### Main Chart List and Playlist Detail

| Field | Information searched |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `genre` | GENRE |
| `tag` | TAG |
| `path` | File or folder path |
| `playlist` / `ref` / `table` | Referenced playlist name |
| `md5` / `hash` | MD5 |
| `sha256` | SHA256 |
| `level` | Level |
| `difficulty` | Difficulty. Also accepts `beginner`, `normal`, `hyper`, `another`, and `insane` |
| `mainbpm` | Main BPM |
| `maxbpm` | Maximum BPM |
| `minbpm` | Minimum BPM |
| `duration` / `length` | Play duration in seconds |
| `judge` / `judge%` / `judgepct` | Judge width. Also accepts `veryhard`, `hard`, `normal`, `easy`, and `veryeasy` |
| `feature` | Chart features: `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll` |
| `notes` | Total notes |
| `long` / `ln` | Long-note count |
| `scratch` | Scratch count |
| `total` | TOTAL value |
| `tn` / `t/n` | TOTAL divided by note count |
| `density` | Average density |
| `peak` / `peakdensity` | Peak density |
| `end` / `enddensity` | Ending density |
| `soflan` | Number of speed changes |
| `clear` | Clear status |
| `rank` / `djlevel` / `dj` | DJ LEVEL |
| `rate` | RATE. Enter `0.95` for 95% |
| `score` | SCORE |
| `combo` | COMBO |
| `bp` | BP |
| `memo` | Memo; Playlist detail only |
| `comment` | Comment; Playlist detail only |

Without a field, the main chart list searches TITLE, ARTIST, GENRE, TAG, PATH, playlist display symbols, MD5, and SHA256. Playlist detail also searches memo and comment.

### Playlist List

| Field | Information searched |
| :--- | :--- |
| `id` | Playlist ID |
| `output` | Output destination display name |
| `name` | Playlist name |
| `folder` / `foldername` | Folder name |
| `prefix` | Folder prefix |
| `symbol` | Display symbol |
| `header` | Header URL |
| `data` | Data URL |

Without a field, the Playlist list searches all the information shown in this table.

While you type, this screen suggests Search fields but not Search values.

### Play Log

| Field | Information searched |
| :--- | :--- |
| `title` | TITLE |
| `artist` | ARTIST |
| `path` | Chart path |
| `folder` | Name shown in the FOLDER column |
| `playlist` / `ref` / `table` | Referenced playlist name |
| `md5` | MD5 |
| `hash` | Hash recorded in the history: MD5 for LR2 and SHA256 for beatoraja |
| `sha256` | SHA256 |
| `date` | Play date and time |
| `year` | Play year |
| `month` | Play month, such as `2026-08`, `2026/08`, or `8` |
| `type` / `kind` | Update type: `score`, `bp`, `clear`, `combo`, or `play` |
| `clear` | Clear status before or after the update |
| `oldclear` | Clear status before the update |
| `newclear` | Clear status after the update |
| `finalized` | Whether the entry is finalized: `true` or `false` |
| `source` | History source name or path |

Without a field, Play Log searches TITLE, ARTIST, PATH, the FOLDER display name, referenced playlist names, hashes, TYPE, clear status, source, and play date.

Play Log does not support chart-list fields such as `level`, `difficulty`, `notes`, `rank`, `rate`, `score`, or `bp`.

Summary cards above the list can be combined with the search box. For example, the SCORE update card is equivalent to `type:score`, and the EASY card is equivalent to `type:clear newclear:EC`. Card filters are combined with the search box using AND; multiple selected cards are combined with OR.

---

## Numbers, Statuses, and Dates

### Compare Numbers

Numeric fields accept ranges and comparison operators.

```text
level:10..12
notes:>=2000
duration:<120
tn:2.0..
rate:0.95..
bp:0..10
```

- `10..12`: from 10 through 12
- `>=2000`: 2000 or more
- `<120`: less than 120
- `2.0..`: 2.0 or more

### Search by Whether Information Is Available

Use `defined` for rows with a value and `undefined` for rows without one. You can also write `undefined` as `undef`.

```text
total:undefined
difficulty:defined
score:defined
rank:undefined
```

Missing chart-analysis or score information matches `undefined`. In the main chart list and playlist detail, `clear` always has a status, including NO SONG or NO PLAY, so it does not match `clear:undefined`. In Play Log, `clear:defined` shows rows that have a clear status recorded after the update.

### Common Suggested Values

| Search field | Common values |
| :--- | :--- |
| `difficulty` | `beginner`, `normal`, `hyper`, `another`, `insane` |
| `judge` | `veryhard`, `hard`, `normal`, `easy`, `veryeasy` |
| `feature` | `ln`, `mine`, `random`, `lnmode`, `cn`, `hcn`, `stop`, `scroll` |
| `rank` / `djlevel` / `dj` | `F`, `E`, `D`, `C`, `B`, `A`, `AA`, `AAA`, `MAX` |
| Play Log `type` / `kind` | `score`, `bp`, `clear`, `combo`, `play` |
| Play Log `finalized` | `true`, `false` |

Fields that can distinguish missing information also suggest `defined` and `undefined`.

### Search by Clear Status

`clear`, `oldclear`, and `newclear` accept the displayed name or one of these abbreviations.

| Input | Clear status |
| :--- | :--- |
| `nosong` | NO SONG |
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

```text
clear:HC
oldclear:NC newclear:HC
```

### Search by Play Date and Time

In Play Log, you can enter a date, a date and time, or a period.

For a specific time, an easy option is to copy the displayed DATE value, place it in double quotes, and add `date:` before it.

```text
date:2026-08-27
date:2026/8/27
date:20260827
date:"2026/08/27 10:22:50"
date:"2026/08/27 10:22:50..2026/08/27 11:04:12"
```

A period includes plays at both its start and end times.

You can also select two or more Play Log rows, right-click, and choose **Add period to search**. This adds a period from the earliest through the latest selected play while keeping the conditions already in the search box.

---

## Advanced Search

### Regular Expressions

Enter a regular expression after `re:`. It can be combined with a field or exclusion.

```text
re:^alpha
title:re:^alpha
-title:re:^test
path:re:\\BMS\\.*\.bms$
```

Regular-expression matching is case-insensitive. An expression that takes too long is stopped and treated as not matching that condition.

### Search for `"` or `\` Inside Double Quotes

Inside double quotes, use `\"` for `"` and `\\` for `\`.

```text
title:"alpha \"quoted\""
```

For ordinary text searches, omitting the closing double quote treats the text through the end of the input as one search word. When quoting a date, time, or period after `date:`, include the closing double quote.

---

## When You See a Warning or No Results

The following conditions show a warning and do not match any rows.

| Example | What to check |
| :--- | :--- |
| `unknown:alpha` | Whether that field is available on the current screen |
| `title:` | Whether a search value follows `:` |
| `-` | Whether an exclusion follows `-` |
| `|` | Whether search text appears before or after `|` |
| `title:re:[` | Whether the regular expression is valid |
| `date:2026-99-99` | Whether the date format and value are valid |

Search fields differ by screen. If you are unsure, clear the search box and choose a field from the **Search fields** suggestions.
