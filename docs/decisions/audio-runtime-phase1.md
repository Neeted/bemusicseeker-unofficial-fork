# Audio Runtime Phase 1 Design Decisions

Status: Accepted for Phase 1
Date: 2026-08-03

## Context

BeMusicSeeker uses the bundled Bass.Net and BASS, BASSWASAPI, BASSASIO, BASSmix, BASSenc, and BASS_FX native libraries for playback and offline audio conversion. The existing code originated from decompilation and mixed native runtime loading, device enumeration, backend negotiation, playback state, settings persistence, and cleanup in static state.

Phase 1 stabilizes those behaviors without changing Bass.Net, any native DLL, supported-version constants, or hashes. Updating the managed/native BASS set is Phase 2 and must remain a separate change.

## Decisions

### 1. Reviewable implementation units

Phase 1 is implemented and reviewed in dependency order:

1. Runtime, session ownership, operation gate, and cleanup.
2. ASIO, WASAPI, and DirectSound negotiation.
3. Requested, negotiated, and observed playback/test contracts.
4. Device catalog, settings UI, and localization.

Each unit must close its ownership and failure contract before the next unit is committed. A final integration verification does not replace unit-level review.

### 2. One durable native-session owner

The audio lifecycle manager is the only durable owner of a native audio session from the first successful native acquisition until cleanup is confirmed. Playback, device-test, conversion, and settings consumers may hold an immutable session token or result, but they do not retain independent pending-cleanup state.

The lifecycle manager serializes initialize and free operations, rejects reentry while a session is active or quarantined, and retains unconfirmed ownership for later cleanup retry. Runtime shutdown first closes the operation gate, waits for active operations, requests session cleanup, and unloads native modules only after ownership is cleared.

### 3. Cleanup and primary failures

Cleanup runs only for layers and handles recorded as successfully acquired. Before releasing a layer, cleanup selects the saved core, WASAPI, or ASIO device.

If device selection fails, cleanup does not stop or free that layer and does not free streams that could belong to another current device. The session retains ownership and is quarantined for retry. A later successful retry performs the release once.

`BASS_ERROR_INIT` returned by an idempotent stop/free operation is treated as already released and logged diagnostically. It does not replace the primary initialization exception. A device-selection failure is not permission to release an unknown current device.

The first initialization or operation exception remains primary. Cleanup failures are recorded through `NLogWrapper` and exposed as cleanup state without overwriting the primary exception. Managed handles and state for resources whose release was confirmed are reset in `finally`.

### 4. NullDevice is not an audible fallback

`NullDevice` is reserved for explicit offline encoding/conversion routes. It is not included in normal playback or device-test fallback candidates and is never reported as DirectSound.

A legacy persisted NullDevice value is not silently rewritten. The settings UI presents it as an unavailable saved backend until the user chooses an audible backend. Normal playback and device test fail before native initialization when that legacy value is requested.

### 5. Deterministic fallback matrix

Negotiation always exhausts same-backend degradation before moving to another backend. Backend-specific device identities are not reused across backends; a cross-backend attempt starts from that backend's default endpoint.

- ASIO: requested/default ASIO device, deterministic rate candidates, Float32 then Int16; then WASAPI exclusive, WASAPI shared, DirectSound.
- WASAPI exclusive: requested event/period, non-event with the requested period, non-event with the default period; then WASAPI shared and DirectSound.
- WASAPI shared: requested event/period, non-event with the requested period, non-event with the default period; then DirectSound.
- DirectSound: requested device, then the backend default; otherwise fail.

Stale identities first use a compatible name match within the same backend when available, then that backend's default. The fallback reason records identity loss, mode/period degradation, format/rate normalization, native error source/code, and any cross-backend destination.

No matrix ends in NullDevice.

### 6. ASIO format and rate negotiation

The ASIO callback format and decode mixer format always have the same byte width.

1. Try a Float32 decode mixer with `BASS_ASIO_FORMAT_FLOAT`.
2. Only when Float32 conversion is unavailable, retry with an Int16 mixer and Int16 callback.

Int8, Int24, and Int32 requests normalize to Float32 for the engine. They are never passed directly to the callback without conversion. The requested format and negotiated engine format remain separate in the result.

For an explicit sample rate, candidates start with the requested rate, then the driver's current rate, followed by distinct standard rates. For Auto, candidates start with the driver's current rate rather than a fixed 48000 Hz. Accepted rate and format are read back and stored in the negotiated result. Phase 1 does not add a custom P/Invoke when the bundled Bass.Net lacks a suitable direct mixer attachment API.

### 7. WASAPI and DirectSound rules

