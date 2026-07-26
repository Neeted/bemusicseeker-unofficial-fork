# .NET 10 Migration Blockers

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [現在地](./PLAN_STATUS.md)

この文書は現在のblockerだけを持つ。完了したunit履歴やfull logはGit history／test artifactへ委ねる。

| ID | State | Blocking condition | Owner | Exit evidence |
|---|---|---|---|---|
| `TFM-01` | queued | 5 projectsが`net472` | `NET10-01` | 全projectのtarget matrix、Release build／test |
| `CFG-01` | queued | Framework configuration referenceとlegacy app.config semantics | `NET10-01/02` | ConfigurationManager package、settings golden test、probing非依存 |
| `DEP-UI-01` | queued | framework-era Livet／Interactivity／Metro／Expression／Code Pack | `NET10-03` | modern package／WPF APIへ移行、legacy HintPathゼロ、UI smoke |
| `DEP-HELPER-01` | queued | QuickConverter、DynamicJson、SgmlReader、unused managed DLL | `NET10-04` | grouped replacements、golden tests、legacy refs削除 |
| `DB-01` | queued | sqlite.net／hand-placed sqlite3のprovider／ABI／data compatibility | `NET10-05` | existing DB、transaction、2 tools、native bundle verification |
| `NATIVE-01` | queued | archive、audio、Everythingのversion／license／publish load未確定 | `NET10-06` | dependency register completed、x64 runtime smoke |
| `PUBLISH-01` | queued | current outputはnet472 build＋`libs` probingでSelf-contained publishではない | `NET10-07` | versioned win-x64 SCD profiles、publish-folder smoke |
| `UPD-01` | queued | updaterが新publish layout／runtime payloadで未検証 | `NET10-07/08` | update、restart、rollback、old-to-new smoke |
| `DATA-01` | queued | existing settings／DB／playlist／package stateのmigration acceptance未実施 | `NET10-08` | copied real-format dataによるgolden／manual smoke |
| `CLEAN-01` | external-gate | .NET runtime未導入clean x64 Windowsで未検証 | `NET10-08` | OS／artifact hash付きmanual acceptance record |

## Rehearsal evidence

過去のdisposable probeではapp、tests、updaterの最小`net10.0-windows` restore／build成功が記録されている。これはsource-level retarget可能性のevidenceであり、runtime、2 tools、dependency replacement、Self-contained publish、existing data、updater acceptanceを完了した証拠ではない。
