# ManagedBass adoption

Status: Accepted
Date: 2026-08-09

## Context

The application used a proprietary managed BASS wrapper for playback and encoder helpers. The wrapper required registration material and supplied high-level encoder types that were not part of the open-source migration target. The native BASS six-DLL set remains an independent runtime and redistribution contract.

## Decision

Use the exact `4.0.2` versions of `ManagedBass`, `ManagedBass.Mix`, `ManagedBass.Fx`, `ManagedBass.Enc`, `ManagedBass.Asio`, and `ManagedBass.Wasapi`. Keep the six native BASS DLLs, their current hashes, resolver ownership, and `libs/x64` output boundary unchanged. Reconstruct encoder command, metadata, session, rendering, and cleanup behavior in project-owned code.

`ManagedBass.Tags` is not included because the former tag DTO is already represented by the project-owned `AudioTagInfo` model. The updater may continue to remove the obsolete `libs/Bass.Net.dll` from an older installation, but that path is not a current dependency or package payload.

## Consequences

- Current source, tests, package graphs, build output, and publish artifacts contain no proprietary managed wrapper or registration stage.
- ManagedBass MIT terms are recorded in `third_party/licenses/02-ManagedBass-MIT.txt` and kept separate from the proprietary native BASS notice.
- Existing persisted audio settings, native component versions, file naming, and runtime ownership contracts remain unchanged.
- Git history is not rewritten. Any historical registration material requires separate vendor-side revocation/rotation and release-security handling.

## External release prerequisites

Before public distribution, confirm the intended native BASS redistribution/commercial entitlement and complete any vendor-side security action for historical registration material. These are release prerequisites, not reasons to retain the retired managed dependency in the current tree.
