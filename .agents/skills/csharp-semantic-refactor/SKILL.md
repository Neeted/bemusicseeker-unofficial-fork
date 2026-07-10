---
name: csharp-semantic-refactor
description: Use for C#/.NET symbol renames, API/signature refactors, namespace/type/member moves, and BeMusicSeeker chart-abstraction renames where identifier replacement in .cs/.xaml would otherwise be tempting. Requires semantic rename first via repo-local Roslynator rename-symbol when safe, user VS Code F2 handoff for location-specific renames, and targeted audit of non-symbol artifacts such as XAML, resources, docs, settings, DB schema, serialization, and generated files.
---

# C# semantic refactor workflow

Use this skill for C#/.NET refactoring, especially BeMusicSeeker work that moves old BMS-centric names toward chart-common domain names.

## Prime Directive

Do not use text replacement as the primary method for C# symbol changes.

Semantic tools first:
1. Repo-local `dotnet roslynator rename-symbol`, when the target can be safely expressed with a narrow `--match` and verified by target-list output plus immediate `git diff` audit.
2. Other callable Roslyn or LSP rename tool, if configured.
3. Compiler-driven edits for API/signature refactors and private/local changes whose complete call-site set can be verified by the compiler and targeted audit.
4. VS Code F2 Rename Symbol / C# Dev Kit, applied by the user only when a required location-specific rename cannot be selected safely by an available automated tool.
5. Targeted text edits only for classified non-symbol artifacts.

Use `rg` for discovery and audit only. Never use `rg`, `sed`, PowerShell replacement, or case-insensitive repository-wide replacement to edit C# identifiers.

## Tool Discovery

Before a rename, check repo-local tools:

```powershell
dotnet tool list --local
dotnet roslynator rename-symbol --help
dotnet roslynator analyze --help
dotnet format --help
```

The repo standard is the verification entry point documented in `AGENTS.md`: `scripts/verify-refactor.ps1`. Use `dotnet format style` or `dotnet format analyzers` only when the change specifically needs style/analyzer formatting checks.

If local tools are declared but commands are unavailable, run `dotnet tool restore` and retry.

Treat tool manifest and analyzer packages as separate concerns:

- `.config/dotnet-tools.json` enables repo-local CLI tools such as `roslynator`. SDK-provided `dotnet format` is not pinned in the tool manifest.
- `Roslynator.*` `PackageReference`s enable build/IDE analyzer diagnostics and affect restore, lock files, warnings, and CI behavior.
- Do not add analyzer packages merely to make `dotnet roslynator rename-symbol` available.

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

Check internally before editing:

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

Do not stop merely because one rename tool is unavailable. For private/local symbols and API/signature changes whose scope can be verified by compiler errors, tests, and a targeted old-name audit, use compiler-driven edits. If the rename is optional cleanup, omit it rather than blocking the active outcome.

Ask the user to run VS Code F2 Rename Symbol only when the rename is required, its symbol scope cannot be selected safely by an available automated tool, and compiler-driven edits cannot prove complete coverage. Include:

- file and location
- target symbol
- old name
- new name
- expected rename scope

Resume only after the user confirms the F2 rename has been applied.

## Roslynator Rename

Use Roslynator only when a symbol set can be selected precisely enough by `--match`, `--match-from`, `--scope`, project, and optional include/exclude filters.

Start with a target-list pass:

```powershell
dotnet roslynator rename-symbol BeMusicSeeker.csproj --scope type --match "<predicate>" --new-name "<expression>" --dry-run -v detailed
```

Known repo-local behavior:

- With `roslynator.dotnet.cli` 0.12.0, `rename-symbol --dry-run` lists the matched symbols but then may throw a `NullReferenceException` in `MSBuildWorkspace.TryApplyChanges(...)`.
- Treat this as a known dry-run bug, not by itself as a failed target-selection check.
- The useful output is the symbol list printed before the exception. Continue only if that list is exact and narrow.
- Immediately after the dry-run attempt, run `git status --short` and confirm that no files changed.
- If the target list is wrong, or any file changed unexpectedly, do not run the write step. Prefer VS Code F2 for cursor-specific renames.

Rules:

- Prefer the target-list pass first, then a narrow non-interactive run using the same `--match` and `--new-name`.
- Inspect the target list before allowing disk writes. For `roslynator.dotnet.cli` 0.12.0, the command can exit non-zero after printing the useful list.
- Run from a clean worktree whenever possible, so an unexpected result can be discarded with ordinary git file restore.
- Keep `--match` specific enough to avoid prefix-wide or namespace-wide surprises.
- Do not use Roslynator for a cursor-position-specific rename that cannot be described safely in `--match`; ask the user to use VS Code F2 instead.
- Inspect `git diff --name-only` and `git diff` immediately after any Roslynator write.
- If the diff contains unintended semantic scope, unrelated symbols, generated/persisted artifacts, or string/config changes that were not planned, discard the Roslynator write and switch to VS Code F2 or a more precise approach.
- After a successful write, continue with the non-symbol artifact audit; Roslynator will not reliably update XAML strings, resource keys, settings names, docs, DB/schema strings, or serialized names.

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

Run the standard repository checks defined in root `AGENTS.md` and `devdocs/plan/BeMusicSeeker_refactoring_plans/00_Codex共通実行ルール.md`.

For semantic refactors, also verify:

- git diff --stat
- git diff --name-only
- `rg` audit for the old name and known casing variants; classify every remaining hit as valid compatibility, persisted contract, user-facing text, stale production reference, stale test reference, or removable wrapper
- targeted `dotnet test` for the affected behavior
- full `dotnet test` when the change crosses shared model/viewmodel/service boundaries
- formatting verification through the SDK `dotnet format whitespace` command
- Roslynator analyzer output; treat new diagnostics as part of the change impact, not as rename tooling setup

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