The WASAPI engine mixer remains Float32. Engine format and endpoint format are separate. Shared mode prioritizes the endpoint mix rate and channel count. Event/custom-period failure first degrades to non-event/default-period operation in the same backend.

In shared mode, application volume is a gain on the BASS Float32 mixer. Because the callback consumes decode data, this gain is implemented by the bundled `BASS_FX_BFX_VOLUME` mixer effect rather than the playback-only `BASS_ATTRIB_VOL` channel attribute. DirectSound uses the same mixer-effect route. The application does not write the Windows audio-session volume during initialization, live updates, or cleanup, so the Windows per-application control remains an independent multiplier. The initial mixer gain and the callback's session-owned source handle are published before `BASS_WASAPI_Start`; tempo graph changes atomically publish the replacement callback source before releasing the previous stream.

DirectSound catalog entries retain the descriptor and original native index together. Native device index 0, disabled entries, and no-sound entries are not selectable audible devices. A default request uses device `-1` where supported and records the actual selected device after initialization.

Phase 1 does not hard-code `BASS_DEVICE_DSOUND` and does not change `IntPtr.Zero` on the assumption that a WPF window handle is the root cause. A future BASS upgrade must evaluate `BASS_DEVICE_DSOUND` for the DirectSound route.

### 8. Requested, negotiated, and observed values

- Requested values are an immutable snapshot of the user's backend, device identity/name, rate, format, buffer, event mode, and volume.
- Negotiated values are the backend, device, rate, channels, engine format, endpoint format, mode, and latency accepted or reported by the native APIs.
- Observed values are runtime measurements such as stream progress, wall-clock duration, playback-position duration, progress ratio, and whether initialization and stream movement were independently confirmed.

Existing internal names that use `Actual` may be retained only when their documentation identifies whether they mean negotiated or observed. New parallel aliases are not added merely for convenience.

Normal playback never persists negotiated values. A settings device test updates editing values only when the request is still current, initialization and stream progress succeed, no fallback or normalization occurred, and explicit backend/device/rate/format requests match the negotiated values. Default device and Auto rate/format intent remain Default/Auto rather than being replaced with a transient concrete value.

### 9. Stale devices and catalog refresh

Catalog refresh is explicit and repeatable. One backend's enumeration failure does not discard another backend's result, and the last good list is retained for a failed refresh.

A saved device missing from the latest successful catalog is represented as an unavailable saved option, distinct from the Default placeholder. Refresh does not erase or rewrite the saved identity. Selecting Default explicitly clears the saved device; selecting another endpoint replaces it.

Catalog options distinguish stable identity, display name, native index, default status, and availability. Asynchronous refresh publishes only the newest request for the currently selected backend.

### 10. Device-test progress criteria

Initialization success and stream progress are separate results. Playback progress is measured with `Stopwatch` and playback position.

The evaluator ignores startup pre-roll until the first forward playback movement, then requires at least one second of measured wall-clock interval. Positions must be monotonic, playback must continue to advance, and the playback-position/wall-clock ratio must remain within `0.75` through `1.25`, inclusive. Ratios of 1.5x and 2x are failures. Passing that interval is a diagnostic milestone, not the end of the player lifetime: a successful audible test retains the player and native session until the finite test sound reaches its natural end.

The overall observation remains bounded. Establishing the progress measurement retains the existing ten-second limit, while natural completion is bounded by the reported sound duration plus the same grace period. A stopped stream counts as naturally complete only when its playback position reaches the reported duration. Missing test audio, no movement, reversal, an out-of-range ratio, early termination, failure to reach the natural end, or an exception fails the test and prevents settings changes. The result does not claim that sound was physically audible; it reports device initialization and stream progress independently.

### 11. Logging and errors

Production audio logs use `Ribbit/Logging/NLogWrapper.cs`. Each attempt records OS/build, bundled BASS component versions, requested values, attempted backend/stage, native error source/code, negotiated values, latency, and fallback destination/reason without adding unbounded hot-path logging.

Known audio initialization exceptions preserve backend, stage, requested and negotiated devices, native error source, and native error code. Settings UI messages for these failures are localized in every supported resource source.

## Compatibility and exclusions

- Persisted setting names and enum numeric values remain compatible.
- Native DLLs, Bass.Net, supported-version constants, hashes, package metadata, and application version are unchanged in Phase 1.
- Windows 10/11 and real-device coverage is recorded as an external test matrix; unit tests use narrow native boundaries and deterministic fakes.
- Phase 2 updates Bass.Net and all BASS native components as one compatible set, updates ABI/constants/hashes/packaging together, and validates rollback and the Windows/device matrix in a separate change.
