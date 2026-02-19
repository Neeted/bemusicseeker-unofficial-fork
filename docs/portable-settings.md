# Portable Settings Behavior

## Summary
BeMusicSeeker now stores user settings in a portable file:

- `config/user.config` (relative to `BeMusicSeeker.exe`)

The app no longer depends on `%LOCALAPPDATA%` once the portable settings file exists.

## First-Run Migration
On startup, before `Settings.Default` is first used, the app runs migration logic:

1. If `config/user.config` already exists, migration is skipped.
2. Otherwise, it searches:
   - `%LOCALAPPDATA%\\BeMusicSeeker\\BeMusicSeeker.exe_Url_*\\*\\user.config`
3. It sorts candidates by:
   - latest `LastWriteTimeUtc`
   - then path ascending (stable tie-break)
4. It validates candidates in order and skips invalid ones:
   - XML must be readable (`invalid_xml` is skipped)
   - `configuration/userSettings/BeMusicSeeker.Properties.Settings` must exist
   - "default-equivalent" configs are skipped:
     - missing/empty `AssemblyVersion`
     - missing/empty `TableListURL`
     - missing/empty `BMSInstallDir`
     - if `OperationModeLR2DB=True`, missing/empty `LR2SongDBPath` or `LR2ConfigXmlPath`
     - if `OperationModeLR2DB=False`, missing/empty `BMSRootPath`
5. The first valid candidate is copied to `config/user.config`.
6. If no valid candidate is found, migration is skipped and startup falls back to defaults.

After migration, the portable file becomes the only settings source.

## Fallback Behavior
- If no legacy file is found, the app starts with defaults from `app.config`.
- If migration fails, startup continues and defaults are used.
- A new `config/user.config` is generated on first `Settings.Default.Save()`.

## Logging
Migration emits trace logs:

- `portable_settings_migration candidates count=...`
- `portable_settings_migration candidate_skip source=... reason=...`
- `portable_settings_migration skip reason=...`
- `portable_settings_migration success ...`
- `portable_settings_migration failed`

Note:

- `Info` logs are written when started with `--log-level=Info`.
- `Error` logs are written to `application.log` regardless of this migration flow.

## Operational Notes
- For portable usage, run from a writable location.
- Running under restricted folders (for example, `Program Files`) can block config writes.
