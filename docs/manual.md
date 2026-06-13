# BeMusicSeeker Unofficial Fork User Manual v2.0

[![Japanese](https://img.shields.io/badge/lang-Japanese-blue.svg)](manual.ja.md)
[![English](https://img.shields.io/badge/lang-English-red.svg)](manual.md)

![BeMusicSeeker](img/header.jpg)

This document is the English user manual for BeMusicSeeker Unofficial Fork. It explains the workflows inherited from the original BeMusicSeeker together with the features, behavior changes, and cautions added by this fork.

## Table of Contents

- [Introduction](#introduction)
- [Initial Setup](#initial-setup)
- [Settings Dialog](#settings-dialog)
- [Startup / Reload / Progress Display](#startup--reload--progress-display)
- [Screen Layout](#screen-layout)
- [Library List](#library-list)
- [Search](#search)
- [Playback / Recording](#playback--recording)
- [Playlists](#playlists)
- [Install / Pending Packages](#install--pending-packages)
- [Maintenance](#maintenance)
- [Backup / Uninstall](#backup--uninstall)
- [Logs and Troubleshooting](#logs-and-troubleshooting)
- [References](#references)

## Introduction

BeMusicSeeker Unofficial Fork is a portable BMS library management tool. It can work with an existing LR2 environment, or it can run in standalone mode using its own database under the application directory.

The fork focuses on large-library performance, playlist workflows, pending-package installation, duplicate and maintenance checks, and better visibility into long-running work.

### Caution

This application can modify or delete BMS-related files and databases. Before using LR2 linked mode, back up important LR2 files such as `song.db`, `config.xml`, score DB, and custom folder files.

## Initial Setup

### Strongly Recommended: Everything 1.5 Alpha x64

Everything 1.5 Alpha x64 is strongly recommended. The app can run without it, but large libraries will be much slower for startup, reload, install-destination estimation, duplicate checks, and pending-package processing.

Install Everything, add your BMS folders to its index, and confirm that file searches work before starting BeMusicSeeker.

### Migrating Existing Settings

If a traditional BeMusicSeeker installation is found on first launch, settings are copied automatically. The copied settings are saved under `config/user.config` in this fork's directory, so the original installation is not modified.

### First Launch

On first launch, choose the display language and open the settings dialog. Set the operating mode, BMS directories, and if needed, LR2-related paths.

### Operating Mode

#### Standalone

Standalone mode does not use LR2's `song.db`. The app creates and manages `data/song.db` under its own directory. Use this mode when you want to manage BMS files, search, install, and maintain packages without LR2 integration.

LR2 scores, LR2 custom folder output, and some LR2IR/ranking-related features are disabled in this mode.

#### LR2 Linked

LR2 linked mode uses LR2's `song.db`, `config.xml`, and score DB. Use this mode when you want BeMusicSeeker to cooperate with your existing LR2 library.

Before the first scan, back up LR2 databases and settings. The fork tries to keep updates safe, but it still handles real files and databases.

### First Scan

The first scan reads the configured BMS folders, builds or updates the app database, and prepares playlist and maintenance information. In large libraries this can take a while. Progress is shown in the status bar and progress dialogs.

## Settings Dialog

The settings dialog is divided into tabs. This section summarizes what each area controls.

### General

General settings include operating mode, BMS directories, LR2 paths, beatoraja integration, language, and startup behavior.

#### Standalone

Configure BMS search directories. The application database is stored under the app directory, and LR2 files are not required.

#### LR2 Linked

Configure LR2's `song.db`, `config.xml`, and score DB paths. Use this mode only after backing up LR2-related files.

#### beatoraja Integration

Configure beatoraja score DB or table cache output if you want to use beatoraja-related features. Playlist `.bmt` output can reduce beatoraja-side table aggregation work in some environments.

### Appearance

Controls theme and display-related settings.

### Playback

Controls preview playback behavior. bmson playback is not currently supported by the built-in player.

### Device

Controls audio device settings.

### Recording

Controls audio-file conversion settings.

### Playlist Settings

Controls playlist reload, external sync, URL completion, and cache output settings.

### Install

Controls install-destination estimation, smart overwrite, automatic install behavior, pending-package handling, and cleanup options.

### Backup

Controls backup behavior for databases and related files.

### Advanced

Advanced settings include startup scan options, initialization behavior, DB read optimizations, LR2IR/ranking-related options, and heavy-operation settings.

#### Advanced Tab Options

Most advanced options are intended for performance tuning or recovery from special cases. Keep defaults unless you understand the effect.

Notable defaults:

| Group | Setting | Default | Meaning |
| :--- | :--- | :--- | :--- |
| Initialization | Skip BMS/config scan on startup | OFF | Loads existing database rows without rebuilding file/resource indexes until manual reload. |
| Initialization | Skip playlist update check on startup | OFF | External playlists are not refreshed automatically. |
| Initialization | Start on Install > Pending | ON | Opens the pending-package screen first. |
| LR2 | Do not estimate offline score ranking | OFF | Skips ranking estimation from LR2IR ranking cache XML. |
| LR2 | Download LR2IR scores and detect unsent IR scores | OFF | When ON, downloads LR2IR score data and compares it with local scores. |
| Install | Try install automatically after download | OFF | Attempts to install downloaded packages directly when possible. |
| Install | Always add new packages to pending | OFF | Keeps new packages in the pending list for confirmation. |
| Install | Use Everything for pending estimation | OFF | Can speed up source-side resource enumeration in very large pending folders. |
| Install | Use first high-confidence candidate even when multiple candidates exist | OFF | Useful in environments with many duplicate BMS folders, but can choose incorrectly. |
| Install | Delete original package even when already-owned charts remain | OFF | Cleans up the source folder after normal install even if already-owned charts remain. |

#### Smart Overwrite

Smart overwrite avoids replacing newer existing bundled resources with older files from an installation source. It is enabled by default. A related option can keep `.bmx`, `.pmx`, `.txt`, and `.bmson` files by auto-numbering instead of overwriting them.

## Startup / Reload / Progress Display

Startup work is staged. The app loads settings, opens DBs, scans files if needed, loads scores, updates playlist information, builds indexes, and publishes UI state. The status bar shows major phases, and long work may use progress dialogs.

### Library Reload

Reload reads the current library state again and applies file additions, removals, and metadata changes. Use it after adding or moving files outside the app.

### Re-run Initialization

Re-running initialization performs a heavier rebuild than ordinary reload. Use it when indexes or generated DB data appear inconsistent.

### Playlist Reload

Playlists can be reloaded individually, in ranges, or as part of the startup/update flow. External playlist failures are visible through playlist `STATUS`.

## Screen Layout

The main screen is organized around a left navigation tree, the main list/detail view, search boxes, context menus, and the status bar.

![Library context menu](img/一覧_ライブラリ_コンテキストメニュー.PNG)

## Library List

The library list shows installed charts and related states. It supports sorting, column settings, tooltips, search, copy operations, context menus, and maintenance views.

### Copy Operations

Selected rows can be copied in useful text formats. Use this for reporting, external list management, or manual inspection.

### Context Menu

Right-clicking a chart or playlist row opens actions appropriate for the current screen and row type.

Open / external pages:

- `Open BMS-IR`: Opens the BMS-IR song page by MD5. Hidden when the row has no MD5.
- `Open Mocha`: Opens the corresponding Mocha page when SHA256 is available.
- `Open MinIR`: Opens the corresponding MinIR page when SHA256 is available.
- `Open main URL` / `Open diff URL`: Opens URL1 / URL2 from playlists or URL completion.
- `Open in Explorer`: Opens the chart folder.
- `Open install destination`: Opens the estimated or recorded install destination.
- `Open with association`: Opens the chart file using the OS association.
- `Open text file`: Opens readme-like files in the same folder from a submenu.
- `Open video`: Opens YouTube / Niconico candidates when available. Some candidates depend on old LR2IR cache services and may not work.
- `Search downloads`: Opens download-source candidates gathered from playlist URLs, comments, URL completion, and related cache data.
- `Open in chart viewer`: Registers or opens the chart in the chart viewer.

Score / ranking:

- `Update ranking data`: Updates ranking cache / IR data for the selected chart. Some backing services may no longer be available.

File and maintenance operations:

- `Fix character encoding`
- `File scan`
- `Install`
- `Fix installed location`
- `Move`
- `Remove playlist entry`
- `Delete chart file`
- `Auto rename folder`
- `Convert to audio file`
- `Remove chart-info parse failure record`

#### Rename to Invalid Extension

`Rename to invalid extension (*.bmx/pmx)` renames BMS/PMS charts to extensions that LR2 normally ignores. Use it for zero-note charts or files that should be removed from the playable library without immediate deletion.

### Tree Context Menu

The navigation tree provides screen-specific operations such as playlist reload, pending-package actions, duplicate merge operations, and maintenance commands.

### Other Popup Menus

Some cells and views have additional popups, such as URL editing, document file lists, or advanced pending-package operations.

## Search

Search boxes support word search, AND search, field-qualified search, phrase search, exclusion, OR, regular expressions, and numeric range searches.

See the [Keyword Search Syntax Guide](keyword-search-syntax-guide.md).

## Playback / Recording

The built-in preview player can play BMS/PMS charts supported by the current player implementation. bmson playback and bmson audio conversion are not supported yet.

Audio conversion uses the recording settings and writes the playback result to an audio file.

## Playlists

### Playlist Summary

The playlist summary helps notice changes in unowned chart counts. You can filter by fully owned / contains unowned charts and double-click a summary row to jump to the playlist detail view.

`STATUS` shows the latest external sync result after startup. Use its tooltip to inspect details when a table fails to load.

### Playlist Detail

Playlist detail shows rows from a selected table. It uses a lightweight display model so large playlists can be opened and sorted more smoothly.

Missing rows show playlist-based actions such as external links, URL open, video candidates, chart viewer, ranking update, and entry removal. File operations are hidden for missing rows.

### Import External Playlist

External playlists can be imported from URLs or local files. Spreadsheet-based and large tables may take longer to respond, so reload timeouts are extended.

### Playlist Properties

Playlist properties control title, symbol, sync mode, folder output, and related metadata.

### Custom Folder Output

Custom folder output can write LR2-compatible or related folder definitions from playlist state. Use with care in LR2 linked mode.

### URL1/URL2 Completion

When playlist `url` / `url_diff` are missing, the app can fill runtime candidates by matching MD5 against configured TSV data and Stella Uploader data. The TSV source can be HTTP/HTTPS or a local file.

## Install / Pending Packages

The install screens help classify package contents, estimate destinations, and apply installs or resource updates.

### New

New packages are candidates that appear to contain charts not currently owned.

### Pending

Pending packages need confirmation, destination estimation, cleanup, or manual handling. Pending detail rows expose actions appropriate to the package state.

### Install-Destination and Merge-Destination Estimation

Install-destination estimation decides where new or differential charts should be placed. Merge-destination estimation is useful for resource-only updates against already-owned charts.

If estimation fails, you can manually enter an install destination.

### Advanced Features

Advanced pending operations include deleting packages that contain only already-owned charts, resource-only overwrite, zero-note invalid-extension rename, and similar cleanup operations. These can affect real files, so check the target before executing.

## Maintenance

### LR2 Compatibility Warnings

`LR2 Compatibility Warnings` lists charts that may fail selection or playback in LR2. LR2 assumes CP932/Shift_JIS-compatible filenames and older path-length limits in many places, so BeMusicSeeker checks both chart file paths and resource definitions such as `#WAV`, `#BMP`, and `#BGA`.

Warning types include:

- LR2 path incompatible: The chart file path contains characters that cannot be represented in CP932/Shift_JIS, so LR2 `folder` / `parent` information may not be created safely.
- LR2 resource incompatible: Resource definitions contain names LR2 may not handle as CP932/Shift_JIS filenames.
- LR2 path too long: The chart file path may exceed LR2-friendly path length.
- LR2 resource path too long: A referenced resource path may exceed LR2-friendly path length.

BeMusicSeeker keeps these charts visible as warnings. If LR2 cannot select or play them, rename chart files, resource files, or parent folders to shorter CP932-compatible paths.

### Zero-Note Search

Zero-note search lists charts that appear to have no notes or suspicious metadata. `Check zero-note notation` can detect charts where metadata says zero notes but visible-note-like notation exists.

### Parse Errors

Parse-error views show charts whose metadata could not be read. You can remove stored parse-failure records after fixing files so they are parsed again.

### Garbled Text Check

Garbled-text check helps correct character encoding. Supported manual correction targets include Japanese Shift_JIS, UTF-8, Korean KS_C_5601, Simplified Chinese GB2312, and Traditional Chinese Big5.

### Duplicate File Check

Duplicate-file check lists charts with duplicate hashes or related folder duplication. Duplicate rows are highlighted and can be merged.

#### Merge Duplicate Folders

Use duplicate-folder merge to consolidate duplicate charts and resources. The app chooses an automatic target when safe, or expands the menu when multiple destinations are possible.

#### Continuous Cleanup with `Ctrl + G`

Press `Ctrl + G` in the duplicate-file check tree to process likely merge targets continuously. After each merge, focus moves to the next likely candidate.

#### Cautions

Duplicate merge moves and deletes real files. Confirm the source and destination before executing large batches.

### Full Resource Scan

Full resource scan rechecks chart resources and updates maintenance information. This is heavier than ordinary display refresh.

## Backup / Uninstall

The app is portable. To uninstall, close the app and remove the extracted directory if you no longer need it.

Back up `config`, `data`, and any LR2 files you configured before deleting or replacing files. In LR2 linked mode, LR2's original files remain outside the fork directory unless you explicitly selected paths inside it.

## Logs and Troubleshooting

### Startup or Initialization Is Slow

Check whether Everything 1.5 Alpha x64 is installed, running, and indexing your BMS folders. Also inspect `install-performance.log` when using `LaunchWithInfoLog.bat`.

### Playlist `STATUS` Fails

External playlist sync depends on remote servers, table format, and network access. Open the tooltip or log to inspect the failure, then try reloading the affected playlist.

### LR2 Cannot Select a Chart

Check whether the chart appears in `LR2 Compatibility Warnings`. Paths or resource names containing characters outside CP932/Shift_JIS, or overly long paths, may fail in LR2.

### Search Syntax Is Unclear

See the [Keyword Search Syntax Guide](keyword-search-syntax-guide.md).

## References

- [Keyword Search Syntax Guide](keyword-search-syntax-guide.md)
- [INFO Log Guide](log-level-info-guide.md)
