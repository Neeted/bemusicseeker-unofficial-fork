# BeMusicSeeker Unofficial Fork

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](README.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](README.md)
![header](docs/img/header.jpg)

## Overview

This project is an unofficial fork created by decompiling `BeMusicSeeker.exe` with `ILSpy`, then modifying and rebuilding it. The original executable was bundled with `Sayaka / 黒皇帝`'s [BeMusicSeeker-installer](https://github.com/SayakaIsBaka/BeMusicSeeker-installer).

This fork was not created with direct permission from the original binary author, [`@rib_2_bit`](https://x.com/rib_2_bit). However, because the original binary was published under the MIT License, we believe creating and publishing a derivative version is permissible.

**[IMPORTANT] About this repository**
To the best of our knowledge, the original source code was never published. Likewise, this repository does not include most of the decompiled source itself.
In practice, this repository is operated as:

- a distribution point for modified release packages (binaries)
- a public archive for newly created scripts and patch-style code added by this fork

## Installation

1. **Install [Everything 1.5 Alpha (x64)](https://www.voidtools.com/everything-1.5a/)**
   - The application can run without it, but you will not get the full startup performance benefit. After installation, make sure Everything has finished indexing the directories that contain your BMS files and that searches work correctly.
2. **Download the latest release**
   - Download the latest build from the [Release](https://github.com/Neeted/bemusicseeker-unofficial-fork/releases) page and extract it to any directory. It runs as a portable application and does not require installation.
3. **Automatic settings migration**
   - On first launch, if a conventional BeMusicSeeker installation is already present, settings are migrated automatically.
   - Migrated settings are stored in `.\config\user.config` under the extracted folder, so the original BeMusicSeeker installation is not affected.
4. **Usage and new features**
   - Basic usage instructions are omitted here. Detailed documentation for the new features is still incomplete, so please use the "Summary of Changes" below as a guide for now.
   - If you launch the application via `LaunchWithInfoLog.bat`, more detailed `[INFO]`-level logs will be written to `application.log`. This is useful for troubleshooting and behavior analysis. See the [INFO Log Level Guide](docs/log-level-info-guide.md).

## Summary of Changes

The list below focuses on the main differences from the traditional build.  
**The biggest concern is damage to BMS-related databases or files. Use this fork at your own risk.**

### Performance

- **Faster initialization**
  - `Everything 1.5a` is effectively required. The original file enumeration was already fast, but this fork uses Everything-based search to better support environments with 10 million+ files.
  - Initialization work outside file enumeration has also been optimized.
- **Faster "move to estimated destination"**
  - The major bottleneck used to be UI updates after every move operation. This has been adjusted to behave more like a batch process.
- **Faster installation destination estimation**
  - The overall logic still follows the old version, but search speed is improved by using hashes and related indexing.
- **Faster merge operations in duplicate-file checking**
  - Duplicate-file checking and merge logic were optimized substantially.
- **Lower overall UI load**
  - UI update timing has been revised, and virtualization is used more aggressively so off-screen elements are not rendered unnecessarily.
  - This increases the risk of temporary inconsistencies between the screen state and internal state, but at the moment performance has been prioritized while stability is improved incrementally.
- **Added an option to use faster sorting in the list view (disabled by default)**
  - "Fast sort" prioritizes lighter display and re-sorting over strict natural-order behavior. The old version used natural sorting everywhere.
  - Difficulty-table folder names still need natural order, so this option only applies fast sorting to the list view.
  - If simple string ordering for `PATH` or `TITLE` is acceptable in the list view, rendering becomes faster.
- **Faster playlist detail rendering**
  - The playlist detail view now uses a lighter display-focused row model instead of carrying the full runtime object graph for each row.
  - This keeps large playlists, including ones with around 100,000 charts, much smoother to open, switch, and re-sort.

### By feature

#### Installation

- **Differential installation for bundled additional charts**
  - Fixed an issue where, if a folder contained already-owned charts, the destination for newly added unowned charts could not be estimated.
- **Smart overwrite mode added (enabled by default)**
  - When installing bundled resources such as audio or BGA, modification timestamps are compared so newer files are not overwritten by older ones.
  - An additional Smart Overwrite setting, `During smart overwrite, keep *.bmx/*.pmx/*.txt/*.bmson by auto-numbering instead of overwriting` (disabled by default), is also available. This is meant to avoid cases such as overwriting an existing bundled `readme.txt` with the differential package's `readme.txt`.
- **Restore file modification timestamps when installing from archives**
  - When installing a zip or similar archive via drag and drop, extracted files now inherit the original modification timestamps from the archive. This improves the accuracy of Smart Overwrite.
- **Support for deleting actual files from the Pending screen**
  - You can now delete installed differential files directly from the Pending screen.
  - The Pending screen's `Move to Trash` dialog includes the option `[x] Delete the entire folder if it no longer contains any BMS files`.
  - This behaves similarly to deletion from the library screen. Unlike library-side deletion, however, no per-folder confirmation dialog is shown.
- **Allow manual input of installation destination**
  - As a fallback when estimation fails, you can now manually enter the path of an already-owned BMS folder.
  - This is intended as a rarely used recovery feature, so convenience features such as second-choice suggestions are intentionally omitted. There is also a known quirk where trying to edit the cell may start playback.
- **Added "Open installation destination"**
  - You can now open the estimated or already-installed destination folder directly from the Pending tree or detail view via the context menu.
- **Added "Estimate merge destination" to the Pending screen**
  - This is intended to make it easier to apply resource patches, such as audio or BGA updates, to already-owned charts.
  - By forcing destination re-estimation for already-installed charts, it becomes possible to update resources only, even if the chart file itself has not changed.
- **Added a context menu item to remove selected BMS packages from the list in New/Pending**
  - Previously, removing items from the list was possible only one by one from the tree. This can now also be invoked for multiple selected items in the detail view.
- **Added a setting to delete the original package even if already-owned charts remain after normal installation (disabled by default)**
  - In the old version, already-installed files were skipped and left in the original folder. This new option makes normal installation behave more like duplicate-file merge processing: after installing the new charts from a package, the source folder can be removed together with the leftovers.
- **Added "Delete packages that contain only already-owned charts" to the Pending tree**
  - The Pending tree now has `Advanced > Delete packages that contain only already-owned charts without using the Recycle Bin`. This is intended for heavily mixed and messy pending states.
- **Added "Overwrite resources only for packages that contain only already-owned charts" to the Pending tree**
  - The Pending tree now has `Advanced > Overwrite resources only for packages that contain only already-owned charts`. This is intended for cases where only audio or BGA files were updated.
- **Added "Rename zero-note charts to invalid extensions (*.bmx/pmx)" to the Pending tree**
  - The Pending tree now has `Advanced > Rename zero-note charts to invalid extensions (*.bmx/pmx)`. This was added because "newly found" charts often turn out to be already-owned files that were just renamed to `.bmx`.
- **Improved multi-package drag-and-drop installation**
  - The old version processed each received path synchronously until destination estimation completed, which kept the drag source locked for a long time. This fork first receives the paths in bulk and then processes them, minimizing the lock duration.

#### Playlist

- **Extended timeout for difficulty table reloads (30 seconds -> 300 seconds)**
  - Some very large difficulty tables, especially spreadsheet-based ones, can take well over 30 seconds to respond.
- **Restored estimated difficulty / recommended tables (including automatic update)**
  - The source for difficulty table data was changed to [DARKSABUN](https://darksabun.club/).
- **Added playlist summary**
  - This makes it easier to notice when the number of unowned charts increases.
  - Filtering by "fully owned" or "contains unowned charts" is available from the search box in the top-right area.
  - Double-clicking a playlist summary row automatically jumps to the corresponding playlist view. This is especially useful when you want to review `[NO SONG]` entries after the unowned count increases.
  - A `STATUS` column has been added. It shows the latest result of external playlist sync attempts made after startup, making it easier to notice broken links and errors such as failed header/data retrieval. Detailed information is available in a tooltip.
- **Added single-playlist reload**
  - Individual tables can now be reloaded from the playlist tree or playlist summary detail view via the context menu.

#### Duplicate file checking

- **Minor improvements to the duplicate-file check screen**
  - Fixed an issue where warnings were shown on multiple lines when three or more duplicates existed, and consolidated duplicate warnings into a single entry.
  - Rows belonging to duplicated charts are now highlighted with a light red background color.
  - **Smart Overwrite behavior also affects this screen.**
- **Expanded duplicate-folder merge (`Ctrl+G`)**
  - **Overview**: In the duplicate-file check tree, pressing `Ctrl + G` while selecting the folder you want to merge away performs the merge as a shortcut.
  - **Automatic mode switching**:
    - If there are only two duplicates, the merge target is unique, so the merge runs immediately.
    - If three or more files are involved, the context menu expands automatically so you can choose the merge destination.
  - **Resolve duplicates inside a single folder**: It is now possible to clean up hash-only duplicates that exist within one folder.
  - **Faster continuous processing**: After each merge, focus automatically moves to the next likely target in the tree. In practice, you can repeatedly press `Ctrl + G` from the top of the list and work through large duplicate sets quickly.

#### Zero-note search

- **Added `Recheck zero-note`**
  - This is available from the right-click menu of the `Zero-note search` tree.
  - It lets you manually identify suspicious charts where LR2 or BeMusicSeeker recorded `karinotes = 0` in the song table, but the actual file still contains visible notes.
  - Such charts are shown with the warning `Recorded as 0 notes in DB, but visible notes exist`, and their rows are highlighted, similar to duplicate-file checking.

#### Garbled text check

- **Added `Simplified Chinese (GB2312)` and `Traditional Chinese (Big5)` to manual garbled-text correction**

### Other

- **Two-tier left sidebar**
  - Improves usability in environments with a very large number of registered difficulty tables.
- **Expanded use of the status bar**
  - The status bar is now used to show initialization and library reload progress, external playlist sync progress, and installation destination estimation progress.
- **Refreshed networking implementation**
  - **User-Agent headers are now added to HTTP requests**: this avoids `403 Forbidden` on some difficulty table sites that reject requests without a UA.
  - **Migrated from `WebClient` to `HttpClient`**: the performance impact is probably small and there is some regression risk, but the migration was done for long-term maintainability.
- **Portable-app style packaging**
  - On first launch, if an existing BeMusicSeeker installation is found, its configuration is automatically inherited.
- **Simple update-check feature**
  - At startup, the app shows a dialog if a newer version is available in the repository.
  - There is no auto-updater. If the dialog appears, please download the latest version manually and overwrite your files yourself.
- **Improved multilingual support**
  - In addition to stronger English and French support, this fork now supports Chinese (Simplified), Chinese (Traditional), and Korean.
- **Reworked file-mutation implementation (overwrite / move / rename / delete / timestamp changes, etc.)**
  - The old version directly used `System.IO.File` in many places, which made it more prone to interruption due to file attributes and access-denied cases.
  - This fork introduces a common file-mutation service and routes BMS-install-related file operations through it, making access-denied interruptions less likely.
  - In terms of practical behavior, this is closer to file operations performed through `Windows Explorer`: even read-only files can often still be overwritten or deleted.
  - Areas unrelated to BMS file operations remain largely unchanged.
- **Automatic conflict resolution when renaming to invalid extensions (`*.bmx/pmx`)**
  - In the old version, a name collision interrupted the process and could leave the physical file behind even though it disappeared from the detail list.
  - In this fork, if the colliding files have the same hash, the rename source is deleted. If the hashes differ, the file is renamed with automatic numbering. This is handled silently without showing a conflict dialog.
- **Reworked memory usage and lifecycle handling**
  - Reference ownership and object lifecycles were revised around library reloads and playlist switching, especially for playlist detail data.
  - This suppresses the unbounded memory buildup that previously looked like a leak during repeated reloads or playlist switching.

## TODO

These are current memo-style ideas. Ordering is roughly "first thought of first"; priority is mixed.

1. Add a screen to batch-exclude or edit file paths that contain characters outside the `Shift_JIS` range
   - There is currently a [song table round-trip update problem caused by non-Shift_JIS paths](docs/sjis-path-validation-note.md). We want a screen to inspect and fix such paths before simply rejecting them from DB updates.
2. Unregistered-chart review screen for BMS Score Viewer
   - Should md5 values that could not be uploaded locally (for example because of size limits) be stored in a local DB? Otherwise they never disappear from the unregistered list.
   - The approach used by [bms-score-uploader](https://github.com/Neeted/bms-score-uploader) would likely work.
3. Feature to download rival data from LR2IR, turn it into a local database, and place it automatically
4. Clear lamp viewer
5. Course content display and reordering
6. Replace hardcoded URLs that are now broken
   - LR2IR cache-related data is expensive to prepare and maintain, so an ideal solution might be a proxy server that fetches from LR2IR only when more than 24 hours have passed since the last update.
7. Investigate whether it is safe to fill empty fields in LR2's `song.db`
   - Some song-table columns may intentionally be left `NULL` to trigger LR2-side parsing or to suppress parse failures. Even if that is true, it may still be safe to fill some values such as BPM.
8. `bmson` management
   - If implemented, it would probably be easier to keep it entirely separate from LR2-style data handling and use an independent database.
   - **At the very least, a warning should be shown when pending packages contain bmson files.**
9. Show a warning dialog when Everything 1.5a integration fails
10. Refactor local variable names lost during decompilation
    - Many variables ended up with meaningless names such as `list1`, `list2`, or `item1`, which seriously hurts readability.
11. Recheck the implementation status of the custom folder export feature
    - There is no concrete improvement plan yet, but it is important enough to justify revisiting the behavior.
12. Bundle LR2 settings files and DBs (`song.db`, `config.xml`)
    - Since LR2 itself is not strictly required as long as the settings and DB exist, bundling them might remove the need to obtain LR2 separately.
    - Without additional work, this would probably still create `.lr2folder` for no meaningful reason when LR2 itself is absent, but that may be acceptable.
    - Full beatoraja support is not planned. For DB updates alone, [a sufficiently fast solution](https://github.com/Neeted/songdata-updater) already exists.
13. Improve the installation destination estimation algorithm
    - Manual `INSTL DST` input was added as a workaround, but it would be better to consider titles and similar metadata as well to reduce mistakes caused by sparse definitions or numbered charts.
14. Add a feature to consolidate `.wav` and `.ogg` when both exist as duplicates

## License Scope

The first-party source code newly created and published in this repository, such as scripts and public patch code, is distributed under the **MIT License**, following the original binary.

- **Newly created first-party code (public scripts, etc.)**: MIT License (see [`LICENSE`](LICENSE))
- **Third-party binaries, fonts, SDKs, etc.**: Other bundled external components are governed by the licenses and terms defined by their respective providers.
- If license terms conflict, the third-party component's own license and notices take precedence for that component.

**For more details on the audit trail and third-party terms:**

- [`ThirdPartyNotices.txt`](ThirdPartyNotices.txt): a list of notices and legal-risk status for each dependency
- [`third_party/licenses/`](third_party/licenses/): original license texts for bundled proprietary components, fonts, and similar assets

### Important Notice Regarding BASS

The release package for this project includes `BASS`-related audio components that are outside the scope of the MIT License.
These binaries are not open source. If you use them commercially, you must obtain the appropriate commercial license from the provider (such as un4seen). Non-commercial personal use may be allowed as freeware in some cases, but you must always comply with the official license terms of both native BASS and the `Bass.Net` wrapper.
