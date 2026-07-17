# .NET 10 migration blocker register

[リファクタリング完了計画](./BeMusicSeekerリファクタリング計画.md)に対する living blocker register。現在の `net472` behavior を維持しながら、Refactoring Completion Gate 前に境界へ閉じる課題と、Gate 後の純粋な `.NET 10` 移行課題を区別する。

この文書は調査履歴や次 ticket の候補を保存しない。blocker の owner、境界、status が変わった場合だけ更新する。

## Current project baseline

- app / tests / updater: `net472`, x64
- app: WPF + WinForms
- SDK: `global.json` で .NET SDK 10 系
- config: `app.config`、`System.Configuration`、custom portable settings provider
- dependencies: NuGet と `libs/*.dll` HintPath の混在
- native: `native/*.dll`、`vendor/native/x64/*.dll`、custom output relocation

## Blockers

| ID | Area | Current coupling | Refactoring Gate の境界条件 | Gate 後の migration work | State |
|---|---|---|---|---|---|
| CFG-01 | `Settings.Default` / `System.Configuration` | ViewModel、domain、XAML、tests が global settings と save timing に直接依存 | load / edit / save / upgrade を configuration owner に集約し、workflow は snapshot / interface を受け取る | ConfigurationManager 継続か新 store かを決め、既存 user.config migration を実装する | open |
| DB-01 | SQLite / raw SQL / schema / transaction | `BMSLibrary`、`BMSPlaylist`、LR2、internal service に connection、SQL、schema、transaction ownership が分散 | `LIB-01` の scan commit、`LIB-03` の catalog mutation、`LIB-04` の LR2 sync、`PL-01` の playlist persistence ごとに repository / gateway と transaction owner を明示し、application / domain workflow が raw connection / SQL を直接所有しない。schema 変更は構造整理と分離する | SQLite provider / native runtime の互換性を検証し、必要な package / API 移行を行う | open |
| CONC-01 | Library mutation serialization / lock ordering | generic mutation route が path / folder / removal の DB write、live catalog apply、owned collection、package residual、LR2 block / folder sync、resource-health、notification を broad host と外側 lock / callback ordering で束ねる | `LIB-03` の catalog owner が immutable request prepare、catalog guard 下の durable transaction、canonical live apply / version、guard release、immutable receipt / canonical event publish を一つの write protocol として所有する。package / LR2 / playlist / UI residual は receipt 後に適用し、catalog guard 中に別 owner を callback せず、owner 間で lock / mutable collection を取得しない。DB failure 時に live catalog と consumer residual が変化しない behavior test を持ち、既存の owned-collection version / PropertyChanged timing を維持する | .NET 10 上で cancellation、synchronization primitive、dispatcher interaction、failure / shutdown ordering を再検証する | open |
| LAYOUT-01 | `app.config` probing / managed output | build 後に managed DLL を `libs` へ移動し root から削除 | output policy と path owner を project / loader 境界へ閉じ、business workflow に漏らさない | deps.json / apphost / publish layout に置換する | open |
| DEP-01 | HintPath managed DLL | Livet、MetroRadiance、Expression、sqlite.net、Bass.Net 等の互換性と identity が未確定 | DLL 固有型を application / domain contract から排除する | 各 DLL の互換性を検証し、NuGet / replacement / retention を決定する | open |
| NAT-01 | Native DLL layout | app / tests で copy 先と load path が不統一 | base directory と native load を用途別 adapter に閉じる | RID native asset / explicit copy / publish layout を決定する | open |
| INT-01 | P/Invoke / manual load / CAS | Everything、filesystem、window、audio に P/Invoke と legacy attribute が残る | call site を既存または新しい platform adapter 内に限定する | platform annotation、interop方式、CAS削除、loader error policy を決定する | open |
| UIH-01 | WPF + WinForms + WebBrowser / COM | MainWindow、player host、dialog、resource が UI technology と結合 | View / view-host adapter の外へ UI 型を出さない | WinForms 継続、WebBrowser / WebView2、System.Drawing の扱いを決定する | open |
| PROC-01 | External process | player、explorer、browser、update / restart の path と quoting が call site に分散 | 用途別 process gateway と request contract に集約する | apphost location と command line behavior を再検証する | open |
| PATH-01 | Base directory / assembly location | settings、native、update、metadata、language catalog が process layout と結合 | application path policy を一つの provider が所有する | framework-dependent / self-contained / single-file 対応範囲を決定する | open |
| UPD-01 | Updater coupling | updater は `net472` exe と custom copy / restart path を前提とする | update / restart を application workflow から gateway へ分離する | updater 同時移行か外部 legacy tool 継続かを決定する | open |
| DEPLOY-01 | `System.Deployment` | project reference はあるが source usage と release policy が不明確 | 実使用の有無を code / build から確定し、不要依存を business code から除く | 不要なら削除、必要なら .NET 対応 deployment を別設計する | open |

## Gate classification rule

各 blocker は Gate audit 時に次のどちらかでなければならない。

1. `boundary met`: architecture 上の owner / adapter / gateway があり、残作業が package、TFM、runtime、deployment、data migration の変更だけである。
2. `not applicable`: 実使用がなく、安全に削除済みである。

「巨大型の private workflow を分けないと移行判断できない」「UI / domain の owner が未定」「global settings の save timing が複数箇所に分散」「複数 workflow が同じ facade lock / mutation-block flag / callback host を共有する」は `boundary met` ではない。

## Update rule

- implementation unit ごとの調査結果や候補 class 名を追記しない。
- owner / boundary が成立した outcome commit で State と Current coupling を更新する。
- package version、replacement、runtime layout の最終決定は Gate 後の `.NET 10` migration plan に置く。
- blocker を close するときは、対応する code / project / test を evidence とし、完了履歴は commit に残す。
