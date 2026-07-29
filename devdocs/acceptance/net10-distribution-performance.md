# .NET 10 distribution performance acceptance

計測日: 2026-07-29

## Decision

main appの最終候補は`bundle-r2r`とする。

`folder-r2r`との差は、warm-cache中央値で18 ms、fresh-install中央値で15 ms、ready時working setで約6 MiBであり、実用差の閾値`max(500 ms, 10%)`／`max(16 MiB, 10%)`を下回る。一方、publish file数は`folder-r2r`の505 filesに対して24 filesである。native self-extractを必要とせず、同等の起動性能で配布file数を減らせるため`bundle-r2r`を選択する。

同じbundle familyの`bundle-il`に対しては、warm-cache中央値が約713 ms速く、fresh-installやworking setに実用上の退行がない。ReadyToRunの採用根拠も満たす。

選択profileは`Properties/PublishProfiles/WinX64SelfContained.pubxml`とし、次のpropertyを固定する。

| Property | Value |
|---|---|
| RuntimeIdentifier | `win-x64` |
| SelfContained | `true` |
| PublishSingleFile | `true` |
| IncludeNativeLibrariesForSelfExtract | `false` |
| PublishReadyToRun | `true` |
| PublishTrimmed | `false` |
| PublishReadyToRunComposite | `false` |
| EnableCompressionInSingleFile | `false` |
| IncludeAllContentForSelfExtract | `false` |

## Protocol

最初に最も複雑な`extract-r2r`をfresh相当1回、同じinstall／extract directoryを再利用するwarm 1回でsmokeし、起動、`startup_ready_operable`検出、graceful shutdown、semantic state保存を確認した。

続いて六candidateを同じfixtureでround-robin実行した。

- 各candidateのwarm-up: 1回
- warm-cache measured: 3回
- fresh-install measured: 3回
- 総起動数: 42
- failure: 0
- 追加測定: なし。実用上同等な候補の判定を変えないため

実行command:

```powershell
pwsh -NoProfile -NonInteractive -File .\scripts\benchmark-net10-distribution.ps1
```

fresh-installはcandidateを新しいinstall directoryへcopyし、`extract-*`には空の`DOTNET_BUNDLE_EXTRACT_BASE_DIR`を与えた。OS page cacheは消去していないため、cold benchmarkとは呼ばない。

## Environment

| Item | Value |
|---|---|
| OS | Microsoft Windows NT 10.0.22635.0 |
| Architecture | x64 |
| CPU | AMD64 Family 23 Model 8 Stepping 2 |
| Logical processors | 12 |
| Installed memory | 34,288,893,952 bytes |
| .NET SDK | 10.0.302 |
| Measurement base HEAD | `cfedc5fd6b64397f6cebe7eeef93955435362372` |
| P1 committed snapshot | `42ea8c41` |
| Defender／network | harnessから変更せず、同一sessionで比較 |

計測時worktreeはP1のproduction fix、fixture、harnessを含むdirty snapshotだった。これらは`42ea8c41`へまとめてcommitした。計測後のselection trade-off処理とoutput directory safety修正はcandidate binaryを変更しないため、同じraw measurementsを最終ロジックで再評価した。

## Candidate summary

次のstartup値はproduction log本文の`elapsedMs`ではなく、外部harnessが`Process.Start()`直前に開始したStopwatchによるprocess start→`startup_ready_operable` log検出の秒数である。`median (min–max)`を示す。working setとpublish sizeはMiB。

| Candidate | Warm-cache startup | Fresh-install startup | Working set | Publish size | Files |
|---|---:|---:|---:|---:|---:|
| `folder-il` | 3.787 (3.751–3.853) | 5.638 (5.635–5.669) | 207.4 | 185.5 | 505 |
| `folder-r2r` | 3.302 (3.270–3.302) | 4.650 (4.553–4.703) | 211.9 | 197.5 | 505 |
| `bundle-il` | 3.997 (3.826–4.061) | 4.964 (4.901–4.969) | 215.2 | 179.5 | 24 |
| `bundle-r2r` | 3.284 (3.272–3.345) | 4.665 (4.607–4.764) | 217.9 | 191.4 | 24 |
| `extract-il` | 3.895 (3.795–4.039) | 5.622 (5.573–5.650) | 215.9 | 182.2 | 18 |
| `extract-r2r` | 3.289 (3.238–3.497) | 5.454 (5.254–5.504) | 217.9 | 194.1 | 18 |

process start→main-window handle／input-idle到達の遅い方をmain-window readyとし、その中央値も外部harnessで記録した。

| Candidate | Warm-cache main-window ready | Fresh-install main-window ready |
|---|---:|---:|
| `folder-il` | 2.895 s | 4.612 s |
| `folder-r2r` | 2.598 s | 3.736 s |
| `bundle-il` | 3.053 s | 3.893 s |
| `bundle-r2r` | 2.632 s | 3.858 s |
| `extract-il` | 2.993 s | 4.516 s |
| `extract-r2r` | 2.582 s | 4.639 s |

`folder-r2r`と`bundle-r2r`をbalanced practical tierとした。`extract-r2r`はwarm-cacheでは同等だが、fresh-installでnative extraction costが見えるため選択しない。

この比較はstartupとready時resourceを対象とし、steady-state workloadを計測していない。bundleは起動後に継続的な展開処理を持たず、ReadyToRun codeもtiered compilationの対象となる一般特性を採用理由に含めるが、アプリ実行中の速度差は断定しない。

## Raw evidence

raw outputはignored artifactとして次へ保持する。

- `artifacts/performance/net10-distribution/report.json`
  - SHA-256: `b4d27762d1d41117848f0b3eacff3cd6950b77d8ed7dff3c877fbe7d6707089a`
- `artifacts/performance/net10-distribution/runs.csv`
  - SHA-256: `54962ad0e88a8738e046330840bc84f224a78bd726e75f57487a13d271d406ab`
- `artifacts/performance/net10-distribution-smoke/report.json`
  - SHA-256: `44fd17f43c99b6444ec1cfbc74676ac4453bb00cfe2186985b86fc747690d17c`
