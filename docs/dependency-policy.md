# Dependency Policy

## Purpose

This project keeps dependency resolution reproducible and auditable.

## Rules

1. No implicit upgrades in this task.
- Only same-version and same-identity replacements are allowed.
- Functional upgrades are handled in a separate task.

2. Prefer PackageReference when identity parity is verified.
- Required checks: AssemblyName, AssemblyVersion, PublicKeyToken.
- FileVersion and SHA256 are recorded for traceability.

3. Keep libs\*.dll only when needed.
- If no reliable NuGet equivalent exists, keep HintPath reference.
- Document the reason in dependency inventory.

4. Native runtime DLLs are vendored in-repo.
- Use `vendor/native/x86` and `vendor/native/x64`.
- Build output must include these folders without external install dependency.

5. Lock package graph.
- Use `packages.lock.json`.
- Keep lock file updated and committed.

## Validation

Run:

```powershell
pwsh scripts/deps/inventory.ps1
pwsh scripts/deps/verify.ps1
```
