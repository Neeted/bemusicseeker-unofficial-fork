# BeMusicSeeker Unofficial Fork

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](README.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](README.md)
![header](docs/img/header.jpg)

## Overview

This project is an unofficial fork, reverse-engineered and reconstructed by decompiling `BeMusicSeeker.exe`—originally bundled in `Sayaka / 黒皇帝`'s [BeMusicSeeker-installer](https://github.com/SayakaIsBaka/BeMusicSeeker-installer)—using `ILSpy`.

This fork was created without direct permission from the original binary's author, [`@rib_2_bit`](https://x.com/rib_2_bit). However, because the original binary was published under the MIT License, we have determined that creating and distributing this derivative work is legally permissible.

**[IMPORTANT] About this Repository**
To our knowledge, the original source code was never made public. This project also **does not** include the vast majority of the source code obtained via decompilation in this repository.
Rather, this repository serves primarily as a **distribution hub for the modded release packages (binaries)** and a public archive for the specific supplementary scripts and differential code newly created for this fork.

## Installation Instructions

1. **Install [Everything 1.5 Alpha (x64)](https://www.voidtools.com/everything-1.5a/)**
    - *Note: The application will run without it, but you won't get the full benefit of the initialization speed-up. Please ensure that after installation, Everything has finished building its database for your BMS directories and that searching is functional.*
2. **Download the Release**
    - Download the latest version from the [Release](https://github.com/Neeted/bemusicseeker-unofficial-fork/releases) page and extract it to any directory. It functions as a portable app and requires no traditional installation.
3. **Automatic Configuration Migration**
    - Upon its first launch, if a conventional BeMusicSeeker installation is detected on your system, its settings will be automatically migrated.
    - These migrated settings are saved locally to `.\config\user.config` in your extracted folder, so your original BeMusicSeeker installation remains completely unaffected.
4. **Usage and New Features**
    - Detailed explanations of basic usage are omitted here. Comprehensive documentation for the new features is currently unwritten. Please refer to the "Summary of Changes" below for an overview.
    - Launching the application via `LaunchWithInfoLog.bat` enables verbose `[INFO]`-level logging in `application.log`, which is useful for troubleshooting and investigating application behavior. For guidance on reading the logs, see the [INFO Log Level Guide](docs/log-level-info-guide.md).

## Summary of Changes

**(*The primary concern with these changes is the potential corruption of BMS databases or files. Please install and use this tool at your own risk.*)**

1. **Initialization Speed-Up**
    - `Everything 1.5a` is now required. While the original file enumeration was already quite fast, it struggled with environments containing 10 million+ files.
    - Various other initialization processes have also been revised.
2. **Faster "Move to Estimated Destination"**
    - The main cause of delay was the UI updating after every single file move. This has been adjusted to function more like a batch process.
3. **User-Agent Header Added to HTTP Requests**
    - Fixed an issue where `403 Forbidden` errors were returned by certain difficulty table sites.
4. **Extended Timeout for Difficulty Table Reloads (30s → 300s)**
    - Heavily populated difficulty tables (e.g., those managed via Google Sheets) often suffered from slow response times, making the previous 30-second timeout insufficient.
5. **Two-Tiered Left Sidebar**
    - Improved UI usability for environments with a massive number of registered difficulty tables.
6. **Added Playlist Summary**
    - Makes it immediately obvious when unowned charts increase.
    - Added the ability to filter playlists by "fully owned" or "contains unowned".
    - Added a single-table reload option to the right-click context menu.
7. **Faster Installation Destination Estimation**
    - The core logic is inherited from the original, but search speeds have been enhanced using file hashes.
8. **Differential Installation for Bundled/Appended Charts**
    - Previously, if an installation folder already contained some owned charts, the tool would fail to estimate the destination for the unowned (new) charts. This has been fixed.
9. **Smart Overwrite Mode (Enabled by Default)**
    - When installing bundled audio or BGA files, the tool now checks file modification dates to prevent accidentally overwriting newer files with older ones.
10. **File Deletion from the "Pending" Screen**
    - Added support for deleting the actual differential files of an already-installed chart directly from the pending screen.
11. **Manual Input for Differential Installation Destinations**
    - Added as a fallback if estimation fails. You can now manually enter the path of an already-owned BMS folder.
    - *Note: As this is intended for rare use, convenience features like "second-choice suggestions" are omitted. Also, be aware of a known quirk where clicking a cell to edit it may trigger audio playback.*
12. **Portable Application Mode**
    - Upon initial startup, if an existing BeMusicSeeker installation is detected on the system, its configuration files are automatically inherited.
13. **Restored Estimated Difficulty / Recommended Tables (including auto-update)**
    - Re-routed the dependency for difficulty table information to [DARKSABUN](https://darksabun.club/). (If updates were not a concern, caching this locally might have been an alternative.)
14. **Added "Open Installation Destination" Feature**
    - Added the ability to directly open the estimated or actual installation folder of owned files from the tree or item context menus on the Pending screen.
15. **Added "Estimate Merge Destination" Feature on the Pending Screen**
    - This feature makes it easier to apply resource patches (audio/BGA) to already-owned BMS charts.
    - By intentionally re-estimating the installation destination even for installed charts, you can overwrite just the resources even if the chart file itself hasn't been updated.
16. **Minor Fixes to the Duplicate File Check Screen**
    - Fixed an issue where warning messages were displayed across multiple lines if 3 or more duplicates existed, consolidating the warning into a single entry.
    - Duplicate chart rows are now highlighted with a light red background color.
    - *(Note: The Smart Overwrite mode's behavior affects this screen as well. Detailed documentation is recommended in the future.)*
17. **Enhanced Multi-Language Support**
    - In addition to strengthening English and French support, Chinese (Simplified), Chinese (Traditional), and Korean are now supported.
18. **Implemented a Simple App Update Check Feature**
    - Displays a dialog at startup if a newer version is available in the repository.
    - *Note: As this is a simple check feature, there is no auto-updater. If the dialog appears, please manually download the latest version and overwrite your files.*
19. **Restored File Modification Dates During Archive Installation**
    - When installing via drag-and-drop from archives (e.g., zip), the modification dates of the extracted files are now correctly restored to match the originals. This improves the accuracy of the "Smart Overwrite Mode."
20. **Improved Merge Function (Duplicate File Check) Performance**
    - Optimized the logic for duplicate file checks and merging, resulting in significantly faster processing speeds.

## TODO

(A memo of ideas currently being considered, ordered by when they were thought of. Priority is mixed.)

1. Add a screen to batch-exclude or edit file paths containing characters outside the `Shift_JIS` range.
    - Currently, [an issue where non-Shift_JIS paths cause round-trip updates in the song table](docs/sjis-path-validation-note.md) creates inconsistencies we want to resolve. However, merely blocking DB registration leaves no trigger to notice path issues. Therefore, we want to implement this confirmation/editing screen before addressing the inconsistency directly.
2. Enhance the "Estimate Merge Destination" feature.
    - It would be preferable to verify if a move actually makes sense before performing it (e.g., checking if source files are newer or if additional files exist), and skip setting the estimated destination if it's pointless.
    - It would be helpful if the UI showed exactly how much was changed by the Smart Overwrite mode. (It currently outputs to `[INFO]` logs, but readability is poor.)
3. "Unregistered Charts" confirmation screen for BMS Score Viewer.
    - Should we maintain a local DB for md5s that couldn't be sent (e.g., due to the 5MB limit)? Otherwise, they stubbornly remain on the "unregistered" screen forever.
    - Following the approach of [bms-score-uploader](https://github.com/Neeted/bms-score-uploader) should work.
4. Feature to download and format rival databases from LR2IR.
5. Clear Lamp viewer feature.
6. Display course contents and allow editing of their order.
7. Replace broken/dead hardcoded URLs.
    - Preparing and updating data for LR2IR caches is difficult. Ideally, setting up a proxy server that only fetches from the main LR2IR if 24 hours have passed since the last update would be best.
8. Investigate if it's safe to populate empty fields in LR2's `song.db`.
   - Some columns in the song table might be intentionally left `NULL` (possibly to trigger LR2's chart parsing or to catch parse errors). Even so, filling in values like BPM might be harmless and requires testing.
9. `bmson` format management.
    - If implemented, it would be much easier to completely decouple this from the LR2 data lineage and use an independent, custom DB.
    - **At the very least, we'd like to show a warning when bmson files are included when adding to the pending list.**
10. Warning dialog when integration with Everything 1.5a fails.
11. Enhance 0-notes check behavior.
    - Would it be better to check for 0 notes at the point of differential installation and automatically suggest converting them to `.bmx` extensions?
    - When a name duplication warning occurs during `.bmx` conversion, the entry disappears from the detail list, but the extension is actually left unchanged. A better flow would be verifying the hash: if duplicates match, delete the new duplicate intended for renaming; if they don't, automatically rename it.
    - The existing 0-notes check sometimes flags charts as having 0 notes even when they don't (possibly caused by LN parser logic).
12. Reorganize UI update timings.
    - Due to the policy of suppressing updates for speed, there are side effects where list details won't refresh unless you switch your tree selection, unlike the old behavior.
13. Difficulty table package creation feature.
    - Assuming intellectual property issues are momentarily set aside, having a feature to easily create "bulk packages" of major difficulty tables would significantly lower the barrier to entry for users.
    - It would be nice to have a dedicated working directory for packaging that allows for differential copying.
    - A "patch output feature for package differential updates" is difficult to account for (e.g., syncing when existing resources are deleted), so it's reasonable to stick to just generating a zip file for the entire difficulty table for now.
    - Under no circumstances will any feature be implemented that extracts only the specific charts listed on a difficulty table while stripping away the other charts (e.g., different difficulties) originally bundled by the BMS creator. (While this practice was seen in some past 'insane difficulty' unpackaged distributions, it lacks respect for the original creators.)
14. Refactor local variable names lost during decompilation.
    - Many variables are named meaninglessly, like `list1, list2...` or `item1, item2...`, causing fatal readability issues (including hindering AI code comprehension) that need cleanup.
15. Investigate the issue where reloading all playlists (either at startup or manually) while a "Recommend (Auto-update)" table is installed causes the playlist update to hang indefinitely.
    - This issue seems to have stopped reproducing after some initialization code was tweaked. As a temporary countermeasure, `ServicePointManager.DefaultConnectionLimit = 12;` has been set.
    - Fundamentally, migrating from `HttpWebRequest` to `HttpClient` is preferable, but difficult to do immediately given the codebase size.
16. Review the implementation status of the custom folder export feature.
    - There are no specific improvement ideas yet, but as it's a critical core feature, its operational specifications need to be reconfirmed.

## License Scope

The first-party source code (i.e., new scripts and code diffs) newly created and published in this repository is distributed under the **MIT License**, maintaining compatibility with the original binary's license.

- **First-party source code**: MIT License (See [`LICENSE`](LICENSE))
- **Third-party binaries, fonts, and SDKs**: Other external components included in the release packages are subject to the individual licenses and terms of service defined by their respective providers.
- In the event of conflicting terms, the third-party provider's license and notices take precedence for that specific component.

**For details on third-party dependencies and audits, please refer to:**

- [`ThirdPartyNotices.txt`](ThirdPartyNotices.txt): List of legal notices and status for each dependent component
- [`third_party/licenses/`](third_party/licenses/): Directory containing the original license texts and proprietary notices for bundled dependencies

### Important Notice Regarding BASS

The release packages for this project include audio components related to `BASS`, which fall outside the scope of the MIT License.
These binaries are **not open source**. Commercial use mandates the acquisition of an appropriate commercial license from the provider (e.g., un4seen). While non-commercial and personal use may be permitted as freeware under certain conditions, users must always comply with the official licensing terms for both the native BASS libraries and the `Bass.Net` wrapper.
