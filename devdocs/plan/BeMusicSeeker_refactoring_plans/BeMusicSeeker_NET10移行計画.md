# BeMusicSeeker .NET 10 Self-contained 移行計画

[現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## 1. 現在の判定

MVVM／owner整理、全5 projectの.NET 10 retarget、managed／native dependency、SQLite、existing-data、update／rollbackのengineeringは完了している。

`NET10-10 P1/P2`の外部比較では、`folder-r2r`と`bundle-r2r`のstartup／working setに実用差がなく、managed bundleはpublish file数を505から24へ減らした。最終main-app profileは`bundle-r2r`を採用する。

選択profileは次の性質を持つ。

- managed assembliesは公式single-file bundleから読み込まれる。
- `IncludeNativeLibrariesForSelfExtract=false`とし、SDK／SQLite／WPFのnative runtimeはexe隣接に置く。起動前のbundle extractionを必要としない。
- `PublishReadyToRun=true`、Composite ReadyToRun／compression／trimmingは無効とする。
- application-owned BASS／7zは`libs/x64`、Everythingは`native`、language catalogは`lang`を維持する。
- production logの`startup_ready_operable elapsedMs`ではなく、外部harnessのprocess startからの値をperformance evidenceとする。

## 2. Target matrix

| Project | Target | 方針 |
|---|---|---|
| `BeMusicSeeker.csproj` | `net10.0-windows`, x64 | win-x64 Self-contained managed bundle＋ReadyToRun、native runtimeはexe隣接 |
| `BeMusicSeeker.Tests` | `net10.0-windows`, x64 | full test／architecture／publish behavior |
| `BeMusicSeeker.Updater` | `net10.0-windows`, x64 | win-x64 Self-contained single-fileを維持 |
| `chart-info-compare` | `net10.0`, x64 | locked restore／Release build／DB behavior |
| `chart-info-export` | `net10.0`, x64 | locked restore／Release build／DB behavior |

trimming、NativeAOT、Composite ReadyToRunは対象外。single-file compressionは明示的に無効とする。

## 3. Active outcome: `NET10-10 Performance-first distribution closure`

active batchは[PLAN_STATUS](./PLAN_STATUS.md)にmaterialize済みであり、plannerを起動しない。

### `P1 BENCHMARK-HARNESS`

公式SDK設定だけで、同じHEADから次のcandidateをclean publishする。candidateは一つのbenchmark scriptからMSBuild propertyで生成し、六つの恒久pubxmlやproduction fallbackを追加しない。publish outputとraw run logはignored `artifacts/performance/net10-distribution/`配下へ置く。

| Candidate | PublishSingleFile | Native self-extract | ReadyToRun |
|---|---:|---:|---:|
| `folder-il` | false | false | false |
| `folder-r2r` | false | false | true |
| `bundle-il` | true | false | false |
| `bundle-r2r` | true | false | true |
| `extract-il` | true | true | false |
| `extract-r2r` | true | true | true |

共通値:

```xml
<SelfContained>true</SelfContained>
<PublishTrimmed>false</PublishTrimmed>
<PublishReadyToRunComposite>false</PublishReadyToRunComposite>
<EnableCompressionInSingleFile>false</EnableCompressionInSingleFile>
<IncludeAllContentForSelfExtract>false</IncludeAllContentForSelfExtract>
```

harness要件:

1. existing-data acceptanceと同じisolated profile／fixtureを使う。
2. harness側Stopwatchを`Process.Start()`前に開始し、process start→main window handle／input idle、process start→`startup_ready_operable`ログ検出を測る。ログ本文の`elapsedMs`はphase内訳にだけ使う。
3. `fresh-install`はimmutable candidateを毎回新しいinstall directoryへcopyする。`extract-*`には毎回空の専用`DOTNET_BUNDLE_EXTRACT_BASE_DIR`を与える。`warm-cache`は同じinstall directoryとextract directoryを再利用する。これはOS page cacheを強制消去するcold benchmarkとは呼ばない。
4. harnessの作成・変更後は、最も複雑な`extract-r2r`をfresh／warm各1回だけ動かし、publish、起動、ready検出、graceful shutdown、集計、JSON／CSV出力を確認する。
5. smokeが通った後、各candidateを1回warm-upし、`warm-cache`と`fresh-install`を各3回測る。candidate順序はround-robinで入れ替え、同じsettings、data、native asset、network／Defender条件を使う。
6. 起動中央値の差が`max(500 ms, 10%)`付近にあり結論が変わり得る場合だけ、上位2候補の曖昧なphaseを各2回追加する。それでも曖昧なら実用上同等として終了し、それ以上の再測定を行わない。
7. median／min／max、failure、peak working set at ready、process CPU time、publish byte数、file数、extract byte数／file数、Windows build、SDK、HEADとdirty有無をmachine-readable reportへ出す。p90、変動係数、source tree全体のfingerprint、resume receiptは選択に使わない。
8. appをgraceful shutdownする。semantic snapshotと詳細な機能受入れはsmokeおよび最終選択profileのacceptanceで確認し、各timed runの前にfixtureを読み込んでcacheを温めない。startup phase logとmain-view build等の既存内部metricsは診断用に保存するが、外部end-to-end metricを置き換えない。

benchmark専用のproduction fallback、loader、startup short-cutを追加しない。

### `P2 PROFILE-SELECTION`

機能受入れを通らないcandidateは失格とする。残候補を次の順で決める。

1. `warm-cache`と`fresh-install`のprocess start→`startup_ready_operable`中央値を比較する。一方が`max(500 ms, 10%)`以上速く、もう一方のphaseやready時working setを同程度以上悪化させないcandidateを実用上優位とする。
2. freshとwarmが逆方向に実用差を持つ場合は一つの総合scoreへ潰さず、初回／通常起動のtrade-offとして記録する。この集合へ同等時のlayout preferenceを適用して自動選定せず、P2で利用頻度等のproduct判断を明示して決める。
3. 実用上優位なcandidateがなければnative self-extractなしを選ぶ。
4. folderとmanaged bundleが同等なら、custom loaderなしで配布file数を減らせるmanaged bundleを選ぶ。
5. 同じlayout familyでReadyToRunが起動を悪化させず、working set増加が`max(16 MiB, 10%)`未満なら、初期JITを減らすReadyToRunを選ぶ。

以上から、明確な反証がない場合の基準候補は`bundle-r2r`とする。これはnative extractionを避け、folderより配布file数を減らし、ReadyToRunで初期JITを軽減するためであり、根拠のないfallbackではない。ReadyToRunによるsize／working set増加が実用差を持つ場合は`bundle-il`、folderが起動で実用上優位なら対応するfolder候補を選ぶ。

起動benchmarkはsteady-state workloadを測らないため、アプリ実行中の処理速度差を断定しない。bundleは起動後の継続的な展開処理を持たず、ReadyToRun codeもtiered compilationの対象になるという一般特性を選択理由へ含める。

選択結果は`devdocs/acceptance/net10-distribution-performance.md`へcurrent-onlyのperformance acceptance reportとして、command、environment、candidate summary、selected profile、理由を記録する。raw JSON／CSVはartifactとして保持し、過去runの逐次logを計画文書へ追記しない。

### `P3 PACKAGE-CLOSURE`

選択profileを唯一のmain-app release contractへ反映する。

- main appの選択profileを中立名`Properties/PublishProfiles/WinX64SelfContained.pubxml`へ置き、未選択profileを退役
- `BeMusicSeeker.csproj`のapplication-owned native／content publish targetをselected profileで一貫させる
- `scripts/publish.ps1`、`scripts/verify-refactor.ps1`、benchmark harness
- portable package layout validatorと関連tests
- existing-data／update success／rollback acceptance
- `devdocs/spec/portable-auto-update.md`
- `devdocs/acceptance/net10-distribution-performance.md`、dependency register、blocker台帳、`PLAN_STATUS.md`

layout規則:

- folder profileではstandard hostのmanaged assemblies、runtime DLL、`.deps.json`、`.runtimeconfig.json`を`BeMusicSeeker.exe`と同じdirectoryに置く。
- bundle profileではSDKが公式に隣接配置するnative runtime fileを許可する。
- BASS／7zは`libs/x64`、Everythingは`native`、language catalogは`lang`に置く。
- managed DLLを`libs`へ移す独自loader、probing、deps rewrite、post-publish relocation、wrapper launcherを作らない。
- `ApplicationPathSnapshot`等の`Environment.ProcessPath`／`AppContext.BaseDirectory`境界はfolder／bundleの両方で維持する。
- updaterはsingle-fileのままにし、`update_work/current`への単一payload handoffを維持する。

### `P4 FINAL-GATE`

選択profileについて次をclean checkout相当で閉じる。

1. 全5 projectのlocked restore、Release build、full tests。
2. Roslynator／format／warning gate。
3. main appとupdaterのwin-x64 Self-contained publish。
4. package／layout validator、hash／managed／native／license inventory。
5. publish outputからのstartup／shutdown、existing-data acceptance。
6. pre-NET10 packageからのupdate success／fault rollback acceptance。
7. current performance reportとselected profileの設定整合、publish outputからのstartup smoke。
8. frozen snapshotのfresh outcome review、重大指摘修正後の再検証／fresh review。

P4完了後、`engineering migration: complete`へ戻し、manual clean-machine／release prerequisiteへhandoffする。

## 4. Engineering Completion Gate

次を満たしたときCodexの.NET 10作業を完了とする。

1. tracked projectに`net472`がない。
2. 全5 projectのlocked restore／Release build、full tests、analyzerが通る。
3. selected main-app profileとupdaterのwin-x64 Self-contained publishが再現できる。
4. selected layoutが公式host／bundlerだけで成立し、custom probing／loader／relocationを持たない。
5. retained native componentがversion、source、license notice、ABI test、publish ownerを持つ。
6. existing-data、update success、rollbackの自動受入れが通る。
7. selected profileがcurrent performance reportのdecision ruleを満たす。
8. fresh outcome reviewで重大指摘がない。

`.NET Desktop Runtime`未導入machine／VM、署名、公開、BASS.NET entitlementはEngineering Gateに含めず、[手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)へhandoffする。
