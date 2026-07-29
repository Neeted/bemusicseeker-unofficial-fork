# BeMusicSeeker .NET 10 移行計画

[現在地](./PLAN_STATUS.md) / [性能計画](./BeMusicSeeker_性能回帰改善計画.md) / [依存台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## Target

- main app／tests／updater: `.NET 10` Windows target
- tools: `.NET 10`
- language: C# 14
- main app: win-x64 Self-contained、managed bundle、ReadyToRun、trimmingなし
- updater: win-x64 Self-contained single-file

## 現在の判定

TFM、managed dependencies、SQLite、archive／audio、native interop、existing-data、updater success／rollback、distribution profileの機能移行は完了している。

残るEngineering作業は、現在の.NET 10 codeにある不要なworkを減らし、実データなしでも検証できるcomponentをsynthetic corpusで改善する[PERF-01](./BeMusicSeeker_性能回帰改善計画.md)である。

## Performance migration rule

- net472は再instrument／再build／再計測しない。既存logはhistorical symptom evidenceだけに使う。
- selected publish profileは固定し、current .NET 10 evidenceでprofile自体が原因と確認された場合だけ再検討する。
- C# 14／.NET 10の新機能は、golden behaviorを持つsynthetic hot pathへ限定して採用する。
- full startup、actual WPF render、Everything／disk等はcurrent .NET 10 markerを用意し、全engineering完了後のユーザー実機確認へ渡す。
- legacy data／settings／playlist／package／update protocolを性能目的で変更しない。

## Exit

PERF-01 Engineering Gateを通過し、`PLAN_STATUS.md`が`engineering migration: complete`へ戻った時点でCodexの.NET 10移行工程を完了とする。実データ性能とruntime-free clean-machineはpost-engineering manual acceptanceであり、Codexは結果を待たない。
