---
name: csharp-semantic-refactor
description: Use for C#/.NET symbol renames, API/signature refactors, namespace/type/member moves, and BeMusicSeeker chart-abstraction renames where identifier replacement in .cs/.xaml would otherwise be tempting. Requires semantic rename first, user VS Code F2 handoff when no callable rename tool exists, and targeted audit of non-symbol artifacts such as XAML, resources, docs, settings, DB schema, serialization, and generated files.
---

# C# semantic refactor workflow

Use this skill for C#/.NET refactoring, especially BeMusicSeeker work that moves old BMS-centric names toward chart-common domain names.

## Prime Directive

Do not use text replacement as the primary method for C# symbol changes.

Semantic tools first:
1. Callable Roslyn or LSP rename tool, if configured.
2. VS Code F2 Rename Symbol / C# Dev Kit, applied by the user when Codex has no callable semantic rename tool.
3. Compiler-driven edits for API/signature refactors.
4. Targeted text edits only for classified non-symbol artifacts.

Use `rg` for discovery and audit only. Never use `rg`, `sed`, PowerShell replacement, or case-insensitive repository-wide replacement to edit C# identifiers.

## Classify The Operation

Before editing, classify the change:

- `SymbolRename`: namespace, type, member, local, parameter, or property rename. Use semantic rename.
- `ApiRefactor`: signature, overload, inheritance/interface, or call-shape change. Use compiler-driven edits after understanding the contract.
- `NonSymbolArtifact`: XAML strings, resources, JSON, docs, logs, settings names, DB schema names, SQL, migrations, serialized names, external file formats, or generated outputs. Edit deliberately after the semantic change.
- `ContractRename`: persisted setting, DB table/column, resource key, serialized field, public plugin/API surface, or user-visible compatibility name. Preserve compatibility or ask before changing.

For BeMusicSeeker chart abstraction, classify every old `BMS*` name as one of:

- chart-common domain concept: safe candidate for `Chart*` naming.
- BMS-format-specific behavior: keep or move behind BMS-specific type/capability.
- bmson-specific behavior: keep separate from BMS row persistence.
- historical/user/persistence term: retain unless there is a migration plan.

## Before editing

Report:

- current branch and whether the worktree has unrelated changes.
- target symbol
- declaration location
- symbol kind
- containing type and namespace
- visibility
- whether it is public API
- whether production code still uses the old API, or only tests do
- whether XAML, resources, settings, config, DB schema, migrations, serialization, generated files, logs, or docs may be involved
- expected semantic scope and expected non-symbol follow-up files

If no semantic rename tool is callable, stop after this report and ask the user to run VS Code F2 Rename Symbol. Include:

- file and location
- target symbol
- old name
- new name
- expected rename scope

Resume only after the user confirms the F2 rename has been applied.

## During editing

- Prefer one semantic rename operation per symbol. Do not bundle unrelated renames just because they share a prefix.
- After semantic rename, inspect the diff before making follow-up edits.
- Edit non-symbol artifacts with narrow, reviewed patches only after classifying why semantic rename cannot handle them.
- Do not leave compatibility wrappers or old-name APIs that are used only by tests. Remove or rename tests with production code.
- For API/signature refactors, do not treat the work as a plain rename. Let build errors expose call sites, and be careful with overload ambiguity such as discarded `out _` arguments; make the discard type explicit when needed.
- Do not rename persisted settings, DB tables/columns, serialized fields, or resource keys without an explicit compatibility or migration plan.

## BeMusicSeeker Audit Checklist

Check the relevant artifact types after semantic rename:

- `.cs`: declarations and call sites should be covered by semantic rename or compiler-driven edits.
- `.xaml`: interaction method names, Livet action `MethodName` values, command names, bindings, and resource keys may be string-based.
- `Resources.resx`, `lang/*.json`, `docs/**`, `devdocs/**`: update user-facing or spec text only when terminology really changes.
- Keyword/search/help changes: keep parser, completion, help resources, localized JSON, and docs synchronized in the same pass.
- `Properties/Settings.*`, column settings, profile/config files: treat setting names and serialized setting members as persisted contracts.
- `song`, `bmson_song`, `playlist`, `playlist_entry`, `chart_digest_map`, `maintenance`, SQL text, and other DB schema names: treat as persisted contracts.
- Generated files: update from the source of truth when possible; do not hand-edit generated output unless that is the established project workflow.
- Tests: keep behavior coverage, but do not preserve production-only compatibility surfaces just to satisfy old tests.

## After editing

Run:

- git diff --stat
- git diff --name-only
- `rg` audit for the old name and known casing variants; classify every remaining hit as valid compatibility, persisted contract, user-facing text, stale production reference, stale test reference, or removable wrapper
- `dotnet restore` when project files, packages, or restore state changed or are uncertain
- `dotnet build`
- targeted `dotnet test` for the affected behavior
- full `dotnet test` when the change crosses shared model/viewmodel/service boundaries
- `dotnet format --verify-no-changes` when formatting/analyzer verification is part of the repo workflow or the change is broad; treat unavailable or noisy format tooling as report-only unless the repo already relies on it

Use a subagent review when available for non-trivial refactors. Ask it to review the diff for stale old names, over-rename, missed non-symbol artifacts, compatibility wrappers, and persistence/config risks.

Before finishing, verify no old production API remains unless it is intentionally retained. Search old symbol names, old enum/context names, adapter APIs, wrappers, and user-visible terms; justify each remaining production hit.

## Risk report

Classify risk by semantic type, not changed lines.

Include:

- operation type
- files changed
- symbol scope
- public API impact
- serialization/database/config impact
- UI/XAML/resource impact
- generated file impact
- build/test/analyzer result
