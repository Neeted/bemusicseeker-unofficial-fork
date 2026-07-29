# BeMusicSeeker .NET 10 Self-contained 移行計画

[現在地](./PLAN_STATUS.md) / [応答性計画](./BeMusicSeeker_応答性・並行処理ハードニング計画.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## 現在の判定

全5 projectの.NET 10 retarget、managed／native dependency、SQLite、existing-data、update／rollback、Self-contained distribution engineeringは完了している。最終main-app profileは外部startup／working-set比較で選択した`managed bundle＋ReadyToRun`であり、native self-extractを使用しない。

現在のhangはsingle-file extractionや.NET 10 hostではなく、MVVM整理中に成立したmodel-lock／synchronous UI applyのwait cycleである。selected distribution profileは維持し、応答性修正がpublish path、startup code、bundle／R2R設定へ触れない限りprofile選定を再実行しない。

## Target matrix

| Project | Target | Current release contract |
|---|---|---|
| `BeMusicSeeker.csproj` | `net10.0-windows`, x64 | win-x64 Self-contained managed bundle＋ReadyToRun、native runtimeはexe隣接 |
| `BeMusicSeeker.Tests` | `net10.0-windows`, x64 | full test／architecture／interaction behavior |
| `BeMusicSeeker.Updater` | `net10.0-windows`, x64 | win-x64 Self-contained single-file |
| `chart-info-compare` | `net10.0`, x64 | locked restore／Release build／DB behavior |
| `chart-info-export` | `net10.0`, x64 | locked restore／Release build／DB behavior |

trimming、NativeAOT、Composite ReadyToRun、single-file compressionは対象外。

## Selected layout

- managed assembliesは公式single-file bundleから読み込む。
- `IncludeNativeLibrariesForSelfExtract=false`とし、SDK／SQLite／WPF native runtimeはexe隣接に置く。
- BASS／7zは`libs/x64`、Everythingは`native`、language catalogは`lang`に置く。
- managed DLLを`libs`へ移すcustom loader、probing、deps rewrite、post-publish relocation、wrapper launcherを作らない。
- updaterの単一payload handoffを維持する。

current performance evidenceは`devdocs/acceptance/net10-distribution-performance.md`、dependency detailsは[依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md)を正本とする。過去の移行unitとbenchmark runはGit historyへ委ねる。

## Engineering Gateの再開範囲

.NET 10 migration自体を再開しない。release-candidate操作で見つかったruntime behavioral regressionだけを応答性Gateで閉じる。

応答性Gate後に、全5 projectのlocked restore／Release build／full tests／analyzer、selected app／updater publish、startup／shutdown、existing-data、update success／rollback、package install interactionを再確認する。

pre-release deadlockの中途状態を対象にしたdurable recovery frameworkは.NET 10 migration taskへ追加しない。filesystem／song DBの差分は既存startup／manual file diff contractで収束させる。

`.NET Desktop Runtime`未導入machine／VM、署名、公開、BASS.NET entitlementは引き続き[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。
