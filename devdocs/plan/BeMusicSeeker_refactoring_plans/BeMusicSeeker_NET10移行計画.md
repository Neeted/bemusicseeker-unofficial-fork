# BeMusicSeeker .NET 10 Self-contained 移行計画

[現在地](./PLAN_STATUS.md) / [共通実行ルール](./00_Codex共通実行ルール.md) / [依存関係台帳](./DOTNET10_DEPENDENCY_REGISTER.md) / [blocker台帳](./DOTNET10_MIGRATION_BLOCKERS.md) / [手動受入れ](./POST_MIGRATION_MANUAL_ACCEPTANCE.md)

## 1. 現在の判定

MVVM／owner整理、全5 projectの.NET 10 retarget、managed／native dependency、SQLite、existing-data、update／rollbackのengineeringは完了している。

一方、main appの現行profileは「配布物を一つのexeへ寄せる」機能受入れで選ばれており、end-to-end startup／working setの比較evidenceを持たない。性能最優先という最終要件に対しては完了判定を一度だけ再開し、`NET10-10 Performance-first distribution closure`で配布profileを確定する。

現在のsingle-file profileは次の性質を持つ。

- managed assembliesはbundleから読み込まれ、通常のfolderへ全展開されない。
- `IncludeNativeLibrariesForSelfExtract=true`のため、CoreCLR、JIT、SQLite等のbundled native payloadはcache miss時の起動前にWindowsのbundle extraction directoryへ展開される。
- `PublishReadyToRun=false`であり、startup時のJIT削減効果は比較していない。
- production logの`startup_ready_operable elapsedMs`はViewModel初期化途中からのphase計測で、process launch、host初期化、native extractionを含まない。

したがって「全DLLを毎回folderへ解凍している」わけではないが、現行profileにはfresh install／cache miss時のnative extractionとsingle-file host costがある。warm cacheでは再利用され得るため、最終判断はfresh installとwarm cacheを分けた外部benchmarkで行う。

## 2. Target matrix

| Project | Target | 方針 |
|---|---|---|
| `BeMusicSeeker.csproj` | `net10.0-windows`, x64 | win-x64 Self-contained。最終profileは`NET10-10`で性能選択 |
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
2. harness側Stopwatchを`Process.Start()`前に開始し、process start→main window handle／input idle、process start→`startup_ready_operable`ログ検出を測る。log pollingは測定誤差がselection thresholdを支配しない間隔またはevent通知で行い、ログ本文の`elapsedMs`はphase内訳にだけ使う。
3. `fresh-install`はimmutable candidateを毎回新しいinstall directoryへcopyする。`extract-*`には毎回空の専用`DOTNET_BUNDLE_EXTRACT_BASE_DIR`を与える。`warm-cache`は同じinstall directoryとextract directoryを再利用する。これはOS page cacheを強制消去するcold benchmarkとは呼ばない。
4. 各candidateについて3回warm-up後に20回の`warm-cache`を測り、別に10回の`fresh-install`を測る。上位差が3%未満、または変動係数が5%を超える場合は、上位candidateだけ各20回を一度追加する。追加測定後も不安定なら再試行を反復せず、no-extractの`folder-il`を保守的defaultとして選ぶ。
5. candidate順序をround-robinで入れ替え、同じsettings、data、native asset、network／Defender条件を使う。
6. p50／p90、failure、peak working set at ready、process CPU time、publish byte数、file数、extract byte数／file数、artifact hash、Windows build、SDKをmachine-readable reportへ出す。
7. appをgraceful shutdownし、設定／DBのsemantic snapshotが測定前後で一致することを確認する。startup phase logとmain-view build等の既存内部metricsは診断用に保存するが、外部end-to-end metricを置き換えない。

benchmark専用のproduction fallback、loader、startup short-cutを追加しない。

### `P2 PROFILE-SELECTION`

機能受入れを通らないcandidateは失格とする。残候補を次の順で決める。

1. `max(fresh-install p90, warm-cache p90)`のprocess start→`startup_ready_operable`が最小のcandidateを基準にする。
2. 基準との差が`max(100 ms, 3%)`以内を同率候補とする。
3. 同率候補ではpeak working set p90が小さいものを選ぶ。差が5%以内ならprocess start→main-window ready p90が小さいものを選ぶ。
4. なお同率なら、native self-extractなし、次にfolder profileを選ぶ。

file数、exe一個、zip sizeはtie-breakerより下位であり、性能selectionを覆さない。ReadyToRunは公式説明上startup改善の可能性がある一方、size／working setを増やし得るため、実測結果だけで採否を決める。

選択結果は`devdocs/acceptance/net10-distribution-performance.md`へcurrent-onlyのperformance acceptance reportとして、command、environment、candidate summary、selected profile、理由、raw report hashを記録する。raw JSON／CSVはartifactとして保持し、過去runの逐次logを計画文書へ追記しない。

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
7. performance reportの再現runとselected profileの閾値再確認。
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
