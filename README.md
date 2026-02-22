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

## TODO

(A memo of ideas currently being considered. Priority is low.)

1. Add a screen to batch-exclude or edit file paths containing characters outside the `Shift_JIS` range.
2. "Overwrite Resource Only" feature.
    - Intended for cases where a new version of a chart is released with changes only to audio or BGA files.
    - Needs consideration whether to integrate this into the current installation screen or keep it separate. An automatic validation feature to decide if an overwrite is necessary would be ideal.
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

## License Scope

The first-party source code (i.e., new scripts and code diffs) newly created and published in this repository is distributed under the **MIT License**, maintaining compatibility with the original binary's license.

- **First-party source code**: MIT License (See `LICENSE`)
- **Third-party binaries, fonts, and SDKs**: Other external components included in the release packages are subject to the individual licenses and terms of service defined by their respective providers.
- In the event of conflicting terms, the third-party provider's license and notices take precedence for that specific component.

**For details on third-party dependencies and audits, please refer to:**
- `ThirdPartyNotices.txt` (List of legal notices and status for each dependent component)
- `docs/dependency-licenses.md` (Audit ledger and criteria for Green/Yellow/Red statuses)
- `third_party/licenses/` (Directory containing the original license texts and proprietary notices for bundled dependencies)

### Important Notice Regarding BASS
The release packages for this project include audio components related to `BASS`, which fall outside the scope of the MIT License.
These binaries are **not open source**. Commercial use mandates the acquisition of an appropriate commercial license from the provider (e.g., un4seen). While non-commercial and personal use may be permitted as freeware under certain conditions, users must always comply with the official licensing terms for both the native BASS libraries and the `Bass.Net` wrapper.
