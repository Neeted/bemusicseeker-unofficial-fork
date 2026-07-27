# .NET 10 Migration Blockers

[移行計画](./BeMusicSeeker_NET10移行計画.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [現在地](./PLAN_STATUS.md)

この文書は現在のblockerだけを持つ。完了したunit履歴やfull logはGit history／test artifactへ委ねる。

| ID | State | Blocking condition | Owner | Exit evidence |
|---|---|---|---|---|
| `DEP-HELPER-01` | resolved | DynamicJson／IniLibrary routes retired; SgmlReader moved to Microsoft.Xml.SgmlReader 1.8.30; System.Collections.Immutable supplied by runtime pack | `NET10-04` | grouped typed boundaries、Shift-JIS settings／HTML golden tests、legacy refs／binaries／notices削除、locked restore／publish verification |
| `DB-01` | resolved | sqlite.net／hand-placed sqlite3のprovider／ABI／data compatibility | `NET10-05` | sqlite-net-pcl／SQLitePCLRaw locked restore、startup／existing DB／schema／transaction／raw hydration／real contention、2 tools、win-x64 native bundle／SCD layout verification |
| `NATIVE-01` | external-gate | archive／audio／Everythingのruntime evidenceは進行中。BASS.NET 2.4.12.1のexact source archive、正式`LICENSE.rtf`、licensee scope、既存registration entitlementの証跡が未確認で、proprietary wrapperの配布可否を判定できない | `NET10-06` / release owner | dependency registerのexact binary／ABI／runtime evidenceに加え、取得元archive、正式license、licensee／registration entitlement、BASS native redistribution evidenceを記録する。証跡なしにGREEN／release可とは扱わない |
| `PUBLISH-01` | queued | current outputはnet10 buildを含むがSelf-contained publishではない | `NET10-07` | versioned win-x64 SCD profiles、publish-folder smoke |
| `UPD-01` | in-progress | updaterのnet10 protocol／SCD起動は検証済み。app publish／old-to-new acceptanceが未完了 | `NET10-07/08` | update、restart、rollback、old-to-new smoke |
| `DATA-01` | queued | existing settings／DB／playlist／package stateのmigration acceptance未実施 | `NET10-08` | copied real-format dataによるgolden／manual smoke |
| `CLEAN-01` | external-gate | .NET runtime未導入clean x64 Windowsで未検証 | `NET10-08` | OS／artifact hash付きmanual acceptance record |

## Rehearsal evidence

N1〜N3ではapp／tests／updaterの`net10.0-windows`と2 toolsの`net10.0`についてsolution／個別Release build、settings／code-page／startup DB-open targeted tests、chart compare／export DB behavior tests、updaterのwin-x64 Self-contained `--version` smoke、repository Release executableのstartup smokeが通っている。M1ではNLog 6.1.4のlogging／archive behavior test、package/output layout、temporary win-x64 Self-contained appの起動とapplication log生成も通っている。これはNET10-01の全5 project baselineとNET10-02のlogging corridor evidenceであり、dependency replacement全体、main app Self-contained publish、existing data、old-to-new updater acceptanceを完了した証拠ではない。
