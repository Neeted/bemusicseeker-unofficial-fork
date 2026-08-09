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

## Current native distribution decision

The current release is a non-commercial, non-revenue end-user software
release. On that accepted basis, the following components are GREEN:

- BASS core and official add-ons: `bass.dll`, `bassmix.dll`, `bassenc.dll`, and
  `basswasapi.dll`, under the upstream Un4seen terms.
- BASSASIO: `bassasio.dll`, under its separate upstream terms.
- BASS_FX: `bass_fx.dll`, as a third-party add-on attributed to
  `(: JOBnik! :) [Arthur Aminov, ISRAEL]`.
- ManagedBass and its five companion packages, exact `4.0.2`, under MIT.

The corresponding English and Japanese distribution notices have separate
entries for the three native classifications. The BASS_FX package archive,
readme, and retained x64 member hashes are recorded in
`third_party/licenses/01b-BASS_FX-NOTICE.txt`. These notices are summaries and
do not replace authoritative upstream terms.

## Future commercial policy trigger

The current GREEN classification does not authorize a future commercial or
monetized release. A policy change to monetization requires a fresh review of
the applicable BASS, BASSASIO, and BASS_FX terms before distribution. This
future review trigger is not a current YELLOW condition.

## External release prerequisites and security gate

The current non-commercial distribution-rights check is complete for the
accepted release facts above. Before any future commercial distribution,
reconfirm the applicable native terms. Separately, historical registration
material from the retired managed wrapper remains an external security gate
for vendor-side revocation/rotation and release-security handling. Values are
not decrypted, reconstructed, or displayed, and Git history is not rewritten.

The non-blocking runtime-free-machine check remains tracked in the post-
migration manual acceptance document.

## Migration completion evidence

The implementation migration is complete. The accepted dependency contract is
the exact six-package `ManagedBass` set at `4.0.2` plus the unchanged native
six-DLL x64 set listed in `devdocs/spec/bass-runtime-dependency-set.md`; the
closeout changed neither package versions nor native binaries and hashes.
Playback, backend negotiation, session ownership, effects, encoder command and
metadata handling, pull rendering, and cleanup now use the project-owned
ManagedBass boundaries described in `devdocs/spec/audio-runtime-phase1.md`.

The former real-encoder gap is closed by the opt-in
`ExternalAudioEncoderSmokeTests` contract. It exercises available user-provided
encoder executables through `BassAudioWriter` and records a deterministic
no-tool failure when none are present; it never bundles encoder binaries or
media fixtures. The normal lane remains tool-independent because the test is
`ProcessIntegration` and is inconclusive without explicit opt-in.

Closeout verification consists of the related encoder Quick lane, the standard
Functional lane, and the Full lane:

```powershell
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Functional
pwsh -NoProfile -File .\scripts\verify-refactor.ps1 -Mode Full
```

The recorded results are:

- compliance Quick: 6 passed;
- encoder-related Quick: 24 passed and the disabled opt-in test was
  inconclusive;
- final Functional: 3,758 total, 3,748 executed, 0 failed, 165.8 seconds;
- final Full: Functional 3,748 executed / 0 failed, ProcessIntegration 35
  passed / 2 skipped, ReleaseAcceptance 2 passed, self-contained publish,
  existing-data acceptance, update acceptance, whitespace verification, and
  Roslynator 0 diagnostics.

No supported external encoder executable was available in the closeout
environment. The explicit opt-in command therefore produced the required
deterministic no-tool failure and recorded the production search order; a real
encoder pass remains an external/manual item.

The historical migration-plan row that temporarily classified native BASS
redistribution as a commercial-entitlement `YELLOW` case is retained in Git
history. It was superseded by the accepted current non-commercial `GREEN`
decision in the closeout notices; this does not rewrite the historical record.
The external security gate for historical registration material remains
separate: values are not decrypted, reconstructed, or displayed.
